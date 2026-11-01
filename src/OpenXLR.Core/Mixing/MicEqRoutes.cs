using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The microphone EQ on XLR 1, XLR 2 and every capture input on one mono
/// port. On XLR 1 and XLR 2 it runs on the unit's own DSP when
/// the channel carries a unit input whose unit runs it (the Wave XLR MK.2 and
/// the XLR Dock MK.2 from firmware 1.0.15, the Wave:3 MK.2 from 0.2.3, each
/// of the Wave XLR Pro's two inputs from 1.0.14), the
/// channel's choice is <c>device</c>, and no Sound
/// Check session runs on it; otherwise in software. A capture input's EQ
/// always runs in software, whatever source feeds it. A unit-run EQ starts no
/// stage: its values go to the device side through <see cref="IDeviceEqPort"/>.
/// Moving between the two switches the leaving one off first, so a voice is
/// never equalised twice.
///
/// The software stage sits at the head of the
/// input's route, between the source (or Sound Check's loop) and the software
/// low cut, ClipGuard and inserts (a capture input has only its inserts):
///
///   source -> OpenXLR_eq_&lt;id&gt;_&lt;gen&gt; -> OpenXLR_lc_&lt;id&gt;_&lt;gen&gt; (when a chain runs) -> OpenXLR_ch_&lt;id&gt;
///
/// The stage is spliced in and out by relinking the head, so the chain
/// behind it keeps running and no plugin restarts. Every value change, the
/// EQ's own switch included, goes to the running stage live; turning the EQ
/// off bypasses inside the stage and keeps it running until the route is
/// next built, so on and off compare at once. The same stage runs the
/// microphone's noise removal and expander ahead of the EQ (<see cref="NoiseFor"/>,
/// <see cref="ExpanderFor"/>) and its compressor and voice changer after it
/// (<see cref="CompressorFor"/>, <see cref="VoiceFor"/>), and a microphone
/// with none of them on when its route is built gets no stage.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What an EQ that cannot run says when this build has no native helper.</summary>
    public const string EqNeedsHost =
        "The microphone EQ needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What an EQ the restart policy has given up on says.</summary>
    internal const string EqGivenUp = "the EQ kept failing and is off; switch it off and on to try again";

    /// <summary>The hardware strips that carry a microphone EQ, which their unit can run: XLR 1 and XLR 2.</summary>
    public static bool IsMicrophoneId(string? id) => id is "xlr1" or "xlr2";

    /// <summary>
    /// Whether a channel carries the microphone EQ: XLR 1, XLR 2, or a
    /// capture input on one mono port. The software stage is mono, so a
    /// capture input on a stereo pair has none.
    /// </summary>
    public static bool CarriesEq(ChannelDefinition channel)
        => IsMicrophoneId(channel.Id) || channel.CaptureSource is not null && channel.Microphone;

    /// <summary>A microphone input of this layout: a channel that carries the EQ.</summary>
    public bool IsMicrophoneChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id && CarriesEq(c)); }

    /// <summary>A microphone whose EQ only ever runs on this computer: a capture input, which has no device choice.</summary>
    public bool EqRunsInSoftwareOnly(string id)
    {
        lock (_gate) return !IsMicrophoneId(id) && _config.Channels.Any(c => c.Id == id && CarriesEq(c));
    }

    private bool CarriesEqLocked(string id) => _config.Channels.Any(c => c.Id == id && CarriesEq(c));

    private static string EqKey(string channel) => "eq:" + channel;

    // Saved values by channel. A microphone absent here has the defaults with
    // the EQ off; an entry for any other channel, read from a file, is kept
    // for the save.
    private readonly Dictionary<string, MicEq> _eq = new(StringComparer.Ordinal);
    // The running stages, the link from each stage's upstream (the source or
    // the loop helper) into it and that upstream's node, and why a stage
    // could not start.
    private readonly Dictionary<string, SoftwareEqBackend> _eqStages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortLink> _eqInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eqUpstreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eqErrors = new(StringComparer.Ordinal);
    // The node each strip's feed comes from: its EQ stage, the loop helper or
    // the source. A feed is reused only from the same node.
    private readonly Dictionary<string, string> _inputHeads = new(StringComparer.Ordinal);
    private long _eqGeneration;

    // Where each microphone's EQ runs when its unit can run it; a channel
    // absent here takes the default, the unit. Machine wiring, saved in
    // mixer.json, never part of a profile.
    private readonly Dictionary<string, string> _eqProcessing = new(StringComparer.Ordinal);
    // The device side, set once the graph is built; without it every EQ runs in software.
    private IDeviceEqPort? _eqPort;
    // The units that can run the EQ, by instance id, driven primary or additional.
    private IReadOnlyDictionary<string, UnitEqSupport> _unitEq = new Dictionary<string, UnitEqSupport>(StringComparer.Ordinal);
    // The channels whose EQ their unit runs now.
    private readonly Dictionary<string, DeviceEqBackend> _eqDevices = new(StringComparer.Ordinal);
    // The channels leaving a unit: its switch is posted off, and software
    // takes over once the unit confirms it, or after EqReleaseWait.
    private readonly Dictionary<string, (DeviceEqBackend Backend, long Deadline)> _eqReleasing = new(StringComparer.Ordinal);
    // Unit inputs that did not keep the EQ written to them in this run, with
    // the reason; switching the EQ or the choice off and on tries again.
    private readonly Dictionary<string, string> _eqDeviceFailed = new(StringComparer.Ordinal);
    // A channel Sound Check is about to record on: its EQ leaves the unit first.
    private string? _soundCheckClaim;

    /// <summary>
    /// The longest a hand-over from the unit waits for the unit to confirm its
    /// EQ is off. A unit that does not answer within it has failed a transfer
    /// and been dropped.
    /// </summary>
    internal static readonly TimeSpan EqReleaseWait = TimeSpan.FromSeconds(1);

    /// <summary>The clock for the hand-over's wait; tests drive it by hand.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    private long NowMs => (long)((Int128)Time.GetTimestamp() * 1000 / Time.TimestampFrequency);

    /// <summary>What a unit input that did not keep a written EQ reports; its channel runs the EQ in software.</summary>
    internal const string EqDeviceFailedReason = "failed";

    /// <summary>A channel's EQ: what was set or recalled, else the defaults with the EQ off.</summary>
    public MicEq EqFor(string channel) { lock (_gate) return EqLocked(channel); }

    private MicEq EqLocked(string channel) => _eq.GetValueOrDefault(channel) ?? MicEq.Default;

    /// <summary>
    /// The channel's choice of where its EQ runs: <c>device</c> unless set to
    /// <c>software</c>. A capture input's is always <c>software</c>.
    /// </summary>
    public string EqProcessingFor(string channel) { lock (_gate) return EqProcessingLocked(channel); }

    private string EqProcessingLocked(string channel)
        => !IsMicrophoneId(channel) ? MicEqProcessing.Software : _eqProcessing.GetValueOrDefault(channel) ?? MicEqProcessing.Device;

    /// <summary>Whether a channel's EQ runs on its unit now.</summary>
    public bool EqRunsOnDevice(string channel) { lock (_gate) return _eqDevices.ContainsKey(channel); }

    public bool CanRunEqOnDevice(string channel, int bands)
    {
        lock (_gate) return bands is >= MicEq.MinimumBands and <= WaveFxEq.Bands
            && EqDeviceTargetLocked(channel, UnitInputsLocked(), bands) is not null;
    }

    /// <summary>
    /// The device side. Set once the graph is built; until then, and in a
    /// run without the submixer, nothing is written to any unit.
    /// </summary>
    public void SetDeviceEqPort(IDeviceEqPort? port)
    {
        lock (_gate) _eqPort = port;
    }

    /// <summary>
    /// The attached units whose model has a hardware EQ, by instance id, with
    /// whether each runs it now. True when that changed what clients see.
    /// </summary>
    public bool SetUnitEqSupport(IReadOnlyDictionary<string, UnitEqSupport> units)
    {
        lock (_gate)
        {
            if (units.Count == _unitEq.Count && units.All(u => _unitEq.TryGetValue(u.Key, out UnitEqSupport? was) && was == u.Value))
                return false;
            _unitEq = new Dictionary<string, UnitEqSupport>(units, StringComparer.Ordinal);
            SyncDeviceEqLocked();
            return true;
        }
    }

    /// <summary>
    /// Put every microphone's EQ where the rule says it runs, and follow the
    /// device side: a confirmed release hands over to software, a unit that
    /// did not keep its EQ is left for software, a unit that connected again
    /// is told its EQ again. True when what clients see changed.
    /// </summary>
    public bool SyncDeviceEq() { lock (_gate) return SyncDeviceEqLocked(); }

    /// <summary>Where a microphone's EQ runs when its unit can run it: <c>device</c> or <c>software</c>.</summary>
    public void SetEqProcessing(string channel, string value)
    {
        if (!MicEqProcessing.IsValid(value)) throw new ArgumentException("setEqProcessing: value must be 'device' or 'software'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            if (!IsMicrophoneId(channel)) throw new ArgumentException(EqSoftwareOnly(channel));
            if (value == MicEqProcessing.Device) _eqProcessing.Remove(channel);
            else _eqProcessing[channel] = value;
            ForgetDeviceEqFailureLocked(channel);
            ApplyEqLocked(channel);
        }
    }

    /// <summary>
    /// The unit input that runs a channel's EQ, or null for software: the
    /// channel carries a unit's input (the carrier rule of
    /// <see cref="UnitInputsLocked"/>), that unit runs the EQ, the channel's
    /// choice is the unit, no Sound Check session runs on it, and the unit
    /// has not failed to keep the EQ for that input in this run.
    /// </summary>
    private EqUnitInput? EqDeviceTargetLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried, int? bands = null)
    {
        if (_eqPort is null || !IsMicrophoneId(channel) || EqProcessingLocked(channel) != MicEqProcessing.Device) return null;
        if ((bands ?? EqLocked(channel).Bands.Count) > WaveFxEq.Bands) return null;
        if (_soundCheckClaim == channel || _soundCheck is not null && _soundCheckChannel == channel) return null;
        if (!EqCarrierLocked(channel, carried, out UnitInputStatus? unit, out UnitEqSupport? support) || !support.Ready) return null;
        var target = new EqUnitInput(unit.Unit, unit.Input);
        return _eqDeviceFailed.ContainsKey(target.Key) ? null : target;
    }

    /// <summary>
    /// The unit input a channel carries, while that unit's model has a
    /// hardware EQ and the unit has that input. An input a one-input unit
    /// does not have (XLR 2 while the primary was a two-input unit a moment
    /// ago) carries no EQ at all, so nothing is posted and nothing fails.
    /// </summary>
    private bool EqCarrierLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UnitInputStatus? unit,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UnitEqSupport? support)
    {
        support = null;
        return carried.TryGetValue(channel, out unit) && _unitEq.TryGetValue(unit.Unit, out support) && unit.Input <= support.Inputs;
    }

    /// <summary>What <c>setEqProcessing</c> on a capture input is refused with.</summary>
    public static string EqSoftwareOnly(string channel)
        => $"setEqProcessing: '{channel}' is a capture input, whose EQ always runs on this computer";

    /// <summary>Whether a route built now would run the channel's EQ in a software stage at its head.</summary>
    internal bool EqWantsSoftwareStage(string channel) { lock (_gate) return CarriesEqLocked(channel) && SoftwareEqLocked(channel).Enabled; }

    /// <summary>Whether a route built now would put the microphone stage at the channel's head, for anything in it.</summary>
    internal bool WantsMicrophoneStage(string channel) { lock (_gate) return StageWantedLocked(channel); }

    /// <summary>Whether a channel's EQ is software's to run: no unit runs it and none is still letting it go.</summary>
    private bool EqInSoftwareLocked(string channel) => !_eqDevices.ContainsKey(channel) && !_eqReleasing.ContainsKey(channel);

    /// <summary>The EQ the software stage runs: the channel's, switched off while a unit has it.</summary>
    private MicEq SoftwareEqLocked(string channel)
    {
        MicEq eq = EqLocked(channel);
        return eq.Enabled && !EqInSoftwareLocked(channel) ? eq with { Enabled = false } : eq;
    }

    private void ForgetDeviceEqFailureLocked(string channel)
    {
        if (UnitInputsLocked().TryGetValue(channel, out UnitInputStatus? unit))
            _eqDeviceFailed.Remove(new EqUnitInput(unit.Unit, unit.Input).Key);
    }

    /// <inheritdoc cref="SyncDeviceEq"/>
    private bool SyncDeviceEqLocked()
    {
        if (_eqPort is not { } port) return false;
        bool changed = false;
        Dictionary<string, UnitInputStatus> carried = UnitInputsLocked();
        long now = NowMs;
        foreach (ChannelDefinition ch in _config.Channels.Where(c => IsMicrophoneId(c.Id)).ToList())
        {
            string id = ch.Id;
            _eqDevices.TryGetValue(id, out DeviceEqBackend? running);
            if (running?.Status is { Phase: DeviceEqPhase.Failed } failed)
            {
                string reason = failed.Reason ?? "did not keep the EQ written to it";
                _eqDeviceFailed[running.Target.Key] = reason;
                string unitName = _unitEq.TryGetValue(running.Target.Unit, out UnitEqSupport? named) ? named.Name : running.Target.Unit;
                _pw.Note($"{ChannelNameLocked(ch)}'s EQ runs in software: the {unitName} {reason}.");
            }
            EqUnitInput? want = EqDeviceTargetLocked(id, carried);
            if (running?.Target != want)
            {
                // Off before on: the unit leaving switches its EQ off, and
                // software takes over only once the unit confirms it. A unit
                // that is no longer driven is written nothing: its audio
                // left the channel with it.
                if (running is not null)
                {
                    _eqDevices.Remove(id);
                    if (_unitEq.ContainsKey(running.Target.Unit))
                    {
                        running.Release();
                        _eqReleasing[id] = (running, now + (long)EqReleaseWait.TotalMilliseconds);
                    }
                    else ApplySoftwareEqLocked(id);
                }
                // Software's stage is switched off before the unit is told,
                // and the stage's off takes the next quantum, the unit's on
                // the next device sweep, which is later.
                if (want is { } target)
                {
                    var backend = new DeviceEqBackend(port, target, ChannelNameLocked(ch));
                    _eqDevices[id] = backend;
                    ApplySoftwareEqLocked(id);
                    backend.Apply(EqLocked(id));
                }
                changed = true;
            }
            else running?.Apply(EqLocked(id));
            if (_eqReleasing.TryGetValue(id, out var leaving)
                && (leaving.Backend.Status is { IsOff: true } or { Phase: DeviceEqPhase.Failed } || now >= leaving.Deadline))
            {
                _eqReleasing.Remove(id);
                ApplySoftwareEqLocked(id);
                changed = true;
            }
            // An input whose EQ runs in software holds its unit's EQ off, so
            // one Wave Link left on cannot double it; said once per connection.
            if (want is null && !_eqReleasing.ContainsKey(id) && EqCarrierLocked(id, carried, out UnitInputStatus? unit, out UnitEqSupport? support)
                && support.Ready
                && port.Status(unit.Unit, unit.Input).Phase == DeviceEqPhase.None)
                port.Post(unit.Unit, unit.Input, DeviceEq.Off, ChannelNameLocked(ch));
        }
        HoldUncarriedInputsOffLocked(port, carried);
        return changed;
    }

    /// <summary>
    /// The primary unit's inputs that no microphone carries, a strip bound to
    /// another source for one, hold their EQ off, said once per connection.
    /// It is the state a binding moving the strip off the unit leaves, reached
    /// the same way when the unit connects with the binding already in place,
    /// so an EQ Wave Link left on cannot stay on the unit's own paths. An
    /// additional unit's input that no microphone carries is never written:
    /// nothing in OpenXLR edits its EQ.
    /// </summary>
    private void HoldUncarriedInputsOffLocked(IDeviceEqPort port, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        if (_drivenUnit is not { } primary || !_unitEq.TryGetValue(primary, out UnitEqSupport? support) || !support.Ready) return;
        var taken = new HashSet<EqUnitInput>(carried.Where(c => IsMicrophoneId(c.Key)).Select(c => new EqUnitInput(c.Value.Unit, c.Value.Input)));
        taken.UnionWith(_eqDevices.Values.Select(b => b.Target));
        taken.UnionWith(_eqReleasing.Values.Select(r => r.Backend.Target));
        for (int input = 1; input <= support.Inputs; input++)
        {
            if (taken.Contains(new EqUnitInput(primary, input)) || port.Status(primary, input).Phase != DeviceEqPhase.None) continue;
            string owner = _config.Channels.FirstOrDefault(c => c.InputPair == input - 1) is { } strip
                ? ChannelNameLocked(strip) : $"{support.Name} input {input}";
            port.Post(primary, input, DeviceEq.Off, owner);
        }
    }

    /// <summary>
    /// Before Sound Check records on a channel whose unit runs its EQ: hand
    /// the EQ to software and wait for the unit to confirm it is off, one
    /// device sweep and <see cref="EqReleaseWait"/> at most, so the take is
    /// the raw microphone and the loop hears every edit. Waits outside the
    /// mixer's lock.
    /// </summary>
    private void ReleaseDeviceEqForSoundCheck(string channel)
    {
        DeviceEqBackend? leaving;
        lock (_gate)
        {
            if (_soundCheck is not null || !_eqDevices.ContainsKey(channel)) return;
            _soundCheckClaim = channel;
            SyncDeviceEqLocked();
            leaving = _eqReleasing.TryGetValue(channel, out var entry) ? entry.Backend : null;
        }
        if (leaving is null) return;
        long deadline = NowMs + (long)EqReleaseWait.TotalMilliseconds;
        while (NowMs < deadline && leaving.Status is not ({ IsOff: true } or { Phase: DeviceEqPhase.Failed }))
            Thread.Sleep(10);
    }

    /// <summary>Replace a microphone's whole EQ: its switch and bands.</summary>
    public void SetEq(string channel, MicEq eq)
    {
        if (MicEq.Check(eq) is string problem) throw new ArgumentException($"setEq: {problem}");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            bool switchedOn = eq.Enabled && !EqLocked(channel).Enabled;
            _eq[channel] = MicEq.Snap(eq);
            if (switchedOn)
            {
                _restarts.Forget(EqKey(channel));
                ForgetDeviceEqFailureLocked(channel);
            }
            ApplyEqLocked(channel);
        }
    }

    /// <summary>The EQ's own switch.</summary>
    public void SetEqEnabled(string channel, bool on)
    {
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicEq eq = EqLocked(channel);
            if (on)
            {
                _restarts.Forget(EqKey(channel));
                ForgetDeviceEqFailureLocked(channel);
            }
            _eq[channel] = eq with { Enabled = on };
            ApplyEqLocked(channel);
        }
    }

    /// <summary>Part of one existing band: the path a drag takes.</summary>
    public void SetEqBand(string channel, int band, EqBandChange change)
    {
        if (band is < 1 or > MicEq.MaximumBands) throw new ArgumentException($"setEqBand: band must be 1 to {MicEq.MaximumBands}");
        if (change.IsEmpty) throw new ArgumentException("setEqBand: need at least one of 'enabled', 'type', 'frequency', 'gain' and 'q'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicEq eq = EqLocked(channel);
            if (band > eq.Bands.Count) throw new ArgumentException("setEqBand: the band does not exist");
            EqBand next = change.ApplyTo(eq.Bands[band - 1]);
            if (MicEq.Check(next) is string problem) throw new ArgumentException($"setEqBand: {problem}");
            _eq[channel] = eq.WithBand(band, MicEq.Snap(next));
            ApplyEqLocked(channel);
        }
    }

    private void RequireMicrophoneLocked(string channel)
    {
        if (!CarriesEqLocked(channel))
            throw new ArgumentException($"'{channel}' is not a microphone input");
    }

    /// <summary>
    /// Bring a microphone's EQ to its saved values where it runs: posted to
    /// its unit, or written to its software stage.
    /// </summary>
    private void ApplyEqLocked(string channel)
    {
        SyncDeviceEqLocked();
        ApplySoftwareEqLocked(channel);
    }

    /// <summary>
    /// Bring a microphone's software stage to its values, the EQ's, noise
    /// removal's, the expander's, the compressor's and the voice changer's:
    /// written live to a running stage, which builds nothing; spliced in when
    /// any is on (the EQ running in software) and no stage runs. A stage that
    /// refuses a value is replaced. A stage whose unit took the EQ over is
    /// switched off and kept until the route is next built, as is one whose
    /// parts were all switched off.
    /// </summary>
    private void ApplySoftwareEqLocked(string channel)
    {
        if (!_built) return;
        MicEq eq = SoftwareEqLocked(channel);
        MicNoise noise = NoiseLocked(channel);
        MicVoice voice = VoiceLocked(channel);
        if (_eqStages.TryGetValue(channel, out SoftwareEqBackend? stage))
        {
            if (stage.IsAlive && (!eq.Enabled || stage.BandCount == eq.Bands.Count))
            {
                try
                {
                    if (stage.BandCount == eq.Bands.Count) stage.Apply(eq);
                    else stage.SetEnabled(false);
                    stage.ApplyNoise(noise);
                    stage.ApplyExpander(ExpanderLocked(channel));
                    stage.ApplyCompressor(CompressorLocked(channel));
                    stage.ApplyVoice(voice);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    _pw.Note($"The EQ on {channel} did not take a change and is started again: {ex.Message}");
                }
            }
            SpliceEqLocked(channel, replace: true);
            return;
        }
        if (!eq.Enabled && !noise.Enabled && !voice.Enabled && !DynamicsWantedLocked(channel)) return;
        // A capture input without inserts plays on its direct feed, which
        // has no head to splice into: it moves to its own route, with the
        // stage at the head, as an insert would move it. A source that is
        // missing waits for the sweep, which builds that route when it
        // returns.
        if (_config.Channels.FirstOrDefault(c => c.Id == channel) is { CaptureSource: not null } capture
            && !_inputFeeds.ContainsKey(channel))
        {
            if (_captureFeeds.ContainsKey(channel)) WireInputFeedsLocked([capture.Id]);
            return;
        }
        SpliceEqLocked(channel, replace: false);
    }

    /// <summary>Every microphone's running EQ brought to its saved values, after a recall.</summary>
    private void SyncEqStagesLocked()
    {
        foreach (ChannelDefinition ch in _config.Channels.Where(CarriesEq).ToList())
            ApplyEqLocked(ch.Id);
    }

    /// <summary>Store recalled EQs, snapped, for the channels the recall names.</summary>
    private void RecallEqLocked(IReadOnlyDictionary<string, MicEq> recalled)
    {
        foreach ((string channel, MicEq eq) in RecallEq(_eq, recalled))
        {
            if (eq.Enabled && !EqLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _eq[channel] = eq;
        }
    }

    /// <summary>
    /// The EQs after a recall: each channel the recalled data names takes its
    /// EQ, snapped; every other channel keeps what it has, so a recall
    /// without EQs (null) changes nothing. A key for a channel that is not a
    /// microphone is kept as it is, and an entry that cannot be read was
    /// refused or dropped by the loader before it got here.
    /// </summary>
    internal static Dictionary<string, MicEq> RecallEq(IReadOnlyDictionary<string, MicEq> current,
        IReadOnlyDictionary<string, MicEq>? recalled)
    {
        var next = new Dictionary<string, MicEq>(current, StringComparer.Ordinal);
        foreach ((string channel, MicEq eq) in recalled ?? new Dictionary<string, MicEq>())
            if (MicEq.Check(eq) is null) next[channel] = MicEq.Snap(eq);
        return next;
    }

    /// <summary>
    /// Start an EQ stage for a strip and link its upstream into it. Null
    /// when it cannot run, with the reason kept for the state; nothing is
    /// left behind then.
    /// </summary>
    private SoftwareEqBackend? StartEqStageLocked(ChannelDefinition ch, string upstream, bool upstreamProcessed, out PortLink? into)
    {
        into = null;
        if (UnconfirmedMicrophonePauseLocked(ch))
        {
            _eqErrors[ch.Id] = "The microphone EQ is waiting for mute confirmation.";
            return null;
        }
        string key = EqKey(ch.Id);
        if (_restarts.Blocked(key)) return null;
        MicEq eq = SoftwareEqLocked(ch.Id);
        MicNoise noise = NoiseLocked(ch.Id);
        FilterHandle? filter = null;
        try
        {
            filter = _pw.CreateMicEq($"{ch.Id}_{++_eqGeneration}", SoftwareEqBackend.Controls(eq, noise), eq.Bands.Count);
            var stage = new SoftwareEqBackend(_pw, filter, eq, noise);
            // The voice, the expander and the compressor go in live, before
            // the microphone is linked in, and their levels when a client
            // shows them.
            stage.ApplyExpander(ExpanderLocked(ch.Id));
            stage.ApplyCompressor(CompressorLocked(ch.Id));
            stage.ApplyVoice(VoiceLocked(ch.Id));
            if (_dynamicsWatch.ContainsKey(ch.Id)) stage.SetDynamicsMeters(true);
            into = RouteInputLocked(upstream, stage.Node, ch, upstreamProcessed);
            if (into.Pairs.Count == 0) throw new InvalidOperationException("The microphone could not be linked into its EQ.");
            _eqErrors.Remove(ch.Id);
            return stage;
        }
        catch (InvalidOperationException ex)
        {
            if (into is not null) _pw.Unlink(into);
            into = null;
            if (filter is not null) _pw.StopFilter(filter);
            _eqErrors[ch.Id] = ex.Message;
            // A missing helper is not a failure to count: nothing retries it.
            if (_pw.EqHostInstalled) _restarts.Failed(key, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Put a strip's head right without touching its chain: start a stage
    /// when the EQ is on and none runs (or <paramref name="replace"/> asks
    /// for a fresh one), link the stage's output to whatever the strip feeds
    /// (its chain, or its channel), then remove the old feed. Make before
    /// break, so the microphone is never silent. Without a stage the source
    /// feeds the chain or channel directly again. False when nothing changed
    /// or the strip has no route to splice into; the next build of the strip
    /// picks the EQ up then.
    /// </summary>
    private bool SpliceEqLocked(string channel, bool replace)
    {
        ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channel && (c.InputPair is not null || c.CaptureSource is not null));
        if (ch is null || !_inputFeeds.TryGetValue(channel, out PortLink? feed) || !_stripSources.TryGetValue(channel, out string? input))
            return false;
        InputRoute route = InputRouteLocked(ch, input);
        string upstream = route.Source!;
        SoftwareEqBackend? old = _eqStages.GetValueOrDefault(channel);
        if (old is not null && old.IsAlive && !replace) return false;
        PortLink? into = null;
        SoftwareEqBackend? stage = route.Eq ? StartEqStageLocked(ch, upstream, route.Looped, out into) : null;
        if (stage is null && old is null) return false;
        string target = _chains.TryGetValue(channel, out FilterHandle? chain) ? chain.SinkName : ch.SinkName;
        string head = stage?.Node ?? upstream;
        PortLink next = RouteInputLocked(head, target, ch, processed: stage is not null || route.Looped);
        if (next.Pairs.Count == 0)
        {
            if (stage is not null)
            {
                _pw.Unlink(into!);
                stage.Release();
                _eqErrors[channel] = "The EQ could not be linked to the microphone's channel.";
            }
            return false;
        }
        _pw.Unlink(new PortLink([.. feed.Pairs.Except(next.Pairs)]));
        if (_eqInputs.Remove(channel, out PortLink? oldInto)) _pw.Unlink(oldInto);
        _eqUpstreams.Remove(channel);
        if (_eqStages.Remove(channel)) old!.Release();
        _inputFeeds[channel] = next;
        _inputHeads[channel] = head;
        if (stage is not null)
        {
            _eqStages[channel] = stage;
            if (_dynamicsWatch.ContainsKey(channel)) _dynamicsMeterHosts[channel] = stage.Stage.NativeHost!;
            _eqInputs[channel] = into!;
            _eqUpstreams[channel] = upstream;
            // A direct link from the microphone that this daemon does not
            // track would carry it past the EQ onto the equalised signal.
            _pw.UnlinkNodes(input, target);
        }
        return true;
    }

    /// <summary>
    /// The sweep's part: a stage whose helper died counts against the
    /// restart policy keyed <c>eq:&lt;channel&gt;</c> and is started again;
    /// one that cannot start leaves the microphone on its path without the
    /// EQ, never silent. An EQ that is on but has no stage, because its
    /// start failed, is tried again on the same budget. True when a route
    /// changed.
    /// </summary>
    private bool HealEqStagesLocked()
    {
        if (!_built) return false;
        bool changed = false;
        HashSet<string>? waitingForPause = MicrophonesPausedLocked && _microphonePausePending
            ? _config.Channels.Where(UnconfirmedMicrophonePauseLocked).Select(c => c.Id).ToHashSet(StringComparer.Ordinal) : null;
        foreach ((string channel, SoftwareEqBackend stage) in _eqStages.ToList())
        {
            if (waitingForPause?.Contains(channel) == true || stage.IsAlive) continue;
            string? words = stage.Stage.LastWords;
            _restarts.Failed(EqKey(channel), words);
            _pw.Note($"The EQ on {channel} stopped{(words is null ? "." : $". The plugin host's last message: {words}")}");
            if (SpliceEqLocked(channel, replace: true)) { changed = true; continue; }
            // Nothing could be linked: drop the dead stage and leave the
            // strip's broken feed to the input sweep, which builds it again.
            if (_eqInputs.Remove(channel, out PortLink? into)) _pw.Unlink(into);
            _eqUpstreams.Remove(channel);
            _eqStages.Remove(channel);
            stage.Release();
            changed = true;
        }
        if (!_pw.EqHostInstalled) return changed;
        foreach (ChannelDefinition ch in _config.Channels.Where(CarriesEq).ToList())
            if (waitingForPause?.Contains(ch.Id) != true && !_eqStages.ContainsKey(ch.Id) && StageWantedLocked(ch.Id) && !_restarts.Blocked(EqKey(ch.Id))
                && _eqErrors.ContainsKey(ch.Id) && _inputFeeds.ContainsKey(ch.Id))
                changed |= SpliceEqLocked(ch.Id, replace: false);
        return changed;
    }

    /// <summary>Stop every EQ stage and forget the heads, with the rest of the input routes.</summary>
    private void RemoveEqStagesLocked()
    {
        foreach (PortLink into in _eqInputs.Values) _pw.Unlink(into);
        foreach (SoftwareEqBackend stage in _eqStages.Values) stage.Release();
        _eqInputs.Clear();
        _eqStages.Clear();
        _eqUpstreams.Clear();
        _inputHeads.Clear();
    }

    /// <summary>
    /// A microphone's EQ for the state. A unit-run EQ reports the
    /// <c>device</c> backend, on or off, and can always run: it needs no
    /// helper. <paramref name="carried"/> is <see cref="UnitInputsLocked"/>.
    /// </summary>
    private MicEqState EqStateLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        MicEq eq = EqLocked(channel);
        string processing = EqProcessingLocked(channel);
        MicEqDevice? device = EqDeviceStateLocked(channel, carried);
        if (_eqDevices.ContainsKey(channel))
            return new MicEqState(eq.Enabled, eq.Bands, "device", true, null) { Processing = processing, Device = device };
        bool installed = _pw.EqHostInstalled;
        bool blocked = _restarts.Blocked(EqKey(channel));
        string? error = !installed ? EqNeedsHost
            : blocked ? _restarts.Message(EqKey(channel), EqGivenUp)
            : eq.Enabled ? _eqErrors.GetValueOrDefault(channel) : null;
        string? backend = _eqStages.ContainsKey(channel) ? "software" : null;
        return new MicEqState(eq.Enabled, eq.Bands, backend, installed && !blocked, error) { Processing = processing, Device = device };
    }

    /// <summary>
    /// The unit a microphone carries, while that unit's model has a hardware
    /// EQ. Never on a capture input: its EQ runs on this computer, even when
    /// an additional unit's microphone feeds it.
    /// </summary>
    private MicEqDevice? EqDeviceStateLocked(string channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        if (!IsMicrophoneId(channel) || !EqCarrierLocked(channel, carried, out UnitInputStatus? unit, out UnitEqSupport? support)) return null;
        string? failure = _eqDeviceFailed.GetValueOrDefault(new EqUnitInput(unit.Unit, unit.Input).Key);
        return new MicEqDevice(unit.Unit, unit.Input, support.Name, support.Firmware, support.Ready && failure is null,
            failure is null ? support.Reason : EqDeviceFailedReason, failure is null ? support.Note : $"The {support.Name} {failure}, so the EQ runs in software.")
        {
            MinimumFirmware = support.MinimumFirmware,
        };
    }

    /// <summary>Every microphone's EQ, for a profile: a recall then names every microphone, capture inputs included.</summary>
    private Dictionary<string, MicEq> SceneEqLocked()
        => _config.Channels.Where(CarriesEq).ToDictionary(c => c.Id, c => EqLocked(c.Id), StringComparer.Ordinal);
}
