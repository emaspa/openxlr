using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class DefaultDefenseTests
{
    [Fact]
    public async Task EveryPassReassertsADriftedDefaultAndNothingRunsAfterAStop()
    {
        var calls = new List<string>();
        string sink = "alsa_output.katana";
        string run(string[] args)
        {
            lock (calls) calls.Add(string.Join(' ', args));
            if (args[0] == "get-default-sink") return "OpenXLR_ch_system";   // WirePlumber moved it
            if (args[0] == "get-default-source") return "OpenXLR_mic";       // already right
            return "";
        }
        using var stop = new CancellationTokenSource();
        Task loop = DefaultDefense.RunAsync(sink, "OpenXLR_mic", run, [50, 50, 50, 50], stop.Token);
        await Task.Delay(140);
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        int passes;
        lock (calls) passes = calls.Count(c => c == "get-default-sink");
        Assert.InRange(passes, 1, 3);                                              // two passes fit in 140 ms, never all four
        lock (calls) Assert.Equal(passes, calls.Count(c => c == $"set-default-sink {sink}"));   // each pass repaired the sink
        lock (calls) Assert.DoesNotContain(calls, c => c.StartsWith("set-default-source"));   // the source was fine
        int after; lock (calls) after = calls.Count;
        await Task.Delay(200);
        lock (calls) Assert.Equal(after, calls.Count);                             // no pass after the stop
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
