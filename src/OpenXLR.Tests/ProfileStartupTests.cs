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
    [InlineData("marker-directory")]
    [InlineData("marker-link-loop")]
    [InlineData("profile-link-loop")]
    public async Task UnreadableProfileMetadataDoesNotPreventStateReplies(string failure)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-metadata-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        string profiles = Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6");
        string marker = Path.Combine(profiles, "recall-on-connect");
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [new Dock()]);
            devices.SweepOnce();
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Night"}""")).Ok);
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Night");
            if (failure == "profile-link-loop")
            {
                Directory.Move(profiles, profiles + ".saved");
                Directory.CreateSymbolicLink(profiles, profiles);
            }
            else
            {
                File.Delete(marker);
                if (failure == "marker-directory") Directory.CreateDirectory(marker);
                else File.CreateSymbolicLink(marker, marker);
            }

            var result = await hub.ExecuteForApiAsync("""{"cmd":"getState","requestId":"metadata"}""");
            Assert.True(result.Ok);
            var state = Assert.IsType<StateMessage>(result.Messages[0]);
            Assert.Equal("0fd9:00a6", state.Device!.UsbId);
            Assert.Equal("Night", state.ActiveProfile);
            Assert.Contains("profiles", state.Warning!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(state.RecallOnConnect);
            Assert.Equal(failure == "profile-link-loop" ? [] : new[] { "Night" }, state.Profiles);
            Assert.Null(Assert.Single(result.Messages.OfType<CommandResultMessage>()).Error);

            if (failure == "profile-link-loop")
            {
                File.Delete(profiles);
                Directory.Move(profiles + ".saved", profiles);
            }
            else
            {
                if (failure == "marker-directory") Directory.Delete(marker);
                else File.Delete(marker);
                ProfileStore.SetRecallOnConnect("0fd9:00a6", "Night");
            }
            state = hub.Snapshot();
            Assert.Null(state.Warning);
            Assert.Equal("Night", state.RecallOnConnect);
            Assert.Equal(new[] { "Night" }, state.Profiles);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
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
                Device = new() { GainDb = 55 }, Mixer = new(),
            });
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);

            var result = await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Saved"}""");

            Assert.False(result.Ok);
            Assert.Equal("device settings were applied, but mixer settings failed: mixer not built (start the daemon with --mixer)", Assert.Single(result.Messages.OfType<ErrorMessage>()).Message);
            Assert.Equal(55, dock.Gain);
            Assert.Null(hub.Snapshot().ActiveProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>"corrupt" does not parse; "invalid" parses but fails validation.</summary>
    private static string BrokenProfile(string scenario)
        => scenario == "corrupt" ? "{" : """{"device":{"gainDb":40},"mixer":{"mixVolumes":{"monitor":1e999}}}""";

    [Fact]
    public async Task ARefusedDeletionKeepsTheActiveProfileAndUnrelatedDeletesLeaveItAlone()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-delete-").FullName;
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
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Keep"}""")).Ok);
            Assert.Equal("Keep", hub.Snapshot().ActiveProfile);
            OpenXlrPaths.WriteAtomic(Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6", "recall-on-connect"), "../Outside");

            var failed = await hub.ExecuteForApiAsync("""{"cmd":"deleteProfile","name":"Keep","requestId":"delete"}""");
            Assert.False(failed.Ok);
            Assert.Contains("invalid profile name", Assert.IsType<CommandResultMessage>(failed.Messages[^1]).Error);
            Assert.Equal("Keep", hub.Snapshot().ActiveProfile);
            Assert.NotNull(ProfileStore.Load("0fd9:00a6", "Keep"));

            ProfileStore.SetRecallOnConnect("0fd9:00a6", null);
            ProfileStore.Save("0fd9:00a6", "Other", new());
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"deleteProfile","name":"Other"}""")).Ok);
            Assert.Equal("Keep", hub.Snapshot().ActiveProfile);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"deleteProfile","name":"Keep"}""")).Ok);
            Assert.Null(hub.Snapshot().ActiveProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

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
    [InlineData("oversized")]
    [InlineData("bad-marker")]
    [InlineData("large-marker")]
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
            bool invalidProfile = scenario is "corrupt" or "invalid" or "missing" or "oversized" or "bad-marker" or "large-marker";
            if (invalidProfile)
            {
                ProfileStore.SetRecallOnConnect(deviceId, "Broken");
                string folder = Path.Combine(OpenXlrPaths.ConfigDir, "profiles", "0fd9-00a6");
                if (scenario == "oversized")
                {
                    using var file = File.Create(Path.Combine(folder, "Broken.json"));
                    file.SetLength(ProfileStore.MaxProfileBytes + 1);
                }
                else if (scenario is "bad-marker" or "large-marker")
                    OpenXlrPaths.WriteAtomic(Path.Combine(folder, "recall-on-connect"),
                        scenario == "bad-marker" ? "../Outside" : new string('x', 1025));
                else if (scenario != "missing")
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

    [Fact]
    public async Task ANamedProfileIsRecalledOnConnectWhileAPluginScanRuns()
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
            ProfileStore.Save("0fd9:00a6", "Night", new() { Device = new() { GainDb = 45 } });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Night");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            await mixer.StartAsync(CancellationToken.None);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            hub.Catalogue = () => [];
            hub.RefreshCatalogue = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); };
            Task<ApiCommandResult> scan = hub.ExecuteForApiAsync("""{"cmd":"rescanPlugins"}""");
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the rescan did not start");
                await Task.Run(() => hub.RecallOnArrivalAsync(devices.CurrentConnection!.Value)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(45, dock.Gain);
                Assert.Equal("Night", hub.Snapshot().ActiveProfile);
                Assert.False(scan.IsCompleted);
            }
            finally { release.Set(); }
            Assert.True((await scan.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
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
                Assert.Equal(0.25, mixer.ExportScene().Scene!.MixVolumes["monitor"]);
            }
            finally { await mixer.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A profile recalled on connect lands on the saved settings at every
    /// start, so it must not undo where this machine runs a plugin: an
    /// insert the saved chain still has keeps its host and its own block, and
    /// one only the profile has comes back as the profile saved it. The
    /// plugins are invented and never load.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task ARecallOnConnectKeepsTheWiringOfEveryInsertThatStays()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        string dir = Directory.CreateTempSubdirectory("openxlr-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        static OpenXLR.Core.Mixing.InsertDefinition Vst3(string id, string classId)
            => new() { Id = id, Kind = "vst3", Plugin = classId };
        const string Voice = "00112233445566778899AABBCCDDEEFF", Verb = "FFEEDDCCBBAA99887766554433221100";
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new OpenXLR.Core.Mixing.MixerSettings
            {
                UserMixes = [],
                Inserts = new()
                {
                    ["xlr1"] = [Vst3("voice", Voice) with { WindowsHost = "openxlr", OwnBlock = true }, Vst3("only-here", Verb)],
                    ["mix:monitor"] = [Vst3("wide", Verb)],
                },
            }.Save();
            var config = new ConfigurationBuilder().Build();
            var dock = new Dock();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [dock]);
            ProfileStore.Save("0fd9:00a6", "Saved", new Profile
            {
                Mixer = new()
                {
                    Inserts = new()
                    {
                        ["xlr1"] = [Vst3("voice", Voice), Vst3("back", Verb) with { WindowsHost = "openxlr", OwnBlock = true }],
                        ["mix:monitor"] = [Vst3("wide", Verb) with { OwnBlock = true, WindowsHost = "openxlr" }],
                    },
                },
            });
            ProfileStore.SetRecallOnConnect("0fd9:00a6", "Saved");
            using var lifetime = new Lifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            devices.SweepOnce();
            try
            {
                await mixer.StartAsync(CancellationToken.None);
                Assert.True(SpinWait.SpinUntil(() => hub.Snapshot().ActiveProfile == "Saved", TimeSpan.FromSeconds(10)));
                Dictionary<string, List<OpenXLR.Core.Mixing.InsertDefinition>> inserts = mixer.ExportScene().Scene!.Inserts!;
                Assert.Equal([("voice", "openxlr", true), ("back", "openxlr", true)],
                    inserts["xlr1"].Select(i => (i.Id, i.WindowsHost, i.OwnBlock)));
                Assert.Equal((null, false), (Assert.Single(inserts["mix:monitor"]).WindowsHost, inserts["mix:monitor"][0].OwnBlock));
            }
            finally { await mixer.StopAsync(CancellationToken.None); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// pipewire-pulse stalled when the daemon started (a wedged USB device
    /// can hold it for minutes), so the first graph build failed. The daemon
    /// keeps trying, and the profile recalled on connect meanwhile lands
    /// once the server answers. A pactl wrapper refuses to load modules
    /// until the test lets the real one through.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task AStartupBuildRefusedByTheAudioServerIsRetriedAndTheRecalledProfileLands()
    {
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        string dir = Directory.CreateTempSubdirectory("openxlr-retry-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realPactl = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pactl"))).First(File.Exists);
        string bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        string answer = Path.Combine(dir, "answer"), refusals = Path.Combine(dir, "refusals");
        ExecutableScript.Write(Path.Combine(bin, "pactl"), $$"""
            if [ "$1" = load-module ] && [ ! -e '{{answer}}' ]; then
              echo refused >> '{{refusals}}'
              echo 'Connection failure: Timeout' >&2
              exit 1
            fi
            exec '{{realPactl}}' "$@"
            """);
        int Refusals() => File.Exists(refusals) ? File.ReadAllLines(refusals).Length : 0;
        string[] OwnModules() => [.. Pactl(realPactl, "list", "short", "modules").Split('\n').Where(l => l.Contains("OpenXLR_", StringComparison.Ordinal))];
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + path);
        try
        {
            new DaemonSettings { Submixer = true }.Save();
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
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices)
            {
                RetryDelay = _ => TimeSpan.FromMilliseconds(100),
            };
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            devices.SweepOnce();
            try
            {
                await mixer.StartAsync(CancellationToken.None);
                Assert.False(mixer.Built);
                Assert.True(hub.Snapshot().MixerWaiting);
                Assert.Contains("waiting for the audio server", hub.Snapshot().Warning);
                // The hardware half does not wait for the audio server.
                Assert.True(SpinWait.SpinUntil(() => dock.Gain == 55, TimeSpan.FromSeconds(10)));
                Assert.True(SpinWait.SpinUntil(() => Refusals() >= 3, TimeSpan.FromSeconds(10)));
                Assert.Empty(OwnModules());

                File.WriteAllText(answer, "");
                await mixer.BuildRetry.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(mixer.Built);
                StateMessage state = hub.Snapshot();
                Assert.False(state.MixerWaiting);
                Assert.DoesNotContain("waiting for the audio server", state.Warning ?? "");
                Assert.Equal(0.25, mixer.ExportScene().Scene!.MixVolumes["monitor"]);
                Assert.Equal("Saved", state.ActiveProfile);
                Assert.NotEmpty(OwnModules());
            }
            finally { await mixer.StopAsync(CancellationToken.None); }
            Assert.Empty(OwnModules());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Pactl(string pactl, params string[] args)
    {
        ProcessResult result = ProcessRunner.Run(pactl, args, TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode);
        return result.StdoutText;
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
