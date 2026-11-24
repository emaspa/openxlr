using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>The format a save writes as <see cref="Version"/>.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The largest mixer.json read or written.</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The format of the release that last wrote the file; 0 in files written
    /// before it was recorded. A save always writes <see cref="CurrentVersion"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Version { get; init; }

    /// <summary>
    /// Top-level keys this release does not know, such as a setting a newer
    /// release wrote. The mixer keeps them and every save writes them back
    /// unchanged, so stepping back one release does not erase them.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

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

    /// <summary>
    /// Channel ids whose sends are linked (<c>setLinkedSends</c>): every send
    /// of the channel plays at one level. A channel absent here is not linked.
    /// </summary>
    public List<string> LinkedSends { get; init; } = [];

    /// <summary>Single monitor output from older files; superseded by MonitorOutputs.</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>All selected monitor outputs (the monitor mixes can feed several).</summary>
    public List<string> MonitorOutputs { get; init; } = [];

    /// <summary>Listening is paused without changing mix mutes. Never profile state.</summary>
    public bool MonitorMuted { get; init; }

    /// <summary>Also pause XLR strips and mono capture channels while monitoring is paused. Never profile state.</summary>
    public bool MonitorMuteMicrophones { get; init; }

    /// <summary>Volume held on one PipeWire output. Machine state, never profile state.</summary>
    public OutputVolumeLock? OutputVolumeLock { get; init; }

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

    /// <summary>
    /// Selected Bluetooth outputs whose link comes down while their feed is
    /// silent. Empty when absent; a name that is not a selected Bluetooth
    /// output is dropped.
    /// </summary>
    public List<string> MonitorIdleRelease { get; init; } = [];

    /// <summary>Application identity to channel id, from manual assignments.</summary>
    public Dictionary<string, string> AppOverrides { get; init; } = [];

    /// <summary>Remembered playback volume by application identity, independent of its route.</summary>
    public Dictionary<string, double> AppVolumes { get; init; } = [];

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

    /// <summary>Remembered playback volume for routed browser tabs by website origin.</summary>
    public Dictionary<string, double> BrowserSiteVolumes { get; init; } = [];
    public Dictionary<string, double> BrowserVolumeLocks { get; init; } = [];

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

    /// <summary>
    /// The Wave XLR Pro's maximizer amount of each mix by mix id, 1 to 100,
    /// which runs while hardware mixing builds the mix alone in a section. A
    /// mix absent here has it off; an amount out of range is dropped when
    /// the file is read.
    /// </summary>
    public Dictionary<string, int> MixMaximizer { get; init; } = [];

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
        // Named so the loader can list the fields before the first serializer call fills it in.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>A hand-edited file may keep a trailing comma or a comment.</summary>
    private static readonly JsonDocumentOptions Document = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>~/.config/openxlr/mixer.json, honouring XDG_CONFIG_HOME.</summary>
    public static string DefaultPath => OpenXlrPaths.ConfigFile("mixer.json");

    /// <summary>Read settings, or null when neither the file nor its backup can be read.</summary>
    public static MixerSettings? Load(string? path = null) => Load(path, out _);

    /// <summary>
    /// Read settings, or null when neither the file nor its backup can be read.
    /// <paramref name="warning"/> is null when the file was read as is. It names
    /// the entries dropped from a file that could otherwise be kept, or the
    /// reason a file could not be read at all. Such a file is copied next to
    /// itself with a ".corrupt" suffix, because the next save replaces it, and
    /// the settings come from the ".bak" copy the last save kept, when there is one.
    /// </summary>
    public static MixerSettings? Load(string? path, out string? warning)
    {
        path ??= DefaultPath;
        warning = null;
        if (File.Exists(path))
        {
            try
            {
                return Read(path, out warning);
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
            }
        }
        string backup = path + ".bak";
        if (!File.Exists(backup)) return null;
        warning = (warning ?? $"{path}: the file is missing") + $"; read the backup {backup}";
        try
        {
            MixerSettings settings = Read(backup, out string? dropped);
            if (dropped is not null) warning += $"; {dropped}";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning += $", which cannot be read either: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// One file's settings. A value of the wrong type is dropped on its own:
    /// one entry of a list or a map, or a whole field otherwise. Throws when
    /// the file is not a JSON object at all.
    /// </summary>
    private static MixerSettings Read(string path, out string? warning)
    {
        warning = null;
        string text = RegularFile.ReadText(path, MaxFileBytes);
        var dropped = new List<string>();
        JsonObject readable;
        using (JsonDocument document = JsonDocument.Parse(text, Document))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("the file does not hold an object");
            Dictionary<string, Type> fields = Json.GetTypeInfo(typeof(MixerSettings)).Properties
                .Where(p => !p.IsExtensionData).ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.Ordinal);
            readable = new JsonObject();
            foreach (JsonProperty field in document.RootElement.EnumerateObject())
            {
                // A key this release does not know is kept as it is.
                if (!fields.TryGetValue(field.Name, out Type? type)) readable[field.Name] = Canonical(field.Value);
                else if (Readable(field.Value, type, field.Name, dropped, out JsonNode? value)) readable[field.Name] = value;
            }
        }
        MixerSettings settings = readable.Deserialize<MixerSettings>(Json) ?? throw new JsonException("the file holds null");
        settings = SavedMixerValidation.Sanitize(settings, out IReadOnlyList<string> invalid);
        dropped.AddRange(invalid);
        if (dropped.Count > 0) warning = $"{path}: dropped malformed entries ({string.Join(", ", dropped)})";
        return settings;
    }

    /// <summary>
    /// The value as JSON this release can read as <paramref name="type"/>, or
    /// false when it cannot. A list or a map keeps every entry that reads and
    /// names each one that does not in <paramref name="dropped"/>.
    /// </summary>
    private static bool Readable(JsonElement value, Type type, string at, List<string> dropped, out JsonNode? node)
    {
        Type? generic = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
        if (generic == typeof(Dictionary<,>) && value.ValueKind == JsonValueKind.Object)
        {
            Type entry = type.GetGenericArguments()[1];
            var map = new JsonObject();
            foreach (JsonProperty item in value.EnumerateObject())
                if (Readable(item.Value, entry, $"{at}.{item.Name}", dropped, out JsonNode? kept)) map[item.Name] = kept;
            node = map;
            return true;
        }
        if (generic == typeof(List<>) && value.ValueKind == JsonValueKind.Array)
        {
            Type entry = type.GetGenericArguments()[0];
            var list = new JsonArray();
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
                if (Readable(item, entry, $"{at}[{index++}]", dropped, out JsonNode? kept)) list.Add(kept);
            node = list;
            return true;
        }
        node = Canonical(value);
        try
        {
            _ = node.Deserialize(type, Json);
            return true;
        }
        catch (JsonException)
        {
            dropped.Add($"{at}: wrong type");
            node = null;
            return false;
        }
    }

    /// <summary>A copy of the value without the file's comments and trailing commas, which the serializer refuses.</summary>
    private static JsonNode? Canonical(JsonElement value) => JsonNode.Parse(value.GetRawText(), documentOptions: Document);

    /// <summary>
    /// Write atomically so a crash mid-write cannot corrupt the file, after
    /// copying the file it replaces to ".bak". Never throws for a failed write
    /// (losing one is not worth taking the daemon down) but returns the
    /// reason, so the caller can keep the change pending, retry, and tell the user.
    /// </summary>
    public string? Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            KeepBackup(path);
            OpenXlrPaths.WriteAtomicJson(path, this with { Version = CurrentVersion }, Json, MaxFileBytes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path}: {ex.Message}";
        }
    }

    /// <summary>
    /// Copy the file a save is about to replace to ".bak", when it still holds
    /// a JSON object. A broken file never replaces the backup, which is then
    /// the last good one. A backup that cannot be kept does not stop the save.
    /// </summary>
    private static void KeepBackup(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            byte[] previous = RegularFile.ReadBytes(path, MaxFileBytes);
            using (JsonDocument document = JsonDocument.Parse(RegularFile.Decode(previous), Document))
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            OpenXlrPaths.WriteAtomic(path + ".bak", previous);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // The new settings matter more than a copy of the old ones.
        }
    }
}

/// <summary>A remembered application: identity, display label, channel.</summary>
public sealed record SavedApp(string Identity, string Label, string ChannelId);

public sealed record UserChannelDefinition(string Id, string Name, string? CaptureSource = null, int CapturePair = 0, int? CaptureMonoChannel = null, string? CaptureUnit = null)
{
    /// <summary>A capture input on its own clock; written only when on, and off in older settings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CaptureOwnClock { get; init; }
}
public sealed record UserMixDefinition(string Id, string Name)
{
    /// <summary>"monitor" or "virtualMic"; absent in older settings, which contain only virtual microphones.</summary>
    public string Kind { get; init; } = "virtualMic";

    /// <summary>A kind a user can create; anything else is dropped on load.</summary>
    public bool HasUserKind => Kind is "monitor" or "virtualMic";
}
