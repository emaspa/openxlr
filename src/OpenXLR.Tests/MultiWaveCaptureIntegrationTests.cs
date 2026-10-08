using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>Which capture node feeds the hardware input channels, and mono capture ports, on a private PipeWire server.</summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    private const string Xlr1 = "OpenXLR_ch_xlr1";
    private static MixerConfig HardwareInputConfig => new()
    {
        Channels = [new("xlr1", "Microphone") { InputPair = 0 }],
        Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
    };

    private static uint WaveSource(PipeWireAdapter pw, string parent, string name)
    {
        if (!pw.ListDevices().Any(d => d.Name == parent)) pw.CreateNullSink(parent, "Fixture");
        uint module = pw.CreateVirtualMic(name, parent + ".monitor", name);
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == name));
        return module;
    }

    [MonitorPipeWireFact]
    public void WithNoInterfaceDrivenALoneWaveXlrSourceStillFeedsTheInputs()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_lone_parent", "test_Wave_XLR_lone");
        // No driven device, at most one interface attached: the 0.1.47 fallback.
        mixer.SetInputDeviceHint([], hardwareOutputRouting: false, modelFallback: true);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 2; });
        Assert.Null(mixer.InputWarning);
        // A second interface attached and none driven: the feed goes, with a reason.
        mixer.SetInputDeviceHint([], hardwareOutputRouting: false, modelFallback: false);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 0);
        Assert.NotNull(mixer.InputWarning);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1));
    }

    [MonitorPipeWireFact]
    public void TwoWaveSourcesAndNoDrivenUnitFeedNothing()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_pair_parent", "test_Wave_XLR_unitA");
        WaveSource(pw, "test_pair_parent", "test_Wave_XLR_unitB");
        mixer.SetInputDeviceHint(null);
        mixer.Build(HardwareInputConfig);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1)); Assert.NotNull(mixer.InputWarning);
        // The driven unit's own fragment picks its source.
        mixer.SetInputDeviceHint(["test_Wave_XLR_unitA", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: false);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 2);
        Assert.Null(mixer.InputWarning);
        Assert.All(GraphLinksInto(Xlr1), link => Assert.StartsWith("test_Wave_XLR_unitA:", link.From));
    }

    [MonitorPipeWireFact]
    public void ASerialSpelledDifferentlyFindsTheLoneSourceByModelAndSaysSo()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        var notes = new List<string>();
        using var mixer = new Mixer(pw) { InputNote = notes.Add };
        WaveSource(pw, "test_spelling_parent", "test_Wave_XLR_unit_A-00");
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: true);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 2; });
        Assert.Contains(notes, n => n.Contains("Wave_XLR_unitA-", StringComparison.Ordinal) && n.Contains("test_Wave_XLR_unit_A-00", StringComparison.Ordinal));
        int noted = notes.Count;
        mixer.EnsureInputFeeds();
        Assert.Equal(noted, notes.Count);   // said once, not on every sweep
    }

    [MonitorPipeWireFact]
    public void ASerialSpelledDifferentlyWithTwoUnitsOfTheModelFeedsNothing()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        WaveSource(pw, "test_spelling_pair_parent", "test_Wave_XLR_unit_A-00");
        WaveSource(pw, "test_spelling_pair_parent", "test_Wave_XLR_unit_B-00");
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-", "Wave_XLR"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(Xlr1)); Assert.NotNull(mixer.InputWarning);
    }

    [MonitorPipeWireFact]
    public void ALongerSerialNeverSubstitutesForTheDrivenUnit()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        uint selected = WaveSource(pw, "test_serial_parent", "test_Wave_XLR_unitA-00");
        WaveSource(pw, "test_serial_parent", "test_Wave_XLR_unitAB-00");
        // Two units of the model attached: the daemon passes the serial fragment alone.
        mixer.SetInputDeviceHint(["Wave_XLR_unitA-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        WaitForRecovery(() => IncomingRouteLinks(Xlr1).Length == 2);
        Assert.All(GraphLinksInto(Xlr1), link => Assert.StartsWith("test_Wave_XLR_unitA-00:", link.From));
        // The driven unit's source leaves; the other unit's microphone does not take its place.
        pw.UnloadModule(selected);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(Xlr1).Length == 0; });
    }

    [MonitorPipeWireFact]
    public void SelectedMonoPortsFeedBothSidesAndNeverFallBackToAnotherPort()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_wave_capture", "Wave fixture");
        pw.CreateVirtualMic("test_wave_source", "test_wave_capture.monitor", "Wave source");
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == "test_wave_source"));
        mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        mixer.CreateCaptureChannel("Left", "test_wave_source", 0, _ => null, monoChannel: 0);
        mixer.CreateCaptureChannel("Right", "test_wave_source", 0, _ => null, monoChannel: 1);
        mixer.CreateCaptureChannel("Missing", "test_wave_source", 0, _ => null, monoChannel: 63);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Count(c => c.CaptureConnected) == 2; });
        var left = GraphLinksInto("OpenXLR_ch_left");
        var right = GraphLinksInto("OpenXLR_ch_right");
        Assert.Equal(2, left.Length); Assert.Equal(2, right.Length);
        Assert.Single(left.Select(l => l.From).Distinct()); Assert.Single(right.Select(l => l.From).Distinct());
        Assert.NotEqual(left[0].From, right[0].From);
        Assert.Empty(IncomingRouteLinks("OpenXLR_ch_missing"));
        MixerConfig restored = MixerConfig.FromSettings(mixer.ExportSettings());
        Assert.Equal(1, restored.Channels.Single(c => c.Id == "right").CaptureMonoChannel);
        Assert.Equal(1, mixer.Snapshot().Channels.Single(c => c.Id == "right").CaptureMonoChannel);
        Assert.All(mixer.Snapshot().Channels.Where(c => c.CaptureSource is not null), c => Assert.Contains("monitor", c.MutedIn));
    }

    [MonitorPipeWireFact]
    public async Task DisablingAnAdditionalUnitRemovesTheChannelsMadeFromItsSource()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-wave-disable-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        try
        {
            new DaemonSettings { Submixer = true }.Save();
            new MixerSettings { UserChannels = [new("system", "System")], UserMixes = [] }.Save();
            WaveSource(pw, "test_disable_parent", "test_XLR_Dock_dockA-00");
            WaveSource(pw, "test_disable_parent", "test_headset_source");
            var primary = new Unit("Wave XLR", 0x007d, "unitP");
            var dock = new Unit("XLR Dock", 0x00a6, "dockA");
            IReadOnlyList<IAudioDevice> attached = [primary, dock];
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [primary]);
            devices.SweepOnce();
            using var lifetime = new TestLifetime();
            using var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var interfaces = new WaveInterfaces(devices, NullLogger<DeviceManager>.Instance, config, () => attached);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, lifetime, interfaces);
            await mixer.StartAsync(CancellationToken.None);
            await interfaces.StartAsync(CancellationToken.None);
            try
            {
                WaitForRecovery(() => interfaces.Snapshot().Count == 2);
                async Task Ok(string command) => Assert.True((await hub.ExecuteForApiAsync(command)).Ok, command);
                IReadOnlyList<string> Captures() => [.. mixer.Snapshot()!.Channels.Where(c => c.CaptureSource is not null).Select(c => c.CaptureSource!)];
                string id = dock.Info.InstanceId;

                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":true}""");
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && u.Connected));
                await Ok("""{"cmd":"createCaptureChannel","name":"Dock mic","source":"test_XLR_Dock_dockA-00","capturePair":0,"captureMonoChannel":0}""");
                await Ok("""{"cmd":"createCaptureChannel","name":"Headset","source":"test_headset_source","capturePair":0}""");
                Assert.Equal(2, Captures().Count);

                // Unplugged but still enabled: the channel stays, silent.
                attached = [primary];
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && !u.Connected));
                Assert.Equal(2, Captures().Count);
                attached = [primary, dock];
                WaitForRecovery(() => interfaces.Snapshot().Any(u => u.Id == id && u.Connected));

                // Disabled: its channel goes, and the headset channel stays.
                string dockChannel = mixer.Snapshot()!.Channels.Single(c => c.CaptureSource == "test_XLR_Dock_dockA-00").Id;
                await Ok($$"""{"cmd":"setWaveInterfaceEnabled","device":"{{id}}","value":false}""");
                Assert.Equal(["test_headset_source"], Captures());
                Assert.DoesNotContain(MixerSettings.Load(MixerSettings.DefaultPath, out _)!.UserChannels!, c => c.CaptureSource == "test_XLR_Dock_dockA-00");
                Assert.Empty(IncomingRouteLinks("OpenXLR_ch_" + dockChannel));
            }
            finally
            {
                await interfaces.StopAsync(CancellationToken.None);
                await mixer.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Unit(string model, ushort product, string serial) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", model, 0x0fd9, product) { Location = new(1, (byte)(product & 0x7f), "1-" + serial, serial) };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { }
        public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { }
        public void SetCompressor(bool on) { }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }

    private static (string From, string To)[] GraphLinksInto(string node)
    {
        using var graph = JsonDocument.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        return [.. PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray())
            .Where(link => link.To.StartsWith(node + ":", StringComparison.Ordinal))];
    }
}
