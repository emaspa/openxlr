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
    // indexes the plugin names per format and a client may ask as often
    // as its command budget allows, so the answer is kept; it is kept only as
    // long as that catalogue is, so a rescan drops it with the plugins in it.
    private sealed class Sent
    {
        public HashSet<(string Kind, string Plugin)>? Used;
        public IReadOnlyList<PluginInfo> List = [];
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<PluginInfo>, Sent> Recent = [];
    private static readonly object Gate = new();

    /// <summary>
    /// The plugins to offer, out of everything installed: within the size a
    /// client will read, with the plugins the saved chains use charged first,
    /// so they are kept before anything else. Only chains whose plugins alone
    /// pass the whole limit lose any, since a message over it reaches no
    /// client at all. A client names an insert and draws its controls from this
    /// list, so a plugin the daemon loads and the list leaves out would show
    /// a working chain as a broken one.
    /// </summary>
    public static IReadOnlyList<PluginInfo> ForClient(IReadOnlyList<PluginInfo> all,
        IEnumerable<(string Kind, string Plugin)> inUse)
    {
        HashSet<(string Kind, string Plugin)> used = [.. inUse];
        Sent sent = Recent.GetValue(all, _ => new Sent());
        lock (Gate)
            if (sent.Used is not null && sent.Used.SetEquals(used)) return sent.List;
        // LV2 first, then each other format in the order it was scanned in.
        IReadOnlyList<PluginInfo>[] others = [.. all.Where(p => p.Kind != "lv2")
            .GroupBy(p => p.Kind, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<PluginInfo>)g.ToList())];
        List<PluginInfo> list = Merge(used, [.. all.Where(p => p.Kind == "lv2")], others);
        lock (Gate)
        {
            sent.Used = used;
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
    /// offered. When it does not, copies of plugins already listed go before
    /// anything distinct in any format, and then the largest entries, so LV2
    /// never loses a plugin to a duplicate of itself and a plugin only one
    /// format offers is never left out for another format's duplicates.
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
        // The other formats share what is left. Distinct plugins from every
        // format come first, smallest first, and only then the copies: one
        // format's copies of LV2 plugins must not take the room a plugin
        // offered only in a later format needs. A distinct plugin counts as
        // listed once taken, so its copy in a later format waits with the
        // other copies. In each pass the first that does not fit ends it.
        var listed = new PluginNames([.. kept, .. others.SelectMany(f => f).Where(pinned.Contains)]);
        var chosen = new List<PluginInfo>();
        var taken = new HashSet<PluginInfo>();
        List<PluginInfo> rest = [.. others.SelectMany(f => f).Where(p => !pinned.Contains(p)).OrderBy(Lv2Catalog.Footprint)];
        foreach (PluginInfo p in rest)
        {
            if (listed.Contains(p)) continue;
            long size = Lv2Catalog.Footprint(p);
            if (used + size > Lv2Catalog.CatalogBudgetBytes) break;
            chosen.Add(p);
            taken.Add(p);
            listed.Add(p);
            used += size;
        }
        foreach (PluginInfo p in rest.Where(p => !taken.Contains(p)))
        {
            long size = Lv2Catalog.Footprint(p);
            if (used + size > Lv2Catalog.CatalogBudgetBytes) break;
            chosen.Add(p);
            used += size;
        }
        foreach (IReadOnlyList<PluginInfo> format in others)
        {
            var mine = new HashSet<PluginInfo>(format);
            kept.AddRange(format.Where(pinned.Contains));
            kept.AddRange(chosen.Where(mine.Contains));
        }
        return kept;
    }

    /// <summary>
    /// Index names once per format, rather than normalizing both names for
    /// every pair of plugins. A match has the same input/output widths and
    /// either the same name or a name preceded by a vendor prefix.
    /// </summary>
    internal sealed class PluginNames
    {
        private readonly HashSet<(int Ins, int Outs, string Name)> _whole = [];
        private readonly HashSet<(int Ins, int Outs, string Name)> _suffixes = [];

        public PluginNames(IEnumerable<PluginInfo> plugins)
        {
            foreach (PluginInfo plugin in plugins) Add(plugin);
        }

        public void Add(PluginInfo plugin)
        {
            string name = Simplified(plugin.Name);
            if (name.Length == 0 || !_whole.Add((plugin.AudioIns, plugin.AudioOuts, name))) return;
            foreach (string suffix in Suffixes(name)) _suffixes.Add((plugin.AudioIns, plugin.AudioOuts, suffix));
        }

        public bool Contains(PluginInfo plugin)
        {
            string name = Simplified(plugin.Name);
            if (name.Length == 0) return false;
            // Native metadata need not obey LV2's display-text limit. Keep
            // exact matching for long names without storing quadratically
            // many characters in their suffixes.
            if (name.Length > Lv2Catalog.MaxText)
                return _whole.Any(key => key.Ins == plugin.AudioIns && key.Outs == plugin.AudioOuts
                    && (HasSuffix(key.Name, name) || HasSuffix(name, key.Name)));
            if (_suffixes.Contains((plugin.AudioIns, plugin.AudioOuts, name))) return true;
            foreach (string suffix in Suffixes(name))
                if (_whole.Contains((plugin.AudioIns, plugin.AudioOuts, suffix))) return true;
            return false;
        }

        // Include the whole name and every suffix starting at a word boundary.
        // Comparing suffixes to suffixes would wrongly equate different vendors
        // whose full names merely share a final word.
        private static IEnumerable<string> Suffixes(string name)
        {
            if (name.Length <= Lv2Catalog.MaxText) yield return name;
            for (int space = name.IndexOf(' '); space >= 0; space = name.IndexOf(' ', space + 1))
                if (name.Length - space - 1 <= Lv2Catalog.MaxText) yield return name[(space + 1)..];
        }

        private static bool HasSuffix(string whole, string suffix)
            => whole == suffix || (whole.Length > suffix.Length && whole[whole.Length - suffix.Length - 1] == ' '
                && whole.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static string Simplified(string name)
        => string.Join(' ', name.ToLowerInvariant().Split([' ', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries));
}
