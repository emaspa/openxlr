namespace OpenXLR.Core.Devices;

/// <summary>
/// Discovers which supported audio interfaces are attached and constructs the
/// matching <see cref="IAudioDevice"/>. New brands are added by registering a
/// (VID, PID) -> factory entry, so the daemon, UI, and plugin need no changes.
///
/// Detection reads sysfs only (/sys/bus/usb/devices), so it works without USB
/// permissions or the udev rule; opening the device for control still needs it.
///
/// The order of the table is also the daemon's preference when several
/// supported interfaces are attached and none is chosen: <see cref="DetectAll"/>
/// lists them in this order. The XLR interfaces come first: a Wave:3,
/// Wave:3 MK.2 or Wave:1 beside one is usually a second microphone, so the
/// USB microphones come last, in that order, and drive the mixer only when no
/// interface is on the bus or the user picks them.
/// </summary>
public static class DeviceRegistry
{
    private readonly record struct Key(ushort Vid, ushort Pid);

    private static readonly (Key Key, Func<IAudioDevice> Make)[] Factories =
    [
        (new Key(WaveXlrProDevice.VendorId, WaveXlrProDevice.ProductId), () => new WaveXlrProDevice()),
        (new Key(Mk1ClassProtocolDevice.VendorId, WaveXlrMk1Device.ProductId), () => new WaveXlrMk1Device()),
        (new Key(XlrDockDevice.VendorId, XlrDockDevice.ProductId), () => new XlrDockDevice()),
        (new Key(WaveXlrMk2Device.VendorId, WaveXlrMk2Device.ProductId), () => new WaveXlrMk2Device()),
        (new Key(XlrDockMk2Device.VendorId, XlrDockMk2Device.ProductId), () => new XlrDockMk2Device()),
        (new Key(Wave3Device.VendorId, Wave3Device.ProductId), () => new Wave3Device()),   // after the XLR interfaces
        (new Key(Wave3Mk2Device.VendorId, Wave3Mk2Device.ProductId), () => new Wave3Mk2Device()),
        (new Key(Wave1Device.VendorId, Wave1Device.ProductId), () => new Wave1Device()),   // last: the Wave:3's backend under the Wave:1's id
        // Add more brands/models here, e.g.:
        // (new Key(0x1220, 0x8fe0), () => new GoXlrDevice()),
    ];

    /// <summary>Every supported device currently attached, in the table's order.</summary>
    public static IReadOnlyList<IAudioDevice> DetectAll()
    {
        var attached = EnumerateUsbLocations(SysfsRoot).ToArray();
        var duplicateSerials = attached.Where(a => !string.IsNullOrEmpty(a.Location.Serial))
            .GroupBy(a => (a.Vid, a.Pid, a.Location.Serial)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        attached = [.. attached.Select(a => duplicateSerials.Contains((a.Vid, a.Pid, a.Location.Serial))
            ? (a.Vid, a.Pid, a.Location with { Serial = null }) : a)];
        var result = new List<IAudioDevice>();
        foreach (var (key, make) in Factories)
            foreach (var unit in attached.Where(a => a.Vid == key.Vid && a.Pid == key.Pid))
                result.Add(UsbTransport.At(unit.Location, make));
        return result;
    }

    /// <summary>The backends for these attached ids, in the table's order, one per attached unit.</summary>
    internal static IReadOnlyList<IAudioDevice> Match(IEnumerable<(ushort Vid, ushort Pid)> attached)
    {
        List<Key> keys = [.. attached.Select(a => new Key(a.Vid, a.Pid))];
        var found = new List<IAudioDevice>();
        foreach ((Key key, Func<IAudioDevice> make) in Factories)
            for (int n = keys.Count(k => k == key); n > 0; n--) found.Add(make());
        return found;
    }

    /// <summary>
    /// The first supported device attached, or null. With several attached,
    /// OPENXLR_DEVICE (a hex product id, e.g. "007d") picks which one.
    /// </summary>
    public static IAudioDevice? DetectFirst()
    {
        IReadOnlyList<IAudioDevice> all = DetectAll();
        if (all.Count == 0) return null;
        string? want = Environment.GetEnvironmentVariable("OPENXLR_DEVICE");
        if (want is not null && ushort.TryParse(want, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            return all.FirstOrDefault(d => d.Info.ProductId == pid) ?? all[0];
        return all[0];
    }

    private const string SysfsRoot = "/sys/bus/usb/devices";

    /// <summary>
    /// The supported units under a sysfs USB device directory. The daemon
    /// scans it ten times a second, so entries that cannot be a unit are
    /// passed over by name before any file is opened: an interface
    /// ("1-4:1.0") has no idVendor, and asking for it raised and caught an
    /// exception per interface per scan.
    /// </summary>
    internal static IEnumerable<(ushort Vid, ushort Pid, UsbLocation Location)> EnumerateUsbLocations(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (name.Contains(':') || name.StartsWith("usb", StringComparison.Ordinal)) continue;   // an interface, or a root hub
            ushort vid, pid;
            byte bus, address;
            string? serial;
            try
            {
                string vendorPath = Path.Combine(dir, "idVendor");
                if (!File.Exists(vendorPath) || !TryReadHexId(vendorPath, out vid) || !TryReadHexId(Path.Combine(dir, "idProduct"), out pid)) continue;
                if (!Factories.Any(f => f.Key == new Key(vid, pid))) continue;
                bus = byte.Parse(File.ReadAllText(Path.Combine(dir, "busnum")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                address = byte.Parse(File.ReadAllText(Path.Combine(dir, "devnum")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, "serial");
                serial = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                if (serial is { Length: > 128 } || serial?.Any(char.IsControl) == true) serial = null;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IOException or UnauthorizedAccessException) { continue; }
            if (bus == 0 || address == 0) continue;
            yield return (vid, pid, new UsbLocation(bus, address, name, serial));
        }
    }

    /// <summary>
    /// A four-digit hex id file such as idVendor ("0fd9\n"), read into a
    /// stack buffer: every attached device is read twice per scan, and
    /// File.ReadAllText allocated a stream and its buffers each time.
    /// </summary>
    private static bool TryReadHexId(string path, out ushort id)
    {
        id = 0;
        Span<byte> buffer = stackalloc byte[16];
        int read;
        using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path))
            read = RandomAccess.Read(handle, buffer, 0);
        ReadOnlySpan<byte> text = buffer[..read].Trim("\n "u8);
        return System.Buffers.Text.Utf8Parser.TryParse(text, out id, out int used, 'x') && used == text.Length && text.Length > 0;
    }
}
