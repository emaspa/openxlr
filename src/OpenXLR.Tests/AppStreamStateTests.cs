using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppStreamStateTests
{
    [Theory]
    [InlineData("game")]
    [InlineData("music")]
    [InlineData("ignore")]
    public async Task PublishedAppRoutesDoNotEchoAssignmentsButUserChangesStillSend(string channel)
    {
        var assignments = new ConcurrentQueue<string>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "assignApp") assignments.Enqueue(command["channel"]!.GetValue<string>());
                if (cmd != "getDiagnostics") continue;
                await SocketTestServer.Send(socket, new { type = "diagnostics" }, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var model = new MainViewModel(client);
        Apply(model, State(App("player", channel)));
        Apply(model, State(App("player", "browser")));
        // The shared send queue makes this reply a barrier after any echoed commands.
        Assert.NotNull(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(assignments);
        Assert.Equal("browser", Assert.Single(model.Apps).ChannelId);

        model.Apps[0].ChannelId = "voicechat";
        Assert.NotNull(await client.RequestDiagnosticsAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("voicechat", Assert.Single(assignments));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"streams\":null}")]
    public async Task MissingStreamStateClearsBothViewsAndNotifiesThePlaceholder(string? json)
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, State(App("player", "music")));
        Assert.True(model.HasApps);
        Assert.Single(model.ActiveApps);
        bool notified = false;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.HasApps)) notified = true; };

        Apply(model, json is null ? null : JsonNode.Parse(json));

        Assert.Empty(model.Apps);
        Assert.Empty(model.ActiveApps);
        Assert.False(model.HasApps);
        Assert.True(notified);
    }

    [Fact]
    public async Task UpdatesPreserveCaseInsensitiveIdentityAndSynchronizeRunningMembership()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        model.Channels.Add(new(client, "music", "Music", []));
        Apply(model, State(App("PLAYER", "music"), App("gone", "music"), App("asleep", "music", false, false)));
        var player = model.Apps[0];
        var asleep = model.Apps[2];
        model.Channels[0].Name = "Songs";
        var next = State(App("player", "music", false, true, "Renamed player"),
            App("asleep", "music", true, true), App("new", "music", false, false));
        Apply(model, next);
        Assert.Equal(["PLAYER", "asleep", "new"], model.Apps.Select(a => a.Identity));
        Assert.Equal([player, asleep], model.ActiveApps);
        Assert.Same(player, model.Apps[0]);
        Assert.Equal("Renamed player", player.Label);
        Assert.Equal("Songs", player.SelectedChannel!.Name);
        Assert.False(player.Active);
        Assert.True(player.Running);

        int changes = 0;
        model.Apps.CollectionChanged += (_, _) => changes++;
        model.ActiveApps.CollectionChanged += (_, _) => changes++;
        player.Channels.CollectionChanged += (_, _) => changes++;
        Apply(model, next);
        Assert.Equal(0, changes);

        Apply(model, State(App("player", "music", false, false)));
        Assert.Empty(model.ActiveApps);
        Assert.Same(player, Assert.Single(model.Apps));
        Apply(model, State());
        Assert.Empty(model.Apps);
        Assert.False(model.HasApps);
    }

    [Fact]
    public async Task RepeatedIdentityInOneSnapshotKeepsOneControlAndUsesTheLastState()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, State(App("Player", "game"), App("PLAYER", "music", false, false, "Player updated")));
        var app = Assert.Single(model.Apps);
        Assert.Equal("music", app.ChannelId);
        Assert.Equal("Player updated", app.Label);
        Assert.Empty(model.ActiveApps);
    }

    [Fact]
    public async Task AHeldBrowserSaysWhereItPlaysAndKeepsItsAssignment()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        model.Channels.Add(new(client, "music", "Music", []));
        model.Channels.Add(new(client, "system", "System", []));
        JsonObject Chrome(string? output, bool active = true)
        {
            JsonObject app = App("chrome", "music", active);
            app["browserHeld"] = true;
            if (output is not null) app["outputChannelId"] = output;
            return app;
        }

        Apply(model, State(Chrome("system")));
        AppStreamViewModel chrome = Assert.Single(model.Apps);
        Assert.Equal("music", chrome.ChannelId);
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Format("BrowserOutputHeldOnChannel", "System"), chrome.BrowserHeldNote);
        Assert.Equal("ch:system", Assert.Single(FlowGraph.From(model).Routes, r => r.From == "app:chrome").To);
        Apply(model, State(Chrome(null)));
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("BrowserOutputHeldElsewhere"), chrome.BrowserHeldNote);
        Assert.DoesNotContain(FlowGraph.From(model).Routes, r => r.From == "app:chrome");
        Apply(model, State(Chrome(null, active: false)));
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("BrowserOutputHeld"), chrome.BrowserHeldNote);
        Apply(model, State(App("chrome", "music")));
        Assert.Null(chrome.BrowserHeldNote);
        Assert.Equal("music", chrome.ChannelId);
        Assert.Equal("ch:music", Assert.Single(FlowGraph.From(model).Routes, r => r.From == "app:chrome").To);
    }

    private static JsonObject App(string identity, string channel, bool active = true, bool running = true, string? label = null)
        => new() { ["identity"] = identity, ["label"] = label ?? identity, ["channelId"] = channel,
            ["active"] = active, ["running"] = running };

    private static JsonObject State(params JsonNode[] apps) => new() { ["streams"] = new JsonArray(apps) };

    private static void Apply(MainViewModel model, JsonNode? state)
        => typeof(MainViewModel).GetMethod("ApplyStreams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [state]);
}
