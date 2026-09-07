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

    [Theory]
    [InlineData(new[] { "audio-effect", "reverb", "stereo" }, "Reverb")]
    [InlineData(new[] { "audio-effect", "multi-effects" }, "Multi Effects")]
    [InlineData(new[] { "audio-effect", "stereo" }, "Effect")]
    [InlineData(new[] { "instrument", "synthesizer" }, "Synthesizer")]
    [InlineData(new string[0], "")]
    public void TheCategoryComesFromThePluginsOwnFeatureList(string[] features, string expected)
        => Assert.Equal(expected, ClapCatalog.Category(features));

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
        ];
        Assert.Equal(3, PluginPickerWindow.Matching(choices, "", lv2: false, clap: false).Count());
        Assert.Equal(["vendor.hall"], PluginPickerWindow.Matching(choices, "", lv2: false, clap: true).Select(c => c.Uri));
        Assert.Equal(2, PluginPickerWindow.Matching(choices, "", lv2: true, clap: false).Count());
        Assert.Equal(3, PluginPickerWindow.Matching(choices, "", lv2: true, clap: true).Count());
        // The search and the buttons combine, and the search matches the format too.
        Assert.Equal(["urn:a"], PluginPickerWindow.Matching(choices, "hall", lv2: true, clap: false).Select(c => c.Uri));
        Assert.Equal(["vendor.hall"], PluginPickerWindow.Matching(choices, "clap", lv2: false, clap: false).Select(c => c.Uri));
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
}
