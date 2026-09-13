using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenXLR.UI.Skinning;

/// <summary>A skin the catalogue found, with whatever the reader complained about.</summary>
public sealed record SkinEntry(SkinPackage Package, IReadOnlyList<string> Errors)
{
    public string Id => Package.Id;
    public string Name => Package.Name;
}

/// <summary>
/// Finds the skins this machine has: the ones compiled into the application,
/// then the XDG data directories, in the order the base directory
/// specification gives them.
///
/// - <c>$XDG_DATA_HOME/openxlr/skins/&lt;id&gt;/skin.json</c> (the user's own,
///   <c>~/.local/share</c> when the variable is unset);
/// - each of <c>$XDG_DATA_DIRS</c> (<c>/usr/local/share:/usr/share</c> when
///   unset) plus <c>/openxlr/skins/&lt;id&gt;/skin.json</c>.
///
/// An id found in more than one place is taken from the first: the user's copy
/// shadows a system one, and a system one shadows a built-in.
/// </summary>
public static class SkinCatalog
{
    /// <summary>How many skins are read in one scan, so a full directory cannot stall the window.</summary>
    private const int MaxSkins = 200;

    private const string ResourcePrefix = "OpenXLR.UI.Assets.Skins.";

    /// <summary>$XDG_DATA_HOME when set and non-empty, else ~/.local/share.</summary>
    public static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    /// <summary>$XDG_DATA_DIRS, split, with the specification's default when unset.</summary>
    public static IReadOnlyList<string> DataDirs =>
        (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs
            ? dirs : "/usr/local/share:/usr/share")
        .Split(':', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Where a user drops a skin folder.</summary>
    public static string UserSkinDir => Path.Combine(DataHome, "openxlr", "skins");

    /// <summary>Every skin this machine has, the default first, then the rest by name.</summary>
    public static IReadOnlyList<SkinEntry> Discover()
    {
        var found = new Dictionary<string, SkinEntry>(StringComparer.Ordinal);

        void Offer(SkinEntry entry)
        {
            if (found.Count >= MaxSkins) return;
            found.TryAdd(entry.Id, entry);
        }

        foreach (SkinEntry entry in Scan(UserSkinDir, SkinOrigin.User)) Offer(entry);
        foreach (string dir in DataDirs)
            foreach (SkinEntry entry in Scan(Path.Combine(dir, "openxlr", "skins"), SkinOrigin.System))
                Offer(entry);
        foreach (SkinEntry entry in BuiltIn()) Offer(entry);

        found[SkinPackage.DefaultId] = new SkinEntry(SkinPackage.Default, []);
        return
        [
            found[SkinPackage.DefaultId],
            .. found.Values
                .Where(e => e.Id != SkinPackage.DefaultId)
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>The one skin with this id, or null when the machine has no such skin.</summary>
    public static SkinEntry? Find(string? id) =>
        id is not { Length: > 0 } || id == SkinPackage.DefaultId
            ? new SkinEntry(SkinPackage.Default, [])
            : Discover().FirstOrDefault(e => e.Id == id);

    /// <summary>The skins compiled into the application.</summary>
    public static IEnumerable<SkinEntry> BuiltIn()
    {
        Assembly assembly = typeof(SkinCatalog).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".skin.json", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            string id = resource[ResourcePrefix.Length..^".skin.json".Length];
            string json;
            using (Stream? stream = assembly.GetManifestResourceStream(resource))
            {
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            SkinReadResult result = SkinReader.Read(id, json, SkinOrigin.BuiltIn, null);
            if (result.Package is { } package) yield return new SkinEntry(package, result.Errors);
        }
    }

    /// <summary>Read every skin folder under one directory. A missing directory is simply empty.</summary>
    public static IReadOnlyList<SkinEntry> Scan(string directory, SkinOrigin origin)
    {
        var entries = new List<SkinEntry>();
        DirectoryInfo[] folders;
        try
        {
            var root = new DirectoryInfo(directory);
            if (!root.Exists) return entries;
            folders = root.GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return entries; }

        foreach (DirectoryInfo folder in folders.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (entries.Count >= MaxSkins) break;
            if (Read(folder.FullName, folder.Name, origin) is { } entry) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Read one skin folder. Null when it holds no skin.json at all; an entry
    /// with errors and no usable package when the document is unreadable, so
    /// Options can say why rather than showing nothing.
    /// </summary>
    public static SkinEntry? Read(string folder, string id, SkinOrigin origin)
    {
        string path = Path.Combine(folder, "skin.json");
        string json;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length > SkinFormat.MaxDocumentBytes)
                return Broken(id, origin, folder, $"skin.json is larger than the {SkinFormat.MaxDocumentBytes / 1024} KB limit.");
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Broken(id, origin, folder, $"skin.json could not be read: {ex.Message}");
        }

        SkinReadResult result = SkinReader.Read(id, json, origin, folder);
        return result.Package is { } package
            ? new SkinEntry(package, result.Errors)
            : Broken(id, origin, folder, result.Errors);
    }

    private static SkinEntry Broken(string id, SkinOrigin origin, string folder, params string[] errors) =>
        Broken(id, origin, folder, (IReadOnlyList<string>)errors);

    private static SkinEntry Broken(string id, SkinOrigin origin, string folder, IReadOnlyList<string> errors) =>
        new(new SkinPackage(SkinReader.IsValidId(id) ? id : "unreadable", id, null, null,
            SkinFormat.Schema, origin, folder, new Dictionary<string, SkinValue>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)), errors);
}
