using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserTabViewModelTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";
    private static readonly ChannelChoice[] Choices = [new("", "Browser default"), new("music", "Music"), new("chat", "Voice Chat")];

    private sealed class Recorder
    {
        internal readonly List<(string Session, int Id, string Site, string? Channel, bool Remember, TaskCompletionSource<string?> Reply)> Sent = [];
        internal readonly List<(string Session, int Id, string Site, TaskCompletionSource<string?> Reply)> Activations = [];
        internal readonly List<string> Errors = [];
        internal BrowserTabViewModel Model { get; }
        internal Recorder(TimeSpan? activationTimeout = null)
        {
            Model = new(Session, 7, (session, id, site, channel, remember) =>
            {
                var reply = new TaskCompletionSource<string?>();
                Sent.Add((session, id, site, channel, remember, reply));
                return reply.Task;
            }, Errors.Add, (session, id, site) =>
            {
                var reply = new TaskCompletionSource<string?>();
                Activations.Add((session, id, site, reply));
                return reply.Task;
            }, activationTimeout ?? TimeSpan.FromMinutes(5));
            Apply();
        }

        internal void Apply(string? channel = null, bool remember = false, bool capturing = false, string? error = null,
            string title = "A browser tab", string site = "https://example.com", bool canEdit = true, ChannelChoice[]? choices = null,
            string? activation = null)
            => Model.ApplyFromDaemon(title, site, channel, remember, capturing, error, choices ?? Choices, canEdit, activation);
    }

    [Fact]
    public void ATabWithoutASharedTitleUsesItsOriginAndRemainsSelectable()
    {
        var f = new Recorder();
        f.Apply(title: "");
        Assert.Equal("https://example.com", f.Model.Title);
        f.Model.SelectedChannel = f.Model.Channels[1];
        Assert.Equal("https://example.com", Assert.Single(f.Sent).Site);
        f.Apply("music", title: "Selected tab");
        Assert.Equal("Selected tab", f.Model.Title);
    }

    [Fact]
    public void DaemonStateUpdatesTheSameChoicesWithoutSendingAssignments()
    {
        var f = new Recorder();
        ChannelChoice original = f.Model.Channels[1];
        f.Apply("music", true, true, title: "Music tab", site: "https://music.example.com");
        Assert.Equal("Music tab", f.Model.Title);
        Assert.Equal("https://music.example.com", f.Model.Site);
        Assert.Equal("music", f.Model.SelectedChannel?.Id);
        Assert.True(f.Model.Remember);
        Assert.True(f.Model.Capturing);
        Assert.Equal(Localizer.Text("BrowserTabCapturing"), f.Model.StatusText);
        Assert.Same(original, f.Model.Channels[1]);
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task AssignmentWaitsForAcknowledgementAndCannotActivateCapture()
    {
        var f = new Recorder();
        f.Model.SelectedChannel = f.Model.Channels[1];
        var sent = Assert.Single(f.Sent);
        Assert.Equal((Session, 7, "https://example.com", "music", false), (sent.Session, sent.Id, sent.Site, sent.Channel, sent.Remember));
        Assert.True(f.Model.IsPending);
        Assert.False(f.Model.Capturing);
        Assert.Equal(Localizer.Text("BrowserTabApplying"), f.Model.StatusText);
        f.Apply();
        Assert.Equal("music", f.Model.ChannelId);
        f.Apply("music");
        sent.Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        Assert.False(f.Model.Capturing);
        Assert.Equal(Localizer.Text("BrowserTabActivationRequired"), f.Model.StatusText);
        f.Apply("music", capturing: true);
        Assert.True(f.Model.Capturing);
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task ARefusalRestoresTheLatestAuthoritativeRouteAndReportsTheError()
    {
        var f = new Recorder();
        f.Apply("music", true);
        f.Model.SelectedChannel = f.Model.Channels[2];
        f.Apply("music", true, title: "Updated title");
        f.Sent[0].Reply.SetResult("capture destination unavailable");
        await Until(() => !f.Model.IsPending);
        Assert.Equal("music", f.Model.ChannelId);
        Assert.True(f.Model.Remember);
        Assert.Equal("Updated title", f.Model.Title);
        Assert.Equal("capture destination unavailable", f.Model.StatusText);
        Assert.Equal(["capture destination unavailable"], f.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOlderReplyCannotChangeANewerAssignment(bool refuseOld)
    {
        var f = new Recorder();
        f.Model.SelectedChannel = f.Model.Channels[1];
        f.Model.SelectedChannel = f.Model.Channels[2];
        f.Apply("chat");
        f.Sent[1].Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        f.Sent[0].Reply.SetResult(refuseOld ? "old refusal" : null);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.Empty(f.Errors);
        Assert.Equal(Localizer.Text("BrowserTabActivationRequired"), f.Model.StatusText);
    }

    [Fact]
    public async Task ANewerAuthoritativeRouteBeforeAcknowledgementWinsOverTheCompletedEdit()
    {
        var f = new Recorder();
        f.Model.SelectedChannel = f.Model.Channels[1];
        f.Apply("music");
        f.Apply("chat", true);
        Assert.Equal("music", f.Model.ChannelId);
        f.Sent[0].Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.True(f.Model.Remember);
        f.Apply();
        Assert.Null(f.Model.ChannelId);
        Assert.Empty(f.Errors);
    }

    [Fact]
    public async Task RememberWebsiteUsesTheChosenChannelAndDefaultClearsRemember()
    {
        var f = new Recorder();
        Assert.False(f.Model.CanRemember);
        f.Model.Remember = true;
        Assert.Empty(f.Sent);
        f.Apply("music");
        f.Model.Remember = true;
        Assert.Equal(("music", true), (f.Sent[0].Channel, f.Sent[0].Remember));
        Assert.Equal("https://example.com", f.Sent[0].Site);
        f.Apply("music", true);
        f.Sent[0].Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        f.Model.SelectedChannel = f.Model.Channels[0];
        Assert.Null(f.Sent[1].Channel);
        Assert.False(f.Sent[1].Remember);
        Assert.False(f.Model.CanRemember);
        f.Apply();
        f.Sent[1].Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        Assert.Equal(Localizer.Text("BrowserTabUsesDefault"), f.Model.StatusText);
    }

    [Fact]
    public void RoutingDisabledKeepsTabsVisibleAndIgnoresEdits()
    {
        var f = new Recorder();
        f.Apply("music", true, canEdit: false);
        f.Model.SelectedChannel = f.Model.Channels[2];
        f.Model.Remember = false;
        f.Model.ForgetWebsite();
        Assert.Equal("music", f.Model.ChannelId);
        Assert.True(f.Model.Remember);
        Assert.False(f.Model.CanEdit);
        Assert.False(f.Model.CanRemember);
        Assert.Empty(f.Sent);
        Assert.Equal(Localizer.Text("BrowserTabRoutingDisabled"), f.Model.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgetWebsiteUsesTheExplicitDeleteCommandAndWaitsForItsResult(bool refused)
    {
        var f = new Recorder();
        f.Apply("music", true);
        f.Model.ForgetWebsite();
        var sent = Assert.Single(f.Sent);
        Assert.Null(sent.Channel);
        Assert.True(sent.Remember);
        Assert.Equal("https://example.com", sent.Site);
        Assert.True(f.Model.IsPending);
        Assert.Null(f.Model.ChannelId);
        Assert.False(f.Model.Remember);
        f.Apply(refused ? "music" : null, refused);
        sent.Reply.SetResult(refused ? "website choice could not be removed" : null);
        await Until(() => !f.Model.IsPending);
        Assert.Equal(refused ? "music" : null, f.Model.ChannelId);
        Assert.Equal(refused, f.Model.Remember);
        if (refused) Assert.Equal(["website choice could not be removed"], f.Errors);
        else Assert.Empty(f.Errors);
    }

    [Fact]
    public async Task DisconnectInvalidatesPendingRepliesAndFreshStateEnablesANewEdit()
    {
        var f = new Recorder();
        f.Model.SelectedChannel = f.Model.Channels[1];
        f.Model.SetCanEdit(false);
        Assert.False(f.Model.IsPending);
        Assert.Null(f.Model.ChannelId);
        f.Apply("chat");
        f.Model.Remember = true;
        f.Sent[0].Reply.SetResult("old connection failed");
        Assert.True(f.Model.IsPending);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.True(f.Model.Remember);
        Assert.Empty(f.Errors);
        f.Apply("chat", true);
        f.Sent[1].Reply.SetResult(null);
        await Until(() => !f.Model.IsPending);
        Assert.True(f.Model.Remember);
    }

    [Fact]
    public async Task RemovingAPendingChannelCancelsItsReplyAndRenamingChoicesSendsNothing()
    {
        var f = new Recorder();
        f.Model.SelectedChannel = f.Model.Channels[1];
        f.Apply(choices: [Choices[0], new("chat", "Calls")]);
        Assert.False(f.Model.IsPending);
        Assert.Null(f.Model.ChannelId);
        Assert.Equal("Calls", f.Model.Channels[1].Name);
        f.Sent[0].Reply.SetResult("removed channel");
        await Until(() => !f.Model.IsPending);
        Assert.Empty(f.Errors);
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task NavigationInvalidatesThePreviousWebsitesPendingEdit()
    {
        var f = new Recorder();
        f.Apply("music");
        f.Model.Remember = true;
        f.Apply("chat", site: "https://another.example.com");
        Assert.Equal("https://example.com", Assert.Single(f.Sent).Site);
        Assert.False(f.Model.IsPending);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.False(f.Model.Remember);
        f.Sent[0].Reply.SetResult("old website request failed");
        await Until(() => !f.Model.IsPending);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.Empty(f.Errors);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("remember")]
    [InlineData("forget")]
    public async Task EveryEditCapturesItsOriginBeforeNotificationsCanApplyANavigation(string action)
    {
        var f = new Recorder();
        f.Apply("music");
        bool navigated = false;
        f.Model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(BrowserTabViewModel.IsPending) || !f.Model.IsPending || navigated) return;
            navigated = true;
            f.Apply("chat", site: "https://another.example.com");
        };
        switch (action)
        {
            case "select": f.Model.SelectedChannel = f.Model.Channels[2]; break;
            case "remember": f.Model.Remember = true; break;
            case "forget": f.Model.ForgetWebsite(); break;
        }

        var sent = Assert.Single(f.Sent);
        Assert.True(navigated);
        Assert.Equal("https://example.com", sent.Site);
        Assert.Equal("https://another.example.com", f.Model.Site);
        Assert.False(f.Model.IsPending);
        sent.Reply.SetResult("the tab navigated before this command was applied");
        await Until(() => !f.Model.IsPending);
        Assert.Equal("chat", f.Model.ChannelId);
        Assert.False(f.Model.Remember);
        Assert.Empty(f.Errors);
    }

    [Fact]
    public void ActivateIsOfferedOnlyWhenItCanStartSomething()
    {
        var f = new Recorder();
        Assert.False(f.Model.CanActivate);
        f.Apply("music");
        Assert.True(f.Model.CanActivate);
        f.Apply("music", capturing: true);
        Assert.False(f.Model.CanActivate);
        f.Apply("music", canEdit: false);
        Assert.False(f.Model.CanActivate);
        f.Model.Activate();
        Assert.Empty(f.Activations);
        f.Apply("music", activation: "popup");
        Assert.False(f.Model.CanActivate);
        f.Apply("music");
        f.Model.SelectedChannel = f.Model.Channels[2];
        Assert.True(f.Model.IsPending);
        Assert.False(f.Model.CanActivate);
        // Without a daemon command to call, as in a view built for layout, there is nothing to offer.
        var bare = new BrowserTabViewModel(Session, 7, (_, _, _, _, _) => Task.FromResult<string?>(null), _ => { });
        bare.ApplyFromDaemon("Tab", "https://example.com", "music", false, false, null, Choices, true);
        Assert.False(bare.CanActivate);
    }

    [Fact]
    public async Task ActivateWaitsForTheBrowserAndFollowsTheStagesItReports()
    {
        var f = new Recorder();
        f.Apply("music");
        f.Model.Activate();
        var sent = Assert.Single(f.Activations);
        Assert.Equal((Session, 7, "https://example.com"), (sent.Session, sent.Id, sent.Site));
        Assert.True(f.Model.IsActivating);
        Assert.False(f.Model.CanActivate);
        Assert.Equal(Localizer.Text("BrowserTabActivating"), f.Model.StatusText);
        f.Model.Activate();
        Assert.Single(f.Activations);
        sent.Reply.SetResult(null);
        await Task.Yield();
        // Delivered is not started: the row waits for the browser's own report.
        Assert.True(f.Model.IsActivating);
        f.Apply("music", activation: "starting");
        Assert.Equal(Localizer.Text("BrowserTabActivating"), f.Model.StatusText);
        f.Apply("music", activation: "popup");
        Assert.Equal(Localizer.Text("BrowserTabActivatePopup"), f.Model.StatusText);
        f.Apply("music", activation: "browser");
        Assert.Equal(Localizer.Text("BrowserTabActivateToolbar"), f.Model.StatusText);
        Assert.False(f.Model.Capturing);
        f.Apply("music", capturing: true);
        Assert.False(f.Model.IsActivating);
        Assert.Equal(Localizer.Text("BrowserTabCapturing"), f.Model.StatusText);
        Assert.Empty(f.Errors);
    }

    [Fact]
    public async Task ARefusedRequestEndsWithTheDaemonsError()
    {
        var f = new Recorder();
        f.Apply("music");
        f.Model.Activate();
        f.Activations[0].Reply.SetResult("activateBrowserTab: the browser companion is not connected");
        await Until(() => !f.Model.IsActivating);
        Assert.Equal("activateBrowserTab: the browser companion is not connected", f.Model.StatusText);
        Assert.Equal(["activateBrowserTab: the browser companion is not connected"], f.Errors);
        Assert.True(f.Model.CanActivate);
    }

    [Fact]
    public void ANewBrowserErrorEndsTheRequestAndAnOldOneDoesNot()
    {
        var f = new Recorder();
        f.Apply("music", error: "Permission denied");
        f.Model.Activate();
        f.Apply("music", error: "Permission denied");
        Assert.True(f.Model.IsActivating);
        f.Apply("music", error: "The selected OpenXLR output is unavailable.");
        Assert.False(f.Model.IsActivating);
        Assert.Equal("The selected OpenXLR output is unavailable.", f.Model.StatusText);
        // The browser clears the old error when it takes a request, so the same error coming back is new.
        f.Model.Activate();
        f.Apply("music");
        Assert.True(f.Model.IsActivating);
        f.Apply("music", error: "The selected OpenXLR output is unavailable.");
        Assert.False(f.Model.IsActivating);
    }

    [Fact]
    public async Task ARequestTheBrowserNeverAnswersExpiresAndALateRefusalIsIgnored()
    {
        var f = new Recorder(TimeSpan.FromMilliseconds(50));
        f.Apply("music");
        f.Model.Activate();
        await Until(() => !f.Model.IsActivating);
        Assert.Equal(Localizer.Text("BrowserTabActivationRequired"), f.Model.StatusText);
        Assert.True(f.Model.CanActivate);
        f.Activations[0].Reply.SetResult("late refusal");
        await Task.Yield();
        Assert.Empty(f.Errors);
        Assert.Equal(Localizer.Text("BrowserTabActivationRequired"), f.Model.StatusText);
    }

    [Fact]
    public void NavigationOrDisablingRoutingEndsTheRequest()
    {
        var f = new Recorder();
        f.Apply("music");
        f.Model.Activate();
        f.Apply("music", site: "https://another.example.com");
        Assert.False(f.Model.IsActivating);
        f.Model.Activate();
        Assert.True(f.Model.IsActivating);
        f.Model.SetCanEdit(false);
        Assert.False(f.Model.IsActivating);
        Assert.Equal(Localizer.Text("BrowserTabRoutingDisabled"), f.Model.StatusText);
    }

    [Fact]
    public void TabRowsReadOnlyTheActivationStagesTheBrowserCanReport()
    {
        var model = new MainViewModel(new DaemonClient("ws://127.0.0.1:1/ws"));
        Report(model, true, new JsonArray(Tab(channel: "music", activation: "popup"), Tab(id: 8, channel: "music", activation: "capturing")));
        Assert.Equal("popup", model.BrowserTabs[0].Activation);
        Assert.Equal(Localizer.Text("BrowserTabActivatePopup"), model.BrowserTabs[0].StatusText);
        Assert.Null(model.BrowserTabs[1].Activation);
        Assert.True(model.BrowserTabs[1].CanActivate);
    }

    [Fact]
    public void TabRowsUseSessionAndIdAndDisappearForLegacyStateOrDisconnect()
    {
        var model = new MainViewModel(new DaemonClient("ws://127.0.0.1:1/ws"));
        Report(model, true, new JsonArray(Tab(), Tab(id: 8), Tab(session: "fedcba9876543210fedcba9876543210"), Tab(), Tab(session: "invalid")));
        Assert.Equal(3, model.BrowserTabs.Count);
        Assert.True(model.HasBrowserTabs);
        // Tabs live in their own window; they no longer enable Manage apps.
        Assert.False(model.HasApps);
        BrowserTabViewModel first = model.BrowserTabs[0];
        BrowserTabViewModel removed = model.BrowserTabs[1];
        Report(model, true, new JsonArray(Tab(title: "Updated")));
        Assert.Same(first, Assert.Single(model.BrowserTabs));
        Assert.Equal("Updated", first.Title);
        Assert.False(removed.CanEdit);
        Assert.Equal(["", "music", "chat"], first.Channels.Select(c => c.Id));
        Invoke(model, "ApplyBrowserTabs", JsonNode.Parse("""{"channels":[],"streams":[]}"""));
        Assert.Empty(model.BrowserTabs);
        Assert.False(model.BrowserTabSupport);
        Report(model, false, new JsonArray(Tab()));
        Assert.False(Assert.Single(model.BrowserTabs).CanEdit);
        Invoke(model, "ResetBrowserTabs");
        Assert.Empty(model.BrowserTabs);
        Assert.False(model.HasBrowserTabs);
    }

    [Fact]
    public async Task EnablingBrowserRoutingRequiresAnAcknowledgementAndRefusalKeepsItDisabled()
    {
        var connected = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = Channel.CreateUnbounded<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            connected.TrySetResult(socket);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() == "setPreserveBrowserOutputs") commands.Writer.TryWrite(command);
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
        Report(model, false, new JsonArray(Tab()));
        model.PreserveBrowserOutputs = true;
        JsonNode first = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("setPreserveBrowserOutputs", first["cmd"]!.GetValue<string>());
        Assert.False(model.PreserveBrowserOutputs);
        Assert.False(model.CanSetBrowserRouting);
        Report(model, true, new JsonArray(Tab()));
        Assert.False(model.PreserveBrowserOutputs);
        Assert.False(model.BrowserTabs[0].CanEdit);
        Report(model, false, new JsonArray(Tab()));
        await Reply(first, "routing unavailable");
        await Until(() => model.CanSetBrowserRouting);
        Assert.False(model.PreserveBrowserOutputs);
        Assert.Equal("routing unavailable", model.Status);
        model.PreserveBrowserOutputs = true;
        JsonNode second = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Report(model, true, new JsonArray(Tab()));
        await Reply(second);
        await Until(() => model.PreserveBrowserOutputs);
        Assert.True(model.BrowserTabs[0].CanEdit);
        Assert.False(commands.Reader.TryRead(out _));

        // Another client can change the value again before our acknowledgement resumes.
        model.PreserveBrowserOutputs = false;
        JsonNode third = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Report(model, false, new JsonArray(Tab()));
        Report(model, true, new JsonArray(Tab()));
        await Reply(third);
        await Until(() => model.CanSetBrowserRouting);
        Assert.True(model.PreserveBrowserOutputs);
        Report(model, false, new JsonArray(Tab()));
        Assert.False(model.PreserveBrowserOutputs);
        Assert.False(model.BrowserTabs[0].CanEdit);

        Task Reply(JsonNode command, string? error = null) => SocketTestServer.Send(socket,
            new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error }, CancellationToken.None);
    }

    internal static JsonObject Tab(string session = Session, int id = 7, string title = "A browser tab", string? channel = null,
        bool remember = false, bool capturing = false, string? activation = null)
    {
        var tab = new JsonObject
        {
            ["session"] = session, ["id"] = id, ["title"] = title, ["site"] = "https://example.com",
            ["channelId"] = channel, ["remember"] = remember, ["capturing"] = capturing,
        };
        if (activation is not null) tab["activation"] = activation;
        return tab;
    }

    internal static void Report(MainViewModel model, bool preserve, JsonArray tabs)
    {
        JsonNode mixer = JsonNode.Parse("""
            {"channels":[{"id":"xlr1","name":"Microphone","hardware":true},{"id":"music","name":"Music"},
                         {"id":"chat","name":"Voice Chat"},{"id":"capture","name":"Capture","captureSource":"source"}],"mixes":[]}
            """)!;
        mixer["preserveBrowserOutputs"] = preserve;
        mixer["browserTabs"] = tabs;
        Invoke(model, "ApplyMixer", mixer);
        Invoke(model, "ApplyBrowserTabs", mixer);
    }

    private static void Invoke(MainViewModel model, string name, params object?[] args)
        => typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
