using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// The manager keeps last settings under the configuration directory, which
// these tests redirect.
[Collection("xdg-config")]
public sealed class DeviceSwitchTests
{
    /// <summary>A device that counts how often its transport is released.</summary>
    private sealed class CountingDevice(DeviceInfo info, bool retainsSettings = true) : IAudioDevice
    {
        public bool DisconnectFails;
        public int Disposals;
        public int GainDb = 30;
        public DeviceInfo Info { get; } = info;
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, RetainsSettings = retainsSettings };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect()
        {
            Connected = false;
            if (DisconnectFails) throw new IOException("disconnect failed (test)");
        }
        public void Dispose() { Disposals++; Connected = false; }
        public DeviceState ReadState() => new() { GainDb = GainDb };
        public void SetGainDb(int db) => GainDb = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static DeviceInfo WaveXlr => new("Elgato", "Wave XLR", 0x0fd9, 0x007d);
    private static DeviceInfo XlrDock => new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingDevicesReleasesTheOldTransportEvenWhenDisconnectFails(bool disconnectFails)
    {
        WithConfigDir(() =>
        {
            var wave = new CountingDevice(WaveXlr) { DisconnectFails = disconnectFails };
            var dock = new CountingDevice(XlrDock);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [wave, dock]);
            manager.SweepOnce();
            Assert.True(wave.Connected);

            Assert.Null(manager.SetActiveDevice("0fd9:00a6"));
            Assert.Equal(1, wave.Disposals);
            Assert.False(manager.Snapshot().Connected);

            manager.SweepOnce();
            Assert.True(dock.Connected);
            Assert.Equal(0x00a6, manager.ActiveInfo!.ProductId);
            Assert.Equal(1, wave.Disposals);
        });
    }

    [Fact]
    public void SwitchingDevicesSavesSettingsStillWaitingToBeWritten()
    {
        WithConfigDir(() =>
        {
            var dock = new CountingDevice(XlrDock, retainsSettings: false);
            var wave = new CountingDevice(WaveXlr);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [dock, wave]);
            manager.SweepOnce();
            manager.MarkRestored();
            using var gain = JsonDocument.Parse("44");
            Assert.Null(manager.Apply("gain", gain.RootElement));
            Assert.Null(DeviceStateStore.LoadLast("0fd9:00a6"));   // still inside the one-second debounce

            Assert.Null(manager.SetActiveDevice("0fd9:007d"));
            Assert.Equal(44, DeviceStateStore.LoadLast("0fd9:00a6")?.GainDb);
        });
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-switch-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
