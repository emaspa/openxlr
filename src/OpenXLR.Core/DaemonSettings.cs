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
///
/// WindowsHostDefault is the host a Windows plugin runs on while its insert
/// follows the default: "yabridge" or "openxlr", written by the daemon for
/// setWindowsHostDefault. Absent, or a value this build does not know, means
/// the build's own default, so a user who chose one keeps it when that
/// changes.
/// </summary>
public sealed record DaemonSettings
{
    public bool? Submixer { get; init; }

    public List<PluginFolder>? PluginFolders { get; init; }

    /// <summary>
    /// Present once the daemon has copied the yabridge companion's folders
    /// into <see cref="PluginFolders"/>, which it does by itself once, at the
    /// first start of a build with both the Windows plugin host and the
    /// companion. Absent until then.
    /// </summary>
    public BridgeFolderCopy? YabridgeFolderCopy { get; init; }

    public string? WindowsHostDefault { get; init; }

    /// <summary>Keys this version does not know, written back unchanged by a save.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
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
            return JsonSerializer.Deserialize<DaemonSettings>(RegularFile.ReadText(FilePath, OpenXlrPaths.MaxPreferenceBytes), Json)
                ?? throw new JsonException("the file holds null");
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    public void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, this, Json, OpenXlrPaths.MaxPreferenceBytes);

    /// <summary>
    /// Save the default host for Windows plugins, keeping every other key.
    /// Null when saved, otherwise why not. A file that cannot be read is not
    /// written over.
    /// </summary>
    public static string? SaveWindowsHostDefault(string host)
    {
        if (Read(out string? problem) is not { } settings)
            return "daemon.json cannot be read, so it was not written over. Repair or remove it, then try again: " + problem;
        try
        {
            (settings with { WindowsHostDefault = host }).Save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not save the default host for Windows plugins: " + ex.Message;
        }
    }

    /// <summary>
    /// The effective submixer switch: the saved choice when there is one,
    /// otherwise the launch-time default the caller derived from its
    /// command line and environment.
    /// </summary>
    public static bool SubmixerEnabled(bool launchDefault) => Load().Submixer ?? launchDefault;
}

/// <summary>An added plugin search folder: the format (lv2, clap or vst3, or windows for both Windows formats) and an absolute directory.</summary>
public sealed record PluginFolder(string Kind, string Path);

/// <summary>What the one-time copy of the yabridge companion's folders did: the folders it added under <c>windows</c>, and those it could not add, each with the reason.</summary>
public sealed record BridgeFolderCopy(List<string>? Added, List<FolderNotCopied>? NotAdded);

/// <summary>A yabridge folder the copy did not add, or the registry itself when it could not be read, and why.</summary>
public sealed record FolderNotCopied(string Path, string Reason);
