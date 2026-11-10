using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

public sealed class AppRoutingViewModelTests
{
    internal static void Report(MainViewModel model, bool? enabled)
    {
        BrowserTabViewModelTests.Report(model, false, new JsonArray());
        Invoke(model, "ApplyAppRouting", enabled is bool value ? new JsonObject { ["routeAppsByDefault"] = value } : new JsonObject());
    }

    [Fact]
    public async Task HeldBrowserKeepsItsPlaybackVolumeControl()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Invoke(model, "ApplyStreams", JsonNode.Parse("""
            {"appVolumeSupported":true,"streams":[{"identity":"chrome","label":"Chrome",
             "channelId":"browser","active":true,"browserHeld":true,"volume":0.5}]}
            """));
        var app = Assert.Single(model.Apps);
        Assert.NotNull(app.BrowserHeldNote);
        Assert.True(app.PlaybackVolume.Enabled);
        Assert.Equal(.5, app.PlaybackVolume.Level);
    }

    [Fact]
    public async Task AnApplicationWithoutASavedLevelShowsTheOneItPlaysAt()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Invoke(model, "ApplyStreams", JsonNode.Parse("""
            {"appVolumeSupported":true,"streams":[
             {"identity":"vlc","label":"VLC","channelId":"music","active":true,"liveVolume":0.6},
             {"identity":"mpv","label":"mpv","channelId":"music","active":true,"liveVolume":1},
             {"identity":"discord","label":"Discord","channelId":"voicechat","active":true,"volume":0.35,"liveVolume":0.35},
             {"identity":"signal","label":"Signal","channelId":"voicechat","active":false}]}
            """));
        PlaybackVolumeViewModel Volume(string identity) => model.Apps.Single(app => app.Identity == identity).PlaybackVolume;
        // The slider starts where the stream is, and Reset has nothing to clear.
        Assert.Equal((.6, "60%", false), (Volume("vlc").Level, Volume("vlc").Label, Volume("vlc").HasLevel));
        Assert.Equal((1, Localizer.Text("PlaybackVolumeShort"), false), (Volume("mpv").Level, Volume("mpv").Label, Volume("mpv").HasLevel));
        Assert.Equal((.35, "35%", true), (Volume("discord").Level, Volume("discord").Label, Volume("discord").HasLevel));
        Assert.Equal((1, Localizer.Text("PlaybackVolumeShort"), false), (Volume("signal").Level, Volume("signal").Label, Volume("signal").HasLevel));

        // The desktop moves the stream: the control follows without an edit of its own.
        Invoke(model, "ApplyStreams", JsonNode.Parse("""
            {"appVolumeSupported":true,"streams":[
             {"identity":"vlc","label":"VLC","channelId":"music","active":true,"liveVolume":0.45}]}
            """));
        Assert.Equal((.45, "45%"), (Volume("vlc").Level, Volume("vlc").Label));
    }

    [Fact]
    public async Task OldDaemonsAndDisconnectedClientsCannotSendThePreference()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Assert.False(model.AppRoutingSupported);
        await model.SetRouteAppsByDefaultAsync(false);
        Report(model, false);
        Assert.False(model.RouteAppsByDefault);
        Assert.False(model.CanSetAppRouting);
        Report(model, null);
        Assert.False(model.AppRoutingSupported);
        Assert.True(model.RouteAppsByDefault);
        Assert.False(model.CanSetAppRouting);
    }

    [Fact]
    public async Task AcknowledgementsNeverOverwriteNewerStateOrAReconnectedSession()
    {
        var connected = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = Channel.CreateUnbounded<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            connected.TrySetResult(socket);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() == "setRouteAppsByDefault") commands.Writer.TryWrite(command);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var model = new MainViewModel(client);
        var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += online => { if (online) up.TrySetResult(); };
        client.Start();
        await up.Task.WaitAsync(TimeSpan.FromSeconds(3));
        WebSocket socket = await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.DaemonConnected))!.SetValue(model, true);
        Report(model, true);
        Task first = model.SetRouteAppsByDefaultAsync(false);
        JsonNode command = await Next();
        Assert.False(command["value"]!.GetValue<bool>());
        Assert.True(model.RouteAppsByDefault);
        Assert.False(model.CanSetAppRouting);
        await model.SetRouteAppsByDefaultAsync(false);
        Assert.False(commands.Reader.TryRead(out _));
        await Reply(command, "disk full");
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.CanSetAppRouting);
        Assert.True(model.RouteAppsByDefault);
        Assert.Equal("disk full", model.Status);

        Task second = model.SetRouteAppsByDefaultAsync(false);
        command = await Next();
        Report(model, false);
        Report(model, true);
        await Reply(command);
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.RouteAppsByDefault);

        Task stale = model.SetRouteAppsByDefaultAsync(false);
        command = await Next();
        Invoke(model, "ResetAppRouting");
        Report(model, false);
        Task current = model.SetRouteAppsByDefaultAsync(true);
        JsonNode currentCommand = await Next();
        string status = model.Status;
        await Reply(command, "old failure");
        await stale.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(status, model.Status);
        Assert.False(model.RouteAppsByDefault);
        Assert.False(model.CanSetAppRouting);
        Report(model, true);
        await Reply(currentCommand);
        await current.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.RouteAppsByDefault);
        Assert.True(model.CanSetAppRouting);

        Task<JsonNode> Next() => commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Task Reply(JsonNode cmd, string? error = null) => SocketTestServer.Send(socket,
            new { type = "commandResult", requestId = cmd["requestId"]!.GetValue<string>(), error }, CancellationToken.None);
    }

    private static void Invoke(MainViewModel model, string name, params object?[] args)
        => typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);
}
