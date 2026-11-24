using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The host of an insert's Windows plugin: yabridge unless the insert says
/// the Windows plugin host built into OpenXLR. The choice is saved with the
/// insert and absent when it is yabridge, set by one command that is refused
/// where it cannot work, and carried in the state. The Windows module and
/// its Wine prefix are read back from the yabridge wrapper, the helper is
/// started with its <c>wine</c> backend, and a saved choice the build cannot
/// honour runs through yabridge. Every plugin here is invented.
/// </summary>
[Collection("xdg-config")]
public sealed class InsertWindowsHostTests : IDisposable
{
    private const string ClassId = "00112233445566778899AABBCCDDEEFF";
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-windows-host-").FullName;
    private readonly Dictionary<string, string?> _before = new();

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

    /// <summary>A Wine prefix with a Windows VST3 module where an installer puts one.</summary>
    private (string Prefix, string Module) InstalledVst3(string name, string prefix = "prefix")
    {
        string root = Path.Combine(_root, prefix);
        Directory.CreateDirectory(Path.Combine(root, "dosdevices"));
        return (root, WriteModule(Path.Combine(root, "drive_c", "Program Files", "Common Files", "VST3", name + ".vst3")));
    }

    /// <summary>
    /// A VST3 wrapper as yabridgectl lays one out: the bridge's library under
    /// <c>x86_64-linux</c>, and under <paramref name="architecture"/> a link
    /// to the Windows module.
    /// </summary>
    private string Vst3Wrapper(string home, string name, string target, string architecture = "x86_64-win")
    {
        string bundle = Path.Combine(_root, home, name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-linux", name + ".so"), "bridge");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", architecture));
        File.CreateSymbolicLink(Path.Combine(bundle, "Contents", architecture, name + ".vst3"), target);
        return bundle;
    }

    /// <summary>A CLAP wrapper: the bridge's library, and beside it a <c>.clap-win</c> link to the Windows module.</summary>
    private string ClapWrapper(string home, string name, string target)
    {
        string wrapper = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, home)).FullName, name + ".clap");
        File.WriteAllText(wrapper, "bridge");
        File.CreateSymbolicLink(Path.ChangeExtension(wrapper, ".clap-win"), target);
        return wrapper;
    }

    private static PluginInfo Plugin(string kind, string id, string name, string? path)
        => new(kind, id, name, "", 2, 2, "", "", [new("5", "Mix", 0, 1, 0.5, false, false, false, false, [])], [], [], []) { Path = path };

    private static WindowsPluginHost.Route Route(PluginInfo plugin)
    {
        WindowsPluginHost.Route? route = WindowsPluginHost.Resolve(plugin, out string? reason, installed: true);
        Assert.Null(reason);
        return Assert.IsType<WindowsPluginHost.Route>(route);
    }

    private static string Refusal(PluginInfo? plugin, bool installed = true)
    {
        Assert.Null(WindowsPluginHost.Resolve(plugin, out string? reason, installed));
        return Assert.IsType<string>(reason);
    }

    // --- from the wrapper to the module and its prefix ------------------------

    [Theory]
    [InlineData("data/openxlr/yabridge/vst3")]   // the companion's own wrappers
    [InlineData("home/.vst3/yabridge")]          // a system yabridge's
    public void AVst3WrapperLeadsToItsModuleAndThePrefixItLivesIn(string home)
    {
        (string prefix, string module) = InstalledVst3("Deep Verb");
        WindowsPluginHost.Route route = Route(Plugin("vst3", ClassId, "Deep Verb", Vst3Wrapper(home, "Deep Verb", module)));
        Assert.Equal(("vst3", module, prefix), (route.Kind, route.Module, route.Prefix));
        // A trailing separator on the bundle changes nothing.
        Assert.Equal(module, Route(Plugin("vst3", ClassId, "Deep Verb", Path.Combine(_root, home, "Deep Verb.vst3") + "/")).Module);
    }

    [Theory]
    [InlineData("data/openxlr/yabridge/clap")]
    [InlineData("home/.clap/yabridge")]
    public void AClapWrapperLeadsToItsModuleAndAModuleOutsideEveryPrefixHasNone(string home)
    {
        // A plugin handed to OpenXLR as a file is kept outside Wine's prefixes.
        string module = WriteModule(Path.Combine(_root, "windows-plugins", "clap", "Deep Verb", "Deep Verb.clap"));
        WindowsPluginHost.Route route = Route(Plugin("clap", "com.example.deep-verb", "Deep Verb", ClapWrapper(home, "Deep Verb", module)));
        Assert.Equal(("clap", module, null), (route.Kind, route.Module, route.Prefix));
    }

    [Fact]
    public void TheModuleIsNamedByItsRealPathThroughEveryLink()
    {
        // A module inside a Windows bundle, reached through a link to the
        // link, from a wrapper that is itself under a linked directory.
        string prefix = Path.Combine(_root, "prefix");
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        string module = WriteModule(Path.Combine(prefix, "drive_c", "VST3", "Deep Verb.vst3", "Contents", "x86_64-win", "Deep Verb.vst3"));
        string first = Path.Combine(_root, "first-link.vst3");
        File.CreateSymbolicLink(first, module);
        string bundle = Vst3Wrapper("real/vst3", "Deep Verb", first);
        File.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        WindowsPluginHost.Route route = Route(Plugin("vst3", ClassId, "Deep Verb", Path.Combine(_root, "alias", "vst3", "Deep Verb.vst3")));
        Assert.Equal((module, prefix), (route.Module, route.Prefix));
        Assert.Equal(Path.Combine(bundle, "Contents", "x86_64-win", "Deep Verb.vst3"), WindowsPluginHost.ModuleLink("vst3", bundle));
    }

    [Fact]
    public void AWrapperWhoseModuleIsGoneIsRefusedWithThePathItNamed()
    {
        string gone = Path.Combine(_root, "prefix", "drive_c", "VST3", "Gone.vst3");
        Assert.Equal($"the Windows plugin behind 'Gone' is missing: {gone}",
            Refusal(Plugin("vst3", ClassId, "Gone", Vst3Wrapper("yabridge/vst3", "Gone", gone))));
        string clap = Path.Combine(_root, "prefix", "drive_c", "CLAP", "Gone.clap");
        Assert.Equal($"the Windows plugin behind 'Gone' is missing: {clap}",
            Refusal(Plugin("clap", "com.example.gone", "Gone", ClapWrapper("yabridge/clap", "Gone", clap))));
    }

    [Fact]
    public void OnlyAWindowsPluginBridgedThroughYabridgeHasARoute()
    {
        const string notBridged = "'Native' is not a Windows plugin bridged through yabridge";
        Assert.Equal(notBridged, Refusal(Plugin("lv2", "urn:example:native", "Native", null)));
        string clap = Path.Combine(_root, "native.clap");
        File.WriteAllText(clap, "a Linux library");
        Assert.Equal(notBridged, Refusal(Plugin("clap", "com.example.native", "Native", clap)));
        // A bundle built for several systems holds a real Windows module
        // and runs natively, so only a link counts.
        string bundle = Path.Combine(_root, "Native.vst3");
        WriteModule(Path.Combine(bundle, "Contents", "x86_64-win", "Native.vst3"));
        Assert.Equal(notBridged, Refusal(Plugin("vst3", ClassId, "Native", bundle)));
        Assert.Null(WindowsPluginHost.ModuleLink("vst3", bundle));
        Assert.Equal("the plugin is not installed", Refusal(null));
    }

    [Fact]
    public void AModuleThatIsNot64BitIsRefused()
    {
        const string refused = "'Old' is not a 64-bit Windows module, the only kind the Windows plugin host runs";
        string narrow = WriteModule(Path.Combine(_root, "modules", "Old.vst3"), machine: 0x014c);
        Assert.Equal(refused, Refusal(Plugin("vst3", ClassId, "Old", Vst3Wrapper("a/vst3", "Old", narrow))));
        string text = Path.Combine(_root, "modules", "Old.clap");
        File.WriteAllText(text, "no module at all");
        Assert.Equal(refused, Refusal(Plugin("clap", "com.example.old", "Old", ClapWrapper("a/clap", "Old", text))));
        // yabridge keeps a 32-bit module in a directory of its own.
        Assert.Equal("'Old' has no 64-bit Windows module, the only kind the Windows plugin host runs",
            Refusal(Plugin("vst3", ClassId, "Old", Vst3Wrapper("b/vst3", "Old", narrow, architecture: "x86-win"))));
    }

    [Fact]
    public void ABuildWithoutTheHostRefusesEveryPlugin()
    {
        (_, string module) = InstalledVst3("Deep Verb");
        PluginInfo plugin = Plugin("vst3", ClassId, "Deep Verb", Vst3Wrapper("yabridge/vst3", "Deep Verb", module));
        Assert.Equal("this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host",
            Refusal(plugin, installed: false));
        Assert.NotNull(WindowsPluginHost.Resolve(plugin, out _, installed: true));
    }

    [Fact]
    public void TheWindowsProgramIsBesideTheHelperUnlessTheEnvironmentNamesIt()
    {
        Set(WindowsPluginHost.ProgramVariable, null);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "openxlr-winhost.exe"), WindowsPluginHost.Executable);
        Assert.Equal(Path.GetDirectoryName(NativePluginHost.Executable), Path.GetDirectoryName(WindowsPluginHost.Executable));
        Set(WindowsPluginHost.ProgramVariable, "/opt/build/openxlr-winhost.exe");
        Assert.Equal("/opt/build/openxlr-winhost.exe", WindowsPluginHost.Executable);
    }

    [Fact]
    public void ThePrefixIsTheNearestDirectoryAboveTheModuleThatHoldsDosdevices()
    {
        string outer = Path.Combine(_root, "outer"), inner = Path.Combine(outer, "drive_c", "inner");
        Directory.CreateDirectory(Path.Combine(outer, "dosdevices"));
        Assert.Equal(outer, WindowsPluginHost.PrefixOf(Path.Combine(outer, "drive_c", "VST3", "A.vst3")));
        Directory.CreateDirectory(Path.Combine(inner, "dosdevices"));
        Assert.Equal(inner, WindowsPluginHost.PrefixOf(Path.Combine(inner, "drive_c", "VST3", "A.vst3")));
        // A file of that name is not a prefix, and neither is no directory.
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "dosdevices"), "");
        Assert.Null(WindowsPluginHost.PrefixOf(Path.Combine(plain, "A.vst3")));
    }

    // --- the plugin whose licence needs the companion's host ------------------

    [Theory]
    [InlineData("Acme Voice Focus", "AcmeVf.vst3", "AcmeVf.vst3", true)]        // by the name its module reports
    [InlineData("Acme VF", "AcmeVoiceFocus_v9.8.vst3", "plugin.vst3", true)]    // by its wrapper's file name
    [InlineData("Acme VF", "AcmeVf.vst3", "acme-voice_focus.vst3", true)]       // by its module's file name
    [InlineData("ACME VOICEFOCUS 2", "a.vst3", "a.vst3", true)]
    [InlineData("Acme Voice Tuner", "AcmeVoiceTuner.vst3", "AcmeVoiceTuner.vst3", false)]
    [InlineData("Focus on Voice", "Focus.vst3", "Voice.vst3", false)]
    public void ThePluginThatNeedsTheCompanionsDiskIdentityIsKnownByItsNames(string name, string wrapper, string module, bool needs)
        => Assert.Equal(needs, WindowsPluginHost.NeedsDiskIdentity(name, wrapper, module));

    [Fact]
    public void ThePluginThatNeedsTheCompanionsDiskIdentityStaysOnYabridge()
    {
        (_, string module) = InstalledVst3("AcmeVoiceFocus_v9.8");
        PluginInfo licensed = Plugin("vst3", ClassId, "Acme Voice Focus", Vst3Wrapper("yabridge/vst3", "AcmeVoiceFocus_v9.8", module));
        Assert.Equal("'Acme Voice Focus' ties its licence to the disk identity that only the yabridge companion reports to a plugin, "
            + "so on the Windows plugin host it would see a different machine", Refusal(licensed));
        // The file is never opened to decide it: an unreadable module is refused the same way.
        File.WriteAllText(module, "not even a module");
        Assert.Contains("ties its licence", Refusal(licensed));

        using var mixer = new Mixer();
        var insert = new InsertDefinition { Id = "voice", Kind = "vst3", Plugin = ClassId };
        mixer.SetInserts("xlr1", [insert]);
        Assert.StartsWith("setInsertWindowsHost: 'Acme Voice Focus' ties its licence to the disk identity",
            Check(mixer, licensed, WindowsPluginHost.OpenXlr));
        // A chain saved by hand to say so runs through yabridge.
        var notes = new List<string>();
        var adapter = new PipeWireAdapter(() => { }, notes.Add);
        Assert.Null(adapter.WindowsRoute(insert with { WindowsHost = "openxlr" }, licensed, out string? kept, installed: true));
        Assert.Contains("ties its licence", kept);
        Assert.Contains("runs through yabridge instead", Assert.Single(notes));
    }

    // --- the command -----------------------------------------------------------

    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);

    private static string? Check(Mixer mixer, PluginInfo? plugin, object value, string channel = "xlr1", string insertId = "voice",
        bool installed = true)
        => CommandValidation.Check(new() { Cmd = "setInsertWindowsHost", Channel = channel, InsertId = insertId, Value = Value(value) },
            mixer, _ => plugin, nativeHostInstalled: true, windowsHostInstalled: installed);

    [Fact]
    public void TheCommandNeedsAnInsertOneOfTheTwoHostsAndAPluginTheHostCanRun()
    {
        (_, string module) = InstalledVst3("Deep Verb");
        PluginInfo bridged = Plugin("vst3", ClassId, "Deep Verb", Vst3Wrapper("yabridge/vst3", "Deep Verb", module));
        PluginInfo clap = Plugin("clap", "com.example.deep-verb", "Deep Verb",
            ClapWrapper("yabridge/clap", "Deep Verb", WriteModule(Path.Combine(_root, "modules", "Deep Verb.clap"))));
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [new() { Id = "voice", Kind = "vst3", Plugin = ClassId }]);
        Assert.Null(Check(mixer, bridged, "openxlr"));
        Assert.Null(Check(mixer, clap, "openxlr"));
        Assert.Null(Check(mixer, bridged, "yabridge"));
        Assert.Equal("setInsertWindowsHost: select an existing insert", Check(mixer, bridged, "openxlr", insertId: "missing"));
        Assert.Equal("setInsertWindowsHost: select an existing insert", Check(mixer, bridged, "openxlr", channel: "nowhere"));
        foreach (object value in new object[] { true, 1, "winhost", "OpenXLR", "" })
            Assert.Equal("setInsertWindowsHost: value must be 'yabridge' or 'openxlr'", Check(mixer, bridged, value));
        Assert.Equal("setInsertWindowsHost: this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host",
            Check(mixer, bridged, "openxlr", installed: false));
        Assert.Equal("setInsertWindowsHost: 'Native' is not a Windows plugin bridged through yabridge",
            Check(mixer, Plugin("vst3", ClassId, "Native", Path.Combine(_root, "Native.vst3")), "openxlr"));
        Assert.Equal("setInsertWindowsHost: the plugin is not installed", Check(mixer, null, "openxlr"));
        string gone = Path.Combine(_root, "nowhere", "Gone.vst3");
        Assert.Equal($"setInsertWindowsHost: the Windows plugin behind 'Gone' is missing: {gone}",
            Check(mixer, Plugin("vst3", ClassId, "Gone", Vst3Wrapper("yabridge/vst3", "Gone", gone)), "openxlr"));
        // Back to yabridge is never refused, so an insert can always leave
        // a host that is gone or a plugin that is.
        Assert.Null(Check(mixer, bridged, "yabridge", installed: false));
        Assert.Null(Check(mixer, null, "yabridge", installed: false));
    }

    // --- saved with the insert, carried in the state ------------------------------

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly InsertDefinition Voice = new() { Id = "voice", Kind = "vst3", Plugin = ClassId };

    [Fact]
    public void TheChoiceIsSavedWithTheInsertCarriedInTheStateAndAbsentForYabridge()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice, Voice with { Id = "other" }]);
        mixer.SetInsertWindowsHost("xlr1", "voice", true);
        mixer.SetInsertWindowsHost("xlr1", "missing", true);   // an unknown insert changes nothing
        IReadOnlyList<InsertStatus> chain = mixer.Snapshot().Inserts["xlr1"];
        Assert.Equal(["openxlr", null], chain.Select(i => i.Insert.WindowsHost));
        Assert.Equal([true, false], chain.Select(i => i.Insert.OnWindowsHost));
        // Nothing runs in an unbuilt mixer, on either host.
        Assert.All(chain, i => Assert.False(i.WindowsHostRunning));
        Assert.All(chain, i => Assert.Null(i.WindowsHostError));

        string path = Path.Combine(_root, "mixer.json");
        Assert.Null(mixer.ExportSettings().Save(path));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("openxlr", saved["inserts"]!["xlr1"]![0]!["windowsHost"]!.GetValue<string>());
        // The default is not written, so a file with no such insert is the
        // file an older build wrote and reads.
        Assert.False(saved["inserts"]!["xlr1"]![1]!.AsObject().ContainsKey("windowsHost"));
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.DoesNotContain("inserts", warning ?? "");
        Assert.Equal(["openxlr", null], loaded.Inserts!["xlr1"].Select(i => i.WindowsHost));

        JsonNode state = JsonSerializer.SerializeToNode(mixer.Snapshot(), Wire)!;
        Assert.Equal("openxlr", state["inserts"]!["xlr1"]![0]!["insert"]!["windowsHost"]!.GetValue<string>());
        Assert.False(state["inserts"]!["xlr1"]![1]!["insert"]!.AsObject().ContainsKey("windowsHost"));
        Assert.False(state["inserts"]!["xlr1"]![0]!["windowsHostRunning"]!.GetValue<bool>());

        mixer.SetInsertWindowsHost("xlr1", "voice", false);
        Assert.Null(mixer.Snapshot().Inserts["xlr1"][0].Insert.WindowsHost);
        Assert.Null(mixer.ExportSettings().Save(path));
        Assert.DoesNotContain("windowsHost", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x"}""", null)]                            // an older save
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"openxlr"}""", "openxlr")]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"yabridge"}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"a-host-of-a-newer-build"}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"OPENXLR"}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":""}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":null}""", null)]
    public void AnInsertReadsOnlyTheHostThisBuildKnowsAndEverythingElseIsYabridge(string json, string? host)
    {
        InsertDefinition insert = JsonSerializer.Deserialize<InsertDefinition>(json, Wire)!;
        Assert.Equal(host, insert.WindowsHost);
        Assert.Equal(host is not null, insert.OnWindowsHost);
        // What is written back holds the field only when it says something.
        Assert.Equal(host is not null, JsonSerializer.SerializeToNode(insert, Wire)!.AsObject().ContainsKey("windowsHost"));
        Assert.False(JsonSerializer.SerializeToNode(insert, Wire)!.AsObject().ContainsKey("onWindowsHost"));
    }

    [Fact]
    public void ASavedFileWithAHostThisBuildDoesNotKnowLoadsWithTheInsertOnYabridge()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice]);
        mixer.SetInsertWindowsHost("xlr1", "voice", true);
        string path = Path.Combine(_root, "mixer.json");
        Assert.Null(mixer.ExportSettings().Save(path));
        JsonNode saved = JsonNode.Parse(File.ReadAllText(path))!;
        saved["inserts"]!["xlr1"]![0]!["windowsHost"] = "a-host-of-a-newer-build";
        File.WriteAllText(path, saved.ToJsonString());
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.DoesNotContain("inserts", warning ?? "");
        InsertDefinition insert = Assert.Single(loaded.Inserts!["xlr1"]);
        Assert.Equal(("voice", ClassId, null), (insert.Id, insert.Plugin, insert.WindowsHost));
    }

    [Fact]
    public void RewritingAChainLeavesEveryInsertOnTheHostItHas()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice, Voice with { Id = "other" }]);
        mixer.SetInsertWindowsHost("xlr1", "voice", true);
        // A client that reorders the chain and adds to it, and knows nothing
        // of the choice: what it sends has no host, or one it made up.
        mixer.SetInserts("xlr1",
        [
            Voice with { Id = "new", WindowsHost = "openxlr" },
            Voice with { Id = "other", Bypass = true },
            Voice with { Label = "Renamed", Params = new() { ["5"] = 0.25 } },
        ]);
        Assert.Equal([("new", null), ("other", null), ("voice", "openxlr")],
            mixer.Snapshot().Inserts["xlr1"].Select(i => (i.Insert.Id, i.Insert.WindowsHost)));
        // The same id with another plugin is another insert.
        mixer.SetInserts("xlr1", [Voice with { Plugin = "FFEEDDCCBBAA99887766554433221100" }]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Insert.WindowsHost);
    }

    [Fact]
    public void TheHostIsPartOfTheChainStructureSoTheHelperStartsAgain()
    {
        ChainStructure running = ChainStructure.Of([Voice]);
        Assert.NotEqual(running, ChainStructure.Of([Voice with { WindowsHost = "openxlr" }]));
        Assert.Equal(running, ChainStructure.Of([Voice with { WindowsHost = "yabridge" }]));
    }

    // --- what the helper is started with ----------------------------------------

    [Fact]
    public void TheHelperIsStartedWithItsWineBackendAndTheModule()
    {
        var route = new WindowsPluginHost.Route("vst3", "/prefix/drive_c/VST3/Deep Verb.vst3", "/prefix");
        const string bundle = "/data/openxlr/yabridge/vst3/Deep Verb.vst3";
        Assert.Equal(["wine", "vst3", route.Module, ClassId], NativePluginHost.Arguments(Voice, bundle, route));
        Assert.Equal(["vst3", bundle, ClassId], NativePluginHost.Arguments(Voice, bundle));
        Assert.Equal(["--own-block", "wine", "vst3", route.Module, ClassId],
            NativePluginHost.Arguments(Voice with { OwnBlock = true }, bundle, route));
        var clap = new InsertDefinition { Id = "c", Kind = "clap", Plugin = "com.example.deep-verb" };
        var clapRoute = new WindowsPluginHost.Route("clap", "/plugins/Deep Verb.clap", null);
        Assert.Equal(["wine", "clap", clapRoute.Module, clap.Plugin], NativePluginHost.Arguments(clap, "/yabridge/clap/Deep Verb.clap", clapRoute));
        Assert.Equal(["clap", "/yabridge/clap/Deep Verb.clap", clap.Plugin], NativePluginHost.Arguments(clap, "/yabridge/clap/Deep Verb.clap"));
    }

    [Theory]
    [InlineData(null, "/prefix", "/prefix")]            // the prefix the module lives in
    [InlineData("", "/prefix", "/prefix")]
    [InlineData("/chosen", "/prefix", null)]            // the daemon's own stands, as it does for yabridge
    [InlineData(null, null, null)]                      // outside every prefix: Wine's default
    [InlineData("/chosen", null, null)]
    public void ThePrefixFollowsYabridgesRule(string? inherited, string? found, string? set)
    {
        var route = new WindowsPluginHost.Route("vst3", "/m.vst3", found);
        Dictionary<string, string> overlay = WindowsPluginHost.HelperEnvironment(route, name => name == "WINEPREFIX" ? inherited : null);
        Assert.Equal(set, overlay.GetValueOrDefault("WINEPREFIX"));
        Assert.Equal(set is null ? 0 : 1, overlay.Count);
    }

    [Fact]
    public void TheWineYabridgeIsToldToStartIsTheOneTheHelperStarts()
    {
        var route = new WindowsPluginHost.Route("vst3", "/m.vst3", null);
        Dictionary<string, string?> inherited = new() { ["WINELOADER"] = "/opt/wine/bin/wine" };
        Assert.Equal("/opt/wine/bin/wine",
            WindowsPluginHost.HelperEnvironment(route, inherited.GetValueOrDefault)[WindowsPluginHost.WineVariable]);
        inherited[WindowsPluginHost.WineVariable] = "/usr/local/bin/wine";
        Assert.Empty(WindowsPluginHost.HelperEnvironment(route, inherited.GetValueOrDefault));
        Assert.Empty(WindowsPluginHost.HelperEnvironment(route, _ => null));
    }

    /// <summary>A stand-in for the helper that writes down what it was started with, says it is ready and waits.</summary>
    private const string Recorder = """
        import os, json, sys
        names = ['WINEPREFIX', 'OPENXLR_WINE', 'WINELOADER', 'WINEDLLOVERRIDES']
        with open(os.environ['OPENXLR_TEST_ENV_OUTPUT'], 'w') as f:
            json.dump({'arguments': sys.argv[1:], 'environment': {name: os.environ.get(name) for name in names}}, f)
        print('ready')
        for line in sys.stdin: pass
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothRoutesStartTheHelperWithTheirOwnArgumentsAndTheSameOverrides(bool windowsHost)
    {
        (string prefix, string module) = InstalledVst3("Deep Verb");
        string bundle = Vst3Wrapper("data/openxlr/yabridge/vst3", "Deep Verb", module);
        var insert = Voice with { Params = new() { ["5"] = 0.25 } };
        string output = Path.Combine(_root, "started.json");
        Set("OPENXLR_TEST_ENV_OUTPUT", output);
        Set("OPENXLR_YABRIDGE", "system");
        Set("WINEPREFIX", null);
        Set("WINELOADER", "/opt/wine/bin/wine");
        Set(WindowsPluginHost.WineVariable, null);
        Set("WINEDLLOVERRIDES", "mscoree,mshtml=");
        WindowsPluginHost.Route? route = windowsHost ? Route(Plugin("vst3", ClassId, "Deep Verb", bundle)) : null;
        using var host = new NativePluginHost(insert, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", Recorder],
            bundle: bundle, windows: route);
        JsonNode started = JsonNode.Parse(File.ReadAllText(output))!;
        string[] arguments = [.. started["arguments"]!.AsArray().Select(a => a!.GetValue<string>())];
        JsonNode environment = started["environment"]!;
        Assert.Equal(windowsHost, host.WindowsHost);
        // Wine runs behind either one, so either one is the daemon's to end.
        Assert.True(host.Bridged);
        Assert.Equal("mscoree,mshtml=;wineusb.sys=d", environment["WINEDLLOVERRIDES"]!.GetValue<string>());
        Assert.Equal("/opt/wine/bin/wine", environment["WINELOADER"]!.GetValue<string>());
        if (windowsHost)
        {
            Assert.Equal(["wine", "vst3", module, ClassId, "node", "2", "48000", "5=0.25"], arguments);
            Assert.Equal(prefix, environment["WINEPREFIX"]!.GetValue<string>());
            Assert.Equal("/opt/wine/bin/wine", environment["OPENXLR_WINE"]!.GetValue<string>());
        }
        else
        {
            // yabridge finds the prefix and the Wine by itself.
            Assert.Equal(["vst3", bundle, ClassId, "node", "2", "48000", "5=0.25"], arguments);
            Assert.Null(environment["WINEPREFIX"]);
            Assert.Null(environment["OPENXLR_WINE"]);
        }
    }

    [Fact]
    public void APrefixTheDaemonWasStartedWithIsTheOneTheHelperGets()
    {
        (_, string module) = InstalledVst3("Deep Verb");
        string bundle = Vst3Wrapper("data/openxlr/yabridge/vst3", "Deep Verb", module);
        string output = Path.Combine(_root, "started.json"), chosen = Path.Combine(_root, "chosen-prefix");
        Set("OPENXLR_TEST_ENV_OUTPUT", output);
        Set("OPENXLR_YABRIDGE", "system");
        Set("WINEPREFIX", chosen);
        using var host = new NativePluginHost(Voice, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", Recorder],
            bundle: bundle, windows: Route(Plugin("vst3", ClassId, "Deep Verb", bundle)));
        Assert.Equal(chosen, JsonNode.Parse(File.ReadAllText(output))!["environment"]!["WINEPREFIX"]!.GetValue<string>());
    }

    // --- a saved choice the build cannot honour -----------------------------------

    [Fact]
    public void ASavedChoiceTheBuildCannotHonourRunsThroughYabridgeAndSaysSo()
    {
        (_, string module) = InstalledVst3("Deep Verb");
        string bundle = Vst3Wrapper("data/openxlr/yabridge/vst3", "Deep Verb", module);
        PluginInfo plugin = Plugin("vst3", ClassId, "Deep Verb", bundle);
        InsertDefinition saved = Voice with { WindowsHost = "openxlr", Label = "My reverb" };
        var notes = new List<string>();
        var events = new List<string>();
        var adapter = new PipeWireAdapter(() => { }, notes.Add) { Events = (_, text) => events.Add(text) };

        // The host is here: the insert runs on it and nothing is said.
        WindowsPluginHost.Route? route = adapter.WindowsRoute(saved, plugin, out string? kept, installed: true);
        Assert.Equal(module, route?.Module);
        Assert.Null(kept);
        Assert.Empty(notes);

        // The host is gone, after a downgrade for one. The chain builder
        // gets no route, so it starts the helper from the wrapper as it
        // does for every other insert of the plugin, and the journal says why.
        Assert.Null(adapter.WindowsRoute(saved, plugin, out kept, installed: false));
        Assert.Equal("this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host", kept);
        Assert.Equal("My reverb is saved to the Windows plugin host and runs through yabridge instead: " + kept, Assert.Single(notes));
        Assert.Equal(notes, events);
        Assert.Equal(["vst3", bundle, ClassId], NativePluginHost.Arguments(saved, plugin.Path, null));

        // The module went away under a host that is here.
        File.Delete(module);
        Assert.Null(adapter.WindowsRoute(saved, plugin, out kept, installed: true));
        Assert.StartsWith("the Windows plugin behind 'Deep Verb' is missing: ", kept);
        Assert.Equal(2, notes.Count);

        // An insert that never chose the host is not looked at.
        Assert.Null(adapter.WindowsRoute(Voice, plugin, out kept, installed: true));
        Assert.Null(kept);
        Assert.Equal(2, notes.Count);
    }

    // --- the editor ---------------------------------------------------------------

    [Fact]
    public void AnInsertOnTheWindowsHostSaysItsEditorIsBlockedAndTheUiKeepsItsButtonOff()
    {
        var running = new InsertStatus(Voice with { WindowsHost = "openxlr" }, null, NativeHostRunning: true) { WindowsHostRunning = true };
        InsertStatus blocked = MixerService.WithEditorBlock(running, null);
        Assert.True(blocked.NativeUiBlocked);
        Assert.Equal(WindowsPluginHost.NoEditor, blocked.NativeUiBlockReason);
        // A compatibility rule for the plugin comes first, and an insert that
        // fell back to yabridge has its editor.
        Assert.Equal("Known editor issue.", MixerService.WithEditorBlock(running, "Known editor issue.").NativeUiBlockReason);
        InsertStatus kept = MixerService.WithEditorBlock(running with { WindowsHostRunning = false, WindowsHostError = "no host" }, null);
        Assert.False(kept.NativeUiBlocked);
        Assert.Null(kept.NativeUiBlockReason);

        // What the daemon sends is what the UI already reads: the button for
        // the plugin's own editor stays visible and cannot be pressed, the
        // reason is shown, and the cog opens OpenXLR's controls.
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2);
        owner.PluginChoices.Add(new(ClassId, "Deep Verb", "Reverb", new JsonArray(), true, true, "vst3"));
        owner.Apply(new JsonArray(JsonSerializer.SerializeToNode(blocked, Wire)));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.True(insert.NativeHostRunning);
        Assert.True(insert.NativeEditorSupported);
        Assert.False(insert.NativeEditorAvailable);
        Assert.Equal(WindowsPluginHost.NoEditor, insert.NativeUiBlockReason);
        Assert.Equal("Open this plugin's controls", insert.ControlsHint);
        // Back on yabridge the same insert has its editor again.
        owner.Apply(new JsonArray(JsonSerializer.SerializeToNode(MixerService.WithEditorBlock(running with { WindowsHostRunning = false }, null), Wire)));
        Assert.True(insert.NativeEditorAvailable);
    }

    // --- the wait for the helper --------------------------------------------------

    [Fact]
    public void AHelperStillWaitingForWineAtTheDeadlineSaysWhatToDo()
    {
        var route = new WindowsPluginHost.Route("vst3", "/m.vst3", null);
        const string silent = "import sys\nfor line in sys.stdin: pass\n";
        NativePluginHost Start(string script, WindowsPluginHost.Route? windows)
            => new(Voice, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", script], TimeSpan.FromMilliseconds(400),
                bundle: "/data/openxlr/yabridge/vst3/Deep Verb.vst3", windows: windows);
        string waiting = Assert.Throws<InvalidOperationException>(() => Start(silent, route)).Message;
        Assert.Equal("Native LV2 startup failed: " + WindowsPluginHost.SilentStart, waiting);
        // What the helper said comes first, and a helper on the other route says nothing new.
        const string said = "import sys\nsys.stderr.write('the plugin did not load\\n')\nsys.stderr.flush()\nfor line in sys.stdin: pass\n";
        Assert.Equal("Native LV2 startup failed: the plugin did not load",
            Assert.Throws<InvalidOperationException>(() => Start(said, route)).Message);
        Assert.Equal("Native LV2 startup failed: ", Assert.Throws<InvalidOperationException>(() => Start(silent, null)).Message);
    }

    // --- what a client is told about the build ------------------------------------

    [Fact]
    public void PluginSetupSaysWhetherTheBuildHasTheHost()
    {
        PluginInstaller Installer(bool helper) => new(Path.Combine(_root, "lv2"), Path.Combine(_root, "clap"), Path.Combine(_root, "vst3"),
            yabridgectl: null, wine: null, hostInstalled: helper, winePrefix: Path.Combine(_root, "wine"),
            windowsImportDirectory: Path.Combine(_root, "imports"));
        Set(WindowsPluginHost.ProgramVariable, Path.Combine(_root, "missing", "openxlr-winhost.exe"));
        Assert.False(Installer(true).Setup().WindowsHostInstalled);
        string program = Path.Combine(_root, "openxlr-winhost.exe");
        File.WriteAllText(program, "the Windows program");
        Set(WindowsPluginHost.ProgramVariable, program);
        PluginSetup setup = Installer(true).Setup();
        Assert.True(setup.WindowsHostInstalled);
        // The Windows program without the helper that starts it is no host.
        Assert.False(Installer(false).Setup().WindowsHostInstalled);
        JsonNode message = JsonSerializer.SerializeToNode(new PluginSetupMessage(setup), Wire)!;
        Assert.True(message["windowsHostInstalled"]!.GetValue<bool>());
        Assert.True(message["hostInstalled"]!.GetValue<bool>());
    }
}
