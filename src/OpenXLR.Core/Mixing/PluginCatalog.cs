namespace OpenXLR.Core.Mixing;

/// <summary>
/// Every plugin an insert can hold, whichever format it comes in. The LV2
/// list is read through lilv in this process; the CLAP and VST3 lists come
/// from the native host, one scan per bundle. All of them are read once and
/// kept, whole: an insert is resolved against everything installed, so a
/// chain that loaded yesterday loads today.
///
/// Nothing here is bounded. The size a client will read bounds the message
/// it is sent, and that lives in <see cref="ClientCatalog"/>.
/// </summary>
public static class PluginCatalog
{
    private static readonly Refreshable<IReadOnlyList<PluginInfo>> All = new(Build);

    private static IReadOnlyList<PluginInfo> Build()
        => Combine(() => Lv2Catalog.Plugins, () => ClapCatalog.Plugins, () => Vst3Catalog.Plugins);

    /// <summary>
    /// Every source's plugins as one list. Each source on its own: one that
    /// fails costs its plugins, not the catalogue. The list is read once and
    /// kept, exceptions included.
    /// </summary>
    internal static IReadOnlyList<PluginInfo> Combine(params Func<IReadOnlyList<PluginInfo>>[] sources)
    {
        var all = new List<PluginInfo>();
        foreach (Func<IReadOnlyList<PluginInfo>> source in sources)
        {
            try { all.AddRange(source()); }
            catch (Exception) { }
        }
        return all;
    }

    /// <summary>Every plugin installed, in every format (blocks on the first call).</summary>
    public static IReadOnlyList<PluginInfo> Plugins => All.Value;

    /// <summary>
    /// Read every source again, for plugins installed since. What was
    /// learnt about a bundle that did not change comes from the scan cache,
    /// so only new bundles cost a scan, and every bundle once after the
    /// native helper itself changes. Blocks until the new list is ready.
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

    /// <summary>Plugin identities supplied by exactly these files or bundle directories.</summary>
    public static HashSet<(string Kind, string Plugin)> IdentitiesUnder(IReadOnlyCollection<string> paths)
        => IdentitiesUnder(Plugins, paths);

    internal static HashSet<(string Kind, string Plugin)> IdentitiesUnder(IEnumerable<PluginInfo> catalogue, IReadOnlyCollection<string> paths)
    {
        string[] roots = [.. paths.Select(WindowsPluginWrappers.Normalize)];
        return [.. catalogue.Where(p => p.Path is not null
                && roots.Any(root => WindowsPluginWrappers.Under(WindowsPluginWrappers.Normalize(p.Path), root)))
            .Select(p => (p.Kind, p.Plugin))];
    }

    /// <summary>The same, but only for plugins that came from one of these directories.</summary>
    public static int CountUnder(IReadOnlyCollection<string> directories, HashSet<(string Kind, string Plugin)> known)
        => Plugins.Count(p => !known.Contains((p.Kind, p.Plugin))
            && (directories.Count == 0 || p.Path is null || directories.Any(d => p.Path.StartsWith(d, StringComparison.Ordinal))));

    /// <summary>
    /// Kick both scans off without waiting for them. <paramref name="then"/>
    /// runs once they are done, on the same thread: the startup scan is the
    /// one that has no user watching it, so whatever it could not read is
    /// reported from there.
    /// </summary>
    public static void Warm(Action? then = null) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try { _ = All.Value; } catch (Exception) { }
        try { then?.Invoke(); } catch (Exception) { }
    });

    /// <summary>The plugin an insert names, by its kind and its identifier.</summary>
    public static PluginInfo? Find(string kind, string plugin) => Find(Plugins, kind, plugin);

    internal static PluginInfo? Find(IReadOnlyList<PluginInfo> catalogue, string kind, string plugin)
        => catalogue.FirstOrDefault(p => p.Kind == kind && p.Plugin == plugin);

    public static PluginInfo? Find(InsertDefinition insert) => Find(insert.Kind, insert.Plugin);

    /// <summary>Whether the optional native host is installed beside the daemon.</summary>
    public static bool HostInstalled => NativePluginHost.HostInstalled;
}
