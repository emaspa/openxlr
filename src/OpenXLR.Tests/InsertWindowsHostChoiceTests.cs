using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The UI's choice of host for a Windows plugin: offered for a plugin bridged
/// through yabridge on a build with the Windows plugin host, sent as one
/// command, showing the host that runs it and why it does not, and leaving
/// the plugin's editor and its compatibility rule as they are on yabridge.
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
        Assert.False(insert.OnWindowsHost);
        Assert.Equal(offered ? Localizer.Text("RunsThroughYabridge") : null, insert.WindowsHostState);

        // An insert already on the host keeps the switch, so it can go back.
        owner.Apply(Chain(windowsHost: "openxlr", running: installed && bridged));
        Assert.True(insert.OnWindowsHost);
        Assert.True(insert.CanChooseWindowsHost);
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
        Assert.True(insert.OnWindowsHost);
        Assert.True(insert.WindowsHostRunning);
        Assert.Equal(Localizer.Text("RunsOnTheWindowsPluginHost"), insert.WindowsHostState);
        Assert.False(insert.HasWindowsHostNote);

        const string reason = "'Deep Verb' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
        owner.Apply(Chain(windowsHost: "openxlr", error: reason));
        Assert.True(insert.OnWindowsHost, "The saved choice stays.");
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
        await owner.SendWindowsHostAsync(insert, true).WaitAsync(TimeSpan.FromSeconds(5));
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

        insert.OnWindowsHost = true;
        for (var deadline = DateTime.UtcNow.AddSeconds(3); received.IsEmpty && DateTime.UtcNow < deadline;) await Task.Delay(10);
        JsonNode sent = Assert.Single(received);
        Assert.Equal("setInsertWindowsHost", sent["cmd"]!.GetValue<string>());
        Assert.Equal("mix:monitor", sent["channel"]!.GetValue<string>());
        Assert.Equal("fx", sent["insertId"]!.GetValue<string>());
        Assert.Equal("openxlr", sent["value"]!.GetValue<string>());
        Assert.Null(insert.WindowsHostNote);

        await owner.SendWindowsHostAsync(insert, false);
        Assert.Equal("yabridge", received.Last()["value"]!.GetValue<string>());

        // The daemon's state comes before its answer and holds the saved host,
        // yabridge, so the switch is back off when the refusal is shown.
        owner.Apply(Chain());
        Assert.False(insert.OnWindowsHost);
        const string reason = "'Deep Verb' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
        refusal = "setInsertWindowsHost: " + reason;
        await owner.SendWindowsHostAsync(insert, true);
        Assert.Equal(Localizer.Format("WindowsHostRefused", reason), insert.WindowsHostNote);
        Assert.True(insert.HasWindowsHostNote);
        Assert.False(insert.HasError);
        owner.Apply(Chain());
        Assert.True(insert.HasWindowsHostNote, "A state push that changes nothing keeps the reason.");

        // The next attempt starts clean, and so does a move made elsewhere.
        refusal = null;
        await owner.SendWindowsHostAsync(insert, true);
        Assert.Null(insert.WindowsHostNote);
        refusal = "setInsertWindowsHost: " + reason;
        await owner.SendWindowsHostAsync(insert, true);
        Assert.NotNull(insert.WindowsHostNote);
        owner.Apply(Chain(windowsHost: "openxlr", running: true));
        Assert.Null(insert.WindowsHostNote);
        Assert.Equal(5, received.Count);
    }
}
