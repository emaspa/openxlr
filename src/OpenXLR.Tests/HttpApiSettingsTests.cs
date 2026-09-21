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
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        vm.HttpApiEnabled = false;
        Assert.False(DaemonSettings.Load().HttpApiEnabled);
        Assert.False(DaemonSettings.Load().Submixer);
        Assert.Contains("Restart", vm.HttpApiNote);
        vm.Submixer = true;
        Assert.True(DaemonSettings.Load().Submixer);
        Assert.False(DaemonSettings.Load().HttpApiEnabled);
        vm.HttpApiEnabled = true;
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
