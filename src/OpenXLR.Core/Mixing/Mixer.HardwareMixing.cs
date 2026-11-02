using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Hardware mixing as clients see it in the state: whether it is wanted and
/// running, why not, and while it runs the pairs, sections, held levels and
/// what it turns off (see <see cref="HardwareMixingPlan"/>).
/// </summary>
public sealed record HardwareMixingState
{
    public bool Wanted { get; init; }
    public bool Active { get; init; }
    public string? Reason { get; init; }
    public string? Note { get; init; }
    public IReadOnlyDictionary<string, int> Pairs { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Sections { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Jacks { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<HardwareMixingClamp> Clamped { get; init; } = [];
    public IReadOnlyDictionary<string, string> Disabled { get; init; } = new Dictionary<string, string>();

    /// <summary>The state of a plan: the maps only while the mode runs.</summary>
    public static HardwareMixingState Of(HardwareMixingPlan plan) => plan.Active
        ? new()
        {
            Wanted = true, Active = true, Pairs = plan.Pairs, Sections = plan.Sections, Jacks = plan.Jacks,
            Clamped = plan.Clamped, Disabled = plan.Disabled,
        }
        : new() { Wanted = plan.Wanted, Reason = plan.Reason, Note = plan.Note };
}

public sealed partial record MixerState
{
    /// <summary>Hardware mixing on the Wave XLR Pro; null until the daemon has planned it once.</summary>
    public HardwareMixingState? HardwareMixing { get; init; }
}

public sealed partial record ChannelStatus
{
    /// <summary>The USB playback pair the channel streams on while hardware mixing runs; null otherwise.</summary>
    public int? HardwarePair { get; init; }
}

public sealed partial record MixStatus
{
    /// <summary>The section of the Wave XLR Pro that builds the mix while hardware mixing runs and a jack plays it; null otherwise.</summary>
    public int? HardwareSection { get; init; }
}

public sealed partial class Mixer
{
    // mixer.json "mixing", "hardwarePairs" and "hardwareSections" (machine
    // wiring, never in a profile), and the plan the last sweep made.
    private string _mixing = MixingMode.Software;
    private readonly Dictionary<string, int> _hardwarePairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hardwareSections = new(StringComparer.Ordinal);
    private HardwareMixingPlan? _hardwarePlan;

    /// <summary>Where the mixes the Pro's outputs play are wanted: <see cref="MixingMode"/>.</summary>
    public string Mixing { get { lock (_gate) return _mixing; } }

    /// <summary>The plan the last sweep made, or null before the first.</summary>
    public HardwareMixingPlan? HardwarePlan { get { lock (_gate) return _hardwarePlan; } }

    /// <summary>Keep the plan a sweep made, for the state. True when what the state shows of it changed.</summary>
    public bool NoteHardwarePlan(HardwareMixingPlan plan)
    {
        lock (_gate)
        {
            bool changed = _hardwarePlan is null || !SameState(HardwareMixingState.Of(_hardwarePlan), HardwareMixingState.Of(plan));
            _hardwarePlan = plan;
            return changed;
        }
    }

    private static bool SameState(HardwareMixingState a, HardwareMixingState b)
        => a.Wanted == b.Wanted && a.Active == b.Active && a.Reason == b.Reason && a.Note == b.Note
            && a.Pairs.Count == b.Pairs.Count && !a.Pairs.Except(b.Pairs).Any()
            && a.Sections.Count == b.Sections.Count && !a.Sections.Except(b.Sections).Any()
            && a.Jacks.Count == b.Jacks.Count && !a.Jacks.Except(b.Jacks).Any()
            && a.Clamped.SequenceEqual(b.Clamped)
            && a.Disabled.Count == b.Disabled.Count && !a.Disabled.Except(b.Disabled).Any();

    /// <summary>
    /// The plan for hardware mixing from the model as it stands, for the
    /// unit the daemon describes; <paramref name="mixing"/> overrides the
    /// saved choice, as a dry run asks.
    /// </summary>
    public HardwareMixingPlan PlanHardwareMixing(HardwareMixingUnit? unit, bool failed = false, string? mixing = null,
        double section0LevelDb = HardwareMixingPlanner.Section0LevelDb)
    {
        lock (_gate) return HardwareMixingPlanner.Plan(HardwareSetupLocked(unit, failed, mixing ?? _mixing, section0LevelDb));
    }

    private HardwareMixingSetup HardwareSetupLocked(HardwareMixingUnit? unit, bool failed, string mixing, double section0LevelDb)
    {
        var levels = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ChannelDefinition channel in _config.Channels)
            foreach (MixDefinition mix in _config.Mixes)
                levels[Cell(channel.Id, mix.Id)] = HardwareLevelDbLocked(channel.Id, mix.Id);
        var unitSources = new Dictionary<int, string>();
        if (_drivenUnit is not null)
        {
            if (CarrierOfLocked(1) is { } xlr1) unitSources[ProMixSections.Xlr1] = xlr1;
            if (CarrierOfLocked(2) is { } xlr2 && xlr2 != unitSources.GetValueOrDefault(ProMixSections.Xlr1)) unitSources[ProMixSections.Xlr2] = xlr2;
            foreach (ChannelDefinition c in _config.Channels)
                if (c.InputPair is int pair && pair is ProMixSections.UsbAux or ProMixSections.LineIn
                    && !_bindings.ContainsKey(c.Id) && ChannelPresentLocked(c))
                    unitSources.TryAdd(pair, c.Id);
        }
        string? proSink = _hardwareOutputRouting ? HardwareOutputSinkLocked() : null;
        return new HardwareMixingSetup
        {
            Wanted = mixing == MixingMode.Hardware,
            MixerRunning = _built,
            Unit = unit,
            SinkPorts = proSink is null ? null : _pw.NodeChannels(proSink),
            SourcePorts = _inputPorts,
            Failed = failed,
            Channels = [.. _config.Channels],
            Mixes = [.. _config.Mixes],
            LevelsDb = levels,
            Jacks = [.. _monitorOutputs.Where(o => o.Contains('#') && !o.EndsWith("#usbaux", StringComparison.Ordinal)).OrderBy(JackOrder)
                .Select(o => (Jack: o[(o.IndexOf('#') + 1)..], Feed: FeedOfLocked(o)))
                .Where(j => JackSuffixes.Contains(j.Jack) && j.Feed is not null)
                .Select(j => (j.Jack, j.Feed!))],
            UnitSources = unitSources,
            Processed = unitSources.Values.Where(ProcessedInSoftwareLocked).ToHashSet(StringComparer.Ordinal),
            DirectPath = _hardwareMicChannels.OfType<string>().ToHashSet(StringComparer.Ordinal),
            MixChains = _config.Mixes.Where(m => InsertsFor(MixKey(m)).Count > 0).Select(m => m.Id).ToHashSet(StringComparer.Ordinal),
            MonitorMuted = _monitorMuted,
            SavedPairs = new Dictionary<string, int>(_hardwarePairs, StringComparer.Ordinal),
            SavedSections = new Dictionary<string, int>(_hardwareSections, StringComparer.Ordinal),
            Section0LevelDb = section0LevelDb,
        };
    }

    /// <summary>
    /// A channel's level in a mix for the unit's cells, in dB: the send, the
    /// channel volume and the mix volume, each on the desktop's cubic scale;
    /// negative infinity while the send or the mix is muted or an exclusive
    /// peer's mute is still waiting.
    /// </summary>
    private double HardwareLevelDbLocked(string channel, string mix)
    {
        string cell = Cell(channel, mix);
        if (_muted.Contains(cell) || _mixMuted.Contains(mix) || ExclusivePeerPendingLocked(channel)) return double.NegativeInfinity;
        double send = SendGain(_levels.GetValueOrDefault(cell, 0.0), ChannelVolumeLocked(channel), 1);
        double master = Math.Clamp(_mixVolume.GetValueOrDefault(mix, 1.0), 0, MixVolumeMaximumLocked(mix));
        return send > 0 && master > 0 ? 60 * Math.Log10(send) + 60 * Math.Log10(master) : double.NegativeInfinity;
    }

    /// <summary>The state's view of hardware mixing, and each channel's pair and each mix's section while it runs.</summary>
    private MixerState WithHardwareMixingLocked(MixerState state)
    {
        if (_hardwarePlan is not { } plan) return state;
        HardwareMixingState view = HardwareMixingState.Of(plan);
        if (!view.Active) return state with { HardwareMixing = view };
        return state with
        {
            HardwareMixing = view,
            Channels = [.. state.Channels.Select(c => view.Pairs.TryGetValue(c.Id, out int pair) ? c with { HardwarePair = pair } : c)],
            Mixes = [.. state.Mixes.Select(m => view.Sections.FirstOrDefault(s => MonitorFeed.Parts(s.Key).Contains(m.Id)) is { Key: not null } s
                ? m with { HardwareSection = s.Value } : m)],
        };
    }

    private void RestoreHardwareMixingLocked(MixerSettings s)
    {
        _mixing = MixingMode.IsValid(s.Mixing) ? s.Mixing : MixingMode.Software;
        _hardwarePairs.Clear();
        foreach ((string channel, int pair) in s.HardwarePairs) _hardwarePairs[channel] = pair;
        _hardwareSections.Clear();
        foreach ((string feed, int section) in s.HardwareSections) _hardwareSections[feed] = section;
        _hardwareEntry = s.HardwareEntry is { Valid: true } entry ? entry : null;
    }

    // --- the mode in the graph ---

    // What the unit held before the mode, while the unit holds the mode or is
    // being put back; the channels' links into their pairs and the pair each
    // link goes to.
    private HardwareEntry? _hardwareEntry;
    private readonly Dictionary<string, PortLink> _hardwareLinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _linkedPairs = new(StringComparer.Ordinal);

    /// <summary>The first selected output's volume as the state shows it: the jacks' own in hardware mixing.</summary>
    public double? OutputVolume { get { lock (_gate) return _outputVolume; } }

    /// <summary>What the unit held before hardware mixing, while it runs or is being left; null otherwise.</summary>
    public HardwareEntry? HardwareEntry { get { lock (_gate) return _hardwareEntry; } }

    // The graph has entered the mode this run: the channels are linked to
    // their pairs and the Pro's sink is held at 100%.
    private bool _hardwareGraphEntered;

    /// <summary>
    /// The unit holds the mode, or is about to: no mix of the computer's is
    /// routed to a jack, since what the unit plays there is its own.
    /// </summary>
    private bool RoutesOffJacksLocked => _hardwareEntry is { Leaving: false };

    /// <summary>The unit builds the jacks' mixes and the graph is in the mode: the Pro's sink is held at 100%.</summary>
    private bool UnitBuildsJacksLocked => RoutesOffJacksLocked && _hardwareGraphEntered;

    /// <summary>Whether the graph has entered the mode this run.</summary>
    public bool HardwareGraphEntered { get { lock (_gate) return UnitBuildsJacksLocked; } }

    /// <summary>
    /// Keep <paramref name="entry"/> as the mode's, before anything is written
    /// to the unit: from here no mix is routed to a jack. The caller saves the
    /// settings, then writes the unit and enters the graph.
    /// </summary>
    public void BeginHardwareMixing(HardwareEntry entry)
    {
        lock (_gate)
        {
            _hardwareEntry = entry with { Leaving = false };
            _hardwareGraphEntered = false;
            if (_built) SetMonitorOutputsLocked([.. _monitorOutputs]);
        }
    }

    /// <summary>The Pro's raw sink while a jack is selected, by the jack's bare name.</summary>
    private string? JackSinkLocked()
        => _monitorOutputs.FirstOrDefault(o => o.Contains('#') && !o.EndsWith("#usbaux", StringComparison.Ordinal)) is { } jack
            ? StripMarker(jack) : null;

    /// <summary>Set the mixing choice. The caller saves the settings; the next sweep enters or leaves.</summary>
    public void SetMixing(string mixing)
    {
        if (!MixingMode.IsValid(mixing)) throw new ArgumentException("mixing must be 'software' or 'hardware'", nameof(mixing));
        lock (_gate) _mixing = mixing;
    }

    /// <summary>Keep the pairs and sections a running plan uses, so they stay across restarts. True when they changed.</summary>
    private bool KeepHardwareMapsLocked(IReadOnlyDictionary<string, int> pairs, IReadOnlyDictionary<string, int> sections)
    {
        bool changed = false;
        foreach ((string channel, int pair) in pairs)
            if (!_hardwarePairs.TryGetValue(channel, out int was) || was != pair)
            {
                // A pair another channel kept goes to this one now.
                foreach (string other in _hardwarePairs.Where(p => p.Value == pair && p.Key != channel).Select(p => p.Key).ToList())
                    _hardwarePairs.Remove(other);
                _hardwarePairs[channel] = pair;
                changed = true;
            }
        foreach ((string feed, int section) in sections)
            if (!_hardwareSections.TryGetValue(feed, out int was) || was != section)
            {
                foreach (string other in _hardwareSections.Where(p => p.Value == section && p.Key != feed).Select(p => p.Key).ToList())
                    _hardwareSections.Remove(other);
                _hardwareSections[feed] = section;
                changed = true;
            }
        return changed;
    }

    /// <summary>
    /// Enter hardware mixing in the graph, after the unit's sections were
    /// written with every membership clear and the jacks' levels lowered:
    /// every route off the jacks, the Pro's sink held at 100%, each channel
    /// linked to its pair. True when the kept maps changed, so the caller
    /// saves the settings.
    /// </summary>
    public bool EnterHardwareGraph(IReadOnlyDictionary<string, int> pairs, IReadOnlyDictionary<string, int> sections)
    {
        lock (_gate)
        {
            if (!RoutesOffJacksLocked || !_built) return false;
            bool changed = KeepHardwareMapsLocked(pairs, sections);
            _hardwareGraphEntered = true;
            SetMonitorOutputsLocked([.. _monitorOutputs]);
            PinProSinkLocked();
            LinkHardwarePairsLocked(pairs);
            return changed;
        }
    }

    /// <summary>
    /// Keep the running mode in step: the maps, the links of channels added,
    /// removed or moved, links that died, and the Pro's sink at 100%. True
    /// when the maps changed, so the caller saves the settings.
    /// </summary>
    public bool DriveHardwareMixing(IReadOnlyDictionary<string, int> pairs, IReadOnlyDictionary<string, int> sections)
    {
        lock (_gate)
        {
            if (!UnitBuildsJacksLocked) return false;
            bool changed = KeepHardwareMapsLocked(pairs, sections);
            if (!_built) return changed;
            PinProSinkLocked();
            LinkHardwarePairsLocked(pairs);
            return changed;
        }
    }

    /// <summary>Replace the running entry: a jack's base level the owner moved on the unit. The caller saves the settings.</summary>
    public void KeepHardwareEntry(HardwareEntry entry)
    {
        lock (_gate)
            if (_hardwareEntry is not null) _hardwareEntry = entry with { Leaving = _hardwareEntry.Leaving };
    }

    /// <summary>
    /// Leave hardware mixing in the graph, after every membership of the
    /// unit's sections was cleared: unlink the pairs, give the Pro's sink
    /// its volume back and route the mixes to the jacks again. The entry
    /// stays, marked leaving, until the unit is put back. The caller saves.
    /// </summary>
    public void LeaveHardwareMixing()
    {
        lock (_gate)
        {
            if (_hardwareEntry is not { Leaving: false } entry) return;
            UnlinkHardwarePairsLocked();
            _hardwareEntry = entry with { Leaving = true };
            _hardwareGraphEntered = false;
            if (!_built) return;
            if (JackSinkLocked() is { } pro)
            {
                try { _pw.SetSinkVolume(pro, entry.OutputVolume ?? 1.0); }
                catch (InvalidOperationException) { /* the unit left; its sink comes back at its own volume */ }
            }
            SetMonitorOutputsLocked([.. _monitorOutputs]);
        }
    }

    /// <summary>The unit is back as it was before the mode: the entry goes. The caller saves.</summary>
    public void ForgetHardwareEntry()
    {
        lock (_gate)
        {
            _hardwareEntry = null;
            _hardwareGraphEntered = false;
        }
    }

    /// <summary>
    /// Hold the Pro's sink at 100% while the unit builds the jacks' mixes: its
    /// volume would move every channel against the unit's own inputs, and the
    /// Aux mix with them. The jacks' output volume moves their headphone
    /// volumes instead (see <see cref="HardwareEntry.OutputVolume"/>).
    /// </summary>
    private void PinProSinkLocked()
    {
        if (JackSinkLocked() is not { } pro) return;
        try
        {
            if (_pw.GetSinkVolume(pro) is double volume && Math.Abs(volume - 1.0) > 0.005) _pw.SetSinkVolume(pro, 1.0);
        }
        catch (InvalidOperationException) { /* the unit left */ }
    }

    /// <summary>
    /// Link every channel that needs a pair into it on the Pro's sink, from
    /// the channel's monitor: the audio its sends start from. A link to a
    /// pair the channel no longer has, or of a channel gone, is undone first,
    /// so two channels never share a pair.
    /// </summary>
    private void LinkHardwarePairsLocked(IReadOnlyDictionary<string, int> pairs)
    {
        string? pro = JackSinkLocked() ?? HardwareOutputSinkLocked();
        foreach (string channel in _hardwareLinks.Keys.ToList())
            if (pro is null || !pairs.TryGetValue(channel, out int pair) || _linkedPairs.GetValueOrDefault(channel, -1) != pair)
            {
                _pw.Unlink(_hardwareLinks[channel]);
                _hardwareLinks.Remove(channel);
                _linkedPairs.Remove(channel);
            }
        if (pro is null || HoldPlaybackLocked(pro)) return;
        foreach ((string channel, int pair) in pairs)
        {
            if (_hardwareLinks.TryGetValue(channel, out PortLink? link))
            {
                if (_pw.EnsureLinks(link) != LinkHealth.Broken && link.Complete) continue;
                _pw.Unlink(link);
            }
            if (_config.Channels.FirstOrDefault(c => c.Id == channel) is not { } definition) continue;
            _hardwareLinks[channel] = _pw.RouteTapToPair(definition.SinkName, "monitor", pro, pair);
            _linkedPairs[channel] = pair;
        }
    }

    private void UnlinkHardwarePairsLocked()
    {
        foreach (PortLink link in _hardwareLinks.Values) _pw.Unlink(link);
        _hardwareLinks.Clear();
        _linkedPairs.Clear();
    }

    /// <summary>The channels' pairs as linked now, for tests and diagnostics.</summary>
    internal IReadOnlyDictionary<string, int> LinkedPairs { get { lock (_gate) return new Dictionary<string, int>(_linkedPairs); } }
}
