using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>The same loader policy for discovery and live hosts, with either bridge provider.</summary>
internal sealed class PluginHostEnvironment(ManagedYabridge? bridge, bool scanner = false)
{
    internal const string CleanVariable = "OPENXLR_PLUGIN_CLEAN_ENV";
    internal const string WineTraceVariable = "OPENXLR_PLUGIN_WINE_TRACE";
    internal const string WineTraceChannels = "+seh,+unwind,+loaddll";
    internal const int ValueLimit = 4096;
    private static readonly string[] LoaderVariables = ["LD_LIBRARY_PATH", "LD_PRELOAD", "LD_AUDIT"];

    // Wrappers, CUDA plugins and desktop integrations use these variables.
    // Removing them is a troubleshooting choice, never a normal launch rule.
    public bool CleanLaunch { get; } = Environment.GetEnvironmentVariable(CleanVariable) == "1";
    // Deep tracing can stall audio and flood a live host's journal. Only
    // scanners opt in; an explicit WINEDEBUG still belongs to the user.
    public bool WineTrace { get; } = scanner && Environment.GetEnvironmentVariable(WineTraceVariable) == "1";
    public string? WineDebug { get; } = Environment.GetEnvironmentVariable("WINEDEBUG")
        ?? (scanner && Environment.GetEnvironmentVariable(WineTraceVariable) == "1" ? WineTraceChannels : null);
    public IReadOnlyDictionary<string, string> Overlay { get; } = BuildOverlay(bridge, scanner);
    public IReadOnlyCollection<string> RemovedVariables => CleanLaunch ? LoaderVariables : [];
    public int StderrCap => WineTrace ? PluginScanLogStore.TraceCaptureBytes : PluginScanLogStore.StreamCapBytes;

    private static IReadOnlyDictionary<string, string> BuildOverlay(ManagedYabridge? bridge, bool scanner)
    {
        // A bridged plugin starts Wine; keep its USB driver out of that prefix.
        Dictionary<string, string> overlay = bridge?.HostEnvironment() ?? new(StringComparer.Ordinal);
        // The system bridge finds its host program and Wine on PATH; hand it
        // only the absolute entries, as the companion's PATH already is.
        if (!overlay.ContainsKey("PATH") && Environment.GetEnvironmentVariable("PATH") is { } inherited
            && SearchPathList.Absolute("PATH", inherited, expandHome: false, out int ignored) is var kept && ignored > 0)
            overlay["PATH"] = string.Join(Path.PathSeparator, kept);
        WineDllOverrides.Apply(overlay);
        if (scanner && Environment.GetEnvironmentVariable(WineTraceVariable) == "1"
            && Environment.GetEnvironmentVariable("WINEDEBUG") is null)
            overlay["WINEDEBUG"] = WineTraceChannels;
        return overlay;
    }

    public void Apply(ProcessStartInfo start) => ProcessRunner.ApplyEnvironment(start, Overlay, RemovedVariables);

    internal string WineRunner()
    {
        string runner = Environment.GetEnvironmentVariable("WINELOADER") ?? "wine";
        if (runner.Contains(Path.DirectorySeparatorChar)) return Path.GetFullPath(runner);
        string path = Overlay.GetValueOrDefault("PATH") ?? Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        foreach (string directory in SearchPathList.Absolute("PATH", path, expandHome: false, out _))
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, runner));
            if (File.Exists(candidate) && (!OperatingSystem.IsLinux()
                || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                return candidate;
        }
        return runner;
    }

    public object Diagnostics()
    {
        var inherited = LoaderVariables.ToDictionary(name => name,
            name => PluginScanDiagnostics.ClipEnds(Environment.GetEnvironmentVariable(name), ValueLimit));
        return new
        {
            cleanLaunch = CleanLaunch,
            loaderEnvironment = inherited.ToDictionary(p => p.Key, p => CleanLaunch ? null : p.Value),
            removedLoaderEnvironment = inherited.Where(p => CleanLaunch && p.Value is not null)
                .ToDictionary(p => p.Key, p => p.Value),
            wineLoader = PluginScanDiagnostics.ClipEnds(Environment.GetEnvironmentVariable("WINELOADER"), ValueLimit),
            wineRunner = PluginScanDiagnostics.ClipEnds(WineRunner(), ValueLimit),
            wineTrace = Environment.GetEnvironmentVariable(WineTraceVariable) == "1",
            scannerWineDebug = PluginScanDiagnostics.ClipEnds(new PluginHostEnvironment(bridge, scanner: true).WineDebug, ValueLimit),
        };
    }
}
