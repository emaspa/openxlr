using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    private const string DirectSerial = "PRODIRECT";
    private const string DirectSource = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_" + DirectSerial + "-00.pro-input-0";
    private const string DirectSink = "alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_" + DirectSerial + "-00.pro-output-0";

    /// <summary>
    /// A Wave XLR Pro the daemon drives while XLR 1 is bound to a webcam: its
    /// jacks stay listed and selected, the Aux return keeps its route, and the
    /// zero-latency microphone path follows the channel that carries the
    /// Pro's input 1, which is XLR 1 on the interface, the capture channel kept
    /// for the Pro's microphone while XLR 1 is bound elsewhere, and none while
    /// no channel carries it.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task TheProStaysDrivenAndItsDirectPathFollowsTheChannelCarryingInput1()
    {
        const string jack = DirectSink + "#hp1";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var modules = new List<uint>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        var pro = new DirectPro();
        DeviceManager? devices = null;
        MixerService? mixer = null;
        try
        {
            modules.Add(LoadModule("module-null-sink", "sink_name=pro_direct_parent", "channels=6", "sink_properties=device.description=Pro-direct-parent"));
            WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains("pro_direct_parent.monitor", StringComparison.Ordinal));
            modules.Add(LoadModule("module-remap-source", $"source_name={DirectSource}", "master=pro_direct_parent.monitor", "channels=6",
                "source_properties=device.description=Pro-direct-capture"));
            modules.Add(LoadModule("module-null-sink", $"sink_name={DirectSink}", "channels=12",
                "channel_map=" + string.Join(',', Enumerable.Range(0, 12).Select(i => "aux" + i)), "sink_properties=device.description=Pro-direct-raw"));
            modules.Add(LoadModule("module-null-sink", "sink_name=direct_cam_signal", "sink_properties=device.description=Webcam-signal"));
            modules.Add(LoadModule("module-remap-source", "source_name=direct_cam", "master=direct_cam_signal.monitor",
                "source_properties=device.description=Webcam-microphone"));
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == DirectSource) && pw.ListDevices().Any(d => d.Name == "direct_cam"));
            WaitForInputPorts(DirectSink, 12);

            devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [pro]);
            devices.SweepOnce();
            Assert.True(pro.Connected);
            mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            await mixer.StartAsync(CancellationToken.None);
            Assert.True(mixer.Built);
            MixerService service = mixer;
            ChannelStatus Channel(string id) => service.Snapshot()!.Channels.Single(c => c.Id == id);
            void Run(Command command) => Assert.Null(service.Apply(command));
            void Sweep() { devices.SweepOnce(); service.SweepOnce(); }

            // The Pro's jacks are listed and selected, the Aux return is routed.
            PrimaryWait(() => { Sweep(); return service.Devices()!.Any(d => d.Name == jack) && Channel("xlr1").BindingConnected; });
            Run(new() { Cmd = "setMonitorOutputs", Devices = [jack] });
            Run(new() { Cmd = "setAuxPortEnabled", Value = JsonSerializer.SerializeToElement(true) });
            PrimaryWait(() => { Sweep(); return GraphLinksInto(DirectSink).Count(l => l.From.StartsWith("OpenXLR_mix_aux", StringComparison.Ordinal)) == 2; });

            // XLR 1 on the interface carries input 1: unmuted in Monitor, the path is on and its send silenced.
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);
            Assert.Equal(new UnitInputStatus(pro.Info.InstanceId, 1, true), Channel("xlr1").UnitInput);
            PrimaryWait(() => { service.SweepOnce(); return LegMuted(pw, "xlr1", "monitor") == true; });

            Run(new() { Cmd = "setMonitorMuted", Value = JsonSerializer.SerializeToElement(true) });
            Assert.False(pro.MicDirect);
            Assert.False(pro.ReadState().OutHp1);
            Assert.True(pro.ReadState().OutUsbAux);
            Sweep();
            Assert.False(pro.MicDirect);
            Assert.False(pro.ReadState().OutHp1);
            Assert.Equal(2, GraphLinksInto(DirectSink).Count(l => l.From.StartsWith("OpenXLR_mix_aux", StringComparison.Ordinal)));
            Assert.DoesNotContain("monitor", Channel("xlr1").MutedIn);
            Run(new() { Cmd = "setMonitorMuted", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);
            Assert.True(pro.ReadState().OutHp1);

            // XLR 1 on the webcam: the Pro stays driven, no channel carries its
            // input 1, so the path is off and XLR 1's send is its own again.
            Run(new() { Cmd = "setInputBinding", Channel = "xlr1", Source = "direct_cam", CaptureMonoChannel = 0 });
            Assert.False(pro.MicDirect);
            Assert.Null(Channel("xlr1").UnitInput);
            Assert.Contains("monitor", Channel("xlr1").MutedIn);   // the console rule
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.False(pro.MicDirect);
            PrimaryWait(() => { service.SweepOnce(); return LegMuted(pw, "xlr1", "monitor") == false; });
            for (int i = 0; i < 3; i++) Sweep();
            Assert.True(pro.Connected);
            Assert.True(devices.Snapshot().Connected);
            Assert.Equal([jack], service.Snapshot()!.MonitorOutputs);
            Assert.Contains(service.Devices()!, d => d.Name == jack);
            Assert.Equal(2, GraphLinksInto(DirectSink).Count(l => l.From.StartsWith("OpenXLR_mix_aux", StringComparison.Ordinal)));
            Assert.Null(service.InputWarning);

            // The Pro's microphone kept as a capture channel takes the path over.
            Run(new() { Cmd = "createCaptureChannel", Name = "Pro mic", Source = DirectSource, CaptureMonoChannel = 0 });
            string kept = service.Snapshot()!.Channels.Single(c => c.Name == "Pro mic").Id;
            PrimaryWait(() => { Sweep(); return Channel(kept).BindingConnected; });
            Assert.Equal(new UnitInputStatus(pro.Info.InstanceId, 1, true), Channel(kept).UnitInput);
            Assert.False(pro.MicDirect);   // a new channel starts muted
            Run(new() { Cmd = "setChannelMuted", Channel = kept, Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);
            PrimaryWait(() => { service.SweepOnce(); return LegMuted(pw, kept, "monitor") == true; });
            Assert.False(LegMuted(pw, "xlr1", "monitor"));
            Run(new() { Cmd = "setChannelMuted", Channel = kept, Mix = "monitor", Value = JsonSerializer.SerializeToElement(true) });
            Assert.False(pro.MicDirect);
            Run(new() { Cmd = "setChannelMuted", Channel = kept, Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);

            // In an exclusive group the capture channel leaves the path, as XLR 1 does.
            Run(new() { Cmd = "setExclusiveGroup", Name = "Mics", Channels = ["xlr1", kept] });
            Assert.False(pro.MicDirect);
            string group = service.Snapshot()!.ExclusiveGroups.Single().Id;
            Run(new() { Cmd = "deleteExclusiveGroup", Group = group });
            Assert.Equal(!Channel(kept).MutedIn.Contains("monitor"), pro.MicDirect);

            // Back on the interface, XLR 1 carries input 1 again and starts
            // muted; the capture channel keeps its own send.
            Run(new() { Cmd = "setChannelMuted", Channel = kept, Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Run(new() { Cmd = "setInputBinding", Channel = "xlr1" });
            Assert.Null(Channel(kept).UnitInput);
            Assert.Equal(1, Channel("xlr1").UnitInput!.Input);
            Assert.False(pro.MicDirect);
            PrimaryWait(() => { service.SweepOnce(); return LegMuted(pw, kept, "monitor") == false; });
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);
            PrimaryWait(() => { service.SweepOnce(); return LegMuted(pw, "xlr1", "monitor") == true; });
            Assert.False(LegMuted(pw, kept, "monitor"));

            Run(new() { Cmd = "createMix", Name = "Temporary phones", Kind = "monitor" });
            Run(new() { Cmd = "setMonitorFeed", Device = jack, Mix = "temporary-phones" });
            Assert.False(pro.MicDirect);
            Run(new() { Cmd = "deleteMix", Mix = "temporary-phones" });
            // The fallback's direct path must be applied before the command
            // returns, without waiting for a later service sweep.
            Assert.True(pro.MicDirect);
            Assert.True(pro.ReadState().OutHp1);
        }
        finally
        {
            if (mixer is not null)
            {
                if (mixer.Built) await mixer.StopAsync(CancellationToken.None);
                mixer.Dispose();
            }
            devices?.SuspendSession();
            devices?.Dispose();
            foreach (uint module in Enumerable.Reverse(modules)) UnloadModule(module);
            if (File.Exists(MixerSettings.DefaultPath)) File.Delete(MixerSettings.DefaultPath);
            string state = Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00b4");
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }

    /// <summary>
    /// A direct path another program left on is switched off while no Pro
    /// jack is a monitor output: at startup, when it is turned on again
    /// meanwhile, and when the monitor moves from a jack to another device.
    /// Each is one write, and the return pair is only asserted for a jack.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task ADirectPathLeftOnIsSwitchedOffWhileNoProJackIsMonitored()
    {
        const string jack = DirectSink + "#hp1";
        const string katana = "direct_katana";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var modules = new List<uint>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["mixer"] = "true", ["monitorOutput"] = katana,
        }).Build();
        var pro = new DirectPro(new() { GainDb = 30, HpMixMicDirect = true, HpMixMonitorReturn = false });
        DeviceManager? devices = null;
        MixerService? mixer = null;
        try
        {
            modules.Add(LoadModule("module-null-sink", "sink_name=pro_direct_parent", "channels=6", "sink_properties=device.description=Pro-direct-parent"));
            WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains("pro_direct_parent.monitor", StringComparison.Ordinal));
            modules.Add(LoadModule("module-remap-source", $"source_name={DirectSource}", "master=pro_direct_parent.monitor", "channels=6",
                "source_properties=device.description=Pro-direct-capture"));
            modules.Add(LoadModule("module-null-sink", $"sink_name={DirectSink}", "channels=12",
                "channel_map=" + string.Join(',', Enumerable.Range(0, 12).Select(i => "aux" + i)), "sink_properties=device.description=Pro-direct-raw"));
            modules.Add(LoadModule("module-null-sink", $"sink_name={katana}", "sink_properties=device.description=Katana"));
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == DirectSource) && pw.ListDevices().Any(d => d.Name == katana));
            WaitForInputPorts(DirectSink, 12);

            devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [pro]);
            devices.SweepOnce();
            Assert.True(pro.Connected);
            Assert.True(devices.Snapshot().State!.HpMixMicDirect);
            mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            await mixer.StartAsync(CancellationToken.None);
            Assert.True(mixer.Built);
            MixerService service = mixer;
            void Run(Command command) => Assert.Null(service.Apply(command));
            void Sweep() { devices.SweepOnce(); service.SweepOnce(); }

            // Startup with the monitor on another device: the path goes off
            // once and stays off, and the return pair is left as it was.
            Assert.Equal([katana], service.Snapshot()!.MonitorOutputs);
            Assert.False(pro.MicDirect);
            Assert.False(devices.Snapshot().State!.HpMixMicDirect);
            for (int i = 0; i < 3; i++) Sweep();
            Assert.Equal(1, pro.MicDirectWrites);
            Assert.False(pro.MonitorReturn);

            // Turned on again from elsewhere, it goes off on the next sweep
            // that reads it (the timer's own pass may hold the first one off).
            pro.LeaveMicDirectOn();
            PrimaryWait(() => { Sweep(); return !pro.MicDirect; });
            for (int i = 0; i < 3; i++) Sweep();
            Assert.Equal(2, pro.MicDirectWrites);

            // Every monitor output a jack: the path follows XLR 1's Monitor send as before.
            PrimaryWait(() => { Sweep(); return service.Devices()!.Any(d => d.Name == jack)
                && service.Snapshot()!.Channels.Single(c => c.Id == "xlr1").BindingConnected; });
            Run(new() { Cmd = "setMonitorOutputs", Devices = [jack] });
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Assert.True(pro.MicDirect);
            Assert.True(pro.MonitorReturn);

            // The monitor moves to the other device: the path goes off, the
            // return pair stays asserted.
            Run(new() { Cmd = "setMonitorOutputs", Devices = [katana] });
            Assert.False(pro.MicDirect);
            Assert.True(pro.MonitorReturn);
            int writes = pro.MicDirectWrites;
            for (int i = 0; i < 3; i++) Sweep();
            Assert.Equal(writes, pro.MicDirectWrites);
        }
        finally
        {
            if (mixer is not null)
            {
                if (mixer.Built) await mixer.StopAsync(CancellationToken.None);
                mixer.Dispose();
            }
            devices?.SuspendSession();
            devices?.Dispose();
            foreach (uint module in Enumerable.Reverse(modules)) UnloadModule(module);
            if (File.Exists(MixerSettings.DefaultPath)) File.Delete(MixerSettings.DefaultPath);
            string state = Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-00b4");
            if (Directory.Exists(state)) Directory.Delete(state, true);
        }
    }

    /// <summary>The graph mute of one send, or null while its leg is not found.</summary>
    private static bool? LegMuted(PipeWireAdapter pw, string channel, string mix)
    {
        string? line = pw.Run("pactl", "list", "short", "modules").Split('\n').FirstOrDefault(l =>
            l.Contains($"sink_name=OpenXLR_ch_{channel} ", StringComparison.Ordinal));
        if (line is null || !pw.FindCombineLegs(uint.Parse(line.Split('\t')[0])).TryGetValue($"OpenXLR_mix_{mix}", out int index)) return null;
        using JsonDocument inputs = JsonDocument.Parse(pw.Run("pactl", "--format=json", "list", "sink-inputs"));
        foreach (JsonElement input in inputs.RootElement.EnumerateArray())
            if (input.GetProperty("index").GetInt32() == index) return input.GetProperty("mute").GetBoolean();
        return null;
    }

    /// <summary>
    /// A Wave XLR Pro as far as the daemon's output routing goes: the
    /// selectors and the headphone mix hold what was last written.
    /// </summary>
    private sealed class DirectPro(DeviceState? initial = null) : IAudioDevice
    {
        private DeviceState _state = initial ?? new() { GainDb = 30 };
        public int MicDirectWrites;
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4) { Location = new(1, 52, "1-" + DirectSerial, DirectSerial) };
        public DeviceCapabilities Capabilities { get; } = new()
        {
            Gain = true, Mute = true, LowCut = true, ClipGuard = true, OutputRouting = true, AuxInput = true, XlrInputs = 2, RetainsSettings = true,
        };
        public bool Connected { get; private set; }
        public bool MicDirect { get { lock (this) return _state.HpMixMicDirect; } }
        public bool MonitorReturn { get { lock (this) return _state.HpMixMonitorReturn; } }
        /// <summary>What Wave Link does to the unit from elsewhere: the daemon only reads it.</summary>
        public void LeaveMicDirectOn() => Write(s => s with { HpMixMicDirect = true });
        public DeviceState ReadState() { lock (this) return _state; }
        private void Write(Func<DeviceState, DeviceState> change) { lock (this) _state = change(_state); }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void SetOutHp1(bool on) => Write(s => s with { OutHp1 = on });
        public void SetOutHp2(bool on) => Write(s => s with { OutHp2 = on });
        public void SetOutUsbAux(bool on) => Write(s => s with { OutUsbAux = on, AuxReturnEnabled = on });
        public void SetOutLineOut(bool on) => Write(s => s with { OutLineOut = on });
        public void SetHpMixMonitorReturn(bool on) => Write(s => s with { HpMixMonitorReturn = on });
        public void SetHpMixMicDirect(bool on) { Interlocked.Increment(ref MicDirectWrites); Write(s => s with { HpMixMicDirect = on }); }
        public void SetGainDb(int db) => Write(s => s with { GainDb = db });
        public void SetMute(bool on) { } public void SetLowCut(bool on) { } public void SetExpander(bool on) { }
        public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int value) { } public void SetHpVolumeDb(double db) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { } public void SetPhantom(bool on) { }
        public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }
}
