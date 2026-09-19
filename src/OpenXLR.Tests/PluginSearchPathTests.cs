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
    private readonly Dictionary<string, string?> _environment = new[] { "XDG_CONFIG_HOME", "LV2_PATH", "CLAP_PATH", "VST3_PATH" }
        .ToDictionary(k => k, Environment.GetEnvironmentVariable);
    public PluginSearchPathTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        foreach (string format in new[] { "LV2", "CLAP", "VST3" })
            Environment.SetEnvironmentVariable(format + "_PATH", Path.Combine(_directory, "default-" + format));
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
        string path = Directory.CreateDirectory(Path.Combine(_directory, "extra " + kind)).FullName;
        Assert.True(PluginSearchPaths.Change(kind, path + "/.", true).Ok);
        Assert.True(PluginSearchPaths.Change(kind, path + "/", true).Ok);
        Assert.Single(PluginSearchPaths.Read(out string? warning));
        Assert.Null(warning);
        var snapshot = PluginSearchPaths.Snapshot().Where(p => p.Kind == kind).ToArray();
        Assert.Contains(snapshot, p => !p.Custom && p.Path.EndsWith("default-" + kind.ToUpperInvariant(), StringComparison.Ordinal));
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

    [Fact]
    public void AnAdditionalLv2BundleIsFoundAndItsPathReachesLiveHosts()
    {
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
        Assert.True(PluginSearchPaths.Change("lv2", root, true).Ok);
        Assert.Contains(Lv2Catalog.ScanNow(), p => p.Plugin == "urn:openxlr:custom-path-test");
        var start = new ProcessStartInfo();
        PluginSearchPaths.ApplyLv2(start);
        Assert.Contains(root, start.Environment["LV2_PATH"]!.Split(':'));
        Assert.Contains(Environment.GetEnvironmentVariable("LV2_PATH")!, start.Environment["LV2_PATH"]!.Split(':'));
        Assert.True(PluginSearchPaths.Change("lv2", root, false).Ok);
        Assert.Null(PluginSearchPaths.Lv2Override());
        Assert.True(File.Exists(Path.Combine(bundle, "manifest.ttl")));
    }

    [Theory]
    [InlineData(Architecture.X64, "x86_64-linux-gnu")]
    [InlineData(Architecture.Arm64, "aarch64-linux-gnu")]
    [InlineData(Architecture.X86, "i386-linux-gnu")]
    [InlineData(Architecture.Arm, "arm-linux-gnueabihf")]
    public void CustomLv2PathsKeepCommonMultiarchBundleDirectories(Architecture architecture, string triplet)
    {
        Assert.Contains($"/usr/lib/{triplet}/lv2", PluginSearchPaths.MultiarchLv2Paths(architecture));
        Environment.SetEnvironmentVariable("LV2_PATH", null);
        Assert.True(PluginSearchPaths.Change("lv2", _directory, true).Ok);
        var paths = PluginSearchPaths.Lv2Override()!.Split(':');
        Assert.Contains("/usr/lib/lv2", paths);
        Assert.Contains(_directory, paths);
        foreach (string path in PluginSearchPaths.MultiarchLv2Paths(RuntimeInformation.ProcessArchitecture))
            Assert.Contains(path, paths);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _environment) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(_directory, true);
    }
}
