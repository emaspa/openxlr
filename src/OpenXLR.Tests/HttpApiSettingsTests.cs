using Microsoft.AspNetCore.Http;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class HttpApiSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-api-settings-").FullName;
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");

    public HttpApiSettingsTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        ExecutableScript.Write(Path.Combine(_directory, "systemctl"), "exit 1");
        Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _oldPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Environment.SetEnvironmentVariable("PATH", _oldPath);
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task SettingsKeepDefaultsOtherPreferencesAndReportRestartFailure()
    {
        Assert.True(DaemonSettings.Load().HttpApiEnabled);
        Assert.True(DaemonPrefs.Load().HttpApiEnabled);
        new DaemonSettings { Submixer = false }.Save();
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        var vm = new OptionsViewModel(client, main);
        vm.HttpApiEnabled = false;
        await WaitUntilAsync(() => main.DaemonRestart.CanRestart && vm.HttpApiNote == "Saved. Restart the audio service to apply the setting.");
        Assert.False(DaemonSettings.Load().HttpApiEnabled);
        Assert.False(DaemonSettings.Load().Submixer);
        Assert.Contains("Restart", vm.HttpApiNote);
        vm.Submixer = true;
        await WaitUntilAsync(() => main.DaemonRestart.CanRestart && vm.SubmixerNote == "Saved. Restart the audio service to apply the setting.");
        Assert.True(DaemonSettings.Load().Submixer);
        Assert.False(DaemonSettings.Load().HttpApiEnabled);
        vm.HttpApiEnabled = true;
        await WaitUntilAsync(() => main.DaemonRestart.CanRestart && vm.HttpApiNote == "Saved. Restart the audio service to apply the setting.");
        Assert.True(DaemonSettings.Load().HttpApiEnabled);
        Assert.True(DaemonSettings.Load().Submixer);
    }

    [Fact]
    public async Task FailedSaveKeepsTheDisplayedChoice()
    {
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        Directory.CreateDirectory(Path.Combine(_directory, "openxlr", "daemon.json"));
        vm.HttpApiEnabled = false;
        Assert.True(vm.HttpApiEnabled);
        Assert.Contains("Could not save", vm.HttpApiNote);
        vm.Submixer = false;
        Assert.True(vm.Submixer);
        Assert.Contains("Could not save", vm.SubmixerNote);
    }

    [Fact]
    public async Task SettingsRestartDoesNotBlockOrRaceTheSharedRestartButtons()
    {
        string started = Path.Combine(_directory, "started");
        string release = Path.Combine(_directory, "release");
        ExecutableScript.Write(Path.Combine(_directory, "systemctl"), """
            touch "${0%/*}/started"
            while [ ! -e "${0%/*}/release" ]; do sleep 0.01; done
            exit 0
            """);
        await using var client = new DaemonClient();
        var main = new MainViewModel(client);
        var vm = new OptionsViewModel(client, main);
        Task change = Task.Run(() => vm.HttpApiEnabled = false);
        try
        {
            await WaitUntilAsync(() => File.Exists(started));
            await change.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(main.DaemonRestart.CanRestart);
            // Every control uses the same restart guard, even if invoked directly.
            vm.HttpApiEnabled = true;
            vm.Submixer = false;
            await main.DaemonRestart.RestartAsync();
            Assert.False(vm.HttpApiEnabled);
            Assert.True(vm.Submixer);
            Assert.False(DaemonSettings.Load().HttpApiEnabled);
            Assert.Null(DaemonSettings.Load().Submixer);
        }
        finally
        {
            File.WriteAllText(release, "release");
            await change;
            await WaitUntilAsync(() => main.DaemonRestart.CanRestart);
        }
        await WaitUntilAsync(() => vm.HttpApiNote?.Contains("restarted", StringComparison.Ordinal) == true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData("channels", "music", 200)]
    [InlineData("channels", "Music", 404)]
    [InlineData("mixes", "monitor", 200)]
    [InlineData("mixes", "missing", 404)]
    [InlineData("inserts", "music", 200)]
    [InlineData("inserts", "missing", 404)]
    [InlineData("channels", null, 200)]
    [InlineData("mixes", null, 200)]
    [InlineData("inserts", null, 200)]
    [InlineData("mixer", null, 200)]
    public void ResourcesUseExactExistingIdsAndRetainEmptyChains(string resource, string? id, int status)
    {
        var mixer = new MixerState
        {
            Channels = [new("music", "Music", new Dictionary<string, double>(), [])],
            Mixes = [new("monitor", "Monitor A", 1, false)],
            Inserts = new Dictionary<string, IReadOnlyList<InsertStatus>> { ["music"] = [] },
        };
        var result = ApiEndpoints.MixerResource(mixer, resource, id);
        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
        Assert.Equal(503, ((IStatusCodeHttpResult)ApiEndpoints.MixerResource(null, resource, id)).StatusCode);
    }
}
