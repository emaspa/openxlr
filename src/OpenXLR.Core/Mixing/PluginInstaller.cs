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

/// <summary>Where plugins are found and installed, and what is there to bridge Windows ones.</summary>
public sealed record PluginSetup(
    bool HostInstalled,
    string Lv2Directory, string ClapDirectory, string Vst3Directory,
    string? YabridgeVersion, bool Wine,
    IReadOnlyList<string> WindowsDirectories,
    IReadOnlyList<string> WineFolders);

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

    private readonly string _lv2, _clap, _vst3, _winePrefix;
    private readonly string? _yabridgectl, _wine;
    private readonly bool _hostInstalled;

    /// <summary>The daemon's own: the home directories, the tools on PATH.</summary>
    public PluginInstaller()
        : this(HomeDirectory(".lv2"), HomeDirectory(".clap"), HomeDirectory(".vst3"),
               OnPath("yabridgectl"), OnPath("wine"), NativePluginHost.HostInstalled) { }

    public PluginInstaller(string lv2Directory, string clapDirectory, string vst3Directory,
        string? yabridgectl, string? wine, bool hostInstalled = true, string? winePrefix = null)
    {
        _lv2 = lv2Directory;
        _clap = clapDirectory;
        _vst3 = vst3Directory;
        _yabridgectl = yabridgectl;
        _wine = wine;
        _hostInstalled = hostInstalled;
        _winePrefix = winePrefix ?? DefaultWinePrefix();
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
                    // yabridge takes directories: a picked plugin means the
                    // directory it is in, a picked directory means itself.
                    windows.Add(string.Equals(item.Path, path, StringComparison.Ordinal) ? Path.GetDirectoryName(path.TrimEnd('/'))! : path);
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

    private static void Copy(string source, string directory, List<string> installed, List<string> destinations, List<string> notes)
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
        try
        {
            Directory.CreateDirectory(directory);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            else if (File.Exists(destination)) File.Delete(destination);
            if (Directory.Exists(source)) CopyTree(source, destination);
            else File.Copy(source, destination);
            installed.Add(name);
            destinations.Add(destination);
            notes.Add($"Installed {name} in {Shorten(directory)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not copy {name} to {Shorten(directory)}: {ex.Message}");
        }
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
        HashSet<string> known = WindowsDirectories().ToHashSet(StringComparer.Ordinal);
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
        destinations.Add(Path.Combine(_vst3, "yabridge"));   // where yabridge writes its bundles
        destinations.Add(Path.Combine(_clap, "yabridge"));
        return directories.Count == 1
            ? $"Bridged the Windows plugins in {Shorten(directories[0])} with yabridge."
            : $"Bridged the Windows plugins in {directories.Count} folders with yabridge.";
    }

    /// <summary>Bridge again whatever yabridge knows, for plugins installed into those folders since.</summary>
    public InstallOutcome SyncWindows()
    {
        if (_yabridgectl is null) return new(false, "yabridge is not installed.", []);
        if (_wine is null) return new(false, "Wine is not installed, and yabridge needs it.", []);
        IReadOnlyList<string> directories = WindowsDirectories();
        if (directories.Count == 0) return new(false, "yabridge has no plugin folders yet. Add a Windows plugin to give it one.", []);
        ProcessResult? sync = Run("sync");
        if (sync is null || sync.ExitCode != 0) return new(false, $"yabridge could not bridge the plugins: {Tail(sync)}", []);
        return new(true, directories.Count == 1
            ? $"Bridged the Windows plugins in {Shorten(directories[0])}."
            : $"Bridged the Windows plugins in {directories.Count} folders.", directories.Select(Shorten).ToList(),
            [Path.Combine(_vst3, "yabridge"), Path.Combine(_clap, "yabridge")]);
    }

    /// <summary>The directories yabridge watches, from `yabridgectl list`.</summary>
    public IReadOnlyList<string> WindowsDirectories()
    {
        if (_yabridgectl is null) return [];
        ProcessResult? list = Run("list");
        if (list is null || list.ExitCode != 0) return [];
        return Encoding.UTF8.GetString(list.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('/')).Select(line => line.TrimEnd('/')).ToList();
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
        IReadOnlyList<string> bridged = WindowsDirectories();
        var known = bridged.Select(d => Path.GetFullPath(d).TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
        // Only the ones still to bridge: a folder already handed over needs
        // no offer, and after bridging one the offer goes away by itself.
        IReadOnlyList<string> wine = _wine is null || _yabridgectl is null
            ? []
            : [.. WinePluginFolders().Where(f => !known.Contains(Path.GetFullPath(f).TrimEnd('/')))];
        return new(_hostInstalled, Shorten(_lv2), Shorten(_clap), Shorten(_vst3), version, _wine is not null, bridged, wine);
    }

    private ProcessResult? Run(params string[] arguments)
    {
        if (_yabridgectl is null) return null;
        try { return ProcessRunner.Run(_yabridgectl, arguments, YabridgeTimeout, cLocale: false); }
        catch (Exception) { return null; }
    }

    private static string Tail(ProcessResult? result)
    {
        if (result is null) return "it could not be started.";
        if (result.TimedOut) return "it did not finish in time.";
        string text = (result.Stderr.Length > 0 ? result.Stderr : Encoding.UTF8.GetString(result.Stdout)).Trim();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? $"exit code {result.ExitCode}." : lines[^1];
    }
}
