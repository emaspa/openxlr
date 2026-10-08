using System.Text.Json;

namespace OpenXLR.Core;

/// <summary>
/// Daemon preferences shared with the UI, stored in
/// ~/.config/openxlr/daemon.json. Read once at daemon start; the UI writes
/// it and restarts the daemon to apply.
///
/// Submixer: null means "not chosen", and the daemon falls back to its
/// command line / environment (the packaged unit sets
/// OPENXLR_BUILD_MIXER=1). With the submixer off, OpenXLR drives the
/// hardware only and leaves the card in its stock PipeWire layout (the UCM
/// split, where one exists).
///
/// PluginFolders are the plugin search folders added from the plugin
/// manager. The daemon writes them itself; the window's mirror keeps fields
/// it does not know, so neither side drops what the other wrote.
/// </summary>
public sealed record DaemonSettings
{
    public bool? Submixer { get; init; }

    public List<PluginFolder>? PluginFolders { get; init; }

    /// <summary>Keys this version does not know, written back unchanged by a save.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigDir => OpenXlrPaths.ConfigDir;

    private static string FilePath => Path.Combine(ConfigDir, "daemon.json");

    public static DaemonSettings Load() => Read(out _) ?? new DaemonSettings();

    /// <summary>
    /// The saved settings, unset ones when there is no file, or null with the
    /// reason when the file exists and cannot be read. A corrupt file must not
    /// stop the daemon, and it is not written over either.
    /// </summary>
    public static DaemonSettings? Read(out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(FilePath)) return new DaemonSettings();
            return JsonSerializer.Deserialize<DaemonSettings>(File.ReadAllText(FilePath), Json)
                ?? throw new JsonException("the file holds null");
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    public void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, this, Json);

    /// <summary>
    /// The effective submixer switch: the saved choice when there is one,
    /// otherwise the launch-time default the caller derived from its
    /// command line and environment.
    /// </summary>
    public static bool SubmixerEnabled(bool launchDefault) => Load().Submixer ?? launchDefault;
}

/// <summary>An added plugin search folder: the format (lv2, clap or vst3) and an absolute directory.</summary>
public sealed record PluginFolder(string Kind, string Path);
