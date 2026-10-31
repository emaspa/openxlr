using System.Collections.Frozen;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>The primary unit's ducking verdict and cached readback, for the daemon's state.</summary>
public sealed record DuckingDeviceState(string Unit, string Name, string? Firmware, bool Ready,
    string? Reason, string? Note, IReadOnlyList<string> Outputs, bool Held, DuckingReadback? Readback);

/// <summary>The saved setup, actual backend, software availability and primary unit verdict.</summary>
public sealed record DuckingStateSnapshot(DuckingSetup Setup, string? Backend, bool Available,
    string? Error, DuckingDeviceState? Device);

public sealed partial class Mixer
{
    private DuckingSetup? _duckingSetup;
    // Loading a scene or settings does not opt the daemon into this owner.
    // The caller opts in through the device port or the ducking sweep.
    private bool _duckingManaged;
    private IDeviceDuckingPort? _duckingPort;
    private string? _duckingUnit;
    private UnitDuckingSupport? _unitDucking;
    private DeviceDuckingBackend? _duckingWriter;
    private bool _duckingOnDevice;
    private long? _duckingReleaseDeadline;
    private readonly HashSet<string> _duckingFailed = new(StringComparer.Ordinal);
    private DuckingStateSnapshot? _lastDuckingSnapshot;

    /// <summary>The maximum wait for verified off. The device side drops a unit whose transfers time out.</summary>
    internal static readonly TimeSpan DuckingReleaseWait = TimeSpan.FromSeconds(1);

    private DuckingSetup DuckingSetupLocked() => _duckingSetup ??= DuckingSetup.Default(_config);

    /// <summary>
    /// Validate and save the whole setup before applying it. The supplied
    /// writer receives the complete mixer settings, normally saved with
    /// MixerSettings.Save. A failed save leaves the setup and both backends
    /// alone. Processing is included here for setDuckingProcessing too.
    /// </summary>
    public void SetDuckingSetup(DuckingSetup setup, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (DuckingSetup.Validate(setup, this) is { } error) throw new ArgumentException($"setDucking: {error}");
            DuckingSetup previous = DuckingSetupLocked();
            DuckingSetup next = setup.Copy();
            if (persist(ExportSettings() with { Ducking = next.Copy() }) is { } problem) throw new IOException(problem);
            _duckingSetup = next;
            _duckingManaged = true;
            if (next.Enabled && !previous.Enabled) _duckingFailed.Clear();
            SyncDuckingLocked();
        }
    }

    /// <summary>
    /// The device side, attached only in software mixer mode, as for the EQ.
    /// Null detaches it without posting to a unit. Attach after building the
    /// graph; tests can attach a fake port without a PipeWire graph.
    /// </summary>
    public void SetDeviceDuckingPort(IDeviceDuckingPort? port)
    {
        lock (_gate)
        {
            if (ReferenceEquals(port, _duckingPort)) return;
            _duckingPort = port;
            _duckingManaged = port is not null;
            _duckingWriter = null;
            _duckingOnDevice = false;
            _duckingReleaseDeadline = null;
        }
    }

    /// <summary>
    /// The primary driven unit's hardware ducking support. Pass nulls when
    /// it leaves or has no ducking capability. Additional units never run
    /// this setup. True when this changes the unit information.
    /// </summary>
    public bool SetUnitDuckingSupport(string? unit, UnitDuckingSupport? support)
    {
        if ((unit is null) != (support is null)) throw new ArgumentException("unit and support must both be present or absent");
        lock (_gate)
        {
            if (_duckingUnit == unit && _unitDucking == support) return false;
            _duckingUnit = unit;
            _unitDucking = support;
            SyncDuckingLocked();
            return true;
        }
    }

    /// <summary>
    /// Reevaluate placement and advance the off-before-on hand-over. Call
    /// on each sweep after routing and device status updates. Also keep
    /// calling EnsureFilterRoutes, which alone repairs buses and stages.
    /// True when the runtime snapshot changed. No USB work is done here.
    /// </summary>
    public bool SyncDucking()
    {
        lock (_gate)
        {
            _duckingManaged = true;
            return SyncDuckingLocked();
        }
    }

    private bool SyncDuckingLocked()
    {
        if (!_duckingManaged) return false;
        SyncDuckingBackendLocked();
        DuckingStateSnapshot next = DuckingSnapshotLocked();
        DuckingStateSnapshot? previous = _lastDuckingSnapshot;
        _lastDuckingSnapshot = next;
        return previous is null || previous.Backend != next.Backend || previous.Available != next.Available
            || previous.Error != next.Error || previous.Device?.Unit != next.Device?.Unit
            || previous.Device?.Name != next.Device?.Name || previous.Device?.Firmware != next.Device?.Firmware
            || previous.Device?.Ready != next.Device?.Ready || previous.Device?.Reason != next.Device?.Reason
            || previous.Device?.Note != next.Device?.Note || previous.Device?.Held != next.Device?.Held
            || previous.Device?.Readback != next.Device?.Readback
            || !(previous.Device?.Outputs ?? []).SequenceEqual(next.Device?.Outputs ?? []);
    }

    private void SyncDuckingBackendLocked()
    {
        DuckingSetup setup = DuckingSetupLocked();
        DuckingView view = DuckingViewLocked();
        if (!view.MixerRunning)
        {
            _duckingWriter = null;
            _duckingOnDevice = false;
            _duckingReleaseDeadline = null;
            SetDuckingPlan(null);
            return;
        }
        string? unit = _duckingUnit == _drivenUnit ? _duckingUnit : null;
        if (_duckingWriter?.Unit != unit)
        {
            // The old primary no longer carries this graph. Never write to
            // a unit that has left; its absence also completes a release.
            _duckingWriter = null;
            _duckingOnDevice = false;
            _duckingReleaseDeadline = null;
        }
        bool connected = false;
        if (_duckingWriter is null && unit is not null && _duckingPort is { } port)
        {
            _duckingWriter = new(port, unit);
            connected = true;
        }
        if (_duckingOnDevice && _duckingWriter?.Status.Phase == DeviceDuckingPhase.Failed)
            _duckingFailed.Add(_duckingWriter.Unit);
        view = view with { Failed = unit is not null && _duckingFailed.Contains(unit) };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, view, unit is null ? null : _unitDucking);
        bool wantDevice = verdict.Backend == ProDucking.Device && _duckingWriter is not null;
        string owner = _config.Channels.FirstOrDefault(c => c.Id == setup.Key)?.Name ?? setup.Key;
        ProDuckingTarget target = DuckingTarget(setup, verdict.Outputs);

        if (wantDevice)
        {
            // Removing the plan unlinks stage outputs before restoring the
            // direct cells. Only then may the device side see an on post.
            SetDuckingPlan(null);
            _duckingReleaseDeadline = null;
            _duckingWriter!.Apply(target, owner);
            _duckingOnDevice = true;
            return;
        }

        if (_duckingWriter is { } writer && (connected || _duckingOnDevice || writer.Status.Phase == DeviceDuckingPhase.None))
        {
            // An off post cannot double a duck, so it goes first. The stages
            // wait only while the unit may still hold ducking on; a post the
            // device side dropped (the unit left before the mixer heard of
            // it) leaves them running instead of rebuilding them every sweep.
            writer.Apply(target with { Enabled = false }, owner);
            if (!writer.Status.IsOff)
            {
                SetDuckingPlan(null);
                _duckingReleaseDeadline ??= NowMs + (long)DuckingReleaseWait.TotalMilliseconds;
            }
        }
        _duckingOnDevice = false;
        if (_duckingReleaseDeadline is { } deadline)
        {
            if (_duckingWriter?.Status.IsOff != true && NowMs < deadline) return;
            _duckingReleaseDeadline = null;
        }
        // A custom layout can even lack the fallback xlr1. Pruning keeps
        // the saved key word but an invalid graph plan must never reach D2.
        DuckingPlan? plan = _config.Channels.Any(c => c.Id == setup.Key && c.Microphone) ? setup.ToPlan() : null;
        if (!SameDuckingPlan(_duckPlan, plan)) SetDuckingPlan(plan);
    }

    private static bool SameDuckingPlan(DuckingPlan? left, DuckingPlan? right)
        => left is null ? right is null : right is not null && left.Key == right.Key && left.Enabled == right.Enabled
            && left.ThresholdDb == right.ThresholdDb && left.DepthDb == right.DepthDb
            && left.AttackMs == right.AttackMs && left.ReleaseMs == right.ReleaseMs && left.Cells.SetEquals(right.Cells);

    private static ProDuckingTarget DuckingTarget(DuckingSetup setup, IReadOnlyList<string> outputs)
        => new(setup.Enabled && setup.Channels.Count > 0 && setup.Mixes.Count > 0,
            setup.Sensitivity, setup.Amount, setup.Speed, outputs.Select(ProDucking.PairOf).ToFrozenSet());

    private DuckingView DuckingViewLocked()
    {
        DuckingSetup setup = DuckingSetupLocked();
        bool carrier = UnitInputsLocked().TryGetValue(setup.Key, out UnitInputStatus? input)
            && input.Primary && input.Input == 1 && input.Unit == _duckingUnit;
        IReadOnlyList<string> feed = MonitorFeed.Parts(JackFeedLocked());
        var mixes = new List<DuckingMixView>();
        foreach (MixDefinition mix in _config.Mixes)
        {
            var consumers = new List<string>();
            bool jacks = false;
            foreach (string output in _monitorOutputs)
            {
                if (_monitorMuted || !MixesForOutputLocked(output).Any(m => m.Id == mix.Id)) continue;
                if (_hardwareOutputRouting && output.Contains('#') && IsDrivenUnitSinkLocked(output)) jacks = true;
                else consumers.Add(_outputNames.GetValueOrDefault(output.Split('#')[0], output));
            }
            var open = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ChannelDefinition channel in _config.Channels)
            {
                string cell = Cell(channel.Id, mix.Id);
                double level = _levels.GetValueOrDefault(cell, channel.Levels.GetValueOrDefault(mix.Id));
                bool muted = _cells.Contains(cell) ? _muted.Contains(cell) : channel.MutedIn.Contains(mix.Id);
                if (level > 0 && ChannelVolumeLocked(channel.Id) > 0 && !muted && !ExclusivePeerPendingLocked(channel.Id)
                    && !_mixMuted.Contains(mix.Id) && _mixVolume.GetValueOrDefault(mix.Id, mix.Volume) > 0)
                    open[channel.Id] = ChannelNameLocked(channel);
            }
            bool keyInSoftware = open.ContainsKey(setup.Key)
                && !(IsHardwareMicLocked(setup.Key) && mix.Kind == MixKind.Monitor && feed.Contains(mix.Id));
            mixes.Add(new(mix.Id, mix.Name, mix.Kind, jacks,
                mix.Kind == MixKind.AuxPort && _auxPortEnabled && _hardwareOutputRouting,
                consumers, open, keyInSoftware));
        }
        return new(_built || _duckingPort is not null, carrier, mixes, feed,
            DuckingStatus.RunningMixes.Count > 0, _duckingUnit is not null && _duckingFailed.Contains(_duckingUnit));
    }

    /// <summary>A detached snapshot. Device is null without hardware ducking on the primary driven unit.</summary>
    public DuckingStateSnapshot DuckingSnapshot() { lock (_gate) return DuckingSnapshotLocked(); }

    private DuckingStateSnapshot DuckingSnapshotLocked()
    {
        DuckingSetup setup = DuckingSetupLocked();
        DuckingRuntime runtime = DuckingStatus;
        UnitDuckingSupport? support = _duckingUnit == _drivenUnit ? _unitDucking : null;
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, DuckingViewLocked(), support);
        DeviceDuckingStatus? status = _duckingWriter?.Status;
        DuckingDeviceState? device = support is null ? null : new(_duckingUnit!, support.Name, support.Firmware,
            verdict.Ready, verdict.Reason, verdict.Note, [.. verdict.Outputs], status?.Phase == DeviceDuckingPhase.Verified,
            status?.Readback);
        return new(setup.Copy(), _duckingOnDevice ? ProDucking.Device : runtime.RunningMixes.Count > 0 ? "software" : null,
            runtime.Available, runtime.Error, device);
    }
}
