using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What survives a restart: fader levels, mutes, device choices, and the per
/// application channel assignments the user made by hand. Stored as JSON under
/// the XDG config directory.
///
/// Only user decisions are persisted. The graph itself is rebuilt from scratch
/// each run, so a stale file can never leave orphaned PipeWire nodes behind.
/// </summary>
public sealed record MixerSettings
{
    /// <summary>
    /// Hardware strips bound to a source, by strip id (<c>xlr1</c>, <c>xlr2</c>);
    /// a strip left out takes the interface's pair. Machine wiring, never profile state.
    /// </summary>
    public Dictionary<string, SourceBinding>? InputBindings { get; init; }

    /// <summary>
    /// XLR 1's source in files written before <see cref="InputBindings"/>.
    /// Loading moves it into <see cref="InputBindings"/>, so a save never writes it.
    /// </summary>
    public PrimaryInputBinding? PrimaryInput { get; init; }

    /// <summary>Ordered application channels; null preserves the legacy defaults.</summary>
    public List<UserChannelDefinition>? UserChannels { get; init; }

    /// <summary>Ordered editable mixes, including monitors. Null reads the legacy user list.</summary>
    public List<UserMixDefinition>? Mixes { get; init; }

    /// <summary>
    /// Legacy user mixes, excluding Monitor A and B. Read only when Mixes is
    /// null; saves still write it next to Mixes so 0.2.0 keeps the added mixes.
    /// </summary>
    public List<UserMixDefinition>? UserMixes { get; init; }

    /// <summary>Exclusive channel groups, each a named list of channel ids.</summary>
    public List<ExclusiveGroupDefinition> ExclusiveGroups { get; init; } = [];

    /// <summary>
    /// The saved settings with the monitor selection replaced by an
    /// environment or command-line override, when one is given. Both the
    /// legacy single field and the current list are set, because
    /// ApplySettings prefers the list and a saved list used to win over
    /// the override silently.
    /// </summary>
    public MixerSettings WithMonitorOverride(string? output) =>
        output is null ? this : this with { MonitorOutput = output, MonitorOutputs = [output] };

    /// <summary>Mix id to master volume.</summary>
    public Dictionary<string, double> MixVolumes { get; init; } = [];

    /// <summary>Mix ids that are muted.</summary>
    public List<string> MixMuted { get; init; } = [];

    /// <summary>"channel|mix" to level.</summary>
    public Dictionary<string, double> Levels { get; init; } = [];

    /// <summary>Channel id to its channel volume; a channel absent here is at full volume.</summary>
    public Dictionary<string, double> ChannelVolumes { get; init; } = [];

    /// <summary>"channel|mix" entries that are muted.</summary>
    public List<string> ChannelMuted { get; init; } = [];

    /// <summary>Single monitor output from older files; superseded by MonitorOutputs.</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>All selected monitor outputs (the monitor mixes can feed several).</summary>
    public List<string> MonitorOutputs { get; init; } = [];

    /// <summary>Listening is paused without changing mix mutes. Never profile state.</summary>
    public bool MonitorMuted { get; init; }

    /// <summary>Also pause XLR strips and mono capture channels while monitoring is paused. Never profile state.</summary>
    public bool MonitorMuteMicrophones { get; init; }

    /// <summary>Original native mute flags while OpenXLR holds selected outputs muted. Never profile state.</summary>
    public Dictionary<string, bool> MonitorOutputMutes { get; init; } = [];

    /// <summary>Output name to the monitor mix feeding it; absent = the stable default monitor mix.</summary>
    public Dictionary<string, string> MonitorFeeds { get; init; } = [];

    /// <summary>
    /// Selected output name to the description it last had in the device
    /// list, so an output that is away can still be named. Only selected
    /// outputs keep an entry. Machine state, never profile state.
    /// </summary>
    public Dictionary<string, string> MonitorOutputLabels { get; init; } = [];

    /// <summary>Application identity to channel id, from manual assignments.</summary>
    public Dictionary<string, string> AppOverrides { get; init; } = [];

    /// <summary>Automatically classify applications without a saved assignment. Machine state, never profile state.</summary>
    public bool RouteAppsByDefault { get; init; } = true;

    /// <summary>
    /// The block size forced on the graph in frames, or null to leave it to
    /// PipeWire. Loading drops a value outside <see cref="Mixer.BlockSizes"/>.
    /// Machine state, never profile state.
    /// </summary>
    public int? BlockSize { get; init; }

    /// <summary>Leave Chromium output selection to the browser companion and desktop. Machine state only.</summary>
    public bool PreserveBrowserOutputs { get; init; }

    /// <summary>Remembered browser origins and their application channel. Titles and runtime tab ids are never saved.</summary>
    public Dictionary<string, string> BrowserSiteChannels { get; init; } = [];

    /// <summary>Every app ever seen, so the list survives silence and restarts.</summary>
    public List<SavedApp> KnownApps { get; init; } = [];

    /// <summary>Software low cut on the first XLR channel (0, 80, or 120 Hz).</summary>
    public int LowCutHz { get; init; }

    /// <summary>Software ClipGuard (post-ADC hard limiter) on the first XLR channel.</summary>
    public bool SoftClipGuard { get; init; }

    /// <summary>Delay faster mixes to line up with the slowest mix's inserts. Off unless chosen; not part of a profile.</summary>
    public bool CompensateMixLatency { get; init; }

    /// <summary>Plugin insert chains by channel id.</summary>
    public Dictionary<string, List<InsertDefinition>> Inserts { get; init; } = [];

    /// <summary>
    /// Microphone EQs by channel id. A microphone absent here has the
    /// defaults with the EQ off. An entry for a channel that is not a
    /// microphone is kept as it is.
    /// </summary>
    public Dictionary<string, MicEq> Eq { get; init; } = [];

    /// <summary>
    /// Where each microphone's EQ runs when its unit can run it, by channel
    /// id: <c>software</c>, or <c>device</c> for a channel absent here.
    /// Machine wiring like <see cref="InputBindings"/>, never profile state.
    /// </summary>
    public Dictionary<string, string> EqProcessing { get; init; } = [];

    /// <summary>
    /// Where each microphone's compressor and expander run when its unit can
    /// run them, by channel id: <c>software</c>, or <c>device</c> for a channel
    /// absent here. Machine wiring like <see cref="EqProcessing"/>.
    /// </summary>
    public Dictionary<string, string> DynamicsProcessing { get; init; } = [];

    /// <summary>
    /// The Wave XLR Pro's sections 18 and 54 as they were before a jack feed
    /// first used them, by section ("18", "54"), as the hex of their first
    /// 16 bytes (cells, membership, byte 14 and the marker). Taken before the
    /// first write and put back, then dropped, once no jack plays that
    /// section, so a unit shared with Wave Link comes back as it was. Machine
    /// wiring like <see cref="EqProcessing"/>, never profile state.
    /// </summary>
    public Dictionary<string, string> HardwareRestore { get; init; } = [];

    /// <summary>
    /// Where the mixes the Wave XLR Pro's outputs play are built:
    /// <c>software</c> (the default) or <c>hardware</c> (see
    /// <see cref="MixingMode"/>). Machine wiring, never profile state.
    /// </summary>
    public string Mixing { get; init; } = MixingMode.Software;

    /// <summary>
    /// The USB playback pair each channel streams on in hardware mixing, by
    /// channel id, kept across restarts, layout edits and trips through
    /// software mixing (see <see cref="HardwareMixingPlanner"/>).
    /// </summary>
    public Dictionary<string, int> HardwarePairs { get; init; } = [];

    /// <summary>The Wave XLR Pro section each jack feed takes in hardware mixing, by feed, kept like <see cref="HardwarePairs"/>.</summary>
    public Dictionary<string, int> HardwareSections { get; init; } = [];

    /// <summary>What the unit held before hardware mixing, while the mode runs or is being left; null otherwise (see <see cref="OpenXLR.Core.Mixing.HardwareEntry"/>).</summary>
    public HardwareEntry? HardwareEntry { get; init; }

    /// <summary>
    /// Noise removal by channel id. A microphone absent here has it off. An
    /// entry for a channel that is not a microphone is kept as it is.
    /// </summary>
    public Dictionary<string, MicNoise> NoiseRemoval { get; init; } = [];

    /// <summary>
    /// The voice changer by channel id. A microphone absent here has it off.
    /// An entry for a channel that is not a microphone is kept as it is.
    /// </summary>
    public Dictionary<string, MicVoice> VoiceChanger { get; init; } = [];

    /// <summary>
    /// The compressor on this computer by channel id. A microphone absent here
    /// has it off. An entry for a channel that is not a microphone is kept.
    /// </summary>
    public Dictionary<string, MicCompressor> Compressor { get; init; } = [];

    /// <summary>
    /// The expander on this computer by channel id. A microphone absent here
    /// has it off. An entry for a channel that is not a microphone is kept.
    /// </summary>
    public Dictionary<string, MicExpander> Expander { get; init; } = [];

    /// <summary>
    /// The monitor mixes that hear the microphones dry, before their software
    /// effects, by mix id. A monitor mix absent here, or false, hears them
    /// through their effects as every other mix does. Saved as
    /// <c>dryMonitors</c>; an older file's per-microphone <c>monitorDry</c>
    /// map is not read.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("dryMonitors")]
    public Dictionary<string, bool> MonitorDry { get; init; } = [];

    /// <summary>
    /// The output EQ of each mix by mix id, in the microphone EQ's shape. A
    /// mix absent here has the defaults with the EQ off; an id that is no
    /// mix of the layout is dropped when the file is read.
    /// </summary>
    public Dictionary<string, MicEq> MixEq { get; init; } = [];

    /// <summary>The ducking setup and processing choice. Absent takes the layout's default targets, switched off.</summary>
    public DuckingSetup? Ducking { get; init; }

    /// <summary>
    /// Every plugin the saved chains name. A client that reconnects while the
    /// daemon is still starting asks for the catalogue before the chains are
    /// restored, and this is what they will hold.
    /// </summary>
    public IReadOnlyCollection<(string Kind, string Plugin)> InsertPlugins()
        => [.. Inserts.Values.SelectMany(list => list).Select(i => (i.Kind, i.Plugin))];

    /// <summary>
    /// Whether the Aux mix feeds the USB Aux port. Null in files written before
    /// the Aux mix existed; migrated from the old monitor-destination choice.
    /// </summary>
    public bool? AuxPortEnabled { get; init; }

    /// <summary>
    /// Devices to enforce as the system defaults, Wave Link style: when set,
    /// the daemon re-asserts them every sweep so nothing else can steal them.
    /// Null means do not enforce.
    /// </summary>
    public string? EnforcedDefaultSink { get; init; }
    public string? EnforcedDefaultSource { get; init; }

    /// <summary>
    /// Icon, colour and hidden flag by <c>channel:&lt;id&gt;</c> or
    /// <c>mix:&lt;id&gt;</c>. Part of the layout, not of a profile.
    /// </summary>
    public Dictionary<string, LayoutAppearance> Appearance { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>~/.config/openxlr/mixer.json, honouring XDG_CONFIG_HOME.</summary>
    public static string DefaultPath => OpenXlrPaths.ConfigFile("mixer.json");

    /// <summary>Read settings, or null when there is no file or it is unreadable.</summary>
    public static MixerSettings? Load(string? path = null) => Load(path, out _);

    /// <summary>
    /// Read settings, or null when there is no file or it is unreadable.
    /// <paramref name="warning"/> is null when the file was read as is. It names
    /// the entries dropped from a file that could otherwise be kept, or the
    /// reason a file could not be read at all. Such a file is copied next to
    /// itself with a ".corrupt" suffix, because the next save replaces it.
    /// </summary>
    public static MixerSettings? Load(string? path, out string? warning)
    {
        path ??= DefaultPath;
        warning = null;
        try
        {
            if (!File.Exists(path)) return null;
            RegularFile.Require(path);
            MixerSettings? settings = JsonSerializer.Deserialize<MixerSettings>(File.ReadAllText(path), Json);
            if (settings is null) return null;
            settings = SavedMixerValidation.Sanitize(settings, out IReadOnlyList<string> dropped);
            if (dropped.Count > 0) warning = $"{path}: dropped malformed entries ({string.Join(", ", dropped)})";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the daemon starting, and the copy
            // survives the save that would otherwise overwrite it.
            warning = $"{path}: {ex.Message}";
            string copy = path + ".corrupt";
            try
            {
                RegularFile.Require(path);
                OpenXlrPaths.WriteAtomic(copy, File.ReadAllBytes(path));
                warning += $"; copy kept as {copy}";
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                warning += $"; could not keep a copy as {copy}: {copyEx.Message}";
            }
            return null;
        }
    }

    /// <summary>
    /// Write atomically so a crash mid-write cannot corrupt the file. Never
    /// throws for a failed write (losing one is not worth taking the daemon
    /// down) but returns the reason, so the caller can keep the change
    /// pending, retry, and tell the user.
    /// </summary>
    public string? Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            OpenXlrPaths.WriteAtomicJson(path, this, Json);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path}: {ex.Message}";
        }
    }
}

/// <summary>A remembered application: identity, display label, channel.</summary>
public sealed record SavedApp(string Identity, string Label, string ChannelId);

public sealed record UserChannelDefinition(string Id, string Name, string? CaptureSource = null, int CapturePair = 0, int? CaptureMonoChannel = null, string? CaptureUnit = null);
public sealed record UserMixDefinition(string Id, string Name)
{
    /// <summary>"monitor" or "virtualMic"; absent in older settings, which contain only virtual microphones.</summary>
    public string Kind { get; init; } = "virtualMic";

    /// <summary>A kind a user can create; anything else is dropped on load.</summary>
    public bool HasUserKind => Kind is "monitor" or "virtualMic";
}
