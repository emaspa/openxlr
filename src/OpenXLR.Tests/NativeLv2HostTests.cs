using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class NativeLv2HostTests
{
    [Theory]
    [InlineData("http://lv2plug.in/ns/ext/urid#map", true)]
    [InlineData("http://lv2plug.in/ns/ext/urid#unmap", true)]
    [InlineData("http://lv2plug.in/ns/ext/worker#schedule", true)]
    [InlineData("http://lv2plug.in/ns/ext/options#options", true)]
    [InlineData("http://lv2plug.in/ns/ext/buf-size#boundedBlockLength", true)]
    [InlineData("http://lv2plug.in/ns/ext/state#loadDefaultState", false)]
    [InlineData("urn:unknown", false)]
    public void NativeHostDoesNotClaimFeaturesItDoesNotImplement(string feature, bool supported)
        => Assert.Equal(supported, NativePluginHost.SupportsFeatures([feature]));

    [Fact]
    public void AMissingHelperIsReportedAsSuchAndNotAsAPluginFault()
    {
        // What a user hit: with the helper gone, every plugin was reported as
        // unable to host an editor, so the reason looked like the plugin's.
        var hostable = new PluginInfo("lv2", "urn:test", "Test", "", 1, 1, "in", "out",
            [], [], ["in"], ["out"]) { HasNativeUi = true };
        Assert.True(hostable.NativeEditorSupported);
        Assert.Equal(NativePluginHost.HostInstalled, hostable.NativeEditorAvailable);

        var noEditor = hostable with { HasNativeUi = false };
        Assert.False(noEditor.NativeEditorSupported);
        Assert.False(noEditor.NativeEditorAvailable);

        var command = new Command
        {
            Cmd = "setInserts",
            Channel = "xlr1",
            Inserts = [new InsertDefinition { Id = "a", Kind = "lv2", Plugin = "urn:test", NativeHost = true }],
        };
        string? refusal = CommandValidation.Check(command, new Layout(), _ => noEditor);
        Assert.Contains("no editor the native host can open", refusal);
    }

    private sealed class Layout : OpenXLR.Core.Mixing.ILayoutInfo
    {
        public bool HasChannel(string id) => true;
        public bool HasMix(string id) => true;
        public bool IsMonitorFeed(string feed) => true;
        public bool IsMonitorOutput(string device) => true;
        public bool IsInsertKey(string key) => true;
        public int OverrideCount => 0;
    }

    [Fact]
    public async Task TheRowCarriesTheHostSwitchAndSaysWhatTheCogWillOpen()
    {
        await using var client = new DaemonClient();
        var owner = new InsertsViewModel(client, "xlr1");
        owner.PluginChoices.Add(new PluginChoice("urn:test", "Test", "", new JsonArray(),
            NativeEditorAvailable: true, NativeEditorSupported: true));
        var insert = new InsertViewModel(owner, "comp", "urn:test", "Test");
        JsonNode definition = JsonNode.Parse(
            "{\"id\":\"comp\",\"kind\":\"lv2\",\"plugin\":\"urn:test\",\"bypass\":false,\"params\":{}}")!;

        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: false);
        Assert.True(insert.CanChooseNativeHost);      // the switch belongs on the row
        Assert.True(insert.CanTurnNativeHostOn);      // and is usable: the helper is here
        Assert.Contains("controls", insert.ControlsHint);

        definition["nativeHost"] = true;
        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.True(insert.NativeEditorAvailable);
        Assert.Contains("own editor", insert.ControlsHint);   // the cog opens that instead

        // Bypassed, there is no process to show, so the cog goes back to ours.
        definition["bypass"] = true;
        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable);
        Assert.Contains("controls", insert.ControlsHint);
        Assert.True(insert.CanTurnNativeHostOn);      // and it can still be switched back
    }

    [Fact]
    public void TheHelperIsPlumbing()
    {
        // Its PipeWire client would otherwise be offered as an application to
        // route, which is what a user saw after the first release with it.
        Assert.True(PipeWireAdapter.IsPlumbingIdentity("openxlr-lv2-host"));
        Assert.True(PipeWireAdapter.IsPlumbingIdentity("OpenXLR.Daemon"));
        Assert.False(PipeWireAdapter.IsPlumbingIdentity("Spotify"));
    }

    [Fact]
    public void EveryFeatureTheHelperAcceptsIsOneItImplements()
    {
        // The C source is the other half of this contract: it refuses to load
        // a plugin whose required features it does not provide, so the two
        // lists have to name the same extensions.
        string source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "lv2-host.c"));
        foreach (string macro in new[] { "LV2_WORKER__schedule", "LV2_OPTIONS__options", "LV2_BUF_SIZE__boundedBlockLength" })
            Assert.Contains(macro, source);
        Assert.True(NativePluginHost.SupportsFeatures(
            ["http://lv2plug.in/ns/ext/worker#schedule", "http://lv2plug.in/ns/ext/options#options"]));
    }

    [Theory]
    [InlineData("http://lv2plug.in/ns/ext/urid#map", true)]
    [InlineData("http://lv2plug.in/ns/ext/instance-access", true)]
    [InlineData("http://lv2plug.in/ns/extensions/ui#parent", true)]
    [InlineData("http://lv2plug.in/ns/extensions/ui#resize", true)]
    [InlineData("http://lv2plug.in/ns/extensions/ui#idleInterface", true)]
    [InlineData("urn:unsupported-ui-feature", false)]
    public void NativeEditorOnlyClaimsUiFeaturesTheHelperProvides(string feature, bool supported)
        => Assert.Equal(supported, NativePluginHost.SupportsUiFeatures([feature]));

    [Fact]
    public void ForcedGraphRateTakesPrecedenceOverDefaultRate()
        => Assert.Equal(96000, PipeWireAdapter.ParseGraphSampleRate(
            "key:'clock.rate' value:'48000'\nkey:'clock.force-rate' value:'96000'"));

    [Fact]
    public void DisabledForcedRateFallsBackToGraphRate()
        => Assert.Equal(48000, PipeWireAdapter.ParseGraphSampleRate(
            "key:'clock.force-rate' value:'0'\nkey:'clock.rate' value:'48000'"));

    [Theory]
    [InlineData("")]
    [InlineData("key:'clock.rate' value:'4000'")]
    public void InvalidRateIsRejectedBeforeOpeningPlugin(string metadata)
        => Assert.Throws<InvalidOperationException>(() => PipeWireAdapter.ParseGraphSampleRate(metadata));

    [Fact]
    public void EditorCommandRequiresARealInsertTarget()
    {
        using var mixer = new Mixer();
        var command = new Command { Cmd = "showInsertUi", Channel = "not-a-channel", InsertId = "eq" };
        Assert.Contains("showInsertUi", CommandValidation.Check(command, mixer, _ => null));
        Assert.Throws<InvalidOperationException>(() => mixer.ShowInsertUi("xlr1", "missing"));
    }

    [Fact]
    public void LiveOutputControlsHaveAnAdditiveStatusField()
    {
        var status = new InsertStatus(new InsertDefinition { Id = "comp", Kind = "lv2", Plugin = "urn:test" },
            null, new Dictionary<string, double> { ["gain_reduction"] = 3 }, NativeHostRunning: true);
        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"gain_reduction\":3", json);
        Assert.Contains("\"nativeHostRunning\":true", json);
    }

    [Fact]
    public async Task EditorButtonRequiresAHealthyLiveInsert()
    {
        await using var client = new DaemonClient();
        var owner = new InsertsViewModel(client, "xlr1");
        owner.PluginChoices.Add(new PluginChoice("urn:test", "Test", "", new JsonArray(), NativeEditorAvailable: true));
        var insert = new InsertViewModel(owner, "comp", "urn:test", "Test");
        JsonNode definition = JsonNode.Parse("""{"id":"comp","kind":"lv2","plugin":"urn:test","bypass":false,"params":{}}""")!;

        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: false);
        Assert.True(insert.NativeEditorSupported);
        Assert.False(insert.NativeEditorAvailable);

        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable); // A live-process flag does not opt an old insert in.
        definition["nativeHost"] = true;
        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.True(insert.NativeEditorAvailable);

        insert.ApplyFromDaemon(definition, error: "chain build failed", nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable);

        definition["bypass"] = true;
        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable);
    }

    [Fact]
    public void OldInsertJsonKeepsFilterChainAndExplicitChoiceRoundTrips()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var old = JsonSerializer.Deserialize<InsertDefinition>("""{"id":"x","kind":"lv2","plugin":"urn:test"}""", json)!;
        Assert.False(old.NativeHost);
        var selected = old with { NativeHost = true };
        Assert.True(JsonSerializer.Deserialize<InsertDefinition>(JsonSerializer.Serialize(selected, json), json)!.NativeHost);
    }

    [Fact]
    public void SettingsPreservePerInsertHostChoice()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-host-choice-").FullName;
        try
        {
            var legacy = new InsertDefinition { Id = "old", Kind = "lv2", Plugin = "urn:test" };
            var settings = new MixerSettings { Inserts = new() { ["mix:stream"] = [legacy, legacy with { Id = "new", NativeHost = true }] } };
            string path = Path.Combine(dir, "mixer.json");
            Assert.Null(settings.Save(path));
            var loaded = MixerSettings.Load(path)!.Inserts["mix:stream"];
            Assert.False(loaded[0].NativeHost);
            Assert.True(loaded[1].NativeHost);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UiPreservesExplicitChoiceAndCanDisableItWhenHelperIsUnavailable()
    {
        await using var client = new DaemonClient();
        var owner = new InsertsViewModel(client, "xlr1");
        owner.Apply(JsonNode.Parse("""[{"insert":{"id":"x","kind":"lv2","plugin":"urn:test","nativeHost":true},"error":"helper unavailable"}]"""));
        var insert = Assert.Single(owner.Items);
        Assert.True(insert.NativeHost);
        Assert.True(insert.CanChooseNativeHost);
        Assert.False(insert.NativeEditorAvailable);
        Assert.Contains("\"nativeHost\":true", JsonSerializer.Serialize(insert.ToPayload()));
        owner.Apply(JsonNode.Parse("""[{"insert":{"id":"x","kind":"lv2","plugin":"urn:test"}}]"""));
        Assert.False(insert.NativeHost);
        Assert.Contains("\"nativeHost\":false", JsonSerializer.Serialize(insert.ToPayload()));
    }
}
