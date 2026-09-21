using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ChannelInsertTests
{
    [Theory]
    [InlineData("xlr1", 1)]
    [InlineData("xlr2", 1)]
    [InlineData("aux", 2)]
    [InlineData("system", 2)]
    [InlineData("browser", 2)]
    [InlineData("mix:monitor", 2)]
    public void EveryChannelHasAnExplicitChainWidth(string key, int width)
    {
        using var mixer = new Mixer();
        Assert.True(mixer.IsInsertKey(key));
        Assert.Equal(width, mixer.InsertChannels(key));
        Assert.Null(CommandValidation.Check(new Command { Cmd = "setInserts", Channel = key, Inserts = [] }, mixer, _ => null));
        Assert.False(mixer.IsInsertKey("nonexistent"));
    }

    [Fact]
    public async Task ChannelInsertsUseTheSharedChainViewAndFollowRenames()
    {
        await using var client = new DaemonClient();
        var shared = new InsertsViewModel(client, "xlr1", 1, "XLR 1");
        var mic = new ChannelViewModel(client, "xlr1", "XLR 1", ["monitor"], shared);
        Assert.Same(shared, mic.Inserts);
        var software = new ChannelViewModel(client, "browser", "Browser", ["monitor"]);
        software.Name = "Work browser";
        Assert.Equal("Work browser", software.Inserts.Title);
        Assert.Contains("stereo", software.Inserts.PickerHint);
        Assert.Contains("channel reaches the mixes", software.Inserts.ChainHint);
        software.Inserts.Apply(JsonNode.Parse("""[{"insert":{"id":"one","kind":"lv2","plugin":"missing","label":"Effect"},"error":"plugin not installed"}]"""));
        Assert.Single(software.Inserts.Items);
    }
}
