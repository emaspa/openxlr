using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SubmixerRestartTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("openxlr-submixer-restart-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public SubmixerRestartTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_home, true);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ChangingTheMixerUsesTheSharedBackgroundRestart(bool succeeds, bool throws)
    {
        await using var client = new DaemonClient();
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var restart = new DaemonRestartViewModel(() => { calls++; return finish.Task; });
        var options = new OptionsViewModel(client, new MainViewModel(client, restart));
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(options.SubmixerNote)
                && options.SubmixerNote != Localizer.Text("RestartingDaemon")) applied.TrySetResult();
        };

        options.Submixer = false;
        Assert.False(restart.CanRestart);
        Assert.False(applied.Task.IsCompleted);
        Assert.Equal(Localizer.Text("RestartingDaemon"), options.SubmixerNote);
        Assert.False(DaemonPrefs.Load().Submixer);
        // Another toggle cannot save a preference the in-flight restart may miss.
        options.Submixer = true;
        Assert.False(options.Submixer);
        Assert.False(DaemonPrefs.Load().Submixer);
        Assert.False(await restart.RestartAsync());
        Assert.Equal(1, calls);

        if (throws) finish.SetException(new IOException("unavailable"));
        else finish.SetResult(succeeds);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(restart.CanRestart);
        Assert.Equal(Localizer.Text(succeeds ? "DaemonRestartedHardwareOnly" : "SavedRestartDaemonToApply"), options.SubmixerNote);
        options.Submixer = true;
        Assert.Equal(2, calls);
        Assert.True(DaemonPrefs.Load().Submixer);
    }

    [Fact]
    public async Task ADamagedPreferenceFileDoesNotRestartTheDaemon()
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        string path = Path.Combine(UiSettings.ConfigDir, "daemon.json");
        File.WriteAllText(path, "{");
        await using var client = new DaemonClient();
        int calls = 0;
        var restart = new DaemonRestartViewModel(() => { calls++; return Task.FromResult(true); });
        var options = new OptionsViewModel(client, new MainViewModel(client, restart));
        options.Submixer = false;
        Assert.Equal(0, calls);
        Assert.NotNull(options.PreferenceError);
        Assert.Equal("{", File.ReadAllText(path));
    }
}
