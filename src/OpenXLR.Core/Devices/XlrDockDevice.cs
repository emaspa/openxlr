using OpenXLR.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato XLR Dock (0fd9:00a6), the Stream Deck+ audio module. The
/// module has no controls that directly mutate its USB state; Stream Deck+
/// keys and dials act through a software client such as the OpenDeck plugin.
///
/// Gain, mute, and headphone volume are driven through the kernel's standard
/// ALSA controls ('Mic Capture Volume' 0..150 for 0..75 dB, 'Mic Capture
/// Switch', 'PCM Playback Volume' 0..120 for -60..0 dB), backed by the same
/// registers Wave Link drives, so those writes cannot disturb the audio
/// streams.
///
/// The dock also speaks the original Wave XLR's class-request dialect (read
/// 0xA1/0x85, write 0x21/0x05, wIndex 0x3303) with a 64-byte config block in
/// the MK.1's own layout: the gain as a Q8.8 dB word at offset 0, the mute
/// byte at 4, the 48V switch at byte 6, the headphone volume as a signed Q8.8
/// word at offset 9 and the low-impedance flag at byte 33. Phantom power and
/// low impedance have no ALSA control and always go through the block. The
/// other three go through the block only when the kernel did not create
/// their ALSA control: a unit was met (2026-09-18, same product and firmware
/// revision as the one verified here) whose firmware answers the capture
/// volume's range query with a maximum no higher than the minimum, so
/// snd-usb-audio drops that control and the card carries only the switches
/// and the playback volume. The block reads the live register (an ALSA write
/// shows up in it at once) and a level sweep with a microphone showed the
/// same 70 dB swing whether the gain was set through ALSA or through the
/// block word. Each control uses exactly one path: the kernel caches feature
/// unit values and never re-reads them, so a block write behind ALSA's back
/// would leave the two disagreeing.
///
/// The device has no physical controls, so nothing changes state behind our
/// back except other ALSA clients; state reads are cached briefly to keep the
/// daemon's poll loop from spawning amixer ten times a second.
/// </summary>
public sealed class XlrDockDevice : IAudioDevice
{
    public const ushort VendorId = 0x0FD9;
    public const ushort ProductId = 0x00A6;

    private const string GainCtl = "Mic Capture Volume";
    private const string MuteCtl = "Mic Capture Switch";
    private const string HpCtl = "PCM Playback Volume";

    // The MK.1 class-request dialect.
    private const byte RtRead = 0xA1;
    private const byte RtWrite = 0x21;
    private const byte ReqRead = 0x85;
    private const byte ReqWrite = 0x05;
    private const ushort UsbIndex = 0x3303;
    private const ushort BlockConfig = 0x0000;
    private const int ConfigLen = 64;
    private const int OffGain = 0;
    private const int OffMute = 4;
    private const int OffPhantom = 6;
    private const int OffHpVol = 9;
    private const int OffLowZ = 33;

    /// <summary>How one of the three ALSA-first controls is reached on this unit.</summary>
    internal enum ControlPath { None, Alsa, Block }

    private static readonly DeviceCapabilities BaseCapabilities = new()
    {
        Gain = true,
        Mute = true,
        HpVolume = true,
        Phantom = true,
        LowImpedance = true,
        XlrInputs = 1,
        HpOutputs = 1,
        RetainsSettings = false,
    };

    private int _card = -1;
    private DeviceState? _cached;
    private DateTime _cachedAt;
    private readonly object _lock = new();
    private ControlPath _gainPath, _mutePath, _hpPath;

    private readonly IUsbTransport _usb;
    private readonly Func<IReadOnlyList<string>, ProcessResult> _amixer;
    private readonly Func<int> _findCard;

    public XlrDockDevice() : this(UsbTransport.Create(), RunAmixer, FindCard) { }

    /// <summary>Tests substitute the transport, amixer and the card lookup.</summary>
    internal XlrDockDevice(IUsbTransport usb, Func<IReadOnlyList<string>, ProcessResult> amixer, Func<int> findCard)
    {
        _usb = usb;
        _amixer = amixer;
        _findCard = findCard;
    }

    public DeviceInfo Info { get; } = new("Elgato", "XLR Dock", VendorId, ProductId);

    /// <summary>
    /// The base set until a connect has looked at the card; after that, a
    /// control the card lacks and the block cannot reach is dropped.
    /// </summary>
    public DeviceCapabilities Capabilities { get; private set; } = BaseCapabilities;

    public string? ConnectionNote { get; private set; }

    internal ControlPath GainPath => _gainPath;
    internal ControlPath MutePath => _mutePath;
    internal ControlPath HpPath => _hpPath;

    public bool Connected => _card >= 0;

    /// <summary>Release the USB transport and its helper process.</summary>
    public void Dispose() => _usb.Dispose();

    private static ProcessResult RunAmixer(IReadOnlyList<string> args)
        => ProcessRunner.Run("amixer", args, TimeSpan.FromSeconds(2), stdoutCap: 1024 * 1024, stderrCap: 64 * 1024);

    private static int FindCard()
    {
        foreach (string dir in Directory.EnumerateDirectories("/proc/asound").OrderBy(d => d))
        {
            string usbid = Path.Combine(dir, "usbid");
            try
            {
                if (File.Exists(usbid) && File.ReadAllText(usbid).Trim() == "0fd9:00a6"
                    && int.TryParse(Path.GetFileName(dir).Replace("card", ""), out int n))
                    return n;
            }
            catch (IOException) { /* card went away mid-scan */ }
        }
        throw new InvalidOperationException("XLR Dock present on USB but its ALSA card was not found");
    }

    public void Connect()
    {
        int card = _findCard();
        lock (_lock)
        {
            _card = card;
            _cached = null;
            // Phantom and low impedance need the USB handle. Without it (for
            // example, before the udev rule applies) the ALSA-backed controls
            // still work; USB-backed setters report the permission problem
            // instead of pretending the write succeeded.
            try { _usb.Open(VendorId, ProductId); }
            catch (Exception) { /* USB-backed setters report the problem when used */ }
            string controls;
            try { controls = Amixer("controls"); }
            catch (Exception) { _card = -1; throw; }
            _gainPath = Resolve(controls, GainCtl);
            _mutePath = Resolve(controls, MuteCtl);
            _hpPath = Resolve(controls, HpCtl);
            Capabilities = BaseCapabilities with
            {
                Gain = _gainPath != ControlPath.None,
                Mute = _mutePath != ControlPath.None,
                HpVolume = _hpPath != ControlPath.None,
            };
            ConnectionNote = Describe();
        }
    }

    private ControlPath Resolve(string controls, string name)
    {
        if (controls.Contains($"name='{name}'", StringComparison.Ordinal)) return ControlPath.Alsa;
        return _usb.IsOpen ? ControlPath.Block : ControlPath.None;
    }

    private string? Describe()
    {
        var missing = new List<string>();
        var unreachable = new List<string>();
        foreach ((string name, ControlPath path) in new[] { (GainCtl, _gainPath), (MuteCtl, _mutePath), (HpCtl, _hpPath) })
        {
            if (path == ControlPath.Block) missing.Add(name);
            else if (path == ControlPath.None) unreachable.Add(name);
        }
        if (missing.Count == 0 && unreachable.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"the card has no {Join(missing)}; driving it through the dock's config block instead");
        if (unreachable.Count > 0)
            parts.Add($"the card has no {Join(unreachable)} and the USB handle is not open (udev rule?), so it is unavailable");
        return string.Join("; ", parts);
        static string Join(List<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _card = -1;
            _cached = null;
            _usb.Close();
        }
    }

    private string Amixer(params string[] args)
    {
        ProcessResult r = _amixer(["-c", _card.ToString(), .. args]);
        if (r.TimedOut) throw new TimeoutException($"amixer {string.Join(' ', args)} timed out");
        if (r.ExitCode != 0) throw new InvalidOperationException($"amixer {string.Join(' ', args)}: {r.Stderr.Trim()}");
        return r.StdoutText;
    }

    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the handle is then abandoned
    /// without libusb_close, since the stuck native call may still use it,
    /// and Connected turns false so the daemon reconnects with a new one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
        => _usb.ControlTransfer(requestType, request, value, UsbIndex, data, (ushort)length, 1000);

    private byte[] ReadConfig()
    {
        var buf = new byte[ConfigLen];
        int n = Transfer(RtRead, ReqRead, BlockConfig, buf, ConfigLen);
        if (n < 0) throw new InvalidOperationException($"read config block: {LibUsb.StrError(n)}");
        if (n != ConfigLen)
            throw new InvalidOperationException($"read config block: got {n} bytes, expected {ConfigLen}");
        return buf;
    }

    /// <summary>
    /// The config block when the handle is open, or null when it is not or
    /// the read failed and nothing depends on it. A control routed through
    /// the block needs the read, so its failure surfaces.
    /// </summary>
    private byte[]? ReadConfigIfOpen()
    {
        if (!_usb.IsOpen) return null;
        bool required = _gainPath == ControlPath.Block || _mutePath == ControlPath.Block || _hpPath == ControlPath.Block;
        try { return ReadConfig(); }
        catch (InvalidOperationException) when (!required) { return null; }
    }

    private void ModifyConfig(Action<byte[]> edit, string what)
    {
        if (!_usb.IsOpen)
            throw new InvalidOperationException(
                $"XLR Dock USB handle not open; {what} needs the udev rule");
        lock (_lock)
        {
            byte[] cfg = ReadConfig();
            edit(cfg);
            int n = Transfer(RtWrite, ReqWrite, BlockConfig, cfg, ConfigLen);
            if (n < 0) throw new InvalidOperationException($"write config block: {LibUsb.StrError(n)}");
            if (n != ConfigLen)
                throw new InvalidOperationException($"write config block: accepted {n} bytes, expected {ConfigLen}");
            _cached = null;
        }
    }

    private void SetConfigByte(int offset, byte value, string what)
        => ModifyConfig(c => c[offset] = value, what);

    private static readonly Regex Values = new(@": values=([A-Za-z0-9,\-]+)", RegexOptions.Compiled);

    private string Get(string name)
    {
        Match m = Values.Match(Amixer("cget", $"name={name}"));
        if (!m.Success) throw new InvalidOperationException($"amixer cget '{name}': no values");
        return m.Groups[1].Value;
    }

    private void Set(string name, string value)
    {
        lock (_lock)
        {
            Amixer("cset", $"name={name}", value);
            _cached = null;   // next ReadState reflects the write immediately
        }
    }

    private static InvalidOperationException Unavailable(string what)
        => new($"XLR Dock exposes no ALSA control for {what} and its USB handle is not open; {what} needs the udev rule");

    public DeviceState ReadState()
    {
        lock (_lock)
        {
            if (_cached is not null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 1)
                return _cached;
            byte[]? cfg = ReadConfigIfOpen();
            int gainDb = _gainPath switch
            {
                ControlPath.Alsa => (int)Math.Round(int.Parse(Get(GainCtl)) / 2.0),
                ControlPath.Block => (int)Math.Round(BinaryPrimitives.ReadUInt16LittleEndian(cfg.AsSpan(OffGain)) / 256.0),
                _ => 0,
            };
            bool mute = _mutePath switch
            {
                ControlPath.Alsa => !Get(MuteCtl).StartsWith("on"),
                ControlPath.Block => cfg![OffMute] != 0,
                _ => false,
            };
            double hpDb = _hpPath switch
            {
                ControlPath.Alsa => int.Parse(Get(HpCtl)) / 2.0 - 60.0,
                ControlPath.Block => Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(cfg.AsSpan(OffHpVol)) / 256.0, -60.0, 0.0),
                _ => 0.0,
            };
            _cached = new DeviceState
            {
                GainDb = gainDb,
                Mute = mute,
                HpVolumeDb = hpDb,
                Phantom = cfg is not null && cfg[OffPhantom] != 0,
                LowImpedance = cfg is not null && cfg[OffLowZ] != 0,
                Crossfade = 100,   // not a hardware feature here; neutral centre
            };
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public void SetGainDb(int db)
    {
        int clamped = Math.Clamp(db, 0, 75);
        switch (_gainPath)
        {
            case ControlPath.Alsa: Set(GainCtl, (clamped * 2).ToString()); break;
            case ControlPath.Block:
                ModifyConfig(c => BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(OffGain), (ushort)(clamped * 256)), "gain");
                break;
            default: throw Unavailable("gain");
        }
    }

    public void SetMute(bool on)
    {
        switch (_mutePath)
        {
            case ControlPath.Alsa: Set(MuteCtl, on ? "off" : "on"); break;
            case ControlPath.Block: SetConfigByte(OffMute, on ? (byte)1 : (byte)0, "mute"); break;
            default: throw Unavailable("mute");
        }
    }

    public void SetHpVolumeDb(double db)
    {
        double clamped = Math.Clamp(db, -60.0, 0.0);
        switch (_hpPath)
        {
            case ControlPath.Alsa: Set(HpCtl, ((int)Math.Round((clamped + 60.0) * 2)).ToString()); break;
            case ControlPath.Block:
                ModifyConfig(c => BinaryPrimitives.WriteInt16LittleEndian(c.AsSpan(OffHpVol), (short)Math.Round(clamped * 256)), "headphone volume");
                break;
            default: throw Unavailable("headphone volume");
        }
    }

    // Everything else runs host-side (Wave Link style) or does not exist on
    // this hardware; the capabilities above keep the UI from offering them.
    public void SetLowCut(bool on) { }
    public void SetExpander(bool on) { }
    public void SetVoiceTune(bool on) { }
    public void SetVoiceTuneStrength(int value) { }
    public void SetLowImpedance(bool on) => SetConfigByte(OffLowZ, on ? (byte)1 : (byte)0, "low impedance");
    public void SetCrossfade(int value) { }
    public void SetPhantom(bool on) => SetConfigByte(OffPhantom, on ? (byte)1 : (byte)0, "phantom");
    public void SetClipGuard(bool on) { }
    public void SetCompressor(bool on) { }

    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        var blocks = new Dictionary<string, string>
        {
            ["paths"] = $"card={_card} gain={_gainPath} mute={_mutePath} hp={_hpPath}",
        };
        try
        {
            blocks["alsa"] = string.Join(' ', new[] { (GainCtl, _gainPath, "gain"), (MuteCtl, _mutePath, "capture"), (HpCtl, _hpPath, "hp") }
                .Where(c => c.Item2 == ControlPath.Alsa)
                .Select(c => $"{c.Item3}={Get(c.Item1)}"));
        }
        catch (Exception ex) { blocks["alsa"] = $"error: {ex.Message}"; }
        if (_usb.IsOpen)
        {
            try { blocks["config"] = Convert.ToHexString(ReadConfig()); }
            catch (Exception ex) { blocks["config"] = $"error: {ex.Message}"; }
            try
            {
                var buf = new byte[51];
                int n = Transfer(RtRead, ReqRead, 0x000A, buf, 51);
                blocks["devinfo"] = n >= 0
                    ? Convert.ToHexString(buf.AsSpan(0, n))
                    : $"error: {LibUsb.StrError(n)}";
            }
            catch (Exception ex) { blocks["devinfo"] = $"error: {ex.Message}"; }
        }
        return blocks;
    }
}
