using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A plugin scan can take minutes. The commands that make inserts or recall
/// a scene answer while one runs, and two scans still run one at a time.
/// </summary>
public sealed partial class HistoryHubTests
{
    /// <summary>A catalogue scan that waits for the test, counting how many run at once.</summary>
    private sealed class HeldScan : IDisposable
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly ManualResetEventSlim _release = new();
        private int _running;

        public int Started;
        public int MostAtOnce;

        public void Run()
        {
            int running = Interlocked.Increment(ref _running);
            Interlocked.Increment(ref Started);
            InterlockedMax(ref MostAtOnce, running);
            _entered.Release();
            try { _release.Wait(TimeSpan.FromSeconds(30)); }
            finally { Interlocked.Decrement(ref _running); }
        }

        public bool WaitEntered(TimeSpan timeout) => _entered.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    [Fact]
    public async Task InsertProfileAndHistoryCommandsAnswerWhileAPluginScanRuns()
    {
        using var f = new Fixture();
        UsePlugins(f);
        using var scan = new HeldScan();
        f.Hub.Catalogue = () => [];
        f.Hub.RefreshCatalogue = scan.Run;
        await f.Ok("""{"cmd":"saveProfile","name":"before"}""");

        Task<ApiCommandResult> first = f.Hub.ExecuteForApiAsync("""{"cmd":"rescanPlugins"}""");
        Task<ApiCommandResult>? second = null;
        try
        {
            Assert.True(scan.WaitEntered(TimeSpan.FromSeconds(10)), "the rescan did not start");
            second = f.Hub.ExecuteForApiAsync("""{"cmd":"rescanPlugins"}""");

            // Every command below took the gate the scan held, and waited for it.
            await Answered(() => f.Ok(Chain("xlr1", Insert("comp", Comp))));
            await Answered(() => f.Ok("""{"cmd":"undo"}"""));
            await Answered(() => f.Ok("""{"cmd":"redo"}"""));
            Assert.Equal("comp", Assert.Single(Inserts(f, "xlr1")).Id);
            await Answered(() => f.Ok("""{"cmd":"setInputBinding","channel":"xlr1","source":"unplugged.mic","captureMonoChannel":2}"""));
            await Answered(() => f.Refused("""{"cmd":"setCaptureSource","channel":"xlr2","source":"unplugged.mic"}"""));
            await Answered(() => f.Ok("""{"cmd":"saveProfile","name":"during"}"""));
            await Answered(() => f.Ok("""{"cmd":"loadProfile","name":"before"}"""));
            Assert.Empty(Inserts(f, "xlr1"));
            await Answered(() => f.Ok("""{"cmd":"deleteProfile","name":"during"}"""));
            // With no interface connected the answer is a refusal, but an answer.
            await Answered(() => f.Refused("""{"cmd":"setRecallOnConnect","name":"before"}"""));

            // The second rescan waits for the first instead of scanning beside it.
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref scan.Started));
        }
        finally { scan.Release(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.True((await second!.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal((2, 1), (scan.Started, scan.MostAtOnce));
    }

    /// <summary>A command run off the test's thread, so one that waits for the scan fails the test instead of holding it.</summary>
    private static Task Answered(Func<Task> command) => Task.Run(command).WaitAsync(TimeSpan.FromSeconds(5));
}
