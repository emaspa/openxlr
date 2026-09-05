using System.Diagnostics;
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
    // These strings pass through the module-argument parser and then the
    // nested property-list parser. JSON quoting preserves whitespace, quotes,
    // apostrophes and backslashes without allowing a label to inject another
    // PipeWire property.
    private static readonly JsonSerializerOptions PropertyJson = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<DspFeatureAvailability>? _clipGuardAvailabilityOverride;
    private readonly List<uint> _modules = [];
    private readonly List<Process> _loopbacks = [];
    private readonly List<Process> _filters = [];

    public PipeWireAdapter() { }

    internal PipeWireAdapter(Func<DspFeatureAvailability> clipGuardAvailabilityOverride)
        => _clipGuardAvailabilityOverride = clipGuardAvailabilityOverride;

    internal static string ModuleProperties(string description, string extra)
        => JsonSerializer.Serialize(
            $"node.description={JsonSerializer.Serialize(description, PropertyJson)} {extra}", PropertyJson);

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
    public uint CreateNullSink(string nodeName, string description, bool isInternal = false)
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
            "sink_properties=" + ModuleProperties(description,
                "node.suspend-on-idle=false priority.session=100" + InternalProperties(isInternal)));
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
            "sink_properties=" + ModuleProperties(description, "priority.session=90"));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    /// <summary>
    /// A combine sink duplicates its input into several mix sinks. Application
    /// channels feed it from a separate stable public sink; hardware inputs use
    /// its monitor directly. Its per-slave streams are the send faders.
    /// </summary>
    /// <param name="needsMonitor">True for hardware channels whose monitor is
    /// linked from the capture device; false for an internal app fan-out.</param>
    public uint CreateCombineSink(string nodeName, IEnumerable<string> slaveSinks, string description, bool needsMonitor)
    {
        string outp = Run("pactl",
            "load-module", "module-combine-sink",
            $"sink_name={nodeName}",
            $"slaves={string.Join(',', slaveSinks)}",
            // suspend-on-idle=false keeps the combine's monitor source running;
            // a suspended monitor makes the channel's level meter read silence
            // even while audio flows through the sink.
            "sink_properties=" + ModuleProperties(description,
                "priority.session=100 node.suspend-on-idle=false" + InternalProperties(true) +
                (needsMonitor ? "" : " media.class=Audio/Filter node.autoconnect=false " +
                    "adapter.auto-port-config=\"{ mode = dsp monitor = true position = preserve }\"")));
        uint id = uint.Parse(outp.Trim());
        _modules.Add(id);
        return id;
    }

    private static string InternalProperties(bool isInternal)
        => isInternal ? " openxlr.internal=true device.class=filter node.virtual=true" : " node.virtual=false";

    internal static bool IsInternalDevice(JsonElement properties)
        => properties.TryGetProperty("openxlr.internal", out JsonElement value)
            && (value.ValueKind == JsonValueKind.True ||
                value.ValueKind == JsonValueKind.String && value.GetString() == "true");

    /// <summary>Unload one module created by this adapter.</summary>
    public void UnloadModule(uint id)
    {
        _modules.Remove(id);
        try { Run("pactl", "unload-module", id.ToString()); }
        catch (InvalidOperationException) { /* already gone */ }
    }

    /// <summary>Update presentation metadata without recreating the audio node.</summary>
    public void SetSinkDescription(string nodeName, string description)
        => Run("pactl", "update-sink-proplist", nodeName,
            $"node.description={JsonSerializer.Serialize(description, PropertyJson)}");

    /// <summary>Update a virtual microphone's presentation metadata in place.</summary>
    public void SetSourceDescription(string nodeName, string description)
        => Run("pactl", "update-source-proplist", nodeName,
            $"node.description={JsonSerializer.Serialize(description, PropertyJson)}");

    /// <summary>A "sink#suffix" pseudo-device address without its suffix.</summary>
    private static string BareSink(string sinkName)
    {
        int marker = sinkName.IndexOf('#');
        return marker >= 0 ? sinkName[..marker] : sinkName;
    }

    /// <summary>Set a sink's volume.</summary>
    public void SetSinkVolume(string sinkName, double volume)
        => Run("pactl", "set-sink-volume", BareSink(sinkName),
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    /// <summary>Mute or unmute a sink.</summary>
    public void SetSinkMuted(string sinkName, bool muted)
        => Run("pactl", "set-sink-mute", BareSink(sinkName), muted ? "1" : "0");

    /// <summary>Set one sink-input's volume (used for the combine fader legs).</summary>
    public void SetSinkInputVolume(int index, double volume)
        => Run("pactl", "set-sink-input-volume", index.ToString(),
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    /// <summary>Mute or unmute one sink-input.</summary>
    public void SetSinkInputMuted(int index, bool muted)
        => Run("pactl", "set-sink-input-mute", index.ToString(), muted ? "1" : "0");

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

    /// <summary>Volume of a sink as 0..1, parsed from pactl (first channel).</summary>
    public double? GetSinkVolume(string sinkName)
        => ParseVolumePercent(TryRun("pactl", "get-sink-volume", BareSink(sinkName)));

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

    /// <summary>Volume of a source as 0..1.</summary>
    public double? GetSourceVolume(string sourceName)
        => ParseVolumePercent(TryRun("pactl", "get-source-volume", sourceName));

    public void SetSourceVolume(string sourceName, double volume)
        => Run("pactl", "set-source-volume", sourceName,
            $"{(int)Math.Round(Math.Clamp(volume, 0, 1) * 100)}%");

    private string? TryRun(string exe, params string[] args)
    {
        try { return Run(exe, args); }
        catch (InvalidOperationException) { return null; }
    }

    private static double? ParseVolumePercent(string? pactlOutput)
    {
        if (pactlOutput is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(pactlOutput, @"(\d+)%");
        return m.Success ? Math.Clamp(int.Parse(m.Groups[1].Value) / 100.0, 0, 1.5) : null;
    }

    /// <summary>The capture device applications record from by default.</summary>
    public void SetDefaultSource(string sourceName)
        => Run("pactl", "set-default-source", sourceName);

    /// <summary>The playback device applications use by default.</summary>
    public void SetDefaultSink(string sinkName)
        => Run("pactl", "set-default-sink", sinkName);

    /// <summary>Current default playback device, or null.</summary>
    public string? GetDefaultSink()
    {
        try { return Run("pactl", "get-default-sink").Trim(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Current default capture device, or null.</summary>
    public string? GetDefaultSource()
    {
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
            "source_properties=" + ModuleProperties(description,
                $"device.description={JsonSerializer.Serialize(description, PropertyJson)} " +
                "priority.session=100 node.virtual=false"));
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
        IReadOnlyList<InsertDefinition>? inserts = null)
        => CreateFilterChain($"OpenXLR_lc_{id}_in", $"OpenXLR_lc_{id}_out", "OpenXLR Mic Filter", 1, lowCutHz, clipGuard, inserts);

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
            PluginInfo? info = Lv2Catalog.Find(ins.Plugin);
            if (info is null || info.InputSymbols.Count < channels || info.OutputSymbols.Count < channels)
                continue;   // unknown or wrong-width plugin: skipped, reported by the caller
            string name = $"i{k++}";
            string controls = ins.Params.Count == 0 ? "" :
                " control = { " + string.Join(' ', ins.Params.Select(p => $"\"{p.Key}\" = {p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")) + " }";
            stages.Add(($"{{ type = lv2 name = {name} plugin = \"{ins.Plugin}\"{controls} }}",
                [.. info.InputSymbols.Take(channels).Select(s => $"{name}:{s}")],
                [.. info.OutputSymbols.Take(channels).Select(s => $"{name}:{s}")]));
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
            $"{{ node.description = \"{description}\" " +
            $"filter.graph = {{ nodes = [ {string.Join(' ', stages.Select(s => s.Node))} ] {links}" +
            $"inputs = [ {inputs} ] outputs = [ {outputs} ] }} " +
            $"capture.props = {{ node.name = {sinkName} media.class = Audio/Sink " +
            $"audio.channels = {channels} audio.position = {position} node.suspend-on-idle = false " +
            "priority.session = 100 } " +
            $"playback.props = {{ node.name = {srcName} media.class = Audio/Source " +
            $"audio.channels = {channels} audio.position = {position} node.suspend-on-idle = false " +
            "priority.session = 100 } }";
        var psi = new ProcessStartInfo("pw-cli")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("load-module");
        psi.ArgumentList.Add("libpipewire-module-filter-chain");
        psi.ArgumentList.Add(spa);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start pw-cli");
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
        bool sinkReady = WaitForPorts(sinkName, "playback", output: false, TimeSpan.FromSeconds(3), p);
        bool sourceReady = sinkReady && WaitForPorts(srcName, "capture", output: true, TimeSpan.FromSeconds(3), p);
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
    /// Change one control of a running filter-chain live: the chain exposes
    /// its plugins' controls as "node:symbol" entries of the Props param on
    /// its sink half. Throws when the node is gone or PipeWire refuses.
    /// </summary>
    public void SetFilterControl(FilterHandle f, string control, double value)
    {
        int id = FindNodeId(f.SinkName) ?? throw new InvalidOperationException($"filter node {f.SinkName} not found");
        string v = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Run("pw-cli", "set-param", id.ToString(), "Props", $"{{ params = [ \"{control}\" {v} ] }}");
    }

    /// <summary>Unload a filter by killing its holder process.</summary>
    public void StopFilter(FilterHandle f)
    {
        try { if (!f.Process.HasExited) { f.Process.Kill(entireProcessTree: true); f.Process.WaitForExit(2000); } }
        catch (Exception) { /* already gone */ }
        _filters.Remove(f.Process);
        f.Process.Dispose();
    }

    private bool WaitForPorts(string node, string prefix, bool output, TimeSpan timeout, Process owner)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (owner.HasExited) return false;
            if (ListPorts(node, prefix, output).Count > 0) return true;
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
        int toPairOffset = 0, int fromPairOffset = 0)
    {
        // Port names vary by device (FL/FR on most nodes, AUX0/AUX1 on
        // multichannel interfaces like the Wave XLR Pro), so discover the real
        // ports rather than assuming, then pair them in order. A mono source
        // into a stereo sink gets its one port linked to both inputs.
        // pw-link addresses ports by their printed name, and a duplicated
        // node (a USB sink caught mid re-enumeration lists every port twice)
        // must not yield duplicate pairs, so identical names collapse first.
        List<string> outs = [.. ListPorts(fromNode, fromPortPrefix, output: true).Distinct()];
        List<string> ins = [.. ListPorts(toNode, toPortPrefix, output: false).Distinct()];
        if (fromPairOffset > 0)
        {
            // A source without that pair feeds nothing. Falling back to the
            // first pair here used to duplicate a mono capture into every
            // channel, which made the channel mutes ineffective.
            outs = SelectPair(outs, fromPairOffset);
        }
        else if (outs.Count > 2)
            outs = [.. outs.Take(2)];
        if (toPairOffset > 0)
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
        for (int i = 0; i < ins.Count && (outs.Count > 0); i++)
        {
            string to = ins[i];
            string from = outs.FirstOrDefault(o => Chan(o) != "" && Chan(o) == Chan(to))
                ?? outs[Math.Min(i, outs.Count - 1)];
            try { Run("pw-link", from, to); pairs.Add((from, to)); }
            catch (InvalidOperationException) { /* racing a disappearing port */ }
        }
        return new PortLink(pairs);
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
    private List<string> ListPorts(string node, string prefix, bool output)
    {
        var ports = new List<string>();
        string listing = Run("pw-link", output ? "-o" : "-i");
        foreach (string line in listing.Split('\n'))
        {
            string name = line.Trim();
            if (name.StartsWith($"{node}:{prefix}", StringComparison.Ordinal))
                ports.Add(name);
        }
        return ports;
    }

    /// <summary>
    /// Make sure every pair of a link set still exists, re-creating any that
    /// died (a USB device re-enumerating destroys its node and every link on
    /// it, while the new node usually keeps the same port names). Returns
    /// Broken when a port is gone entirely, meaning the route needs a fresh
    /// port discovery instead.
    /// </summary>
    public LinkHealth EnsureLinks(PortLink link)
    {
        var health = LinkHealth.Healthy;
        foreach ((string from, string to) in link.Pairs)
        {
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
            pair = suffix == "usbaux" ? 5 : 1;   // aux mix return vs monitor-bus return
            outputSink = outputSink[..marker];
        }
        return LinkNodes(fromNode, fromPrefix, outputSink, "playback", pair);
    }

    /// <summary>
    /// Feed a capture device into a channel sink; <paramref name="sourcePair"/>
    /// selects which stereo pair of a multichannel source (0 = first).
    /// </summary>
    public PortLink RouteInputToChannel(string sourceName, string channelSink, int sourcePair = 0)
        => LinkNodes(sourceName, "capture", channelSink, "playback", 0, sourcePair);

    /// <summary>Stop one loopback (used when re-pointing a device selection).</summary>
    public void StopLoopback(LoopbackHandle lb)
    {
        try { if (!lb.Process.HasExited) { lb.Process.Kill(entireProcessTree: true); lb.Process.WaitForExit(2000); } }
        catch (Exception) { /* already gone */ }
        _loopbacks.Remove(lb.Process);
        lb.Process.Dispose();
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
    {
        var found = new List<AudioNode>();
        byte[] json = DumpJson();
        JsonDocument doc;
        try { doc = PipeWireSnapshot.Parse(json); }
        catch (JsonException) { return found; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
            {
                if (!o.TryGetProperty("type", out JsonElement t) ||
                    !(t.GetString()?.EndsWith("Node", StringComparison.Ordinal) ?? false)) continue;
                if (!o.TryGetProperty("info", out JsonElement info) ||
                    !info.TryGetProperty("props", out JsonElement props)) continue;
                if (IsInternalDevice(props)) continue;

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
                found.Add(new AudioNode(name, desc, isSink ? AudioNodeKind.Sink : AudioNodeKind.Source,
                    name.StartsWith("OpenXLR", StringComparison.Ordinal), physical));
            }
        }

        // The Pro's physical outputs all share its hardware monitor bus, fed by
        // USB playback channels 2/3 and gated per-output by the selectors in
        // block 0x0001 (decoded 2026-08-25, confirmed by ear on both jacks).
        // Each output is advertised as its own pseudo-sink; the daemon flips
        // the matching hardware selector when one is chosen as the monitor.
        // Headphones jack 1 is on the front of the unit, jack 2 on the back.
        AudioNode? pro = exposeHardwareMonitorOutputs
            ? found.FirstOrDefault(n =>
                n.Kind == AudioNodeKind.Sink &&
                (hardwareSinkHint is null ||
                 n.Name.Contains(hardwareSinkHint, StringComparison.OrdinalIgnoreCase)))
            : null;
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
    /// Binaries that are audio plumbing, not applications: they register as
    /// PipeWire clients but should never appear in the app list.
    /// </summary>
    private static readonly string[] PlumbingBinaries =
    [
        "pipewire", "pipewire-pulse", "wireplumber", "pactl", "parec", "paplay",
        "pw-cli", "pw-dump", "pw-cat", "pw-play", "pw-record", "pw-loopback",
        "pw-link", "pw-mon", "speech-dispatcher", "OpenXLR.Daemon", "OpenXLR.UI",
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

    /// <summary>
    /// Every running application that has registered with PipeWire, playing or
    /// not. Browsers, chat apps and players connect as clients the moment they
    /// initialise audio, so this is "audio-capable and running".
    /// </summary>
    public IReadOnlyList<AudioStream> ListClients()
    {
        var found = new List<AudioStream>();
        byte[] json = DumpJson();
        JsonDocument doc;
        try { doc = PipeWireSnapshot.Parse(json); }
        catch (JsonException) { return found; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
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

                found.Add(new AudioStream(0, appName, binary, null));
            }
        }
        return found;
    }

    /// <summary>
    /// Every application playback stream (what PulseAudio calls a sink-input),
    /// with the identity fields the matcher needs. OpenXLR's own loopbacks are
    /// excluded: they are plumbing, not applications.
    /// </summary>
    public IReadOnlyList<AudioStream> ListStreams()
    {
        var found = new List<AudioStream>();
        byte[] json = DumpJson();
        JsonDocument doc;
        try { doc = PipeWireSnapshot.Parse(json); }
        catch (JsonException) { return found; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
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
                string? binary = Str(props, "application.process.binary");
                if (binary is not null && binary.EndsWith(" (deleted)", StringComparison.Ordinal))
                    binary = binary[..^10];
                found.Add(new AudioStream(
                    o.GetProperty("id").GetInt32(),
                    Str(props, "application.name"),
                    binary,
                    Str(props, "media.name")) { Serial = serial });
            }
        }
        return found;

        static string? Str(JsonElement props, string key)
            => props.TryGetProperty(key, out JsonElement v) ? v.GetString() : null;
    }

    /// <summary>All audio nodes as (id, node.name, media.class).</summary>
    public IEnumerable<(int Id, string Name, string MediaClass)> DumpNodes()
    {
        byte[] json = DumpJson();
        JsonDocument doc;
        try { doc = PipeWireSnapshot.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
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
    }

    private bool WaitForNode(string nodeName, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (FindNodeId(nodeName) is not null) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Remove everything this adapter created, in reverse order.</summary>
    public void TearDown()
    {
        foreach (Process p in _loopbacks.Concat(_filters))
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } }
            catch (Exception) { /* already gone */ }
            p.Dispose();
        }
        _loopbacks.Clear();
        _filters.Clear();

        for (int i = _modules.Count - 1; i >= 0; i--)
        {
            try { Run("pactl", "unload-module", _modules[i].ToString()); }
            catch (Exception) { /* already unloaded */ }
        }
        _modules.Clear();
    }

    // The sweep asks for the graph several times a second (streams, devices,
    // routes); each pw-dump is about 2 MB of JSON, so one snapshot serves a
    // whole sweep. Staleness is bounded by the window below.
    private static readonly object DumpGate = new();
    private static byte[]? _dumpJson;
    private static DateTime _dumpAt;
    private static readonly TimeSpan DumpWindow = TimeSpan.FromMilliseconds(400);

    // Kept as UTF-8 bytes and parsed from them: the string form is twice
    // the size and was the bulk of the daemon's large-object garbage.
    private static byte[] DumpJson()
    {
        lock (DumpGate)
        {
            if (_dumpJson is not null && DateTime.UtcNow - _dumpAt < DumpWindow) return _dumpJson;
            _dumpJson = RunBytes("pw-dump");
            _dumpAt = DateTime.UtcNow;
            return _dumpJson;
        }
    }

    private static byte[] RunBytes(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["LC_ALL"] = "C";
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        var stdout = new MemoryStream();
        Task copy = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task<string> stderrTask = DrainKeepingHead(p.StandardError.BaseStream);
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} timed out after 5 seconds");
        }
        copy.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)}: {stderrTask.GetAwaiter().GetResult().Trim()}");
        return stdout.ToArray();
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        // pactl's human-readable output is parsed ("Sink Input #", "Owner
        // Module:") and pactl is localised; a German desktop would break
        // every fader. Every helper runs in the C locale.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANGUAGE"] = "C";
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException(
                $"{exe} {string.Join(' ', args)} timed out after 5 seconds");
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} failed: {stderr.Trim()}");
        return stdout;
    }
}

/// <summary>A running fader (one channel's send into one mix).</summary>
public sealed record LoopbackHandle(string Id, string CaptureNodeName, string PlaybackNodeName, Process Process);

/// <summary>A loaded filter-chain: sink half (input), source half (output),
/// and the pw-cli process whose lifetime is the module's.</summary>
public sealed record FilterHandle(string Id, string SinkName, string SourceName, Process Process);

/// <summary>Whether an optional host-side DSP feature can be loaded safely.</summary>
public sealed record DspFeatureAvailability(bool Available, string? Error);

/// <summary>A set of direct port links between two nodes.</summary>
public sealed record PortLink(IReadOnlyList<(string From, string To)> Pairs);

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
    bool IsPhysical = false);
