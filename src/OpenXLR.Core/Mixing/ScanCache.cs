using System.Buffers.Binary;
using System.Globalization;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What the native host said about each bundle, kept between daemon runs.
/// Describing a module means creating every plugin in it, and one with two
/// hundred takes a quarter of a minute, which is too long to spend at every
/// start. A bundle is read again only when it changes, or when the helper
/// that reads it does.
///
/// One module's description can run to megabytes, and the daemon lives
/// under a firm heap limit, so the descriptions stay as bytes in a file
/// each; only a small index of stamps is ever held whole.
///
/// A description is also only what one helper had to say, so each entry
/// records which helper wrote it. When the helper changes it may describe
/// the same bundle differently, and without this an updated OpenXLR would
/// keep answering from what the old one learnt about every plugin already
/// installed, for as long as those bundles sat untouched.
/// </summary>
public sealed class ScanCache
{
    /// <summary>One bundle as it was when scanned, where its description is, and what wrote it.</summary>
    /// <param name="Target">
    /// Where a bundle that is itself a symbolic link pointed, so a link aimed
    /// at another file of the same length and timestamp is not mistaken for
    /// the one that was scanned. Null for anything that is not a link, which
    /// is also what entries written before this was recorded carry.
    /// </param>
    /// <param name="Failed">
    /// The scanner could not describe this bundle. There is no description
    /// file, and <see cref="File"/> is empty. Entries written before this
    /// was recorded carry false, which is what they were.
    /// </param>
    public sealed record Entry(long Modified, long Size, string File, string? Scanner = null, string? Target = null,
        bool Failed = false, string? FailureReason = null, DateTimeOffset? FailedAt = null);

    private readonly string _directory;
    private readonly string _scanner;
    private readonly Dictionary<string, Entry> _index;
    private bool _dirty;

    public ScanCache(string directory, string? scanner = null)
    {
        _directory = directory;
        _scanner = string.IsNullOrEmpty(scanner) ? ScannerStamp : scanner;
        _index = Load(Path.Combine(directory, "index.json"));
    }

    /// <summary>
    /// The helper that does the scanning, as its file on disk: an OpenXLR
    /// that ships a new one asks it about every bundle again. Entries
    /// written before this was recorded carry no stamp and match nothing,
    /// so each of them costs one scan and is then kept as usual.
    /// </summary>
    public static string ScannerStamp => StampText(NativePluginHost.Executable);

    /// <summary>One file's stamp as a word, or "none" where there is no such file.</summary>
    internal static string StampText(string path) => Stamp(path) is { } stamp
        ? stamp.Size.ToString(CultureInfo.InvariantCulture) + "-" + stamp.Modified.ToString(CultureInfo.InvariantCulture)
        : "none";

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

    /// <summary>What this cache still holds about a bundle, or null once the bundle or the helper has changed.</summary>
    private Entry? Current(string bundle)
        => _index.TryGetValue(bundle, out Entry? entry) && string.Equals(entry.Scanner, _scanner, StringComparison.Ordinal)
            && Stamp(bundle) is { } stamp
            && entry.Modified == stamp.Modified && entry.Size == stamp.Size
            && string.Equals(entry.Target, stamp.Target, StringComparison.Ordinal)
            ? entry : null;

    /// <summary>The description of a bundle that has not changed since, read by this same helper, or null.</summary>
    public byte[]? Lookup(string bundle)
    {
        if (Current(bundle) is not { Failed: false } entry) return null;
        try
        {
            string path = Path.Combine(_directory, entry.File);
            if (new FileInfo(path).LinkTarget is not null) return null;
            using var stream = File.OpenRead(path);
            // Cache reads have the same byte budget as a live scanner. Size
            // changes during the read must not cause an unbounded allocation.
            long length = stream.Length;
            if (length > ProcessRunner.DefaultStdoutCap) return null;
            byte[] description = new byte[(int)length];
            stream.ReadExactly(description);
            return stream.ReadByte() == -1 ? description : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// This bundle already failed to be described, and has not changed since.
    /// A bundle that hangs the scanner costs the whole deadline every time it
    /// is asked, so it is asked once and then left alone until the bundle,
    /// the helper or the bridge changes, or the user rescans.
    /// </summary>
    public bool KnownFailure(string bundle) => Current(bundle) is { Failed: true };

    public Entry? Failure(string bundle) => Current(bundle) is { Failed: true } entry ? entry : null;

    /// <summary>Discard damaged cache data so the next scan asks the bundle again.</summary>
    public void Invalidate(string bundle)
    {
        if (!_index.Remove(bundle, out Entry? entry)) return;
        if (entry.File.Length > 0)
            try { File.Delete(Path.Combine(_directory, entry.File)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _dirty = true;
    }

    public void Store(string bundle, byte[] description)
    {
        if (Stamp(bundle) is not { } stamp) return;
        string file = FileName(bundle);
        try
        {
            OpenXlrPaths.WriteAtomic(Path.Combine(_directory, file), description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        _index[bundle] = new Entry(stamp.Modified, stamp.Size, file, _scanner, stamp.Target);
        _dirty = true;
    }

    /// <summary>Record that this bundle could not be described, so the next scan can pass it by.</summary>
    public void StoreFailure(string bundle, string reason = "scan-failed", DateTimeOffset? failedAt = null)
    {
        if (Stamp(bundle) is not { } stamp) return;
        // A bundle that described itself before and fails now leaves its old
        // description behind; nothing will read it again.
        if (_index.TryGetValue(bundle, out Entry? previous) && previous.File.Length > 0)
            try { File.Delete(Path.Combine(_directory, previous.File)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _index[bundle] = new Entry(stamp.Modified, stamp.Size, "", _scanner, stamp.Target,
            Failed: true, FailureReason: reason, FailedAt: failedAt ?? DateTimeOffset.UtcNow);
        _dirty = true;
    }

    /// <summary>Forget bundles that are gone, and write the index if anything changed.</summary>
    public void Save()
    {
        foreach ((string bundle, Entry entry) in _index.Where(e => !File.Exists(e.Key) && !Directory.Exists(e.Key)).ToList())
        {
            _index.Remove(bundle);
            if (entry.File.Length > 0)
                try { File.Delete(Path.Combine(_directory, entry.File)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _dirty = true;
        }
        Flush();
    }

    /// <summary>
    /// Write the index if anything changed, without the sweep for bundles
    /// that are gone. A scan calls this after each bundle it had to ask, so
    /// a scan stopped halfway keeps what it learnt.
    /// </summary>
    public void Flush()
    {
        if (!_dirty) return;
        try
        {
            OpenXlrPaths.EnsurePrivateDir(_directory);
            OpenXlrPaths.WriteAtomic(Path.Combine(_directory, "index.json"), JsonSerializer.Serialize(_index, Options));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next run scans again */ }
    }

    /// <summary>
    /// A bundle's identity on disk: the files its links load, and where those
    /// links point. A directory folds every file's relative path, link target
    /// and target stamp into one hash, so an updated, retargeted, broken or
    /// repaired link makes a different bundle.
    ///
    /// A link with nothing behind it is stamped as broken rather than leaving
    /// the whole bundle unstampable. A bundle carries files the host never
    /// loads, a wrapper for another architecture among them, and one leftover
    /// link to a plugin that was removed must not cost a full rescan of the
    /// bundle at every start. Whether the files the host does need are usable
    /// is the scanner's answer, not the cache's: a scan that fails is kept as
    /// a failure rather than a description, and a source that disappears
    /// changes the stamp, so the entry that was already there stops being
    /// used.
    /// </summary>
    internal static (long Modified, long Size, string? Target)? Stamp(string bundle)
    {
        try
        {
            if (File.Exists(bundle))
            {
                FileIdentity one = FileStamp(bundle);
                return one.Broken ? null : (one.Modified, one.Size, one.Target);
            }
            if (!Directory.Exists(bundle)) return null;
            long total = 0;
            using var stamp = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] numbers = new byte[16];
            var directories = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in BundleFiles(bundle, directories: directories).Order(StringComparer.Ordinal))
            {
                FileIdentity identity = FileStamp(file, directories);
                // The newest file alone can hide an updated Windows source
                // whose timestamp is still older than the Linux wrapper. Only
                // a link adds its target, so a bundle of plain files keeps the
                // stamp it already has in the index and needs no rescan.
                stamp.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(bundle, file) + "\0"));
                if (identity.Target.Length > 0) stamp.AppendData(Encoding.UTF8.GetBytes(identity.Target + "\0"));
                BinaryPrimitives.WriteInt64LittleEndian(numbers, identity.Modified);
                BinaryPrimitives.WriteInt64LittleEndian(numbers.AsSpan(8), identity.Size);
                stamp.AppendData(numbers);
                total += identity.Size;
            }
            return (BinaryPrimitives.ReadInt64LittleEndian(stamp.GetHashAndReset()), total, DirectoryTarget(bundle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Keep valid resource aliases, but reject directory cycles before they
    /// multiply the file list. A shared entry budget also bounds an acyclic
    /// tree with many aliases. Such a bundle can still run without a cache.
    /// </summary>
    internal static IEnumerable<string> BundleFiles(string bundle, int entryBudget = 100_000,
        Dictionary<string, string>? directories = null)
    {
        var pending = new Stack<(string Path, bool Leaving)>();
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        pending.Push((bundle, false));
        while (pending.TryPop(out var next))
        {
            if (next.Leaving)
            {
                ancestors.Remove(next.Path);
                continue;
            }
            string canonical = WindowsPluginWrappers.Canonical(next.Path);
            if (!ancestors.Add(canonical)) throw new IOException("A plugin bundle contains a directory link cycle.");
            pending.Push((canonical, true));
            directories?.TryAdd(Path.GetFullPath(next.Path), canonical);
            // Enumeration already knows the entry kind. Keep that result so
            // resource files need no separate directory stat before stamping.
            var entries = new FileSystemEnumerable<(string Path, bool Directory)>(next.Path,
                static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false });
            foreach (var entry in entries)
            {
                if (entryBudget-- <= 0) throw new IOException("A plugin bundle exceeds the cache directory entry limit.");
                if (entry.Directory) pending.Push((entry.Path, false));
                else yield return entry.Path;
            }
        }
    }

    /// <summary>
    /// Where a bundle or one of its parent directories points. The
    /// managed folder links to Windows plugins installed elsewhere, and a
    /// link aimed at a different copy is a different bundle even when the two
    /// hold files of the same names, lengths and times. Null for a real
    /// directory reached without links.
    /// </summary>
    private static string? DirectoryTarget(string bundle)
    {
        string canonical = WindowsPluginWrappers.Canonical(bundle);
        return canonical == WindowsPluginWrappers.Normalize(bundle) ? null : canonical;
    }

    /// <summary>One file as the host would load it, and what it resolves to.</summary>
    /// <param name="Target">Empty for a plain file, else where the link ends up.</param>
    /// <param name="Broken">The file is a link with nothing behind it, or is gone.</param>
    private readonly record struct FileIdentity(long Modified, long Size, string Target, bool Broken);

    private static FileIdentity FileStamp(string path, Dictionary<string, string>? directories = null)
    {
        // A linked architecture or search folder can select another build
        // without changing a module's size or timestamp. Resolve its parent
        // once per directory in this walk, not once per resource file.
        string full = Path.GetFullPath(path);
        string parent = Path.GetDirectoryName(full)!;
        if (directories is null || !directories.TryGetValue(parent, out string? canonical))
        {
            canonical = WindowsPluginWrappers.Canonical(parent);
            directories?.Add(parent, canonical);
        }
        bool linkedParent = canonical != parent;
        var info = new FileInfo(linkedParent ? Path.Combine(canonical, Path.GetFileName(full)) : full);
        string target = linkedParent ? info.FullName : "";
        // Bridge bundles link to the original Windows module. FileInfo on
        // the link describes the link itself, which stays unchanged when the
        // plugin is updated, moved or deleted. Stamp the loaded file instead,
        // and keep the resolved path: two builds of one plugin can share a
        // length and a timestamp, and then only the path tells them apart.
        if (info.LinkTarget is null)
            return info.Exists ? new(info.LastWriteTimeUtc.Ticks, info.Length, target, false) : new(0, 0, target, true);
        try
        {
            if (info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } resolved)
                return new(resolved.LastWriteTimeUtc.Ticks, resolved.Length, WindowsPluginWrappers.Canonical(resolved.FullName), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a loop, or an unreadable path */ }
        // Where a broken link points is part of the identity too, so aiming
        // it somewhere else, or putting the file back, reads as a change.
        return new(0, 0, "missing:" + Path.GetFullPath(info.LinkTarget, info.DirectoryName ?? "."), true);
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Options)
                ?? new(StringComparer.Ordinal);
            // The index is cache data, not permission to read or delete an
            // arbitrary path. Failure markers have no description file.
            foreach ((string bundle, Entry entry) in entries.ToArray())
                if (entry is null || entry.File != (entry.Failed ? "" : FileName(bundle))) entries.Remove(bundle);
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);   // a damaged cache is just a slow start
        }
    }

    private static string FileName(string bundle)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(bundle))) + ".json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
