using System.Diagnostics;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class NativeHostProtocolTests
{
    private static NativePluginHost Start(string script, TimeSpan? startupTimeout = null,
        TimeSpan? patience = null)
        => new(new InsertDefinition { Id = "test", Kind = "lv2", Plugin = "urn:test" },
            "test", 2, 48000, "/usr/bin/python3", ["-u", "-c", script], startupTimeout, patience);

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
            """, patience: TimeSpan.FromMilliseconds(500));
        using var stalled = Start("""
            import sys
            print('ready')
            print('heartbeat')
            for line in sys.stdin:
                pass
            """, patience: TimeSpan.FromMilliseconds(500));

        var live = new FilterHandle("live", "sink", "source", beating.Process) { NativeHost = beating };
        var stuck = new FilterHandle("stuck", "sink", "source", stalled.Process) { NativeHost = stalled };

        bool noticed = false;
        for (int attempt = 0; attempt < 40 && !noticed; attempt++)
        {
            noticed = !stuck.IsAlive;
            if (!noticed) await Task.Delay(100);
        }
        Assert.True(noticed, "a stage whose helper stopped beating should read as dead");
        Assert.True(stalled.IsRunning, "and it is a live process, which is what made this invisible before");
        Assert.True(live.IsAlive, "a stage whose helper is still beating must not be rebuilt");
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
