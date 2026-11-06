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
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true),
            () => { questions++; return Task.FromResult(accept); },
            (_, _) => { restarts++; return Task.FromResult(true); }, () => launches++, () => Assert.Fail("unexpected failure"));
        await prompt.CheckAsync("0.0.1", 42, () => true);
        await prompt.CheckAsync("0.0.1", 42, () => true);
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
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true),
            () => { questions++; return answer.Task; },
            (_, _) => { restarts++; return Task.FromResult(true); }, () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        Task first = prompt.CheckAsync("0.0.1", 42, () => current);
        await prompt.CheckAsync("0.0.1", 42, () => current);
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
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(true), () => Task.FromResult(true),
            (_, _) => throws ? Task.FromException<bool>(new IOException()) : Task.FromResult(false),
            () => Assert.Fail("relaunch"), () => failures++);
        await prompt.CheckAsync("0.0.1", 42, () => true);
        Assert.Equal(1, failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("999.0.0")]
    public async Task AnUnknownOrNewerVersionDoesNotClaimAQuestion(string? version)
    {
        var prompt = new DaemonUpgradePrompt((_, _) => throw new Exception("claim"),
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync(version, 42, () => true);
        await prompt.CheckAsync(AppVersion.Current, 42, () => true);
    }

    [Fact]
    public async Task AFailedClaimDoesNotAskOrRestart()
    {
        var prompt = new DaemonUpgradePrompt((_, _) => Task.FromResult(false),
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync("0.0.1", 42, () => true);
    }
    [Fact]
    public async Task AConnectionLostDuringTheClaimNeverShowsAStaleQuestion()
    {
        bool current = true;
        var prompt = new DaemonUpgradePrompt((_, _) => { current = false; return Task.FromResult(true); },
            () => throw new Exception("ask"), (_, _) => throw new Exception("restart"),
            () => Assert.Fail("relaunch"), () => Assert.Fail("failure"));
        await prompt.CheckAsync("0.0.1", 42, () => current);
    }

}
