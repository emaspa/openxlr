using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The UI's choice of host for a Windows plugin: offered for a plugin bridged
/// through yabridge on a build with the Windows plugin host, as following the
/// default, which it names, or a pin to either host, sent as one command,
/// showing the host that runs it and why it does not, and leaving the
/// plugin's editor and its compatibility rule as they are on yabridge.
/// </summary>
public sealed class InsertWindowsHostChoiceTests
{
    private const string ClassId = "0123456789ABCDEF0123456789ABCDEF";

    private static PluginChoice Choice(bool bridged, bool ruleBlocked = false)
        => new(ClassId, "Deep Verb", "Reverb", new JsonArray(), true, true, "vst3", ruleBlocked, bridged);

    private static JsonArray Chain(string? windowsHost = null, bool running = false, string? error = null,
        bool blocked = false, string? reason = null)
    {
        var insert = new JsonObject { ["id"] = "fx", ["kind"] = "vst3", ["plugin"] = ClassId, ["params"] = new JsonObject() };
        if (windowsHost is not null) insert["windowsHost"] = windowsHost;
        var entry = new JsonObject { ["insert"] = insert, ["nativeHostRunning"] = true, ["windowsHostRunning"] = running };
        if (error is not null) entry["windowsHostError"] = error;
        if (blocked) { entry["nativeUiBlocked"] = true; entry["nativeUiBlockReason"] = reason; }
        return [entry];
    }

    [Theory]
    [InlineData("vst3", "/home/u/.local/share/openxlr/yabridge/vst3/Deep Verb.vst3", true)]
    [InlineData("clap", "/home/u/.clap/yabridge/Deep Verb.clap", true)]
    [InlineData("vst3", "/home/u/.vst3/yabridge/Deep Verb.vst3", true)]
    [InlineData("vst3", "/usr/lib/vst3/Deep Verb.vst3", false)]
    [InlineData("vst3", "/home/u/.vst3/not-yabridge/Deep Verb.vst3", false)]
    [InlineData("lv2", "/home/u/yabridge/x.lv2", false)]
    [InlineData("vst3", null, false)]
    public void ACatalogueEntryIsBridgedWhenItsBundleIsAYabridgeWrapper(string kind, string? path, bool bridged)
        => Assert.Equal(bridged, PluginChoice.IsBridged(kind, path));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void TheChoiceIsOfferedForABridgedPluginOnABuildWithTheHost(bool installed, bool bridged, bool offered)
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = installed };
        owner.PluginChoices.Add(Choice(bridged));
        owner.Apply(Chain());
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.Equal(bridged, insert.Bridged);
        Assert.Equal(offered, insert.CanChooseWindowsHost);
        Assert.Null(insert.WindowsHostPin);
        Assert.Equal("default", insert.SelectedWindowsHost.Value);
        Assert.Equal(offered ? Localizer.Text("RunsThroughYabridge") : null, insert.WindowsHostState);

        // A pinned insert keeps the control, so it can follow the default again.
        owner.Apply(Chain(windowsHost: "openxlr", running: installed && bridged));
        Assert.Equal("openxlr", insert.WindowsHostPin);
        Assert.Equal("openxlr", insert.SelectedWindowsHost.Value);
        Assert.True(insert.CanChooseWindowsHost);
        owner.Apply(Chain(windowsHost: "yabridge"));
        Assert.Equal("yabridge", insert.WindowsHostPin);
        Assert.Equal("yabridge", insert.SelectedWindowsHost.Value);
        Assert.True(insert.CanChooseWindowsHost);
    }

    [Fact]
    public void TheControlOffersTheDefaultAndBothPinsAndNamesTheHostTheDefaultMeans()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.Apply(Chain());
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.Equal(["default", "yabridge", "openxlr"], insert.WindowsHostOptions.Select(o => o.Value));
        Assert.Equal([Localizer.Text("WindowsHostFollowDefault"), Localizer.Text("WindowsHostPinYabridge"), Localizer.Text("WindowsHostPinOpenXlr")],
            insert.WindowsHostOptions.Select(o => o.Label));

        // The daemon says what the default is, and the first choice names it.
        WindowsHostOption first = insert.WindowsHostOptions[0];
        var raised = new List<string?>();
        first.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        owner.ApplyWindowsHostSetup(JsonNode.Parse("""{"windowsHostInstalled":true,"windowsHostDefault":"yabridge"}"""));
        Assert.Equal(Localizer.Text("WindowsHostFollowDefaultYabridge"), first.Label);
        owner.ApplyWindowsHostSetup(JsonNode.Parse("""{"windowsHostInstalled":true,"windowsHostDefault":"openxlr"}"""));
        Assert.Equal(Localizer.Text("WindowsHostFollowDefaultOpenXlr"), first.Label);
        Assert.Contains(nameof(WindowsHostOption.Label), raised);
        // The same choice objects stay, so the control keeps its selection.
        Assert.Same(first, insert.WindowsHostOptions[0]);
        Assert.Same(first, insert.SelectedWindowsHost);
        // A value this UI does not know names no host, and a new connection forgets it.
        owner.ApplyWindowsHostSetup(JsonNode.Parse("""{"windowsHostDefault":"a-host-of-a-newer-build"}"""));
        Assert.Equal(Localizer.Text("WindowsHostFollowDefault"), first.Label);
        owner.ApplyWindowsHostSetup(JsonNode.Parse("""{"windowsHostDefault":"openxlr"}"""));
        owner.ResetForNewConnection();
        Assert.Null(owner.WindowsHostDefault);

        // Following a default that means the host and running through
        // yabridge says so in words of its own, not as a pin.
        owner.WindowsHostInstalled = true;
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.WindowsHostDefault = "openxlr";
        const string reason = "it did not start on the Windows plugin host: the plugin did not load";
        owner.Apply(Chain(error: reason));
        Assert.Equal(Localizer.Format("WindowsHostDefaultKept", reason), insert.WindowsHostNote);
        Assert.Equal(Localizer.Text("RunsThroughYabridge"), insert.WindowsHostState);
        owner.Apply(Chain(windowsHost: "openxlr", error: reason));
        Assert.Equal(Localizer.Format("WindowsHostKept", reason), insert.WindowsHostNote);
    }

    [Fact]
    public void TheChoiceWaitsForTheCatalogue()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.Apply(Chain());
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.False(insert.CanChooseWindowsHost);
        var raised = new List<string?>();
        insert.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        owner.PluginChoices.Add(Choice(bridged: true));
        insert.RefreshCatalogue();
        Assert.Contains(nameof(InsertViewModel.CanChooseWindowsHost), raised);
        Assert.True(insert.CanChooseWindowsHost);

        raised.Clear();
        owner.WindowsHostInstalled = false;
        Assert.Contains(nameof(InsertViewModel.CanChooseWindowsHost), raised);
        Assert.False(insert.CanChooseWindowsHost);
    }

    [Fact]
    public void TheStateShowsTheHostThatRunsThePluginAndWhyItIsNotTheSavedOne()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.Apply(Chain(windowsHost: "openxlr", running: true));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.Equal("openxlr", insert.WindowsHostPin);
        Assert.True(insert.WindowsHostRunning);
        Assert.Equal(Localizer.Text("RunsOnTheWindowsPluginHost"), insert.WindowsHostState);
        Assert.False(insert.HasWindowsHostNote);

        const string reason = "'Deep Verb' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
        owner.Apply(Chain(windowsHost: "openxlr", error: reason));
        Assert.Equal("openxlr", insert.WindowsHostPin);   // the saved choice stays
        Assert.False(insert.WindowsHostRunning);
        Assert.Equal(Localizer.Text("RunsThroughYabridge"), insert.WindowsHostState);
        Assert.Equal(Localizer.Format("WindowsHostKept", reason), insert.WindowsHostNote);
        Assert.False(insert.HasError, "Audio goes on through yabridge, so the insert has not failed.");
        Assert.True(insert.IsActive);
    }

    [Fact]
    public void AnInsertOnTheWindowsHostHasItsEditorAndItsRuleLikeAnyOther()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.Apply(Chain(windowsHost: "openxlr", running: true));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.True(insert.WindowsHostRunning);
        Assert.False(insert.NativeUiBlocked);
        Assert.Null(insert.NativeUiBlockReason);
        Assert.True(insert.NativeEditorAvailable, "Plugin UI opens the editor on this host.");

        // A block is a compatibility rule's on this host as on yabridge, with
        // its reason, whether the insert's status or the catalogue carries it.
        owner.Apply(Chain(windowsHost: "openxlr", running: true, blocked: true, reason: "Known editor issue."));
        Assert.True(insert.NativeUiBlocked);
        Assert.Equal("Known editor issue.", insert.NativeUiBlockReason);
        Assert.False(insert.NativeEditorAvailable);
        owner.Apply(Chain(windowsHost: "openxlr", running: true));
        Assert.False(insert.NativeUiBlocked);
        owner.PluginChoices.Clear();
        owner.PluginChoices.Add(Choice(bridged: true, ruleBlocked: true));
        insert.RefreshCatalogue();
        Assert.True(insert.NativeUiBlocked);
        Assert.False(insert.NativeEditorAvailable);
    }

    [Fact]
    public async Task ASwitchThatDidNotReachTheDaemonSaysSo()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.Apply(Chain());
        InsertViewModel insert = Assert.Single(owner.Items);
        await owner.SendWindowsHostAsync(insert, "openxlr").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Localizer.Text("DaemonDisconnectedNoChange"), insert.WindowsHostNote);
    }

    [Fact]
    public async Task TheSwitchSendsOneCommandAndShowsARefusal()
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<JsonNode>();
        string? refusal = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string? requestId = command["requestId"]?.GetValue<string>();
                if (command["cmd"]?.GetValue<string>() == "setInsertWindowsHost")
                {
                    received.Enqueue(command);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId, error = refusal }, stop);
                }
                else if (requestId is not null)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var owner = new InsertsViewModel(client, "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(Choice(bridged: true));
        owner.Apply(Chain());
        InsertViewModel insert = Assert.Single(owner.Items);
        owner.Apply(Chain(windowsHost: "openxlr"));
        owner.Apply(Chain());
        Assert.Empty(received);   // applying the daemon's state sends nothing back

        insert.SelectedWindowsHost = insert.WindowsHostOptions[2];
        for (var deadline = DateTime.UtcNow.AddSeconds(3); received.IsEmpty && DateTime.UtcNow < deadline;) await Task.Delay(10);
        JsonNode sent = Assert.Single(received);
        Assert.Equal("setInsertWindowsHost", sent["cmd"]!.GetValue<string>());
        Assert.Equal("mix:monitor", sent["channel"]!.GetValue<string>());
        Assert.Equal("fx", sent["insertId"]!.GetValue<string>());
        Assert.Equal("openxlr", sent["value"]!.GetValue<string>());
        Assert.Equal("openxlr", insert.WindowsHostPin);
        Assert.Null(insert.WindowsHostNote);
        // Choosing what is already chosen sends nothing, and a control that
        // clears its selection while it is rebuilt changes nothing.
        insert.SelectedWindowsHost = insert.WindowsHostOptions[2];
        insert.SelectedWindowsHost = null!;
        await Task.Delay(50);
        Assert.Single(received);

        // Back to the default: the command says "default".
        insert.SelectedWindowsHost = insert.WindowsHostOptions[0];
        for (var deadline = DateTime.UtcNow.AddSeconds(3); received.Count < 2 && DateTime.UtcNow < deadline;) await Task.Delay(10);
        Assert.Equal("default", received.Last()["value"]!.GetValue<string>());
        Assert.Null(insert.WindowsHostPin);

        await owner.SendWindowsHostAsync(insert, "yabridge");
        Assert.Equal("yabridge", received.Last()["value"]!.GetValue<string>());

        // The daemon's state comes before its answer and holds the saved host,
        // none, so the control is back on the default when the refusal is shown.
        owner.Apply(Chain());
        Assert.Null(insert.WindowsHostPin);
        const string reason = "'Deep Verb' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
        refusal = "setInsertWindowsHost: " + reason;
        await owner.SendWindowsHostAsync(insert, "openxlr");
        Assert.Equal(Localizer.Format("WindowsHostRefused", reason), insert.WindowsHostNote);
        Assert.True(insert.HasWindowsHostNote);
        Assert.False(insert.HasError);
        owner.Apply(Chain());
        Assert.True(insert.HasWindowsHostNote, "A state push that changes nothing keeps the reason.");

        // The next attempt starts clean, and so does a move made elsewhere.
        refusal = null;
        await owner.SendWindowsHostAsync(insert, "openxlr");
        Assert.Null(insert.WindowsHostNote);
        refusal = "setInsertWindowsHost: " + reason;
        await owner.SendWindowsHostAsync(insert, "openxlr");
        Assert.NotNull(insert.WindowsHostNote);
        owner.Apply(Chain(windowsHost: "openxlr", running: true));
        Assert.Null(insert.WindowsHostNote);
        Assert.Equal(6, received.Count);
    }
}
