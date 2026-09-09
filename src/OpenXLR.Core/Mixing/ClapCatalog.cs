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
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow());

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    public static void Reset() => Scan.Reset();

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
        => ManagedYabridge.Discover() is null ? [.. paths]
            : [Path.Combine(ManagedYabridge.PluginHome, "clap"), .. paths];

    internal static IReadOnlyList<PluginInfo> ScanNow(IEnumerable<string>? directories = null)
        => HostScan.Run("clap", "scan-clap", directories ?? SearchPath(),
            directory => Directory.EnumerateFiles(directory, "*.clap", SearchOption.AllDirectories));

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
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow());

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    public static void Reset() => Scan.Reset();

    public static IReadOnlyList<string> SearchPath()
    {
        string? configured = Environment.GetEnvironmentVariable("VST3_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return WithManagedPath(configured.Split(':', StringSplitOptions.RemoveEmptyEntries));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WithManagedPath([Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"]);
    }

    private static IReadOnlyList<string> WithManagedPath(IEnumerable<string> paths)
        => ManagedYabridge.Discover() is null ? [.. paths]
            : [Path.Combine(ManagedYabridge.PluginHome, "vst3"), .. paths];

    /// <summary>Bundles at any depth, since yabridge keeps its own directory under ~/.vst3, never descending into one.</summary>
    internal static IEnumerable<string> Bundles(string directory)
    {
        var found = new List<string>();
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string entry in entries)
            {
                if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) found.Add(entry);
                else if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
        return found;
    }

    internal static IReadOnlyList<PluginInfo> ScanNow(IEnumerable<string>? directories = null)
        => HostScan.Run("vst3", "scan-vst3", directories ?? SearchPath(), Bundles);

    internal static IReadOnlyList<PluginInfo> Parse(string json) => HostScan.Parse(json, "vst3");
}

/// <summary>What the two scanners share: running the helper per bundle, and reading its JSON.</summary>
internal static class HostScan
{
    internal static IReadOnlyList<PluginInfo> Run(string kind, string command, IEnumerable<string> directories,
        Func<string, IEnumerable<string>> bundlesIn,
        Func<string, ProcessResult>? describe = null, ScanCache? scanCache = null)
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
                try { bundles = bundlesIn(directory).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    evidence.Add(directory, "directory-error", detail: ex.Message);
                    continue;
                }
                evidence.Add(directory, "directory", detail: $"{bundles.Count()} candidate bundles");
                foreach (string bundle in bundles)
                {
                    byte[]? description = cache.Lookup(bundle);
                    bool cached = description is not null;
                    string? stderr = null;
                    if (description is null)
                    {
                        // A module that carries hundreds of plugins is created and
                        // released plugin by plugin; a minute is generous for that,
                        // and it is spent once per bundle until the bundle changes.
                        ProcessResult scan;
                        try { scan = describe is not null ? describe(bundle)
                            : ProcessRunner.Run(NativePluginHost.Executable, [command, bundle], TimeSpan.FromSeconds(60),
                                environment: bridge?.HostEnvironment()); }
                        catch (Exception ex)
                        {
                            evidence.Add(bundle, "start-error", detail: ex.Message);
                            continue;
                        }
                        stderr = scan.Stderr;
                        if (scan.ExitCode != 0 || scan.TimedOut || scan.Truncated)
                        {
                            evidence.Add(bundle, scan.TimedOut ? "timeout" : scan.Truncated ? "output-limit" : "scan-failed",
                                exitCode: scan.ExitCode, detail: stderr);
                            continue;
                        }
                        description = scan.Stdout;
                        cache.Store(bundle, description);
                    }
                    try
                    {
                        IReadOnlyList<PluginInfo> parsed = Parse(description, kind);
                        int before = result.Count;
                        result.AddRange(parsed.Where(plugin => known.Add(plugin.Plugin)));
                        evidence.Add(bundle, parsed.Count == 0 ? "no-plugins" : "ok", cached,
                            parsed.Count, parsed.Count - (result.Count - before), detail: stderr);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        evidence.Add(bundle, "invalid-description", cached, detail: ex.Message);
                    }
                }
            }
            cache.Save();
        }
        catch (Exception ex) { evidence.Add("", "scan-error", detail: ex.Message); }
        finally { evidence.Complete(); }
        return result;
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
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return result;
        // The scanner writes "file" before "plugins", so a plugin's path is
        // known by the time one is read; if it is not, the bundle is unnamed.
        string file = "";
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("file"u8))
            {
                reader.Read();
                file = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
            }
            else if (reader.ValueTextEquals("plugins"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    if (ReadPlugin(ref reader, kind, file) is PluginInfo info)
                        result.Add(info);
            }
            else { reader.Read(); reader.Skip(); }
        }
        return result;
    }

    /// <summary>One plugin, from the object the reader stands on to its end.</summary>
    private static PluginInfo? ReadPlugin(ref Utf8JsonReader reader, string kind, string file)
    {
        string? id = null, name = null;
        var features = new List<string>();
        var parameters = new List<PluginParam>();
        int ins = 0, outs = 0;
        bool gui = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = Text(ref reader); }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = Text(ref reader); }
            else if (reader.ValueTextEquals("audioIns"u8)) { reader.Read(); ins = Whole(ref reader); }
            else if (reader.ValueTextEquals("audioOuts"u8)) { reader.Read(); outs = Whole(ref reader); }
            else if (reader.ValueTextEquals("gui"u8)) { reader.Read(); gui = reader.TokenType == JsonTokenType.True; }
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
                    if (ReadParam(ref reader) is PluginParam param) parameters.Add(param);
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
        };
    }

    /// <summary>One parameter, addressed by its id, which is how the helper takes it.</summary>
    private static PluginParam? ReadParam(ref Utf8JsonReader reader)
    {
        long id = 0;
        string name = "";
        double min = 0, max = 1;
        double? initial = null;
        bool stepped = false, enumeration = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = (long)Number(ref reader); }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = Text(ref reader) ?? ""; }
            else if (reader.ValueTextEquals("min"u8)) { reader.Read(); min = Number(ref reader); }
            else if (reader.ValueTextEquals("max"u8)) { reader.Read(); max = Number(ref reader, 1); }
            else if (reader.ValueTextEquals("default"u8)) { reader.Read(); initial = Number(ref reader); }
            else if (reader.ValueTextEquals("stepped"u8)) { reader.Read(); stepped = reader.TokenType == JsonTokenType.True; }
            else if (reader.ValueTextEquals("enum"u8)) { reader.Read(); enumeration = reader.TokenType == JsonTokenType.True; }
            else { reader.Read(); reader.Skip(); }
        }
        return new PluginParam(
            id.ToString(CultureInfo.InvariantCulture), name, min, max, initial ?? min,
            Toggled: stepped && min == 0 && max == 1,
            Integer: stepped,
            Logarithmic: false,
            Enumeration: enumeration,
            []);
    }

    private static string? Text(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

    private static double Number(ref Utf8JsonReader reader, double fallback = 0)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) ? value : fallback;

    private static int Whole(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) ? value : 0;

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
