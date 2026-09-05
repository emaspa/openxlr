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
/// </summary>
public sealed record DaemonSettings
{
    public bool? Submixer { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ConfigDir => OpenXlrPaths.ConfigDir;

    private static string FilePath => Path.Combine(ConfigDir, "daemon.json");

    public static DaemonSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DaemonSettings>(File.ReadAllText(FilePath), Json) ?? new DaemonSettings();
        }
        catch (Exception) { /* a corrupt file must not stop the daemon */ }
        return new DaemonSettings();
    }

    public void Save() => OpenXlrPaths.WriteAtomicJson(FilePath, this, Json);

    /// <summary>
    /// The effective submixer switch: the saved choice when there is one,
    /// otherwise the launch-time default the caller derived from its
    /// command line and environment.
    /// </summary>
    public static bool SubmixerEnabled(bool launchDefault) => Load().Submixer ?? launchDefault;
}
