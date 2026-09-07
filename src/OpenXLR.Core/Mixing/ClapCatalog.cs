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
    {
        var result = new List<PluginInfo>();
        if (!NativePluginHost.HostInstalled) return result;
        foreach (string directory in directories ?? SearchPath())
        {
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> bundles;
            try { bundles = Directory.EnumerateFiles(directory, "*.clap", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string bundle in bundles)
            {
                ProcessResult scan;
                try { scan = ProcessRunner.Run(NativePluginHost.Executable, ["scan-clap", bundle], TimeSpan.FromSeconds(10)); }
                catch (Exception) { continue; }
                if (scan.ExitCode != 0 || scan.TimedOut || scan.Truncated) continue;
                try { result.AddRange(Parse(System.Text.Encoding.UTF8.GetString(scan.Stdout))); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* one bad bundle */ }
            }
        }
        return result;
    }

    /// <summary>The scanner's JSON for one bundle, as plugins the picker can offer.</summary>
    internal static IReadOnlyList<PluginInfo> Parse(string json)
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
            result.Add(new PluginInfo("clap", id, p["name"]?.GetValue<string>() ?? id, Category(features),
                ins, outs, "", "", parameters, [], [], [])
            {
                HasNativeUi = p["gui"]?.GetValue<bool>() == true,
                Path = file,
            });
        }
        return result;
    }

    /// <summary>The picker's grouping, from the plugin's own feature list.</summary>
    internal static string Category(IReadOnlyList<string> features)
    {
        foreach (string feature in features)
        {
            if (feature is "audio-effect" or "instrument" or "note-effect" or "note-detector" or "analyzer"
                or "mono" or "stereo" or "surround" or "ambisonic") continue;
            return string.Join(' ', feature.Split('-').Select(word =>
                word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
        }
        return features.Contains("audio-effect") ? "Effect" : "";
    }
}
