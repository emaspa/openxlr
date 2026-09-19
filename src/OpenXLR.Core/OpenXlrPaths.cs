using System;
using System.IO;
using System.Text;
using System.Text.Json;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
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
/// Compiled into the daemon through OpenXLR.Core and into the window and the
/// terminal mixer as a linked source file (internal, in that application's own
/// namespace, so the test project sees one public type), so all three agree
/// without the two front ends taking a dependency on the device and mixer
/// code.
/// </summary>
#if OPENXLR_UI || OPENXLR_TUI
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
    /// The control API token: written by the daemon at every start, read by
    /// each client before it connects. Under the runtime directory (tmpfs,
    /// per login session, gone at logout) when the session has one, else
    /// under the configuration directory; private either way.
    /// </summary>
    public static string TokenPath =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
            ? Path.Combine(run, "openxlr", "token")
            : ConfigFile("token");

    /// <summary>The current token, or null when the daemon has not written one.</summary>
    public static string? ReadToken()
    {
        try
        {
            string t = File.ReadAllText(TokenPath).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

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

    /// <summary>
    /// Write text atomically, creating its directory. Private permissions are
    /// the default. Startup files in desktop-owned directories opt out so the
    /// directory keeps its permissions and the file follows the process umask.
    /// </summary>
    public static void WriteAtomic(string path, string text, bool privatePermissions = true)
        => WriteAtomic(path, Encoding.UTF8.GetBytes(text), privatePermissions);

    /// <summary>Write bytes atomically, creating the directory; see the text overload.</summary>
    public static void WriteAtomic(string path, byte[] bytes, bool privatePermissions = true)
    {
        string dir = Path.GetDirectoryName(path)!;
        if (privatePermissions) EnsurePrivateDir(dir);
        else Directory.CreateDirectory(dir);
        // Each writer owns its staging file. Reusing path + ".tmp" lets
        // concurrent writers collide, follows a leftover symbolic link, and
        // trips over a leftover from a crash for as long as it stays there.
        string tmp = Path.Combine(dir, ".openxlr-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool published = false;
        try
        {
            using (FileStream stream = privatePermissions ? CreatePrivate(tmp)
                : new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
            File.Move(tmp, path, overwrite: true);
            published = true;
        }
        finally
        {
            // The staging file is gone once the rename took it; after a
            // failure it is removed so the next writer finds a clean directory.
            if (!published)
            {
                try { File.Delete(tmp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the caller sees the failure that got here */ }
            }
        }
    }

    /// <summary>Serialize a value and write it as a private file atomically.</summary>
    public static void WriteAtomicJson<T>(string path, T value, JsonSerializerOptions options)
        => WriteAtomic(path, JsonSerializer.Serialize(value, options));

    /// <summary>
    /// Open a new private file for writing (0600 from the first byte), for
    /// callers that stream into it, such as the diagnostics archive. Existing
    /// files and symbolic links are refused without truncating their contents.
    /// </summary>
    public static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        var stream = new FileStream(path, options);
        // The umask can strip bits from the requested mode; the file is ours
        // to tighten before the first byte lands.
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(stream.SafeFileHandle) != PrivateFile)
            File.SetUnixFileMode(stream.SafeFileHandle, PrivateFile);
        return stream;
    }
}
