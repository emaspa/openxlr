using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

// The manager keeps last settings under the configuration directory, which
// these tests redirect.
[Collection("xdg-config")]
public sealed class RestoreRetryTests
{
    private const string Id = "0fd9:00a6";

    /// <summary>
    /// A dock back from a power loss at its firmware defaults, whose mute
    /// write fails the first <see cref="MuteFailures"/> times, as one USB
    /// transfer error in the restore burst would.
    /// </summary>
    private sealed class FlakyDock : IAudioDevice
    {
        public int MuteFailures;
        public int MuteAttempts;
        public int GainDb = 43;
        public bool Mute, LowCut, Phantom;
        public void Dispose() { }
        public DeviceInfo Info { get; init; } = new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);
        public DeviceCapabilities Capabilities { get; init; } = new() { Gain = true, Mute = true, LowCut = true, Phantom = true, RetainsSettings = false };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() => new() { GainDb = GainDb, Mute = Mute, LowCut = LowCut, Phantom = Phantom };
        public void SetGainDb(int db) => GainDb = db;
        public void SetMute(bool on)
        {
            MuteAttempts++;
            if (MuteFailures > 0)
            {
                MuteFailures--;
                throw new IOException("control transfer failed: LIBUSB_ERROR_PIPE");
            }
            Mute = on;
        }
        public void SetLowCut(bool on) => LowCut = on;
        public void SetPhantom(bool on) => Phantom = on;
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static readonly DeviceState Saved = new() { GainDb = 55, Mute = true, LowCut = true, Phantom = true };

    private static void WithConfigDir(Action body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static DeviceManager Connected(FlakyDock dock, ManualTime time)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
            new ConfigurationBuilder().Build(), () => [dock], time: time);
        manager.SweepOnce();
        return manager;
    }

    private static void AssertSaved(DeviceState? state)
    {
        Assert.NotNull(state);
        Assert.Equal(55, state.GainDb);
        Assert.True(state.Mute);
        Assert.True(state.LowCut);
        Assert.True(state.Phantom);
    }

    [Fact]
    public void OneFailedWriteDoesNotSkipTheOtherControlsAndIsTriedAgain()
    {
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(Id, Saved);
            var dock = new FlakyDock { MuteFailures = 1 };
            var time = new ManualTime();
            using DeviceManager manager = Connected(dock, time);

            manager.RestoreLastState();

            // Mute comes before low cut and phantom power in the restore.
            Assert.Equal(55, dock.GainDb);
            Assert.False(dock.Mute);
            Assert.True(dock.LowCut);
            Assert.True(dock.Phantom);

            time.Advance(DeviceManager.RestoreRetryDelay);
            manager.SweepOnce();

            Assert.True(dock.Mute);
            Assert.Equal(2, dock.MuteAttempts);
            AssertSaved(manager.Snapshot().State);
            manager.SuspendSession();
            AssertSaved(DeviceStateStore.LoadLast(Id));
        });
    }

    [Fact]
    public void ARestoreThatFailedPartwayDoesNotReplaceTheSavedSettings()
    {
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(Id, Saved);
            var dock = new FlakyDock { MuteFailures = 1 };
            var time = new ManualTime();
            using DeviceManager manager = Connected(dock, time);

            manager.RestoreLastState();
            manager.SweepOnce();            // the poll reads the half-restored unit
            manager.SuspendSession();       // and anything pending is written out

            AssertSaved(DeviceStateStore.LoadLast(Id));
        });
    }

    [Fact]
    public void AGivenUpRestoreNamesTheUnitByModelOnTheTimeline()
    {
        // An additional interface keeps its settings under its instance id,
        // which the timeline, part of the diagnostics archive, never shows.
        const string instance = "0fd9:00a6:AAY3H5481241GS";
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(instance, Saved);
            var dock = new FlakyDock { MuteFailures = int.MaxValue };
            var time = new ManualTime();
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance,
                new ConfigurationBuilder().Build(), () => [dock], time: time);
            manager.SetSessionStorageId(instance);
            manager.SweepOnce();

            manager.RestoreLastState();
            for (int i = 0; i < DeviceManager.RestoreAttempts; i++)
            {
                time.Advance(DeviceManager.RestoreRetryDelay);
                manager.SweepOnce();
            }

            Assert.Equal(DeviceManager.RestoreAttempts, dock.MuteAttempts);
            string entry = Assert.Single(manager.Timeline.Snapshot().Events,
                e => e.Text.StartsWith("restoring the last settings", StringComparison.Ordinal)).Text;
            Assert.StartsWith($"restoring the last settings of {dock.Info.DisplayName} was given up", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("AAY3H5481241GS", entry, StringComparison.Ordinal);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void ARestoreThatKeepsFailingIsGivenUpAndPersistenceResumes()
    {
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(Id, Saved);
            var dock = new FlakyDock { MuteFailures = int.MaxValue };
            var time = new ManualTime();
            using DeviceManager manager = Connected(dock, time);

            manager.RestoreLastState();
            for (int i = 0; i < DeviceManager.RestoreAttempts + 2; i++)
            {
                time.Advance(DeviceManager.RestoreRetryDelay);
                manager.SweepOnce();
            }

            Assert.Equal(DeviceManager.RestoreAttempts, dock.MuteAttempts);
            // Until something changes, the saved settings stay as they were.
            manager.SuspendSession();
            AssertSaved(DeviceStateStore.LoadLast(Id));
        });
    }

    [Fact]
    public void AChangeAfterAGivenUpRestoreIsSavedAgain()
    {
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(Id, Saved);
            var dock = new FlakyDock { MuteFailures = int.MaxValue };
            var time = new ManualTime();
            using DeviceManager manager = Connected(dock, time);

            manager.RestoreLastState();
            for (int i = 0; i < DeviceManager.RestoreAttempts; i++)
            {
                time.Advance(DeviceManager.RestoreRetryDelay);
                manager.SweepOnce();
            }
            using var gain = JsonDocument.Parse("30");
            Assert.Null(manager.Apply("gain", gain.RootElement));
            manager.SuspendSession();

            Assert.Equal(30, DeviceStateStore.LoadLast(Id)!.GainDb);
        });
    }

    [Fact]
    public void AProfileLoadedDuringTheRetriesEndsThem()
    {
        WithConfigDir(() =>
        {
            DeviceStateStore.SaveLast(Id, Saved);
            var dock = new FlakyDock { MuteFailures = 1 };
            var time = new ManualTime();
            using DeviceManager manager = Connected(dock, time);

            manager.RestoreLastState();
            Assert.Null(manager.ApplyProfile(new DeviceState { GainDb = 20 }, restoring: true));
            manager.MarkRestored();
            time.Advance(DeviceManager.RestoreRetryDelay);
            manager.SweepOnce();

            Assert.Equal(1, dock.MuteAttempts);
            Assert.Equal(20, dock.GainDb);
        });
    }
}
