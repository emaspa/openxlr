using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using ProcessResult = OpenXLR.Core.ProcessResult;

namespace OpenXLR.Tests;

/// <summary>
/// Windows plugins read straight from their folders for the Windows plugin
/// host built into OpenXLR, with no yabridge wrapper: which folders, how they
/// are scanned, how the catalogue keeps one entry per plugin, and how a saved
/// insert finds its plugin through either path. Every folder is a temporary
/// one and the Wine prefix is this test's own; every plugin is invented, and
/// the helper is a stand-in, so nothing here starts Wine or loads plugin code.
/// </summary>
[Collection("xdg-config")]
public sealed class WindowsHostFolderTests : IDisposable
{
    private const string ClassId = "00112233445566778899AABBCCDDEEFF";
    private const string OtherId = "FFEEDDCCBBAA99887766554433221100";
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-windows-folders-").FullName;
    private readonly Dictionary<string, string?> _before = new();

    public WindowsHostFolderTests()
    {
        Set("XDG_CONFIG_HOME", Directory.CreateDirectory(Path.Combine(_root, "config")).FullName);
        Set("WINEPREFIX", Prefix);
        Directory.CreateDirectory(Path.Combine(Prefix, "dosdevices"));
    }

    private string Prefix => Path.Combine(_root, "prefix");
    private string CommonFiles => Path.Combine(Prefix, "drive_c", "Program Files", "Common Files");

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

    /// <summary>The least a file needs to pass for a Windows module: the two headers and the machine it is built for.</summary>
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

    /// <summary>A Windows VST3 bundle directory as an installer writes one, with its module under <paramref name="architecture"/>.</summary>
    private static string Vst3Bundle(string folder, string name, string architecture = "x86_64-win", ushort machine = 0x8664)
    {
        string bundle = Path.Combine(folder, name + ".vst3");
        WriteModule(Path.Combine(bundle, "Contents", architecture, name + ".vst3"), machine);
        return bundle;
    }

    /// <summary>A yabridge VST3 wrapper of a module: the bridge's library and a link to the module.</summary>
    private string Vst3Wrapper(string name, string module)
    {
        string bundle = Path.Combine(_root, "data", "openxlr", "yabridge", "vst3", name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-linux", name + ".so"), "bridge");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win"));
        File.CreateSymbolicLink(Path.Combine(bundle, "Contents", "x86_64-win", name + ".vst3"), module);
        return bundle;
    }

    /// <summary>What the scanner prints for a bundle holding one plugin with one control.</summary>
    private static string Description(string bundle, string id, string name)
        => JsonSerializer.Serialize(new
        {
            file = bundle,
            plugins = new[]
            {
                new { id, name, audioIns = 2, audioOuts = 2, gui = true, widths = new[] { 1, 2 }, features = new[] { "Fx" },
                      @params = new[] { new { id = 5, name = "Mix", min = 0.0, max = 1.0, @default = 0.5 } } },
            },
        });

    private static ProcessResult Answer(string json) => new(0, Encoding.UTF8.GetBytes(json), "", TimedOut: false, Truncated: false);

    private ScanCache Cache() => new(Path.Combine(_root, "cache", Guid.NewGuid().ToString("N")));

    /// <summary>The Windows folders' VST3 scan with a stand-in for the helper that answers for the bundles it knows.</summary>
    private IReadOnlyList<PluginInfo> ScanVst3(IReadOnlyList<PluginInfo> found, Dictionary<string, string> answers,
        List<string>? asked = null, bool installed = true, ScanCache? cache = null)
        => HostScan.WithWindows("vst3", "scan-vst3", found, Vst3Catalog.Bundles, retryFailures: false, installed,
            WindowsPluginHost.Directories(), bundle =>
            {
                asked?.Add(bundle);
                return answers.TryGetValue(bundle, out string? json) ? Answer(json)
                    : new(1, [], "no such bundle", TimedOut: false, Truncated: false);
            }, cache ?? Cache());

    private static PluginInfo Bridged(string id, string name, string bundle)
        => new("vst3", id, name, "", 2, 2, "", "", [new("5", "Mix", 0, 1, 0.5, false, false, false, false, [])], [], [], []) { Path = bundle };

    // --- the folders ---------------------------------------------------------------

    [Fact]
    public void TheStandardFoldersAreThePrefixsCommonFilesAndAddedOnesFollow()
    {
        Assert.Equal([Path.Combine(CommonFiles, "VST3"), Path.Combine(CommonFiles, "CLAP")], WindowsPluginHost.StandardDirectories());
        // Without WINEPREFIX it is Wine's own default; only the name is
        // worked out here, nothing under the home folder is read.
        Set("WINEPREFIX", null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine", "drive_c", "Program Files", "Common Files", "VST3"),
            WindowsPluginHost.StandardDirectories()[0]);
        Set("WINEPREFIX", Prefix);

        string added = Directory.CreateDirectory(Path.Combine(_root, "my windows plugins")).FullName;
        InstallOutcome outcome = PluginSearchPaths.Change("windows", added, true);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(new PluginFolder("windows", added), Assert.Single(PluginSearchPaths.Read(out _)));
        Assert.Equal([.. WindowsPluginHost.StandardDirectories(), added], WindowsPluginHost.Directories());
        // A Windows folder is the Windows plugin host's alone: the native
        // formats do not search it.
        Assert.DoesNotContain(added, ClapCatalog.SearchPath());
        Assert.DoesNotContain(added, Vst3Catalog.SearchPath());

        // Listed where the build has the host, which is the only one that
        // searches them, and nowhere else.
        PluginSearchDirectory[] listed = [.. PluginSearchPaths.Snapshot(windowsHost: true).Where(d => d.Kind == "windows")];
        Assert.Equal(WindowsPluginHost.Directories(), listed.Select(d => d.Path));
        Assert.Equal([false, false, true], listed.Select(d => d.Custom));
        Assert.Equal([false, false, true], listed.Select(d => d.Exists));
        Assert.DoesNotContain(PluginSearchPaths.Snapshot(windowsHost: false), d => d.Kind == "windows");

        Assert.True(PluginSearchPaths.Change("windows", added, false).Ok);
        Assert.Equal(WindowsPluginHost.StandardDirectories(), WindowsPluginHost.Directories());
    }

    [Fact]
    public void AWindowsFolderStaysClearOfEveryFolderAlreadySearched()
    {
        // Around a standard folder, or inside one.
        Directory.CreateDirectory(Path.Combine(CommonFiles, "VST3", "Vendor"));
        Assert.False(PluginSearchPaths.Change("windows", CommonFiles, true).Ok);
        Assert.False(PluginSearchPaths.Change("windows", Path.Combine(CommonFiles, "VST3", "Vendor"), true).Ok);
        // A folder the native formats search, yabridge's wrappers included,
        // is not read again as Windows plugins.
        string clap = Directory.CreateDirectory(Path.Combine(_root, "clap")).FullName;
        Set("CLAP_PATH", clap);
        InstallOutcome refused = PluginSearchPaths.Change("windows", clap, true);
        Assert.False(refused.Ok);
        Assert.Equal("This folder is inside, or holds, a folder that is already searched.", refused.Message);
        Assert.Empty(PluginSearchPaths.Read(out _));
    }

    [Theory]
    [InlineData("windows", "/plugins", true)]
    [InlineData("windows", "relative", false)]
    [InlineData("windows", "/c:/plugins", false)]
    [InlineData("Windows", "/plugins", false)]
    [InlineData("windows-vst3", "/plugins", false)]
    public void TheCommandTakesTheWindowsKindOnTheSameTerms(string kind, string path, bool valid)
    {
        using var mixer = new Mixer();
        foreach (string command in new[] { "addPluginSearchPath", "removePluginSearchPath" })
        {
            string? error = CommandValidation.Check(new Command { Cmd = command, Kind = kind, Path = path }, mixer, _ => null);
            if (valid) Assert.Null(error);
            else Assert.Equal($"{command}: kind must be lv2, clap, vst3 or windows and path an absolute folder of at most 4096 characters, without a colon or control character", error);
        }
    }

    // --- scanning -------------------------------------------------------------------

    [Fact]
    public void EachSixtyFourBitBundleIsDescribedOnTheWindowsHostAndRunsOnlyThere()
    {
        string vst3 = Path.Combine(CommonFiles, "VST3");
        string verb = Vst3Bundle(Path.Combine(vst3, "Vendor"), "Deep Verb");
        string single = WriteModule(Path.Combine(vst3, "Single.vst3"));
        string old = Vst3Bundle(vst3, "Old", architecture: "x86-win", machine: 0x014c);
        string narrow = WriteModule(Path.Combine(vst3, "Narrow.vst3"), machine: 0x014c);
        var asked = new List<string>();
        IReadOnlyList<PluginInfo> catalogue = ScanVst3([], new()
        {
            [verb] = Description(verb, ClassId, "Deep Verb"),
            [single] = Description(single, OtherId, "Single"),
        }, asked);

        // The 32-bit ones are never started.
        Assert.Equal([single, verb], asked.Order(StringComparer.Ordinal));
        Assert.Equal(["Deep Verb", "Single"], catalogue.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(catalogue, p => Assert.True(p.WindowsHostOnly));
        Assert.Equal(verb, catalogue.Single(p => p.Plugin == ClassId).Path);
        Assert.Equal(["5"], catalogue.Single(p => p.Plugin == ClassId).Params.Select(p => p.Symbol));

        PluginScanReport report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "windows-vst3");
        Assert.Equal("windows-not-64-bit", report.Entries.Single(e => e.Path == old).Outcome);
        Assert.Equal("windows-not-64-bit", report.Entries.Single(e => e.Path == narrow).Outcome);
        Assert.Contains(PluginScanDiagnostics.Failures([report]), f => f.Path == old && f.Kind == "windows-vst3");
        Assert.Contains("(it has no 64-bit Windows module)", PluginScanDiagnostics.Sentence(PluginScanDiagnostics.Failures([report])));
        // The CLAP folder is there too, empty, and says so.
        Assert.Equal("directory-missing", report.Entries.Single(e => e.Path == Path.Combine(CommonFiles, "CLAP")).Outcome);
    }

    [Fact]
    public void ABuildWithoutTheHostScansNoWindowsFolder()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        var asked = new List<string>();
        IReadOnlyList<PluginInfo> native = [Bridged(OtherId, "Native", "/usr/lib/vst3/Native.vst3")];
        Assert.Same(native, ScanVst3(native, new() { [verb] = Description(verb, ClassId, "Deep Verb") }, asked, installed: false));
        Assert.Empty(asked);
    }

    [Fact]
    public void APluginReachableThroughAYabridgeWrapperIsListedOnceByItsWrapper()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        string other = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Other");
        string wrapper = Vst3Wrapper("Deep Verb", Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3"));
        var asked = new List<string>();
        IReadOnlyList<PluginInfo> catalogue = ScanVst3([Bridged(ClassId, "Deep Verb", wrapper)], new()
        {
            [verb] = Description(verb, ClassId, "Deep Verb"),
            [other] = Description(other, OtherId, "Other"),
        }, asked);
        PluginInfo kept = Assert.Single(catalogue, p => p.Plugin == ClassId);
        Assert.Equal(wrapper, kept.Path);
        Assert.False(kept.WindowsHostOnly);
        Assert.True(Assert.Single(catalogue, p => p.Plugin == OtherId).WindowsHostOnly);
        // The module the wrapper links to is never loaded on the Windows
        // plugin host: a plugin kept on yabridge is not started by a scan.
        Assert.Equal([other], asked);
        // The scan says why the folder's copy is not listed, as it says of a
        // bundle whose plugins were all found already.
        PluginScanReport report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "windows-vst3");
        PluginScanEntry entry = report.Entries.Single(e => e.Path == verb);
        Assert.Equal(("ok", 1, 1, false), (entry.Outcome, entry.Plugins, entry.Duplicates, entry.Cached));
        Assert.Equal($"Not loaded on the Windows plugin host: the yabridge wrapper {wrapper} links to this module and lists its plugins.", entry.Detail);
        // And the doctor explains it in the same words.
        JsonNode scan = JsonSerializer.SerializeToNode(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        DoctorNote note = Assert.Single(InstallationDoctor.WindowsPluginNotes(Diagnostics(new { windowsHostInstalled = true, scans = new[] { scan } })),
            n => n.Path == verb);
        Assert.StartsWith("listed through its yabridge wrapper", note.Information);
    }

    [Fact]
    public void AModuleCountsEveryPluginItsWrapperListsAndAWrapperThatListsNothingSkipsNothing()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        string module = Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3");
        string wrapper = Vst3Wrapper("Deep Verb", module);
        // A module of two classes, both listed through the wrapper.
        IReadOnlyDictionary<string, (string Wrapper, int Plugins)> wrapped = HostScan.WrappedModules("vst3",
            [Bridged(ClassId, "Deep Verb", wrapper), Bridged(OtherId, "Deep Verb Mono", wrapper),
             Bridged("AA", "Native", "/usr/lib/vst3/Native.vst3"), Bridged("BB", "Found", verb) with { WindowsHostOnly = true }]);
        Assert.Equal((wrapper, 2), Assert.Single(wrapped, m => m.Key == module).Value);
        Assert.Single(wrapped);
        // Another format's wrapper is not this one's.
        Assert.Empty(HostScan.WrappedModules("clap", [Bridged(ClassId, "Deep Verb", wrapper)]));

        // The wrapper is there but its own scan listed nothing, so the
        // catalogue holds nothing of the module: it is described here.
        var asked = new List<string>();
        IReadOnlyList<PluginInfo> catalogue = ScanVst3([], new() { [verb] = Description(verb, ClassId, "Deep Verb") }, asked);
        Assert.Equal([verb], asked);
        Assert.True(Assert.Single(catalogue).WindowsHostOnly);
    }

    [Fact]
    public void AClapModuleItsWrapperLinksToIsNotLoadedEither()
    {
        string folder = Path.Combine(CommonFiles, "CLAP");
        string clap = WriteModule(Path.Combine(folder, "Deep Verb.clap"));
        string home = Directory.CreateDirectory(Path.Combine(_root, "data", "openxlr", "yabridge", "clap")).FullName;
        string wrapper = Path.Combine(home, "Deep Verb.clap");
        File.WriteAllText(wrapper, "bridge");
        File.CreateSymbolicLink(Path.ChangeExtension(wrapper, ".clap-win"), clap);
        var asked = new List<string>();
        IReadOnlyList<PluginInfo> found = [Bridged("com.example.deep-verb", "Deep Verb", wrapper) with { Kind = "clap" }];
        Assert.Same(found, HostScan.WithWindows("clap", "scan-clap", found, ClapCatalog.Bundles, retryFailures: false, installed: true,
            WindowsPluginHost.Directories(), bundle => { asked.Add(bundle); return Answer("{}"); }, Cache()));
        Assert.Empty(asked);
        PluginScanEntry entry = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "windows-clap").Entries.Single(e => e.Path == clap);
        Assert.Equal(("ok", 1, 1), (entry.Outcome, entry.Plugins, entry.Duplicates));
    }

    [Fact]
    public void OnceTheWrapperIsGoneTheNextScanDescribesTheModuleHereAndTheCacheKeepsUp()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        string wrapper = Vst3Wrapper("Deep Verb", Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3"));
        var answers = new Dictionary<string, string> { [verb] = Description(verb, ClassId, "Deep Verb") };
        string cacheDirectory = Path.Combine(_root, "cache", "kept");
        IReadOnlyList<PluginInfo> bridged = [Bridged(ClassId, "Deep Verb", wrapper)];
        PluginScanEntry Entry() => PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "windows-vst3").Entries.Single(e => e.Path == verb);

        // With the wrapper, nothing is started and nothing is cached.
        var asked = new List<string>();
        Assert.Same(bridged, ScanVst3(bridged, answers, asked, cache: new ScanCache(cacheDirectory)));
        Assert.Empty(asked);
        Assert.Null(new ScanCache(cacheDirectory).Lookup(verb));
        Assert.False(new ScanCache(cacheDirectory).KnownFailure(verb));

        // The companion is removed, its wrapper with it: the next scan
        // describes the module on the Windows plugin host and keeps that.
        Directory.Delete(wrapper, recursive: true);
        IReadOnlyList<PluginInfo> alone = ScanVst3([], answers, asked, cache: new ScanCache(cacheDirectory));
        Assert.Equal([verb], asked);
        PluginInfo found = Assert.Single(alone);
        Assert.Equal((ClassId, verb, true), (found.Plugin, found.Path, found.WindowsHostOnly));
        Assert.Equal(("ok", false, 1, 0), (Entry().Outcome, Entry().Cached, Entry().Plugins, Entry().Duplicates));
        Assert.NotNull(new ScanCache(cacheDirectory).Lookup(verb));

        // The scan after that reads the cache.
        asked.Clear();
        Assert.Single(ScanVst3([], answers, asked, cache: new ScanCache(cacheDirectory)));
        Assert.Empty(asked);
        Assert.True(Entry().Cached);

        // A wrapper made again is skipped again, and the cached description
        // stays for the day it goes once more.
        wrapper = Vst3Wrapper("Deep Verb", Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3"));
        bridged = [Bridged(ClassId, "Deep Verb", wrapper)];
        Assert.Same(bridged, ScanVst3(bridged, answers, asked, cache: new ScanCache(cacheDirectory)));
        Assert.Equal(("ok", false, 1, 1), (Entry().Outcome, Entry().Cached, Entry().Plugins, Entry().Duplicates));
        Assert.Empty(asked);
        Assert.NotNull(new ScanCache(cacheDirectory).Lookup(verb));
        Directory.Delete(wrapper, recursive: true);
        Assert.Single(ScanVst3([], answers, asked, cache: new ScanCache(cacheDirectory)));
        Assert.Empty(asked);
        Assert.True(Entry().Cached);
    }

    [Fact]
    public void TheScanIsStartedWithTheWineBackendInThePrefixTheModuleLivesIn()
    {
        Assert.Equal(["wine", "scan-vst3", "/p/Deep Verb.vst3"], HostScan.WindowsScanArguments("scan-vst3", "/p/Deep Verb.vst3"));
        var environment = new PluginHostEnvironment(null, scanner: true);
        var route = new WindowsPluginHost.Route("vst3", "/prefix/drive_c/Deep Verb.vst3", "/prefix");
        IReadOnlyDictionary<string, string> set = HostScan.WindowsScanEnvironment(environment, route, _ => null);
        Assert.Equal("/prefix", set["WINEPREFIX"]);
        Assert.Contains("wineusb.sys=d", set["WINEDLLOVERRIDES"]);
        // The daemon's own prefix stands, as it does for an insert.
        Assert.False(HostScan.WindowsScanEnvironment(environment, route, name => name == "WINEPREFIX" ? "/chosen" : null)
            .ContainsKey("WINEPREFIX"));
        Assert.Equal("/opt/wine/bin/wine", HostScan.WindowsScanEnvironment(environment, route,
            name => name == "WINELOADER" ? "/opt/wine/bin/wine" : null)[WindowsPluginHost.WineVariable]);
    }

    // --- the route of a plugin found in a folder --------------------------------------

    [Fact]
    public void APluginFoundInAFolderRunsFromItsModuleInItsPrefix()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        PluginInfo found = Bridged(ClassId, "Deep Verb", verb) with { WindowsHostOnly = true };
        WindowsPluginHost.Route route = WindowsPluginHost.Resolve(found, out string? reason, installed: true)!;
        Assert.Null(reason);
        Assert.Equal(("vst3", Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3"), Prefix), (route.Kind, route.Module, route.Prefix));

        // A module named otherwise is the first one there, as the helper finds it.
        string renamed = Path.Combine(CommonFiles, "VST3", "Renamed.vst3");
        WriteModule(Path.Combine(renamed, "Contents", "x86_64-win", "Inner.vst3"));
        Assert.Equal(Path.Combine(renamed, "Contents", "x86_64-win", "Inner.vst3"),
            WindowsPluginHost.Resolve(found with { Path = renamed }, out _, installed: true)!.Module);

        // A CLAP file outside every prefix runs in Wine's default one.
        string clap = WriteModule(Path.Combine(_root, "elsewhere", "Deep Verb.clap"));
        WindowsPluginHost.Route clapRoute = WindowsPluginHost.Resolve(
            found with { Kind = "clap", Plugin = "com.example.deep-verb", Path = clap }, out _, installed: true)!;
        Assert.Equal((clap, (string?)null), (clapRoute.Module, clapRoute.Prefix));
    }

    [Fact]
    public void APluginFoundInAFolderThatCannotRunThereSaysWhy()
    {
        PluginInfo Found(string path) => Bridged(ClassId, "Deep Verb", path) with { WindowsHostOnly = true };
        string Refusal(PluginInfo plugin, bool installed = true)
        {
            Assert.Null(WindowsPluginHost.Resolve(plugin, out string? reason, installed));
            return reason!;
        }
        string gone = Path.Combine(CommonFiles, "VST3", "Gone.vst3");
        Assert.Equal($"the Windows plugin 'Deep Verb' is missing: {gone}", Refusal(Found(gone)));
        Assert.Equal("'Deep Verb' has no 64-bit Windows module, the only kind the Windows plugin host runs",
            Refusal(Found(Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Old", architecture: "x86-win", machine: 0x014c))));
        Assert.Equal("'Deep Verb' is not a 64-bit Windows module, the only kind the Windows plugin host runs",
            Refusal(Found(WriteModule(Path.Combine(CommonFiles, "VST3", "Narrow.vst3"), machine: 0x014c))));
        Assert.Equal("this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host",
            Refusal(Found(Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb")), installed: false));
    }

    // --- saved inserts ------------------------------------------------------------------

    /// <summary>An insert as an older build saved it against the yabridge wrapper: no host, its values by parameter id.</summary>
    private static InsertDefinition Saved(string? host = null)
    {
        var json = new JsonObject
        {
            ["id"] = "voice", ["kind"] = "vst3", ["plugin"] = ClassId, ["label"] = "My reverb",
            ["params"] = new JsonObject { ["5"] = 0.25 },
        };
        if (host is not null) json["windowsHost"] = host;
        return json.Deserialize<InsertDefinition>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>A stand-in for the helper that writes down what it was started with, says it is ready and waits.</summary>
    private const string Recorder = """
        import os, json, sys
        with open(os.environ['OPENXLR_TEST_ENV_OUTPUT'], 'w') as f:
            json.dump({'arguments': sys.argv[1:], 'prefix': os.environ.get('WINEPREFIX')}, f)
        print('ready')
        for line in sys.stdin: pass
        """;

    /// <summary>
    /// What the chain builder starts for an insert against this catalogue:
    /// the plugin the insert names, its route, and the helper's arguments
    /// and Wine prefix, read back from a stand-in for the helper.
    /// </summary>
    private (WindowsPluginHost.Route? Route, string[] Arguments, string? Prefix) Start(IReadOnlyList<PluginInfo> catalogue, InsertDefinition insert)
    {
        PluginInfo info = PluginCatalog.Find(catalogue, insert.Kind, insert.Plugin)!;
        var adapter = new PipeWireAdapter(() => { }, _ => { });
        WindowsPluginHost.Route? route = adapter.WindowsRoute(insert, info, out _, installed: true);
        string output = Path.Combine(_root, "started-" + Guid.NewGuid().ToString("N") + ".json");
        Set("OPENXLR_TEST_ENV_OUTPUT", output);
        Set("OPENXLR_YABRIDGE", "system");
        // The prefix is the module's, not one the test process was given.
        Set("WINEPREFIX", null);
        try
        {
            using var host = new NativePluginHost(insert, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", Recorder],
                bundle: info.Path, windows: route);
            Assert.Equal(route is not null, host.WindowsHost);
        }
        finally { Set("WINEPREFIX", Prefix); }
        JsonNode started = JsonNode.Parse(File.ReadAllText(output))!;
        return (route, [.. started["arguments"]!.AsArray().Select(a => a!.GetValue<string>())], started["prefix"]?.GetValue<string>());
    }

    [Fact]
    public void AnInsertSavedAgainstAWrapperLoadsAsBeforeAndOnTheWindowsHostOnceTheWrapperIsGone()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        string module = Path.Combine(verb, "Contents", "x86_64-win", "Deep Verb.vst3");
        string wrapper = Vst3Wrapper("Deep Verb", module);
        var answers = new Dictionary<string, string> { [verb] = Description(verb, ClassId, "Deep Verb") };

        // The wrapper is there: the insert runs through yabridge from it,
        // with its saved value, exactly as it did before the folders were read.
        IReadOnlyList<PluginInfo> withWrapper = ScanVst3([Bridged(ClassId, "Deep Verb", wrapper)], answers);
        (WindowsPluginHost.Route? route, string[] arguments, string? prefix) = Start(withWrapper, Saved());
        Assert.Null(route);
        Assert.Equal(["vst3", wrapper, ClassId, "node", "2", "48000", "5=0.25"], arguments);
        Assert.Null(prefix);
        // Saved to the Windows plugin host, it runs there from the module behind the wrapper.
        (route, arguments, _) = Start(withWrapper, Saved("openxlr"));
        Assert.Equal(module, route!.Module);

        // The wrapper is gone, yabridge removed with it, and the module is
        // still in the prefix: the same saved insert comes up on the Windows
        // plugin host from the folder's entry, with the same value.
        Directory.Delete(wrapper, recursive: true);
        IReadOnlyList<PluginInfo> withoutWrapper = ScanVst3([], answers);
        foreach (InsertDefinition saved in new[] { Saved(), Saved("openxlr") })
        {
            (route, arguments, prefix) = Start(withoutWrapper, saved);
            Assert.Equal((module, Prefix), (route!.Module, route.Prefix));
            Assert.Equal(["wine", "vst3", module, ClassId, "node", "2", "48000", "5=0.25"], arguments);
            Assert.Equal(Prefix, prefix);
        }

        // And once the module is gone as well, the chain says why rather
        // than starting a Windows bundle as a Linux one.
        Directory.Delete(verb, recursive: true);
        PluginInfo stale = Assert.Single(withoutWrapper);
        var adapter = new PipeWireAdapter(() => { }, _ => { });
        string message = Assert.Throws<InvalidOperationException>(() => adapter.WindowsRoute(Saved(), stale, out _, installed: true)).Message;
        Assert.Equal($"My reverb runs only on the Windows plugin host and cannot start there: the Windows plugin 'Deep Verb' is missing: {verb}.", message);
    }

    [Fact]
    public void ThePluginFoundInAFolderTakesEitherValueOfTheHostCommand()
    {
        string verb = Vst3Bundle(Path.Combine(CommonFiles, "VST3"), "Deep Verb");
        PluginInfo found = Bridged(ClassId, "Deep Verb", verb) with { WindowsHostOnly = true };
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Saved()]);
        foreach (string value in new[] { "openxlr", "yabridge" })
            Assert.Null(CommandValidation.Check(new() { Cmd = "setInsertWindowsHost", Channel = "xlr1", InsertId = "voice",
                Value = JsonSerializer.SerializeToElement(value) }, mixer, _ => found, nativeHostInstalled: true, windowsHostInstalled: true));
    }

    // --- what the client shows -------------------------------------------------------------

    [Fact]
    public void TheClientNamesTheHostOfAPluginFoundInAFolderWithoutOfferingTheSwitch()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2) { WindowsHostInstalled = true };
        owner.PluginChoices.Add(new(ClassId, "Deep Verb", "", new JsonArray(), true, true, "vst3"));
        var running = new InsertStatus(Saved(), null, NativeHostRunning: true) { WindowsHostRunning = true };
        owner.Apply(new JsonArray(JsonSerializer.SerializeToNode(running, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.False(insert.CanChooseWindowsHost);
        Assert.Equal(OpenXLR.UI.Localization.Localizer.Text("RunsOnTheWindowsPluginHost"), insert.WindowsHostState);

        Assert.Equal("Windows", new PluginSearchDirectoryItem("windows", "/w", false, true).Format);
        Assert.Equal("VST3", new PluginSearchDirectoryItem("vst3", "/v", false, true).Format);
        Assert.Contains("Windows", new PluginSearchDirectoryItem("windows", "/w", true, true).Detail);
    }

    // --- through the real helper, under Wine ------------------------------------------------------

    /// <summary>
    /// The two fixture plugins of the native Windows host checks, copied into
    /// a folder of a Wine prefix made for the test, scanned by the real helper
    /// and the real Windows program, and their routes read back. Run with
    /// <c>OPENXLR_TEST_WINEPREFIX</c> naming that prefix, <c>OPENXLR_WINHOST</c>
    /// naming the Windows program and the helper beside the tests (a build of
    /// <c>native</c> with <c>WINHOST=1</c> before this one), on a private X
    /// server.
    /// </summary>
    [WinhostScanFact]
    public void TheRealHelperDescribesTheWindowsPluginsOfAFolderInThePrefix()
    {
        string prefix = Environment.GetEnvironmentVariable("OPENXLR_TEST_WINEPREFIX")!;
        string native = Path.GetDirectoryName(Environment.GetEnvironmentVariable(WindowsPluginHost.ProgramVariable)!)!;
        Assert.True(WindowsPluginHost.Installed, "the helper and the Windows program are not where the daemon looks");
        Assert.Null(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        Set("WINEPREFIX", prefix);
        string folder = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "openxlr-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // A VST3 bundle directory as an installer lays one out, and a CLAP file.
            string vst3 = Path.Combine(folder, "VST3", "Fixture.vst3");
            Directory.CreateDirectory(Path.Combine(vst3, "Contents", "x86_64-win"));
            File.Copy(Path.Combine(native, "tests", "winhost-plugin.vst3"), Path.Combine(vst3, "Contents", "x86_64-win", "Fixture.vst3"));
            string clap = Path.Combine(folder, "CLAP", "Fixture.clap");
            Directory.CreateDirectory(Path.GetDirectoryName(clap)!);
            File.Copy(Path.Combine(native, "tests", "winhost-plugin.clap"), clap);

            foreach ((string kind, string command, string bundle) in new[] { ("vst3", "scan-vst3", vst3), ("clap", "scan-clap", clap) })
            {
                Func<string, Action<string, Exception>?, IEnumerable<string>> walk = kind == "vst3" ? Vst3Catalog.Bundles : ClapCatalog.Bundles;
                IReadOnlyList<PluginInfo> found = HostScan.WithWindows(kind, command, [], walk, retryFailures: true,
                    directories: [folder], scanCache: Cache());
                PluginScanReport report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "windows-" + kind);
                Assert.True(found.Count > 0, string.Join("\n", report.Entries.Select(e => $"{e.Path}: {e.Outcome} {e.Detail}")));
                Assert.All(found, p => Assert.Equal((kind, bundle, true), (p.Kind, p.Path, p.WindowsHostOnly)));
                WindowsPluginHost.Route route = WindowsPluginHost.Resolve(found[0], out string? reason)!;
                Assert.Null(reason);
                Assert.Equal(prefix, route.Prefix);
                Assert.Equal(kind == "vst3" ? Path.Combine(vst3, "Contents", "x86_64-win", "Fixture.vst3") : clap, route.Module);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // --- the doctor ----------------------------------------------------------------------------

    private static JsonElement Diagnostics(object discovery)
        => JsonSerializer.SerializeToElement(new { apiVersion = "1", ok = true, messages = new object[] { new { type = "pluginDiagnostics", discovery } } });

    [Fact]
    public void TheDoctorSaysWhereTheWindowsHostLooksAndWhatItLeftOut()
    {
        object Discovery(bool installed) => new
        {
            windowsHostInstalled = installed,
            windowsHostDirectories = new[] { new { path = "/p/VST3", exists = true }, new { path = "/p/CLAP", exists = false } },
            scans = new object[]
            {
                new { kind = "vst3", entries = new[] { new { path = "/native/Broken.vst3", outcome = "scan-failed", plugins = 0, duplicates = 0 } } },
                new { kind = "windows-vst3", entries = new object[]
                {
                    new { path = "/p/VST3", outcome = "directory", plugins = 0, duplicates = 0 },
                    new { path = "/p/VST3/Listed.vst3", outcome = "ok", plugins = 1, duplicates = 0 },
                    new { path = "/p/VST3/Wrapped.vst3", outcome = "ok", plugins = 1, duplicates = 1 },
                    new { path = "/p/VST3/Old.vst3", outcome = "windows-not-64-bit", plugins = 0, duplicates = 0 },
                    new { path = "/p/VST3/Slow.vst3", outcome = "timeout", plugins = 0, duplicates = 0 },
                    new { path = "/p/VST3/Crash.vst3", outcome = "scan-failed", plugins = 0, duplicates = 0, detail = "it crashed\nmore" },
                } },
            },
        };
        Assert.Empty(InstallationDoctor.WindowsPluginNotes(Diagnostics(Discovery(installed: false))));
        Assert.Empty(InstallationDoctor.WindowsPluginNotes(JsonSerializer.SerializeToElement(new { ok = false })));

        IReadOnlyList<DoctorNote> notes = InstallationDoctor.WindowsPluginNotes(Diagnostics(Discovery(installed: true)));
        Assert.Equal(["/p/VST3", "/p/CLAP", "/p/VST3/Wrapped.vst3", "/p/VST3/Old.vst3", "/p/VST3/Slow.vst3", "/p/VST3/Crash.vst3"],
            notes.Select(n => n.Path));
        Assert.StartsWith("searched for Windows VST3 and CLAP plugins", notes[0].Information);
        Assert.Contains("cannot see this folder", notes[1].Information);
        Assert.Contains("WINEPREFIX", notes[1].Information);
        Assert.StartsWith("listed through its yabridge wrapper", notes[2].Information);
        Assert.StartsWith("not listed: OpenXLR's Windows plugin host runs only 64-bit", notes[3].Information);
        Assert.StartsWith("not listed: the scan did not finish in time", notes[4].Information);
        Assert.Equal("not listed: the scan ended with scan-failed. it crashed", notes[5].Information);
    }

    [Fact]
    public void TheDoctorKeepsItsWindowsNotesToAFewAndCountsTheRest()
    {
        var entries = Enumerable.Range(0, 100).Select(i => new { path = $"/p/VST3/{i}.vst3", outcome = "windows-not-64-bit" }).ToArray();
        IReadOnlyList<DoctorNote> notes = InstallationDoctor.WindowsPluginNotes(Diagnostics(new
        {
            windowsHostInstalled = true,
            scans = new[] { new { kind = "windows-vst3", entries } },
        }));
        Assert.Equal(InstallationDoctor.WindowsPluginNoteLimit, notes.Count);
        Assert.Equal($"{100 - InstallationDoctor.WindowsPluginNoteLimit + 1} more notes; openxlr doctor --json and the diagnostics archive have every scan entry",
            notes[^1].Information);
    }

    [Fact]
    public async Task TheDoctorAsksTheRunningDaemonAndSaysNothingWhenItCannot()
    {
        string reply = Diagnostics(new { windowsHostInstalled = true, windowsHostDirectories = new[] { new { path = "/p/VST3", exists = true } } }).GetRawText();
        DoctorReport report = await InstallationDoctor.Inspect([], null, runtime: true,
            readState: _ => Task.FromResult("{}"), readPluginDiagnostics: _ => Task.FromResult(reply));
        Assert.Equal("/p/VST3", Assert.Single(report.Notes).Path);
        report = await InstallationDoctor.Inspect([], null, runtime: true,
            readState: _ => Task.FromResult("{}"), readPluginDiagnostics: _ => throw new HttpRequestException("no daemon"));
        Assert.Empty(report.Notes);
        // Without the runtime checks the daemon is not asked.
        bool asked = false;
        report = await InstallationDoctor.Inspect([], null, runtime: false,
            readState: _ => Task.FromResult("{}"), readPluginDiagnostics: _ => { asked = true; return Task.FromResult(reply); });
        Assert.False(asked);
    }
}

/// <summary>A check that runs Wine and the Windows plugin host, only where it is asked for with a prefix of its own and the Windows program named.</summary>
public sealed class WinhostScanFactAttribute : FactAttribute
{
    public WinhostScanFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENXLR_TEST_WINEPREFIX"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENXLR_WINHOST")))
            Skip = "Run with OPENXLR_TEST_WINEPREFIX and OPENXLR_WINHOST set, after make -C native WINHOST=1, under xvfb-run.";
    }
}
