using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What the native host said about each bundle, kept between daemon runs.
/// Describing a module means creating every plugin in it, and one with two
/// hundred takes a quarter of a minute, which is too long to spend at every
/// start. A bundle is read again only when its file changes.
///
/// One module's description can run to megabytes, and the daemon lives
/// under a firm heap limit, so the descriptions stay as bytes in a file
/// each; only a small index of stamps is ever held whole.
/// </summary>
public sealed class ScanCache
{
    /// <summary>One bundle as it was when scanned, and where its description is.</summary>
    public sealed record Entry(long Modified, long Size, string File);

    private readonly string _directory;
    private readonly Dictionary<string, Entry> _index;
    private bool _dirty;

    public ScanCache(string directory)
    {
        _directory = directory;
        _index = Load(Path.Combine(directory, "index.json"));
    }

    /// <summary>Where the daemon keeps it: under the user's cache directory.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome))
                cacheHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cacheHome, "openxlr", "plugin-scans");
        }
    }

    /// <summary>The description of a bundle that has not changed since, or null.</summary>
    public byte[]? Lookup(string bundle)
    {
        if (!_index.TryGetValue(bundle, out Entry? entry) || Stamp(bundle) is not (long modified, long size)
            || entry.Modified != modified || entry.Size != size)
            return null;
        try { return File.ReadAllBytes(Path.Combine(_directory, entry.File)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Store(string bundle, byte[] description)
    {
        if (Stamp(bundle) is not (long modified, long size)) return;
        string file = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(bundle))) + ".json";
        try
        {
            OpenXlrPaths.EnsurePrivateDir(_directory);
            string temporary = Path.Combine(_directory, file + ".tmp");
            using (FileStream stream = OpenXlrPaths.CreatePrivate(temporary))
                stream.Write(description);
            File.Move(temporary, Path.Combine(_directory, file), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        _index[bundle] = new Entry(modified, size, file);
        _dirty = true;
    }

    /// <summary>Forget bundles that are gone, and write the index if anything changed.</summary>
    public void Save()
    {
        foreach ((string bundle, Entry entry) in _index.Where(e => !File.Exists(e.Key) && !Directory.Exists(e.Key)).ToList())
        {
            _index.Remove(bundle);
            try { File.Delete(Path.Combine(_directory, entry.File)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _dirty = true;
        }
        if (!_dirty) return;
        try
        {
            OpenXlrPaths.EnsurePrivateDir(_directory);
            OpenXlrPaths.WriteAtomic(Path.Combine(_directory, "index.json"), JsonSerializer.Serialize(_index, Options));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next run scans again */ }
    }

    /// <summary>A bundle's identity on disk: for a directory, its newest file counts.</summary>
    internal static (long Modified, long Size)? Stamp(string bundle)
    {
        try
        {
            if (File.Exists(bundle))
            {
                var info = new FileInfo(bundle);
                return (info.LastWriteTimeUtc.Ticks, info.Length);
            }
            if (!Directory.Exists(bundle)) return null;
            long newest = 0, total = 0;
            foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                newest = Math.Max(newest, info.LastWriteTimeUtc.Ticks);
                total += info.Length;
            }
            return (newest, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Options)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);   // a damaged cache is just a slow start
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
