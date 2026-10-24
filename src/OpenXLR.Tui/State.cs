using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Tui;

/// <summary>
/// A channel's or mix's icon, colour and hidden flag from the layout editor.
/// Presentation only; the strip's sends and routing do not depend on it.
/// </summary>
internal sealed record AppearanceEntry
{
    public string Icon { get; init; } = string.Empty;
    public string? Colour { get; init; }
    public bool Hidden { get; init; }

    /// <summary>The built-in icon the daemon chose for the item's kind.</summary>
    public string DefaultIcon { get; init; } = string.Empty;

    /// <summary>The colour to draw the name in, or null for the theme's.</summary>
    public Rgb? Accent => Rgb.TryParse(Colour);

    /// <summary>
    /// The name with the icon in front of it, when there is one. A terminal
    /// draws text, so a named icon is drawn as its text character; a skin's
    /// own icon name falls back to the built-in icon of that name or the default.
    /// </summary>
    public string Label(string name) => LayoutIcons.TextFor(Icon, DefaultIcon) is { Length: > 0 } icon ? $"{icon} {name}" : name;
}

/// <summary>One mix master: a monitor mix, a virtual microphone or the Aux port.</summary>
internal sealed record MixEntry
{
    public AppearanceEntry Appearance { get; init; } = new();
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }

    public bool EffectiveMute(bool monitorPaused) => Muted || Kind == "monitor" && monitorPaused;
    public string Kind { get; init; } = "virtualMic";
    public bool? Editable { get; init; }
    public bool IsEditable => Editable ?? Kind == "virtualMic";
    public bool? Deletable { get; init; }
    public bool IsDeletable => Deletable ?? IsEditable;

    /// <summary>A monitor mix goes to 150%, everything else to 100%.</summary>
    public double Ceiling => Kind == "monitor" ? 1.5 : 1.0;
}

/// <summary>One channel strip and its send into every mix.</summary>
internal sealed record ChannelEntry
{
    public AppearanceEntry Appearance { get; init; } = new();
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, double> Levels { get; init; } = [];
    public List<string> MutedIn { get; init; } = [];

    /// <summary>
    /// The channel's own volume, which scales every send it makes; null for
    /// every channel of a daemon that has none, and for a hardware input of a
    /// daemon that gave volumes to application and capture channels only.
    /// </summary>
    public double? Volume { get; init; }
    public bool Hardware { get; init; }
    public string? CaptureSource { get; init; }
    public int CapturePair { get; init; }
    public bool CaptureConnected { get; init; }

    /// <summary>Null from a daemon that predates capture microphones.</summary>
    public bool? Microphone { get; init; }

    private int? _chainWidth;
    private bool _hasChainWidth;

    /// <summary>The chain's width, or the XLR-only rule when the field is absent.</summary>
    public int? ChainWidth
    {
        get => _hasChainWidth ? _chainWidth : Id is "xlr1" or "xlr2" ? 1 : null;
        init { _chainWidth = value; _hasChainWidth = true; }
    }

    /// <summary>
    /// False when the active device has no jack behind this channel, which is
    /// XLR 2 and Aux In on everything but the Wave XLR Pro. The daemon keeps
    /// sending the strip so its levels survive a device change.
    /// </summary>
    public bool Present { get; init; } = true;

    /// <summary>What feeds the channel, or null from a daemon that predates input bindings.</summary>
    public BindingEntry? Binding { get; init; }

    /// <summary>The channel's feed exists; always true for an application channel.</summary>
    public bool BindingConnected { get; init; } = true;

    /// <summary>Why the channel's source is silent, or null.</summary>
    public string? BindingWarning { get; init; }

    /// <summary>The driven unit's microphone input this channel carries, or null.</summary>
    public UnitInputEntry? UnitInput { get; init; }

    /// <summary>A microphone's EQ, or null for a channel that has none and from a daemon that predates it.</summary>
    public EqEntry? Eq { get; init; }

    /// <summary>XLR 1 or XLR 2, the strips a binding can move to another source.</summary>
    public bool Bindable => Hardware && Id is "xlr1" or "xlr2";

    /// <summary>A microphone strip fed by one port of another capture source.</summary>
    public bool SourceBound => Hardware && Binding is { Kind: "source", Source.Length: > 0 };

    /// <summary>A hardware strip on the interface's own input, which an older daemon always is.</summary>
    public bool UnitBound => Hardware && (Binding is null || Binding.Kind == "unit");

    /// <summary>
    /// True for microphone inputs, with the XLR-only rule on an older daemon.
    /// The daemon sends a pair for every channel and repeats the one reading in both
    /// sides of it, so a stereo meter here would draw the same bar twice.
    /// </summary>
    public bool Mono => Microphone ?? Id is "xlr1" or "xlr2";

    public double Level(string mix) => Levels.TryGetValue(mix, out double value) ? value : 0;

    public bool IsMuted(string mix) => MutedIn.Contains(mix);

    /// <summary>
    /// Step the channel volume by <paramref name="by"/>, within 0 to 1. A mix
    /// master, or a channel the daemon reports no volume for, says so.
    /// </summary>
    public static void StepVolume(App app, ChannelEntry? channel, double by)
    {
        if (channel?.Volume is not double volume)
        {
            app.Say("This channel has no channel volume");
            return;
        }
        app.Link.Send("setChannelVolume", body =>
        {
            body["channel"] = channel.Id;
            body["value"] = Math.Round(Math.Clamp(volume + by, 0, 1), 2);
        });
    }
}

/// <summary>A live playback stream the daemon has placed on a channel.</summary>
internal sealed record StreamEntry
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string? ChannelId { get; init; }
    public bool Active { get; init; }
    public bool Running { get; init; }
}

/// <summary>One plugin in a chain, with the status the daemon reports for it.</summary>
internal sealed record InsertEntry
{
    public InsertBody Insert { get; init; } = new();
    public string? Error { get; init; }
    public bool NativeHostRunning { get; init; }
    public bool NativeUiBlocked { get; init; }
    public string? NativeUiBlockReason { get; init; }
}

internal sealed record InsertBody
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Plugin { get; init; } = string.Empty;
    public string? Label { get; init; }
    public bool Bypass { get; init; }
    public bool NativeHost { get; init; }

    /// <summary>The plugin's control values, carried back untouched whenever the chain is rewritten.</summary>
    public Dictionary<string, double> Params { get; init; } = [];
}

/// <summary>A PipeWire sink or source the daemon can see.</summary>
internal sealed record DeviceEntry
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Kind { get; init; }
    public bool IsOwn { get; init; }
    public bool IsPhysical { get; init; }
    public double? Volume { get; init; }
    public bool? Muted { get; init; }

    /// <summary>A source's channel count, when PipeWire reports it.</summary>
    public int? Channels { get; init; }

    /// <summary>A source's channel positions in port order, such as MONO, FL or AUX2.</summary>
    public List<string>? Positions { get; init; }

    /// <summary>The attached supported interface that publishes this node, or null.</summary>
    public string? DrivenUnit { get; init; }

    /// <summary>Kind 0 is a sink in the daemon's own listing.</summary>
    public bool IsSink => Kind == 0;
}

/// <summary>
/// What feeds a channel: <c>unit</c> (the interface's capture pair),
/// <c>source</c> (one port of an exact capture node) or <c>apps</c>.
/// </summary>
internal sealed record BindingEntry
{
    public string Kind { get; init; } = string.Empty;
    public string? Source { get; init; }
    public int? Pair { get; init; }
    public int? MonoChannel { get; init; }

    /// <summary>The supported unit whose node a strip is bound to, which the strip follows when the node is renamed.</summary>
    public string? Unit { get; init; }
}

/// <summary>
/// An attached supported unit, or one enabled as additional: the primary
/// (<see cref="Active"/>) and the units driven beside it, with their own
/// controls, which <c>setWaveControl</c> writes.
/// </summary>
internal sealed record WaveInterfaceEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
    public bool Enabled { get; init; }
    public bool Connected { get; init; }
    public Dictionary<string, JsonElement>? Capabilities { get; init; }
    public Dictionary<string, JsonElement>? State { get; init; }

    /// <summary>Driven as an additional interface and answering now.</summary>
    public bool Driven => !Active && Enabled && Connected;

    public bool Can(string capability) =>
        Capabilities?.TryGetValue(capability, out JsonElement value) == true && value.ValueKind == JsonValueKind.True;

    public int Count(string capability) =>
        Capabilities?.TryGetValue(capability, out JsonElement value) == true && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : 0;

    public bool Flag(string control) =>
        State?.TryGetValue(control, out JsonElement value) == true && value.ValueKind == JsonValueKind.True;

    public double Number(string control) =>
        State?.TryGetValue(control, out JsonElement value) == true && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : 0;
}

/// <summary>
/// Ducking as the daemon reports it: the switch, the key microphone, the
/// channels it lowers and the mixes it lowers them in, the three presets,
/// where it should run (<see cref="Processing"/>), what runs it now
/// (<see cref="Backend"/>: <c>device</c>, <c>software</c> or nothing) and
/// the unit's side in <see cref="Device"/>. A daemon that predates the
/// channels and mixes leaves them null, and they are not offered.
/// </summary>
internal sealed record DuckingEntry
{
    public bool Enabled { get; init; }
    public string Key { get; init; } = "xlr1";
    public List<string>? Channels { get; init; }
    public List<string>? Mixes { get; init; }
    public string Sensitivity { get; init; } = "medium";
    public string Amount { get; init; } = "medium";
    public string Speed { get; init; } = "medium";
    public string? Processing { get; init; }
    public string? Backend { get; init; }
    public bool Available { get; init; } = true;
    public string? Error { get; init; }
    public DuckingDeviceEntry? Device { get; init; }

    /// <summary>A short word for the switch row: where it runs, or what is wrong; null while off.</summary>
    [JsonIgnore]
    public string? Note => !Enabled ? null
        : Backend == "software" ? "in software"
        : Backend == "device" ? Device is { Held: false } ? "not held yet" : "on the unit"
        : !Available ? "cannot run"
        : "not running";

    /// <summary>The depth of the chosen amount in dB, which the reduction bar fills to.</summary>
    [JsonIgnore]
    public double Depth => Amount switch { "low" => 6, "high" => 24, _ => 12 };

    /// <summary>True while the unit runs it, or would once switched on.</summary>
    [JsonIgnore]
    public bool OnUnit => Backend == "device"
        || Backend is null && Processing == "device" && Device is { Ready: true };

    /// <summary>
    /// Where the key is heard, in a few words: the unit listens to its own
    /// input before anything on this computer, so an insert on XLR 1 does not
    /// change what ducks; software listens after the microphone's EQ and inserts.
    /// </summary>
    [JsonIgnore]
    public string KeyHeard => OnUnit ? "XLR 1 before inserts" : "after the EQ and inserts";

    /// <summary>
    /// Why the unit does not run it, while the unit is the choice and cannot;
    /// null otherwise. The daemon's sentence names the channel or mix, which
    /// the window shows; a terminal row has room for the reason alone.
    /// </summary>
    [JsonIgnore]
    public string? Why => Processing != "device" || Backend == "device" || Device is not { Ready: false } device ? null
        : device.Reason switch
        {
            "mixerOff" => "the software mixer is off",
            "keyNotSupported" => "the unit ducks only with XLR 1",
            "monitorReturn" => "the jacks' feed is not in the unit's mix",
            "auxNotChecked" => "the Aux port is not checked yet",
            "mixElsewhere" => "a ducked mix plays elsewhere too",
            "sharedOutput" => "the jacks play a mix not ducked",
            "partialMix" => "a channel in the mix is not ducked",
            "keyInMix" => "the key is heard in a ducked mix",
            "failed" => "the unit did not keep the ducking",
            "unsupportedBlock" => "the unit did not answer a read",
            _ => device.Note ?? device.Reason,
        };
}

/// <summary>The unit's side of ducking: whether it runs the setup, why not, and whether it held the last write.</summary>
internal sealed record DuckingDeviceEntry
{
    public string Unit { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Ready { get; init; } = true;
    public string? Reason { get; init; }
    public string? Note { get; init; }
    public List<string> Outputs { get; init; } = [];
    public bool Held { get; init; } = true;
}

/// <summary>
/// One of a unit's input-stage controls: <see cref="Device"/> null for the
/// primary, which <c>set</c> writes, or an additional unit's id, which
/// <c>setWaveControl</c> writes.
/// </summary>
internal sealed record UnitControl(string? Device, string Control);

/// <summary>Which input stage of which driven unit a channel carries.</summary>
internal sealed record UnitInputEntry
{
    public string Unit { get; init; } = string.Empty;
    public int Input { get; init; }
    public bool Primary { get; init; }
}

/// <summary>
/// A microphone's EQ as far as the terminal shows it: its switch, what runs
/// it and the choice of where. The bands are the window's to edit.
/// </summary>
internal sealed record EqEntry
{
    public bool Enabled { get; init; }

    /// <summary><c>device</c> while the unit's own processor runs it, <c>software</c>, or null while nothing does.</summary>
    public string? Backend { get; init; }
    public bool Available { get; init; } = true;
    public string? Error { get; init; }

    /// <summary>The channel's choice, <c>device</c> or <c>software</c>; null from a daemon that predates it.</summary>
    public string? Processing { get; init; }

    /// <summary>The unit the channel carries while its model runs the EQ itself, or null.</summary>
    public EqDeviceEntry? Device { get; init; }

    /// <summary>The choice can be made here: a unit that runs the EQ, and a daemon that reports the choice.</summary>
    [JsonIgnore]
    public bool Offered => Device is not null && Processing is "device" or "software";

    /// <summary>What runs the EQ, in a few words.</summary>
    [JsonIgnore]
    public string Status => Error is { Length: > 0 } error ? error
        : !Available ? "cannot run"
        : !Enabled ? "off"
        : Backend switch
        {
            "device" => $"on, on the {Device?.Name ?? "interface"}",
            "software" => "on, in software",
            _ => "on, starting",
        };

    /// <summary>
    /// Why the unit does not run it, while the choice is the unit and the
    /// unit cannot; null otherwise.
    /// </summary>
    [JsonIgnore]
    public string? Why => Device is not { Ready: false } device || Backend == "device" ? null : device.Reason switch
    {
        "firmwareOld" => $"firmware {device.Firmware ?? "?"}, needs {device.MinimumFirmware ?? "1.0.15"}",
        "firmwareUnknown" => $"firmware unknown, needs {device.MinimumFirmware ?? "1.0.15"}",
        "unsupportedBlock" => "EQ settings not recognised",
        "failed" => "the unit did not keep the EQ",
        _ => device.Note,
    };
}

/// <summary>The unit behind a microphone's EQ: its model, firmware and whether it runs the EQ now.</summary>
internal sealed record EqDeviceEntry
{
    public string Unit { get; init; } = string.Empty;
    public int Input { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Firmware { get; init; }
    public bool Ready { get; init; }
    public string? Reason { get; init; }
    public string? Note { get; init; }
    public string? MinimumFirmware { get; init; }
}

/// <summary>An interface attached to the machine, whether or not it is the active one.</summary>
internal sealed record DetectedDevice
{
    public string UsbId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Active { get; init; }
}

internal sealed record DeviceDescriptor
{
    public string Vendor { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string UsbId { get; init; } = string.Empty;
    public string? Note { get; init; }
}

internal sealed record MixerSnapshot
{
    public List<MixEntry> Mixes { get; init; } = [];
    public List<ChannelEntry> Channels { get; init; } = [];

    private List<ChannelEntry>? _shown;

    /// <summary>
    /// The channels worth drawing: every one the active device can feed. A
    /// snapshot is replaced rather than edited, so this is worked out once.
    /// </summary>
    [JsonIgnore]
    public List<ChannelEntry> Shown => _shown ??= [.. Channels.Where(channel => channel.Present)];

    private List<ChannelEntry>? _strips;

    /// <summary>
    /// The channels the mixer tab draws a strip for: the shown ones less
    /// those hidden in the layout editor. The matrix still lists a hidden
    /// channel, so its sends stay within reach.
    /// </summary>
    [JsonIgnore]
    public List<ChannelEntry> Strips => _strips ??= [.. Shown.Where(channel => !channel.Appearance.Hidden)];

    private List<MixEntry>? _masters;

    /// <summary>
    /// The mixes the mixer tab draws a master and sends for: every mix less
    /// those hidden in the layout editor. The matrix still lists a hidden
    /// mix, so its master and the sends into it stay within reach.
    /// </summary>
    [JsonIgnore]
    public List<MixEntry> Masters => _masters ??= [.. Mixes.Where(mix => !mix.Appearance.Hidden)];
    public List<string> MonitorOutputs { get; init; } = [];
    public Dictionary<string, string> MonitorFeeds { get; init; } = [];
    public string? DefaultMonitorMix { get; init; }
    public double? OutputVolume { get; init; }
    public bool MonitorMuted { get; init; }
    public bool MonitorMuteMicrophones { get; init; }
    public bool MicrophonePausePending { get; init; }
    public bool AuxPortEnabled { get; init; }
    public int LowCutHz { get; init; }
    public bool SoftClipGuard { get; init; }
    public bool SoftClipGuardAvailable { get; init; }
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }
    public List<StreamEntry> Streams { get; init; } = [];
    public Dictionary<string, List<InsertEntry>> Inserts { get; init; } = [];
    public string? LayoutWarning { get; init; }
}

/// <summary>
/// A state message, kept as one immutable snapshot the drawing code reads
/// without a lock. The hardware controls and the capabilities stay as raw JSON
/// values: they are a long list of flags that grows with every device, and the
/// terminal only ever asks for one by name.
/// </summary>
internal sealed record Snapshot
{
    public string DaemonVersion { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public string? Warning { get; init; }
    public DeviceDescriptor? Device { get; init; }
    public Dictionary<string, JsonElement> Capabilities { get; init; } = [];
    public Dictionary<string, JsonElement> State { get; init; } = [];
    public MixerSnapshot Mixer { get; init; } = new();
    public List<DeviceEntry> Devices { get; init; } = [];
    public List<string> Profiles { get; init; } = [];
    public string? ActiveProfile { get; init; }
    public string? RecallOnConnect { get; init; }
    public List<DetectedDevice> Detected { get; init; } = [];
    public List<WaveInterfaceEntry> WaveInterfaces { get; init; } = [];

    /// <summary>The ducking setup and where it runs, present whenever the daemon runs; null from one that predates it.</summary>
    public DuckingEntry? Ducking { get; init; }

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static Snapshot? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Snapshot>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The channel that carries the primary unit's microphone input 1 or 2,
    /// or null when none does. The daemon names it in <c>unitInput</c>. While
    /// no channel names one, which is the case with no unit connected and
    /// with a daemon that predates input bindings, a strip still on the
    /// interface stands for its input, as it always has.
    /// </summary>
    public ChannelEntry? Carrier(int input)
    {
        List<ChannelEntry> channels = Mixer.Channels;
        if (channels.FirstOrDefault(channel => channel.UnitInput is { Primary: true } unit && unit.Input == input) is { } named)
            return named;
        // The daemon names the other input but not this one: no channel carries it.
        if (channels.Any(channel => channel.UnitInput is { Primary: true })) return null;
        return Strip(input) is { UnitBound: true } strip ? strip : null;
    }

    /// <summary>XLR 1 or XLR 2 by input number, or null when the mixer has no such strip.</summary>
    public ChannelEntry? Strip(int input) =>
        Mixer.Channels.FirstOrDefault(channel => channel.Hardware && channel.Id == (input == 2 ? "xlr2" : "xlr1"));

    /// <summary>
    /// The device control behind a channel's microphone mute: <c>mute</c> or
    /// <c>mute2</c> of the primary on the channel carrying its input 1 or 2,
    /// or of an additional unit on the channel its <c>unitInput</c> names
    /// while that unit is driven. Null for every other channel and while the
    /// unit is not connected. A strip bound to another microphone is not an
    /// interface, so its key is a send mute like any other channel's.
    /// </summary>
    public UnitControl? UnitMute(ChannelEntry channel)
    {
        if (Connected && Carrier(1) is { } first && first.Id == channel.Id) return new(null, "mute");
        if (Connected && Carrier(2) is { } second && second.Id == channel.Id) return new(null, "mute2");
        if (channel.UnitInput is { Primary: false } other && Additional(other.Unit) is { Driven: true } unit
            && unit.Can("mute"))
            return new(unit.Id, other.Input == 2 ? "mute2" : "mute");
        return null;
    }

    /// <summary>An additional unit by its id, or null.</summary>
    public WaveInterfaceEntry? Additional(string id) =>
        WaveInterfaces.FirstOrDefault(unit => !unit.Active && unit.Id == id);

    /// <summary>The primary unit's id, the unit in the header, or null.</summary>
    [JsonIgnore]
    public string? PrimaryUnit => WaveInterfaces.FirstOrDefault(unit => unit.Active)?.Id;

    /// <summary>A unit control's current on or off value.</summary>
    public bool Flag(UnitControl control) => control.Device is null
        ? Flag(control.Control)
        : Additional(control.Device)?.Flag(control.Control) == true;

    /// <summary>Write a unit control: <c>set</c> on the primary, <c>setWaveControl</c> on an additional unit.</summary>
    public static void Send(DaemonLink link, UnitControl control, bool value)
    {
        if (control.Device is null)
            link.Send("set", body => { body["control"] = control.Control; body["value"] = value; });
        else
            link.Send("setWaveControl", body =>
            {
                body["device"] = control.Device;
                body["control"] = control.Control;
                body["value"] = value;
            });
    }

    /// <summary>
    /// The source a strip or capture input plays from: the name it was bound to, or,
    /// for a strip bound to a supported unit whose node was renamed (driven,
    /// or its card profile switched), the one node that unit publishes now.
    /// </summary>
    public string? BoundSource(ChannelEntry strip)
    {
        string? source = strip.SourceBound ? strip.Binding!.Source : strip.CaptureSource;
        if (source is null) return null;
        if (Devices.Any(device => !device.IsSink && device.Name == source)) return source;
        return strip.Binding?.Unit is { } unit
            && Devices.Where(device => !device.IsSink && !device.IsOwn && device.DrivenUnit == unit).ToArray() is [var renamed]
            ? renamed.Name : source;
    }

    /// <summary>
    /// What a heading over the interface's input calls it: the strip's own
    /// name while the input plays there, the channel that carries it
    /// otherwise, or that it is not in the mixer.
    /// </summary>
    public string InputHeading(int input)
    {
        ChannelEntry? carrier = Carrier(input);
        ChannelEntry? strip = Strip(input);
        if (carrier is not null && carrier.Id == strip?.Id) return carrier.Name;
        if (carrier is not null) return $"Input {input} on {carrier.Name}";
        // No mixer to place it in: the strip names the interface would give it.
        if (strip is null && !Mixer.Channels.Any(channel => channel.UnitInput is not null))
            return $"{(Capabilities.TryGetValue("xlrJacks", out JsonElement jacks) && jacks.ValueKind == JsonValueKind.False ? "Mic" : "XLR")} {input}";
        return $"Input {input}, not in the mixer";
    }

    /// <summary>
    /// What feeds a bindable strip, as a person reads it: the interface's own
    /// input, or the source's description with its port.
    /// </summary>
    public string BindingLabel(ChannelEntry strip) => strip.SourceBound
        ? $"{SourceName(BoundSource(strip)!)}, port {(strip.Binding!.MonoChannel ?? 0) + 1}"
        : UnitInputLabel(strip);

    /// <summary>The interface's own input behind a strip: its XLR jack, or a USB microphone's capsule.</summary>
    public string UnitInputLabel(ChannelEntry strip) =>
        UnitXlrJacks ? $"Interface jack ({(strip.Id == "xlr2" ? "XLR 2" : "XLR 1")})" : "Built-in microphone";

    /// <summary>False on a Wave:3 or Wave:1, whose input is the microphone's own capsule.</summary>
    public bool UnitXlrJacks =>
        !(Capabilities.TryGetValue("xlrJacks", out JsonElement jacks) && jacks.ValueKind == JsonValueKind.False);

    /// <summary>A source's description from the device list, or its node name when it is not listed.</summary>
    public string SourceName(string source) =>
        Devices.FirstOrDefault(device => !device.IsSink && device.Name == source) is { Description.Length: > 0 } listed
            ? listed.Description
            : source;

    /// <summary>
    /// The capture sources a microphone strip can take, as the window lists
    /// them: every source but OpenXLR's own nodes and the primary unit's,
    /// which is the interface's own input listed first, and none the daemon
    /// would refuse (an OpenXLR name, a monitor tap, a control character,
    /// over 256 characters). Another supported unit's node is offered.
    /// </summary>
    [JsonIgnore]
    public List<DeviceEntry> BindingChoices =>
        [.. Devices.Where(device => !device.IsSink && !device.IsOwn && Selectable(device.Name)
            && (device.DrivenUnit is null || device.DrivenUnit != PrimaryUnit))];

    /// <summary>The attached supported unit behind a source that is not driven yet, which a binding can offer to drive.</summary>
    public WaveInterfaceEntry? UndrivenUnit(string? source) =>
        source is not null && Devices.FirstOrDefault(device => !device.IsSink && device.Name == source)?.DrivenUnit is { } id
        && WaveInterfaces.FirstOrDefault(unit => unit.Id == id) is { Active: false, Enabled: false, Connected: true } unit
            ? unit : null;

    private static bool Selectable(string name) =>
        name.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
        && !name.StartsWith("OpenXLR", StringComparison.Ordinal) && !name.EndsWith(".monitor", StringComparison.Ordinal);

    public bool Can(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public int Count(string capability) =>
        Capabilities.TryGetValue(capability, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    public bool Flag(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public double Number(string control) =>
        State.TryGetValue(control, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
}
