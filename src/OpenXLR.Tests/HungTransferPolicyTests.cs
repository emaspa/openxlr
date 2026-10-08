using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class HungTransferPolicyTests
{
    [Fact]
    public void ThreeHangsSetADeviceAsideAndAReplugGivesItAFreshCount()
    {
        var policy = new HungTransferPolicy();
        Assert.False(policy.NoteHung("0fd9:007d@a"));
        Assert.False(policy.NoteHung("0fd9:007d@a"));
        Assert.False(policy.IsSetAside("0fd9:007d@a"));
        Assert.True(policy.NoteHung("0fd9:007d@a"));
        Assert.True(policy.IsSetAside("0fd9:007d@a"));
        Assert.Equal(["0fd9:007d@a"], policy.SetAside);
        Assert.False(policy.IsSetAside("0fd9:007d@b"));   // another unit of the model is not affected

        policy.Returned("0fd9:007d@a");
        Assert.False(policy.IsSetAside("0fd9:007d@a"));
        Assert.Equal(0, policy.HungCount("0fd9:007d@a"));
        Assert.False(policy.NoteHung("0fd9:007d@a"));     // counting starts over
    }
}
