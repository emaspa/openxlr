using System;
using System.IO;
using System.Text.Json;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// Where OpenXLR keeps its files and how it writes them. One place for the
/// XDG rules (an empty variable counts as unset, like a missing one) and for
/// the file modes: everything under the configuration directory is private
/// to the user (0700 directories, 0600 files), since profiles and the app
/// registry describe what runs on the machine. Writes are atomic: a
/// temporary file next to the target, then a rename, so a crash mid-write
/// never leaves a truncated file behind.
///
/// Compiled into the daemon through OpenXLR.Core and into the window as a
/// linked source file (internal, in the window's own namespace, so the test
/// project sees one public type), so both agree without the window taking a
/// dependency on the device and mixer code.
/// </summary>
#if OPENXLR_UI
internal static class OpenXlrPaths
#else
public static class OpenXlrPaths
#endif
{
    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>$XDG_CONFIG_HOME when set and non-empty, else ~/.config.</summary>
    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>The configuration directory, ~/.config/openxlr.</summary>
    public static string ConfigDir => Path.Combine(ConfigHome, "openxlr");

    /// <summary>A file directly under the configuration directory.</summary>
    public static string ConfigFile(string name) => Path.Combine(ConfigDir, name);

    /// <summary>
    /// Create a directory under the configuration directory, private to the
    /// user, and tighten it if an earlier version created it world-readable.
    /// </summary>
    public static void EnsurePrivateDir(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        // CreateDirectory applies the mode to the leaf only; the components
        // between the configuration directory and the leaf would come up
        // with the default mode, so create them one by one. Anything above
        // the configuration directory (~/.config itself) is left alone.
        string root = Path.GetFullPath(ConfigDir);
        string full = Path.GetFullPath(path);
        if (full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            string cur = root;
            CreatePrivate(cur);
            foreach (string part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                cur = Path.Combine(cur, part);
                CreatePrivate(cur);
            }
        }
        else CreatePrivate(full);

        static void CreatePrivate(string dir)
        {
            Directory.CreateDirectory(dir, PrivateDir);
            if (File.GetUnixFileMode(dir) != PrivateDir) File.SetUnixFileMode(dir, PrivateDir);
        }
    }

    /// <summary>Write text to a private file atomically, creating its directory.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string dir = Path.GetDirectoryName(path)!;
        EnsurePrivateDir(dir);
        string tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        using (var writer = new StreamWriter(tmp, options)) writer.Write(text);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(tmp) != PrivateFile) File.SetUnixFileMode(tmp, PrivateFile);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Serialize a value and write it as a private file atomically.</summary>
    public static void WriteAtomicJson<T>(string path, T value, JsonSerializerOptions options)
        => WriteAtomic(path, JsonSerializer.Serialize(value, options));

    /// <summary>
    /// Open a new private file for writing (0600 from the first byte), for
    /// callers that stream into it, such as the diagnostics archive.
    /// </summary>
    public static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        return new FileStream(path, options);
    }
}
