using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>What a path the user picked turned out to be.</summary>
public enum PluginItemKind
{
    /// <summary>A directory named .lv2 with a manifest inside.</summary>
    Lv2Bundle,
    /// <summary>A .clap file built for Linux.</summary>
    ClapBundle,
    /// <summary>A .vst3 directory or file built for Linux.</summary>
    Vst3Bundle,
    /// <summary>A VST3 or CLAP plugin built for Windows: yabridge's job.</summary>
    WindowsPlugin,
    /// <summary>A Windows VST2 .dll, which nothing here can load.</summary>
    WindowsVst2,
    /// <summary>A Windows installer (.exe or .msi).</summary>
    Installer,
    /// <summary>A zip or tar the user has not extracted.</summary>
    Archive,
    /// <summary>Nothing that can be installed.</summary>
    Unknown,
}

public sealed record PluginItem(PluginItemKind Kind, string Path);

public sealed record WindowsPluginFile(string Path, string Name, string Format, bool Enabled, bool CanDelete, string? WinePrefix, bool InUse = false);
public sealed record WindowsPluginFiles(bool Ok, string Message, IReadOnlyList<WindowsPluginFile> Plugins);

/// <summary>Where plugins are found and installed, and what is there to bridge Windows ones.</summary>
public sealed record PluginSetup(
    bool HostInstalled,
    string Lv2Directory, string ClapDirectory, string Vst3Directory,
    string? YabridgeVersion, bool Wine,
    IReadOnlyList<string> WindowsDirectories,
    IReadOnlyList<string> WineFolders)
{
    /// <summary>Wine's version as it reports it, or null when Wine is not installed.</summary>
    public string? WineVersion { get; init; }

    /// <summary>
    /// What a user should know before opening a bridged plugin's own editor
    /// with this pair of versions, or null when there is nothing to say.
    /// </summary>
    public string? WindowsEditorNote { get; init; }
    public string BridgeProvider { get; init; } = "system";
    public string? BridgeDirectory { get; init; }
    public string? WindowsPluginDirectory { get; init; }
    public string? WindowsImportDirectory { get; init; }
}

/// <summary>
/// How an install went, in words the user can read. The destinations are
/// where the plugins landed, so the caller can tell which of the plugins it
/// finds afterwards came from this install.
/// </summary>
public sealed record InstallOutcome(bool Ok, string Message, IReadOnlyList<string> Installed, IReadOnlyList<string>? Destinations = null);

/// <summary>
/// Puts a plugin the user picked where the catalogues look. A Linux bundle
/// is copied into the matching directory under the home, a Windows plugin
/// directory is handed to yabridge, and everything else gets an answer
/// that says what to do instead. Nothing here loads plugin code.
/// </summary>
public sealed class PluginInstaller
{
    /// <summary>How long yabridge gets to bridge a directory: it copies files, it does not run them.</summary>
    private static readonly TimeSpan YabridgeTimeout = TimeSpan.FromMinutes(3);

    private readonly string _lv2, _clap, _vst3, _winePrefix, _windowsImports;
    private readonly string? _yabridgectl, _wine;
    private readonly bool _hostInstalled;
    private readonly ManagedYabridge? _managed;

    /// <summary>The daemon's own: the home directories, the tools on PATH.</summary>
    public PluginInstaller()
        : this(HomeDirectory(".lv2"), HomeDirectory(".clap"), HomeDirectory(".vst3"),
               OnPath("yabridgectl"), OnPath("wine"), NativePluginHost.HostInstalled)
    {
        _managed = ManagedYabridge.Discover();
        if (_managed is not null) _yabridgectl = _managed.Controller;
    }

    public PluginInstaller(string lv2Directory, string clapDirectory, string vst3Directory,
        string? yabridgectl, string? wine, bool hostInstalled = true, string? winePrefix = null,
        string? windowsImportDirectory = null)
    {
        _lv2 = lv2Directory;
        _clap = clapDirectory;
        _vst3 = vst3Directory;
        _yabridgectl = yabridgectl;
        _wine = wine;
        _hostInstalled = hostInstalled;
        _winePrefix = winePrefix ?? DefaultWinePrefix();
        _windowsImports = windowsImportDirectory ?? Path.Combine(Path.GetDirectoryName(ManagedYabridge.PluginHome)!, "windows-plugins");
    }

    private static string HomeDirectory(string name)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);

    /// <summary>
    /// A tool on PATH, or where a user installs one by hand. The daemon is a
    /// user service and its PATH is systemd's, not the one a login shell
    /// builds, so the places a tarball install puts a tool are looked in
    /// whether or not the user added them to a shell profile: yabridge's own
    /// instructions unpack it into ~/.local/share/yabridge.
    /// </summary>
    internal static string? OnPath(string name)
    {
        var directories = new List<string>();
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path)) directories.AddRange(path.Split(':', StringSplitOptions.RemoveEmptyEntries));
        directories.Add(HomeDirectory(Path.Combine(".local", "bin")));
        directories.Add(HomeDirectory(Path.Combine(".local", "share", "yabridge")));
        directories.Add("/usr/local/bin");
        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // --- what a path is ---------------------------------------------------------

    /// <summary>What one path is, from its name and, for a file, its first bytes.</summary>
    public static PluginItem Inspect(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('/'));
        string lower = name.ToLowerInvariant();
        if (Directory.Exists(path))
        {
            if (lower.EndsWith(".lv2", StringComparison.Ordinal))
                return new(File.Exists(Path.Combine(path, "manifest.ttl")) ? PluginItemKind.Lv2Bundle : PluginItemKind.Unknown, path);
            if (lower.EndsWith(".vst3", StringComparison.Ordinal))
            {
                string contents = Path.Combine(path, "Contents");
                if (Directory.Exists(contents))
                {
                    string[] architectures = Directory.GetDirectories(contents).Select(d => Path.GetFileName(d).ToLowerInvariant()).ToArray();
                    if (architectures.Any(a => a.EndsWith("-linux", StringComparison.Ordinal))) return new(PluginItemKind.Vst3Bundle, path);
                    if (architectures.Any(a => a.EndsWith("-win", StringComparison.Ordinal))) return new(PluginItemKind.WindowsPlugin, path);
                }
                return new(PluginItemKind.Unknown, path);
            }
            return new(PluginItemKind.Unknown, path);
        }
        if (!File.Exists(path)) return new(PluginItemKind.Unknown, path);
        if (lower.EndsWith(".zip", StringComparison.Ordinal) || lower.EndsWith(".7z", StringComparison.Ordinal)
            || lower.EndsWith(".rar", StringComparison.Ordinal) || lower.Contains(".tar", StringComparison.Ordinal)
            || lower.EndsWith(".tgz", StringComparison.Ordinal) || lower.EndsWith(".txz", StringComparison.Ordinal))
            return new(PluginItemKind.Archive, path);
        if (lower.EndsWith(".exe", StringComparison.Ordinal) || lower.EndsWith(".msi", StringComparison.Ordinal))
            return new(PluginItemKind.Installer, path);
        bool clap = lower.EndsWith(".clap", StringComparison.Ordinal);
        bool vst3 = lower.EndsWith(".vst3", StringComparison.Ordinal);
        bool dll = lower.EndsWith(".dll", StringComparison.Ordinal);
        if (!clap && !vst3 && !dll) return new(PluginItemKind.Unknown, path);
        switch (Header(path))
        {
            case Binary.Elf when clap: return new(PluginItemKind.ClapBundle, path);
            case Binary.Elf when vst3: return new(PluginItemKind.Vst3Bundle, path);
            case Binary.Windows when dll: return new(PluginItemKind.WindowsVst2, path);
            case Binary.Windows: return new(PluginItemKind.WindowsPlugin, path);
            default: return new(PluginItemKind.Unknown, path);
        }
    }

    private enum Binary { Other, Elf, Windows }

    private static Binary Header(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            int read = stream.Read(head);
            if (read >= 4 && head[0] == 0x7f && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F') return Binary.Elf;
            if (read >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') return Binary.Windows;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Binary.Other;
    }

    /// <summary>
    /// Everything installable at a path: the path itself when it is a
    /// plugin, else the plugins inside it, a few levels deep, so a folder
    /// of Windows plugins or an extracted download works as one pick.
    /// </summary>
    public static IReadOnlyList<PluginItem> Items(string path)
    {
        PluginItem self = Inspect(path);
        if (self.Kind != PluginItemKind.Unknown || !Directory.Exists(path)) return [self];
        var found = new List<PluginItem>();
        Collect(path, 0, found);
        return found.Count == 0 ? [self] : found;
    }

    private static void Collect(string directory, int depth, List<PluginItem> found)
    {
        if (depth > 3 || found.Count >= 200) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (string entry in entries)
        {
            PluginItem item = Inspect(entry);
            if (item.Kind is PluginItemKind.Archive or PluginItemKind.Installer) continue;   // not what a folder pick means
            if (item.Kind != PluginItemKind.Unknown) found.Add(item);
            else if (Directory.Exists(entry)) Collect(entry, depth + 1, found);
        }
    }

    // --- installing -------------------------------------------------------------

    /// <summary>Install whatever is at the path. Never throws; the outcome says what happened.</summary>
    public InstallOutcome Install(string path)
    {
        if (!Path.IsPathRooted(path)) return new(false, "The path has to be absolute.", []);
        if (!File.Exists(path) && !Directory.Exists(path)) return new(false, $"There is nothing at {path}.", []);
        IReadOnlyList<PluginItem> items = Items(path);
        string name = Path.GetFileName(path.TrimEnd('/'));
        if (items.Count == 1 && items[0].Kind is PluginItemKind.Unknown or PluginItemKind.Archive or PluginItemKind.Installer or PluginItemKind.WindowsVst2)
            return new(false, Refusal(items[0], name), []);

        var installed = new List<string>();
        var destinations = new List<string>();
        var notes = new List<string>();
        var windows = new List<string>();
        int vst2 = 0;
        foreach (PluginItem item in items)
        {
            switch (item.Kind)
            {
                case PluginItemKind.Lv2Bundle: Copy(item.Path, _lv2, installed, destinations, notes); break;
                case PluginItemKind.ClapBundle: Copy(item.Path, _clap, installed, destinations, notes); break;
                case PluginItemKind.Vst3Bundle: Copy(item.Path, _vst3, installed, destinations, notes); break;
                case PluginItemKind.WindowsPlugin:
                    if (string.Equals(item.Path, path, StringComparison.Ordinal))
                    {
                        // A single selection must not register its siblings.
                        // Missing bridge tools are reported below without copying.
                        string? directory = _yabridgectl is null || _wine is null
                            ? Path.GetDirectoryName(path.TrimEnd('/'))
                            : ImportWindowsPlugin(path, notes);
                        if (directory is not null) windows.Add(directory);
                    }
                    else windows.Add(path);   // an explicit folder pick stays in place
                    break;
                case PluginItemKind.WindowsVst2: vst2++; break;
            }
        }
        if (windows.Count > 0)
        {
            bool pickedOne = items.Count == 1 && string.Equals(items[0].Path, path, StringComparison.Ordinal);
            notes.Add(Bridge(windows.Distinct(StringComparer.Ordinal).ToList(), pickedOne ? name : null, installed, destinations));
        }
        else if (vst2 > 0) notes.Add(vst2 == 1 ? "One VST2 plugin was left out: OpenXLR cannot load VST2." : $"{vst2} VST2 plugins were left out: OpenXLR cannot load VST2.");
        if (installed.Count > 0 && !_hostInstalled && items.Any(i => i.Kind is not PluginItemKind.Lv2Bundle))
            notes.Add("The native plugin host is not installed beside the daemon, so CLAP and VST3 plugins cannot run.");
        bool ok = installed.Count > 0;
        if (!ok && notes.Count == 0) notes.Add($"Nothing was installed from {name}.");
        return new(ok, string.Join(" ", notes), installed, destinations);
    }

    private static string Refusal(PluginItem item, string name) => item.Kind switch
    {
        PluginItemKind.Archive => $"{name} is an archive. Extract it first, then pick the plugin inside.",
        PluginItemKind.Installer => $"{name} is a Windows installer. Run it with Wine (wine {name}), then pick the folder it installed into, usually Program Files/Common Files/VST3 under the Wine prefix.",
        PluginItemKind.WindowsVst2 => $"{name} is a VST2 plugin, which OpenXLR cannot load. Its VST3 or CLAP version works.",
        _ => $"Nothing to install at {name}. Pick a .clap file, a .vst3 or .lv2 folder, or a folder holding plugins.",
    };

    private string? ImportWindowsPlugin(string source, List<string> notes)
    {
        string name = Path.GetFileName(source.TrimEnd('/'));
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0 || stem is "." or ".." || name.Any(char.IsControl))
        {
            notes.Add("The plugin needs a valid file name without control characters.");
            return null;
        }
        string format = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        string directory = Path.Combine(_windowsImports, format, stem);
        var imported = new List<string>();
        // Installed plugins can depend on their Wine prefix and neighbours.
        // One link isolates the selection without moving it out of that prefix.
        try { Copy(source, directory, imported, [], notes, linkSource: InWinePrefix(source)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            notes.Add($"Could not import {name}: {ex.Message}");
        }
        return imported.Count > 0 ? directory : null;
    }

    private static bool InWinePrefix(string source) => WinePrefixFor(source) is not null;

    private static string? WinePrefixFor(string source)
    {
        string path = WindowsPluginWrappers.Canonical(source);
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, "system.reg")) && Directory.Exists(Path.Combine(parent, "drive_c"))) return parent;
        return null;
    }

    private static void Copy(string source, string directory, List<string> installed, List<string> destinations, List<string> notes,
        bool linkSource = false)
    {
        string name = Path.GetFileName(source.TrimEnd('/'));
        string destination = Path.Combine(directory, name);
        string sourceFull = Path.GetFullPath(source).TrimEnd('/');
        if (string.Equals(sourceFull, Path.GetFullPath(destination).TrimEnd('/'), StringComparison.Ordinal))
        {
            installed.Add(name);   // already where it belongs: a rescan is all it needs
            destinations.Add(destination);
            return;
        }
        // Replacing a plugin must not be able to lose the working one. The new
        // copy is built beside the destination under a name nothing looks at,
        // the installed bundle is moved aside only once that copy is complete,
        // and the swap is two renames inside one directory. A failure anywhere
        // puts the old bundle back and leaves nothing half-written behind: an
        // unreadable file or a full disk costs the update, not the plugin.
        string staged = destination + ".openxlr-new";
        string retired = destination + ".openxlr-old";
        try
        {
            Directory.CreateDirectory(directory);
            Remove(staged);
            Remove(retired);
            if (linkSource)
            {
                if (Directory.Exists(source)) Directory.CreateSymbolicLink(staged, Path.GetFullPath(source));
                else File.CreateSymbolicLink(staged, Path.GetFullPath(source));
            }
            else if (Directory.Exists(source)) CopyTree(source, staged);
            else File.Copy(source, staged);
            bool replacing = Directory.Exists(destination) || File.Exists(destination) || new FileInfo(destination).LinkTarget is not null;
            if (replacing) Move(destination, retired);
            try { Move(staged, destination); }
            catch { if (replacing) Move(retired, destination); throw; }
            // The new bundle is in place; the old one is only litter now.
            try { Remove(retired); } catch (Exception) { /* removed on the next install */ }
            installed.Add(name);
            destinations.Add(destination);
            notes.Add(linkSource
                ? $"Linked {name} from its Wine prefix into {Shorten(directory)}."
                : $"Installed {name} in {Shorten(directory)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Remove(staged); } catch (Exception) { /* the note already says the install failed */ }
            notes.Add($"Could not copy {name} to {Shorten(directory)}: {ex.Message}");
        }
    }

    /// <summary>Delete a path whether it is a bundle directory or a single file.</summary>
    private static void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
    }

    /// <summary>Rename within one directory, for either kind of bundle.</summary>
    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(entry));
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                // A link inside a bundle points within it; a copied link does the same.
                File.CreateSymbolicLink(target, info.LinkTarget);
            }
            else if (Directory.Exists(entry)) CopyTree(entry, target);
            else File.Copy(entry, target);
        }
    }

    private static string Shorten(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    // --- Windows plugins through yabridge ------------------------------------------

    /// <summary>
    /// Add each directory to yabridge and sync once. Returns a sentence.
    /// <paramref name="picked"/> names the plugin when the user picked one
    /// rather than a folder, for the wording.
    /// </summary>
    private string Bridge(IReadOnlyList<string> directories, string? picked, List<string> installed, List<string> destinations)
    {
        if (_yabridgectl is null || _wine is null)
        {
            string missing = _yabridgectl is null && _wine is null ? "yabridge and Wine are" : _yabridgectl is null ? "yabridge is" : "Wine is";
            string what = picked is not null ? $"{picked} is a Windows plugin"
                : directories.Count == 1 ? $"{Path.GetFileName(directories[0])} holds Windows plugins" : "These are Windows plugins";
            return $"{what}, and {missing} not installed. Install {(missing.StartsWith("yabridge and", StringComparison.Ordinal) ? "them" : "it")}, then add {(picked is not null || directories.Count == 1 ? "it" : "them")} again.";
        }
        if (!PrepareManagedBridge()) return "The OpenXLR bridge could not select its packaged libraries.";
        if (!TryWindowsDirectories(out IReadOnlyList<string> directoriesBefore, out string? listError)) return listError!;
        HashSet<string> known = directoriesBefore.ToHashSet(StringComparer.Ordinal);
        foreach (string directory in directories)
        {
            if (known.Contains(Path.GetFullPath(directory).TrimEnd('/'))) continue;
            ProcessResult? add = Run("add", directory);
            if (add is null || add.ExitCode != 0)
                return $"yabridge would not take {Shorten(directory)}: {Tail(add)}";
        }
        ProcessResult? sync = Run("sync");
        if (sync is null || sync.ExitCode != 0) return $"yabridge could not bridge the plugins: {Tail(sync)}";
        installed.AddRange(directories.Select(Shorten));
        destinations.Add(BridgedVst3Directory);
        destinations.Add(BridgedClapDirectory);
        return directories.Count == 1
            ? $"Bridged the Windows plugins in {Shorten(directories[0])} with yabridge."
            : $"Bridged the Windows plugins in {directories.Count} folders with yabridge.";
    }

    /// <summary>Register a Windows plugin folder without copying any native plugins beside it.</summary>
    public InstallOutcome AddWindowsFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return new(false, "The folder path has to be absolute.", []);
        if (!Directory.Exists(path)) return new(false, $"There is no folder at {path}.", []);
        try
        {
            if (!Items(path).Any(i => i.Kind == PluginItemKind.WindowsPlugin))
                return new(false, "This folder holds no Windows VST3 or CLAP plugins. Use Install file or Install folder for native Linux plugins.", []);
            var installed = new List<string>();
            var destinations = new List<string>();
            string message = Bridge([path], null, installed, destinations);
            return new(installed.Count > 0, message, installed, destinations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Could not add the plugin folder: {ex.Message}", []);
        }
    }

    /// <summary>The individual Windows plugins reachable from one registered folder, including exclusions.</summary>
    public WindowsPluginFiles ListWindowsPlugins(string folder, IReadOnlyCollection<string>? inUsePluginPaths = null)
    {
        if (!ValidPluginPath(folder)) return new(false, "The folder path has to be absolute and contain no control characters.", []);
        try
        {
            folder = WindowsPluginWrappers.Normalize(folder);
            if (!TryWindowsDirectories(out IReadOnlyList<string> folders, out string? error)) return new(false, error!, []);
            if (!folders.Contains(folder, StringComparer.Ordinal)) return new(false, "This folder is not registered with yabridge.", []);
            if (!TryBlacklist(out HashSet<string> excluded, out error)) return new(false, error!, []);
            string[] usedTargets = inUsePluginPaths is { Count: > 0 }
                ? WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory)
                    .Where(w => WindowsPluginWrappers.InUse(w, inUsePluginPaths))
                    .SelectMany(w => w.Targets).Select(WindowsPluginWrappers.Canonical).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            var plugins = new List<WindowsPluginFile>();
            foreach (PluginItem item in Items(folder).Where(i => i.Kind == PluginItemKind.WindowsPlugin))
            {
                string path = WindowsPluginWrappers.Normalize(item.Path);
                string? prefix = WinePrefixFor(path);
                string canonical = WindowsPluginWrappers.Canonical(path);
                bool used = usedTargets.Any(t => WindowsPluginWrappers.Under(t, canonical));
                plugins.Add(new(path, Path.GetFileName(path), Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    !PluginBlocked(path, folders, excluded), prefix is null && !WindowsPluginWrappers.HasLink(path), prefix, used));
            }
            return new(true, "", plugins.DistinctBy(p => p.Path).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Could not list the plugins: {ex.Message}", []);
        }
    }

    /// <summary>Resolve the selected source to bridge paths without changing files or exclusions.</summary>
    public bool TryWindowsPluginWrappers(string path, out IReadOnlyList<string> wrappers, out string? error)
    {
        wrappers = [];
        try
        {
            if (!TryPlugin(path, out path, out _, out _, out error)) return false;
            wrappers = WindowsPluginWrappers.ForPlugin(
                WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory), path)
                .Select(w => w.Path).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"Could not resolve the plugin's bridge wrappers: {ex.Message}";
            return false;
        }
    }

    /// <summary>Exclude or restore one canonical Windows plugin without changing its source files.</summary>
    public InstallOutcome SetWindowsPluginEnabled(string path, bool enabled, IReadOnlyCollection<string>? inUsePluginPaths = null)
    {
        bool changed = false;
        try
        {
            if (!TryPlugin(path, out path, out IReadOnlyList<string> folders, out HashSet<string> excluded, out string? error))
                return new(false, error!, []);
            string canonical = WindowsPluginWrappers.Canonical(path);
            if (enabled && PluginBlocked(path, folders, excluded, ignoreExact: canonical))
                return new(false, "This plugin is excluded by a folder-level rule. Remove that rule in yabridge before enabling the plugin.", []);
            if (enabled && !excluded.Contains(canonical)) return new(true, "This plugin is already enabled.", []);
            IReadOnlyList<WindowsPluginWrappers.Wrapper> wrappers = WindowsPluginWrappers.ForPlugin(
                WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory), path);
            if (!enabled && wrappers.Any(w => WindowsPluginWrappers.InUse(w, inUsePluginPaths)))
                return new(false, "Remove this plugin from your insert chains before excluding it.", []);
            if (_wine is null) return new(false, "Wine is not installed. It is needed to sync the plugin wrappers safely.", []);
            if (!PrepareManagedBridge()) return new(false, "The OpenXLR bridge could not select its packaged libraries.", []);
            if (enabled || !excluded.Contains(canonical))
            {
                ProcessResult? result = Run("blacklist", enabled ? "rm" : "add", canonical);
                if (result?.Ok != true) return new(false, $"yabridge could not change the plugin exclusion: {Tail(result)}", []);
                changed = true;
            }
            if (!TryBlacklist(out excluded, out error))
                return new(false, $"The exclusion command completed, but its result could not be checked: {error} Wrappers were kept.", []);
            if (excluded.Contains(canonical) == enabled)
                return new(false, "yabridge did not apply the requested exclusion. Wrappers were kept.", []);
            ProcessResult? sync = Run("sync");
            if (sync?.Ok != true)
                return new(false, $"The plugin exclusion is saved, but syncing failed and wrappers were kept: {Tail(sync)}", []);
            int removed = 0;
            if (!enabled)
            {
                // Sync may have retargeted a shared wrapper name to another
                // plugin. Only delete wrappers still pointing at this one.
                wrappers = WindowsPluginWrappers.ForPlugin(WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory), path);
                foreach (var wrapper in wrappers) { wrapper.Remove(); removed++; }
            }
            return new(true, enabled ? $"Enabled {Path.GetFileName(path)} and synced its bridge wrappers."
                : $"Excluded {Path.GetFileName(path)} and removed {removed} bridge wrappers. Original plugin files were kept.", [],
                [BridgedVst3Directory, BridgedClapDirectory]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, changed ? $"The exclusion changed, but wrapper cleanup did not finish: {ex.Message} Original files were kept."
                : $"Could not change the plugin exclusion: {ex.Message}", []);
        }
    }

    /// <summary>Delete one standalone Windows plugin, never an installer-managed or linked source.</summary>
    public InstallOutcome DeleteWindowsPlugin(string path, IReadOnlyCollection<string>? inUsePluginPaths = null)
    {
        bool deleting = false;
        try
        {
            if (!TryPlugin(path, out path, out _, out HashSet<string> excluded, out string? error)) return new(false, error!, []);
            if (WinePrefixFor(path) is not null || WindowsPluginWrappers.HasLink(path))
                return new(false, "This plugin is installed in Wine or reached through a symbolic link. Use its Wine uninstaller; its files were kept.", []);
            string canonical = WindowsPluginWrappers.Canonical(path);
            IReadOnlyList<WindowsPluginWrappers.Wrapper> wrappers = WindowsPluginWrappers.ForPlugin(
                WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory), path);
            if (wrappers.Any(w => WindowsPluginWrappers.InUse(w, inUsePluginPaths)))
                return new(false, "Remove this plugin from your insert chains before deleting its files.", []);
            if (_wine is null) return new(false, "Wine is not installed. It is needed to sync the remaining plugins safely.", []);
            if (!PrepareManagedBridge()) return new(false, "The OpenXLR bridge could not select its packaged libraries.", []);
            deleting = true;
            Remove(path);
            if (excluded.Contains(canonical))
            {
                // rm accepts a missing path when it exactly matches a saved
                // canonical entry; an obsolete symlink alias would not work.
                ProcessResult? unexclude = Run("blacklist", "rm", canonical);
                if (unexclude?.Ok != true) return new(false, $"Plugin files were deleted, but their exclusion could not be removed: {Tail(unexclude)}", []);
            }
            ProcessResult? sync = Run("sync");
            if (sync?.Ok != true) return new(false, $"Plugin files were deleted, but syncing failed and wrappers were kept: {Tail(sync)}", []);
            wrappers = WindowsPluginWrappers.ForPlugin(WindowsPluginWrappers.Read(BridgedVst3Directory, BridgedClapDirectory), path);
            foreach (var wrapper in wrappers) wrapper.Remove();
            return new(true, $"Deleted {Path.GetFileName(path)} and removed {wrappers.Count} bridge wrappers. Other plugins were kept.", []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, deleting ? $"Plugin deletion did not finish; some files may already be removed: {ex.Message}"
                : $"Could not delete the plugin: {ex.Message}", []);
        }
    }

    private static bool ValidPluginPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    private bool TryPlugin(string path, out string normalized, out IReadOnlyList<string> folders, out HashSet<string> excluded, out string? error)
    {
        normalized = path;
        folders = [];
        excluded = new(StringComparer.Ordinal);
        error = "The plugin path must be absolute and contain no control characters.";
        if (!ValidPluginPath(path)) return false;
        normalized = WindowsPluginWrappers.Normalize(path);
        if (!TryWindowsDirectories(out folders, out error)) return false;
        string requested = normalized;
        if (!folders.Where(f => WindowsPluginWrappers.Under(requested, f))
            .Any(f => Items(f).Any(i => i.Kind == PluginItemKind.WindowsPlugin && WindowsPluginWrappers.Normalize(i.Path) == requested)))
        {
            error = "This is not a Windows VST3 or CLAP plugin in a registered folder.";
            return false;
        }
        return TryBlacklist(out excluded, out error);
    }

    private bool TryBlacklist(out HashSet<string> excluded, out string? error)
    {
        excluded = new(StringComparer.Ordinal);
        ProcessResult? list = Run("blacklist", "list");
        if (list?.Ok != true) { error = $"yabridge could not list plugin exclusions: {Tail(list)}"; return false; }
        foreach (string line in list.StdoutText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.StartsWith('/')) excluded.Add(WindowsPluginWrappers.Normalize(line));
        error = null;
        return true;
    }

    private static bool PluginBlocked(string path, IReadOnlyList<string> folders, HashSet<string> excluded, string? ignoreExact = null)
    {
        // yabridge checks each canonical entry it visits. An ancestor outside
        // a registered root is not visited, nor are the parents of a link's target.
        foreach (string folder in folders.Where(f => WindowsPluginWrappers.Under(path, f)))
        {
            bool blocked = false;
            for (string? current = path; current is not null && WindowsPluginWrappers.Under(current, folder); current = Path.GetDirectoryName(current))
            {
                string canonical = WindowsPluginWrappers.Canonical(current);
                if (canonical != ignoreExact && excluded.Contains(canonical)) { blocked = true; break; }
            }
            if (!blocked) return false;
        }
        return true;
    }

    /// <summary>Unregister one source and remove only its unused VST3/CLAP wrappers, never its plugins.</summary>
    public InstallOutcome RemoveWindowsFolder(string path, IReadOnlyCollection<string>? inUsePluginPaths = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return new(false, "The folder path has to be absolute.", []);
        if (_yabridgectl is null) return new(false, "yabridge is not installed.", []);
        bool unregistered = false;
        try
        {
            path = WindowsPluginWrappers.Normalize(path);
            if (!TryWindowsDirectories(out IReadOnlyList<string> known, out string? listError))
                return new(false, listError!, []);
            string? registered = known.FirstOrDefault(d => WindowsPluginWrappers.Normalize(d) == path);
            if (registered is null) return new(false, "This folder is not in yabridge's registered plugin folders.", []);
            string[] retained = [.. known.Where(d => d != registered).Select(WindowsPluginWrappers.Normalize)];
            IReadOnlyList<WindowsPluginWrappers.Wrapper> wrappers = WindowsPluginWrappers.Find(BridgedVst3Directory, BridgedClapDirectory, path, retained);
            if (inUsePluginPaths is not null && wrappers.Any(w => inUsePluginPaths.Any(p => WindowsPluginWrappers.Under(WindowsPluginWrappers.Normalize(p), w.Path))))
                return new(false, "Remove the inserts using this folder from the mixer before removing the folder.", []);
            // A retained source may share a wrapper name. Sync gets the chance
            // to retarget it before deciding which wrappers are ours to delete.
            if (retained.Length > 0 && _wine is null)
                return new(false, "Wine is not installed. It is needed to sync the remaining plugin folders safely.", []);
            if (!PrepareManagedBridge()) return new(false, "The OpenXLR bridge could not select its packaged libraries.", []);
            ProcessResult? remove = Run("rm", registered);
            if (remove?.Ok != true) return new(false, $"yabridge could not remove the folder: {Tail(remove)}", []);
            unregistered = true;
            if (!TryWindowsDirectories(out IReadOnlyList<string> remaining, out listError))
                return new(false, $"The folder was removed from the list, but wrappers were kept: {listError}", []);
            if (remaining.Any(d => WindowsPluginWrappers.Normalize(d) == path))
                return new(false, "yabridge still lists the folder. Its wrappers were kept.", []);
            if (remaining.Count > 0)
            {
                ProcessResult? sync = Run("sync");
                if (sync?.Ok != true)
                    return new(false, $"The folder was removed from the list, but its wrappers were kept because syncing the remaining folders failed: {Tail(sync)}", []);
            }
            wrappers = WindowsPluginWrappers.Find(BridgedVst3Directory, BridgedClapDirectory, path,
                remaining.Select(WindowsPluginWrappers.Normalize).ToArray());
            foreach (WindowsPluginWrappers.Wrapper wrapper in wrappers) wrapper.Remove();
            return new(true, $"Removed {Shorten(registered)} from the plugin folders and cleaned up {wrappers.Count} bridge wrappers. Original plugin files were kept.", []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, unregistered
                ? $"The folder was removed from the list, but wrapper cleanup did not finish: {ex.Message} Original plugin files were kept."
                : $"Could not remove the plugin folder: {ex.Message}", []);
        }
    }

    /// <summary>Bridge again whatever yabridge knows, for plugins installed into those folders since.</summary>
    public InstallOutcome SyncWindows(IReadOnlyCollection<string>? inUsePluginPaths = null)
    {
        if (_yabridgectl is null) return new(false, "yabridge is not installed.", []);
        if (_wine is null) return new(false, "Wine is not installed, and yabridge needs it.", []);
        if (!PrepareManagedBridge()) return new(false, "The OpenXLR bridge could not select its packaged libraries.", []);
        if (!TryWindowsDirectories(out IReadOnlyList<string> directories, out string? error)) return new(false, error!, []);
        if (directories.Count == 0) return new(false, "yabridge has no plugin folders yet. Add a Windows plugin to give it one.", []);
        ProcessResult? sync = Run("sync");
        if (sync?.Ok != true) return new(false, $"yabridge could not bridge the plugins: {Tail(sync)}", []);
        if (!TryWindowsDirectories(out directories, out error))
            return new(false, $"Plugins were synced, but missing-source wrappers were kept: {error}", []);
        int removed = 0, kept = 0;
        try
        {
            foreach (var wrapper in WindowsPluginWrappers.Missing(BridgedVst3Directory, BridgedClapDirectory, directories))
            {
                if (WindowsPluginWrappers.InUse(wrapper, inUsePluginPaths)) { kept++; continue; }
                wrapper.Remove();
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Plugins were synced, but missing-source wrapper cleanup did not finish: {ex.Message}", []);
        }
        string message = directories.Count == 1
            ? $"Bridged the Windows plugins in {Shorten(directories[0])}."
            : $"Bridged the Windows plugins in {directories.Count} folders.";
        if (removed > 0) message += $" Removed {removed} wrappers whose source plugins are missing.";
        if (kept > 0) message += $" Kept {kept} missing-source wrappers still used by insert chains.";
        return new(true, message, directories.Select(Shorten).ToList(), [BridgedVst3Directory, BridgedClapDirectory]);
    }

    private string BridgedVst3Directory => _managed is null ? Path.Combine(_vst3, "yabridge")
        : Path.Combine(ManagedYabridge.PluginHome, "vst3");
    private string BridgedClapDirectory => _managed is null ? Path.Combine(_clap, "yabridge")
        : Path.Combine(ManagedYabridge.PluginHome, "clap");

    private bool PrepareManagedBridge()
        => _managed is null || Run("set", "--path", _managed.Directory)?.Ok == true;

    /// <summary>The directories yabridge watches, from `yabridgectl list`.</summary>
    public IReadOnlyList<string> WindowsDirectories()
        => TryWindowsDirectories(out IReadOnlyList<string> directories, out _) ? directories : [];

    private bool TryWindowsDirectories(out IReadOnlyList<string> directories, out string? error)
    {
        directories = [];
        ProcessResult? list = Run("list");
        if (list?.Ok != true)
        {
            error = $"yabridge could not list the plugin folders: {Tail(list)}";
            return false;
        }
        directories = Encoding.UTF8.GetString(list.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('/')).Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).ToList();
        error = null;
        return true;
    }

    /// <summary>Where Wine keeps its own drive: WINEPREFIX, or ~/.wine as Wine itself does.</summary>
    internal static string DefaultWinePrefix()
    {
        string? configured = Environment.GetEnvironmentVariable("WINEPREFIX");
        return string.IsNullOrWhiteSpace(configured) ? HomeDirectory(".wine") : configured;
    }

    /// <summary>
    /// The folders a Windows installer puts a plugin in, when they hold one.
    /// They live under a dot directory Wine owns, which a file dialog hides,
    /// so the window offers them rather than asking the user to find them.
    /// Only VST3 and CLAP, the two formats OpenXLR can load.
    /// </summary>
    public IReadOnlyList<string> WinePluginFolders()
    {
        string drive = Path.Combine(_winePrefix, "drive_c");
        var found = new List<string>();
        var sixtyFourBit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string format in new[] { "VST3", "CLAP" })
        {
            string folder = Path.Combine(drive, "Program Files", "Common Files", format);
            IReadOnlyList<string> names = PluginNames(folder);
            if (names.Count == 0) continue;
            found.Add(folder);
            sixtyFourBit.UnionWith(names);
        }
        foreach (string format in new[] { "VST3", "CLAP" })
        {
            // An installer usually writes both builds of a plugin. The 32-bit
            // copy of one already there as 64-bit is not worth bridging: it is
            // the same plugin twice in the picker, under one name that two
            // bridged bundles would fight over. A folder is offered only for
            // what it holds and the other does not.
            string folder = Path.Combine(drive, "Program Files (x86)", "Common Files", format);
            if (PluginNames(folder).Any(name => !sixtyFourBit.Contains(name))) found.Add(folder);
        }
        return found;
    }

    /// <summary>The plugins in a folder, by file name; empty when there are none.</summary>
    private static IReadOnlyList<string> PluginNames(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return [.. Directory.EnumerateFileSystemEntries(folder)
                .Where(e => e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)
                         || e.EndsWith(".clap", StringComparison.OrdinalIgnoreCase))
                .Select(e => Path.GetFileName(e.TrimEnd('/')))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Whether a bridged plugin's own editor will ignore the mouse. Since
    /// Wine 9.22 the position a plugin's window believes it has and the one
    /// it actually has are not the same, and every click arrives offset by
    /// the distance between them, which for a window anywhere but the very
    /// corner of the screen is far outside the plugin. yabridge has a fix in
    /// progress; released yabridge, up to 5.1.1, does not carry it.
    /// </summary>
    internal static bool EditorsIgnoreTheMouse(string? yabridgeVersion, string? wineVersion)
        => yabridgeVersion is not null   // nothing is bridged without it
           && AtLeast(wineVersion, 9, 22) && !AtLeast(yabridgeVersion, 5, 2);

    /// <summary>A version string such as "wine-11.17" or "5.1.1" against a floor.</summary>
    internal static bool AtLeast(string? version, int major, int minor)
    {
        if (version is null) return false;
        var digits = new List<int>();
        int index = 0;
        while (index < version.Length && digits.Count < 2)
        {
            if (!char.IsAsciiDigit(version[index])) { index++; continue; }
            int start = index;
            while (index < version.Length && char.IsAsciiDigit(version[index])) index++;
            digits.Add(int.Parse(version.AsSpan(start, index - start)));
            // Only a run of digits separated by a dot is the rest of a version.
            if (index >= version.Length || version[index] != '.') break;
            index++;
        }
        if (digits.Count == 0) return false;
        if (digits[0] != major) return digits[0] > major;
        return digits.Count > 1 && digits[1] >= minor;
    }

    /// <summary>Read bridge status without syncing or executing a plugin.</summary>
    public object Diagnostics()
    {
        object? status = null;
        if (_yabridgectl is not null)
        {
            try
            {
                ProcessResult result = ProcessRunner.Run(_yabridgectl, ["status"], TimeSpan.FromSeconds(5),
                    stdoutCap: 64 * 1024, stderrCap: 16 * 1024, environment: _managed?.ControllerEnvironment());
                status = new { result.ExitCode, result.TimedOut, result.Truncated, output = result.StdoutText, error = result.Stderr };
            }
            catch (Exception ex) { status = new { error = PluginScanDiagnostics.Clip(ex.Message, 2048) }; }
        }
        return new
        {
            controller = _yabridgectl, wineExecutable = _wine, winePrefix = _winePrefix,
            sourceCommit = _managed?.SourceCommit, status,
            hostExecutable = NativePluginHost.Executable, hostInstalled = _hostInstalled,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            searchPaths = new { lv2Override = Environment.GetEnvironmentVariable("LV2_PATH"), clap = ClapCatalog.SearchPath().Take(64), vst3 = Vst3Catalog.SearchPath().Take(64) },
            scans = PluginScanDiagnostics.Snapshot()
        };
    }

    /// <summary>What is there: the directories, the host, yabridge and Wine.</summary>
    public PluginSetup Setup()
    {
        string? version = null;
        if (_yabridgectl is not null)
        {
            ProcessResult? result = Run("--version");
            if (result is not null && result.ExitCode == 0)
            {
                string text = Encoding.UTF8.GetString(result.Stdout).Trim();
                version = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? text;
                if (version.Length == 0) version = "installed";
            }
            else version = "installed";
        }
        string? wineVersion = null;
        if (_wine is not null)
        {
            try
            {
                ProcessResult result = ProcessRunner.Run(_wine, ["--version"], TimeSpan.FromSeconds(10), cLocale: false);
                if (result.ExitCode == 0) wineVersion = Encoding.UTF8.GetString(result.Stdout).Trim().Split('\n')[0].Trim();
            }
            catch (Exception) { /* Wine that will not say is Wine we know nothing about */ }
        }
        IReadOnlyList<string> bridged = WindowsDirectories();
        var known = bridged.Select(d => Path.GetFullPath(d).TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        // Only the ones still to bridge: a folder already handed over needs
        // no offer, and after bridging one the offer goes away by itself.
        IReadOnlyList<string> wine = _wine is null || _yabridgectl is null
            ? []
            : [.. WinePluginFolders().Where(f => !known.Contains(Path.GetFullPath(f).TrimEnd('/')))];
        return new(_hostInstalled, Shorten(_lv2), Shorten(_clap), Shorten(_vst3), _managed?.Version ?? version, _wine is not null, bridged, wine)
        {
            WineVersion = wineVersion,
            BridgeProvider = _managed is null ? "system" : "openxlr",
            BridgeDirectory = _managed is null ? null : Shorten(_managed.Directory),
            WindowsPluginDirectory = _managed is null ? null : Shorten(ManagedYabridge.PluginHome),
            WindowsImportDirectory = Shorten(_windowsImports),
            WindowsEditorNote = _managed is null && EditorsIgnoreTheMouse(version, wineVersion)
                ? $"A Windows plugin's own editor can ignore the mouse with yabridge {version} and {wineVersion}: since Wine 9.22 its clicks can arrive somewhere else entirely. The optional openxlr-yabridge package includes the input fix. The manual covers bridge selection."
                : null,
        };
    }

    /// <summary>
    /// Null only when there is no controller to run. A controller that cannot
    /// be started comes back as a failed run carrying the reason the operating
    /// system gave, because that reason is the whole diagnosis.
    /// </summary>
    private ProcessResult? Run(params string[] arguments)
    {
        if (_yabridgectl is null) return null;
        try { return ProcessRunner.Run(_yabridgectl, arguments, YabridgeTimeout, cLocale: false,
            environment: _managed?.ControllerEnvironment()); }
        catch (Exception ex) { return new(-1, [], "it could not be started: " + ex.Message, TimedOut: false, Truncated: false); }
    }

    private static string Tail(ProcessResult? result)
    {
        if (result is null) return "it is not installed.";
        if (result.TimedOut) return "it did not finish in time.";
        string text = (result.Stderr.Length > 0 ? result.Stderr : Encoding.UTF8.GetString(result.Stdout)).Trim();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? $"exit code {result.ExitCode}." : lines[^1];
    }
}
