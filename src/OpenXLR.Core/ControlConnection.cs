using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>One WebSocket over the user's Unix socket; no network, proxy or token file.</summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
sealed class ControlConnection : IDisposable
{
    private readonly Uri _uri;
    private readonly HttpMessageInvoker? _http;
    public ClientWebSocket Socket { get; } = new();
    internal int PeerProcessId { get; private set; }

    // An explicit endpoint is used by client tests and callers that own their
    // transport. Normal clients always use the private Unix socket.
    public ControlConnection(Uri? endpoint = null, string? socketPath = null)
    {
        _uri = endpoint ?? new Uri("ws://localhost/ws");
        if (endpoint is null) _http = new HttpMessageInvoker(HttpHandler(socketPath, pid => PeerProcessId = pid));
    }

    public Task ConnectAsync(CancellationToken cancel) => _http is null
        ? Socket.ConnectAsync(_uri, cancel) : Socket.ConnectAsync(_uri, _http, cancel);

    public static SocketsHttpHandler HttpHandler(string? path = null) => HttpHandler(path, null);

    private static SocketsHttpHandler HttpHandler(string? path, Action<int>? peerConnected) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = async (_, cancel) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path ?? OpenXlrPaths.ControlSocketPath), cancel).ConfigureAwait(false);
                int pid = UnixPeerCredentials.SameUserProcessId(socket);
                peerConnected?.Invoke(pid);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public void Dispose() { Socket.Dispose(); _http?.Dispose(); }
}

/// <summary>Authenticate both ends before HTTP or application data crosses the connection.</summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
static class UnixPeerCredentials
{
    public static uint User => OperatingSystem.IsLinux() ? GetEffectiveUser() : throw new PlatformNotSupportedException("The control socket requires Linux peer credentials.");

    public static void RequireSameUser(Socket socket) => _ = SameUserProcessId(socket);

    internal static int SameUserProcessId(Socket socket)
    {
        if (socket.AddressFamily != AddressFamily.Unix) throw new IOException("Peer credentials require a Unix socket.");
        uint size = 12;
        if (!OperatingSystem.IsLinux() || GetCredentials(socket.SafeHandle, 1, 17, out var peer, ref size) != 0 || size != 12)
            throw new IOException("Could not authenticate the Unix socket peer.");
        if (peer.User != User) throw new UnauthorizedAccessException("The control socket peer belongs to another user.");
        return peer.Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public int Process; public uint User, Group; }
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUser();
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetCredentials(SafeSocketHandle socket, int level, int option, out Credentials credentials, ref uint size);
}
