using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _lv2, _clap, _vst3, _picked;

    public PluginInstallerTests()
    {
        _lv2 = Path.Combine(_root, "home", ".lv2");
        _clap = Path.Combine(_root, "home", ".clap");
        _vst3 = Path.Combine(_root, "home", ".vst3");
        _picked = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(_picked);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly byte[] Elf = [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0];
    private static readonly byte[] Windows = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0];

    private string File_(string relative, byte[] head)
    {
        string path = Path.Combine(_picked, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, head);
        return path;
    }

    private string LinuxVst3(string name)
    {
        string bundle = Path.Combine(_picked, name);
        File_(Path.Combine(name, "Contents", "x86_64-linux", Path.GetFileNameWithoutExtension(name) + ".so"), Elf);
        File.WriteAllText(Path.Combine(bundle, "Contents", "moduleinfo.json"), "{}");
        return bundle;
    }

    private string WindowsVst3(string name)
    {
        string bundle = Path.Combine(_picked, name);
        File_(Path.Combine(name, "Contents", "x86_64-win", Path.GetFileNameWithoutExtension(name) + ".vst3"), Windows);
        return bundle;
    }

    private string Lv2(string name)
    {
        string bundle = Path.Combine(_picked, name);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), "@prefix lv2: <http://lv2plug.in/ns/lv2core#> .");
        File_(Path.Combine(name, "plugin.so"), Elf);
        return bundle;
    }

    private PluginInstaller Installer(string? yabridgectl = null, string? wine = null, bool host = true)
        => new(_lv2, _clap, _vst3, yabridgectl, wine, host);

    [Fact]
    public void WhatAPathIsComesFromItsNameAndItsFirstBytes()
    {
        Assert.Equal(PluginItemKind.ClapBundle, PluginInstaller.Inspect(File_("Hall.clap", Elf)).Kind);
        Assert.Equal(PluginItemKind.WindowsPlugin, PluginInstaller.Inspect(File_("Hall-win.clap", Windows)).Kind);
        Assert.Equal(PluginItemKind.Vst3Bundle, PluginInstaller.Inspect(File_("Old.vst3", Elf)).Kind);
        Assert.Equal(PluginItemKind.WindowsPlugin, PluginInstaller.Inspect(File_("Old-win.vst3", Windows)).Kind);
        Assert.Equal(PluginItemKind.WindowsVst2, PluginInstaller.Inspect(File_("Synth.dll", Windows)).Kind);
        Assert.Equal(PluginItemKind.Vst3Bundle, PluginInstaller.Inspect(LinuxVst3("Comp.vst3")).Kind);
        Assert.Equal(PluginItemKind.WindowsPlugin, PluginInstaller.Inspect(WindowsVst3("Comp-win.vst3")).Kind);
        Assert.Equal(PluginItemKind.Lv2Bundle, PluginInstaller.Inspect(Lv2("gate.lv2")).Kind);
        Assert.Equal(PluginItemKind.Archive, PluginInstaller.Inspect(File_("Plugin-1.0.zip", [0x50, 0x4b])).Kind);
        Assert.Equal(PluginItemKind.Archive, PluginInstaller.Inspect(File_("Plugin-1.0.tar.gz", [0x1f, 0x8b])).Kind);
        Assert.Equal(PluginItemKind.Installer, PluginInstaller.Inspect(File_("Setup.exe", Windows)).Kind);
        Assert.Equal(PluginItemKind.Unknown, PluginInstaller.Inspect(File_("readme.txt", [0x41])).Kind);
        Assert.Equal(PluginItemKind.Unknown, PluginInstaller.Inspect(File_("odd.clap", [0x41, 0x42, 0x43, 0x44])).Kind);
        // A .lv2 directory without a manifest is just a directory.
        string empty = Path.Combine(_picked, "empty.lv2");
        Directory.CreateDirectory(empty);
        Assert.Equal(PluginItemKind.Unknown, PluginInstaller.Inspect(empty).Kind);
    }

    [Fact]
    public void AFolderPickIsEverythingInstallableInside()
    {
        LinuxVst3(Path.Combine("set", "A.vst3"));
        File_(Path.Combine("set", "deeper", "B.clap"), Elf);
        File_(Path.Combine("set", "notes.txt"), [0x41]);
        File_(Path.Combine("set", "Setup.exe"), Windows);   // not what a folder pick means
        IReadOnlyList<PluginItem> items = PluginInstaller.Items(Path.Combine(_picked, "set"));
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == PluginItemKind.Vst3Bundle && i.Path.EndsWith("A.vst3"));
        Assert.Contains(items, i => i.Kind == PluginItemKind.ClapBundle && i.Path.EndsWith("B.clap"));
    }

    [Fact]
    public void LinuxBundlesAreCopiedIntoTheHomeDirectories()
    {
        string clap = File_("Hall.clap", Elf);
        string vst3 = LinuxVst3("Comp.vst3");
        string lv2 = Lv2("gate.lv2");
        PluginInstaller installer = Installer();

        InstallOutcome one = installer.Install(clap);
        Assert.True(one.Ok, one.Message);
        Assert.Equal(["Hall.clap"], one.Installed);
        Assert.True(File.Exists(Path.Combine(_clap, "Hall.clap")));
        Assert.Contains("Installed Hall.clap", one.Message);
        Assert.Equal([Path.Combine(_clap, "Hall.clap")], one.Destinations);

        InstallOutcome two = installer.Install(vst3);
        Assert.True(two.Ok, two.Message);
        Assert.True(File.Exists(Path.Combine(_vst3, "Comp.vst3", "Contents", "x86_64-linux", "Comp.so")));
        Assert.True(File.Exists(Path.Combine(_vst3, "Comp.vst3", "Contents", "moduleinfo.json")));

        InstallOutcome three = installer.Install(lv2);
        Assert.True(three.Ok, three.Message);
        Assert.True(File.Exists(Path.Combine(_lv2, "gate.lv2", "manifest.ttl")));

        // Installing again replaces what is there rather than failing on it.
        File.WriteAllBytes(clap, [.. Elf, 1, 2, 3]);
        Assert.True(installer.Install(clap).Ok);
        Assert.Equal(Elf.Length + 3, new FileInfo(Path.Combine(_clap, "Hall.clap")).Length);
    }

    [Fact]
    public void ABundleAlreadyInPlaceIsOnlyRescanned()
    {
        Directory.CreateDirectory(_clap);
        string inPlace = Path.Combine(_clap, "Hall.clap");
        File.WriteAllBytes(inPlace, Elf);
        InstallOutcome outcome = Installer().Install(inPlace);
        Assert.True(outcome.Ok);
        Assert.Equal(["Hall.clap"], outcome.Installed);
        Assert.Equal("", outcome.Message);   // nothing was copied, and nothing went wrong
    }

    [Fact]
    public void WhatCannotBeInstalledGetsAnAnswerThatSaysWhatToDo()
    {
        PluginInstaller installer = Installer();
        InstallOutcome archive = installer.Install(File_("Plugin-1.0.zip", [0x50, 0x4b]));
        Assert.False(archive.Ok);
        Assert.Contains("Extract it first", archive.Message);

        InstallOutcome setup = installer.Install(File_("Setup.exe", Windows));
        Assert.False(setup.Ok);
        Assert.Contains("Run it with Wine", setup.Message);

        InstallOutcome vst2 = installer.Install(File_("Synth.dll", Windows));
        Assert.False(vst2.Ok);
        Assert.Contains("VST2", vst2.Message);

        InstallOutcome text = installer.Install(File_("readme.txt", [0x41]));
        Assert.False(text.Ok);
        Assert.Contains("Nothing to install", text.Message);

        Assert.False(installer.Install("relative/path.clap").Ok);
        Assert.False(installer.Install(Path.Combine(_picked, "missing.clap")).Ok);
    }

    [Fact]
    public void WindowsPluginsWithoutYabridgeSaySo()
    {
        string bundle = WindowsVst3("Comp-win.vst3");
        InstallOutcome none = Installer().Install(bundle);
        Assert.False(none.Ok);
        Assert.Equal("Comp-win.vst3 is a Windows plugin, and yabridge and Wine are not installed. Install them, then add it again.", none.Message);
        InstallOutcome folder = Installer().Install(Path.GetDirectoryName(bundle)!);
        Assert.Contains("Downloads holds Windows plugins", folder.Message);

        InstallOutcome noWine = Installer(yabridgectl: "/bin/true").Install(bundle);
        Assert.False(noWine.Ok);
        Assert.Contains("Wine is not installed", noWine.Message);
    }

    /// <summary>A yabridgectl that records its calls and answers like the real one.</summary>
    private string FakeYabridgectl(string knownDirectory = "")
    {
        string log = Path.Combine(_root, "yabridgectl.log");
        string script = Path.Combine(_root, "yabridgectl");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$@" >> "{{log}}"
            case "$1" in
              --version) echo "yabridgectl 5.1.1" ;;
              list) [ -n "{{knownDirectory}}" ] && echo "{{knownDirectory}}" ;;
              add) [ -d "$2" ] || { echo "not a directory" >&2; exit 1; } ;;
              sync) echo "Finished setting up 3 files" ;;
            esac
            exit 0
            """);
        if (!OperatingSystem.IsWindows())   // where these tests run; the analyser wants it said
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private string[] YabridgeCalls() => File.Exists(Path.Combine(_root, "yabridgectl.log"))
        ? File.ReadAllLines(Path.Combine(_root, "yabridgectl.log")) : [];

    [Fact]
    public void AWindowsPluginIsHandedToYabridgeByItsDirectory()
    {
        string bundle = WindowsVst3(Path.Combine("VST3", "Comp-win.vst3"));
        string directory = Path.GetDirectoryName(bundle)!;
        PluginInstaller installer = Installer(FakeYabridgectl(), wine: "/bin/true");

        InstallOutcome outcome = installer.Install(bundle);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("Bridged the Windows plugins", outcome.Message);
        Assert.Equal([$"list", $"add {directory}", "sync"], YabridgeCalls());
        Assert.Equal([Path.Combine(_vst3, "yabridge"), Path.Combine(_clap, "yabridge")], outcome.Destinations);

        // A picked folder of Windows plugins is the folder itself, added once.
        File_(Path.Combine("VST3", "Other.vst3"), Windows);
        File.Delete(Path.Combine(_root, "yabridgectl.log"));
        outcome = installer.Install(directory);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal([$"list", $"add {directory}", "sync"], YabridgeCalls());
    }

    [Fact]
    public void ADirectoryYabridgeKnowsIsOnlySynced()
    {
        string bundle = WindowsVst3(Path.Combine("VST3", "Comp-win.vst3"));
        string directory = Path.GetDirectoryName(bundle)!;
        PluginInstaller installer = Installer(FakeYabridgectl(directory), wine: "/bin/true");
        Assert.True(installer.Install(bundle).Ok);
        Assert.Equal(["list", "sync"], YabridgeCalls());

        InstallOutcome sync = installer.SyncWindows();
        Assert.True(sync.Ok, sync.Message);
        PluginSetup setup = installer.Setup();
        Assert.Equal("5.1.1", setup.YabridgeVersion);
        Assert.True(setup.Wine);
        Assert.Equal([directory], setup.WindowsDirectories);
    }

    [Fact]
    public void AMixedFolderInstallsTheLinuxPluginsAndBridgesTheRest()
    {
        string folder = Path.Combine(_picked, "mixed");
        File_(Path.Combine("mixed", "Hall.clap"), Elf);
        WindowsVst3(Path.Combine("mixed", "Comp-win.vst3"));
        File_(Path.Combine("mixed", "Old.dll"), Windows);
        InstallOutcome outcome = Installer(FakeYabridgectl(), wine: "/bin/true").Install(folder);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(File.Exists(Path.Combine(_clap, "Hall.clap")));
        Assert.Contains("Installed Hall.clap", outcome.Message);
        Assert.Contains("Bridged the Windows plugins", outcome.Message);
        Assert.Contains($"add {folder}", YabridgeCalls());
    }

    [Fact]
    public void WithoutTheNativeHostAClapInstallSaysItCannotRun()
    {
        InstallOutcome outcome = Installer(host: false).Install(File_("Hall.clap", Elf));
        Assert.True(outcome.Ok);
        Assert.Contains("native plugin host is not installed", outcome.Message);
    }

    [Fact]
    public void AToolIsFoundWhereItIsInstalledByHand()
    {
        // The daemon is a user service: its PATH is systemd's, not a login
        // shell's, so a tarball install has to be found without one.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tarball = Path.Combine(home, ".local", "share", "yabridge");
        string name = "openxlr-test-tool-" + Guid.NewGuid().ToString("N");
        Assert.Null(PluginInstaller.OnPath(name));
        Directory.CreateDirectory(tarball);
        string tool = Path.Combine(tarball, name);
        try
        {
            File.WriteAllText(tool, "#!/bin/sh\n");
            Assert.Equal(tool, PluginInstaller.OnPath(name));
        }
        finally { File.Delete(tool); }
    }

    [Fact]
    public void WhatAWindowsInstallerLeftInWineIsOffered()
    {
        string prefix = Path.Combine(_root, "wine");
        string vst3 = Path.Combine(prefix, "drive_c", "Program Files", "Common Files", "VST3");
        string clap = Path.Combine(prefix, "drive_c", "Program Files", "Common Files", "CLAP");
        string thirtyTwo = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Common Files", "VST3");
        Directory.CreateDirectory(clap);
        File.WriteAllBytes(Path.Combine(clap, "Thing.clap"), Windows);
        Directory.CreateDirectory(Path.Combine(vst3, "Nova.vst3"));
        // What an installer writes: the same plugin in both builds. Only the
        // 64-bit folder is worth bridging.
        Directory.CreateDirectory(thirtyTwo);
        File.WriteAllBytes(Path.Combine(thirtyTwo, "Nova.vst3"), Windows);

        PluginInstaller withYabridge = new(_lv2, _clap, _vst3, FakeYabridgectl(), wine: "/bin/true", hostInstalled: true, winePrefix: prefix);
        Assert.Equal([vst3, clap], withYabridge.WinePluginFolders());   // the 32-bit copy is not offered
        Assert.Equal([vst3, clap], withYabridge.Setup().WineFolders);

        // Nothing to offer when the tools that would use it are missing.
        Assert.Empty(new PluginInstaller(_lv2, _clap, _vst3, null, null, true, prefix).Setup().WineFolders);

        // A folder already bridged is not offered again.
        PluginInstaller bridged = new(_lv2, _clap, _vst3, FakeYabridgectl(vst3), wine: "/bin/true", hostInstalled: true, winePrefix: prefix);
        Assert.Equal([clap], bridged.Setup().WineFolders);
    }

    [Fact]
    public void A32BitPluginWithNo64BitCopyIsStillOffered()
    {
        string prefix = Path.Combine(_root, "wine32");
        string sixtyFour = Path.Combine(prefix, "drive_c", "Program Files", "Common Files", "VST3");
        string thirtyTwo = Path.Combine(prefix, "drive_c", "Program Files (x86)", "Common Files", "VST3");
        Directory.CreateDirectory(sixtyFour);
        File.WriteAllBytes(Path.Combine(sixtyFour, "Nova.vst3"), Windows);
        Directory.CreateDirectory(thirtyTwo);
        File.WriteAllBytes(Path.Combine(thirtyTwo, "Nova.vst3"), Windows);
        File.WriteAllBytes(Path.Combine(thirtyTwo, "OldThing.vst3"), Windows);

        PluginInstaller installer = new(_lv2, _clap, _vst3, FakeYabridgectl(), wine: "/bin/true", hostInstalled: true, winePrefix: prefix);
        Assert.Equal([sixtyFour, thirtyTwo], installer.WinePluginFolders());
    }

    [Fact]
    public void WithoutAWinePrefixThereIsNothingToOffer()
    {
        PluginInstaller none = new(_lv2, _clap, _vst3, FakeYabridgectl(), wine: "/bin/true", hostInstalled: true,
            winePrefix: Path.Combine(_root, "no-such-prefix"));
        Assert.Empty(none.WinePluginFolders());
    }

    [Fact]
    public void TheVersionsThatLeaveABridgedEditorDeafAreRecognised()
    {
        // Wine 9.22 changed how a plugin window's position is tracked, and
        // released yabridge does not carry the fix, so every click lands far
        // from where it was aimed.
        Assert.True(PluginInstaller.EditorsIgnoreTheMouse("5.1.1", "wine-11.17"));
        Assert.True(PluginInstaller.EditorsIgnoreTheMouse("5.1.1", "wine-9.22 (Staging)"));
        Assert.True(PluginInstaller.EditorsIgnoreTheMouse("5.0.4", "wine-10.0"));

        Assert.False(PluginInstaller.EditorsIgnoreTheMouse("5.1.1", "wine-9.21"));
        Assert.False(PluginInstaller.EditorsIgnoreTheMouse("5.1.1", "wine-8.21"));
        Assert.False(PluginInstaller.EditorsIgnoreTheMouse("5.2.0", "wine-11.17"));   // the fix, once released
        Assert.False(PluginInstaller.EditorsIgnoreTheMouse("6.0.0", "wine-11.17"));
        Assert.False(PluginInstaller.EditorsIgnoreTheMouse(null, "wine-11.17"));      // no yabridge, no bridging
        Assert.False(PluginInstaller.EditorsIgnoreTheMouse("5.1.1", null));

        // Whatever a version looks like, only its first two numbers count.
        Assert.True(PluginInstaller.AtLeast("wine-11.17", 9, 22));
        Assert.True(PluginInstaller.AtLeast("yabridgectl 5.1.1", 5, 1));
        Assert.False(PluginInstaller.AtLeast("yabridgectl 5.1.1", 5, 2));
        Assert.False(PluginInstaller.AtLeast("installed", 5, 2));
        Assert.False(PluginInstaller.AtLeast("", 1, 0));
    }

    [Fact]
    public void TheSetupNamesTheDirectoriesAndWhatIsMissing()
    {
        PluginSetup setup = Installer().Setup();
        Assert.Null(setup.YabridgeVersion);
        Assert.False(setup.Wine);
        Assert.Empty(setup.WindowsDirectories);
        Assert.EndsWith(".clap", setup.ClapDirectory);
        Assert.True(setup.HostInstalled);
    }

    [Fact]
    public void ARefreshableForgetsItsValueOnReset()
    {
        int computed = 0;
        var value = new Refreshable<int>(() => ++computed);
        Assert.Equal(1, value.Value);
        Assert.Equal(1, value.Value);
        value.Reset();
        Assert.Equal(2, value.Value);
        Assert.Equal(2, computed);
    }

    [Fact]
    public void TheInstallCommandCarriesThePath()
    {
        Command? cmd = JsonSerializer.Deserialize<Command>("""{"cmd":"installPlugin","path":"/home/me/Downloads/Hall.clap","requestId":"r1"}""");
        Assert.NotNull(cmd);
        Assert.Equal("/home/me/Downloads/Hall.clap", cmd.Path);
        var message = new PluginInstallMessage(true, "Installed Hall.clap in ~/.clap.", ["Hall.clap"], 1, 278);
        string json = JsonSerializer.Serialize(message);
        Assert.Contains("\"type\":\"pluginInstall\"", json);
        Assert.Contains("\"added\":1", json);
        var setup = new PluginSetupMessage(new PluginSetup(true, "~/.lv2", "~/.clap", "~/.vst3", "5.1.1", true, ["/home/me/.wine/drive_c/VST3"], ["/home/me/.wine/drive_c/Program Files/Common Files/CLAP"]));
        string setupJson = JsonSerializer.Serialize(setup);
        Assert.Contains("\"yabridge\":\"5.1.1\"", setupJson);
        Assert.Contains("\"wineFolders\":[\"/home/me/.wine/drive_c/Program Files/Common Files/CLAP\"]", setupJson);
        Assert.DoesNotContain("\"Setup\"", setupJson);
    }

    [Fact]
    public void TheOptionsLineOnWindowsPluginsSaysWhatIsMissing()
    {
        Assert.Contains("yabridge and Wine are not installed", OptionsViewModel.WindowsLine(null, false, 0));
        Assert.Contains("yabridge is not", OptionsViewModel.WindowsLine(null, true, 0));
        Assert.Contains("Wine is not", OptionsViewModel.WindowsLine("5.1.1", false, 0));
        Assert.Contains("no folder bridged yet", OptionsViewModel.WindowsLine("5.1.1", true, 0));
        Assert.Contains("2 folders bridged", OptionsViewModel.WindowsLine("5.1.1", true, 2));
    }
}
