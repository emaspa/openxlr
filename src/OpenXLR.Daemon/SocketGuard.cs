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
        // One clock for the whole message, not one per fragment. A timer and a
        // cancellation registration per fragment would let a peer that sends
        // many small (or empty, which the byte count cannot see) continuation
        // frames pile both up until the deadline passed, on every connection
        // at once. Cancelling it on the way out releases the timer at once,
        // rather than leaving it armed until the deadline it never needed.
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        try { return await ReceiveMessageAsync(socket, buf, maxBytes, messageDeadline, stopping, clock.Token); }
        finally { clock.Cancel(); }
    }

    private static async Task<(Outcome Outcome, byte[]? Message)> ReceiveMessageAsync(
        WebSocket socket, byte[] buf, int maxBytes, TimeSpan messageDeadline, CancellationToken stopping,
        CancellationToken clock)
    {
        using var ms = new MemoryStream();
        Task? expired = null;   // started by the first fragment of a multi-frame message
        int fragments = 0;
        WebSocketReceiveResult res;
        do
        {
            Task<WebSocketReceiveResult> recv;
            try
            {
                recv = socket.ReceiveAsync(buf, stopping);
                if (expired is not null)
                {
                    // Cancelling a pending receive would abort the socket
                    // without a word to the peer, so race it with the clock
                    // and close properly when the clock wins.
                    if (expired.IsCompleted || await Task.WhenAny(recv, expired) != recv)
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
            // An empty continuation frame carries no bytes, so the size limit
            // cannot see it. Count the frames as well, or a peer could hold a
            // handler and a connection slot busy with nothing at all until the
            // deadline. One frame per byte of the limit is far more than any
            // real client sends and still bounds the loop.
            if (ms.Length + res.Count > maxBytes || ++fragments > maxBytes)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, $"command exceeds {maxBytes} bytes");
                return (Outcome.TooBig, null);
            }
            ms.Write(buf, 0, res.Count);
            // The clock starts with the first fragment, so a quiet client
            // between commands is never on it, and it runs once for the whole
            // message rather than once per fragment.
            if (expired is null && !res.EndOfMessage) expired = Task.Delay(messageDeadline, clock);
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
