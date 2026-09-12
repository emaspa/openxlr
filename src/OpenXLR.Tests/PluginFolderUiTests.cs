using System.Text.Json.Nodes;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginFolderUiTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "openxlr-folders-ui-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public PluginFolderUiTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    [Fact]
    public void SetupRefreshReplacesFolderListAndDistinguishesSharedConfiguration()
    {
        var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"yabridge":"5.1.1","wine":true,"bridgeProvider":"system","windowsImportDirectory":"/imports",
             "windowsDirectories":["/plugins/z","/plugins/a","/plugins/a"]}
            """));
        Assert.Equal(new[] { "/plugins/a", "/plugins/z" }, vm.WindowsDirectories);
        Assert.True(vm.HasWindowsDirectories);
        Assert.True(vm.CanManageWindows);
        Assert.True(vm.CanSyncWindows);
        Assert.True(vm.SystemBridge);
        Assert.Contains("/imports", vm.WindowsImportNote);

        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"yabridge":"5.1.1","wine":false,"bridgeProvider":"openxlr","windowsDirectories":[]}
            """));
        Assert.Empty(vm.WindowsDirectories);
        Assert.False(vm.HasWindowsDirectories);
        Assert.True(vm.CanManageWindows);
        Assert.False(vm.CanSyncWindows);
        Assert.False(vm.SystemBridge);

        vm.ApplyPluginSetup(null);
        Assert.False(vm.CanManageWindows);
        Assert.False(vm.CanSyncWindows);
    }

    [Theory]
    [InlineData("addWindowsPluginFolder")]
    [InlineData("removeWindowsPluginFolder")]
    public void FolderCommandsRequireBoundedAbsolutePaths(string command)
    {
        foreach (string? path in new[] { null, "", "plugins", "~/plugins", "/plugins\nother", "/" + new string('x', 4096) })
            Assert.Contains(command, CommandValidation.CheckPluginFolderPath(new Command { Cmd = command, Path = path }));
        Assert.Null(CommandValidation.CheckPluginFolderPath(new Command { Cmd = command, Path = "/home/user/Windows plugins" }));
    }
}
