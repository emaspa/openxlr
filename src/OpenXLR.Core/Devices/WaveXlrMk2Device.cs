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
/// ClipGuard at 2 (set = disabled: 0x04 before firmware 1.0.15, 0x10 from
/// it, where 0x04 is the unit's gain lock, which OpenXLR's gain lock holds),
/// voice tune strength at 10. Block 0x0005 (2 bytes): headphone attenuation at 0
/// (quarter-dB steps, 0 = loudest, 240 = -60 dB) and bit1 of byte 1 = low
/// impedance. Block 0x0001 (6 bytes): crossfade at 0 (0..200, 100 = centre).
/// Decoded from a Wave Link USB capture during the Pro reverse engineering;
/// no commit block is needed. Gain, mute, low cut, expander, voice tune,
/// headphone volume, low impedance and crossfade were verified on hardware
/// by a community tester (issue #2, 0.1.10); phantom, ClipGuard and
/// compressor follow the Pro's bit positions, which the tester's block dump
/// matched, and were confirmed by the same tester in 0.1.12.
///
/// From firmware 1.0.15 the same block carries a four-band EQ the unit runs
/// on its own DSP: a switch at byte 2 bit 0 and five bytes per band from
/// byte 18 (<see cref="WaveFxEq"/>). Wave Link 3.5 writes the whole block per
/// change with no commit. Its curves match the RBJ cookbook OpenXLR's
/// software EQ uses, measured on the XLR Dock MK.2 within about 1 dB for
/// every type. Block 0x0000 (117 bytes) holds the firmware version at bytes 5
/// to 7, read once at connect; the EQ is driven only from 1.0.15, the version
/// both models were measured on.
///
/// The same struct holds the settings of the unit's compressor and expander
/// at bytes 3 to 17 (<see cref="WaveFxDynamics"/>), driven behind the EQ's
/// gate on every model of this class. The layout is verified on the Wave:3
/// MK.2; the Wave XLR MK.2 and the XLR Dock MK.2 hold the same fields with
/// the same factory values, and are driven the same way unchecked by ear.
///
/// On the Wave XLR MK.2 from firmware 1.0.15 the poll also reads the status
/// block 0x0006 (<see cref="Mk2Status"/>) for whether a microphone and
/// headphones are plugged in, and the device manager reads the meter block
/// 0x0002 (<see cref="Mk2Meters"/>) for the compressor's and expander's
/// levels while a client watches them. Both layouts were measured on the
/// desk on 2026-11-10. The XLR Dock MK.2 and the Wave:3 MK.2 answer blocks
/// of the same lengths, which have not been checked on them, so neither
/// block is read there.
///
/// Block 0x0016 (61 bytes on 1.0.15) holds the LED ring: the ring's and the
/// level meter's brightness and the ring's colour in each of the dial's
/// modes (<see cref="WaveFxLed"/>), measured on a Wave XLR MK.2 on 1.0.15.
/// Only that model is driven there; the XLR Dock MK.2 and the Wave:3 MK.2
/// have not been measured and are left alone.
///
/// The XLR Dock MK.2 (<see cref="XlrDockMk2Device"/>) presents the same USB
/// descriptor layout (five interfaces, vendor interface 3 whose alternate
/// setting 0 has no endpoints; we never leave that setting)
/// and is driven through this class at its own product id.
///
/// The Wave:3 MK.2 (<see cref="Wave3Mk2Device"/>) is a USB microphone on the
/// same platform and the same layout. It hands this class a narrower set of
/// controls and an EQ gate in its own firmware numbering. A control the
/// model does not advertise is left out of the state and refused by its
/// setter, so no bit this class cannot name on that model is ever written.
/// </summary>
public class WaveXlrMk2Device : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B6;

    private const byte RtRead = 0xC1;
    private const byte RtWrite = 0x41;
    private const byte VReq = 0x01;

    private const ushort BlockIdentity = 0x0000;
    private const int IdentityLen = 117;
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

    // Byte 2 of the settings block. Before firmware 1.0.15, 0x04 set is
    // ClipGuard off, as a tester's Wave Link dump read and the dock's
    // ClipGuard sounded on 2026-09-05. From 1.0.15 Wave Link writes 0x10 for
    // ClipGuard off and 0x04 for the unit's gain lock, which refuses gain
    // set through the USB volume control (captured on a Wave XLR MK.2 on
    // 1.0.15, 2026-11-10); 0x08 is the limiter's other mode bit and is
    // never left set.
    private const int ClipGuardOffset = 2;
    private const byte ClipGuardOffBefore1015 = 0x04;
    private const byte LimiterOffMask = 0x10;
    private const byte LimiterModeMask = 0x18;
    private const byte GainLockMask = 0x04;

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

    /// <summary>The model's controls, with <see cref="DeviceCapabilities.HardwareEq"/> set at connect.</summary>
    public DeviceCapabilities Capabilities { get; private set; }
    private readonly DeviceCapabilities _modelCapabilities;

    // The first firmware the model runs the EQ on, null on a model that does
    // not run it at all. Such a model never asks the gate, so it carries no
    // EQ reason and no updater advice.
    private readonly Version? _eqFirmware;
    // The widest band gain in dB the unit's EQ is written, either way.
    private readonly double _eqMaxGain;

    // Whether the model's crossfade byte is the mirror of OpenXLR's crossfade.
    private readonly bool _crossfadeReversed;

    // The flag byte's low cut bit, which differs between models.
    private readonly byte _lowCutMask;

    // The first firmware whose byte 2 holds the limiter mode at 0x18 and the
    // gain lock at 0x04; null on a model whose ClipGuard bit has not been
    // read from Wave Link on its newer firmware, which keeps 0x04.
    private readonly Version? _limiterModeFirmware;

    // Whether this unit's firmware is the limiter-mode layout, decided at connect.
    private bool _limiterMode;

    // The first firmware whose meter and status blocks are read; null on a
    // model whose blocks have not been checked.
    private readonly Version? _statusFirmware;

    // Whether this unit's meter and status blocks are read, decided at connect.
    private bool _statusBlocks;

    // The first firmware the model's LED block is driven on, null on a model
    // whose block has not been measured, where it is never read or written.
    private readonly Version? _ledFirmware;

    // The LED block as the connect or the last LED write left it, which
    // ReadState decodes without a transfer; null on a unit without LEDs
    // OpenXLR sets. Nothing but OpenXLR changes it while the unit is driven.
    private byte[]? _led;

    // Whether OpenXLR holds the unit's gain lock, set by SetUnitGainLock
    // after each connect; a ClipGuard write keeps byte 2 bit 0x04 to it.
    private bool _gainLockHeld;

    /// <summary>The version block 0x0000 reports, as major.minor.patch; null until read.</summary>
    public string? Firmware { get; private set; }

    /// <summary>Whether this unit runs the EQ, decided at connect by its firmware and its block.</summary>
    public DeviceEqSupport? EqSupport { get; private set; }

    /// <summary>Whether this unit runs the compressor and expander, decided at connect with the EQ.</summary>
    public DeviceEqSupport? DynamicsSupport { get; private set; }

    // The settings block as the last read or write left it, which ReadEq
    // and ReadDynamics decode without a transfer.
    private byte[]? _settings;

    public WaveXlrMk2Device() : this(ProductId, "Wave XLR MK.2", physicalControls: true, vIndex: 0x0203,
        statusFirmware: StatusFirmware, ledFirmware: WaveFxLed.Mk2Firmware, eqMaxGain: WaveFxEq.MeasuredMaxGainDb) { }

    internal WaveXlrMk2Device(IUsbTransport usb) : this(ProductId, "Wave XLR MK.2", physicalControls: true, vIndex: 0x0203, usb: usb,
        statusFirmware: StatusFirmware, ledFirmware: WaveFxLed.Mk2Firmware, eqMaxGain: WaveFxEq.MeasuredMaxGainDb) { }

    /// <param name="model">Must match the device's iProduct string minus the
    /// vendor, since the daemon derives the PipeWire node-name hint from it.</param>
    /// <param name="vIndex">wIndex of the vendor transfers, see <see cref="_vIndex"/>.</param>
    /// <param name="statusFirmware">The first firmware on which the unit's
    /// meter and status blocks (0x0002 and 0x0006) are read; null on a model
    /// whose blocks have not been checked, which reads neither.</param>
    /// <param name="ledFirmware">The first firmware on which the model's LED
    /// block is driven; null to leave the block alone.</param>
    /// <param name="eqMaxGain">See the full constructor.</param>
    protected WaveXlrMk2Device(ushort productId, string model, bool physicalControls, ushort vIndex, bool retainsSettings = true,
        ushort? alternateVIndex = null, IUsbTransport? usb = null, Version? statusFirmware = null, Version? ledFirmware = null,
        double eqMaxGain = WaveFxEq.DefaultMaxGainDb)
        : this(productId, model, vIndex, Mk2Capabilities(physicalControls, retainsSettings), WaveFxEq.Mk2EqFirmware, alternateVIndex, usb,
            limiterModeFirmware: LimiterModeFirmware, statusFirmware: statusFirmware, ledFirmware: ledFirmware, eqMaxGain: eqMaxGain) { }

    /// <summary>The Wave XLR MK.2's and the XLR Dock MK.2's first firmware with the limiter mode and the gain lock in byte 2.</summary>
    public static Version LimiterModeFirmware { get; } = new(1, 0, 15);

    /// <summary>The Wave XLR MK.2's first firmware whose meter and status blocks are read: the version they were measured on.</summary>
    public static Version StatusFirmware { get; } = new(1, 0, 15);

    /// <param name="capabilities">The controls the model has; each one this
    /// class can drive and the model lacks is left out of the state and
    /// refused by its setter. Its <see cref="DeviceCapabilities.GainMaxDb"/>
    /// is the highest gain written.</param>
    /// <param name="eqFirmware">The first firmware, in the model's own
    /// numbering, on which the unit runs the EQ on its DSP; null for a model
    /// without one, whose <see cref="EqSupport"/> stays null.</param>
    /// <param name="crossfadeReversed">The model's crossfade byte runs from
    /// the computer at 0 to the microphone at 200, the mirror of OpenXLR's
    /// crossfade and of the Wave XLR MK.2's byte.</param>
    /// <param name="lowCutMask">The flag byte's low cut bit on the model:
    /// 0x10 on the Wave XLR MK.2 and the XLR Dock MK.2, 0x02 on the Wave:3
    /// MK.2, where 0x02 is phantom power on the others.</param>
    /// <param name="limiterModeFirmware">The first firmware on which byte 2
    /// carries ClipGuard at 0x10 and the gain lock at 0x04; null to keep
    /// 0x04 as ClipGuard on every firmware.</param>
    /// <param name="ledFirmware">The first firmware on which the model's LED
    /// block 0x0016 is driven (<see cref="WaveFxLed"/>); null to leave it alone.</param>
    /// <param name="eqMaxGain">The widest band gain, in dB either way, the
    /// unit's EQ is written: <see cref="WaveFxEq.MeasuredMaxGainDb"/> on a
    /// model measured to it, <see cref="WaveFxEq.DefaultMaxGainDb"/> otherwise.</param>
    protected WaveXlrMk2Device(ushort productId, string model, ushort vIndex, DeviceCapabilities capabilities, Version? eqFirmware,
        ushort? alternateVIndex = null, IUsbTransport? usb = null, bool crossfadeReversed = false, byte lowCutMask = LowCutMask,
        Version? limiterModeFirmware = null, Version? statusFirmware = null, Version? ledFirmware = null,
        double eqMaxGain = WaveFxEq.DefaultMaxGainDb)
    {
        _eqMaxGain = eqMaxGain;
        _limiterModeFirmware = limiterModeFirmware;
        _statusFirmware = statusFirmware;
        _ledFirmware = ledFirmware;
        _preferredVIndex = _vIndex = vIndex;
        _alternateVIndex = alternateVIndex;
        _usb = usb ?? UsbTransport.Create();
        _eqFirmware = eqFirmware;
        _crossfadeReversed = crossfadeReversed;
        _lowCutMask = lowCutMask;
        Info = UsbTransport.WithLocation(new DeviceInfo("Elgato", model, VendorId, productId));
        Capabilities = _modelCapabilities = capabilities with { HardwareEq = false, HardwareGainLock = false, LedRing = false };
    }

    private static DeviceCapabilities Mk2Capabilities(bool physicalControls, bool retainsSettings) => new()
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

    private readonly IUsbTransport _usb;

    public bool Connected => _usb.IsOpen;

    /// <summary>Release the USB transport and its helper process.</summary>
    public void Dispose() => _usb.Dispose();

    public void Connect()
    {
        lock (_lock)
        {
            ForgetUnitLocked();
            if (!_usb.Open(VendorId, Info.ProductId))
                throw new InvalidOperationException($"{Info.Model} present but could not be opened (udev rule?)");
            try
            {
                bool answered = SelectBank();
                Version? version = ReadEqSupport(answered);
                ReadLedSupport(version);
            }
            catch
            {
                _usb.Close();
                ForgetUnitLocked();
                throw;
            }
        }
    }

    // What one connection learnt about its unit; a reconnect learns it again,
    // since a firmware update re-enumerates the unit.
    private void ForgetUnitLocked()
    {
        ConnectionNote = null;
        Firmware = null;
        _limiterMode = false;
        _statusBlocks = false;
        _gainLockHeld = false;
        EqSupport = null;
        DynamicsSupport = null;
        Capabilities = _modelCapabilities;
        _settings = null;
        _led = null;
        _vIndex = _preferredVIndex;
    }

    /// <summary>The bank the unit answers on; false when neither known bank answered.</summary>
    private bool SelectBank()
    {
        if (_alternateVIndex is not ushort alternate) return true;
        if (ProbeBank()) return true;
        _vIndex = alternate;
        if (ProbeBank())
        {
            ConnectionNote = $"USB control bank 0x{alternate:x4} selected: 0x{_preferredVIndex:x4} did not answer the expected blocks.";
            return true;
        }
        // Neither bank answered the three blocks the way both known
        // variants do. Stay open on the preferred bank rather than
        // refuse the device: the ordinary reads then report what the
        // firmware really does and diagnostics can dump the blocks,
        // which is what a third variant needs to be understood.
        _vIndex = _preferredVIndex;
        ConnectionNote = $"neither USB control bank 0x{_preferredVIndex:x4} nor 0x{alternate:x4} answered the expected settings, headphones and crossfade blocks; using 0x{_preferredVIndex:x4}. Collect diagnostics for a device report.";
        return false;
    }

    /// <summary>
    /// The firmware gate: block 0x0000 once, then the settings block when the
    /// version is the model's first EQ firmware or later (1.0.15 on the Wave
    /// XLR MK.2 and the XLR Dock MK.2), whose EQ fields must decode. Anything else
    /// leaves the EQ to software and says why. On a bank that did not answer,
    /// nothing is read. A unit that stalls or answers short here is not
    /// refused: only its EQ runs in software. A model without the EQ reads
    /// the version alone and has no EQ support to report.
    /// </summary>
    private Version? ReadEqSupport(bool bankAnswered)
    {
        Version? version = null;
        if (bankAnswered)
        {
            var identity = new byte[IdentityLen];
            int n = TransferWithRetry(RtRead, VReq, BlockIdentity, identity, IdentityLen);
            if (n > 0) version = WaveFxEq.FirmwareVersion(identity.AsSpan(0, n));
        }
        Firmware = version is null ? null : WaveFxEq.Format(version);
        // An unread version keeps the older meaning, as the EQ stays in software.
        _limiterMode = version is not null && _limiterModeFirmware is not null && version >= _limiterModeFirmware;
        _statusBlocks = version is not null && _statusFirmware is not null && version >= _statusFirmware;
        // The gain lock bit exists only in the limiter-mode layout; below it
        // 0x04 is ClipGuard off and is never written for the lock.
        Capabilities = _modelCapabilities with { HardwareGainLock = _limiterMode };
        if (_eqFirmware is null) return version;
        byte[]? block = null;
        EqSupport = WaveFxEq.Gate(Info.Model, version, _eqFirmware, () =>
        {
            block = Read(BlockSettings, SettingsLen, used: 0);
            return block.Length >= WaveFxEq.StructLength && WaveFxEq.Decodes(WaveFxEq.Decode(block));
        }) with { MaxGain = _eqMaxGain };
        if (EqSupport.Ready) _settings = block;
        else if (EqSupport.Reason == DeviceEqSupport.UnsupportedBlock)
            ConnectionNote = ConnectionNote is null ? WaveFxEq.UnsupportedBlockNote : $"{ConnectionNote} Also, {WaveFxEq.UnsupportedBlockNote}";
        // The compressor and expander live in the same struct, behind the
        // same gate, on a model that has both switches.
        if (_modelCapabilities is { Compressor: true, Expander: true })
            DynamicsSupport = WaveFxDynamics.Gate(Info.Model, EqSupport, () => WaveFxDynamics.Decodes(WaveFxDynamics.Decode(block)));
        Capabilities = _modelCapabilities with
        {
            HardwareEq = EqSupport.Ready,
            HardwareDynamics = DynamicsSupport?.Ready == true,
            HardwareGainLock = _limiterMode,
            DynamicsMeters = _statusBlocks && DynamicsSupport?.Ready == true,
        };
        return version;
    }

    /// <summary>
    /// The LED gate, after the firmware is read: on a model whose LED block
    /// was measured, a version at least the measured one and a read of the
    /// block that answers in full. Anything else leaves the LEDs alone, and
    /// a unit that stalls or answers short here is not refused.
    /// </summary>
    private void ReadLedSupport(Version? version)
    {
        if (_ledFirmware is null || version is null || version < _ledFirmware) return;
        var block = new byte[WaveFxLed.Length];
        if (TransferWithRetry(RtRead, VReq, WaveFxLed.Block, block, WaveFxLed.Length) != WaveFxLed.Length) return;
        _led = block;
        Capabilities = Capabilities with { LedRing = true };
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
            ForgetUnitLocked();
        }
    }

    /// <summary>
    /// The USB reset, for a model that recovers its capture this way
    /// (<see cref="DeviceCapabilities.CaptureRecovery"/>). What the unit
    /// held is forgotten: it reconnects as after a replug.
    /// </summary>
    public int ResetUsb()
    {
        if (!Capabilities.CaptureRecovery) throw new InvalidOperationException($"{Info.Model} is not reset on USB");
        lock (_lock)
        {
            ForgetUnitLocked();
            return _usb.Reset();
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
        lock (_lock) _settings = s;
        byte[] hp = Read(BlockHp, HpLen, HpUsed);
        byte[] xf = Read(BlockCrossfade, CrossfadeLen, CrossfadeUsed);
        Mk2StatusReading? status = ReadStatus();
        // A control the model lacks reads as off: on the Wave:3 MK.2 the
        // flag byte carries bits whose meaning is not known there.
        DeviceCapabilities c = _modelCapabilities;
        return new DeviceState
        {
            GainDb = s[0],
            Mute = c.Mute && (s[1] & MuteMask) != 0,
            LowCut = c.LowCut && (s[1] & _lowCutMask) != 0,
            Expander = c.Expander && (s[1] & ExpanderMask) != 0,
            VoiceTune = c.VoiceTune && (s[1] & VoiceTuneMask) != 0,
            Phantom = c.Phantom && (s[1] & PhantomMask) != 0,
            Compressor = c.Compressor && (s[1] & CompressorMask) != 0,
            ClipGuard = c.ClipGuard && (s[ClipGuardOffset] & (_limiterMode ? LimiterOffMask : ClipGuardOffBefore1015)) == 0,
            UnitGainLock = _limiterMode ? (s[ClipGuardOffset] & GainLockMask) != 0 : null,
            VoiceTuneStrength = c.VoiceTune ? s[10] : 0,
            HpVolumeDb = -hp[0] / 4.0,
            LowImpedance = c.LowImpedance && (hp[1] & LowZMask) != 0,
            Crossfade = CrossfadeByte(xf[0]),
            MicConnected = status?.MicConnected,
            HeadphonesConnected = status?.HeadphonesConnected,
            Led = LedLocked(),
        };
    }

    /// <summary>
    /// The status block, on a unit whose blocks are read; null on any other
    /// and when the read fails or answers short, so the poll still reports
    /// every control. A transfer that hangs still throws.
    /// </summary>
    private Mk2StatusReading? ReadStatus()
    {
        if (!_statusBlocks) return null;
        try { return Mk2Status.Decode(Read(Mk2Status.Block, Mk2Status.Length, used: 0)); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Block 0x0002, the unit's meters, read once and decoded, on a unit with
    /// <see cref="DeviceCapabilities.DynamicsMeters"/>. Nothing polls it
    /// here: the device manager reads it only while a client watches the
    /// compressor's and expander's levels. A failed read gives null; a hung
    /// one throws.
    /// </summary>
    public Mk2MeterReading? ReadInputMeters()
    {
        if (!Capabilities.DynamicsMeters) return null;
        try { return Mk2Meters.Decode(Read(Mk2Meters.Block, Mk2Meters.Length, used: 0)); }
        catch (InvalidOperationException) { return null; }
    }


    private DeviceLed? LedLocked()
    {
        lock (_lock) return _led is { } led ? WaveFxLed.Decode(led) : null;
    }

    /// <summary>Refuse a control the model does not advertise before anything is read or written.</summary>
    private void Require(bool supported, string control)
    {
        if (!supported) throw new InvalidOperationException($"{Info.Model} has no {control} this driver can set");
    }

    private void Flag(bool supported, string control, byte mask, bool on)
    {
        Require(supported, control);
        Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[1] = on ? (byte)(b[1] | mask) : (byte)(b[1] & ~mask));
    }

    public void SetGainDb(int db)
    {
        Require(_modelCapabilities.Gain, "gain");
        Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[0] = (byte)Math.Clamp(db, 0, _modelCapabilities.GainMaxDb));
    }

    public void SetMute(bool on) => Flag(_modelCapabilities.Mute, "mute", MuteMask, on);
    public void SetLowCut(bool on) => Flag(_modelCapabilities.LowCut, "low cut", _lowCutMask, on);
    public void SetExpander(bool on) => Flag(_modelCapabilities.Expander, "expander", ExpanderMask, on);
    public void SetVoiceTune(bool on) => Flag(_modelCapabilities.VoiceTune, "voice tune", VoiceTuneMask, on);

    public void SetVoiceTuneStrength(int value)
    {
        Require(_modelCapabilities.VoiceTune, "voice tune");
        Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[10] = (byte)Math.Clamp(value, 0, 100));
    }

    public void SetHpVolumeDb(double db)
    {
        Require(_modelCapabilities.HpVolume, "headphone volume");
        Modify(BlockHp, HpLen, HpUsed, b => b[0] = (byte)Math.Clamp((int)Math.Round(-db * 4), 0, 240));
    }

    public void SetLowImpedance(bool on)
    {
        Require(_modelCapabilities.LowImpedance, "low impedance mode");
        Modify(BlockHp, HpLen, HpUsed, b => b[1] = on ? (byte)(b[1] | LowZMask) : (byte)(b[1] & ~LowZMask));
    }

    public void SetCrossfade(int value)
    {
        Require(_modelCapabilities.Crossfade, "crossfade");
        Modify(BlockCrossfade, CrossfadeLen, CrossfadeUsed, b => b[0] = (byte)CrossfadeByte(value));
    }

    /// <summary>
    /// OpenXLR's crossfade (0 the microphone alone, 200 the computer alone)
    /// as the model's byte, held to 0 to 200, and back: the same number on a
    /// model whose byte runs that way, its mirror on one whose byte does not.
    /// </summary>
    private int CrossfadeByte(int value)
    {
        int held = Math.Clamp(value, 0, 200);
        return _crossfadeReversed ? 200 - held : held;
    }

    public void SetPhantom(bool on) => Flag(_modelCapabilities.Phantom, "phantom power", PhantomMask, on);
    public void SetCompressor(bool on) => Flag(_modelCapabilities.Compressor, "compressor", CompressorMask, on);

    public void SetClipGuard(bool on)
    {
        Require(_modelCapabilities.ClipGuard, "ClipGuard");
        Modify(BlockSettings, SettingsLen, SettingsUsed, b => b[ClipGuardOffset] = ClipGuardByte(b[ClipGuardOffset], on));
    }

    /// <summary>
    /// Byte 2 with ClipGuard switched, as Wave Link writes it on the unit's
    /// firmware. From 1.0.15 the write also brings the gain lock to what
    /// OpenXLR holds: set while <see cref="SetUnitGainLock"/> holds it,
    /// cleared otherwise. Earlier OpenXLR releases wrote 0x04 there for
    /// ClipGuard off, which locked the gain against the desktop's volume
    /// control and left ClipGuard on.
    /// </summary>
    private byte ClipGuardByte(byte b, bool on)
    {
        if (!_limiterMode)
            return on ? (byte)(b & ~ClipGuardOffBefore1015) : (byte)(b | ClipGuardOffBefore1015);
        b = (byte)(b & ~(LimiterModeMask | GainLockMask));
        if (_gainLockHeld) b |= GainLockMask;
        return on ? b : (byte)(b | LimiterOffMask);
    }

    /// <summary>
    /// Hold or release the unit's own gain lock, byte 2 bit 0x04 of the
    /// settings block from firmware 1.0.15, as Wave Link writes it. While it
    /// is set the unit ignores gain written through its USB volume control,
    /// and still takes the gain written to byte 0 of the block (measured on a
    /// Wave XLR MK.2 on 1.0.15). One read and one write of the block, none
    /// when the unit already holds the bit; later ClipGuard writes keep it.
    /// False when nothing was written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The unit has no gain lock of its own (<see cref="DeviceCapabilities.HardwareGainLock"/>), or a transfer failed.</exception>
    public bool SetUnitGainLock(bool on)
    {
        if (!Capabilities.HardwareGainLock)
            throw new InvalidOperationException($"{Info.Model}: this unit has no gain lock OpenXLR can set");
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen, SettingsUsed);
            byte held = b[ClipGuardOffset];
            b[ClipGuardOffset] = on ? (byte)(held | GainLockMask) : (byte)(held & ~GainLockMask);
            if (b[ClipGuardOffset] != held) Write(BlockSettings, b);
            // Recorded once the unit has it, so a failed write leaves later
            // ClipGuard writes keeping the bit as it was.
            _gainLockHeld = on;
            _settings = b;
            return b[ClipGuardOffset] != held;
        }
    }

    /// <summary>
    /// Write the LEDs <paramref name="change"/> sets: one read of block
    /// 0x0016 and one write of the whole block with only those fields
    /// changed, none when the unit already holds them. The unit applies the
    /// write at once.
    /// </summary>
    public bool SetLed(DeviceLedChange change)
    {
        if (!Capabilities.LedRing)
            throw new InvalidOperationException($"{Info.Model}: this unit has no LEDs OpenXLR can set");
        lock (_lock)
        {
            byte[] b = Read(WaveFxLed.Block, WaveFxLed.Length, WaveFxLed.Length);
            byte[] held = (byte[])b.Clone();
            WaveFxLed.Encode(b, change);
            if (b.AsSpan().SequenceEqual(held))
            {
                _led = held;
                return false;
            }
            Write(WaveFxLed.Block, b);
            _led = b;
            return true;
        }
    }

    public bool SetEq(int input, DeviceEq eq)
    {
        if (input != 1 || EqSupport is not { Ready: true } support)
            throw new InvalidOperationException($"{Info.Model}: this unit does not run the EQ on input {input}");
        if (!WaveFxEq.FitsGain(eq, support.MaxGain))
            throw new ArgumentException($"{Info.Model}: a band gain is beyond the {support.MaxGain} dB this unit is written", nameof(eq));
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen, WaveFxEq.StructLength);
            byte[] held = (byte[])b.Clone();
            WaveFxEq.Encode(b, eq);
            if (b.AsSpan().SequenceEqual(held))
            {
                _settings = held;
                return false;
            }
            Write(BlockSettings, b);
            _settings = b;
            return true;
        }
    }

    public DeviceEq? ReadEq(int input)
    {
        lock (_lock)
            return input == 1 && EqSupport?.Ready == true && _settings is { Length: >= WaveFxEq.StructLength } s ? WaveFxEq.Decode(s) : null;
    }

    /// <summary>
    /// Write the compressor and expander into the settings block, as
    /// <see cref="SetEq"/> writes the EQ: one read, one write of the whole
    /// block with only their fields changed, no commit. False when the unit
    /// already held them and nothing was written.
    /// </summary>
    public bool SetDynamics(int input, DeviceDynamics dynamics)
    {
        if (input != 1 || DynamicsSupport?.Ready != true)
            throw new InvalidOperationException($"{Info.Model}: this unit does not run the compressor and expander on input {input}");
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen, WaveFxEq.StructLength);
            byte[] held = (byte[])b.Clone();
            WaveFxDynamics.Encode(b, dynamics);
            if (b.AsSpan().SequenceEqual(held))
            {
                _settings = held;
                return false;
            }
            Write(BlockSettings, b);
            _settings = b;
            return true;
        }
    }

    public DeviceDynamics? ReadDynamics(int input)
    {
        lock (_lock)
            return input == 1 && DynamicsSupport?.Ready == true && _settings is { Length: >= WaveFxEq.StructLength } s ? WaveFxDynamics.Decode(s) : null;
    }

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((string name, ushort block, int len) in new[]
                 { ("identity", BlockIdentity, IdentityLen), ("settings", BlockSettings, SettingsLen), ("hp", BlockHp, HpLen),
                   ("crossfade", BlockCrossfade, CrossfadeLen) }
                     .Concat(_ledFirmware is null ? [] : new[] { ("led", WaveFxLed.Block, WaveFxLed.Length) }))
        {
            try
            {
                byte[] read = Read(block, len, used: 0);
                blocks[name] = Convert.ToHexString(read);
                if (block == BlockSettings && _eqFirmware is not null)
                {
                    blocks["eq"] = WaveFxEq.Decode(read) is { } eq
                        ? eq + (WaveFxEq.Decodes(eq) ? "" : " (outside the EQ's ranges)")
                        : "too short to hold the EQ";
                    blocks["dynamics"] = WaveFxDynamics.Decode(read) is { } dynamics
                        ? dynamics + (WaveFxDynamics.Decodes(dynamics) ? "" : " (outside the unit's limits)")
                        : "too short to hold the compressor and expander";
                }
            }
            catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
        }
        if (_statusBlocks)
            foreach ((string name, ushort block, int len, Func<byte[], IReadOnlyDictionary<string, string>> describe) in
                     new (string, ushort, int, Func<byte[], IReadOnlyDictionary<string, string>>)[]
                     { ("meterBlock", Mk2Meters.Block, Mk2Meters.Length, b => Mk2Meters.Describe(b)),
                       ("statusBlock", Mk2Status.Block, Mk2Status.Length, b => Mk2Status.Describe(b)) })
            {
                try
                {
                    byte[] read = Read(block, len, used: 0);
                    blocks[name] = Convert.ToHexString(read);
                    foreach ((string key, string value) in describe(read)) blocks[key] = value;
                }
                catch (Exception ex) { blocks[name] = $"error: {ex.Message}"; }
            }
        blocks["firmware"] = Firmware ?? "not read";
        if (_ledFirmware is not null)
            blocks["ledSupport"] = Capabilities.LedRing ? "set by OpenXLR"
                : $"left alone: firmware {Firmware ?? "not read"}, driven from {WaveFxEq.Format(_ledFirmware)}";
        blocks["eqSupport"] = _eqFirmware is null ? "in software: the model has no hardware EQ"
            : EqSupport is { } support ? support.Ready ? "on the unit" : $"in software: {support.Reason}" : "not decided";
        if (DynamicsSupport is { } dynamicsSupport)
            blocks["dynamicsSupport"] = dynamicsSupport.Ready ? "on the unit" : $"in software: {dynamicsSupport.Reason}";
        return blocks;
    }
}
