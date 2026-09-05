using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class MonitorFeedTests
{
    [Fact]
    public void AFeedIsOneMixOrSeveralJoinedWithPlus()
    {
        Assert.Equal(["monitor"], MonitorFeed.Parts("monitor"));
        Assert.Equal(["monitor", "monitor2"], MonitorFeed.Parts("monitor+monitor2"));
        Assert.Equal(["monitor2"], MonitorFeed.Parts(" monitor2 + "));
        Assert.Empty(MonitorFeed.Parts(null));
        Assert.Equal("monitor+monitor2", MonitorFeed.Join(["monitor", "monitor2"]));
        Assert.True(MonitorFeed.Includes("monitor+monitor2", "monitor2"));
        Assert.False(MonitorFeed.Includes("monitor", "monitor2"));
    }

    [Fact]
    public void TheSummedOptionReadsMonitorAPlusB()
    {
        Assert.Equal("Monitor A+B", MainViewModel.SummedName(["Monitor A", "Monitor B"]));
        Assert.Equal("Desk+Booth", MainViewModel.SummedName(["Desk", "Booth"]));
    }
}
