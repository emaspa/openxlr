using System.Net.WebSockets;

namespace OpenXLR.Daemon;

/// <summary>
/// Deadlines for one client socket, so a peer that stalls can never hold a
/// connection slot or a handler for longer than a few seconds. Three
/// rules: a message must arrive whole within <see cref="MessageDeadline"/>
/// of its first fragment; a close handshake gets <see cref="CloseDeadline"/>
/// before the socket is aborted; a peer that stops answering the transport
/// pings is dropped by the keep-alive timeout the accept sets up (a client
/// that is merely quiet stays: the window and the plugin only listen for
/// most of their life).
/// </summary>
internal static class SocketGuard
{
    public static readonly TimeSpan MessageDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CloseDeadline = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Why <see cref="ReceiveMessageAsync"/> returned no message.</summary>
    public enum Outcome { Message, PeerClosed, NotText, TooBig, TooSlow, Stopping }

    /// <summary>
    /// Receive one whole text message, or say why there is none. On every
    /// outcome but Message and Stopping the socket has already been closed
    /// with the matching status (bounded, see <see cref="CloseAsync"/>).
    /// </summary>
    public static async Task<(Outcome Outcome, byte[]? Message)> ReceiveMessageAsync(
        WebSocket socket, byte[] buf, int maxBytes, TimeSpan messageDeadline, CancellationToken stopping)
    {
        using var ms = new MemoryStream();
        DateTime? due = null;   // set by the first fragment of a multi-frame message
        WebSocketReceiveResult res;
        do
        {
            Task<WebSocketReceiveResult> recv;
            try
            {
                recv = socket.ReceiveAsync(buf, stopping);
                if (due is DateTime d)
                {
                    // Cancelling a pending receive would abort the socket
                    // without a word to the peer, so race it with the clock
                    // and close properly when the clock wins.
                    TimeSpan left = d - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || await Task.WhenAny(recv, Task.Delay(left, stopping)) != recv)
                    {
                        if (stopping.IsCancellationRequested)
                        {
                            await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "daemon stopping");
                            return (Outcome.Stopping, null);
                        }
                        await CloseWithPendingReceiveAsync(socket, recv, WebSocketCloseStatus.PolicyViolation,
                            $"message not completed within {messageDeadline.TotalSeconds:0} s");
                        return (Outcome.TooSlow, null);
                    }
                }
                res = await recv;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "daemon stopping");
                return (Outcome.Stopping, null);
            }
            if (res.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "");
                return (Outcome.PeerClosed, null);
            }
            if (res.MessageType != WebSocketMessageType.Text)
            {
                await CloseAsync(socket, WebSocketCloseStatus.InvalidMessageType, "text messages only");
                return (Outcome.NotText, null);
            }
            if (ms.Length + res.Count > maxBytes)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, $"command exceeds {maxBytes} bytes");
                return (Outcome.TooBig, null);
            }
            ms.Write(buf, 0, res.Count);
            // The clock starts with the first fragment, so a quiet client
            // between commands is never on it.
            if (due is null && !res.EndOfMessage) due = DateTime.UtcNow + messageDeadline;
        } while (!res.EndOfMessage);
        return (Outcome.Message, ms.ToArray());
    }

    /// <summary>
    /// Close while a receive is still pending: send the close frame through
    /// the output side (allowed alongside a receive), give the peer the
    /// close deadline to answer, then abort. The pending receive completes
    /// either with the peer's close frame or with the abort; both are
    /// observed so nothing is left unhandled.
    /// </summary>
    private static async Task CloseWithPendingReceiveAsync(WebSocket socket, Task<WebSocketReceiveResult> pending,
        WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(CloseDeadline);
            await socket.CloseOutputAsync(status, reason, cts.Token);
            await Task.WhenAny(pending, Task.Delay(CloseDeadline));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                                   or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // fall through to the abort
        }
        if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
        {
            try { socket.Abort(); } catch (ObjectDisposedException) { }
        }
        _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Close with a status and wait for the peer's close frame, but only
    /// for <see cref="CloseDeadline"/>; then abort. A peer that never
    /// answers the handshake cannot keep the handler alive.
    /// </summary>
    public static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        => await CloseAsync(socket, status, reason, CloseDeadline);

    public static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, TimeSpan deadline)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var cts = new CancellationTokenSource(deadline);
        try
        {
            await socket.CloseAsync(status, reason, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException
                                   or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { socket.Abort(); } catch (ObjectDisposedException) { }
        }
    }
}
