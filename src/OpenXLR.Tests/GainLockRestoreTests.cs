using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// The manager keeps gain locks and last settings under the configuration
// directory, which these tests redirect.
[Collection("xdg-config")]
public sealed class GainLockRestoreTests
{
    /// <summary>
    /// A dock that forgets its settings when it loses power: it comes back at
    /// the gain its firmware chooses, whatever it was set to before.
    /// </summary>
    private sealed class ForgetfulDock : IAudioDevice
    {
        public int FirmwareGainDb = 43;
        public readonly List<int> GainWrites = [];
        public void Dispose() { }
        public DeviceInfo Info { get; } = new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, Phantom = true, RetainsSettings = false };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = FirmwareGainDb };
        public void SetGainDb(int db) { GainWrites.Add(db); FirmwareGainDb = db; }
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static void WithConfigDir(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static DeviceManager Connected(ForgetfulDock dock)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
            new ConfigurationBuilder().Build(), () => [dock]);
        manager.SweepOnce();
        return manager;
    }

    [Fact]
    public void TheLockedGainIsGivenBackToADockThatForgotIt()
    {
        WithConfigDir(_ =>
        {
            // What the user set and locked, saved as the last settings seen.
            DeviceStateStore.SaveLast("0fd9:00a6", new DeviceState { GainDb = 55 });
            var dock = new ForgetfulDock();          // powers up at 43 dB
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            string? status = manager.RestoreLastState();

            Assert.NotNull(status);
            Assert.Equal([55], dock.GainWrites);      // the lock did not eat the restore
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void TheLockStillRefusesAGainChangeFromAClient()
    {
        WithConfigDir(_ =>
        {
            var dock = new ForgetfulDock { FirmwareGainDb = 55 };
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            string? error = manager.Apply("gain", JsonDocument.Parse("20").RootElement);

            Assert.Equal("gain is locked", error);
            Assert.Empty(dock.GainWrites);
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void AProfileLoadedByHandStillLeavesALockedGainAlone()
    {
        WithConfigDir(_ =>
        {
            var dock = new ForgetfulDock { FirmwareGainDb = 55 };
            DeviceManager manager = Connected(dock);
            Assert.Null(manager.Apply("gainLock", JsonDocument.Parse("true").RootElement));

            Assert.Null(manager.ApplyProfile(new DeviceState { GainDb = 20 }));

            Assert.Empty(dock.GainWrites);
            Assert.Equal(55, manager.Snapshot().State!.GainDb);
        });
    }

    [Fact]
    public void WithoutTheLockNothingAboutRestoringChanges()
    {
        WithConfigDir(_ =>
        {
            DeviceStateStore.SaveLast("0fd9:00a6", new DeviceState { GainDb = 55 });
            var dock = new ForgetfulDock();
            DeviceManager manager = Connected(dock);

            manager.RestoreLastState();

            Assert.Equal([55], dock.GainWrites);
        });
    }
}
