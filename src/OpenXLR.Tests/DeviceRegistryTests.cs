using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The registry's table order is the daemon's preference when nothing is
/// chosen, so the Wave:3, a USB microphone, comes after every XLR interface,
/// and the Wave:1, which shares its backend, after it. Devices are constructed,
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
    }

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
}
