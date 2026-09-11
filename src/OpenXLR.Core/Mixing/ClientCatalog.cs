using System.Runtime.CompilerServices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The catalogue as a client is sent it, which is the only place a size
/// limit belongs: a client reads one message and drops anything longer, while
/// the daemon resolves an insert against everything installed. Nothing here
/// is on the path that loads a chain, and <see cref="PluginCatalog"/> does
/// not call into it: a lookup that went through this list is the defect the
/// split exists to prevent.
/// </summary>
public static class ClientCatalog
{
    // What was last built, against the catalogue it was built from. A merge
    // costs a comparison per plugin per format and a client may ask as often
    // as its command budget allows, so the answer is kept; it is kept only as
    // long as that catalogue is, so a rescan drops it with the plugins in it.
    private sealed class Sent
    {
        public string? Key;
        public IReadOnlyList<PluginInfo> List = [];
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<PluginInfo>, Sent> Recent = [];
    private static readonly object Gate = new();

    /// <summary>
    /// The plugins to offer, out of everything installed: within the size a
    /// client will read, with the plugins the saved chains use kept whatever
    /// the budget. A client names an insert and draws its controls from this
    /// list, so a plugin the daemon loads and the list leaves out would show
    /// a working chain as a broken one.
    /// </summary>
    public static IReadOnlyList<PluginInfo> ForClient(IReadOnlyList<PluginInfo> all,
        IEnumerable<(string Kind, string Plugin)> inUse)
    {
        HashSet<(string Kind, string Plugin)> used = [.. inUse];
        string key = string.Join('\n', used.Select(u => u.Kind + ' ' + u.Plugin).Order(StringComparer.Ordinal));
        Sent sent = Recent.GetValue(all, _ => new Sent());
        lock (Gate)
            if (sent.Key == key) return sent.List;
        // LV2 first, then each other format in the order it was scanned in.
        IReadOnlyList<PluginInfo>[] others = [.. all.Where(p => p.Kind != "lv2")
            .GroupBy(p => p.Kind, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<PluginInfo>)g.ToList())];
        List<PluginInfo> list = Merge(used, [.. all.Where(p => p.Kind == "lv2")], others);
        lock (Gate)
        {
            sent.Key = key;
            sent.List = list;
        }
        return list;
    }

    /// <summary>
    /// One list within the size a client is sent. The plugins the saved
    /// chains use come first and are charged to the budget first, so a chain
    /// the daemon can build is one a client can name and draw controls for.
    /// LV2 comes next and whole, as it always did; the other formats take the
    /// room that is left. When everything fits, every copy of a plugin is
    /// offered. When it does not, a format's copies of plugins already listed
    /// go before anything distinct, and then its largest entries, so LV2
    /// never loses a plugin to a duplicate of itself.
    /// </summary>
    internal static List<PluginInfo> Merge(IReadOnlyList<PluginInfo> lv2, params IReadOnlyList<PluginInfo>[] others)
        => Merge(new HashSet<(string Kind, string Plugin)>(), lv2, others);

    internal static List<PluginInfo> Merge(IReadOnlyCollection<(string Kind, string Plugin)> inUse,
        IReadOnlyList<PluginInfo> lv2, params IReadOnlyList<PluginInfo>[] others)
    {
        // The chains' own plugins, smallest first so a monster cannot take
        // the room several ordinary ones need. There are only ever a few
        // chains' worth of them, but the budget still ends the list: a
        // message a client drops would leave the picker with nothing at all.
        var pinned = new HashSet<PluginInfo>();
        long used = 0;
        if (inUse.Count > 0)
            foreach (PluginInfo p in lv2.Concat(others.SelectMany(f => f))
                .Where(p => inUse.Contains((p.Kind, p.Plugin)))
                .OrderBy(Lv2Catalog.Footprint))
            {
                long size = Lv2Catalog.Footprint(p);
                if (used + size > Lv2Catalog.CatalogBudgetBytes) break;
                if (pinned.Add(p)) used += size;
            }
        List<PluginInfo> withinBudget = Lv2Catalog.WithinBudget(
            [.. lv2.Where(p => !pinned.Contains(p))], Lv2Catalog.CatalogBudgetBytes - used);
        used += withinBudget.Sum(p => (long)Lv2Catalog.Footprint(p));
        var keptLv2 = new HashSet<PluginInfo>(withinBudget);
        List<PluginInfo> kept = [.. lv2.Where(p => pinned.Contains(p) || keptLv2.Contains(p))];
        foreach (IReadOnlyList<PluginInfo> format in others)
        {
            kept.AddRange(format.Where(pinned.Contains));
            // Distinct plugins first, smallest first, then the copies with
            // whatever room is left; the first that does not fit ends it.
            var listed = kept.ToList();
            foreach (PluginInfo p in format.Where(p => !pinned.Contains(p))
                .OrderBy(p => listed.Any(k => SamePlugin(k, p))).ThenBy(Lv2Catalog.Footprint))
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
}
