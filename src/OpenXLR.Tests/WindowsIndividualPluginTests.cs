using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class WindowsIndividualPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-individual-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Windows = [(byte)'M', (byte)'Z', 0x90, 0];
    private static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1];
    private string Registry => Path.Combine(_root, "folders");
    private string Exclusions => Path.Combine(_root, "excluded");
    private string[] Calls => File.ReadAllLines(Path.Combine(_root, "calls"));
    private string Vst3 => Path.Combine(_root, "vst3", "yabridge");
    private string Clap => Path.Combine(_root, "clap", "yabridge");

    public WindowsIndividualPluginTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Source(string relative, byte[]? bytes = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Windows);
        return path;
    }

    private PluginInstaller Installer(string[] folders, string[]? excluded = null, string? fail = null, string? sync = null)
    {
        File.WriteAllLines(Registry, folders);
        File.WriteAllLines(Exclusions, excluded ?? []);
        string controller = ExecutableScript.Write(Path.Combine(_root, "yabridgectl"), $$"""
            printf '%s\n' "$*" >> '{{_root}}/calls'
            stage="$1"
            if [ "$1" = blacklist ]; then stage="$1 $2"; fi
            if [ "$stage" = '{{fail}}' ]; then printf 'test failure\n' >&2; exit 1; fi
            case "$1" in
              list) cat '{{Registry}}' ;;
              blacklist)
                case "$2" in
                  list) cat '{{Exclusions}}' ;;
                  add)
                    [ -e "$3" ] || exit 2
                    resolved=$(readlink -f -- "$3") || exit 2
                    grep -Fx -- "$resolved" '{{Exclusions}}' >/dev/null || printf '%s\n' "$resolved" >> '{{Exclusions}}'
                    ;;
                  rm)
                    grep -Fx -- "$3" '{{Exclusions}}' >/dev/null || exit 2
                    grep -Fxv -- "$3" '{{Exclusions}}' > '{{Exclusions}}.new'
                    mv '{{Exclusions}}.new' '{{Exclusions}}'
                    ;;
                esac ;;
              sync) {{sync ?? ":"}} ;;
            esac
            exit 0
            """);
        return new(Path.Combine(_root, "lv2"), Path.Combine(_root, "clap"), Path.Combine(_root, "vst3"), controller, "/bin/true",
            windowsImportDirectory: Path.Combine(_root, "imports"));
    }

    private string Vst3Wrapper(string name, string source)
    {
        string bundle = Path.Combine(Vst3, name + ".vst3");
        string contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "x86_64-linux"));
        Directory.CreateDirectory(Path.Combine(contents, "x86_64-win"));
        File.WriteAllBytes(Path.Combine(contents, "x86_64-linux", name + ".so"), Elf);
        File.CreateSymbolicLink(Path.Combine(contents, "x86_64-win", name + ".vst3"), source);
        return bundle;
    }

    private string ClapWrapper(string name, string source)
    {
        Directory.CreateDirectory(Clap);
        string module = Path.Combine(Clap, name + ".clap");
        File.WriteAllBytes(module, Elf);
        File.CreateSymbolicLink(Path.ChangeExtension(module, ".clap-win"), source);
        return module;
    }

    [Fact]
    public void ResolvingAnImportedWinePluginForChainRemovalDoesNotChangeFilesOrExclusions()
    {
        string source = Source("prefix/drive_c/EQ.vst3");
        Source("prefix/system.reg", [1]);
        string alias = Path.Combine(_root, "imports", "EQ.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        File.CreateSymbolicLink(alias, source);
        string wrapper = Vst3Wrapper("EQ", alias);
        var installer = Installer([Path.GetDirectoryName(alias)!]);

        Assert.True(installer.TryWindowsPluginWrappers(alias, out var paths, out string? error), error);
        Assert.Equal(new[] { wrapper }, paths);
        Assert.False(installer.TryWindowsPluginWrappers(source, out _, out _));
        Assert.Equal(Windows, File.ReadAllBytes(source));
        Assert.Equal(source, new FileInfo(alias).LinkTarget);
        Assert.Empty(File.ReadAllLines(Exclusions));
        Assert.Equal(new[] { "list", "blacklist list", "list" }, Calls);
    }

    [Fact]
    public void ListingIncludesExcludedPluginsButNotNativePluginsOrOtherFolders()
    {
        string vst = Source("plugins/Eq.vst3");
        string clap = Source("plugins/Comp.clap");
        Source("plugins/Native.clap", Elf);
        Source("elsewhere/Other.vst3");
        string folder = Path.GetDirectoryName(vst)!;
        string wrapper = Vst3Wrapper("Eq", vst);
        var installer = Installer([folder], [clap]);

        WindowsPluginFiles result = installer.ListWindowsPlugins(folder, [wrapper]);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Plugins.Count);
        var eq = Assert.Single(result.Plugins, p => p.Path == vst);
        Assert.Equal("Eq.vst3", eq.Name);
        Assert.Equal("vst3", eq.Format);
        Assert.True(eq.Enabled);
        Assert.True(eq.CanDelete);
        Assert.True(eq.InUse);
        Assert.Null(eq.WinePrefix);
        var comp = Assert.Single(result.Plugins, p => p.Path == clap);
        Assert.False(comp.Enabled);
        Assert.False(comp.InUse);
        Assert.Equal("clap", comp.Format);
        Assert.False(installer.ListWindowsPlugins(Path.GetDirectoryName(folder)!).Ok);
        Assert.False(installer.ListWindowsPlugins("relative").Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExclusionRemovesOnlyOnePluginsWrappersAndKeepsItsFiles(bool clap)
    {
        string source = Source(clap ? "plugins/Eq.clap" : "plugins/Eq.vst3");
        string other = Source("plugins/Other.vst3");
        string wrapper = clap ? ClapWrapper("Eq", source) : Vst3Wrapper("Eq", source);
        string otherWrapper = Vst3Wrapper("Other", other);
        var installer = Installer([Path.GetDirectoryName(source)!]);

        InstallOutcome result = installer.SetWindowsPluginEnabled(source, false);

        Assert.True(result.Ok, result.Message);
        Assert.Equal([source], File.ReadAllLines(Exclusions));
        Assert.False(File.Exists(wrapper) || Directory.Exists(wrapper));
        Assert.True(Directory.Exists(otherWrapper));
        Assert.Equal(Windows, File.ReadAllBytes(source));
        Assert.Equal(Windows, File.ReadAllBytes(other));
        Assert.DoesNotContain(Calls, c => c.Contains("--prune"));
        Assert.True(installer.SetWindowsPluginEnabled(source, false).Ok);
        Assert.Single(Calls, c => c.StartsWith("blacklist add", StringComparison.Ordinal));
        Assert.True(installer.SetWindowsPluginEnabled(source, true).Ok);
        Assert.Empty(File.ReadAllLines(Exclusions));
        Assert.True(installer.SetWindowsPluginEnabled(source, true).Ok);
        Assert.Single(Calls, c => c.StartsWith("blacklist rm", StringComparison.Ordinal));
    }

    [Fact]
    public void WineImportsUseCanonicalExclusionsAndCannotDeleteTheOriginal()
    {
        string original = Source("prefix/drive_c/VST3/Eq.vst3");
        Source("prefix/system.reg", [1]);
        string managed = Path.Combine(_root, "imports", "Eq.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        File.CreateSymbolicLink(managed, original);
        string wrapper = Vst3Wrapper("Eq", managed);
        var installer = Installer([Path.GetDirectoryName(managed)!]);

        var row = Assert.Single(installer.ListWindowsPlugins(Path.GetDirectoryName(managed)!).Plugins);
        Assert.Equal(Path.Combine(_root, "prefix"), row.WinePrefix);
        Assert.False(row.CanDelete);
        Assert.True(installer.SetWindowsPluginEnabled(managed, false).Ok);
        Assert.Equal([original], File.ReadAllLines(Exclusions));
        Assert.False(Directory.Exists(wrapper));
        Assert.False(Assert.Single(installer.ListWindowsPlugins(Path.GetDirectoryName(managed)!).Plugins).Enabled);
        Assert.False(installer.DeleteWindowsPlugin(managed).Ok);
        Assert.True(File.Exists(original));
        Assert.NotNull(new FileInfo(managed).LinkTarget);
        Assert.True(installer.SetWindowsPluginEnabled(managed, true).Ok);
        Assert.Contains("blacklist rm " + original, Calls);
    }

    [Fact]
    public void AnExcludedAncestorIsNotRemovedToEnableOnePlugin()
    {
        string source = Source("plugins/nested/Eq.vst3");
        string root = Path.Combine(_root, "plugins");
        string blocked = Path.GetDirectoryName(source)!;
        var installer = Installer([root], [blocked, source]);
        Assert.False(Assert.Single(installer.ListWindowsPlugins(root).Plugins).Enabled);
        var result = installer.SetWindowsPluginEnabled(source, true);
        Assert.False(result.Ok);
        Assert.Contains("folder-level", result.Message);
        Assert.Equal(new[] { blocked, source }, File.ReadAllLines(Exclusions));
        Assert.DoesNotContain(Calls, c => c.StartsWith("blacklist rm", StringComparison.Ordinal));
    }

    [Fact]
    public void BlacklistedParentsOutsideTheWalkDoNotDisableAnImportedLink()
    {
        string original = Source("original/Eq.vst3");
        string managed = Path.Combine(_root, "imports", "Eq.vst3");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        File.CreateSymbolicLink(managed, original);
        var installer = Installer([Path.GetDirectoryName(managed)!], [Path.GetDirectoryName(original)!]);
        Assert.True(Assert.Single(installer.ListWindowsPlugins(Path.GetDirectoryName(managed)!).Plugins).Enabled);
    }

    [Fact]
    public void ASeparateNestedRegistrationCanBypassAnAncestorExclusion()
    {
        string source = Source("plugins/nested/Eq.vst3");
        string parent = Path.Combine(_root, "plugins");
        string nested = Path.GetDirectoryName(source)!;
        var installer = Installer([parent, nested], [parent, source]);
        Assert.True(installer.SetWindowsPluginEnabled(source, true).Ok);
        Assert.Equal([parent], File.ReadAllLines(Exclusions));
        Assert.True(Assert.Single(installer.ListWindowsPlugins(nested).Plugins).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InUsePluginsAreRefusedBeforeExclusionOrDeletion(bool delete)
    {
        string source = Source("plugins/Eq.vst3");
        string wrapper = Vst3Wrapper("Eq", source);
        var installer = Installer([Path.GetDirectoryName(source)!]);
        var result = delete ? installer.DeleteWindowsPlugin(source, [wrapper])
            : installer.SetWindowsPluginEnabled(source, false, [wrapper]);
        Assert.False(result.Ok);
        Assert.Contains("insert chains", result.Message);
        Assert.True(File.Exists(source));
        Assert.True(Directory.Exists(wrapper));
        Assert.Empty(File.ReadAllLines(Exclusions));
        Assert.DoesNotContain(Calls, c => c.StartsWith("blacklist add", StringComparison.Ordinal) || c == "sync");
    }

    [Fact]
    public void DeletingOneStandalonePluginKeepsItsSiblingAndClearsItsOwnExclusion()
    {
        string source = Source("plugins/Eq.vst3");
        string other = Source("plugins/Other.vst3");
        string wrapper = Vst3Wrapper("Eq", source);
        string otherWrapper = Vst3Wrapper("Other", other);
        var installer = Installer([Path.GetDirectoryName(source)!], [source, other]);
        var result = installer.DeleteWindowsPlugin(source);
        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(source));
        Assert.False(Directory.Exists(wrapper));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(otherWrapper));
        Assert.Equal([other], File.ReadAllLines(Exclusions));
    }

    [Fact]
    public void DeletingABundleDoesNotFollowItsResourceLink()
    {
        string module = Source("plugins/Eq.vst3/Contents/x86_64-win/Eq.vst3");
        string bundle = Path.Combine(_root, "plugins", "Eq.vst3");
        string resources = Source("shared/keep.txt", [1, 2, 3]);
        Directory.CreateSymbolicLink(Path.Combine(bundle, "Contents", "Resources"), Path.GetDirectoryName(resources)!);
        string wrapper = Vst3Wrapper("Eq", module);
        var installer = Installer([Path.GetDirectoryName(bundle)!]);
        var result = installer.DeleteWindowsPlugin(bundle);
        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(bundle));
        Assert.False(Directory.Exists(wrapper));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(resources));
    }

    [Fact]
    public void DeletionRefusesWineSourcesLinksNativeFilesAndUnregisteredPaths()
    {
        string wine = Source("prefix/drive_c/VST3/Eq.vst3");
        Source("prefix/system.reg", [1]);
        string native = Source("plugins/Native.clap", Elf);
        string outside = Source("outside/Other.vst3");
        string link = Path.Combine(_root, "plugins", "Link.vst3");
        File.CreateSymbolicLink(link, outside);
        var installer = Installer([Path.GetDirectoryName(wine)!, Path.GetDirectoryName(native)!]);
        foreach (string path in new[] { wine, native, outside, link, Path.GetDirectoryName(native)!, "relative" })
            Assert.False(installer.DeleteWindowsPlugin(path).Ok);
        Assert.True(File.Exists(wine));
        Assert.True(File.Exists(native));
        Assert.True(File.Exists(outside));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void AnAncestorSourceLinkAlsoPreventsDeletion()
    {
        string original = Source("original/Eq.vst3");
        string alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(original)!);
        var installer = Installer([alias]);
        string selected = Path.Combine(alias, "Eq.vst3");
        var row = Assert.Single(installer.ListWindowsPlugins(alias).Plugins);
        Assert.False(row.CanDelete);
        Assert.False(installer.DeleteWindowsPlugin(selected).Ok);
        Assert.True(File.Exists(original));
        Assert.Equal(original, WindowsPluginWrappers.Canonical(selected));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("blacklist list")]
    [InlineData("blacklist add")]
    public void FailedExclusionPrerequisitesDoNotDeleteSourceOrWrapper(string fail)
    {
        string source = Source("plugins/Eq.vst3");
        string wrapper = Vst3Wrapper("Eq", source);
        var result = Installer([Path.GetDirectoryName(source)!], fail: fail).SetWindowsPluginEnabled(source, false);
        Assert.False(result.Ok);
        Assert.Contains("test failure", result.Message);
        Assert.True(File.Exists(source));
        Assert.True(Directory.Exists(wrapper));
        Assert.Empty(File.ReadAllLines(Exclusions));
    }

    [Fact]
    public void FailedSyncReportsAnExclusionThatWasAlreadySaved()
    {
        string source = Source("plugins/Eq.vst3");
        string wrapper = Vst3Wrapper("Eq", source);
        var result = Installer([Path.GetDirectoryName(source)!], fail: "sync").SetWindowsPluginEnabled(source, false);
        Assert.False(result.Ok);
        Assert.Contains("saved", result.Message);
        Assert.Equal([source], File.ReadAllLines(Exclusions));
        Assert.True(Directory.Exists(wrapper));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ASharedWrapperRetargetedBySyncIsKept()
    {
        string source = Source("plugins/Eq.vst3");
        string other = Source("other/Eq.vst3");
        string wrapper = Vst3Wrapper("Eq", source);
        string link = Path.Combine(wrapper, "Contents", "x86_64-win", "Eq.vst3");
        var installer = Installer([Path.GetDirectoryName(source)!, Path.GetDirectoryName(other)!],
            sync: $"ln -sfn '{other}' '{link}'");
        Assert.True(installer.SetWindowsPluginEnabled(source, false).Ok);
        Assert.True(Directory.Exists(wrapper));
        Assert.Equal(other, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void AWrapperCombiningDifferentSourceArchitecturesIsNotPartiallyDeleted()
    {
        string first = Source("plugins/Eq.vst3");
        string other = Source("other/Eq.vst3");
        string wrapper = Vst3Wrapper("Eq", first);
        string win32 = Path.Combine(wrapper, "Contents", "i386-win");
        Directory.CreateDirectory(win32);
        File.CreateSymbolicLink(Path.Combine(win32, "Eq.vst3"), other);
        var installer = Installer([Path.GetDirectoryName(first)!, Path.GetDirectoryName(other)!]);
        var result = installer.SetWindowsPluginEnabled(first, false);
        Assert.False(result.Ok);
        Assert.Contains("shared by multiple", result.Message);
        Assert.Empty(File.ReadAllLines(Exclusions));
        Assert.True(Directory.Exists(wrapper));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void SyncCleansOnlyRegisteredMissingSourcesAndKeepsInUseWrappers()
    {
        string live = Source("plugins/Live.vst3");
        string folder = Path.GetDirectoryName(live)!;
        string missing = Vst3Wrapper("Gone", Path.Combine(folder, "Gone.vst3"));
        string used = Vst3Wrapper("Used", Path.Combine(folder, "Used.vst3"));
        string orphan = Vst3Wrapper("Unregistered", Path.Combine(_root, "elsewhere", "Gone.vst3"));
        string healthy = Vst3Wrapper("Live", live);
        string clap = ClapWrapper("Gone", Path.Combine(folder, "Gone.clap"));
        var result = Installer([folder]).SyncWindows([used]);
        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(missing));
        Assert.False(File.Exists(clap));
        Assert.True(Directory.Exists(used));
        Assert.True(Directory.Exists(orphan));
        Assert.True(Directory.Exists(healthy));
        Assert.Contains("Kept 1", result.Message);
        Assert.DoesNotContain(Calls, c => c.Contains("--prune"));
    }

    [Fact]
    public void FailedSyncNeverCleansMissingSourceWrappers()
    {
        string source = Source("plugins/Live.vst3");
        string missing = Vst3Wrapper("Gone", Path.Combine(Path.GetDirectoryName(source)!, "Gone.vst3"));
        var result = Installer([Path.GetDirectoryName(source)!], fail: "sync").SyncWindows();
        Assert.False(result.Ok);
        Assert.True(Directory.Exists(missing));
    }
}
