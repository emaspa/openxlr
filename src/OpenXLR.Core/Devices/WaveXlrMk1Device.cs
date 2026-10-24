using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The original Wave XLR (MK.1, 0fd9:007d): a UAC1 device with a small
/// class-request protocol on the unclaimed interface, entirely different from
/// the MK.2/Pro vendor block bank.
///
///   read : bmRequestType=0xA1, bRequest=0x85, wValue=block, wIndex=0x3303
///   write: bmRequestType=0x21, bRequest=0x05, wValue=block, wIndex=0x3303
///
/// wIndex 0x3303 (not 0x3300) bypasses the snd-usb-audio ownership check; the
/// firmware only validates the 0x33 prefix. Block 0 is a 34-byte config:
/// gain u16le at 0, mute at 4, headphone volume as int16 Q8.8 dB at 9, the
/// dial-mode selector at 14, low impedance at 33. Block 1 carries input
/// meters, block 0x0A a 51-byte device info record. Protocol from the
/// openwave project (MIT), verified on MK.1 hardware by its users.
///
/// The gain field's scale is Q8.8 dB: openwave PR #8 measured exactly 256
/// raw units per dB at four points against the ALSA capture control (on the
/// XLR Dock, which shares this protocol). Still open for a tester: the MK.1's
/// maximum, clamped to 75 dB here while openwave's profile says 0x5000 = 80;
/// the device's own gain display settles it.
///
/// Crossfade, low cut, and the voice DSP exist on the hardware but their
/// config offsets are not yet mapped, so the capabilities exclude them; a
/// Wave Link capture from an MK.1 is the way to add them.
///
/// The kernel's ALSA card carries 'Mic Capture Volume', 'Mic Capture Switch'
/// and 'PCM Playback Volume', and the desktop's volume controls and
/// WirePlumber's restore at login act on those. On the XLR Dock, which
/// shares this protocol, the capture volume is the preamp gain itself and an
/// ALSA write shows up in the block at once; an MK.1 owner's archive showed
/// the card's capture volume spanning 0 to 75 dB while the block read 48 dB,
/// so the two had drifted apart. The kernel caches those values and never
/// reads them back, so the block stays the one source of truth and the card
/// is kept in step with it as on the Wave:3 (<see cref="Wave3Device"/>):
/// every change seen in the block (the dial, the mute pad, a write from
/// OpenXLR) is written to the card, and every half second the card's mute
/// and headphone level are read back and a change made there by something
/// else is written into the block. The gain only goes from the block to the
/// card. Each control's range and dB span are read from the card once per
/// connect rather than assumed, because no MK.1 amixer dump is public.
/// </summary>
public abstract class Mk1ClassProtocolDevice : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;

    private const byte RtRead = 0xA1;
    private const byte RtWrite = 0x21;
    private const byte ReqRead = 0x85;
    private const byte ReqWrite = 0x05;
    private const ushort Index = 0x3303;

    private const ushort BlockConfig = 0x0000;
    private const ushort BlockDevInfo = 0x000A;

    private const int OffGain = 0;
    private const int OffMute = 4;
    private const int OffPhantom = 6;   // 48V, 0x01 on; openwave PR #8, found against the MK.1's own LED
    private const int OffHpVol = 9;
    private const int OffLowZ = 33;

    /// <summary>Config block length: 34 on the MK.1, 64 on the XLR Dock.</summary>
    protected abstract int ConfigLen { get; }
    /// <summary>Whether offset 33 is the low-impedance flag (MK.1 only; on the
    /// Dock's longer block that offset falls inside the second sub-structure).</summary>
    protected abstract bool HasLowZ { get; }

    private const string GainCtl = "Mic Capture Volume";
    private const string MuteCtl = "Mic Capture Switch";
    private const string HpCtl = "PCM Playback Volume";
    private static readonly TimeSpan AlsaReadInterval = TimeSpan.FromMilliseconds(500);

    private const double GainMaxDb = 75, HpMinDb = -60, HpMaxDb = 0;

    private readonly object _lock = new();

    public abstract DeviceInfo Info { get; }
    public abstract DeviceCapabilities Capabilities { get; }

    private readonly IUsbTransport _usb;
    private readonly Func<IReadOnlyList<string>, ProcessResult>? _amixer;
    private readonly Func<int>? _findCard;
    private readonly Func<DateTime> _now;

    // The ALSA card kept in step with the block, -1 without one; the two
    // level controls' ranges and whether the card has the mute switch, read
    // once after a connect (null until then); the values
    // the card was last given or last read at; when the card was last read;
    // the last mirror failure.
    private int _card = -1;
    private Dictionary<string, AlsaRange>? _ranges;
    private bool _hasMute;
    private AlsaValues? _mirrored;
    private DateTime _alsaReadAt;
    private string? _mirrorError;

    /// <summary>A level control's raw range and the dB span it covers, as amixer reports them.</summary>
    internal readonly record struct AlsaRange(int Min, int Max, double MinDb, double MaxDb)
    {
        internal int ToRaw(double db)
            => Min + (int)Math.Round((Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb) * (Max - Min), MidpointRounding.AwayFromZero);

        internal double ToDb(int raw) => MinDb + (Math.Clamp(raw, Min, Max) - Min) * (MaxDb - MinDb) / (Max - Min);
    }

    /// <summary>
    /// The three controls in the card's own units, and the microphone muted.
    /// -1 and null for one the card has not been given or does not have.
    /// </summary>
    internal readonly record struct AlsaValues(int Gain, bool? Mute, int Hp);

    /// <summary>The transport, amixer (null leaves the card alone), the card lookup and the clock.</summary>
    private protected Mk1ClassProtocolDevice(IUsbTransport usb, Func<IReadOnlyList<string>, ProcessResult>? amixer,
        Func<int>? findCard, Func<DateTime>? now)
    {
        _usb = usb;
        _amixer = amixer;
        _findCard = amixer is null ? null : findCard ?? (() => AlsaCards.Find("/proc/asound", Info.Location,
            $"{VendorId:x4}:{Info.ProductId:x4}", Info.Model));
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string? ConnectionNote { get; private set; }

    public bool Connected => _usb.IsOpen;

    /// <summary>Release the USB transport and its helper process.</summary>
    public void Dispose() => _usb.Dispose();

    public void Connect()
    {
        if (!_usb.Open(VendorId, Info.ProductId))
            throw new InvalidOperationException($"{Info.Model} present but could not be opened (udev rule?)");
        lock (_lock)
        {
            _card = -1;
            _ranges = null;
            _mirrored = null;
            _mirrorError = null;
            ConnectionNote = null;
            if (_findCard is null) return;
            // A unit without its card still works from OpenXLR; only the
            // desktop's controls fall out of step, and the note says so.
            try { _card = _findCard(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _mirrorError = ex.Message;
                ConnectionNote = $"{ex.Message}; the desktop's volume controls are not kept in step with the interface";
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _usb.Close();
            _card = -1;
            _ranges = null;
            _mirrored = null;
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

    private byte[] Read(ushort block, int length)
    {
        var buf = new byte[length];
        lock (_lock)
        {
            int n = Transfer(RtRead, ReqRead, block, buf, length);
            if (n < 0) throw new InvalidOperationException($"read block {block:x4}: {LibUsb.StrError(n)}");
            if (n != length)
                throw new InvalidOperationException($"read block {block:x4}: got {n} bytes, expected {length}");
        }
        return buf;
    }

    private void Write(ushort block, byte[] data)
    {
        lock (_lock)
        {
            int n = Transfer(RtWrite, ReqWrite, block, data, data.Length);
            if (n < 0) throw new InvalidOperationException($"write block {block:x4}: {LibUsb.StrError(n)}");
            if (n != data.Length)
                throw new InvalidOperationException($"write block {block:x4}: accepted {n} bytes, expected {data.Length}");
        }
    }

    private void Modify(Action<byte[]> edit)
    {
        lock (_lock)
        {
            byte[] cfg = Read(BlockConfig, ConfigLen);
            edit(cfg);
            Write(BlockConfig, cfg);
        }
    }

    public DeviceState ReadState()
    {
        lock (_lock)
        {
            byte[] c = Read(BlockConfig, ConfigLen);
            if (_card >= 0) MirrorLocked(c);
            return Decode(c);
        }
    }

    private DeviceState Decode(byte[] c) => new()
    {
        GainDb = (int)Math.Round(BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(OffGain)) / 256.0),
        Mute = c[OffMute] != 0,
        Phantom = c[OffPhantom] != 0,
        HpVolumeDb = Math.Max(-60.0, BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(OffHpVol)) / 256.0),
        LowImpedance = HasLowZ && c[OffLowZ] != 0,
        Crossfade = 100,   // not mapped in this protocol; keep the neutral centre
    };

    private static double GainDb(ReadOnlySpan<byte> c)
        => Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(c[OffGain..]) / 256.0, 0, GainMaxDb);

    private static double HpDb(ReadOnlySpan<byte> c)
        => Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(c[OffHpVol..]) / 256.0, HpMinDb, HpMaxDb);

    /// <summary>
    /// One step of keeping the card in step with the block (see the class
    /// summary). The first step after a connect reads the card's ranges and
    /// gives it all three; after that a control the block changed is written
    /// to the card, which wins over a read of the card in the same step, and
    /// otherwise, once the read is due, a mute or headphone level the card
    /// holds that differs from what it was last given is written into
    /// <paramref name="c"/> and the device. An amixer failure leaves that
    /// control to the next step and is kept for the diagnostics dump; a
    /// failed block write throws, as any block write does.
    /// </summary>
    private void MirrorLocked(byte[] c)
    {
        if (_ranges is null)
        {
            string? contents = null;
            if (!TryAlsa(() => contents = Amixer("contents"))) return;
            _ranges = ParseRanges(contents!);
            _hasMute = Wave3Device.ParseContents(contents!).ContainsKey(MuteCtl);
        }
        DateTime now = _now();
        if (_mirrored is null) _alsaReadAt = now;
        AlsaValues seen = _mirrored ?? new(-1, null, -1);
        AlsaValues block = ToAlsa(c, _ranges);
        AlsaValues next = seen;
        bool gainOut = block.Gain >= 0 && block.Gain != seen.Gain, muteOut = _hasMute && block.Mute != seen.Mute,
            hpOut = block.Hp >= 0 && block.Hp != seen.Hp;
        if (gainOut && TryAlsa(() => Cset(GainCtl, block.Gain.ToString(CultureInfo.InvariantCulture)))) next = next with { Gain = block.Gain };
        if (muteOut && TryAlsa(() => Cset(MuteCtl, block.Mute == true ? "off" : "on"))) next = next with { Mute = block.Mute };
        if (hpOut && TryAlsa(() => Cset(HpCtl, block.Hp.ToString(CultureInfo.InvariantCulture)))) next = next with { Hp = block.Hp };

        if ((!muteOut || !hpOut) && now - _alsaReadAt >= AlsaReadInterval)
        {
            _alsaReadAt = now;
            Dictionary<string, string>? card = null;
            if (TryAlsa(() => card = Wave3Device.ParseContents(Amixer("contents"))))
            {
                bool edited = false;
                if (_hasMute && !muteOut && card!.TryGetValue(MuteCtl, out string? sw) && sw is "on" or "off" && (sw == "off") != seen.Mute)
                {
                    c[OffMute] = sw == "off" ? (byte)1 : (byte)0;
                    next = next with { Mute = sw == "off" };
                    edited = true;
                }
                if (!hpOut && _ranges.TryGetValue(HpCtl, out AlsaRange hpRange) && card!.TryGetValue(HpCtl, out string? hp)
                    && int.TryParse(hp.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
                    && level >= hpRange.Min && level <= hpRange.Max && level != seen.Hp)
                {
                    short word = (short)(Math.Clamp(hpRange.ToDb(level), HpMinDb, HpMaxDb) * 256);
                    BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffHpVol), word);
                    next = next with { Hp = level };
                    edited = true;
                }
                if (edited) Write(BlockConfig, c);
            }
        }
        _mirrored = next;
    }

    /// <summary>The block's three controls in the card's units; -1 for a level control the card does not have.</summary>
    internal static AlsaValues ToAlsa(ReadOnlySpan<byte> c, IReadOnlyDictionary<string, AlsaRange> ranges)
        => new(ranges.TryGetValue(GainCtl, out AlsaRange gain) ? gain.ToRaw(GainDb(c)) : -1,
            c[OffMute] != 0,
            ranges.TryGetValue(HpCtl, out AlsaRange hp) ? hp.ToRaw(HpDb(c)) : -1);

    private static readonly Regex RangeEntry = new(
        @"name='(?<name>[^']+)'[^\n]*\n[ \t]+; type=INTEGER,[^\n]*?min=(?<min>-?\d+),max=(?<max>-?\d+)[^\n]*\n[ \t]+: values=[^\n]*\n[ \t]+\| dB(?:minmax-min=(?<lo>-?[0-9.]+)dB,max=(?<hi>-?[0-9.]+)dB|scale-min=(?<lo>-?[0-9.]+)dB,step=(?<step>[0-9.]+)dB)",
        RegexOptions.Compiled);

    /// <summary>
    /// The integer controls of `amixer contents` that carry a dB span, by
    /// name. A control whose maximum is not above its minimum, or whose span
    /// is empty, is left out: the kernel has been seen dropping the dock's
    /// capture volume for such an answer, and nothing could be mapped onto it.
    /// </summary>
    internal static Dictionary<string, AlsaRange> ParseRanges(string contents)
    {
        var ranges = new Dictionary<string, AlsaRange>(StringComparer.Ordinal);
        foreach (Match m in RangeEntry.Matches(contents))
        {
            int min = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
            int max = int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture);
            double lo = double.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture);
            double hi = m.Groups["step"].Success
                ? lo + double.Parse(m.Groups["step"].Value, CultureInfo.InvariantCulture) * (max - min)
                : double.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture);
            if (max > min && hi > lo) ranges[m.Groups["name"].Value] = new(min, max, lo, hi);
        }
        return ranges;
    }

    private bool TryAlsa(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            _mirrorError = ex.Message;
            return false;
        }
        // A missing amixer (alsa-utils is only recommended) fails to start
        // rather than exiting. It must not take the interface down with it,
        // and is not looked for again until the next connect.
        catch (System.ComponentModel.Win32Exception ex)
        {
            _mirrorError = $"amixer could not be started: {ex.Message}";
            _card = -1;
            return false;
        }
    }

    private string Amixer(params string[] args)
    {
        ProcessResult r = _amixer!(["-c", _card.ToString(CultureInfo.InvariantCulture), .. args]);
        if (r.TimedOut) throw new TimeoutException($"amixer {string.Join(' ', args)} timed out");
        if (r.ExitCode != 0) throw new InvalidOperationException($"amixer {string.Join(' ', args)}: {r.Stderr.Trim()}");
        return r.StdoutText;
    }

    private void Cset(string name, string value) => Amixer("cset", $"name={name}", value);

    public void SetGainDb(int db)
    {
        ushort raw = (ushort)(Math.Clamp(db, 0, 75) * 256);
        Modify(c => BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(OffGain), raw));
    }

    public void SetMute(bool on) => Modify(c => c[OffMute] = on ? (byte)1 : (byte)0);

    public void SetHpVolumeDb(double db)
    {
        short raw = (short)(Math.Clamp(db, -60.0, 0.0) * 256);
        Modify(c => BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffHpVol), raw));
    }

    public void SetLowImpedance(bool on)
    {
        if (HasLowZ) Modify(c => c[OffLowZ] = on ? (byte)1 : (byte)0);
    }

    public void SetPhantom(bool on) => Modify(c => c[OffPhantom] = on ? (byte)1 : (byte)0);

    // Present on the hardware but not yet mapped in this protocol.
    public void SetLowCut(bool on) { }
    public void SetExpander(bool on) { }
    public void SetVoiceTune(bool on) { }
    public void SetVoiceTuneStrength(int value) { }
    public void SetCrossfade(int value) { }
    public void SetClipGuard(bool on) { }
    public void SetCompressor(bool on) { }

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>();
        try { blocks["config"] = Convert.ToHexString(Read(BlockConfig, ConfigLen)); }
        catch (Exception ex) { blocks["config"] = $"error: {ex.Message}"; }
        try { blocks["devinfo"] = Convert.ToHexString(Read(BlockDevInfo, 51)); }
        catch (Exception ex) { blocks["devinfo"] = $"error: {ex.Message}"; }
        lock (_lock)
        {
            blocks["alsa"] = _card < 0
                ? (_mirrorError is string why ? $"not mirrored: {why}" : "not mirrored")
                : $"card={_card} given={(_mirrored is AlsaValues m ? $"gain={m.Gain} capture={(m.Mute is bool muted ? (muted ? "off" : "on") : "?")} hp={m.Hp}" : "nothing yet")}"
                    + (_ranges is { } r ? $" ranges={string.Join(' ', r.Select(p => FormattableString.Invariant($"'{p.Key}'={p.Value.Min}..{p.Value.Max}:{p.Value.MinDb:0.##}..{p.Value.MaxDb:0.##}dB")))}" : "")
                    + (_mirrorError is string error ? $" last error: {error}" : "");
        }
        return blocks;
    }
}

/// <summary>The original Wave XLR (MK.1, 0fd9:007d). Protocol from the
/// openwave project; gain, mute, headphone volume, low impedance and phantom
/// verified on two units by community testers (issues #6 and earlier).
/// Mic capture needs 52-openxlr-mk1-capture-hold.conf (packaging/): without
/// it, a playback stream opening before capture starts silences the mic for
/// that capture stream's whole life; see the file for why.</summary>
public sealed class WaveXlrMk1Device : Mk1ClassProtocolDevice
{
    public const ushort ProductId = 0x007D;

    public WaveXlrMk1Device() : base(UsbTransport.Create(), AlsaCards.RunAmixer, null, null) { }

    /// <summary>Tests substitute the transport, amixer (null leaves the card alone), the card lookup and the clock.</summary>
    internal WaveXlrMk1Device(IUsbTransport usb, Func<IReadOnlyList<string>, ProcessResult>? amixer, Func<int>? findCard,
        Func<DateTime>? now = null) : base(usb, amixer, findCard, now) { }

    protected override int ConfigLen => 34;
    protected override bool HasLowZ => true;

    public override DeviceInfo Info { get; } = UsbTransport.WithLocation(new("Elgato", "Wave XLR", VendorId, ProductId));

    public override DeviceCapabilities Capabilities { get; } = new()
    {
        Gain = true,
        PhysicalControls = true,
        Mute = true,
        HpVolume = true,
        LowImpedance = true,
        Phantom = true,
        XlrInputs = 1,
        HpOutputs = 1,
        GainMaxDb = 75,
        RetainsSettings = false,
    };
}
