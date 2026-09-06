using System.Text.Json;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class ProfileCompatibilityTests
{
    [Fact]
    public void OldSceneWithoutMonitorOutputsPreservesLegacyMeaning()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("{}")!;
        Assert.Null(scene.MonitorOutputs);
    }

    [Fact]
    public void ExplicitEmptyMonitorOutputsMeansDisconnectAll()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>(
            """{"MonitorOutputs":[]}""")!;
        Assert.NotNull(scene.MonitorOutputs);
        Assert.Empty(scene.MonitorOutputs);
    }

    [Fact]
    public void OldSceneWithoutMonitorFeedsKeepsTheCurrentFeeds()
    {
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>("""{"MonitorOutputs":["a"]}""")!;
        Assert.Null(scene.MonitorFeeds);
    }

    [Fact]
    public void SceneFeedsRoundTrip()
    {
        var scene = new MixerScene { MonitorFeeds = new() { ["alsa_output.chat"] = "monitor2" } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene))!;
        Assert.Equal("monitor2", back.MonitorFeeds!["alsa_output.chat"]);
    }
}
