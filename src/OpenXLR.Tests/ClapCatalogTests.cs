using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ClapCatalogTests
{
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

    [Fact]
    public void LV2NeverLosesAPluginToACopyOfItselfInAnotherFormat()
    {
        static PluginInfo plugin(string kind, string name, int width, int parameters) => new(kind, kind + ":" + name, name, "",
            width, width, "", "", [.. Enumerable.Range(0, parameters).Select(i => new PluginParam(i.ToString(), "p" + i, 0, 1, 0, false, false, false, false, []))],
            [], [], []);

        // Under the budget, every copy is offered.
        var lv2 = new[] { plugin("lv2", "LSP Compressor Mono", 1, 30), plugin("lv2", "Dragonfly Hall Reverb", 2, 18) };
        var vst3 = new[] { plugin("vst3", "Compressor Mono", 1, 30), plugin("vst3", "Dragonfly Hall Reverb", 2, 18) };
        Assert.Equal(4, PluginCatalog.Merge(lv2, vst3).Count);

        // Over it, a copy goes before anything distinct, and LV2 stays whole.
        var heavy = Enumerable.Range(0, 400).Select(i => plugin("lv2", $"Plugin {i}", 1, Lv2Catalog.MaxControls)).ToList();
        var heavyVst3 = heavy.Select(p => plugin("vst3", p.Name, 1, Lv2Catalog.MaxControls)).ToList();
        List<PluginInfo> merged = PluginCatalog.Merge(heavy, [.. heavyVst3, plugin("vst3", "Something Else", 2, 3)]);
        List<PluginInfo> keptLv2 = Lv2Catalog.WithinBudget([.. heavy]);
        Assert.Equal(keptLv2.Count, merged.Count(p => p.Kind == "lv2"));
        Assert.Contains(merged, p => p.Name == "Something Else");      // the distinct one got in
        Assert.DoesNotContain(merged, p => p.Kind == "vst3" && p.Name == "Plugin 0");

        Assert.True(PluginCatalog.SamePlugin(plugin("lv2", "LSP Compressor Mono", 1, 1), plugin("vst3", "Compressor Mono", 1, 1)));
        Assert.True(PluginCatalog.SamePlugin(plugin("lv2", "x42 - IR Convolver", 2, 1), plugin("vst3", "IR Convolver", 2, 1)));
        Assert.False(PluginCatalog.SamePlugin(plugin("lv2", "Compressor Mono", 1, 1), plugin("vst3", "Compressor Stereo", 2, 1)));
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
