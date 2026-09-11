namespace OpenXLR.Core.Mixing;

/// <summary>Bounded evidence from the last completed scan of each native format.</summary>
public sealed record PluginScanEntry(string Path, string Outcome, bool Cached = false,
    int Plugins = 0, int Duplicates = 0, int? ExitCode = null, string? Detail = null);

public sealed record PluginScanReport(string Kind, DateTimeOffset CompletedAt,
    IReadOnlyList<PluginScanEntry> Entries, int Omitted);

/// <summary>One bundle the last scan of a format found and could not read.</summary>
public sealed record PluginScanFailure(string Kind, string Path, string Outcome, int? ExitCode);

public static class PluginScanDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PluginScanReport> Reports = new();

    public static IReadOnlyList<PluginScanReport> Snapshot()
    {
        lock (Gate) return Reports.Values.OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray();
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
        ["output-limit"] = "it described too much",
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
            .Select(e => new PluginScanFailure(r.Kind, e.Path, e.Outcome, e.ExitCode)))];

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
        return $"{count} could not be read: {names}; the daemon's log says more.";
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

        public void Add(string path, string outcome, bool cached = false, int plugins = 0,
            int duplicates = 0, int? exitCode = null, string? detail = null)
        {
            if (_entries.Count >= Limit)
            {
                _omitted++;
                if (outcome is "ok" or "directory") return;
                int replace = _entries.FindLastIndex(e => e.Outcome is "ok" or "directory");
                if (replace < 0) return;
                _entries.RemoveAt(replace);
            }
            _entries.Add(new(Clip(path, 4096)!, outcome, cached, plugins, duplicates, exitCode, ClipEnds(detail, 2048)));
        }

        public PluginScanReport Complete()
        {
            var report = new PluginScanReport(kind, DateTimeOffset.UtcNow, _entries.ToArray(), _omitted);
            lock (Gate) Reports[kind] = report;
            return report;
        }
    }

    internal static string? Clip(string? text, int limit)
        => text is { Length: > 0 } && text.Length > limit ? text[..limit] + " [truncated]" : text;

    /// <summary>Keep the startup context and later errors without letting a bridge banner fill the detail.</summary>
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
