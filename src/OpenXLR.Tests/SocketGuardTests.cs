using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class SocketGuardTests
{
    [Fact]
    public async Task AFragmentedMessageThatNeverCompletesIsClosedOnItsDeadline()
    {
        var served = new TaskCompletionSource<(SocketGuard.Outcome, TimeSpan)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var sw = Stopwatch.StartNew();
            var result = await SocketGuard.ReceiveMessageAsync(socket, new byte[1024], 64 * 1024, TimeSpan.FromMilliseconds(500), stop);
            served.TrySetResult((result.Outcome, sw.Elapsed));
        });
        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(server.Url), cts.Token);
        await client.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":"), WebSocketMessageType.Text, endOfMessage: false, cts.Token);
        // The client now stalls. The server must give up on its own.
        (SocketGuard.Outcome outcome, TimeSpan elapsed) = await served.Task.WaitAsync(cts.Token);
        Assert.Equal(SocketGuard.Outcome.TooSlow, outcome);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        // And it told the client why, with a bounded close.
        var buf = new byte[1024];
        WebSocketReceiveResult res = await client.ReceiveAsync(buf, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, res.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, res.CloseStatus);
    }

    [Fact]
    public async Task AWholeMessageArrivesUntouchedAndAQuietClientIsNotOnTheClock()
    {
        var served = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var result = await SocketGuard.ReceiveMessageAsync(socket, new byte[8], 64 * 1024, TimeSpan.FromMilliseconds(300), stop);
            served.TrySetResult(result.Outcome + ":" + Encoding.UTF8.GetString(result.Message ?? []));
        });
        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(server.Url), cts.Token);
        await Task.Delay(700, cts.Token);   // longer than the message deadline, but no message has started
        await client.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"getState\"}"), WebSocketMessageType.Text, true, cts.Token);
        Assert.Equal("Message:{\"cmd\":\"getState\"}", await served.Task.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task ACloseThePeerNeverAnswersIsAbortedOnItsDeadline()
    {
        var closed = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var sw = Stopwatch.StartNew();
            await SocketGuard.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "test", TimeSpan.FromMilliseconds(300));
            closed.TrySetResult(sw.Elapsed);
        });
        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(server.Url), cts.Token);
        // The client never reads, so it never completes the close handshake.
        TimeSpan elapsed = await closed.Task.WaitAsync(cts.Token);
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }
}
