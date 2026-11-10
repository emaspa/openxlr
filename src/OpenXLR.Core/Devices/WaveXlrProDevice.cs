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
///
/// Each struct also holds the input's compressor and expander settings at
/// bytes 3 to 17 (XLR 2's at 41 to 55), the layout verified on the Wave:3
/// MK.2 (<see cref="WaveFxDynamics"/>). The Pro holds the same factory
/// values there on both inputs, so it is driven the same way, behind the
/// EQ's gate and only when both inputs' fields decode; that its DSP runs
/// them from these bytes is inferred and not yet heard.
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

    /// <summary>Whether this unit runs each input's compressor and expander, decided at connect with the EQ.</summary>
    public DeviceEqSupport? DynamicsSupport { get; private set; }

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
        HardwareMixing = true,
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

    // Block 0x0001, the unit's mixes and output selectors, is read and
    // written through ProMixPlan (today's mode) and decoded for
    // diagnostics by ProMixSections; nothing else here writes it.

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
        DynamicsSupport = null;
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
        // Each input's compressor and expander, in the same struct as its EQ.
        DynamicsSupport = WaveFxDynamics.Gate(Info.Model, EqSupport,
            () => WaveFxDynamics.Decodes(DynamicsOf(block!, 1)) && WaveFxDynamics.Decodes(DynamicsOf(block!, 2)));
        Capabilities = ModelCapabilities with { HardwareEq = EqSupport.Ready, HardwareDynamics = DynamicsSupport.Ready };
    }

    private static DeviceEq? EqOf(byte[] settings, int input)
        => WaveFxEq.Decode(settings.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength));

    private static DeviceDynamics? DynamicsOf(byte[] settings, int input)
        => WaveFxDynamics.Decode(settings.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength));

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
        ProMixHeld mix = ProMixPlan.Read(b1);
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
            LineOutLevelDb = ProJackLevels.DbOf(b5[ProJackLevels.LineOutOffset]),
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
            OutHp1 = mix.Hp1,
            OutHp2 = mix.Hp2,
            OutUsbAux = mix.UsbAux,
            OutLineOut = mix.LineOut,
            HpMixMonitorReturn = mix.MonitorReturn,
            HpMixMicDirect = mix.Mic1Direct,
            HpMixMicLevelDb = mix.Mic1LevelDb,
            HpMixMic2Direct = mix.Mic2Direct,
            HpMixMic2LevelDb = mix.Mic2LevelDb,
            AuxReturnEnabled = mix.AuxReturnEnabled,
            ProMixBlock = Convert.ToHexString(b1),
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
    // ProMixPlan), and the capabilities leave the control out.
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

    // --- block 0x0001, the mixes and output selectors (see ProMixPlan) ---

    /// <summary>
    /// Bring block 0x0001 to a plan under the lock, so no other write lands
    /// between the reads and the writes: block 0x0008 read first when the
    /// plan carries ducking, then block 0x0001; the plan's edits made on
    /// fresh copies; 0x0008 written first when it changed, as Wave Link
    /// writes ducking, then 0x0001 once when it changed, then the commit
    /// Wave Link sends when a selector or a membership bit changed (the
    /// selectors need it; level cells and ducking fields apply without
    /// one, and Wave Link sends none for them).
    /// </summary>
    public ProMixWrite? WriteMix(ProMixPlan plan)
    {
        lock (_lock)
        {
            byte[]? b8 = null, new8 = null;
            if (plan.Ducking is { } ducking)
            {
                if (ProDucking.Valid(ducking) is { } problem) throw new ArgumentException(problem, nameof(plan));
                b8 = Read(ProDucking.Block, ProDucking.Length);
                new8 = (byte[])b8.Clone();
                ProDucking.ApplyEntries(new8, ducking);
            }
            byte[] b1 = Read(BlockCrossfade, CrossfadeLen);
            var new1 = new byte[CrossfadeLen];
            ProMixChanges changes = plan.Apply(b1, new1);
            bool wrote8 = b8 is not null && !new8.AsSpan().SequenceEqual(b8);
            bool wrote1 = !new1.AsSpan().SequenceEqual(b1);
            // The jacks' levels: every one that goes down before the mixes
            // change, every one that goes up after, so no source is louder
            // on a jack for a moment (hardware mixing's ear safety).
            byte[]? b5 = plan.JackLevels is null ? null : Read(BlockHp, HpLen);
            byte[]? down5 = b5 is null ? null : plan.JackLevels!.Lowered(b5);
            if (b5 is not null && !down5.AsSpan().SequenceEqual(b5)) Write(BlockHp, down5!);
            if (wrote8) Write(ProDucking.Block, new8!);
            bool committed = wrote1 && changes.Commits();
            if (committed) WriteCommitted(BlockCrossfade, new1);
            else if (wrote1) Write(BlockCrossfade, new1);
            byte[]? new5 = b5 is null ? null : plan.JackLevels!.All(b5);
            if (new5 is not null && !new5.AsSpan().SequenceEqual(down5)) Write(BlockHp, new5);
            if (b8 is not null) _ducking = ProDucking.Decode(new8!, new1);
            return new ProMixWrite(changes, wrote8, b8 is null ? "not read" : ProDucking.Diff(b8, new8!), wrote1, ProDucking.Diff(b1, new1), committed)
            {
                Change5 = b5 is null || new5.AsSpan().SequenceEqual(b5) ? "" : ProDucking.Diff(b5, new5!),
            };
        }
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

    /// <summary>
    /// Write one input's compressor and expander into its struct, as
    /// <see cref="SetEq"/> writes its EQ: one read of the settings block and
    /// one write of the whole block under the lock, no commit, every byte
    /// they do not own (the other input's struct and the aux tail included)
    /// as read. False when the unit already held them.
    /// </summary>
    public bool SetDynamics(int input, DeviceDynamics dynamics)
    {
        if (input is not (1 or 2) || DynamicsSupport?.Ready != true)
            throw new InvalidOperationException($"{Info.Model}: this unit does not run the compressor and expander on input {input}");
        lock (_lock)
        {
            byte[] b = Read(BlockSettings, SettingsLen);
            byte[] held = (byte[])b.Clone();
            WaveFxDynamics.Encode(b.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength), dynamics);
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
            return input is 1 or 2 && DynamicsSupport?.Ready == true && _settings is { Length: SettingsLen } s ? DynamicsOf(s, input) : null;
    }

    // --- ducking (blocks 0x0008 and 0x0001, see ProDucking) ---

    private volatile DuckingReadback? _ducking;

    public DuckingReadback? ReadDucking() => _ducking;

    // Written with block 0x0001 through WriteMix, which takes the plan's
    // ducking target; block 0x0008 stays with ProDucking's codec.

    /// <summary>
    /// Block 0x0002, the unit's meters, read once and decoded. Nothing polls
    /// it here: the device manager reads it only while a client takes meters
    /// and the unit runs ducking or builds mixes in hardware mixing. A failed
    /// read gives null, so a unit that refuses the block still answers every
    /// control; a hung one throws.
    /// </summary>
    public ProMeterReading? ReadMeters()
    {
        try { return ProMeters.Decode(Read(ProMeters.Block, ProMeters.Length)); }
        catch (IOException ex) when (ex is not UsbHungException) { return null; }
    }

    /// <summary>Every readable vendor block as hex, for tester diagnostics, with the firmware, the mix sections, the meters and each input's EQ decoded.</summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        foreach ((ushort block, int len) in new (ushort, int)[]
                 { (BlockIdentity, IdentityLen), (0x0001, 108), (ProMeters.Block, ProMeters.Length), (0x0004, 80), (0x0005, 8), (0x0006, 29), (0x0008, 96) })
        {
            try
            {
                byte[] read = Read(block, len);
                blocks[$"0x{block:X4}"] = Convert.ToHexString(read);
                if (block == ProMixSections.Block)
                    foreach ((string key, string value) in ProMixSections.Describe(read)) blocks[key] = value;
                if (block == ProMeters.Block)
                    foreach ((string key, string value) in ProMeters.Describe(read)) blocks[key] = value;
                if (block == BlockSettings)
                    for (int input = 1; input <= 2; input++)
                    {
                        blocks[$"eq{input}"] = EqOf(read, input) is { } eq
                            ? eq + (WaveFxEq.Decodes(eq) ? "" : " (outside the EQ's ranges)")
                            : "too short to hold the EQ";
                        blocks[$"dynamics{input}"] = DynamicsOf(read, input) is { } dynamics
                            ? dynamics + (WaveFxDynamics.Decodes(dynamics) ? "" : " (outside the unit's limits)")
                            : "too short to hold the compressor and expander";
                    }
            }
            catch (IOException ex) { blocks[$"0x{block:X4}"] = $"read failed: {ex.Message}"; }
        }
        blocks["firmware"] = Firmware ?? "not read";
        blocks["eqSupport"] = EqSupport is { } support ? support.Ready ? "on the unit" : $"in software: {support.Reason}" : "not decided";
        blocks["dynamicsSupport"] = DynamicsSupport is { } dynamicsSupport
            ? dynamicsSupport.Ready ? "on the unit" : $"in software: {dynamicsSupport.Reason}" : "not decided";
        return blocks;
    }

    public void Dispose() => _usb.Dispose();
}
