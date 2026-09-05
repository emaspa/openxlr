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
/// Phantom power and low impedance are the exceptions: the kernel exposes no
/// controls for them, but the dock also speaks the original Wave XLR's
/// class-request dialect (read 0xA1/0x85, write 0x21/0x05, wIndex 0x3303)
/// with a 64-byte config block, byte 6 the 48V switch and byte 33 the
/// low-impedance flag, the MK.1's own layout, which the openwave project
/// mapped against that device's 48V LED (openwave PR #8). Verified on this
/// hardware 2026-08-30: each write is accepted, persists, changes nothing
/// else in the block, phantom brings a condenser microphone on the dock's
/// XLR to life, and low impedance audibly switches the headphone stage.
/// Wave Link itself never writes these bytes for the dock, which is why the
/// earlier Windows capture audit found no such traffic.
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

    // The MK.1 class-request dialect, used here only for phantom power.
    private const byte RtRead = 0xA1;
    private const byte RtWrite = 0x21;
    private const byte ReqRead = 0x85;
    private const byte ReqWrite = 0x05;
    private const ushort UsbIndex = 0x3303;
    private const ushort BlockConfig = 0x0000;
    private const int ConfigLen = 64;
    private const int OffPhantom = 6;
    private const int OffLowZ = 33;

    private static IntPtr _ctx = IntPtr.Zero;
    private IntPtr _handle = IntPtr.Zero;

    private int _card = -1;
    private DeviceState? _cached;
    private DateTime _cachedAt;
    private readonly object _lock = new();

    public DeviceInfo Info { get; } = new("Elgato", "XLR Dock", VendorId, ProductId);

    public DeviceCapabilities Capabilities { get; } = new()
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

    public bool Connected => _card >= 0;

    public void Connect()
    {
        foreach (string dir in Directory.EnumerateDirectories("/proc/asound").OrderBy(d => d))
        {
            string usbid = Path.Combine(dir, "usbid");
            try
            {
                if (File.Exists(usbid) && File.ReadAllText(usbid).Trim() == "0fd9:00a6"
                    && int.TryParse(Path.GetFileName(dir).Replace("card", ""), out int n))
                {
                    _card = n;
                    OpenUsb();
                    return;
                }
            }
            catch (IOException) { /* card went away mid-scan */ }
        }
        throw new InvalidOperationException("XLR Dock present on USB but its ALSA card was not found");
    }

    // Phantom and low impedance need the USB handle. Without it (for example,
    // before the udev rule applies) gain, mute, and headphone volume still work
    // through ALSA; USB-backed setters report the permission problem instead
    // of pretending the write succeeded.
    private void OpenUsb()
    {
        if (_ctx == IntPtr.Zero && LibUsb.libusb_init(out _ctx) != 0) return;
        _handle = LibUsb.libusb_open_device_with_vid_pid(_ctx, VendorId, ProductId);
    }

    public void Disconnect()
    {
        _card = -1;
        if (_handle != IntPtr.Zero) { LibUsb.libusb_close(_handle); _handle = IntPtr.Zero; }
    }

    private string Amixer(params string[] args)
    {
        var psi = new ProcessStartInfo("amixer")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_card.ToString());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start amixer");
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(2000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"amixer {string.Join(' ', args)} timed out");
        }
        string outText = outTask.GetAwaiter().GetResult();
        string errText = errTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"amixer {string.Join(' ', args)}: {errText.Trim()}");
        return outText;
    }


    /// <summary>
    /// All control transfers go through here. A transfer that never returns
    /// (issue #6) throws UsbHungException; the handle is then abandoned
    /// without libusb_close, since the stuck native call may still use it,
    /// and Connected turns false so the daemon reconnects with a new one.
    /// </summary>
    private int Transfer(byte requestType, byte request, ushort value, byte[] data, int length)
    {
        try { return LibUsb.ControlTransfer(_handle, requestType, request, value, UsbIndex, data, (ushort)length, 1000); }
        catch (UsbHungException) { _handle = IntPtr.Zero; throw; }
    }

    private byte[] ReadConfig()
    {
        var buf = new byte[ConfigLen];
        int n = Transfer(RtRead, ReqRead, BlockConfig, buf, ConfigLen);
        if (n < 0) throw new InvalidOperationException($"read config block: {LibUsb.StrError(n)}");
        if (n != ConfigLen)
            throw new InvalidOperationException($"read config block: got {n} bytes, expected {ConfigLen}");
        return buf;
    }

    private (bool Phantom, bool LowZ) ReadUsbFlags()
    {
        if (_handle == IntPtr.Zero) return (false, false);
        try
        {
            byte[] c = ReadConfig();
            return (c[OffPhantom] != 0, c[OffLowZ] != 0);
        }
        catch (InvalidOperationException) { return (false, false); }
    }

    private void SetConfigByte(int offset, byte value, string what)
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"XLR Dock USB handle not open; {what} needs the udev rule");
        lock (_lock)
        {
            byte[] cfg = ReadConfig();
            cfg[offset] = value;
            int n = Transfer(RtWrite, ReqWrite, BlockConfig, cfg, ConfigLen);
            if (n < 0) throw new InvalidOperationException($"write config block: {LibUsb.StrError(n)}");
            if (n != ConfigLen)
                throw new InvalidOperationException($"write config block: accepted {n} bytes, expected {ConfigLen}");
            _cached = null;
        }
    }

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

    public DeviceState ReadState()
    {
        lock (_lock)
        {
            if (_cached is not null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 1)
                return _cached;
            int gainRaw = int.Parse(Get(GainCtl));
            bool unmuted = Get(MuteCtl).StartsWith("on");
            int hpRaw = int.Parse(Get(HpCtl));
            (bool phantom, bool lowZ) = ReadUsbFlags();
            _cached = new DeviceState
            {
                GainDb = (int)Math.Round(gainRaw / 2.0),
                Mute = !unmuted,
                HpVolumeDb = hpRaw / 2.0 - 60.0,
                Phantom = phantom,
                LowImpedance = lowZ,
                Crossfade = 100,   // not a hardware feature here; neutral centre
            };
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    public void SetGainDb(int db) => Set(GainCtl, (Math.Clamp(db, 0, 75) * 2).ToString());

    public void SetMute(bool on) => Set(MuteCtl, on ? "off" : "on");

    public void SetHpVolumeDb(double db)
        => Set(HpCtl, ((int)Math.Round((Math.Clamp(db, -60.0, 0.0) + 60.0) * 2)).ToString());

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
        try
        {
            var blocks = new Dictionary<string, string>
            {
                ["alsa"] = $"card={_card} gain={Get(GainCtl)} capture={Get(MuteCtl)} hp={Get(HpCtl)}",
            };
            if (_handle != IntPtr.Zero)
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
        catch (Exception ex)
        {
            return new Dictionary<string, string> { ["alsa"] = $"error: {ex.Message}" };
        }
    }
}
