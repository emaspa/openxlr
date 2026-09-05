using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DaemonClientTests
{
    [Fact]
    public async Task ConcurrentQueriesShareReplyEvenWhenOneCallerTimesOut()
    {
        int connections = 0, requests = 0;
        var release = Completion();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            Interlocked.Increment(ref connections);
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;   // every connection opens with the token
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    Interlocked.Increment(ref requests);
                    await release.Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = "EQ" } } }, stop);
                }
                else
                    await SocketTestServer.Send(socket, new { type = "diagnostics" }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<System.Text.Json.Nodes.JsonNode?> first = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.Zero));
        var second = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("EQ", (await first)![0]!["name"]!.GetValue<string>());
        Assert.Equal("EQ", (await second)![0]!["name"]!.GetValue<string>());
        // This reply is a server-side barrier after any accidental duplicate requests.
        Assert.NotNull(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, requests);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task DisconnectReleasesQueryAndReconnectsWithoutReplayingIt()
    {
        int connections = 0, requests = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                Assert.Equal("auth", (await SocketTestServer.Receive(socket, stop))["cmd"]!.GetValue<string>());
                await SocketTestServer.Receive(socket, stop);
                Interlocked.Increment(ref requests);
                socket.Abort();
            }
            else
            {
                await SocketTestServer.Send(socket, new { type = "state" }, stop);
                await Task.Delay(Timeout.Infinite, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        var restored = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.StateReceived += _ => restored.TrySetResult();
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5)));
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, connections);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DisconnectedCommandsReportFailureAndDisposalIsIdempotent()
    {
        var client = new DaemonClient();
        string? error = null;
        client.ErrorReceived += message => error = message;
        await client.SetLevelAsync("music", "monitor", 0.5);
        Assert.Contains("disconnected", error);
        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromSeconds(30)));
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(client.Start);
        await client.SetLevelAsync("music", "monitor", 0.5);
        Assert.Null(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task DisposalUnblocksPendingReceiveAndQuery()
    {
        var received = Completion();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = client.RequestPluginsAsync(TimeSpan.FromMinutes(1));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private static TaskCompletionSource Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
