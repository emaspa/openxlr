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
    private const string MixSerial = "PROHWMIX";
    private const string MixSource = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_" + MixSerial + "-00.pro-input-0";
    private const string MixSink = "alsa_output.usb-Elgato_Systems_Elgato_Wave_XLR_Pro_" + MixSerial + "-00.pro-output-0";

    /// <summary>
    /// Hardware mixing against a private PipeWire server, with a 17-channel
    /// stand-in for the Pro's sink, an 18-channel one for its source and the
    /// unit modelled as its blocks. Entering takes Monitor A off Headphones 1's
    /// pair and links each channel to its own pair instead; the unit gets
    /// section 0 from the faders, byte 98 at 0 dB and Headphones 1 lowered by
    /// 21 dB and by the output volume, while the Pro's sink goes to 100%. The
    /// output volume then moves the headphone volume. With the USB Aux port
    /// on, the unit builds the Aux mix in section 36 from the same pairs, Aux
    /// In left out, and the graph's Aux mix leaves pair 5. Leaving puts the
    /// graph, the Aux mix's route, the sink's volume, sections 0 and 36, byte
    /// 98 and the jack back as they were.
    /// </summary>
    [MonitorPipeWireFact]
    public async Task HardwareMixingEntersAndLeavesWithTheGraphAndTheUnitAsTheyWere()
    {
        const string hp1 = MixSink + "#hp1";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        var modules = new List<uint>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["mixer"] = "true" }).Build();
        var pro = new MixingPro();
        DeviceManager? devices = null;
        MixerService? mixer = null;
        try
        {
            modules.Add(LoadModule("module-null-sink", "sink_name=pro_mix_parent", "channels=18",
                "channel_map=" + string.Join(',', Enumerable.Range(0, 18).Select(i => "aux" + i)), "sink_properties=device.description=Pro-mix-parent"));
            WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains("pro_mix_parent.monitor", StringComparison.Ordinal));
            modules.Add(LoadModule("module-remap-source", $"source_name={MixSource}", "master=pro_mix_parent.monitor", "channels=18",
                "channel_map=" + string.Join(',', Enumerable.Range(0, 18).Select(i => "aux" + i)), "source_properties=device.description=Pro-mix-capture"));
            modules.Add(LoadModule("module-null-sink", $"sink_name={MixSink}", "channels=17",
                "channel_map=" + string.Join(',', Enumerable.Range(0, 17).Select(i => "aux" + i)), "sink_properties=device.description=Pro-mix-raw"));
            WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == MixSource));
            WaitForInputPorts(pw, MixSink, 17);
            ProcessRunner.Run("pactl", ["set-sink-volume", MixSink, "50%"]);

            devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => [pro]);
            devices.SweepOnce();
            Assert.True(pro.Connected);
            mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            await mixer.StartAsync(CancellationToken.None);
            Assert.True(mixer.Built);
            MixerService service = mixer;
            void Run(Command command) => Assert.Null(service.Apply(command));
            void Sweep() { devices.SweepOnce(); service.SweepOnce(); }
            (string From, string To)[] Links() => GraphLinksInto(MixSink);
            string[] PortsFrom(string node) => [.. Links().Where(l => l.From.StartsWith(node + ":", StringComparison.Ordinal))
                .Select(l => l.To[(MixSink.Length + 1)..]).Distinct().Order()];
            double SinkVolume() => pw.GetSinkVolume(MixSink) ?? double.NaN;
            string[] Into(string port) => [.. Links().Where(l => l.To == MixSink + ":" + port).Select(l => l.From)];

            PrimaryWait(() => { Sweep(); return service.Devices()!.Any(d => d.Name == hp1); });
            Run(new() { Cmd = "setMonitorOutputs", Devices = [hp1] });
            Run(new() { Cmd = "setAuxPortEnabled", Value = JsonSerializer.SerializeToElement(true) });
            for (int i = 0; i < 2; i++) Sweep();
            WaitForRecovery(() => PortsFrom("OpenXLR_mix_monitor").SequenceEqual(["playback_AUX2", "playback_AUX3"]));
            // The Aux mix streams on pair 5 in software mixing.
            WaitForRecovery(() => Into("playback_AUX10").Length > 0 && Into("playback_AUX11").Length > 0);
            byte[] before = pro.Block;
            Assert.Equal(0x20, before[ProMixSections.SelectorUsbAux]);
            Assert.Equal(0x54, before[98]);
            Assert.Equal(0.5, SinkVolume(), 2);

            // The dry run: nothing in the way, nothing changed.
            HardwareMixingPlan dry = service.DryRunMixing(MixingMode.Hardware);
            Assert.True(dry.Active, dry.Note);
            Assert.Equal(21, dry.EntryOffsetsDb["hp1"], 3);
            Assert.Equal(before, pro.Block);

            Run(new() { Cmd = "setMixing", Value = JsonSerializer.SerializeToElement("hardware") });
            for (int i = 0; i < 3; i++) Sweep();
            Assert.True(service.Snapshot()!.HardwareMixing!.Active);
            // The graph: no mix on the jacks' pairs, each channel on its own.
            WaitForRecovery(() => PortsFrom("OpenXLR_mix_monitor").Length == 0);
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_system").SequenceEqual(["playback_AUX0", "playback_AUX1"]));
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_game").SequenceEqual(["playback_AUX2", "playback_AUX3"]));
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_music").SequenceEqual(["playback_AUX4", "playback_AUX5"]));
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_sfx").SequenceEqual(["playback_AUX12", "playback_AUX13"]));
            Assert.Empty(PortsFrom("OpenXLR_ch_xlr1"));
            Assert.Equal(1.0, SinkVolume(), 2);
            Assert.Equal(0.5, service.Snapshot()!.OutputVolume!.Value, 2);
            // The unit: section 0 from the faders, byte 98 at 0 dB, Headphones 1
            // down by 21 dB and by the output volume's 18.06 dB.
            byte[] mode = pro.Block;
            Assert.Equal(0x1e, mode[90]);
            Assert.Equal(0x00, mode[98]);
            Assert.Equal(0xf0, mode[12]);                       // pairs 0 to 3; XLR 1, Aux In and Line In muted in Monitor A
            Assert.Equal(0x0d, mode[13]);                       // pairs 4 and 6, and the cue on pair 7
            Assert.Equal(7, service.Snapshot()!.HardwareMixing!.CuePair);
            Assert.Equal(unchecked((byte)(sbyte)-36), mode[ProMixSections.SourceOfPair(1)]);   // Game at 0.5
            Assert.Equal(unchecked((byte)(sbyte)-27), mode[ProMixSections.SourceOfPair(0)]);   // System at 0.6
            Assert.Equal(0x00, mode[ProMixSections.SourceOfPair(2)]);
            Assert.Equal(ProJackLevels.ByteOf(-10 - 21 + 60 * Math.Log10(0.5)), pro.Levels[0]);
            HardwareEntry entry = MixerSettings.Load()!.HardwareEntry!;
            Assert.Equal(-31, entry.Jacks["hp1"], 3);
            Assert.Equal(21, entry.Offsets["hp1"], 3);
            Assert.Equal(0.5, entry.OutputVolume!.Value, 2);
            Assert.Equal(1, service.Snapshot()!.Channels.Single(c => c.Id == "game").HardwarePair);
            Assert.Equal(0, service.Snapshot()!.Mixes.Single(m => m.Id == "monitor").HardwareSection);
            // The Aux mix: built by the unit in section 36, the port still on
            // 20, Aux In and pair 5 out (no channel streams there), and the
            // graph's Aux mix off pair 5.
            Assert.Equal(36, service.Snapshot()!.Mixes.Single(m => m.Id == "auxout").HardwareSection);
            Assert.Equal(0x20, mode[ProMixSections.SelectorUsbAux]);
            Assert.Equal(0, mode[36 + 12] & 0x04);
            Assert.Equal(0, mode[36 + 13] & 0x02);
            Assert.Equal(service.DryRunMixing(MixingMode.Hardware).Content.Single(c => c.Section == 36).Hex,
                Convert.ToHexString(mode, 36, ProMixPlan.UnitSectionLength));
            WaitForRecovery(() => Into("playback_AUX10").Length == 0 && Into("playback_AUX11").Length == 0);
            PrimaryWait(() => MixerSettings.Load()?.HardwarePairs.GetValueOrDefault("game", -1) == 1);

            // The output volume moves the headphone volume, not the sink.
            Run(new() { Cmd = "setOutputVolume", Value = JsonSerializer.SerializeToElement(1.0) });
            Sweep();
            Assert.Equal(ProJackLevels.ByteOf(-31), pro.Levels[0]);
            Assert.Equal(1.0, SinkVolume(), 2);
            Run(new() { Cmd = "setOutputVolume", Value = JsonSerializer.SerializeToElement(0.5) });
            Sweep();
            Assert.Equal(ProJackLevels.ByteOf(-31 + 60 * Math.Log10(0.5)), pro.Levels[0]);

            // A fader moves a cell, a mute a membership.
            Run(new() { Cmd = "setLevel", Channel = "music", Mix = "monitor", Value = JsonSerializer.SerializeToElement(0.5) });
            Sweep();
            Assert.Equal(unchecked((byte)(sbyte)-36), pro.Block[ProMixSections.SourceOfPair(2)]);
            Run(new() { Cmd = "setChannelMuted", Channel = "game", Mix = "monitor", Value = JsonSerializer.SerializeToElement(true) });
            Sweep();
            Assert.Equal(0, pro.Block[12] & 0x20);
            // With the cue pair free the mode does not refuse an audition.
            Assert.DoesNotContain("USB pair", service.Apply(new() { Cmd = "auditionClip" }) ?? "");

            // A processed XLR 1 (the soft low cut, the unit having none here):
            // its channel plays back on pair 7, the first free, and the unit's
            // mixes take it there instead of the raw input; the cue moves to
            // pair 5. No write ever holds both, so the voice is never doubled.
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            for (int i = 0; i < 2; i++) Sweep();
            Assert.Equal(0x01, pro.Block[12] & 0x01);
            pro.History.Clear();
            Run(new() { Cmd = "setLowCutHz", Value = JsonSerializer.SerializeToElement(80) });
            PrimaryWait(() => { Sweep(); return (pro.Block[13] & 0x08) != 0; });
            Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, service.Snapshot()!.HardwareMixing!.ReturnPairs);
            Assert.Equal(5, service.Snapshot()!.HardwareMixing!.CuePair);
            Assert.Equal(7, service.Snapshot()!.Channels.Single(c => c.Id == "xlr1").HardwarePair);
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_xlr1").SequenceEqual(["playback_AUX14", "playback_AUX15"]));
            byte[] processed = pro.Block;
            Assert.Equal(0, processed[12] & 0x01);
            Assert.Equal(0x00, processed[ProMixSections.SourceOfPair(7)]);
            Assert.Equal(0x0f, processed[13] & 0x0f);          // pairs 4 and 6, XLR 1 on 7, the cue on 5
            // The cue reaches pair 5 in the same write that moves XLR 1 to 7:
            // no block ever has the raw input in beside that.
            Assert.NotEmpty(pro.History);
            Assert.DoesNotContain(pro.History, b => (b[12] & 0x01) != 0 && (b[13] & 0x02) != 0);
            Assert.DoesNotContain("processedMic:xlr1", service.Snapshot()!.HardwareMixing!.Disabled.Keys);
            // Processing off: back to the raw input, the pair unlinked, the cue on 7 again.
            pro.History.Clear();
            Run(new() { Cmd = "setLowCutHz", Value = JsonSerializer.SerializeToElement(0) });
            PrimaryWait(() => { Sweep(); return (pro.Block[12] & 0x01) != 0; });
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_xlr1").Length == 0);
            Assert.Equal(7, service.Snapshot()!.HardwareMixing!.CuePair);
            Assert.Empty(service.Snapshot()!.HardwareMixing!.ReturnPairs);
            Assert.DoesNotContain(pro.History, b => (b[12] & 0x01) != 0 && (b[13] & 0x02) != 0);
            Run(new() { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor", Value = JsonSerializer.SerializeToElement(true) });
            Sweep();

            // Ducking: the unit lowers Game and Music in sections 0 and 36
            // only, from their own pairs, keyed by XLR 1; Monitor A and the Aux
            // mix are the unit's alone, so no software stage runs for them.
            Run(new()
            {
                Cmd = "setDucking",
                Value = JsonSerializer.SerializeToElement(new { enabled = true, channels = new[] { "game", "music" }, mixes = new[] { "monitor", "auxout" } }),
            });
            PrimaryWait(() => { Sweep(); return service.Ducking().Device?.Held == true && (pro.Block[16] & 0x3c) == 0x3c && (pro.Block[52] & 0x3c) == 0x3c; });
            DuckingState ducking = service.Ducking();
            Assert.Equal("device", ducking.Backend);
            Assert.True(ducking.Device!.Ready, ducking.Device.Note);
            Assert.Equal([1, 2], ducking.Device.Readback!.Pairs);
            byte[] ducked = pro.Block;
            Assert.Equal(0, ducked[16] & 0x82);                 // pairs 0 and 3 not ducked
            Assert.Equal(0, ducked[17] & 0xaa);                 // nor 4 to 7
            Assert.Equal(0, ducked[52] & 0x82);
            Assert.Equal(0, ducked[53] & 0xaa);
            foreach (int at in new[] { 34, 70, 88 }) Assert.Equal(0, (ducked[at] | ducked[at + 1]) & 0xaa);
            Assert.Equal(0x01, pro.Ducking[5 * 8 + ProDucking.FlagsByte] & 0x01);
            Assert.Equal(0x01, pro.Ducking[6 * 8 + ProDucking.FlagsByte] & 0x01);
            Assert.DoesNotContain(service.Meters()!.Keys, k => k.StartsWith("duck:", StringComparison.Ordinal));
            Run(new() { Cmd = "setDucking", Value = JsonSerializer.SerializeToElement(new { enabled = false }) });
            PrimaryWait(() => { Sweep(); return (pro.Block[16] & 0xaa) == 0 && (pro.Block[52] & 0xaa) == 0; });

            // Leaving: the graph, the sink, section 0, byte 98 and the jack as before.
            Run(new() { Cmd = "setChannelMuted", Channel = "game", Mix = "monitor", Value = JsonSerializer.SerializeToElement(false) });
            Run(new() { Cmd = "setLevel", Channel = "music", Mix = "monitor", Value = JsonSerializer.SerializeToElement(1.0) });
            Run(new() { Cmd = "setMixing", Value = JsonSerializer.SerializeToElement("software") });
            PrimaryWait(() => { Sweep(); return MixerSettings.Load()?.HardwareEntry is null; });
            WaitForRecovery(() => PortsFrom("OpenXLR_mix_monitor").SequenceEqual(["playback_AUX2", "playback_AUX3"]));
            WaitForRecovery(() => PortsFrom("OpenXLR_ch_game").Length == 0 && PortsFrom("OpenXLR_ch_system").Length == 0);
            WaitForRecovery(() => Into("playback_AUX10").Length > 0 && Into("playback_AUX11").Length > 0);
            Assert.Equal(0.5, SinkVolume(), 2);
            byte[] after = pro.Block;
            Assert.Equal(0x54, after[98]);
            Assert.Equal(before[..16], after[..16]);
            Assert.Equal(before[36..52], after[36..52]);
            Assert.Equal(0x20, after[ProMixSections.SelectorUsbAux]);
            Assert.Equal(ProJackLevels.ByteOf(-10), pro.Levels[0]);
            Assert.False(service.Snapshot()!.HardwareMixing!.Active);
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
    /// A Wave XLR Pro on firmware 1.0.14 as far as hardware mixing goes:
    /// block 0x0001 and the jacks' levels in block 0x0005, each taking a
    /// plan's edits as the driver makes them. Headphones 1 starts at -10 dB.
    /// </summary>
    private sealed class MixingPro : IAudioDevice
    {
        private readonly byte[] _block = ProMixPlanTests.Neutral();
        private readonly byte[] _levels = [40, 0x02, 40, 0, 0, 0, 0, 0];
        private readonly byte[] _ducking = ProDuckingTests.Block8(ProDuckingTests.MediumBytes, ProDuckingTests.IdleFlags);

        public byte[] Block { get { lock (this) return (byte[])_block.Clone(); } }
        public byte[] Levels { get { lock (this) return (byte[])_levels.Clone(); } }
        public byte[] Ducking { get { lock (this) return (byte[])_ducking.Clone(); } }
        /// <summary>Block 0x0001 after each write that changed it, oldest first.</summary>
        public List<byte[]> History { get; } = [];

        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4) { Location = new(1, 53, "1-" + MixSerial, MixSerial) };
        public DeviceCapabilities Capabilities { get; } = new()
        {
            // No hardware low cut, so XLR 1's soft low cut runs in software
            // and the test can process the microphone without a plugin host.
            Gain = true, Mute = true, LowCut = false, ClipGuard = true, OutputRouting = true, AuxInput = true, LineInput = true,
            XlrInputs = 2, RetainsSettings = true, HardwareMixing = true, Ducking = true,
        };
        public DuckingReadback? ReadDucking() { lock (this) return ProDucking.Decode(_ducking, _block); }
        public string? Firmware => "1.0.14";
        public bool Connected { get; private set; }
        public DeviceState ReadState()
        {
            lock (this)
                return ProMixPlanTests.StateOf(_block) with
                {
                    HpVolumeDb = ProJackLevels.DbOf(_levels[0]),
                    Hp2VolumeDb = ProJackLevels.DbOf(_levels[2]),
                    LineOutLevelDb = ProJackLevels.DbOf(_levels[4]),
                };
        }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public ProMixWrite? WriteMix(ProMixPlan plan)
        {
            lock (this)
            {
                byte[] before = (byte[])_block.Clone(), levels = (byte[])_levels.Clone();
                if (plan.Ducking is { } ducking) ProDucking.ApplyEntries(_ducking, ducking);
                ProMixChanges changes = plan.Apply(before, _block);
                if (plan.JackLevels is { } wanted) wanted.All(levels).CopyTo(_levels, 0);
                bool wrote = !_block.AsSpan().SequenceEqual(before);
                if (wrote) History.Add((byte[])_block.Clone());
                return new ProMixWrite(changes, false, "not read", wrote, ProDucking.Diff(before, _block), wrote && changes.Commits())
                {
                    Change5 = levels.AsSpan().SequenceEqual(_levels) ? "" : ProDucking.Diff(levels, _levels),
                };
            }
        }
        public void SetGainDb(int db) { }
        public void SetMute(bool on) { } public void SetLowCut(bool on) { } public void SetExpander(bool on) { }
        public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int value) { }
        public void SetHpVolumeDb(double db) { lock (this) _levels[0] = ProJackLevels.ByteOf(db); }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int value) { } public void SetPhantom(bool on) { }
        public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }
}
