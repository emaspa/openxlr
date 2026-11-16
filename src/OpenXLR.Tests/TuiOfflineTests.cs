using System.Net.WebSockets;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer without its daemon: the header says the link is down
/// instead of naming a live device, a key that would send a change says it
/// went nowhere, and the link keeps reading and reconnecting whatever a
/// message or a listener throws.
/// </summary>
[Collection("xdg-config")]
public sealed class TuiOfflineTests : IDisposable
{
    private const string State = """
        { "type": "state", "daemonVersion": "9.8.7", "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": {}, "state": {}, "devices": [], "profiles": [],
          "mixer": { "channels": [], "mixes": [{ "id": "monitor", "name": "Monitor A", "volume": 0.5, "muted": false, "kind": "monitor" }],
                     "monitorOutputs": [], "inserts": {} } }
        """;

    private readonly string _runtime = Path.Combine(Path.GetTempPath(), "openxlr-tui-offline-" + Guid.NewGuid());
    private readonly string? _oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

    public TuiOfflineTests()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtime);
        Directory.CreateDirectory(_runtime);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _oldRuntime);
        Directory.Delete(_runtime, recursive: true);
    }

    [Fact]
    public void TheHeaderSaysTheDaemonIsGoneAndComesBackOnReconnect()
    {
        var link = new DaemonLink();
        link.Receive(State);
        var app = new App(link, Theme.Material, time: new ManualTime());

        Screen screen = Draw(app);
        Assert.Contains("Elgato Wave XLR Pro", Row(screen, 0), StringComparison.Ordinal);
        Assert.Contains("v9.8.7", Row(screen, 0), StringComparison.Ordinal);
        Assert.Equal(app.Theme.LedOn, screen.At(10, 0).Fore);

        link.EndSession();
        screen = Draw(app);
        string header = Row(screen, 0);
        Assert.False(link.Connected);
        Assert.Contains("offline, showing the last known state", header, StringComparison.Ordinal);
        Assert.Contains(link.Status, header, StringComparison.Ordinal);
        Assert.DoesNotContain("Wave XLR Pro", header, StringComparison.Ordinal);
        Assert.DoesNotContain("v9.8.7", header, StringComparison.Ordinal);
        Assert.Equal("disconnected", link.Status);
        Assert.Equal(app.Theme.LedAlert, screen.At(10, 0).Fore);
        Assert.Contains("No daemon", Frame(screen), StringComparison.Ordinal);

        link.Receive(State);
        screen = Draw(app);
        Assert.Contains("Elgato Wave XLR Pro", Row(screen, 0), StringComparison.Ordinal);
        Assert.Contains("v9.8.7", Row(screen, 0), StringComparison.Ordinal);
        Assert.Equal(app.Theme.LedOn, screen.At(10, 0).Fore);
        Assert.DoesNotContain("No daemon", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void AFaderKeyWithoutTheDaemonSaysItWasNotSent()
    {
        var link = new DaemonLink();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(State);
        var time = new ManualTime();
        var app = new App(link, Theme.Material, time: time);
        app.Draw(new Screen(120, 32));

        link.EndSession();
        // Moving the selection sends nothing, so it says nothing.
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Home));
        Assert.DoesNotContain(DaemonLink.NotConnected, Bottom(app), StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Char, '+'));
        Assert.Empty(sent);
        Assert.Contains(DaemonLink.NotConnected, Bottom(app), StringComparison.Ordinal);

        // The line goes after four seconds, as any notice does.
        time.Advance(TimeSpan.FromSeconds(5));
        link.Receive(State);
        app.Handle(new KeyPress(Key.Char, '+'));
        Assert.Equal("setMixVolume", JsonNode.Parse(Assert.Single(sent))!["cmd"]!.GetValue<string>());
        Assert.DoesNotContain(DaemonLink.NotConnected, Bottom(app), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestWithoutTheDaemonAnswersAtOnce()
    {
        await using var link = new DaemonLink();
        string? answer = await link.Request("undo", _ => { }).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DaemonLink.NotConnected, answer);
    }

    [Theory]
    [InlineData("""{"type":"error","message":"Volume refused"}""")]
    [InlineData("""{"type":"commandResult","ok":false,"error":"Volume refused"}""")]
    public void ARefusedChangeShowsTheDaemonsErrorOnTheBottomLine(string message)
    {
        var link = new DaemonLink();
        link.Receive(State);
        var app = new App(link, Theme.Material, time: new ManualTime());
        link.Receive(message);

        Screen screen = Draw(app);
        Assert.Contains("Volume refused", Row(screen, screen.Height - 1), StringComparison.Ordinal);
        Assert.Equal(app.Theme.DangerBack, screen.At(0, screen.Height - 1).Back);

        // The next key withdraws it, as with any message on that line.
        app.Handle(new KeyPress(Key.Down));
        Assert.DoesNotContain("Volume refused", Bottom(app), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"state","mixer":[]}""")]
    [InlineData("""{"type":"state","mixer":{"channels":{},"mixes":[]}}""")]
    [InlineData("""{"type":"state","connected":"maybe"}""")]
    [InlineData("""{"type":"state","mixer":{"inserts":{"mic":5}}}""")]
    [InlineData("""{"type":"state","mixer":{"mixes":[{"id":"monitor","volume":null}]}}""")]
    public void AStateOfAnUnexpectedShapeIsSkippedAndTheLastOneKept(string message)
    {
        var link = new DaemonLink();
        link.Receive(State);
        Snapshot before = link.State!;

        link.Receive(message);
        Assert.StartsWith("Skipped a state message", link.LastError, StringComparison.Ordinal);
        Assert.Same(before, link.State);
        Assert.True(link.Connected);

        link.Receive("""{"type":"error","message":"Still reading"}""");
        Assert.Equal("Still reading", link.LastError);
    }

    [Fact]
    public async Task AMessageThatCannotBeReadDoesNotEndTheSession()
    {
        int connections = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            Interlocked.Increment(ref connections);
            await SocketTestServer.Receive(socket, stop); // state request
            await SocketTestServer.Send(socket, JsonNode.Parse(State)!, stop);
            await SocketTestServer.Send(socket, new { type = "state", connected = "maybe" }, stop);
            await SocketTestServer.Send(socket, new { type = "error", message = "Still here" }, stop);
            await SocketTestServer.Receive(socket, stop);
        });
        await using DaemonLink link = new(server.Url);
        bool skipped = false;
        link.Changed += () =>
        {
            if (link.LastError?.StartsWith("Skipped", StringComparison.Ordinal) == true) skipped = true;
            if (link.LastError == "Still here") done.TrySetResult();
        };
        link.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(skipped);
        Assert.True(link.Connected);
        Assert.Equal("Wave XLR Pro", link.State!.Device!.Model);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task AnUnexpectedFailureEndsTheSessionAndTheLinkReconnects()
    {
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, JsonNode.Parse(State)!, stop);
            await SocketTestServer.Receive(socket, stop);
        });
        await using DaemonLink link = new(server.Url);
        int thrown = 0;
        List<(bool Connected, string Status)> seen = [];
        link.Changed += () =>
        {
            lock (seen) seen.Add((link.Connected, link.Status));
            // A listener failing in a way the link does not expect, once.
            if (link.Status == "connecting" && Interlocked.Increment(ref thrown) == 1)
                throw new InvalidOperationException("unexpected");
            if (link.Connected && link.State is not null) restored.TrySetResult();
        };
        link.Start();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(6));
        lock (seen) Assert.Contains((false, "connection failed"), seen);
        Assert.True(link.Connected);
    }

    [Fact]
    public async Task ADaemonThatStopsLeavesTheLinkDisconnectedUntilItIsBack()
    {
        int connections = 0;
        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            int connection = Interlocked.Increment(ref connections);
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, JsonNode.Parse(State)!, stop);
            if (connection == 1)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "daemon stopping", stop);
                return;
            }
            await SocketTestServer.Receive(socket, stop);
        });
        await using DaemonLink link = new(server.Url);
        bool sawOffline = false;
        link.Changed += () =>
        {
            if (!link.Connected && link.Status == "daemon stopping" && link.State is not null) sawOffline = true;
            if (link.Connected && link.State is not null && Volatile.Read(ref connections) == 2) back.TrySetResult();
        };
        link.Start();
        await back.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(sawOffline);
    }

    [Fact]
    public async Task ThePumpLeavesTheLinkDisconnectedWhenItEnds()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop);
            await SocketTestServer.Send(socket, JsonNode.Parse(State)!, stop);
            await SocketTestServer.Receive(socket, stop);
        });
        DaemonLink link = new(server.Url);
        link.Changed += () => { if (link.Connected && link.State is not null) ready.TrySetResult(); };
        link.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(link.Connected);

        await link.DisposeAsync();
        Assert.False(link.Connected);
    }

    private static Screen Draw(App app)
    {
        Screen screen = new(120, 32);
        app.Draw(screen);
        return screen;
    }

    private static string Row(Screen screen, int y) =>
        new(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray());

    private static string Frame(Screen screen) =>
        string.Join('\n', Enumerable.Range(0, screen.Height).Select(y => Row(screen, y)));

    private static string Bottom(App app)
    {
        Screen screen = Draw(app);
        return Row(screen, screen.Height - 1);
    }
}
