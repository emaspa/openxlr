using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorFeedTests
{
    [Theory]
    [InlineData("stream", true, false)]
    [InlineData("chat", true, false)]
    [InlineData("auxout", true, false)]
    [InlineData("monitor+chat", true, false)]
    [InlineData("monitor2+monitor", true, true)]
    [InlineData("monitor", true, true)]
    [InlineData("chat+chat", false, false)]
    [InlineData("unknown", false, false)]
    [InlineData("", false, false)]
    public void EveryMixCanFeedAnOutputButOnlyMonitorMixesCanUseTheDirectMicPath(string feed, bool valid, bool direct)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, mixer.IsMonitorFeed(feed));
        Assert.Equal(direct, mixer.IsMonitorOnlyFeed(feed));
    }

    [Fact]
    public void FeedPickerKeepsApiSumsAndRenamedMixesWithoutSendingCommands()
    {
        int changed = 0;
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => { changed++; return Task.FromResult<string?>(null); });
        output.SyncFeed([new("monitor", "Monitor A"), new("stream", "Stream"), new("chat", "Chat")], "stream+chat");
        Assert.Equal("stream+chat", output.Feed!.Id);
        Assert.Equal("Stream+Chat", output.Feed.Name);
        output.SyncFeed([new("monitor", "Monitor A"), new("stream", "Recording"), new("chat", "Chat")], "stream+chat");
        Assert.Equal("Recording+Chat", output.Feed!.Name);
        Assert.Equal(0, changed);
        output.Sync(true);
        output.FeedMixes[0].IsSelected = true;
        Assert.Equal("monitor+stream+chat", output.Feed!.Id);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void CheckedMixesSendEachNewSumAndKeepAtLeastOneChoice()
    {
        var sent = new List<string>();
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, feed) => { sent.Add(feed); return Task.FromResult<string?>(null); });
        output.Sync(true);
        output.SyncFeed([new("desk", "Desk"), new("booth", "Booth"), new("chat", "Chat")], "desk");
        Assert.False(output.FeedMixes[0].CanToggle);
        output.FeedMixes[0].IsSelected = false;
        Assert.Equal("desk", output.Feed!.Id);
        Assert.Empty(sent);
        output.FeedMixes[1].IsSelected = true;
        output.FeedMixes[2].IsSelected = true;
        output.FeedMixes[0].IsSelected = false;
        output.FeedMixes[1].IsSelected = false;
        Assert.Equal(["desk+booth", "desk+booth+chat", "booth+chat", "chat"], sent);
        Assert.False(output.FeedMixes[2].CanToggle);
        Assert.All(output.FeedMixes.Take(2), choice => Assert.True(choice.CanToggle));
    }

    [Fact]
    public void StateRefreshPreservesChoiceObjectsAndNeverSendsFeedCommands()
    {
        int sent = 0;
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => { sent++; return Task.FromResult<string?>(null); });
        MixOption[] mixes = [new("desk", "Desk"), new("booth", "Booth")];
        output.Sync(true);
        output.SyncFeed(mixes, "booth+desk");
        MonitorMixChoice first = output.FeedMixes[0];
        output.SyncFeed(mixes, "desk+booth");
        Assert.Same(first, output.FeedMixes[0]);
        output.SyncFeed([new("booth", "Studio")], "booth");
        Assert.Equal("Studio", output.Feed!.Name);
        Assert.Equal("booth", Assert.Single(output.FeedMixes).Id);
        output.SyncFeed([], null);
        Assert.Empty(output.FeedMixes);
        Assert.Null(output.Feed);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void SharedMixesAreSelectedIndependentlyForEveryOutput()
    {
        var sent = new List<(string, string)>();
        MixOption[] mixes = [new("desk", "Desk"), new("booth", "Booth")];
        MonitorOutputItem Output(string name)
        {
            var result = new MonitorOutputItem(name, name, () => { }, (device, feed) => { sent.Add((device, feed)); return Task.FromResult<string?>(null); });
            result.Sync(true);
            result.SyncFeed(mixes, "desk");
            return result;
        }
        var headset = Output("headset");
        var speakers = Output("speakers");
        headset.FeedMixes[1].IsSelected = true;
        Assert.Equal("desk", speakers.Feed!.Id);
        speakers.FeedMixes[1].IsSelected = true;
        Assert.Equal([("headset", "desk+booth"), ("speakers", "desk+booth")], sent);
        speakers.Sync(false);
        speakers.FeedMixes[0].IsSelected = false;
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingEditsKeepTheLatestSelectionAcrossOldStateAndOldReplies(bool oldReplyFails)
    {
        var replies = new Queue<TaskCompletionSource<string?>>();
        var sent = new List<string>();
        var errors = new List<string>();
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, feed) =>
        {
            sent.Add(feed);
            var reply = new TaskCompletionSource<string?>();
            replies.Enqueue(reply);
            return reply.Task;
        }, reportError: errors.Add);
        MixOption[] mixes = [new("desk", "Desk"), new("booth", "Booth"), new("chat", "Chat")];
        output.Sync(true);
        output.SyncFeed(mixes, "desk");
        output.FeedMixes[1].IsSelected = true;
        output.SyncFeed(mixes, "desk");
        Assert.Equal("desk+booth", output.Feed!.Id);
        output.FeedMixes[2].IsSelected = true;
        replies.Dequeue().SetResult(oldReplyFails ? "refused" : null);
        await Task.Yield();
        output.SyncFeed(mixes, "desk+booth");
        Assert.Equal("desk+booth+chat", output.Feed!.Id);
        output.SyncFeed(mixes, "desk+booth+chat");
        replies.Dequeue().SetResult(null);
        await Task.Yield();
        Assert.Equal(["desk+booth", "desk+booth+chat"], sent);
        Assert.Equal("desk+booth+chat", output.Feed!.Id);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamingAndAddingMixesKeepsTheStableIdsOfAPendingFeed(bool refused)
    {
        var reply = new TaskCompletionSource<string?>();
        var sent = new List<string>();
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, feed) =>
        {
            sent.Add(feed);
            return reply.Task;
        });
        output.Sync(true);
        output.SyncFeed([new("desk", "Desk"), new("booth", "Booth")], "desk");
        output.FeedMixes[1].IsSelected = true;
        output.SyncFeed([new("booth", "Renamed booth"), new("desk", "Renamed desk"), new("new", "New mix")], "desk");
        Assert.Equal(["booth", "desk"], output.FeedMixes.Where(m => m.IsSelected).Select(m => m.Id));
        Assert.Contains("Renamed booth", output.Feed!.Name);
        reply.SetResult(refused ? "refused" : null);
        await Task.Yield();
        if (refused) Assert.Equal("desk", output.Feed!.Id);
        else
        {
            output.SyncFeed([new("booth", "Renamed booth"), new("desk", "Renamed desk"), new("new", "New mix")], "desk+booth");
            Assert.Equal("booth+desk", output.Feed!.Id);
        }
        Assert.False(output.FeedMixes.Single(m => m.Id == "new").IsSelected);
        Assert.Equal(["desk+booth"], sent);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("deselect")]
    [InlineData("disconnect")]
    public async Task AReplyCannotRestoreAnObsoleteFeedAfterItsContextChanges(string change)
    {
        var reply = new TaskCompletionSource<string?>();
        var errors = new List<string>();
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => reply.Task, reportError: errors.Add);
        output.Sync(true);
        output.SyncFeed([new("desk", "Desk"), new("booth", "Booth")], "desk");
        output.FeedMixes[1].IsSelected = true;
        if (change == "deselect") output.Sync(false);
        if (change == "disconnect") output.CancelFeedEdit();
        output.SyncFeed([new("desk", "Desk")], "desk");
        reply.SetResult("refused");
        await Task.Yield();
        Assert.Equal("desk", output.Feed!.Id);
        Assert.Single(output.FeedMixes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ARejectedFeedReturnsToTheLatestDaemonSelection()
    {
        var reply = new TaskCompletionSource<string?>();
        var errors = new List<string>();
        var output = new MonitorOutputItem("headset", "Headset", () => { }, (_, _) => reply.Task, reportError: errors.Add);
        MixOption[] mixes = [new("desk", "Desk"), new("booth", "Booth")];
        output.Sync(true);
        output.SyncFeed(mixes, "desk");
        output.FeedMixes[1].IsSelected = true;
        output.SyncFeed(mixes, "desk");
        reply.SetResult("disk full");
        await Task.Yield();
        Assert.Equal("desk", output.Feed!.Id);
        Assert.False(output.FeedMixes[1].IsSelected);
        Assert.Equal(["disk full"], errors);
    }

    [Fact]
    public async Task TheIdleReleaseIsOfferedForBluetoothAndSentOnlyForASelectedOne()
    {
        var sent = new List<(string Device, bool Enabled)>();
        var reply = new TaskCompletionSource<string?>();
        var errors = new List<string>();
        var wired = new MonitorOutputItem("alsa_output.headset", "Headset", () => { }, (_, _) => Task.FromResult<string?>(null),
            idleReleaseChanged: (device, on) => { sent.Add((device, on)); return Task.FromResult<string?>(null); });
        var headphones = new MonitorOutputItem("bluez_output.AA_BB.1", "Buds", () => { }, (_, _) => Task.FromResult<string?>(null),
            reportError: errors.Add, idleReleaseChanged: (device, on) => { sent.Add((device, on)); return reply.Task; });
        Assert.False(wired.IsBluetooth);
        Assert.True(headphones.IsBluetooth);

        // The daemon's value arrives without echoing a command.
        headphones.SyncIdleRelease(true);
        Assert.True(headphones.IdleRelease);
        headphones.SyncIdleRelease(false);
        Assert.Empty(sent);

        // Not selected, or not Bluetooth: nothing is sent.
        headphones.IdleRelease = true;
        wired.Sync(true);
        wired.IdleRelease = true;
        Assert.Empty(sent);

        // A refusal puts back what the daemon last reported and says why.
        headphones.SyncIdleRelease(false);
        headphones.Sync(true);
        headphones.IdleRelease = true;
        Assert.Equal([("bluez_output.AA_BB.1", true)], sent);
        headphones.SyncIdleRelease(false);
        Assert.True(headphones.IdleRelease);   // the edit still waits for its answer
        reply.SetResult("refused");
        await Task.Yield();
        Assert.False(headphones.IdleRelease);
        Assert.Equal(["refused"], errors);
    }

    [Theory]
    [InlineData(null, "studio")]
    [InlineData("desk", "desk")]
    [InlineData("removed", "studio")]
    public async Task OutputDefaultsUseTheReportedMonitorOrTheOlderDaemonsMonitorFallback(string? reported, string expected)
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        var state = System.Text.Json.Nodes.JsonNode.Parse("""
            {"mixes":[{"id":"chat","name":"Chat","kind":"virtualMic"},
                       {"id":"studio","name":"Studio","kind":"monitor"},
                       {"id":"desk","name":"Desk","kind":"monitor"}],
             "monitorOutputs":["headset"],"monitorFeeds":{}}
            """)!;
        if (reported is not null) state["defaultMonitorMix"] = reported;
        var apply = typeof(MainViewModel).GetMethod("ApplyDevices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        apply.Invoke(model, [new System.Text.Json.Nodes.JsonArray(), state]);
        Assert.Equal(expected, Assert.Single(model.MonitorOutputs).Feed!.Id);
        state["mixes"]!.AsArray().RemoveAt(2);
        state["mixes"]!.AsArray().RemoveAt(1);
        apply.Invoke(model, [new System.Text.Json.Nodes.JsonArray(), state]);
        Assert.Null(Assert.Single(model.MonitorOutputs).Feed);
    }

    [Theory]
    [InlineData("removed", "monitor")]
    [InlineData("removed+desk", "desk")]
    [InlineData("desk+monitor+removed", "monitor+desk")]
    [InlineData(" desk ", "desk")]
    [InlineData("monitor + desk", "monitor+desk")]
    [InlineData("+ monitor ++ desk +", "monitor+desk")]
    public async Task StaleSavedMixIdsShowOnlyTheRoutesTheDaemonCanActuallyUse(string reported, string expected)
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        var state = System.Text.Json.Nodes.JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},
                       {"id":"desk","name":"Desk","kind":"monitor"}],
             "monitorOutputs":["headset"],"monitorFeeds":{}}
            """)!;
        state["monitorFeeds"]!["headset"] = reported;
        typeof(MainViewModel).GetMethod("ApplyDevices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(model, [new System.Text.Json.Nodes.JsonArray(), state]);
        Assert.Equal(expected, Assert.Single(model.MonitorOutputs).Feed!.Id);
    }

    [Fact]
    public async Task ListeningToAuxExposesItsFadersWithoutInventingAUsbPort()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client) { CapOutputRouting = false };
        typeof(MainViewModel).GetProperty(nameof(model.DeviceConnected))!.SetValue(model, true);
        var state = System.Text.Json.Nodes.JsonNode.Parse("""
            {"mixes":[{"id":"auxout","name":"Aux","kind":"auxPort"}],
             "channels":[{"id":"game","name":"Game"}],"monitorFeeds":{"headset":"auxout"}}
            """);
        var apply = typeof(MainViewModel).GetMethod("ApplyMixer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        apply.Invoke(model, [state]);
        Assert.True(model.Mixes[0].Visible);
        Assert.True(model.Channels[0].Sends[0].Visible);
        Assert.False(model.Mixes[0].ShowAuxPortToggle);
        state!["monitorFeeds"] = new System.Text.Json.Nodes.JsonObject();
        apply.Invoke(model, [state]);
        Assert.False(model.Mixes[0].Visible);
        Assert.False(model.Channels[0].Sends[0].Visible);
    }

    [Fact]
    public void AFeedIsOneMixOrSeveralJoinedWithPlus()
    {
        Assert.Equal(["monitor"], MonitorFeed.Parts("monitor"));
        Assert.Equal(["monitor", "monitor2"], MonitorFeed.Parts("monitor+monitor2"));
        Assert.Equal(["monitor2"], MonitorFeed.Parts(" monitor2 + "));
        Assert.Empty(MonitorFeed.Parts(null));
        Assert.Equal("monitor+monitor2", MonitorFeed.Join(["monitor", "monitor2"]));
        Assert.True(MonitorFeed.Includes("monitor+monitor2", "monitor2"));
        Assert.False(MonitorFeed.Includes("monitor", "monitor2"));
    }

    [Fact]
    public void TheSummedOptionReadsMonitorAPlusB()
    {
        Assert.Equal("Monitor A+B", MainViewModel.SummedName(["Monitor A", "Monitor B"]));
        Assert.Equal("Desk+Booth", MainViewModel.SummedName(["Desk", "Booth"]));
    }
}
