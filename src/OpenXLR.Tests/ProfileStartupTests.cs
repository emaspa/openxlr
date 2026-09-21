using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ProfileStartupTests
{
    private sealed class Dock : IAudioDevice
    {
        public DeviceInfo Info { get; init; } = new("Elgato", "XLR Dock", 0x0fd9, 0x00a6);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, RetainsSettings = false };
        public bool Connected { get; private set; }
        public int Gain = 75;
        public DeviceState ReadState() => new() { GainDb = Gain };
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public void SetGainDb(int db) => Gain = db;
        public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { }
        public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { }
        public void SetCompressor(bool on) { }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("invalid")]
    public async Task LastSettingsRestoreDoesNotWaitForAPluginScan(string scenario)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            DeviceStateStore.SaveLast("0fd9:00a6", new() { GainDb = 55 });
            if (scenario != "none") ProfileStore.SetRecallOnConnect("0fd9:00a6", "Broken");
            if (scenario is "corrupt" or "invalid")
                OpenXlrPaths.WriteAtomic(Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6", "Broken.json"), BrokenProfile(scenario));
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            await mixer.StartAsync(CancellationToken.None);
            object gate = typeof(WebSocketHub).GetField("_installGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hub)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task scan = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
            Task? recall = null;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                recall = Task.Run(() => hub.RecallOnArrivalAsync(devices.CurrentConnection!.Value));
                await recall.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(55, dock.Gain);
                using var gain = JsonDocument.Parse("60");
                Assert.Null(devices.Apply("gain", gain.RootElement));
                await Task.Delay(1100);
                devices.SweepOnce();
                Assert.Equal(60, DeviceStateStore.LoadLast("0fd9:00a6")!.GainDb);
            }
            finally
            {
                release.Set();
                await scan;
                if (recall is not null) await recall;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ManualRecallReportsAppliedDeviceSettingsWhenTheMixerFails()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            ProfileStore.Save("0fd9:00a6", "Saved", new()
            {
                Device = new() { GainDb = 55 }, Mixer = new(), Presentation = new() { Skin = "deck" },
            });
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);

            var result = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""");

            Assert.False(result.Ok);
            Assert.Equal("device settings were applied, but mixer settings failed: mixer not built (start the daemon with --mixer)", Assert.Single(result.Messages.OfType<ErrorMessage>()).Message);
            Assert.Equal(55, dock.Gain);
            Assert.Null(hub.Snapshot().ActiveProfile);
            Assert.Null(hub.Snapshot().ProfilePresentation);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileCommandsPreserveWindowChoicesAndPublishOnlySuccessfulRecalls()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-presentation-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.True((await hub.ExecuteForApiAsync("""
                {"cmd":"saveProfile","name":"Saved","presentation":{"skin":"deck","compactMixer":true,
                 "sectionOrder":["SubmixerTile","MonitorTile"],"collapsedSections":["InputsTile"]}}
                """)).Ok);
            Assert.Null(hub.Snapshot().ProfilePresentation); // saving does not recall
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Saved"}""")).Ok);
            Assert.Equal("deck", ProfileStore.Load("0fd9:00a6", "Saved")!.Presentation!.Skin);
            Assert.False((await hub.ExecuteForApiAsync("""
                {"cmd":"saveProfile","name":"Saved","presentation":{"sectionOrder":[null]}}
                """)).Ok);
            Assert.Equal("deck", ProfileStore.Load("0fd9:00a6", "Saved")!.Presentation!.Skin);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""")).Ok);
            var first = Assert.IsType<ProfilePresentationMessage>(hub.Snapshot().ProfilePresentation);
            Assert.Equal("deck", first.Settings.Skin);
            Assert.True(first.Settings.CompactMixer);
            Assert.Same(first, hub.Snapshot().ProfilePresentation);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Missing"}""")).Ok);
            Assert.Same(first, hub.Snapshot().ProfilePresentation);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""")).Ok);
            Assert.NotEqual(first.Revision, hub.Snapshot().ProfilePresentation!.Revision);
            ProfileStore.Save("0fd9:00a6", "Legacy", new() { Device = new() { GainDb = 40 } });
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Legacy"}""")).Ok);
            Assert.Null(hub.Snapshot().ProfilePresentation);
            Assert.Equal(40, dock.Gain);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>"corrupt" does not parse; "invalid" parses but fails validation.</summary>
    private static string BrokenProfile(string scenario)
        => scenario == "corrupt" ? "{" : """{"device":{"gainDb":40},"mixer":{"mixVolumes":{"monitor":1e999}}}""";

    [Fact]
    public async Task ManualRecallOfAnInvalidProfileNamesItAndAppliesNothing()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            OpenXlrPaths.WriteAtomic(Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6", "Broken.json"), BrokenProfile("invalid"));
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);

            var result = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Broken"}""");

            Assert.False(result.Ok);
            Assert.Equal("profile 'Broken': Invalid saved mixer field 'mixVolumes': non-finite number.",
                Assert.Single(result.Messages.OfType<ErrorMessage>()).Message);
            Assert.Equal(75, dock.Gain);
            Assert.Null(hub.Snapshot().ActiveProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("switched")]
    [InlineData("replugged")]
    [InlineData("stopping")]
    public async Task ArrivalRejectsStaleWorkAndPreservesLastSettings(string scenario)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-review-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            var attached = new List<IAudioDevice> { dock };
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => attached);
            devices.SweepOnce();
            var arrival = devices.CurrentConnection!.Value;
            if (scenario is "switched" or "replugged")
            {
                dock.Disconnect();
                attached.Clear();
                devices.SweepOnce();
                if (scenario == "switched") dock = new Dock { Info = new("Elgato", "Wave XLR", 0x0fd9, 0x007d) };
                attached.Add(dock);
                devices.SweepOnce();
                Assert.NotEqual(arrival, devices.CurrentConnection!.Value);
            }
            string deviceId = devices.CurrentConnection!.Value.DeviceId;
            DeviceStateStore.SaveLast(deviceId, new() { GainDb = 55 });
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            await mixer.StartAsync(CancellationToken.None);
            bool invalidProfile = scenario is "corrupt" or "invalid" or "missing";
            if (invalidProfile)
            {
                ProfileStore.SetRecallOnConnect(deviceId, "Broken");
                if (scenario != "missing")
                    OpenXlrPaths.WriteAtomic(Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6", "Broken.json"), BrokenProfile(scenario));
            }
            if (scenario == "stopping") lifetime.StopApplication();
            await hub.RecallOnArrivalAsync(arrival);
            Assert.Equal(invalidProfile ? 55 : 75, dock.Gain);
            Assert.Null(hub.Snapshot().ActiveProfile);
            await Task.Delay(1100);
            devices.SweepOnce();
            Assert.Equal(55, DeviceStateStore.LoadLast(deviceId)!.GainDb);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task ManualRecallSupersedesAnArrivalWaitingForInitialization(string scenario)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            if (scenario == "valid")
                ProfileStore.Save("0fd9:00a6", "Automatic", new() { Device = new() { GainDb = 45 } });
            if (scenario == "corrupt")
                OpenXlrPaths.WriteAtomic(Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6", "Automatic.json"), "{");
            DeviceStateStore.SaveLast("0fd9:00a6", new() { GainDb = 35 });
            ProfileStore.Save("0fd9:00a6", "Manual", new() { Device = new() { GainDb = 55 } });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Automatic");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Task recall = hub.RecallOnArrivalAsync(devices.CurrentConnection!.Value);
            Assert.False(recall.IsCompleted);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Manual"}""")).Ok);
            await mixer.StartAsync(CancellationToken.None);
            await recall.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(55, dock.Gain);
            Assert.Equal("Manual", hub.Snapshot().ActiveProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [MonitorPipeWireFact]
    public async Task StartupProfileWinsOverSavedMixerSettings()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new OpenXLR.Core.Mixing.MixerSettings
            {
                UserChannels = [new("game", "Game")], UserMixes = [],
                MixVolumes = new() { ["monitor"] = 0.9 },
            }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            ProfileStore.Save("0fd9:00a6", "Saved", new Profile
            {
                Device = new() { GainDb = 55 },
                Mixer = new() { MixVolumes = new() { ["monitor"] = 0.25 } },
            });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Saved");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.True(mixer.SubmixerEnabled);
            devices.SweepOnce(); // Exercise the actual DeviceArrived subscription.
            Assert.False(mixer.Initialized.IsCompleted);
            try
            {
                await mixer.StartAsync(CancellationToken.None);
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().ActiveProfile == "Saved", TimeSpan.FromSeconds(10)));
                Assert.Equal("Saved", hub.Snapshot().ActiveProfile);
                Assert.Equal(55, dock.Gain);
                Assert.Equal(0.25, mixer.ExportScene()!.MixVolumes["monitor"]);
            }
            finally { await mixer.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivalWaitsForInitializationAndCanBeCancelled(bool cancel)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            devices.SweepOnce();
            ProfileStore.Save("0fd9:00a6", "Saved", new Profile { Device = new() { GainDb = 55 } });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Saved");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            DeviceStateStore.SaveLast("0fd9:00a6", new() { GainDb = 55 });
            Task recall = hub.RecallOnArrivalAsync(devices.CurrentConnection!.Value);
            Assert.False(recall.IsCompleted);
            Assert.Equal(75, dock.Gain);
            if (cancel) lifetime.StopApplication();
            else await mixer.StartAsync(CancellationToken.None);
            await recall.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel ? 75 : 55, dock.Gain);
            Assert.Equal(cancel ? null : "Saved", hub.Snapshot().ActiveProfile);
            await Task.Delay(1100);
            devices.SweepOnce();
            Assert.Equal(55, DeviceStateStore.LoadLast("0fd9:00a6")!.GainDb);
            if (!cancel)
            {
                using var gain = JsonDocument.Parse("75");
                using var locked = JsonDocument.Parse("true");
                Assert.Null(devices.Apply("gain", gain.RootElement));
                Assert.Null(devices.Apply("gainLock", locked.RootElement));
                var loaded = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""");
                Assert.True(loaded.Ok);
                Assert.Equal(55, dock.Gain);
                Assert.True(devices.Snapshot().State!.GainLocked);
                Assert.Equal("gain is locked", devices.Apply("gain", gain.RootElement));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
