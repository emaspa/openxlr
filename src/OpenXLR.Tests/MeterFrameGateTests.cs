using System.Text;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class MeterFrameGateTests
{
    private static byte[] Frame(string levels) => Encoding.UTF8.GetBytes(levels);

    [Fact]
    public void AnUnchangedFrameWaitsForTheKeepalive()
    {
        long now = 0;
        var gate = new MeterFrameGate(() => now);
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
        for (now = 66; now < MeterFrameGate.Keepalive; now += 66)
            Assert.False(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
        now = MeterFrameGate.Keepalive;
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
        // The keepalive counts from the repeat it sent.
        now += 66;
        Assert.False(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
    }

    [Fact]
    public void AChangedFrameGoesOutAtOnce()
    {
        long now = 0;
        var gate = new MeterFrameGate(() => now);
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
        now = 66;
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0.5,0.4]}""")));
        now = 132;
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0]}""")));
        now = 198;
        Assert.True(gate.ShouldSend(Frame("""{"ch:xlr1":[0,0],"in:xlr1":[-60,-50,0]}""")));
    }
}
