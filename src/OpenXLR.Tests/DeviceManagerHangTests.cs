using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    /// <summary>
    /// A Wave XLR that hangs every read while <see cref="Hanging"/> is set,
    /// and the next <see cref="WriteHangs"/> writes of its gain or mute.
    /// </summary>
    private sealed class FlakyDevice : IAudioDevice
    {
        public bool Hanging;
        public int WriteHangs;
        public bool Mute;
        public string? Note;
        public string? ConnectionNote => Note;
        public int Disposals;
        public void Dispose() => Disposals++;
        public int Connects, Hangs;
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR", 0x0fd9, 0x007d);
        public DeviceCapabilities Capabilities { get; init; } = new() { Gain = true, Mute = true, HpVolume = true };
        public bool Connected { get; private set; }
        public void Connect() { Connects++; Connected = true; }
        public void Disconnect() => Connected = false;
        /// <summary>The USB helper exited on its own: the transport is closed, nothing failed in a transfer.</summary>
        public void HelperExits() => Connected = false;
        public DeviceState ReadState()
        {
            if (!Hanging) return new DeviceState { GainDb = 40, Mute = Mute };
            Hang();
            return null!;
        }
        private void Hang()
        {
            Hangs++;
            Connected = false;   // what a real transport does once the helper is killed
            throw new UsbHungException("USB control transfer did not return (test)");
        }
        private void Write(Action write)
        {
            if (WriteHangs > 0) { WriteHangs--; Hang(); }
            write();
        }
        public void SetGainDb(int db) => Write(() => { });
        public void SetMute(bool on) => Write(() => Mute = on);
        public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static int HungCount(DeviceManager manager, IAudioDevice device)
        => ((HungTransferPolicy)typeof(DeviceManager).GetField("_hung", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!).HungCount(device.Info.InstanceId);

    private static void SetField(DeviceManager manager, string name, object? value)
        => typeof(DeviceManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, value);

    private static void WithConfigDir(Action body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-hang-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        TimeSpan prevDelay = DeviceManager.HungReconnectDelay;
        try { body(); }
        finally
        {
            DeviceManager.HungReconnectDelay = prevDelay;
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AHangDuringAProfileRecallBacksOffAndCountsTowardTheSetAside()
    {
        WithConfigDir(() =>
        {
            DeviceManager.HungReconnectDelay = TimeSpan.FromMilliseconds(200);
            var device = new FlakyDevice();
            var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);

            for (int hang = 1; hang <= HungTransferPolicy.Limit; hang++)
            {
                device.WriteHangs = 1;
                string? error = manager.ApplyProfile(new DeviceState { GainDb = 40, Mute = true }, restoring: true);
                Assert.Contains("did not return", error);
                Assert.Equal(hang, HungCount(manager, device));
                // Dropped and disposed, as after a hang in the poll.
                Assert.False(manager.Snapshot().Connected);
                Assert.Equal(hang, device.Disposals);

                int connects = device.Connects;
                manager.SweepOnce();
                Assert.Equal(connects, device.Connects);   // the hang backoff holds the reopen
                Thread.Sleep(DeviceManager.HungReconnectDelay + TimeSpan.FromMilliseconds(100));
                manager.SweepOnce();
                Assert.Equal(hang < HungTransferPolicy.Limit ? connects + 1 : connects, device.Connects);
            }
            Assert.Contains("no longer driven", manager.Warning);
            Assert.False(manager.Snapshot().Connected);
        });
    }

    [Fact]
    public void AHangDuringARestoreIsCountedAndTheRestoreIsTriedAgainAfterTheReconnect()
    {
        WithConfigDir(() =>
        {
            DeviceManager.HungReconnectDelay = TimeSpan.Zero;
            DeviceStateStore.SaveLast("0fd9:007d", new DeviceState { GainDb = 40, Mute = true });
            var device = new FlakyDevice { Capabilities = new() { Gain = true, Mute = true, HpVolume = true, RetainsSettings = false } };
            var time = new ManualTime();
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device], time: time);
            manager.SweepOnce();

            device.WriteHangs = 1;
            Assert.Null(manager.RestoreLastState());
            Assert.Equal(1, HungCount(manager, device));
            Assert.False(manager.Snapshot().Connected);
            Assert.Equal(1, device.Disposals);
            Assert.True(manager.RestoreRetrying);

            manager.SweepOnce();                        // reopened, the same unit
            Assert.True(manager.Snapshot().Connected);
            Assert.False(device.Mute);
            time.Advance(DeviceManager.RestoreRetryDelay);
            manager.SweepOnce();                        // the restore's next attempt
            Assert.True(device.Mute);
            Assert.False(manager.RestoreRetrying);
            Assert.True(manager.Snapshot().State!.Mute);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void AHangReadingAUnitThatStayedPluggedInIsCountedAndTheRestoreWaits()
    {
        WithConfigDir(() =>
        {
            DeviceManager.HungReconnectDelay = TimeSpan.Zero;
            DeviceStateStore.SaveLast("0fd9:007d", new DeviceState { GainDb = 40, Mute = true });
            var device = new FlakyDevice { Capabilities = new() { Gain = true, Mute = true, HpVolume = true, RetainsSettings = false } };
            var time = new ManualTime();
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device], time: time);
            manager.SweepOnce();
            // The arrival of a unit that stayed on the bus, with no read of
            // it yet: the restore reads what it holds first.
            SetField(manager, "_unwatchedArrival", true);
            SetField(manager, "_last", null);
            device.Hanging = true;

            Assert.Null(manager.RestoreLastState());
            Assert.Equal(1, HungCount(manager, device));
            Assert.False(manager.Snapshot().Connected);
            Assert.True(manager.RestoreRetrying);

            device.Hanging = false;
            manager.SweepOnce();
            time.Advance(DeviceManager.RestoreRetryDelay);
            manager.SweepOnce();
            Assert.True(device.Mute);
            Assert.False(manager.RestoreRetrying);
            manager.SuspendSession();
        });
    }

    [Fact]
    public void AHangReadingTheUnitForAResetIsCounted()
    {
        WithConfigDir(() =>
        {
            var device = new FlakyDevice();
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device]);
            manager.SweepOnce();
            SetField(manager, "_last", null);
            device.Hanging = true;

            Assert.Contains("did not return", manager.ResetToDefaults());
            Assert.Equal(1, HungCount(manager, device));
            Assert.False(manager.Snapshot().Connected);
            Assert.Equal(1, device.Disposals);
        });
    }

    [Fact]
    public void AHangInAnUndoStepWritesNothingMoreToTheDroppedUnit()
    {
        WithConfigDir(() =>
        {
            var device = new FlakyDevice { WriteHangs = 1 };
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device]);
            manager.SweepOnce();

            IReadOnlyList<DevicePatchOutcome> outcomes = manager.ApplyPatch(device.Info.InstanceId,
            [
                new DevicePatchEntry(ControlNames.Gain, 40, 45),
                new DevicePatchEntry(ControlNames.HpVolumeDb, 0.0, -10.0),
            ]);
            Assert.Equal([DevicePatchOutcome.Failed, DevicePatchOutcome.Gone], outcomes);
            Assert.Equal(1, HungCount(manager, device));
            Assert.False(manager.Snapshot().Connected);
        });
    }

    [Fact]
    public void ADeviceWhoseHelperExitedIsDisposedBeforeAnotherIsDriven()
    {
        WithConfigDir(() =>
        {
            var first = new FlakyDevice();
            var second = new FlakyDevice();
            var attached = new List<IAudioDevice> { first };
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [.. attached]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);

            first.HelperExits();
            attached[0] = second;
            manager.SweepOnce();
            Assert.Equal(1, first.Disposals);
            Assert.Equal(0, HungCount(manager, first));   // not a hang
            Assert.Equal(1, second.Connects);
            Assert.True(manager.Snapshot().Connected);

            // With nothing else attached, the closed device is still released.
            second.HelperExits();
            attached.Clear();
            manager.SweepOnce();
            Assert.Equal(1, second.Disposals);
            Assert.False(manager.Snapshot().Connected);
        });
    }

    /// <summary>A device that opens and then answers every read badly.</summary>
    private sealed class BrokenDevice : IAudioDevice
    {
        public int Connects;
        public void Dispose() { }
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR MK.2", 0x0fd9, 0x00b6);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true };
        public bool Connected { get; private set; }
        public void Connect() { Connects++; Connected = true; }
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() => throw new InvalidOperationException("read block 0004: 1 bytes, at least 11 expected");
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void AFailingPollWaitsBeforeTheDeviceIsReopenedAndWaitsLongerEachTime()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-test-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        TimeSpan prevDelay = DeviceManager.PollRetryDelay;
        DeviceManager.PollRetryDelay = TimeSpan.FromMilliseconds(150);
        try
        {
            var device = new BrokenDevice();
            var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device]);

            for (int i = 0; i < 5; i++) manager.SweepOnce();   // connect, fail, drop; then wait
            Assert.Equal(1, device.Connects);
            Thread.Sleep(200);
            manager.SweepOnce();
            Assert.Equal(2, device.Connects);                  // reopened after the first wait
            Thread.Sleep(200);
            manager.SweepOnce();
            Assert.Equal(2, device.Connects);                  // the second wait is twice as long
            Thread.Sleep(150);
            manager.SweepOnce();
            Assert.Equal(3, device.Connects);
        }
        finally
        {
            DeviceManager.PollRetryDelay = prevDelay;
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
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

    [Fact]
    public void TheConnectionNoteTravelsWithTheDeviceInTheState()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-note-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            var quiet = new FlakyDevice();
            var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [quiet]);
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);
            Assert.Null(manager.Snapshot().Device!.Note);

            var noted = new FlakyDevice { Note = "the card has no 'Mic Capture Volume'; driving it through the dock's config block instead" };
            manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [noted]);
            manager.SweepOnce();
            Assert.Equal(noted.Note, manager.Snapshot().Device!.Note);
            Assert.Null(manager.Warning);   // a note is not a warning: the unit works
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class StoppingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    [Fact]
    public void OnceTheHostBeginsToStopNoUnitIsOpenedAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-stopping-" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            using var lifetime = new StoppingLifetime();
            var device = new FlakyDevice();
            var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [device], lifetime);
            manager.SweepOnce();
            Assert.Equal(1, device.Connects);
            device.Disconnect();                 // the unit went away for a moment
            manager.SweepOnce();
            Assert.Equal(2, device.Connects);    // and is opened again while the daemon runs

            lifetime.StopApplication();
            manager.SweepOnce();
            Assert.True(manager.Snapshot().Connected);   // a connected unit is still polled
            device.Disconnect();
            manager.SweepOnce(); manager.SweepOnce();
            Assert.Equal(2, device.Connects);    // but none is opened during the stop
            Assert.False(manager.Snapshot().Connected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
