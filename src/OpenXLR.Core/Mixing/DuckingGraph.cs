using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The key channels, each on a key input of the stage in order, and the
/// channel|mix cells carried through native duck stages. Never saved.
/// </summary>
internal sealed record DuckingPlan(IReadOnlyList<string> Keys, IReadOnlySet<string> Cells,
    double ThresholdDb, double DepthDb, double AttackMs, double ReleaseMs, bool Enabled)
{
    /// <summary>Layout edits keep surviving keys and cells, and drop the plan when its last key is removed.</summary>
    internal DuckingPlan? Prune(MixerConfig config)
    {
        List<string> keys = [.. Keys.Where(k => config.Channels.Any(c => c.Id == k))];
        if (keys.Count == 0) return null;
        var cells = config.Channels.Where(c => !keys.Contains(c.Id))
            .SelectMany(c => config.Mixes.Select(m => $"{c.Id}|{m.Id}")).ToHashSet(StringComparer.Ordinal);
        return this with { Keys = keys, Cells = Cells.Where(cells.Contains).ToFrozenSet(StringComparer.Ordinal) };
    }
}

/// <summary>Software availability, the last failure, and the mixes currently fed by a live stage.</summary>
internal sealed record DuckingRuntime(bool Available, string? Error, IReadOnlyList<string> RunningMixes);

public sealed partial class Mixer
{
    internal const string DuckingNeedsHost =
        "Ducking needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";
    private const string DuckingGivenUp = "ducking kept failing and is bypassed; switch it off and on to try again";

    private DuckingPlan? _duckPlan;
    private readonly Dictionary<string, DuckBus> _duckBuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _duckLegIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _duckErrors = new(StringComparer.Ordinal);
    // Meter readers never wait for the graph lock or a helper operation.
    private readonly ConcurrentDictionary<string, NativePluginHost> _duckMeterHosts = new(StringComparer.Ordinal);

    private sealed class DuckBus(MixDefinition mix, uint module)
    {
        public MixDefinition Mix { get; } = mix;
        public uint Module { get; } = module;
        public FilterHandle? Stage;
        public PortLink? Input, Output, Bypass;
        // One link per key, on the stage's key input of the same index.
        public readonly PortLink?[] Keys = new PortLink?[DuckingSetup.MaxKeys];
        public string[]? KeyChannels;
        public readonly Dictionary<string, double> Written = new(StringComparer.Ordinal);
        public bool Ready;
    }

    private static string DuckKey(string mix) => "duck:" + mix;

    internal DuckingRuntime DuckingStatus
    {
        get
        {
            lock (_gate)
            {
                string? blocked = _duckBuses.Keys.FirstOrDefault(m => _restarts.Blocked(DuckKey(m)));
                bool installed = _pw.DuckHostInstalled;
                string? error = !installed ? DuckingNeedsHost
                    : blocked is not null ? _restarts.Message(DuckKey(blocked), DuckingGivenUp)
                    : _duckErrors.Values.FirstOrDefault();
                return new(installed && blocked is null, error,
                    [.. _duckBuses.Where(b => b.Value.Stage is { IsAlive: true }
                        && b.Value.Output is { Complete: true, Pairs.Count: 2 }).Select(b => b.Key).Order(StringComparer.Ordinal)]);
            }
        }
    }

    /// <summary>
    /// Replace the internal graph plan. Values and the switch go to running
    /// stages live. An off plan starts no stage, but keeps existing stages at
    /// unity until a rebuild. Null or no cells restores the direct sends.
    /// The caller's cell set is copied. Invalid ids or native control values
    /// are refused before the graph changes; microphone selection belongs to
    /// the setup layer that calls this API.
    /// </summary>
    internal void SetDuckingPlan(DuckingPlan? plan)
    {
        lock (_gate)
        {
            if (plan is not null)
            {
                if (plan.Keys.Count is 0 or > DuckingSetup.MaxKeys || plan.Keys.Distinct(StringComparer.Ordinal).Count() != plan.Keys.Count
                    || plan.Keys.Any(k => !_config.Channels.Any(c => c.Id == k)))
                    throw new ArgumentException($"Ducking needs one to {DuckingSetup.MaxKeys} distinct key channels in the layout.");
                if (plan.Prune(_config) is not { } pruned || !pruned.Cells.SetEquals(plan.Cells))
                    throw new ArgumentException("Ducking cells must exist in the layout and cannot belong to the key.");
                Check(plan.ThresholdDb, -60, 0, nameof(plan.ThresholdDb));
                Check(plan.DepthDb, 0, 40, nameof(plan.DepthDb));
                Check(plan.AttackMs, 1, 5000, nameof(plan.AttackMs));
                Check(plan.ReleaseMs, 1, 10000, nameof(plan.ReleaseMs));
                plan = pruned;
            }
            DuckingPlan? previous = _duckPlan;
            // Disconnect the output before any cell moves back to its direct
            // leg. The remaining cells in this mix hear a brief gap too.
            foreach (DuckBus bus in _duckBuses.Values)
                if (!DuckCells(previous, bus.Mix.Id).SetEquals(DuckCells(plan, bus.Mix.Id))) SuspendDuckBusLocked(bus);
            _duckPlan = plan;
            if (plan is { Enabled: true } && previous is not { Enabled: true })
                foreach (MixDefinition mix in _config.Mixes) _restarts.Forget(DuckKey(mix.Id));
            if (plan is not null && previous is not null && plan.Keys.SequenceEqual(previous.Keys) && plan.Cells.SetEquals(previous.Cells)
                && plan.Cells.Count > 0 && _duckBuses.Count > 0
                && _config.Mixes.Where(m => DuckCells(plan, m.Id).Count > 0).All(m =>
                    _duckBuses.TryGetValue(m.Id, out DuckBus? bus) && bus.Ready && bus.Stage is { IsAlive: true }))
            {
                foreach (DuckBus bus in _duckBuses.Values)
                    try { WriteDuckControlsLocked(bus, DuckControlsLocked()); }
                    catch (InvalidOperationException ex) { FailDuckStageLocked(bus, ex.Message); }
                return;
            }
            if (_built) SyncDuckBusesLocked();
        }

        static void Check(double value, double min, double max, string name)
        {
            if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name);
        }
    }

    private static HashSet<string> DuckCells(DuckingPlan? plan, string mix)
        => plan?.Cells.Where(c => c.EndsWith("|" + mix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal) ?? [];

    private bool UsesDuckBusLocked(string cell, string mix)
        => _duckBuses.ContainsKey(mix) && _duckPlan?.Cells.Contains(cell) == true;

    /// <summary>
    /// The two paths of one cell, both using the existing fader and mute
    /// calculation; both closed while the microphone's dry route carries the
    /// cell instead (see <see cref="DryLegTargetLocked"/>).
    /// </summary>
    private ((double Level, bool Muted) Direct, (double Level, bool Muted) Bus) CellLegTargetsLocked(
        string channel, string mix, out bool waiting)
    {
        var target = CellTargetLocked(channel, mix, out waiting);
        if (DryCarriesLocked(channel, mix)) return ((0, true), (0, true));
        return UsesDuckBusLocked(Cell(channel, mix), mix) ? ((0, true), target) : (target, (0, true));
    }

    /// <summary>
    /// The stage's key_open mask: bit i set while key i is heard in at least
    /// one send, after channel, group and mix controls and the microphone
    /// pause. The stage follows the loudest open key.
    /// </summary>
    private int DuckKeyOpenLocked()
    {
        if (_duckPlan is not { } plan) return 0;
        int open = 0;
        for (int i = 0; i < plan.Keys.Count; i++)
            if (DuckKeyHeardLocked(plan.Keys[i])) open |= 1 << i;
        return open;
    }

    private bool DuckKeyHeardLocked(string key)
    {
        // The pause mutes a microphone's channel sink, but the sink's monitor,
        // which feeds the key, still carries the speech.
        if (MicrophonesPausedLocked && _config.Channels.Any(c => c.Id == key && c.Microphone)) return false;
        foreach (MixDefinition mix in _config.Mixes)
        {
            (double level, bool muted) = CellTargetLocked(key, mix.Id, out _);
            if (level > 0 && !muted && !_mixMuted.Contains(mix.Id) && _mixVolume.GetValueOrDefault(mix.Id, 1) > 0)
                return true;
        }
        return false;
    }

    private Dictionary<string, double> DuckControlsLocked() => new(StringComparer.Ordinal)
    {
        ["enabled"] = _duckPlan!.Enabled ? 1 : 0,
        ["threshold"] = _duckPlan.ThresholdDb,
        ["depth"] = _duckPlan.DepthDb,
        ["attack"] = _duckPlan.AttackMs,
        ["release"] = _duckPlan.ReleaseMs,
        ["key_open"] = DuckKeyOpenLocked(),
    };

    private void WriteDuckControlsLocked(DuckBus bus, IReadOnlyDictionary<string, double> controls)
    {
        if (bus.Stage is not { IsAlive: true } stage) return;
        foreach ((string symbol, double value) in controls)
        {
            if (bus.Written.TryGetValue(symbol, out double was) && was == value) continue;
            stage.NativeHost!.SetControl(symbol, value);
            bus.Written[symbol] = value;
        }
    }

    // A cell change can close the last key send, including through a group.
    // Only a changed key_open is written, with no graph or process work.
    private void PushDuckKeyOpenLocked()
    {
        if (_duckBuses.Count == 0 || _duckPlan is null) return;
        var controls = new Dictionary<string, double> { ["key_open"] = DuckKeyOpenLocked() };
        foreach (DuckBus bus in _duckBuses.Values)
            try { WriteDuckControlsLocked(bus, controls); }
            catch (InvalidOperationException ex) { FailDuckStageLocked(bus, ex.Message); }
    }

    private void SuspendDuckBusLocked(DuckBus bus)
    {
        DropDuckOutputLocked(bus);
        bus.Ready = false;
    }

    private void DropDuckOutputLocked(DuckBus bus)
    {
        _duckMeterHosts.TryRemove(bus.Mix.Id, out _);
        if (bus.Output is not null) _pw.Unlink(bus.Output);
        if (bus.Bypass is not null) _pw.Unlink(bus.Bypass);
        bus.Output = bus.Bypass = null;
    }

    /// <summary>Called before any new combine can grow uninitialised bus legs.</summary>
    private void SuspendDuckBusesLocked()
    {
        foreach (DuckBus bus in _duckBuses.Values) SuspendDuckBusLocked(bus);
    }

    private IEnumerable<string> CombineSinkNamesLocked()
        => _config.Mixes.Select(m => m.SinkName).Concat(_duckBuses.Values.Select(b => b.Mix.DuckBusName));

    private bool PrepareDuckBusLocked(DuckBus bus)
    {
        if (!WaitForLegsLocked([.. _combineModules.Values], [bus.Mix.SinkName, bus.Mix.DuckBusName])) return false;
        ReapplyCellsLocked([bus.Mix.Id]);
        if (_config.Channels.Any(c => _pendingCells.Contains(Cell(c.Id, bus.Mix.Id)))) return false;
        _pw.SetSinkVolume(bus.Mix.DuckBusName, 1);
        _pw.SetSinkMuted(bus.Mix.DuckBusName, false);
        bus.Ready = true;
        return true;
    }

    /// <summary>Reconcile the plan's buses without reloading a channel or an unaffected stage.</summary>
    private bool SyncDuckBusesLocked()
    {
        bool changed = false;
        var wanted = _config.Mixes.Where(m => DuckCells(_duckPlan, m.Id).Count > 0).ToList();
        foreach (string id in _duckBuses.Keys.Where(id => wanted.All(m => m.Id != id)).ToList())
        {
            RemoveDuckBusLocked(id);
            changed = true;
        }
        foreach (string id in _duckErrors.Keys.Where(id => wanted.All(m => m.Id != id)).ToList()) _duckErrors.Remove(id);
        foreach (MixDefinition mix in wanted)
        {
            if (!_duckBuses.TryGetValue(mix.Id, out DuckBus? bus))
            {
                if (_duckPlan is not { Enabled: true }) continue;
                try
                {
                    EnsurePulseHeadroomLocked(newStreams: _config.Channels.Count, newNodes: 2);
                    bus = new(mix, _pw.CreateNullSink(mix.DuckBusName, $"OpenXLR {mix.Name} duck bus", visible: false));
                    _duckBuses[mix.Id] = bus;
                    changed = true;
                }
                catch (InvalidOperationException ex) { _duckErrors[mix.Id] = ex.Message; continue; }
            }
            if (!bus.Ready)
            {
                try
                {
                    if (!PrepareDuckBusLocked(bus))
                        throw new InvalidOperationException("Not every duck bus send could be set; the mix uses its direct sends.");
                }
                catch (InvalidOperationException ex)
                {
                    // A late leg must never open at the session manager's
                    // remembered level into a connected bus. Restore direct
                    // sends and try building this bus on a later sweep.
                    RemoveDuckBusLocked(mix.Id);
                    _duckErrors[mix.Id] = ex.Message;
                    changed = true;
                    continue;
                }
                changed = true;
            }
            if (bus.Stage is { IsAlive: false } dead)
            {
                FailDuckStageLocked(bus, dead.LastWords);
                changed = true;
                continue;   // the bypass carries audio for this sweep; retry on the next
            }
            try
            {
                if (bus.Stage is null && _pw.DuckHostInstalled && !_restarts.Blocked(DuckKey(mix.Id)))
                {
                    var controls = DuckControlsLocked();
                    bus.Stage = _pw.CreateDucker(mix.Id, controls);
                    foreach (var (symbol, value) in controls) bus.Written[symbol] = value;
                    changed = true;
                }
                if (bus.Stage is not null)
                {
                    WriteDuckControlsLocked(bus, DuckControlsLocked());
                    changed |= WireDuckStageLocked(bus);
                    _duckErrors.Remove(mix.Id);
                }
                else changed |= BypassDuckBusLocked(bus);
            }
            catch (InvalidOperationException ex)
            {
                FailDuckStageLocked(bus, ex.Message);
                changed = true;
            }
        }
        return changed;
    }

    private bool WireDuckStageLocked(DuckBus bus)
    {
        FilterHandle stage = bus.Stage!;
        bool changed = EnsureDuckLinkLocked(ref bus.Input, bus.Mix.DuckBusName, "monitor", stage.SinkName, "playback", 2);
        IReadOnlyList<string> keys = _duckPlan!.Keys;
        if (bus.KeyChannels is null || !bus.KeyChannels.SequenceEqual(keys))
        {
            UnlinkDuckKeysLocked(bus);
            bus.KeyChannels = [.. keys];
            changed = true;
        }
        // Each key's first channel on a key input of its own: the stage
        // takes the loudest, so two keys never add up.
        for (int i = 0; i < keys.Count; i++)
        {
            string keySink = _config.Channels.First(c => c.Id == keys[i]).SinkName;
            changed |= EnsureDuckLinkLocked(ref bus.Keys[i], keySink, "monitor", stage.SinkName, $"key_{i}", 1, fromChannel: 0);
        }
        // Break before make: even a partial old output must go before a
        // different path starts feeding the same mix.
        if (bus.Bypass is not null) { _pw.Unlink(bus.Bypass); bus.Bypass = null; changed = true; }
        changed |= EnsureDuckLinkLocked(ref bus.Output, stage.SourceName, "capture", bus.Mix.SinkName, "playback", 2);
        _duckMeterHosts[bus.Mix.Id] = stage.NativeHost!;
        return changed;
    }

    private bool EnsureDuckLinkLocked(ref PortLink? link, string from, string prefix, string to, string input,
        int count, int? fromChannel = null)
    {
        if (link is { Complete: true } && link.Pairs.Count == count)
        {
            LinkHealth health = _pw.EnsureLinks(link);
            if (health != LinkHealth.Broken) return health == LinkHealth.Relinked;
        }
        PortLink fresh = _pw.LinkNodes(from, prefix, to, input, fromChannel: fromChannel);
        if (link is not null) _pw.Unlink(new PortLink([.. link.Pairs.Except(fresh.Pairs)]));
        link = fresh;
        if (!fresh.Complete || fresh.Pairs.Count != count)
            throw new InvalidOperationException($"Ducking could not link {from} to {to}.");
        return true;
    }

    private bool BypassDuckBusLocked(DuckBus bus)
    {
        if (!bus.Ready) return false;
        _duckMeterHosts.TryRemove(bus.Mix.Id, out _);
        if (bus.Output is not null) { _pw.Unlink(bus.Output); bus.Output = null; }
        try
        {
            return EnsureDuckLinkLocked(ref bus.Bypass, bus.Mix.DuckBusName, "monitor", bus.Mix.SinkName, "playback", 2);
        }
        catch (InvalidOperationException ex) { _duckErrors[bus.Mix.Id] = ex.Message; return false; }
    }

    private void StopDuckStageLocked(DuckBus bus)
    {
        _duckMeterHosts.TryRemove(bus.Mix.Id, out _);
        if (bus.Output is not null) _pw.Unlink(bus.Output);
        if (bus.Input is not null) _pw.Unlink(bus.Input);
        UnlinkDuckKeysLocked(bus);
        if (bus.Stage is not null) _pw.StopFilter(bus.Stage);
        bus.Output = bus.Input = null;
        bus.Stage = null;
        bus.KeyChannels = null;
        bus.Written.Clear();
    }

    private void UnlinkDuckKeysLocked(DuckBus bus)
    {
        for (int i = 0; i < bus.Keys.Length; i++)
        {
            if (bus.Keys[i] is { } link) _pw.Unlink(link);
            bus.Keys[i] = null;
        }
    }

    private void FailDuckStageLocked(DuckBus bus, string? reason)
    {
        StopDuckStageLocked(bus);
        _restarts.Failed(DuckKey(bus.Mix.Id), reason);
        _duckErrors[bus.Mix.Id] = reason ?? "The duck stage stopped; the mix is bypassed.";
        _pw.Note($"Ducking on {bus.Mix.Id} is bypassed: {_duckErrors[bus.Mix.Id]}");
        BypassDuckBusLocked(bus);
    }

    private void RemoveDuckBusLocked(string id)
    {
        if (!_duckBuses.Remove(id, out DuckBus? bus)) return;
        DropDuckOutputLocked(bus);
        StopDuckStageLocked(bus);
        try { _pw.UnloadModule(bus.Module); }
        finally
        {
            // Even a module that refuses to unload has no output now. Its
            // former targets must get their direct sends back.
            foreach (string cell in _duckLegIndex.Keys.Where(c => c.EndsWith("|" + id, StringComparison.Ordinal)).ToList())
                _duckLegIndex.Remove(cell);
            foreach (ChannelDefinition ch in _config.Channels)
                if (_cells.Contains(Cell(ch.Id, id))) ApplyCellLocked(ch.Id, id);
        }
        _duckErrors.Remove(id);
        _restarts.Forget(DuckKey(id));
    }

    private void RemoveDuckBusesLocked()
    {
        // The adapter unloads the bus modules with the rest of the graph.
        // ForgetGraph has already forgotten their ids after a server restart.
        foreach (DuckBus bus in _duckBuses.Values)
        {
            DropDuckOutputLocked(bus);
            StopDuckStageLocked(bus);
        }
        _duckBuses.Clear();
        _duckLegIndex.Clear();
        _duckErrors.Clear();
    }

    private void PruneDuckPlanLocked()
    {
        SetDuckingPlan(_duckPlan?.Prune(_config));
    }

    private bool HealDuckBusesLocked()
    {
        bool changed = false;
        foreach (DuckBus bus in _duckBuses.Values.ToList())
        {
            if (_pw.SinkExists(bus.Mix.DuckBusName) != false) continue;
            // The bus was removed externally. Its id may already have been
            // reused, so only forget it, then restore the direct path.
            DropDuckOutputLocked(bus);
            StopDuckStageLocked(bus);
            _pw.ForgetModule(bus.Module);
            _duckBuses.Remove(bus.Mix.Id);
            foreach (ChannelDefinition ch in _config.Channels) ApplyCellLocked(ch.Id, bus.Mix.Id);
            changed = true;
        }
        return SyncDuckBusesLocked() || changed;
    }
}
