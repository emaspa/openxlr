using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The single seam over PipeWire. Everything the mixer does to the audio graph
/// goes through here, using the primitives verified on hardware:
///   - null sink            : pactl load-module module-null-sink
///   - virtual capture device: pactl load-module module-remap-source
///   - channel fan-out      : module-combine-sink, whose per-slave streams are
///                            the faders
///   - physical routes      : direct pw-link port connections, clocked by the
///                            hardware sink
///   - discovery            : pw-dump (JSON graph)
/// </summary>
public sealed class PipeWireAdapter
{
    private const string ClipGuardPluginFile = "hard_limiter_1413.so";
    // Intermediate DSP ports stay in the graph, not in desktop device lists
    // or the session manager's automatic microphone and playback routing.
    private const string InternalFilterProperties = "node.autoconnect = false node.hidden = true state.restore-props = false";

    private readonly Func<DspFeatureAvailability>? _clipGuardAvailabilityOverride;
    private readonly Action? _progress;
    private readonly List<uint> _modules = [];
    private readonly List<Process> _loopbacks = [];
    private readonly List<Process> _filters = [];
    private readonly HashSet<NativePluginHost> _nativeHosts = [];
    private readonly WineSession _wine;
    private readonly Action<string>? _note;
    private PipeWireGraph? _graph;
    // Cancelled at the daemon's stop deadline: a helper still running then
    // is killed and none is started after it, so an audio server that has
    // stopped answering cannot hold the stop past the service manager's
    // timeout. Default (never cancelled) outside a stop.
    private CancellationToken _stopDeadline;

    /// <summary>
    /// End every helper this adapter runs once <paramref name="deadline"/> is
    /// cancelled: one in flight is killed, later ones are not started and
    /// fail at once. Set when the daemon begins to stop.
    /// </summary>
    internal void EndHelpersAt(CancellationToken deadline) => _stopDeadline = deadline;

    /// <summary>
    /// How many of its modules the last <see cref="TearDown"/> left loaded
    /// because the stop deadline passed. The next start removes them with
    /// <see cref="UnloadStaleModules"/>.
    /// </summary>
    public int ModulesLeft { get; private set; }

    /// <summary>
    /// Latency compensation is on: an LV2 insert that declares a latency port
    /// runs in the native host, which reads the figure, when the host is
    /// installed and carries the plugin. Without the host it stays in the
    /// filter chain and gives no figure.
    /// </summary>
    internal bool MeasurePluginLatency { get; set; }

    private bool NeedsLatencyHost(InsertDefinition insert) => MeasurePluginLatency && insert.Kind == "lv2"
        && NativePluginHost.HostInstalled
        && PluginCatalog.Find(insert) is { ReportsLatency: true } info
        && NativePluginHost.SupportsFeatures(info.RequiredFeatures);

    /// <summary>Use a registry subscription for this adapter until the returned lease is disposed.</summary>
    public IDisposable WatchGraph(Action<string>? note = null)
    {
        lock (DumpGate)
        {
            if (_graph is not null) throw new InvalidOperationException("PipeWire registry is already subscribed");
            _graph = new PipeWireGraph(note);
            _graph.WaitFor(_ => true, TimeSpan.FromSeconds(5));
            return new GraphWatch(this, _graph);
        }
    }

    private sealed class GraphWatch(PipeWireAdapter owner, PipeWireGraph graph) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner.DumpGate)
            {
                if (ReferenceEquals(owner._graph, graph)) owner._graph = null;
                owner._dumpObjects = null;
                owner._defaultsRevision = -1;
                owner._inputPorts = owner._outputPorts = (-1, null);
                owner._directSinkVolumeUntil.Clear();
            }
            graph.Dispose();
        }
    }

    public PipeWireAdapter() => _wine = new WineSession();

    /// <summary>
    /// Report completed helper operations, including failures, to the daemon's
    /// progress gate. <paramref name="note"/> carries the few lines the mixer
    /// has for the journal; the core has no logger of its own.
    /// </summary>
    public PipeWireAdapter(Action progress, Action<string>? note = null)
    {
        _progress = progress;
        _note = note;
        _wine = new WineSession(note);
    }

    /// <summary>Hand the journal one line, when the daemon gave this adapter somewhere to put it.</summary>
    internal void Note(string line) => _note?.Invoke(line);

    internal PipeWireAdapter(Func<DspFeatureAvailability> clipGuardAvailabilityOverride)
    {
        _clipGuardAvailabilityOverride = clipGuardAvailabilityOverride;
        _wine = new WineSession();
    }

    /// <summary>
    /// pactl joins its arguments into one module-argument string, and PipeWire's
    /// module parser splits that on whitespace, honouring double quotes; the
    /// value of sink_properties is then parsed again as key=value pairs,
    /// honouring single quotes. So the whole property list goes in double
    /// quotes and a description with spaces in single quotes. The earlier
    /// form (a backslash-escaped description, no outer quotes) kept the
    /// description but silently dropped every property after it: the
    /// channels ran for months without priority.session, without
    /// suspend-on-idle=false, and flagged node.virtual, which KDE's audio
    /// applet hides. Verified on PipeWire 1.6 with all four properties.
    ///
    /// Each pass strips one layer of backslash escapes, so a character that
    /// matters to the inner pass (a quote or a backslash in a user's name)
    /// is escaped there by <see cref="PropValue"/> and the result escaped
    /// again here for the outer pass. One layer, the earlier form, let a
    /// name with an apostrophe end the description at the apostrophe and
    /// drop the properties after it. Verified on PipeWire 1.6.8 with a
    /// name holding both quotes, a backslash and '#'.
    /// </summary>
    internal static string PropList(string props)
        => '"' + props.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

    /// <summary>
    /// A string inside a filter-chain (SPA JSON) argument. PipeWire's
    /// property parser treats '#' as the start of a comment even inside a
    /// quoted string, so an LV2 URI such as darc#mono broke the whole graph
    /// with "Mismatched bracket" and "Could not load module". The JSON
    /// unicode escape survives that pass and decodes to '#' afterwards
    /// (verified on PipeWire 1.6.8). Quotes and backslashes are escaped the
    /// JSON way.
    /// </summary>
    internal static string SpaString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("#", "\\u0023");
    /// <summary>A value inside a <see cref="PropList"/>: single-quoted for the inner pass.</summary>
    internal static string PropValue(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>
    /// Check the optional LADSPA dependency before changing a live graph. PipeWire
    /// otherwise starts a short-lived holder process and only reports the missing
    /// plugin after the old microphone route has already been removed.
    /// </summary>
    public DspFeatureAvailability GetSoftwareClipGuardAvailability()
    {
        if (_clipGuardAvailabilityOverride is not null) return _clipGuardAvailabilityOverride();
        string? plugin = FindLadspaPlugin(ClipGuardPluginFile, LadspaSearchDirectories());
        return plugin is not null
            ? new DspFeatureAvailability(true, null)
            : new DspFeatureAvailability(false,
                "software ClipGuard is unavailable: LADSPA plugin hard_limiter_1413 was not found; " +
                "install swh-plugins and restart OpenXLR");
    }

    /// <summary>Find one LADSPA module without trying to load it into the live graph.</summary>
    internal static string? FindLadspaPlugin(string fileName, IEnumerable<string> directories)
    {
        foreach (string directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // An invalid user-supplied LADSPA_PATH entry is skipped; the
                // remaining standard directories may still contain the plugin.
            }
        }
        return null;
    }

    private static IReadOnlyList<string> LadspaSearchDirectories()
    {
        var result = new List<string>();
        string? configured = Environment.GetEnvironmentVariable("LADSPA_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            result.AddRange(configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) result.Add(Path.Combine(home, ".ladspa"));
        result.AddRange([
            "/usr/lib/ladspa", "/usr/lib64/ladspa",
            "/usr/local/lib/ladspa", "/usr/local/lib64/ladspa",
        ]);

        // Debian multiarch puts LADSPA modules below paths such as
        // /usr/lib/x86_64-linux-gnu/ladspa. Distribution packages normally set
        // no LADSPA_PATH, so include those directories explicitly.
        try
        {
            if (Directory.Exists("/usr/lib"))
                foreach (string multiarch in Directory.EnumerateDirectories("/usr/lib", "*-linux-gnu"))
                    result.Add(Path.Combine(multiarch, "ladspa"));
        }
        catch (IOException) { /* directory changed while enumerating */ }
        catch (UnauthorizedAccessException) { /* unusual sandbox; standard paths remain */ }
        return result;
    }

    /// <summary>
    /// Unload leftover modules from a previous instance, identified by our
    /// node-name prefix in the module arguments. Modules this instance loaded
    /// are left alone, so it is safe to call at any point.
    /// </summary>
    public void UnloadStaleModules(string namePrefix)
    {
        string listing;
        try { listing = Run("pactl", "list", "short", "modules"); }
        catch (InvalidOperationException) { return; }
        foreach (string line in listing.Split('\n'))
        {
            string[] cols = line.Split('\t');
            if (cols.Length < 3 || !cols[2].Contains(namePrefix, StringComparison.Ordinal)) continue;
            if (!uint.TryParse(cols[0], out uint id) || _modules.Contains(id)) continue;
            try { Run("pactl", "unload-module", id.ToString()); }
            catch (InvalidOperationException) { /* already gone */ }
        }
    }

    /// <summary>Load a null sink; returns its module id for later unload.</summary>
    public uint CreateNullSink(string nodeName, string description, bool visible = true)
    {
        // suspend-on-idle must be off: an idle channel sink would otherwise be
        // suspended by PipeWire and drop the first moment of audio (or all of it)
        // when an application starts playing into it again.
        string outp = Run("pactl",
            "load-module", "module-null-sink",
            $"sink_name={nodeName}",
            "media.class=Audio/Sink",
            // priority.session far below any hardware sink, so WirePlumber never
            // auto-switches the system default to one of our internal sinks
            // (which silently swallows the user's desktop audio).
            // node.virtual=false: KDE's audio applet hides virtual devices,
            // and these are devices the user assigns applications to.
            "sink_properties=" + PropList($"node.description={PropValue(description)}" +
            " node.suspend-on-idle=false " + (visible ? "priority.session=100 node.virtual=false" :
                "priority.session=0 node.virtual=true node.hidden=true")));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// A remap sink is a filter attached to a master sink: it is clocked by its
    /// master, so audio written to it always flows (no loopback process, no
    /// separate clock island). One remap per channel-mix cell carries that
    /// cell's fader volume.
    /// </summary>
    public uint CreateRemapSink(string nodeName, string masterSink, string description)
    {
        string outp = Run("pactl",
            "load-module", "module-remap-sink",
            $"sink_name={nodeName}",
            $"master={masterSink}",
            "sink_properties=" + PropList($"node.description={PropValue(description)} priority.session=90"));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// A combine sink duplicates its input into several slave sinks. One per
    /// channel: applications play into it and every mix receives the audio
    /// through that channel's remap cells.
    /// </summary>
    /// <param name="visible">
    /// Whether desktop audio applets should list the sink as a playback
    /// device. Application channels are (users assign apps to them);
    /// hardware input channels are not, since nothing should play into a
    /// channel that carries a microphone.
    /// </param>
    /// <param name="slaves">
    /// The mix sinks to feed: explicit node names separated by commas, or a
    /// "~pattern" regular expression. PipeWire's combine sink keeps watching
    /// the registry, so with a pattern a mix sink created later gets its own
    /// stream (leg) without reloading the combine, and a removed mix drops
    /// its leg. Verified on PipeWire 1.6 through pactl.
    /// </param>
    public uint CreateCombineSink(string nodeName, string slaves, string description, bool visible = true)
    {
        string outp = Run("pactl",
            "load-module", "module-combine-sink",
            $"sink_name={nodeName}",
            $"slaves={slaves}",
            // suspend-on-idle=false keeps the combine's monitor source running;
            // a suspended monitor makes the channel's level meter read silence
            // even while audio flows through the sink.
            // Combines share a media name. Keep the desktop from storing one
            // channel's pause or gain and restoring it onto another channel.
            "sink_properties=" + PropList($"node.description={PropValue(description)}" +
            $" priority.session=100 node.suspend-on-idle=false state.restore-props=false node.virtual={(visible ? "false" : "true")}"));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>Remove one owned module, retaining it for teardown if unloading fails.</summary>
    public void UnloadModule(uint id)
    {
        if (!_modules.Contains(id)) throw new InvalidOperationException("module is not owned by this mixer");
        Run("pactl", "unload-module", id.ToString());
        _modules.Remove(id);
    }

    /// <summary>An owned node disappeared externally; its old module id is no longer ours.</summary>
    internal void ForgetModule(uint id) => _modules.Remove(id);

    /// <summary>A "sink#suffix" pseudo-device address without its suffix.</summary>
    private static string BareSink(string sinkName)
    {
        int marker = sinkName.IndexOf('#');
        return marker >= 0 ? sinkName[..marker] : sinkName;
    }

    /// <summary>
    /// Volume and mute of every sink this daemon created, read from the
    /// graph dump. The session manager or a desktop applet can turn one of
    /// them down, and a channel sink at half volume quietly cuts every mix
    /// it feeds by 15 dB (seen on a user's machine: all nine channel sinks
    /// restored to 55% by something outside OpenXLR).
    /// </summary>
    public IReadOnlyList<OwnSinkLevel> OwnSinkLevels() => OwnSinkLevels(GraphObjects());

    /// <summary>Selected sinks' mute flags from the shared registry, without another helper or device catalogue.</summary>
    public IReadOnlyDictionary<string, (string Identity, bool Muted)> ObservedSinkMutes(IReadOnlySet<string> names)
    {
        var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        if (names.Count == 0) return result;
        foreach (JsonElement node in GraphObjects())
        {
            if (!node.TryGetProperty("type", out var type) || type.GetString() != "PipeWire:Interface:Node"
                || !node.TryGetProperty("info", out var info) || !info.TryGetProperty("props", out var props)
                || !props.TryGetProperty("node.name", out var name) || name.GetString() is not string sink
                || !names.Contains(sink) || !props.TryGetProperty("media.class", out var kind)
                || kind.GetString() != "Audio/Sink") continue;
            if (SinkLevel(info) is (_, _, bool muted))
            {
                string identity = props.TryGetProperty("object.serial", out var serial) ? "serial:" + serial.ToString()
                    : node.TryGetProperty("id", out var id) ? "node:" + id.ToString() : "";
                result[sink] = (identity, muted);
            }
        }
        return result;
    }

    /// <summary>Read selected native identities and mute flags together, before registry echoes arrive.</summary>
    internal IReadOnlyDictionary<string, (string Identity, bool Muted)> NativeSinkMutes(IReadOnlySet<string> names)
        => names.Count == 0 ? new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
            : ParseNativeSinkMutes(RunBytes("pactl", 4 * 1024 * 1024, "--format=json", "list", "sinks"), names);

    internal static IReadOnlyDictionary<string, (string Identity, bool Muted)> ParseNativeSinkMutes(byte[] json, IReadOnlySet<string> names)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a native sink array.");
        var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (JsonElement sink in document.RootElement.EnumerateArray())
        {
            if (sink.ValueKind != JsonValueKind.Object || !sink.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new JsonException("A native sink has no name.");
            string key = name.GetString()!;
            if (!names.Contains(key)) continue;
            if (!sink.TryGetProperty("mute", out var mute) || mute.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("A native sink has no mute flag.");
            string? identity = null;
            if (sink.TryGetProperty("properties", out var props))
            {
                if (props.ValueKind != JsonValueKind.Object) throw new JsonException("A native sink has invalid properties.");
                if (props.TryGetProperty("object.serial", out var serial) && NativeRegistryId(serial) is { } serialId) identity = "serial:" + serialId;
                else if (props.TryGetProperty("object.id", out var node) && NativeRegistryId(node) is { } nodeId) identity = "node:" + nodeId;
            }
            // A Pulse index is not a PipeWire node id. It is compared only
            // with later native snapshots, in its own namespace.
            if (identity is null && sink.TryGetProperty("index", out var index) && NativeRegistryId(index) is { } pulseId) identity = "pulse:" + pulseId;
            if (identity is null) throw new JsonException("A native sink has no identity.");
            if (!result.TryAdd(key, (identity, mute.GetBoolean()))) throw new JsonException("More than one native sink has the selected name.");
        }
        return result;

        static string? NativeRegistryId(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number)) return number.ToString(CultureInfo.InvariantCulture);
            return value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                ? number.ToString(CultureInfo.InvariantCulture) : null;
        }
    }

    internal static IReadOnlyList<OwnSinkLevel> OwnSinkLevels(byte[] json)
    {
        return OwnSinkLevels(ParseObjects(json));
    }

    private static IReadOnlyList<OwnSinkLevel> OwnSinkLevels(IEnumerable<JsonElement> objects)
    {
        var found = new List<OwnSinkLevel>();
        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;
            string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
            if (name is null || !name.StartsWith("OpenXLR_", StringComparison.Ordinal)) continue;
            if (!props.TryGetProperty("media.class", out JsonElement m) || m.GetString() != "Audio/Sink") continue;
            if (SinkLevel(info) is (double volume, double desktop, bool muted))
                found.Add(new OwnSinkLevel(name, volume, muted) { DesktopVolume = desktop });
        }
        return found;
    }

    /// <summary>
    /// The daemon's own DSP stages, published as streams so no application
    /// can record from or play into one as a device. The desktop still lists
    /// each as an application with a volume and a mute, which nothing of ours
    /// sets, so the sweep holds them at unity as it holds our sinks.
    /// </summary>
    public IReadOnlyList<OwnStageStream> OwnStageStreams() => OwnStageStreams(GraphObjects());

    /// <summary>Our sinks and our stage streams from one graph read, for the sweep that holds both.</summary>
    public (IReadOnlyList<OwnSinkLevel> Sinks, IReadOnlyList<OwnStageStream> Stages) OwnLevels()
    {
        JsonElement[] objects = GraphObjects();
        return (OwnSinkLevels(objects), OwnStageStreams(objects));
    }

    internal static IReadOnlyList<OwnStageStream> OwnStageStreams(byte[] json) => OwnStageStreams(ParseObjects(json));

    private static List<OwnStageStream> OwnStageStreams(IEnumerable<JsonElement> objects)
    {
        var found = new List<OwnStageStream>();
        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;
            string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
            if (name is null || !name.StartsWith("OpenXLR_", StringComparison.Ordinal)) continue;
            string? kind = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() : null;
            if (kind is not ("Stream/Input/Audio" or "Stream/Output/Audio")) continue;
            if (Serial(props) is not int serial) continue;
            if (SinkLevel(info) is (double volume, _, bool muted))
                found.Add(new OwnStageStream(name, serial, kind == "Stream/Input/Audio", volume, muted));
        }
        return found;
    }

    /// <summary>Put one of our stage streams back to full volume, unmuted.</summary>
    public void HoldStageStream(OwnStageStream stage)
    {
        string kind = stage.Input ? "source-output" : "sink-input";
        string index = stage.Serial.ToString(CultureInfo.InvariantCulture);
        Run("pactl", $"set-{kind}-volume", index, "100%");
        Run("pactl", $"set-{kind}-mute", index, "0");
        InvalidateDump();
    }

    /// <summary>A node's object serial, which pipewire-pulse uses as its stream index.</summary>
    private static int? Serial(JsonElement props)
    {
        if (!props.TryGetProperty("object.serial", out JsonElement s)) return null;
        int serial = 0;
        bool parsed = s.ValueKind switch
        {
            JsonValueKind.Number => s.TryGetInt32(out serial),
            JsonValueKind.String => int.TryParse(s.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out serial),
            _ => false,
        };
        return parsed ? serial : null;
    }

    /// <summary>
    /// The level and mute each OpenXLR combine leg carries right now, keyed
    /// by its sink-input index (the node's object serial), with the level as
    /// the desktop percentage <see cref="SetSinkInputVolume"/> writes.
    /// </summary>
    public IReadOnlyDictionary<int, (int Percent, bool Muted)> CombineLegLevels() => CombineLegLevels(GraphObjects());

    internal static IReadOnlyDictionary<int, (int Percent, bool Muted)> CombineLegLevels(byte[] json)
        => CombineLegLevels(ParseObjects(json));

    private static Dictionary<int, (int Percent, bool Muted)> CombineLegLevels(IEnumerable<JsonElement> objects)
    {
        var found = new Dictionary<int, (int, bool)>();
        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;
            string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
            if (name is null || !name.StartsWith("output.OpenXLR_", StringComparison.Ordinal)) continue;
            if (!props.TryGetProperty("media.class", out JsonElement m) || m.GetString() != "Stream/Output/Audio") continue;
            if (Serial(props) is not int serial) continue;
            if (SinkLevel(info) is (_, double desktop, bool muted))
                found[serial] = ((int)Math.Round(desktop * 100), muted);
        }
        return found;
    }

    /// <summary>
    /// A sink node's level from its Props param: the quietest channel as
    /// linear amplitude, the loudest as a desktop percentage, and mute.
    /// </summary>
    private static (double Volume, double DesktopVolume, bool Muted)? SinkLevel(JsonElement info, bool firstChannel = false)
    {
        if (!info.TryGetProperty("params", out JsonElement pars) ||
            !pars.TryGetProperty("Props", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement p in list.EnumerateArray())
        {
            if (!p.TryGetProperty("channelVolumes", out JsonElement cv) || cv.ValueKind != JsonValueKind.Array) continue;
            double volume = 1.0, maximum = 0.0, first = 0.0;
            bool any = false;
            foreach (JsonElement v in cv.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0)
                {
                    if (!any) first = d;
                    volume = any ? Math.Min(volume, d) : d;
                    maximum = Math.Max(maximum, d);
                    any = true;
                }
            if (!any) continue;
            bool muted = p.TryGetProperty("mute", out JsonElement mu) && mu.ValueKind == JsonValueKind.True;
            // PipeWire stores linear amplitude; Pulse desktop percentages
            // use its cube root. Device lists keep the loudest channel;
            // the legacy sink-volume query keeps pactl's first channel.
            // Neither read changes channel balance.
            return (volume, Math.Round(Math.Cbrt(firstChannel ? first : maximum) * 100) / 100, muted);
        }
        return null;
    }

    /// <summary>
    /// The loudest an output device is driven from here, and the most a read
    /// of one reports. PipeWire and every desktop applet allow a boost past
    /// unity, so a device found at 120% has to be writable at 120% as well:
    /// a mixer that reads a range it cannot write lets the outputs it holds
    /// together drift apart with nothing to say so. Monitor mix masters share
    /// this range. Channel sends,
    /// other mix masters and the microphone keep their own 0 to 1 range.
    /// </summary>
    public const double MaxSinkVolume = 1.5;

    /// <summary>A device volume as pactl takes it, within the range above.</summary>
    internal static string VolumePercent(double volume)
        => $"{(int)Math.Round(Math.Clamp(volume, 0, MaxSinkVolume) * 100)}%";

    private readonly Dictionary<string, long> _directSinkVolumeUntil = new(StringComparer.Ordinal);

    /// <summary>Set a sink's volume (0 to <see cref="MaxSinkVolume"/>).</summary>
    public void SetSinkVolume(string sinkName, double volume)
    {
        string bare = BareSink(sinkName);
        Run("pactl", "set-sink-volume", bare, VolumePercent(volume));
        lock (DumpGate)
        {
            _dumpObjects = null;
            if (!IsOwnSink(bare))
            {
                // A device can quantize or clamp a write. Read that device
                // directly during immediate synchronization.
                long now = Environment.TickCount64;
                foreach (var (name, until) in _directSinkVolumeUntil)
                    if (until <= now) _directSinkVolumeUntil.Remove(name);
                _directSinkVolumeUntil[bare] = now + 1000;
            }
        }
        if (IsOwnSink(bare))
            WaitForSinkLevel(bare, level => Math.Abs(level.DesktopVolume - Math.Round(Math.Clamp(volume, 0, MaxSinkVolume) * 100) / 100) < .005);
    }

    /// <summary>Mute or unmute a sink.</summary>
    public void SetSinkMuted(string sinkName, bool muted)
    {
        string bare = BareSink(sinkName);
        Run("pactl", "set-sink-mute", bare, muted ? "1" : "0");
        InvalidateDump();
        if (IsOwnSink(bare)) WaitForSinkLevel(bare, level => level.Muted == muted);
    }

    private static bool IsOwnSink(string bareName) => bareName.StartsWith("OpenXLR_", StringComparison.Ordinal);

    private void WaitForSinkLevel(string sinkName, Func<OwnSinkLevel, bool> matches)
    {
        PipeWireGraph? graph = _graph;
        if (graph is null || !graph.IsReady) return;
        if (LiveObjects() is { } observed && FindSink(observed, sinkName) is { } current && SinkLevel(current) is null) return;
        // pactl and the registry are separate connections. Give the registry a
        // moment to echo the write so the next sweep does not read the old
        // level as a new desktop change. pactl already applied the write, so
        // a late echo is only that: the sweep after it reads the same value.
        graph.WaitFor(objects => FindSink(objects, sinkName) is { } info && SinkLevel(info) is { } level
                && matches(new OwnSinkLevel(sinkName, level.Volume, level.Muted) { DesktopVolume = level.DesktopVolume }),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Read a sink's mute directly, without a cached graph snapshot.</summary>
    public bool GetSinkMuted(string sinkName)
        => Run("pactl", "get-sink-mute", BareSink(sinkName)).Trim() switch
        {
            "Mute: yes" => true,
            "Mute: no" => false,
            _ => throw new InvalidOperationException("output mute is unavailable"),
        };

    /// <summary>Toggle a sink's mute atomically at pipewire-pulse.</summary>
    public void ToggleSinkMuted(string sinkName)
    {
        Run("pactl", "set-sink-mute", BareSink(sinkName), "toggle");
        InvalidateDump();
    }

    /// <summary>Set one sink-input's volume (used for the combine fader legs).</summary>
    public void SetSinkInputVolume(int index, double volume)
    {
        Run("pactl", "set-sink-input-volume", index.ToString(), $"{SinkInputPercent(volume)}%");
        InvalidateDump();
    }

    /// <summary>The desktop percentage <see cref="SetSinkInputVolume"/> writes for a volume.</summary>
    internal static int SinkInputPercent(double volume) => (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);

    /// <summary>Mute or unmute one sink-input.</summary>
    public void SetSinkInputMuted(int index, bool muted)
    {
        Run("pactl", "set-sink-input-mute", index.ToString(), muted ? "1" : "0");
        InvalidateDump();
    }

    /// <summary>
    /// The internal streams of a combine sink module, keyed by the sink each
    /// feeds. These streams ARE the channel's faders: one per mix, each with its
    /// own volume and mute.
    /// </summary>
    public IReadOnlyDictionary<string, int> FindCombineLegs(uint combineModule)
    {
        var sinkNames = new Dictionary<string, string>();  // index -> name
        foreach (string line in Run("pactl", "list", "sinks", "short").Split('\n'))
        {
            string[] parts = line.Split('\t');
            if (parts.Length >= 2) sinkNames[parts[0]] = parts[1];
        }

        var legs = new Dictionary<string, int>();
        string listing = Run("pactl", "list", "sink-inputs");
        foreach (string block in listing.Split("Sink Input #").Skip(1))
        {
            int nl = block.IndexOf('\n');
            if (nl < 0 || !int.TryParse(block[..nl].Trim(), out int index)) continue;
            if (!block.Contains($"Owner Module: {combineModule}\n") &&
                !block.Contains($"Owner Module: {combineModule}\r")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(block, @"Sink: (\d+)");
            if (m.Success && sinkNames.TryGetValue(m.Groups[1].Value, out string? name))
                legs[name] = index;
        }
        return legs;
    }

    /// <summary>
    /// pipewire-pulse's open files against its soft limit, or null when the
    /// process or its /proc entries cannot be read. Every combine leg, meter
    /// and module costs the PulseAudio server a handful of descriptors, and
    /// systemd's default soft limit of 1024 is reached with a few extra
    /// channels or mixes; past it the server drops nodes at random.
    /// </summary>
    public (int Used, int Limit)? PulseFileUsage()
    {
        try
        {
            // Our own session's server: the one running as our user. On a
            // shared machine another user's pipewire-pulse is not ours to
            // read, and would answer for the wrong session if it were.
            string? uid = ProcUid("/proc/self");
            if (uid is null) return null;
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!Path.GetFileName(dir).All(char.IsAsciiDigit)) continue;
                try
                {
                    if (File.ReadAllText(Path.Combine(dir, "comm")).Trim() != "pipewire-pulse" || ProcUid(dir) != uid) continue;
                    int? limit = File.ReadLines(Path.Combine(dir, "limits"))
                        .Where(line => line.StartsWith("Max open files", StringComparison.Ordinal))
                        .Select(line => line[14..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Where(columns => columns.Length >= 1 && int.TryParse(columns[0], out _))
                        .Select(columns => (int?)int.Parse(columns[0]))
                        .FirstOrDefault();
                    if (limit is null) continue;
                    int used = Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")).Count();
                    return (used, limit.Value);
                }
                catch (Exception) { /* gone meanwhile, or not readable: keep looking */ }
            }
        }
        catch (Exception) { /* no /proc */ }
        return null;
    }

    /// <summary>The real uid of a /proc entry, from its status file.</summary>
    private static string? ProcUid(string procDir)
    {
        try
        {
            return File.ReadLines(Path.Combine(procDir, "status"))
                .Where(line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Select(line => line[4..].Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Whether a sink of this name is published right now (null when pactl is unavailable).</summary>
    public bool? SinkExists(string sinkName)
    {
        if (LiveObjects() is { } objects)
            return FindSink(objects, BareSink(sinkName)) is not null;
        string? sinks = TryRun("pactl", "list", "sinks", "short");
        if (sinks is null) return null;
        return sinks.Split('\n').Any(line => line.Split('\t') is { Length: >= 2 } columns && columns[1] == sinkName);
    }

    /// <summary>
    /// Drop the module bookkeeping without unloading anything: after a
    /// pipewire-pulse restart the modules are gone and their ids are being
    /// handed to other clients, so unloading them would hit someone else.
    /// </summary>
    public void ForgetModules() => _modules.Clear();

    /// <summary>Like <see cref="FindCombineLegs"/>, but null when pactl fails or stalls.</summary>
    public IReadOnlyDictionary<string, int>? TryFindCombineLegs(uint combineModule)
    {
        try { return FindCombineLegs(combineModule); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The serials of every stream currently playing into a sink.</summary>
    public IReadOnlyList<int> StreamSerialsOnSink(string sinkName)
    {
        string sinks = TryRun("pactl", "list", "sinks", "short") ?? "";
        string? sinkId = sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 2 && columns[1] == sinkName)
            .Select(columns => columns[0])
            .FirstOrDefault();
        if (sinkId is null) return [];
        return [.. (TryRun("pactl", "list", "sink-inputs", "short") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 2 && columns[1] == sinkId && int.TryParse(columns[0], out _))
            .Select(columns => int.Parse(columns[0]))];
    }

    /// <summary>A sink's desktop volume, within the supported output range.</summary>
    public double? GetSinkVolume(string sinkName)
    {
        string bare = BareSink(sinkName);
        JsonElement[]? observed;
        lock (DumpGate)
            observed = _directSinkVolumeUntil.TryGetValue(bare, out long until) && Environment.TickCount64 < until
                ? null : LiveObjects();
        if (observed is { } objects)
        {
            if (FindSink(objects, bare) is not { } info) return null;
            if (SinkLevel(info, firstChannel: true) is { } level) return Math.Clamp(level.DesktopVolume, 0, MaxSinkVolume);
            // Some sinks expose no readable Props volume. Pulse can still
            // provide one; keep their controls working through the old query.
        }
        return ParseVolumePercent(TryRun("pactl", "get-sink-volume", bare));
    }

    /// <summary>
    /// Force a sink's hardware stream to close and reopen (brief audio gap on
    /// that device). The Wave XLR Pro latches its aux-return routing at
    /// playback-stream start, so enabling the aux output on a running stream
    /// needs this bounce to take effect.
    /// </summary>
    public void BounceSink(string sinkName)
    {
        string bare = BareSink(sinkName);
        try
        {
            Run("pactl", "suspend-sink", bare, "1");
            Thread.Sleep(300);
            Run("pactl", "suspend-sink", bare, "0");
        }
        catch (InvalidOperationException) { /* device gone */ }
    }

    /// <summary>
    /// The named sources' volumes, base volumes and hardware flags, read in
    /// one pactl call; null when pactl fails or answers with something that
    /// is not a source list. A percentage would round a volume to steps of
    /// up to a decibel, so the raw volumes are read.
    /// </summary>
    public IReadOnlyDictionary<string, SourceVolume>? GetSourceVolumes(IReadOnlySet<string> names)
    {
        if (names.Count == 0) return new Dictionary<string, SourceVolume>(StringComparer.Ordinal);
        try { return CaptureGain.ParseSources(RunBytes("pactl", 4 * 1024 * 1024, "--format=json", "list", "sources"), names); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException) { return null; }
    }

    /// <summary>
    /// Set a source's volume to a raw Pulse volume (65536 is 0 dB), on every
    /// channel. An absolute number, since pactl reads a leading sign as a
    /// change relative to the current volume.
    /// </summary>
    public void SetSourceVolume(string sourceName, long raw)
    {
        Run("pactl", "set-source-volume", sourceName, Math.Max(0, raw).ToString(CultureInfo.InvariantCulture));
        InvalidateDump();
    }

    /// <summary>
    /// The named sources' channel volumes and mutes as the registry holds
    /// them, one line per source, for telling cheaply whether one changed;
    /// null while the registry cannot be read.
    /// </summary>
    public string? SourceVolumeSignature(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return "";
        JsonElement[] objects;
        try { objects = GraphObjects(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException) { return null; }
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("info", out JsonElement info) || !info.TryGetProperty("props", out JsonElement props)
                || !props.TryGetProperty("node.name", out JsonElement n) || n.GetString() is not { } name || !names.Contains(name)
                || !props.TryGetProperty("media.class", out JsonElement m) || m.GetString() is not ("Audio/Source" or "Audio/Source/Virtual")) continue;
            string level = info.TryGetProperty("params", out JsonElement pars) && pars.TryGetProperty("Props", out JsonElement list)
                && list.ValueKind == JsonValueKind.Array && list.EnumerateArray().FirstOrDefault(p => p.TryGetProperty("channelVolumes", out _)) is
                    { ValueKind: JsonValueKind.Object } p
                ? p.GetProperty("channelVolumes").GetRawText() + (p.TryGetProperty("mute", out JsonElement mute) ? mute.GetRawText() : "")
                : "";
            lines[name] = lines.ContainsKey(name) ? "twice" : level;
        }
        return string.Join('\n', names.Order(StringComparer.Ordinal).Select(name => $"{name}\t{lines.GetValueOrDefault(name, "missing")}"));
    }

    /// <summary>The controls of one ALSA card as <c>amixer -c N contents</c> prints them, or null.</summary>
    public static string? AlsaControls(string card) => AlsaControls(card, out _);

    /// <summary>
    /// The same, saying whether amixer is missing altogether: that answer
    /// stays true until the user installs it, unlike a failed run.
    /// </summary>
    public static string? AlsaControls(string card, out bool noAmixer)
    {
        noAmixer = false;
        try
        {
            ProcessResult result = ProcessRunner.Run("amixer", ["-c", card, "contents"], TimeSpan.FromSeconds(2),
                stdoutCap: 1024 * 1024, stderrCap: 64 * 1024);
            return result is { TimedOut: false, Truncated: false, Incomplete: false, ExitCode: 0 } ? result.StdoutText : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            noAmixer = true;
            return null;
        }
    }

    private string? TryRun(string exe, params string[] args)
    {
        try { return Run(exe, args); }
        catch (InvalidOperationException) { return null; }
    }

    internal static double? ParseVolumePercent(string? pactlOutput)
    {
        if (pactlOutput is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(pactlOutput, @"(\d+)%");
        return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value) / 100.0, 0, MaxSinkVolume) : null;
    }

    /// <summary>The capture device applications record from by default.</summary>
    public void SetDefaultSource(string sourceName)
    {
        Run("pactl", "set-default-source", sourceName);
        InvalidateObservedDefaults();
    }

    /// <summary>The playback device applications use by default.</summary>
    public void SetDefaultSink(string sinkName)
    {
        Run("pactl", "set-default-sink", sinkName);
        InvalidateObservedDefaults();
    }

    /// <summary>Current default playback device, or null.</summary>
    public string? GetDefaultSink()
    {
        if (ReadObservedDefaults() is { } defaults) return defaults.Sink;
        try { return Run("pactl", "get-default-sink").Trim(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Current default capture device, or null.</summary>
    public string? GetDefaultSource()
    {
        if (ReadObservedDefaults() is { } defaults) return defaults.Source;
        try { return Run("pactl", "get-default-source").Trim(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Publish a sink's monitor as a cleanly-named capture device.</summary>
    public uint CreateVirtualMic(string sourceName, string masterMonitor, string description)
    {
        // The master monitor source registers asynchronously after its sink is
        // created; loading the remap before it exists fails with EINVAL. Seen
        // only on a freshly restarted PipeWire, so wait for it briefly.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            string sources = TryRun("pactl", "list", "sources", "short") ?? "";
            if (sources.Contains(masterMonitor, StringComparison.Ordinal)) break;
            Thread.Sleep(200);
        }
        string outp = Run("pactl",
            "load-module", "module-remap-source",
            $"source_name={sourceName}",
            $"master={masterMonitor}",
            // Both properties: apps read one or the other depending on the API.
            // Low priority.session so WirePlumber never promotes a virtual mic
            // to system default capture on its own.
            "source_properties=" + PropList($"device.description={PropValue(description)}" +
            $" node.description={PropValue(description)} priority.session=100 node.virtual=false"));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// Spawn a fader: carries <paramref name="fromSink"/>'s monitor into
    /// <paramref name="toSink"/>. The returned playback node name is what
    /// <see cref="SetLoopbackVolume"/> addresses. Set
    /// <paramref name="fromIsSource"/> when the origin is a real capture device
    /// (a microphone) rather than a sink whose monitor is being tapped.
    /// </summary>
    public LoopbackHandle CreateLoopback(string id, string fromSink, string toSink, double volume,
        bool fromIsSource = false)
    {
        string capName = $"OpenXLR_lb_{id}_cap";
        string playName = $"OpenXLR_lb_{id}_play";
        var psi = new ProcessStartInfo("pw-loopback")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Deliberately NOT node.passive: a passive node does not drive the graph,
        // and with every hop passive (channel -> mix -> output) nothing pulls the
        // audio through, so the chain goes silent and idle sinks get suspended.
        // Verified by ear: the same audio passes through a non-passive loopback
        // and does not through a passive one.
        psi.ArgumentList.Add("--capture-props=" +
            $"node.name={capName} target.object={fromSink}" +
            (fromIsSource ? "" : " stream.capture.sink=true"));
        psi.ArgumentList.Add("--playback-props=" +
            $"node.name={playName} target.object={toSink}");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start pw-loopback");
        _ = p.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
        _loopbacks.Add(p);

        var handle = new LoopbackHandle(id, capName, playName, p);
        // Wait for the node to appear, then apply the fader level.
        if (WaitForNode(playName, TimeSpan.FromSeconds(3)))
            SetLoopbackVolume(handle, volume);
        return handle;
    }

    /// <summary>
    /// Insert the mono mic filter (the software DSP for devices whose own
    /// DSP lives host-side): an optional high-pass (the low cut) and an
    /// optional post-ADC hard limiter (ClipGuard; LADSPA hardLimiter from
    /// swh-plugins), chained when both are active. module-filter-chain is
    /// not reachable through the PulseAudio emulation, so it is loaded by a
    /// held "pw-cli -m" process: the module lives exactly as long as the
    /// process (kill = unload). The filter appears as a sink half (feed the
    /// mic into it) and a source half (link onward to the channel).
    /// </summary>
    public FilterHandle CreateMicFilter(string id, int lowCutHz, bool clipGuard,
        IReadOnlyList<InsertDefinition>? inserts = null, int channels = 1)
        => CreateFilterChain($"OpenXLR_lc_{id}_in", $"OpenXLR_lc_{id}_out", "OpenXLR Mic Filter", channels, lowCutHz, clipGuard, inserts);

    /// <summary>
    /// The Sound Check loop for one microphone channel: the native helper's
    /// mono "soundcheck" backend, one input, its main output for the chain
    /// and its tap output past the chain, under a fixed node name. Refused at
    /// once when the helper is not installed, since a build without it has
    /// nothing that could answer. <paramref name="quantum"/> is the graph's
    /// quantum in frames from the same settings, or null when they name none.
    /// </summary>
    internal FilterHandle CreateSoundCheck(string id, out int rate, out int? quantum, string? executable = null)
    {
        executable ??= NativePluginHost.Executable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.SoundCheckNeedsHost);
        string settings = Run("pw-metadata", "-n", "settings");
        rate = ParseGraphSampleRate(settings);
        quantum = ParseGraphQuantum(settings);
        string node = "OpenXLR_soundcheck_" + id;
        NativePluginHost host;
        try
        {
            host = new NativePluginHost(new() { Id = id, Kind = "soundcheck", Plugin = "soundcheck" },
                node, 1, rate, executable, [], meterSymbols: SoundCheckMeters);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Sound Check could not start the native plugin host. {ex.Message}", ex);
        }
        _nativeHosts.Add(host);
        var filter = new FilterHandle(node, node, node, host.Process) { NativeHost = host };
        if (!WaitForPorts(node, "playback", false, TimeSpan.FromSeconds(3), host.Process)
            || !WaitForPorts(node, "capture", true, TimeSpan.FromSeconds(3), host.Process)
            || !WaitForPorts(node, "tap", true, TimeSpan.FromSeconds(3), host.Process))
        {
            StopFilter(filter);
            throw new InvalidOperationException("Sound Check audio ports did not appear.");
        }
        return filter;
    }

    /// <summary>How many peaks the Sound Check helper reports for one take.</summary>
    internal const int TakePeakCount = 200;

    /// <summary>The meters the Sound Check helper reports: progress, the playhead, and the take's peaks with their count.</summary>
    internal static readonly IReadOnlySet<string> SoundCheckMeters = new HashSet<string>(
        ["frames", "mode", "cursor", "takeFrames", "take", .. Enumerable.Range(0, TakePeakCount).Select(i => $"p{i}")], StringComparer.Ordinal);

    /// <summary>
    /// The level meter Mic setup reads ahead of a microphone's chain: the
    /// native helper's mono "level" backend under a fixed node name, fed by
    /// the caller from the channel's own capture port, its output left
    /// unlinked. Refused at once without the helper.
    /// </summary>
    internal FilterHandle CreateInputMeter(string id, string? executable = null)
    {
        executable ??= NativePluginHost.Executable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.SoundCheckNeedsHost);
        int rate = ParseGraphSampleRate(Run("pw-metadata", "-n", "settings"));
        string node = "OpenXLR_level_" + id;
        NativePluginHost host;
        try
        {
            host = new NativePluginHost(new() { Id = id, Kind = "level", Plugin = "level" },
                node, 1, rate, executable, [], meterSymbols: new HashSet<string>(StringComparer.Ordinal) { "peak", "rms", "clips" });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"The input meter could not start the native plugin host. {ex.Message}", ex);
        }
        _nativeHosts.Add(host);
        var filter = new FilterHandle(node, node, node, host.Process) { NativeHost = host };
        if (!WaitForPorts(node, "playback", false, TimeSpan.FromSeconds(3), host.Process))
        {
            StopFilter(filter);
            throw new InvalidOperationException("The input meter's audio port did not appear.");
        }
        return filter;
    }

    /// <summary>
    /// The native helper the microphone EQ runs in. Null means the one
    /// installed beside the daemon; a test points it elsewhere to see what a
    /// build without the helper does.
    /// </summary>
    internal string? EqHost { get; set; }

    /// <summary>Whether the microphone EQ's helper is there to start.</summary>
    internal bool EqHostInstalled => File.Exists(EqHost ?? NativePluginHost.Executable);

    // The engines each helper build said it carries, by its path and time.
    private readonly Dictionary<string, IReadOnlyList<string>> _noiseEngines = new(StringComparer.Ordinal);

    /// <summary>
    /// The noise removal engines the microphone stage's helper carries, as its
    /// <c>engines</c> command reports them, in <see cref="MicNoise.Engines"/>
    /// order. Asked once per build of the helper; empty without a helper and
    /// from one that does not answer.
    /// </summary>
    internal IReadOnlyList<string> NoiseEngines
    {
        get
        {
            string executable = EqHost ?? NativePluginHost.Executable;
            DateTime built;
            try
            {
                if (!File.Exists(executable)) return [];
                built = File.GetLastWriteTimeUtc(executable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            string key = executable + "\n" + built.Ticks.ToString(CultureInfo.InvariantCulture);
            lock (_noiseEngines)
            {
                if (_noiseEngines.TryGetValue(key, out IReadOnlyList<string>? known)) return known;
                IReadOnlyList<string> engines = [];
                try
                {
                    ProcessResult result = ProcessRunner.Run(executable, ["engines"], TimeSpan.FromSeconds(3), stdoutCap: 4096);
                    if (result.ExitCode == 0) engines = ParseNoiseEngines(System.Text.Encoding.UTF8.GetString(result.Stdout));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
                _noiseEngines[key] = engines;
                return engines;
            }
        }
    }

    /// <summary>
    /// The engines in the helper's <c>engines</c> line, a JSON object whose
    /// <c>denoise</c> array names them; names this daemon does not know are
    /// left out, and anything unreadable is no engine at all.
    /// </summary>
    internal static IReadOnlyList<string> ParseNoiseEngines(string? output)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output ?? "");
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("denoise", out var list)
                || list.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            var named = list.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
            return [.. MicNoise.Engines.Where(named.Contains)];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>The soundboard player's helper; null uses the installed helper beside the daemon.</summary>
    internal string? PlayerHost { get; set; }

    internal string PlayerExecutable => PlayerHost ?? NativePluginHost.Executable;

    internal bool PlayerHostInstalled => File.Exists(PlayerExecutable);

    /// <summary>The soundboard player's node: one per daemon, named after the channel it plays into.</summary>
    internal const string PlayerNode = "OpenXLR_player_" + Mixer.SoundboardChannel;

    /// <summary>
    /// The player's meters: voices playing each slot, how far the newest is,
    /// the newest press whose hold voice has ended, and the slot the
    /// audition plays, plus one.
    /// </summary>
    internal static readonly IReadOnlySet<string> PlayerMeters = new HashSet<string>(
        [.. Enumerable.Range(0, SoundboardLibrary.MaxClips).SelectMany(i => new[] { $"playing_{i}", $"pos_{i}", $"ended_{i}" }), AuditionMeter],
        StringComparer.Ordinal);

    /// <summary>The player's meter for the audition: the slot it plays, plus one, or 0.</summary>
    internal const string AuditionMeter = "audition";

    /// <summary>The prefix of the player's audition pair, <c>audition_0</c> and <c>audition_1</c>.</summary>
    internal const string AuditionPorts = "audition";

    /// <summary>
    /// The soundboard player: the helper's stereo "player" backend under
    /// <see cref="PlayerNode"/>, started with each slot's controls at
    /// <paramref name="controls"/>. Its outputs, <c>capture_0</c> and
    /// <c>capture_1</c>, and its audition pair, <c>audition_0</c> and
    /// <c>audition_1</c>, are linked by the caller; its inputs stay unlinked.
    /// Refused at once without the helper.
    /// </summary>
    internal FilterHandle CreatePlayer(IReadOnlyDictionary<string, double> controls, out int rate)
    {
        string executable = PlayerExecutable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.SoundboardNeedsHost);
        rate = ParseGraphSampleRate(Run("pw-metadata", "-n", "settings"));
        NativePluginHost host;
        try
        {
            host = new NativePluginHost(new() { Id = "soundboard", Kind = "player", Plugin = "player", Params = new(controls) },
                PlayerNode, 2, rate, executable, [], meterSymbols: PlayerMeters, note: _note);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"The soundboard could not start the native plugin host. {ex.Message}", ex);
        }
        _nativeHosts.Add(host);
        var filter = new FilterHandle(PlayerNode, PlayerNode, PlayerNode, host.Process) { NativeHost = host };
        try
        {
            if (!WaitForPorts(PlayerNode, "capture", true, TimeSpan.FromSeconds(3), host.Process, count: 2)
                || !WaitForPorts(PlayerNode, AuditionPorts, true, TimeSpan.FromSeconds(3), host.Process, count: 2))
                throw new InvalidOperationException("The soundboard player's audio ports did not appear.");
            return filter;
        }
        catch
        {
            StopFilter(filter);
            throw;
        }
    }

    // The formats each helper build said it decodes, by its path and time.
    private readonly Dictionary<string, IReadOnlyList<string>> _playerFormats = new(StringComparer.Ordinal);

    /// <summary>
    /// The clip formats the player's helper decodes, as its <c>formats</c>
    /// command reports them. Asked once per build of the helper; empty
    /// without a helper and from one that does not answer.
    /// </summary>
    internal IReadOnlyList<string> PlayerFormats
    {
        get
        {
            string executable = PlayerExecutable;
            DateTime built;
            try
            {
                if (!File.Exists(executable)) return [];
                built = File.GetLastWriteTimeUtc(executable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            string key = executable + "\n" + built.Ticks.ToString(CultureInfo.InvariantCulture);
            lock (_playerFormats)
            {
                if (_playerFormats.TryGetValue(key, out IReadOnlyList<string>? known)) return known;
                IReadOnlyList<string> formats = [];
                try
                {
                    ProcessResult result = ProcessRunner.Run(executable, ["formats"], TimeSpan.FromSeconds(3), stdoutCap: 4096);
                    if (result.ExitCode == 0) formats = ParsePlayerFormats(System.Text.Encoding.UTF8.GetString(result.Stdout));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
                _playerFormats[key] = formats;
                return formats;
            }
        }
    }

    /// <summary>The formats in the helper's <c>formats</c> line that the soundboard knows, in its order.</summary>
    internal static IReadOnlyList<string> ParsePlayerFormats(string? output)
    {
        try
        {
            using var document = JsonDocument.Parse(output ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("formats", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array) return [];
            var named = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
            return [.. SoundboardLibrary.Extensions.Where(named.Contains)];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Check a clip file in a helper process of its own, so a file that
    /// crashes the decoder never reaches the running player. Throws with the
    /// helper's sentence when the file is refused.
    /// </summary>
    internal ClipProbe ProbeClip(string path)
    {
        string executable = PlayerExecutable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.SoundboardNeedsHost);
        ProcessResult result;
        try { result = ProcessRunner.Run(executable, ["probe", path], TimeSpan.FromSeconds(20), stdoutCap: 4096, stderrCap: 4096); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"The file could not be checked: {ex.Message}", ex);
        }
        if (result.TimedOut) throw new InvalidOperationException("The file could not be checked in time.");
        return ParseProbe(result.StdoutText)
            ?? throw new InvalidOperationException(result.ExitCode == 0 || result.Stdout.Length > 0
                ? "The file could not be checked." : "The file could not be checked; the decoder stopped.");
    }

    /// <summary>
    /// The clip a <c>probe ok FORMAT CHANNELS RATE FRAMES</c> line describes.
    /// A <c>probe error MESSAGE</c> line throws with the message; anything
    /// else is null.
    /// </summary>
    internal static ClipProbe? ParseProbe(string? output)
    {
        string line = (output ?? "").Split('\n')[0].TrimEnd('\r');
        string[] parts = line.Split(' ');
        if (parts.Length >= 3 && parts[0] == "probe" && parts[1] == "error")
        {
            string message = new string([.. line["probe error ".Length..].Take(200).Where(c => !char.IsControl(c))]).Trim().TrimEnd('.');
            throw new InvalidOperationException(message.Length == 0 ? "The file was refused."
                : char.ToUpperInvariant(message[0]) + message[1..] + ".");
        }
        if (parts.Length != 6 || parts[0] != "probe" || parts[1] != "ok") return null;
        string format = parts[2];
        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int channels) || channels is not (1 or 2)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int rate) || rate <= 0
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long frames) || frames <= 0)
            return null;
        string extension = format is "other" ? "audio" : format;
        if (!SoundboardLibrary.Extensions.Contains(extension, StringComparer.Ordinal)) return null;
        return new ClipProbe(extension, channels, rate, frames);
    }

    /// <summary>The duck stage's helper; null uses the installed helper beside the daemon.</summary>
    internal string? DuckHost { get; set; }

    internal bool DuckHostInstalled => File.Exists(DuckHost ?? NativePluginHost.Executable);

    /// <summary>One stereo duck stage with a separate mono key and a reduction meter in dB.</summary>
    internal FilterHandle CreateDucker(string mixId, IReadOnlyDictionary<string, double> controls)
    {
        string executable = DuckHost ?? NativePluginHost.Executable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.DuckingNeedsHost);
        int rate = ParseGraphSampleRate(Run("pw-metadata", "-n", "settings"));
        string node = new MixDefinition(mixId, mixId, MixKind.Monitor).DuckStageName;
        NativePluginHost host;
        try
        {
            host = new NativePluginHost(new() { Id = mixId, Kind = "duck", Plugin = "duck", Params = new(controls) },
                node, 2, rate, executable, [], meterSymbols: new HashSet<string>(StringComparer.Ordinal) { "reduction" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Ducking could not start the native plugin host. {ex.Message}", ex);
        }
        _nativeHosts.Add(host);
        var filter = new FilterHandle(node, node, node, host.Process) { NativeHost = host };
        try
        {
            if (!WaitForPorts(node, "playback", false, TimeSpan.FromSeconds(3), host.Process, count: 2)
                || !WaitForPorts(node, "capture", true, TimeSpan.FromSeconds(3), host.Process, count: 2)
                || !WaitForPorts(node, "key", false, TimeSpan.FromSeconds(3), host.Process))
                throw new InvalidOperationException("Ducking's audio ports did not appear.");
            return filter;
        }
        catch
        {
            StopFilter(filter);
            throw;
        }
    }

    /// <summary>
    /// One microphone's EQ stage: the native helper's mono "eq" backend under
    /// <c>OpenXLR_eq_&lt;id&gt;</c>, started with every control at
    /// <paramref name="controls"/>, so it plays the right curve from its
    /// first block. Refused at once when the helper is not installed.
    /// </summary>
    internal FilterHandle CreateMicEq(string id, IReadOnlyDictionary<string, double> controls, int bands = MicEq.BandCount)
    {
        if (bands is < MicEq.MinimumBands or > MicEq.MaximumBands) throw new ArgumentOutOfRangeException(nameof(bands));
        string executable = EqHost ?? NativePluginHost.Executable;
        if (!File.Exists(executable)) throw new InvalidOperationException(Mixer.EqNeedsHost);
        int rate = ParseGraphSampleRate(Run("pw-metadata", "-n", "settings"));
        string node = "OpenXLR_eq_" + id;
        NativePluginHost host;
        try
        {
            host = new NativePluginHost(new() { Id = id, Kind = "eq", Plugin = bands.ToString(CultureInfo.InvariantCulture), Params = new(controls) },
                node, 1, rate, executable, [], meterSymbols: SoftwareEqBackend.Meters,
                // The stage's trace lines say when it replaced non-finite
                // samples or cleared a state; they belong in the journal.
                note: _note);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"The microphone EQ could not start the native plugin host. {ex.Message}", ex);
        }
        _nativeHosts.Add(host);
        var filter = new FilterHandle(node, node, node, host.Process) { NativeHost = host };
        if (!WaitForPorts(node, "playback", false, TimeSpan.FromSeconds(3), host.Process)
            || !WaitForPorts(node, "capture", true, TimeSpan.FromSeconds(3), host.Process))
        {
            StopFilter(filter);
            throw new InvalidOperationException("The microphone EQ's audio ports did not appear.");
        }
        return filter;
    }

    /// <summary>A stereo insert chain for a mix, spliced between the mix and its consumers.</summary>
    public FilterHandle CreateMixChain(string id, string description, IReadOnlyList<InsertDefinition> inserts)
        => CreateFilterChain($"OpenXLR_ins_{id}_in", $"OpenXLR_ins_{id}_out", description, 2, 0, false, inserts);

    /// <summary>
    /// Build and hold a filter-chain: the builtin low cut and LADSPA limiter
    /// (mono chains only), then the LV2 inserts as stages i0, i1, ... in
    /// order, each channel linked stage to stage. Mono chains link a plugin's
    /// first audio in and out; stereo chains link its first two.
    /// </summary>
    private FilterHandle CreateFilterChain(string sinkName, string srcName, string description, int channels,
        int lowCutHz, bool clipGuard, IReadOnlyList<InsertDefinition>? inserts)
    {
        if (inserts?.Any(i => !i.Bypass && (i.NeedsNativeHost || NeedsLatencyHost(i))) == true)
            return CreateHostedChain(sinkName, srcName, description, channels, lowCutHz, clipGuard, inserts);
        if (clipGuard)
        {
            DspFeatureAvailability support = GetSoftwareClipGuardAvailability();
            if (!support.Available) throw new InvalidOperationException(support.Error);
        }

        // Every stage in chain order as (node definition, input ports, output ports), one port per channel.
        var stages = new List<(string Node, string[] In, string[] Out)>();
        if (channels == 1 && lowCutHz > 0)
            stages.Add(($"{{ type = builtin name = hp label = bq_highpass control = {{ \"Freq\" = {lowCutHz}.0 }} }}", ["hp:In"], ["hp:Out"]));
        if (channels == 1 && clipGuard)
            stages.Add(("{ type = ladspa name = lim plugin = hard_limiter_1413 label = hardLimiter " +
                "control = { \"dB limit\" = -3.0 \"Wet level\" = 1.0 \"Residue level\" = 0.0 } }", ["lim:Input"], ["lim:Output"]));
        int k = 0;
        foreach (InsertDefinition ins in inserts ?? [])
        {
            if (ins.Bypass || ins.Kind != "lv2") continue;
            PluginInfo? info = PluginCatalog.Find(ins);
            if (info is null || info.InputSymbols.Count < channels || info.OutputSymbols.Count < channels)
                continue;   // unknown or wrong-width plugin: skipped, reported by the caller
            string name = $"i{k++}";
            string controls = ins.Params.Count == 0 ? "" :
                " control = { " + string.Join(' ', ins.Params.Select(p => $"\"{SpaString(p.Key)}\" = {p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + " }";
            // The port symbols come from the bundle's metadata, quoted below
            // in the links and the inputs and outputs lists.
            stages.Add(($"{{ type = lv2 name = {name} plugin = \"{SpaString(ins.Plugin)}\"{controls} }}",
                [.. info.InputSymbols.Take(channels).Select(s => $"{name}:{SpaString(s)}")],
                [.. info.OutputSymbols.Take(channels).Select(s => $"{name}:{SpaString(s)}")]));
        }
        if (stages.Count == 0)
            throw new ArgumentException("filter chain needs at least one stage");
        var linkList = new List<string>();
        for (int s = 1; s < stages.Count; s++)
            for (int c = 0; c < channels; c++)
                linkList.Add($"{{ output = \"{stages[s - 1].Out[c]}\" input = \"{stages[s].In[c]}\" }}");
        string links = linkList.Count == 0 ? "" : "links = [ " + string.Join(' ', linkList) + " ] ";
        string inputs = string.Join(' ', stages[0].In.Select(p => $"\"{p}\""));
        string outputs = string.Join(' ', stages[^1].Out.Select(p => $"\"{p}\""));
        string position = channels == 1 ? "[ MONO ]" : "[ FL FR ]";
        string spa =
            $"{{ node.description = \"{SpaString(description)}\" " +
            $"filter.graph = {{ nodes = [ {string.Join(' ', stages.Select(s => s.Node))} ] {links}" +
            $"inputs = [ {inputs} ] outputs = [ {outputs} ] }} " +
            $"capture.props = {{ node.name = {sinkName} media.class = Stream/Input/Audio " +
            $"audio.channels = {channels} audio.position = {position} node.suspend-on-idle = false " +
            $"priority.session = 100 {InternalFilterProperties} }} " +
            $"playback.props = {{ node.name = {srcName} media.class = Stream/Output/Audio " +
            $"audio.channels = {channels} audio.position = {position} node.suspend-on-idle = false " +
            $"priority.session = 100 {InternalFilterProperties} }} }}";
        try { return StartFilterChain(sinkName, srcName, spa); }
        catch (InvalidOperationException failure) when (NativeFallbackFor(inserts, channels) is { } active)
        {
            return CreateFallbackChain(sinkName, srcName, description, channels, lowCutHz, clipGuard, active, failure);
        }
    }

    /// <summary>
    /// The active inserts of a filter chain PipeWire refused, when the native
    /// host can take them over: the helper is installed, every active insert
    /// is a known LV2 plugin of the chain's width, and at least one of them
    /// needs nothing the helper lacks. Null otherwise, and the refusal stands.
    /// </summary>
    private static IReadOnlyList<InsertDefinition>? NativeFallbackFor(IReadOnlyList<InsertDefinition>? inserts, int channels)
    {
        var active = inserts?.Where(i => !i.Bypass).ToArray() ?? [];
        if (active.Length == 0 || !NativePluginHost.HostInstalled) return null;
        var plugins = active.Select(i => i.Kind == "lv2" ? PluginCatalog.Find(i) : null).ToArray();
        if (plugins.Any(p => p is null || p.InputSymbols.Count < channels || p.OutputSymbols.Count < channels)) return null;
        return plugins.Any(p => NativePluginHost.SupportsFeatures(p!.RequiredFeatures)) ? active : null;
    }

    /// <summary>
    /// Rebuild a chain PipeWire's filter chain refused, deciding each insert
    /// on its own. A lone insert moves to the native host. Several inserts
    /// each get their own filter-chain stage first, so only a stage PipeWire
    /// refuses in turn moves to the native host. The saved host choice is
    /// not changed: the next build tries the filter chain again.
    /// </summary>
    private FilterHandle CreateFallbackChain(string sinkName, string srcName, string description, int channels,
        int lowCutHz, bool clipGuard, IReadOnlyList<InsertDefinition> active, InvalidOperationException failure)
    {
        InsertDefinition? lone = active.Count == 1 ? active[0] : null;
        if (lone is not null && !NativePluginHost.SupportsFeatures(PluginCatalog.Find(lone)!.RequiredFeatures))
            throw failure;
        try
        {
            var chain = CreateHostedChain(sinkName, srcName, description, channels, lowCutHz, clipGuard, active,
                lone is null ? null : (lone.Id, failure.Message));
            if (lone is not null)
                Note($"PipeWire's filter chain could not load {InsertName(lone)}, so the native host runs it. {failure.Message}");
            return chain;
        }
        catch (Exception fallback)
        {
            throw new InvalidOperationException($"{failure.Message} Retrying with the native host failed: {fallback.Message}", fallback);
        }
    }

    private static string InsertName(InsertDefinition insert)
        => string.IsNullOrWhiteSpace(insert.Label) ? insert.Plugin : insert.Label;

    /// <summary>Load a filter-chain module from its SPA description and wait for both halves.</summary>
    private FilterHandle StartFilterChain(string sinkName, string srcName, string spa)
    {
        var p = Process.Start(FilterChainProcess(spa)) ?? throw new InvalidOperationException("failed to start pw-cli");
        // pw-cli -m intentionally lives for the module's lifetime. Drain both
        // pipes continuously: leaving redirected output unread can fill the OS
        // pipe and freeze the holder process (and every graph operation waiting
        // behind the mixer's lock).
        // pw-cli -m reports every registry event for as long as it lives, so
        // the pipes are drained for its lifetime but only the first few KB
        // are kept, which is where a load failure explains itself.
        Task<string> stdoutTask = DrainKeepingHead(p.StandardOutput.BaseStream);
        Task<string> stderrTask = DrainKeepingHead(p.StandardError.BaseStream);
        _filters.Add(p);
        var handle = new FilterHandle(sinkName, sinkName, srcName, p);
        // Ports lag node registration; linking a port-less node silently
        // yields an empty PortLink. A dead holder is detected immediately, and
        // a half-created module is removed instead of being returned as usable.
        bool sinkReady, sourceReady;
        try
        {
            sinkReady = WaitForPorts(sinkName, "playback", output: false, TimeSpan.FromSeconds(3), p);
            sourceReady = sinkReady && WaitForPorts(srcName, "capture", output: true, TimeSpan.FromSeconds(3), p);
        }
        catch
        {
            // No caller owns the holder until its ports have been found.
            StopFailedFilter(handle, stdoutTask, stderrTask);
            throw;
        }
        if (!sinkReady || !sourceReady)
        {
            string detail = StopFailedFilter(handle, stdoutTask, stderrTask);
            string missing = !sinkReady ? sinkName : srcName;
            throw new InvalidOperationException(
                $"PipeWire filter chain did not create the required ports for {missing}" +
                (detail.Length == 0 ? "" : $": {detail}"));
        }
        return handle;
    }

    /// <summary>
    /// The pw-cli process that holds a filter-chain module. The module loads
    /// LV2 plugins inside it, so it gets the LV2_PATH the catalogue was read with.
    /// </summary>
    internal static ProcessStartInfo FilterChainProcess(string spa)
    {
        var psi = new ProcessStartInfo("pw-cli")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("load-module");
        psi.ArgumentList.Add("libpipewire-module-filter-chain");
        psi.ArgumentList.Add(spa);
        PluginSearchPaths.ApplyLv2(psi);
        return psi;
    }

    /// <summary>
    /// A stereo delay after a mix, for latency compensation: one builtin
    /// delay line per side, up to the compensation limit, starting at zero.
    /// Its nodes are hidden from the desktop's device lists.
    /// </summary>
    internal FilterHandle CreateMixDelay(string id)
    {
        string sink = $"OpenXLR_delay_{id}_in", source = $"OpenXLR_delay_{id}_out";
        string max = (MixLatency.MaxMilliseconds / 1000).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string spa = "{ node.description = \"OpenXLR mix latency delay\" " +
            "filter.graph = { nodes = [ " +
            $"{{ type = builtin name = left label = delay config = {{ max-delay = {max} }} control = {{ \"Delay (s)\" = 0.0 }} }} " +
            $"{{ type = builtin name = right label = delay config = {{ max-delay = {max} }} control = {{ \"Delay (s)\" = 0.0 }} }} ] " +
            "inputs = [ \"left:In\" \"right:In\" ] outputs = [ \"left:Out\" \"right:Out\" ] } " +
            $"capture.props = {{ node.name = {sink} media.class = Stream/Input/Audio audio.channels = 2 audio.position = [ FL FR ] node.suspend-on-idle = false {InternalFilterProperties} }} " +
            $"playback.props = {{ node.name = {source} media.class = Stream/Output/Audio audio.channels = 2 audio.position = [ FL FR ] node.suspend-on-idle = false {InternalFilterProperties} }} }}";
        return StartFilterChain(sink, source, spa);
    }

    /// <summary>Set both sides of a mix delay, live, without rebuilding anything.</summary>
    internal void SetMixDelay(FilterHandle filter, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds is < 0 or > MixLatency.MaxMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        int id = FindNodeId(filter.SinkName) ?? throw new InvalidOperationException("The mix delay is not in the graph.");
        string value = (milliseconds / 1000).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Run("pw-cli", "set-param", id.ToString(), "Props", $"{{ params = [ \"left:Delay (s)\" {value} \"right:Delay (s)\" {value} ] }}");
    }

    /// <summary>
    /// Splice native editors into the chain, retaining filter-chain for other
    /// plugins. <paramref name="fallback"/> names an LV2 insert PipeWire's
    /// filter chain refused, run in the native host whatever its saved choice,
    /// with the refusal kept on its stage for the insert's status.
    /// </summary>
    private FilterHandle CreateHostedChain(string sinkName, string sourceName, string description, int channels,
        int lowCutHz, bool clipGuard, IReadOnlyList<InsertDefinition> inserts,
        (string Id, string Reason)? fallback = null)
    {
        var stages = new List<FilterHandle>();
        var insertStages = new List<(string Id, FilterHandle Stage)>();
        try
        {
            if (lowCutHz > 0 || clipGuard)
                stages.Add(CreateFilterChain(sinkName + "_safety", sourceName + "_safety", description,
                    channels, lowCutHz, clipGuard, null));
            int rate = ParseGraphSampleRate(Run("pw-metadata", "-n", "settings"));
            foreach (InsertDefinition insert in inserts.Where(i => !i.Bypass))
            {
                PluginInfo? info = PluginCatalog.Find(insert);
                string? refused = fallback is { } f && f.Id == insert.Id && insert.Kind == "lv2" ? f.Reason : null;
                // The plugin is named because this error is reported on every
                // insert in the chain: without the name, the one that failed
                // cannot be told from the ones that were behind it.
                if (info is null || (refused is null ? !info.Supported : !NativePluginHost.SupportsFeatures(info.RequiredFeatures)))
                    throw new InvalidOperationException(
                        $"{InsertName(insert)} is unavailable or requires unsupported host features.");
                string node = $"{sinkName}_stage_{insertStages.Count}";
                FilterHandle stage;
                if (insert.NeedsNativeHost || refused is not null || NeedsLatencyHost(insert))
                {
                    if (!NativePluginHost.HostInstalled)
                        throw new InvalidOperationException("The native plugin host is not installed.");
                    if (insert.NativeHost && !info.NativeEditorSupported)
                        throw new InvalidOperationException($"{info.Name} has no editor the native host can open; select the filter chain.");
                    var host = new NativePluginHost(insert, node, channels, rate, info.Path, _note);
                    _nativeHosts.Add(host);
                    // Wine starts behind a bridged plugin and outlives this
                    // helper, so from here on it is this daemon's to end.
                    if (host.Bridged) _wine.HelperStarted();
                    stage = new(node, node, node, host.Process) { NativeHost = host, FilterChainError = refused };
                    stages.Add(stage);
                    if (!WaitForPorts(node, "playback", false, TimeSpan.FromSeconds(3), host.Process)
                        || !WaitForPorts(node, "capture", true, TimeSpan.FromSeconds(3), host.Process))
                        throw new InvalidOperationException($"The native host's ports did not appear for {insert.Plugin}.");
                }
                else
                {
                    stage = CreateFilterChain(node + "_in", node + "_out", description, channels, 0, false, [insert]);
                    stages.Add(stage);
                }
                // A filter-chain stage PipeWire refused comes back as a
                // hosted chain of its own. Index the host inside it, so
                // status, the editor and live controls reach the instance
                // that runs.
                insertStages.Add((insert.Id, stage.InsertStages.FirstOrDefault(s => s.Id == insert.Id).Stage ?? stage));
            }
            for (int i = 1; i < stages.Count; i++)
                if (LinkNodes(stages[i - 1].SourceName, "capture", stages[i].SinkName, "playback").Pairs.Count < channels)
                    throw new InvalidOperationException("Could not link the plugin stages.");
            return new(sinkName, stages[0].SinkName, stages[^1].SourceName, stages[0].Process)
            { Stages = stages, InsertStages = insertStages };
        }
        catch
        {
            foreach (FilterHandle stage in stages) StopFilter(stage);
            throw;
        }
    }

    internal static int ParseGraphSampleRate(string metadata)
    {
        foreach (string key in new[] { "clock.force-rate", "clock.rate" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(metadata,
                $"key:'{System.Text.RegularExpressions.Regex.Escape(key)}' value:'(\\d+)'");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int rate) && rate is >= 8000 and <= 384000)
                return rate;
        }
        throw new InvalidOperationException("PipeWire did not report a supported graph sample rate.");
    }

    /// <summary>
    /// The graph's quantum in frames from the settings metadata: a forced
    /// one when set, else the default. Null when neither is a usable figure.
    /// Clients can ask for a smaller quantum, so this is the most a cycle
    /// takes rather than what it takes now.
    /// </summary>
    internal static int? ParseGraphQuantum(string metadata)
    {
        foreach (string key in new[] { "clock.force-quantum", "clock.quantum" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(metadata,
                $"key:'{System.Text.RegularExpressions.Regex.Escape(key)}' value:'(\\d+)'");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int quantum) && quantum is >= 8 and <= 65536)
                return quantum;
        }
        return null;
    }

    /// <summary>
    /// The graph's sample rate as the settings metadata in the registry
    /// snapshot gives it, read without starting a process: the forced rate
    /// when one is set, else the default rate. Null when the snapshot holds
    /// no such metadata.
    /// </summary>
    internal int? GraphSampleRate() => ParseGraphSampleRate(GraphObjects());

    /// <summary>The rate in the <c>settings</c> metadata among <paramref name="objects"/>, as <see cref="ParseGraphSampleRate(string)"/> reads it from pw-metadata.</summary>
    internal static int? ParseGraphSampleRate(IEnumerable<JsonElement> objects)
    {
        foreach (JsonElement item in objects)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "PipeWire:Interface:Metadata"
                || !item.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object
                || !props.TryGetProperty("metadata.name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                || name.GetString() != "settings"
                || !item.TryGetProperty("metadata", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                continue;
            int? Value(string key)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("subject", out JsonElement subject) && subject.ValueKind == JsonValueKind.Number && subject.GetRawText() == "0"
                        && entry.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String && k.GetString() == key
                        && entry.TryGetProperty("value", out JsonElement value))
                    {
                        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int rate) && rate is >= 8000 and <= 384000 ? rate : null;
                    }
                return null;
            }
            return Value("clock.force-rate") ?? Value("clock.rate");
        }
        return null;
    }

    /// <summary>
    /// Change one control of a running filter-chain live: the chain exposes
    /// its plugins' controls as "node:symbol" entries of the Props param on
    /// its sink half. Throws when the node is gone or PipeWire refuses.
    /// </summary>
    public void SetFilterControl(FilterHandle f, string control, double value)
    {
        if (f.InsertStages.Count > 0)
        {
            int separator = control.IndexOf(':');
            if (separator < 2 || control[0] != 'i'
                || !int.TryParse(control.AsSpan(1, separator - 1), out int index)
                || index < 0 || index >= f.InsertStages.Count)
                throw new InvalidOperationException("Unknown plugin stage.");
            FilterHandle stage = f.InsertStages[index].Stage;
            if (stage.NativeHost is { } host) host.SetControl(control[(separator + 1)..], value);
            else SetFilterControl(stage, "i0:" + control[(separator + 1)..], value);
            return;
        }
        int id = FindNodeId(f.SinkName) ?? throw new InvalidOperationException($"filter node {f.SinkName} not found");
        string v = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Run("pw-cli", "set-param", id.ToString(), "Props", $"{{ params = [ \"{SpaString(control)}\" {v} ] }}");
    }

    /// <summary>Unload a filter by killing its holder process.</summary>
    public void StopFilter(FilterHandle f)
    {
        if (f.Stages.Count > 0)
        {
            foreach (FilterHandle stage in f.Stages) StopFilter(stage);
            return;
        }
        if (f.NativeHost is { } host)
        {
            _nativeHosts.Remove(host);
            host.Dispose();
            return;
        }
        try { if (!f.Process.HasExited) { f.Process.Kill(entireProcessTree: true); f.Process.WaitForExit(2000); } }
        catch (Exception) { /* already gone */ }
        _filters.Remove(f.Process);
        f.Process.Dispose();
    }

    private bool WaitForPorts(string node, string prefix, bool output, TimeSpan timeout, Process owner, int count = 1)
    {
        long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < end)
        {
            if (owner.HasExited) return false;
            if (ListPorts(node, prefix, output).Count >= count) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Read a pipe until it closes, retaining only its first <paramref name="keep"/>
    /// bytes as text. Long-lived holder processes keep printing for hours;
    /// nothing after the start is ever wanted.
    /// </summary>
    private static async Task<string> DrainKeepingHead(Stream pipe, int keep = 8192)
    {
        var buf = new byte[4096];
        var head = new MemoryStream();
        int n;
        while ((n = await pipe.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            int room = keep - (int)head.Length;
            if (room > 0) head.Write(buf, 0, Math.Min(n, room));
        }
        return System.Text.Encoding.UTF8.GetString(head.ToArray());
    }

    private string StopFailedFilter(FilterHandle handle, Task<string> stdoutTask, Task<string> stderrTask)
    {
        Process p = handle.Process;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(2000);
        }
        catch (Exception) { /* already gone */ }

        string stderr = "";
        string stdout = "";
        try { stderr = stderrTask.GetAwaiter().GetResult().Trim(); } catch (Exception) { }
        try { stdout = stdoutTask.GetAwaiter().GetResult().Trim(); } catch (Exception) { }
        _filters.Remove(p);
        p.Dispose();
        return stderr.Length > 0 ? stderr : stdout;
    }

    /// <summary>
    /// Best-effort removal of every direct link between two nodes, whatever
    /// created it. Used to clear a stale raw bypass before inserting a filter;
    /// name-based pw-link -d can fail during churn, so failures are ignored.
    /// </summary>
    public void UnlinkNodes(string fromNode, string toNode)
    {
        string listing;
        try { listing = Run("pw-link", "-l"); }
        catch (InvalidOperationException) { return; }
        string? currentOut = null;
        foreach (string raw in listing.Split('\n'))
        {
            if (raw.Length == 0) continue;
            string line = raw.TrimEnd();
            if (!char.IsWhiteSpace(raw[0])) { currentOut = line; continue; }
            string t = line.TrimStart();
            if (!t.StartsWith("|->", StringComparison.Ordinal)) continue;
            string target = t[3..].Trim();
            if (currentOut is not null &&
                currentOut.StartsWith(fromNode + ":", StringComparison.Ordinal) &&
                target.StartsWith(toNode + ":", StringComparison.Ordinal))
            {
                try { Run("pw-link", "-d", currentOut, target); }
                catch (InvalidOperationException) { /* racing churn */ }
            }
        }
    }

    /// <summary>Set a fader level live (0.0 removes the source from that mix).</summary>
    public void SetLoopbackVolume(LoopbackHandle lb, double volume)
    {
        int? nodeId = FindNodeId(lb.PlaybackNodeName);
        if (nodeId is null) return;
        Run("wpctl", "set-volume", nodeId.Value.ToString(),
            volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Route an application's stream onto a channel sink. Takes the stream's
    /// object.serial (the PulseAudio sink-input id), not the PipeWire node id.
    /// </summary>
    public void MoveStreamToSink(int streamSerial, string sinkName)
        => Run("pactl", "move-sink-input", streamSerial.ToString(), sinkName);

    /// <summary>
    /// Confirm the published destination, not just pactl's acknowledgement.
    /// A new stream can still be unbound when a successful move returns.
    /// </summary>
    public bool IsStreamOnSink(int streamSerial, string sinkName)
        => IsStreamOnSink(
            TryRun("pactl", "list", "sink-inputs", "short") ?? "",
            TryRun("pactl", "list", "sinks", "short") ?? "",
            streamSerial, sinkName);

    /// <summary>The sink a stream currently plays into, by name, or null.</summary>
    public string? StreamSinkName(int streamSerial)
        => StreamSinkName(TryRun("pactl", "list", "sink-inputs", "short") ?? "",
                          TryRun("pactl", "list", "sinks", "short") ?? "", streamSerial);

    internal static string? StreamSinkName(string sinkInputs, string sinks, int streamSerial)
    {
        string? sinkId = sinkInputs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 2 && columns[0] == streamSerial.ToString())
            .Select(columns => columns[1])
            .FirstOrDefault();
        if (sinkId is null || sinkId == uint.MaxValue.ToString()) return null;
        return sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 2 && columns[0] == sinkId)
            .Select(columns => columns[1])
            .FirstOrDefault();
    }

    internal static bool IsStreamOnSink(string sinkInputs, string sinks, int streamSerial, string sinkName)
    {
        string? sinkId = sinkInputs.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 2 && columns[0] == streamSerial.ToString())
            .Select(columns => columns[1])
            .FirstOrDefault();
        if (sinkId is null || sinkId == uint.MaxValue.ToString()) return false;

        return sinks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Any(columns => columns.Length >= 2 && columns[0] == sinkId && columns[1] == sinkName);
    }

    /// <summary>
    /// Connect two nodes with direct port links (FL to FL, FR to FR). Unlike a
    /// loopback there is no process and no clock bridging: the linked island is
    /// driven by the hardware sink's clock, which is what actually makes audio
    /// flow. Loopbacks from a null sink to a hardware sink stall on this system
    /// (audio for about a second, then silence), verified by ear; direct links
    /// are the standard PipeWire answer for this exact routing.
    /// </summary>
    public PortLink LinkNodes(string fromNode, string fromPortPrefix, string toNode, string toPortPrefix,
        int toPairOffset = 0, int fromPairOffset = 0, int? fromChannel = null, bool exactPair = false)
    {
        if (fromChannel is < 0 or >= 64) throw new ArgumentOutOfRangeException(nameof(fromChannel));
        // Port names vary by device (FL/FR on most nodes, AUX0/AUX1 on
        // multichannel interfaces like the Wave XLR Pro), so discover the real
        // ports rather than assuming, then pair them in order. A mono source
        // into a stereo sink gets its one port linked to both inputs.
        // pw-link addresses ports by their printed name, and a duplicated
        // node (a USB sink caught mid re-enumeration lists every port twice)
        // must not yield duplicate pairs, so identical names collapse first.
        List<string> outs = [.. ListPorts(fromNode, fromPortPrefix, output: true).Distinct()];
        List<string> ins = [.. ListPorts(toNode, toPortPrefix, output: false).Distinct()];
        // One selected port of a multichannel source feeds both sides like a
        // mono source; a source without that port feeds nothing.
        if (fromChannel is { } selected) outs = [.. outs.Skip(selected).Take(1)];
        else if (fromPairOffset > 0)
        {
            // A source without that pair feeds nothing. Falling back to the
            // first pair here used to duplicate a mono capture into every
            // channel, which made the channel mutes ineffective.
            outs = SelectPair(outs, fromPairOffset);
        }
        else if (outs.Count > 2)
            outs = [.. outs.Take(2)];
        if (toPairOffset > 0 || exactPair)
        {
            // A target without the requested return pair must receive
            // nothing. Keeping its first pair here routed the Pro-only Aux
            // mix into the normal speakers of stereo Wave XLR models.
            ins = SelectPair(ins, toPairOffset);
        }
        // Raw multichannel sinks are named multichannel-output on a stock
        // system and pro-output-N once the card has a UCM profile.
        else if (ins.Count > 2 && toPairOffset == 0
                 && (toNode.Contains("multichannel", StringComparison.Ordinal)
                     || toNode.Contains(".pro-output-", StringComparison.Ordinal)))
            ins = [.. ins.Take(2)];

        // Pair by channel suffix where both sides have one (FL to FL, FR to
        // FR, whatever the listing order); the positional fallback keeps a
        // mono source feeding both inputs of a stereo sink. A crossed link
        // (FR into FL) once slipped through here via index clamping and put
        // the right channel on both speakers.
        static string Chan(string port)
        {
            int i = port.LastIndexOf('_');
            return i < 0 ? "" : port[(i + 1)..];
        }
        var pairs = new List<(string From, string To)>();
        bool complete = true;
        for (int i = 0; i < ins.Count && (outs.Count > 0); i++)
        {
            string to = ins[i];
            string from = outs.FirstOrDefault(o => Chan(o) != "" && Chan(o) == Chan(to))
                ?? outs[Math.Min(i, outs.Count - 1)];
            try { Run("pw-link", from, to); pairs.Add((from, to)); }
            // The pair is already connected, for instance the surviving side
            // of a route being repaired: it belongs to this route as well.
            catch (InvalidOperationException ex) when (ex.Message.Contains("File exists", StringComparison.Ordinal))
            { pairs.Add((from, to)); }
            // A port that vanished or refused the link leaves the route short;
            // the sweep sees Complete false and builds it again.
            catch (InvalidOperationException) { complete = false; }
        }
        return new PortLink(pairs) { Complete = complete };
    }

    /// <summary>
    /// Select a stereo pair from an ordered port list. Missing pairs never
    /// fall back to pair zero; a final mono port is kept so mono devices can
    /// still be addressed deliberately.
    /// </summary>
    internal static List<string> SelectPair(IReadOnlyList<string> ports, int pairOffset)
    {
        if (pairOffset < 0) throw new ArgumentOutOfRangeException(nameof(pairOffset));
        int start = checked(pairOffset * 2);
        return ports.Count > start ? [.. ports.Skip(start).Take(2)] : [];
    }

    /// <summary>Ports of a node whose name starts with a prefix, in pw-link order.</summary>
    internal List<string> ListPorts(string node, string prefix, bool output)
    {
        var ports = new List<string>();
        IReadOnlyList<string> listing = ReadObservedPorts(output)
            ?? Run("pw-link", output ? "-o" : "-i").Split('\n').Select(line => line.Trim()).ToArray();
        string requested = $"{node}:{prefix}";
        foreach (string name in listing)
            if (name.StartsWith(requested, StringComparison.Ordinal)) ports.Add(name);
        // Filter-chain streams use input/output ports, while native hosts
        // keep playback/capture. Prefer the requested ports when present.
        if (ports.Count == 0 && node.StartsWith("OpenXLR_", StringComparison.Ordinal)
            && prefix is "playback" or "capture")
        {
            string streamPrefix = $"{node}:" + (prefix == "playback" ? "input" : "output");
            ports.AddRange(listing.Where(name => name.StartsWith(streamPrefix, StringComparison.Ordinal)));
        }
        return ports;
    }

    /// <summary>
    /// Make sure every pair of a link set still exists, re-creating any that
    /// died (a USB device re-enumerating destroys its node and every link on
    /// it, while the new node usually keeps the same port names). Returns
    /// Broken when a port is gone entirely, meaning the route needs a fresh
    /// port discovery instead, and for a route that came up with a pair
    /// missing, which the caller rebuilds the same way. A pair the registry
    /// already shows connected costs no helper call.
    /// </summary>
    public LinkHealth EnsureLinks(PortLink link)
    {
        if (!link.Complete) return LinkHealth.Broken;
        var health = LinkHealth.Healthy;
        HashSet<(string From, string To)> known = GraphLinks();
        foreach ((string from, string to) in link.Pairs)
        {
            if (known.Contains((from, to))) continue;
            try
            {
                Run("pw-link", from, to);
                health = LinkHealth.Relinked;
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("File exists", StringComparison.Ordinal)) continue;
                return LinkHealth.Broken;
            }
        }
        return health;
    }

    // The link index is rebuilt only when the graph snapshot it came from
    // was replaced, so a quiet graph answers every route check from memory.
    private JsonElement[]? _linkObjects;
    private HashSet<(string From, string To)> _graphLinks = [];

    /// <summary>Every port link in the graph as (output port, input port) names, the form pw-link prints.</summary>
    private HashSet<(string From, string To)> GraphLinks()
    {
        lock (DumpGate)
        {
            JsonElement[] objects;
            // Without a graph the check falls back to asking pw-link per pair.
            try { objects = GraphObjects(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { return []; }
            if (ReferenceEquals(objects, _linkObjects)) return _graphLinks;
            _graphLinks = ParseGraphLinks(objects);
            _linkObjects = objects;
            return _graphLinks;
        }
    }

    internal static HashSet<(string From, string To)> ParseGraphLinks(IEnumerable<JsonElement> objects)
    {
        var nodes = new Dictionary<int, string>();
        var ports = new Dictionary<int, (int Node, string Name)>();
        var links = new List<(int From, int To)>();
        foreach (JsonElement entry in objects)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out JsonElement id) || !TryInt(id, out int number)
                || !entry.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) continue;
            string? kind = type.GetString();
            if (kind == "PipeWire:Interface:Link")
            {
                if (info.TryGetProperty("output-port-id", out JsonElement from) && TryInt(from, out int source)
                    && info.TryGetProperty("input-port-id", out JsonElement to) && TryInt(to, out int target))
                    links.Add((source, target));
                continue;
            }
            if (!info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object) continue;
            if (kind == "PipeWire:Interface:Node" && props.TryGetProperty("node.name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
                nodes[number] = name.GetString()!;
            else if (kind == "PipeWire:Interface:Port" && props.TryGetProperty("node.id", out JsonElement node)
                && int.TryParse(node.ToString(), out int owner)
                && props.TryGetProperty("port.name", out JsonElement port) && port.ValueKind == JsonValueKind.String)
                ports[number] = (owner, port.GetString()!);
        }
        var result = new HashSet<(string From, string To)>();
        foreach ((int from, int to) in links)
            if (ports.TryGetValue(from, out var source) && ports.TryGetValue(to, out var target)
                && nodes.TryGetValue(source.Node, out string? sourceName) && nodes.TryGetValue(target.Node, out string? targetName))
                result.Add(($"{sourceName}:{source.Name}", $"{targetName}:{target.Name}"));
        return result;

        static bool TryInt(JsonElement value, out int number)
        {
            number = 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
        }
    }

    /// <summary>Remove a set of port links made by <see cref="LinkNodes"/>.</summary>
    public void Unlink(PortLink link)
    {
        foreach ((string from, string to) in link.Pairs)
        {
            try { Run("pw-link", "-d", from, to); }
            catch (InvalidOperationException) { /* already gone */ }
        }
    }

    /// <summary>
    /// Remove each pair and link it again straight away, so the ports behind
    /// it are joined afresh. The number of pairs linked again; one that
    /// refuses stays removed for the caller's repair to find.
    /// </summary>
    public int Relink(IReadOnlyList<(string From, string To)> pairs)
    {
        foreach ((string from, string to) in pairs)
        {
            try { Run("pw-link", "-d", from, to); }
            catch (InvalidOperationException) { /* already gone */ }
        }
        int linked = 0;
        foreach ((string from, string to) in pairs)
        {
            try { Run("pw-link", from, to); linked++; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("File exists", StringComparison.Ordinal)) { linked++; }
            catch (InvalidOperationException) { }
        }
        return linked;
    }

    /// <summary>Confirm removal without recreating any pair; a refused removal keeps its owner pending.</summary>
    public bool EnsureUnlinked(PortLink link)
    {
        if (link.Pairs.Count == 0) return true;
        HashSet<(string From, string To)>? observed = null;
        bool inspected = false;
        bool removed = true;
        foreach ((string from, string to) in link.Pairs)
        {
            try { Run("pw-link", "-d", from, to); }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.EndsWith(": failed to unlink ports: No such file or directory", StringComparison.Ordinal)) continue;
                if (!inspected)
                {
                    inspected = true;
                    // Our own link writes can precede their registry echo.
                    // Only a fresh snapshot can confirm a refused unlink is
                    // already absent; an unavailable graph keeps ownership.
                    try
                    {
                        using JsonDocument snapshot = PipeWireSnapshot.Parse(RunBytes("pw-dump"));
                        observed = ParseGraphLinks(snapshot.RootElement.EnumerateArray());
                    }
                    catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { }
                }
                if (observed is null || observed.Contains((from, to))) removed = false;
            }
        }
        return removed;
    }

    /// <summary>
    /// Point a mix's sink at an output device. A "sink#..." pseudo-device
    /// address (the Wave XLR Pro's physical outputs) routes into the USB
    /// return pair that reaches that output's hardware mix: the analog
    /// outputs (jacks, line out) carry the Personal mix, fed by channels 2/3
    /// (confirmed by ear); the USB Aux port carries the aux mix, fed by the
    /// Music return on channels 10/11 (confirmed from the working Windows
    /// capture; the aux port receives no audio from the Personal mix pairs).
    /// </summary>
    public PortLink RouteMixToOutput(string mixSink, string outputSink)
        => RouteTapToOutput(mixSink, "monitor", outputSink);

    /// <summary>
    /// Route a tap (a mix sink's monitor, or an insert chain's source half)
    /// into an output; "#usbaux" and "#hp1"-style markers select the device's
    /// return pair.
    /// </summary>
    public PortLink RouteTapToOutput(string fromNode, string fromPrefix, string outputSink)
    {
        int pair = 0;
        int marker = outputSink.IndexOf('#');
        if (marker >= 0)
        {
            string suffix = outputSink[(marker + 1)..];
            pair = suffix == "usbaux" ? OpenXLR.Core.Devices.ProReturnPairs.Aux : OpenXLR.Core.Devices.ProReturnPairs.Monitor;
            outputSink = outputSink[..marker];
        }
        return LinkNodes(fromNode, fromPrefix, outputSink, "playback", pair);
    }

    /// <summary>
    /// Route a tap into one USB playback pair of an output's device: a jack
    /// feed of the Wave XLR Pro beyond the first streams on a pair of its own
    /// (see <see cref="OpenXLR.Core.Devices.ProJackFeeds"/>). Any '#' marker
    /// only names the device.
    /// </summary>
    public PortLink RouteTapToOutput(string fromNode, string fromPrefix, string outputSink, int pair)
    {
        int marker = outputSink.IndexOf('#');
        return LinkNodes(fromNode, fromPrefix, marker < 0 ? outputSink : outputSink[..marker], "playback", pair);
    }

    /// <summary>
    /// Route a tap into exactly one USB playback pair of a device's sink,
    /// pair 0 included: a channel's own pair in hardware mixing.
    /// </summary>
    public PortLink RouteTapToPair(string fromNode, string fromPrefix, string sink, int pair)
        => LinkNodes(fromNode, fromPrefix, StripMarkerOf(sink), "playback", pair, exactPair: true);

    private static string StripMarkerOf(string name) => name.IndexOf('#') is int marker and >= 0 ? name[..marker] : name;

    /// <summary>
    /// Feed a capture device into a channel sink; <paramref name="sourcePair"/>
    /// selects which stereo pair of a multichannel source (0 = first).
    /// </summary>
    public PortLink RouteInputToChannel(string sourceName, string channelSink, int sourcePair = 0)
        => LinkNodes(sourceName, "capture", channelSink, "playback", 0, sourcePair);

    /// <summary>Check a bound strip's source port before creating any plugin hosts for it.</summary>
    internal bool HasCaptureChannel(string sourceName, int channel)
        => ListPorts(sourceName, "", output: true).Distinct().Skip(channel).Any();

    /// <summary>Stop one loopback (used when re-pointing a device selection).</summary>
    public void StopLoopback(LoopbackHandle lb)
    {
        try { if (!lb.Process.HasExited) { lb.Process.Kill(entireProcessTree: true); lb.Process.WaitForExit(2000); } }
        catch (Exception) { /* already gone */ }
        _loopbacks.Remove(lb.Process);
        lb.Process.Dispose();
    }

    /// <summary>Where the kernel's sound card files are; tests point it elsewhere.</summary>
    internal string AsoundRoot { get; init; } = "/proc/asound";

    /// <summary>
    /// A capture source as the graph and its card's kernel stream show it
    /// (see <see cref="CaptureRun"/>). The card and the stream come from the
    /// node's own ALSA properties, so a renumbered card after a replug is
    /// followed. <see cref="CaptureRun.Missing"/> without a graph.
    /// </summary>
    public CaptureRun CaptureRunOf(string sourceName)
    {
        JsonElement[] objects;
        try { objects = GraphObjects(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return CaptureRun.Missing; }
        return CaptureRunOf(objects, sourceName, AsoundRoot);
    }

    internal static CaptureRun CaptureRunOf(IEnumerable<JsonElement> objects, string sourceName, string asoundRoot)
    {
        foreach (JsonElement o in objects)
        {
            if (o.ValueKind != JsonValueKind.Object
                || !o.TryGetProperty("type", out JsonElement type) || type.GetString() != "PipeWire:Interface:Node"
                || !o.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id)
                || !o.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object
                || !props.TryGetProperty("node.name", out JsonElement name) || name.GetString() != sourceName) continue;
            bool running = info.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
                && state.GetString() == "running";
            int? card = PropInt(props, "api.alsa.pcm.card") ?? PropInt(props, "alsa.card");
            bool? kernel = card is int number ? CaptureRun.ReadKernelStatus(asoundRoot, number, PropInt(props, "alsa.device") ?? 0) : null;
            return new CaptureRun(id, card is not null, running, kernel);
        }
        return CaptureRun.Missing;

        // PipeWire writes these as numbers, older versions as strings.
        static int? PropInt(JsonElement props, string key)
            => !props.TryGetProperty(key, out JsonElement value) ? null
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n
                : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed
                : null;
    }

    /// <summary>
    /// Whether every pair of a route is linked in the graph right now, without
    /// making any of them; false for a route that came up short.
    /// </summary>
    public bool LinksPresent(PortLink link)
    {
        if (!link.Complete || link.Pairs.Count == 0) return false;
        HashSet<(string From, string To)> known = GraphLinks();
        return link.Pairs.All(known.Contains);
    }

    /// <summary>PipeWire node id for a node.name, or null if absent.</summary>
    public int? FindNodeId(string nodeName)
    {
        foreach (var (id, name, _) in DumpNodes())
            if (name == nodeName) return id;
        return null;
    }

    /// <summary>
    /// Every sink (output) and source (input) in the graph, real or virtual.
    /// PipeWire makes no distinction, so a null sink, a loopback, or another
    /// app's virtual device is selectable exactly like a physical card.
    /// Monitor sources are excluded: they are the tap side of a sink, not a
    /// device a user would pick as a microphone.
    /// </summary>
    public IReadOnlyList<AudioNode> ListDevices(
        bool exposeHardwareMonitorOutputs = false, string? hardwareSinkHint = null)
        => ListDevices(exposeHardwareMonitorOutputs, hardwareSinkHint is null ? [] : [hardwareSinkHint]);

    /// <summary>
    /// The same, with the hardware outputs found by the first of
    /// <paramref name="hardwareSinkHints"/> that names a sink. A hint that
    /// names sinks of two cards exposes no hardware outputs: aliasing the
    /// wrong unit's jacks would switch the other unit's selectors.
    /// </summary>
    public IReadOnlyList<AudioNode> ListDevices(bool exposeHardwareMonitorOutputs, IReadOnlyList<string> hardwareSinkHints)
    {
        var found = new List<AudioNode>();
        var profiles = new List<string?>();
        foreach (JsonElement o in GraphObjects())
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;

            string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
            if (name is null) continue;
            string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";

            bool isSink = mc == "Audio/Sink";
            bool isSource = mc is "Audio/Source" or "Audio/Source/Virtual";
            if (!isSink && !isSource) continue;
            if (isSource && name.EndsWith(".monitor", StringComparison.Ordinal)) continue;

            string desc = props.TryGetProperty("node.description", out JsonElement d)
                ? d.GetString() ?? name : name;
            // Real hardware carries device.api (alsa, bluez5, ...); any
            // software-created source or sink does not.
            bool physical = props.TryGetProperty("device.api", out JsonElement api) &&
                            !string.IsNullOrEmpty(api.GetString());
            // Sinks carry their desktop level so keys and dials can show it.
            var level = isSink ? SinkLevel(info) : null;
            // Sources carry their port layout so a picker can name each port.
            (int? channels, string[]? positions) = isSource ? PortLayout(props) : (null, null);
            found.Add(new AudioNode(name, desc, isSink ? AudioNodeKind.Sink : AudioNodeKind.Source,
                name.StartsWith("OpenXLR", StringComparison.Ordinal), physical)
                { Volume = level?.DesktopVolume, Muted = level?.Muted, Channels = channels, Positions = positions });
            profiles.Add(physical && props.TryGetProperty("device.profile.description", out JsonElement pd)
                ? pd.GetString() : null);
        }
        // Outputs and sources are shortened apart: a card's output and its
        // source may share a short name, since no picker lists both.
        foreach (AudioNodeKind kind in new[] { AudioNodeKind.Sink, AudioNodeKind.Source })
        {
            int[] of = [.. Enumerable.Range(0, found.Count).Where(i => found[i].Kind == kind)];
            string[] shortNames = ShortHardwareNames([.. of.Select(i => (found[i].Description, profiles[i]))]);
            for (int k = 0; k < of.Length; k++)
                if (shortNames[k] != found[of[k]].Description) found[of[k]] = found[of[k]] with { Description = shortNames[k] };
        }

        // The Pro's physical outputs each play the hardware mix their selector
        // in block 0x0001 names (decoded 2026-08-25, confirmed by ear on both
        // jacks): the first jack feed on USB playback channels 2/3, a second
        // and third on pairs of their own (ProJackFeeds). Each output is
        // advertised as its own pseudo-sink; the daemon sets the matching
        // hardware selector when one is chosen as the monitor.
        // Headphones jack 1 is on the front of the unit, jack 2 on the back.
        AudioNode? pro = null;
        if (exposeHardwareMonitorOutputs)
        {
            IEnumerable<AudioNode> sinks = found.Where(n => n.Kind == AudioNodeKind.Sink);
            pro = hardwareSinkHints.Count == 0 ? sinks.FirstOrDefault()
                : InterfaceNodes.Pick(sinks, hardwareSinkHints, out _, out _).FirstOrDefault();
        }
        if (pro is not null)
        {
            found.Add(new AudioNode($"{pro.Name}#hp1", "Headphones 1 (front)",
                AudioNodeKind.Sink, IsOwn: false, IsPhysical: true));
            found.Add(new AudioNode($"{pro.Name}#hp2", "Headphones 2 (rear)",
                AudioNodeKind.Sink, IsOwn: false, IsPhysical: true));
            // No "#usbaux" entry: the USB Aux port is owned by the Aux mix
            // (its own submixer column), not the monitor selection.
            found.Add(new AudioNode($"{pro.Name}#lineout", "Line Out",
                AudioNodeKind.Sink, IsOwn: false, IsPhysical: true));
            // Hide the raw multichannel sink from device pickers: its first
            // pair reaches no physical output (audio vanishes), and choosing
            // it clears the hardware output selectors. The pseudo-outputs
            // above cover every real destination; internal routing keeps
            // using the raw sink by name regardless of this listing.
            found.Remove(pro);
        }
        return found;
    }

    /// <summary>
    /// Hardware outputs and sources named without the ALSA profile PipeWire
    /// appends ("Elgato Wave XLR Dock MK.2 Analog Stereo" reads "Elgato Wave
    /// XLR Dock MK.2"). The profile comes from the node's own
    /// <c>device.profile.description</c>, so a translated desktop loses its
    /// translated suffix. A node keeps its full name when the short one would
    /// match another of the given nodes, as two profiles of one card would;
    /// the caller passes the outputs and the sources apart.
    /// </summary>
    internal static string[] ShortHardwareNames(IReadOnlyList<(string Description, string? Profile)> nodes)
    {
        string[] shortened = [.. nodes.Select(n => n.Profile is { Length: > 0 } profile
            && n.Description.Length > profile.Length + 1
            && n.Description.EndsWith(" " + profile, StringComparison.Ordinal)
                ? n.Description[..^(profile.Length + 1)].TrimEnd() : n.Description)];
        return [.. shortened.Select((name, i) => name == nodes[i].Description
            || Enumerable.Range(0, nodes.Count).Any(j => j != i && (shortened[j] == name || nodes[j].Description == name))
                ? nodes[i].Description : name)];
    }

    /// <summary>
    /// A node's channel count as its <c>audio.channels</c> property gives it,
    /// read from the graph the adapter already holds; null when the node is
    /// not there or does not say.
    /// </summary>
    public int? NodeChannels(string nodeName)
    {
        foreach (JsonElement o in GraphObjects())
            if (o.TryGetProperty("info", out JsonElement info) && info.TryGetProperty("props", out JsonElement props)
                && props.TryGetProperty("node.name", out JsonElement n) && n.ValueKind == JsonValueKind.String && n.GetString() == nodeName)
                return PortLayout(props).Channels;
        return null;
    }

    /// <summary>
    /// A node's channel count and channel positions from its properties:
    /// <c>audio.channels</c> is a number and <c>audio.position</c> a list
    /// PipeWire writes as text ("[ FL, FR ]", or "FL,FR" from pactl). The
    /// positions are dropped when their count disagrees with the channels.
    /// </summary>
    internal static (int? Channels, string[]? Positions) PortLayout(JsonElement props)
    {
        int? channels = props.TryGetProperty("audio.channels", out JsonElement c) && c.ValueKind == JsonValueKind.Number
            && c.TryGetInt32(out int n) && n is > 0 and <= 64 ? n : null;
        string[]? positions = null;
        if (props.TryGetProperty("audio.position", out JsonElement p))
        {
            IEnumerable<string> names = p.ValueKind switch
            {
                JsonValueKind.String => (p.GetString() ?? "").Split([',', ' ', '[', ']'], StringSplitOptions.RemoveEmptyEntries),
                JsonValueKind.Array => p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? ""),
                _ => [],
            };
            positions = [.. names.Where(name => name.Length is > 0 and <= 16 && name.All(char.IsAsciiLetterOrDigit))];
            if (positions.Length == 0 || positions.Length > 64) positions = null;
        }
        channels ??= positions?.Length;
        if (positions is not null && positions.Length != channels) positions = null;
        return (channels, positions);
    }

    /// <summary>
    /// Binaries that are audio plumbing, not applications: they register as
    /// PipeWire clients but should never appear in the app list.
    /// </summary>
    private static readonly string[] PlumbingBinaries =
    [
        "pipewire", "pipewire-pulse", "wireplumber", "pactl", "parec", "paplay",
        "pw-cli", "pw-dump", "pw-cat", "pw-play", "pw-record", "pw-loopback",
        "pw-link", "pw-mon", "speech-dispatcher", "OpenXLR.Daemon", "OpenXLR.UI",
        "openxlr-lv2-host",   // an insert's own plugin host, not an application
        "libcanberra", "xdg-desktop-portal", "xdg-desktop-portal-kde",
        "xdg-desktop-portal-gnome", "pavucontrol",
    ];

    /// <summary>
    /// Desktop-environment services: audio-capable, but noise in an app list.
    /// Excluded from the running-clients scan only; when one actually plays a
    /// sound its stream still registers it, so nothing is ever unroutable.
    /// </summary>
    private static readonly string[] DesktopServiceBinaries =
    [
        "kwin_wayland", "kwin_x11", "plasmashell", "gnome-shell",
        "polkit-kde-authentication-agent-1", "libcanberra", "gsd-media-keys",
        "kded5", "kded6", "kaccess", "orca", "ksmserver", "krunner",
    ];

    /// <summary>
    /// True for identities that are audio plumbing, not applications. A bare
    /// translation-layer binary ("wine64-preloader") and Wine's transient
    /// "format test stream" probe also count: they are phases of a game's
    /// startup, not apps, and used to leave duplicate registry entries next
    /// to the game's real identity.
    /// </summary>
    public static bool IsPlumbingIdentity(string identity)
        => Array.Exists(PlumbingBinaries, pb => identity.Equals(pb, StringComparison.OrdinalIgnoreCase))
           || Array.Exists(WineBinaries, wb => identity.Equals(wb, StringComparison.OrdinalIgnoreCase))
           || identity.EndsWith("|format test stream", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WineBinaries =
        ["wine", "wine64", "wine-preloader", "wine64-preloader", "wineserver",
         "steamwebhelper"];   // superseded identity: its audio now registers as "steam"

    /// <summary>Parse one graph for both halves of the application's sweep.</summary>
    internal (IReadOnlyList<AudioStream> Streams, IReadOnlyList<AudioStream> Clients) ListApplications()
    {
        JsonElement[] objects = GraphObjects();
        return (ListStreams(objects), ListClients(objects));
    }

    internal static (IReadOnlyList<AudioStream> Streams, IReadOnlyList<AudioStream> Clients) ListApplications(byte[] json)
    {
        JsonElement[] objects = ParseObjects(json);
        return (ListStreams(objects), ListClients(objects));
    }

    /// <summary>
    /// Every running application that has registered with PipeWire, playing or
    /// not. Browsers, chat apps and players connect as clients the moment they
    /// initialise audio, so this is "audio-capable and running".
    /// </summary>
    public IReadOnlyList<AudioStream> ListClients() => ListClients(GraphObjects());

    internal static IReadOnlyList<AudioStream> ListClients(byte[] json)
    {
        return ListClients(ParseObjects(json));
    }

    private static IReadOnlyList<AudioStream> ListClients(IEnumerable<JsonElement> objects)
    {
        var found = new List<AudioStream>();
        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                t.GetString() != "PipeWire:Interface:Client") continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;

            string? binary = props.TryGetProperty("application.process.binary", out JsonElement b) ? b.GetString() : null;
            if (binary is not null && binary.EndsWith(" (deleted)", StringComparison.Ordinal))
                binary = binary[..^10];
            string? appName = props.TryGetProperty("application.name", out JsonElement an) ? an.GetString() : null;
            // Chromium registers its capture-side client as "<name> input";
            // it is the same application, so drop the suffix.
            if (appName is not null && appName.EndsWith(" input", StringComparison.Ordinal))
                appName = appName[..^6];
            if (binary is null && appName is null) continue;
            bool Listed(string[] list, string? v) => v is not null && Array.Exists(list, e =>
                    v.Equals(e, StringComparison.OrdinalIgnoreCase));
            if (Listed(PlumbingBinaries, binary) || Listed(PlumbingBinaries, appName)) continue;
            if (Listed(DesktopServiceBinaries, binary) || Listed(DesktopServiceBinaries, appName)) continue;
            if (appName is not null && appName.Contains("OpenXLR", StringComparison.Ordinal)) continue;

            found.Add(new AudioStream(0, appName, binary, null)
            {
                ProcessId = ProcessId(props),
                FlatpakInstanceId = FlatpakInstance(props),
                IconName = Text(props, "application.icon_name"),
                AppId = Text(props, "pipewire.access.portal.app_id") ?? Text(props, "application.id"),
            });
        }
        return found;
    }

    /// <summary>
    /// Every application playback stream (what PulseAudio calls a sink-input),
    /// with the identity fields the matcher needs. OpenXLR's own loopbacks are
    /// excluded: they are plumbing, not applications.
    /// </summary>
    public IReadOnlyList<AudioStream> ListStreams() => ListStreams(GraphObjects());

    internal static IReadOnlyList<AudioStream> ListStreams(byte[] json)
    {
        return ListStreams(ParseObjects(json));
    }

    private static IReadOnlyList<AudioStream> ListStreams(IEnumerable<JsonElement> objects)
    {
        var found = new List<AudioStream>();
        // Native PipeWire streams can leave process metadata on their
        // owning client. Read it from this same snapshot, regardless of
        // whether the client appears before or after its playback node.
        var clients = new Dictionary<int, JsonElement>();
        foreach (JsonElement o in objects)
            if (o.TryGetProperty("type", out JsonElement type) &&
                type.GetString() == "PipeWire:Interface:Client" &&
                o.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out int clientId) &&
                o.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("props", out JsonElement props))
                clients[clientId] = props;

        foreach (JsonElement o in objects)
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;

            string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";
            if (mc != "Stream/Output/Audio") continue;

            // Exclude the mixer's own plumbing. Filter modules (combine and
            // remap sinks, loopbacks) run internal streams named things like
            // "output.OpenXLR_ch_game"; moving those rewires the mixer
            // itself. Real applications never carry node.link-group.
            if (props.TryGetProperty("node.link-group", out _)) continue;

            string? nodeName = props.TryGetProperty("node.name", out JsonElement nn) ? nn.GetString() : null;
            if (nodeName is not null && nodeName.Contains("OpenXLR", StringComparison.Ordinal)) continue;

            string? mediaName = props.TryGetProperty("media.name", out JsonElement mn) ? mn.GetString() : null;
            if (mediaName is not null && mediaName.StartsWith("Simultaneous output", StringComparison.Ordinal)) continue;

            // object.serial is what PulseAudio exposes as the sink-input
            // id, and pactl move-sink-input addresses streams by that, not
            // by the PipeWire node id.
            int serial = props.TryGetProperty("object.serial", out JsonElement os) &&
                         os.TryGetInt32(out int sv) ? sv : o.GetProperty("id").GetInt32();
            // A binary replaced on disk while running (updates) reports
            // as "name (deleted)"; strip it or the app splits identities.
            JsonElement client = default;
            if (props.TryGetProperty("client.id", out JsonElement owner) && owner.ValueKind == JsonValueKind.Number &&
                owner.TryGetInt32(out int ownerId))
                clients.TryGetValue(ownerId, out client);
            string? AppProperty(string key) => Str(props, key) ?? Str(client, key);
            string? binary = AppProperty("application.process.binary");
            if (binary is not null && binary.EndsWith(" (deleted)", StringComparison.Ordinal))
                binary = binary[..^10];
            found.Add(new AudioStream(
                o.GetProperty("id").GetInt32(),
                AppProperty("application.name"),
                binary,
                Str(props, "media.name"))
            {
                Serial = serial,
                ProcessId = ProcessId(client) is > 0 and var pid ? pid : ProcessId(props),
                FlatpakInstanceId = FlatpakInstance(client),
                IconName = AppProperty("application.icon_name"),
                // The portal's id is set by PipeWire on the client and cannot
                // be forged by a sandboxed application, so it comes first.
                AppId = Str(client, "pipewire.access.portal.app_id") ?? AppProperty("application.id"),
            });
        }
        return found;

        static string? Str(JsonElement props, string key)
            => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(key, out JsonElement v) &&
               v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString() : null;
    }

    private static string? Text(JsonElement props, string key)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(key, out JsonElement v) &&
           v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    private static string? FlatpakInstance(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty("pipewire.access.portal.instance_id", out var value))
            return Text(props, "pipewire.access.portal.app_id") is not null ? "invalid" : null;
        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) && id > 0
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "invalid";
    }

    private static int ProcessId(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty("application.process.id", out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return Math.Max(0, number);
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed) ? Math.Max(0, parsed) : 0;
    }

    /// <summary>All audio nodes as (id, node.name, media.class).</summary>
    public IEnumerable<(int Id, string Name, string MediaClass)> DumpNodes()
    {
        foreach (JsonElement o in GraphObjects())
        {
            if (!o.TryGetProperty("type", out JsonElement t) ||
                !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out JsonElement info) ||
                !info.TryGetProperty("props", out JsonElement props)) continue;
            string? name = props.TryGetProperty("node.name", out JsonElement n) ? n.GetString() : null;
            if (name is null) continue;
            string mc = props.TryGetProperty("media.class", out JsonElement m) ? m.GetString() ?? "" : "";
            yield return (o.GetProperty("id").GetInt32(), name, mc);
        }
    }

    private bool WaitForNode(string nodeName, TimeSpan timeout)
    {
        long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < end)
        {
            if (FindNodeId(nodeName) is not null) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Whether Wine can be ended now, and in which prefixes. Empty unless a
    /// bridged plugin ran here, every bridged helper has gone, and a Wine
    /// session this daemon started is up. Deciding is separate from doing, so
    /// a caller can decide under its lock and make the call outside it.
    /// </summary>
    internal IReadOnlyList<WineSession.Ending> WineEndings() => _wine.Decide(_nativeHosts.Any(host => host.Bridged));

    /// <summary>Carry out decided endings. Never throws, never waits long.</summary>
    internal void EndWine(IReadOnlyList<WineSession.Ending> endings) => _wine.Run(endings);

    /// <summary>Remove everything this adapter created, in reverse order.</summary>
    public void TearDown()
    {
        foreach (NativePluginHost host in _nativeHosts) host.Dispose();
        _nativeHosts.Clear();
        // Here the call is made rather than handed out: at a daemon stop this
        // is the last chance to end Wine, and what it leaves has to be gone
        // before the unit's control group is counted. It takes Wine a couple
        // of seconds and nothing below depends on it, so it goes out now and
        // is joined at the end: the stop pays the longer of the two, not the
        // sum. It is bounded by the runner's deadline and cannot fail the
        // teardown.
        IReadOnlyList<WineSession.Ending> endings = WineEndings();
        Task wine = endings.Count == 0 ? Task.CompletedTask : Task.Run(() => EndWine(endings));
        foreach (Process p in _loopbacks.Concat(_filters))
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } }
            catch (Exception) { /* already gone */ }
            p.Dispose();
        }
        _loopbacks.Clear();
        _filters.Clear();

        int left = 0;
        for (int i = _modules.Count - 1; i >= 0; i--)
        {
            try { Run("pactl", "unload-module", _modules[i].ToString()); }
            catch (Exception) when (_stopDeadline.IsCancellationRequested) { left++; }
            catch (Exception) { /* already unloaded */ }
        }
        ModulesLeft = left;
        _modules.Clear();
        try { wine.Wait(WineSession.Deadline + TimeSpan.FromSeconds(1)); }
        catch (Exception) { /* EndWine swallows its own failures; this is the join */ }
    }

    // Standalone callers without a subscription retain a short-lived snapshot.
    // A daemon subscription owns its cache while it is connected; while it
    // reconnects, the same one-shot dump keeps the sweep working, so routing,
    // default enforcement and route repair never wait on the registry.
    private readonly object DumpGate = new();
    private JsonElement[]? _dumpObjects;
    private long _dumpAt;   // monotonic ms
    private static readonly TimeSpan DumpWindow = TimeSpan.FromMilliseconds(400);

    private void InvalidateDump()
    {
        lock (DumpGate) _dumpObjects = null;
    }

    // Parse only changed batches in the daemon; standalone helpers parse each
    // one-shot dump once, shared by their devices, streams and volume queries.
    private JsonElement[] GraphObjects()
    {
        lock (DumpGate)
        {
            if (_graph is not null && _graph.IsReady) return _graph.Read();
            if (_dumpObjects is not null && Environment.TickCount64 - _dumpAt < DumpWindow.TotalMilliseconds) return _dumpObjects;
            _dumpObjects = ParseObjects(RunBytes("pw-dump"));
            _dumpAt = Environment.TickCount64;
            return _dumpObjects;
        }
    }

    // Only a live subscription is authoritative here. Standalone callers and
    // a reconnect keep the existing helper reads, rather than a stale cache.
    private JsonElement[]? LiveObjects()
    {
        lock (DumpGate)
        {
            if (_graph is null || !_graph.IsReady) return null;
            try { return _graph.Read(); }
            catch (IOException) { return null; } // disconnected between the two reads
        }
    }

    private static JsonElement? FindSink(JsonElement[] objects, string name)
    {
        foreach (JsonElement item in objects)
            if (item.TryGetProperty("type", out var type) && IsString(type, "PipeWire:Interface:Node")
                && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty("media.class", out var kind) && IsString(kind, "Audio/Sink")
                && props.TryGetProperty("node.name", out var node) && IsString(node, name))
                return info;
        return null;
    }

    private static bool IsString(JsonElement value, string expected)
        => value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private (string? Sink, string? Source)? _defaults;
    private long _defaultsRevision = -1, _defaultsAt, _defaultsEpoch;
    private bool _readingDefaults;

    private void InvalidateObservedDefaults()
    {
        lock (DumpGate)
        {
            _defaultsRevision = -1;
            _defaultsEpoch++;
        }
    }

    private (string? Sink, string? Source)? ReadObservedDefaults()
    {
        PipeWireGraph graph;
        long revision, epoch;
        lock (DumpGate)
        {
            if (_graph is null || !_graph.IsReady) return null;
            graph = _graph;
            revision = graph.DefaultRevision;
            epoch = _defaultsEpoch;
            // When the paired query fails, direct queries still read current
            // defaults. Give the paired query a short break between attempts.
            int refreshMs = _defaults is null ? 5000 : 30000;
            if (_defaultsRevision == revision && Environment.TickCount64 - _defaultsAt < refreshMs)
                return _defaults;
            if (_readingDefaults) return null;
            _readingDefaults = true;
        }

        try
        {
            // Metadata deltas can omit a deleted key. Refresh both defaults
            // through Pulse without making registry readers wait for it.
            var defaults = QueryDefaultPair();
            lock (DumpGate)
            {
                if (!ReferenceEquals(_graph, graph) || !graph.IsReady
                    || graph.DefaultRevision != revision || _defaultsEpoch != epoch) return null;
                _defaults = defaults;
                _defaultsRevision = revision;
                _defaultsAt = Environment.TickCount64;
                return defaults;
            }
        }
        finally
        {
            lock (DumpGate) _readingDefaults = false;
        }
    }

    private (string? Sink, string? Source)? QueryDefaultPair()
    {
        try
        {
            using var doc = JsonDocument.Parse(RunBytes("pactl", "--format=json", "info"));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("default_sink_name", out var sink) || sink.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("default_source_name", out var source) || source.ValueKind != JsonValueKind.String)
                return null;
            return (sink.GetString(), source.GetString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException) { return null; }
    }

    private (long Revision, IReadOnlyList<string>? Names) _inputPorts = (-1, null), _outputPorts = (-1, null);

    private IReadOnlyList<string>? ReadObservedPorts(bool output)
    {
        lock (DumpGate)
        {
            ref var cached = ref (output ? ref _outputPorts : ref _inputPorts);
            if (_graph?.ReadPortSnapshot(cached.Revision) is not { } ports) return null;
            // Volume, metadata and link updates do not change port names.
            if (ports.Snapshot is { } snapshot)
                cached = (ports.Revision, ObservedPorts(snapshot, output));
            return cached.Names;
        }
    }

    internal static IReadOnlyList<string> ObservedPorts(IEnumerable<JsonElement> objects, bool output)
    {
        JsonElement[] snapshot = objects as JsonElement[] ?? objects.ToArray();
        var nodes = new Dictionary<uint, string>();
        foreach (JsonElement item in snapshot)
            if (item.TryGetProperty("type", out var type) && type.GetString() == "PipeWire:Interface:Node"
                && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out uint nodeId)
                && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("props", out var props)
                && props.TryGetProperty("node.name", out var name) && name.ValueKind == JsonValueKind.String)
                nodes[nodeId] = name.GetString()!;
        var ports = new List<(uint NodeId, uint PortId, string Name)>();
        foreach (JsonElement item in snapshot)
            if (item.TryGetProperty("type", out var type) && type.GetString() == "PipeWire:Interface:Port"
                && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("direction", out var direction) && direction.GetString() == (output ? "output" : "input")
                && info.TryGetProperty("props", out var props) && props.TryGetProperty("node.id", out var owner)
                && uint.TryParse(owner.ToString(), out uint nodeId) && nodes.TryGetValue(nodeId, out string? node)
                && props.TryGetProperty("port.name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                uint order = props.TryGetProperty("port.id", out var id) && uint.TryParse(id.ToString(), out uint parsed) ? parsed : uint.MaxValue;
                ports.Add((nodeId, order, $"{node}:{name.GetString()}"));
            }
        return ports.OrderBy(p => p.NodeId).ThenBy(p => p.PortId).Select(p => p.Name).ToArray();
    }

    private static JsonElement[] ParseObjects(byte[] json)
    {
        try
        {
            using JsonDocument doc = PipeWireSnapshot.Parse(json);
            return doc.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private byte[] RunBytes(string exe, params string[] args) => RunBytes(exe, ProcessRunner.DefaultStdoutCap, args);

    private byte[] RunBytes(string exe, int stdoutCap, params string[] args)
    {
        try { return RunBytesCore(exe, args, _stopDeadline, stdoutCap); }
        finally { _progress?.Invoke(); }
    }

    private static byte[] RunBytesCore(string exe, string[] args, CancellationToken stopDeadline, int stdoutCap)
    {
        // Bounded: 5 s and the caller's output cap (64 MiB for graph dumps), the
        // process tree killed past either, so a runaway helper never grows
        // the daemon's heap.
        ProcessResult r = ProcessRunner.Run(exe, args, stdoutCap: stdoutCap, cancel: stopDeadline);
        if (r.Cancelled) throw StoppedHelper(exe, args);
        if (r.TimedOut)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} timed out after {ProcessRunner.DefaultTimeout.TotalSeconds:0} seconds");
        if (r.Truncated)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} produced more than {stdoutCap} bytes");
        if (r.Incomplete)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: output ended before the helper closed it");
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: {r.Stderr.Trim()}");
        return r.Stdout;
    }

    internal string Run(string exe, params string[] args)
    {
        if (exe == "pw-link") Interlocked.Increment(ref _linkHelperStarts);
        try { return RunCore(exe, args, _stopDeadline); }
        finally { _progress?.Invoke(); }
    }

    private long _linkHelperStarts;
    internal long LinkHelperStarts => Interlocked.Read(ref _linkHelperStarts);

    private static InvalidOperationException StoppedHelper(string exe, string[] args)
        => new($"{exe} {string.Join(' ', args)} not finished: the daemon's stop deadline passed");

    private static string RunCore(string exe, string[] args, CancellationToken stopDeadline)
    {
        // pactl's human-readable output is parsed ("Sink Input #", "Owner
        // Module:") and pactl is localised; a German desktop would break
        // every fader. The runner puts every helper in the C locale and
        // bounds its time and output.
        ProcessResult r = ProcessRunner.Run(exe, args, cancel: stopDeadline);
        if (r.Cancelled) throw StoppedHelper(exe, args);
        if (r.TimedOut)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} timed out after {ProcessRunner.DefaultTimeout.TotalSeconds:0} seconds");
        if (r.Truncated)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} produced more than {ProcessRunner.DefaultStdoutCap} bytes");
        if (r.Incomplete)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: output ended before the helper closed it");
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} failed: {r.Stderr.Trim()}");
        return r.StdoutText;
    }
}

/// <summary>A running fader (one channel's send into one mix).</summary>
public sealed record LoopbackHandle(string Id, string CaptureNodeName, string PlaybackNodeName, Process Process);

/// <summary>A loaded filter-chain: sink half (input), source half (output),
/// and the pw-cli process whose lifetime is the module's.</summary>
public sealed record FilterHandle(string Id, string SinkName, string SourceName, Process Process)
{
    internal NativePluginHost? NativeHost { get; init; }
    internal IReadOnlyList<FilterHandle> Stages { get; init; } = [];
    internal IReadOnlyList<(string Id, FilterHandle Stage)> InsertStages { get; init; } = [];
    /// <summary>
    /// Why PipeWire's filter chain refused the LV2 insert this native stage
    /// runs in its place; null for a stage that runs where it was saved to.
    /// </summary>
    internal string? FilterChainError { get; init; }
    internal bool IsAlive => Stages.Count > 0 ? Stages.All(stage => stage.IsAlive)
        : NativeHost?.IsHealthy ?? !Process.HasExited;
    /// <summary>
    /// What the native helper that brought this chain down last said, or
    /// null when no helper died or it died saying nothing.
    /// </summary>
    internal string? LastWords => Stages.Count > 0
        ? Stages.Select(stage => stage.LastWords).FirstOrDefault(words => words is not null)
        : NativeHost is { IsHealthy: false } host && host.LastMessage.Trim() is { Length: > 0 } said ? said : null;
}

/// <summary>
/// What the helper's probe found in a clip file: the extension the copy is
/// stored under, its channels, and its own rate and length.
/// </summary>
internal sealed record ClipProbe(string Extension, int Channels, int Rate, long Frames)
{
    public double Seconds => (double)Frames / Rate;
}

/// <summary>Whether an optional host-side DSP feature can be loaded safely.</summary>
public sealed record DspFeatureAvailability(bool Available, string? Error);

/// <summary>A set of direct port links between two nodes.</summary>
public sealed record PortLink(IReadOnlyList<(string From, string To)> Pairs)
{
    /// <summary>False when a discovered pair could not be linked while the route was made.</summary>
    internal bool Complete { get; init; } = true;
}

/// <summary>Outcome of verifying a <see cref="PortLink"/>'s pairs.</summary>
public enum LinkHealth { Healthy, Relinked, Broken }

public enum AudioNodeKind { Sink, Source }

/// <summary>
/// A selectable audio device. PipeWire does not distinguish real hardware from
/// virtual nodes, so both appear here; <paramref name="IsOwn"/> marks the nodes
/// OpenXLR itself created, and <paramref name="IsPhysical"/> marks real
/// hardware (device.api present), letting pickers filter to actual devices.
/// </summary>
public sealed record AudioNode(string Name, string Description, AudioNodeKind Kind, bool IsOwn,
    bool IsPhysical = false)
{
    /// <summary>A sink's desktop volume (1.0 = 100%), null for sources and pseudo-outputs.</summary>
    public double? Volume { get; init; }
    /// <summary>A sink's mute, null for sources and pseudo-outputs.</summary>
    public bool? Muted { get; init; }
    /// <summary>A source's channel count from <c>audio.channels</c>, null for sinks and when PipeWire does not say.</summary>
    public int? Channels { get; init; }
    /// <summary>A source's channel positions in port order ("MONO", "FL", "AUX2"), null for sinks and when unknown.</summary>
    public IReadOnlyList<string>? Positions { get; init; }
    /// <summary>
    /// The instance id of the attached supported interface whose card
    /// publishes this node, or null. Set by the daemon, which knows the
    /// attached units; a model id when two attached units of one model
    /// cannot be told apart by their node names.
    /// </summary>
    public string? DrivenUnit { get; init; }
}

/// <summary>One of the daemon's own DSP stage streams: its Pulse stream index, direction, linear volume and mute.</summary>
public sealed record OwnStageStream(string Name, int Serial, bool Input, double Volume, bool Muted);

/// <summary>One of the daemon's own sinks: its linear volume (1.0 = unity) and mute.</summary>
public sealed record OwnSinkLevel(string Name, double Volume, bool Muted)
{
    /// <summary>Desktop volume, where 1.0 means 100%; separate from raw amplitude.</summary>
    public double DesktopVolume { get; init; }
}
