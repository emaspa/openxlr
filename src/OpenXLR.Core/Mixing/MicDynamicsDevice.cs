using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Where the compressor and the expander of XLR 1 and XLR 2 run, by the
/// rule the EQ follows: on the unit's own DSP when the channel carries a
/// unit's input, that unit runs them (<see cref="UnitEqSupport.Dynamics"/>),
/// the channel's choice is <c>device</c>, no Sound Check session runs on it
/// and the unit has kept what was written to it; otherwise in the software
/// stage. One choice covers both, since the unit holds them in one struct. A
/// capture input's always run in software.
///
/// Moving between the two switches the leaving side off first, as the EQ
/// does: the unit's two switches are written off and software takes over
/// once the unit confirms it, after <see cref="EqReleaseWait"/> at most;
/// moving to the unit, the stage's compressor and expander are switched off
/// before the unit is told. While software runs them, or they are off, the
/// unit holds both switches off, so a compressor left on by another program
/// cannot stack on OpenXLR's.
///
/// A microphone that has no compressor or expander setting of its own when
/// its unit first takes them adopts what the unit holds, so a unit whose
/// own switch was on keeps compressing after an upgrade.
/// </summary>
public sealed partial class Mixer
{
    // Where each microphone's compressor and expander run when its unit can
    // run them; a channel absent here takes the default, the unit. Machine
    // wiring, saved in mixer.json, never part of a profile.
    private readonly Dictionary<string, string> _dynamicsProcessing = new(StringComparer.Ordinal);
    // The channels whose compressor and expander their unit runs now.
    private readonly Dictionary<string, DeviceDynamicsBackend> _dynamicsDevices = new(StringComparer.Ordinal);
    // The channels leaving a unit: its switches are posted off, and software
    // takes over once the unit confirms it, or after EqReleaseWait.
    private readonly Dictionary<string, (DeviceDynamicsBackend Backend, long Deadline)> _dynamicsReleasing = new(StringComparer.Ordinal);
    // Unit inputs that did not keep what was written to them in this run,
    // with the reason; switching either processor or the choice off and on
    // tries again.
    private readonly Dictionary<string, string> _dynamicsDeviceFailed = new(StringComparer.Ordinal);

    private static string DynamicsKey(EqUnitInput input) => $"dyndev:{input.Unit}:{input.Input}";

    /// <summary>
    /// The channel's choice of where its compressor and expander run:
    /// <c>device</c> unless set to <c>software</c>. A capture input's is always
    /// <c>software</c>.
    /// </summary>
    public string DynamicsProcessingFor(string channel) { lock (_gate) return DynamicsProcessingLocked(channel); }

    private string DynamicsProcessingLocked(string channel)
        => !IsMicrophoneId(channel) ? UnitDynamics.Software : _dynamicsProcessing.GetValueOrDefault(channel) ?? UnitDynamics.Device;

    /// <summary>Whether a channel's compressor and expander run on its unit now.</summary>
    public bool DynamicsRunOnDevice(string channel) { lock (_gate) return _dynamicsDevices.ContainsKey(channel); }

    /// <summary>What <c>setDynamicsProcessing</c> on a capture input is refused with.</summary>
    public static string DynamicsSoftwareOnly(string channel)
        => $"setDynamicsProcessing: '{channel}' is a capture input, whose compressor and expander always run on this computer";

    /// <summary>Where a microphone's compressor and expander run when its unit can run them: <c>device</c> or <c>software</c>.</summary>
    public void SetDynamicsProcessing(string channel, string value)
    {
        if (!UnitDynamics.IsValid(value)) throw new ArgumentException("setDynamicsProcessing: value must be 'device' or 'software'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            if (!IsMicrophoneId(channel)) throw new ArgumentException(DynamicsSoftwareOnly(channel));
            if (value == UnitDynamics.Device) _dynamicsProcessing.Remove(channel);
            else _dynamicsProcessing[channel] = value;
            ForgetDeviceDynamicsFailureLocked(channel);
            ApplyEqLocked(channel);
        }
    }

    private void ForgetDeviceDynamicsFailureLocked(string channel)
    {
        if (UnitInputsLocked().TryGetValue(channel, out UnitInputStatus? unit))
            _dynamicsDeviceFailed.Remove(DynamicsKey(new EqUnitInput(unit.Unit, unit.Input)));
    }

    /// <summary>
    /// The unit input that runs a channel's compressor and expander, or null
    /// for software, by the rule of <see cref="EqDeviceTargetLocked"/>.
    /// </summary>
    private EqUnitInput? DynamicsDeviceTargetLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        if (_eqPort is null || !IsMicrophoneId(channel) || DynamicsProcessingLocked(channel) != UnitDynamics.Device) return null;
        if (_soundCheckClaim == channel || _soundCheck is not null && _soundCheckChannel == channel) return null;
        if (!EqCarrierLocked(channel, carried, out UnitInputStatus? unit, out UnitEqSupport? support) || support.Dynamics is not { Ready: true })
            return null;
        var target = new EqUnitInput(unit.Unit, unit.Input);
        return _dynamicsDeviceFailed.ContainsKey(DynamicsKey(target)) ? null : target;
    }

    /// <summary>Whether a route built now would run the channel's compressor or expander in its software stage.</summary>
    internal bool DynamicsWantsSoftwareStage(string channel) { lock (_gate) return CarriesMicrophoneStageLocked(channel) && DynamicsWantedLocked(channel); }

    /// <summary>Whether a channel's compressor and expander are software's to run: no unit runs them and none is still letting them go.</summary>
    private bool DynamicsInSoftwareLocked(string channel) => !_dynamicsDevices.ContainsKey(channel) && !_dynamicsReleasing.ContainsKey(channel);

    /// <summary>The compressor the software stage runs: the channel's, switched off while a unit has it.</summary>
    private MicCompressor SoftwareCompressorLocked(string channel)
    {
        MicCompressor c = CompressorLocked(channel);
        return c.Enabled && !DynamicsInSoftwareLocked(channel) ? c with { Enabled = false } : c;
    }

    /// <summary>The expander the software stage runs: the channel's, switched off while a unit has it.</summary>
    private MicExpander SoftwareExpanderLocked(string channel)
    {
        MicExpander e = ExpanderLocked(channel);
        return e.Enabled && !DynamicsInSoftwareLocked(channel) ? e with { Enabled = false } : e;
    }

    /// <summary>
    /// The device side of <see cref="SyncDeviceEqLocked"/> for the compressor
    /// and the expander: put each microphone's where the rule says they run,
    /// follow a confirmed release and a failure, tell a unit that connected
    /// again, and hold the switches off where software runs them. True when
    /// what clients see changed.
    /// </summary>
    private bool SyncDeviceDynamicsLocked(IDeviceEqPort port, IReadOnlyDictionary<string, UnitInputStatus> carried, long now)
    {
        bool changed = false;
        foreach (ChannelDefinition ch in _config.Channels.Where(c => IsMicrophoneId(c.Id)).ToList())
        {
            string id = ch.Id;
            _dynamicsDevices.TryGetValue(id, out DeviceDynamicsBackend? running);
            if (running?.Status is { Phase: DeviceEqPhase.Failed } failed)
            {
                string reason = failed.Reason ?? "did not keep the compressor and expander written to it";
                _dynamicsDeviceFailed[DynamicsKey(running.Target)] = reason;
                string unitName = _unitEq.TryGetValue(running.Target.Unit, out UnitEqSupport? named) ? named.Name : running.Target.Unit;
                _pw.Note($"{ChannelNameLocked(ch)}'s compressor and expander run in software: the {unitName} {reason}.");
            }
            EqUnitInput? want = DynamicsDeviceTargetLocked(id, carried);
            // A microphone with no setting of its own waits for the unit's
            // first read, so it can take what the unit holds: writing first
            // would switch off what it is about to adopt.
            if (want is { } waiting && running is null && NeedsAdoptionLocked(id) && port.HeldDynamics(waiting.Unit, waiting.Input) is null)
                continue;
            if (running?.Target != want)
            {
                if (running is not null)
                {
                    _dynamicsDevices.Remove(id);
                    if (_unitEq.ContainsKey(running.Target.Unit))
                    {
                        running.Release();
                        _dynamicsReleasing[id] = (running, now + (long)EqReleaseWait.TotalMilliseconds);
                    }
                    else ApplySoftwareEqLocked(id);
                }
                if (want is { } target)
                {
                    AdoptUnitDynamicsLocked(ch, port, target);
                    var backend = new DeviceDynamicsBackend(port, target, ChannelNameLocked(ch));
                    _dynamicsDevices[id] = backend;
                    ApplySoftwareEqLocked(id);
                    backend.Apply(CompressorLocked(id), ExpanderLocked(id));
                }
                changed = true;
            }
            else running?.Apply(CompressorLocked(id), ExpanderLocked(id));
            if (_dynamicsReleasing.TryGetValue(id, out var leaving)
                && (leaving.Backend.Status is { IsOff: true } or { Phase: DeviceEqPhase.Failed } || now >= leaving.Deadline))
            {
                _dynamicsReleasing.Remove(id);
                ApplySoftwareEqLocked(id);
                changed = true;
            }
            // Software runs them, or a unit cannot: the unit's own switches
            // are held off once per connection, so they never stack.
            if (want is null && !_dynamicsReleasing.ContainsKey(id)
                && EqCarrierLocked(id, carried, out UnitInputStatus? unit, out UnitEqSupport? support)
                && support.Dynamics is { Ready: true }
                && port.DynamicsStatus(unit.Unit, unit.Input).Phase == DeviceEqPhase.None)
                port.PostDynamics(unit.Unit, unit.Input, DeviceDynamics.Off, ChannelNameLocked(ch));
        }
        HoldUncarriedDynamicsOffLocked(port, carried);
        return changed;
    }

    /// <summary>
    /// A microphone with no compressor and no expander setting of its own
    /// takes what its unit holds the first time the unit runs them, with the
    /// knee and hold at their defaults. Said once in the journal.
    /// </summary>
    private void AdoptUnitDynamicsLocked(ChannelDefinition ch, IDeviceEqPort port, EqUnitInput target)
    {
        if (!NeedsAdoptionLocked(ch.Id)) return;
        if (port.HeldDynamics(target.Unit, target.Input) is not { } held || DeviceDynamicsMapping.FromDevice(held) is not { } adopted) return;
        _compressor[ch.Id] = adopted.Compressor;
        _expander[ch.Id] = adopted.Expander;
        string unitName = _unitEq.TryGetValue(target.Unit, out UnitEqSupport? named) ? named.Name : target.Unit;
        _pw.Note($"{ChannelNameLocked(ch)} takes the compressor ({(held.Compressor ? "on" : "off")}) and expander ({(held.Expander ? "on" : "off")}) the {unitName} held.");
    }

    /// <summary>Whether a microphone has neither a compressor nor an expander setting of its own, set or read from a file.</summary>
    private bool NeedsAdoptionLocked(string channel) => !_compressor.ContainsKey(channel) && !_expander.ContainsKey(channel);

    /// <summary>
    /// The primary unit's inputs that no microphone carries hold their
    /// compressor and expander off, said once per connection, as
    /// <see cref="HoldUncarriedInputsOffLocked"/> does for the EQ.
    /// </summary>
    private void HoldUncarriedDynamicsOffLocked(IDeviceEqPort port, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        if (_drivenUnit is not { } primary || !_unitEq.TryGetValue(primary, out UnitEqSupport? support) || support.Dynamics is not { Ready: true })
            return;
        var taken = new HashSet<EqUnitInput>(carried.Where(c => IsMicrophoneId(c.Key)).Select(c => new EqUnitInput(c.Value.Unit, c.Value.Input)));
        taken.UnionWith(_dynamicsDevices.Values.Select(b => b.Target));
        taken.UnionWith(_dynamicsReleasing.Values.Select(r => r.Backend.Target));
        for (int input = 1; input <= support.Inputs; input++)
        {
            if (taken.Contains(new EqUnitInput(primary, input)) || port.DynamicsStatus(primary, input).Phase != DeviceEqPhase.None) continue;
            string owner = _config.Channels.FirstOrDefault(c => c.InputPair == input - 1) is { } strip
                ? ChannelNameLocked(strip) : $"{support.Name} input {input}";
            port.PostDynamics(primary, input, DeviceDynamics.Off, owner);
        }
    }

    /// <summary>
    /// The unit a microphone carries, while that unit's model runs a
    /// compressor and an expander. Never on a capture input.
    /// </summary>
    private MicEqDevice? DynamicsDeviceStateLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        if (!IsMicrophoneId(channel) || !EqCarrierLocked(channel, carried, out UnitInputStatus? unit, out UnitEqSupport? support)
            || support.Dynamics is not { } dynamics) return null;
        string? failure = _dynamicsDeviceFailed.GetValueOrDefault(DynamicsKey(new EqUnitInput(unit.Unit, unit.Input)));
        return new MicEqDevice(unit.Unit, unit.Input, support.Name, support.Firmware, dynamics.Ready && failure is null,
            failure is null ? dynamics.Reason : EqDeviceFailedReason,
            failure is null ? dynamics.Note : $"The {support.Name} {failure}, so the compressor and expander run in software.")
        {
            MinimumFirmware = support.MinimumFirmware,
            Levels = support.DynamicsLevels,
        };
    }

    /// <summary>Which side runs a channel's compressor and expander for the state: <c>device</c>, <c>software</c> or null.</summary>
    private string? DynamicsBackendLocked(string channel)
        => _dynamicsDevices.ContainsKey(channel) ? UnitDynamics.Device
            : _eqStages.TryGetValue(channel, out SoftwareEqBackend? stage) && stage.IsAlive && DynamicsWantedLocked(channel) ? UnitDynamics.Software
            : null;
}
