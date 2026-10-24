using System.Buffers.Binary;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The original Wave XLR's ALSA mirror against a fake transport and a fake
/// card. The card's ranges are the XLR Dock's (the same protocol); no MK.1
/// amixer dump is public, which is why the backend reads them from the card.
/// </summary>
public sealed class WaveXlrMk1DeviceTests
{
    /// <summary>A 34-byte config block behind the transport: reads serve it, writes replace it.</summary>
    private sealed class FakeUsb : IUsbTransport
    {
        public byte[] Config { get; set; } = new byte[34];
        public List<byte[]> Writes { get; } = [];
        public bool IsOpen { get; private set; }

        public bool Open(ushort vid, ushort pid)
        {
            Assert.Equal((0x0FD9, 0x007D), (vid, pid));
            IsOpen = true;
            return true;
        }

        public void Close() => IsOpen = false;

        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout)
        {
            Assert.Equal(0x3303, i);
            if (t == 0xA1)
            {
                if (v != 0) return -5;
                int n = Math.Min(Config.Length, len);
                Config.AsSpan(0, n).CopyTo(data);
                return n;
            }
            Assert.Equal((0x21, 0x05, 0), (t, r, v));
            Config = data[..len];
            Writes.Add(Config.ToArray());
            return len;
        }

        public void Dispose() { }
    }

    private sealed class FakeCard
    {
        public int Gain { get; set; } = 147;   // the owner's archive: about 73.5 dB cached
        public string Capture { get; set; } = "on";
        public int Hp { get; set; } = 73;
        public List<(string Name, string Value)> Sets { get; } = [];
        public int Reads { get; private set; }
        public string? Fail { get; set; }
        /// <summary>The capture volume's line as the kernel describes it; replaced to test other answers.</summary>
        public string GainRange { get; set; } = """
              ; type=INTEGER,access=rw---R--,values=1,min=0,max=150,step=0
              : values={0}
              | dBminmax-min=0.00dB,max=75.00dB
            """;

        public ProcessResult Run(IReadOnlyList<string> args)
        {
            Assert.Equal(["-c", "5"], args.Take(2));
            if (Fail is not null) return new ProcessResult(1, [], Fail, false, false);
            switch (args[2])
            {
                case "contents":
                    Reads++;
                    string text = $"""
                        numid=3,iface=MIXER,name='PCM Playback Switch'
                          ; type=BOOLEAN,access=rw------,values=1
                          : values=on
                        numid=4,iface=MIXER,name='PCM Playback Volume'
                          ; type=INTEGER,access=rw---R--,values=2,min=0,max=120,step=0
                          : values={Hp},{Hp}
                          | dBminmax-min=-60.00dB,max=0.00dB
                        numid=5,iface=MIXER,name='Mic Capture Switch'
                          ; type=BOOLEAN,access=rw------,values=1
                          : values={Capture}
                        numid=6,iface=MIXER,name='Mic Capture Volume'
                        {string.Format(GainRange, Gain)}

                        """;
                    return new ProcessResult(0, System.Text.Encoding.UTF8.GetBytes(text), "", false, false);
                case "cset":
                    Assert.Equal(5, args.Count);
                    string name = args[3]["name=".Length..];
                    Sets.Add((name, args[4]));
                    switch (name)
                    {
                        case "Mic Capture Volume": Gain = int.Parse(args[4]); break;
                        case "Mic Capture Switch": Capture = args[4]; break;
                        case "PCM Playback Volume": Hp = int.Parse(args[4]); break;
                        default: Assert.Fail($"unexpected control {name}"); break;
                    }
                    return new ProcessResult(0, [], "", false, false);
                default:
                    Assert.Fail($"unexpected amixer {string.Join(' ', args)}");
                    return null!;
            }
        }
    }

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    }

    private static byte[] Block(double gainDb = 48, bool mute = false, double hpDb = -23.5)
    {
        var c = new byte[34];
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(0), (ushort)(gainDb * 256));
        c[4] = mute ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(9), (short)(hpDb * 256));
        return c;
    }

    private static (WaveXlrMk1Device Device, FakeUsb Usb, FakeCard Card, Clock Clock) Mirrored(FakeCard? card = null)
    {
        var usb = new FakeUsb { Config = Block() };
        card ??= new FakeCard();
        var clock = new Clock();
        var dev = new WaveXlrMk1Device(usb, card.Run, () => 5, () => clock.Now);
        dev.Connect();
        return (dev, usb, card, clock);
    }

    [Fact]
    public void TheFirstPollGivesTheCardWhatTheBlockHolds()
    {
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        Assert.Null(dev.ConnectionNote);
        DeviceState s = dev.ReadState();
        Assert.Equal(48, s.GainDb);
        // The stale 73.5 dB the card cached is replaced by the block's 48 dB.
        Assert.Equal([("Mic Capture Volume", "96"), ("Mic Capture Switch", "on"), ("PCM Playback Volume", "73")], card.Sets);
        Assert.Equal(1, card.Reads);   // the ranges, not the values
        Assert.Empty(usb.Writes);

        card.Sets.Clear();
        dev.ReadState();
        Assert.Empty(card.Sets);
        Assert.Empty(usb.Writes);
        Assert.StartsWith("card=5 given=gain=96 capture=on hp=73 ranges=", dev.DumpBlocks()["alsa"]);
    }

    [Fact]
    public void AChangeOnTheInterfaceReachesTheCard()
    {
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();

        usb.Config[4] = 1;                                                       // the mute pad
        BinaryPrimitives.WriteUInt16LittleEndian(usb.Config.AsSpan(0), 30 * 256);   // the dial
        dev.ReadState();
        Assert.Equal([("Mic Capture Volume", "60"), ("Mic Capture Switch", "off")], card.Sets);
        Assert.Empty(usb.Writes);

        card.Sets.Clear();
        dev.SetHpVolumeDb(-20);
        dev.ReadState();
        Assert.Equal([("PCM Playback Volume", "80")], card.Sets);
        Assert.Single(usb.Writes);
    }

    [Fact]
    public void ADesktopMuteOrHeadphoneChangeReachesTheInterfaceEveryHalfSecond()
    {
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, Clock clock) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();

        card.Hp = 100;
        card.Capture = "off";
        clock.Now += TimeSpan.FromMilliseconds(200);
        dev.ReadState();
        Assert.Empty(usb.Writes);

        clock.Now += TimeSpan.FromMilliseconds(300);
        DeviceState s = dev.ReadState();
        Assert.Single(usb.Writes);
        Assert.Equal(-10 * 256, BinaryPrimitives.ReadInt16LittleEndian(usb.Config.AsSpan(9)));
        Assert.Equal(1, usb.Config[4]);
        Assert.Equal((-10.0, true), (s.HpVolumeDb, s.Mute));
        Assert.Empty(card.Sets);

        clock.Now += TimeSpan.FromSeconds(1);
        dev.ReadState();
        Assert.Single(usb.Writes);
        Assert.Empty(card.Sets);
    }

    [Fact]
    public void TheCardsGainIsNeverTakenBack()
    {
        // Something on the desktop raises the capture volume. If the firmware
        // takes it, the block shows it and the state follows; the card alone
        // is a cache and never sets the gain.
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, Clock clock) = Mirrored();
        dev.ReadState();
        card.Gain = 150;
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void AGainTheFirmwareTookFromTheCardShowsInTheState()
    {
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(usb.Config.AsSpan(0), 74 * 256);   // the firmware applied a desktop write
        Assert.Equal(74, dev.ReadState().GainDb);
        Assert.Equal([("Mic Capture Volume", "148")], card.Sets);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void ACardWithoutAUsableCaptureVolumeStillMirrorsTheRest()
    {
        // The kernel drops a capture volume whose range is empty; one that
        // survives with max not above min carries nothing to map onto.
        var card = new FakeCard
        {
            GainRange = """
                  ; type=INTEGER,access=rw---R--,values=1,min=0,max=0,step=0
                  : values={0}
                  | dBminmax-min=0.00dB,max=0.00dB
                """,
        };
        (WaveXlrMk1Device dev, FakeUsb usb, _, _) = Mirrored(card);
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Equal([("Mic Capture Switch", "on"), ("PCM Playback Volume", "73")], card.Sets);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void ACardWithoutTheMuteSwitchIsNotAskedForIt()
    {
        var card = new FakeCard();
        var usb = new FakeUsb { Config = Block() };
        var clock = new Clock();
        // The fake prints the switch; drop it from the answer the backend reads.
        var dropped = new WaveXlrMk1Device(usb, args => args[2] == "contents"
            ? Strip(card.Run(args), "numid=5,iface=MIXER,name='Mic Capture Switch'")
            : card.Run(args), () => 5, () => clock.Now);
        dropped.Connect();
        dropped.ReadState();
        Assert.DoesNotContain(card.Sets, s => s.Name == "Mic Capture Switch");
        usb.Config[4] = 1;
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(dropped.ReadState().Mute);
        Assert.DoesNotContain(card.Sets, s => s.Name == "Mic Capture Switch");
        Assert.Empty(usb.Writes);
    }

    private static ProcessResult Strip(ProcessResult result, string header)
    {
        var lines = result.StdoutText.Split('\n').ToList();
        int at = lines.FindIndex(l => l.StartsWith(header, StringComparison.Ordinal));
        lines.RemoveRange(at, 3);
        return new ProcessResult(0, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)), "", false, false);
    }

    [Fact]
    public void ADbScaleRangeIsReadAsWellAsAMinMaxOne()
    {
        var ranges = Mk1ClassProtocolDevice.ParseRanges("""
            numid=6,iface=MIXER,name='Mic Capture Volume'
              ; type=INTEGER,access=rw---R--,values=1,min=0,max=150,step=0
              : values=96
              | dBscale-min=0.00dB,step=0.50dB,mute=0

            """);
        var gain = Assert.Single(ranges).Value;
        Assert.Equal((0, 150, 0.0, 75.0), (gain.Min, gain.Max, gain.MinDb, gain.MaxDb));
        Assert.Equal(96, gain.ToRaw(48));
        Assert.Equal(-23.5, new Mk1ClassProtocolDevice.AlsaRange(0, 120, -60, 0).ToDb(73));
    }

    [Fact]
    public void AnAmixerFailureLeavesTheInterfaceWorkingAndIsRetried()
    {
        (WaveXlrMk1Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        card.Fail = "amixer: Control hw:5 open error: No such device";
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Contains("No such device", dev.DumpBlocks()["alsa"]);
        Assert.Empty(card.Sets);

        card.Fail = null;
        dev.ReadState();
        Assert.Equal(3, card.Sets.Count);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void AMissingAmixerLeavesTheInterfaceWorking()
    {
        var usb = new FakeUsb { Config = Block() };
        var dev = new WaveXlrMk1Device(usb, _ => throw new System.ComponentModel.Win32Exception(2, "No such file or directory"), () => 5);
        dev.Connect();
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Equal("not mirrored: amixer could not be started: No such file or directory", dev.DumpBlocks()["alsa"]);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void WithoutItsCardTheInterfaceIsDrivenAndTheNoteSaysSo()
    {
        var usb = new FakeUsb { Config = Block() };
        var card = new FakeCard();
        var dev = new WaveXlrMk1Device(usb, card.Run, () => throw new InvalidOperationException("Wave XLR present on USB but its ALSA card was not found"));
        dev.Connect();
        Assert.True(dev.Connected);
        Assert.Contains("ALSA card was not found", dev.ConnectionNote);
        Assert.Contains("not kept in step", dev.ConnectionNote);
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Empty(card.Sets);
        Assert.Equal(0, card.Reads);
        Assert.StartsWith("not mirrored:", dev.DumpBlocks()["alsa"]);
    }

    [Fact]
    public void WithoutAmixerTheCardIsLeftAlone()
    {
        var usb = new FakeUsb { Config = Block() };
        var dev = new WaveXlrMk1Device(usb, null, () => throw new InvalidOperationException("no card lookup without amixer"));
        dev.Connect();
        Assert.Equal(48, dev.ReadState().GainDb);
        Assert.Null(dev.ConnectionNote);
        Assert.Equal("not mirrored", dev.DumpBlocks()["alsa"]);
    }
}
