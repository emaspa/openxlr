using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>What a client finds when it looks for the daemon.</summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
enum DaemonPresence
{
    /// <summary>The control socket accepted this user's connection.</summary>
    Reachable,
    /// <summary>
    /// Neither the socket nor the old port answered: no daemon, or one of
    /// this release that is starting or stopping.
    /// </summary>
    NotRunning,
    /// <summary>
    /// The loopback port of the releases before the control socket answered:
    /// a daemon from before the update.
    /// </summary>
    OlderDaemon,
    /// <summary>The control socket answered for another user and was refused.</summary>
    OtherUser,
}

/// <summary>
/// Tells the cases apart after a client could not connect, so each client can
/// say what to do. Packages do not restart a running daemon, so after an
/// upgrade the old daemon still runs, listening only on the loopback port.
///
/// That port is the one sign of an older daemon: every release before the
/// control socket listened on it, and no later one does. Only a bare TCP
/// connect is made there; nothing is sent. The Flatpak shares the host's
/// network, so the port answers across the sandbox as well.
///
/// The single-instance lock is no such sign. A daemon of this release holds
/// it while its socket is not accepting: from the moment it starts until the
/// mixer and devices are up, which takes seconds, and from the moment it
/// stops accepting while it shuts down. A probe in either window during a
/// restart would call it older, so the probe leaves the lock alone.
/// </summary>
#if OPENXLR_UI || OPENXLR_TUI
internal
#else
public
#endif
static class DaemonProbe
{
    /// <summary>The loopback port the releases before the control socket listened on.</summary>
    public const int LegacyPort = 37890;

    public static async Task<DaemonPresence> ProbeAsync(string? socketPath = null, int legacyPort = LegacyPort,
        CancellationToken cancel = default)
    {
        socketPath ??= OpenXlrPaths.ControlSocketPath;
        if (await SocketAsync(socketPath, cancel).ConfigureAwait(false) is { } answered) return answered;
        return await LegacyPortAnswersAsync(legacyPort, cancel).ConfigureAwait(false)
            ? DaemonPresence.OlderDaemon : DaemonPresence.NotRunning;
    }

    /// <summary>The command that restarts the daemon in this installation.</summary>
    public static string RestartCommand => Environment.GetEnvironmentVariable("FLATPAK_ID") == "io.github.emaspa.openxlr"
        ? "systemctl --user restart openxlr-flatpak" : "systemctl --user restart openxlr-daemon";

    /// <summary>One plain English sentence for clients without the UI's catalogue.</summary>
    public static string Describe(DaemonPresence presence) => presence switch
    {
        DaemonPresence.OlderDaemon => "An OpenXLR daemon from before the update is still running and cannot "
            + "accept this client. Restart it: " + RestartCommand,
        DaemonPresence.OtherUser => "The OpenXLR control socket belongs to another user and was refused.",
        DaemonPresence.NotRunning => "The OpenXLR daemon is not running.",
        _ => "The OpenXLR daemon did not accept the connection.",
    };

    /// <summary>The short name clients see in an openxlr-control error line.</summary>
    public static string Reason(DaemonPresence presence) => presence switch
    {
        DaemonPresence.OlderDaemon => "olderDaemon",
        DaemonPresence.OtherUser => "otherUser",
        DaemonPresence.NotRunning => "notRunning",
        _ => "unavailable",
    };

    /// <summary>Null when the socket is absent, refuses, or does not answer in time.</summary>
    private static async Task<DaemonPresence?> SocketAsync(string path, CancellationToken cancel)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
            UnixPeerCredentials.RequireSameUser(socket);
            return DaemonPresence.Reachable;
        }
        catch (UnauthorizedAccessException) { return DaemonPresence.OtherUser; }
        // A socket of another user's daemon is 0600 in its own 0700 directory.
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied) { return DaemonPresence.OtherUser; }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ArgumentException
            && !cancel.IsCancellationRequested) { return null; }
    }

    private static async Task<bool> LegacyPortAnswersAsync(int port, CancellationToken cancel)
    {
        if (port is <= 0 or > 65535) return false;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }
}
