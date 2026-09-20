using System.Buffers.Binary;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato Wave:3 (0fd9:0070): a USB condenser microphone with a headphone
/// jack, not an XLR interface. Nobody on this project owns one. Every protocol
/// fact below comes from public work on other people's units, each line names
/// its source, and none of it has been run against a Wave:3 from here;
/// docs/hardware-support.md records every control as coded, not verified.
///
/// Sources, by the tag used below:
///   OW   rikkichy/openwave, crates/openwave-core/src/profiles.rs and
///        protocol.rs: an implementation that runs on the hardware and has
///        users. Where it and a schema reading differ, it wins here.
///   W3R  LukasParke/wave3-research, WAVE3_PROTOCOL_SUMMARY.md and
///        native-linux/docs/protocol-notes.md: live probing of one unit
///        (firmware 0.3.7, API 5.3) from Linux.
///   LW   zhgmx/LibreWave, docs/protocol-evidence.md: a schema recovered
///        from Wave Link for the API 5.3 and 5.4 /config message, not run on
///        hardware by that project.
///
/// Transport, on which all three agree: the original Wave XLR's class-request
/// dialect (<see cref="Mk1ClassProtocolDevice"/>), read 0xA1/0x85, write
/// 0x21/0x05, wIndex 0x3303, a 16-byte config block at wValue 0 and a device
/// info block at wValue 0x000A. W3R found that vendor-type requests
/// (0xC1/0x41) stall and that a wValue scan with them rebooted the unit into
/// its DFU product id 0x0071, so this class sends class requests only, and
/// only for those two block numbers.
///
/// The config block, byte by byte:
///   0..1   gain, Q8.8 dB, 0 to 40, little-endian: OW reads and writes it
///          there (unsigned, capped at 0x2800), LW the same (signed). W3R
///          logged the block while turning the dial and saw this word follow
///          the dial in each of its three modes, gain being one of them;
///          <see cref="ReadState"/> says how the two readings are reconciled.
///   4      microphone mute, 0x01 muted (OW, W3R probed it, LW).
///   5      ClipGuard, 0x01 on (W3R probed it, LW).
///   6      not touched. LW's schema calls it the low cut; OW has no code
///          path that reaches it on any device and keeps its low cut as a
///          filter in the capture chain; W3R wrote it and saw no effect. The
///          low cut on this device is the submixer's, as on the XLR Dock.
///   7..8   headphone level, signed Q8.8 dB, -60 to 0 (OW, LW). W3R reads
///          byte 8 as whole signed dB and byte 7 as a flag that toggles 0x80
///          while the dial moves the volume, which is the fraction byte of a
///          half-dB step, so the three agree on the wire. OW writes the value
///          truncated toward zero, "matching the firmware setter's int()",
///          and so does this class.
///   9      headphone mute, 0x01 muted (W3R probed it, LW). W3R saw the
///          firmware set it when the level reaches its floor. Read into the
///          state, and released by a level written above the floor.
///   10..11 direct monitor balance, Q8.8 percent, 0 to 100 (OW, capped at
///          0x6400, on the hardware; LW). W3R reads byte 11 as the dial's mix
///          value in mix mode, which is that word's integer byte, and bytes
///          10 and 13 as the ring's red and blue with a separate mix byte at
///          14. OW's users would have a bug report if writing 10 coloured the
///          ring, so the word is written; 13 and 14 keep their read values.
///   12     the physical dial's target: 1 gain, 2 headphones, 3 monitor mix
///          (OW, W3R observed it, LW).
///   15     LW's schema calls it a gain lock, a device policy that ignores
///          the host's volume requests; W3R calls it the LED brightness. Not
///          touched; the daemon's own gain lock needs no byte.
///   2, 3, 6, 13, 14 and 15 are retransmitted with the values read.
///
/// Every write reads the block, changes the bytes of one field and writes all
/// 16 back under the lock, so the bytes this class does not own keep the
/// values the firmware gave them. Nothing is written after a short or failed
/// read. No value is snapped to a grid: OW writes arbitrary raw values on the
/// hardware, so the firmware does not need one, and a grid would swallow the
/// Stream Deck's five-unit crossfade ticks.
/// </summary>
public sealed class Wave3Device : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x0070;

    private const byte RtRead = 0xA1;
    private const byte RtWrite = 0x21;
    private const byte ReqRead = 0x85;
    private const byte ReqWrite = 0x05;
    private const ushort Index = 0x3303;

    private const ushort BlockConfig = 0x0000;
    private const ushort BlockDevInfo = 0x000A;
    internal const int ConfigLen = 16;
    // W3R read 51 bytes of device info, the MK.1's length; OW asks for 64 and
    // places the firmware version and the serial further in. 51 is what a
    // unit is known to have answered; the dump shows whatever comes back.
    private const int DevInfoLen = 51;

    internal const int OffGain = 0;
    internal const int OffMute = 4;
    internal const int OffClipGuard = 5;
    internal const int OffHpVol = 7;
    internal const int OffHpMute = 9;
    internal const int OffMonitorMix = 10;
    internal const int OffDialTarget = 12;

    internal const byte DialGain = 1;
    internal const byte DialHeadphones = 2;
    internal const byte DialMonitorMix = 3;

    // OW's caps (0x2800 and 0x6400), LW's ranges and W3R's UAC ranges (0 to
    // 40 dB, -60 to 0 dB) agree on the limits.
    internal const int GainMinDb = 0, GainMaxDb = 40;
    internal const double HpMinDb = -60, HpMaxDb = 0;

    private readonly object _lock = new();
    private readonly IUsbTransport _usb;

    // The gain last read while the dial was on gain, when every source reads
    // the word at 0 as the gain; null until the dial has been there once
    // since the connect.
    private int? _gainDb;

    public Wave3Device() : this(UsbTransport.Create()) { }

    /// <summary>Tests substitute the transport.</summary>
    internal Wave3Device(IUsbTransport usb) => _usb = usb;

    public DeviceInfo Info { get; } = new("Elgato", "Wave:3", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true,
        Mute = true,
        ClipGuard = true,
        HpVolume = true,
        Crossfade = true,
        // A multi-function dial and a capacitive mute pad (W3R), so the
        // daemon's gain lock is kept off the window as on every device with
        // a dial it cannot bind.
        PhysicalControls = true,
        XlrInputs = 1,   // the capsule feeds the one input strip
        HpOutputs = 1,
        // Whether the unit keeps its settings over a power cycle is stated
        // by none of the sources. True means the daemon writes nothing at
        // connect that the user did not ask for; a replug on real hardware
        // decides it.
        RetainsSettings = true,
    };

    public bool Connected => _usb.IsOpen;

    /// <summary>Release the USB transport and its helper process.</summary>
    public void Dispose() => _usb.Dispose();

    public void Connect()
    {
        if (!_usb.Open(VendorId, ProductId))
            throw new InvalidOperationException($"{Info.Model} present but could not be opened (udev rule?)");
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _usb.Close();
            _gainDb = null;   // a replug may change what the dial was on
        }
    }

    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the transport has dropped the
    /// device by then (the helper process is killed), Connected turns false
    /// and the daemon reconnects with a fresh one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
        => _usb.ControlTransfer(requestType, request, value, Index, data, (ushort)length, 1000);

    /// <summary>
    /// The 16 bytes, or an error. An answer of another length is refused
    /// rather than decoded: every field is read at a fixed offset and every
    /// write sends the whole block back, so a firmware that answers
    /// differently is one to report, not to guess at.
    /// </summary>
    private byte[] ReadConfigLocked()
    {
        var buf = new byte[ConfigLen];
        int n = Transfer(RtRead, ReqRead, BlockConfig, buf, ConfigLen);
        if (n < 0) throw new InvalidOperationException($"read config block: {LibUsb.StrError(n)}");
        if (n != ConfigLen)
            throw new InvalidOperationException($"read config block: got {n} bytes, expected {ConfigLen}");
        return buf;
    }

    private void Modify(Action<byte[]> edit)
    {
        lock (_lock)
        {
            byte[] cfg = ReadConfigLocked();
            edit(cfg);
            int n = Transfer(RtWrite, ReqWrite, BlockConfig, cfg, ConfigLen);
            if (n < 0) throw new InvalidOperationException($"write config block: {LibUsb.StrError(n)}");
            if (n != ConfigLen)
                throw new InvalidOperationException($"write config block: accepted {n} bytes, expected {ConfigLen}");
        }
    }

    /// <summary>
    /// The block decoded. The gain word is trusted while the dial is on gain,
    /// where OW and W3R agree it is the gain, and remembered from there.
    /// While the dial is on headphones or the mix, OW reads the word as the
    /// gain still and W3R as the dial's own value, so the gain last read on
    /// gain is reported instead; until the dial has been on gain once since
    /// the connect there is nothing better than the word as OW takes it.
    /// </summary>
    public DeviceState ReadState()
    {
        lock (_lock)
        {
            byte[] c = ReadConfigLocked();
            if (DialOnGain(c)) _gainDb = GainFromWord(c);
            return Decode(c, _gainDb);
        }
    }

    internal static bool DialOnGain(ReadOnlySpan<byte> c) => c[OffDialTarget] == DialGain;

    internal static int GainFromWord(ReadOnlySpan<byte> c)
        => Math.Clamp((int)Math.Round(FromQ88(c[OffGain..]), MidpointRounding.AwayFromZero), GainMinDb, GainMaxDb);

    /// <summary>The state a block holds, given the gain last read with the dial on gain (see <see cref="ReadState"/>).</summary>
    internal static DeviceState Decode(ReadOnlySpan<byte> c, int? gainSeenOnGain) => new()
    {
        GainDb = DialOnGain(c) || gainSeenOnGain is null ? GainFromWord(c) : gainSeenOnGain.Value,
        Mute = c[OffMute] != 0,
        ClipGuard = c[OffClipGuard] != 0,
        HpVolumeDb = Math.Clamp(FromQ88(c[OffHpVol..]), HpMinDb, HpMaxDb),
        HpMute = c[OffHpMute] != 0,
        Crossfade = CrossfadeFromWord(BinaryPrimitives.ReadInt16LittleEndian(c[OffMonitorMix..])),
    };

    /// <summary>A little-endian Q8.8 word as a number: 256 raw units per unit.</summary>
    internal static double FromQ88(ReadOnlySpan<byte> at) => BinaryPrimitives.ReadInt16LittleEndian(at) / 256.0;

    /// <summary>
    /// The monitor balance as OpenXLR's crossfade: the same control on another
    /// scale. Both blend the direct microphone path against the computer's
    /// playback inside the device, on the way to the headphones, with the
    /// microphone alone at one end and the computer alone at the other; the
    /// Pro's word runs 0 to 200 with 100 at the centre, the Wave:3's 0 to 100
    /// percent, so one crossfade unit is half a percent, 128 raw. Which end of
    /// the Wave:3's word is the microphone is stated by none of the sources;
    /// 0 is taken as microphone only, the direction of the Pro's crossfade
    /// and of W3R's reading of its byte 14, and an owner settles it by ear.
    /// </summary>
    internal static short CrossfadeToWord(int crossfade) => (short)(Math.Clamp(crossfade, 0, 200) * 128);

    internal static int CrossfadeFromWord(short raw)
        => Math.Clamp((int)Math.Round(raw / 128.0, MidpointRounding.AwayFromZero), 0, 200);

    /// <summary>The headphone level as the firmware's word: held to the range, then truncated toward zero as OW does.</summary>
    internal static short HpToWord(double db)
    {
        if (!double.IsFinite(db)) throw new ArgumentOutOfRangeException(nameof(db), db, "not a number");
        return (short)(Math.Clamp(db, HpMinDb, HpMaxDb) * 256);
    }

    public void SetGainDb(int db)
        => Modify(c => BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffGain), (short)(Math.Clamp(db, GainMinDb, GainMaxDb) * 256)));

    public void SetMute(bool on) => Modify(c => c[OffMute] = on ? (byte)1 : (byte)0);

    public void SetClipGuard(bool on) => Modify(c => c[OffClipGuard] = on ? (byte)1 : (byte)0);

    public void SetHpVolumeDb(double db)
    {
        short word = HpToWord(db);
        Modify(c =>
        {
            BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffHpVol), word);
            // W3R saw the firmware assert the headphone mute when the level
            // reaches the floor. A level above it is a request to hear
            // something, so the mute goes with it rather than leaving the
            // jack silent with nothing in OpenXLR to release it.
            if (word > HpMinDb * 256) c[OffHpMute] = 0;
        });
    }

    public void SetCrossfade(int value)
        => Modify(c => BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffMonitorMix), CrossfadeToWord(value)));

    // Not on this hardware (phantom, low impedance, expander, voice tune,
    // compressor), or the submixer's by design (low cut, see the class
    // summary). The capabilities keep the daemon from offering them.
    public void SetLowCut(bool on) { }
    public void SetExpander(bool on) { }
    public void SetVoiceTune(bool on) { }
    public void SetVoiceTuneStrength(int value) { }
    public void SetLowImpedance(bool on) { }
    public void SetPhantom(bool on) { }
    public void SetCompressor(bool on) { }

    /// <summary>
    /// The two blocks as read, at whatever length the firmware answers, the
    /// dial's target in words and where the reported gain comes from, so an
    /// owner's report says what the dial was doing when the block was taken.
    /// </summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        lock (_lock)
        {
            foreach ((string name, ushort block, int len) in new[] { ("config", BlockConfig, ConfigLen), ("devinfo", BlockDevInfo, DevInfoLen) })
            {
                try
                {
                    var buf = new byte[len];
                    int n = Transfer(RtRead, ReqRead, block, buf, len);
                    blocks[name] = n >= 0 ? Convert.ToHexString(buf.AsSpan(0, n)) : $"error: {LibUsb.StrError(n)}";
                    if (name == "config" && n > OffDialTarget)
                        blocks["dial"] = buf[OffDialTarget] switch
                        {
                            DialGain => "gain",
                            DialHeadphones => "headphones",
                            DialMonitorMix => "monitor mix",
                            byte other => $"unknown ({other})",
                        };
                }
                catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
            }
            blocks["gain"] = _gainDb is int g
                ? $"{g} dB, last read with the dial on gain"
                : "not read with the dial on gain yet; the word at 0 is reported as is";
        }
        return blocks;
    }
}
