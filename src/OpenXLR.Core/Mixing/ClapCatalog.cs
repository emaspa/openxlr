using System.Globalization;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The installed CLAP plugins. A bundle is a shared library that has to be
/// loaded to be described, so the native host does that in a process of its
/// own, one per bundle: a plugin that crashes while being asked about its
/// ports costs that bundle and nothing else, and the daemon never loads
/// plugin code.
/// </summary>
public static class ClapCatalog
{
    private static volatile bool _retryFailures;
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow(retryFailures: _retryFailures));

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    /// <param name="retryFailures">Ask again about bundles the last scan could not describe.</param>
    public static void Reset(bool retryFailures = false)
    {
        _retryFailures = retryFailures;
        Scan.Reset();
    }

    /// <summary>Where bundles live, in the order the CLAP specification gives.</summary>
    public static IReadOnlyList<string> SearchPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAP_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return WithManagedPath(configured.Split(':', StringSplitOptions.RemoveEmptyEntries));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WithManagedPath([Path.Combine(home, ".clap"), "/usr/lib/clap", "/usr/local/lib/clap"]);
    }

    private static IReadOnlyList<string> WithManagedPath(IEnumerable<string> paths)
        => PluginSearchPaths.Include("clap", ManagedYabridge.Discover() is null ? paths
            : [Path.Combine(ManagedYabridge.PluginHome, "clap"), .. paths]);

    internal static IReadOnlyList<PluginInfo> ScanNow(IEnumerable<string>? directories = null, bool retryFailures = false)
        => HostScan.Run("clap", "scan-clap", directories ?? SearchPath(),
            Bundles,
            logs: new PluginScanLogStore(PluginScanLogStore.DefaultDirectory), retryFailures: retryFailures);

    internal static IEnumerable<string> Bundles(string directory)
        => HostScan.FindBundles(directory, ".clap", directoryBundles: false);

    /// <summary>The same walk, told about each directory it could not read.</summary>
    internal static IEnumerable<string> Bundles(string directory, Action<string, Exception>? unreadable)
        => HostScan.FindBundles(directory, ".clap", directoryBundles: false, unreadable: unreadable);

    internal static IReadOnlyList<PluginInfo> Parse(string json) => HostScan.Parse(json, "clap");
}

/// <summary>
/// The installed VST3 plugins, read the same way: a bundle is a directory
/// (or, for older plugins, a file) named .vst3, and the native host
/// describes it in a process of its own. A plugin is named by its class id,
/// since one module carries many.
/// </summary>
public static class Vst3Catalog
{
    private static volatile bool _retryFailures;
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow(retryFailures: _retryFailures));

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    /// <param name="retryFailures">Ask again about bundles the last scan could not describe.</param>
    public static void Reset(bool retryFailures = false)
    {
        _retryFailures = retryFailures;
        Scan.Reset();
    }

    public static IReadOnlyList<string> SearchPath()
    {
        string? configured = Environment.GetEnvironmentVariable("VST3_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return WithManagedPath(configured.Split(':', StringSplitOptions.RemoveEmptyEntries));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WithManagedPath([Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"]);
    }

    private static IReadOnlyList<string> WithManagedPath(IEnumerable<string> paths)
        => PluginSearchPaths.Include("vst3", ManagedYabridge.Discover() is null ? paths
            : [Path.Combine(ManagedYabridge.PluginHome, "vst3"), .. paths]);

    /// <summary>Bundles at any depth, since yabridge keeps its own directory under ~/.vst3, never descending into one.</summary>
    internal static IEnumerable<string> Bundles(string directory)
        => HostScan.FindBundles(directory, ".vst3", directoryBundles: true, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same walk, told about each directory it could not read.</summary>
    internal static IEnumerable<string> Bundles(string directory, Action<string, Exception>? unreadable)
        => HostScan.FindBundles(directory, ".vst3", directoryBundles: true, StringComparison.OrdinalIgnoreCase, unreadable);

    internal static IReadOnlyList<PluginInfo> ScanNow(IEnumerable<string>? directories = null, bool retryFailures = false)
        => HostScan.Run("vst3", "scan-vst3", directories ?? SearchPath(), Bundles,
            logs: new PluginScanLogStore(PluginScanLogStore.DefaultDirectory), retryFailures: retryFailures);

    internal static IReadOnlyList<PluginInfo> Parse(string json) => HostScan.Parse(json, "vst3");
}

/// <summary>What the two scanners share: running the helper per bundle, and reading its JSON.</summary>
internal static class HostScan
{
    /// <summary>
    /// Follow linked plugin folders once, including linked search roots. Keep
    /// the discovered paths for cache identity, but compare resolved directory
    /// paths so aliases and parent links cannot multiply scans. A failed child
    /// directory does not discard bundles already found in its siblings.
    /// </summary>
    /// <param name="unreadable">
    /// Told of each directory the walk had to pass over. Skipping it is what
    /// keeps one unreadable folder from costing the whole search root, but a
    /// skip that nobody hears of leaves a plugin missing from the catalogue
    /// with a diagnostics archive that says the folder was fine.
    /// </param>
    internal static IEnumerable<string> FindBundles(string directory, string extension,
        bool directoryBundles, StringComparison comparison = StringComparison.Ordinal,
        Action<string, Exception>? unreadable = null)
    {
        var found = new List<string>();
        var pending = new Stack<string>([directory]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                if (!visited.Add(WindowsPluginWrappers.Canonical(current))) continue;
                // Enumeration itself is lazy and can fail during MoveNext.
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    bool isDirectory = Directory.Exists(entry);
                    if (entry.EndsWith(extension, comparison) && (directoryBundles || !isDirectory)) found.Add(entry);
                    else if (isDirectory) pending.Push(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable?.Invoke(current, ex); }
        }
        return found;
    }

    /// <summary>
    /// Scan one format. <paramref name="logs"/> is where the whole output of
    /// a failed attempt is kept; the two catalogues pass the daemon's store,
    /// and a caller that supplies its own <paramref name="describe"/> passes
    /// its own or none, so a test never writes into the user's cache.
    /// </summary>
    /// <param name="retryFailures">
    /// Ask again about bundles a previous scan could not describe. A scan the
    /// user asked for does; the one that builds the catalogue on its own
    /// passes them by, so a bundle that hangs the scanner costs its deadline
    /// once rather than at every start.
    /// </param>
    internal static IReadOnlyList<PluginInfo> Run(string kind, string command, IEnumerable<string> directories,
        Func<string, Action<string, Exception>?, IEnumerable<string>> bundlesIn,
        Func<string, ProcessResult>? describe = null, ScanCache? scanCache = null,
        PluginScanLogStore? logs = null, bool retryFailures = false)
    {
        // Nothing here may throw: the catalogue is read once and kept, so an
        // exception would be kept with it, and every lookup after would fail.
        var result = new List<PluginInfo>();
        var evidence = new PluginScanDiagnostics.Capture(kind);
        try
        {
            if (describe is null && !NativePluginHost.HostInstalled)
            {
                evidence.Add(NativePluginHost.Executable, "host-missing");
                return result;
            }
            ManagedYabridge? bridge = ManagedYabridge.Discover();
            var environment = new PluginHostEnvironment(bridge, scanner: true);
            // What a saved log may say about the tools involved, taken from
            // what discovery has already read. Nothing here probes, launches
            // or asks the network for a version.
            string? bridgeStamp = bridge is null ? null
                : $"yabridge {bridge.Version} (source {bridge.SourceCommit})";
            string? scannerStamp = describe is null ? NativePluginHost.Executable : null;
            var cache = scanCache ?? new ScanCache(bridge is null ? ScanCache.DefaultDirectory
                : Path.Combine(ScanCache.DefaultDirectory, "bridge-" + bridge.CacheKey));
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    evidence.Add(directory, "directory-missing");
                    continue;
                }
                IEnumerable<string> bundles;
                // A folder the walk could not read is reported under its own
                // path and the walk goes on; a root that fails outright is the
                // catch below.
                try
                {
                    bundles = bundlesIn(directory, (path, ex) => evidence.Add(path, "directory-error", detail: ex.Message))
                        .OrderBy(f => f, StringComparer.Ordinal).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    evidence.Add(directory, "directory-error", detail: ex.Message);
                    continue;
                }
                evidence.Add(directory, "directory", detail: $"{bundles.Count()} candidate bundles");
                foreach (string bundle in bundles)
                {
                    try
                    {
                        if (!SourceExists(bundle))
                        {
                            evidence.Add(bundle, "source-missing",
                                detail: "The plugin path or its symbolic-link target is missing. Restore the original plugin or install it again, then rescan.");
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        evidence.Add(bundle, "scan-error", detail: ex.Message);
                        continue;
                    }
                    byte[]? description = cache.Lookup(bundle);
                    bool cached = description is not null;
                    // A bundle that already failed is left alone: it is the
                    // one that costs the whole deadline, and a catalogue the
                    // daemon builds by itself must not spend a minute on each
                    // of them at every start. The failure stays in this
                    // report, so the archive still shows what was skipped.
                    if (description is null && !retryFailures && cache.Failure(bundle) is { } failure)
                    {
                        evidence.SkipFailure(bundle, failure.FailureReason, failure.FailedAt);
                        continue;
                    }
                    string? stderr = null;
                    int? exitCode = null;
                    bool timedOut = false, outputCapped = false;
                    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                    long begun = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (description is null)
                    {
                        // A module that carries hundreds of plugins is created and
                        // released plugin by plugin; a minute is generous for that,
                        // and it is spent once per bundle until the bundle changes.
                        ProcessResult scan;
                        try { scan = describe is not null ? describe(bundle)
                            : ProcessRunner.Run(NativePluginHost.Executable, [command, bundle], TimeSpan.FromSeconds(60),
                                stderrCap: environment.StderrCap,
                                environment: environment.Overlay, removeEnvironment: environment.RemovedVariables); }
                        catch (Exception ex)
                        {
                            // A missing helper or temporary resource shortage says
                            // nothing about the bundle. Forget an older failure too,
                            // so a repaired launch can return it on the next run.
                            cache.Invalidate(bundle);
                            evidence.Add(bundle, "start-error", detail: ex.Message);
                            continue;
                        }
                        stderr = scan.Stderr;
                        // The runner reports -1 when the exit status could not be
                        // read at all, which is not an exit status to report.
                        exitCode = scan.ExitCode == -1 ? null : scan.ExitCode;
                        timedOut = scan.TimedOut;
                        outputCapped = scan.Truncated;
                        if (!scan.Ok)
                        {
                            bool missingWindows = kind == "vst3" && !scan.TimedOut && !scan.Truncated && !scan.Incomplete
                                && stderr.Contains("does not contain a Windows VST3 module", StringComparison.Ordinal);
                            string outcome = scan.TimedOut ? "timeout" : scan.Truncated ? "output-limit"
                                : scan.Incomplete ? "output-incomplete"
                                : missingWindows ? "windows-module-missing" : "scan-failed";
                            string detail = missingWindows ? stderr + "\n" + MissingWindowsModuleDetail(bundle) : stderr;
                            PluginScanLogRef log = Keep(logs, new PluginScanAttempt(kind, bundle, outcome, startedAt,
                                System.Diagnostics.Stopwatch.GetElapsedTime(begun), exitCode, timedOut, outputCapped,
                                Scanner: scannerStamp, Bridge: bridgeStamp) { WineTrace = environment.WineTrace, WineDebug = environment.WineDebug }, scan.Stdout, stderr, scan.Truncated);
                            evidence.Add(bundle, outcome, exitCode: scan.ExitCode, detail: detail,
                                logId: log.Id, logNote: log.Note, wineTrace: environment.WineTrace);
                            cache.StoreFailure(bundle, outcome, startedAt);
                            continue;
                        }
                        description = scan.Stdout;
                    }
                    try
                    {
                        IReadOnlyList<PluginInfo> parsed = Parse(description, kind);
                        if (!cached) cache.Store(bundle, description);
                        int before = result.Count;
                        result.AddRange(parsed.Where(plugin => known.Add(plugin.Plugin)));
                        evidence.Add(bundle, parsed.Count == 0 ? "no-plugins" : "ok", cached,
                            parsed.Count, parsed.Count - (result.Count - before), detail: stderr, wineTrace: environment.WineTrace);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        // The description itself is the evidence here, whether it
                        // came from a launch or from the cache a launch filled.
                        PluginScanLogRef log = Keep(logs, new PluginScanAttempt(kind, bundle, "invalid-description",
                            startedAt, System.Diagnostics.Stopwatch.GetElapsedTime(begun), exitCode, timedOut,
                            outputCapped, cached, scannerStamp, bridgeStamp, ex.Message) { WineTrace = environment.WineTrace, WineDebug = environment.WineDebug },
                            description, stderr ?? "", outputCapped);
                        evidence.Add(bundle, "invalid-description", cached, detail: ex.Message,
                            logId: log.Id, logNote: log.Note, wineTrace: environment.WineTrace);
                        // Fresh malformed output belongs to this bundle. Damaged
                        // cached bytes do not: discard them so the next scan can
                        // ask the scanner before deciding the bundle is broken.
                        if (cached) cache.Invalidate(bundle);
                        else cache.StoreFailure(bundle, "invalid-description", startedAt);
                    }
                }
            }
            cache.Save();
        }
        catch (Exception ex) { evidence.Add("", "scan-error", detail: ex.Message); }
        finally { evidence.Complete(); }
        return result;
    }

    /// <summary>
    /// Save what the failed attempt printed, when there is somewhere to save
    /// it. A store that cannot write hands back the reason, which the entry
    /// carries; it never becomes an exception, because losing the log must
    /// not lose the scan or the error it was recording.
    /// </summary>
    private static PluginScanLogRef Keep(PluginScanLogStore? logs, PluginScanAttempt attempt,
        ReadOnlySpan<byte> stdout, string stderr, bool capped)
        => logs is null ? default : logs.Save(attempt, stdout, capped, stderr, capped);

    private static bool SourceExists(string path)
    {
        if (Directory.Exists(path)) return true;
        var file = new FileInfo(path);
        // On Unix, File.Exists also reports dangling symbolic links as files.
        return file.LinkTarget is null ? file.Exists
            : file.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
    }

    internal static string MissingWindowsModuleDetail(string bundle)
    {
        const string advice = "Restore the original Windows plugin or install it again, then sync and rescan. A generated yabridge wrapper does not contain the original plugin.";
        try
        {
            string name = Path.GetFileNameWithoutExtension(bundle.TrimEnd(Path.DirectorySeparatorChar));
            foreach (string architecture in new[] { "x86_64-win", "i386-win" })
            {
                string module = Path.Combine(bundle, "Contents", architecture, name + ".vst3");
                string? target = new FileInfo(module).LinkTarget;
                if (target is not null && !SourceExists(module))
                    return $"Missing Windows source: {Path.GetFullPath(target, Path.GetDirectoryName(module)!)}. {advice}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return advice;
    }

    internal static IReadOnlyList<PluginInfo> Parse(string json, string kind)
        => Parse(System.Text.Encoding.UTF8.GetBytes(json), kind);

    /// <summary>
    /// The scanner's JSON for one bundle, as plugins the picker can offer.
    /// Read as a stream of tokens rather than into a tree: one module can
    /// describe two hundred plugins in several megabytes, and a tree of that
    /// runs to many times its size, which the daemon's bounded heap does not
    /// have. Nothing is held but the plugins themselves.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> Parse(ReadOnlySpan<byte> json, string kind)
    {
        var result = new List<PluginInfo>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read()) throw new JsonException("The scanner returned an empty description.");
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            while (reader.Read()) { }
            return result;
        }
        // The scanner writes "file" before "plugins", so a plugin's path is
        // known by the time one is read; if it is not, the bundle is unnamed.
        string file = "";
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("file"u8))
            {
                reader.Read();
                file = Text(ref reader) ?? "";
            }
            else if (reader.ValueTextEquals("plugins"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    if (ReadPlugin(ref reader, kind, file) is PluginInfo info)
                        result.Add(info);
                }
            }
            else { reader.Read(); reader.Skip(); }
        }
        // Read through the end before caching. A complete plugin followed by
        // truncated JSON or extra output is still a broken description.
        while (reader.Read()) { }
        return result;
    }

    /// <summary>One plugin, from the object the reader stands on to its end.</summary>
    private static PluginInfo? ReadPlugin(ref Utf8JsonReader reader, string kind, string file)
    {
        string? id = null, name = null, layoutRefused = null;
        var features = new List<string>();
        var parameters = new List<PluginParam>();
        var parameterIds = new HashSet<string>(StringComparer.Ordinal);
        List<int>? widths = null;
        int ins = 0, outs = 0;
        bool gui = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = Text(ref reader); }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = Text(ref reader); }
            else if (reader.ValueTextEquals("audioIns"u8)) { reader.Read(); ins = Whole(ref reader); }
            else if (reader.ValueTextEquals("audioOuts"u8)) { reader.Read(); outs = Whole(ref reader); }
            else if (reader.ValueTextEquals("gui"u8)) { reader.Read(); gui = Flag(ref reader); }
            // The widths the helper asked the plugin about. Present and
            // empty is an answer too: the plugin refused every one.
            else if (reader.ValueTextEquals("widths"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                widths = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) { reader.Skip(); continue; }
                    int width = Whole(ref reader);
                    if (width > 0 && widths.Count < 8 && !widths.Contains(width)) widths.Add(width);
                }
            }
            // The helper says so when the plugin's port layout is one it
            // would refuse to load, in the words it would print. Carrying
            // that here keeps the picker from offering a plugin that cannot
            // be inserted, on the same footing as a missing host feature.
            else if (reader.ValueTextEquals("layoutRefused"u8)) { reader.Read(); layoutRefused = Text(ref reader); }
            else if (reader.ValueTextEquals("features"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (features.Count < Lv2Catalog.MaxFeatures && reader.TokenType == JsonTokenType.String)
                        features.Add(reader.GetString() ?? "");
                    else reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("params"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject || parameters.Count >= Lv2Catalog.MaxControls) { reader.Skip(); continue; }
                    if (ReadParam(ref reader) is PluginParam param && parameterIds.Add(param.Symbol)) parameters.Add(param);
                }
            }
            else { reader.Read(); reader.Skip(); }
        }
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new PluginInfo(kind, id, string.IsNullOrWhiteSpace(name) ? id : name, Category(features),
            ins, outs, "", "", parameters, [], [], [])
        {
            HasNativeUi = gui,
            Path = file,
            Widths = widths,
            UnsupportedFeatures = string.IsNullOrWhiteSpace(layoutRefused) ? [] : [layoutRefused],
        };
    }

    /// <summary>One parameter, addressed by its id, which is how the helper takes it.</summary>
    private static PluginParam? ReadParam(ref Utf8JsonReader reader)
    {
        uint? id = null;
        string name = "";
        double min = 0, max = 1;
        double? initial = null;
        bool stepped = false, enumeration = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                // Both hosts address controls by uint32, reserving UINT32_MAX
                // for an invalid id. Never round or wrap scanner metadata.
                id = reader.TokenType == JsonTokenType.Number && reader.TryGetUInt32(out uint value)
                    && value != uint.MaxValue ? value : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = Text(ref reader) ?? ""; }
            else if (reader.ValueTextEquals("min"u8)) { reader.Read(); min = Number(ref reader); }
            else if (reader.ValueTextEquals("max"u8)) { reader.Read(); max = Number(ref reader, 1); }
            else if (reader.ValueTextEquals("default"u8)) { reader.Read(); initial = Number(ref reader); }
            else if (reader.ValueTextEquals("stepped"u8)) { reader.Read(); stepped = Flag(ref reader); }
            else if (reader.ValueTextEquals("enum"u8)) { reader.Read(); enumeration = Flag(ref reader); }
            else { reader.Read(); reader.Skip(); }
        }
        // A valid JSON number such as 1e999 can still overflow a double.
        // Omit only that control so one bad range cannot prevent every
        // plugin from being serialized to the window.
        if (id is null || !double.IsFinite(min) || !double.IsFinite(max) || min > max
            || (initial.HasValue && !double.IsFinite(initial.Value)))
            return null;
        return new PluginParam(
            id.Value.ToString(CultureInfo.InvariantCulture), name, min, max, Math.Clamp(initial ?? min, min, max),
            Toggled: stepped && min == 0 && max == 1,
            Integer: stepped,
            Logarithmic: false,
            Enumeration: enumeration,
            []);
    }

    // Consume the entire value even when a scalar field contains an object
    // or array, so nested properties cannot become plugin or control fields.
    private static string? Text(ref Utf8JsonReader reader)
    {
        string? value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        reader.Skip();
        return value;
    }

    private static double Number(ref Utf8JsonReader reader, double fallback = 0)
    {
        double result = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) ? value : fallback;
        reader.Skip();
        return result;
    }

    private static int Whole(ref Utf8JsonReader reader)
    {
        int result = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) ? value : 0;
        reader.Skip();
        return result;
    }

    private static bool Flag(ref Utf8JsonReader reader)
    {
        bool value = reader.TokenType == JsonTokenType.True;
        reader.Skip();
        return value;
    }

    /// <summary>
    /// The picker's grouping, from the plugin's own feature list: CLAP
    /// features such as "audio-effect", "reverb", "stereo", or VST3
    /// sub-categories such as "Fx", "Reverb", "Stereo". The generic words
    /// are skipped so the specific one is what shows.
    /// </summary>
    internal static string Category(IReadOnlyList<string> features)
    {
        foreach (string feature in features)
        {
            string lower = feature.ToLowerInvariant();
            if (lower is "audio-effect" or "fx" or "instrument" or "note-effect" or "note-detector" or "analyzer"
                or "mono" or "stereo" or "surround" or "ambisonic" or "only-rt" or "only-offline-process") continue;
            return string.Join(' ', feature.Split('-').Select(word =>
                word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
        }
        return features.Any(f => f.Equals("audio-effect", StringComparison.OrdinalIgnoreCase)
            || f.Equals("fx", StringComparison.OrdinalIgnoreCase)) ? "Effect" : "";
    }
}
