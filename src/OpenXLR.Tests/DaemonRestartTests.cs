using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DaemonRestartTests
{
    [Fact]
    public async Task RestartIsAwaitedWithoutBlockingAndRepeatedClicksAreIgnored()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var model = new DaemonRestartViewModel(() => { calls++; return finish.Task; });
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Task<bool> restart = model.RestartAsync();
        Assert.False(restart.IsCompleted);
        Assert.False(model.CanRestart);
        Assert.Contains("Restarting", model.Status);
        Assert.False(await model.RestartAsync());
        Assert.Equal(1, calls);
        finish.SetResult(true);
        Assert.True(await restart);
        Assert.True(model.CanRestart);
        Assert.Contains("Service restarted", model.Status);
        Assert.Equal(2, changes.Count(name => name == nameof(model.CanRestart)));
    }

    [Fact]
    public async Task ReconnectClearsTheWaitingLine()
    {
        var model = new DaemonRestartViewModel(() => Task.FromResult(true));
        await model.RestartAsync();
        Assert.Contains("Waiting for the daemon connection", model.Status);
        model.ConnectionRestored();
        Assert.Null(model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureReenablesButtonAndAllowsRetry(bool throws)
    {
        int calls = 0;
        var model = new DaemonRestartViewModel(() =>
        {
            calls++;
            return throws ? Task.FromException<bool>(new IOException("unavailable")) : Task.FromResult(false);
        });
        Assert.False(await model.RestartAsync());
        Assert.True(model.CanRestart);
        Assert.Contains("Restart failed", model.Status);
        await model.RestartAsync();
        Assert.Equal(2, calls);
    }
}
