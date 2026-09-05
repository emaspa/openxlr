namespace OpenXLR.Core.Devices;

/// <summary>
/// The Wave XLR MK.2 (0fd9:00b6): the vendor block protocol the Pro inherited,
/// at wIndex 0x0203 with single-input blocks.
///
///   read : bmRequestType=0xC1, bRequest=0x01, wValue=block, wIndex=0x0203
///   write: bmRequestType=0x41, bRequest=0x01, wValue=block, wIndex=0x0203
///
/// Block 0x0004 (38 bytes) is one input struct, the same layout as each of
/// the Pro's two: gain dB at 0 (0..80), a flag byte at 1 (bit0 mute, bit1
/// phantom, bit4 low cut, bit5 expander, bit6 voice tune, bit7 compressor),
/// ClipGuard at 2 (0x04 set = disabled, inverted like the Pro), voice tune
/// strength at 10. Block 0x0005 (2 bytes): headphone attenuation at 0
/// (quarter-dB steps, 0 = loudest, 240 = -60 dB) and bit1 of byte 1 = low
/// impedance. Block 0x0001 (6 bytes): crossfade at 0 (0..200, 100 = centre).
/// Decoded from a Wave Link USB capture during the Pro reverse engineering;
/// no commit block is needed. Gain, mute, low cut, expander, voice tune,
/// headphone volume, low impedance and crossfade were verified on hardware
/// by a community tester (issue #2, 0.1.10); phantom, ClipGuard and
/// compressor follow the Pro's bit positions, which the tester's block dump
/// matched, and await the same verification.
///
/// The XLR Dock MK.2 (<see cref="XlrDockMk2Device"/>) presents the same USB
/// descriptor layout (five interfaces, vendor interface 3 without endpoints)
/// and is driven through this class at its own product id.
/// </summary>
public class WaveXlrMk2Device : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B6;

    private const byte RtRead = 0xC1;
    private const byte RtWrite = 0x41;
    private const byte VReq = 0x01;

    private const ushort BlockCrossfade = 0x0001;
    private const ushort BlockSettings = 0x0004;
    private const ushort BlockHp = 0x0005;
    private const int CrossfadeLen = 6;
    private const int SettingsLen = 38;
    private const int HpLen = 2;

    private const int ClipGuardOffset = 2;
    private const byte ClipGuardOffMask = 0x04;   // set = ClipGuard disabled

    private const byte MuteMask = 0x01;
    private const byte PhantomMask = 0x02;
    private const byte LowCutMask = 0x10;
    private const byte ExpanderMask = 0x20;
    private const byte VoiceTuneMask = 0x40;
    private const byte CompressorMask = 0x80;
    private const byte LowZMask = 0x02;

    private static IntPtr _ctx = IntPtr.Zero;
    private IntPtr _handle = IntPtr.Zero;
    private readonly object _lock = new();

    /// <summary>
    /// wIndex of every vendor transfer: the low byte is the vendor interface
    /// (3 on both), the high byte selects the block bank the firmware exposes
    /// there, 0x02 on the Wave XLR MK.2 and 0x01 on the XLR Dock MK.2 (which
    /// stalls 0x0203 and answers 0x0103 like the Pro).
    /// </summary>
    private readonly ushort _vIndex;

    public DeviceInfo Info { get; }

    public DeviceCapabilities Capabilities { get; }

    public WaveXlrMk2Device() : this(ProductId, "Wave XLR MK.2", physicalControls: true, vIndex: 0x0203) { }

    /// <param name="model">Must match the device's iProduct string minus the
    /// vendor, since the daemon derives the PipeWire node-name hint from it.</param>
    /// <param name="vIndex">wIndex of the vendor transfers, see <see cref="_vIndex"/>.</param>
    protected WaveXlrMk2Device(ushort productId, string model, bool physicalControls, ushort vIndex, bool retainsSettings = true)
    {
        _vIndex = vIndex;
        Info = new DeviceInfo("Elgato", model, VendorId, productId);
        Capabilities = new DeviceCapabilities
        {
            Gain = true,
            PhysicalControls = physicalControls,
            RetainsSettings = retainsSettings,
            Mute = true,
            LowCut = true,
            Expander = true,
            VoiceTune = true,
            HpVolume = true,
            LowImpedance = true,
            Crossfade = true,
            Phantom = true,
            ClipGuard = true,
            Compressor = true,
            XlrInputs = 1,
            HpOutputs = 1,
        };
    }

    public bool Connected => _handle != IntPtr.Zero;

    public void Connect()
    {
        if (_ctx == IntPtr.Zero)
        {
            int rc = LibUsb.libusb_init(out _ctx);
            if (rc != 0) throw new InvalidOperationException($"libusb_init failed: {LibUsb.StrError(rc)}");
        }
        _handle = LibUsb.libusb_open_device_with_vid_pid(_ctx, VendorId, Info.ProductId);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"{Info.Model} present but could not be opened (udev rule?)");
    }

    public void Disconnect()
    {
        if (_handle != IntPtr.Zero) { LibUsb.libusb_close(_handle); _handle = IntPtr.Zero; }
    }


    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the handle is then abandoned
    /// without libusb_close, since the stuck native call may still use it,
    /// and Connected turns false so the daemon reconnects with a new one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
    {
        try { return LibUsb.ControlTransfer(_handle, requestType, request, value, _vIndex, data, (ushort)length, 1000); }
        catch (UsbHungException) { _handle = IntPtr.Zero; throw; }
    }

    // libusb's LIBUSB_ERROR_IO. The XLR Dock MK.2 returns it on roughly one
    // block read in several hundred while its audio interface is streaming
    // (measured on hardware, never when idle); the next transfer succeeds,
    // so one immediate retry keeps the daemon from dropping and reopening
    // the device every half minute.
    private const int ErrorIo = -1;

    private int TransferWithRetry(byte requestType, byte request, ushort value, byte[] data, int length)
    {
        int n = Transfer(requestType, request, value, data, length);
        if (n == ErrorIo) n = Transfer(requestType, request, value, data, length);
        return n;
    }

    private byte[] Read(ushort block, int length)
    {
        var buf = new byte[length];
        lock (_lock)
        {
            int n = TransferWithRetry(RtRead, VReq, block, buf, length);
            if (n < 0) throw new InvalidOperationException($"read block {block:x4}: {LibUsb.StrError(n)}");
            // The block lengths come from a Wave Link capture; a short read is
            // tolerated so a firmware that answers less still gets decoded
            // (ReadState only indexes the low offsets) and DumpBlocks shows
            // the real length in diagnostics.
            if (n != length) Array.Resize(ref buf, n);
        }
        return buf;
    }

    private void Write(ushort block, byte[] data)
    {
        lock (_lock)
        {
            int n = TransferWithRetry(RtWrite, VReq, block, data, data.Length);
            if (n < 0) throw new InvalidOperationException($"write block {block:x4}: {LibUsb.StrError(n)}");
            if (n != data.Length)
                throw new InvalidOperationException($"write block {block:x4}: accepted {n} bytes, expected {data.Length}");
        }
    }

    private void Modify(ushort block, int length, Action<byte[]> edit)
    {
        byte[] b = Read(block, length);
        edit(b);
        Write(block, b);
    }

    public DeviceState ReadState()
    {
        byte[] s = Read(BlockSettings, SettingsLen);
        byte[] hp = Read(BlockHp, HpLen);
        byte[] xf = Read(BlockCrossfade, CrossfadeLen);
        return new DeviceState
        {
            GainDb = s[0],
            Mute = (s[1] & MuteMask) != 0,
            LowCut = (s[1] & LowCutMask) != 0,
            Expander = (s[1] & ExpanderMask) != 0,
            VoiceTune = (s[1] & VoiceTuneMask) != 0,
            Phantom = (s[1] & PhantomMask) != 0,
            Compressor = (s[1] & CompressorMask) != 0,
            ClipGuard = (s[ClipGuardOffset] & ClipGuardOffMask) == 0,
            VoiceTuneStrength = s[10],
            HpVolumeDb = -hp[0] / 4.0,
            LowImpedance = (hp[1] & LowZMask) != 0,
            Crossfade = xf[0],
        };
    }

    private void Flag(byte mask, bool on)
        => Modify(BlockSettings, SettingsLen, b => b[1] = on ? (byte)(b[1] | mask) : (byte)(b[1] & ~mask));

    public void SetGainDb(int db)
        => Modify(BlockSettings, SettingsLen, b => b[0] = (byte)Math.Clamp(db, 0, 80));

    public void SetMute(bool on) => Flag(MuteMask, on);
    public void SetLowCut(bool on) => Flag(LowCutMask, on);
    public void SetExpander(bool on) => Flag(ExpanderMask, on);
    public void SetVoiceTune(bool on) => Flag(VoiceTuneMask, on);

    public void SetVoiceTuneStrength(int value)
        => Modify(BlockSettings, SettingsLen, b => b[10] = (byte)Math.Clamp(value, 0, 100));

    public void SetHpVolumeDb(double db)
        => Modify(BlockHp, HpLen, b => b[0] = (byte)Math.Clamp((int)Math.Round(-db * 4), 0, 240));

    public void SetLowImpedance(bool on)
        => Modify(BlockHp, HpLen, b => b[1] = on ? (byte)(b[1] | LowZMask) : (byte)(b[1] & ~LowZMask));

    public void SetCrossfade(int value)
        => Modify(BlockCrossfade, CrossfadeLen, b => b[0] = (byte)Math.Clamp(value, 0, 200));

    public void SetPhantom(bool on) => Flag(PhantomMask, on);
    public void SetCompressor(bool on) => Flag(CompressorMask, on);

    public void SetClipGuard(bool on)
        => Modify(BlockSettings, SettingsLen, b => b[ClipGuardOffset] = on
            ? (byte)(b[ClipGuardOffset] & ~ClipGuardOffMask)
            : (byte)(b[ClipGuardOffset] | ClipGuardOffMask));

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((string name, ushort block, int len) in new[]
                 { ("settings", BlockSettings, SettingsLen), ("hp", BlockHp, HpLen),
                   ("crossfade", BlockCrossfade, CrossfadeLen) })
        {
            try { blocks[name] = Convert.ToHexString(Read(block, len)); }
            catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
        }
        return blocks;
    }
}
