using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DaemonUpgradePromptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyRestartAcceptsAndTheQuestionIsNotRepeated(bool accept)
    {
        int questions = 0, restarts = 0, launches = 0;
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true), () => true,
            () => { questions++; return Task.FromResult(accept); },
            (_, _) => { restarts++; return Task.FromResult(true); }, () => launches++, () => Assert.Fail("unexpected failure"));
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
        Assert.Equal(1, questions);
        Assert.Equal(accept ? 1 : 0, restarts);
        Assert.Equal(restarts, launches);
    }

    [Fact]
    public async Task ConcurrentStatePushesAndAChangedConnectionCannotRestart()
    {
        var answer = new TaskCompletionSource<bool>();
        int questions = 0, restarts = 0;
        bool current = true;
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true), () => true,
            () => { questions++; return answer.Task; },
            (_, _) => { restarts++; return Task.FromResult(true); }, () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        Task first = prompt.CheckAsync("0.0.1", 42, 1, () => current, () => true);
        await prompt.CheckAsync("0.0.1", 42, 1, () => current, () => true);
        current = false;
        answer.SetResult(true);
        await first;
        Assert.Equal(1, questions);
        Assert.Equal(0, restarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRestartLeavesTheUiRunning(bool throws)
    {
        int failures = 0;
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true), () => true, () => Task.FromResult(true),
            (_, _) => throws ? Task.FromException<bool>(new IOException()) : Task.FromResult(false),
            () => Assert.Fail("relaunch"), () => failures++);
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("999.0.0")]
    public async Task AnUnknownOrNewerVersionDoesNotClaimAQuestion(string? version)
    {
        var prompt = new DaemonUpgradePrompt((_, _) => throw new Exception("probe"), () => throw new Exception("claim"),
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync(version, 42, 1, () => true, () => true);
        await prompt.CheckAsync(AppVersion.Current, 42, 1, () => true, () => true);
    }

    [Fact]
    public async Task AFailedClaimDoesNotAskOrRestart()
    {
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true), () => false,
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
    }
    [Fact]
    public async Task AConnectionLostDuringTheProbeNeverShowsAStaleQuestion()
    {
        bool current = true;
        var prompt = new DaemonUpgradePrompt((_, _) => { current = false; return Task.FromResult(true); }, () => throw new Exception("claim"),
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync("0.0.1", 42, 1, () => current, () => true);
    }

    [Fact]
    public async Task AnUnmanagedConnectionDoesNotSuppressTheQuestionForAServiceThatConnectsLater()
    {
        int probes = 0, questions = 0;
        var prompt = new DaemonUpgradePrompt((pid, _) =>
        {
            probes++;
            return Task.FromResult(pid == 43);
        }, () => true, () => { questions++; return Task.FromResult(false); },
            (_, _) => throw new Exception("restart"), () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));

        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => true);
        Assert.Equal(1, probes);
        Assert.Equal(0, questions);
        await prompt.CheckAsync("0.0.1", 43, 2, () => true, () => true);
        Assert.Equal(2, probes);
        Assert.Equal(1, questions);
    }

    [Fact]
    public async Task HidingTheMixerDuringTheProbeDoesNotConsumeTheQuestion()
    {
        bool visible = true;
        int claims = 0, questions = 0, probes = 0;
        var prompt = new DaemonUpgradePrompt((_, _) =>
        {
            if (++probes == 1) visible = false;
            return Task.FromResult(true);
        }, () => { claims++; return true; }, () => { questions++; return Task.FromResult(false); },
            (_, _) => throw new Exception("restart"), () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));

        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => visible);
        Assert.Equal(0, claims);
        Assert.Equal(0, questions);
        visible = true;
        await prompt.CheckAsync("0.0.1", 42, 1, () => true, () => visible);
        Assert.Equal(1, claims);
        Assert.Equal(1, questions);
    }

}
