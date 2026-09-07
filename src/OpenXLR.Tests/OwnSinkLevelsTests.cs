using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class OwnSinkLevelsTests
{
    private static byte[] Dump(params string[] nodes) => Encoding.UTF8.GetBytes("[" + string.Join(",", nodes) + "]");

    private static string Node(string name, string mediaClass, string volumes, bool mute)
        => "{\"id\": 1, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"node.name\": \"" + name +
           "\", \"media.class\": \"" + mediaClass + "\"}, \"params\": {\"Props\": [{\"volume\": 1.0, \"channelVolumes\": [" +
           volumes + "], \"mute\": " + (mute ? "true" : "false") + "}]}}}";

    [Fact]
    public void OnlyOurSinksAreListedWithTheirLowestChannelVolume()
    {
        byte[] json = Dump(
            Node("OpenXLR_ch_xlr1", "Audio/Sink", "0.166378, 0.166378", false),
            Node("OpenXLR_mix_stream", "Audio/Sink", "1.0, 1.0", true),
            Node("OpenXLR_ch_game", "Audio/Sink", "1.0, 0.5", false),
            Node("OpenXLR_ch_xlr1.monitor", "Audio/Source", "0.2, 0.2", false),
            Node("alsa_output.usb-Elgato_Wave_XLR_Pro-00.multichannel-output", "Audio/Sink", "1.0", false));
        var levels = PipeWireAdapter.OwnSinkLevels(json);
        Assert.Equal(["OpenXLR_ch_xlr1", "OpenXLR_mix_stream", "OpenXLR_ch_game"], levels.Select(l => l.Name));
        Assert.Equal(0.166378, levels[0].Volume, 6);
        Assert.False(levels[0].Muted);
        Assert.True(levels[1].Muted);
        Assert.Equal(0.5, levels[2].Volume);
    }

    [Fact]
    public void ABrokenDumpYieldsNothing()
        => Assert.Empty(PipeWireAdapter.OwnSinkLevels(Encoding.UTF8.GetBytes("[{\"type\": \"PipeWire:Interface:Node\"")));
}
