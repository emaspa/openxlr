using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class DefaultDefenseTests
{
    [Fact]
    public async Task EveryPassReassertsADriftedDefaultAndNothingRunsAfterAStop()
    {
        // The first pass repairs the sink. The second pass is held inside its
        // first helper call while the stop arrives; its repair must not run.
        var calls = new List<string>();
        string sink = "alsa_output.katana";
        int gets = 0;
        var secondPassInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string run(string[] args)
        {
            lock (calls) calls.Add(string.Join(' ', args));
            if (args[0] == "get-default-sink")
            {
                if (Interlocked.Increment(ref gets) == 2) { secondPassInside.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
                return "OpenXLR_ch_system";   // WirePlumber moved it
            }
            if (args[0] == "get-default-source") return "OpenXLR_mic";   // already right
            return "";
        }
        using var stop = new CancellationTokenSource();
        Task loop = DefaultDefense.RunAsync(sink, "OpenXLR_mic", run, [10, 10, 10, 10], stop.Token);
        await secondPassInside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        release.Set();
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        lock (calls)
        {
            Assert.Equal(2, calls.Count(c => c == "get-default-sink"));                     // two passes started, never all four
            Assert.Equal(1, calls.Count(c => c == $"set-default-sink {sink}"));             // only the first pass repaired
            Assert.DoesNotContain(calls, c => c.StartsWith("set-default-source"));          // the source was fine
        }
        int after; lock (calls) after = calls.Count;
        await Task.Delay(100);
        lock (calls) Assert.Equal(after, calls.Count);                                     // no pass after the stop
    }

    [Fact]
    public async Task AFailingCommandIsReportedAndTheLoopGoesOn()
    {
        var errors = new List<string>();
        int n = 0;
        Task loop = DefaultDefense.RunAsync("sink", null, _ => { n++; throw new InvalidOperationException("pactl gone"); },
            [10, 10], CancellationToken.None, errors.Add);
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, n);
        Assert.Equal(["pactl gone", "pactl gone"], errors);
    }
}
