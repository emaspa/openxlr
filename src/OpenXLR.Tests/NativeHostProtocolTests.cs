using System.Diagnostics;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class NativeHostProtocolTests
{
    private static NativePluginHost Start(string script, TimeSpan? startupTimeout = null,
        TimeSpan? patience = null, IReadOnlySet<string>? meters = null)
        => new(new InsertDefinition { Id = "test", Kind = "lv2", Plugin = "urn:test" },
            "test", 2, 48000, "/usr/bin/python3", ["-u", "-c", script], startupTimeout, patience,
            meterSymbols: meters);

    [Fact]
    public void FakeHelperCoversReadyControlMeterAndUiReplies()
    {
        using var host = Start("""
            import sys
            print('ready')
            for line in sys.stdin:
                if line.startswith('set '):
                    _, symbol, value = line.split()
                    print('control', symbol, value)
                    print('meter peak 0.25')
                elif line.strip() == 'show':
                    print('heartbeat')
                    print('ui-heartbeat')
                    print('ui opened')
            """);
        host.SetControl("gain", 0.37);
        host.ShowUi(); // output before the reply has been consumed by the same reader
        Assert.True(host.IsHealthy);
        Assert.Equal(0.25, host.Meters["peak"]);
        Assert.Equal(0.37, host.DrainChanges().Single().Value);
        Assert.Empty(host.DrainChanges());
    }

    [Fact]
    public void LiveLatencyReportsReplaceOldValuesAndRejectAChangedSampleRate()
    {
        using var host = Start("""
            import sys
            print('ready')
            print('latency 480 48000')
            count = 0
            for line in sys.stdin:
                if line.strip() == 'show':
                    count += 1
                    if count == 2: print('latency 960 48000')
                    if count == 3: print('latency 960 44100')
                    if count == 4: print('latency 0 48000')
                    print('ui opened')
            """);
        host.ShowUi(); Assert.Equal(10, host.LatencyMilliseconds);
        host.ShowUi(); Assert.Equal(20, host.LatencyMilliseconds);
        host.ShowUi(); Assert.Null(host.LatencyMilliseconds);
        host.ShowUi(); Assert.Equal(0, host.LatencyMilliseconds);
    }

    [Fact]
    public void APartialProtocolLineHasAFixedBoundAndRecoversAtTheNextNewline()
    {
        var line = new System.Text.StringBuilder();
        bool discard = false;
        var received = new List<string>();
        string block = new('x', 4096);
        NativePluginHost.FoldOutputBlock(line, block, ref discard, received.Add);
        Assert.Equal(4096, line.Length);
        NativePluginHost.FoldOutputBlock(line, "\n", ref discard, received.Add);
        Assert.Equal(block, Assert.Single(received));
        received.Clear();
        for (int i = 0; i < 100; i++)
        {
            NativePluginHost.FoldOutputBlock(line, block, ref discard, received.Add);
            Assert.InRange(line.Length, 0, 4096);
        }
        Assert.True(discard);
        NativePluginHost.FoldOutputBlock(line, "\nready\r", ref discard, received.Add);
        NativePluginHost.FoldOutputBlock(line, "\nheartbeat\nui opened\n", ref discard, received.Add);
        Assert.Equal(["ready", "heartbeat", "ui opened"], received);
        Assert.False(discard);
        Assert.Empty(line.ToString());
    }

    [Fact]
    public void PluginOutputCannotGrowTheControlAndMeterTablesWithoutBound()
    {
        using var host = Start("""
            import sys
            print('ready')
            print('control gain 0.25')
            print('meter peak 0.25')
            for i in range(5000):
                print('control c%d 0.5' % i)
                print('meter m%d 0.5' % i)
            print('meter rms 0.5')
            for line in sys.stdin:
                if line.strip() == 'show':
                    print('control gain 0.75')
                    print('meter peak 0.75')
                    print('ui opened')
                elif line.startswith('set '):
                    _, symbol, value = line.split()
                    print('control', symbol, value)
            """, meters: new HashSet<string>(["peak", "rms"], StringComparer.Ordinal));
        host.ShowUi();
        // Five thousand names the catalogue never declared take no slot, so
        // a meter first reported after the flood still lands. Without the
        // filter, "rms" arrives at a full table and is refused.
        Assert.Equal(2, host.Meters.Count);
        Assert.Equal(0.75, host.Meters["peak"]);
        Assert.Equal(0.5, host.Meters["rms"]);
        var changes = host.DrainChanges().ToArray();
        Assert.Equal(4096, changes.Length);
        Assert.Equal(0.75, changes.Single(pair => pair.Key == "gain").Value);
        host.SetControl("after-drain", 0.9);
        host.ShowUi();
        Assert.Equal(0.9, host.DrainChanges().Single(pair => pair.Key == "after-drain").Value);
    }

    [Fact]
    public void MaximumLengthSymbolsWorkAndNonFiniteOrEmptyValuesAreIgnored()
    {
        using var host = Start("""
            import sys
            print('ready')
            for line in sys.stdin:
                if line.strip() == 'show':
                    for kind in ['control', 'meter']:
                        print(kind, 's' * 255, '0.75')
                        for value in ['NaN', 'Infinity', '-Infinity', '1e999', '']:
                            print(kind, 'invalid', value)
                        print(kind + '  0.5')
                    print('ui opened')
            """);
        host.ShowUi();
        var control = Assert.Single(host.DrainChanges());
        var meter = Assert.Single(host.Meters);
        Assert.Equal(new string('s', 255), control.Key);
        Assert.Equal(control, meter);
        Assert.Equal(0.75, control.Value);
    }

    [Fact]
    public void OversizedProtocolLinesAndSymbolsAreDiscardedWithoutLosingTheNextReply()
    {
        using var host = Start("""
            import sys
            print('ready')
            print('control poisoned 0.5' + ' ' * 16384)
            print('meter poisoned 0.5' + ' ' * 16384)
            print('control ' + 's' * 256 + ' 0.5')
            print('meter ' + 's' * 256 + ' 0.5')
            print('x' * 16384, end='')
            print('ready')
            for line in sys.stdin:
                if line.strip() == 'show':
                    print('control gain 0.75')
                    print('meter peak 0.5')
                    print('heartbeat')
                    print('ui-heartbeat')
                    print('ui opened')
            """);
        host.ShowUi();
        Assert.Equal("peak", Assert.Single(host.Meters).Key);
        Assert.Equal("gain", Assert.Single(host.DrainChanges()).Key);
        Assert.True(host.IsHealthy);
        Assert.False(host.EditorStalled);
    }

    [Fact]
    public void UnresponsiveEditorHasOneSecondBudgetAndCannotQueueMoreShows()
    {
        using var host = Start("""
            import sys
            print('ready')
            for line in sys.stdin:
                if line.strip() == 'show':
                    print('heartbeat')
            """);
        var elapsed = Stopwatch.StartNew();
        Assert.Contains("did not answer in time",
            Assert.Throws<InvalidOperationException>(() => host.ShowUi()).Message);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 0.8, 2.5);
        Assert.Contains("already opening", Assert.Throws<InvalidOperationException>(() => host.ShowUi()).Message);
        Assert.True(host.IsHealthy); // editor timeout must not kill working DSP
    }

    [Fact]
    public void MissingReadyFailsWithinStartupDeadline()
    {
        var elapsed = Stopwatch.StartNew();
        Assert.Throws<InvalidOperationException>(() => Start("import time; time.sleep(60)", TimeSpan.FromMilliseconds(150)));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 0.1, 3);
    }

    [Fact]
    public async Task AHelperThatStopsBeatingIsUnhealthyWhileItIsStillAlive()
    {
        // The helper stops beating once the plugin has been inside one audio
        // callback for several seconds. It is still a live process with a
        // turning editor loop, so nothing else would notice; this is what
        // tells the chain healing in the sweep to replace it.
        using var host = Start("""
            import sys
            print('ready')
            print('heartbeat')
            print('ui-heartbeat')
            for line in sys.stdin:
                pass
            """, patience: TimeSpan.FromMilliseconds(500));

        bool noticed = false;
        for (int attempt = 0; attempt < 40 && !noticed; attempt++)
        {
            noticed = host.IsRunning && !host.IsHealthy;
            if (!noticed) await Task.Delay(100);
        }
        Assert.True(noticed, "a live helper that stopped beating should read as unhealthy");
    }

    [Fact]
    public async Task AStalledHostMakesItsChainStageReadAsDead()
    {
        // The step that turns a silent helper into a replaced one: the chain
        // stage carrying it stops reading as alive, which is what the sweep's
        // healing pass looks at. A stage whose helper is beating stays alive,
        // so a working plugin is never rebuilt underneath the user.
        using var beating = Start("""
            import sys, threading, time
            lock = threading.Lock()
            def say(line):
                with lock:
                    sys.stdout.write(line + '\n')
                    sys.stdout.flush()
            say('ready')
            def beat():
                while True:
                    say('heartbeat')
                    time.sleep(0.05)
            threading.Thread(target=beat, daemon=True).start()
            for line in sys.stdin:
                pass
            """, patience: TimeSpan.FromSeconds(5));
        using var stalled = Start("""
            import sys
            print('ready')
            print('heartbeat')
            for line in sys.stdin:
                pass
            """, patience: TimeSpan.FromMilliseconds(500));

        var live = new FilterHandle("live", "sink", "source", beating.Process) { NativeHost = beating };
        var stuck = new FilterHandle("stuck", "sink", "source", stalled.Process) { NativeHost = stalled };

        // The beating helper gets five seconds of patience against its 50 ms
        // beat, and the stalled one half a second. A loaded runner can pause a
        // helper and its output reader together, and with the same short
        // patience on both, the live stage read as dead for a sample now and
        // then, which is not what this test is about. What it is about is that
        // a beating stage is never rebuilt, so that holds on every sample.
        bool noticed = false;
        for (int attempt = 0; attempt < 40 && !noticed; attempt++)
        {
            Assert.True(live.IsAlive, "a stage whose helper is still beating must not be rebuilt");
            noticed = !stuck.IsAlive;
            if (!noticed) await Task.Delay(100);
        }
        Assert.True(noticed, "a stage whose helper stopped beating should read as dead");
        Assert.True(live.IsAlive, "a stage whose helper is still beating must not be rebuilt");
        Assert.True(stalled.IsRunning, "and it is a live process, which is what made this invisible before");
    }

    [Fact]
    public async Task AFrozenEditorLoopIsReportedWithoutCondemningTheAudio()
    {
        // The helper's two beats mean different things: one says the process
        // lives, the other that its editor loop is still turning. A plugin's
        // interface can block the second without touching the first.
        // One lock around stdout: unbuffered print writes the text and the
        // newline separately, so two threads can otherwise split a line.
        using var host = Start("""
            import sys, threading, time
            lock = threading.Lock()
            def say(line):
                with lock:
                    sys.stdout.write(line + '\n')
                    sys.stdout.flush()
            say('ready')
            def beat():
                while True:
                    say('heartbeat')
                    time.sleep(0.1)
            threading.Thread(target=beat, daemon=True).start()
            for line in sys.stdin:
                pass
            """, patience: TimeSpan.FromSeconds(1));

        bool bothTrue = false;
        for (int attempt = 0; attempt < 60 && !bothTrue; attempt++)
        {
            bothTrue = host.IsHealthy && host.EditorStalled;
            if (!bothTrue) await Task.Delay(100);
        }
        Assert.True(bothTrue, "a beating process whose editor loop is frozen should read as both");
        Assert.Contains("unresponsive", Assert.Throws<InvalidOperationException>(() => host.ShowUi()).Message);
    }
}
