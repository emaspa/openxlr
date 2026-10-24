using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserNativeHostTests
{
    [Fact]
    public async Task FramesUseLittleEndianLengthsAndCleanEofIsNotAnError()
    {
        using var stream = new MemoryStream();
        var payload = new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray() };
        await BrowserNativeHost.WriteFrameAsync(stream, payload);
        byte[] bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        stream.Position = 0;
        Assert.True(JsonNode.DeepEquals(payload, await BrowserNativeHost.ReadFrameAsync(stream)));
        Assert.Null(await BrowserNativeHost.ReadFrameAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BrowserNativeHost.MaxMessageBytes + 1)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidLengthsAreRejectedBeforeTheBodyIsRead(uint length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeHost.ReadFrameAsync(stream));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task TruncatedAndMalformedFramesAreRefused()
    {
        using var shortHeader = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeHost.ReadFrameAsync(shortHeader));
        using var shortBody = Frame("{}", declaredSize: 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeHost.ReadFrameAsync(shortBody));
        using var array = Frame("[]");
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserNativeHost.ReadFrameAsync(array));
        using var invalid = Frame("not json");
        await Assert.ThrowsAnyAsync<JsonException>(() => BrowserNativeHost.ReadFrameAsync(invalid));
        using var duplicate = Frame("""{"type":"tabs","type":"enable"}""");
        await Assert.ThrowsAnyAsync<JsonException>(() => BrowserNativeHost.ReadFrameAsync(duplicate));
    }

    [Theory]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?query=private")]
    [InlineData("https://user:password@example.com")]
    [InlineData("file:///private/document")]
    [InlineData("data:text/plain,hello")]
    public void MetadataContainsOriginsRatherThanPageUrls(string site)
    {
        var tabs = new JsonArray(Tab(site: site));
        Assert.Empty(BrowserNativeHost.ReadTabs(tabs));
    }

    [Theory]
    [InlineData("http://[::ffff:102:304]", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://[::ffff:1.2.3.4]", false)]
    [InlineData("https://a*b.example", false)]
    [InlineData("https://a!b.example", false)]
    [InlineData("https://EXAMPLE.com", false)]
    public void OnlyOriginsTheDaemonReportsUnchangedAreListed(string site, bool listed)
    {
        var other = Tab(); other["id"] = 8;
        IReadOnlyList<BrowserNativeTab> tabs = BrowserNativeHost.ReadTabs(new JsonArray(Tab(site: site), other));
        Assert.Equal(listed ? new[] { 7, 8 } : new[] { 8 }, tabs.Select(tab => tab.Id));
    }

    [Fact]
    public async Task UnsupportedTabOriginsAreSkippedWithoutStoppingTheHost()
    {
        string session = new('a', 32);
        var registered = new TaskCompletionSource<JsonArray>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession") await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                if (cmd == "syncBrowserTabs" && command["tabs"]!.AsArray().Count > 0) registered.TrySetResult(command["tabs"]!.AsArray());
                if (cmd == "setPreserveBrowserOutputs") enabled.TrySetResult();
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = host.RunAsync(timeout.Token);
        Task draining = Task.Run(async () =>
        {
            try { while (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token) is not null) { } }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        });
        try
        {
            JsonObject Listed(int id, string site) { var tab = Tab(site: site); tab["id"] = id; return tab; }
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(
                Listed(1, "http://[::ffff:102:304]"), Listed(2, "https://a*b.example"), Listed(3, "https://a&b.example"),
                Listed(4, "https://example.com")) }, timeout.Token);
            JsonArray tabs = await registered.Task.WaitAsync(timeout.Token);
            Assert.Equal([1, 4], tabs.Select(tab => tab!["id"]!.GetValue<int>()));
            Assert.Equal("http://[::ffff:102:304]", tabs[0]!["site"]!.GetValue<string>());
            Assert.False(running.IsCompleted);
            // The host keeps serving the browser after skipping the tabs.
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "enable", ["value"] = true }, timeout.Token);
            await enabled.Task.WaitAsync(timeout.Token);
            Assert.False(running.IsCompleted);
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            await draining;
            await output.Reader.CompleteAsync();
        }
    }

    [Fact]
    public async Task ActivationRequestsReachTheBrowserOnlyForATabItListedOnThatOrigin()
    {
        string session = new('a', 32);
        var registered = new TaskCompletionSource<JsonArray>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession") await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                if (cmd != "syncBrowserTabs" || command["tabs"]!.AsArray().Count == 0) continue;
                registered.TrySetResult(command["tabs"]!.AsArray());
                // Another tab, another origin, then the listed one.
                await SocketTestServer.Send(socket, new { type = "browserTabActivation", tabId = 8, site = "https://example.com" }, stop);
                await SocketTestServer.Send(socket, new { type = "browserTabActivation", tabId = 7, site = "https://other.example" }, stop);
                await SocketTestServer.Send(socket, new { type = "browserTabActivation", tabId = 7, site = "https://example.com" }, stop);
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = host.RunAsync(timeout.Token);
        try
        {
            var tab = Tab(); tab["activation"] = "popup";
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(tab) }, timeout.Token);
            // The browser's stage goes on to the daemon with the tab.
            Assert.Equal("popup", (await registered.Task.WaitAsync(timeout.Token))[0]!["activation"]!.GetValue<string>());
            JsonObject message;
            do message = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            while (message["type"]!.GetValue<string>() != "activate");
            Assert.Equal(["type", "tabId", "site"], message.Select(item => item.Key));
            Assert.Equal(7, message["tabId"]!.GetValue<int>());
            Assert.Equal("https://example.com", message["site"]!.GetValue<string>());
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Theory]
    [InlineData("\"capturing\"")]
    [InlineData("true")]
    public void AnUnknownActivationStageIsInvalidMetadata(string stage)
    {
        var tab = Tab(); tab["activation"] = JsonNode.Parse(stage);
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(new JsonArray(tab)));
        var none = Tab(); none["activation"] = null;
        Assert.Null(Assert.Single(BrowserNativeHost.ReadTabs(new JsonArray(none))).Activation);
    }

    [Fact]
    public async Task InvalidBrowserDataEndsTheHostWithAnExitCodeInsteadOfACrash()
    {
        // The input stays open, as the browser's does, so only the invalid
        // message can end the host.
        var input = new Pipe();
        var log = new StringWriter();
        var host = new BrowserNativeHost(new DaemonClient("ws://127.0.0.1:1/ws"), input.Reader.AsStream(), new MemoryStream());
        Task<int> serving = Task.Run(() => BrowserNativeHost.Serve(host, log));
        await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "assign", ["tabId"] = 7,
            ["site"] = "https://example.com/private", ["channel"] = "music", ["remember"] = false });
        Assert.Equal(2, await serving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("invalid data", log.ToString(), StringComparison.Ordinal);
        await input.Writer.CompleteAsync();
    }

    [Fact]
    public void MetadataCountsIdsAndTextAreBounded()
    {
        Assert.Single(BrowserNativeHost.ReadTabs(new JsonArray(Tab())));
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(new JsonArray(Tab(), Tab())));
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(new JsonArray(Tab(title: new string('x', 161)))));
        var error = Tab(); error["error"] = new string('x', 201);
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(new JsonArray(error)));
        var missing = Tab(); missing.Remove("id");
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(new JsonArray(missing)));
        var many = new JsonArray();
        for (int id = 0; id < 129; id++) { var tab = Tab(); tab["id"] = id; many.Add(tab); }
        Assert.Throws<InvalidDataException>(() => BrowserNativeHost.ReadTabs(many));
    }

    [Fact]
    public void StateWhitelistExcludesTokensRawFieldsAndOtherProviderTabs()
    {
        string own = new('a', 32), foreign = new('b', 32);
        JsonObject state = State(own);
        state["token"] = "TOP_SECRET";
        state["mixer"]!["token"] = "MIXER_SECRET";
        state["mixer"]!["channels"]!.AsArray().Add(new JsonObject
            { ["id"] = "xlr1", ["name"] = "PRIVATE_HARDWARE", ["hardware"] = true });
        state["mixer"]!["channels"]!.AsArray().Add(new JsonObject
            { ["id"] = "capture", ["name"] = "PRIVATE_CAPTURE", ["captureSource"] = "source" });
        var mine = Tab(); mine["session"] = own; mine["channelId"] = "music"; mine["remember"] = true; mine["token"] = "TAB_SECRET";
        mine["error"] = new JsonObject { ["token"] = "ERROR_SECRET" };
        var other = Tab(title: "PRIVATE_OTHER_BROWSER"); other["session"] = foreign;
        state["mixer"]!["browserTabs"] = new JsonArray(mine, other);

        JsonObject filtered = BrowserNativeHost.FilterState(state, own);
        Assert.Equal(["type", "preserveBrowserOutputs", "channels", "tabs"], filtered.Select(item => item.Key));
        JsonObject channel = Assert.Single(filtered["channels"]!.AsArray())!.AsObject();
        Assert.Equal(["id", "name", "label"], channel.Select(item => item.Key));
        Assert.Equal("OpenXLR Music", channel["label"]!.GetValue<string>());
        JsonObject tab = Assert.Single(filtered["tabs"]!.AsArray())!.AsObject();
        Assert.Equal(["id", "site", "title", "channelId", "remember", "capturing"], tab.Select(item => item.Key));
        string serialized = filtered.ToJsonString();
        Assert.DoesNotContain("SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(own, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(foreign, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionIsQueriedAfterStateAndCachedTabsAreReannouncedAfterReconnect()
    {
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new ConcurrentQueue<string>();
        var registered = new ConcurrentQueue<(string Session, JsonNode Tabs)>();
        int connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            string session = new(connection == 1 ? 'a' : 'b', 32);
            Assert.Equal("auth", (await SocketTestServer.Receive(socket, stop))["cmd"]!.GetValue<string>());
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession")
                {
                    sessions.Enqueue(session);
                    await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                }
                else if (cmd == "syncBrowserTabs")
                {
                    Assert.Equal(session, command["session"]!.GetValue<string>());
                    JsonArray tabs = command["tabs"]!.AsArray();
                    registered.Enqueue((session, tabs.DeepClone()));
                    var state = State(session);
                    foreach (JsonNode? source in tabs)
                    {
                        var tab = source!.DeepClone().AsObject();
                        tab["session"] = session; tab["channelId"] = "music"; tab["remember"] = false;
                        state["mixer"]!["browserTabs"]!.AsArray().Add(tab);
                    }
                    await SocketTestServer.Send(socket, state, stop);
                }
                else Assert.Fail("Native host sent an unexpected daemon command: " + cmd);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                if (cmd == "syncBrowserTabs" && connection == 1 && command["tabs"]!.AsArray().Count > 0)
                {
                    await reconnect.Task.WaitAsync(stop);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", stop);
                    return;
                }
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var client = new DaemonClient(server.Url);
        var host = new BrowserNativeHost(client, input.Reader.AsStream(), output.Writer.AsStream());
        Task running = host.RunAsync();
        await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(Tab()) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonObject first = await ReadStateWithTab(output.Reader.AsStream(), timeout.Token);
        Assert.Equal("music", first["tabs"]![0]!["channelId"]!.GetValue<string>());
        reconnect.TrySetResult();
        JsonObject disconnected = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
        Assert.Equal("state", disconnected["type"]!.GetValue<string>());
        Assert.False(disconnected["preserveBrowserOutputs"]!.GetValue<bool>());
        Assert.Empty(disconnected["channels"]!.AsArray());
        Assert.Empty(disconnected["tabs"]!.AsArray());
        JsonObject connectionError = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
        Assert.Equal("error", connectionError["type"]!.GetValue<string>());
        JsonObject second = await ReadStateWithTab(output.Reader.AsStream(), timeout.Token);
        Assert.Equal(7, second["tabs"]![0]!["id"]!.GetValue<int>());
        Assert.Equal([new string('a', 32), new string('b', 32)], sessions);
        Assert.Contains(registered, entry => entry.Session == new string('b', 32) && entry.Tabs.AsArray().Count == 1);
        await input.Writer.CompleteAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        await output.Reader.CompleteAsync();
    }

    [Fact]
    public async Task CommandFailuresDoNotMirrorDaemonSecretsAndEofDisposesTheSocket()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            try
            {
                await SocketTestServer.Receive(socket, stop);
                await SocketTestServer.Send(socket, State(new string('a', 32)), stop);
                while (!stop.IsCancellationRequested)
                {
                    JsonNode command = await SocketTestServer.Receive(socket, stop);
                    string cmd = command["cmd"]!.GetValue<string>();
                    if (cmd == "getBrowserSession")
                        await SocketTestServer.Send(socket, new { type = "browserSession", session = new string('a', 32) }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                        error = cmd == "setPreserveBrowserOutputs" ? "PRIVATE_TOKEN could not be saved" : null }, stop);
                }
            }
            finally { closed.TrySetResult(); }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        Task running = host.RunAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonObject? message;
        do { message = await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token); }
        while (message?["type"]?.GetValue<string>() != "state");
        await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "enable", ["value"] = true,
            ["cmd"] = "getDiagnostics", ["token"] = "BROWSER_SECRET" });
        do { message = await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token); }
        while (message?["type"]?.GetValue<string>() != "error");
        Assert.Equal(["type", "error"], message!.Select(item => item.Key));
        Assert.DoesNotContain("PRIVATE_TOKEN", message.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BROWSER_SECRET", message.ToJsonString(), StringComparison.Ordinal);
        Assert.InRange(message["error"]!.GetValue<string>().Length, 1, 200);
        await input.Writer.CompleteAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await output.Reader.CompleteAsync();
    }

    [Fact]
    public async Task AQueuedStateFromTheOldConnectionIsDiscardedWhenBrowserOutputIsBlocked()
    {
        var change = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) > 1) { await Task.Delay(Timeout.Infinite, stop); return; }
            string session = new('a', 32);
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "getBrowserSession")
                    await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                if (command["cmd"]!.GetValue<string>() != "syncBrowserTabs") continue;
                await change.Task.WaitAsync(stop);
                var state = State(session);
                state["mixer"]!["channels"]![0]!["name"] = "OLD_QUEUED";
                await SocketTestServer.Send(socket, state, stop);
                await drop.Task.WaitAsync(stop);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", stop);
                return;
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var client = new DaemonClient(server.Url);
        var gate = new GateOutput(output.Writer.AsStream());
        var host = new BrowserNativeHost(client, input.Reader.AsStream(), gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = host.RunAsync(timeout.Token);
        client.ConnectionChanged += up => { if (!up) disconnected.TrySetResult(); };
        try
        {
            await gate.Entered.Task.WaitAsync(timeout.Token);
            change.TrySetResult();
            object stateGate = typeof(BrowserNativeHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            FieldInfo published = typeof(BrowserNativeHost).GetField("_lastPublished", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // Wait for the updated state to enter the output queue, rather
            // than relying on how the two background readers are scheduled.
            while (true)
            {
                lock (stateGate)
                    if ((published.GetValue(host) as JsonObject)?["channels"]?[0]?["name"]?.GetValue<string>() == "OLD_QUEUED") break;
                await Task.Delay(5, timeout.Token);
            }
            drop.TrySetResult();
            await disconnected.Task.WaitAsync(timeout.Token);
            gate.Release.TrySetResult();
            JsonObject inFlight = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("Music", inFlight["channels"]![0]!["name"]!.GetValue<string>());
            JsonObject reset = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.False(reset["preserveBrowserOutputs"]!.GetValue<bool>());
            Assert.Empty(reset["channels"]!.AsArray());
            Assert.Empty(reset["tabs"]!.AsArray());
            Assert.DoesNotContain("OLD_QUEUED", reset.ToJsonString(), StringComparison.Ordinal);
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            gate.Release.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Fact]
    public async Task BrowserEofDisposesItsSocketWhileACommandAcknowledgementIsStillPending()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            try
            {
                await SocketTestServer.Receive(socket, stop);
                await SocketTestServer.Send(socket, State(new string('a', 32)), stop);
                while (!stop.IsCancellationRequested)
                {
                    JsonNode command = await SocketTestServer.Receive(socket, stop);
                    string cmd = command["cmd"]!.GetValue<string>();
                    if (cmd == "getBrowserSession")
                        await SocketTestServer.Send(socket, new { type = "browserSession", session = new string('a', 32) }, stop);
                    if (cmd == "setPreserveBrowserOutputs") { pending.TrySetResult(); continue; }
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                }
            }
            finally { closed.TrySetResult(); }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        Task running = host.RunAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonObject? message;
        do { message = await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token); }
        while (message?["type"]?.GetValue<string>() != "state");
        await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "enable", ["value"] = true });
        await pending.Task.WaitAsync(timeout.Token);
        await input.Writer.CompleteAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await output.Reader.CompleteAsync();
    }

    [Theory]
    [InlineData("older")]
    [InlineData("waiting")]
    [InlineData("partial")]
    public async Task UnsupportedStatesDoNotQuerySessionsAndLaterSupportedStateStartsNormally(string kind)
    {
        var connected = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ConcurrentQueue<string>();
        var sends = new SemaphoreSlim(1, 1);
        async Task Send(WebSocket socket, object payload, CancellationToken stop)
        {
            await sends.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, payload, stop); }
            finally { sends.Release(); }
        }
        int supported = 0;
        string session = new('a', 32);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            connected.TrySetResult(socket);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                commands.Enqueue(cmd);
                if (cmd == "getBrowserSession" && Volatile.Read(ref supported) == 1)
                    await Send(socket, new { type = "browserSession", session }, stop);
                await Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                    error = Volatile.Read(ref supported) == 0 ? "unknown command" : null }, stop);
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = host.RunAsync(timeout.Token);
        try
        {
            WebSocket socket = await connected.Task.WaitAsync(timeout.Token);
            var state = State(session);
            if (kind == "waiting") state["mixer"] = null;
            else
            {
                state["mixer"]!.AsObject().Remove("browserTabs");
                if (kind == "older") state["mixer"]!.AsObject().Remove("preserveBrowserOutputs");
            }
            await Send(socket, state, timeout.Token);
            JsonObject empty = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("state", empty["type"]!.GetValue<string>());
            Assert.False(empty["preserveBrowserOutputs"]!.GetValue<bool>());
            Assert.Empty(empty["channels"]!.AsArray());
            Assert.Empty(empty["tabs"]!.AsArray());
            JsonObject error = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("error", error["type"]!.GetValue<string>());
            Assert.Contains(kind == "waiting" ? "waiting" : "Update OpenXLR", error["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Empty(commands);
            for (int repeat = 0; repeat < 5; repeat++)
            {
                state["sequence"] = repeat;
                await Send(socket, state, timeout.Token);
            }
            Volatile.Write(ref supported, 1);
            await Send(socket, State(session), timeout.Token);
            // Repeated unsupported pushes emit no duplicate status. The
            // next native frame is the newly supported state.
            JsonObject ready = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("state", ready["type"]!.GetValue<string>());
            Assert.Single(ready["channels"]!.AsArray());
            Assert.Equal(["getBrowserSession", "syncBrowserTabs"], commands);
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Fact]
    public async Task AggregateMetadataSizeIsCheckedBeforeSendingToTheDaemon()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        // Each tab fits its individual limits, but UTF-8 and JSON escaping
        // make this complete command larger than the daemon's 64 KiB cap.
        BrowserNativeTab[] tabs = [.. Enumerable.Range(0, 128).Select(id => new BrowserNativeTab(id,
            "https://example.com", new string('\u00fc', 160), false))];
        Assert.Equal(DaemonClient.BrowserTabsTooLarge,
            await client.SyncBrowserTabsAsync(new string('a', 32), tabs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedSessionsBackOffWithoutRepeatingErrorsAndReconnectRetriesImmediately(bool reconnect)
    {
        long now = 10_000;
        var clockReads = Channel.CreateUnbounded<long>();
        long Clock()
        {
            long value = Interlocked.Read(ref now);
            clockReads.Writer.TryWrite(value);
            return value;
        }
        var socketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentQueue<long>();
        var sends = new SemaphoreSlim(1, 1);
        async Task Send(WebSocket socket, object payload, CancellationToken stop)
        {
            await sends.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, payload, stop); }
            finally { sends.Release(); }
        }
        int accepting = 0, connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            string session = new(connection == 1 ? 'a' : 'b', 32);
            await SocketTestServer.Receive(socket, stop);
            socketReady.TrySetResult(socket);
            await Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                bool allowed = Volatile.Read(ref accepting) == 1;
                if (cmd == "getBrowserSession")
                {
                    attempts.Enqueue(Interlocked.Read(ref now));
                    if (allowed) await Send(socket, new { type = "browserSession", session }, stop);
                }
                await Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                    error = cmd == "getBrowserSession" && !allowed ? "At most eight browser companions can connect." : null }, stop);
                if (cmd == "getBrowserSession" && attempts.Count == 2)
                {
                    secondAttempt.TrySetResult();
                    if (reconnect)
                    {
                        await drop.Task.WaitAsync(stop);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", stop);
                        return;
                    }
                }
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream(), Clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = host.RunAsync(timeout.Token);
        try
        {
            WebSocket socket = await socketReady.Task.WaitAsync(timeout.Token);
            JsonObject empty = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("state", empty["type"]!.GetValue<string>());
            Assert.Empty(empty["channels"]!.AsArray());
            JsonObject refusal = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("error", refusal["type"]!.GetValue<string>());
            Assert.Contains("Retrying automatically", refusal["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal([10_000L], attempts);

            for (int repeat = 0; repeat < 8; repeat++) await ObserveSkippedState(10_000);
            Interlocked.Exchange(ref now, 10_999);
            await ObserveSkippedState(10_999);
            Assert.Single(attempts);

            while (clockReads.Reader.TryRead(out _)) { }
            Interlocked.Exchange(ref now, 11_000);
            await Send(socket, State(new string('a', 32)), timeout.Token);
            await secondAttempt.Task.WaitAsync(timeout.Token);
            // Observe both the deadline check and the new failure's deadline
            // calculation before changing the test clock again.
            Assert.Equal(11_000, await clockReads.Reader.ReadAsync(timeout.Token));
            Assert.Equal(11_000, await clockReads.Reader.ReadAsync(timeout.Token));
            for (int repeat = 0; repeat < 4; repeat++) await ObserveSkippedState(11_000);
            Assert.Equal([10_000L, 11_000L], attempts);
            Volatile.Write(ref accepting, 1);
            if (reconnect) drop.TrySetResult();
            else
            {
                Interlocked.Exchange(ref now, 13_000);
                await Send(socket, State(new string('a', 32)), timeout.Token);
            }

            int connectionErrors = 0;
            JsonObject ready;
            while (true)
            {
                ready = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
                if (ready["type"]!.GetValue<string>() == "state") break;
                Assert.True(reconnect);
                Assert.Contains("connection was lost", ready["error"]!.GetValue<string>(), StringComparison.Ordinal);
                connectionErrors++;
            }
            Assert.Equal(reconnect ? 1 : 0, connectionErrors);
            Assert.Single(ready["channels"]!.AsArray());
            Assert.Equal([10_000L, 11_000L, reconnect ? 11_000L : 13_000L], attempts);
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));

            async Task ObserveSkippedState(long expected)
            {
                while (clockReads.Reader.TryRead(out _)) { }
                await Send(socket, State(new string('a', 32)), timeout.Token);
                Assert.Equal(expected, await clockReads.Reader.ReadAsync(timeout.Token));
            }
        }
        finally
        {
            await timeout.CancelAsync();
            drop.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(5, 16000)]
    [InlineData(6, 30000)]
    [InlineData(int.MaxValue, 30000)]
    public void SessionRetryDelayStaysBounded(int failures, int milliseconds)
        => Assert.Equal(milliseconds, BrowserNativeHost.SessionRetryDelayMilliseconds(failures));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/path")]
    [InlineData("https://user:password@example.com")]
    [InlineData("file:///private/document")]
    public async Task AssignmentsRequireAValidExpectedSite(string? site)
    {
        var input = new Pipe();
        using var output = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = new BrowserNativeHost(new DaemonClient("ws://127.0.0.1:1/ws"), input.Reader.AsStream(), output);
        Task running = host.RunAsync(timeout.Token);
        var assignment = new JsonObject { ["type"] = "assign", ["tabId"] = 7, ["channel"] = "music", ["remember"] = true };
        if (site is not null) assignment["site"] = site;
        await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), assignment, timeout.Token);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => running.WaitAsync(timeout.Token));
        await input.Writer.CompleteAsync();
    }

    [Fact]
    public async Task QueuedNativeAssignmentPreservesItsExpectedSiteAfterANewerStateArrives()
    {
        const string oldSite = "https://old.example.com", newSite = "https://new.example.com";
        string session = new('a', 32);
        var socketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var assigned = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = new SemaphoreSlim(1, 1);
        async Task Send(WebSocket socket, object payload, CancellationToken stop)
        {
            await sends.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, payload, stop); }
            finally { sends.Release(); }
        }
        JsonObject WithTab(string site)
        {
            JsonObject state = State(session);
            var tab = Tab(site); tab["session"] = session; tab["channelId"] = null; tab["remember"] = false;
            state["mixer"]!["browserTabs"]!.AsArray().Add(tab);
            return state;
        }
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            socketReady.TrySetResult(socket);
            await Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession") await Send(socket, new { type = "browserSession", session }, stop);
                if (cmd == "syncBrowserTabs") await Send(socket, WithTab(oldSite), stop);
                if (cmd == "setPreserveBrowserOutputs")
                {
                    holding.TrySetResult();
                    await release.Task.WaitAsync(stop);
                }
                if (cmd == "assignBrowserTab") assigned.TrySetResult(command);
                await Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                    error = cmd == "assignBrowserTab" ? "assignBrowserTab: the tab changed website; select it again" : null }, stop);
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var host = new BrowserNativeHost(new DaemonClient(server.Url), input.Reader.AsStream(), output.Writer.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = host.RunAsync(timeout.Token);
        try
        {
            WebSocket socket = await socketReady.Task.WaitAsync(timeout.Token);
            Assert.Equal(oldSite, (await ReadStateWithTab(output.Reader.AsStream(), timeout.Token))["tabs"]![0]!["site"]!.GetValue<string>());
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "enable", ["value"] = true }, timeout.Token);
            await holding.Task.WaitAsync(timeout.Token);
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "assign", ["tabId"] = 7,
                ["site"] = oldSite, ["channel"] = "music", ["remember"] = true }, timeout.Token);
            await Send(socket, WithTab(newSite), timeout.Token);
            Assert.Equal(newSite, (await ReadStateWithTab(output.Reader.AsStream(), timeout.Token))["tabs"]![0]!["site"]!.GetValue<string>());
            release.TrySetResult();
            JsonNode command = await assigned.Task.WaitAsync(timeout.Token);
            Assert.Equal(session, command["session"]!.GetValue<string>());
            Assert.Equal(7, command["tabId"]!.GetValue<int>());
            Assert.Equal(oldSite, command["site"]!.GetValue<string>());
            Assert.Equal("music", command["channel"]!.GetValue<string>());
            Assert.True(command["remember"]!.GetValue<bool>());
            JsonObject failure = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("error", failure["type"]!.GetValue<string>());
            Assert.Equal("The tab changed website. Select it again in the browser extension.", failure["error"]!.GetValue<string>());
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            release.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Fact]
    public async Task ControlsAcceptedBeforeDisconnectCannotRunOnTheReplacementConnection()
    {
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resynced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = new ConcurrentQueue<JsonNode>();
        int connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            string session = new(connection == 1 ? 'a' : 'b', 32);
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession")
                {
                    await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                    if (connection > 1) reconnected.TrySetResult();
                }
                else if (cmd == "syncBrowserTabs")
                {
                    JsonObject state = State(session);
                    if (command["tabs"]!.AsArray().Count > 0)
                    {
                        var tab = command["tabs"]![0]!.DeepClone().AsObject(); tab["session"] = session; tab["channelId"] = null;
                        state["mixer"]!["browserTabs"]!.AsArray().Add(tab);
                    }
                    await SocketTestServer.Send(socket, state, stop);
                    if (connection > 1 && command["tabs"]!.AsArray().Count == 1
                        && command["tabs"]![0]?["title"]?.GetValue<string>() == "Updated while queued") resynced.TrySetResult();
                }
                else if (connection > 1)
                {
                    controls.Enqueue(command.DeepClone());
                    if (cmd == "assignBrowserTab" && command["channel"] is null) marker.TrySetResult();
                }
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                if (connection == 1 && cmd == "syncBrowserTabs" && command["tabs"]!.AsArray().Count > 0)
                {
                    await drop.Task.WaitAsync(stop);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", stop);
                    return;
                }
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var observed = new ObservedInput(input.Reader.AsStream());
        var host = new BrowserNativeHost(new DaemonClient(server.Url), observed, output.Writer.AsStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Task running = host.RunAsync(timeout.Token);
        var gate = (SemaphoreSlim)typeof(BrowserNativeHost).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        bool held = false;
        try
        {
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(Tab()) }, timeout.Token);
            await ReadStateWithTab(output.Reader.AsStream(), timeout.Token);
            await gate.WaitAsync(timeout.Token); held = true;
            JsonObject[] queued = [new() { ["type"] = "enable", ["value"] = false },
                new() { ["type"] = "assign", ["tabId"] = 7, ["site"] = "https://example.com", ["channel"] = "music", ["remember"] = true },
                new() { ["type"] = "tabs", ["tabs"] = new JsonArray(Tab(title: "Updated while queued")) }];
            Task accepted = observed.After(queued.Sum(message => 4L + JsonSerializer.SerializeToUtf8Bytes(message).Length));
            foreach (JsonObject message in queued) await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), message, timeout.Token);
            await accepted.WaitAsync(timeout.Token);
            drop.TrySetResult();
            await reconnected.Task.WaitAsync(timeout.Token);
            object stateGate = typeof(BrowserNativeHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            FieldInfo sessionField = typeof(BrowserNativeHost).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (true)
            {
                lock (stateGate) if ((string?)sessionField.GetValue(host) == new string('b', 32)) break;
                await Task.Delay(5, timeout.Token);
            }
            gate.Release(); held = false;
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "assign", ["tabId"] = 7,
                ["site"] = "https://example.com", ["channel"] = null, ["remember"] = false }, timeout.Token);
            await marker.Task.WaitAsync(timeout.Token);
            await resynced.Task.WaitAsync(timeout.Token);
            JsonNode fresh = Assert.Single(controls);
            Assert.Equal("assignBrowserTab", fresh["cmd"]!.GetValue<string>());
            Assert.Equal(new string('b', 32), fresh["session"]!.GetValue<string>());
            Assert.Null(fresh["channel"]);
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (held) gate.Release();
            await timeout.CancelAsync();
            drop.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorsQueuedBeforeDisconnectCannotReplaceTheNewConnectionStatus(bool syncing)
    {
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connections = 0;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            if (Interlocked.Increment(ref connections) > 1) { await Task.Delay(Timeout.Infinite, stop); return; }
            string session = new('a', 32);
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, State(session), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "getBrowserSession") await SocketTestServer.Send(socket, new { type = "browserSession", session }, stop);
                bool failed = cmd == "setPreserveBrowserOutputs" || cmd == "syncBrowserTabs" && command["tabs"]!.AsArray().Count > 0;
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                    error = failed ? "rejected on the previous connection" : null }, stop);
                if (failed)
                {
                    await drop.Task.WaitAsync(stop);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "restart", stop);
                    return;
                }
            }
        });
        var input = new Pipe(); var output = new Pipe();
        var client = new DaemonClient(server.Url);
        var blocked = new GateOutput(output.Writer.AsStream());
        var host = new BrowserNativeHost(client, input.Reader.AsStream(), blocked);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = host.RunAsync(timeout.Token);
        client.ConnectionChanged += up => { if (!up) disconnected.TrySetResult(); };
        try
        {
            await blocked.Entered.Task.WaitAsync(timeout.Token);
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), syncing
                ? new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(Tab()) }
                : new JsonObject { ["type"] = "enable", ["value"] = true }, timeout.Token);
            await BrowserNativeHost.WriteFrameAsync(input.Writer.AsStream(), new JsonObject { ["type"] = "tabs", ["tabs"] = new JsonArray(Tab()) }, timeout.Token);
            object stateGate = typeof(BrowserNativeHost).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            FieldInfo versionField = typeof(BrowserNativeHost).GetField("_tabsVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (true)
            {
                lock (stateGate) if ((long)versionField.GetValue(host)! >= (syncing ? 2 : 1)) break;
                await Task.Delay(5, timeout.Token);
            }
            drop.TrySetResult();
            await disconnected.Task.WaitAsync(timeout.Token);
            blocked.Release.TrySetResult();
            Assert.Equal("state", (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!["type"]!.GetValue<string>());
            JsonObject reset = (await BrowserNativeHost.ReadFrameAsync(output.Reader.AsStream(), timeout.Token))!;
            Assert.Equal("state", reset["type"]!.GetValue<string>());
            Assert.Empty(reset["channels"]!.AsArray());
            Assert.False(reset["preserveBrowserOutputs"]!.GetValue<bool>());
            await input.Writer.CompleteAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await timeout.CancelAsync();
            drop.TrySetResult(); blocked.Release.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
            await output.Reader.CompleteAsync();
        }
    }

    private sealed class ObservedInput(Stream source) : Stream
    {
        private long _read, _target = long.MaxValue;
        private readonly TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task After(long bytes)
        {
            Volatile.Write(ref _target, Volatile.Read(ref _read) + bytes);
            return _accepted.Task;
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _read) >= Volatile.Read(ref _target)) _accepted.TrySetResult();
            int count = await source.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref _read, count);
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GateOutput(Stream target) : Stream
    {
        private int _blocked;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await target.WriteAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static JsonObject Tab(string site = "https://example.com", string title = "Example")
        => new() { ["id"] = 7, ["site"] = site, ["title"] = title, ["capturing"] = true };

    private static JsonObject State(string session) => new()
    {
        ["type"] = "state", ["sessionToken"] = "HIDDEN_TOKEN",
        ["mixer"] = new JsonObject { ["preserveBrowserOutputs"] = true,
            ["channels"] = new JsonArray(new JsonObject { ["id"] = "music", ["name"] = "Music", ["hardware"] = false }),
            ["browserTabs"] = new JsonArray(), ["privateSession"] = session },
    };

    private static MemoryStream Frame(string json, int? declaredSize = null)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredSize ?? data.Length);
        stream.Write(header); stream.Write(data); stream.Position = 0;
        return stream;
    }

    private static async Task<JsonObject> ReadStateWithTab(Stream stream, CancellationToken stop)
    {
        while (await BrowserNativeHost.ReadFrameAsync(stream, stop) is { } state)
            if (state["type"]?.GetValue<string>() == "state" && state["tabs"]!.AsArray().Count == 1) return state;
        throw new EndOfStreamException();
    }
}
