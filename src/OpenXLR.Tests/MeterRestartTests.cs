using System.Diagnostics;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// A meter whose capture process exits on its own (parec loses its sink or
/// its stream, or is killed from outside) starts a new one after a backoff,
/// instead of reading zero until the daemon restarts.
/// </summary>
public sealed class MeterRestartTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-meter-restart-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A reader that notes each start, then plays <paramref name="body"/>.</summary>
    private ProcessStartInfo Reader(string body)
    {
        string script = ExecutableScript.Write(Path.Combine(_directory, "reader"), $"""
            echo started >> "{_directory}/starts"
            {body}
            """);
        return new ProcessStartInfo(script) { RedirectStandardOutput = true, RedirectStandardError = true };
    }

    private int Starts()
        => File.Exists(Path.Combine(_directory, "starts")) ? File.ReadAllLines(Path.Combine(_directory, "starts")).Length : 0;

    [Fact]
    public void AReaderThatExitsIsStartedAgain()
    {
        using var meters = new MeterReader { RestartDelay = TimeSpan.FromMilliseconds(50), RestartSettle = TimeSpan.Zero };
        meters.Add("mix:stream", Reader("exit 0"));
        Assert.True(SpinWait.SpinUntil(() => Starts() >= 3, TimeSpan.FromSeconds(10)), $"started {Starts()} time(s)");
        // The meter stays listed throughout, reading what its readers bring.
        Assert.True(meters.Read().ContainsKey("mix:stream"));
    }

    [Fact]
    public void AReaderThatKeepsExitingAtOnceWaitsLongerEachTime()
    {
        // Waits of 100, 200, 400, 400 ms...: about five starts in 1.5 s,
        // where a fixed 100 ms would make fifteen.
        using var meters = new MeterReader
        {
            RestartDelay = TimeSpan.FromMilliseconds(100), RestartLimit = TimeSpan.FromMilliseconds(400),
            RestartSettle = TimeSpan.FromSeconds(30),
        };
        meters.Add("ch:music", Reader("exit 0"));
        Thread.Sleep(1500);
        int starts = Starts();
        Assert.InRange(starts, 3, 8);
    }

    [Fact]
    public void ARemovedMeterIsNotStartedAgain()
    {
        using var meters = new MeterReader { RestartDelay = TimeSpan.FromMilliseconds(50), RestartSettle = TimeSpan.Zero };
        meters.Add("mix:chat", Reader("exec sleep 30"));
        Assert.True(SpinWait.SpinUntil(() => Starts() == 1, TimeSpan.FromSeconds(5)));
        meters.Remove("mix:chat");
        Thread.Sleep(500);
        Assert.Equal(1, Starts());
        Assert.Null(meters.LastSound("mix:chat"));
    }

    [Fact]
    public void ARestartKeepsWhenTheMeterLastHeardSound()
    {
        // A reader that keeps being started must not make an output released
        // while idle look busy: the last sound carries over.
        using var meters = new MeterReader { RestartDelay = TimeSpan.FromMilliseconds(50), RestartSettle = TimeSpan.Zero };
        meters.Add("mix:monitor", Reader("exit 0"));
        long started = meters.LastSound("mix:monitor")!.Value;
        Assert.True(SpinWait.SpinUntil(() => Starts() >= 3, TimeSpan.FromSeconds(10)));
        Assert.Equal(started, meters.LastSound("mix:monitor"));
    }
}
