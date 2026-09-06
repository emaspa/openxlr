using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// The manager reads gain locks and daemon settings from the configuration
// directory, which this test redirects.
[Collection("xdg-config")]
public sealed class DeviceManagerHangTests
{
    /// <summary>A Wave XLR that hangs every read while <see cref="Hanging"/> is set.</summary>
    private sealed class FlakyDevice : IAudioDevice
    {
        public bool Hanging;
        public int Connects, Hangs;
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, HpVolume = true };
        public bool Connected { get; private set; }
        public void Connect() { Connects++; Connected = true; }
        public void Disconnect() => Connected = false;
        public DeviceState ReadState()
        {
            if (!Hanging) return new DeviceState { GainDb = 40 };
            Hangs++;
            Connected = false;   // what a real transport does once the helper is killed
            throw new UsbHungException("USB control transfer did not return (test)");
        }
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void ThreeHangsSetTheDeviceAsideAndAReplugBringsItBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        TimeSpan prevDelay = DeviceManager.HungReconnectDelay;
        DeviceManager.HungReconnectDelay = TimeSpan.Zero;
        try
        {
            var device = new FlakyDevice { Hanging = true };
            var attached = new List<IAudioDevice> { device };
            var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => attached);

            for (int i = 0; i < HungTransferPolicy.Limit; i++) manager.SweepOnce();   // connect, hang, drop; three times
            Assert.Equal(HungTransferPolicy.Limit, device.Hangs);
            Assert.NotNull(manager.Warning);
            Assert.Contains("no longer driven", manager.Warning);

            int connects = device.Connects;
            device.Hanging = false;
            manager.SweepOnce(); manager.SweepOnce();
            Assert.Equal(connects, device.Connects);        // set aside: not reconnected even though it would work now
            Assert.False(manager.Snapshot().Connected);

            attached.Clear();                              // unplugged
            manager.SweepOnce();
            attached.Add(device);                          // and back
            manager.SweepOnce();
            Assert.Null(manager.Warning);
            Assert.Equal(connects + 1, device.Connects);   // driven again with a fresh count
            Assert.True(manager.Snapshot().Connected);
            Assert.Equal(40, manager.Snapshot().State!.GainDb);
        }
        finally
        {
            DeviceManager.HungReconnectDelay = prevDelay;
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
