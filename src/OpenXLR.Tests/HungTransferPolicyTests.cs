using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class HungTransferPolicyTests
{
    [Fact]
    public void ThreeHangsSetADeviceAsideAndAReplugGivesItAFreshCount()
    {
        var policy = new HungTransferPolicy();
        Assert.False(policy.NoteHung(0x007d));
        Assert.False(policy.NoteHung(0x007d));
        Assert.False(policy.IsSetAside(0x007d));
        Assert.True(policy.NoteHung(0x007d));
        Assert.True(policy.IsSetAside(0x007d));
        Assert.Equal([0x007d], policy.SetAside);
        Assert.False(policy.IsSetAside(0x00b4));   // another model is not affected

        policy.Returned(0x007d);
        Assert.False(policy.IsSetAside(0x007d));
        Assert.Equal(0, policy.HungCount(0x007d));
        Assert.False(policy.NoteHung(0x007d));     // counting starts over
    }
}
