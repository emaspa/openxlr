using System;
using System.IO;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>A private socket owned for this listener's lifetime, including after an unclean shutdown.</summary>
#if OPENXLR_UI
internal
#else
public
#endif
sealed class ControlSocketListener : IDisposable
{
    public Socket Socket { get; }
    public string Path { get; }
    private readonly RegularFile.LinuxFileStatus _owned;

    private ControlSocketListener(Socket socket, string path, RegularFile.LinuxFileStatus owned)
        => (Socket, Path, _owned) = (socket, path, owned);

    /// <summary>Call while holding DaemonLock, before any device or audio service starts.</summary>
    public static ControlSocketListener Bind(string? path = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The control socket requires Linux.");
        path ??= OpenXlrPaths.ControlSocketPath;
        if (!System.IO.Path.IsPathFullyQualified(path) || Encoding.UTF8.GetByteCount(path) > 107)
            throw new IOException("The control socket needs an absolute path of at most 107 UTF-8 bytes. Set XDG_RUNTIME_DIR to a shorter private directory.");
        string directory = System.IO.Path.GetDirectoryName(path)!;
        PrepareDirectory(directory);
        if (RegularFile.Inspect(path, noFollow: true) is { } old)
        {
            if ((old.Mode & 0xf000) != 0xc000 || old.User != UnixPeerCredentials.User)
                throw new IOException("Refusing to replace a control path that is not this user's socket.");
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.ConnectAsync(new UnixDomainSocketEndPoint(path)).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                throw new IOException("Another control socket is still listening.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
            {
                if (!Same(old, RegularFile.Inspect(path, noFollow: true)))
                    throw new IOException("The control socket changed during stale-socket inspection.");
                File.Delete(path);
            }
        }
        // Socket.Bind gives its endpoint unconditional unlink-on-close. Bind
        // the descriptor first, so cleanup can instead check our inode and
        // leave a path another listener or a user has replaced alone.
        int descriptor = CreateSocket(1, 1 | 0x80000, 0); // AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC
        if (descriptor < 0) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        var handle = new SafeSocketHandle((IntPtr)descriptor, ownsHandle: true);
        Socket? socket = null;
        try
        {
            var address = new UnixDomainSocketEndPoint(path).Serialize();
            if (BindSocket(handle, address.Buffer.ToArray(), (uint)address.Size) != 0)
                throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            socket = new Socket(handle);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new(socket, path, RegularFile.Inspect(path, noFollow: true)!.Value);
        }
        catch { socket?.Dispose(); handle.Dispose(); throw; }
    }

    public void Dispose()
    {
        Socket.Dispose();
        // A renamed path or a new listener's socket is not ours to remove.
        try { if (Same(_owned, RegularFile.Inspect(Path, noFollow: true))) File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Prepare the shared lock/socket directory without chmod through a link.</summary>
    public static void PrepareDirectory(string directory)
    {
        if (RegularFile.Inspect(directory, noFollow: true) is { } parent &&
            ((parent.Mode & 0xf000) != 0x4000 || parent.User != UnixPeerCredentials.User))
            throw new IOException("The control socket directory must be a directory owned by this user, without a symbolic link.");
        OpenXlrPaths.EnsurePrivateDir(directory);
    }

    private static bool Same(RegularFile.LinuxFileStatus expected, RegularFile.LinuxFileStatus? current)
        => current is { } value && (value.Mode & 0xf000) == 0xc000 && value.User == expected.User
           && value.Inode == expected.Inode && value.DeviceMajor == expected.DeviceMajor && value.DeviceMinor == expected.DeviceMinor;

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int CreateSocket(int family, int type, int protocol);
    [DllImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static extern int BindSocket(SafeSocketHandle socket, byte[] address, uint length);
}
