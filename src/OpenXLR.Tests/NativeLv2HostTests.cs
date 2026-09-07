using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

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
            null, new Dictionary<string, double> { ["gain_reduction"] = 3 });
        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"gain_reduction\":3", json);
    }
}
