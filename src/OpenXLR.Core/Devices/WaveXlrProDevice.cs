namespace OpenXLR.Core.Devices;

/// <summary>
/// Wave XLR Pro (0fd9:00b4) hardware control over the vendor block protocol.
///
/// Transport (decoded from a Wave Link USB capture, verified on hardware):
///   read : bmRequestType=0xC1, bRequest=0x01, wValue=block, wIndex=0x0103
///   write: bmRequestType=0x41, bRequest=0x01, wValue=block, wIndex=0x0103, data
/// Each block is a fixed-size property bank; individual controls are byte fields
/// at fixed offsets. Writes are read-modify-write of the whole block. Needs the
/// udev rule granting access to 0fd9:00b4 (MODE 0660 / uaccess).
///
/// Each input's 38-byte struct in block 0x0004 carries the four-band EQ the
/// unit runs on its own DSP, in the MK.2 family's layout (<see cref="WaveFxEq"/>):
/// XLR 1's switch at byte 2 bit 0 and bands at 18 to 37, XLR 2's at byte 40
/// and 56 to 75. Wave Link 3.5 writes it on firmware 1.0.14 as a whole block
/// per change with no commit, and the change reads back on the next poll
/// (captures 16 to 20 of 2026-10-21). Block 0x0000 (117 bytes) holds the
/// firmware version at bytes 5 to 7, read once at connect; the EQ is driven
/// from 1.0.14.
/// </summary>
public sealed class WaveXlrProDevice : IAudioDevice, IDisposable
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B4;

    public DeviceInfo Info { get; } = UsbTransport.WithLocation(new("Elgato", "Wave XLR Pro", VendorId, ProductId));

    /// <summary>The model's controls, with <see cref="DeviceCapabilities.HardwareEq"/> set at connect.</summary>
    public DeviceCapabilities Capabilities { get; private set; }

    /// <summary>The version block 0x0000 reports, as major.minor.patch; null until read.</summary>
    public string? Firmware { get; private set; }

    /// <summary>Whether this unit runs the EQ, decided at connect by its firmware and its block.</summary>
    public DeviceEqSupport? EqSupport { get; private set; }

    /// <summary>Said once at connect when the EQ fields do not decode.</summary>
    public string? ConnectionNote { get; private set; }

    private static readonly DeviceCapabilities ModelCapabilities = new()
    {
        Gain = true,
        PhysicalControls = true, Mute = true, LowCut = true, Expander = true, VoiceTune = true,
        HpVolume = true, LowImpedance = true,
        Phantom = true, ClipGuard = true, Compressor = true,
        OutputRouting = true, AuxInput = true, LineInput = true, LineInGainMaxDb = WaveXlrProDevice.LineInGainMaxDb,
        XlrInputs = 2, HpOutputs = 2,
        BuiltInDefaults = true,
        Ducking = true,
    };

    private const ushort VIndex = 0x0103;
    private const ushort BlockIdentity = 0x0000; // firmware version at bytes 5 to 7
    private const int IdentityLen = 117;
    private const byte VReq = 0x01;
    private const byte RtRead = 0xC1;
    private const byte RtWrite = 0x41;

    // Block numbers (wValue) and their lengths.
    private const ushort BlockCrossfade = 0x0001;
    private const int CrossfadeLen = 108;
    private const ushort BlockCommit = 0x0003;   // write-only; Wave Link sends it after config writes
    private const int CommitLen = 12;
    private const ushort BlockSettings = 0x0004;
    private const int SettingsLen = 80;
    private const ushort BlockHp = 0x0005;
    private const int HpLen = 8;

    // Field offsets within the blocks. Block 0x0004 carries one 38-byte
    // structure per XLR input (verified bidirectionally against ALSA: off0
    // tracks the front pair, off38 the rear pair); XLR 2 fields are the same
    // offsets shifted by Xlr2Base. The 4-byte tail starts with the Line In
    // gain at off76, the centre pair of the ALSA capture volume (measured on
    // the desk unit, firmware 1.0.14); bytes 77 to 79 are left as read except
    // by the USB Aux level and the two level locks in byte 77.
    private const int Xlr2Base = 38;
    private const int GainOffset = 0;      // block 0x0004
    private const int FlagsOffset = 1;     // block 0x0004 / 0x0005
    private const int ClipGuardOffset = 2; // block 0x0004 (per XLR struct; 0x04 set = DISABLED)
    private const int VoiceTuneStrengthOffset = 10; // block 0x0004
    private const int LineInGainOffset = 76;        // block 0x0004 tail, dB = byte (unit takes 0..80, written 0..40)
    private const int AuxLevelLockOffset = 77;      // block 0x0004 tail (0x04 = locked)
    private const byte AuxLevelLockMask = 0x04;
    // Line In's level lock, in the same byte (desk measurement, 2026-10-26):
    // while it is set the unit ignores Line In gain writes through its ALSA
    // control, and still takes the ones through this block.
    private const int LineInLevelLockOffset = 77;
    private const byte LineInLevelLockMask = 0x08;
    private const int AuxLevelOffset = 79;          // block 0x0004 tail, dB = -byte/4 (0..-60)
    private const int HpVolOffset = 0;     // block 0x0005 (headphones 1)
    private const int Hp2VolOffset = 2;    // block 0x0005 (headphones 2)

    // Physical output MIX ASSIGNMENT in block 0x0001 (captures 2-4 + live jack
    // test): one byte per output, holding a hardware-mix id. 0x1e = the
    // Personal/monitor mix (feeds the analog outputs; carries USB return pair
    // 2/3 among others), 0x20 = the aux mix (Wave Link's user-created
    // "MacBook Mix"), 0x23 = unassigned. A BlockCommit write must follow.
    private const int OutHp1Offset = 90;
    private const int OutHp2Offset = 91;
    private const int OutUsbAuxOffset = 92;
    private const int OutLineOutOffset = 93;
    private const byte OutSourceMonitor = 0x1e;
    private const byte OutSourceAuxMix = 0x20;
    private const byte OutSourceOff = 0x23;
    // Headphone-mix membership (section 0 of the block, the mix the jacks
    // play; see the protocol notes, section 12): byte 12 bit 5 = USB return
    // pair 2/3 in the jacks' mix, byte 12 bit 0 = XLR 1, the microphone's
    // zero-latency path. Both measured on the desk unit (2026-10-30) through
    // the mix's capture stream.
    private const int HpMixReturnsOffset = 12;
    private const byte HpMixMonitorReturnMask = 0x20;
    private const byte HpMixMicDirectMask = 0x01;
    // XLR 1's level in the jacks' mix, int8 in half dB like every level cell
    // (desk, 2026-10-30: 00 played the mic at the return's level, f4 6 dB
    // under it, c8 28 dB under). OpenXLR used to drive this byte as a Mic/PC
    // crossfade from 0 to 200, which put the centre at the cell's +12 dB cap.
    // A level write applies at once, with no commit and no stream restart.
    private const int HpMixMicLevelOffset = 0;
    private const int HpMixMicLevelFloor = -120;  // 88, -60 dB: Wave Link's floor
    private const int HpMixMicLevelCeiling = 24;  // 18, +12 dB: the firmware holds anything above
    // Pair 2/3's level in the jacks' mix, int8 in half dB, 0x00 = 0 dB
    // (measured +6.2 dB at 0x0c, -6.1 dB at 0xf4). Wave Link keeps its own
    // fader level here, so a unit that dual-boots plays the Monitor mix that
    // far down until the cell is put back to unity.
    private const int HpMixMonitorReturnLevelOffset = 5;
    // Line In's membership in the same mix: byte 12 bit 3, by the section
    // layout (source 3). A unit set up elsewhere can hold it set, which plays
    // the Line In jack straight to the jacks beside the Monitor mix that
    // already carries the Line In channel (an owner's archive: byte 12 `e8`).
    private const byte HpMixLineInMask = 0x08;
    // Byte 13 bit 1 is USB playback pair 5 in the same mix: the Aux mix's
    // stream on AUX10 and AUX11 (desk, 2026-10-30, a tone on that pair came
    // and went with the bit, and byte 9 set its level). OpenXLR used to set it
    // as the microphone's direct path, which played the whole Aux mix on the
    // jacks beside the Monitor mix, and played nothing where Wave Link had
    // left byte 9 at its floor. The jacks hear the Aux mix through Monitor A
    // only, so the bit is cleared with the Monitor return, as Line In's is.
    private const int HpMixPairsOffset = 13;
    private const byte HpMixAuxMixMask = 0x02;
    // The other USB playback pairs in the same mix: pair k is source 4 + k,
    // so pairs 0, 2 and 3 are byte 12 bits 4, 6 and 7, and pairs 4 to 7 are
    // byte 13 bits 0 to 3, beside the Aux mix's pair 5. Each measured on the
    // desk unit (2026-10-31): a tone on the pair reached the mix's capture
    // with its bit set and not with it clear, at its level cell (bytes 4 to
    // 11, all 00 there). That unit held pairs 6 and 7 set and an owner's held
    // pair 0, so anything played to those channels of the Pro's sink reached
    // the jacks with no control in OpenXLR. Nothing of OpenXLR's plays there:
    // the jacks hear the Monitor mix on pair 1 only, so the other pairs are
    // cleared with the Monitor return too. Bit 2, the USB Aux input (desk,
    // 2026-10-31, a Mac on the port reached the mix at 0 dB), stays as read:
    // an owner can hear a second computer there with Aux In's send muted.
    private const byte HpMixReturnsOtherPairsMask = 0xd0;
    // XLR 2's membership, byte 12 bit 1 (source 1), at the level in byte 1
    // (desk, 2026-10-31, a microphone on XLR 2: absent with the bit clear,
    // its capture at 0 dB with it set, 6 dB down at f4). OpenXLR gives XLR 2
    // no direct path, so a unit that holds the bit would play it on the jacks
    // beside its Monitor send. It is cleared with the return, as Line In's is.
    private const byte HpMixXlr2Mask = 0x02;
    private const byte HpMixOtherPairsMask = HpMixAuxMixMask | 0x0d;

    // The aux mix's matrix cell for the Music return channel (USB playback
    // pair 10/11), decoded from capture 4 where music was confirmed flowing to
    // the aux port: level byte (quarter-dB attenuation, 0 = 0 dB) and its
    // membership bit. OpenXLR streams the monitor mix on that pair when the
    // aux output is enabled.
    private const int AuxMixMusicLevelOffset = 45;
    private const int AuxMixMusicMemberOffset = 49;
    private const byte AuxMixMusicMemberMask = 0x02;
    // Hardware-direct input cells in the aux mix (off48 bit0 = mic, bit3 =
    // line-in). Cleared when OpenXLR drives the port: all audio then travels
    // through the software Aux mix, so nothing arrives twice.
    private const int AuxMixInputMemberOffset = 48;
    private const byte AuxMixInputMemberMask = 0x09;

    // Flag masks in block 0x0004 offset 1 (all confirmed via the logged
    // capture 2 and, for mute, bidirectionally against ALSA).
    private const byte MuteMask = 0x01;       // bit0
    private const byte PhantomMask = 0x02;    // bit1
    private const byte LowCutMask = 0x10;     // bit4
    private const byte ExpanderMask = 0x20;   // bit5
    private const byte VoiceTuneMask = 0x40;  // bit6
    private const byte CompressorMask = 0x80; // bit7
    // Bits 2 and 3 exist but are unlabeled (bit3 reads as set on this unit).

    // ClipGuard lives in the struct's offset-2 byte, INVERTED: 0x04 = disabled.
    private const byte ClipGuardOffMask = 0x04;

    // Flag mask in block 0x0005 offset 1.
    private const byte LowZMask = 0x02;      // bit1

    public const int GainMaxDb = 80;
    // The Line In takes 0 to 80 dB like the XLR inputs, but it hisses above
    // about 20 dB on an owner's unit, so OpenXLR writes no more than 40.
    public const int LineInGainMaxDb = 40;
    public const int VoiceTuneStrengthMax = 100;

    private readonly IUsbTransport _usb;
    private readonly object _lock = new();

    // The settings block as the last poll or EQ write left it, which ReadEq
    // decodes without a transfer.
    private byte[]? _settings;

    public WaveXlrProDevice() : this(UsbTransport.Create()) { }

    internal WaveXlrProDevice(IUsbTransport usb)
    {
        _usb = usb;
        Capabilities = ModelCapabilities;
    }

    public bool Connected => _usb.IsOpen;

    public void Connect()
    {
        lock (_lock)
        {
            ForgetUnitLocked();
            if (!_usb.Open(VendorId, ProductId))
                throw new InvalidOperationException(
                    "Wave XLR Pro not found or no permission (install the udev rule for 0fd9:00b4).");
            try { ReadEqSupport(); }
            catch
            {
                _usb.Close();
                ForgetUnitLocked();
                throw;
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _usb.Close();
            ForgetUnitLocked();
        }
    }

    // What one connection learnt about its unit; a reconnect learns it again,
    // since a firmware update re-enumerates the unit.
    private void ForgetUnitLocked()
    {
        Firmware = null;
        EqSupport = null;
        ConnectionNote = null;
        Capabilities = ModelCapabilities;
        _settings = null;
    }

    /// <summary>
    /// The firmware gate: block 0x0000 once, then the settings block when the
    /// version is 1.0.14 or later, whose EQ fields must decode in both input
    /// structs. Anything else leaves both inputs' EQ to software and says
    /// why. A unit that stalls or answers short here is not refused: only its
    /// EQ runs in software.
    /// </summary>
    private void ReadEqSupport()
    {
        var identity = new byte[IdentityLen];
        int n = Transfer(RtRead, VReq, BlockIdentity, identity, IdentityLen);
        Version? version = n > 0 ? WaveFxEq.FirmwareVersion(identity.AsSpan(0, n)) : null;
        Firmware = version is null ? null : WaveFxEq.Format(version);
        byte[]? block = null;
        EqSupport = WaveFxEq.Gate(Info.Model, version, WaveFxEq.ProEqFirmware, () =>
        {
            block = new byte[SettingsLen];
            return Transfer(RtRead, VReq, BlockSettings, block, SettingsLen) == SettingsLen
                && WaveFxEq.Decodes(EqOf(block, 1)) && WaveFxEq.Decodes(EqOf(block, 2));
        });
        if (EqSupport.Ready) _settings = block;
        else if (EqSupport.Reason == DeviceEqSupport.UnsupportedBlock) ConnectionNote = WaveFxEq.UnsupportedBlockNote;
        Capabilities = ModelCapabilities with { HardwareEq = EqSupport.Ready };
    }

    private static DeviceEq? EqOf(byte[] settings, int input)
        => WaveFxEq.Decode(settings.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength));

    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the transport has dropped the
    /// device by then (the helper process is killed), Connected turns false
    /// and the daemon reconnects with a fresh one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
        => _usb.ControlTransfer(requestType, request, value, VIndex, data, (ushort)length, 1000);

    private byte[] Read(ushort block, int length)
    {
        var buf = new byte[length];
        lock (_lock)
        {
            int n = Transfer(RtRead, VReq, block, buf, length);
            if (n < 0) throw new IOException($"vendor read block {block:X4} failed: {LibUsb.StrError(n)}");
            if (n != length)
                throw new IOException($"vendor read block {block:X4} returned {n} bytes; expected {length}");
        }
        return buf;
    }

    private void Write(ushort block, byte[] data)
    {
        lock (_lock)
        {
            int n = Transfer(RtWrite, VReq, block, data, data.Length);
            if (n < 0) throw new IOException($"vendor write block {block:X4} failed: {LibUsb.StrError(n)}");
            if (n != data.Length)
                throw new IOException($"vendor write block {block:X4} accepted {n} bytes; expected {data.Length}");
        }
    }

    /// <summary>
    /// Write a config block then send the BlockCommit that Wave Link sends
    /// after every change. The output selectors verifiably require it; other
    /// fields apply without it, but committing matches the reference behavior.
    /// </summary>
    private void WriteCommitted(ushort block, byte[] data)
    {
        Write(block, data);
        var commit = new byte[CommitLen];
        commit[0] = 0x04;
        Write(BlockCommit, commit);
    }

    /// <summary>Read every control field into one snapshot.</summary>
    public DeviceState ReadState()
    {
        byte[] b1 = Read(BlockCrossfade, CrossfadeLen);
        byte[] b4 = Read(BlockSettings, SettingsLen);
        lock (_lock) _settings = b4;
        byte[] b5 = Read(BlockHp, HpLen);
        // A unit that refuses the ducking block still answers every other control.
        try { _ducking = ProDucking.Decode(Read(ProDucking.Block, ProDucking.Length), b1); }
        catch (IOException) { _ducking = null; }
        byte f = b4[FlagsOffset];
        byte f2 = b4[Xlr2Base + FlagsOffset];
        return new DeviceState
        {
            GainDb = b4[GainOffset],
            Mute = (f & MuteMask) != 0,
            LowCut = (f & LowCutMask) != 0,
            Expander = (f & ExpanderMask) != 0,
            VoiceTune = (f & VoiceTuneMask) != 0,
            VoiceTuneStrength = b4[VoiceTuneStrengthOffset],
            HpVolumeDb = -b5[HpVolOffset] / 4.0,
            Hp2VolumeDb = -b5[Hp2VolOffset] / 4.0,
            LowImpedance = (b5[FlagsOffset] & LowZMask) != 0,
            Phantom = (f & PhantomMask) != 0,
            ClipGuard = (b4[ClipGuardOffset] & ClipGuardOffMask) == 0,
            Compressor = (f & CompressorMask) != 0,
            Gain2Db = b4[Xlr2Base + GainOffset],
            Mute2 = (f2 & MuteMask) != 0,
            LowCut2 = (f2 & LowCutMask) != 0,
            Expander2 = (f2 & ExpanderMask) != 0,
            VoiceTune2 = (f2 & VoiceTuneMask) != 0,
            VoiceTuneStrength2 = b4[Xlr2Base + VoiceTuneStrengthOffset],
            Phantom2 = (f2 & PhantomMask) != 0,
            ClipGuard2 = (b4[Xlr2Base + ClipGuardOffset] & ClipGuardOffMask) == 0,
            Compressor2 = (f2 & CompressorMask) != 0,
            OutHp1 = b1[OutHp1Offset] != OutSourceOff,
            OutHp2 = b1[OutHp2Offset] != OutSourceOff,
            OutUsbAux = b1[OutUsbAuxOffset] != OutSourceOff,
            OutLineOut = b1[OutLineOutOffset] != OutSourceOff,
            HpMixMonitorReturn = (b1[HpMixReturnsOffset] & (HpMixMonitorReturnMask | HpMixXlr2Mask | HpMixLineInMask | HpMixReturnsOtherPairsMask)) == HpMixMonitorReturnMask &&
                                 (b1[HpMixPairsOffset] & HpMixOtherPairsMask) == 0 &&
                                 b1[HpMixMonitorReturnLevelOffset] == 0x00,
            HpMixMicDirect = (b1[HpMixReturnsOffset] & HpMixMicDirectMask) != 0,
            HpMixMicLevelDb = Math.Min((int)(sbyte)b1[HpMixMicLevelOffset], HpMixMicLevelCeiling) / 2.0,
            AuxReturnEnabled = b1[AuxMixMusicLevelOffset] == 0x00 &&
                               (b1[AuxMixMusicMemberOffset] & AuxMixMusicMemberMask) != 0,
            AuxLevelDb = -b4[AuxLevelOffset] / 4.0,
            AuxLevelLock = (b4[AuxLevelLockOffset] & AuxLevelLockMask) != 0,
            LineInGainDb = b4[LineInGainOffset],
            LineInLevelLock = (b4[LineInLevelLockOffset] & LineInLevelLockMask) != 0,
        };
    }

    // --- setters (read-modify-write a single field) ---

    public void SetGainDb(int db)
    {
        db = Math.Clamp(db, 0, GainMaxDb);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[GainOffset] = (byte)db;
        Write(BlockSettings, b);
    }

    public void SetVoiceTuneStrength(int value)
    {
        value = Math.Clamp(value, 0, VoiceTuneStrengthMax);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[VoiceTuneStrengthOffset] = (byte)value;
        Write(BlockSettings, b);
    }

    // The Pro has no crossfade to offer: the byte once driven as one is XLR
    // 1's level in the jacks' mix, which follows the Monitor send (see
    // SetHpMixMicLevelDb), and the capabilities leave the control out.
    public void SetCrossfade(int value) { }

    /// <summary>Set HP volume in dB; the hardware byte is -4*dB (0 = loudest).</summary>
    public void SetHpVolumeDb(double db)
    {
        int byte0 = Math.Clamp((int)Math.Round(-db * 4.0), 0, 240);
        byte[] b = Read(BlockHp, HpLen);
        b[HpVolOffset] = (byte)byte0;
        Write(BlockHp, b);
    }

    /// <summary>Second headphone output volume; same -4*dB byte encoding.</summary>
    public void SetHp2VolumeDb(double db)
    {
        int byte0 = Math.Clamp((int)Math.Round(-db * 4.0), 0, 240);
        byte[] b = Read(BlockHp, HpLen);
        b[Hp2VolOffset] = (byte)byte0;
        Write(BlockHp, b);
    }

    public void SetGain2Db(int db)
    {
        db = Math.Clamp(db, 0, GainMaxDb);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[Xlr2Base + GainOffset] = (byte)db;
        Write(BlockSettings, b);
    }

    public void SetVoiceTuneStrength2(int value)
    {
        value = Math.Clamp(value, 0, VoiceTuneStrengthMax);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[Xlr2Base + VoiceTuneStrengthOffset] = (byte)value;
        Write(BlockSettings, b);
    }

    public void SetMute2(bool on) => SetSettingsBit2(MuteMask, on);
    public void SetLowCut2(bool on) => SetSettingsBit2(LowCutMask, on);
    public void SetExpander2(bool on) => SetSettingsBit2(ExpanderMask, on);
    public void SetVoiceTune2(bool on) => SetSettingsBit2(VoiceTuneMask, on);
    public void SetPhantom2(bool on) => SetSettingsBit2(PhantomMask, on);
    public void SetCompressor2(bool on) => SetSettingsBit2(CompressorMask, on);
    public void SetClipGuard2(bool on) => SetClipGuardByte(Xlr2Base + ClipGuardOffset, on);

    private void SetSettingsBit2(byte mask, bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        int off = Xlr2Base + FlagsOffset;
        b[off] = (byte)(on ? b[off] | mask : b[off] & ~mask);
        Write(BlockSettings, b);
    }

    public void SetMute(bool on) => SetSettingsBit(MuteMask, on);
    public void SetLowCut(bool on) => SetSettingsBit(LowCutMask, on);
    public void SetExpander(bool on) => SetSettingsBit(ExpanderMask, on);
    public void SetVoiceTune(bool on) => SetSettingsBit(VoiceTuneMask, on);
    public void SetPhantom(bool on) => SetSettingsBit(PhantomMask, on);
    public void SetCompressor(bool on) => SetSettingsBit(CompressorMask, on);
    public void SetClipGuard(bool on) => SetClipGuardByte(ClipGuardOffset, on);

    /// <summary>ClipGuard's byte is inverted: 0x04 set = disabled.</summary>
    private void SetClipGuardByte(int offset, bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        b[offset] = (byte)(on ? b[offset] & ~ClipGuardOffMask : b[offset] | ClipGuardOffMask);
        WriteCommitted(BlockSettings, b);
    }

    // --- physical output routing (block 0x0001 off90..93 + commit) ---

    public void SetOutHp1(bool on) => SetOutputSelector(OutHp1Offset, on);
    public void SetOutHp2(bool on) => SetOutputSelector(OutHp2Offset, on);
    public void SetOutLineOut(bool on) => SetOutputSelector(OutLineOutOffset, on);

    /// <summary>
    /// The USB Aux port cannot tap the Personal mix (verified: no USB playback
    /// pair reaches it under 0x1e); it works assigned to the aux mix with the
    /// Music return cell open, exactly how Wave Link delivers audio there.
    /// </summary>
    public void SetOutUsbAux(bool on)
    {
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[OutUsbAuxOffset] = on ? OutSourceAuxMix : OutSourceOff;
        if (on)
        {
            b[AuxMixMusicLevelOffset] = 0x00;                 // 0 dB
            b[AuxMixMusicMemberOffset] |= AuxMixMusicMemberMask;
            b[AuxMixInputMemberOffset] &= unchecked((byte)~AuxMixInputMemberMask);
        }
        WriteCommitted(BlockCrossfade, b);
    }

    private void SetOutputSelector(int offset, bool on)
    {
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[offset] = on ? OutSourceMonitor : OutSourceOff;
        WriteCommitted(BlockCrossfade, b);
    }

    // The headphone mix latches membership changes when the host playback
    // stream (re)starts, like the aux return; the caller bounces the sink.
    // Turning the return on also opens its level cell, as the aux return does,
    // and takes XLR 2, the Line In jack and every other USB playback pair, the
    // Aux mix's stream among them, out of the mix: the jacks then hear those
    // only through the Monitor mix, as the aux mix does once
    // OpenXLR drives its port. Turning it off leaves those bits as they are.
    public void SetHpMixMonitorReturn(bool on)
    {
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        if (on)
        {
            b[HpMixReturnsOffset] = (byte)((b[HpMixReturnsOffset] | HpMixMonitorReturnMask) & ~(HpMixXlr2Mask | HpMixLineInMask | HpMixReturnsOtherPairsMask));
            b[HpMixPairsOffset] &= unchecked((byte)~HpMixOtherPairsMask);
            b[HpMixMonitorReturnLevelOffset] = 0x00;          // 0 dB
        }
        else b[HpMixReturnsOffset] &= unchecked((byte)~HpMixMonitorReturnMask);
        WriteCommitted(BlockCrossfade, b);
    }
    public void SetHpMixMicDirect(bool on) => SetBlock1Bit(HpMixReturnsOffset, HpMixMicDirectMask, on);

    /// <summary>XLR 1's level in the jacks' mix, held to -60 to +12 dB in half-dB steps.</summary>
    public void SetHpMixMicLevelDb(double db)
    {
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[HpMixMicLevelOffset] = HpMixMicLevelByte(db);
        Write(BlockCrossfade, b);
    }

    internal static byte HpMixMicLevelByte(double db)
        => unchecked((byte)(sbyte)(double.IsFinite(db)
            ? Math.Clamp((int)Math.Round(db * 2, MidpointRounding.AwayFromZero), HpMixMicLevelFloor, HpMixMicLevelCeiling)
            : HpMixMicLevelFloor));

    private void SetBlock1Bit(int offset, byte mask, bool on)
    {
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[offset] = (byte)(on ? b[offset] | mask : b[offset] & ~mask);
        WriteCommitted(BlockCrossfade, b);
    }

    // --- USB Aux input (block 0x0004 tail) ---

    /// <summary>Aux input level in dB (-60..0); hardware byte is -4*dB.</summary>
    public void SetAuxLevelDb(double db)
    {
        int v = Math.Clamp((int)Math.Round(-db * 4.0), 0, 240);
        byte[] b = Read(BlockSettings, SettingsLen);
        b[AuxLevelOffset] = (byte)v;
        WriteCommitted(BlockSettings, b);
    }

    public void SetAuxLevelLock(bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        b[AuxLevelLockOffset] = (byte)(on ? b[AuxLevelLockOffset] | AuxLevelLockMask : b[AuxLevelLockOffset] & ~AuxLevelLockMask);
        WriteCommitted(BlockSettings, b);
    }

    // --- Line In (block 0x0004 tail) ---

    /// <summary>
    /// The analog Line In's gain in dB, held to 0 to
    /// <see cref="LineInGainMaxDb"/>, the first byte of the settings block's
    /// tail, as the XLR gains are written: one read and one write of the
    /// whole block, no commit, under the lock so no other write lands
    /// between them. Every other byte goes back as read. Returns the value
    /// written.
    /// </summary>
    public int SetLineInGainDb(int db)
    {
        db = Math.Clamp(db, 0, LineInGainMaxDb);
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen);
            b[LineInGainOffset] = (byte)db;
            Write(BlockSettings, b);
        }
        return db;
    }

    /// <summary>
    /// Line In's level lock, block 0x0004 byte 77 bit 3. While it is set the
    /// unit ignores Line In gain writes through its ALSA control, so the
    /// desktop mixer and other applications cannot move the gain;
    /// <see cref="SetLineInGainDb"/> writes this block and still applies. It
    /// is written as the gain is: one read and one write of the whole block,
    /// no commit, under the lock, every other byte as read, the USB Aux lock
    /// beside it included.
    /// </summary>
    public void SetLineInLevelLock(bool on)
    {
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen);
            b[LineInLevelLockOffset] = (byte)(on ? b[LineInLevelLockOffset] | LineInLevelLockMask
                                                 : b[LineInLevelLockOffset] & ~LineInLevelLockMask);
            Write(BlockSettings, b);
        }
    }

    public void SetLowImpedance(bool on)
    {
        byte[] b = Read(BlockHp, HpLen);
        b[FlagsOffset] = (byte)(on ? b[FlagsOffset] | LowZMask : b[FlagsOffset] & ~LowZMask);
        Write(BlockHp, b);
    }

    private void SetSettingsBit(byte mask, bool on)
    {
        byte[] b = Read(BlockSettings, SettingsLen);
        b[FlagsOffset] = (byte)(on ? b[FlagsOffset] | mask : b[FlagsOffset] & ~mask);
        Write(BlockSettings, b);
    }

    // --- the microphone EQ on the unit's DSP (block 0x0004, one struct per input) ---

    /// <summary>
    /// Write one input's EQ into its struct through a read of the settings
    /// block and one write of the whole block, under the lock every other
    /// setter's read-modify-write takes. No commit: Wave Link sends none for
    /// EQ writes, and they read back on the next poll. Every byte the EQ does
    /// not own, the other input's struct and the aux tail included, goes back
    /// as read. False when the unit already held it and nothing was written.
    /// </summary>
    public bool SetEq(int input, DeviceEq eq)
    {
        if (input is not (1 or 2) || EqSupport?.Ready != true)
            throw new InvalidOperationException($"{Info.Model}: this unit does not run the EQ on input {input}");
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen);
            byte[] held = (byte[])b.Clone();
            WaveFxEq.Encode(b.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength), eq);
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
            return input is 1 or 2 && EqSupport?.Ready == true && _settings is { Length: SettingsLen } s ? EqOf(s, input) : null;
    }

    // --- ducking (blocks 0x0008 and 0x0001, see ProDucking) ---

    private volatile DuckingReadback? _ducking;

    public DuckingReadback? ReadDucking() => _ducking;

    /// <summary>
    /// Bring the unit's ducking to a target: both blocks read fresh, the
    /// fields ProDucking owns edited, and each block written back only when
    /// it changed, 0x0008 first as Wave Link does. No commit: Wave Link sends
    /// none for ducking and the unit keeps the bits without one.
    /// </summary>
    public DuckingWrite SetDucking(OpenXLR.Core.Mixing.ProDuckingTarget target)
    {
        // Under the lock, as SetEq's read-modify-write is, so no other write lands between the reads and the writes.
        lock (_lock)
        {
            byte[] b8 = Read(ProDucking.Block, ProDucking.Length);
            byte[] b1 = Read(BlockCrossfade, CrossfadeLen);
            byte[] new8 = (byte[])b8.Clone(), new1 = (byte[])b1.Clone();
            ProDucking.Apply(new8, new1, target);
            bool wrote8 = !new8.AsSpan().SequenceEqual(b8), wrote1 = !new1.AsSpan().SequenceEqual(b1);
            if (wrote8) Write(ProDucking.Block, new8);
            if (wrote1) Write(BlockCrossfade, new1);
            _ducking = ProDucking.Decode(new8, new1);
            return new DuckingWrite(wrote8, ProDucking.Diff(b8, new8), wrote1, ProDucking.Diff(b1, new1));
        }
    }

    /// <summary>Every readable vendor block as hex, for tester diagnostics, with the firmware and each input's EQ decoded.</summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((ushort block, int len) in new (ushort, int)[]
                 { (BlockIdentity, IdentityLen), (0x0001, 108), (0x0002, 150), (0x0004, 80), (0x0005, 8), (0x0006, 29), (0x0008, 96) })
        {
            try
            {
                byte[] read = Read(block, len);
                blocks[$"0x{block:X4}"] = Convert.ToHexString(read);
                if (block == BlockSettings)
                    for (int input = 1; input <= 2; input++)
                        blocks[$"eq{input}"] = EqOf(read, input) is { } eq
                            ? eq + (WaveFxEq.Decodes(eq) ? "" : " (outside the EQ's ranges)")
                            : "too short to hold the EQ";
            }
            catch (IOException ex) { blocks[$"0x{block:X4}"] = $"read failed: {ex.Message}"; }
        }
        blocks["firmware"] = Firmware ?? "not read";
        blocks["eqSupport"] = EqSupport is { } support ? support.Ready ? "on the unit" : $"in software: {support.Reason}" : "not decided";
        return blocks;
    }

    public void Dispose() => _usb.Dispose();
}
