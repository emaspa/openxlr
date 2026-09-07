using System.Diagnostics;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class NativeHostProtocolTests
{
    private static NativePluginHost Start(string script, TimeSpan? startupTimeout = null)
        => new(new InsertDefinition { Id = "test", Kind = "lv2", Plugin = "urn:test" },
            "test", 2, 48000, "/usr/bin/python3", ["-u", "-c", script], startupTimeout);

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
        Assert.ThrowsAny<OperationCanceledException>(() => host.ShowUi());
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
}
