using System.Buffers.Binary;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave:3 backend against a fake transport. Nothing here touches USB,
/// and nothing here shows the protocol is right: the layout under test is
/// the public research the class cites, and the tests hold the code to it.
/// </summary>
public sealed class Wave3DeviceTests
{
    /// <summary>A config block behind the transport: reads serve it, writes replace it.</summary>
    private sealed class FakeUsb : IUsbTransport
    {
        public byte[] Config { get; set; } = new byte[16];
        /// <summary>How many bytes the fake answers for the config block.</summary>
        public int ConfigAnswer { get; set; } = 16;
        public byte[]? DevInfo { get; set; }
        public List<byte[]> Writes { get; } = [];
        public List<(byte Type, byte Request, ushort Value)> Transfers { get; } = [];
        public bool IsOpen { get; private set; }

        public bool Open(ushort vid, ushort pid)
        {
            Assert.Equal((0x0FD9, 0x0070), (vid, pid));
            IsOpen = true;
            return true;
        }

        public void Close() => IsOpen = false;

        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout)
        {
            Transfers.Add((t, r, v));
            Assert.Equal(0x3303, i);
            // A vendor-type request (0xC1/0x41) rebooted a unit into DFU
            // (wave3-research); only the class dialect may reach the device.
            Assert.True(t is 0xA1 or 0x21, $"request type {t:x2}");
            if (t == 0xA1)
            {
                Assert.Equal(0x85, r);
                switch (v)
                {
                    case 0:
                        int n = Math.Min(ConfigAnswer, len);
                        Config.AsSpan(0, n).CopyTo(data);
                        return n;
                    case 0x000A when DevInfo is not null:
                        int m = Math.Min(DevInfo.Length, len);
                        DevInfo.AsSpan(0, m).CopyTo(data);
                        return m;
                    default:
                        return -5;   // LIBUSB_ERROR_NOT_FOUND: no other block exists here
                }
            }
            Assert.Equal(0x05, r);
            Assert.Equal(0, v);
            Assert.Equal(16, len);
            data.AsSpan(0, len).CopyTo(Config);
            Writes.Add(data.AsSpan(0, len).ToArray());
            return len;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A block laid out as the sources say, every byte the class does not
    /// own set to a distinct marker so a write that strays shows.
    /// </summary>
    private static byte[] Block(int gainDb = 20, bool mute = false, bool clipGuard = true,
        double hpDb = -12.5, bool hpMute = false, int mixPercent = 40, byte dial = Wave3Device.DialGain)
    {
        var c = new byte[16];
        for (int i = 0; i < c.Length; i++) c[i] = (byte)(0xA0 + i);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), (short)(gainDb * 256));
        c[4] = mute ? (byte)1 : (byte)0;
        c[5] = clipGuard ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(7), (short)(hpDb * 256));
        c[9] = hpMute ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(10), (short)(mixPercent * 256));
        c[12] = dial;
        return c;
    }

    /// <summary>The bytes no setter owns: retransmitted as read, never changed.</summary>
    private static readonly int[] Carried = [2, 3, 6, 12, 13, 14, 15];

    private static (Wave3Device Device, FakeUsb Usb) Mic(byte[]? block = null)
    {
        var usb = new FakeUsb { Config = block ?? Block() };
        var dev = new Wave3Device(usb);
        dev.Connect();
        usb.Transfers.Clear();   // the device info read at connect
        return (dev, usb);
    }

    private static short Word(FakeUsb usb, int offset) => BinaryPrimitives.ReadInt16LittleEndian(usb.Config.AsSpan(offset));

    [Fact]
    public void CapabilitiesAreTheMicrophonesOwnAndNothingMore()
    {
        var dev = new Wave3Device(new FakeUsb());
        Assert.Equal(new DeviceInfo("Elgato", "Wave:3", 0x0FD9, 0x0070), dev.Info);
        Assert.Equal("Wave_3", dev.Info.NodeNameFragment);
        Assert.Equal(new DeviceCapabilities
        {
            Gain = true,
            Mute = true,
            ClipGuard = true,
            HpVolume = true,
            Crossfade = true,
            PhysicalControls = true,
            XlrInputs = 1,
            HpOutputs = 1,
            GainMaxDb = 40,
            RetainsSettings = true,
        }, dev.Capabilities);
    }

    [Theory]
    [InlineData("gain")]
    [InlineData("mute")]
    [InlineData("clipGuard")]
    [InlineData("hpVolumeDb")]
    [InlineData("crossfade")]
    [InlineData("gainLock")]
    public void TheDaemonOffersItsControls(string control)
        => Assert.Null(DeviceManager.UnsupportedControlReason(new Wave3Device(new FakeUsb()).Capabilities, control));

    [Theory]
    [InlineData("lowCut")]
    [InlineData("phantom")]
    [InlineData("lowImpedance")]
    [InlineData("expander")]
    [InlineData("voiceTune")]
    [InlineData("compressor")]
    [InlineData("gain2")]
    [InlineData("hp2VolumeDb")]
    [InlineData("outHp1")]
    [InlineData("auxLevelDb")]
    public void TheDaemonRefusesWhatTheMicrophoneLacks(string control)
        => Assert.Contains("not supported",
            DeviceManager.UnsupportedControlReason(new Wave3Device(new FakeUsb()).Capabilities, control));

    [Fact]
    public void ConnectOpensTheMicrophoneAndDisconnectCloses()
    {
        (Wave3Device dev, FakeUsb usb) = Mic();
        Assert.True(dev.Connected);
        dev.Disconnect();
        Assert.False(dev.Connected);
        Assert.False(usb.IsOpen);
    }

    [Fact]
    public void ReadStateDecodesEveryField()
    {
        (Wave3Device dev, _) = Mic(Block(gainDb: 20, mute: false, clipGuard: true, hpDb: -12.5, hpMute: false, mixPercent: 40));
        DeviceState s = dev.ReadState();
        Assert.Equal(20, s.GainDb);
        Assert.False(s.Mute);
        Assert.True(s.ClipGuard);
        Assert.Equal(-12.5, s.HpVolumeDb);
        Assert.False(s.HpMute);
        Assert.Equal(80, s.Crossfade);          // 40 percent of the way to PC
        // Nothing the microphone lacks is reported as on.
        Assert.False(s.LowCut || s.Phantom || s.LowImpedance || s.Expander || s.VoiceTune || s.Compressor);

        (dev, _) = Mic(Block(gainDb: 0, mute: true, clipGuard: false, hpDb: -60, hpMute: true, mixPercent: 100));
        s = dev.ReadState();
        Assert.Equal((0, true, false, -60.0, true, 200), (s.GainDb, s.Mute, s.ClipGuard, s.HpVolumeDb, s.HpMute, s.Crossfade));
    }

    [Fact]
    public void TheGainWordIsTrustedOnGainAndRememberedElsewhere()
    {
        // The dial on headphones at connect, the word at 0 holding -12 (the
        // dial's value in wave3-research's reading): nothing better is known
        // yet, so the word is reported as openwave reads it, held to the range.
        byte[] c = Block(hpDb: -12, dial: Wave3Device.DialHeadphones);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), -12 * 256);
        (Wave3Device dev, FakeUsb usb) = Mic(c);
        Assert.Equal(0, dev.ReadState().GainDb);
        Assert.Contains("not read with the dial on gain", dev.DumpBlocks()["gain"]);

        // The dial turned to gain: the word is the gain, and is remembered.
        usb.Config[12] = Wave3Device.DialGain;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 20 * 256);
        Assert.Equal(20, dev.ReadState().GainDb);
        Assert.Equal("20 dB, last read with the dial on gain", dev.DumpBlocks()["gain"]);

        // The dial turned to the mix, the word following it to 75: the gain
        // reported is the one seen on gain, not 40 dB.
        usb.Config[12] = Wave3Device.DialMonitorMix;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 75 * 256);
        Assert.Equal(20, dev.ReadState().GainDb);

        // Back on gain, whatever the word says is the gain again.
        usb.Config[12] = Wave3Device.DialGain;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 31 * 256);
        Assert.Equal(31, dev.ReadState().GainDb);

        // A disconnect forgets it: after a replug the dial may be anywhere.
        dev.Disconnect();
        dev.Connect();
        usb.Config[12] = Wave3Device.DialMonitorMix;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 75 * 256);
        Assert.Equal(40, dev.ReadState().GainDb);   // 75, held to the range, as openwave would show it
    }

    [Fact]
    public void AGainWriteDoesNotPretendToHaveBeenTaken()
    {
        // wave3-research says host writes to the gain are ignored and only the
        // dial moves it; openwave writes it. The write goes out, and what is
        // reported stays what was last read with the dial on gain.
        (Wave3Device dev, FakeUsb usb) = Mic(Block(gainDb: 20));
        Assert.Equal(20, dev.ReadState().GainDb);
        usb.Config[12] = Wave3Device.DialHeadphones;
        dev.SetGainDb(35);
        Assert.Equal(35 * 256, Word(usb, 0));
        Assert.Equal(20, dev.ReadState().GainDb);
        usb.Config[12] = Wave3Device.DialGain;
        Assert.Equal(35, dev.ReadState().GainDb);
    }

    [Theory]
    [InlineData(0x0000, 0.0)]
    [InlineData(0x0080, 0.5)]
    [InlineData(0x2800, 40.0)]      // the gain's top
    [InlineData(0x6400, 100.0)]     // the balance's top
    [InlineData(0xF380, -12.5)]
    [InlineData(0xC400, -60.0)]     // the headphone floor
    [InlineData(0xFF00, -1.0)]
    public void AQ88WordIs256UnitsPerUnit(int raw, double value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)raw);
        Assert.Equal(value, Wave3Device.FromQ88(bytes));
    }

    [Theory]
    [InlineData(-12.5, -3200)]
    [InlineData(-12.3, -3148)]      // -3148.8 truncated toward zero, as openwave writes it
    [InlineData(-12.7, -3251)]      // -3251.2
    [InlineData(-0.7, -179)]        // -179.2
    [InlineData(-60.0, -15360)]
    [InlineData(-70.0, -15360)]     // held to the floor
    [InlineData(0.0, 0)]
    [InlineData(3.0, 0)]            // held to the top
    public void TheHeadphoneLevelIsHeldToItsRangeThenTruncatedTowardZero(double db, int raw)
        => Assert.Equal((short)raw, Wave3Device.HpToWord(db));

    [Fact]
    public void NotANumberIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Wave3Device.HpToWord(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Wave3Device.HpToWord(double.NegativeInfinity));
        (Wave3Device dev, FakeUsb usb) = Mic();
        Assert.Throws<ArgumentOutOfRangeException>(() => dev.SetHpVolumeDb(double.NaN));
        Assert.Empty(usb.Writes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 128)]            // one crossfade unit is half a percent
    [InlineData(95, 12160)]
    [InlineData(100, 12800)]
    [InlineData(105, 13440)]
    [InlineData(200, 25600)]
    [InlineData(250, 25600)]        // held to the top
    [InlineData(-9, 0)]
    public void TheCrossfadeIsHalfAPercentPerUnitWithNoGrid(int crossfade, int raw)
    {
        Assert.Equal((short)raw, Wave3Device.CrossfadeToWord(crossfade));
        Assert.Equal(Math.Clamp(crossfade, 0, 200), Wave3Device.CrossfadeFromWord((short)raw));
    }

    [Theory]
    [InlineData(12200, 95)]         // 95.3 from the dial
    [InlineData(12224, 96)]         // 95.5 rounds away from zero
    [InlineData(-128, 0)]
    [InlineData(30000, 200)]
    public void ACrossfadeWordOffTheHalfPercentIsRoundedAndHeld(int raw, int crossfade)
        => Assert.Equal(crossfade, Wave3Device.CrossfadeFromWord((short)raw));

    [Fact]
    public void TheStreamDeckDialTravelsBothWays()
    {
        // The plugin steps the crossfade five units per tick from the value
        // it last saw. Every tick must land where it asked, down and up,
        // or the dial only travels one way.
        (Wave3Device dev, FakeUsb usb) = Mic();
        dev.SetCrossfade(100);
        int at = 100;
        for (int tick = 0; tick < 6; tick++)
        {
            at -= 5;
            dev.SetCrossfade(at);
            Assert.Equal(at, dev.ReadState().Crossfade);
        }
        Assert.Equal(70, at);
        for (int tick = 0; tick < 12; tick++)
        {
            at += 5;
            dev.SetCrossfade(at);
            Assert.Equal(at, dev.ReadState().Crossfade);
        }
        Assert.Equal(130, dev.ReadState().Crossfade);
        Assert.Equal(65 * 256, Word(usb, 10));

        // The headphone dial steps 0.6 dB; every step reads back lower than
        // the one before, and at or just above the level asked, since the
        // word is truncated toward zero.
        double level = -12.0;
        dev.SetHpVolumeDb(level);
        double last = dev.ReadState().HpVolumeDb;
        for (int tick = 0; tick < 10; tick++)
        {
            level -= 0.6;
            dev.SetHpVolumeDb(level);
            double now = dev.ReadState().HpVolumeDb;
            Assert.True(now < last, $"{now} after {last}");
            Assert.InRange(now - level, 0, 1.0 / 256);
            last = now;
        }
    }

    [Fact]
    public void EachSetterWritesItsBytesAndCarriesTheRest()
    {
        (Wave3Device dev, FakeUsb usb) = Mic();
        byte[] before = (byte[])usb.Config.Clone();

        dev.SetGainDb(33);
        dev.SetMute(true);
        dev.SetClipGuard(false);
        dev.SetHpVolumeDb(-30.25);
        dev.SetCrossfade(150);

        Assert.Equal(5, usb.Writes.Count);
        byte[] c = usb.Config;
        Assert.Equal(33 * 256, Word(usb, 0));
        Assert.Equal(1, c[4]);
        Assert.Equal(0, c[5]);
        Assert.Equal(-30.25 * 256, Word(usb, 7));
        Assert.Equal(0, c[9]);                     // was 0, a level above the floor keeps it released
        Assert.Equal(75 * 256, Word(usb, 10));     // 150 of 200 is 75 percent
        foreach (int i in Carried) Assert.Equal(before[i], c[i]);

        // Each write was preceded by a read of the block it modified, and
        // each write sent the whole block.
        Assert.Equal(10, usb.Transfers.Count);
        for (int i = 0; i < usb.Transfers.Count; i += 2)
        {
            Assert.Equal((0xA1, 0x85, 0), usb.Transfers[i]);
            Assert.Equal((0x21, 0x05, 0), usb.Transfers[i + 1]);
            Assert.Equal(16, usb.Writes[i / 2].Length);
        }

        // Every setter changed one field of the block and nothing else.
        byte[] first = usb.Writes[0];
        for (int i = 2; i < 16; i++) Assert.Equal(before[i], first[i]);
        byte[] second = usb.Writes[1];
        for (int i = 0; i < 16; i++) Assert.Equal(i == 4 ? 1 : first[i], second[i]);

        DeviceState s = dev.ReadState();
        Assert.Equal((33, true, false, -30.25, 150), (s.GainDb, s.Mute, s.ClipGuard, s.HpVolumeDb, s.Crossfade));
    }

    [Fact]
    public void ALevelAboveTheFloorReleasesTheFirmwaresHeadphoneMute()
    {
        (Wave3Device dev, FakeUsb usb) = Mic(Block(hpDb: -60, hpMute: true));
        Assert.True(dev.ReadState().HpMute);

        dev.SetHpVolumeDb(-60);            // at the floor: the firmware's mute is its own to keep
        Assert.Equal(1, usb.Config[9]);
        Assert.True(dev.ReadState().HpMute);

        dev.SetHpVolumeDb(-59.9);          // anything above it is a request to hear
        Assert.Equal(0, usb.Config[9]);
        Assert.False(dev.ReadState().HpMute);
        Assert.Equal(-15334, Word(usb, 7));   // -15334.4 truncated toward zero

        // No setter asserts it, and every other setter carries it as read.
        usb.Config[9] = 1;
        dev.SetMute(false);
        dev.SetClipGuard(true);
        dev.SetCrossfade(100);
        dev.SetGainDb(10);
        Assert.Equal(1, usb.Config[9]);
        Assert.True(dev.ReadState().HpMute);
    }

    [Fact]
    public void WritesAreHeldToTheRanges()
    {
        (Wave3Device dev, FakeUsb usb) = Mic();

        dev.SetGainDb(90);
        Assert.Equal(0x2800, Word(usb, 0));
        dev.SetGainDb(-5);
        Assert.Equal(0, Word(usb, 0));

        dev.SetHpVolumeDb(3);
        Assert.Equal(0, Word(usb, 7));
        dev.SetHpVolumeDb(-100);
        Assert.Equal(-15360, Word(usb, 7));

        dev.SetCrossfade(999);
        Assert.Equal(0x6400, Word(usb, 10));
        dev.SetCrossfade(-9);
        Assert.Equal(0, Word(usb, 10));
    }

    [Fact]
    public void ReadsOutsideTheRangesAreHeldToThem()
    {
        byte[] c = Block();
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), 48 * 256);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(7), -80 * 256);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(10), 112 * 256);
        DeviceState s = Wave3Device.Decode(c, null);
        Assert.Equal((40, -60.0, 200), (s.GainDb, s.HpVolumeDb, s.Crossfade));

        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), -1 * 256);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(7), 5 * 256);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(10), -1 * 256);
        s = Wave3Device.Decode(c, null);
        Assert.Equal((0, 0.0, 0), (s.GainDb, s.HpVolumeDb, s.Crossfade));

        // A half-dB gain reads as the nearest whole dB, halves away from zero.
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), (short)(20.5 * 256));
        Assert.Equal(21, Wave3Device.Decode(c, null).GainDb);
        BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(0), (short)(20.4 * 256));
        Assert.Equal(20, Wave3Device.Decode(c, null).GainDb);
    }

    [Fact]
    public void AShortAnswerIsRefusedAndNothingIsWritten()
    {
        (Wave3Device dev, FakeUsb usb) = Mic();
        usb.ConfigAnswer = 12;

        var ex = Assert.Throws<InvalidOperationException>(() => dev.ReadState());
        Assert.Contains("12 bytes", ex.Message);
        Assert.Contains("expected 16", ex.Message);

        ex = Assert.Throws<InvalidOperationException>(() => dev.SetMute(true));
        Assert.Contains("expected 16", ex.Message);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void ARefusedReadIsAnErrorNotAState()
    {
        var usb = new FakeUsb { ConfigAnswer = 0 };
        var dev = new Wave3Device(usb);
        dev.Connect();
        Assert.Throws<InvalidOperationException>(() => dev.ReadState());
        Assert.Throws<InvalidOperationException>(() => dev.SetCrossfade(100));
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void WhatTheMicrophoneLacksIsANoOpOnTheWire()
    {
        (Wave3Device dev, FakeUsb usb) = Mic();
        dev.SetLowCut(true);
        dev.SetPhantom(true);
        dev.SetLowImpedance(true);
        dev.SetExpander(true);
        dev.SetVoiceTune(true);
        dev.SetVoiceTuneStrength(70);
        dev.SetCompressor(true);
        Assert.Empty(usb.Transfers);
        Assert.Empty(usb.Writes);
        Assert.Equal(0xA6, usb.Config[6]);   // the low cut byte in one schema, never touched
    }

    [Fact]
    public void TheDumpShowsTheBlocksAsAnsweredAndTheDialsTarget()
    {
        (Wave3Device dev, FakeUsb usb) = Mic(Block(dial: Wave3Device.DialHeadphones));
        IReadOnlyDictionary<string, string> blocks = dev.DumpBlocks();
        Assert.Equal(Convert.ToHexString(usb.Config), blocks["config"]);
        Assert.Equal("headphones", blocks["dial"]);
        Assert.StartsWith("error:", blocks["devinfo"]);   // the fake has none; a unit answering nothing is reported, not fatal
        Assert.Empty(usb.Writes);

        usb.DevInfo = Enumerable.Range(0, 51).Select(i => (byte)i).ToArray();
        usb.Config[12] = 7;
        blocks = dev.DumpBlocks();
        Assert.Equal(Convert.ToHexString(usb.DevInfo), blocks["devinfo"]);
        Assert.Equal("unknown (7)", blocks["dial"]);

        usb.DevInfo = new byte[40];   // shorter than asked: shown at its own length
        Assert.Equal(80, dev.DumpBlocks()["devinfo"].Length);
    }

    [Fact]
    public void ConnectReadsTheFirmwareVersionFromTheDeviceInfo()
    {
        var info = new byte[64];
        info[21] = 1; info[22] = 2; info[23] = 2;
        var usb = new FakeUsb { DevInfo = info };
        var dev = new Wave3Device(usb);
        dev.Connect();
        Assert.Equal("1.2.2", dev.Firmware);
        Assert.Equal("1.2.2", dev.DumpBlocks()["firmware"]);
        Assert.Contains(((byte)0xA1, (byte)0x85, (ushort)0x000A), usb.Transfers);

        dev.Disconnect();
        Assert.Null(dev.Firmware);
        usb.DevInfo = new byte[23];   // too short to hold the version
        dev.Connect();
        Assert.Null(dev.Firmware);
        usb.DevInfo = null;           // no answer at all: still connected, version unknown
        dev.Disconnect();
        dev.Connect();
        Assert.True(dev.Connected);
        Assert.Null(dev.Firmware);
        Assert.Equal("not read", dev.DumpBlocks()["firmware"]);
    }

    /// <summary>The kernel's three controls for the Wave:3 behind a fake amixer, in its own units.</summary>
    private sealed class FakeCard
    {
        public int Gain { get; set; } = 24;
        public string Capture { get; set; } = "on";
        public int Hp { get; set; } = 102;
        public List<(string Name, string Value)> Sets { get; } = [];
        public int Reads { get; private set; }
        public string? Fail { get; set; }

        public ProcessResult Run(IReadOnlyList<string> args)
        {
            Assert.Equal(["-c", "3"], args.Take(2));
            if (Fail is not null) return new ProcessResult(1, [], Fail, false, false);
            switch (args[2])
            {
                case "contents":
                    Reads++;
                    // Laid out as amixer prints it (wave3-research's dump of a unit).
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
                          ; type=INTEGER,access=rw---R--,values=1,min=0,max=80,step=0
                          : values={Gain}
                          | dBminmax-min=0.00dB,max=40.00dB

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
        public DateTime Now { get; set; } = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (Wave3Device Device, FakeUsb Usb, FakeCard Card, Clock Clock) Mirrored(byte[]? block = null)
    {
        var usb = new FakeUsb { Config = block ?? Block(gainDb: 20, mute: false, hpDb: -12.5) };
        var card = new FakeCard();
        var clock = new Clock();
        var dev = new Wave3Device(usb, card.Run, () => 3, () => clock.Now);
        dev.Connect();
        return (dev, usb, card, clock);
    }

    [Fact]
    public void TheFirstPollGivesTheCardWhatTheBlockHolds()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        Assert.Null(dev.ConnectionNote);
        DeviceState s = dev.ReadState();
        Assert.Equal(20, s.GainDb);
        Assert.Equal([("Mic Capture Volume", "40"), ("Mic Capture Switch", "on"), ("PCM Playback Volume", "95")], card.Sets);
        Assert.Equal(0, card.Reads);   // the card's cached values are not taken over the block's
        Assert.Empty(usb.Writes);

        // Nothing changed: nothing is written either way.
        card.Sets.Clear();
        dev.ReadState();
        Assert.Empty(card.Sets);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void AChangeOnTheMicrophoneReachesTheCard()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();

        usb.Config[4] = 1;                                                  // the mute pad
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 30 * 256);   // the dial, on gain
        dev.ReadState();
        Assert.Equal([("Mic Capture Volume", "60"), ("Mic Capture Switch", "off")], card.Sets);
        Assert.Empty(usb.Writes);

        // A write from OpenXLR goes the same way on the next poll.
        card.Sets.Clear();
        dev.SetHpVolumeDb(-20);
        dev.ReadState();
        Assert.Equal([("PCM Playback Volume", "80")], card.Sets);
        Assert.Single(usb.Writes);
    }

    [Fact]
    public void AChangeOnTheCardReachesTheMicrophoneEveryHalfSecond()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, Clock clock) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();

        // The desktop's volume control lowers the headphones and mutes the mic.
        card.Hp = 100;
        card.Capture = "off";
        clock.Now += TimeSpan.FromMilliseconds(200);
        dev.ReadState();
        Assert.Empty(usb.Writes);      // not read again yet

        clock.Now += TimeSpan.FromMilliseconds(300);
        DeviceState s = dev.ReadState();
        Assert.Equal(1, card.Reads);
        Assert.Single(usb.Writes);
        Assert.Equal(-10 * 256, Word(usb, 7));
        Assert.Equal(1, usb.Config[4]);
        Assert.Equal((-10.0, true), (s.HpVolumeDb, s.Mute));
        Assert.Empty(card.Sets);       // the card already holds what it set

        // The next read finds the two agreeing and writes nothing.
        clock.Now += TimeSpan.FromSeconds(1);
        dev.ReadState();
        Assert.Single(usb.Writes);
        Assert.Empty(card.Sets);
    }

    [Fact]
    public void TheCardsGainIsNeverTakenBack()
    {
        // The firmware ignores a gain written through the class control
        // (wave3-research), so the card's gain is only ever a stale cache.
        (Wave3Device dev, FakeUsb usb, FakeCard card, Clock clock) = Mirrored();
        dev.ReadState();
        card.Gain = 10;
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(20, dev.ReadState().GainDb);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void TheMicrophoneWinsWhenBothChangeInOneStep()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, Clock clock) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();
        card.Hp = 100;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(7), -30 * 256);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(-30, dev.ReadState().HpVolumeDb);
        Assert.Equal([("PCM Playback Volume", "60")], card.Sets);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void TheGainGivenToTheCardFollowsTheDialOnlyOnGain()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        dev.ReadState();
        card.Sets.Clear();
        usb.Config[12] = Wave3Device.DialMonitorMix;
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(0), 75 * 256);   // the dial's value, in one reading
        dev.ReadState();
        Assert.Empty(card.Sets);
    }

    [Fact]
    public void AnAmixerFailureLeavesTheMicrophoneWorkingAndIsRetried()
    {
        (Wave3Device dev, FakeUsb usb, FakeCard card, _) = Mirrored();
        card.Fail = "amixer: Control hw:3 open error: No such device";
        Assert.Equal(20, dev.ReadState().GainDb);
        Assert.Contains("No such device", dev.DumpBlocks()["alsa"]);
        Assert.Empty(card.Sets);

        card.Fail = null;
        dev.ReadState();
        Assert.Equal(3, card.Sets.Count);
        Assert.StartsWith("card=3 given=gain=40 capture=on hp=95", dev.DumpBlocks()["alsa"]);
        Assert.Empty(usb.Writes);
    }

    [Fact]
    public void WithoutItsCardTheMicrophoneIsDrivenAndTheNoteSaysSo()
    {
        var usb = new FakeUsb { Config = Block() };
        var card = new FakeCard();
        var dev = new Wave3Device(usb, card.Run, () => throw new InvalidOperationException("Wave:3 present on USB but its ALSA card was not found"));
        dev.Connect();
        Assert.True(dev.Connected);
        Assert.Contains("ALSA card was not found", dev.ConnectionNote);
        Assert.Contains("not kept in step", dev.ConnectionNote);
        Assert.Equal(20, dev.ReadState().GainDb);
        Assert.Empty(card.Sets);
        Assert.Equal(0, card.Reads);
        Assert.StartsWith("not mirrored:", dev.DumpBlocks()["alsa"]);
    }

    [Fact]
    public void TheCardIsFoundByTheUnitsBus()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-wave3-cards").FullName;
        try
        {
            foreach ((int card, string id, string bus) in new[] { (1, "0fd9:00a6", "001/004"), (2, "0fd9:0070", "001/009"), (4, "0fd9:0070", "003/002") })
            {
                Directory.CreateDirectory(Path.Combine(root, $"card{card}"));
                File.WriteAllText(Path.Combine(root, $"card{card}", "usbid"), id + "\n");
                File.WriteAllText(Path.Combine(root, $"card{card}", "usbbus"), bus + "\n");
            }
            Assert.Equal(4, AlsaCards.Find(root, new UsbLocation(3, 2, "3-1", null), "0fd9:0070", "Wave:3"));
            Assert.Equal(2, AlsaCards.Find(root, new UsbLocation(1, 9, "1-2", null), "0fd9:0070", "Wave:3"));
            var ex = Assert.Throws<InvalidOperationException>(() => AlsaCards.Find(root, null, "0fd9:0070", "Wave:3"));
            Assert.Contains("Several Wave:3s", ex.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
