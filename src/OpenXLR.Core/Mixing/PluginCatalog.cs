namespace OpenXLR.Core.Mixing;

/// <summary>
/// Every plugin an insert can hold, whichever format it comes in. The LV2
/// list comes from one scan by the native host (through lilv in this process
/// on a build without it); the CLAP and VST3 lists come from the native
/// host, one scan per bundle. All of them are read once and
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
    /// A refresh is something the user asked for: an install, a sync, a
    /// folder change or a rescan. So it also asks again about the bundles
    /// that failed to describe themselves, which the catalogue the daemon
    /// builds on its own passes by. Rescanning is how a user who has fixed
    /// a broken plugin gets it looked at again.
    ///
    /// The new catalogue is built aside and takes the old one's place only
    /// once it is complete. Until then every reader gets the old catalogue
    /// without waiting: the mixer resolves inserts under its own lock for
    /// every state push, and a lookup that waited for a scan of several
    /// minutes would hold every command and the sweep with it, long enough
    /// for the service watchdog to stop the daemon. Module descriptions are
    /// read as a stream, so the old catalogue and the one being built fit in
    /// the heap together; the old one is swept as soon as it is replaced.
    /// Whatever a caller still holds keeps its own copy alive past that, so a
    /// caller that only wants to compare should keep the plugins' names, not
    /// the plugins.
    /// </summary>
    public static IReadOnlyList<PluginInfo> Refresh()
    {
        Lv2Catalog.Refresh(retryFailures: true);
        ClapCatalog.Refresh(retryFailures: true);
        Vst3Catalog.Refresh(retryFailures: true);
        All.Refresh();
        IReadOnlyList<PluginInfo> plugins = All.Value;
        // The old catalogue goes now. Without the native host lilv builds and
        // frees a large model in this process to answer this; without the
        // trim the C library keeps every megabyte of it for a use that never
        // comes, and a few rescans cost more memory than the daemon ever
        // needs.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        NativeHeap.Trim();
        return plugins;
    }

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

    /// <summary>Plugins in the catalogue not among <paramref name="known"/>, only those that came from one of these directories when any are given.</summary>
    public static int CountUnder(IEnumerable<PluginInfo> catalogue, IReadOnlyCollection<string> directories, HashSet<(string Kind, string Plugin)> known)
        => catalogue.Count(p => !known.Contains((p.Kind, p.Plugin))
            && (directories.Count == 0 || p.Path is null || directories.Any(d => p.Path.StartsWith(d, StringComparison.Ordinal))));

    /// <summary>
    /// Take out of the catalogue, now and without a scan, the plugins under
    /// these directories whose file is gone. Whatever removes plugin files
    /// calls this before inserts can be made again: the scan that follows
    /// can take minutes, and an insert checked against a catalogue that still
    /// lists a removed plugin would be made from files that are not there.
    /// Nothing happens before the first catalogue is built.
    /// </summary>
    public static void WithdrawMissing(IReadOnlyCollection<string> directories)
    {
        string[] roots = [.. directories.Select(WindowsPluginWrappers.Normalize)];
        All.Amend(plugins => Present(plugins, roots));
    }

    /// <summary>The catalogue without the plugins under <paramref name="roots"/> whose file is gone; the same list when none is.</summary>
    internal static IReadOnlyList<PluginInfo> Present(IReadOnlyList<PluginInfo> catalogue, IReadOnlyCollection<string> roots)
    {
        bool Gone(PluginInfo p) => p.Path is not null
            && roots.Any(root => WindowsPluginWrappers.Under(WindowsPluginWrappers.Normalize(p.Path), root))
            && !Path.Exists(p.Path);
        return catalogue.Any(Gone) ? [.. catalogue.Where(p => !Gone(p))] : catalogue;
    }

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
