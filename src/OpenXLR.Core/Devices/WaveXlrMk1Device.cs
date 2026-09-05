using System.Buffers.Binary;

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
/// Note: the kernel driver also exposes mute and headphone volume as ALSA
/// card controls backed by separate state. This backend drives the firmware
/// only; if the two visibly diverge in practice, port openwave's two-way
/// ALSA sync.
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

    private static IntPtr _ctx = IntPtr.Zero;
    private IntPtr _handle = IntPtr.Zero;
    private readonly object _lock = new();

    public abstract DeviceInfo Info { get; }
    public abstract DeviceCapabilities Capabilities { get; }

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
        try { return LibUsb.ControlTransfer(_handle, requestType, request, value, Index, data, (ushort)length, 1000); }
        catch (UsbHungException) { _handle = IntPtr.Zero; throw; }
    }

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
        byte[] cfg = Read(BlockConfig, ConfigLen);
        edit(cfg);
        Write(BlockConfig, cfg);
    }

    public DeviceState ReadState()
    {
        byte[] c = Read(BlockConfig, ConfigLen);
        return new DeviceState
        {
            GainDb = (int)Math.Round(BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(OffGain)) / 256.0),
            Mute = c[OffMute] != 0,
            Phantom = c[OffPhantom] != 0,
            HpVolumeDb = Math.Max(-60.0, BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(OffHpVol)) / 256.0),
            LowImpedance = HasLowZ && c[OffLowZ] != 0,
            Crossfade = 100,   // not mapped in this protocol; keep the neutral centre
        };
    }

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
        return blocks;
    }
}

/// <summary>The original Wave XLR (MK.1, 0fd9:007d). Protocol from the
/// openwave project; gain, mute, headphone volume, low impedance and phantom
/// verified on two units by community testers (issues #6 and earlier).</summary>
public sealed class WaveXlrMk1Device : Mk1ClassProtocolDevice
{
    public const ushort ProductId = 0x007D;

    protected override int ConfigLen => 34;
    protected override bool HasLowZ => true;

    public override DeviceInfo Info { get; } = new("Elgato", "Wave XLR", VendorId, ProductId);

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
        RetainsSettings = false,
    };
}
