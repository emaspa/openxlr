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
    public async Task Xlr1BindingRoutesOnePortAndSurvivesHotplugAndProfiles()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_signal", "Test microphone signal");
        uint source = pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "USB microphone");
        pw.CreateVirtualMic("Wave_XLR_primary_fallback", "primary_signal.monitor", "Elgato fallback");
        pw.CreateNullSink("soundcheck_output", "Test output");
        mixer.SetInputBinding("xlr1", new("primary_source", 0, 1));
        mixer.Build(MixerConfig.Default());
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return Strip(mixer, "xlr1").BindingConnected; });
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
            mixer.SetInputBinding("xlr1", new("primary_source", 0, 0));
            Assert.Contains("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").MutedIn);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            PrimarySound(.1);
            MixerSettings saved = mixer.ExportSettings();
            MixerScene profile = mixer.ExportScene();
            Assert.Throws<IOException>(() => mixer.SetInputBinding("xlr1", new("primary_source", 0, 1), _ => "disk full"));
            Assert.Equal(new("primary_source", 0, 0), mixer.InputBinding("xlr1"));
            mixer.SetChannelMuted("xlr1", "monitor", false);
            PrimarySound(.1);
            pw.UnloadModule(source);
            bool disconnected = false;
            PrimaryWait(() => { disconnected |= mixer.EnsureInputFeeds(); return !Strip(mixer, "xlr1").BindingConnected; });
            Assert.True(disconnected); // The sweep must push the disconnected state even without USB events.
            PrimarySound(0); // The available Wave_XLR source must never take its place.
            Assert.StartsWith("Mic 1: ", mixer.InputWarning!);   // a bound strip is Mic 1
            Assert.Contains("not connected", mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").BindingWarning);
            pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "Microphone returned");
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            PrimarySound(.1);
            mixer.SetInputBinding("xlr1", new("primary_source", 0, 63));
            Assert.False(Strip(mixer, "xlr1").BindingConnected);
            Assert.Contains("port 64", mixer.InputWarning!);
            mixer.SetLowCutHz(120);
            for (int i = 0; i < 3; i++) mixer.EnsureInputFeeds();
            Assert.DoesNotContain(pw.ListDevices(), d => d.Name.StartsWith("OpenXLR_lc_xlr1_", StringComparison.Ordinal));
            mixer.ApplyScene(profile);
            Assert.Equal(63, mixer.InputBinding("xlr1")!.Port); // Profiles never change machine wiring.
            PrimarySound(0);
            mixer.ApplySettings(saved);
            PrimarySound(.1);
            Assert.Equal(channelNode, pw.FindNodeId("OpenXLR_ch_xlr1"));
            Assert.Equal(mixNode, pw.FindNodeId("OpenXLR_mix_monitor"));
            mixer.SetInputBinding("xlr1", null);
            mixer.SetChannelMuted("xlr1", "monitor", false);
            Assert.True(Strip(mixer, "xlr1").BindingConnected);
            Assert.Null(mixer.InputBinding("xlr1"));
            Assert.Equal(new ChannelBinding("unit", Pair: 0), mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").Binding);
        }
        finally { stop.Cancel(); await generator; }
    }

    [MonitorPipeWireFact]
    public void DuplicateSourceNamesSilenceOnlyTheirStripUntilTheBindingIsUnambiguous()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_ambiguous_signal", "Microphone ambiguity test");
        pw.CreateVirtualMic("primary_ambiguous_source", "primary_ambiguous_signal.monitor", "First microphone");
        pw.CreateVirtualMic("primary_steady_source", "primary_ambiguous_signal.monitor", "Steady microphone");
        mixer.SetInputBinding("xlr1", new("primary_steady_source"));
        mixer.SetInputBinding("xlr2", new("primary_ambiguous_source", 0, 1));
        mixer.Build(MixerConfig.Default());
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected && Strip(mixer, "xlr2").BindingConnected; });
        (string From, string To)[] steady = GraphLinksInto("OpenXLR_ch_xlr1");
        uint duplicate = pw.CreateVirtualMic("primary_ambiguous_source", "primary_ambiguous_signal.monitor", "Second microphone");
        PrimaryWait(() => pw.ListDevices().Count(d => d.Name == "primary_ambiguous_source") == 2);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return !Strip(mixer, "xlr2").BindingConnected; });
        Assert.Contains("More than one source", Strip(mixer, "xlr2").BindingWarning);
        Assert.Contains("Mic 2: More than one source", mixer.InputWarning!);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr2"));
        // The other strip never noticed.
        Assert.True(Strip(mixer, "xlr1").BindingConnected);
        Assert.Null(Strip(mixer, "xlr1").BindingWarning);
        Assert.Equal(steady, GraphLinksInto("OpenXLR_ch_xlr1"));
        pw.UnloadModule(duplicate);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr2").BindingConnected; });
        Assert.Null(mixer.InputWarning);
        Assert.Equal(2, IncomingRouteLinks("OpenXLR_ch_xlr2").Length);
        Assert.All(GraphLinksInto("OpenXLR_ch_xlr2"), link => Assert.Equal("primary_ambiguous_source:capture_FR", link.From));
    }

    [MonitorPipeWireFact]
    public async Task ReturningXlr1ToTheInterfaceWithNoUnitKeepsSoftwareMonitoring()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        pw.CreateNullSink("primary_signal", "Microphone test signal");
        pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "USB microphone");
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
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr1", Source = "primary_source" }));
            // The saved Pro destination survives while XLR 1 takes another source.
            Assert.Null(mixer.Apply(new() { Cmd = "setMonitorOutputs", Devices = ["unplugged_pro#hp1"] }));
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr1" }));
            Assert.False(devices.Snapshot().Connected);
            PrimaryWait(() => { mixer.SweepOnce(); return mixer.Snapshot()!.Channels.Single(c => c.Id == "xlr1").BindingConnected; });
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
            if (File.Exists(MixerSettings.DefaultPath)) File.Delete(MixerSettings.DefaultPath);
        }
    }

    [MonitorPipeWireFact]
    public async Task SupportedUnitNodesAreBoundWithTheirUnitAndTheProKeepsItsJacksWhileXlr1IsBound()
    {
        const string proSink = "alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_PROSER-00.pro-output-0";
        const string proSource = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_PROSER-00.pro-input-0";
        const string jack = proSink + "#hp1", speakers = "binding_desk_speakers";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        pw.CreateNullSink(proSink, "Simulated Pro raw sink");
        pw.CreateVirtualMic(proSource, proSink + ".monitor", "Simulated Pro capture");
        pw.CreateNullSink(speakers, "Desk speakers");
        pw.CreateNullSink("binding_cam_signal", "Webcam signal");
        pw.CreateVirtualMic("binding_cam", "binding_cam_signal.monitor", "Webcam microphone");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        var pro = new Unit("Wave XLR Pro", 0x00b4, "PROSER");
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [pro]);
        devices.SweepOnce();   // detects and drives the simulated unit
        var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            PrimaryWait(() => mixer.Devices()!.Any(d => d.Name == "binding_cam") && mixer.Devices()!.Any(d => d.Name == proSource));
            AudioNode proNode = mixer.Devices()!.Single(d => d.Name == proSource);
            Assert.Equal(pro.Info.InstanceId, proNode.DrivenUnit);
            AudioNode cam = mixer.Devices()!.Single(d => d.Name == "binding_cam");
            Assert.Null(cam.DrivenUnit);
            Assert.True(cam.Channels > 0);
            Assert.Equal(cam.Channels, cam.Positions?.Count);

            // A supported unit's node is a source like any other, even the
            // driven unit's own; the binding remembers whose node it is.
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr2", Source = proSource }));
            Assert.Equal(new ChannelBinding("source", proSource, 0, 0, pro.Info.InstanceId), mixer.Snapshot()!.Channels.Single(c => c.Id == "xlr2").Binding);
            Assert.Equal(pro.Info.InstanceId, MixerSettings.Load()!.InputBindings!["xlr2"].Unit);
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr2" }));
            Assert.Null(mixer.Snapshot()!.Channels.Single(c => c.Id == "xlr2").Binding.Source);

            Assert.Null(mixer.Apply(new() { Cmd = "setMonitorOutputs", Devices = [jack] }));
            Assert.Null(mixer.Apply(new() { Cmd = "setMonitorFeed", Device = jack, Mix = "monitor+monitor2" }));

            // XLR 1 on a webcam leaves the unit driven and its jack where it
            // was, whatever the default output is: nothing is set aside.
            pw.SetDefaultSink(speakers);
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr1", Source = "binding_cam", CaptureMonoChannel = 1 }));
            for (int i = 0; i < 3; i++) { devices.SweepOnce(); mixer.SweepOnce(); }
            Assert.True(pro.Connected);
            Assert.True(devices.Snapshot().Connected);
            Assert.Equal([jack], mixer.Snapshot()!.MonitorOutputs);
            Assert.Equal("monitor+monitor2", mixer.Snapshot()!.MonitorFeeds[jack]);
            Assert.Empty(IncomingRouteLinks(speakers));
            Assert.Null(mixer.InputWarning);
            Assert.Equal([jack], MixerSettings.Load()!.MonitorOutputs);
            Assert.Null(mixer.Apply(new() { Cmd = "setInputBinding", Channel = "xlr1" }));
            Assert.Equal([jack], mixer.Snapshot()!.MonitorOutputs);
            Assert.True(pro.Connected);
        }
        finally
        {
            if (mixer.Built) await mixer.StopAsync(CancellationToken.None);
            mixer.Dispose();
            devices.SuspendSession();
            pw.TearDown();
            if (File.Exists(MixerSettings.DefaultPath)) File.Delete(MixerSettings.DefaultPath);
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
    public async Task SoundCheckRecordsTheBoundStripAndOnlyItsOwnSourceChangeEndsTheSession()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("primary_signal", "Test microphone signal");
        pw.CreateVirtualMic("primary_source", "primary_signal.monitor", "USB microphone");
        pw.CreateNullSink("soundcheck_output", "Test output");
        mixer.SetInputBinding("xlr1", new("primary_source", 0, 1));
        mixer.Build(MixerConfig.Default());
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        mixer.SetChannelMuted("xlr1", "monitor", false);
        PrimaryWait(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return Strip(mixer, "xlr1").BindingConnected; });
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> generator = PrimarySignal(stop.Token);
        try
        {
            PrimarySound(.4);
            // XLR 2 takes the interface, and none is here: nothing to record.
            Assert.Contains("not connected", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr2", "record")).Message);
            mixer.SoundCheck("xlr1", "record");
            PrimaryWait(() => mixer.Snapshot().SoundCheck.Seconds >= .5);
            mixer.SoundCheck("xlr1", "loop");
            PrimaryWait(() => mixer.Snapshot().SoundCheck.Mode == "looping");
            stop.Cancel(); await generator;
            PrimarySound(.4);
            // Another strip's binding is not this session's microphone.
            mixer.SetInputBinding("xlr2", new("primary_source", 0, 0));
            Assert.Equal("xlr1", mixer.SoundCheckChannel);
            Assert.Equal("looping", mixer.Snapshot().SoundCheck.Mode);
            Assert.Contains("Stop Sound Check", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr2", "record")).Message);
            PrimarySound(.4);
            // An attached device with hardware DSP must not suppress the software DSP on a bound XLR 1.
            mixer.SetLowCutApplicable(false);
            mixer.SetLowCutHz(120);
            PrimarySound(0);
            mixer.SetLowCutHz(0);
            PrimarySound(.4);
            mixer.SetInputBinding("xlr1", new("primary_source", 0, 0));
            Assert.Null(mixer.SoundCheckChannel);
            Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
            Assert.False(mixer.SetHardwareMicMonitor(true));
        }
        finally { stop.Cancel(); await generator; }
    }

    [MonitorPipeWireFact]
    public async Task BindingCommandsPersistAcrossRestartAndGenericProfilesContainOnlyTheMixer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        using var lifetime = new PrimaryTestLifetime();
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
        await mixer.StartAsync(CancellationToken.None);
        try
        {
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setInputBinding","channel":"xlr1","source":"unplugged.mic","captureMonoChannel":2}""")).Ok);
            Assert.Equal(new SourceBinding("unplugged.mic", 0, 2), MixerSettings.Load()!.InputBindings!["xlr1"]);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setInputBinding","channel":"xlr2","source":"unplugged.mic","captureMonoChannel":3}""")).Ok);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"setInputBinding","channel":"aux","source":"unplugged.mic"}""")).Ok);
            Assert.Equal(new SourceBinding("unplugged.mic", 0, 3), MixerSettings.Load()!.InputBindings!["xlr2"]);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setMixVolume","mix":"monitor","value":0.36}""")).Ok);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"setInputBinding","channel":"xlr1","source":"unplugged.mic","captureMonoChannel":2}""")).Ok);
            Assert.Equal(.36, MixerSettings.Load()!.MixVolumes["monitor"]);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Generic test"}""")).Ok);
            Profile profile = ProfileStore.Load("generic", "Generic test")!;
            Assert.Null(profile.Device);
            Assert.Null(profile.AdditionalDevices);
            Assert.NotNull(profile.Mixer);
            Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"loadProfile","name":"Generic test"}""")).Ok);
            Assert.Equal(new ChannelBinding("source", "unplugged.mic", 0, 2), mixer.Snapshot()!.Channels.Single(c => c.Id == "xlr1").Binding);
            Assert.Equal(new ChannelBinding("source", "unplugged.mic", 0, 3), mixer.Snapshot()!.Channels.Single(c => c.Id == "xlr2").Binding);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"set","control":"phantom","value":true}""")).Ok);
            // With no interface connected the profiles are generic, and recall on connect belongs to a unit.
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"setRecallOnConnect","name":"Generic test"}""")).Ok);
            await mixer.StopAsync(CancellationToken.None);
            using var restoredDevices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var restored = new MixerService(NullLogger<MixerService>.Instance, config, restoredDevices);
            await restored.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(new ChannelBinding("source", "unplugged.mic", 0, 2), restored.Snapshot()!.Channels.Single(c => c.Id == "xlr1").Binding);
                Assert.Equal(new ChannelBinding("source", "unplugged.mic", 0, 3), restored.Snapshot()!.Channels.Single(c => c.Id == "xlr2").Binding);
                Assert.Null(restored.Apply(new() { Cmd = "setInputBinding", Channel = "xlr1" }));
                // Each strip keeps its own binding.
                Assert.False(MixerSettings.Load()!.InputBindings!.ContainsKey("xlr1"));
                Assert.True(MixerSettings.Load()!.InputBindings!.ContainsKey("xlr2"));
            }
            finally { await restored.StopAsync(CancellationToken.None); }
        }
        finally
        {
            if (mixer.Built) await mixer.StopAsync(CancellationToken.None);
            ProfileStore.Delete("generic", "Generic test");
            if (File.Exists(MixerSettings.DefaultPath)) File.Delete(MixerSettings.DefaultPath);
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
            var scene = new MixerScene { MixVolumes = new() { ["monitor"] = .42 } };
            ProfileStore.Save("generic", "Existing", new Profile { Mixer = scene });
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new PrimaryTestLifetime();
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime);
            Assert.False(devices.Snapshot().Connected);   // no interface: the generic profiles
            Assert.False(mixer.Built);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Existing"}""")).Ok);
            Assert.Equal(.42, ProfileStore.Load("generic", "Existing")!.Mixer!.MixVolumes["monitor"]);
            Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"saveProfile","name":"Empty"}""")).Ok);
            Assert.Null(ProfileStore.Load("generic", "Empty"));
            // The earlier XLR 1 command is gone; setInputBinding with channel xlr1 replaced it.
            ApiCommandResult removed = await hub.ExecuteForApiAsync("""{"cmd":"setPrimaryInput","source":"usb.mic"}""");
            Assert.False(removed.Ok);
            string reply = JsonSerializer.Serialize(removed.Messages);
            Assert.Contains("unknown cmd", reply);
            Assert.Contains("setPrimaryInput", reply);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(directory, true);
        }
    }

    private const string BindUnit = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_BINDUNIT-00.pro-input-0";

    /// <summary>
    /// A six-port capture node named like a Wave XLR Pro's, fed a constant
    /// 0.2 on every port, so XLR 1, XLR 2 and Aux In all read 0.2 from it.
    /// Returns the two module ids, parent first.
    /// </summary>
    internal static (uint Parent, uint Node) LoadBindUnit(PipeWireAdapter pw)
    {
        uint parent = LoadModule("module-null-sink", "sink_name=bind_unit_parent", "channels=6", "sink_properties=device.description=Bind-unit-parent");
        WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains("bind_unit_parent.monitor", StringComparison.Ordinal));
        return (parent, LoadBindUnitNode(pw));
    }

    /// <summary>The unit's capture node alone, as a replug brings it back.</summary>
    private static uint LoadBindUnitNode(PipeWireAdapter pw)
    {
        uint node = LoadModule("module-remap-source", $"source_name={BindUnit}", "master=bind_unit_parent.monitor", "channels=6",
            "source_properties=device.description=Bind-unit");
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == BindUnit));
        return node;
    }

    private static uint LoadModule(params string[] args)
    {
        ProcessResult loaded = ProcessRunner.Run("pactl", ["load-module", .. args]);
        Assert.True(loaded.Ok, loaded.Stderr);
        return uint.Parse(loaded.StdoutText.Trim());
    }

    internal static void UnloadModule(uint module) => ProcessRunner.Run("pactl", ["unload-module", module.ToString()]);

    internal static Task<ProcessResult> BindUnitSignal(CancellationToken stop) => Task.Run(() =>
        ProcessRunner.Run("python3", ["-c", """
            import struct, subprocess
            args = ['--format=f32', '--rate=48000', '--channels=6']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', 'bind_unit_parent',
                '--properties={ node.name = "OpenXLR_bind_unit_fixture" }', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<6f', *([.2] * 6)) * (48000 * 120), timeout=125)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """], TimeSpan.FromSeconds(130), cancel: stop));

    private static ChannelStatus Strip(Mixer mixer, string id) => mixer.Snapshot().Channels.Single(c => c.Id == id);

    /// <summary>What a strip's own sink carries, read from its monitor.</summary>
    private static void StripSound(string strip, double expected)
    {
        ProcessResult result = SoundCheckIntegrationTests.CaptureSound(expected, target: $"OpenXLR_ch_{strip}");
        Assert.True(result.Ok, strip + ": " + result.StdoutText + result.Stderr);
    }

    [MonitorPipeWireFact]
    public async Task EachStripFollowsItsOwnSourceWhenTheUnitAndTheSourceComeAndGo()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint unitParent, uint unit) = LoadBindUnit(pw);
        pw.CreateNullSink("primary_signal", "Webcam test signal");
        uint source = pw.CreateVirtualMic("bind_source", "primary_signal.monitor", "Webcam microphone");
        mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(MixerConfig.Default());
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> unitSignal = BindUnitSignal(stop.Token);
        Task<ProcessResult> webcam = PrimarySignal(stop.Token);
        try
        {
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected && Strip(mixer, "xlr2").BindingConnected; });
            StripSound("xlr1", .2);
            StripSound("xlr2", .2);
            mixer.SetChannelMuted("xlr2", "monitor", false);
            (string From, string To)[] xlr2Links = GraphLinksInto("OpenXLR_ch_xlr2");

            // XLR 1 moves to the webcam's right port; XLR 2 stays on the unit, untouched and unmuted.
            mixer.SetInputBinding("xlr1", new("bind_source", 0, 1));
            Assert.Contains("monitor", Strip(mixer, "xlr1").MutedIn);
            Assert.DoesNotContain("monitor", Strip(mixer, "xlr2").MutedIn);
            Assert.Equal(xlr2Links, GraphLinksInto("OpenXLR_ch_xlr2"));
            Assert.All(GraphLinksInto("OpenXLR_ch_xlr1"), link => Assert.Equal("bind_source:capture_FR", link.From));
            Assert.Equal(new ChannelBinding("source", "bind_source", 0, 1), Strip(mixer, "xlr1").Binding);
            Assert.True(Strip(mixer, "xlr1").Present);
            StripSound("xlr1", .4);
            StripSound("xlr2", .2);

            // The unit goes: XLR 1 keeps playing, XLR 2 falls silent.
            UnloadModule(unit);
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return !Strip(mixer, "xlr2").BindingConnected; });
            Assert.True(Strip(mixer, "xlr1").BindingConnected);
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr2"));
            StripSound("xlr1", .4);

            // The source goes and the unit returns: XLR 2 heals, XLR 1 waits for its own source.
            pw.UnloadModule(source);
            unit = LoadBindUnitNode(pw);
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr2").BindingConnected && !Strip(mixer, "xlr1").BindingConnected; });
            Assert.Contains("not connected", Strip(mixer, "xlr1").BindingWarning);
            Assert.Contains("Mic 1: bind_source is not connected", mixer.InputWarning);
            Assert.Null(Strip(mixer, "xlr2").BindingWarning);
            // The unit's node never takes the bound strip's place.
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr1"));
            StripSound("xlr2", .2);
            source = pw.CreateVirtualMic("bind_source", "primary_signal.monitor", "Webcam microphone");
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            Assert.Null(mixer.InputWarning);
            StripSound("xlr1", .4);

            // Two strips on one port.
            mixer.SetInputBinding("xlr2", new("bind_source", 0, 1));
            Assert.Contains("monitor", Strip(mixer, "xlr2").MutedIn);
            Assert.True(Strip(mixer, "xlr1").BindingConnected && Strip(mixer, "xlr2").BindingConnected);
            Assert.All(GraphLinksInto("OpenXLR_ch_xlr2"), link => Assert.Equal("bind_source:capture_FR", link.From));
            StripSound("xlr2", .4);
            StripSound("xlr1", .4);

            // Scenes leave bindings alone.
            MixerScene scene = mixer.ExportScene();
            mixer.SetInputBinding("xlr2", null);
            mixer.ApplyScene(scene);
            Assert.Null(mixer.InputBinding("xlr2"));
            Assert.Equal(new SourceBinding("bind_source", 0, 1), mixer.InputBinding("xlr1"));
            Assert.Equal(new ChannelBinding("unit", Pair: 1), Strip(mixer, "xlr2").Binding);

            // A failed save puts the binding and its route back.
            (string From, string To)[] xlr1Links = GraphLinksInto("OpenXLR_ch_xlr1");
            Assert.Throws<IOException>(() => mixer.SetInputBinding("xlr1", new("bind_source", 0, 0), _ => "disk full"));
            Assert.Equal(new SourceBinding("bind_source", 0, 1), mixer.InputBinding("xlr1"));
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            Assert.All(GraphLinksInto("OpenXLR_ch_xlr1"), link => Assert.Equal("bind_source:capture_FR", link.From));
            Assert.NotEmpty(xlr1Links);
            StripSound("xlr1", .4);
        }
        finally
        {
            stop.Cancel();
            await unitSignal;
            await webcam;
            UnloadModule(unit);
            UnloadModule(unitParent);
        }
    }

    [MonitorPipeWireFact]
    public void AStripTheUnitLeftSilentIsWiredAgainBesideALiveOne()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint unitParent, uint unit) = LoadBindUnit(pw);
        try
        {
            mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
            // XLR 2's route fails while XLR 1 and Aux In come up, as on a
            // replug whose ports are still settling.
            RejectNextRouteLink(BindUnit + ":", "OpenXLR_ch_xlr2:", () => mixer.Build(MixerConfig.Default()), everyLinkInto: true);
            Assert.True(Strip(mixer, "xlr1").BindingConnected);
            Assert.True(Strip(mixer, "aux").BindingConnected);
            Assert.False(Strip(mixer, "xlr2").BindingConnected);
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_xlr2"));
            (string From, string To)[] xlr1Links = GraphLinksInto("OpenXLR_ch_xlr1");

            WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks("OpenXLR_ch_xlr2").Length == 2; });
            Assert.True(Strip(mixer, "xlr2").BindingConnected);
            Assert.Equal([BindUnit + ":capture_3", BindUnit + ":capture_4"], GraphLinksInto("OpenXLR_ch_xlr2").Select(l => l.From).Order());
            Assert.Equal(xlr1Links, GraphLinksInto("OpenXLR_ch_xlr1"));
            Stable(() => mixer.EnsureInputFeeds(), "OpenXLR_ch_xlr2", 2);
        }
        finally
        {
            mixer.TearDown();
            UnloadModule(unit);
            UnloadModule(unitParent);
        }
    }

    [MonitorPipeWireFact]
    public void TheUnitsMicrophoneMovesToACaptureChannelWhileXlr1IsBoundElsewhere()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        (uint unitParent, uint unit) = LoadBindUnit(pw);
        try
        {
            pw.CreateNullSink("primary_signal", "Webcam test signal");
            pw.CreateVirtualMic("bind_source", "primary_signal.monitor", "Webcam microphone");
            mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
            mixer.SetDrivenUnit("0fd9:00b4@00000000000000bd");
            mixer.SetInputJacks(2, true);
            mixer.Build(MixerConfig.Default());
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
            Assert.Equal(1, Strip(mixer, "xlr1").UnitInput!.Input);
            Assert.Equal(2, Strip(mixer, "xlr2").UnitInput!.Input);

            mixer.SetInputBinding("xlr1", new("bind_source", 0, 0));
            Assert.Null(Strip(mixer, "xlr1").UnitInput);
            mixer.CreateCaptureChannel("Pro mic", BindUnit, 0, _ => null, monoChannel: 0);
            mixer.CreateCaptureChannel("Pro mic again", BindUnit, 0, _ => null, monoChannel: 0);
            string kept = mixer.Snapshot().Channels.Single(c => c.Name == "Pro mic").Id;
            PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, kept).BindingConnected; });
            ChannelStatus capture = Strip(mixer, kept);
            Assert.Equal(new UnitInputStatus("0fd9:00b4@00000000000000bd", 1, true), capture.UnitInput);
            Assert.Equal(new ChannelBinding("source", BindUnit, 0, 0), capture.Binding);
            Assert.Single(mixer.Snapshot().Channels, c => c.UnitInput?.Input == 1);
            Assert.Equal(2, Strip(mixer, "xlr2").UnitInput!.Input);

            // Back on the unit, the strip wins and the capture channel stays as it is.
            mixer.SetInputBinding("xlr1", null);
            Assert.Equal(1, Strip(mixer, "xlr1").UnitInput!.Input);
            Assert.Null(Strip(mixer, kept).UnitInput);
            Assert.False(Strip(mixer, kept).Hardware);
        }
        finally
        {
            mixer.TearDown();
            UnloadModule(unit);
            UnloadModule(unitParent);
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
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', 'primary_signal',
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
