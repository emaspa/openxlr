namespace OpenXLR.Core.Mixing;

/// <summary>
/// Bounded evidence from the last completed scan of each native format.
/// <c>LogId</c> names the saved scanner output for a failed attempt, and
/// <c>LogNote</c> says why there is none. Both are absent from an entry an
/// older OpenXLR recorded, and from every entry that did not fail.
/// </summary>
public sealed record PluginScanEntry(string Path, string Outcome, bool Cached = false,
    int Plugins = 0, int Duplicates = 0, int? ExitCode = null, string? Detail = null,
    string? LogId = null, string? LogNote = null);

public sealed record PluginScanReport(string Kind, DateTimeOffset CompletedAt,
    IReadOnlyList<PluginScanEntry> Entries, int Omitted)
{
    public int SkippedFailedCount { get; init; }
    public IReadOnlyList<PluginSkippedBundle> SkippedFailedBundles { get; init; } = [];
}

public sealed record PluginSkippedBundle(string Kind, string Path, string Outcome, string Reason, DateTimeOffset? FailedAt);
public sealed record PluginSkippedScans(int Count, IReadOnlyList<PluginSkippedBundle> Bundles);

/// <summary>One bundle the last scan of a format found and could not read.</summary>
public sealed record PluginScanFailure(string Kind, string Path, string Outcome, int? ExitCode,
    string? LogId = null);

public static class PluginScanDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PluginScanReport> Reports = new();

    public static IReadOnlyList<PluginScanReport> Snapshot()
    {
        lock (Gate) return Reports.Values.OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Quiet catalogue omissions, counted independently of the bounded scan evidence.</summary>
    public static PluginSkippedScans SkippedFailures(IEnumerable<PluginScanReport>? reports = null)
    {
        var completed = (reports ?? Snapshot()).ToArray();
        return new(completed.Sum(r => r.SkippedFailedCount),
            [.. completed.SelectMany(r => r.SkippedFailedBundles).Take(Capture.Limit)]);
    }

    /// <summary>
    /// The outcomes that cost the catalogue a plugin it should have had.
    /// A folder that is not there, a bundle that describes nothing and a
    /// native host that was never installed are states a working system can
    /// be in, so they are not among them.
    /// </summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["timeout"] = "timed out",
        ["scan-failed"] = "the scanner failed",
        ["source-missing"] = "the plugin file or link target is missing",
        ["windows-module-missing"] = "the original Windows plugin is missing",
        ["windows-not-64-bit"] = "it has no 64-bit Windows module",
        ["output-limit"] = "it described too much",
        ["output-incomplete"] = "its description ended early",
        ["start-error"] = "the scanner could not start",
        ["invalid-description"] = "its description could not be read",
        ["directory-error"] = "the folder could not be read",
        ["scan-error"] = "the scan failed",
    };

    /// <summary>Every bundle the last scan of each format could not read.</summary>
    public static IReadOnlyList<PluginScanFailure> Failures() => Failures(Snapshot());

    /// <summary>The same over reports a caller already holds.</summary>
    public static IReadOnlyList<PluginScanFailure> Failures(IEnumerable<PluginScanReport> reports)
        => [.. reports.SelectMany(r => r.Entries.Where(e => Reasons.ContainsKey(e.Outcome))
            .Select(e => new PluginScanFailure(r.Kind, e.Path, e.Outcome, e.ExitCode, e.LogId)))];

    /// <summary>
    /// The failures as a sentence for the user, empty when there are none.
    /// A scan that loses a bundle used to say nothing at all: the catalogue
    /// simply came back the size it was, and the answer to a rescan read as
    /// if there had been nothing to find. Bounded the way the rest of the
    /// reply is, three names and a count, so it stays a sentence.
    /// </summary>
    public static string Sentence(IReadOnlyList<PluginScanFailure> failures)
    {
        if (failures.Count == 0) return "";
        const int Named = 3;
        string names = string.Join(", ", failures.Take(Named).Select(f =>
            $"{Name(f.Path)} ({Reasons.GetValueOrDefault(f.Outcome, f.Outcome)})"));
        if (failures.Count > Named) names += $" and {failures.Count - Named} more";
        string count = failures.Count == 1 ? "1 bundle" : $"{failures.Count} bundles";
        string recovery = failures.Any(f => f.Outcome is "source-missing" or "windows-module-missing")
            ? " Restore the original plugin files or install them again, then rescan." : "";
        // The summary here is a line; the scanner's own output is far longer
        // than a line and is kept on disk instead, so say where it went.
        string saved = failures.Any(f => f.LogId is { Length: > 0 })
            ? " The scanner output was saved for the diagnostics archive." : "";
        return $"{count} could not be read: {names}; the daemon's log says more.{saved}{recovery}";
    }

    private static string Name(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('/'));
        return name.Length == 0 ? "a plugin folder" : name;
    }

    internal sealed class Capture(string kind)
    {
        internal const int Limit = 128;
        private readonly List<PluginScanEntry> _entries = [];
        private int _omitted;
        private int _skippedFailedCount;
        private readonly List<PluginSkippedBundle> _skippedFailed = [];

        public void SkipFailure(string path, string? outcome, DateTimeOffset? failedAt)
        {
            _skippedFailedCount++;
            // Old cache entries have no reason or timestamp. Say that rather
            // than giving the current scan's time to a failure it did not run.
            string code = outcome ?? "unknown";
            if (_skippedFailed.Count < Limit)
                _skippedFailed.Add(new(kind, Clip(path, 4096)!, code,
                    Reasons.GetValueOrDefault(code, "previous scan failed; reason not recorded"), failedAt));
            Add(path, "skipped-failed", cached: true);
        }

        public void Add(string path, string outcome, bool cached = false, int plugins = 0,
            int duplicates = 0, int? exitCode = null, string? detail = null,
            string? logId = null, string? logNote = null, bool wineTrace = false)
        {
            if (_entries.Count >= Limit)
            {
                _omitted++;
                if (outcome is "ok" or "directory") return;
                int replace = _entries.FindLastIndex(e => e.Outcome is "ok" or "directory");
                if (replace < 0) return;
                _entries.RemoveAt(replace);
            }
            _entries.Add(new(Clip(path, 4096)!, outcome, cached, plugins, duplicates, exitCode,
                ScanDetail(detail, wineTrace), logId, Clip(logNote, 512)));
        }

        public PluginScanReport Complete()
        {
            var report = new PluginScanReport(kind, DateTimeOffset.UtcNow, _entries.ToArray(), _omitted)
            {
                SkippedFailedCount = _skippedFailedCount,
                SkippedFailedBundles = _skippedFailed.ToArray(),
            };
            lock (Gate) Reports[kind] = report;
            return report;
        }
    }

    internal const int TraceDetailCharacters = 16 * 1024;

    // A deep trace starts with module loads and the opening fault. Spending
    // its larger detail budget on the tail would keep the secondary unwind
    // instead. The saved log has room for more and collapses repeated lines.
    private static string? ScanDetail(string? text, bool wineTrace)
    {
        if (!wineTrace) return ClipEnds(text, 2048);
        const string marker = " [truncated]";
        return text is { Length: > TraceDetailCharacters }
            ? text[..(TraceDetailCharacters - marker.Length)] + marker : text;
    }

    internal static string? Clip(string? text, int limit)
        => text is { Length: > 0 } && text.Length > limit ? text[..limit] + " [truncated]" : text;

    /// <summary>
    /// Keep the startup context and later errors without letting a bridge
    /// banner fill the detail. What this drops is not lost: a failed attempt's
    /// bounded output goes to <see cref="PluginScanLogStore"/>, and the entry's
    /// <c>LogId</c> names it.
    /// </summary>
    internal static string? ClipEnds(string? text, int limit)
    {
        if (text is not { Length: > 0 } || text.Length <= limit) return text;
        const string marker = " [truncated] ";
        if (limit <= marker.Length) return text[..limit];
        int room = limit - marker.Length;
        int head = room / 4;
        return text[..head] + marker + text[^(room - head)..];
    }
}
