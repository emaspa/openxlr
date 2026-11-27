using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The host of an insert's Windows plugin: a pin to yabridge or to the
/// Windows plugin host built into OpenXLR, or the daemon's default when the
/// insert has none. The pin is saved with the insert and absent while it
/// follows the default, set by one command that is refused where it cannot
/// work, and carried in the state. The default is a daemon setting, and a
/// hold list keeps a plugin on yabridge while it follows it. The Windows
/// module and its Wine prefix are read back from the yabridge wrapper, the
/// helper is started with its <c>wine</c> backend, a pin the build cannot
/// honour runs through yabridge, and a plugin that follows the default and
/// fails to start on the host is started again through yabridge. Every
/// plugin here is invented.
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

    // --- a plugin that ties its licence to the disk -----------------------------

    [Fact]
    public void APluginThatTiesItsLicenceToTheDiskMovesLikeAnyOther()
    {
        // The Windows plugin host reports the system disk's identity as the
        // companion's host does, so nothing is held back by its name.
        (string prefix, string module) = InstalledVst3("AcmeVoiceFocus_v9.8");
        PluginInfo licensed = Plugin("vst3", ClassId, "Acme Voice Focus", Vst3Wrapper("yabridge/vst3", "AcmeVoiceFocus_v9.8", module));
        WindowsPluginHost.Route route = Route(licensed);
        Assert.Equal((module, prefix), (route.Module, route.Prefix));

        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [new InsertDefinition { Id = "voice", Kind = "vst3", Plugin = ClassId }]);
        Assert.Null(Check(mixer, licensed, WindowsPluginHost.OpenXlr));
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
        Assert.Null(Check(mixer, bridged, "default"));
        Assert.Equal("setInsertWindowsHost: select an existing insert", Check(mixer, bridged, "openxlr", insertId: "missing"));
        Assert.Equal("setInsertWindowsHost: select an existing insert", Check(mixer, bridged, "openxlr", channel: "nowhere"));
        foreach (object value in new object[] { true, 1, "winhost", "OpenXLR", "Default", "" })
            Assert.Equal("setInsertWindowsHost: value must be 'yabridge', 'openxlr' or 'default'", Check(mixer, bridged, value));
        Assert.Equal("setInsertWindowsHost: this build has no Windows plugin host, openxlr-winhost.exe is not beside the native plugin host",
            Check(mixer, bridged, "openxlr", installed: false));
        Assert.Equal("setInsertWindowsHost: 'Native' is not a Windows plugin bridged through yabridge",
            Check(mixer, Plugin("vst3", ClassId, "Native", Path.Combine(_root, "Native.vst3")), "openxlr"));
        Assert.Equal("setInsertWindowsHost: the plugin is not installed", Check(mixer, null, "openxlr"));
        string gone = Path.Combine(_root, "nowhere", "Gone.vst3");
        Assert.Equal($"setInsertWindowsHost: the Windows plugin behind 'Gone' is missing: {gone}",
            Check(mixer, Plugin("vst3", ClassId, "Gone", Vst3Wrapper("yabridge/vst3", "Gone", gone)), "openxlr"));
        // A pin to yabridge and the default are never refused, so an insert
        // can always leave a host that is gone or a plugin that is.
        Assert.Null(Check(mixer, bridged, "yabridge", installed: false));
        Assert.Null(Check(mixer, null, "yabridge", installed: false));
        Assert.Null(Check(mixer, bridged, "default", installed: false));
        Assert.Null(Check(mixer, null, "default", installed: false));
    }

    // --- saved with the insert, carried in the state ------------------------------

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly InsertDefinition Voice = new() { Id = "voice", Kind = "vst3", Plugin = ClassId };

    [Fact]
    public void TheChoiceIsSavedWithTheInsertCarriedInTheStateAndAbsentWhileItFollowsTheDefault()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice, Voice with { Id = "other" }]);
        mixer.SetInsertWindowsHost("xlr1", "voice", WindowsPluginHost.OpenXlr);
        mixer.SetInsertWindowsHost("xlr1", "missing", WindowsPluginHost.OpenXlr);   // an unknown insert changes nothing
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

        mixer.SetInsertWindowsHost("xlr1", "voice", null);
        Assert.Null(mixer.Snapshot().Inserts["xlr1"][0].Insert.WindowsHost);
        Assert.Null(mixer.ExportSettings().Save(path));
        Assert.DoesNotContain("windowsHost", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x"}""", null)]                            // an older save
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"openxlr"}""", "openxlr")]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"yabridge"}""", "yabridge")]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"default"}""", null)]  // a command value, never saved
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"a-host-of-a-newer-build"}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":"OPENXLR"}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":""}""", null)]
    [InlineData("""{"id":"voice","kind":"vst3","plugin":"x","windowsHost":null}""", null)]
    public void AnInsertReadsOnlyTheHostsThisBuildKnowsAndEverythingElseFollowsTheDefault(string json, string? host)
    {
        InsertDefinition insert = JsonSerializer.Deserialize<InsertDefinition>(json, Wire)!;
        Assert.Equal(host, insert.WindowsHost);
        Assert.Equal(host == "openxlr", insert.OnWindowsHost);
        // What is written back holds the field only when it says something.
        Assert.Equal(host is not null, JsonSerializer.SerializeToNode(insert, Wire)!.AsObject().ContainsKey("windowsHost"));
        Assert.False(JsonSerializer.SerializeToNode(insert, Wire)!.AsObject().ContainsKey("onWindowsHost"));
    }

    [Fact]
    public void ASavedFileWithAHostThisBuildDoesNotKnowLoadsWithTheInsertFollowingTheDefault()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice]);
        mixer.SetInsertWindowsHost("xlr1", "voice", WindowsPluginHost.OpenXlr);
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

    /// <summary>
    /// Pins written into <c>mixer.json</c> by hand, with the daemon stopped,
    /// before a build's first start: the daemon reads each as a pin of its
    /// own, an insert without one as following the default, and reading the
    /// file changes nothing in it. A default written into <c>daemon.json</c>
    /// by hand is read the same way and left as it was. What the daemon saves
    /// afterwards keeps the pins as they were. The recall into a built mixer
    /// is followed through the steps it takes for inserts, since building one
    /// needs an audio server.
    /// </summary>
    [Fact]
    public void APinWrittenIntoTheFileByHandIsHonouredAndKept()
    {
        (_, string module) = InstalledVst3("Deep Verb");
        PluginInfo bridged = Plugin("vst3", ClassId, "Deep Verb", Vst3Wrapper("wrappers/yabridge", "Deep Verb", module));
        string path = Path.Combine(_root, "mixer.json");
        string written = $$"""
            {
              "version": {{MixerSettings.CurrentVersion}},
              "inserts": {
                "xlr1": [
                  { "id": "pinned", "kind": "vst3", "plugin": "{{ClassId}}", "windowsHost": "openxlr" },
                  { "id": "yabridge", "kind": "vst3", "plugin": "{{ClassId}}", "windowsHost": "yabridge" },
                  { "id": "default", "kind": "vst3", "plugin": "{{ClassId}}" }
                ]
              }
            }

            """;
        File.WriteAllText(path, written);

        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.Null(warning);
        Assert.Equal(written, File.ReadAllText(path));
        Assert.Equal(["mixer.json"], Directory.EnumerateFiles(_root).Select(Path.GetFileName));
        Assert.Equal(["openxlr", "yabridge", null], loaded.Inserts!["xlr1"].Select(i => i.WindowsHost));

        // The chains as a built mixer takes them from the file (ApplySettings
        // recalls them over the empty ones it starts with), and what the chain
        // builder then starts for each insert, under either default.
        List<InsertDefinition> chain = Mixer.RecallChains(new Dictionary<string, List<InsertDefinition>>(), loaded.Inserts, _ => false, keepWiring: false)["xlr1"];
        Assert.Equal(["openxlr", "yabridge", null], chain.Select(i => i.WindowsHost));
        Assert.Equal([true, false, false], chain.Select(i => i.OnWindowsHost));
        var adapter = new PipeWireAdapter(() => { }, _ => { });
        WindowsPluginHost.Route? Start(InsertDefinition insert, string host, out string? kept)
            => adapter.WindowsRoute(insert, bridged, out kept, installed: true, defaultHost: host, held: []);
        foreach (string host in new[] { WindowsPluginHost.Yabridge, WindowsPluginHost.OpenXlr })
        {
            // A pin wins whatever the default.
            Assert.Equal(module, Start(chain[0], host, out string? kept)!.Module);
            Assert.Null(kept);
            Assert.Null(Start(chain[1], host, out kept));
            Assert.Null(kept);
            // An absent value means the default.
            Assert.Equal(host == WindowsPluginHost.OpenXlr ? module : null, Start(chain[2], host, out kept)?.Module);
            Assert.Null(kept);
        }
        Assert.Equal(written, File.ReadAllText(path));

        // The default written into daemon.json by hand is read as written,
        // and an unknown one as the build's own, and reading changes nothing.
        string config = Path.Combine(_root, "config");
        Set("XDG_CONFIG_HOME", config);
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        string daemon = Path.Combine(config, "openxlr", "daemon.json");
        foreach ((string value, string meant) in new[]
        {
            ("yabridge", WindowsPluginHost.Yabridge),
            ("openxlr", WindowsPluginHost.OpenXlr),
            ("a-host-of-a-newer-build", WindowsPluginHost.BuiltInDefault),
        })
        {
            string settings = $$"""
                {
                  "submixer": true,
                  "windowsHostDefault": "{{value}}"
                }

                """;
            File.WriteAllText(daemon, settings);
            OpenXLR.Core.DaemonSettings read = OpenXLR.Core.DaemonSettings.Load();
            Assert.Equal(value, read.WindowsHostDefault);
            Assert.Equal(meant, WindowsPluginHost.DefaultFrom(read.WindowsHostDefault));
            Assert.Equal(settings, File.ReadAllText(daemon));
        }
        File.WriteAllText(daemon, """{"submixer":true}""");
        Assert.Equal(WindowsPluginHost.BuiltInDefault, WindowsPluginHost.DefaultFrom(OpenXLR.Core.DaemonSettings.Load().WindowsHostDefault));

        // The next save writes the chains back as they were read.
        Assert.Null((loaded with { Inserts = new() { ["xlr1"] = chain } }).Save(path));
        JsonArray saved = JsonNode.Parse(File.ReadAllText(path))!["inserts"]!["xlr1"]!.AsArray();
        Assert.Equal(["pinned", "yabridge", "default"], saved.Select(i => i!["id"]!.GetValue<string>()));
        Assert.Equal("openxlr", saved[0]!["windowsHost"]!.GetValue<string>());
        Assert.Equal("yabridge", saved[1]!["windowsHost"]!.GetValue<string>());
        Assert.False(saved[2]!.AsObject().ContainsKey("windowsHost"));
    }

    [Fact]
    public void RewritingAChainLeavesEveryInsertOnTheHostItHas()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice, Voice with { Id = "other" }]);
        mixer.SetInsertWindowsHost("xlr1", "voice", WindowsPluginHost.OpenXlr);
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

        // An insert that follows a default of yabridge is not looked at.
        Assert.Null(adapter.WindowsRoute(Voice, plugin, out kept, installed: true, defaultHost: WindowsPluginHost.Yabridge));
        Assert.Null(kept);
        Assert.Equal(2, notes.Count);
    }

    // --- three stored states ----------------------------------------------------------

    /// <summary>The insert record as the build before the stored yabridge pin read it: only "openxlr" counted.</summary>
    private sealed record OlderInsert
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Plugin { get; init; }
        public string? WindowsHost { get => _host; init => _host = value == "openxlr" ? value : null; }
        private readonly string? _host;
    }

    [Fact]
    public void BothPinsAreSavedAndAnOlderBuildReadsTheYabridgePinAsItsOwnDefault()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Voice with { Id = "pinned-own" }, Voice with { Id = "pinned-yabridge" }, Voice with { Id = "following" }]);
        mixer.SetInsertWindowsHost("xlr1", "pinned-own", WindowsPluginHost.OpenXlr);
        mixer.SetInsertWindowsHost("xlr1", "pinned-yabridge", WindowsPluginHost.Yabridge);
        string path = Path.Combine(_root, "mixer.json");
        Assert.Null(mixer.ExportSettings().Save(path));
        JsonArray chain = JsonNode.Parse(File.ReadAllText(path))!["inserts"]!["xlr1"]!.AsArray();
        Assert.Equal(["openxlr", "yabridge", null], chain.Select(i => i!["windowsHost"]?.GetValue<string>()));
        Assert.False(chain[2]!.AsObject().ContainsKey("windowsHost"));

        // This build reads its own file back, all three states.
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
        Assert.DoesNotContain("inserts", warning ?? "");
        Assert.Equal(["openxlr", "yabridge", null], loaded.Inserts!["xlr1"].Select(i => i.WindowsHost));
        // An older build reads the same records: a yabridge pin is absent
        // to it, which was yabridge, so a downgrade runs that plugin where
        // the user put it.
        OlderInsert[] older = [.. chain.Select(i => i.Deserialize<OlderInsert>(Wire)!)];
        Assert.Equal(["openxlr", null, null], older.Select(i => i.WindowsHost));
        // And what an older build wrote, absent or "openxlr", reads here as
        // following the default and the pin it was.
        string olderFile = JsonSerializer.Serialize(older, Wire);
        Assert.Equal(["openxlr", null, null],
            JsonSerializer.Deserialize<InsertDefinition[]>(olderFile, Wire)!.Select(i => i.WindowsHost));

        // Following the default again removes the field.
        mixer.SetInsertWindowsHost("xlr1", "pinned-yabridge", null);
        Assert.Equal(["openxlr", null, null], mixer.Snapshot().Inserts["xlr1"].Select(i => i.Insert.WindowsHost));
    }

    // --- the default and the hold list ---------------------------------------------------

    [Fact]
    public void TheBuiltInDefaultIsTheOwnHostAndAnUnknownSavedValueFallsBackToIt()
    {
        Assert.Equal(WindowsPluginHost.OpenXlr, WindowsPluginHost.BuiltInDefault);
        Assert.Equal(WindowsPluginHost.OpenXlr, WindowsPluginHost.DefaultFrom("openxlr"));
        Assert.Equal(WindowsPluginHost.Yabridge, WindowsPluginHost.DefaultFrom("yabridge"));
        foreach (string? saved in new[] { null, "", "default", "OpenXLR", "a-host-of-a-newer-build" })
            Assert.Equal(WindowsPluginHost.BuiltInDefault, WindowsPluginHost.DefaultFrom(saved));
    }

    [Fact]
    public void TheHoldListShipsEmptyAndHoldsAPluginByFormatAndId()
    {
        Assert.Empty(WindowsPluginHost.Held);
        IReadOnlyList<WindowsPluginHost.HeldPlugin> held = [new("vst3", ClassId, "Deep Verb", "it drops out on this host")];
        Assert.Equal("Deep Verb", WindowsPluginHost.HeldEntry(Voice, held)?.Name);
        Assert.Null(WindowsPluginHost.HeldEntry(Voice with { Kind = "clap" }, held));
        Assert.Null(WindowsPluginHost.HeldEntry(Voice with { Plugin = "FFEEDDCCBBAA99887766554433221100" }, held));
        Assert.Null(WindowsPluginHost.HeldEntry(Voice));
    }

    /// <summary>A Windows VST3 plugin installed in a prefix and bridged by the companion, as the catalogue lists it.</summary>
    private (PluginInfo Plugin, string Module) BridgedVst3(string name = "Deep Verb")
    {
        (_, string module) = InstalledVst3(name);
        return (Plugin("vst3", ClassId, name, Vst3Wrapper("data/openxlr/yabridge/vst3", name, module)), module);
    }

    [Fact]
    public void AnInsertThatFollowsTheDefaultResolvesInTheOrderTheDefaultGives()
    {
        (PluginInfo plugin, string module) = BridgedVst3();
        InsertDefinition following = Voice with { Label = "My reverb" };
        var notes = new List<string>();
        var events = new List<string>();
        var adapter = new PipeWireAdapter(() => { }, notes.Add) { Events = (_, text) => events.Add(text) };
        WindowsPluginHost.Route? Resolve(InsertDefinition insert, out string? kept, string host = WindowsPluginHost.OpenXlr,
            bool installed = true, IReadOnlyList<WindowsPluginHost.HeldPlugin>? held = null)
            => adapter.WindowsRoute(insert, plugin, out kept, installed, defaultHost: host, held: held ?? Array.Empty<WindowsPluginHost.HeldPlugin>());

        // The default is yabridge: the wrapper runs, nothing to say.
        Assert.Null(Resolve(following, out string? kept, host: WindowsPluginHost.Yabridge));
        Assert.Null(kept);
        // The default is this host and it finds the 64-bit module.
        Assert.Equal(module, Resolve(following, out kept)?.Module);
        Assert.Null(kept);
        // A held plugin runs through yabridge with the list's reason.
        IReadOnlyList<WindowsPluginHost.HeldPlugin> held = [new("vst3", ClassId, "Deep Verb", "it drops out on this host")];
        Assert.Null(Resolve(following, out kept, held: held));
        Assert.Equal("Deep Verb is held on yabridge in this release: it drops out on this host", kept);
        // A pin wins over the default and over the list, either way.
        Assert.Null(Resolve(following with { WindowsHost = "yabridge" }, out kept));
        Assert.Null(kept);
        Assert.Equal(module, Resolve(following with { WindowsHost = "openxlr" }, out kept, host: WindowsPluginHost.Yabridge, held: held)?.Module);
        // A build without the host runs it through yabridge with nothing to say.
        Assert.Null(Resolve(following, out kept, installed: false));
        Assert.Null(kept);
        // A native plugin is never looked at.
        string native = Path.Combine(_root, "native.vst3");
        Directory.CreateDirectory(native);
        Assert.Null(adapter.WindowsRoute(following, Plugin("vst3", ClassId, "Native", native), out kept, installed: true,
            defaultHost: WindowsPluginHost.OpenXlr, held: []));
        Assert.Null(kept);

        // The host cannot run it: yabridge does, with the reason, and the
        // journal and the timeline stay quiet, since every chain build
        // would say it again.
        File.Delete(module);
        Assert.Null(Resolve(following, out kept));
        Assert.StartsWith("the Windows plugin behind 'Deep Verb' is missing: ", kept);
        Assert.Empty(notes);
        Assert.Empty(events);
        // A pin to the host that cannot run it still says so.
        Assert.Null(Resolve(following with { WindowsHost = "openxlr" }, out kept));
        Assert.StartsWith("My reverb is saved to the Windows plugin host and runs through yabridge instead: ", Assert.Single(notes));
    }

    [Fact]
    public void TheDefaultDecidesTheChainStructureOnlyForAnUnpinnedBridgedPlugin()
    {
        (PluginInfo plugin, _) = BridgedVst3();
        IReadOnlyList<WindowsPluginHost.HeldPlugin> none = [];
        bool Wanted(InsertDefinition insert, string host, PluginInfo? found = null, IReadOnlyList<WindowsPluginHost.HeldPlugin>? held = null)
            => WindowsPluginHost.Wanted(insert, found ?? plugin, host, held ?? none);
        Assert.False(Wanted(Voice, WindowsPluginHost.Yabridge));
        Assert.True(Wanted(Voice, WindowsPluginHost.OpenXlr));
        Assert.True(Wanted(Voice with { WindowsHost = "openxlr" }, WindowsPluginHost.Yabridge));
        Assert.False(Wanted(Voice with { WindowsHost = "yabridge" }, WindowsPluginHost.OpenXlr));
        Assert.False(Wanted(Voice, WindowsPluginHost.OpenXlr, held: [new("vst3", ClassId, "Deep Verb", "held")]));
        // A native plugin and one with no wrapper never move with the default.
        Assert.False(Wanted(Voice, WindowsPluginHost.OpenXlr, Plugin("vst3", ClassId, "Native", "/usr/lib/vst3/Native.vst3")));
        Assert.False(Wanted(Voice, WindowsPluginHost.OpenXlr, plugin with { WindowsHostOnly = true }));
        Assert.False(Wanted(Voice with { Kind = "lv2" }, WindowsPluginHost.OpenXlr));
    }

    // --- the second start through yabridge ---------------------------------------------

    [Fact]
    public void APluginThatFollowsTheDefaultAndFailsOnTheHostStartsOnceMoreThroughYabridge()
    {
        (PluginInfo plugin, string module) = BridgedVst3();
        var route = new WindowsPluginHost.Route("vst3", module, null);
        var notes = new List<string>();
        var events = new List<string>();
        var adapter = new PipeWireAdapter(() => { }, notes.Add) { Events = (_, text) => events.Add(text) };
        var tried = new List<WindowsPluginHost.Route?>();
        string Start(WindowsPluginHost.Route? on)
        {
            tried.Add(on);
            return on is null ? "yabridge" : throw new InvalidOperationException(NativePluginHost.StartupFailed + "the plugin did not load");
        }
        InsertDefinition following = Voice with { Label = "My reverb" };

        string? kept = null;
        Assert.Equal("yabridge", adapter.StartHelper(following, plugin, route, ref kept, Start, yabridge: () => true));
        Assert.Equal([route, null], tried);
        Assert.Equal("it did not start on the Windows plugin host: the plugin did not load", kept);
        Assert.Equal("My reverb follows the default host and it did not start on the Windows plugin host: the plugin did not load, "
            + "so it starts through yabridge instead", Assert.Single(notes));
        Assert.Equal(notes, events);

        // A start that works is the only start.
        tried.Clear();
        kept = null;
        Assert.Equal("own", adapter.StartHelper(following, plugin, route, ref kept, on => { tried.Add(on); return "own"; }, yabridge: () => true));
        Assert.Equal([route], tried);
        Assert.Null(kept);

        // A pinned insert, a plugin with no wrapper and a machine without
        // yabridge fail as they did.
        foreach ((InsertDefinition insert, PluginInfo info, bool yabridge) in new[]
        {
            (following with { WindowsHost = "openxlr" }, plugin, true),
            (following, plugin with { WindowsHostOnly = true }, true),
            (following, plugin, false),
        })
        {
            tried.Clear();
            kept = null;
            Assert.Throws<InvalidOperationException>(() => adapter.StartHelper(insert, info, route, ref kept, Start, yabridge: () => yabridge));
            Assert.Equal([route], tried);
            Assert.Null(kept);
        }
        Assert.Single(notes);
    }

    [Fact]
    public void AStartThatRanIntoTheDeadlineIsNotStartedAgain()
    {
        // Through the real helper start, so the exceptions are the ones the
        // chain builder sees: a helper that says nothing for the whole wait,
        // and one that fails at once on the Windows plugin host only.
        (PluginInfo plugin, string module) = BridgedVst3();
        var route = new WindowsPluginHost.Route("vst3", module, null);
        var adapter = new PipeWireAdapter(() => { }, _ => { });
        const string silent = "import sys\nfor line in sys.stdin: pass\n";
        const string ownHostFails = """
            import sys
            if sys.argv[1] == 'wine':
                import time
                sys.stderr.write('the plugin did not load\n')
                sys.stderr.flush()
                time.sleep(0.3)
                sys.exit(1)
            print('ready', flush=True)
            for line in sys.stdin: pass
            """;
        var tried = new List<bool>();
        Func<WindowsPluginHost.Route?, NativePluginHost> Helper(string script) => on =>
        {
            tried.Add(on is not null);
            return new NativePluginHost(Voice, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", script], TimeSpan.FromMilliseconds(600),
                bundle: plugin.Path, windows: on);
        };
        Set("OPENXLR_YABRIDGE", "system");

        string? kept = null;
        var timeout = Assert.Throws<InvalidOperationException>(() => adapter.StartHelper(Voice, plugin, route, ref kept, Helper(silent), yabridge: () => true));
        Assert.Equal(NativePluginHost.StartupFailed + WindowsPluginHost.SilentStart, timeout.Message);
        Assert.Equal([true], tried);
        Assert.Null(kept);

        tried.Clear();
        using NativePluginHost started = adapter.StartHelper(Voice, plugin, route, ref kept, Helper(ownHostFails), yabridge: () => true);
        Assert.Equal([true, false], tried);
        Assert.False(started.WindowsHost);
        Assert.True(started.IsRunning);
        Assert.Equal("it did not start on the Windows plugin host: the plugin did not load", kept);
    }

    // --- the setting ------------------------------------------------------------------

    [Fact]
    public async Task TheDefaultIsSavedInDaemonJsonReportedInPluginSetupAndRefusedForAnyOtherValue()
    {
        string before = WindowsPluginHost.Default;
        try
        {
            await using var fixture = new PluginWineTraceFixture();
            string config = Path.Combine(fixture.Root, "config", "openxlr");
            Directory.CreateDirectory(config);
            string file = Path.Combine(config, "daemon.json");
            File.WriteAllText(file, """{"submixer":true,"pluginFolders":[{"kind":"windows","path":"/w"}],"futureKey":7}""");

            var initial = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"getPluginSetup"}""");
            PluginSetupMessage setup = Assert.IsType<PluginSetupMessage>(Assert.Single(initial.Messages));
            Assert.Equal(WindowsPluginHost.Default, setup.WindowsHostDefault);
            Assert.Equal(setup.WindowsHostDefault, JsonSerializer.SerializeToNode(setup)!["windowsHostDefault"]!.GetValue<string>());

            foreach (string host in new[] { "openxlr", "yabridge", "openxlr" })
            {
                var result = await fixture.Hub.ExecuteForApiAsync(JsonSerializer.Serialize(new { cmd = "setWindowsHostDefault", value = host, requestId = "d" }));
                Assert.True(result.Ok);
                setup = Assert.IsType<PluginSetupMessage>(result.Messages[0]);
                Assert.Equal(host, setup.WindowsHostDefault);
                Assert.Null(Assert.IsType<CommandResultMessage>(result.Messages[^1]).Error);
                JsonNode saved = JsonNode.Parse(File.ReadAllText(file))!;
                Assert.Equal(host, saved["windowsHostDefault"]!.GetValue<string>());
                // Every other key stays, the ones this build does not know too.
                Assert.True(saved["submixer"]!.GetValue<bool>());
                Assert.Equal("/w", saved["pluginFolders"]![0]!["path"]!.GetValue<string>());
                Assert.Equal(7, saved["futureKey"]!.GetValue<int>());
            }
            Assert.Equal("openxlr", OpenXLR.Core.DaemonSettings.Load().WindowsHostDefault);

            foreach (string fields in new[] { "{}", "{\"value\":null}", "{\"value\":true}", "{\"value\":\"default\"}", "{\"value\":\"OpenXLR\"}" })
            {
                JsonNode command = JsonNode.Parse(fields)!;
                command["cmd"] = "setWindowsHostDefault";
                command["requestId"] = "refused";
                Assert.Equal("setWindowsHostDefault: value must be 'yabridge' or 'openxlr'", CommandValidation.CheckWindowsHostDefault(command.Deserialize<Command>()!));
                var refused = await fixture.Hub.ExecuteForApiAsync(command.ToJsonString());
                Assert.False(refused.Ok);
                Assert.Equal("setWindowsHostDefault: value must be 'yabridge' or 'openxlr'", Assert.IsType<CommandResultMessage>(refused.Messages[^1]).Error);
            }
            Assert.Equal("openxlr", JsonNode.Parse(File.ReadAllText(file))!["windowsHostDefault"]!.GetValue<string>());

            // A file that cannot be read is not written over.
            File.WriteAllText(file, "{ not json");
            var unreadable = await fixture.Hub.ExecuteForApiAsync("""{"cmd":"setWindowsHostDefault","value":"yabridge","requestId":"unreadable"}""");
            Assert.False(unreadable.Ok);
            Assert.StartsWith("daemon.json cannot be read", Assert.IsType<CommandResultMessage>(unreadable.Messages[^1]).Error);
            Assert.Equal("{ not json", File.ReadAllText(file));
        }
        finally { WindowsPluginHost.Default = before; }
    }

    [Fact]
    public void TheDefaultIsMachineWiringAndNoStep()
        => Assert.Equal(HistoryClass.Ignored, HistoryPolicy.ClassOf("setWindowsHostDefault"));

    [Fact]
    public void OptionsShowsTheDefaultOnABuildWithTheHostAndSendsTheOtherChoice()
    {
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        options.ApplyPluginSetup(JsonNode.Parse("""{"windowsHostInstalled":true,"windowsHostDefault":"yabridge"}"""));
        Assert.True(options.ShowsWindowsHostDefault);
        Assert.True(options.CanSetWindowsHostDefault);
        Assert.Equal("yabridge", options.SelectedWindowsHostDefault?.Value);
        Assert.Equal(["yabridge", "openxlr"], options.WindowsHostDefaultOptions.Select(o => o.Value));
        options.ApplyPluginSetup(JsonNode.Parse("""{"windowsHostInstalled":true,"windowsHostDefault":"openxlr"}"""));
        Assert.Equal("openxlr", options.SelectedWindowsHostDefault?.Value);
        // A build without the host has nothing to choose.
        options.ApplyPluginSetup(JsonNode.Parse("""{"windowsHostInstalled":false,"windowsHostDefault":"yabridge"}"""));
        Assert.False(options.ShowsWindowsHostDefault);
        Assert.False(options.CanSetWindowsHostDefault);
        // An older daemon says nothing about it.
        options.ApplyPluginSetup(JsonNode.Parse("""{"windowsHostInstalled":true}"""));
        Assert.Null(options.SelectedWindowsHostDefault);
        Assert.False(options.CanSetWindowsHostDefault);
    }

    // --- the editor ---------------------------------------------------------------

    [Fact]
    public void AnInsertOnTheWindowsHostHasItsEditorAndTheUiOffersIt()
    {
        var running = new InsertStatus(Voice with { WindowsHost = "openxlr" }, null, NativeHostRunning: true) { WindowsHostRunning = true };
        InsertStatus open = MixerService.WithEditorBlock(running, null);
        Assert.False(open.NativeUiBlocked);
        Assert.Null(open.NativeUiBlockReason);
        // A compatibility rule for the plugin blocks the editor on this host
        // as on any other.
        InsertStatus ruled = MixerService.WithEditorBlock(running, "Known editor issue.");
        Assert.True(ruled.NativeUiBlocked);
        Assert.Equal("Known editor issue.", ruled.NativeUiBlockReason);

        // What the daemon sends is what the UI already reads: the button for
        // the plugin's own editor can be pressed while the insert runs here.
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2);
        owner.PluginChoices.Add(new(ClassId, "Deep Verb", "Reverb", new JsonArray(), true, true, "vst3"));
        owner.Apply(new JsonArray(JsonSerializer.SerializeToNode(open, Wire)));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.True(insert.NativeHostRunning);
        Assert.True(insert.NativeEditorSupported);
        Assert.True(insert.NativeEditorAvailable);
        Assert.Null(insert.NativeUiBlockReason);
        // And cannot under the rule, with the reason shown.
        owner.Apply(new JsonArray(JsonSerializer.SerializeToNode(ruled, Wire)));
        Assert.False(insert.NativeEditorAvailable);
        Assert.Equal("Known editor issue.", insert.NativeUiBlockReason);
    }

    [Fact]
    public void AHelperOnTheWindowsHostIsAskedForItsEditorLikeAnyOther()
    {
        // The helper's answer to `show` is the daemon's answer to
        // showInsertUi, whichever backend the helper runs.
        const string helper = """
            import sys
            print('ready', flush=True)
            for line in sys.stdin:
                if line.strip() == 'show':
                    print(REPLY, flush=True)
            """;
        NativePluginHost Start(string reply)
            => new(Voice, "node", 2, 48000, "/usr/bin/python3", ["-u", "-c", helper.Replace("REPLY", reply)], TimeSpan.FromSeconds(8),
                bundle: "/data/openxlr/yabridge/vst3/Deep Verb.vst3", windows: new WindowsPluginHost.Route("vst3", "/m.vst3", null));
        using (NativePluginHost opened = Start("'ui opened'"))
        {
            Assert.True(opened.WindowsHost);
            opened.ShowUi();
        }
        using NativePluginHost refused = Start("'ui unavailable: this plugin has no editor the host can show'");
        Assert.Equal("unavailable: this plugin has no editor the host can show",
            Assert.Throws<InvalidOperationException>(refused.ShowUi).Message);
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
