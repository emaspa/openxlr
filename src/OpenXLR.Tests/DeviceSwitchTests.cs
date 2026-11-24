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

    [Fact]
    public void SettingsAreSavedASecondLaterEvenWhenTheWallClockStepsBack()
    {
        WithConfigDir(() =>
        {
            var time = new ManualTime();
            var dock = new CountingDevice(XlrDock, retainsSettings: false);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [dock], time: time);
            manager.SweepOnce();
            manager.MarkRestored();
            using var gain = JsonDocument.Parse("44");
            Assert.Null(manager.Apply("gain", gain.RootElement));
            manager.SweepOnce();
            Assert.Null(DeviceStateStore.LoadLast("0fd9:00a6"));   // inside the one-second debounce

            // A time sync sets the clock back an hour; the debounce still ends a second on.
            time.StepWallClock(TimeSpan.FromHours(-1));
            time.Advance(TimeSpan.FromSeconds(1));
            manager.SweepOnce();
            Assert.Equal(44, DeviceStateStore.LoadLast("0fd9:00a6")?.GainDb);
        });
    }

    /// <summary>
    /// A strip bound to a source never stops the unit being driven: with the
    /// software mixer on, a binding on either strip leaves the unit open
    /// through every sweep, its controls answering and the picker working.
    /// </summary>
    [Theory]
    [InlineData("xlr1")]
    [InlineData("xlr2")]
    public void ABindingOnEitherStripNeverReleasesUsb(string strip)
    {
        WithConfigDir(() =>
        {
            new DaemonSettings { Submixer = true }.Save();
            Assert.Null(new OpenXLR.Core.Mixing.MixerSettings
                { InputBindings = new() { [strip] = new("unplugged.microphone", 0, 1) } }.Save());
            var wave = new CountingDevice(WaveXlr);
            var dock = new CountingDevice(XlrDock);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [wave, dock]);
            for (int i = 0; i < 10; i++) manager.SweepOnce();
            Assert.True(wave.Connected);
            Assert.Equal(0, wave.Disposals);
            Assert.True(manager.Snapshot().Connected);
            manager.MarkRestored();
            using var gain = JsonDocument.Parse("45");
            Assert.Null(manager.Apply("gain", gain.RootElement));
            Assert.Equal(45, wave.GainDb);
            Assert.Null(manager.SetActiveDevice("0fd9:00a6"));
            manager.SweepOnce();
            Assert.True(dock.Connected);
            manager.SuspendSession();
        });
    }

    /// <summary>
    /// A saved binding of XLR 1 to another source leaves USB control alone
    /// whether or not the software mixer runs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASavedInputBindingKeepsUsbControlWithOrWithoutTheMixer(bool mixerEnabled)
    {
        WithConfigDir(() =>
        {
            new DaemonSettings { Submixer = mixerEnabled }.Save();
            Assert.Null(new OpenXLR.Core.Mixing.MixerSettings
                { InputBindings = new() { ["xlr1"] = new("unplugged.microphone", 0, 1) } }.Save());
            var wave = new CountingDevice(WaveXlr);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [wave]);
            manager.SweepOnce();
            Assert.True(wave.Connected);
            Assert.Equal(0, wave.Disposals);
            Assert.Equal(new OpenXLR.Core.Mixing.SourceBinding("unplugged.microphone", 0, 1),
                OpenXLR.Core.Mixing.MixerSettings.Load()!.InputBindings!["xlr1"]);
            manager.SuspendSession();
        });
    }

    /// <summary>
    /// The nodes of every attached unit are recognised, the driven one and
    /// one plugged in later beside it, from the detection each sweep does.
    /// </summary>
    [Fact]
    public void TheNodesOfAttachedUnitsAreRecognisedWhileOneIsDriven()
    {
        WithConfigDir(() =>
        {
            var dock = new CountingDevice(XlrDock with { Location = new UsbLocation(1, 3, "1-3", "DOCK1") });
            var attached = new List<IAudioDevice> { dock };
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [.. attached]);
            const string node = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_SER1-00.mono-fallback";
            manager.SweepOnce();
            Assert.True(dock.Connected);
            Assert.Null(manager.SupportedUnitFor(node));
            var wave = new CountingDevice(WaveXlr with { Location = new UsbLocation(1, 4, "1-4", "SER1") });
            attached.Add(wave);
            manager.SweepOnce();
            Assert.Equal((wave.Info.InstanceId, "Elgato Wave XLR"), manager.SupportedUnitFor(node));
            Assert.True(dock.Connected);
            Assert.False(wave.Connected);   // the picker chose nothing, so the first unit stays the primary
            Assert.Null(manager.SupportedUnitFor("alsa_input.usb-046d_Webcam-02.analog-stereo"));
            manager.SuspendSession();
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
