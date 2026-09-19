using System.Diagnostics;
using System.Runtime.InteropServices;
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
            var kept = entries.Where(e => e is not null && Valid(e.Kind, e.Path))
                .Select(e => e with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.Path)) })
                .Where(e => Valid(e.Kind, e.Path)).Distinct().Take(MaxPaths).ToArray();
            if (kept.Length != entries.Count) warning = "Invalid, duplicate or excessive plugin search paths were ignored.";
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning = "Could not read plugin search paths: " + ex.Message;
            return [];
        }
    }

    public static InstallOutcome Change(string kind, string path, bool add)
    {
        if (!Valid(kind, path)) return new(false, "Choose LV2, CLAP or VST3 and an absolute directory path without a colon or control character.", []);
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Valid(kind, path)) return new(false, "The filesystem root is not a plugin search directory.", []);
        lock (Gate)
        {
            var entries = Read(out string? warning).ToList();
            if (warning is not null) return new(false, warning + " Repair plugin-paths.json before editing it.", []);
            var entry = new Entry(kind, path);
            if (add)
            {
                if (entries.Contains(entry)) return new(true, "This search path is already registered.", []);
                if (!Directory.Exists(path)) return new(false, "The search directory does not exist or is not accessible.", []);
                if (entries.Count >= MaxPaths) return new(false, "The limit of 32 additional search paths has been reached.", []);
                entries.Add(entry);
            }
            else entries.Remove(entry); // offline folders must remain removable
            try { OpenXlrPaths.WriteAtomicJson(FilePath, entries, Json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, "Could not save search paths: " + ex.Message, []); }
            return new(true, add ? "Search directory added." : "Search directory removed; plugin files were kept.", []);
        }
    }

    internal static IReadOnlyList<string> Additional(string kind) => [.. Read(out _).Where(p => p.Kind == kind).Select(p => p.Path)];
    internal static IReadOnlyList<string> Include(string kind, IEnumerable<string> paths) => [.. paths.Concat(Additional(kind)).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyList<string> Lv2Path()
    {
        string? configured = Environment.GetEnvironmentVariable("LV2_PATH");
        return Include("lv2", !string.IsNullOrWhiteSpace(configured)
            ? configured.Split(':', StringSplitOptions.RemoveEmptyEntries)
            : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lv2"),
                "/usr/lib/lv2", "/usr/local/lib/lv2", "/usr/lib64/lv2", "/usr/local/lib64/lv2",
                .. MultiarchLv2Paths(RuntimeInformation.ProcessArchitecture)]);
    }

    // Debian-family packages may install bundles under the architecture's
    // library directory. Adding a custom path must not hide those bundles.
    internal static string[] MultiarchLv2Paths(Architecture architecture)
    {
        string? triplet = architecture switch
        {
            Architecture.X64 => "x86_64-linux-gnu",
            Architecture.X86 => "i386-linux-gnu",
            Architecture.Arm64 => "aarch64-linux-gnu",
            Architecture.Arm => "arm-linux-gnueabihf",
            _ => null,
        };
        return triplet is null ? [] : [$"/usr/lib/{triplet}/lv2", $"/usr/local/lib/{triplet}/lv2"];
    }

    // Only override lilv's compiled-in defaults when the user adds an LV2 path.
    internal static string? Lv2Override() => Additional("lv2").Count == 0 ? null : string.Join(':', Lv2Path());
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
