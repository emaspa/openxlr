using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI;

internal static class DisplayOrder
{
    // Keep missing and newly introduced tiles reachable, and ignore stale ids.
    internal static string[] Complete(IEnumerable<string>? saved, IReadOnlyList<string> available)
        => (saved ?? []).Concat(available).Where(available.Contains).Distinct(StringComparer.Ordinal).ToArray();

    internal static string[] Place(IReadOnlyList<string> current, string source, string target, bool after)
    {
        var result = current.ToList();
        if (source == target || !result.Contains(source) || !result.Contains(target)) return result.ToArray();
        result.Remove(source);
        result.Insert(result.IndexOf(target) + (after ? 1 : 0), source);
        return result.ToArray();
    }
}
