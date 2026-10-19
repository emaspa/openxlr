using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task PrimaryMicrophoneSelectionRoutesOnePortAndSurvivesHotplugAndProfiles()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_signal", "Generic test microphone");
        uint source = pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "Generic microphone");
        pw.CreateVirtualMic("Wave_XLR_primary_fallback", "primary_signal.monitor", "Elgato fallback");
        pw.CreateNullSink("soundcheck_output", "Test output");
        mixer.SetPrimaryInput(new("primary_source", 1));
        mixer.Build(MixerConfig.Default());
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return mixer.Snapshot().PrimaryInputConnected; });
        int? channelNode = pw.FindNodeId("OpenXLR_ch_xlr1"), mixNode = pw.FindNodeId("OpenXLR_mix_monitor");
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> generator = PrimarySignal(stop.Token);
        try
        {
            Assert.All(mixer.Snapshot().Channels.Where(c => c.Hardware && c.Id != "xlr1"), c => Assert.False(c.Present));
            Assert.False(mixer.SetHardwareMicMonitor(true));
            mixer.SetChannelMuted("xlr1", "monitor", false);
            PrimarySound(.4);
            mixer.SetInputDeviceHint("Wave_XLR_primary_fallback");
            PrimarySound(.4);
            // The source name is unchanged, but its selected physical port changed.
            mixer.SetPrimaryInput(new("primary_source", 0));
            Assert.Contains("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").MutedIn);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            PrimarySound(.1);
            MixerSettings saved = mixer.ExportSettings();
            MixerScene profile = mixer.ExportScene();
            Assert.Throws<IOException>(() => mixer.SetPrimaryInput(new("primary_source", 1), _ => "disk full"));
            Assert.Equal(new("primary_source", 0), mixer.PrimaryInput);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            PrimarySound(.1);
            pw.UnloadModule(source);
            bool disconnected = false;
            PrimaryWait(() => { disconnected |= mixer.EnsureInputFeeds(); return !mixer.Snapshot().PrimaryInputConnected; });
            Assert.True(disconnected); // The sweep must push the disconnected state even without USB events.
            PrimarySound(0); // The available Wave_XLR source must never take its place.
            Assert.Contains("not connected", mixer.InputWarning!);
            pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "Microphone returned");
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().PrimaryInputConnected; });
            PrimarySound(.1);
            mixer.SetPrimaryInput(new("primary_source", 63));
            Assert.False(mixer.Snapshot().PrimaryInputConnected);
            Assert.Contains("port 64", mixer.InputWarning!);
            mixer.SetLowCutHz(120);
            for (int i = 0; i < 3; i++) mixer.EnsureInputFeeds();
            Assert.DoesNotContain(pw.ListDevices(), d => d.Name.StartsWith("OpenXLR_lc_xlr1_", StringComparison.Ordinal));
            mixer.ApplyScene(profile);
            Assert.Equal(63, mixer.PrimaryInput!.Channel); // Profiles never change machine wiring.
            PrimarySound(0);
            mixer.ApplySettings(saved);
            PrimarySound(.1);
            Assert.Equal(channelNode, pw.FindNodeId("OpenXLR_ch_xlr1"));
            Assert.Equal(mixNode, pw.FindNodeId("OpenXLR_mix_monitor"));
            mixer.SetPrimaryInput(null);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            Assert.True(mixer.Snapshot().PrimaryInputConnected);
            Assert.Null(mixer.PrimaryInput);
        }
        finally { stop.Cancel(); await generator; }
    }

    [MonitorPipeWireFact]
    public void DuplicatePrimarySourceNamesStaySilentUntilTheBindingIsUnambiguous()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_ambiguous_signal", "Microphone ambiguity test");
        pw.CreateVirtualMic("primary_ambiguous_source", "primary_ambiguous_signal.monitor", "First microphone");
        mixer.SetPrimaryInput(new("primary_ambiguous_source"));
        mixer.Build(MixerConfig.Default());
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().PrimaryInputConnected; });
        uint duplicate = pw.CreateVirtualMic("primary_ambiguous_source", "primary_ambiguous_signal.monitor", "Second microphone");
        PrimaryWait(() => pw.ListDevices().Count(d => d.Name == "primary_ambiguous_source") == 2);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return !mixer.Snapshot().PrimaryInputConnected; });
        Assert.Contains("more than one", mixer.InputWarning!);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
        pw.UnloadModule(duplicate);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().PrimaryInputConnected; });
        Assert.Null(mixer.InputWarning);
        Assert.Equal(2, IncomingRouteLinks("OpenXLR_ch_xlr1").Length);
    }

    [MonitorPipeWireFact]
    public async Task ReturningToAutomaticModeWithoutUsbControlKeepsSoftwareMonitoring()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        pw.CreateNullSink("primary_signal", "Microphone test signal");
        pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "Generic microphone");
        pw.CreateVirtualMic("Wave_XLR_primary_fallback", "primary_signal.monitor", "Unclaimed interface");
        pw.CreateNullSink("soundcheck_output", "Monitor test tap");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        await mixer.StartAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> generator = PrimarySignal(stop.Token);
        try
        {
            Assert.Null(mixer.Apply(new() { Cmd = "setPrimaryInput", Source = "primary_source" }));
            // The saved Pro destination survives a temporary switch to a generic microphone.
            Assert.Null(mixer.Apply(new() { Cmd = "setMonitorOutputs", Devices = ["unplugged_pro#hp1"] }));
            Assert.Null(mixer.Apply(new() { Cmd = "setPrimaryInput" }));
            Assert.False(devices.Snapshot().Connected);
            PrimaryWait(() => { mixer.SweepOnce(); return mixer.Snapshot()!.PrimaryInputConnected; });
            using var unmuted = JsonDocument.Parse("false");
            Assert.Null(mixer.Apply(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = unmuted.RootElement }));
            // Tap the software send independently of the unavailable physical jack.
            PortLink tap = pw.LinkNodes("OpenXLR_mix_monitor", "monitor", "soundcheck_output", "playback", fromChannel: 0);
            try { PrimarySound(.1); }
            finally { pw.Unlink(tap); }
        }
        finally
        {
            stop.Cancel(); await generator;
            await mixer.StopAsync(CancellationToken.None);
            pw.TearDown();
            File.Delete(MixerSettings.DefaultPath);
        }
    }

    [MonitorPipeWireFact]
    public void NewCaptureSourcesTriggerADeviceListPushWithoutAnOutputChange()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_discovery_signal", "Source discovery test");
        mixer.Build(new MixerConfig { Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            Channels = [new("system", "System")] });
        Assert.True(mixer.SyncDeviceList());
        Assert.False(mixer.SyncDeviceList());
        uint source = pw.CreateVirtualMic("primary_discovery_source", "primary_discovery_signal.monitor", "Microphone plugged in");
        PrimaryWait(() => pw.ListDevices().Any(d => d.Name == "primary_discovery_source"));
        Assert.True(mixer.SyncDeviceList());
        Assert.False(mixer.SyncDeviceList());
        pw.UnloadModule(source);
        PrimaryWait(() => !pw.ListDevices().Any(d => d.Name == "primary_discovery_source"));
        Assert.True(mixer.SyncDeviceList());
    }

    [MonitorPipeWireFact]
    public async Task GenericSoundCheckUsesTheSelectedPortAndSourceChangesEndTheSession()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_signal", "Generic test microphone");
        pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "Generic microphone");
        pw.CreateNullSink("soundcheck_output", "Test output");
        mixer.SetPrimaryInput(new("primary_source", 1));
        mixer.Build(MixerConfig.Default());
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        mixer.SetChannelMuted("xlr1", "monitor", false);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return mixer.Snapshot().PrimaryInputConnected; });
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> generator = PrimarySignal(stop.Token);
        try
        {
            PrimarySound(.4);
            Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr2", "record"));
            mixer.SoundCheck("xlr1", "record");
            PrimaryWait(() => mixer.Snapshot().SoundCheck.Seconds >= .5);
            mixer.SoundCheck("xlr1", "loop");
            PrimaryWait(() => mixer.Snapshot().SoundCheck.Mode == "looping");
            stop.Cancel(); await generator;
            PrimarySound(.4);
            // An attached device with hardware DSP must not suppress generic software DSP.
            mixer.SetLowCutApplicable(false);
            mixer.SetLowCutHz(120);
            PrimarySound(0);
            mixer.SetLowCutHz(0);
            PrimarySound(.4);
            mixer.SetPrimaryInput(new("primary_source", 0));
            Assert.Null(mixer.SoundCheckChannel);
            Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
            Assert.False(mixer.SetHardwareMicMonitor(true));
        }
        finally { stop.Cancel(); await generator; }
    }

    [MonitorPipeWireFact]
    public async Task GenericCommandsPersistAcrossRestartAndProfilesContainOnlyTheMixer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        using var lifetime = new PrimaryTestLifetime();
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setPrimaryInput","source":"unplugged.mic","captureMonoChannel":2}""")).Ok);
            Assert.False(devices.HardwareEnabled);
            Assert.Equal(new("unplugged.mic", 2), MixerSettings.Load()!.PrimaryInput);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setMixVolume","mix":"monitor","value":0.36}""")).Ok);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setPrimaryInput","source":"unplugged.mic","captureMonoChannel":2}""")).Ok);
            Assert.Equal(.36, MixerSettings.Load()!.MixVolumes["monitor"]);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Generic test"}""")).Ok);
            Profile profile = ProfileStore.Load("generic", "Generic test")!;
            Assert.Null(profile.Device);
            Assert.Null(profile.AdditionalDevices);
            Assert.NotNull(profile.Mixer);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Generic test"}""")).Ok);
            Assert.Equal(new("unplugged.mic", 2), mixer.Snapshot()!.PrimaryInput);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"set","control":"phantom","value":true}""")).Ok);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"setRecallOnConnect","name":"Generic test"}""")).Ok);
            await mixer.StopAsync(CancellationToken.None);
            using var restoredDevices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var restored = new MixerService(NullLogger<MixerService>.Instance, config, restoredDevices);
            await restored.StartAsync(CancellationToken.None);
            try
            {
                Assert.False(restoredDevices.HardwareEnabled);
                Assert.Equal(new("unplugged.mic", 2), restored.Snapshot()!.PrimaryInput);
                Assert.Null(restored.Apply(new() { Cmd = "setPrimaryInput" }));
                Assert.True(restoredDevices.HardwareEnabled);
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally
        {
            if (mixer.Built) await mixer.StopAsync(CancellationToken.None);
            ProfileStore.Delete("generic", "Generic test");
            File.Delete(MixerSettings.DefaultPath);
        }
    }

    [Fact]
    public async Task GenericProfileSaveCannotReplaceASceneBeforeTheMixerIsReady()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-generic-profile-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            Assert.Null(new MixerSettings { PrimaryInput = new("unplugged.microphone") }.Save());
            var scene = new MixerScene { MixVolumes = new() { ["monitor"] = .42 } };
            ProfileStore.Save("generic", "Existing", new Profile { Mixer = scene });
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new PrimaryTestLifetime();
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.False(devices.HardwareEnabled);
            Assert.False(mixer.Built);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Existing"}""")).Ok);
            Assert.Equal(.42, ProfileStore.Load("generic", "Existing")!.Mixer!.MixVolumes["monitor"]);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Empty"}""")).Ok);
            Assert.Null(ProfileStore.Load("generic", "Empty"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(directory, true);
        }
    }

    private static void PrimaryWait(Func<bool> ready)
        => Assert.True(SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(6)));

    private static void PrimarySound(double expected)
    {
        ProcessResult result = SoundCheckIntegrationTests.CaptureSound(expected);
        Assert.True(result.Ok, result.StdoutText + result.Stderr);
    }

    private static Task<ProcessResult> PrimarySignal(CancellationToken stop) => Task.Run(() =>
        ProcessRunner.Run("python3", ["-c", """
            import struct, subprocess
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--target', 'primary_signal',
                '--properties={ node.name = "OpenXLR_primary_fixture" }', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', .1, .4) * (48000 * 60), timeout=65)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """], TimeSpan.FromSeconds(70), cancel: stop));

    private sealed class PrimaryTestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() => _stop.Dispose();
    }
}
