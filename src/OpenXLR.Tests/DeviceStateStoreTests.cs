using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

// Both stores read XDG_CONFIG_HOME, which these tests redirect, so they must not run in parallel.
[Collection("xdg-config")]
public sealed class DeviceStateStoreTests
{
    [Fact]
    public void LastAndDefaultsRoundTripWithoutDaemonStamps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            const string dev = "0fd9:007d";
            Assert.Null(DeviceStateStore.LoadLast(dev));
            Assert.Null(DeviceStateStore.LoadDefaults(dev));

            var seen = new DeviceState
            {
                GainDb = 42, Mute = true, Phantom = true, HpVolumeDb = -12.5, LowImpedance = true,
                GainLocked = true, PhantomSettling = true, PhantomSettleSeconds = 9,
            };
            DeviceStateStore.SaveLast(dev, seen);
            DeviceState? last = DeviceStateStore.LoadLast(dev);
            Assert.NotNull(last);
            Assert.Equal(DeviceStateStore.Hardware(seen), last);
            Assert.False(last.GainLocked);           // daemon stamps are not hardware state
            Assert.False(last.PhantomSettling);
            Assert.Equal(0, last.PhantomSettleSeconds);
            Assert.Equal(42, last.GainDb);
            Assert.Equal(-12.5, last.HpVolumeDb);

            var boot = new DeviceState { GainDb = 75, HpVolumeDb = 0 };
            DeviceStateStore.SaveDefaults(dev, boot);
            Assert.Equal(boot, DeviceStateStore.LoadDefaults(dev));
            Assert.Equal(DeviceStateStore.Hardware(seen), DeviceStateStore.LoadLast(dev));   // separate files

            DeviceStateStore.ClearLast(dev);
            Assert.Null(DeviceStateStore.LoadLast(dev));
            Assert.Equal(boot, DeviceStateStore.LoadDefaults(dev));   // the defaults survive a reset
            DeviceStateStore.ClearLast(dev);                           // clearing twice is fine
            Assert.Empty(ProfileStore.List(dev));                      // nothing shows up as a profile
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void OnlyTheDevicesWithoutMemoryAreRestored()
    {
        Assert.True(new WaveXlrProDevice().Capabilities.RetainsSettings);
        Assert.True(new WaveXlrMk2Device().Capabilities.RetainsSettings);
        Assert.False(new WaveXlrMk1Device().Capabilities.RetainsSettings);
        Assert.False(new XlrDockDevice().Capabilities.RetainsSettings);
        Assert.False(new XlrDockMk2Device().Capabilities.RetainsSettings);
    }
}
