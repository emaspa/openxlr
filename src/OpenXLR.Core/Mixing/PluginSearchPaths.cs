using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenXLR.Core.Mixing;

public sealed record PluginSearchDirectory(string Kind, string Path, bool Custom, bool Exists);

/// <summary>
/// Plugin folders added from the plugin manager, searched on top of each
/// format's normal places. They are saved in daemon.json. Removing one stops
/// the search there and never deletes a file.
///
/// The kind <c>windows</c> is a folder of Windows VST3 and CLAP plugins for
/// the Windows plugin host built into OpenXLR, searched for both formats on
/// top of the standard folders of the Wine prefix
/// (<see cref="WindowsPluginHost.Directories"/>).
/// </summary>
public static class PluginSearchPaths
{
    public const int MaxPaths = 32;
    private static readonly object Gate = new();

    public static bool Valid(string? kind, string? path) => kind is "lv2" or "clap" or "vst3" or "windows" &&
        path is { Length: > 1 and <= 4096 } && Path.IsPathFullyQualified(path) &&
        !path.Any(char.IsControl) && !path.Contains(':');

    /// <summary>
    /// The saved folders that can still be used, resolved through symbolic
    /// links. One that no longer resolves (a link loop, a path made invalid
    /// by hand) is skipped and named in <paramref name="warning"/>; it stays
    /// saved, so editing the list does not lose it.
    /// </summary>
    public static IReadOnlyList<PluginFolder> Read(out string? warning)
    {
        var kept = new List<PluginFolder>();
        int skipped = 0;
        if (DaemonSettings.Read(out string? problem) is not { } settings)
        {
            warning = "daemon.json cannot be read, so no added plugin folder is searched: " + problem;
            return kept;
        }
        foreach (PluginFolder? folder in settings.PluginFolders ?? [])
        {
            if (Resolve(folder) is { } usable) { if (!kept.Contains(usable)) kept.Add(usable); }
            else skipped++;
        }
        if (kept.Count > MaxPaths) { skipped += kept.Count - MaxPaths; kept.RemoveRange(MaxPaths, kept.Count - MaxPaths); }
        warning = skipped == 0 ? null
            : $"{skipped} saved plugin {(skipped == 1 ? "folder was" : "folders were")} skipped: the path is invalid or cannot be resolved.";
        return kept;
    }

    private static PluginFolder? Resolve(PluginFolder? folder)
    {
        if (folder is null || !Valid(folder.Kind, folder.Path)) return null;
        try
        {
            string path = WindowsPluginWrappers.Canonical(folder.Path);
            return Valid(folder.Kind, path) && !BroadRoot(path) ? folder with { Path = path } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Add or remove one folder and save the list. A request that changes
    /// nothing, or is refused, answers with <see cref="InstallOutcome.RefreshCatalogue"/>
    /// off, so no rescan follows it.
    /// </summary>
    public static InstallOutcome Change(string kind, string path, bool add)
    {
        if (!Valid(kind, path))
            return Refused("Choose LV2, CLAP, VST3 or Windows and an absolute folder path without a colon or control character.");
        string canonical;
        try { canonical = WindowsPluginWrappers.Canonical(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Refused("Could not resolve the folder: " + ex.Message); }
        lock (Gate)
        {
            if (DaemonSettings.Read(out string? problem) is not { } settings)
                return Refused("daemon.json cannot be read, so it was not written over. Repair or remove it, then try again: " + problem);
            var folders = (settings.PluginFolders ?? []).Where(f => f is not null).ToList();
            if (add)
            {
                if (!Valid(kind, canonical) || BroadRoot(canonical))
                    return Refused("Choose a dedicated plugin folder, not a system or home root.");
                if (folders.Any(f => f.Kind == kind && SameFolder(f.Path, canonical)))
                    return new(true, "This folder is already searched.", []) { RefreshCatalogue = false };
                if (AddRefusal(kind, canonical, folders.Count, []) is { } refusal) return Refused(refusal);
                folders.Add(new(kind, canonical));
            }
            else if (folders.RemoveAll(f => f.Kind == kind && (f.Path == path || SameFolder(f.Path, canonical))) == 0)
                return new(true, "This folder was not in the list.", []) { RefreshCatalogue = false };
            try { (settings with { PluginFolders = folders }).Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Refused("Could not save the plugin folders: " + ex.Message); }
            return new(true, add ? "Plugin folder added." : "Plugin folder removed; its files were kept.", []);
        }
    }

    /// <summary>
    /// Why a resolved folder cannot join the list, or null when it can: the
    /// overlap rule, against the folders already searched and any
    /// <paramref name="pending"/> in the same write, its presence, and the limit.
    /// </summary>
    private static string? AddRefusal(string kind, string canonical, int count, IEnumerable<string> pending)
    {
        if (Directories(kind).Concat(pending).Any(existing => Overlaps(canonical, existing)))
            return "This folder is inside, or holds, a folder that is already searched.";
        if (!Directory.Exists(canonical)) return "The folder does not exist or cannot be read.";
        if (count >= MaxPaths) return $"At most {MaxPaths} plugin folders can be added.";
        return null;
    }

    /// <summary>The most folders the record of the yabridge folder copy names as not added.</summary>
    internal const int MaxRecordedFolders = 64;

    /// <summary>
    /// Add yabridge's folders as <c>windows</c> folders in one write of
    /// daemon.json, together with the record that the copy ran, so it runs
    /// once. A folder the Windows plugin host already searches, by itself or
    /// inside another, is passed over in silence. One that cannot be added on
    /// the terms of <see cref="Change"/> is named in the record with the
    /// reason, after <paramref name="problems"/>. Null, with nothing written,
    /// when the copy already ran or daemon.json cannot be read; a failed
    /// write throws, and the next start tries again.
    /// </summary>
    internal static BridgeFolderCopy? CopyWindowsFolders(IEnumerable<string> sources, IEnumerable<FolderNotCopied> problems)
    {
        lock (Gate)
        {
            if (DaemonSettings.Read(out _) is not { } settings || settings.YabridgeFolderCopy is not null) return null;
            var folders = (settings.PluginFolders ?? []).Where(f => f is not null).ToList();
            var added = new List<string>();
            var notAdded = new List<FolderNotCopied>(problems);
            IReadOnlyList<string> searched = WindowsPluginHost.Directories();
            foreach (string source in sources.Distinct(StringComparer.Ordinal))
            {
                if (!Valid("windows", source))
                {
                    notAdded.Add(new(source, "The path is not absolute, or it holds a colon or a control character."));
                    continue;
                }
                string canonical;
                try { canonical = WindowsPluginWrappers.Canonical(source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    notAdded.Add(new(source, "Could not resolve the folder: " + ex.Message));
                    continue;
                }
                if (searched.Concat(added).Any(folder => WindowsPluginWrappers.Under(canonical, folder)))
                    continue;
                string? refusal = !Valid("windows", canonical) || BroadRoot(canonical)
                    ? "This is a system or home root, not a dedicated plugin folder."
                    : AddRefusal("windows", canonical, folders.Count, added);
                if (refusal is not null)
                {
                    notAdded.Add(new(source, refusal));
                    continue;
                }
                folders.Add(new("windows", canonical));
                added.Add(canonical);
            }
            var record = new BridgeFolderCopy(added, [.. notAdded.Take(MaxRecordedFolders)]);
            (settings with { PluginFolders = added.Count > 0 ? folders : settings.PluginFolders, YabridgeFolderCopy = record }).Save();
            return record;
        }
    }

    private static InstallOutcome Refused(string message) => new(false, message, []) { RefreshCatalogue = false };

    private static bool SameFolder(string saved, string canonical)
    {
        try { return WindowsPluginWrappers.Canonical(saved) == canonical; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return saved == canonical; }
    }

    internal static IReadOnlyList<string> Additional(string kind) => [.. Read(out _).Where(f => f.Kind == kind).Select(f => f.Path)];

    /// <summary>A format's normal search path with the added folders after it, aliases folded.</summary>
    internal static IReadOnlyList<string> Include(string kind, IEnumerable<string> paths)
        => Fold(paths.Concat(Additional(kind)));

    private static IReadOnlyList<string> Fold(IEnumerable<string> paths) => [.. paths.Select(ResolveForDiscovery).Distinct(StringComparer.Ordinal)];

    private static string ResolveForDiscovery(string path)
    {
        try { return WindowsPluginWrappers.Canonical(path); }
        // Keep an unreadable root for the scanner's diagnostics. A broken
        // environment path must not discard every healthy search root.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }

    /// <summary>
    /// Where lilv looks when LV2_PATH is unset: its Linux default (~/.lv2,
    /// /usr/local/lib/lv2, /usr/lib/lv2), the lib64 and Debian multiarch
    /// directories distributions build it with, and the Nix profiles. Only
    /// directories that exist are kept.
    /// </summary>
    internal static IReadOnlyList<string> StandardLv2Directories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? multiarch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64-linux-gnu",
            Architecture.Arm64 => "aarch64-linux-gnu",
            _ => null,
        };
        string[] candidates =
        [
            Path.Combine(home, ".lv2"), "/usr/local/lib/lv2", "/usr/lib/lv2", "/usr/local/lib64/lv2", "/usr/lib64/lv2",
            .. multiarch is null ? Array.Empty<string>() : [$"/usr/lib/{multiarch}/lv2"],
            Path.Combine(home, ".nix-profile", "lib", "lv2"), $"/etc/profiles/per-user/{Environment.UserName}/lib/lv2",
            "/run/current-system/sw/lib/lv2",
        ];
        return [.. candidates.Where(Directory.Exists)];
    }

    /// <summary>
    /// LV2_PATH's absolute entries (a leading ~ is the home folder, as lilv
    /// reads it), or the standard directories when it is unset or names none.
    /// </summary>
    private static IReadOnlyList<string> Lv2Base(out bool adjusted)
        => SearchPathList.Directories("LV2_PATH", StandardLv2Directories, out adjusted);

    /// <summary>The LV2 directories searched: LV2_PATH or the standard ones, then the added folders.</summary>
    public static IReadOnlyList<string> Lv2Directories() => Include("lv2", Lv2Base(out _));

    /// <summary>
    /// The LV2_PATH every LV2 consumer gets: the catalogue's lilv scan, the
    /// PipeWire filter-chain and the native host. Null when no LV2 folder was
    /// added and the daemon's LV2_PATH holds only absolute entries, so each
    /// keeps the environment and lilv defaults it has today. Otherwise the
    /// path is spelled out, because lilv reads either LV2_PATH or its
    /// compiled default, never both, and would resolve a relative entry
    /// against the working directory.
    /// </summary>
    public static string? Lv2Path()
    {
        IReadOnlyList<string> added = Additional("lv2");
        IReadOnlyList<string> standard = Lv2Base(out bool adjusted);
        return added.Count == 0 && !adjusted ? null : string.Join(':', Fold(standard.Concat(added)));
    }

    /// <summary>Hand a helper that loads LV2 plugins the same LV2_PATH the catalogue was read with.</summary>
    internal static void ApplyLv2(ProcessStartInfo start)
    {
        if (Lv2Path() is { } path) start.Environment["LV2_PATH"] = path;
    }

    /// <summary>
    /// The folders a new one of this kind must stay clear of. A Windows
    /// folder is searched for CLAP and VST3 alike, so it must stay clear of
    /// theirs as well: a folder of yabridge wrappers read again as Windows
    /// plugins would load every module twice to find what is already listed.
    /// </summary>
    private static IReadOnlyList<string> Directories(string kind) => kind switch
    {
        "clap" => ClapCatalog.SearchPath(),
        "vst3" => Vst3Catalog.SearchPath(),
        "windows" => [.. WindowsPluginHost.Directories(), .. ClapCatalog.SearchPath(), .. Vst3Catalog.SearchPath()],
        _ => Lv2Directories(),
    };

    private static bool Overlaps(string first, string second) => first == second ||
        first.StartsWith(second + "/", StringComparison.Ordinal) || second.StartsWith(first + "/", StringComparison.Ordinal);

    private static bool BroadRoot(string path) => path is "/" or "/usr" or "/usr/local" or "/usr/lib" or "/usr/lib64"
        or "/home" or "/opt" or "/var" or "/var/lib" or "/etc" ||
        path == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ||
        new[] { "/proc", "/sys", "/dev", "/run" }.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));

    /// <summary>
    /// Every directory each format searches, with the added ones marked. The
    /// Windows folders are listed only in a build with the Windows plugin
    /// host, the only one that searches them.
    /// </summary>
    /// <param name="windowsHost">The host's presence, for a test; the build's own when null.</param>
    public static IReadOnlyList<PluginSearchDirectory> Snapshot(bool? windowsHost = null)
    {
        var added = Read(out _).ToHashSet();
        IEnumerable<(string, IReadOnlyList<string>)> windows = windowsHost ?? WindowsPluginHost.Installed
            ? [("windows", WindowsPluginHost.Directories())] : [];
        return [.. new[] { ("lv2", Lv2Directories()), ("clap", ClapCatalog.SearchPath()), ("vst3", Vst3Catalog.SearchPath()) }
            .Concat(windows)
            .SelectMany(group => group.Item2.Select(path => new PluginSearchDirectory(group.Item1, path,
                added.Contains(new(group.Item1, path)), Directory.Exists(path))))];
    }
}
