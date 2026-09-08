using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ManagedYabridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _old = new();
    private string Package => Path.Combine(_root, "package");

    public ManagedYabridgeTests()
    {
        Directory.CreateDirectory(Package);
        foreach (string file in ManagedYabridge.RequiredFiles) File.WriteAllText(Path.Combine(Package, file), "fixture");
        File.WriteAllText(Path.Combine(Package, "openxlr-yabridge.json"), JsonSerializer.Serialize(new
        {
            formatVersion = 1, version = "5.1.1.54", sourceCommit = new string('a', 40), wineInputFix = true,
        }));
    }

    private void Env(string name, string value)
    {
        if (!_old.ContainsKey(name)) _old[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private void Select()
    {
        Env("OPENXLR_YABRIDGE", Package);
        Env("XDG_CONFIG_HOME", Path.Combine(_root, "config"));
        Env("XDG_DATA_HOME", Path.Combine(_root, "data"));
        Env("WINEPREFIX", Path.Combine(_root, "wine"));
    }

    [Fact]
    public void CompletePackageIsSelectedAndSystemOptOutIsRespected()
    {
        Assert.Equal(Package, ManagedYabridge.Find(null, [Package])?.Directory);
        Assert.Null(ManagedYabridge.Find("system", [Package]));
        Assert.Null(ManagedYabridge.Find("/missing/package", [Package]));
        File.Delete(Path.Combine(Package, "libyabridge-vst3.so"));
        Assert.Null(ManagedYabridge.Find(null, [Package]));
    }

    [Fact]
    public void InvalidReceiptDoesNotClaimTheInputFix()
    {
        File.WriteAllText(Path.Combine(Package, "openxlr-yabridge.json"), "{}");
        Assert.Null(ManagedYabridge.Find(null, [Package]));
    }

    [Fact]
    public void BridgeUpdatesAndLocationChangesInvalidateScanCache()
    {
        var bridge = new ManagedYabridge(Package, "5.1.1.54-1", new string('a', 40));
        Assert.NotEqual(bridge.CacheKey, (bridge with { Version = "5.1.1.54-2" }).CacheKey);
        Assert.NotEqual(bridge.CacheKey, (bridge with { Directory = Package + "-other" }).CacheKey);
        Assert.NotEqual(bridge.CacheKey, (bridge with { SourceCommit = new string('b', 40) }).CacheKey);
    }

    [Fact]
    public void HostGetsMatchingLibrariesWithoutRedirectingPluginSettings()
    {
        Select();
        ManagedYabridge bridge = Assert.IsType<ManagedYabridge>(ManagedYabridge.Discover());
        var host = bridge.HostEnvironment();
        Assert.StartsWith(Package + ":", host["PATH"]);
        Assert.False(host.ContainsKey("HOME"));
        Assert.False(host.ContainsKey("WINEPREFIX"));
        Assert.False(host.ContainsKey("XDG_CONFIG_HOME"));
        Assert.Equal(Path.Combine(_root, "config", "openxlr", "bridge"), bridge.ControllerEnvironment()["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine(_root, "data", "openxlr", "yabridge"), bridge.ControllerEnvironment()["OPENXLR_YABRIDGE_PLUGIN_HOME"]);
    }

    [Fact]
    public void PrivateWrappersPrecedeSystemCopiesEvenWithConfiguredSearchPaths()
    {
        Select();
        Env("CLAP_PATH", "/user/clap:/system/clap");
        Env("VST3_PATH", "/user/vst3:/system/vst3");
        Assert.Equal([Path.Combine(ManagedYabridge.PluginHome, "clap"), "/user/clap", "/system/clap"], ClapCatalog.SearchPath());
        Assert.Equal([Path.Combine(ManagedYabridge.PluginHome, "vst3"), "/user/vst3", "/system/vst3"], Vst3Catalog.SearchPath());
    }

    [Fact]
    public void InstallerSyncUsesPrivateRegistryAndPinnedLibraryDirectory()
    {
        Select();
        string log = Path.Combine(_root, "calls");
        Env("OPENXLR_TEST_LOG", log);
        Env("OPENXLR_TEST_WINDOWS", Path.Combine(_root, "windows"));
        Env("PATH", Package + ":" + Environment.GetEnvironmentVariable("PATH"));
        Script("wine", "echo wine-11.17\n");
        Script("yabridgectl", """
            printf '%s|%s|%s|%s\n' "$*" "$XDG_CONFIG_HOME" "$OPENXLR_YABRIDGE_PLUGIN_HOME" "$PATH" >> "$OPENXLR_TEST_LOG"
            case "$1" in
              --version) echo 'yabridgectl 5.1.1' ;;
              list) printf '%s\n' "$OPENXLR_TEST_WINDOWS" ;;
            esac
            """);
        var installer = new PluginInstaller();
        InstallOutcome outcome = installer.SyncWindows();
        Assert.True(outcome.Ok, outcome.Message);
        string[] calls = File.ReadAllLines(log);
        Assert.StartsWith("set --path " + Package + "|", calls[0]);
        Assert.All(calls, call => Assert.Contains("|" + OpenXlrPaths.ConfigFile("bridge") + "|" + ManagedYabridge.PluginHome + "|" + Package + ":", call));
        Assert.Contains(Path.Combine(ManagedYabridge.PluginHome, "vst3"), outcome.Destinations!);
        Assert.Equal("openxlr", installer.Setup().BridgeProvider);
        Assert.Null(installer.Setup().WindowsEditorNote);
        Assert.False(Directory.Exists(Path.Combine(_root, "config", "yabridgectl")));
    }

    [Fact]
    public void ProcessEnvironmentOverridesAreConfinedToTheChild()
    {
        string? original = Environment.GetEnvironmentVariable("OPENXLR_BRIDGE_CHILD_TEST");
        ProcessResult result = ProcessRunner.Run("/bin/sh", ["-c", "printf '%s' \"$OPENXLR_BRIDGE_CHILD_TEST\""],
            environment: new Dictionary<string, string> { ["OPENXLR_BRIDGE_CHILD_TEST"] = "private" });
        Assert.True(result.Ok);
        Assert.Equal("private", result.StdoutText);
        Assert.Equal(original, Environment.GetEnvironmentVariable("OPENXLR_BRIDGE_CHILD_TEST"));
    }

    private void Script(string name, string body)
    {
        string path = Path.Combine(Package, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _old) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, true);
    }
}
