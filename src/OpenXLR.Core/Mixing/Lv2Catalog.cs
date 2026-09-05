using System.Runtime.InteropServices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The installed LV2 plugins, read through lilv (the same library PipeWire's
/// filter-chain uses to load them, so whatever this lists is loadable in
/// principle). Scanned once, lazily, on a background thread: lilv's
/// load_all walks every bundle on LV2_PATH and can take a second on a
/// system with the big collections installed.
///
/// Only what an insert UI needs is read: names, audio port counts, the
/// control ports with their ranges and hints, and the required features
/// (so a plugin the host cannot satisfy can be flagged instead of failing
/// silently at load).
/// </summary>
public static class Lv2Catalog
{
    private static readonly Lazy<IReadOnlyList<PluginInfo>> Scan = new(ScanNow, LazyThreadSafetyMode.ExecutionAndPublication);

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

    /// <summary>Kick the scan off without waiting for it.</summary>
    public static void Warm() => ThreadPool.QueueUserWorkItem(_ => { try { _ = Scan.Value; } catch (Exception) { } });

    public static PluginInfo? Find(string uri) => Plugins.FirstOrDefault(p => p.Plugin == uri);

    private static IReadOnlyList<PluginInfo> ScanNow()
    {
        var result = new List<PluginInfo>();
        IntPtr world;
        try { world = Lilv.lilv_world_new(); }
        catch (DllNotFoundException) { return result; }   // no lilv: no LV2 inserts, nothing else affected
        if (world == IntPtr.Zero) return result;
        try
        {
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
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
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
        string name = Lilv.OwnedString(Lilv.lilv_plugin_get_name(plugin)) ?? uri;
        string category = "";
        IntPtr cls = Lilv.lilv_plugin_get_class(plugin);
        if (cls != IntPtr.Zero)
            category = Lilv.Str(Lilv.lilv_node_as_string(Lilv.lilv_plugin_class_get_label(cls))) ?? "";

        uint n = Lilv.lilv_plugin_get_num_ports(plugin);
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
                if (isIn) { audioIns++; inSym ??= sym; inSyms.Add(sym); }
                else if (Lilv.lilv_port_is_a(plugin, port, outputPort)) { audioOuts++; outSym ??= sym; outSyms.Add(sym); }
                continue;
            }
            if (!isIn || !Lilv.lilv_port_is_a(plugin, port, controlPort)) continue;
            if (Lilv.lilv_port_has_property(plugin, port, notOnGui)) continue;
            string pname = Lilv.OwnedString(Lilv.lilv_port_get_name(plugin, port)) ?? sym;
            var points = new List<ScalePoint>();
            IntPtr sps = Lilv.lilv_port_get_scale_points(plugin, port);
            if (sps != IntPtr.Zero)
            {
                for (IntPtr sit = Lilv.lilv_scale_points_begin(sps); !Lilv.lilv_scale_points_is_end(sps, sit); sit = Lilv.lilv_scale_points_next(sps, sit))
                {
                    IntPtr sp = Lilv.lilv_scale_points_get(sps, sit);
                    string? lbl = Lilv.Str(Lilv.lilv_node_as_string(Lilv.lilv_scale_point_get_label(sp)));
                    IntPtr vn = Lilv.lilv_scale_point_get_value(sp);
                    double v = Lilv.lilv_node_is_float(vn) || Lilv.lilv_node_is_int(vn) ? Lilv.lilv_node_as_float(vn) : 0;
                    if (lbl is not null) points.Add(new ScalePoint(lbl, v));
                }
                Lilv.lilv_scale_points_free(sps);
            }
            float min = float.IsNaN(mins[i]) ? 0 : mins[i];
            float max = float.IsNaN(maxs[i]) ? 1 : maxs[i];
            float def = float.IsNaN(defs[i]) ? min : defs[i];
            pars.Add(new PluginParam(sym, pname, min, max, def,
                Lilv.lilv_port_has_property(plugin, port, toggled),
                Lilv.lilv_port_has_property(plugin, port, integer),
                Lilv.lilv_port_has_property(plugin, port, logarithmic),
                Lilv.lilv_port_has_property(plugin, port, enumeration),
                points));
        }
        if (audioIns == 0 || audioOuts == 0 || inSym is null || outSym is null) return null;   // generators and analysers are not inserts

        var features = new List<string>();
        IntPtr req = Lilv.lilv_plugin_get_required_features(plugin);
        if (req != IntPtr.Zero)
        {
            for (IntPtr fit = Lilv.lilv_nodes_begin(req); !Lilv.lilv_nodes_is_end(req, fit); fit = Lilv.lilv_nodes_next(req, fit))
            {
                string? f = Lilv.Str(Lilv.lilv_node_as_uri(Lilv.lilv_nodes_get(req, fit)));
                if (f is not null) features.Add(f);
            }
            Lilv.lilv_nodes_free(req);
        }
        IReadOnlyList<string>? uiFeatures = X11UiRequiredFeatures(world, plugin);
        return new PluginInfo("lv2", uri, name, category, audioIns, audioOuts, inSym, outSym, pars, features, inSyms, outSyms)
        {
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
