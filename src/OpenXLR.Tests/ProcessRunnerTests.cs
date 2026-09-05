using System.Diagnostics;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class ProcessRunnerTests
{
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
