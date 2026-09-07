using System.Reflection;
using System.Text.Json.Serialization;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

// The OpenXLR daemon control protocol: JSON messages over a WebSocket at
// ws://127.0.0.1:37890/ws. Clients (Avalonia UI, OpenDeck plugin, CLI) connect,
// receive a "state" message immediately and on every change, and send commands.
// One connection carries both the hardware controls and the submixer, so a
// fader move by one client is broadcast to all. camelCase on the wire.

/// <summary>Command from a client to the daemon.</summary>
public sealed record Command
{
    /// <summary>
    /// Command name. <see cref="WebSocketHub"/> owns dispatch; the root README's
    /// WebSocket table is the canonical public list so this DTO cannot drift
    /// into a second protocol specification.
    /// </summary>
    [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";

    /// <summary>
    /// Optional correlation id. When present, a mixer command is answered
    /// with a <c>commandResult</c> message carrying it, after the state that
    /// reflects the outcome, so an editor can wait for the acknowledgement.
    /// </summary>
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }

    /// <summary>For "set": the control name (see <see cref="ControlNames"/>).</summary>
    [JsonPropertyName("control")] public string? Control { get; init; }

    /// <summary>The value: number for levels, bool for toggles.</summary>
    [JsonPropertyName("value")] public System.Text.Json.JsonElement Value { get; init; }

    /// <summary>Mixer commands: which channel.</summary>
    [JsonPropertyName("channel")] public string? Channel { get; init; }

    /// <summary>Mixer commands: which mix.</summary>
    [JsonPropertyName("mix")] public string? Mix { get; init; }

    /// <summary>setLayoutOrder: complete ordered lists of editable stable IDs.</summary>
    [JsonPropertyName("channels")] public List<string>? Channels { get; init; }
    [JsonPropertyName("mixes")] public List<string>? Mixes { get; init; }

    /// <summary>"assignStream": the PipeWire stream (sink-input) id to route.</summary>
    [JsonPropertyName("streamId")] public int? StreamId { get; init; }

    /// <summary>
    /// "setMonitorOutput": PipeWire node.name (null disconnects);
    /// "setMonitorFeed": the selected output whose feed changes (with "mix"); or
    /// "setActiveDevice": the interface's vvvv:pppp id.
    /// </summary>
    [JsonPropertyName("device")] public string? Device { get; init; }

    /// <summary>For "setMonitorOutputs": every output the monitor mix should feed.</summary>
    [JsonPropertyName("devices")] public List<string>? Devices { get; init; }

    /// <summary>For "assignApp": the application identity to route.</summary>
    [JsonPropertyName("identity")] public string? Identity { get; init; }

    /// <summary>For "assignApp": display label when pre-registering an app.</summary>
    [JsonPropertyName("label")] public string? Label { get; init; }

    /// <summary>"setEnforcedDefaults": devices to hold as system defaults (null = don't enforce).</summary>
    [JsonPropertyName("sink")] public string? Sink { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }

    /// <summary>"saveProfile" / "loadProfile" / "deleteProfile": the profile name;
    /// "setRecallOnConnect": the profile to recall on connect, empty to clear.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>"setInserts": the channel's whole insert chain, in order.</summary>
    [JsonPropertyName("inserts")] public List<InsertDefinition>? Inserts { get; init; }

    /// <summary>"setInsertParam" / "setInsertBypass": which insert.</summary>
    [JsonPropertyName("insertId")] public string? InsertId { get; init; }

    /// <summary>"setInsertParam": the control port symbol.</summary>
    [JsonPropertyName("symbol")] public string? Symbol { get; init; }

    /// <summary>"installPlugin": the file or directory the user picked, absolute.</summary>
    [JsonPropertyName("path")] public string? Path { get; init; }
}

/// <summary>Reply to "getPluginSetup": where plugins go and what bridges Windows ones.</summary>
public sealed record PluginSetupMessage(PluginSetup Setup)
{
    [JsonPropertyName("type")] public string Type => "pluginSetup";
    [JsonPropertyName("hostInstalled")] public bool HostInstalled => Setup.HostInstalled;
    [JsonPropertyName("lv2Directory")] public string Lv2Directory => Setup.Lv2Directory;
    [JsonPropertyName("clapDirectory")] public string ClapDirectory => Setup.ClapDirectory;
    [JsonPropertyName("vst3Directory")] public string Vst3Directory => Setup.Vst3Directory;
    /// <summary>yabridgectl's version when it is installed, else null.</summary>
    [JsonPropertyName("yabridge")] public string? Yabridge => Setup.YabridgeVersion;
    [JsonPropertyName("wine")] public bool Wine => Setup.Wine;
    [JsonPropertyName("windowsDirectories")] public IReadOnlyList<string> WindowsDirectories => Setup.WindowsDirectories;
    [JsonIgnore] public PluginSetup Setup { get; } = Setup;
}

/// <summary>Reply to "installPlugin", "syncWindowsPlugins" and "rescanPlugins": what happened, and what the catalogue gained.</summary>
public sealed record PluginInstallMessage(bool Ok, string Message, IReadOnlyList<string> Installed, int Added, int Total)
{
    [JsonPropertyName("type")] public string Type => "pluginInstall";
    [JsonPropertyName("ok")] public bool Ok { get; } = Ok;
    /// <summary>A sentence or two for the user.</summary>
    [JsonPropertyName("message")] public string Message { get; } = Message;
    /// <summary>The bundles or directories installed, by name.</summary>
    [JsonPropertyName("installed")] public IReadOnlyList<string> Installed { get; } = Installed;
    /// <summary>Plugins in the catalogue now that were not before.</summary>
    [JsonPropertyName("added")] public int Added { get; } = Added;
    [JsonPropertyName("total")] public int Total { get; } = Total;
}

/// <summary>Reply to "listPlugins": everything the insert picker can offer.</summary>
public sealed record PluginsMessage
{
    public PluginsMessage(IReadOnlyList<PluginInfo> plugins) => Plugins = plugins;

    [JsonPropertyName("type")] public string Type => "plugins";
    [JsonPropertyName("plugins")] public IReadOnlyList<PluginInfo> Plugins { get; }
}

/// <summary>Full-state push from the daemon to all clients.</summary>
public sealed record StateMessage
{
    [JsonPropertyName("type")] public string Type => "state";
    /// <summary>
    /// The daemon's own version, so a client can tell when it is talking to
    /// a daemon left running across a package upgrade (the UI shows a
    /// restart prompt; a daemon older than 0.1.13 omits the field).
    /// </summary>
    [JsonPropertyName("daemonVersion")] public string? DaemonVersion { get; init; }
    /// <summary>
    /// A condition the user should know about, in one sentence, or null:
    /// today, mixer settings that cannot be written to disk. Clients show
    /// it where they show the daemon's status.
    /// </summary>
    [JsonPropertyName("warning")] public string? Warning { get; init; }
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("device")] public DeviceDescriptor? Device { get; init; }
    [JsonPropertyName("capabilities")] public DeviceCapabilities? Capabilities { get; init; }
    [JsonPropertyName("state")] public DeviceState? State { get; init; }
    /// <summary>Submixer state; null until the mixer graph is built.</summary>
    [JsonPropertyName("mixer")] public MixerState? Mixer { get; init; }

    /// <summary>
    /// Every selectable sink and source in the graph, real or virtual, so
    /// clients can offer output and input pickers. Null when the mixer is off.
    /// </summary>
    [JsonPropertyName("devices")] public IReadOnlyList<AudioNode>? Devices { get; init; }

    /// <summary>Saved profile names, so any client can offer recall.</summary>
    [JsonPropertyName("profiles")] public IReadOnlyList<string>? Profiles { get; init; }
    /// <summary>
    /// The profile last recalled or saved for the active device, or null.
    /// A bookkeeping value: later manual changes do not clear it, so a
    /// client shows it as "last recalled", not "state matches".
    /// </summary>
    [JsonPropertyName("activeProfile")] public string? ActiveProfile { get; init; }
    /// <summary>
    /// The profile recalled whenever the active device connects fresh
    /// (daemon start, replug, switch to it), or null when none is set.
    /// </summary>
    [JsonPropertyName("recallOnConnect")] public string? RecallOnConnect { get; init; }

    /// <summary>Every attached supported interface, so clients can offer a picker.</summary>
    [JsonPropertyName("detected")] public IReadOnlyList<DetectedDevice>? Detected { get; init; }
}

public sealed record DetectedDevice(
    [property: JsonPropertyName("usbId")] string UsbId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("active")] bool Active);

public sealed record DeviceDescriptor(string Vendor, string Model, string UsbId);

/// <summary>
/// Live levels, sent far more often than full state and kept deliberately small:
/// ids are "ch:&lt;channel&gt;" and "mix:&lt;mix&gt;", values are peaks in 0..1.
/// </summary>
public sealed record MetersMessage
{
    public MetersMessage(IReadOnlyDictionary<string, double[]> levels) => Levels = levels;

    [JsonPropertyName("type")] public string Type => "meters";
    [JsonPropertyName("levels")] public IReadOnlyDictionary<string, double[]> Levels { get; }
}

public sealed record ErrorMessage
{
    public ErrorMessage(string message) => Message = message;

    [JsonPropertyName("type")] public string Type => "error";
    [JsonPropertyName("message")] public string Message { get; }
}

/// <summary>The outcome of a command that carried a requestId; error is null on success.</summary>
public sealed record CommandResultMessage(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("error")] string? Error)
{
    [JsonPropertyName("type")] public string Type => "commandResult";
}

/// <summary>Canonical control names accepted by "set".</summary>
public static class ControlNames
{
    public const string Gain = "gain";                       // int dB
    public const string Mute = "mute";                       // bool
    public const string LowCut = "lowCut";                   // bool
    public const string Expander = "expander";               // bool
    public const string VoiceTune = "voiceTune";             // bool
    public const string VoiceTuneStrength = "voiceTuneStrength"; // int 0..100
    public const string HpVolumeDb = "hpVolumeDb";           // number dB
    public const string LowImpedance = "lowImpedance";       // bool
    public const string Crossfade = "crossfade";             // int 0..200
    public const string Phantom = "phantom";                 // bool
    public const string ClipGuard = "clipGuard";             // bool
    public const string Compressor = "compressor";           // bool
    public const string OutHp1 = "outHp1";                   // bool
    public const string OutHp2 = "outHp2";                   // bool
    public const string OutUsbAux = "outUsbAux";             // bool
    public const string OutLineOut = "outLineOut";           // bool
    public const string AuxLevelDb = "auxLevelDb";           // number dB (-60..0)
    public const string AuxLevelLock = "auxLevelLock";       // bool
}

/// <summary>Reply to "getDiagnostics": raw vendor blocks for bug reports.</summary>
public sealed record DiagnosticsMessage(IReadOnlyDictionary<string, string> Blocks)
{
    [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type => "diagnostics";
}

/// <summary>The daemon's version as stamped by the build (Directory.Build.props).</summary>
public static class DaemonVersion
{
    public static readonly string Current =
        typeof(DaemonVersion).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";
}
