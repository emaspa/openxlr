namespace OpenXLR.Core.Mixing;

/// <summary>
/// What feeds a monitor output: one monitor mix id, or several joined with
/// '+' ("monitor+monitor2" is Monitor A and Monitor B summed into the same
/// output, so one pair of headphones can carry the desktop from A and a
/// separately processed microphone from B). The state, profiles and the
/// setMonitorFeed command all carry the feed in this form.
/// </summary>
public static class MonitorFeed
{
    public const char Separator = '+';

    /// <summary>The mix ids in a feed, in the order written, blanks dropped.</summary>
    public static IReadOnlyList<string> Parts(string? feed)
        => feed is null ? [] : [.. feed.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static string Join(IEnumerable<string> mixIds) => string.Join(Separator, mixIds);

    public static bool Includes(string? feed, string mixId) => Parts(feed).Contains(mixId);
}
