using System.Diagnostics;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

public sealed record PluginSearchDirectory(string Kind, string Path, bool Custom, bool Exists);

/// <summary>User additions supplement the loader's normal paths; removing one never deletes files.</summary>
public static class PluginSearchPaths
{
    public const int MaxPaths = 32;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string FilePath => OpenXlrPaths.ConfigFile("plugin-paths.json");
    public sealed record Entry(string Kind, string Path);

    public static bool Valid(string? kind, string? path) => kind is "lv2" or "clap" or "vst3" &&
        path is { Length: > 1 and <= 4096 } && Path.IsPathFullyQualified(path) &&
        !path.Any(char.IsControl) && !path.Contains(':');

    public static IReadOnlyList<Entry> Read(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return [];
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > 1024 * 1024) throw new IOException("plugin path file exceeds 1 MiB");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new IOException("plugin path file changed while reading");
            var entries = JsonSerializer.Deserialize<List<Entry>>(bytes, Json) ?? throw new JsonException("empty plugin path list");
            var kept = entries.Select(ResolveEntry).OfType<Entry>().Distinct().Take(MaxPaths).ToArray();
            if (kept.Length != entries.Count) warning = "Invalid, unreadable, duplicate or excessive plugin search paths were ignored.";
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = "Could not read plugin search paths: " + ex.Message;
            return [];
        }
    }

    // A directory can become unreadable after registration. Keep the healthy
    // entries available, but retain the warning so edits cannot erase the bad one.
    private static Entry? ResolveEntry(Entry? entry)
    {
        if (entry is null || !Valid(entry.Kind, entry.Path)) return null;
        try
        {
            string path = WindowsPluginWrappers.Canonical(entry.Path);
            return Valid(entry.Kind, path) && !BroadRoot(path) ? entry with { Path = path } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    public static InstallOutcome Change(string kind, string path, bool add)
    {
        InstallOutcome outcome = ChangeCore(kind, path, add);
        return outcome.Ok ? outcome : outcome with { RefreshCatalogue = false };
    }

    private static InstallOutcome ChangeCore(string kind, string path, bool add)
    {
        if (!Valid(kind, path)) return new(false, "Choose LV2, CLAP or VST3 and an absolute directory path without a colon or control character.", []);
        try { path = WindowsPluginWrappers.Canonical(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "Could not resolve search directory: " + ex.Message, []) { RefreshCatalogue = false }; }
        if (!Valid(kind, path) || BroadRoot(path))
            return new(false, "Choose a dedicated plugin directory, not a system or home root.", []) { RefreshCatalogue = false };
        lock (Gate)
        {
            var entries = Read(out string? warning).ToList();
            if (warning is not null) return new(false, warning + " Repair plugin-paths.json before editing it.", []);
            var entry = new Entry(kind, path);
            if (add)
            {
                if (entries.Contains(entry)) return new(true, "This search path is already registered.", []) { RefreshCatalogue = false };
                if (Effective(kind).Any(existing => Overlaps(path, existing)))
                    return new(false, "This directory overlaps an existing search directory.", []) { RefreshCatalogue = false };
                if (!Directory.Exists(path)) return new(false, "The search directory does not exist or is not accessible.", []);
                if (entries.Count >= MaxPaths) return new(false, "The limit of 32 additional search paths has been reached.", []);
                entries.Add(entry);
            }
            else if (!entries.Remove(entry))
                return new(true, "This search path is not registered.", []) { RefreshCatalogue = false };
            try { OpenXlrPaths.WriteAtomicJson(FilePath, entries, Json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, "Could not save search paths: " + ex.Message, []); }
            return new(true, add ? "Search directory added." : "Search directory removed; plugin files were kept.", []);
        }
    }

    internal static IReadOnlyList<string> Additional(string kind) => [.. Read(out _).Where(p => p.Kind == kind).Select(p => p.Path)];
    internal static IReadOnlyList<string> Include(string kind, IEnumerable<string> paths) => [.. paths.Concat(Additional(kind)).Select(ResolveForDiscovery).Distinct(StringComparer.Ordinal)];

    private static string ResolveForDiscovery(string path)
    {
        try { return WindowsPluginWrappers.Canonical(path); }
        // Keep an unreadable root for the scanner's diagnostics. A broken
        // environment path must not discard every healthy search root.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }

    // lilv's compiled default can contain Nix store paths. Without an explicit
    // LV2_PATH, do not replace it with an assumed filesystem layout.
    public static IReadOnlyList<string> Lv2Path() => Include("lv2",
        (Environment.GetEnvironmentVariable("LV2_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries));

    internal static string? Lv2Override() => Additional("lv2").Count == 0 ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LV2_PATH")) ? null : string.Join(':', Lv2Path());

    private static IReadOnlyList<string> Effective(string kind) => kind switch
    {
        "clap" => ClapCatalog.SearchPath(), "vst3" => Vst3Catalog.SearchPath(), _ => Lv2Path(),
    };
    private static bool Overlaps(string first, string second) => first == second ||
        first.StartsWith(second + "/", StringComparison.Ordinal) || second.StartsWith(first + "/", StringComparison.Ordinal);
    private static bool BroadRoot(string path) => path is "/" or "/usr" or "/usr/local" or "/usr/lib" or "/usr/lib64"
        or "/home" or "/opt" or "/var" or "/var/lib" or "/etc" ||
        path == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ||
        new[] { "/proc", "/sys", "/dev", "/run" }.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

    internal static void ApplyLv2(ProcessStartInfo start)
    {
        if (Lv2Override() is { } path) start.Environment["LV2_PATH"] = path;
    }

    public static IReadOnlyList<PluginSearchDirectory> Snapshot()
    {
        var custom = Read(out _).ToHashSet();
        return [.. new[] { ("lv2", Lv2Path()), ("clap", ClapCatalog.SearchPath()), ("vst3", Vst3Catalog.SearchPath()) }
            .SelectMany(group => group.Item2.Select(path => new PluginSearchDirectory(group.Item1, path,
                custom.Contains(new(group.Item1, path)), Directory.Exists(path))))];
    }
}
