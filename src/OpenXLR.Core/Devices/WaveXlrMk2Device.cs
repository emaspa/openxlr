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
/// descriptor layout (five interfaces, vendor interface 3 whose alternate
/// setting 0 has no endpoints; we never leave that setting)
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
    // How much of each block ReadState and the setters index (the highest
    // offset used, plus one); a shorter answer is refused rather than decoded.
    private const int CrossfadeUsed = 1;
    private const int SettingsUsed = 11;
    private const int HpUsed = 2;

    private const int ClipGuardOffset = 2;
    private const byte ClipGuardOffMask = 0x04;   // set = ClipGuard disabled

    private const byte MuteMask = 0x01;
    private const byte PhantomMask = 0x02;
    private const byte LowCutMask = 0x10;
    private const byte ExpanderMask = 0x20;
    private const byte VoiceTuneMask = 0x40;
    private const byte CompressorMask = 0x80;
    private const byte LowZMask = 0x02;

    private readonly object _lock = new();

    // A dock can expose either known bank. Selection is read-only and lasts
    // for one connection; no failed write ever switches a live device's bank.
    private readonly ushort _preferredVIndex;
    private readonly ushort? _alternateVIndex;
    private ushort _vIndex;
    public string? ConnectionNote { get; private set; }

    public DeviceInfo Info { get; }

    public DeviceCapabilities Capabilities { get; }

    public WaveXlrMk2Device() : this(ProductId, "Wave XLR MK.2", physicalControls: true, vIndex: 0x0203) { }

    /// <param name="model">Must match the device's iProduct string minus the
    /// vendor, since the daemon derives the PipeWire node-name hint from it.</param>
    /// <param name="vIndex">wIndex of the vendor transfers, see <see cref="_vIndex"/>.</param>
    protected WaveXlrMk2Device(ushort productId, string model, bool physicalControls, ushort vIndex, bool retainsSettings = true,
        ushort? alternateVIndex = null, IUsbTransport? usb = null)
    {
        _preferredVIndex = _vIndex = vIndex;
        _alternateVIndex = alternateVIndex;
        _usb = usb ?? UsbTransport.Create();
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

    private readonly IUsbTransport _usb;

    public bool Connected => _usb.IsOpen;

    /// <summary>Release the USB transport and its helper process.</summary>
    public void Dispose() => _usb.Dispose();

    public void Connect()
    {
        lock (_lock)
        {
            ConnectionNote = null;
            _vIndex = _preferredVIndex;
            if (!_usb.Open(VendorId, Info.ProductId))
                throw new InvalidOperationException($"{Info.Model} present but could not be opened (udev rule?)");
            try
            {
                if (_alternateVIndex is not ushort alternate) return;
                if (ProbeBank()) return;
                _vIndex = alternate;
                if (ProbeBank())
                {
                    ConnectionNote = $"USB control bank 0x{alternate:x4} selected: 0x{_preferredVIndex:x4} did not answer the expected blocks.";
                    return;
                }
                // Neither bank answered the three blocks the way both known
                // variants do. Stay open on the preferred bank rather than
                // refuse the device: the ordinary reads then report what the
                // firmware really does and diagnostics can dump the blocks,
                // which is what a third variant needs to be understood.
                _vIndex = _preferredVIndex;
                ConnectionNote = $"neither USB control bank 0x{_preferredVIndex:x4} nor 0x{alternate:x4} answered the expected settings, headphones and crossfade blocks; using 0x{_preferredVIndex:x4}. Collect diagnostics for a device report.";
            }
            catch
            {
                _usb.Close();
                _vIndex = _preferredVIndex;
                throw;
            }
        }
    }

    // A successful settings read alone is not enough to authorize writes, so
    // all three blocks have to answer in full. A filled read is evidence that
    // the bank is the right one, not proof: a longer block truncated to the
    // requested length answers the same way. Stalls and short answers move on
    // to the alternative, while disconnects and timeouts fail normally.
    private bool ProbeBank()
    {
        foreach ((ushort block, int length) in new[]
            { (BlockSettings, SettingsLen), (BlockHp, HpLen), (BlockCrossfade, CrossfadeLen) })
        {
            int count = TransferWithRetry(RtRead, VReq, block, new byte[length], length);
            if (count == -9) return false; // LIBUSB_ERROR_PIPE: this bank is unsupported.
            if (count < 0) throw new InvalidOperationException($"probe bank 0x{_vIndex:x4}, block {block:x4}: {LibUsb.StrError(count)}");
            if (count != length) return false;
        }
        return true;
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _usb.Close();
            _vIndex = _preferredVIndex;
            ConnectionNote = null;
        }
    }

    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the transport has dropped the
    /// device by then (the helper process is killed), Connected turns false
    /// and the daemon reconnects with a fresh one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
        => _usb.ControlTransfer(requestType, request, value, _vIndex, data, (ushort)length, 1000);

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

    /// <param name="used">The least length the caller indexes; a device
    /// answering less is refused here instead of failing at the index.</param>
    private byte[] Read(ushort block, int length, int used)
    {
        var buf = new byte[length];
        lock (_lock)
        {
            int n = TransferWithRetry(RtRead, VReq, block, buf, length);
            if (n < 0) throw new InvalidOperationException($"read block {block:x4}: {LibUsb.StrError(n)}");
            if (n < used) throw new InvalidOperationException($"read block {block:x4}: {n} bytes, at least {used} expected");
            // The block lengths come from a Wave Link capture; a short read
            // beyond the offsets in use is tolerated so a firmware that
            // answers less still gets decoded, and DumpBlocks shows the real
            // length in diagnostics.
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

    private void Modify(ushort block, int length, int used, Action<byte[]> edit)
    {
        byte[] b = Read(block, length, used);
        edit(b);
        Write(block, b);
    }

    public DeviceState ReadState()
    {
        byte[] s = Read(BlockSettings, SettingsLen, SettingsUsed);
        byte[] hp = Read(BlockHp, HpLen, HpUsed);
        byte[] xf = Read(BlockCrossfade, CrossfadeLen, CrossfadeUsed);
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
        => Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[1] = on ? (byte)(b[1] | mask) : (byte)(b[1] & ~mask));

    public void SetGainDb(int db)
        => Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[0] = (byte)Math.Clamp(db, 0, 80));

    public void SetMute(bool on) => Flag(MuteMask, on);
    public void SetLowCut(bool on) => Flag(LowCutMask, on);
    public void SetExpander(bool on) => Flag(ExpanderMask, on);
    public void SetVoiceTune(bool on) => Flag(VoiceTuneMask, on);

    public void SetVoiceTuneStrength(int value)
        => Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[10] = (byte)Math.Clamp(value, 0, 100));

    public void SetHpVolumeDb(double db)
        => Modify(BlockHp, HpLen, HpUsed, b => b[0] = (byte)Math.Clamp((int)Math.Round(-db * 4), 0, 240));

    public void SetLowImpedance(bool on)
        => Modify(BlockHp, HpLen, HpUsed, b => b[1] = on ? (byte)(b[1] | LowZMask) : (byte)(b[1] & ~LowZMask));

    public void SetCrossfade(int value)
        => Modify(BlockCrossfade, CrossfadeLen, CrossfadeUsed, b => b[0] = (byte)Math.Clamp(value, 0, 200));

    public void SetPhantom(bool on) => Flag(PhantomMask, on);
    public void SetCompressor(bool on) => Flag(CompressorMask, on);

    public void SetClipGuard(bool on)
        => Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[ClipGuardOffset] = on
            ? (byte)(b[ClipGuardOffset] & ~ClipGuardOffMask)
            : (byte)(b[ClipGuardOffset] | ClipGuardOffMask));

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((string name, ushort block, int len) in new[]
                 { ("settings", BlockSettings, SettingsLen), ("hp", BlockHp, HpLen),
                   ("crossfade", BlockCrossfade, CrossfadeLen) })
        {
            try { blocks[name] = Convert.ToHexString(Read(block, len, used: 0)); }
            catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
        }
        return blocks;
    }
}
