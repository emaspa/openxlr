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

    [Fact]
    public async Task RuntimeWarningsRefreshIndependentlyAndDisappearWhenSetupIsUnavailable()
    {
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        vm.ApplyPluginSetup(JsonNode.Parse("""
            {"windowsEditorNote":"Install the companion","memoryLockNote":"Configure memory locking"}
            """));
        Assert.True(vm.HasWindowsEditorNote);
        Assert.True(vm.HasMemoryLockNote);
        Assert.Equal("Configure memory locking", vm.MemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"memoryLockNote":"Configure memory locking"}"""));
        Assert.False(vm.HasWindowsEditorNote);
        Assert.True(vm.HasMemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("{}"));
        Assert.False(vm.HasMemoryLockNote);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"memoryLockNote":"Old warning"}"""));
        vm.ApplyPluginSetup(null);
        Assert.False(vm.HasMemoryLockNote);
        Assert.False(vm.HasWindowsEditorNote);
    }

    [Fact]
    public async Task SkippedBundlesReachOptionsThroughTheSetupProtocolAndClearOnRefresh()
    {
        await using var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        var setup = new OpenXLR.Core.Mixing.PluginSetup(true, "~/.lv2", "~/.clap", "~/.vst3", null, false, [], [])
        {
            SkippedScans = new(2, [new("vst3", "/plugins/Timeout.vst3", "timeout", "timed out", DateTimeOffset.Parse("2026-09-17T10:00:00Z")),
                new("clap", "/plugins/Old.clap", "unknown", "reason not recorded", null)]),
        };
        vm.ApplyPluginSetup(System.Text.Json.JsonSerializer.SerializeToNode(new PluginSetupMessage(setup),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Equal("Skipped after a failed scan: 2", vm.SkippedPlugins);
        Assert.Equal(2, vm.SkippedPluginDetails.Count);
        Assert.Contains("/plugins/Timeout.vst3", vm.SkippedPluginDetails[0]);
        Assert.Contains("timed out", vm.SkippedPluginDetails[0]);
        Assert.DoesNotContain("time not recorded", vm.SkippedPluginDetails[0]);
        Assert.Contains("time not recorded", vm.SkippedPluginDetails[1]);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"skippedFailedCount":0,"skippedFailedBundles":[]}"""));
        Assert.Empty(vm.SkippedPluginDetails);
        Assert.Equal("Skipped after a failed scan: 0", vm.SkippedPlugins);
        vm.ApplyPluginSetup(JsonNode.Parse("{}"));
        Assert.Equal("Skipped bundles: unavailable", vm.SkippedPlugins);
        vm.ApplyPluginSetup(null);
        Assert.Empty(vm.SkippedPluginDetails);
    }

    [Fact]
    public void PluginFileReplyUsesTheFieldsTheManagerReads()
    {
        var result = new OpenXLR.Core.Mixing.WindowsPluginFiles(true, "", [
            new("/wine/EQ.vst3", "EQ.vst3", "vst3", false, false, "/wine", true),
        ]);
        string wire = System.Text.Json.JsonSerializer.Serialize(new WindowsPluginFilesMessage(result),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        JsonNode reply = JsonNode.Parse(wire)!;
        Assert.Equal("windowsPluginFiles", reply["type"]!.GetValue<string>());
        Assert.True(reply["ok"]!.GetValue<bool>());
        JsonNode row = reply["plugins"]![0]!;
        Assert.Equal("/wine/EQ.vst3", row["path"]!.GetValue<string>());
        Assert.False(row["enabled"]!.GetValue<bool>());
        Assert.False(row["canDelete"]!.GetValue<bool>());
        Assert.Equal("/wine", row["winePrefix"]!.GetValue<string>());
        Assert.True(row["inUse"]!.GetValue<bool>());
        Assert.Null(reply["result"]);
    }

    [Theory]
    [InlineData("addWindowsPluginFolder")]
    [InlineData("removeWindowsPluginFolder")]
    [InlineData("getWindowsPluginFiles")]
    [InlineData("removeWindowsPluginInserts")]
    [InlineData("setWindowsPluginEnabled")]
    [InlineData("deleteWindowsPlugin")]
    public void FolderCommandsRequireBoundedAbsolutePaths(string command)
    {
        foreach (string? path in new[] { null, "", "plugins", "~/plugins", "/plugins\nother", "/" + new string('x', 4096) })
            Assert.Contains(command, CommandValidation.CheckPluginPath(new Command { Cmd = command, Path = path }));
        Assert.Null(CommandValidation.CheckPluginPath(new Command { Cmd = command, Path = "/home/user/Windows plugins" }));
    }

    [Fact]
    public void OptionsShowsTheYabridgeControlsOnlyWhereSomethingUsesYabridge()
    {
        var client = new DaemonClient();
        var vm = new OptionsViewModel(client, new MainViewModel(client));
        const string companion = """
            "yabridge":"5.1.1.54-2","wine":true,"bridgeProvider":"openxlr",
            "windowsPluginDirectory":"~/.local/share/openxlr/yabridge","windowsDirectories":["/a","/b"],
            "wineFolders":["/w/VST3"],"windowsEditorNote":"editor","memoryLockNote":"memory lock"
            """;
        string Setup(string fields) => "{" + companion + ",\"windowsHostInstalled\":true," + fields + "}";

        // Installed, and nothing uses it.
        vm.ApplyPluginSetup(JsonNode.Parse(Setup("\"windowsHostDefault\":\"openxlr\",\"yabridgeInserts\":0")));
        Assert.False(vm.ShowsYabridgeTools);
        Assert.False(vm.CanBridgeWine);
        Assert.False(vm.HasWindowsEditorNote);
        Assert.False(vm.HasMemoryLockNote);
        Assert.DoesNotContain("wrappers", vm.PluginDirectories);
        Assert.EndsWith("yabridge 5.1.1.54-2 is installed. No insert uses it.", vm.WindowsPlugins);
        Assert.DoesNotContain("bridged", vm.WindowsPlugins);
        // There is still a second host to choose, with what the choice does in its tooltip.
        Assert.True(vm.ShowsWindowsHostDefault);

        // An insert on it, or the default on it, brings the controls back,
        // for the folders yabridge already knows. A new folder goes to the
        // host, so there is nothing to add to yabridge and no Wine folder to
        // give it.
        foreach (string fields in new[] { "\"windowsHostDefault\":\"openxlr\",\"yabridgeInserts\":2", "\"windowsHostDefault\":\"yabridge\",\"yabridgeInserts\":0" })
        {
            vm.ApplyPluginSetup(JsonNode.Parse(Setup(fields)));
            Assert.True(vm.ShowsYabridgeTools);
            Assert.False(vm.AddsYabridgeFolders);
            Assert.False(vm.CanBridgeWine);
            Assert.True(vm.HasWindowsEditorNote);
            Assert.True(vm.HasMemoryLockNote);
            Assert.Contains("~/.local/share/openxlr/yabridge", vm.PluginDirectories);
            Assert.Contains("OpenXLR bridge 5.1.1.54-2 (64-bit) and Wine are installed, 2 folders bridged.", vm.WindowsPlugins);
            Assert.True(vm.ShowsWindowsHostDefault);
        }

        // No yabridge at all: nothing of it, and nothing to choose between.
        vm.ApplyPluginSetup(JsonNode.Parse("""{"wine":true,"windowsHostInstalled":true,"windowsHostDefault":"openxlr","memoryLockNote":"memory lock"}"""));
        Assert.False(vm.ShowsYabridgeTools);
        Assert.False(vm.ShowsWindowsHostDefault);
        Assert.False(vm.HasMemoryLockNote);
        Assert.DoesNotContain("yabridge", vm.WindowsPlugins);
        // Unless the default is still yabridge, which the picker has to be able to put back.
        vm.ApplyPluginSetup(JsonNode.Parse("""{"wine":true,"windowsHostInstalled":true,"windowsHostDefault":"yabridge"}"""));
        Assert.True(vm.ShowsWindowsHostDefault);
        Assert.False(vm.ShowsYabridgeTools);

        // A build without the host keeps every yabridge control, used or not.
        vm.ApplyPluginSetup(JsonNode.Parse("{" + companion + ",\"windowsHostInstalled\":false,\"windowsHostDefault\":\"openxlr\"}"));
        Assert.True(vm.ShowsYabridgeTools);
        Assert.True(vm.AddsYabridgeFolders);
        Assert.True(vm.CanBridgeWine);
        Assert.True(vm.HasMemoryLockNote);
        Assert.False(vm.ShowsWindowsHostDefault);
        vm.ApplyPluginSetup(JsonNode.Parse("""{"wine":true,"windowsHostInstalled":false}"""));
        Assert.True(vm.ShowsYabridgeTools);
    }
}
