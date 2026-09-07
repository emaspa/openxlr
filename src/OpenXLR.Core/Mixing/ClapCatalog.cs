using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private static readonly Lazy<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    /// <summary>Where bundles live, in the order the CLAP specification gives.</summary>
    public static IReadOnlyList<string> SearchPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAP_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return [.. configured.Split(':', StringSplitOptions.RemoveEmptyEntries)];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(home, ".clap"), "/usr/lib/clap", "/usr/local/lib/clap"];
    }

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
    private static readonly Lazy<IReadOnlyList<PluginInfo>> Scan = new(() => ScanNow(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    public static IReadOnlyList<string> SearchPath()
    {
        string? configured = Environment.GetEnvironmentVariable("VST3_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return [.. configured.Split(':', StringSplitOptions.RemoveEmptyEntries)];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"];
    }

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
        Func<string, IEnumerable<string>> bundlesIn)
    {
        // Nothing here may throw: the catalogue is read once and kept, so an
        // exception would be kept with it, and every lookup after would fail.
        var result = new List<PluginInfo>();
        try
        {
            if (!NativePluginHost.HostInstalled) return result;
            var cache = new ScanCache(ScanCache.DefaultDirectory);
            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory)) continue;
                IEnumerable<string> bundles;
                try { bundles = bundlesIn(directory).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (string bundle in bundles)
                {
                    byte[]? description = cache.Lookup(bundle);
                    if (description is null)
                    {
                        // A module that carries hundreds of plugins is created and
                        // released plugin by plugin; a minute is generous for that,
                        // and it is spent once per bundle until the bundle changes.
                        ProcessResult scan;
                        try { scan = ProcessRunner.Run(NativePluginHost.Executable, [command, bundle], TimeSpan.FromSeconds(60)); }
                        catch (Exception) { continue; }
                        if (scan.ExitCode != 0 || scan.TimedOut || scan.Truncated) continue;
                        description = scan.Stdout;
                        cache.Store(bundle, description);
                    }
                    try { result.AddRange(Parse(description, kind)); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* one bad bundle */ }
                }
            }
            cache.Save();
        }
        catch (Exception) { /* whatever was read stands */ }
        return result;
    }

    internal static IReadOnlyList<PluginInfo> Parse(string json, string kind)
        => Parse(System.Text.Encoding.UTF8.GetBytes(json), kind);

    /// <summary>
    /// The scanner's JSON for one bundle, as plugins the picker can offer.
    /// Read from bytes: a large module's description would double in size as
    /// a string, and the daemon's heap is bounded.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> Parse(ReadOnlySpan<byte> json, string kind)
    {
        var result = new List<PluginInfo>();
        if (JsonNode.Parse(json) is not JsonObject root) return result;
        string file = root["file"]?.GetValue<string>() ?? "";
        foreach (JsonNode? node in root["plugins"] as JsonArray ?? [])
        {
            if (node is not JsonObject p) continue;
            string? id = p["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var features = (p["features"] as JsonArray)?.Select(f => f?.GetValue<string>() ?? "").ToList() ?? [];
            var parameters = new List<PluginParam>();
            foreach (JsonNode? q in p["params"] as JsonArray ?? [])
            {
                if (q is not JsonObject param) continue;
                double min = param["min"]?.GetValue<double>() ?? 0, max = param["max"]?.GetValue<double>() ?? 1;
                double def = param["default"]?.GetValue<double>() ?? min;
                bool stepped = param["stepped"]?.GetValue<bool>() == true;
                bool enumeration = param["enum"]?.GetValue<bool>() == true;
                parameters.Add(new PluginParam(
                    (param["id"]?.GetValue<long>() ?? 0).ToString(CultureInfo.InvariantCulture),
                    param["name"]?.GetValue<string>() ?? "",
                    min, max, def,
                    Toggled: stepped && min == 0 && max == 1,
                    Integer: stepped,
                    Logarithmic: false,
                    Enumeration: enumeration,
                    []));
                if (parameters.Count == Lv2Catalog.MaxControls) break;
            }
            int ins = p["audioIns"]?.GetValue<int>() ?? 0, outs = p["audioOuts"]?.GetValue<int>() ?? 0;
            result.Add(new PluginInfo(kind, id, p["name"]?.GetValue<string>() ?? id, Category(features),
                ins, outs, "", "", parameters, [], [], [])
            {
                HasNativeUi = p["gui"]?.GetValue<bool>() == true,
                Path = file,
            });
        }
        return result;
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
