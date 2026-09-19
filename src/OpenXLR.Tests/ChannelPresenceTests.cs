using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Which hardware strips the daemon tells clients to show. Only the Wave XLR
/// Pro has a second XLR jack and the auxiliary input stage, so on every other
/// model those channels can never carry audio. They stay in the layout and
/// keep their levels; the state says the device cannot feed them.
/// </summary>
public sealed class ChannelPresenceTests
{
    [Fact]
    public void AChannelTheDeviceCannotFeedIsReportedAbsent()
    {
        using var mixer = new Mixer();

        // Without a device there is nothing to go on, so every strip shows,
        // which is what the window has always done before one is connected.
        Assert.All(mixer.Snapshot().Channels, channel => Assert.True(channel.Present));

        Assert.True(mixer.SetInputJacks(1, false));
        Dictionary<string, ChannelStatus> single = mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.True(single["xlr1"].Present);
        Assert.False(single["xlr2"].Present);
        Assert.False(single["aux"].Present);
        // An application channel has no jack behind it and is never absent.
        Assert.True(single["game"].Present);
        // The strip is still there, so its sends survive the device change.
        Assert.NotEmpty(single["xlr2"].Levels);

        Assert.True(mixer.SetInputJacks(2, true));
        Dictionary<string, ChannelStatus> pro = mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.True(pro["xlr2"].Present);
        Assert.True(pro["aux"].Present);
    }

    [Fact]
    public void OnlyAChangedAnswerAsksForABroadcast()
    {
        using var mixer = new Mixer();
        Assert.True(mixer.SetInputJacks(1, false));
        Assert.False(mixer.SetInputJacks(1, false));
        Assert.True(mixer.SetInputJacks(null, null));
        Assert.All(mixer.Snapshot().Channels, channel => Assert.True(channel.Present));
    }
}
