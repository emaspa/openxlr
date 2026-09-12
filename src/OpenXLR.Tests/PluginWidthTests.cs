using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// A plugin's place in a chain follows the widths it takes, not its default
/// width alone: a VST3 plugin that reports stereo and accepts mono when
/// asked belongs on the mono inputs, and one that was asked and refused
/// belongs nowhere. Where nothing asked, the port counts decide as before.
/// </summary>
public sealed class PluginWidthTests
{
    private const string Scan = """
        {"file":"/vst3/Fx.vst3","plugins":[
          {"id":"AAAA0000000000000000000000000001","name":"Either","vendor":"","features":["Fx"],
           "audioIns":2,"audioOuts":2,"widths":[1,2],"gui":true,"params":[]},
          {"id":"AAAA0000000000000000000000000002","name":"Stereo only","vendor":"","features":["Fx"],
           "audioIns":2,"audioOuts":2,"widths":[2],"gui":true,"params":[]},
          {"id":"AAAA0000000000000000000000000003","name":"Refuses","vendor":"","features":["Fx"],
           "audioIns":6,"audioOuts":6,"widths":[],"gui":true,"params":[]},
          {"id":"AAAA0000000000000000000000000004","name":"Old description","vendor":"","features":["Fx"],
           "audioIns":2,"audioOuts":2,"gui":true,"params":[]},
          {"id":"AAAA0000000000000000000000000005","name":"Odd widths","vendor":"","features":["Fx"],
           "audioIns":1,"audioOuts":1,"widths":[1,"two",{"n":2},[2],1,0,-3],"gui":true,"params":[]}
        ]}
        """;

    [Fact]
    public void ScannerWidthsAreReadAndTheirAbsenceIsKept()
    {
        IReadOnlyList<PluginInfo> found = Vst3Catalog.Parse(Scan);
        Assert.Equal(5, found.Count);
        Assert.Equal([1, 2], found[0].Widths);
        Assert.Equal([2], found[1].Widths);
        Assert.NotNull(found[2].Widths);
        Assert.Empty(found[2].Widths!);
        Assert.Null(found[3].Widths);
        Assert.Equal([1], found[4].Widths);   // malformed entries and duplicates are skipped, not fatal
        Assert.Equal((2, 2), (found[0].AudioIns, found[0].AudioOuts));   // the default width stays what it was
    }

    [Fact]
    public void FitsFollowsTheAnswerWhenThereIsOneAndThePortsOtherwise()
    {
        IReadOnlyList<PluginInfo> found = Vst3Catalog.Parse(Scan);
        Assert.True(found[0].Fits(1));
        Assert.True(found[0].Fits(2));
        Assert.False(found[1].Fits(1));
        Assert.True(found[1].Fits(2));
        // Asked and refused: the six-channel default does not make it a stereo plugin.
        Assert.False(found[2].Fits(1));
        Assert.False(found[2].Fits(2));
        // Never asked: two ports each way is a stereo plugin and not a mono one.
        Assert.False(found[3].Fits(1));
        Assert.True(found[3].Fits(2));

        PluginInfo lv2 = new("lv2", "urn:test:mono", "Mono", "", 1, 1, "in", "out", [], [], ["in"], ["out"]);
        Assert.True(lv2.Fits(1));
        Assert.False(lv2.Fits(2));
        PluginInfo wide = lv2 with { AudioIns = 4, AudioOuts = 4 };
        Assert.False(wide.Fits(1));
        Assert.True(wide.Fits(2));
        PluginInfo analyser = lv2 with { AudioOuts = 0 };
        Assert.False(analyser.Fits(1));
    }

    [Fact]
    public void ThePickerReadsWidthsFromTheCatalogueAndFallsBackToPorts()
    {
        JsonNode either = JsonNode.Parse("""{"audioIns":2,"audioOuts":2,"widths":[1,2]}""")!;
        JsonNode stereo = JsonNode.Parse("""{"audioIns":2,"audioOuts":2,"widths":[2]}""")!;
        JsonNode refused = JsonNode.Parse("""{"audioIns":2,"audioOuts":2,"widths":[]}""")!;
        JsonNode oldStereo = JsonNode.Parse("""{"audioIns":2,"audioOuts":2}""")!;
        JsonNode oldMono = JsonNode.Parse("""{"audioIns":1,"audioOuts":1}""")!;
        JsonNode nullWidths = JsonNode.Parse("""{"audioIns":1,"audioOuts":1,"widths":null}""")!;
        Assert.True(InsertsViewModel.Fits(either, 1));
        Assert.True(InsertsViewModel.Fits(either, 2));
        Assert.False(InsertsViewModel.Fits(stereo, 1));
        Assert.True(InsertsViewModel.Fits(stereo, 2));
        Assert.False(InsertsViewModel.Fits(refused, 1));
        Assert.False(InsertsViewModel.Fits(refused, 2));
        Assert.False(InsertsViewModel.Fits(oldStereo, 1));
        Assert.True(InsertsViewModel.Fits(oldStereo, 2));
        Assert.True(InsertsViewModel.Fits(oldMono, 1));
        Assert.False(InsertsViewModel.Fits(oldMono, 2));
        Assert.True(InsertsViewModel.Fits(nullWidths, 1));
    }

    [Fact]
    public void TheCatalogueSentToClientsCarriesWidthsOnlyWhereTheyWereAsked()
    {
        IReadOnlyList<PluginInfo> found = Vst3Catalog.Parse(Scan);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        JsonNode sent = JsonNode.Parse(JsonSerializer.Serialize(found, options))!;
        Assert.Equal("[1,2]", sent[0]!["widths"]!.ToJsonString());
        Assert.Equal("[]", sent[2]!["widths"]!.ToJsonString());
        Assert.Null(sent[3]!["widths"]);
        Assert.True(InsertsViewModel.Fits(sent[0]!, 1));
        Assert.False(InsertsViewModel.Fits(sent[2]!, 2));
    }

    // --- the daemon's refusal ---

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "mix:monitor";
        // XLR 1 already holds the stereo-only VST3 plugin under "kept".
        public InsertDefinition? InsertInChain(string key, string id) => key is "xlr1" && id is "kept"
            ? new InsertDefinition { Id = "kept", Kind = "vst3", Plugin = "stereo" } : null;
        public int OverrideCount => 0;
    }

    private static readonly PluginInfo Either = new("vst3", "either", "Either", "Fx", 2, 2, "", "", [], [], [], []) { Widths = [1, 2] };
    private static readonly PluginInfo StereoOnly = Either with { Plugin = "stereo", Name = "Stereo only", Widths = [2] };
    private static readonly PluginInfo StereoOnlyClap = StereoOnly with { Kind = "clap" };
    private static readonly PluginInfo Refuses = Either with { Plugin = "refuses", Name = "Refuses", Widths = [] };
    private static readonly PluginInfo OldStereo = Either with { Plugin = "old", Name = "Old description", Widths = null };
    private static readonly PluginInfo MonoLv2 = new("lv2", "urn:test:mono", "Mono", "", 1, 1, "in", "out", [], [], ["in"], ["out"]);

    private static PluginInfo? Find(InsertDefinition insert) => (insert.Kind, insert.Plugin) switch
    {
        ("vst3", "either") => Either, ("vst3", "stereo") => StereoOnly, ("clap", "stereo") => StereoOnlyClap,
        ("vst3", "refuses") => Refuses, ("vst3", "old") => OldStereo, ("lv2", "urn:test:mono") => MonoLv2, _ => null,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string? Check(string channel, string id, string kind, string plugin)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(
            $$"""{"cmd":"setInserts","channel":"{{channel}}","inserts":[{"id":"{{id}}","kind":"{{kind}}","plugin":"{{plugin}}"}]}""", Json)!,
            new Layout(), Find, nativeHostInstalled: true);

    [Fact]
    public void WidthSupportDoesNotBypassTheNativeHostRequirement()
    {
        var command = new Command
        {
            Cmd = "setInserts", Channel = "xlr1",
            Inserts = [new() { Id = "a", Kind = "vst3", Plugin = "either" }],
        };
        Assert.Contains("native plugin host is not installed",
            CommandValidation.Check(command, new Layout(), Find, nativeHostInstalled: false));
        Assert.Null(CommandValidation.Check(command, new Layout(), Find, nativeHostInstalled: true));
    }

    [Theory]
    [InlineData("xlr1", "a", "vst3", "either", null)]
    [InlineData("xlr2", "a", "vst3", "either", null)]
    [InlineData("mix:monitor", "a", "vst3", "either", null)]
    [InlineData("xlr1", "a", "vst3", "stereo", "no mono layout")]
    [InlineData("mix:monitor", "a", "vst3", "stereo", null)]
    [InlineData("xlr1", "a", "vst3", "refuses", "no mono layout")]
    [InlineData("mix:monitor", "a", "vst3", "refuses", "no stereo layout")]
    [InlineData("xlr1", "a", "vst3", "old", "no mono layout")]
    [InlineData("mix:monitor", "a", "vst3", "old", null)]
    [InlineData("xlr1", "a", "lv2", "urn:test:mono", null)]
    [InlineData("mix:monitor", "a", "lv2", "urn:test:mono", "no stereo layout")]
    public void AnInsertIsRefusedAtAWidthItsPluginCannotRun(string channel, string id, string kind, string plugin, string? expected)
    {
        string? result = Check(channel, id, kind, plugin);
        if (expected is null) Assert.Null(result);
        else Assert.Contains(expected, result);
    }

    [Fact]
    public void AnInsertAlreadyInTheChainIsNotTrappedByTheRule()
    {
        // The chain holds "kept" with a plugin that does not fit; sending the
        // chain back (a reorder, a removal of something else) still passes.
        Assert.Null(Check("xlr1", "kept", "vst3", "stereo"));
        // The same id on another chain is a new insert there.
        Assert.Contains("no stereo layout", Check("mix:monitor", "kept", "lv2", "urn:test:mono"));
    }

    [Fact]
    public void ReplacingThePluginUnderAKeptIdIsAnAddition()
    {
        // The id survives but the plugin does not: a different plugin, or the
        // same plugin id in another format, is held to the chain's width.
        Assert.Contains("no mono layout", Check("xlr1", "kept", "vst3", "refuses"));
        Assert.Contains("no mono layout", Check("xlr1", "kept", "clap", "stereo"));
        // A replacement that fits is an ordinary addition.
        Assert.Null(Check("xlr1", "kept", "vst3", "either"));
    }

    [Fact]
    public void TheDefaultLayoutRulesFollowTheKey()
    {
        ILayoutInfo layout = new Layout();
        Assert.Equal(1, layout.InsertChannels("xlr1"));
        Assert.Equal(1, layout.InsertChannels("xlr2"));
        Assert.Equal(2, layout.InsertChannels("mix:monitor"));
        Assert.Equal(2, layout.InsertChannels("mix:stream"));
    }
}
