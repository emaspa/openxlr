using System;
using System.IO;
using System.Runtime.InteropServices;
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
/// temporary file next to the target, synced to disk, then a rename and a
/// sync of the directory, so neither a crash mid-write nor a power loss
/// right after one leaves a truncated file behind.
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
    /// <summary>The byte limit for the shared preference files.</summary>
    public const int MaxPreferenceBytes = 4 * 1024 * 1024;

    private const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>$XDG_CONFIG_HOME when set and non-empty, else ~/.config.</summary>
    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>The configuration directory, ~/.config/openxlr.</summary>
    public static string ConfigDir => Path.Combine(ConfigHome, "openxlr");

    /// <summary>Runtime history such as logs, separate from atomic preferences.</summary>
    public static string StateDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "openxlr");

    /// <summary>
    /// Data the daemon keeps that is neither a preference nor a log, such as
    /// the soundboard's clips: $XDG_DATA_HOME/openxlr when that is set and
    /// non-empty, else ~/.local/share/openxlr.
    /// </summary>
    public static string DataDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "openxlr");

    /// <summary>A file directly under the configuration directory.</summary>
    public static string ConfigFile(string name) => Path.Combine(ConfigDir, name);

    /// <summary>
    /// The private runtime directory, or the configuration directory on a
    /// headless session without XDG_RUNTIME_DIR.
    /// </summary>
    public static string RuntimeDir =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
            ? Path.Combine(run, "openxlr") : ConfigDir;

    /// <summary>The control API, authenticated by Linux peer credentials.</summary>
    public static string ControlSocketPath => Path.Combine(RuntimeDir, "control.sock");

    /// <summary>
    /// The daemon's single-instance lock. Every release since the lock was
    /// added keeps it at this path, so a client can tell that a daemon from
    /// before the control socket is still running.
    /// </summary>
    public static string DaemonLockPath => Path.Combine(RuntimeDir, "daemon.lock");

    /// <summary>The API token that releases before the control socket wrote; only removed now.</summary>
    public static string LegacyTokenPath => Path.Combine(RuntimeDir, "token");

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
    /// Create or check a directory that holds a socket, such as the runtime
    /// directory with the daemon's control socket and lock. It has to be a
    /// real directory, not a symbolic link, owned by this user with mode
    /// 0700; one of ours with a looser mode is tightened, anything else is
    /// refused with an <see cref="IOException"/> that says why. Its parent
    /// (XDG_RUNTIME_DIR, or the configuration home without one) has to belong
    /// to this user or to root and must not be writable by other users unless
    /// it is sticky, so nobody else can swap the directory after the check.
    /// That matters when XDG_RUNTIME_DIR is shared or wrongly set: in a
    /// container, under sudo -E, or pointed at /tmp.
    /// </summary>
    public static void EnsurePrivateRuntimeDir(string directory)
    {
        if (!OperatingSystem.IsLinux()) { EnsurePrivateDir(directory); return; }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string parent = Path.GetDirectoryName(full) ?? throw new IOException($"The runtime directory {full} has no parent.");
        RequireSafeParent(full, parent, allowMissing: true);
        // Checked before anything is created or tightened, so the mode change
        // below never reaches through a link or into another user's directory.
        RequirePrivateDirectory(full, allowMissing: true, exactMode: false);
        EnsurePrivateDir(full);
        RequireSafeParent(full, parent, allowMissing: false);
        RequirePrivateDirectory(full, allowMissing: false, exactMode: true);
    }

    private static void RequirePrivateDirectory(string directory, bool allowMissing, bool exactMode)
    {
        if (RegularFile.Inspect(directory, noFollow: true) is not { } status)
        {
            if (allowMissing) return;
            throw new IOException($"The runtime directory {directory} could not be created.");
        }
        if ((status.Mode & 0xf000) == 0xa000)
            throw new IOException($"The runtime directory {directory} is a symbolic link, not a directory.");
        if ((status.Mode & 0xf000) != 0x4000)
            throw new IOException($"The runtime directory {directory} is not a directory.");
        uint user = UnixPeerCredentials.User;
        if (status.User != user)
            throw new IOException($"The runtime directory {directory} belongs to user {status.User}, not to this user ({user}).");
        if (exactMode && (status.Mode & 0xfff) != (int)PrivateDir)
            throw new IOException($"The runtime directory {directory} has mode {Convert.ToString(status.Mode & 0xfff, 8)}, not 700.");
    }

    private static void RequireSafeParent(string directory, string parent, bool allowMissing)
    {
        // The parent may itself be a link (/var/run to /run); what it leads to counts.
        if (RegularFile.Inspect(parent) is not { } status)
        {
            if (allowMissing) return;
            throw new IOException($"The parent of the runtime directory {directory} is missing.");
        }
        uint user = UnixPeerCredentials.User;
        if ((status.Mode & 0xf000) != 0x4000)
            throw new IOException($"The parent of the runtime directory {directory} is not a directory.");
        if (status.User != user && status.User != 0)
            throw new IOException($"The parent of the runtime directory {directory}, {parent}, belongs to user {status.User}; set XDG_RUNTIME_DIR to this user's own runtime directory.");
        // Writable by everyone and not sticky: anyone could rename the directory away.
        if ((status.Mode & 0x2) != 0 && (status.Mode & 0x200) == 0)
            throw new IOException($"The parent of the runtime directory {directory}, {parent}, is writable by every user; set XDG_RUNTIME_DIR to this user's own runtime directory.");
    }

    /// <summary>
    /// Write text atomically, creating its directory. Private permissions are
    /// the default. Startup files in desktop-owned directories opt out so the
    /// directory keeps its permissions and the file follows the process umask.
    /// </summary>
    public static void WriteAtomic(string path, string text, bool privatePermissions = true)
        => WriteAtomic(path, Encoding.UTF8.GetBytes(text), privatePermissions);

    /// <summary>
    /// Write bytes atomically; an explicit Unix file mode is applied before
    /// publication, for restoring an original without changing its directory.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes, bool privatePermissions = true)
        => WriteAtomic(path, bytes, privatePermissions, fileMode: null);

    public static void WriteAtomic(string path, byte[] bytes, bool privatePermissions, UnixFileMode? fileMode)
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
            using (FileStream stream = privatePermissions || fileMode is not null ? CreatePrivate(tmp)
                : new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                if (!OperatingSystem.IsWindows() && fileMode is { } mode) File.SetUnixFileMode(stream.SafeFileHandle, mode);
                // The data reaches the disk before the rename can. Without it a
                // file system that orders metadata ahead of data (XFS, F2FS)
                // can come back from a power loss with the new name on an
                // empty file.
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            published = true;
            SyncDirectory(dir);
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

    /// <summary>
    /// Make a rename in the directory durable. Best effort: the file is
    /// already complete under one name or the other, so a directory that
    /// cannot be opened or synced only loses which of the two survives.
    /// </summary>
    private static void SyncDirectory(string dir)
    {
        if (!OperatingSystem.IsLinux()) return;
        // O_RDONLY | O_CLOEXEC, the same values on x86-64 and arm64. .NET
        // refuses to open a directory, so the descriptor comes from libc.
        int fd = LibcOpen(dir, 0x80000);
        if (fd < 0) return;
        _ = LibcFsync(fd);
        _ = LibcClose(fd);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int LibcFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LibcClose(int fd);

    /// <summary>Serialize a value and write it as a private file atomically.</summary>
    public static void WriteAtomicJson<T>(string path, T value, JsonSerializerOptions options)
        => WriteAtomicJson(path, value, options, int.MaxValue);

    /// <summary>Refuse serialized JSON above the byte limit before replacing the existing file.</summary>
    public static void WriteAtomicJson<T>(string path, T value, JsonSerializerOptions options, int maximumBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        if (bytes.Length > maximumBytes)
            throw new IOException($"JSON file exceeds the {maximumBytes} byte limit.");
        WriteAtomic(path, bytes);
    }

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
