using System.Buffers.Binary;
using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The XLR Dock reaches gain, mute and headphone volume through ALSA when
/// the kernel created the control and through the MK.1 config block when it
/// did not (a unit was met whose firmware answers the capture volume's range
/// query with max at or below min, so snd-usb-audio drops that control).
/// </summary>
public sealed class XlrDockDeviceTests
{
    /// <summary>A dock's config block behind the transport: reads serve it, writes replace it.</summary>
    private sealed class FakeUsb : IUsbTransport
    {
        public byte[] Config { get; } = new byte[64];
        public bool Openable { get; init; } = true;
        public List<byte[]> Writes { get; } = [];
        public bool IsOpen { get; private set; }
        public bool Open(ushort vid, ushort pid) { IsOpen = Openable; return IsOpen; }
        public void Close() => IsOpen = false;
        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout)
        {
            Assert.Equal(0x3303, i);
            if ((t & 0x80) != 0)
            {
                Assert.Equal(0xA1, t); Assert.Equal(0x85, r);
                if (v != 0) return -5;   // only the config block exists here
                Config.AsSpan(0, len).CopyTo(data);
                return len;
            }
            Assert.Equal(0x21, t); Assert.Equal(0x05, r); Assert.Equal(0, v);
            data.AsSpan(0, len).CopyTo(Config);
            Writes.Add(data.AsSpan(0, len).ToArray());
            return len;
        }
        public void Dispose() { }
    }

    /// <summary>amixer over a card with a chosen set of controls.</summary>
    private sealed class FakeAmixer
    {
        private readonly Dictionary<string, string> _values = new();
        private readonly Dictionary<string, string> _types = new();
        public List<string> Calls { get; } = [];

        public FakeAmixer WithInteger(string name, int value) { _values[name] = value.ToString(); _types[name] = "INTEGER"; return this; }
        public FakeAmixer WithSwitch(string name, bool on) { _values[name] = on ? "on" : "off"; _types[name] = "BOOLEAN"; return this; }
        public string Value(string name) => _values[name];

        public ProcessResult Run(IReadOnlyList<string> args)
        {
            Calls.Add(string.Join(' ', args));
            Assert.Equal("-c", args[0]);
            Assert.Equal("7", args[1]);
            string verb = args[2];
            if (verb == "controls")
            {
                var sb = new StringBuilder();
                int id = 1;
                foreach (string ctl in _values.Keys) sb.Append($"numid={id++},iface=MIXER,name='{ctl}'\n");
                return Ok(sb.ToString());
            }
            string name = args[3]["name=".Length..];
            if (!_values.ContainsKey(name))
                return new ProcessResult(1, [], "amixer: Cannot find the given element from control sysdefault:7\n", false, false);
            if (verb == "cget")
                return Ok($"numid=1,iface=MIXER,name='{name}'\n  ; type={_types[name]},access=rw------,values=1\n  : values={_values[name]}\n");
            Assert.Equal("cset", verb);
            _values[name] = args[4];
            return Ok("");
        }

        private static ProcessResult Ok(string stdout) => new(0, Encoding.UTF8.GetBytes(stdout), "", false, false);
    }

    private static (XlrDockDevice Device, FakeUsb Usb, FakeAmixer Amixer) Dock(FakeAmixer amixer, bool usbOpenable = true)
    {
        var usb = new FakeUsb { Openable = usbOpenable };
        BinaryPrimitives.WriteUInt16LittleEndian(usb.Config.AsSpan(0), 40 * 256);     // 40 dB
        usb.Config[4] = 0;                                                             // unmuted
        BinaryPrimitives.WriteInt16LittleEndian(usb.Config.AsSpan(9), (short)(-31 * 256)); // -31 dB
        usb.Config[6] = 1;                                                             // phantom on
        var dev = new XlrDockDevice(usb, amixer.Run, () => 7);
        return (dev, usb, amixer);
    }

    private static FakeAmixer FullCard() => new FakeAmixer()
        .WithSwitch("PCM Playback Switch", true)
        .WithInteger("PCM Playback Volume", 58)
        .WithSwitch("Mic Capture Switch", true)
        .WithInteger("Mic Capture Volume", 150);

    private static FakeAmixer CardWithoutCaptureVolume() => new FakeAmixer()
        .WithSwitch("PCM Playback Switch", true)
        .WithInteger("PCM Playback Volume", 58)
        .WithSwitch("Mic Capture Switch", true);

    [Fact]
    public void AFullCardKeepsEveryControlOnAlsa()
    {
        (XlrDockDevice dev, FakeUsb usb, FakeAmixer amixer) = Dock(FullCard());
        dev.Connect();

        Assert.True(dev.Connected);
        Assert.Null(dev.ConnectionNote);
        Assert.Equal(XlrDockDevice.ControlPath.Alsa, dev.GainPath);
        Assert.Equal(XlrDockDevice.ControlPath.Alsa, dev.MutePath);
        Assert.Equal(XlrDockDevice.ControlPath.Alsa, dev.HpPath);
        Assert.True(dev.Capabilities.Gain);

        DeviceState s = dev.ReadState();
        Assert.Equal(75, s.GainDb);          // ALSA's 150, not the block's 40
        Assert.False(s.Mute);
        Assert.Equal(-31.0, s.HpVolumeDb);
        Assert.True(s.Phantom);              // the block still serves the USB-only flags

        dev.SetGainDb(20);
        Assert.Equal("40", amixer.Value("Mic Capture Volume"));
        Assert.Empty(usb.Writes);            // never behind the kernel's cache
    }

    [Fact]
    public void AMissingCaptureVolumeGoesThroughTheBlockWord()
    {
        (XlrDockDevice dev, FakeUsb usb, FakeAmixer amixer) = Dock(CardWithoutCaptureVolume());
        dev.Connect();

        Assert.True(dev.Connected);
        Assert.Equal(XlrDockDevice.ControlPath.Block, dev.GainPath);
        Assert.Equal(XlrDockDevice.ControlPath.Alsa, dev.MutePath);
        Assert.Equal(XlrDockDevice.ControlPath.Alsa, dev.HpPath);
        Assert.True(dev.Capabilities.Gain);
        Assert.Contains("'Mic Capture Volume'", dev.ConnectionNote);
        Assert.Contains("config block", dev.ConnectionNote);

        DeviceState s = dev.ReadState();
        Assert.Equal(40, s.GainDb);          // the block word
        Assert.False(s.Mute);                // still ALSA
        Assert.Equal(-31.0, s.HpVolumeDb);   // still ALSA (58 raw)

        dev.SetGainDb(62);
        byte[] written = Assert.Single(usb.Writes);
        Assert.Equal(62 * 256, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(0)));
        Assert.Equal(1, written[6]);         // the rest of the block untouched
        Assert.DoesNotContain(amixer.Calls, c => c.Contains("cset") && c.Contains("Mic Capture Volume"));
        Assert.Equal(62, dev.ReadState().GainDb);

        dev.SetGainDb(200);                  // clamped to the preamp's range
        Assert.Equal(75 * 256, BinaryPrimitives.ReadUInt16LittleEndian(usb.Config.AsSpan(0)));

        dev.SetMute(true);                   // mute keeps its ALSA path
        Assert.Equal("off", amixer.Value("Mic Capture Switch"));
        Assert.Equal(2, usb.Writes.Count);
    }

    [Fact]
    public void AMissingControlWithoutTheUsbHandleIsReportedNotFatal()
    {
        (XlrDockDevice dev, _, _) = Dock(CardWithoutCaptureVolume(), usbOpenable: false);
        dev.Connect();

        Assert.True(dev.Connected);
        Assert.Equal(XlrDockDevice.ControlPath.None, dev.GainPath);
        Assert.False(dev.Capabilities.Gain);
        Assert.True(dev.Capabilities.Mute);
        Assert.Contains("udev", dev.ConnectionNote);

        DeviceState s = dev.ReadState();     // the state still reads; the dock stays connected
        Assert.Equal(0, s.GainDb);
        Assert.False(s.Mute);
        Assert.False(s.Phantom);

        var ex = Assert.Throws<InvalidOperationException>(() => dev.SetGainDb(10));
        Assert.Contains("gain", ex.Message);
    }

    [Fact]
    public void EveryControlCanFallBackToTheBlock()
    {
        var amixer = new FakeAmixer().WithSwitch("PCM Playback Switch", true);
        (XlrDockDevice dev, FakeUsb usb, _) = Dock(amixer);
        dev.Connect();

        Assert.Equal(XlrDockDevice.ControlPath.Block, dev.MutePath);
        Assert.Equal(XlrDockDevice.ControlPath.Block, dev.HpPath);
        DeviceState s = dev.ReadState();
        Assert.Equal(40, s.GainDb);
        Assert.False(s.Mute);
        Assert.Equal(-31.0, s.HpVolumeDb);

        dev.SetMute(true);
        Assert.Equal(1, usb.Config[4]);
        Assert.True(dev.ReadState().Mute);
        dev.SetHpVolumeDb(-12.5);
        Assert.Equal((short)(-12.5 * 256), BinaryPrimitives.ReadInt16LittleEndian(usb.Config.AsSpan(9)));
        Assert.Equal(-12.5, dev.ReadState().HpVolumeDb);
    }

    [Fact]
    public void DisconnectForgetsTheCard()
    {
        (XlrDockDevice dev, FakeUsb usb, _) = Dock(FullCard());
        dev.Connect();
        dev.Disconnect();
        Assert.False(dev.Connected);
        Assert.False(usb.IsOpen);
    }
}
