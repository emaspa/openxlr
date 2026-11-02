using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    /// <summary>
    /// The whole daemon path on a private graph, with a fake device port for
    /// a driven unit that has hardware ducking: setDucking through the hub is
    /// saved to mixer.json before it applies; Monitor A heard on another
    /// output is mixElsewhere, so the unit is posted off and the software
    /// stage runs; switching off keeps the stage at unity; with a target mix
    /// heard nowhere the unit runs it again, posted on with no pairs, and the
    /// stage goes; software chosen by setDuckingProcessing brings it back.
    /// Monitor B, which no output plays, stands for a mix heard nowhere.
    /// </summary>
    [DspPipeWireFact]
    public async Task OneSetupRunsOnTheUnitOrInSoftwareThroughTheDaemon()
    {
        Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true.");
        string dir = Directory.CreateTempSubdirectory("openxlr-duck-service-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        var outputs = new PipeWireAdapter();
        IDisposable? registry = null;
        uint? sink = null;
        DeviceManager? devices = null;
        MixerService? mixer = null;
        try
        {
            registry = outputs.WatchGraph();
            sink = outputs.CreateNullSink("test_duck_service_output", "Duck service output");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
            var unit = new DuckingUnit();
            devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [unit]);
            devices.SweepOnce();
            Assert.True(unit.Connected);
            var port = new ServicePort(unit.Info.InstanceId);
            mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices) { DuckingPort = port };
            await mixer.StartAsync(CancellationToken.None);
            Assert.True(mixer.Built);
            var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new ServiceLifetime());
            MixerService service = mixer;
            DeviceManager manager = devices;

            void Run(string command)
            {
                ApiCommandResult result = hub.ExecuteForApiAsync(command).GetAwaiter().GetResult();
                Assert.True(result.Ok, string.Join("; ", result.Messages.OfType<CommandResultMessage>().Select(m => m.Error)));
            }
            DuckingState Settled(Func<DuckingState, bool> done, string what)
            {
                DuckingState? state = null;
                DuckWait(() => { manager.SweepOnce(); service.SweepOnce(); state = hub.Snapshot().Ducking; return state is not null && done(state); }, what);
                return state!;
            }

            DuckingState start = Settled(s => s.Device is not null, "The unit's ducking verdict is missing.");
            Assert.False(start.Enabled);
            Assert.Equal((unit.Info.InstanceId, "Wave XLR Pro"), (start.Device!.Unit, start.Device.Name));

            // Monitor A heard on a computer output: the unit cannot duck it.
            Run("""{"cmd":"setMonitorOutputs","devices":["test_duck_service_output"],"requestId":"route"}""");
            Run("""{"cmd":"setDucking","value":{"enabled":true,"amount":"high","speed":"fast"},"requestId":"on"}""");
            DuckingSetup saved = MixerSettings.Load(MixerSettings.DefaultPath)!.Ducking!;
            Assert.Equal((true, "high", "fast", "device"), (saved.Enabled, saved.Amount, saved.Speed, saved.Processing));
            DuckingState software = Settled(s => s.Backend == "software", "Software ducking did not start.");
            Assert.Equal(("mixElsewhere", false), (software.Device!.Reason, software.Device.Ready));
            Assert.False(port.Posts[^1].Enabled);
            Assert.True(software.Available);
            string stage = Assert.Single(DuckNodes("OpenXLR_duck_")).Key;
            Assert.Equal("OpenXLR_duck_monitor", stage);
            DuckWait(() => service.Meters()?.ContainsKey("duck:monitor") == true, "The reduction meter is missing.");

            // Off leaves the running stage at unity: no graph change for a Deck toggle.
            string serial = DuckNodes("OpenXLR_duck_monitor").Single().Value;
            Run("""{"cmd":"setDucking","value":{"enabled":false},"requestId":"off"}""");
            Assert.Equal("software", Settled(s => !s.Enabled, "Ducking did not switch off.").Backend);
            Assert.Equal(serial, DuckNodes("OpenXLR_duck_monitor").Single().Value);

            // A target mix heard nowhere: the unit runs it, on with no pairs, and the stage goes.
            Run("""{"cmd":"setDucking","value":{"enabled":true,"mixes":["monitor2"]},"requestId":"again"}""");
            DuckingState device = Settled(s => s.Backend == "device", "The unit did not take ducking back.");
            Assert.True(device.Device!.Ready);
            Assert.True(port.Posts[^1].Enabled);
            Assert.Empty(port.Posts[^1].Pairs);
            DuckWait(() => DuckNodes("OpenXLR_duck_").Count == 0, "The software stage survived the hand-over.");

            // Software chosen: saved, the unit posted off, the stage back.
            Run("""{"cmd":"setDuckingProcessing","value":"software","requestId":"software"}""");
            Assert.Equal("software", MixerSettings.Load(MixerSettings.DefaultPath)!.Ducking!.Processing);
            Assert.Equal("software", Settled(s => s.Backend == "software", "Choosing software did not start the stage.").Processing);
            Assert.False(port.Posts[^1].Enabled);
            Assert.Equal("OpenXLR_duck_monitor2", Assert.Single(DuckNodes("OpenXLR_duck_")).Key);

            // The state a client reads has the final shape.
            JsonElement json = JsonSerializer.SerializeToElement(hub.Snapshot().Ducking);
            Assert.False(json.TryGetProperty("targets", out _));
            Assert.Equal(JsonValueKind.Array, json.GetProperty("channels").ValueKind);
        }
        finally
        {
            if (mixer is not null) { await mixer.StopAsync(CancellationToken.None); mixer.Dispose(); }
            devices?.Dispose();
            if (sink is { } module) outputs.UnloadModule(module);
            registry?.Dispose();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A unit that keeps every target at once.</summary>
    private sealed class ServicePort(string unit) : IDaemonDuckingPort
    {
        public readonly List<ProDuckingTarget> Posts = [];
        private DeviceDuckingStatus _status = DeviceDuckingStatus.None;

        public event Action? StatusChanged { add { } remove { } }

        public (string Unit, UnitDuckingSupport Support)? Primary() => (unit, DuckingPlacementTests.Pro);

        public void Post(string target, ProDuckingTarget value, string owner)
        {
            Assert.Equal(unit, target);
            lock (Posts)
            {
                Posts.Add(value);
                _status = new(DeviceDuckingPhase.Verified, value);
            }
        }

        public DeviceDuckingStatus Status(string target) { lock (Posts) return target == unit ? _status : DeviceDuckingStatus.None; }
    }

    private sealed class ServiceLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>A driven unit with no audio nodes of its own, so the mixer's outputs are the test's.</summary>
    private sealed class DuckingUnit : IAudioDevice
    {
        private DeviceState _state = new() { GainDb = 30 };
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4) { Location = new(1, 53, "1-DUCKSERVICE", "DUCKSERVICE") };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, XlrInputs = 2, RetainsSettings = true, Ducking = true };
        public bool Connected { get; private set; }
        public DeviceState ReadState() { lock (this) return _state; }
        private void Write(Func<DeviceState, DeviceState> change) { lock (this) _state = change(_state); }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void SetGainDb(int db) => Write(s => s with { GainDb = db });
        public void SetMute(bool on) { } public void SetLowCut(bool on) { } public void SetExpander(bool on) { }
        public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { } public void SetPhantom(bool on) { }
        public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }
}
