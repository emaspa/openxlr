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
/// lists them in this order, so an interface verified on hardware comes before
/// one that is not. The Wave:3 backend is coded from public research and not
/// yet run on the device, so it is last, and drives the mixer only when it is
/// the only supported interface on the bus or the user picks it.
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
        (new Key(Wave3Device.VendorId, Wave3Device.ProductId), () => new Wave3Device()),   // last: not yet run on the device
        // Add more brands/models here, e.g.:
        // (new Key(0x1220, 0x8fe0), () => new GoXlrDevice()),
    ];

    /// <summary>Every supported device currently attached, in the table's order.</summary>
    public static IReadOnlyList<IAudioDevice> DetectAll() => Match(EnumerateUsbIds());

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

    private static IEnumerable<(ushort Vid, ushort Pid)> EnumerateUsbIds()
    {
        string root = "/sys/bus/usb/devices";
        if (!Directory.Exists(root)) yield break;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string vidPath = Path.Combine(dir, "idVendor");
            string pidPath = Path.Combine(dir, "idProduct");
            if (!File.Exists(vidPath) || !File.Exists(pidPath)) continue;

            ushort vid, pid;
            try
            {
                vid = Convert.ToUInt16(File.ReadAllText(vidPath).Trim(), 16);
                pid = Convert.ToUInt16(File.ReadAllText(pidPath).Trim(), 16);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IOException)
            {
                continue;
            }
            yield return (vid, pid);
        }
    }
}
