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
    [InlineData("http://lv2plug.in/ns/ext/worker#schedule", false)]
    [InlineData("http://lv2plug.in/ns/ext/options#options", false)]
    [InlineData("urn:unknown", false)]
    public void NativeHostDoesNotClaimFeaturesItDoesNotImplement(string feature, bool supported)
        => Assert.Equal(supported, NativePluginHost.SupportsFeatures([feature]));

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
        Assert.True(insert.NativeEditorAvailable);

        insert.ApplyFromDaemon(definition, error: "chain build failed", nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable);

        definition["bypass"] = true;
        insert.ApplyFromDaemon(definition, error: null, nativeHostRunning: true);
        Assert.False(insert.NativeEditorAvailable);
    }
}
