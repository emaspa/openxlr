using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ClapCatalogTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("audioIns")]
    [InlineData("audioOuts")]
    [InlineData("gui")]
    [InlineData("layoutRefused")]
    public void NestedScalarValuesCannotReplacePluginIdentity(string field)
    {
        foreach (string invalid in new[] { "{\"id\":\"nested\"}", "[{\"id\":\"nested\"}]" })
        {
            string description = "{\"plugins\":[{\"id\":\"first\",\"" + field + "\":" + invalid
                + ",\"params\":[{\"id\":7}]},{\"id\":\"second\"}]}";
            foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
            {
                Assert.Equal(["first", "second"], plugins.Select(p => p.Plugin));
                Assert.Equal("7", Assert.Single(plugins[0].Params).Symbol);
            }
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("default")]
    [InlineData("stepped")]
    [InlineData("enum")]
    public void NestedScalarValuesCannotReplaceControlIdentity(string field)
    {
        foreach (string invalid in new[] { "{\"id\":99}", "[{\"id\":99}]" })
        {
            string description = "{\"plugins\":[{\"id\":\"test\",\"params\":[{\"id\":7,\"" + field
                + "\":" + invalid + "},{\"id\":8}]}]}";
            foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
                Assert.Equal(["7", "8"], Assert.Single(plugins).Params.Select(p => p.Symbol));
        }
    }

    [Fact]
    public void NestedFileMetadataCannotSupplyPlugins()
    {
        const string description = """
            {"file":{"plugins":[{"id":"nested"}]},"plugins":[{"id":"real"}]}
            """;
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
        {
            Assert.Equal("real", Assert.Single(plugins).Plugin);
            Assert.Equal("", plugins[0].Path);
        }
    }

    [Fact]
    public void NonObjectPluginEntriesDoNotHideLaterPlugins()
    {
        const string description = """
            {"plugins":[null,42,"bad",[{"id":"nested"}],{"id":{"id":"nested"}},{"id":"real"}]}
            """;
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
            Assert.Equal("real", Assert.Single(plugins).Plugin);
    }

    [Fact]
    public void ControlsHaveOrderedBoundsAndDefaultsWithinTheirRange()
    {
        const string description = """
            {"plugins":[{"id":"test","params":[
                {"id":1,"min":2,"max":1},
                {"id":2,"min":3,"max":3,"default":3},
                {"id":3,"min":-5,"max":-1,"default":-6},
                {"id":4,"min":10,"max":20,"default":21},
                {"id":5,"min":10,"max":20}
            ]}]}
            """;
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
        {
            IReadOnlyList<PluginParam> controls = Assert.Single(plugins).Params;
            Assert.Equal(["2", "3", "4", "5"], controls.Select(p => p.Symbol));
            Assert.Equal([3.0, -5.0, 20.0, 10.0], controls.Select(p => p.Default));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"0\"}")]
    [InlineData("{\"id\":-1}")]
    [InlineData("{\"id\":0.5}")]
    [InlineData("{\"id\":4294967296}")]
    [InlineData("{\"id\":4294967295}")]
    [InlineData("{\"id\":1e999}")]
    [InlineData("{\"id\":{\"nested\":1}}")]
    [InlineData("{\"id\":[0]}")]
    public void InvalidParameterIdsCannotAliasARealControl(string invalid)
    {
        string description = "{\"plugins\":[{\"id\":\"test\",\"params\":[" + invalid
            + ",{\"id\":0,\"name\":\"Zero\"},{\"id\":4294967294,\"name\":\"High\"}]}]}";
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
            Assert.Equal(["0", "4294967294"], Assert.Single(plugins).Params.Select(p => p.Symbol));
    }

    [Fact]
    public void DuplicateParameterIdsKeepOnlyTheFirstControl()
    {
        const string description = """
            {"plugins":[{"id":"test","params":[
                {"id":7,"name":"First"},{"id":7,"name":"Duplicate"},{"id":9,"name":"Other"}
            ]}]}
            """;
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
            Assert.Equal(["First", "Other"], Assert.Single(plugins).Params.Select(p => p.Name));
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("default")]
    public void NonFiniteControlsCannotBreakTheCatalogueReply(string field)
    {
        string description = "{\"file\":\"plugin.clap\",\"plugins\":[{\"id\":\"test\",\"name\":\"Test\",\"params\":["
            + "{\"id\":1,\"name\":\"Broken\",\"" + field + "\":1e999},"
            + "{\"id\":2,\"name\":\"Healthy\",\"min\":0,\"max\":1,\"default\":0.5}]}]}";
        foreach (IReadOnlyList<PluginInfo> plugins in new[] { ClapCatalog.Parse(description), Vst3Catalog.Parse(description) })
        {
            PluginParam control = Assert.Single(Assert.Single(plugins).Params);
            Assert.Equal("2", control.Symbol);
            Assert.Equal(0.5, control.Default);
            Assert.NotEmpty(JsonSerializer.SerializeToUtf8Bytes(plugins));
        }
    }

    // What the helper's scanner prints for one bundle, shortened.
    private const string Scan = """
        {"file":"/usr/lib/clap/DragonflyHallReverb.clap","plugins":[
          {"id":"michaelwillis.dragonfly.hall","name":"Dragonfly Hall Reverb","vendor":"Michael Willis",
           "features":["audio-effect","reverb","stereo"],"audioIns":2,"audioOuts":2,"gui":true,
           "params":[
             {"id":3,"name":"Size","module":"","min":10,"max":60,"default":24,"readonly":false,"stepped":false,"enum":false},
             {"id":7,"name":"Enable","module":"","min":0,"max":1,"default":1,"readonly":false,"stepped":true,"enum":false}]},
          {"id":"vendor.tool","name":"Analyser","vendor":"","features":["analyzer","stereo"],"audioIns":2,"audioOuts":2,
           "gui":false,"params":[]}
        ]}
        """;

    // A bundle whose plugins the helper would refuse to load. Both are real
    // LSP layouts: a mixer declares more input buses than the host carries,
    // a crossover more output buses.
    private const string RefusedScan = """
        {"file":"/usr/lib/clap/lsp-plugins.clap","plugins":[
          {"id":"in.lsp-plug.mixer_x8_mono","name":"Mixer x8 Mono","vendor":"LSP",
           "features":["audio-effect","mono"],"audioIns":0,"audioOuts":1,"gui":true,
           "layoutRefused":"the plugin declares 9 audio input ports; this host carries 1 to 8","params":[]},
          {"id":"in.lsp-plug.crossover_stereo","name":"Crossover Stereo","vendor":"LSP",
           "features":["audio-effect","stereo"],"audioIns":2,"audioOuts":0,"gui":true,
           "layoutRefused":"the plugin declares 9 audio output ports; this host carries 1 to 8","params":[]},
          {"id":"in.lsp-plug.comp_stereo","name":"Compressor Stereo","vendor":"LSP",
           "features":["audio-effect","stereo"],"audioIns":2,"audioOuts":2,"gui":true,"params":[]}
        ]}
        """;

    [Fact]
    public void APortLayoutTheHostWouldRefuseKeepsThePluginOutOfThePicker()
    {
        // The helper refuses a layout it cannot hand a buffer for, and says
        // so in the scan. The catalogue carries that through as an
        // unsupported feature, so the picker never offers a plugin that would
        // then be turned away, and the reason is the loader's own words.
        IReadOnlyList<PluginInfo> found = ClapCatalog.Parse(RefusedScan);
        Assert.Equal(3, found.Count);

        Assert.False(found[0].Supported);
        Assert.Equal(["the plugin declares 9 audio input ports; this host carries 1 to 8"],
            found[0].UnsupportedFeatures);
        Assert.False(found[1].Supported);
        Assert.Equal(["the plugin declares 9 audio output ports; this host carries 1 to 8"],
            found[1].UnsupportedFeatures);

        // Both directions are still described. A refusal in one used to leave
        // the other reading uninitialised stack in the helper, which reached
        // the catalogue as a nonsense width.
        Assert.Equal((0, 1), (found[0].AudioIns, found[0].AudioOuts));
        Assert.Equal((2, 0), (found[1].AudioIns, found[1].AudioOuts));

        // A plugin from the same bundle with a layout the host carries is
        // offered as usual.
        Assert.True(found[2].Supported);
        Assert.Empty(found[2].UnsupportedFeatures);
        Assert.Equal((2, 2), (found[2].AudioIns, found[2].AudioOuts));
    }

    [Fact]
    public void TheScannersJsonBecomesPluginsThePickerCanOffer()
    {
        IReadOnlyList<PluginInfo> found = ClapCatalog.Parse(Scan);
        Assert.Equal(2, found.Count);

        PluginInfo hall = found[0];
        Assert.Equal("clap", hall.Kind);
        Assert.Equal("michaelwillis.dragonfly.hall", hall.Plugin);
        Assert.Equal("Dragonfly Hall Reverb", hall.Name);
        Assert.Equal("Reverb", hall.Category);
        Assert.Equal("/usr/lib/clap/DragonflyHallReverb.clap", hall.Path);
        Assert.Equal((2, 2), (hall.AudioIns, hall.AudioOuts));
        Assert.True(hall.HasNativeUi);
        Assert.True(hall.Supported);            // nothing stops it being inserted
        Assert.True(hall.NativeEditorSupported); // and its editor is one the host can show

        // Parameters are addressed by their CLAP id, which is how the helper
        // names them on its pipe.
        PluginParam size = hall.Params[0];
        Assert.Equal(("3", "Size", 10.0, 60.0, 24.0), (size.Symbol, size.Name, size.Min, size.Max, size.Default));
        Assert.False(size.Integer);
        PluginParam enable = hall.Params[1];
        Assert.True(enable.Toggled);
        Assert.True(enable.Integer);

        PluginInfo analyser = found[1];
        Assert.False(analyser.HasNativeUi);
        Assert.False(analyser.NativeEditorSupported);
    }

    [Fact]
    public void AVST3ModuleIsReadTheSameWayNamedByClassId()
    {
        const string scan = """
            {"file":"/usr/lib/vst3/lsp-plugins.vst3","plugins":[
              {"id":"647370206B316D202020202062677379","name":"Compressor Mono","vendor":"LSP",
               "features":["Fx","Dynamics","Mono"],"audioIns":1,"audioOuts":1,"gui":true,
               "params":[{"id":5,"name":"Ratio","module":"","min":1,"max":100,"default":4,"readonly":false,"stepped":false,"enum":false}]}]}
            """;
        PluginInfo comp = Assert.Single(Vst3Catalog.Parse(scan));
        Assert.Equal("vst3", comp.Kind);
        Assert.Equal("647370206B316D202020202062677379", comp.Plugin);
        Assert.Equal("Dynamics", comp.Category);   // "Fx" and "Mono" are the generic words
        Assert.Equal("/usr/lib/vst3/lsp-plugins.vst3", comp.Path);
        Assert.True(comp.NativeEditorSupported);
        Assert.Equal("5", comp.Params[0].Symbol);

        var insert = new InsertDefinition { Id = "c", Kind = "vst3", Plugin = comp.Plugin };
        Assert.True(insert.RunsNatively);
        Assert.Equal(["vst3", comp.Path!, comp.Plugin], NativePluginHost.Arguments(insert, comp.Path));
    }

    [Theory]
    [InlineData(new[] { "Fx", "Reverb", "Stereo" }, "Reverb")]
    [InlineData(new[] { "Fx", "Stereo" }, "Effect")]
    [InlineData(new[] { "audio-effect", "reverb", "stereo" }, "Reverb")]
    [InlineData(new[] { "audio-effect", "multi-effects" }, "Multi Effects")]
    [InlineData(new[] { "audio-effect", "stereo" }, "Effect")]
    [InlineData(new[] { "instrument", "synthesizer" }, "Synthesizer")]
    [InlineData(new string[0], "")]
    public void TheCategoryComesFromThePluginsOwnFeatureList(string[] features, string expected)
        => Assert.Equal(expected, HostScan.Category(features));

    [Fact]
    public void ABadBundleDescriptionIsSkippedNotFatal()
    {
        Assert.Empty(ClapCatalog.Parse("""{"file":"x","plugins":[{"name":"no id"}]}"""));
        Assert.Empty(ClapCatalog.Parse("[]"));
        Assert.ThrowsAny<JsonException>(() => ClapCatalog.Parse("not json"));   // the reader's subtype counts
    }

    [Fact]
    public void TheHelperIsToldTheFormatAndWhereToFindThePlugin()
    {
        var lv2 = new InsertDefinition { Id = "a", Kind = "lv2", Plugin = "urn:lv2:comp" };
        Assert.Equal(["lv2", "urn:lv2:comp"], NativePluginHost.Arguments(lv2, null));

        var clap = new InsertDefinition { Id = "b", Kind = "clap", Plugin = "vendor.reverb" };
        Assert.Equal(["clap", "/usr/lib/clap/reverb.clap", "vendor.reverb"],
            NativePluginHost.Arguments(clap, "/usr/lib/clap/reverb.clap"));
        Assert.Throws<InvalidOperationException>(() => NativePluginHost.Arguments(clap, null));
    }

    [Fact]
    public void ACLAPInsertAlwaysRunsInTheNativeHost()
    {
        var clap = new InsertDefinition { Id = "b", Kind = "clap", Plugin = "vendor.reverb" };
        Assert.True(clap.RunsNatively);
        Assert.False(clap.NativeHost);   // nothing was chosen; there is nothing to choose
        var lv2 = new InsertDefinition { Id = "a", Kind = "lv2", Plugin = "urn:lv2:comp" };
        Assert.False(lv2.RunsNatively);
        Assert.True((lv2 with { NativeHost = true }).RunsNatively);

        // The derived flag is not part of the wire format.
        string json = JsonSerializer.Serialize(clap, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("runsNatively", json);
    }

    [Fact]
    public void ValidationAcceptsTheKindAndNeedsTheHostForIt()
    {
        PluginInfo reverb = ClapCatalog.Parse(Scan)[0];
        var command = new Command
        {
            Cmd = "setInserts",
            Channel = "mix:stream",
            Inserts = [new InsertDefinition { Id = "r", Kind = "clap", Plugin = reverb.Plugin, Params = new() { ["3"] = 40 } }],
        };
        string? verdict = CommandValidation.Check(command, new Layout(), _ => reverb);
        if (PluginCatalog.HostInstalled) Assert.Null(verdict);
        else Assert.Contains("not installed", verdict);

        // A parameter it does not have is refused by name. Asked through an
        // LV2 insert, so the answer is the same on a machine without the host.
        command.Inserts[0] = command.Inserts[0] with { Kind = "lv2", Params = new() { ["99"] = 1 } };
        Assert.Contains("has no control", CommandValidation.Check(command, new Layout(), _ => reverb));
    }

    [Fact]
    public void ThePickersFormatButtonsNarrowTheListAndNeverEmptyItByDefault()
    {
        var empty = new System.Text.Json.Nodes.JsonArray();
        PluginChoice[] choices =
        [
            new("urn:a", "Dragonfly Hall Reverb", "Reverb", empty, Kind: "lv2"),
            new("vendor.hall", "Dragonfly Hall Reverb", "Reverb", empty, Kind: "clap"),
            new("urn:b", "LSP Compressor", "Compressor", empty, Kind: "lv2"),
            new("647370206B316D202020202062677379", "Compressor Mono", "Dynamics", empty, Kind: "vst3"),
        ];
        Assert.Equal(4, PluginPickerWindow.Matching(choices, "", lv2: false, clap: false).Count());
        Assert.Equal(["vendor.hall"], PluginPickerWindow.Matching(choices, "", lv2: false, clap: true).Select(c => c.Uri));
        Assert.Equal(2, PluginPickerWindow.Matching(choices, "", lv2: true, clap: false).Count());
        Assert.Equal(3, PluginPickerWindow.Matching(choices, "", lv2: true, clap: true).Count());
        Assert.Equal(["647370206B316D202020202062677379"], PluginPickerWindow.Matching(choices, "", lv2: false, clap: false, vst3: true).Select(c => c.Uri));
        // The search and the buttons combine, and the search matches the format too.
        Assert.Equal(["urn:a"], PluginPickerWindow.Matching(choices, "hall", lv2: true, clap: false).Select(c => c.Uri));
        Assert.Equal(["vendor.hall"], PluginPickerWindow.Matching(choices, "clap", lv2: false, clap: false).Select(c => c.Uri));
    }

    [Fact]
    public void AScanIsKeptUntilTheBundleChanges()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-scan-cache-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Thing.vst3");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux"));
            string library = Path.Combine(bundle, "Contents", "x86_64-linux", "Thing.so");
            File.WriteAllText(library, "not really");
            string cacheDir = Path.Combine(dir, "cache", "plugin-scans");
            byte[] description = System.Text.Encoding.UTF8.GetBytes("{\"file\":\"x\",\"plugins\":[]}");

            var cache = new ScanCache(cacheDir);
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            cache.Save();
            Assert.True(File.Exists(Path.Combine(cacheDir, "index.json")));

            // A fresh daemon reads it back without scanning.
            Assert.Equal(description, new ScanCache(cacheDir).Lookup(bundle));

            // A changed library means a new scan.
            File.WriteAllText(library, "not really, but longer now");
            Assert.Null(new ScanCache(cacheDir).Lookup(bundle));

            // A bundle that is gone leaves the cache on the next save.
            Directory.Delete(bundle, true);
            var later = new ScanCache(cacheDir);
            later.Store(Path.Combine(dir, "nothing.vst3"), description);   // a bundle that does not exist is not stored
            later.Save();
            Assert.DoesNotContain("Thing.vst3", File.ReadAllText(Path.Combine(cacheDir, "index.json")));
            Assert.Single(Directory.GetFiles(cacheDir));   // only the index is left
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A daemon that died mid-store used to leave "<hash>.json.tmp" behind,
    /// and the next store of the same bundle failed on the name being taken,
    /// so that bundle was scanned at every start from then on.
    /// </summary>
    [Fact]
    public void AStaleTemporaryFileDoesNotBlockStoringTheBundle()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-scan-stale-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Thing.vst3");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux"));
            File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-linux", "Thing.so"), "not really");
            string cacheDir = Path.Combine(dir, "cache", "plugin-scans");
            byte[] description = System.Text.Encoding.UTF8.GetBytes("{\"file\":\"x\",\"plugins\":[]}");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(bundle)));
            Directory.CreateDirectory(cacheDir);
            string stale = Path.Combine(cacheDir, hash + ".json.tmp");
            File.WriteAllText(stale, "half written");

            var cache = new ScanCache(cacheDir);
            cache.Store(bundle, description);
            cache.Save();

            Assert.Equal(description, new ScanCache(cacheDir).Lookup(bundle));
            Assert.Equal("half written", File.ReadAllText(stale));   // not ours to touch
            Assert.Empty(Directory.GetFiles(cacheDir, ".openxlr-*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AScanIsReadAgainWhenTheHelperThatWroteItChanged()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-scan-scanner-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Thing.clap");
            File.WriteAllText(bundle, "not really");
            string cacheDir = Path.Combine(dir, "cache", "plugin-scans");
            string index = Path.Combine(cacheDir, "index.json");
            byte[] description = System.Text.Encoding.UTF8.GetBytes("{\"file\":\"x\",\"plugins\":[]}");

            var first = new ScanCache(cacheDir, "helper-one");
            first.Store(bundle, description);
            first.Save();

            // The same helper answers from the cache; another one does not,
            // however untouched the bundle is.
            Assert.Equal(description, new ScanCache(cacheDir, "helper-one").Lookup(bundle));
            Assert.Null(new ScanCache(cacheDir, "helper-two").Lookup(bundle));

            // What the daemon uses when it is not told: the helper's own file,
            // which says something else as soon as another one is installed.
            Assert.Equal(ScanCache.StampText(NativePluginHost.Executable), ScanCache.ScannerStamp);
            string helper = Path.Combine(dir, "openxlr-lv2-host");
            Assert.Equal("none", ScanCache.StampText(helper));
            File.WriteAllText(helper, "one helper");
            string oneHelper = ScanCache.StampText(helper);
            File.WriteAllText(helper, "a helper that is longer");
            Assert.NotEqual(oneHelper, ScanCache.StampText(helper));

            Assert.Null(new ScanCache(cacheDir).Lookup(bundle));
            var stamped = new ScanCache(cacheDir);
            stamped.Store(bundle, description);
            stamped.Save();
            Assert.Equal(description, new ScanCache(cacheDir).Lookup(bundle));
            Assert.Equal(description, new ScanCache(cacheDir, ScanCache.ScannerStamp).Lookup(bundle));

            // An index written before any of this was recorded: one scan each,
            // and then it is kept like any other.
            string written = File.ReadAllText(index);
            Assert.Contains("\"scanner\":", written);
            File.WriteAllText(index, System.Text.RegularExpressions.Regex.Replace(written, ",\"scanner\":\"[^\"]*\"", ""));
            Assert.Null(new ScanCache(cacheDir, "helper-one").Lookup(bundle));
            var rescanned = new ScanCache(cacheDir, "helper-one");
            rescanned.Store(bundle, description);
            rescanned.Save();
            Assert.Equal(description, new ScanCache(cacheDir, "helper-one").Lookup(bundle));

            // The bridge keeps its own index beside it, and neither reaches
            // into the other, with or without a helper of the same name.
            string bridgeDir = Path.Combine(cacheDir, "bridge-ABC");
            byte[] bridged = System.Text.Encoding.UTF8.GetBytes("{\"file\":\"y\",\"plugins\":[]}");
            Assert.Null(new ScanCache(bridgeDir, "helper-one").Lookup(bundle));
            var bridge = new ScanCache(bridgeDir, "helper-one");
            bridge.Store(bundle, bridged);
            bridge.Save();
            Assert.Equal(bridged, new ScanCache(bridgeDir, "helper-one").Lookup(bundle));
            Assert.Null(new ScanCache(bridgeDir, "helper-two").Lookup(bundle));
            Assert.Equal(description, new ScanCache(cacheDir, "helper-one").Lookup(bundle));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LV2NeverLosesAPluginToACopyOfItselfInAnotherFormat()
    {
        static PluginInfo plugin(string kind, string name, int width, int parameters) => new(kind, kind + ":" + name, name, "",
            width, width, "", "", [.. Enumerable.Range(0, parameters).Select(i => new PluginParam(i.ToString(), "p" + i, 0, 1, 0, false, false, false, false, []))],
            [], [], []);

        // Under the budget, every copy is offered.
        var lv2 = new[] { plugin("lv2", "LSP Compressor Mono", 1, 30), plugin("lv2", "Dragonfly Hall Reverb", 2, 18) };
        var vst3 = new[] { plugin("vst3", "Compressor Mono", 1, 30), plugin("vst3", "Dragonfly Hall Reverb", 2, 18) };
        Assert.Equal(4, ClientCatalog.Merge(lv2, vst3).Count);

        // Over it, a copy goes before anything distinct, and LV2 stays whole.
        var heavy = Enumerable.Range(0, 400).Select(i => plugin("lv2", $"Plugin {i}", 1, Lv2Catalog.MaxControls)).ToList();
        var heavyVst3 = heavy.Select(p => plugin("vst3", p.Name, 1, Lv2Catalog.MaxControls)).ToList();
        List<PluginInfo> merged = ClientCatalog.Merge(heavy, [.. heavyVst3, plugin("vst3", "Something Else", 2, 3)]);
        List<PluginInfo> keptLv2 = Lv2Catalog.WithinBudget([.. heavy]);
        Assert.Equal(keptLv2.Count, merged.Count(p => p.Kind == "lv2"));
        Assert.Contains(merged, p => p.Name == "Something Else");      // the distinct one got in
        Assert.DoesNotContain(merged, p => p.Kind == "vst3" && p.Name == "Plugin 0");

    }

    [Fact]
    public void AVst3PluginNoOtherFormatOffersIsNotCrowdedOutByAnEarlierFormatsCopies()
    {
        static PluginInfo plugin(string kind, string name, int parameters) => new(kind, kind + ":" + name, name, "",
            1, 1, "", "", [.. Enumerable.Range(0, parameters).Select(i => new PluginParam(i.ToString(), "p" + i, 0, 1, 0, false, false, false, false, []))],
            [], [], []);

        // As on a desk with LSP installed in all three formats: LV2 takes
        // most of the message, the CLAP builds of the same plugins could
        // fill the rest, and a Windows plugin comes in the VST3 scan after
        // them, larger than any one CLAP copy.
        int each = Lv2Catalog.Footprint(plugin("lv2", "LSP Plugin 0", 300));
        int count = (int)(Lv2Catalog.CatalogBudgetBytes * 9L / 10 / each);
        List<PluginInfo> lv2 = [.. Enumerable.Range(0, count).Select(i => plugin("lv2", $"LSP Plugin {i}", 300))];
        List<PluginInfo> clap = [.. Enumerable.Range(0, count).Select(i => plugin("clap", $"Plugin {i}", 300))];
        PluginInfo nova = plugin("vst3", "TDR Nova", 400);
        long room = Lv2Catalog.CatalogBudgetBytes - lv2.Sum(p => (long)Lv2Catalog.Footprint(p));
        Assert.True(room > Lv2Catalog.Footprint(nova));
        Assert.True(clap.Sum(p => (long)Lv2Catalog.Footprint(p)) > room);

        List<PluginInfo> merged = ClientCatalog.Merge(lv2, clap, [nova]);

        Assert.Equal(lv2.Count, merged.Count(p => p.Kind == "lv2"));
        Assert.Contains(merged, p => p.Plugin == nova.Plugin);
        // The copies still take what room is left after it, in their format's place.
        Assert.Contains(merged, p => p.Kind == "clap");
        Assert.True(merged.FindIndex(p => p.Kind == "clap") < merged.FindIndex(p => p.Kind == "vst3"));
        Assert.True(merged.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);

        // A plugin two later formats offer and LV2 does not is distinct once:
        // its second copy waits with the other copies.
        PluginInfo clapOnly = plugin("clap", "Only Here", 3), vst3Copy = plugin("vst3", "Only Here", 3);
        List<PluginInfo> both = ClientCatalog.Merge(lv2, [.. clap, clapOnly], [nova, vst3Copy]);
        Assert.Contains(both, p => p.Plugin == clapOnly.Plugin);
        Assert.Contains(both, p => p.Plugin == nova.Plugin);
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => true;
        public bool HasMix(string id) => true;
        public bool IsMonitorFeed(string feed) => true;
        public bool IsMonitorOutput(string device) => true;
        public bool IsInsertKey(string key) => true;
        public int OverrideCount => 0;
    }

    [Fact]
    public void ALargeDescriptionIsReadWithoutHoldingATreeOfIt()
    {
        // What a module of two hundred plugins looks like: too large to hold
        // as a node tree in the daemon's heap, so the parser reads it as a
        // stream. The test is that everything survives the reading, unknown
        // fields with a structure of their own included.
        var json = new System.Text.StringBuilder("{\"file\":\"/usr/lib/vst3/big.vst3\",\"plugins\":[");
        for (int i = 0; i < 200; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"id\":\"" + i.ToString("D32") + "\",\"name\":\"Plugin " + i + "\",\"vendor\":\"Test\",");
            json.Append("\"features\":[\"Fx\",\"Reverb\"],\"unknown\":{\"nested\":[1,2,{\"deep\":true}]},");
            json.Append("\"audioIns\":2,\"audioOuts\":2,\"gui\":true,\"params\":[");
            for (int q = 0; q < 60; q++)
            {
                if (q > 0) json.Append(',');
                json.Append("{\"id\":" + q + ",\"name\":\"Param " + q + "\",\"module\":\"Group\",");
                json.Append("\"min\":-12.5,\"max\":12.5,\"default\":0,\"readonly\":false,\"stepped\":false,\"enum\":false}");
            }
            json.Append("]}");
        }
        json.Append("]}");

        IReadOnlyList<PluginInfo> found = Vst3Catalog.Parse(json.ToString());
        Assert.Equal(200, found.Count);
        Assert.Equal("Plugin 7", found[7].Name);
        Assert.Equal("/usr/lib/vst3/big.vst3", found[7].Path);
        Assert.Equal("Reverb", found[7].Category);
        Assert.Equal((2, 2), (found[7].AudioIns, found[7].AudioOuts));
        Assert.True(found[7].HasNativeUi);
        Assert.Equal(60, found[7].Params.Count);
        Assert.Equal("3", found[7].Params[3].Symbol);
        Assert.Equal(-12.5, found[7].Params[3].Min);
        Assert.Equal(0, found[7].Params[3].Default);
    }

    [Fact]
    public void WhatTheScannerLeavesOutTakesItsUsualValue()
    {
        const string sparse = """
            {"plugins":[
              {"name":"No id","audioIns":1,"audioOuts":1},
              {"id":"only.id"},
              {"id":"defaults","name":"Defaults","extra":{"a":[1,{"b":2}]},
               "params":[{"id":4,"name":"Level","min":-60},{"id":5,"stepped":true,"min":0,"max":1}]}
            ]}
            """;
        IReadOnlyList<PluginInfo> found = ClapCatalog.Parse(sparse);
        Assert.Equal(2, found.Count);                  // the one without an id is not a plugin
        Assert.Equal("only.id", found[0].Name);        // no name: its id stands in
        Assert.Equal("", found[0].Path);               // no file: an unnamed bundle
        Assert.Equal((0, 0), (found[0].AudioIns, found[0].AudioOuts));
        Assert.False(found[0].HasNativeUi);

        PluginInfo defaults = found[1];
        Assert.Equal(2, defaults.Params.Count);
        Assert.Equal(-60, defaults.Params[0].Min);
        Assert.Equal(1, defaults.Params[0].Max);    // no max: one
        Assert.Equal(-60, defaults.Params[0].Default);  // no default: the minimum
        Assert.True(defaults.Params[1].Toggled);        // stepped from zero to one
    }

    [Fact]
    public void WhatIsNotADescriptionDescribesNothing()
    {
        Assert.Empty(ClapCatalog.Parse("[1,2,3]"));
        Assert.Empty(ClapCatalog.Parse("\"a string\""));
        Assert.Empty(ClapCatalog.Parse("{\"plugins\":\"not an array\"}"));
        Assert.Empty(ClapCatalog.Parse("{}"));
    }
}
