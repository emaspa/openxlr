using System.Buffers.Binary;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// What moving from the yabridge companion to OpenXLR's Windows plugin host
/// needs: the one-time copy of the companion's folders, installs with no
/// yabridge, and the doctor's list of what would go with the companion.
/// Every folder, registry and Wine prefix is this test's own, every plugin is
/// invented, and nothing here starts Wine or yabridge or loads plugin code.
/// </summary>
[Collection("xdg-config")]
public sealed class YabridgeMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-yabridge-migration-").FullName;
    private readonly Dictionary<string, string?> _before = new();

    public YabridgeMigrationTests()
    {
        Set("XDG_CONFIG_HOME", Directory.CreateDirectory(Path.Combine(_root, "config")).FullName);
        Set("XDG_DATA_HOME", Directory.CreateDirectory(Path.Combine(_root, "data")).FullName);
        Set("CLAP_PATH", Directory.CreateDirectory(Path.Combine(_root, "clap")).FullName);
        Set("VST3_PATH", Directory.CreateDirectory(Path.Combine(_root, "vst3")).FullName);
        Set("WINEPREFIX", Prefix);
        // The companion of this machine, if it has one, is never consulted.
        Set("OPENXLR_YABRIDGE", "system");
        Set(WindowsPluginHost.WineVariable, null);
        Set("WINELOADER", null);
        Directory.CreateDirectory(Path.Combine(Prefix, "dosdevices"));
        Directory.CreateDirectory(Path.Combine(CommonFiles, "VST3"));
    }

    private string Prefix => Path.Combine(_root, "prefix");
    private string CommonFiles => Path.Combine(Prefix, "drive_c", "Program Files", "Common Files");
    private static string DaemonJson => OpenXLR.Core.OpenXlrPaths.ConfigFile("daemon.json");

    private void Set(string name, string? value)
    {
        _before.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _before) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, recursive: true);
    }

    private static string WriteModule(string path, ushort machine = 0x8664)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image = new byte[0x80];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        image[0x3c] = 0x40;
        "PE\0\0"u8.CopyTo(image.AsSpan(0x40));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x44), machine);
        File.WriteAllBytes(path, image);
        return path;
    }

    private static string Vst3Bundle(string folder, string name, ushort machine = 0x8664)
    {
        string bundle = Path.Combine(folder, name + ".vst3");
        WriteModule(Path.Combine(bundle, "Contents", "x86_64-win", name + ".vst3"), machine);
        return bundle;
    }

    /// <summary>A yabridge VST3 wrapper: the bridge's library and a link to the module, which may not exist.</summary>
    private string Vst3Wrapper(string name, string module, string architecture = "x86_64-win")
    {
        string bundle = Path.Combine(_root, "data", "openxlr", "yabridge", "vst3", name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-linux", name + ".so"), "bridge");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", architecture));
        File.CreateSymbolicLink(Path.Combine(bundle, "Contents", architecture, name + ".vst3"), module);
        return bundle;
    }

    private static PluginInfo Plugin(string id, string name, string bundle, bool windowsHostOnly = false)
        => new("vst3", id, name, "", 2, 2, "", "", [], [], [], []) { Path = bundle, WindowsHostOnly = windowsHostOnly };

    private static ChainInsert Insert(string id, string plugin, string chainName = "XLR 1")
        => new("mic", chainName, new InsertDefinition { Id = id, Kind = "vst3", Plugin = plugin });

    private static string Registry(params string[] folders)
    {
        string path = YabridgeMigration.RegistryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "yabridge_home = '/usr/lib/openxlr/yabridge'\nplugin_dirs = [\n"
            + string.Concat(folders.Select(f => $"    '{f}',\n")) + "]\nvst2_location = 'centralized'\nno_verify = false\nblacklist = []\n\n"
            + "[last_known_config]\nwine_version = 'wine-11.20'\nyabridge_host_hash = 1734701445371201188\n");
        return path;
    }

    // --- the registry ---------------------------------------------------------------

    [Fact]
    public void TheRegistryIsReadAsYabridgectlWritesIt()
    {
        Assert.Equal(["/a/one", "/b/two"], YabridgeMigration.PluginDirs("""
            yabridge_home = '/usr/lib/openxlr/yabridge'
            plugin_dirs = [
                '/a/one',
                '/b/two',
            ]
            vst2_location = 'centralized'
            no_verify = false
            blacklist = []

            [last_known_config]
            plugin_dirs = ['/not/this/one']
            """));
        // A basic string with escapes, a comment, keys in another order, and an inline table before it.
        Assert.Equal(["/it's here", "/tab\there", "/\u00e9"], YabridgeMigration.PluginDirs(
            "# yabridgectl\nother = { a = 1, b = \"x\" } # note\nplugin_dirs = [\"/it's here\", # first\n  \"/tab\\there\", \"/\\u00e9\"]\n"));
        Assert.Empty(YabridgeMigration.PluginDirs("no_verify = false\n"));
        Assert.Empty(YabridgeMigration.PluginDirs("plugin_dirs = []"));
        foreach (string broken in new[] { "plugin_dirs = ['/a'", "plugin_dirs = '/a'", "plugin_dirs = [1]", "plugin_dirs = ['''/a''']",
                     "plugin_dirs = ['/a' '/b']", "plugin_dirs", "plugin_dirs = [\"/a\\q\"]" })
            Assert.Throws<FormatException>(() => YabridgeMigration.PluginDirs(broken));

        Assert.Empty(YabridgeMigration.ReadRegistry(Path.Combine(_root, "absent.toml"), out string? none));
        Assert.Null(none);
        File.WriteAllText(Path.Combine(_root, "broken.toml"), "plugin_dirs = [");
        Assert.Empty(YabridgeMigration.ReadRegistry(Path.Combine(_root, "broken.toml"), out string? problem));
        Assert.StartsWith("The yabridge folder registry cannot be read: ", problem);
    }

    // --- the one-time copy -------------------------------------------------------------

    [Fact]
    public void TheCompanionsFoldersAreCopiedOnceAndThoseAlreadySearchedArePassedOver()
    {
        string imported = Directory.CreateDirectory(Path.Combine(WindowsPluginHost.ImportDirectory, "vst3", "Kotelnikov")).FullName;
        string elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere", "Plugins")).FullName;
        string inside = Directory.CreateDirectory(Path.Combine(elsewhere, "Vendor")).FullName;
        string native = Directory.CreateDirectory(Path.Combine(_root, "clap", "bridged")).FullName;
        string missing = Path.Combine(_root, "gone");
        string registry = Registry(imported, Path.Combine(CommonFiles, "VST3"), elsewhere, inside, native, missing, "/c:/plugins");
        byte[] registryBefore = File.ReadAllBytes(registry);

        // A build without the host, or without the companion, copies nothing and writes nothing.
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: false, bridge: true));
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: false));
        Assert.False(File.Exists(DaemonJson));

        BridgeFolderCopy copy = Assert.IsType<BridgeFolderCopy>(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true));
        Assert.Equal([elsewhere], copy.Added);
        Assert.Equal([native, missing, "/c:/plugins"], copy.NotAdded!.Select(f => f.Path));
        Assert.Equal(["This folder is inside, or holds, a folder that is already searched.", "The folder does not exist or cannot be read.",
            "The path is not absolute, or it holds a colon or a control character."], copy.NotAdded!.Select(f => f.Reason));
        Assert.Equal([new PluginFolder("windows", elsewhere)], PluginSearchPaths.Read(out _));
        Assert.Contains(elsewhere, WindowsPluginHost.Directories());
        BridgeFolderCopy saved = DaemonSettings.Load().YabridgeFolderCopy!;
        Assert.Equal(copy.Added, saved.Added);
        Assert.Equal(copy.NotAdded, saved.NotAdded);
        // The registry is read, never written.
        Assert.Equal(registryBefore, File.ReadAllBytes(registry));

        // Once only: a second start adds nothing, even after the user removed the folder.
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true));
        Assert.True(PluginSearchPaths.Change("windows", elsewhere, add: false).Ok);
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true));
        Assert.Empty(PluginSearchPaths.Read(out _));
    }

    [Fact]
    public void TheCopyKeepsTheLimitAndWhatDaemonJsonAlreadyHeld()
    {
        string extra = Directory.CreateDirectory(Path.Combine(_root, "elsewhere", "Plugins")).FullName;
        Registry(extra);
        Directory.CreateDirectory(Path.GetDirectoryName(DaemonJson)!);
        File.WriteAllText(DaemonJson, JsonSerializer.Serialize(new
        {
            submixer = true,
            futureKey = "kept",
            pluginFolders = Enumerable.Range(0, PluginSearchPaths.MaxPaths).Select(i => new { kind = "lv2", path = $"/lv2/{i}" }),
        }));
        BridgeFolderCopy copy = YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true)!;
        Assert.Empty(copy.Added!);
        Assert.Equal($"At most {PluginSearchPaths.MaxPaths} plugin folders can be added.", Assert.Single(copy.NotAdded!).Reason);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(DaemonJson));
        Assert.Equal("kept", saved.RootElement.GetProperty("futureKey").GetString());
        Assert.True(saved.RootElement.GetProperty("submixer").GetBoolean());
        Assert.Equal(PluginSearchPaths.MaxPaths, saved.RootElement.GetProperty("pluginFolders").GetArrayLength());
        Assert.True(saved.RootElement.TryGetProperty("yabridgeFolderCopy", out _));
    }

    [Fact]
    public void AnUnreadableRegistryOrDaemonJsonIsNeverWrittenOver()
    {
        string registry = YabridgeMigration.RegistryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, "plugin_dirs = [ 'unclosed");
        // daemon.json that cannot be read: nothing is copied, nothing is written, and the next start tries again.
        Directory.CreateDirectory(Path.GetDirectoryName(DaemonJson)!);
        File.WriteAllText(DaemonJson, "{ not json");
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true));
        Assert.Equal("{ not json", File.ReadAllText(DaemonJson));

        // A registry that cannot be read is named, and the copy counts as done.
        File.Delete(DaemonJson);
        BridgeFolderCopy copy = YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true)!;
        FolderNotCopied problem = Assert.Single(copy.NotAdded!);
        Assert.Equal(registry, problem.Path);
        Assert.StartsWith("The yabridge folder registry cannot be read: ", problem.Reason);
        Assert.Null(DaemonSettings.Load().PluginFolders);
        Assert.Equal("plugin_dirs = [ 'unclosed", File.ReadAllText(registry));

        // With no registry at all the copy also runs, and records that it did.
        File.Delete(DaemonJson);
        File.Delete(registry);
        copy = YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true)!;
        Assert.Empty(copy.Added!);
        Assert.Empty(copy.NotAdded!);
        Assert.Null(YabridgeMigration.CopyFoldersOnce(windowsHost: true, bridge: true));
    }

    // --- installing with no yabridge -------------------------------------------------------

    [Fact]
    public void InstallFileAndInstallFolderNeedOnlyTheWindowsHostAndWine()
    {
        string downloads = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        string home = Path.Combine(_root, "home");
        var installer = new PluginInstaller(Path.Combine(home, ".lv2"), Path.Combine(home, ".clap"), Path.Combine(home, ".vst3"),
            yabridgectl: null, wine: "/bin/true", hostInstalled: true, windowsHost: true);

        // One plugin: copied into the import directory, which the host searches, and nothing else written.
        string bundle = Vst3Bundle(downloads, "Comp");
        InstallOutcome one = installer.Install(bundle);
        Assert.True(one.Ok, one.Message);
        string copied = Path.Combine(WindowsPluginHost.ImportDirectory, "vst3", "Comp");
        Assert.True(File.Exists(Path.Combine(copied, "Comp.vst3", "Contents", "x86_64-win", "Comp.vst3")));
        Assert.Equal([copied], one.Destinations);
        Assert.EndsWith("Comp.vst3 runs on OpenXLR's Windows plugin host.", one.Message);
        Assert.Contains(WindowsPluginHost.ImportDirectory, WindowsPluginHost.Directories());
        Assert.False(File.Exists(DaemonJson));

        // A folder of them stays in place and becomes a Windows plugin folder.
        string vendor = Directory.CreateDirectory(Path.Combine(_root, "Vendor")).FullName;
        Vst3Bundle(vendor, "Verb");
        Vst3Bundle(vendor, "Gate");
        InstallOutcome folder = installer.Install(vendor);
        Assert.True(folder.Ok, folder.Message);
        Assert.Equal([new PluginFolder("windows", vendor)], PluginSearchPaths.Read(out _));
        Assert.Equal([vendor], folder.Destinations);
        Assert.EndsWith("run on OpenXLR's Windows plugin host.", folder.Message);

        // A folder the host already searches is left as it is, and so is the list.
        Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Nova");
        Assert.True(installer.Install(Path.Combine(CommonFiles, "VST3")).Ok);
        Assert.True(installer.AddWindowsFolder(vendor).Ok);
        Assert.Single(PluginSearchPaths.Read(out _));

        // Wine is still needed, and the answer says only that.
        var noWine = new PluginInstaller(Path.Combine(home, ".lv2"), Path.Combine(home, ".clap"), Path.Combine(home, ".vst3"),
            yabridgectl: null, wine: null, hostInstalled: true, windowsHost: true);
        InstallOutcome refused = noWine.Install(bundle);
        Assert.False(refused.Ok);
        Assert.Equal("Comp.vst3 is a Windows plugin, and Wine is not installed. Install it, then add it again.", refused.Message);
    }

    // --- what the companion alone keeps -----------------------------------------------------

    [Fact]
    public void TheInsertsThatNeedAWrapperAreThoseWhoseModuleNoWindowsFolderHolds()
    {
        string covered = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Nova");
        string outside = Vst3Bundle(Path.Combine(_root, "elsewhere"), "Kotelnikov");
        string narrow = Vst3Bundle(Path.Combine(_root, "elsewhere"), "Old", machine: 0x014c);
        var catalogue = new Dictionary<string, PluginInfo>
        {
            ["A"] = Plugin("A", "Nova", Vst3Wrapper("Nova", Path.Combine(covered, "Contents", "x86_64-win", "Nova.vst3"))),
            ["B"] = Plugin("B", "Kotelnikov", Vst3Wrapper("Kotelnikov", Path.Combine(outside, "Contents", "x86_64-win", "Kotelnikov.vst3"))),
            ["C"] = Plugin("C", "Old", Vst3Wrapper("Old", Path.Combine(narrow, "Contents", "x86_64-win", "Old.vst3"))),
            ["D"] = Plugin("D", "Gone", Vst3Wrapper("Gone", Path.Combine(_root, "nowhere", "Gone.vst3"))),
            ["E"] = Plugin("E", "Ancient", Vst3Wrapper("Ancient", Path.Combine(_root, "nowhere", "Ancient.vst3"), architecture: "x86-win")),
            ["F"] = Plugin("F", "Direct", covered, windowsHostOnly: true),
            ["G"] = Plugin("G", "Native", Directory.CreateDirectory(Path.Combine(_root, "vst3", "Native.vst3")).FullName),
        };
        PluginInfo? Find(string kind, string id) => kind == "vst3" ? catalogue.GetValueOrDefault(id) : null;
        ChainInsert[] inserts = [.. "ABCDEFGH".Select(id => Insert("i" + id, id.ToString()))];

        IReadOnlyList<BridgeOnlyInsert> bridgeOnly = YabridgeMigration.BridgeOnlyInserts(inserts, Find);
        Assert.Equal(["iB", "iC", "iD", "iE"], bridgeOnly.Select(i => i.Id));
        Assert.Equal(["outside-windows-folders", "not-64-bit", "module-missing", "not-64-bit"], bridgeOnly.Select(i => i.Reason));
        Assert.Equal(Path.Combine(outside, "Contents", "x86_64-win", "Kotelnikov.vst3"), bridgeOnly[0].Module);
        Assert.Equal(catalogue["B"].Path, bridgeOnly[0].Wrapper);
        Assert.Equal(["iA", "iB", "iC", "iD", "iE", "iF"], YabridgeMigration.WindowsInserts(inserts, Find).Select(i => i.Id));

        // Adding the folder as a Windows plugin folder takes the insert off the list.
        Assert.True(PluginSearchPaths.Change("windows", Path.Combine(_root, "elsewhere"), add: true).Ok);
        Assert.Equal(["iC", "iD", "iE"], YabridgeMigration.BridgeOnlyInserts(inserts, Find).Select(i => i.Id));
    }

    [Fact]
    public void TheDoctorListsWhatTheCompanionAloneKeepsAndAMissingWine()
    {
        string outside = Vst3Bundle(Path.Combine(_root, "elsewhere"), "Kotelnikov");
        PluginInfo plugin = Plugin("B", "Kotelnikov", Vst3Wrapper("Kotelnikov", Path.Combine(outside, "Contents", "x86_64-win", "Kotelnikov.vst3")));
        ChainInsert[] inserts = [Insert("one", "B", "XLR 1"), Insert("two", "B", "Stream")];
        Directory.CreateDirectory(Path.GetDirectoryName(DaemonJson)!);
        new DaemonSettings { YabridgeFolderCopy = new([], [new("/plugins/Vendor", "The folder does not exist or cannot be read.")]) }.Save();
        string home = Path.Combine(_root, "home");

        JsonElement Reply(bool windowsHost) => JsonSerializer.SerializeToElement(new
        {
            apiVersion = "1", ok = true,
            messages = new object[]
            {
                new
                {
                    type = "pluginDiagnostics",
                    discovery = new PluginInstaller(home, home, home, null, null, hostInstalled: true, windowsHost: windowsHost)
                        .Diagnostics(inserts, (kind, id) => id == "B" ? plugin : null),
                },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        IReadOnlyList<DoctorNote> notes = InstallationDoctor.WindowsPluginNotes(Reply(windowsHost: true));
        Assert.Equal(["wine", plugin.Path!, plugin.Path!, "/plugins/Vendor"], notes.Take(4).Select(n => n.Path));
        Assert.Equal("not installed, and 2 inserts use a Windows plugin, which cannot start without it: XLR 1 Kotelnikov, Stream Kotelnikov. "
            + "Install Wine with your distribution's package manager; the package is called wine", notes[0].Information);
        Assert.StartsWith("the plugin of the insert XLR 1 Kotelnikov is in the plugin list only through this yabridge wrapper. Its Windows module, "
            + Path.Combine(outside, "Contents", "x86_64-win", "Kotelnikov.vst3"), notes[1].Information);
        Assert.Contains("loses its plugin when the openxlr-yabridge companion is removed", notes[1].Information);
        Assert.Equal("not added to the Windows plugin folders when OpenXLR copied the yabridge companion's folders: The folder does not exist or cannot be read. "
            + "Plugins there reach the plugin list only through their yabridge wrappers; add the folder under Windows in the plugin manager once that is dealt with",
            notes[3].Information);

        // Without the host only Wine is said: yabridge is then the only way a Windows plugin runs.
        Assert.Equal("wine", Assert.Single(InstallationDoctor.WindowsPluginNotes(Reply(windowsHost: false))).Path);
    }
}
