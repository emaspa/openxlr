using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class RestartPolicyTests
{
    [Fact]
    public void AChainThatKeepsDyingIsLeftOffUntilTheWindowPasses()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 3, windowMs: 300_000);
        Assert.False(policy.Blocked("xlr1"));

        // Three deaths are three rebuilds; the fourth leaves the chain off.
        for (int death = 1; death <= 3; death++)
        {
            now += 1_000;
            policy.Failed("xlr1");
            Assert.Equal(death == 3, policy.Blocked("xlr1"));
        }

        // Another chain's history is its own.
        policy.Failed("mix:stream");
        Assert.False(policy.Blocked("mix:stream"));

        // The window runs from the first death, so a chain that has been quiet
        // for long enough gets its chances back.
        now += 300_001;
        Assert.False(policy.Blocked("xlr1"));
    }

    [Fact]
    public void OnlyAGivenUpChainLapsesAndOnlyOnceItsWindowHasPassed()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 2, windowMs: 300_000);
        policy.Failed("xlr1");
        policy.Failed("mix:stream");
        policy.Failed("mix:stream");
        Assert.False(policy.Lapsed("mix:stream"));

        // A chain that failed fewer times than the limit was never off, so
        // the window passing has nothing to give back.
        now += 300_001;
        Assert.True(policy.Lapsed("mix:stream"));
        Assert.False(policy.Lapsed("xlr1"));
        Assert.False(policy.Lapsed("xlr2"));

        // Built again, it starts a new history.
        policy.Forget("mix:stream");
        Assert.False(policy.Lapsed("mix:stream"));
    }

    [Fact]
    public void ChangingAChainMakesItNewAgain()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 2, windowMs: 300_000);
        policy.Failed("xlr1");
        policy.Failed("xlr1");
        Assert.True(policy.Blocked("xlr1"));

        policy.Forget("xlr1");
        Assert.False(policy.Blocked("xlr1"));
    }

    [Fact]
    public void AGivenUpChainSaysWhatItsHostLastSaid()
    {
        long now = 0;
        var policy = new RestartPolicy(() => now, limit: 2, windowMs: 300_000);
        Assert.Equal(RestartPolicy.GivenUp, policy.Message("xlr1"));

        // A death with nothing said keeps the reason an earlier one gave, and
        // a later reason replaces it, also once the chain is already blocked.
        policy.Failed("xlr1", "the plugin asked to be reloaded; restart the chain");
        policy.Failed("xlr1");
        Assert.True(policy.Blocked("xlr1"));
        Assert.Equal(RestartPolicy.GivenUp + ". The plugin host's last message: the plugin asked to be reloaded; restart the chain",
            policy.Message("xlr1"));
        policy.Failed("xlr1", "  PipeWire: disconnected \n");
        Assert.True(policy.Blocked("xlr1"));
        Assert.EndsWith("last message: PipeWire: disconnected", policy.Message("xlr1"));

        // A plugin shares the helper's stderr, so the line is cut to size.
        policy.Failed("xlr1", new string('x', 4096));
        Assert.EndsWith(": " + new string('x', RestartPolicy.ReasonCap), policy.Message("xlr1"));

        // A chain with no reason says only that it failed, and a changed
        // chain forgets the old one's reason with the rest of its history.
        policy.Failed("mix:stream");
        policy.Failed("mix:stream", "   ");
        Assert.Equal(RestartPolicy.GivenUp, policy.Message("mix:stream"));
        policy.Forget("xlr1");
        Assert.Equal(RestartPolicy.GivenUp, policy.Message("xlr1"));
    }

    [Fact]
    public void AHostThatDiesAfterItIsReadyLeavesItsReasonAndItsTrace()
    {
        // The helper's last stderr line used to be read only when startup
        // failed, so one that quit later was replaced without a word. Its
        // trace lines reach the journal, bounded; a plugin's own do not.
        string python = $"""
            import sys
            for i in range({NativePluginHost.TraceLineBudget + 10}):
                sys.stderr.write('trace: line %d\n' % i)
            sys.stderr.write('a plugin writing for itself\n')
            sys.stderr.flush()
            print('ready', flush=True)
            sys.stdin.readline()
            sys.stderr.write('the plugin asked to be reloaded; restart the chain\n')
            sys.exit(1)
            """;
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = new NativePluginHost(new InsertDefinition { Id = "fixture", Kind = "lv2", Plugin = "urn:fixture" },
            "fixture_stage_0", 2, 48000, "/usr/bin/python3", ["-u", "-c", python], note: notes.Enqueue);
        var chain = new FilterHandle("chain", "chain", "chain", host.Process)
        {
            Stages = [new FilterHandle("fixture_stage_0", "fixture_stage_0", "fixture_stage_0", host.Process) { NativeHost = host }],
        };
        Assert.True(chain.IsAlive);
        Assert.Null(chain.LastWords);

        host.SetControl("gain", 1);   // any line lets the fixture go on to its exit
        Assert.True(host.Process.WaitForExit(5000));
        Assert.False(chain.IsAlive);
        const string reason = "the plugin asked to be reloaded; restart the chain";
        // Process exit does not join our asynchronous stderr reader. Its
        // production wait is bounded so a child holding stderr cannot stall
        // the sweep. Allow that reader to finish without weakening the reason.
        Assert.True(SpinWait.SpinUntil(() => chain.LastWords == reason, TimeSpan.FromSeconds(5)), chain.LastWords);
        Assert.Equal(reason, chain.LastWords);
        Assert.Equal(NativePluginHost.TraceLineBudget, notes.Count);
        Assert.Equal("Plugin host fixture_stage_0: trace: line 0", notes.First());
        Assert.All(notes, note => Assert.StartsWith("Plugin host fixture_stage_0: trace: line ", note));
    }
}
