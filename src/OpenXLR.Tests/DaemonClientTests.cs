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
                // Like the daemon: the reply, then the commandResult for the requestId.
                string requestId = command["requestId"]!.GetValue<string>();
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    Interlocked.Increment(ref requests);
                    await release.Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = "EQ" } } }, stop);
                }
                else
                    await SocketTestServer.Send(socket, new { type = "diagnostics" }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId }, stop);
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
    public async Task ALateReplyToATimedOutQueryNeverAnswersTheNextOne()
    {
        // The first catalog request is answered only after a second one was
        // sent; the answers arrive in order, each followed by its
        // commandResult, and the second caller must get the second answer.
        var release = Completion();
        var answers = new System.Collections.Concurrent.ConcurrentQueue<(string Id, string Name)>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                answers.Enqueue((command["requestId"]!.GetValue<string>(), answers.Count == 0 ? "OLD" : "NEW"));
                if (answers.Count < 2) { await release.Task.WaitAsync(stop); continue; }
                while (answers.TryDequeue(out var a))
                {
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = new[] { new { name = a.Name } } }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = a.Id }, stop);
                }
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await client.RequestPluginsAsync(TimeSpan.FromMilliseconds(200)));   // gives up; its reply is still owed
        Task<System.Text.Json.Nodes.JsonNode?> second = client.RequestPluginsAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("NEW", (await second)![0]!["name"]!.GetValue<string>());
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
        Assert.Null(await client.AddWindowsPluginFolderAsync("/plugins", TimeSpan.FromSeconds(30)));
        Assert.Null(await client.RemoveWindowsPluginFolderAsync("/plugins", TimeSpan.FromSeconds(30)));
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

    [Fact]
    public async Task PluginFolderChangesAreSentSeparatelyInsteadOfSharingAnInstallReply()
    {
        var received = Completion();
        var release = Completion();
        var commands = new List<(string Command, string? Path)>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string name = command["cmd"]!.GetValue<string>();
                if (name == "auth") continue;
                commands.Add((name, command["path"]?.GetValue<string>()));
                if (commands.Count == 1)
                {
                    received.SetResult();
                    await release.Task.WaitAsync(stop);
                }
                await SocketTestServer.Send(socket, new { type = "pluginInstall", ok = true, message = name }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var add = client.AddWindowsPluginFolderAsync("/plugins/new", TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = client.RemoveWindowsPluginFolderAsync("/plugins/old", TimeSpan.FromSeconds(5));
        var sync = client.SyncWindowsPluginsAsync(TimeSpan.FromSeconds(5));
        var removeUses = client.RemoveWindowsPluginInsertsAsync("/plugins/new/EQ.vst3", TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("addWindowsPluginFolder", (await add)!["message"]!.GetValue<string>());
        Assert.Equal("removeWindowsPluginFolder", (await remove)!["message"]!.GetValue<string>());
        Assert.Equal("syncWindowsPlugins", (await sync)!["message"]!.GetValue<string>());
        Assert.Equal("removeWindowsPluginInserts", (await removeUses)!["message"]!.GetValue<string>());
        Assert.Equal(new (string, string?)[]
        {
            ("addWindowsPluginFolder", "/plugins/new"),
            ("removeWindowsPluginFolder", "/plugins/old"),
            ("syncWindowsPlugins", null),
            ("removeWindowsPluginInserts", "/plugins/new/EQ.vst3"),
        }, commands);
    }

    [Fact]
    public async Task FilesFromDifferentFoldersDoNotShareAReply()
    {
        var received = Completion();
        var release = Completion();
        int requests = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                if (++requests == 1) { received.SetResult(); await release.Task.WaitAsync(stop); }
                await SocketTestServer.Send(socket, new
                {
                    type = "windowsPluginFiles", ok = true,
                    plugins = new[] { new { path = command["path"]!.GetValue<string>() + "/EQ.vst3" } },
                }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = Completion();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = client.RequestWindowsPluginFilesAsync("/first", TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.RequestWindowsPluginFilesAsync("/second", TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("/first/EQ.vst3", (await first)!["plugins"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("/second/EQ.vst3", (await second)!["plugins"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(2, requests);
    }

    private static TaskCompletionSource Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
