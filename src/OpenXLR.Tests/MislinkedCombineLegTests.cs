using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Which combine legs the sweep reloads their channel for, read from a
/// registry dump: a leg no link leaves, and a leg linked from one channel
/// position into another.
/// </summary>
public sealed class MislinkedCombineLegTests
{
    private static byte[] Dump(params string[] objects) => Encoding.UTF8.GetBytes("[" + string.Join(",", objects) + "]");

    private static string Node(uint id, string name, string mediaClass, int serial)
        => "{\"id\": " + id + ", \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"node.name\": \"" + name
           + "\", \"media.class\": \"" + mediaClass + "\", \"object.serial\": " + serial + "}}}";

    private static string Port(uint id, uint node, string direction, string? channel)
        => "{\"id\": " + id + ", \"type\": \"PipeWire:Interface:Port\", \"info\": {\"direction\": \"" + direction
           + "\", \"props\": {\"node.id\": " + node + (channel is null ? "" : ", \"audio.channel\": \"" + channel + "\"") + "}}}";

    private static string Link(uint id, uint fromNode, uint fromPort, uint toNode, uint toPort)
        => "{\"id\": " + id + ", \"type\": \"PipeWire:Interface:Link\", \"info\": {\"output-node-id\": " + fromNode
           + ", \"output-port-id\": " + fromPort + ", \"input-node-id\": " + toNode + ", \"input-port-id\": " + toPort + ", \"state\": \"active\"}}";

    // A stereo leg (node 10, ports 11 and 12) and a stereo mix sink (node 20, ports 21 and 22).
    private static readonly string[] Stereo =
    [
        Node(10, "output.OpenXLR_ch_mic_OpenXLR_mix_auxout", "Stream/Output/Audio", 410),
        Port(11, 10, "output", "FL"),
        Port(12, 10, "output", "FR"),
        Node(20, "OpenXLR_mix_auxout", "Audio/Sink", 420),
        Port(21, 20, "input", "FL"),
        Port(22, 20, "input", "FR"),
    ];

    [Fact]
    public void ALegLinkedSideToSideIsHealthy()
        => Assert.Empty(PipeWireAdapter.MislinkedCombineLegs(Dump([.. Stereo, Link(30, 10, 11, 20, 21), Link(31, 10, 12, 20, 22)])));

    [Fact]
    public void ALegLinkedCrossedIsMislinked()
        => Assert.Equal([410], PipeWireAdapter.MislinkedCombineLegs(Dump([.. Stereo, Link(30, 10, 11, 20, 22), Link(31, 10, 12, 20, 21)])));

    [Fact]
    public void ALegWithOneSideCrossedIsMislinked()
        => Assert.Equal([410], PipeWireAdapter.MislinkedCombineLegs(Dump([.. Stereo, Link(30, 10, 11, 20, 22)])));

    [Fact]
    public void ALegNoLinkLeavesIsMislinked()
        => Assert.Equal([410], PipeWireAdapter.MislinkedCombineLegs(Dump(Stereo)));

    [Fact]
    public void OnlyTheCrossedLegOfTwoIsMislinked()
    {
        byte[] json = Dump([.. Stereo,
            Node(40, "output.OpenXLR_ch_music_OpenXLR_mix_auxout", "Stream/Output/Audio", 440),
            Port(41, 40, "output", "FL"),
            Port(42, 40, "output", "FR"),
            Link(30, 10, 11, 20, 21), Link(31, 10, 12, 20, 22),
            Link(32, 40, 41, 20, 22), Link(33, 40, 42, 20, 21)]);
        Assert.Equal([440], PipeWireAdapter.MislinkedCombineLegs(json));
    }

    [Fact]
    public void AStereoLegIntoAMonoSinkIsHealthy()
    {
        byte[] json = Dump(
            Node(10, "output.OpenXLR_ch_game_OpenXLR_mix_chat", "Stream/Output/Audio", 410),
            Port(11, 10, "output", "FL"),
            Port(12, 10, "output", "FR"),
            Node(20, "OpenXLR_mix_chat", "Audio/Sink", 420),
            Port(21, 20, "input", "MONO"),
            Link(30, 10, 11, 20, 21), Link(31, 10, 12, 20, 21));
        Assert.Empty(PipeWireAdapter.MislinkedCombineLegs(json));
    }

    [Fact]
    public void AMonoLegIntoAStereoSinkIsHealthy()
    {
        byte[] json = Dump(
            Node(10, "output.OpenXLR_ch_xlr1_OpenXLR_mix_stream", "Stream/Output/Audio", 410),
            Port(11, 10, "output", "MONO"),
            Node(20, "OpenXLR_mix_stream", "Audio/Sink", 420),
            Port(21, 20, "input", "FL"),
            Port(22, 20, "input", "FR"),
            Link(30, 10, 11, 20, 21), Link(31, 10, 11, 20, 22));
        Assert.Empty(PipeWireAdapter.MislinkedCombineLegs(json));
    }

    [Fact]
    public void PortsWithoutAPlaceOfTheirOwnAreLinkedInOrderAndHealthy()
    {
        // AUX ports and ports that name no channel are paired by order.
        byte[] json = Dump(
            Node(10, "output.OpenXLR_ch_line_OpenXLR_mix_stream", "Stream/Output/Audio", 410),
            Port(11, 10, "output", "AUX0"),
            Port(12, 10, "output", null),
            Node(20, "OpenXLR_mix_stream", "Audio/Sink", 420),
            Port(21, 20, "input", "FR"),
            Port(22, 20, "input", "FL"),
            Link(30, 10, 11, 20, 21), Link(31, 10, 12, 20, 22));
        Assert.Empty(PipeWireAdapter.MislinkedCombineLegs(json));
    }

    [Fact]
    public void OtherStreamsLinkedCrossedAreNotLegs()
    {
        byte[] json = Dump(
            Node(10, "Firefox", "Stream/Output/Audio", 410),
            Port(11, 10, "output", "FL"),
            Port(12, 10, "output", "FR"),
            Node(20, "OpenXLR_ch_music", "Audio/Sink", 420),
            Port(21, 20, "input", "FL"),
            Port(22, 20, "input", "FR"),
            Link(30, 10, 11, 20, 22), Link(31, 10, 12, 20, 21));
        Assert.Empty(PipeWireAdapter.MislinkedCombineLegs(json));
    }

    [Theory]
    [InlineData("FL", "FL", true)]
    [InlineData("FL", "FR", false)]
    [InlineData("FR", "FL", false)]
    [InlineData("FL", "FC", false)]
    [InlineData("SL", "RL", true)]
    [InlineData("RR", "SR", true)]
    [InlineData("SL", "RR", false)]
    [InlineData("FL", "MONO", true)]
    [InlineData("MONO", "FR", true)]
    [InlineData("AUX1", "FL", true)]
    [InlineData("FR", "UNK", true)]
    public void PositionsPairAsWirePlumberPairsThem(string output, string input, bool same)
        => Assert.Equal(same, PipeWireAdapter.SamePosition(output, input));
}
