using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginSearchPathTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-search-paths-").FullName;
    private readonly Dictionary<string, string?> _environment = new[] { "XDG_CONFIG_HOME", "LV2_PATH" }
        .ToDictionary(k => k, Environment.GetEnvironmentVariable);
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
        Assert.NotNull(CommandValidation.Check(new Command { Cmd = "addPluginSearchPath", Kind = kind, Path = path }, mixer, _ => null));
    }

    [Theory]
    [InlineData("lv2")]
    [InlineData("clap")]
    [InlineData("vst3")]
    public void PathsSupplementDefaultsRoundTripAndRemainRemovableWhenOffline(string kind)
    {
        string[] defaults = PluginSearchPaths.Snapshot().Where(p => p.Kind == kind).Select(p => p.Path).ToArray();
        string path = Directory.CreateDirectory(Path.Combine(_directory, "extra " + kind)).FullName;
        Assert.True(PluginSearchPaths.Change(kind, path + "/.", true).Ok);
        Assert.True(PluginSearchPaths.Change(kind, path + "/", true).Ok);
        Assert.Single(PluginSearchPaths.Read(out string? warning));
        Assert.Null(warning);
        var snapshot = PluginSearchPaths.Snapshot().Where(p => p.Kind == kind).ToArray();
        Assert.All(defaults, path => Assert.Contains(snapshot, p => !p.Custom && p.Path == path));
        Assert.Contains(snapshot, p => p.Custom && p.Path == path && p.Exists);
        Directory.Delete(path);
        Assert.Contains(PluginSearchPaths.Snapshot(), p => p.Path == path && !p.Exists);
        Assert.True(PluginSearchPaths.Change(kind, path, false).Ok);
        Assert.Empty(PluginSearchPaths.Read(out _));
        Assert.False(PluginSearchPaths.Change(kind, path, true).Ok);
    }

    [Fact]
    public void CorruptAndOversizedSettingsAreNotOverwrittenByAnEdit()
    {
        string file = OpenXlrPaths.ConfigFile("plugin-paths.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        foreach (string contents in new[] { "null", "[null]", "[{}]", new string(' ', 1024 * 1024 + 1) })
        {
            File.WriteAllText(file, contents);
            Assert.Empty(PluginSearchPaths.Read(out string? warning));
            Assert.NotNull(warning);
            Assert.False(PluginSearchPaths.Change("clap", _directory, true).Ok);
            Assert.Equal(contents, File.ReadAllText(file));
        }
    }

    [Fact]
    public void ConcurrentEditsKeepTheirEntriesAndRespectTheLimit()
    {
        var paths = Enumerable.Range(0, 40).Select(i => Directory.CreateDirectory(Path.Combine(_directory, "p" + i)).FullName).ToArray();
        Parallel.ForEach(paths, p => PluginSearchPaths.Change("clap", p, true));
        Assert.Equal(PluginSearchPaths.MaxPaths, PluginSearchPaths.Read(out string? warning).Count);
        Assert.Null(warning);
        Assert.False(PluginSearchPaths.Change("lv2", "/.", true).Ok);
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(OpenXlrPaths.ConfigFile("plugin-paths.json")));
    }

    [Fact]
    public void AFailedSaveLeavesTheOriginalPathsUntouched()
    {
        string path = OpenXlrPaths.ConfigFile("plugin-paths.json");
        Directory.CreateDirectory(path);
        Assert.False(PluginSearchPaths.Change("clap", _directory, true).Ok);
        Assert.Empty(PluginSearchPaths.Read(out _));
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ExplicitAndAdditionalLv2PathsAreAppliedWithoutReplacingAnUnsetDefault(bool unset, bool add)
    {
        if (unset) Environment.SetEnvironmentVariable("LV2_PATH", null);
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr library)) return;
        NativeLibrary.Free(library);
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
        if (add) Assert.True(PluginSearchPaths.Change("lv2", root, true).Ok);
        else Environment.SetEnvironmentVariable("LV2_PATH", root);
        Assert.Contains(Lv2Catalog.ScanNow(), p => p.Plugin == "urn:openxlr:custom-path-test");
        var start = new ProcessStartInfo();
        PluginSearchPaths.ApplyLv2(start);
        if (unset) Assert.False(start.Environment.ContainsKey("LV2_PATH"));
        else
        {
            Assert.Contains(root, start.Environment["LV2_PATH"]!.Split(':'));
            Assert.Contains(Environment.GetEnvironmentVariable("LV2_PATH")!, start.Environment["LV2_PATH"]!.Split(':'));
        }
        Assert.True(PluginSearchPaths.Change("lv2", root, false).Ok);
        Assert.Equal(Environment.GetEnvironmentVariable("LV2_PATH"), PluginSearchPaths.Lv2Override());
        Assert.True(File.Exists(Path.Combine(bundle, "manifest.ttl")));
    }

    [Fact]
    public void UnsetLv2PathKeepsTheCompiledDefaultAndDoesNotInventPaths()
    {
        Environment.SetEnvironmentVariable("LV2_PATH", null);
        var original = Lv2Catalog.ScanNow().Select(p => p.Plugin).ToHashSet();
        Assert.True(PluginSearchPaths.Change("lv2", _directory, true).Ok);
        Assert.Null(PluginSearchPaths.Lv2Override());
        var start = new ProcessStartInfo();
        PluginSearchPaths.ApplyLv2(start);
        Assert.False(start.Environment.ContainsKey("LV2_PATH"));
        Assert.All(PluginSearchPaths.Snapshot().Where(p => p.Kind == "lv2"), p => Assert.True(p.Custom));
        Assert.Subset(Lv2Catalog.ScanNow().Select(p => p.Plugin).ToHashSet(), original);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStoredDirectoryThatBecomesASymlinkLoopDoesNotHideHealthyRoots(bool brokenFirst)
    {
        string healthy = Directory.CreateDirectory(Path.Combine(_directory, "healthy")).FullName;
        string broken = Directory.CreateDirectory(Path.Combine(_directory, "broken")).FullName;
        foreach (string path in brokenFirst ? new[] { broken, healthy } : new[] { healthy, broken })
            Assert.True(PluginSearchPaths.Change("lv2", path, true).Ok);
        string file = OpenXlrPaths.ConfigFile("plugin-paths.json");
        string saved = File.ReadAllText(file);
        Directory.Delete(broken);
        Directory.CreateSymbolicLink(broken, broken);

        var roots = PluginSearchPaths.Read(out string? warning);
        Assert.Equal(new PluginSearchPaths.Entry("lv2", healthy), Assert.Single(roots));
        Assert.NotNull(warning);
        Assert.Contains(healthy, PluginSearchPaths.Lv2Path());
        Assert.Contains(PluginSearchPaths.Snapshot(), p => p.Kind == "lv2" && p.Path == healthy && p.Custom && p.Exists);
        Assert.False(PluginSearchPaths.Change("lv2", healthy, false).Ok);
        Assert.Equal(saved, File.ReadAllText(file));

        File.Delete(broken);
        Directory.CreateDirectory(broken);
        Assert.Equal(2, PluginSearchPaths.Read(out warning).Count);
        Assert.Null(warning);
        Assert.True(PluginSearchPaths.Change("lv2", broken, false).Ok);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _environment) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(_directory, true);
    }
}
