using System.Diagnostics;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task ServiceLogsAreBoundedWithoutStoppingTheServiceAndShutdownIsGraceful()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-service-test-").FullName;
        try
        {
            using var cancellation = new CancellationTokenSource();
            string ready = Path.Combine(dir, "ready"), stopped = Path.Combine(dir, "stopped"), log = Path.Combine(dir, "daemon.log");
            Task<int> service = ProcessRunner.RunServiceAsync("sh", ["-c",
                "trap 'printf stopped > \"$2\"; exit 0' TERM; head -c 2200000 /dev/zero; printf ready > \"$1\"; while :; do sleep 0.1; done",
                "service-test", ready, stopped], log, cancellation.Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
                Assert.False(service.IsCompleted);
                Assert.InRange(new FileInfo(log).Length, 1, 1024 * 1024);
                Assert.NotEmpty(File.ReadAllBytes(log));
            }
            finally { cancellation.Cancel(); }
            Assert.Equal(0, await service.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("stopped", File.ReadAllText(stopped));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(log));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OutputComesBackWholeWithTheExitCodeAndStderr()
    {
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "printf 'out'; printf 'err' >&2; exit 3"]);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("out", r.StdoutText);
        Assert.Equal("err", r.Stderr);
        Assert.False(r.TimedOut);
        Assert.False(r.Truncated);
        Assert.False(r.Ok);
        Assert.True((await ProcessRunner.RunAsync("true", [])).Ok);
    }

    [Fact]
    public async Task ARunawayOutputIsCappedAndTheProcessKilled()
    {
        var sw = Stopwatch.StartNew();
        // Would print for ever; the cap must end it, not the deadline.
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "yes"], TimeSpan.FromSeconds(20), stdoutCap: 256 * 1024);
        Assert.True(r.Truncated);
        Assert.False(r.TimedOut);
        Assert.Equal(256 * 1024, r.Stdout.Length);
        Assert.InRange(sw.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ADeadlineKillsTheWholeTree()
    {
        var sw = Stopwatch.StartNew();
        // A child of the shell; killing the shell alone would leave it running.
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "sleep 30 & wait"], TimeSpan.FromMilliseconds(400));
        Assert.True(r.TimedOut);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HelpersRunInTheCLocale()
    {
        ProcessResult r = await ProcessRunner.RunAsync("sh", ["-c", "printf '%s' \"$LC_ALL\""]);
        Assert.Equal("C", r.StdoutText);
        ProcessResult raw = await ProcessRunner.RunAsync("sh", ["-c", "printf '%s' \"${LC_ALL:-unset}\""], cLocale: false);
        Assert.Equal(Environment.GetEnvironmentVariable("LC_ALL") ?? "unset", raw.StdoutText);   // untouched without the flag
    }
}
