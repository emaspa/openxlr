using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using DaemonPrefs = OpenXLR.UI.DaemonPrefs;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginSearchPathTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-search-paths-").FullName;
    private readonly Dictionary<string, string?> _environment = new[] { "XDG_CONFIG_HOME", "LV2_PATH" }
        .ToDictionary(k => k, Environment.GetEnvironmentVariable);
    private string SettingsFile => Path.Combine(OpenXlrPaths.ConfigDir, "daemon.json");

    public PluginSearchPathTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        Environment.SetEnvironmentVariable("LV2_PATH", Path.Combine(_directory, "default-LV2"));
    }

    [Theory]
    [InlineData("lv2", "relative")]
    [InlineData("vst2", "/plugins")]
    [InlineData("clap", "/plugins:other")]
    [InlineData("clap", "/plugins\nother")]
    [InlineData("lv2", "/")]
    [InlineData("lv2", null)]
    public void InvalidPathsAreRejectedAtTheCommandBoundary(string kind, string? path)
    {
        using var mixer = new Mixer();
        foreach (string command in new[] { "addPluginSearchPath", "removePluginSearchPath" })
            Assert.NotNull(CommandValidation.Check(new Command { Cmd = command, Kind = kind, Path = path }, mixer, _ => null));
    }

    [Theory]
    [InlineData("lv2")]
    [InlineData("clap")]
    [InlineData("vst3")]
    public void FoldersSupplementTheDefaultsAndStayRemovableWhenOffline(string kind)
    {
        string[] defaults = PluginSearchPaths.Snapshot().Where(p => p.Kind == kind).Select(p => p.Path).ToArray();
        string path = Directory.CreateDirectory(Path.Combine(_directory, "extra " + kind)).FullName;
        Assert.True(PluginSearchPaths.Change(kind, path + "/.", true).Ok);
        var again = PluginSearchPaths.Change(kind, path + "/", true);
        Assert.True(again.Ok);
        Assert.False(again.RefreshCatalogue);
        Assert.Single(PluginSearchPaths.Read(out string? warning));
        Assert.Null(warning);
        var snapshot = PluginSearchPaths.Snapshot().Where(p => p.Kind == kind).ToArray();
        Assert.All(defaults, path => Assert.Contains(snapshot, p => !p.Custom && p.Path == path));
        Assert.Contains(snapshot, p => p.Custom && p.Path == path && p.Exists);
        Directory.Delete(path);
        Assert.Contains(PluginSearchPaths.Snapshot(), p => p.Path == path && p.Custom && !p.Exists);
        Assert.True(PluginSearchPaths.Change(kind, path, false).Ok);
        Assert.Empty(PluginSearchPaths.Read(out _));
        Assert.False(PluginSearchPaths.Change(kind, path, true).Ok);
    }

    [Fact]
    public void TheFoldersShareDaemonJsonWithTheWindowsSubmixerChoice()
    {
        Directory.CreateDirectory(OpenXlrPaths.ConfigDir);
        File.WriteAllText(SettingsFile, """{"submixer":false,"laterSetting":7}""");
        string path = Directory.CreateDirectory(Path.Combine(_directory, "plugins")).FullName;
        Assert.True(PluginSearchPaths.Change("clap", path, true).Ok);
        var saved = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.False(saved["submixer"]!.GetValue<bool>());
        Assert.Equal(7, saved["laterSetting"]!.GetValue<int>());
        Assert.Equal(path, saved["pluginFolders"]![0]!["path"]!.GetValue<string>());
        // The window writes the same file when the submixer is switched.
        (DaemonPrefs.Load() with { Submixer = true }).Save();
        Assert.True(DaemonSettings.Load().Submixer);
        Assert.Equal(new PluginFolder("clap", path), Assert.Single(PluginSearchPaths.Read(out _)));
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsFile));
    }

    [Fact]
    public void UnusableSavedEntriesAreSkippedWithAWarningAndKeptThroughAnEdit()
    {
        string healthy = Directory.CreateDirectory(Path.Combine(_directory, "healthy")).FullName;
        string broken = Directory.CreateDirectory(Path.Combine(_directory, "broken")).FullName;
        Assert.True(PluginSearchPaths.Change("lv2", broken, true).Ok);
        Assert.True(PluginSearchPaths.Change("lv2", healthy, true).Ok);
        Directory.Delete(broken);
        Directory.CreateSymbolicLink(broken, broken);

        Assert.Equal(new PluginFolder("lv2", healthy), Assert.Single(PluginSearchPaths.Read(out string? warning)));
        Assert.NotNull(warning);
        Assert.Contains(healthy, PluginSearchPaths.Lv2Directories());
        Assert.DoesNotContain(broken, PluginSearchPaths.Lv2Path()!.Split(':'));

        string other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;
        Assert.True(PluginSearchPaths.Change("lv2", other, true).Ok);
        Assert.True(PluginSearchPaths.Change("lv2", healthy, false).Ok);
        File.Delete(broken);
        Directory.CreateDirectory(broken);
        Assert.Equal([new PluginFolder("lv2", broken), new PluginFolder("lv2", other)], PluginSearchPaths.Read(out warning));
        Assert.Null(warning);
    }

    [Fact]
    public void MalformedEntriesAreSkippedWithAWarning()
    {
        Directory.CreateDirectory(OpenXlrPaths.ConfigDir);
        string healthy = Directory.CreateDirectory(Path.Combine(_directory, "healthy")).FullName;
        File.WriteAllText(SettingsFile, $$"""{"pluginFolders":[null,{},{"kind":"vst2","path":"/x"},{"kind":"vst3","path":"{{healthy}}"}]}""");
        Assert.Equal(new PluginFolder("vst3", healthy), Assert.Single(PluginSearchPaths.Read(out string? warning)));
        Assert.Contains("3 saved plugin folders", warning);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsReportedAndNotWrittenOver()
    {
        Directory.CreateDirectory(OpenXlrPaths.ConfigDir);
        File.WriteAllText(SettingsFile, "{\"submixer\": fals");
        Assert.Empty(PluginSearchPaths.Read(out string? warning));
        Assert.Contains("daemon.json cannot be read", warning);
        var outcome = PluginSearchPaths.Change("clap", _directory, true);
        Assert.False(outcome.Ok);
        Assert.False(outcome.RefreshCatalogue);
        Assert.Equal("{\"submixer\": fals", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void ConcurrentEditsKeepTheirEntriesAndRespectTheLimit()
    {
        var paths = Enumerable.Range(0, 40).Select(i => Directory.CreateDirectory(Path.Combine(_directory, "p" + i)).FullName).ToArray();
        Parallel.ForEach(paths, p => PluginSearchPaths.Change("clap", p, true));
        Assert.Equal(PluginSearchPaths.MaxPaths, PluginSearchPaths.Read(out string? warning).Count);
        Assert.Null(warning);
        Assert.False(PluginSearchPaths.Change("lv2", "/.", true).Ok);
    }

    [Fact]
    public void AFailedSaveIsReportedAndRescansNothing()
    {
        Directory.CreateDirectory(SettingsFile);
        var outcome = PluginSearchPaths.Change("clap", _directory, true);
        Assert.False(outcome.Ok);
        Assert.False(outcome.RefreshCatalogue);
        Assert.Contains("Could not save", outcome.Message);
        Assert.True(Directory.Exists(SettingsFile));
    }

    /// <summary>
    /// The folder's plugin must be found by the catalogue and loadable by
    /// every LV2 host: the PipeWire filter-chain and the native host get the
    /// same LV2_PATH, and that path finds the plugin. Adding a folder must not
    /// lose a plugin lilv's compiled default found.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAddedLv2FolderIsScannedAndOnThePathEveryHostReceives(bool unset)
    {
        string? configured = unset ? null : Directory.CreateDirectory(Path.Combine(_directory, "configured")).FullName;
        Environment.SetEnvironmentVariable("LV2_PATH", configured);
        bool lilv = NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr library);
        if (lilv) NativeLibrary.Free(library);
        string root = Directory.CreateDirectory(Path.Combine(_directory, "custom lv2")).FullName;
        string bundle = Directory.CreateDirectory(Path.Combine(root, "test.lv2")).FullName;
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), """
            @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
            @prefix doap: <http://usefulinc.com/ns/doap#> .
            <urn:openxlr:custom-path-test> a lv2:Plugin ; doap:name "Custom path test" ;
              lv2:binary <test.so> ;
              lv2:port [ a lv2:AudioPort, lv2:InputPort ; lv2:index 0 ; lv2:symbol "in" ; lv2:name "In" ],
                       [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 1 ; lv2:symbol "out" ; lv2:name "Out" ] .
            """);
        Assert.Null(PluginSearchPaths.Lv2Path());
        // Without an added folder every host keeps the environment it inherits.
        Assert.Equal(configured, PipeWireAdapter.FilterChainProcess("{}").Environment.TryGetValue("LV2_PATH", out string? inherited) ? inherited : null);
        HashSet<string> before = lilv ? [.. Lv2Catalog.ScanNow().Select(p => p.Plugin)] : [];
        Assert.DoesNotContain("urn:openxlr:custom-path-test", before);

        Assert.True(PluginSearchPaths.Change("lv2", root, true).Ok);
        string path = Assert.IsType<string>(PluginSearchPaths.Lv2Path());
        string[] directories = path.Split(':');
        Assert.Equal(root, directories[^1]);
        if (configured is not null) Assert.Equal(configured, directories[0]);
        else Assert.All(PluginSearchPaths.StandardLv2Directories(),
            d => Assert.Contains(WindowsPluginWrappers.Canonical(d), directories));

        Assert.Equal(path, PipeWireAdapter.FilterChainProcess("{}").Environment["LV2_PATH"]);
        Assert.Equal(path, HostEnvironment());
        if (lilv)
        {
            var after = Lv2Catalog.ScanNow().Select(p => p.Plugin).ToHashSet();
            Assert.Contains("urn:openxlr:custom-path-test", after);
            // lilv reads the process environment, which a test cannot change,
            // so only the unset case compares with what lilv found on its own.
            if (unset) Assert.Subset(after, before);
            Assert.Contains(Lv2Catalog.ScanNow(path), p => p.Plugin == "urn:openxlr:custom-path-test");
        }

        Assert.True(PluginSearchPaths.Change("lv2", root, false).Ok);
        Assert.Null(PluginSearchPaths.Lv2Path());
        Assert.Equal(configured ?? "unset", HostEnvironment());
        Assert.True(File.Exists(Path.Combine(bundle, "manifest.ttl")));
    }

    /// <summary>What LV2_PATH a native host process is started with.</summary>
    private string HostEnvironment()
    {
        string output = Path.Combine(_directory, "host-lv2-path");
        File.Delete(output);
        using (new NativePluginHost(new InsertDefinition { Id = "fixture", Kind = "lv2", Plugin = "urn:fixture" },
            "fixture", 1, 48000, "/bin/sh",
            ["-c", "printf '%s' \"${LV2_PATH-unset}\" > \"$0\"; echo ready; cat > /dev/null", output])) { }
        return File.ReadAllText(output);
    }

    [Theory]
    [InlineData("/usr")]
    [InlineData("/proc")]
    [InlineData("/proc/self")]
    [InlineData("/sys")]
    [InlineData("/dev")]
    public void BroadSystemTreesAreNotSearchRoots(string path)
    {
        Assert.False(PluginSearchPaths.Change("vst3", path, true).Ok);
        Assert.Empty(PluginSearchPaths.Read(out _));
    }

    [Fact]
    public void AliasesAndNestedDirectoriesDoNotMultiplyScans()
    {
        string path = Directory.CreateDirectory(Path.Combine(_directory, "plugins")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(path, "vendor")).FullName;
        string alias = Path.Combine(_directory, "alias");
        Directory.CreateSymbolicLink(alias, path);
        Assert.True(PluginSearchPaths.Change("vst3", path, true).Ok);
        var duplicate = PluginSearchPaths.Change("vst3", alias, true);
        Assert.True(duplicate.Ok);
        Assert.False(duplicate.RefreshCatalogue);
        Assert.False(PluginSearchPaths.Change("vst3", child, true).Ok);
        Assert.False(PluginSearchPaths.Change("vst3", _directory, true).Ok);
        Assert.Single(PluginSearchPaths.Read(out _));
        Assert.True(PluginSearchPaths.Change("vst3", alias, false).Ok);
        Assert.False(PluginSearchPaths.Change("vst3", alias, false).RefreshCatalogue);
    }

    [Fact]
    public void AChildOfAnExplicitDefaultPathIsNotAddedAgain()
    {
        string child = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("LV2_PATH")!, "vendor")).FullName;
        Assert.False(PluginSearchPaths.Change("lv2", child, true).Ok);
        Assert.Empty(PluginSearchPaths.Read(out _));
    }

    [Fact]
    public void RecursiveDiscoveryStopsAtItsEntryBudgetAndReportsTheLimit()
    {
        for (int i = 0; i < 20; i++) Directory.CreateDirectory(Path.Combine(_directory, "tree", "p" + i));
        var errors = new List<Exception>();
        Assert.Empty(HostScan.FindBundles(Path.Combine(_directory, "tree"), ".vst3", true,
            unreadable: (_, ex) => errors.Add(ex), entryLimit: 10));
        Assert.Contains("10 entry", Assert.Single(errors).Message);
    }

    [Fact]
    public void ALoopingSearchRootDoesNotDiscardHealthyPaths()
    {
        string loop = Path.Combine(_directory, "loop");
        Directory.CreateSymbolicLink(loop, loop);
        string healthy = Directory.CreateDirectory(Path.Combine(_directory, "healthy")).FullName;
        Assert.Equal(new[] { loop, healthy }, PluginSearchPaths.Include("clap", [loop, healthy]));
        Assert.False(PluginSearchPaths.Change("clap", loop, true).Ok);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _environment) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(_directory, true);
    }
}
