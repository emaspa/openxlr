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

    /// <summary>"channel|mix" entries that are muted.</summary>
    public List<string> ChannelMuted { get; init; } = [];

    /// <summary>Single monitor output from older files; superseded by MonitorOutputs.</summary>
    public string? MonitorOutput { get; init; }

    /// <summary>All selected monitor outputs (the monitor mixes can feed several).</summary>
    public List<string> MonitorOutputs { get; init; } = [];

    /// <summary>Output name to the monitor mix feeding it; absent = the first monitor mix.</summary>
    public Dictionary<string, string> MonitorFeeds { get; init; } = [];

    /// <summary>Application identity to channel id, from manual assignments.</summary>
    public Dictionary<string, string> AppOverrides { get; init; } = [];

    /// <summary>Every app ever seen, so the list survives silence and restarts.</summary>
    public List<SavedApp> KnownApps { get; init; } = [];

    /// <summary>Software low cut on the first XLR channel (0, 80, or 120 Hz).</summary>
    public int LowCutHz { get; init; }

    /// <summary>Software ClipGuard (post-ADC hard limiter) on the first XLR channel.</summary>
    public bool SoftClipGuard { get; init; }

    /// <summary>Plugin insert chains by channel id.</summary>
    public Dictionary<string, List<InsertDefinition>> Inserts { get; init; } = [];

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

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>~/.config/openxlr/mixer.json, honouring XDG_CONFIG_HOME.</summary>
    public static string DefaultPath => OpenXlrPaths.ConfigFile("mixer.json");

    /// <summary>Read settings, or null when there is no file or it is unreadable.</summary>
    public static MixerSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MixerSettings>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;   // a corrupt file must not stop the daemon starting
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
