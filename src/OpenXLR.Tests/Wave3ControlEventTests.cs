using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

public sealed partial class Wave3DeviceTests
{
    private sealed class ControlEvents : IAlsaControlEvents
    {
        public bool Available { get; set; } = true;
        /// <summary>A change to both controls, when the test does not care which.</summary>
        public bool Pending { get; set; }
        public AlsaControls Changes { get; set; }
        public bool Disposed { get; private set; }
        public bool DisconnectOnRead { get; set; }

        public AlsaControls Changed()
        {
            if (DisconnectOnRead) Available = false;
            AlsaControls value = Changes | (Pending ? AlsaControls.Mute | AlsaControls.Headphones : AlsaControls.None);
            Pending = false;
            Changes = AlsaControls.None;
            return value;
        }
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
    public void ADesktopEditOfAControlJustWrittenIsReadWithinHalfASecond(bool alsoMute)
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            usb.Config = Block(gainDb: 20, mute: alsoMute, hpDb: -30);
            card.AfterSet = name =>
            {
                if (name != "PCM Playback Volume") return;
                card.Hp = 100; // Desktop edit after the mirror writes its value.
                events.Changes |= AlsaControls.Headphones;
            };
            Assert.Equal(-30, dev.ReadState().HpVolumeDb);
            card.AfterSet = null;
            clock.Now += TimeSpan.FromMilliseconds(400);
            Assert.Equal(-30, dev.ReadState().HpVolumeDb);
            Assert.Equal(0, card.Reads);
            clock.Now += TimeSpan.FromMilliseconds(100);
            Assert.Equal(-10, dev.ReadState().HpVolumeDb);
            Assert.Equal(1, card.Reads);
        }
    }

    [Fact]
    public void ADesktopEditOfTheOtherControlDuringAMirrorWriteIsReadAtOnce()
    {
        var (dev, usb, card, _, events) = EventMirrored();
        using (dev)
        {
            usb.Config = Block(gainDb: 20, mute: false, hpDb: -30);
            card.AfterSet = name =>
            {
                events.Changes |= name == "PCM Playback Volume" ? AlsaControls.Headphones : AlsaControls.Mute;
                if (name == "PCM Playback Volume") { card.Capture = "off"; events.Changes |= AlsaControls.Mute; }
            };
            var state = dev.ReadState();
            Assert.Equal(-30, state.HpVolumeDb);
            Assert.True(state.Mute);
            Assert.Equal(1, card.Reads);
        }
    }

    [Fact]
    public void TurningTheDialReadsTheCardOnlyEveryHalfSecond()
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            // The kernel reports every write, this backend's own included.
            card.Sets.Clear();
            card.AfterSet = name => events.Changes |= name == "PCM Playback Volume" ? AlsaControls.Headphones : AlsaControls.Mute;
            // The dial moves on every other poll: the polls between see no new value.
            for (int i = 1; i <= 18; i++)
            {
                clock.Now += TimeSpan.FromMilliseconds(100);
                if (i % 2 == 1) usb.Config = Block(gainDb: 20, mute: false, hpDb: -12.5 - i);
                dev.ReadState();
            }
            Assert.Equal(9, card.Sets.Count(s => s.Name == "PCM Playback Volume"));
            Assert.Equal(3, card.Reads);

            // The last write is read back within half a second of the dial
            // stopping, and a desktop edit after that at once.
            for (int i = 0; i < 5; i++) { clock.Now += TimeSpan.FromMilliseconds(100); dev.ReadState(); }
            Assert.Equal(4, card.Reads);
            card.Capture = "off";
            events.Changes = AlsaControls.Mute;
            clock.Now += TimeSpan.FromMilliseconds(100);
            Assert.True(dev.ReadState().Mute);
            Assert.Equal(5, card.Reads);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedAlsaWriteDoesNotKeepReadbackPending(bool mute)
    {
        var (dev, usb, card, clock, events) = EventMirrored();
        using (dev)
        {
            card.Sets.Clear();
            card.FailedSetName = mute ? "Mic Capture Switch" : "PCM Playback Volume";
            usb.Config = Block(gainDb: 20, mute: mute, hpDb: mute ? -12.5 : -30);
            events.Pending = true;
            dev.ReadState();
            Assert.Equal(1, card.Reads);

            for (int i = 0; i < 10; i++)
            {
                clock.Now += TimeSpan.FromMilliseconds(100);
                dev.ReadState();
            }
            Assert.Equal(1, card.Reads);
            Assert.Equal(11, card.Sets.Count);

            card.FailedSetName = null;
            dev.ReadState();
            Assert.Equal(mute ? "off" : "on", card.Capture);
            Assert.Equal(mute ? 95 : 60, card.Hp);
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

    [Theory]
    [InlineData(1u, "Mic Capture Switch", 1)]
    [InlineData(1u, "PCM Playback Volume", 2)]
    [InlineData(3u, "PCM Playback Volume", 2)]
    [InlineData(1u, "Mic Capture Volume", 0)]
    [InlineData(1u, "PCM Playback Switch", 0)]
    [InlineData(2u, "Mic Capture Switch", 0)]
    [InlineData(4u, "PCM Playback Volume", 0)]
    [InlineData(uint.MaxValue, "Mic Capture Switch", 0)]
    public void OnlyMuteAndHeadphoneValueEventsTriggerReadback(uint mask, string name, int expected)
        => Assert.Equal((AlsaControls)expected, AlsaControlEvents.ReadbackControl(mask, name));

    [Fact]
    public void AControlWatchOnANegativeCardIsUnavailable()
        => Assert.Null(AlsaControlEvents.Open(-1));

    [Fact]
    public void ACardEnumeratedAfterUsbConnectIsMirroredWithoutReplugging()
    {
        var usb = new FakeUsb { Config = Block() };
        var card = new FakeCard();
        var clock = new Clock();
        var events = new ControlEvents();
        int lookups = 0;
        using var dev = new Wave3Device(usb, card.Run, () =>
        {
            if (++lookups == 1) throw new InvalidOperationException("ALSA card was not found");
            return 3;
        }, clock, _ => events);
        dev.Connect();
        Assert.NotNull(dev.ConnectionNote);
        clock.Now += TimeSpan.FromMilliseconds(1999);
        dev.ReadState();
        Assert.Empty(card.Sets);
        Assert.Equal(1, lookups);

        clock.Now += TimeSpan.FromMilliseconds(1);
        dev.ReadState();
        Assert.Equal(2, lookups);
        Assert.Equal(3, card.Sets.Count);
        Assert.Null(dev.ConnectionNote);
        Assert.Contains("readback=events+30s", dev.DumpBlocks()["alsa"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAlsaResetRebindsTheNewCardWithoutReconnectingUsb(bool cardArrivesLate)
    {
        var usb = new FakeUsb { Config = Block(gainDb: 20, mute: true, hpDb: -30) };
        var oldCard = new FakeCard();
        var newCard = new FakeCard();
        var oldEvents = new ControlEvents();
        var newEvents = new ControlEvents();
        var clock = new Clock();
        var accessed = new List<int>();
        var subscriptions = new List<int>();
        int currentCard = 3;
        using var dev = new Wave3Device(usb, args =>
        {
            int card = int.Parse(args[1]);
            accessed.Add(card);
            return (card == 3 ? oldCard : newCard).Run(["-c", "3", .. args.Skip(2)]);
        }, () => currentCard >= 0 ? currentCard : throw new InvalidOperationException("ALSA card was not found"), clock, card =>
        {
            subscriptions.Add(card);
            return card == 3 ? oldEvents : newEvents;
        });

        dev.Connect();
        Assert.True(dev.ReadState().Mute);
        var deviceInfoRead = usb.Transfers[0];
        accessed.Clear();
        currentCard = cardArrivesLate ? -1 : 4;
        oldEvents.DisconnectOnRead = true;
        usb.Config = Block(gainDb: 20, mute: false, hpDb: -20);
        clock.Now += TimeSpan.FromMilliseconds(100);
        Assert.False(dev.ReadState().Mute);
        Assert.True(oldEvents.Disposed);
        Assert.DoesNotContain(3, accessed);

        if (cardArrivesLate)
        {
            Assert.Empty(accessed);
            currentCard = 4;
            clock.Now += TimeSpan.FromMilliseconds(1999);
            dev.ReadState();
            Assert.Empty(accessed);
            clock.Now += TimeSpan.FromMilliseconds(1);
            dev.ReadState();
        }

        Assert.Equal([3, 4], subscriptions);
        Assert.Equal([4, 4, 4], accessed);
        Assert.Equal("on", newCard.Capture);
        Assert.Equal(80, newCard.Hp);
        Assert.Null(dev.ConnectionNote);
        Assert.True(usb.IsOpen);
        Assert.Single(usb.Transfers, t => t == deviceInfoRead);
        Assert.Empty(usb.Writes);

        newCard.Hp = 100;
        newEvents.Pending = true;
        Assert.Equal(-10, dev.ReadState().HpVolumeDb);
        Assert.Single(usb.Writes);
    }

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
