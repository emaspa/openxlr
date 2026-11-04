using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed partial class Wave3DeviceTests
{
    private sealed class ControlEvents : IAlsaControlEvents
    {
        public bool Available { get; set; } = true;
        public bool Pending { get; set; }
        public bool Disposed { get; private set; }
        public bool Changed() { bool value = Pending; Pending = false; return value; }
        public void Dispose() { Disposed = true; Available = false; }
    }

    private static (Wave3Device Device, FakeUsb Usb, FakeCard Card, Clock Clock, ControlEvents Events) EventMirrored()
    {
        var usb = new FakeUsb { Config = Block(gainDb: 20, mute: false, hpDb: -12.5) };
        var card = new FakeCard();
        var clock = new Clock();
        var events = new ControlEvents();
        var dev = new Wave3Device(usb, card.Run, () => 3, clock, _ => events);
        dev.Connect();
        dev.ReadState();
        return (dev, usb, card, clock, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADesktopEditDuringAMirrorWriteIsReadOnTheNextPoll(bool alsoMute)
    {
        var (dev, usb, card, _, events) = EventMirrored();
        using (dev)
        {
            usb.Config = Block(gainDb: 20, mute: alsoMute, hpDb: -30);
            card.AfterSet = name =>
            {
                if (name != "PCM Playback Volume") return;
                card.Hp = 100; // Desktop edit after the mirror writes its value.
                events.Pending = true;
            };
            Assert.Equal(-30, dev.ReadState().HpVolumeDb);
            int reads = alsoMute ? 0 : 1;
            Assert.Equal(reads, card.Reads);
            Assert.Equal(-10, dev.ReadState().HpVolumeDb);
            Assert.Equal(reads + 1, card.Reads);
        }
    }

    [Fact]
    public void AnIdleControlSubscriptionReadsTheCardOnlyEveryThirtySeconds()
    {
        var (dev, _, card, clock, _) = EventMirrored();
        using (dev)
        {
            for (int i = 1; i < 30; i++) { clock.Now += TimeSpan.FromSeconds(1); dev.ReadState(); }
            Assert.Equal(0, card.Reads);
            Assert.Contains("readback=events+30s", dev.DumpBlocks()["alsa"]);
            clock.WallClockOffset = TimeSpan.FromHours(-1);
            clock.Now += TimeSpan.FromSeconds(1);
            dev.ReadState();
            Assert.Equal(1, card.Reads);
        }
    }

    [Fact]
    public void ControlEventsApplyDesktopChangesWithoutWaitingForTheFallback()
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            card.Capture = "off";
            card.Hp = 100;
            events.Pending = true;
            clock.Now += TimeSpan.FromMilliseconds(100);
            var state = dev.ReadState();
            Assert.Equal(1, card.Reads);
            Assert.True(state.Mute);
            Assert.Equal(-10, state.HpVolumeDb);
            Assert.Single(usb.Writes);
            dev.ReadState();
            Assert.Equal(1, card.Reads);
        }
    }

    [Fact]
    public void LosingTheSubscriptionRestoresTheFastFallbackAndDisposeClosesIt()
    {
        var (dev, _, card, clock, events) = EventMirrored();
        events.Available = false;
        clock.Now += TimeSpan.FromMilliseconds(500);
        dev.ReadState();
        Assert.Equal(1, card.Reads);
        Assert.Contains("readback=poll-500ms", dev.DumpBlocks()["alsa"]);
        dev.Disconnect();
        Assert.True(events.Disposed);
        dev.Dispose();
    }
    [Fact]
    public void AFailedEventReadIsRetriedWithoutAnotherEvent()
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            card.Capture = "off";
            card.Fail = "temporarily unavailable";
            events.Pending = true;
            dev.ReadState();
            Assert.Empty(usb.Writes);
            card.Fail = null;
            events.Pending = true;
            clock.Now += TimeSpan.FromMilliseconds(499);
            Assert.False(dev.ReadState().Mute);
            Assert.Equal(0, card.Reads);
            clock.Now += TimeSpan.FromMilliseconds(1);
            Assert.True(dev.ReadState().Mute);
            Assert.Equal(1, card.Reads);
            Assert.Single(usb.Writes);
        }
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(8)]
    public void AnEventIsAppliedAfterATemporaryConfigWriteFailure(int failedWrite)
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            card.Capture = "off";
            card.Hp = 100;
            events.Pending = true;
            usb.ConfigWriteAnswer = failedWrite;
            Assert.Throws<InvalidOperationException>(() => dev.ReadState());
            Assert.Empty(usb.Writes);
            usb.ConfigWriteAnswer = 16;
            clock.Now += TimeSpan.FromMilliseconds(499);
            Assert.False(dev.ReadState().Mute);
            Assert.Equal(1, card.Reads);
            clock.Now += TimeSpan.FromMilliseconds(1);
            var state = dev.ReadState();
            Assert.True(state.Mute);
            Assert.Equal(-10, state.HpVolumeDb);
            Assert.Equal(2, card.Reads);
            Assert.Single(usb.Writes);
        }
    }

    [Fact]
    public void FailedUsbWriteDoesNotReplaySuccessfulAlsaWrites()
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            card.Sets.Clear();
            usb.Config = Block(gainDb: 20, mute: true, hpDb: -12.5);
            card.Hp = 100;
            card.AfterSet = name =>
            {
                if (name != "Mic Capture Switch") return;
                card.Capture = "on";
                events.Pending = true;
            };
            usb.ConfigWriteAnswer = -7;
            Assert.Throws<InvalidOperationException>(() => dev.ReadState());
            Assert.Single(card.Sets);

            card.AfterSet = null;
            usb.ConfigWriteAnswer = 16;
            clock.Now += TimeSpan.FromMilliseconds(500);
            DeviceState state = dev.ReadState();
            Assert.Single(card.Sets);
            Assert.False(state.Mute);
            Assert.Equal(-10, state.HpVolumeDb);
        }
    }

    [Fact]
    public void ReconnectingReplacesTheControlSubscriptionAndDisposeReleasesIt()
    {
        var usb = new FakeUsb { Config = Block() };
        var card = new FakeCard();
        var subscriptions = new List<ControlEvents>();
        using var dev = new Wave3Device(usb, card.Run, () => 3, events: _ =>
        {
            var events = new ControlEvents();
            subscriptions.Add(events);
            return events;
        });
        dev.Connect();
        dev.Disconnect();
        Assert.True(subscriptions[0].Disposed);
        dev.Connect();
        Assert.Equal(2, subscriptions.Count);
        Assert.False(subscriptions[1].Disposed);
        dev.Dispose();
        Assert.True(subscriptions[1].Disposed);
    }

    [Fact]
    public void AControlWatchOnANegativeCardIsUnavailable()
        => Assert.Null(AlsaControlEvents.Open(-1));

    [Fact]
    public void AnUnavailableControlHandleIsRetriedAndRecoveryRefreshesTheCard()
    {
        var usb = new FakeUsb { Config = Block() };
        var card = new FakeCard();
        var clock = new Clock();
        var events = new ControlEvents();
        int attempts = 0;
        using var dev = new Wave3Device(usb, card.Run, () => 3, clock,
            _ => ++attempts == 1 ? null : events);
        dev.Connect();
        dev.ReadState();
        clock.Now += TimeSpan.FromSeconds(1);
        dev.ReadState();
        Assert.Equal(1, attempts);
        Assert.Equal(1, card.Reads);
        clock.Now += TimeSpan.FromSeconds(29);
        dev.ReadState();
        Assert.Equal(2, attempts);
        Assert.Equal(2, card.Reads);
        clock.Now += TimeSpan.FromSeconds(1);
        dev.ReadState();
        Assert.Equal(2, card.Reads);
        Assert.Contains("readback=events+30s", dev.DumpBlocks()["alsa"]);
    }

}
