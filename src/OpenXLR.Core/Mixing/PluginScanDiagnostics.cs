namespace OpenXLR.Core.Mixing;

/// <summary>Bounded evidence from the last completed scan of each native format.</summary>
public sealed record PluginScanEntry(string Path, string Outcome, bool Cached = false,
    int Plugins = 0, int Duplicates = 0, int? ExitCode = null, string? Detail = null);

public sealed record PluginScanReport(string Kind, DateTimeOffset CompletedAt,
    IReadOnlyList<PluginScanEntry> Entries, int Omitted);

public static class PluginScanDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PluginScanReport> Reports = new();

    public static IReadOnlyList<PluginScanReport> Snapshot()
    {
        lock (Gate) return Reports.Values.OrderBy(r => r.Kind, StringComparer.Ordinal).ToArray();
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
            _entries.Add(new(Clip(path, 4096)!, outcome, cached, plugins, duplicates, exitCode, Clip(detail, 2048)));
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
}
