namespace OpenXLR.Core.Mixing;

/// <summary>
/// Every plugin an insert can hold, whichever format it comes in. The LV2
/// list is read through lilv in this process; the CLAP list comes from the
/// native host, one scan per bundle. Both are read once and kept.
/// </summary>
public static class PluginCatalog
{
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> All = new(Build);

    private static IReadOnlyList<PluginInfo> Build()
    {
        // Each source on its own: one that fails costs its plugins, not the
        // catalogue. The list is read once and kept, exceptions included.
        static IReadOnlyList<PluginInfo> read(Func<IReadOnlyList<PluginInfo>> source)
        {
            try { return source(); }
            catch (Exception) { return []; }
        }
        return Merge(read(() => Lv2Catalog.Plugins), read(() => ClapCatalog.Plugins), read(() => Vst3Catalog.Plugins));
    }

    /// <summary>
    /// One list within the size a client is sent. LV2 comes first and whole,
    /// as it always did; the other formats take the room that is left. When
    /// everything fits, every copy of a plugin is offered. When it does not,
    /// a format's copies of plugins already listed go before anything
    /// distinct, and then its largest entries, so LV2 never loses a plugin
    /// to a duplicate of itself.
    /// </summary>
    internal static List<PluginInfo> Merge(IReadOnlyList<PluginInfo> lv2, params IReadOnlyList<PluginInfo>[] others)
    {
        List<PluginInfo> kept = Lv2Catalog.WithinBudget([.. lv2]);
        long used = kept.Sum(p => (long)Lv2Catalog.Footprint(p));
        foreach (IReadOnlyList<PluginInfo> format in others)
        {
            // Distinct plugins first, smallest first, then the copies with
            // whatever room is left; the first that does not fit ends it.
            var listed = kept.ToList();
            foreach (PluginInfo p in format.OrderBy(p => listed.Any(k => SamePlugin(k, p))).ThenBy(Lv2Catalog.Footprint))
            {
                long size = Lv2Catalog.Footprint(p);
                if (used + size > Lv2Catalog.CatalogBudgetBytes) break;
                kept.Add(p);
                used += size;
            }
        }
        return kept;
    }

    /// <summary>
    /// The same plugin in another format: the same width, and a name that is
    /// the same or the same behind a vendor prefix, as "LSP Compressor Mono"
    /// and "Compressor Mono" are.
    /// </summary>
    internal static bool SamePlugin(PluginInfo a, PluginInfo b)
    {
        if (a.AudioIns != b.AudioIns || a.AudioOuts != b.AudioOuts) return false;
        string x = Simplified(a.Name), y = Simplified(b.Name);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || x.EndsWith(" " + y, StringComparison.Ordinal) || y.EndsWith(" " + x, StringComparison.Ordinal);
    }

    private static string Simplified(string name)
        => string.Join(' ', name.ToLowerInvariant().Split([' ', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The whole catalogue, within the size a client is sent (blocks on the first call).</summary>
    public static IReadOnlyList<PluginInfo> Plugins => All.Value;

    /// <summary>
    /// Read every source again, for plugins installed since. What was
    /// learnt about a bundle that did not change comes from the scan cache,
    /// so only new bundles cost a scan. Blocks until the new list is ready.
    ///
    /// The old catalogue is dropped first and the heap swept before the new
    /// one is built: the daemon runs under a firm heap limit, and holding
    /// two catalogues and a large module's description at once does not fit
    /// in it. Whatever a caller still holds keeps its own copy alive, so a
    /// caller that only wants to compare should keep the plugins' names,
    /// not the plugins.
    /// </summary>
    public static IReadOnlyList<PluginInfo> Refresh()
    {
        Lv2Catalog.Reset();
        ClapCatalog.Reset();
        Vst3Catalog.Reset();
        All.Reset();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        NativeHeap.Trim();
        IReadOnlyList<PluginInfo> plugins = All.Value;
        // lilv builds and frees a large model to answer this; without the
        // trim the C library keeps every megabyte of it for a use that never
        // comes, and a few rescans cost more memory than the daemon ever needs.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        NativeHeap.Trim();
        return plugins;
    }

    /// <summary>Every plugin listed now, by format and identifier: enough to tell what a rescan gained.</summary>
    public static HashSet<(string Kind, string Plugin)> Identities()
        => [.. Plugins.Select(p => (p.Kind, p.Plugin))];

    /// <summary>The same, but only for plugins that came from one of these directories.</summary>
    public static int CountUnder(IReadOnlyCollection<string> directories, HashSet<(string Kind, string Plugin)> known)
        => Plugins.Count(p => !known.Contains((p.Kind, p.Plugin))
            && (directories.Count == 0 || p.Path is null || directories.Any(d => p.Path.StartsWith(d, StringComparison.Ordinal))));

    /// <summary>Kick both scans off without waiting for them.</summary>
    public static void Warm() => ThreadPool.QueueUserWorkItem(_ => { try { _ = All.Value; } catch (Exception) { } });

    /// <summary>The plugin an insert names, by its kind and its identifier.</summary>
    public static PluginInfo? Find(string kind, string plugin)
        => Plugins.FirstOrDefault(p => p.Kind == kind && p.Plugin == plugin);

    public static PluginInfo? Find(InsertDefinition insert) => Find(insert.Kind, insert.Plugin);

    /// <summary>Whether the optional native host is installed beside the daemon.</summary>
    public static bool HostInstalled => NativePluginHost.HostInstalled;
}
