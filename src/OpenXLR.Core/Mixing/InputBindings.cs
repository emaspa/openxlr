using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One port of a capture source, by exact node name, feeding a hardware
/// strip. A strip takes a single mono port, fed to both sides, so
/// <see cref="Pair"/> is 0 and <see cref="MonoChannel"/> is the port.
/// </summary>
public sealed record SourceBinding(string Source, int Pair = 0, int? MonoChannel = 0)
{
    /// <summary>
    /// The supported Elgato unit the source belonged to when it was bound,
    /// by the id <c>waveInterfaces</c> gives it, or null for any other
    /// source. The daemon fills it in. While the exact node is missing, a
    /// binding with a unit follows that unit's card to the node it publishes
    /// now: driving the unit, or switching its card profile, renames the node.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>A name the capture rules accept, pair 0, a mono port from 0 to 63, and a unit id or none.</summary>
    [JsonIgnore]
    public bool IsValid => Pair == 0 && MonoChannel is not null && CaptureBinding.IsValid(Source, Pair, MonoChannel) && IsUnitId(Unit);

    /// <summary>
    /// Null, an instance id (<c>0fd9:0070@…</c>), or the bare model id
    /// (<c>0fd9:0070</c>) a unit goes by when its card cannot be told apart
    /// from another one's.
    /// </summary>
    public static bool IsUnitId(string? id) => id is null || UsbLocation.IsInstanceId(id)
        || id is { Length: 9 } && id[4] == ':' && id.Where((_, index) => index != 4).All(char.IsAsciiHexDigit);

    /// <summary>The model part of a unit id, which decides what the unit's inputs are.</summary>
    internal static string ModelOf(string unit) => unit.Length > 9 ? unit[..9] : unit;

    /// <summary>The zero-based mono port, or the first port of a capture pair.</summary>
    [JsonIgnore]
    public int Port => MonoChannel ?? Pair * 2;
}

/// <summary>
/// XLR 1's source in the earlier <c>primaryInput</c> field of mixer.json,
/// read from files written before <c>inputBindings</c> and never written.
/// </summary>
public sealed record PrimaryInputBinding(string Source, int Channel = 0)
{
    [JsonIgnore]
    public bool IsValid => CaptureBinding.IsValid(Source, 0, Channel);
}

/// <summary>
/// What feeds a channel, in the state: <c>unit</c> (a hardware strip on the
/// driven interface's capture pair), <c>source</c> (an exact capture node,
/// on a hardware strip or a capture channel) or <c>apps</c>.
/// </summary>
public sealed record ChannelBinding(string Kind, string? Source = null, int? Pair = null, int? MonoChannel = null, string? Unit = null);

/// <summary>
/// A supported unit attached next to the primary one, as the daemon sees it.
/// </summary>
/// <param name="Id">Its instance id, as <c>waveInterfaces</c> spells it.</param>
/// <param name="Fragments">The node-name fragments that find its own nodes and no other unit's; empty while it is not driven.</param>
/// <param name="XlrInputs">How many microphone inputs it has.</param>
/// <param name="XlrJacks">Its inputs are XLR jacks, not a USB microphone's capsule.</param>
/// <param name="Driven">Driven as an additional interface, so its controls can be shown.</param>
public sealed record AttachedUnit(string Id, IReadOnlyList<string> Fragments, int XlrInputs, bool XlrJacks, bool Driven)
{
    internal bool SameAs(AttachedUnit other) => Id == other.Id && XlrInputs == other.XlrInputs && XlrJacks == other.XlrJacks
        && Driven == other.Driven && Fragments.SequenceEqual(other.Fragments, StringComparer.Ordinal);

    /// <summary>Whether a node is one of this unit's own: its name holds one of the unit's fragments.</summary>
    internal bool Owns(string node) => Fragments.Any(f => f.Length > 0 && node.Contains(f, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which input stage of which driven unit a channel carries.</summary>
/// <param name="Unit">The unit's instance id, as <c>waveInterfaces</c> spells it.</param>
/// <param name="Input">1 or 2: the unit's first or second microphone input.</param>
/// <param name="Primary">True for the unit in the header, the one <c>state</c> and <c>capabilities</c> describe.</param>
public sealed record UnitInputStatus(string Unit, int Input, bool Primary);

public sealed partial class Mixer
{
    // Strip id -> the source it is bound to. Absent means the strip takes
    // its pair of the driven interface. Machine wiring: kept across builds
    // and layout changes, saved in mixer.json, never part of a scene.
    private readonly Dictionary<string, SourceBinding> _bindings = [];
    // The bindings each strip was last wired with, the source node each
    // strip is fed from now, and why a bound strip is silent.
    private readonly Dictionary<string, SourceBinding> _wiredBindings = [];
    private readonly Dictionary<string, string> _stripSources = [];
    private readonly Dictionary<string, string> _stripWarnings = [];
    private readonly Dictionary<string, string> _captureWarnings = [];

    private string? _drivenUnit;   // the primary unit's instance id while one is driven
    private bool _attached;        // a supported unit is attached, driven or not
    private bool _unitXlrJacks = true;   // the last driven unit's inputs are XLR jacks, not a capsule
    private IReadOnlyList<AttachedUnit> _units = [];   // the other attached units
    // Whether each model's inputs are XLR jacks, from every unit seen in this
    // run, so a strip bound to an unplugged unit keeps its name.
    private readonly Dictionary<string, bool> _modelJacks = new(StringComparer.Ordinal);

    /// <summary>
    /// The name a microphone strip goes by while it is not fed by a real XLR
    /// jack: bound to another source, or on a USB microphone's capsule.
    /// Fixed, as "XLR 1" and "XLR 2" are; the ids stay xlr1 and xlr2.
    /// </summary>
    public static string MicName(string strip) => strip == "xlr2" ? "Mic 2" : "Mic 1";

    /// <summary>
    /// The name the state reports for a channel. XLR 1 and XLR 2 keep their
    /// names only while a real XLR jack feeds them; a strip bound to another
    /// source, or on a Wave:3's or Wave:1's capsule, is Mic 1 or Mic 2.
    /// </summary>
    private string ChannelNameLocked(ChannelDefinition channel)
        => channel.InputPair is not null && channel.Id is "xlr1" or "xlr2" && !OnXlrJackLocked(channel.Id) ? MicName(channel.Id) : channel.Name;

    /// <summary>
    /// Whether a strip is fed by a real XLR jack: the driven unit's, unless
    /// that unit has a capsule, or one of another supported unit's jacks that
    /// the strip is bound to. Any other source is not a jack.
    /// </summary>
    private bool OnXlrJackLocked(string strip)
    {
        if (!_bindings.TryGetValue(strip, out SourceBinding? binding)) return _unitXlrJacks;
        if (binding.Unit is not { } unit) return false;
        if (unit == _drivenUnit) return _unitXlrJacks;
        return _modelJacks.TryGetValue(SourceBinding.ModelOf(unit), out bool jacks) && jacks;
    }

    /// <summary>
    /// The supported units attached next to the primary one: which of them are
    /// driven, the fragments that find their nodes, and what their inputs
    /// are. True when it changed what the state reports.
    /// </summary>
    public bool SetAttachedUnits(IReadOnlyList<AttachedUnit> units)
    {
        lock (_gate)
        {
            bool changed = false;
            foreach (AttachedUnit unit in units)
            {
                string model = SourceBinding.ModelOf(unit.Id);
                if (_modelJacks.TryGetValue(model, out bool known) && known == unit.XlrJacks) continue;
                _modelJacks[model] = unit.XlrJacks;
                changed = true;
            }
            if (_units.Count == units.Count && _units.Zip(units).All(pair => pair.First.SameAs(pair.Second))) return changed;
            _units = [.. units];
            return true;
        }
    }

    /// <summary>
    /// Whether the driven unit's inputs are XLR jacks, from its capabilities;
    /// null while no unit is driven keeps the last answer of this run. True
    /// when it changed what the state reports.
    /// </summary>
    public bool SetUnitXlrJacks(bool? xlrJacks)
    {
        lock (_gate)
        {
            if (xlrJacks is not bool value) return false;
            if (_drivenUnit is { } unit) _modelJacks[SourceBinding.ModelOf(unit)] = value;
            if (value == _unitXlrJacks) return false;
            _unitXlrJacks = value;
            return true;
        }
    }

    /// <summary>The hardware strips that can take a source: XLR 1 and XLR 2. Aux In stays on the interface.</summary>
    public static bool IsBindableStrip(string? channel) => channel is "xlr1" or "xlr2";

    public bool HasBindableStrip(string id) => IsBindableStrip(id) && HasChannel(id);

    /// <summary>The source a strip is bound to, or null when it takes the interface's pair.</summary>
    public SourceBinding? InputBinding(string channel) { lock (_gate) return _bindings.GetValueOrDefault(channel); }

    /// <summary>Every strip bound to a source.</summary>
    public IReadOnlyDictionary<string, SourceBinding> InputBindings { get { lock (_gate) return new Dictionary<string, SourceBinding>(_bindings); } }

    /// <summary>
    /// Bind every strip as <paramref name="bindings"/> says, the strips it
    /// leaves out to the interface. Used at start and by saved settings.
    /// </summary>
    public void SetInputBindings(IReadOnlyDictionary<string, SourceBinding>? bindings)
    {
        lock (_gate)
        {
            foreach (string strip in (string[])["xlr1", "xlr2"])
                SetInputBinding(strip, bindings?.GetValueOrDefault(strip));
        }
    }

    /// <summary>
    /// Bind one hardware strip to a source port, or back to the interface
    /// with null, without rebuilding channels or mixes. A missing source
    /// stays selected and silent. The strip's monitor sends start muted
    /// after a change, so plugging in a hot microphone cannot feed back;
    /// the other strips keep their feeds, chains and mutes.
    /// </summary>
    public void SetInputBinding(string channel, SourceBinding? binding, Func<MixerSettings, string?>? save = null)
    {
        if (!IsBindableStrip(channel)) throw new ArgumentException("Only XLR 1 and XLR 2 take an input binding.");
        if (binding is not null && !binding.IsValid) throw new ArgumentException("Select an external capture source and a mono port from 0 to 63.");
        lock (_gate)
        {
            SourceBinding? previous = _bindings.GetValueOrDefault(channel);
            if (previous == binding)
            {
                if (save is not null) PersistLocked(save);
                return;
            }
            if (!_built) { AssignBindingLocked(channel, binding); return; }
            var previousMutes = new HashSet<string>(_muted);
            string?[] previousHardwareMics = [.. _hardwareMicChannels];
            bool previousClipGuard = _softClipGuard;
            try
            {
                if (_soundCheck is not null && _soundCheckChannel == channel) StopSoundCheckLocked(restore: false);
                bool clipGuardApplied = SoftClipGuardApplicableLocked;
                AssignBindingLocked(channel, binding);
                // A software ClipGuard left on behind the unit's hardware one
                // starts to apply once XLR 1 takes a source. Without its
                // plugin it goes off, as when the unit changes, rather than
                // failing the binding.
                if (!clipGuardApplied && SoftClipGuardApplicableLocked && _softClipGuard
                    && !_pw.GetSoftwareClipGuardAvailability().Available)
                    _softClipGuard = false;
                // Each of the Pro's direct paths follows the channel that
                // carries its input. When the binding moves an input to another
                // channel, or out of the mixer, that path is off until the
                // daemon has looked at the new channel's sends.
                DropMovedHardwareMicsLocked();
                MuteInputMonitorsLocked(channel);
                WireInputFeedsLocked([channel]);
                if (save is not null) PersistLocked(save);
            }
            catch (Exception change)
            {
                AssignBindingLocked(channel, previous);
                _softClipGuard = previousClipGuard;
                _muted.Clear();
                _muted.UnionWith(previousMutes);
                try
                {
                    RestoreHardwareMicsLocked(previousHardwareMics);
                    ReapplyMixesLocked(_config.Mixes);
                    WireInputFeedsLocked([channel]);
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("The input binding could not be changed or restored.", change, rollback);
                }
                throw;
            }
        }
    }

    private void AssignBindingLocked(string channel, SourceBinding? binding)
    {
        if (binding is null) _bindings.Remove(channel);
        else _bindings[channel] = binding;
    }

    /// <summary>
    /// The driven primary unit's instance id, or null while none is driven.
    /// It names the unit in <c>unitInput</c>. True when it changed.
    /// </summary>
    public bool SetDrivenUnit(string? instanceId)
    {
        lock (_gate)
        {
            if (_drivenUnit == instanceId) return false;
            _drivenUnit = instanceId;
            return true;
        }
    }

    /// <summary>
    /// Whether a supported unit is attached, driven or not. Before any unit
    /// has connected in this run, XLR 2, Aux In and Line In are present only
    /// while one is. True when it changed.
    /// </summary>
    public bool SetAttached(bool attached)
    {
        lock (_gate)
        {
            if (_attached == attached) return false;
            _attached = attached;
            return true;
        }
    }

    /// <summary>
    /// Whether a client should draw this channel. A channel that needs no
    /// jack is always present, and so is a strip bound to a source. A strip
    /// on the interface is present when the connected unit has its jack;
    /// with no unit connected it keeps the last answer of this run, and
    /// before any unit has connected XLR 1 is present and XLR 2 and Aux In
    /// are present only while a supported unit is attached. Line In also
    /// needs the interface's capture source in the layout it was measured in,
    /// the last one wired in this run.
    /// </summary>
    private bool ChannelPresentLocked(ChannelDefinition channel)
    {
        if (channel.InputPair is null || _bindings.ContainsKey(channel.Id)) return true;
        int? jacks = _deviceXlrInputs ?? _lastXlrInputs;
        bool? aux = _deviceXlrInputs is null ? _lastAuxInput : _deviceAuxInput;
        if (channel.NeedsLineInput)
        {
            bool? line = _deviceXlrInputs is null ? _lastLineInput : _deviceLineInput;
            return (jacks is null ? _attached : line is true) && _inputPorts == ChannelDefinition.LineInputSourcePorts;
        }
        if (jacks is null) return channel.NeedsXlrInputs is not > 1 && !channel.NeedsAuxInput || _attached;
        if (channel.NeedsXlrInputs is int needed && jacks < needed) return false;
        return !channel.NeedsAuxInput || aux is not false;
    }

    /// <summary>
    /// The channel that carries the driven unit's microphone input 1 or 2,
    /// or null. The hardware strip on that input's pair wins while it takes
    /// the interface; then a strip bound to the unit's own node at that
    /// input's port (the first for input 1, the third for input 2); then the
    /// first capture channel, in layout order, on the unit's own node at that
    /// port or on its pair.
    /// </summary>
    private string? UnitInputChannelLocked(int input)
        => _drivenUnit is null ? null : CarrierOfLocked(input);

    // The same rule without asking whether a unit is driven: the Pro's
    // direct path only runs while the daemon drives one.
    // A unit id that two attached units share matches no saved unit; the
    // strip on the interface and the exact node still carry the inputs.
    private string? CarrierOfLocked(int input)
        => UnitInputChannel(_config.Channels, _bindings, _inputDevice, _deviceXlrInputs ?? 1, input,
            AmbiguousUnitLocked(_drivenUnit) ? null : _drivenUnit);

    /// <inheritdoc cref="UnitInputChannelLocked"/>
    internal static string? UnitInputChannel(IReadOnlyList<ChannelDefinition> channels,
        IReadOnlyDictionary<string, SourceBinding> bindings, string? unitNode, int xlrInputs, int input, string? unitId = null)
    {
        if (input is not (1 or 2) || input > xlrInputs) return null;
        ChannelDefinition? strip = channels.FirstOrDefault(c => c.InputPair == input - 1);
        if (strip is not null && !bindings.ContainsKey(strip.Id)) return strip.Id;
        return CarrierOn(channels, bindings, input,
            binding => unitId is not null && binding.Unit == unitId || unitNode is not null && binding.Source == unitNode,
            capture => unitId is not null && capture.CaptureUnit == unitId || unitNode is not null && capture.CaptureSource == unitNode);
    }

    /// <summary>
    /// The channel that carries an additional unit's input 1 or 2, or null:
    /// a strip bound to that unit's node at the input's port, else the first
    /// capture channel on one of its nodes at that port or on its pair. A
    /// unit that is not driven has no controls to show, so nothing carries it.
    /// </summary>
    internal static string? AdditionalUnitInputChannel(IReadOnlyList<ChannelDefinition> channels,
        IReadOnlyDictionary<string, SourceBinding> bindings, AttachedUnit unit, int input)
    {
        if (!unit.Driven || unit.Fragments.Count == 0 || input is not (1 or 2) || input > unit.XlrInputs) return null;
        return CarrierOn(channels, bindings, input, binding => binding.Unit == unit.Id || unit.Owns(binding.Source),
            capture => capture.CaptureUnit == unit.Id || unit.Owns(capture.CaptureSource!));
    }

    // The strips bound to a unit at the input's port come first, then the
    // capture channels on its node, each in layout order.
    private static string? CarrierOn(IReadOnlyList<ChannelDefinition> channels, IReadOnlyDictionary<string, SourceBinding> bindings,
        int input, Func<SourceBinding, bool> boundToUnit, Func<ChannelDefinition, bool> captureOnUnit)
    {
        int port = input == 1 ? 0 : 2;
        ChannelDefinition? bound = channels.FirstOrDefault(c => c.InputPair is not null
            && bindings.TryGetValue(c.Id, out SourceBinding? b) && b.Port == port && boundToUnit(b));
        if (bound is not null) return bound.Id;
        return channels.FirstOrDefault(c => c.CaptureSource is not null && captureOnUnit(c)
            && (c.CaptureMonoChannel == port || c.CaptureMonoChannel is null && c.CapturePair == input - 1))?.Id;
    }

    /// <summary>
    /// Which unit input each channel carries: the primary unit's inputs
    /// first, then each driven additional unit's. A channel carries one input
    /// at most, and each input appears on one channel at most.
    /// </summary>
    private Dictionary<string, UnitInputStatus> UnitInputsLocked()
    {
        var carried = new Dictionary<string, UnitInputStatus>(StringComparer.Ordinal);
        if (_drivenUnit is { } primary)
            for (int input = 1; input <= 2; input++)
                if (UnitInputChannelLocked(input) is { } channel) carried.TryAdd(channel, new(primary, input, Primary: true));
        foreach (AttachedUnit unit in _units)
        {
            if (unit.Id == _drivenUnit || AmbiguousUnitLocked(unit.Id)) continue;
            for (int input = 1; input <= 2; input++)
                if (AdditionalUnitInputChannel(_config.Channels, _bindings, unit, input) is { } channel)
                    carried.TryAdd(channel, new(unit.Id, input, Primary: false));
        }
        return carried;
    }

    private ChannelBinding BindingStateLocked(ChannelDefinition channel)
    {
        if (channel.InputPair is int pair)
            return _bindings.TryGetValue(channel.Id, out SourceBinding? b)
                ? new("source", b.Source, b.Pair, b.MonoChannel, b.Unit)
                : new("unit", Pair: pair);
        return channel.CaptureSource is { } source
            ? new("source", source, channel.CapturePair, channel.CaptureMonoChannel, channel.CaptureUnit)
            : new("apps");
    }

    private bool BindingConnectedLocked(ChannelDefinition channel)
        => channel.InputPair is not null ? _stripSources.ContainsKey(channel.Id) && _inputFeeds.ContainsKey(channel.Id)
            : channel.CaptureSource is null || _captureFeeds.ContainsKey(channel.Id) || _inputFeeds.ContainsKey(channel.Id);

    private string? BindingWarningLocked(ChannelDefinition channel)
        => (channel.InputPair is not null ? _stripWarnings : _captureWarnings).GetValueOrDefault(channel.Id);

    /// <summary>
    /// The unit-level sentence, then one per silent bound strip with the
    /// strip's name in front, for clients that show one line.
    /// </summary>
    private string? InputWarningLocked()
    {
        List<string> parts = [];
        if (_inputWarning is not null) parts.Add(_inputWarning);
        foreach (ChannelDefinition channel in _config.Channels)
            if (_stripWarnings.TryGetValue(channel.Id, out string? warning)) parts.Add($"{ChannelNameLocked(channel)}: {warning}");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// The source a bound strip is fed from, or null with the reason noted
    /// for the strip. The exact name must identify one source that has the
    /// port; no other microphone ever takes its place.
    /// </summary>
    private string? ResolveBoundSourceLocked(string strip, SourceBinding binding, IReadOnlyList<AudioNode> sources)
    {
        string? source = BoundSourceLocked(binding, sources, out string? warning);
        if (warning is null) _stripWarnings.Remove(strip);
        else _stripWarnings[strip] = warning;
        return source;
    }

    private bool AmbiguousUnitLocked(string? unit)
        => unit is { Length: 9 } && _units.Count(u => SourceBinding.ModelOf(u.Id) == unit)
            + (_drivenUnit is { } primary && SourceBinding.ModelOf(primary) == unit && !_units.Any(u => u.Id == primary) ? 1 : 0) > 1;

    /// <summary>
    /// The node a binding is fed from now, or null with the reason. The exact
    /// name wins. While it is missing, a binding to a supported unit's node
    /// follows the unit's card, whose part of the node name stays the same
    /// when the unit is driven, its card profile switches or it is plugged in
    /// again: the one source that card publishes with the port is the unit's
    /// microphone under its new name. A card with several sources (a split
    /// profile) is not guessed at.
    /// </summary>
    private string? BoundSourceLocked(SourceBinding binding, IReadOnlyList<AudioNode> sources, out string? warning)
    {
        int matches = sources.Count(s => s.Name == binding.Source);
        if (matches == 0 && binding.Unit is not null)
        {
            if (AmbiguousUnitLocked(binding.Unit))
            {
                warning = $"{binding.Source} is gone and more than one attached interface has model {binding.Unit}. The strip stays silent until it is bound again.";
                return null;
            }
            string card = InterfaceNodes.CardKey(binding.Source);
            List<AudioNode> renamed = [.. sources.Where(s => !s.IsOwn && InterfaceNodes.CardKey(s.Name) == card)];
            if (renamed.Count == 1)
            {
                string node = renamed[0].Name;
                bool hasPort = renamed[0].Channels is int ports ? ports > binding.Port : _pw.HasCaptureChannel(node, binding.Port);
                warning = hasPort ? null : $"{node} has no port {binding.Port + 1}. The strip stays silent until it returns.";
                return hasPort ? node : null;
            }
            if (renamed.Count > 1)
            {
                warning = $"{binding.Source} is gone and its interface now has {renamed.Count} sources. The strip stays silent until it is bound again.";
                return null;
            }
        }
        warning = matches switch
        {
            0 => $"{binding.Source} is not connected. The strip stays silent until it returns.",
            1 => _pw.HasCaptureChannel(binding.Source, binding.Port) ? null
                : $"{binding.Source} has no port {binding.Port + 1}. The strip stays silent until it returns.",
            _ => $"More than one source is named {binding.Source}. The strip stays silent until the name is unique.",
        };
        return warning is null ? binding.Source : null;
    }

    /// <summary>Console rule: a strip whose source changed comes up muted in the monitor mixes.</summary>
    private void MuteInputMonitorsLocked(string channel)
    {
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
        {
            if (!_cells.Contains(Cell(channel, mix.Id))) continue;
            _muted.Add(Cell(channel, mix.Id));
            ApplyCellLocked(channel, mix.Id);
        }
    }

    private PortLink RouteInputLocked(string source, string target, ChannelDefinition channel, bool processed = false)
    {
        if (UnconfirmedMicrophonePauseLocked(channel)) return new PortLink([]);
        SourceBinding? binding = _bindings.GetValueOrDefault(channel.Id);
        // The loop helper's tap goes past the chain on a link of its own, so
        // only its main output feeds what follows it.
        string fromPrefix = _soundCheck is not null && source == _soundCheck.SourceName ? "capture" : "";
        PortLink route = channel.CaptureSource is not null
            ? _pw.LinkNodes(source, fromPrefix, target, "playback",
                fromPairOffset: processed ? 0 : channel.CapturePair,
                fromChannel: processed ? null : channel.CaptureMonoChannel)
            : binding is not null && !processed
            ? _pw.LinkNodes(source, "", target, "playback", fromChannel: binding.Port)
            : _pw.RouteInputToChannel(source, target, processed ? 0 : channel.InputPair!.Value);
        if ((binding is not null || channel.CaptureSource is not null)
            && (!route.Complete || route.Pairs.Count < (target == channel.SinkName ? 2 : channel.ChainWidth ?? 1)))
        {
            // A half-connected microphone must retry instead of being kept
            // as a healthy route that carries audio on one side only.
            _pw.Unlink(route);
            return new PortLink([]);
        }
        return route;
    }
}
