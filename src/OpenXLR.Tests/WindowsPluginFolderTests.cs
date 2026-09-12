using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class WindowsPluginFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-folders-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1];
    private static readonly byte[] Windows = [(byte)'M', (byte)'Z', 0x90, 0];
    private string Registry => Path.Combine(_root, "folders");
    private string Vst3 => Path.Combine(_root, "vst3", "yabridge");
    private string Clap => Path.Combine(_root, "clap", "yabridge");
    private string[] Calls => File.ReadAllLines(Path.Combine(_root, "calls"));

    public WindowsPluginFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Source(string relative, byte[]? bytes = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Windows);
        return path;
    }

    private PluginInstaller Installer(string[] folders, string? fail = null, string? sync = null, bool wine = true)
    {
        File.WriteAllLines(Registry, folders);
        string controller = Path.Combine(_root, "yabridgectl");
        File.WriteAllText(controller, $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> '{{_root}}/calls'
            if [ "$1" = '{{fail}}' ]; then printf 'test failure\n' >&2; exit 1; fi
            case "$1" in
              list) cat '{{Registry}}' ;;
              add) printf '%s\n' "$2" >> '{{Registry}}' ;;
              rm) grep -Fxv -- "$2" '{{Registry}}' > '{{Registry}}.new'; mv '{{Registry}}.new' '{{Registry}}' ;;
              sync) {{sync ?? ":"}} ;;
            esac
            exit 0
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(controller, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(Path.Combine(_root, "lv2"), Path.Combine(_root, "clap"), Path.Combine(_root, "vst3"), controller, wine ? "/bin/true" : null);
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
    public void AddingAWindowsFolderDoesNotCopyNativePluginsBesideIt()
    {
        string folder = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        Source("source/Linux.clap", Elf);
        var installer = Installer([]);

        InstallOutcome result = installer.AddWindowsFolder(folder);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["list", "add " + folder, "sync"], Calls);
        Assert.False(File.Exists(Path.Combine(_root, "clap", "Linux.clap")));
        Assert.Equal([folder], File.ReadAllLines(Registry));
    }

    [Fact]
    public void AddingRejectsFilesRelativeMissingAndNativeOnlyFolders()
    {
        string native = Source("native/Linux.clap", Elf);
        var installer = Installer([]);
        Assert.False(installer.AddWindowsFolder(native).Ok);
        Assert.False(installer.AddWindowsFolder("relative").Ok);
        Assert.False(installer.AddWindowsFolder(Path.Combine(_root, "missing")).Ok);
        Assert.False(installer.AddWindowsFolder(Path.GetDirectoryName(native)!).Ok);
        Assert.False(File.Exists(Path.Combine(_root, "calls")));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("add")]
    [InlineData("sync")]
    public void AddingReportsControllerFailures(string command)
    {
        string folder = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        InstallOutcome result = Installer([], fail: command).AddWindowsFolder(folder);
        Assert.False(result.Ok);
        Assert.Contains("test failure", result.Message);
        Assert.Equal(command, Calls[^1].Split(' ')[0]);
    }

    [Fact]
    public void RemovingDeletesOnlyMatchingWrappersAndKeepsAllSourceFiles()
    {
        string plugin = Source("source/Comp.vst3");
        string clapSource = Source("source/Hall.clap");
        string source = Path.GetDirectoryName(plugin)!;
        string bundle = Vst3Wrapper("Comp", plugin);
        string clap = ClapWrapper("Hall", clapSource);
        string resource = Source("source/Resources/preset.txt", [1, 2, 3]);
        Directory.CreateSymbolicLink(Path.Combine(bundle, "Contents", "Resources"), Path.GetDirectoryName(resource)!);
        string unrelated = Vst3Wrapper("Other", Source("other/Other.vst3"));
        string unrelatedClap = ClapWrapper("Other", Source("other/Other.clap"));
        var installer = Installer([source], wine: false);

        InstallOutcome result = installer.RemoveWindowsFolder(source);

        Assert.True(result.Ok, result.Message);
        Assert.Empty(File.ReadAllLines(Registry));
        Assert.False(Directory.Exists(bundle));
        Assert.False(File.Exists(clap));
        Assert.Null(new FileInfo(Path.ChangeExtension(clap, ".clap-win")).LinkTarget);
        Assert.True(Directory.Exists(unrelated));
        Assert.True(File.Exists(unrelatedClap));
        Assert.Equal(Windows, File.ReadAllBytes(plugin));
        Assert.Equal(Windows, File.ReadAllBytes(clapSource));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(resource));
        Assert.Equal(["list", "rm " + source, "list"], Calls);
        Assert.DoesNotContain(Calls, c => c.Contains("--prune"));
    }

    [Fact]
    public void AMissingRegisteredSourceCanStillBeRemoved()
    {
        string source = Path.Combine(_root, "gone");
        string bundle = Vst3Wrapper("Gone", Path.Combine(source, "Gone.vst3"));
        var installer = Installer([source]);

        InstallOutcome result = installer.RemoveWindowsFolder(source + "/");

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(bundle));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("rm")]
    public void RemovalNeverCleansWrappersWhenListingOrUnregisteringFails(string command)
    {
        string plugin = Source("source/Comp.vst3");
        string source = Path.GetDirectoryName(plugin)!;
        string bundle = Vst3Wrapper("Comp", plugin);
        var installer = Installer([source], fail: command);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Contains("test failure", result.Message);
        Assert.True(Directory.Exists(bundle));
        Assert.Equal([source], File.ReadAllLines(Registry));
    }

    [Fact]
    public void FailedSyncReportsPartialRemovalAndKeepsWrappers()
    {
        string plugin = Source("source/Comp.vst3");
        string source = Path.GetDirectoryName(plugin)!;
        string other = Path.GetDirectoryName(Source("other/Other.vst3"))!;
        string bundle = Vst3Wrapper("Comp", plugin);
        var installer = Installer([source, other], fail: "sync");
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Contains("removed from the list", result.Message);
        Assert.Contains("wrappers were kept", result.Message);
        Assert.True(Directory.Exists(bundle));
        Assert.Equal([other], File.ReadAllLines(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAnInUseWrapperIsRejectedBeforeAnyMutation(bool clap)
    {
        string plugin = Source(clap ? "source/Comp.clap" : "source/Comp.vst3");
        string source = Path.GetDirectoryName(plugin)!;
        string wrapper = clap ? ClapWrapper("Comp", plugin) : Vst3Wrapper("Comp", plugin);
        var installer = Installer([source]);
        InstallOutcome result = installer.RemoveWindowsFolder(source, [wrapper]);
        Assert.False(result.Ok);
        Assert.Contains("Remove the inserts", result.Message);
        Assert.Equal(["list"], Calls);
        Assert.Equal([source], File.ReadAllLines(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedOverlappingRegistrationKeepsTheWrapper(bool removingParent)
    {
        string plugin = Source("source/child/Comp.vst3");
        string child = Path.GetDirectoryName(plugin)!;
        string parent = Path.GetDirectoryName(child)!;
        string bundle = Vst3Wrapper("Comp", plugin);
        var installer = Installer([parent, child]);
        InstallOutcome result = installer.RemoveWindowsFolder(removingParent ? parent : child);
        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(bundle));
        Assert.Equal([removingParent ? child : parent], File.ReadAllLines(Registry));
        Assert.Equal("sync", Calls[^1]);
    }

    [Fact]
    public void SyncCanRetargetADuplicateWrapperToARetainedSource()
    {
        string first = Source("source/Comp.vst3");
        string other = Source("other/Comp.vst3");
        string bundle = Vst3Wrapper("Comp", first);
        string link = Path.Combine(bundle, "Contents", "x86_64-win", "Comp.vst3");
        var installer = Installer([Path.GetDirectoryName(first)!, Path.GetDirectoryName(other)!],
            sync: $"ln -sfn '{other}' '{link}'");
        InstallOutcome result = installer.RemoveWindowsFolder(Path.GetDirectoryName(first)!);
        Assert.True(result.Ok, result.Message);
        Assert.True(Directory.Exists(bundle));
        Assert.Equal(other, new FileInfo(link).LinkTarget);
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void SiblingNamesAreNotMistakenForTheRemovedFolder()
    {
        string first = Source("plugins/One.vst3");
        string second = Source("plugins-other/Two.vst3");
        string one = Vst3Wrapper("One", first);
        string two = Vst3Wrapper("Two", second);
        var installer = Installer([Path.GetDirectoryName(first)!]);
        Assert.True(installer.RemoveWindowsFolder(Path.GetDirectoryName(first)!).Ok);
        Assert.False(Directory.Exists(one));
        Assert.True(Directory.Exists(two));
    }

    [Fact]
    public void UnregisteredAndRelativePathsNeverMutateTheRegistry()
    {
        string source = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        var installer = Installer([source]);
        Assert.False(installer.RemoveWindowsFolder("relative").Ok);
        Assert.False(installer.RemoveWindowsFolder(source + "-other").Ok);
        Assert.Equal(["list"], Calls);
        Assert.Equal([source], File.ReadAllLines(Registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolicWrapperRootsAndBundlesAreRefused(bool rootLink)
    {
        string source = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        string original = Source("original/keep.txt");
        Directory.CreateDirectory(Vst3);
        if (rootLink)
        {
            Directory.Delete(Vst3);
            Directory.CreateSymbolicLink(Vst3, Path.GetDirectoryName(original)!);
        }
        else Directory.CreateSymbolicLink(Path.Combine(Vst3, "Comp.vst3"), Path.GetDirectoryName(original)!);
        var installer = Installer([source]);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Contains("symbolic link", result.Message);
        Assert.Equal(["list"], Calls);
        Assert.True(File.Exists(original));
        Assert.Equal([source], File.ReadAllLines(Registry));
    }

    [Fact]
    public void UnexpectedFilesInABundleAreNotDeleted()
    {
        string plugin = Source("source/Comp.vst3");
        string source = Path.GetDirectoryName(plugin)!;
        string bundle = Vst3Wrapper("Comp", plugin);
        string original = Path.Combine(bundle, "original.dll");
        File.WriteAllBytes(original, Windows);
        var installer = Installer([source]);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Equal(["list"], Calls);
        Assert.Equal(Windows, File.ReadAllBytes(original));
    }

    [Fact]
    public void AWindowsClapFileIsNeverDeletedAsANativeWrapper()
    {
        string plugin = Source("source/Comp.clap");
        string source = Path.GetDirectoryName(plugin)!;
        string wrapper = ClapWrapper("Comp", plugin);
        File.WriteAllBytes(wrapper, Windows);
        var installer = Installer([source]);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Equal(["list"], Calls);
        Assert.Equal(Windows, File.ReadAllBytes(wrapper));
    }

    [Fact]
    public void MissingYabridgeAndWineProduceUsefulAnswers()
    {
        string source = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        var none = new PluginInstaller(Path.Combine(_root, "lv2"), Path.Combine(_root, "clap"), Path.Combine(_root, "vst3"), null, null);
        Assert.Contains("yabridge and Wine are not installed", none.AddWindowsFolder(source).Message);
        Assert.Contains("yabridge is not installed", none.RemoveWindowsFolder(source).Message);
        Assert.False(File.Exists(Path.Combine(_root, "calls")));
    }

    [Fact]
    public void ALinkedContentsDirectoryCannotRedirectCleanupToOriginalFiles()
    {
        string plugin = Source("source/Comp.vst3");
        string source = Path.GetDirectoryName(plugin)!;
        string bundle = Vst3Wrapper("Comp", plugin);
        string contents = Path.Combine(bundle, "Contents");
        string moved = Path.Combine(_root, "original-contents");
        Directory.Move(contents, moved);
        Directory.CreateSymbolicLink(contents, moved);
        var installer = Installer([source]);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Contains("symbolic link", result.Message);
        Assert.Equal(["list"], Calls);
        Assert.True(File.Exists(Path.Combine(moved, "x86_64-linux", "Comp.so")));
        Assert.True(File.Exists(plugin));
    }

    [Fact]
    public void MissingWineOnlyBlocksRemovalWhenOtherFoldersNeedSyncing()
    {
        string source = Path.GetDirectoryName(Source("source/Comp.vst3"))!;
        string other = Path.GetDirectoryName(Source("other/Other.vst3"))!;
        var installer = Installer([source, other], wine: false);
        InstallOutcome result = installer.RemoveWindowsFolder(source);
        Assert.False(result.Ok);
        Assert.Contains("Wine is not installed", result.Message);
        Assert.Equal([source, other], File.ReadAllLines(Registry));
        Assert.Equal(["list"], Calls);
    }
}
