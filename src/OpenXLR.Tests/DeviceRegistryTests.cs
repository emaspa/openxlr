using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The registry's table order is the daemon's preference when nothing is
/// chosen, so the Wave:3, a USB microphone, comes after every XLR interface,
/// the Wave:3 MK.2 beside it, and the Wave:1, which shares the Wave:3's
/// backend, after both. Devices are constructed,
/// never opened.
/// </summary>
public sealed class DeviceRegistryTests
{
    private static int[] Pids(IEnumerable<(ushort, ushort)> attached)
    {
        IReadOnlyList<IAudioDevice> found = DeviceRegistry.Match(attached);
        try { return [.. found.Select(d => (int)d.Info.ProductId)]; }
        finally { foreach (IAudioDevice d in found) d.Dispose(); }
    }

    [Fact]
    public void AnXlrInterfaceComesBeforeTheWave3WhateverTheBusOrder()
    {
        Assert.Equal([0x00b4, 0x007d, 0x0070], Pids([(0x0fd9, 0x0070), (0x0fd9, 0x007d), (0x1234, 0x5678), (0x0fd9, 0x00b4)]));
        Assert.Equal([0x00a6, 0x0070], Pids([(0x0fd9, 0x0070), (0x0fd9, 0x00a6)]));
        Assert.Equal([0x00b6, 0x00c7, 0x0070], Pids([(0x0fd9, 0x00c7), (0x0fd9, 0x0070), (0x0fd9, 0x00b6)]));
        Assert.Equal([0x007d, 0x0070, 0x006e], Pids([(0x0fd9, 0x006e), (0x0fd9, 0x0070), (0x0fd9, 0x007d)]));
        Assert.Equal([0x00b4, 0x00c7, 0x0070, 0x00ca, 0x006e],
            Pids([(0x0fd9, 0x00ca), (0x0fd9, 0x006e), (0x0fd9, 0x0070), (0x0fd9, 0x00c7), (0x0fd9, 0x00b4)]));
    }

    [Fact]
    public void AloneOnTheBusTheWave3Mk2IsDetected()
        => Assert.Equal([0x00ca], Pids([(0x0fd9, 0x00ca)]));

    [Fact]
    public void AloneOnTheBusTheWave1IsDetected()
        => Assert.Equal([0x006e], Pids([(0x0fd9, 0x006e)]));

    [Fact]
    public void AloneOnTheBusTheWave3IsDetected()
        => Assert.Equal([0x0070], Pids([(0x0fd9, 0x0070)]));

    [Fact]
    public void UnknownIdsAndAnEmptyBusGiveNothing()
    {
        Assert.Empty(Pids([(0x1234, 0x5678), (0x0fd9, 0x0071)]));   // 0071 is the Wave:3's DFU id, not a device to drive
        Assert.Empty(Pids([]));
    }

    [Fact]
    public void EveryAttachedUnitGetsItsOwnBackend()
        => Assert.Equal([0x007d, 0x007d, 0x0070], Pids([(0x0fd9, 0x0070), (0x0fd9, 0x007d), (0x0fd9, 0x007d)]));

    [Fact]
    public void TheSysfsScanReadsDevicesAndPassesOverInterfacesAndHubs()
    {
        string root = Directory.CreateTempSubdirectory("openxlr-sysfs-").FullName;
        try
        {
            void Entry(string name, params (string File, string Text)[] files)
            {
                string dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                foreach ((string file, string text) in files) File.WriteAllText(Path.Combine(dir, file), text);
            }
            Entry("1-4", ("idVendor", "0fd9\n"), ("idProduct", "00b4\n"), ("busnum", "1\n"), ("devnum", "7\n"), ("serial", "AB12\n"));
            Entry("1-4:1.0");                                            // an interface: no idVendor at all
            Entry("1-4:1.1", ("idVendor", "0fd9\n"), ("idProduct", "0070\n"), ("busnum", "1\n"), ("devnum", "7\n"));
            Entry("usb1", ("idVendor", "1d6b\n"), ("idProduct", "0002\n"), ("busnum", "1\n"), ("devnum", "1\n"));
            Entry("2-1", ("idVendor", "0fd9\n"), ("idProduct", "0070\n"), ("busnum", "2\n"), ("devnum", "3\n"));
            Entry("2-2", ("idVendor", "zzzz\n"), ("idProduct", "0070\n"), ("busnum", "2\n"), ("devnum", "4\n"));
            Entry("2-3", ("idVendor", "046d\n"), ("idProduct", "085e\n"), ("busnum", "2\n"), ("devnum", "5\n"));

            var found = DeviceRegistry.EnumerateUsbLocations(root).ToArray();

            Assert.Equal(
                [((ushort)0x0fd9, (ushort)0x00b4, new OpenXLR.Core.UsbLocation(1, 7, "1-4", "AB12")),
                 ((ushort)0x0fd9, (ushort)0x0070, new OpenXLR.Core.UsbLocation(2, 3, "2-1", null))],
                found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
