using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class CoalescedWorkQueueTests
{
    [Fact]
    public async Task SlowSnapshotNeverBlocksProducersAndBurstsBecomeOnePendingSnapshot()
    {
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var queue = new CoalescedWorkQueue(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            else second.TrySetResult();
        }, NullLogger.Instance, "Test work");
        Task pump = queue.RunAsync(stop.Token);
        try
        {
            queue.Signal();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) queue.Signal();
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref calls));
            release.Set();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            stop.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedSnapshotDoesNotStopLaterUpdates()
    {
        using var stop = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var queue = new CoalescedWorkQueue(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.SetResult();
                throw new InvalidOperationException("simulated snapshot failure");
            }
            next.SetResult();
        }, NullLogger.Instance, "Test work");
        Task pump = queue.RunAsync(stop.Token);
        try
        {
            queue.Signal();
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Signal();
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ARepeatedFailureCarriesItsTraceOnceAndRecoveryIsSaid()
    {
        using var stop = new CancellationTokenSource();
        var log = new RecordingLogger<CoalescedWorkQueueTests>();
        var calls = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        int call = 0;
        var queue = new CoalescedWorkQueue(() =>
        {
            int n = Interlocked.Increment(ref call) - 1;
            calls[n].SetResult();
            if (n < 3) throw new InvalidOperationException("read config block: Operation timed out");
        }, log, "Test work");
        Task pump = queue.RunAsync(stop.Token);
        try
        {
            foreach (var reached in calls)
            {
                queue.Signal();
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.True(SpinWait.SpinUntil(() => log.Entries.Any(e => e.Level == LogLevel.Information), TimeSpan.FromSeconds(5)));
        }
        finally
        {
            stop.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning.Exception);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Debug && e.Exception is null));
        Assert.Contains("recovered", Assert.Single(log.Entries, e => e.Level == LogLevel.Information).Message);
    }

    [Fact]
    public async Task IdlePumpStopsWithoutAnotherSignal()
    {
        using var stop = new CancellationTokenSource();
        var queue = new CoalescedWorkQueue(() => throw new Exception("unexpected"), NullLogger.Instance, "Test work");
        Task pump = queue.RunAsync(stop.Token);
        stop.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StopWaitsForCurrentWorkWithoutStartingPendingWork()
    {
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var queue = new CoalescedWorkQueue(() =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        }, NullLogger.Instance, "Test work");
        Task pump = queue.RunAsync(stop.Token);
        try
        {
            queue.Signal();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Signal();
            stop.Cancel();
            Assert.False(pump.IsCompleted);
        }
        finally
        {
            release.Set();
            stop.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, calls);
    }
}
