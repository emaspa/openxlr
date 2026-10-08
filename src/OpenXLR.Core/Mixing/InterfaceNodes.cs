namespace OpenXLR.Core.Mixing;

/// <summary>
/// Finding one interface's PipeWire nodes by a name fragment. A card can
/// publish several nodes (the Wave XLR Pro's UCM split has one per jack), so
/// matches are grouped by card: nodes of one card are one interface, nodes of
/// two cards are two units that the fragment cannot tell apart.
/// </summary>
public static class InterfaceNodes
{
    /// <summary>
    /// The card part of a node name: for an ALSA node the text between the
    /// "alsa_input." or "alsa_output." prefix and the next dot
    /// ("usb-Elgato_Systems_Elgato_Wave_XLR_SERIAL-00"); any other node is
    /// its own card.
    /// </summary>
    public static string CardKey(string nodeName)
    {
        int start = 0;
        if (nodeName.StartsWith("alsa_", StringComparison.Ordinal) && nodeName.IndexOf('.') is int dot and > 0)
            start = dot + 1;
        int end = nodeName.IndexOf('.', start);
        return end < 0 ? nodeName[start..] : nodeName[start..end];
    }

    /// <summary>
    /// The nodes, in their given order, of the one card that the first
    /// fragment naming any node names. <paramref name="matched"/> is that
    /// fragment. When it names nodes of more than one card the result is
    /// empty and <paramref name="ambiguous"/> is true; a later, broader
    /// fragment would only name more of them, so none is tried.
    /// </summary>
    public static List<AudioNode> Pick(IEnumerable<AudioNode> nodes, IReadOnlyList<string> fragments,
        out string? matched, out bool ambiguous)
    {
        matched = null;
        ambiguous = false;
        List<AudioNode> all = [.. nodes];
        foreach (string fragment in fragments)
        {
            if (fragment.Length == 0) continue;
            List<AudioNode> found = [.. all.Where(n => n.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))];
            if (found.Count == 0) continue;
            matched = fragment;
            if (found.Select(n => CardKey(n.Name)).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                ambiguous = true;
                return [];
            }
            return found;
        }
        return [];
    }
}
