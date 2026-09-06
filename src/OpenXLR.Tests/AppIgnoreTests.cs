using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class AppIgnoreTests
{
    [Fact]
    public void IgnoreOverrideWinsOverTheNameRules()
    {
        var matcher = new StreamMatcher();
        var discord = new AudioStream(7, "Discord", "Discord", "playback") { Serial = 70 };
        Assert.Equal("voicechat", matcher.Match(discord));

        matcher.SetOverride(discord.Identity, StreamMatcher.Ignore);
        Assert.Equal(StreamMatcher.Ignore, matcher.Match(discord));

        matcher.RemoveOverride(discord.Identity);
        Assert.Equal("voicechat", matcher.Match(discord));
    }

    [Fact]
    public void StreamSinkNameResolvesThroughTheSinkId()
    {
        const string inputs = "70\t5\tprotocol-native.c\tfloat32le 2ch 48000Hz\n";
        const string sinks = "5\tOpenXLR_ch_voicechat\tPipeWire\tfloat32le 2ch 48000Hz\tRUNNING\n" +
                             "9\talsa_output.usb-Headset-00.analog-stereo\tPipeWire\ts16le 2ch 48000Hz\tIDLE\n";
        Assert.Equal("OpenXLR_ch_voicechat", PipeWireAdapter.StreamSinkName(inputs, sinks, 70));
        Assert.Null(PipeWireAdapter.StreamSinkName(inputs, sinks, 71));
        Assert.Null(PipeWireAdapter.StreamSinkName("70\t4294967295\tx\n", sinks, 70));
    }
}
