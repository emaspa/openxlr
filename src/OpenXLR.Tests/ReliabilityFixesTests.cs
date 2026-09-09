using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Quiet failure modes in routing, settings and plugin hosting, each with
/// the case that showed it. They are unrelated to each other; what they
/// share is that every one of them was quiet, and would have stayed quiet
/// until it cost a user their settings, their mutes, or the daemon.
/// </summary>
public sealed class ReliabilityFixesTests
{
    // --- recalling a profile that predates a channel or a mix -------------------

    [Fact]
    public void RecallingAnOlderProfileLeavesUnknownSendsMuted()
    {
        // The profile was saved when only "stream" existed. "podcast" was
        // added afterwards, so every send into it sits at unity behind the
        // mute a new mix starts with. Recalling the profile must not open it:
        // that is the microphone into a mix the profile never knew about.
        var muted = new HashSet<string> { "xlr1|podcast", "game|podcast", "xlr1|stream" };
        string[] cells = ["xlr1|stream", "game|stream", "xlr1|podcast", "game|podcast"];
        var savedLevels = new Dictionary<string, double> { ["xlr1|stream"] = 0.8, ["game|stream"] = 0.5 };

        Mixer.RecallMutes(muted, cells, savedLevels.Keys, ["game|stream"]);

        Assert.Contains("xlr1|podcast", muted);   // never named by the profile: left as it was
        Assert.Contains("game|podcast", muted);
        Assert.Contains("game|stream", muted);    // named and muted by the profile
        Assert.DoesNotContain("xlr1|stream", muted);   // named by the profile, not in its mute list
    }

    [Fact]
    public void RecallingAProfileStillSetsEverySendItNames()
    {
        // The safety rule must not turn into "mutes are never cleared". A
        // send the profile carries a level for is set exactly as it says.
        var muted = new HashSet<string> { "xlr1|stream", "game|stream" };
        string[] cells = ["xlr1|stream", "game|stream"];
        Mixer.RecallMutes(muted, cells, new[] { "xlr1|stream", "game|stream" }, ["game|stream"]);
        Assert.Equal(["game|stream"], muted);

        // A mix master works the same way: named, so it follows the profile.
        var mixMuted = new HashSet<string> { "stream", "podcast" };
        Mixer.RecallMutes(mixMuted, ["stream", "chat", "podcast"], new[] { "stream", "chat" }, []);
        Assert.Equal(["podcast"], mixMuted);   // podcast is not in the profile, so its mute stays
    }

    // --- a combine leg that appears after its fader was pushed ------------------

    /// <summary>The sweeps a reconciliation round falls on, over a run of sweeps.</summary>
    private static List<int> ReconciliationRounds(int sweeps, Func<int, bool> applied)
    {
        var rounds = new List<int>();
        for (int sweep = 0, wait = 0; sweep < sweeps; sweep++)
        {
            if (wait > 0) { wait--; continue; }
            rounds.Add(sweep);
            if (applied(sweep)) break;
            wait = Math.Max(0, Mixer.CellRoundGap(rounds.Count) - 1);
        }
        return rounds;
    }

    [Fact]
    public void AMissingSendIsRetriedPromptlyThenLessOftenButNeverGivenUp()
    {
        // A leg that is merely late is up within a sweep or two, so the first
        // rounds run back to back. After that the gap doubles and settles at a
        // minute, and there it stays: a send whose leg has not appeared is a
        // send sitting at full level and unmuted, so giving up on it would
        // leave it open for as long as the daemon runs.
        var rounds = ReconciliationRounds(4000, _ => false);

        Assert.Equal([0, 1, 2], rounds.Take(3));                     // three prompt rounds
        Assert.Equal([2, 4, 8, 16, 32], rounds.Skip(3).Take(5).Select((s, i) => s - rounds[i + 2]));
        Assert.All(rounds.Zip(rounds.Skip(1)), pair => Assert.True(pair.Second - pair.First <= 60,
            "no two rounds should be more than a minute of sweeps apart"));
        Assert.True(rounds[^1] > 3900, "rounds should still be running an hour of sweeps in");

        // And the steady state stays cheap: a round a minute, not a round a
        // second, for as long as the send is waiting.
        Assert.InRange(rounds.Count, 8, 80);
    }

    [Fact]
    public void ASendWhoseLegArrivesLateIsSetWhenItDoes()
    {
        // Well past where a twelve-round budget would have expired. The
        // sweep has to still be looking, and has to catch it inside one
        // steady-state gap.
        const int legAppears = 900;
        var rounds = ReconciliationRounds(4000, sweep => sweep >= legAppears);
        Assert.InRange(rounds[^1], legAppears, legAppears + 60);
    }

    [Fact]
    public void ASendWhoseChannelOrMixWasDeletedStopsBeingRetried()
    {
        // The other way a cell leaves the list: it no longer exists. A
        // deleted channel or mix is not waiting for a leg, and its cells must
        // not keep the sweep looking for one.
        var pending = new HashSet<string> { "gone|stream", "xlr1|gone", "xlr1|stream" };
        Mixer.ForgetRemovedCells(pending, new HashSet<string> { "xlr1|stream" });
        Assert.Equal(["xlr1|stream"], pending);

        Mixer.ForgetRemovedCells(pending, new HashSet<string>());
        Assert.Empty(pending);   // nothing waiting, so the sweep stands down
    }

    // --- exporting settings while a plugin control moves ------------------------

    [Fact]
    public void AnExportedInsertDoesNotShareTheLiveParameterDictionary()
    {
        // Controls are written in place under the mixer's lock while the
        // debounced save serializes the export outside it. Sharing the
        // dictionary let a save enumerate one that gained a key mid-write,
        // which throws inside a timer callback and ends the daemon.
        var live = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] = [new InsertDefinition { Id = "i1", Kind = "lv2", Plugin = "urn:test", Params = { ["gain"] = 0.5 } }],
        };
        var exported = Mixer.CopyInserts(live);

        live["xlr1"][0].Params["threshold"] = 0.25;   // an editor edit during the save
        live["xlr1"][0].Params["gain"] = 0.9;

        Assert.Equal(0.5, exported["xlr1"][0].Params["gain"]);
        Assert.DoesNotContain("threshold", exported["xlr1"][0].Params.Keys);
        Assert.NotSame(live["xlr1"][0].Params, exported["xlr1"][0].Params);
    }

    // --- the last save before the graph goes ------------------------------------

    [Fact]
    public void NothingIsWrittenOnceTheWriterIsClosed()
    {
        // The graph is torn down right after the final save, and a torn-down
        // mixer exports empty levels, mutes and monitor routing. A debounced
        // tick still in flight, or the flush in Dispose, used to write that
        // empty state over the user's settings.
        var written = new List<string>();
        string state = "the user's mixer";
        var saver = new SettingsSaver(() => { written.Add(state); return null; }, _ => { });

        saver.Schedule();
        Assert.True(saver.Pending);
        Assert.Null(saver.Close(write: true));
        Assert.Equal(["the user's mixer"], written);
        Assert.True(saver.Closed);
        Assert.False(saver.Pending);

        state = "";                 // the graph is gone; an export would be empty now
        saver.Schedule();           // a change arriving during teardown
        saver.Flush();              // the pending debounce tick
        saver.Dispose();            // and the flush on the way out
        Assert.Equal(["the user's mixer"], written);
    }

    [Fact]
    public void APendingChangeIsStillWrittenAtTheLastMoment()
    {
        var written = new List<string>();
        var saver = new SettingsSaver(() => { written.Add("settings"); return null; }, _ => { });
        saver.Schedule();
        saver.Close(write: true);
        Assert.Equal(["settings"], written);
    }

    [Fact]
    public void AFailedWriteStaysPendingAndIsReportedOnce()
    {
        var reported = new List<string?>();
        string? failure = "read-only file system";
        var saver = new SettingsSaver(() => failure, reported.Add, null);

        saver.Schedule();
        saver.Flush();
        saver.Flush();
        Assert.True(saver.Pending);
        Assert.Equal("read-only file system", saver.Error);
        Assert.Equal(["read-only file system"], reported);   // once per distinct reason

        failure = null;
        saver.Flush();
        Assert.False(saver.Pending);
        Assert.Null(saver.Error);
        Assert.Equal(["read-only file system", null], reported);
        saver.Dispose();
    }

    [Fact]
    public void ALayoutCommandThatSavesItselfLeavesNothingPending()
    {
        var written = new List<string>();
        var saver = new SettingsSaver(() => { written.Add("debounced"); return null; }, _ => { });
        saver.Schedule();
        saver.RunSaved(() => written.Add("layout"));
        Assert.False(saver.Pending);
        saver.Flush();
        Assert.Equal(["layout"], written);   // the debounce was cancelled by the layout save
        saver.Dispose();
        Assert.Equal(["layout"], written);
    }

    // --- a helper that writes to stderr without newlines ------------------------

    [Fact]
    public void AHelperCannotMakeTheDaemonHoldItsOutput()
    {
        // ReadLineAsync buffers a whole line before it hands one over, so a
        // plugin logging through the helper and never writing a newline grew
        // that buffer without a limit while its heartbeats kept the process
        // looking healthy.
        var line = new StringBuilder();
        var flood = new string('x', 4096);
        for (int block = 0; block < 256; block++)
            Assert.Null(NativePluginHost.FoldErrorBlock(line, flood));   // no newline, so no line yet
        Assert.Equal(2048, line.Length);   // a megabyte in, and this is all that is held

        Assert.Equal(new string('x', 2048), NativePluginHost.FoldErrorBlock(line, "\n"));
        Assert.Equal(0, line.Length);
    }

    [Fact]
    public void CompleteLinesStillArriveWholeAndTrimmed()
    {
        var line = new StringBuilder();
        Assert.Equal("second", NativePluginHost.FoldErrorBlock(line, "first\r\nsecond\r\nthird"));
        Assert.Equal("third", line.ToString());
        Assert.Null(NativePluginHost.FoldErrorBlock(line, " part"));
        Assert.Equal("third part", NativePluginHost.FoldErrorBlock(line, "\n"));
    }

    // --- retiring a meter while its pump thread starts up -----------------------

    [Fact]
    public void RetiringAMeterWhileItsPumpStartsDoesNotThrowOnItsThread()
    {
        // Add released the gate before the pump thread had taken the process's
        // output stream, so a Remove in between could dispose the process
        // first. Process.StandardOutput then throws ObjectDisposedException,
        // on a raw background thread with no handler above it, which ends the
        // daemon. Both streams are taken under the gate now, and a stream
        // closed underneath fails inside the loops where it is caught. The
        // original window is narrow (the pump thread usually runs before Add
        // even returns), so this covers the lifecycle rather than reproducing
        // the race: there is nothing to assert but the run itself, since an
        // unhandled exception on those threads takes the test host with it.
        using var meters = new MeterReader();
        for (int i = 0; i < 50; i++)
        {
            var psi = new ProcessStartInfo("sleep") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("30");
            meters.Add($"m{i}", psi);
            meters.Remove($"m{i}");
        }
        Thread.Sleep(200);   // give every pump and drain thread time to reach its first read
        Assert.Empty(meters.Read());
    }

    // --- a peer that sends nothing, in many frames ------------------------------

    [Fact]
    public async Task AMessageThatArrivesInMoreFramesThanItsLimitIsRefused()
    {
        // An empty continuation frame carries no bytes, so the size limit
        // cannot see it. Counting the frames bounds the loop, and the one
        // deadline for the whole message replaces the timer and cancellation
        // registration that used to be created per frame.
        var served = new TaskCompletionSource<SocketGuard.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var result = await SocketGuard.ReceiveMessageAsync(socket, new byte[64], 16, TimeSpan.FromSeconds(5), stop);
            served.TrySetResult(result.Outcome);
        });
        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(server.Url), cts.Token);
        try
        {
            for (int frame = 0; frame < 64 && !served.Task.IsCompleted; frame++)
                await client.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, endOfMessage: false, cts.Token);
        }
        catch (WebSocketException) { /* the server closed on us, which is the point */ }
        Assert.Equal(SocketGuard.Outcome.TooBig, await served.Task.WaitAsync(cts.Token));
    }
}
