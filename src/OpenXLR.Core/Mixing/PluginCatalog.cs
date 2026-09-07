namespace OpenXLR.Core.Mixing;

/// <summary>
/// Every plugin an insert can hold, whichever format it comes in. The LV2
/// list is read through lilv in this process; the CLAP list comes from the
/// native host, one scan per bundle. Both are read once and kept.
/// </summary>
public static class PluginCatalog
{
    private static readonly Lazy<IReadOnlyList<PluginInfo>> All = new(
        () => Lv2Catalog.WithinBudget([.. Lv2Catalog.Plugins, .. ClapCatalog.Plugins]),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The whole catalogue, within the size a client is sent (blocks on the first call).</summary>
    public static IReadOnlyList<PluginInfo> Plugins => All.Value;

    /// <summary>Kick both scans off without waiting for them.</summary>
    public static void Warm() => ThreadPool.QueueUserWorkItem(_ => { try { _ = All.Value; } catch (Exception) { } });

    /// <summary>The plugin an insert names, by its kind and its identifier.</summary>
    public static PluginInfo? Find(string kind, string plugin)
        => Plugins.FirstOrDefault(p => p.Kind == kind && p.Plugin == plugin);

    public static PluginInfo? Find(InsertDefinition insert) => Find(insert.Kind, insert.Plugin);

    /// <summary>Whether the optional native host is installed beside the daemon.</summary>
    public static bool HostInstalled => NativePluginHost.HostInstalled;
}
