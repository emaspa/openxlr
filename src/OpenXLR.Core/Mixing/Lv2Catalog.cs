using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The installed LV2 plugins. lilv (the same library PipeWire's filter-chain
/// uses to load them, so whatever this lists is loadable in principle) parses
/// every bundle's Turtle files, and with the native host installed it does
/// that in a helper process (<c>openxlr-lv2-host scan-lv2</c>), as CLAP and
/// VST3 bundles are described: a bundle whose data crashes or stalls the
/// parser costs that scan, with a deadline and a bound on what the helper
/// may print, and the daemon keeps the catalogue it had. A build without
/// the native host reads the bundles through lilv in this process, as every
/// build did before the helper could.
///
/// Only what an insert UI needs is read: names, audio port counts, the
/// control ports with their ranges and hints, and the required features
/// (so a plugin the host cannot satisfy can be flagged instead of failing
/// silently at load).
/// </summary>
public static class Lv2Catalog
{
    // The last catalogue a scan completed. A scan that fails hands this back
    // instead of an empty list, so one broken bundle installed since does
    // not take every LV2 insert out of the picker.
    private static IReadOnlyList<PluginInfo>? _lastGood;
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> Scan = new(() =>
        KeepLastGood(ref _lastGood, Collect(null, new PluginScanLogStore(PluginScanLogStore.DefaultDirectory))));

    /// <summary>
    /// How long the helper may take. lilv reads a full LSP, x42 and Calf
    /// installation in a few seconds; a scan still running after this is
    /// stuck in a bundle.
    /// </summary>
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What the helper may print. A few hundred plugins with every LSP
    /// plugin among them come to about 6 MiB; this is ten times that, and
    /// the same bound a single CLAP or VST3 module gets.
    /// </summary>
    internal const int OutputCapBytes = ProcessRunner.DefaultStdoutCap;

    /// <summary>
    /// Standard error: one phase marker per plugin, and whatever lilv says
    /// about the bundles it reads. Past this the scan counts as failed.
    /// </summary>
    internal const int ErrorCapBytes = 1024 * 1024;

    /// <summary>
    /// The host features PipeWire's filter-chain LV2 loader provides (read
    /// from libspa-filter-graph-plugin-lv2.so, PipeWire 1.6): a plugin that
    /// requires anything else, an instance-access UI, data-access, a
    /// resize-port host, cannot run in the chain.
    /// </summary>
    public static readonly IReadOnlySet<string> FilterChainFeatures = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://lv2plug.in/ns/ext/urid#map",
        "http://lv2plug.in/ns/ext/urid#unmap",
        "http://lv2plug.in/ns/ext/options#options",
        "http://lv2plug.in/ns/ext/log#log",
        "http://lv2plug.in/ns/ext/worker#schedule",
        "http://lv2plug.in/ns/ext/buf-size#boundedBlockLength",
        "http://lv2plug.in/ns/ext/buf-size#fixedBlockLength",
        "http://lv2plug.in/ns/ext/buf-size#powerOf2BlockLength",
    };

    /// <summary>Required features the chain host lacks, in catalog order.</summary>
    public static IReadOnlyList<string> UnsupportedFeatures(IEnumerable<string> required)
        => [.. required.Where(f => !FilterChainFeatures.Contains(f))];

    /// <summary>Every LV2 plugin lilv can see (blocks on the first call).</summary>
    public static IReadOnlyList<PluginInfo> Plugins => Scan.Value;

    /// <summary>Scan again; until the new list is complete, readers get the old one.</summary>
    public static void Refresh() => Scan.Refresh();

    /// <summary>Kick the scan off without waiting for it.</summary>
    public static void Warm() => ThreadPool.QueueUserWorkItem(_ => { try { _ = Scan.Value; } catch (Exception) { } });

    public static PluginInfo? Find(string uri) => Plugins.FirstOrDefault(p => p.Plugin == uri);

    /// <summary>
    /// A fresh scan outside the cached one, empty when it failed. With
    /// <paramref name="lv2Path"/> only those directories are read (tests
    /// point it at their own bundles); otherwise the LV2_PATH every LV2 host
    /// is given, from <see cref="PluginSearchPaths.Lv2Path"/>, or lilv's
    /// default when no folder was added.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> ScanNow(string? lv2Path = null) => Collect(lv2Path, logs: null) ?? [];

    /// <summary>
    /// One scan, through the helper when it is installed, or null when the
    /// helper failed. <paramref name="logs"/> keeps the output of a failed
    /// attempt; the daemon's catalogue passes its store, a test none.
    /// </summary>
    internal static IReadOnlyList<PluginInfo>? Collect(string? lv2Path, PluginScanLogStore? logs)
    {
        lv2Path ??= PluginSearchPaths.Lv2Path();
        if (NativePluginHost.HostInstalled)
            return FromHelper(NativePluginHost.Executable, lv2Path, logs);
        var evidence = new PluginScanDiagnostics.Capture("lv2");
        try
        {
            IReadOnlyList<PluginInfo> plugins = InProcess(lv2Path);
            evidence.Add(Searched(lv2Path), plugins.Count == 0 ? "no-plugins" : "ok", plugins: plugins.Count,
                detail: "read in the daemon: the native host is not installed");
            return plugins;
        }
        finally { evidence.Complete(); }
    }

    /// <summary>
    /// What the daemon keeps: a completed scan, or after a failed one the
    /// catalogue the last completed scan gave, empty when there was none.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> KeepLastGood(ref IReadOnlyList<PluginInfo>? lastGood, IReadOnlyList<PluginInfo>? scanned)
    {
        if (scanned is null) return Volatile.Read(ref lastGood) ?? [];
        Volatile.Write(ref lastGood, scanned);
        return scanned;
    }

    /// <summary>
    /// Run the helper and read its catalogue, or null when it crashed, ran
    /// out of time, printed more than it may or printed something that is
    /// not a catalogue. Each failure is recorded under <paramref name="kind"/>
    /// in the scan diagnostics, which the daemon logs, with the bundle the
    /// helper was reading when its phase markers name one.
    /// </summary>
    internal static IReadOnlyList<PluginInfo>? FromHelper(string executable, string? lv2Path,
        PluginScanLogStore? logs, string kind = "lv2", TimeSpan? timeout = null,
        int stdoutCap = OutputCapBytes, int stderrCap = ErrorCapBytes)
    {
        // Nothing here may throw: the catalogue is read once and kept.
        var evidence = new PluginScanDiagnostics.Capture(kind);
        string searched = Searched(lv2Path);
        try
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            long begun = System.Diagnostics.Stopwatch.GetTimestamp();
            ProcessResult scan;
            try
            {
                scan = ProcessRunner.Run(executable, lv2Path is null ? ["scan-lv2"] : ["scan-lv2", lv2Path],
                    timeout ?? ScanTimeout, stdoutCap: stdoutCap, stderrCap: stderrCap);
            }
            catch (Exception ex)
            {
                evidence.Add(searched, "start-error", detail: ex.Message);
                return null;
            }
            int? exitCode = scan.ExitCode == -1 ? null : scan.ExitCode;
            string? outcome = scan.Ok ? null : scan.TimedOut ? "timeout" : scan.Truncated ? "output-limit"
                : scan.Incomplete ? "output-incomplete" : "scan-failed";
            IReadOnlyList<PluginInfo>? plugins = null;
            string? invalid = null;
            if (outcome is null)
            {
                try { plugins = Parse(scan.Stdout); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    outcome = "invalid-description";
                    invalid = ex.Message;
                }
            }
            if (plugins is not null)
            {
                evidence.Add(searched, plugins.Count == 0 ? "no-plugins" : "ok", plugins: plugins.Count);
                return plugins;
            }
            // Output past the cap stops wherever the helper was; that bundle
            // did not cause it. A crash or a stall is in the bundle it was
            // reading, which its last marker names.
            string path = outcome is "scan-failed" or "timeout" or "output-incomplete"
                ? Culprit(scan.Stderr) ?? searched : searched;
            PluginScanLogRef log = logs is null ? default : logs.Save(
                new PluginScanAttempt(kind, path, outcome!, startedAt, System.Diagnostics.Stopwatch.GetElapsedTime(begun),
                    exitCode, scan.TimedOut, scan.Truncated, Scanner: executable, Detail: invalid),
                scan.Stdout, scan.Truncated, scan.Stderr, scan.Truncated);
            evidence.Add(path, outcome!, exitCode: exitCode, detail: invalid ?? scan.Stderr,
                logId: log.Id, logNote: log.Note);
            return null;
        }
        catch (Exception ex)
        {
            evidence.Add(searched, "scan-error", detail: ex.Message);
            return null;
        }
        finally { evidence.Complete(); }
    }

    /// <summary>The directories a scan reads, for the diagnostics.</summary>
    private static string Searched(string? lv2Path)
    {
        if (lv2Path is not null) return lv2Path;
        try { return string.Join(':', PluginSearchPaths.Lv2Directories()); }
        catch (Exception) { return "LV2_PATH"; }
    }

    private const string BundleMarker = "trace: scan LV2 plugin ";

    /// <summary>The bundle the helper's last phase marker names, if any.</summary>
    internal static string? Culprit(string stderr)
    {
        int at = stderr.LastIndexOf(BundleMarker, StringComparison.Ordinal);
        // A marker starts its line; the same words inside lilv's own
        // messages do not count.
        while (at > 0 && stderr[at - 1] != '\n')
            at = stderr.LastIndexOf(BundleMarker, at - 1, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = stderr.IndexOf('\n', at);
        string line = end < 0 ? stderr[at..] : stderr[at..end];
        int bundle = line.IndexOf(": bundle ", StringComparison.Ordinal);
        string path = bundle < 0 ? "" : line[(bundle + ": bundle ".Length)..];
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// The helper's catalogue, held to the rules the reading in this process
    /// applies: the helper is trusted no more than a bundle is. Read as a
    /// stream of tokens, as module descriptions are, so nothing but the
    /// plugins is held.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> Parse(ReadOnlySpan<byte> json)
    {
        var result = new List<PluginInfo>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The scanner did not print a catalogue.");
        bool listed = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("plugins"u8)) { reader.Read(); reader.Skip(); continue; }
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("The scanner's plugin list is not a list.");
            listed = true;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                if (ReadPlugin(ref reader) is PluginInfo info && known.Add(info.Plugin)) result.Add(info);
            }
        }
        // Read through the end: a catalogue followed by anything else is
        // not one the helper finished.
        while (reader.Read()) { }
        if (!listed) throw new JsonException("The scanner printed no plugin list.");
        return Sorted(result);
    }

    private static List<PluginInfo> Sorted(List<PluginInfo> plugins)
    {
        plugins.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        // Whole: the budget belongs to the message a client is sent, not to
        // what an insert can name. A chain saved around a large plugin has to
        // keep loading after another format's plugins arrive beside it.
        return plugins;
    }

    /// <summary>One plugin object; null for one the catalogue does not list.</summary>
    private static PluginInfo? ReadPlugin(ref Utf8JsonReader reader)
    {
        string? uri = null, name = null, category = null;
        bool latency = false;
        int audioIns = 0, audioOuts = 0;
        var inputs = new List<string>();
        var outputs = new List<string>();
        var features = new List<string>();
        List<string>? uiFeatures = null;
        var pars = new List<PluginParam>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("uri"u8)) { reader.Read(); uri = HostScan.Text(ref reader); }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = HostScan.Text(ref reader); }
            else if (reader.ValueTextEquals("class"u8)) { reader.Read(); category = HostScan.Text(ref reader); }
            else if (reader.ValueTextEquals("latency"u8)) { reader.Read(); latency = HostScan.Flag(ref reader); }
            else if (reader.ValueTextEquals("audioIns"u8)) { reader.Read(); audioIns = HostScan.Whole(ref reader); }
            else if (reader.ValueTextEquals("audioOuts"u8)) { reader.Read(); audioOuts = HostScan.Whole(ref reader); }
            else if (reader.ValueTextEquals("inputs"u8)) { reader.Read(); Strings(ref reader, inputs, MaxControls); }
            else if (reader.ValueTextEquals("outputs"u8)) { reader.Read(); Strings(ref reader, outputs, MaxControls); }
            else if (reader.ValueTextEquals("features"u8)) { reader.Read(); Strings(ref reader, features, MaxFeatures); }
            else if (reader.ValueTextEquals("ui"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                uiFeatures = [];
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("features"u8)) { reader.Read(); Strings(ref reader, uiFeatures, MaxFeatures); }
                    else { reader.Read(); reader.Skip(); }
                }
            }
            else if (reader.ValueTextEquals("params"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject || pars.Count >= MaxControls) { reader.Skip(); continue; }
                    if (ReadParam(ref reader) is PluginParam param) pars.Add(param);
                }
            }
            else { reader.Read(); reader.Skip(); }
        }
        if (uri is null) return null;
        // Plugin hosts wrapped as LV2 (Carla's rack and patchbay) need their
        // own GUI and bridges; inside a headless filter-chain they are dead
        // weight, and their declared features look like any other plugin's.
        if (uri.StartsWith("http://kxstudio.sf.net/carla", StringComparison.Ordinal)) return null;
        if (audioIns <= 0 || audioOuts <= 0 || inputs.Count == 0 || outputs.Count == 0) return null;   // generators and analysers are not inserts
        if (uri.Length > MaxUri) return null;
        List<string> required = [.. features.Select(f => f.Length <= MaxUri ? f : f[..MaxUri])];
        return new PluginInfo("lv2", uri, Clip(name ?? uri), Clip(category ?? ""), audioIns, audioOuts,
            inputs[0], outputs[0], pars, required, [.. inputs.Select(Clip)], [.. outputs.Select(Clip)])
        {
            ReportsLatency = latency,
            UnsupportedFeatures = UnsupportedFeatures(required),
            HasNativeUi = uiFeatures is not null,
            NativeUiRequiredFeatures = uiFeatures ?? [],
        };
    }

    /// <summary>One control; null for one the catalogue leaves out.</summary>
    private static PluginParam? ReadParam(ref Utf8JsonReader reader)
    {
        string? symbol = null, name = null;
        float min = 0, max = 1;
        float? initial = null;
        bool toggled = false, integer = false, logarithmic = false, enumeration = false;
        var points = new List<ScalePoint>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("symbol"u8)) { reader.Read(); symbol = HostScan.Text(ref reader); }
            else if (reader.ValueTextEquals("name"u8)) { reader.Read(); name = HostScan.Text(ref reader); }
            else if (reader.ValueTextEquals("min"u8)) { reader.Read(); min = Single(ref reader, 0); }
            else if (reader.ValueTextEquals("max"u8)) { reader.Read(); max = Single(ref reader, 1); }
            else if (reader.ValueTextEquals("default"u8)) { reader.Read(); initial = Single(ref reader, float.NaN); }
            else if (reader.ValueTextEquals("toggled"u8)) { reader.Read(); toggled = HostScan.Flag(ref reader); }
            else if (reader.ValueTextEquals("integer"u8)) { reader.Read(); integer = HostScan.Flag(ref reader); }
            else if (reader.ValueTextEquals("logarithmic"u8)) { reader.Read(); logarithmic = HostScan.Flag(ref reader); }
            else if (reader.ValueTextEquals("enum"u8)) { reader.Read(); enumeration = HostScan.Flag(ref reader); }
            else if (reader.ValueTextEquals("points"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject || points.Count >= MaxScalePoints) { reader.Skip(); continue; }
                    string? label = null;
                    float value = float.NaN;
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("label"u8)) { reader.Read(); label = HostScan.Text(ref reader); }
                        else if (reader.ValueTextEquals("value"u8)) { reader.Read(); value = Single(ref reader, float.NaN); }
                        else { reader.Read(); reader.Skip(); }
                    }
                    if (label is not null && float.IsFinite(value)) points.Add(new ScalePoint(Clip(label), value));
                }
            }
            else { reader.Read(); reader.Skip(); }
        }
        if (symbol is null) return null;
        float def = initial is float d && !float.IsNaN(d) ? d : min;
        // Infinite metadata cannot be sent as a JSON number.
        if (!float.IsFinite(min) || !float.IsFinite(max) || !float.IsFinite(def) || min > max) return null;
        string sym = Clip(symbol);
        return new PluginParam(sym, Clip(name ?? sym), min, max, Math.Clamp(def, min, max),
            toggled, integer, logarithmic, enumeration, points);
    }

    /// <summary>
    /// A number lilv read as a float. The helper prints nine significant
    /// digits, which read back to the same float, so the value the daemon
    /// holds is the one the in-process reading gives.
    /// </summary>
    private static float Single(ref Utf8JsonReader reader, float fallback)
    {
        float result = reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) ? (float)value : fallback;
        reader.Skip();
        return result;
    }

    private static void Strings(ref Utf8JsonReader reader, List<string> into, int limit)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (into.Count < limit && reader.TokenType == JsonTokenType.String) into.Add(reader.GetString() ?? "");
            else reader.Skip();
        }
    }

    /// <summary>
    /// The reading in this process, for a build without the native host. It
    /// loads lilv into the daemon, so a bundle that crashes lilv takes the
    /// daemon with it; the helper exists to keep that out of the daemon.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> InProcess(string? lv2Path)
    {
        var result = new List<PluginInfo>();
        IntPtr world;
        try { world = Lilv.lilv_world_new(); }
        catch (DllNotFoundException) { return result; }   // no lilv: no LV2 inserts, nothing else affected
        if (world == IntPtr.Zero) return result;
        try
        {
            if (lv2Path is not null)
            {
                IntPtr pathNode = Lilv.lilv_new_string(world, lv2Path);
                Lilv.lilv_world_set_option(world, "http://drobilla.net/ns/lilv#lv2-path", pathNode);
                Lilv.lilv_node_free(pathNode);
            }
            Lilv.lilv_world_load_all(world);
            IntPtr controlPort = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#ControlPort");
            IntPtr audioPort = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#AudioPort");
            IntPtr inputPort = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#InputPort");
            IntPtr outputPort = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#OutputPort");
            IntPtr toggled = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#toggled");
            IntPtr integer = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#integer");
            IntPtr enumeration = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#enumeration");
            IntPtr logarithmic = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/ext/port-props#logarithmic");
            IntPtr notOnGui = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/ext/port-props#notOnGUI");

            IntPtr plugins = Lilv.lilv_world_get_all_plugins(world);
            for (IntPtr it = Lilv.lilv_plugins_begin(plugins); !Lilv.lilv_plugins_is_end(plugins, it); it = Lilv.lilv_plugins_next(plugins, it))
            {
                IntPtr plugin = Lilv.lilv_plugins_get(plugins, it);
                try
                {
                    PluginInfo? info = Describe(world, plugin, controlPort, audioPort, inputPort, outputPort,
                        toggled, integer, enumeration, logarithmic, notOnGui);
                    if (info is not null) result.Add(info);
                }
                catch (Exception) { /* one broken bundle must not hide the rest */ }
            }
            foreach (IntPtr n in new[] { controlPort, audioPort, inputPort, outputPort, toggled, integer, enumeration, logarithmic, notOnGui })
                Lilv.lilv_node_free(n);
        }
        finally
        {
            Lilv.lilv_world_free(world);
        }
        return Sorted(result);
    }

    // Limits on what a bundle can make the daemon hold. lilv reports whatever
    // the bundle declares, and a broken or hostile one could declare
    // millions of ports; a real plugin has a few hundred at most (the
    // largest LSP and x42 plugins sit well under these).
    internal const int MaxPorts = 4096;
    internal const int MaxControls = 512;
    internal const int MaxScalePoints = 256;
    internal const int MaxText = 200;
    /// <summary>A plugin URI longer than this is not one the chain host will ever be asked for; the bundle is skipped.</summary>
    internal const int MaxUri = 512;
    /// <summary>Required features beyond this count are not read; no real plugin declares more than a handful.</summary>
    internal const int MaxFeatures = 64;
    /// <summary>
    /// Serialized size the catalog sent to a client may reach. A client reads
    /// at most 8 MiB in one message and drops anything longer, which would
    /// leave a picker with nothing in it, so the message stops short of that
    /// with room for what is around the list. What the daemon holds and
    /// resolves inserts against is not bounded by this.
    /// </summary>
    internal const int CatalogBudgetBytes = 7 * 1024 * 1024;

    private static string Clip(string s) => s.Length <= MaxText ? s : s[..MaxText];

    /// <summary>
    /// A plugin's footprint in the serialized catalog. The fixed parts are
    /// the field names and punctuation JSON puts around each value, measured
    /// against what the daemon actually sends, and rounded up: an estimate
    /// that ran under the truth would let the catalog past the size a client
    /// will read.
    /// </summary>
    internal static int Footprint(PluginInfo p)
    {
        if (Footprints.TryGetValue(p, out object? cached)) return (int)cached;
        // Measured, not estimated: the bytes the daemon writes for this entry
        // under its wire options, plus the separator. An estimate that ran
        // over the truth trimmed real catalogues from the picker for nothing.
        int size = JsonSerializer.SerializeToUtf8Bytes(p, WireJson).Length + 1;
        Footprints.AddOrUpdate(p, size);
        return size;
    }

    // The daemon's message options; PluginInfo is immutable, so one measure
    // per instance serves every ordering and budget pass.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly ConditionalWeakTable<PluginInfo, object> Footprints = new();

    /// <summary>
    /// Keep a list under the budget by dropping the largest plugins first: a
    /// handful of monsters must not push every ordinary plugin out of the
    /// window's picker. The room already spent on the chains' own plugins is
    /// taken off the budget by the caller.
    /// </summary>
    internal static List<PluginInfo> WithinBudget(List<PluginInfo> all, long budget = CatalogBudgetBytes)
    {
        long total = all.Sum(p => (long)Footprint(p));
        if (total <= budget) return all;
        var keep = new HashSet<PluginInfo>(all);
        foreach (PluginInfo p in all.OrderByDescending(Footprint))
        {
            if (total <= budget) break;
            keep.Remove(p);
            total -= Footprint(p);
        }
        return [.. all.Where(keep.Contains)];
    }

    private static PluginInfo? Describe(IntPtr world, IntPtr plugin, IntPtr controlPort, IntPtr audioPort,
        IntPtr inputPort, IntPtr outputPort, IntPtr toggled, IntPtr integer, IntPtr enumeration, IntPtr logarithmic, IntPtr notOnGui)
    {
        string? uri = Lilv.Str(Lilv.lilv_node_as_uri(Lilv.lilv_plugin_get_uri(plugin)));
        if (uri is null) return null;
        // Plugin hosts wrapped as LV2 (Carla's rack and patchbay) need their
        // own GUI and bridges; inside a headless filter-chain they are dead
        // weight, and their declared features look like any other plugin's.
        if (uri.StartsWith("http://kxstudio.sf.net/carla", StringComparison.Ordinal)) return null;
        string name = Clip(Lilv.OwnedString(Lilv.lilv_plugin_get_name(plugin)) ?? uri);
        string category = "";
        IntPtr cls = Lilv.lilv_plugin_get_class(plugin);
        if (cls != IntPtr.Zero)
            category = Clip(Lilv.Str(Lilv.lilv_node_as_string(Lilv.lilv_plugin_class_get_label(cls))) ?? "");

        uint n = Lilv.lilv_plugin_get_num_ports(plugin);
        if (n > MaxPorts) return null;   // nothing real declares this many; a bundle like that is skipped
        var mins = new float[n]; var maxs = new float[n]; var defs = new float[n];
        Lilv.lilv_plugin_get_port_ranges_float(plugin, mins, maxs, defs);

        int audioIns = 0, audioOuts = 0;
        string? inSym = null, outSym = null;
        var inSyms = new List<string>();
        var outSyms = new List<string>();
        var pars = new List<PluginParam>();
        for (uint i = 0; i < n; i++)
        {
            IntPtr port = Lilv.lilv_plugin_get_port_by_index(plugin, i);
            if (port == IntPtr.Zero) continue;
            bool isIn = Lilv.lilv_port_is_a(plugin, port, inputPort);
            string sym = Lilv.Str(Lilv.lilv_node_as_string(Lilv.lilv_port_get_symbol(plugin, port))) ?? $"port{i}";
            if (Lilv.lilv_port_is_a(plugin, port, audioPort))
            {
                    if (isIn) { audioIns++; inSym ??= sym; if (inSyms.Count < MaxControls) inSyms.Add(Clip(sym)); }
                else if (Lilv.lilv_port_is_a(plugin, port, outputPort)) { audioOuts++; outSym ??= sym; if (outSyms.Count < MaxControls) outSyms.Add(Clip(sym)); }
                continue;
            }
            if (!isIn || !Lilv.lilv_port_is_a(plugin, port, controlPort)) continue;
            if (Lilv.lilv_port_has_property(plugin, port, notOnGui)) continue;
            if (pars.Count >= MaxControls) continue;   // the first controls are the ones a picker can show anyway
            sym = Clip(sym);
            string pname = Clip(Lilv.OwnedString(Lilv.lilv_port_get_name(plugin, port)) ?? sym);
            var points = new List<ScalePoint>();
            IntPtr sps = Lilv.lilv_port_get_scale_points(plugin, port);
            if (sps != IntPtr.Zero)
            {
                for (IntPtr sit = Lilv.lilv_scale_points_begin(sps); !Lilv.lilv_scale_points_is_end(sps, sit); sit = Lilv.lilv_scale_points_next(sps, sit))
                {
                    if (points.Count >= MaxScalePoints) break;
                    IntPtr sp = Lilv.lilv_scale_points_get(sps, sit);
                    string? lbl = Lilv.Str(Lilv.lilv_node_as_string(Lilv.lilv_scale_point_get_label(sp)));
                    IntPtr vn = Lilv.lilv_scale_point_get_value(sp);
                    double v = Lilv.lilv_node_is_float(vn) || Lilv.lilv_node_is_int(vn) ? Lilv.lilv_node_as_float(vn) : 0;
                    if (lbl is not null && double.IsFinite(v)) points.Add(new ScalePoint(Clip(lbl), v));
                }
                Lilv.lilv_scale_points_free(sps);
            }
            float min = float.IsNaN(mins[i]) ? 0 : mins[i];
            float max = float.IsNaN(maxs[i]) ? 1 : maxs[i];
            float def = float.IsNaN(defs[i]) ? min : defs[i];
            // NaN means a missing range to lilv and keeps the legacy defaults
            // above. Infinite metadata cannot be sent as a JSON number.
            if (!float.IsFinite(min) || !float.IsFinite(max) || !float.IsFinite(def) || min > max) continue;
            pars.Add(new PluginParam(sym, pname, min, max, Math.Clamp(def, min, max),
                Lilv.lilv_port_has_property(plugin, port, toggled),
                Lilv.lilv_port_has_property(plugin, port, integer),
                Lilv.lilv_port_has_property(plugin, port, logarithmic),
                Lilv.lilv_port_has_property(plugin, port, enumeration),
                points));
        }
        if (audioIns == 0 || audioOuts == 0 || inSym is null || outSym is null) return null;   // generators and analysers are not inserts

        if (uri.Length > MaxUri) return null;
        var features = new List<string>();
        IntPtr req = Lilv.lilv_plugin_get_required_features(plugin);
        if (req != IntPtr.Zero)
        {
            for (IntPtr fit = Lilv.lilv_nodes_begin(req); !Lilv.lilv_nodes_is_end(req, fit) && features.Count < MaxFeatures; fit = Lilv.lilv_nodes_next(req, fit))
            {
                string? f = Lilv.Str(Lilv.lilv_node_as_uri(Lilv.lilv_nodes_get(req, fit)));
                if (f is not null) features.Add(f.Length <= MaxUri ? f : f[..MaxUri]);
            }
            Lilv.lilv_nodes_free(req);
        }
        IReadOnlyList<string>? uiFeatures = X11UiRequiredFeatures(world, plugin);
        return new PluginInfo("lv2", uri, name, category, audioIns, audioOuts, inSym, outSym, pars, features, inSyms, outSyms)
        {
            ReportsLatency = Lilv.lilv_plugin_has_latency(plugin),
            UnsupportedFeatures = UnsupportedFeatures(features),
            HasNativeUi = uiFeatures is not null,
            NativeUiRequiredFeatures = uiFeatures ?? [],
        };
    }

    /// <summary>
    /// Required features of the X11 UI the native helper will select (the
    /// first one in lilv's catalog order), or null when there is no X11 UI.
    /// </summary>
    private static IReadOnlyList<string>? X11UiRequiredFeatures(IntPtr world, IntPtr plugin)
    {
        IntPtr uis = Lilv.lilv_plugin_get_uis(plugin);
        if (uis == IntPtr.Zero) return null;
        IntPtr type = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/extensions/ui#X11UI");
        IntPtr requiredFeature = Lilv.lilv_new_uri(world, "http://lv2plug.in/ns/lv2core#requiredFeature");
        try
        {
            for (IntPtr it = Lilv.lilv_uis_begin(uis); !Lilv.lilv_uis_is_end(uis, it); it = Lilv.lilv_uis_next(uis, it))
            {
                IntPtr ui = Lilv.lilv_uis_get(uis, it);
                if (!Lilv.lilv_ui_is_a(ui, type)) continue;
                var features = new List<string>();
                IntPtr required = Lilv.lilv_world_find_nodes(
                    world, Lilv.lilv_ui_get_uri(ui), requiredFeature, IntPtr.Zero);
                if (required != IntPtr.Zero)
                {
                    for (IntPtr fit = Lilv.lilv_nodes_begin(required); !Lilv.lilv_nodes_is_end(required, fit); fit = Lilv.lilv_nodes_next(required, fit))
                    {
                        string? feature = Lilv.Str(Lilv.lilv_node_as_uri(Lilv.lilv_nodes_get(required, fit)));
                        if (feature is not null) features.Add(feature);
                    }
                    Lilv.lilv_nodes_free(required);
                }
                return features;
            }
            return null;
        }
        finally
        {
            Lilv.lilv_node_free(requiredFeature);
            Lilv.lilv_node_free(type);
            Lilv.lilv_uis_free(uis);
        }
    }

    /// <summary>The slice of liblilv this catalog uses.</summary>
    private static class Lilv
    {
        private const string Lib = "liblilv-0.so.0";

        [DllImport(Lib)] public static extern IntPtr lilv_world_new();
        [DllImport(Lib)] public static extern void lilv_world_free(IntPtr world);
        [DllImport(Lib)] public static extern void lilv_world_load_all(IntPtr world);
        [DllImport(Lib)] public static extern IntPtr lilv_world_get_all_plugins(IntPtr world);
        [DllImport(Lib)] public static extern IntPtr lilv_world_find_nodes(IntPtr world, IntPtr subject, IntPtr predicate, IntPtr obj);
        [DllImport(Lib)] public static extern IntPtr lilv_new_uri(IntPtr world, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
        [DllImport(Lib)] public static extern IntPtr lilv_new_string(IntPtr world, [MarshalAs(UnmanagedType.LPUTF8Str)] string str);
        [DllImport(Lib)] public static extern void lilv_world_set_option(IntPtr world, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri, IntPtr value);
        [DllImport(Lib)] public static extern void lilv_node_free(IntPtr node);
        [DllImport(Lib)] public static extern IntPtr lilv_node_as_uri(IntPtr node);
        [DllImport(Lib)] public static extern IntPtr lilv_node_as_string(IntPtr node);
        [DllImport(Lib)] public static extern float lilv_node_as_float(IntPtr node);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_node_is_float(IntPtr node);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_node_is_int(IntPtr node);

        [DllImport(Lib)] public static extern IntPtr lilv_plugins_begin(IntPtr plugins);
        [DllImport(Lib)] public static extern IntPtr lilv_plugins_get(IntPtr plugins, IntPtr iter);
        [DllImport(Lib)] public static extern IntPtr lilv_plugins_next(IntPtr plugins, IntPtr iter);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_plugins_is_end(IntPtr plugins, IntPtr iter);

        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_uri(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_name(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_class(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_class_get_label(IntPtr cls);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_plugin_has_latency(IntPtr plugin);
        [DllImport(Lib)] public static extern uint lilv_plugin_get_num_ports(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_port_by_index(IntPtr plugin, uint index);
        [DllImport(Lib)] public static extern void lilv_plugin_get_port_ranges_float(IntPtr plugin, float[] mins, float[] maxs, float[] defs);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_required_features(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_plugin_get_uis(IntPtr plugin);
        [DllImport(Lib)] public static extern IntPtr lilv_uis_begin(IntPtr uis);
        [DllImport(Lib)][return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_uis_is_end(IntPtr uis, IntPtr iterator);
        [DllImport(Lib)] public static extern IntPtr lilv_uis_next(IntPtr uis, IntPtr iterator);
        [DllImport(Lib)] public static extern IntPtr lilv_uis_get(IntPtr uis, IntPtr iterator);
        [DllImport(Lib)] public static extern IntPtr lilv_ui_get_uri(IntPtr ui);
        [DllImport(Lib)][return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_ui_is_a(IntPtr ui, IntPtr type);
        [DllImport(Lib)] public static extern void lilv_uis_free(IntPtr uis);

        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_port_is_a(IntPtr plugin, IntPtr port, IntPtr cls);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_port_has_property(IntPtr plugin, IntPtr port, IntPtr prop);
        [DllImport(Lib)] public static extern IntPtr lilv_port_get_symbol(IntPtr plugin, IntPtr port);
        [DllImport(Lib)] public static extern IntPtr lilv_port_get_name(IntPtr plugin, IntPtr port);
        [DllImport(Lib)] public static extern IntPtr lilv_port_get_scale_points(IntPtr plugin, IntPtr port);

        [DllImport(Lib)] public static extern IntPtr lilv_scale_points_begin(IntPtr sps);
        [DllImport(Lib)] public static extern IntPtr lilv_scale_points_get(IntPtr sps, IntPtr iter);
        [DllImport(Lib)] public static extern IntPtr lilv_scale_points_next(IntPtr sps, IntPtr iter);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_scale_points_is_end(IntPtr sps, IntPtr iter);
        [DllImport(Lib)] public static extern void lilv_scale_points_free(IntPtr sps);
        [DllImport(Lib)] public static extern IntPtr lilv_scale_point_get_label(IntPtr sp);
        [DllImport(Lib)] public static extern IntPtr lilv_scale_point_get_value(IntPtr sp);

        [DllImport(Lib)] public static extern IntPtr lilv_nodes_begin(IntPtr nodes);
        [DllImport(Lib)] public static extern IntPtr lilv_nodes_get(IntPtr nodes, IntPtr iter);
        [DllImport(Lib)] public static extern IntPtr lilv_nodes_next(IntPtr nodes, IntPtr iter);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool lilv_nodes_is_end(IntPtr nodes, IntPtr iter);
        [DllImport(Lib)] public static extern void lilv_nodes_free(IntPtr nodes);

        public static string? Str(IntPtr utf8) => utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);

        /// <summary>Read a node this process owns, then free it.</summary>
        public static string? OwnedString(IntPtr node)
        {
            if (node == IntPtr.Zero) return null;
            try { return Str(lilv_node_as_string(node)); }
            finally { lilv_node_free(node); }
        }
    }
}
