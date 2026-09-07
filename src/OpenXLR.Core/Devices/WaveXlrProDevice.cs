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
/// </summary>
public sealed class WaveXlrProDevice : IAudioDevice, IDisposable
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00B4;

    public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true,
        PhysicalControls = true, Mute = true, LowCut = true, Expander = true, VoiceTune = true,
        HpVolume = true, LowImpedance = true, Crossfade = true,
        Phantom = true, ClipGuard = true, Compressor = true,
        OutputRouting = true, AuxInput = true,
        XlrInputs = 2, HpOutputs = 2,
        BuiltInDefaults = true,
    };

    private const ushort VIndex = 0x0103;
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
    // offsets shifted by Xlr2Base. The 4-byte tail (off76) is a third input
    // stage, not yet exposed.
    private const int Xlr2Base = 38;
    private const int GainOffset = 0;      // block 0x0004
    private const int FlagsOffset = 1;     // block 0x0004 / 0x0005
    private const int ClipGuardOffset = 2; // block 0x0004 (per XLR struct; 0x04 set = DISABLED)
    private const int VoiceTuneStrengthOffset = 10; // block 0x0004
    private const int AuxLevelLockOffset = 77;      // block 0x0004 tail (0x04 = locked)
    private const int AuxLevelOffset = 79;          // block 0x0004 tail, dB = -byte/4 (0..-60)
    private const int HpVolOffset = 0;     // block 0x0005 (headphones 1)
    private const int Hp2VolOffset = 2;    // block 0x0005 (headphones 2)
    private const int CrossfadeOffset = 0; // block 0x0001

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
    // Headphone-mix membership (issue #8, verified by ear): byte 12 bit 5 =
    // USB return pair 2/3 in the jacks' mix, byte 13 bit 1 = mic direct path.
    // The notes' "Personal section" at off16..33 does not affect the jacks.
    private const int HpMixReturnsOffset = 12;
    private const byte HpMixMonitorReturnMask = 0x20;
    private const int HpMixInputsOffset = 13;
    private const byte HpMixMicDirectMask = 0x02;

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
    public const int VoiceTuneStrengthMax = 100;
    public const int CrossfadeMax = 200;

    private readonly IUsbTransport _usb = UsbTransport.Create();
    private readonly object _lock = new();

    public bool Connected => _usb.IsOpen;

    public void Connect()
    {
        if (!_usb.Open(VendorId, ProductId))
            throw new InvalidOperationException(
                "Wave XLR Pro not found or no permission (install the udev rule for 0fd9:00b4).");
    }

    public void Disconnect() => _usb.Close();

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
        byte[] b5 = Read(BlockHp, HpLen);
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
            Crossfade = b1[CrossfadeOffset],
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
            HpMixMonitorReturn = (b1[HpMixReturnsOffset] & HpMixMonitorReturnMask) != 0,
            HpMixMicDirect = (b1[HpMixInputsOffset] & HpMixMicDirectMask) != 0,
            AuxReturnEnabled = b1[AuxMixMusicLevelOffset] == 0x00 &&
                               (b1[AuxMixMusicMemberOffset] & AuxMixMusicMemberMask) != 0,
            AuxLevelDb = -b4[AuxLevelOffset] / 4.0,
            AuxLevelLock = (b4[AuxLevelLockOffset] & 0x04) != 0,
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

    public void SetCrossfade(int value)
    {
        value = Math.Clamp(value, 0, CrossfadeMax);
        byte[] b = Read(BlockCrossfade, CrossfadeLen);
        b[CrossfadeOffset] = (byte)value;
        Write(BlockCrossfade, b);
    }

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
    public void SetHpMixMonitorReturn(bool on) => SetBlock1Bit(HpMixReturnsOffset, HpMixMonitorReturnMask, on);
    public void SetHpMixMicDirect(bool on) => SetBlock1Bit(HpMixInputsOffset, HpMixMicDirectMask, on);

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
        b[AuxLevelLockOffset] = (byte)(on ? b[AuxLevelLockOffset] | 0x04 : b[AuxLevelLockOffset] & ~0x04);
        WriteCommitted(BlockSettings, b);
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

    /// <summary>Every readable vendor block as hex, for tester diagnostics.</summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((ushort block, int len) in new (ushort, int)[]
                 { (0x0001, 108), (0x0002, 150), (0x0004, 80), (0x0005, 8), (0x0006, 29), (0x0008, 96) })
        {
            try { blocks[$"0x{block:X4}"] = Convert.ToHexString(Read(block, len)); }
            catch (IOException ex) { blocks[$"0x{block:X4}"] = $"read failed: {ex.Message}"; }
        }
        return blocks;
    }

    public void Dispose() => _usb.Dispose();
}
