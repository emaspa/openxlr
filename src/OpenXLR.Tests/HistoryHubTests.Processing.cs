using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Undo and redo of microphone and mix processing through the hub: each
/// setting is one value of a step, a drag across it is one step, a value
/// moved since is skipped, and an EQ that runs on the unit goes back to the
/// unit through the device port.
/// </summary>
public sealed partial class HistoryHubTests
{
    private const string Dock = "0fd9:00c7@0123456789abcdef";

    [Fact]
    public async Task AnEqBandDragIsOneStepAndAPauseStartsAnother()
    {
        using var f = new Fixture();
        MicEq original = Eq(f);
        foreach (double gain in new[] { 1, 2.5, 4 })
        {
            await f.Ok($$"""{"cmd":"setEqBand","channel":"xlr1","band":2,"gain":{{gain}}}""");
            f.Now += 80;
        }
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("eq", "xlr1", 1, "Mic 1 EQ"), (step.Kind, step.Channel, step.Count, step.Label));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setEqBand","channel":"xlr1","band":3,"frequency":3000}""");
        Assert.Equal(2, f.State().History!.UndoSteps);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(4, Eq(f).Bands[1].Gain);
        Assert.Equal(original.Bands[2].Frequency, Eq(f).Bands[2].Frequency);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(original, Eq(f));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(4, Eq(f).Bands[1].Gain);
    }

    [Fact]
    public async Task AnEqMovedSinceIsSkippedAndNamed()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setEqBand","channel":"xlr1","band":2,"gain":3}""");
        f.Mixer.SetEqBand("xlr1", 2, new EqBandChange(null, null, null, -3, null));
        Assert.Equal("undo: Mic 1 EQ was skipped: it changed since", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal(-3, Eq(f).Bands[1].Gain);
    }

    [Fact]
    public async Task AMixesEqIsItsOwnStep()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setEqBand","mix":"stream","band":1,"gain":-6}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("mixEq", null, "stream", "Stream EQ"), (step.Kind, step.Channel, step.Mix, step.Label));
        Assert.Equal(MicEq.Default, f.Value(SceneField.Eq, "xlr1"));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(MicEq.Default, f.Value(SceneField.MixEq, "stream"));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(-6, ((MicEq)f.Value(SceneField.MixEq, "stream")!).Bands[0].Gain);
    }

    [Fact]
    public async Task NoiseRemovalAndTheVoiceChangerAreUndone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setNoiseRemoval","channel":"xlr1","strength":40}""");
        Assert.Equal(("noiseRemoval", "Mic 1 noise removal"), (f.State().History!.Undo!.Kind, f.State().History!.Undo!.Label));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setVoiceChanger","channel":"xlr2","voice":"robot","pitch":3}""");
        Assert.Equal(("voiceChanger", "Mic 2 voice changer"), (f.State().History!.Undo!.Kind, f.State().History!.Undo!.Label));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(MicVoice.Default, f.Value(SceneField.VoiceChanger, "xlr2"));
        Assert.Equal(40, ((MicNoise)f.Value(SceneField.NoiseRemoval, "xlr1")!).Strength);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(MicNoise.Default, f.Value(SceneField.NoiseRemoval, "xlr1"));
        await f.Ok("""{"cmd":"redo"}""");
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(new MicVoice { Voice = "robot", Pitch = 3 }, f.Value(SceneField.VoiceChanger, "xlr2"));
    }

    [Fact]
    public async Task TheCompressorAndExpanderAreUndoneAndAKnobTurnIsOneStep()
    {
        using var f = new Fixture();
        foreach (int threshold in new[] { -20, -24, -30 })
        {
            await f.Ok($$"""{"cmd":"setCompressor","channel":"xlr1","threshold":{{threshold}}}""");
            f.Now += 80;
        }
        Assert.Equal(("compressor", "Mic 1 compressor"), (f.State().History!.Undo!.Kind, f.State().History!.Undo!.Label));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setExpander","channel":"xlr1","range":40}""");
        Assert.Equal(("expander", "Mic 1 expander"), (f.State().History!.Undo!.Kind, f.State().History!.Undo!.Label));
        Assert.Equal(2, f.State().History!.UndoSteps);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(MicExpander.Default, f.Value(SceneField.Expander, "xlr1"));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(MicCompressor.Default, f.Value(SceneField.Compressor, "xlr1"));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(-30, ((MicCompressor)f.Value(SceneField.Compressor, "xlr1")!).Threshold);
    }

    [Fact]
    public async Task TheLowCutIsUndone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setLowCutHz","value":80}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("lowCut", "xlr1", "Mic 1 low cut"), (step.Kind, step.Channel, step.Label));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(0, f.Value(SceneField.LowCut, SceneKeys.FirstMicrophone));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(80, f.Value(SceneField.LowCut, SceneKeys.FirstMicrophone));
    }

    [Fact]
    public async Task ClipGuardIsUndone()
    {
        using var f = new Fixture();
        // The unit has the hardware ClipGuard, so the software one is a
        // stored choice that needs no plugin on this machine.
        f.Mixer.SetClipGuardApplicable(false);
        await f.Ok("""{"cmd":"setSoftClipGuard","value":true}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("clipGuard", "xlr1", "Mic 1 ClipGuard"), (step.Kind, step.Channel, step.Label));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(false, f.Value(SceneField.SoftClipGuard, SceneKeys.FirstMicrophone));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(true, f.Value(SceneField.SoftClipGuard, SceneKeys.FirstMicrophone));
    }

    [Fact]
    public async Task TheDryMonitorIsUndone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setMonitorDry","mix":"monitor","value":true}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("monitorDry", "monitor", "Dry monitor in Monitor"), (step.Kind, step.Mix, step.Label));
        await f.Ok("""{"cmd":"undo"}""");
        Assert.False(f.Mixer.MonitorDry("monitor"));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.True(f.Mixer.MonitorDry("monitor"));
    }

    [Fact]
    public async Task TheDuckingSetupIsUndoneAndWhereItRunsStays()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setDucking","value":{"keys":["xlr1"],"channels":["xlr2"],"amount":"high"}}""");
        Assert.Equal(("ducking", "Ducking"), (f.State().History!.Undo!.Kind, f.State().History!.Undo!.Label));
        await f.Ok("""{"cmd":"setDuckingProcessing","value":"software"}""");
        Assert.Equal(1, f.State().History!.UndoSteps);

        await f.Ok("""{"cmd":"undo"}""");
        DuckingSetup setup = f.Mixer.DuckingSnapshot().Setup;
        Assert.Empty(setup.Channels);
        Assert.Equal(DuckingLevel.Preset(ProDucking.Medium), setup.Amount);
        Assert.Equal("software", setup.Processing);
        await f.Ok("""{"cmd":"redo"}""");
        setup = f.Mixer.DuckingSnapshot().Setup;
        Assert.Equal(["xlr2"], setup.Channels);
        Assert.Equal(DuckingLevel.Preset(ProDucking.High), setup.Amount);
    }

    [Fact]
    public async Task AnEqOnTheUnitIsWrittenBackThroughTheDevicePort()
    {
        using var f = new Fixture();
        var port = new EqPort();
        f.Mixer.SetDrivenUnit(Dock);
        f.Mixer.SetDeviceEqPort(port);
        f.Mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = new("Wave XLR Dock MK.2", "1.0.15", true) });

        await f.Ok("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"builtin:clear"}""");
        port.Sweep();
        Assert.True(f.Mixer.EqRunsOnDevice("xlr1"));
        Assert.Equal(DeviceEqMapping.ToDevice(EqPresets.Clear.Eq), port.Held[(Dock, 1)]);
        f.Now += 2000;
        await f.Ok("""{"cmd":"setEqBand","channel":"xlr1","band":3,"gain":6}""");
        port.Sweep();
        Assert.Equal(24, port.Held[(Dock, 1)].Bands![2].Gain);

        await f.Ok("""{"cmd":"undo"}""");
        port.Sweep();
        Assert.Equal(DeviceEqMapping.ToDevice(EqPresets.Clear.Eq), port.Held[(Dock, 1)]);
        await f.Ok("""{"cmd":"undo"}""");
        port.Sweep();
        Assert.False(port.Held[(Dock, 1)].Enabled);
        Assert.False(f.Mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Equal(MicEq.Default, Eq(f));

        await f.Ok("""{"cmd":"redo"}""");
        port.Sweep();
        Assert.Equal(DeviceEqMapping.ToDevice(EqPresets.Clear.Eq), port.Held[(Dock, 1)]);
        await f.Ok("""{"cmd":"redo"}""");
        port.Sweep();
        Assert.Equal(24, port.Held[(Dock, 1)].Bands![2].Gain);
        Assert.False(f.Mixer.EqWantsSoftwareStage("xlr1"));
    }

    [Fact]
    public async Task WhereProcessingRunsAndTheMaximizerAreNotSteps()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}""");
        await f.Ok("""{"cmd":"setDynamicsProcessing","channel":"xlr1","value":"software"}""");
        await f.Ok("""{"cmd":"setDuckingProcessing","value":"software"}""");
        Assert.Equal(0, f.State().History!.UndoSteps);
    }

    private static MicEq Eq(Fixture f) => (MicEq)f.Value(SceneField.Eq, "xlr1")!;

    /// <summary>The device side of a unit's EQ: posts wait for <see cref="Sweep"/>, which writes them as the unit would.</summary>
    private sealed class EqPort : IDeviceEqPort
    {
        private readonly Dictionary<(string, int), DeviceEq> _pending = [];
        private readonly Dictionary<(string, int), DeviceEqStatus> _status = [];
        public readonly Dictionary<(string, int), DeviceEq> Held = [];

        public void Post(string unit, int input, DeviceEq eq, string owner)
        {
            lock (this)
            {
                _pending[(unit, input)] = eq;
                _status.Remove((unit, input));
            }
        }

        public DeviceEqStatus Status(string unit, int input)
        {
            lock (this)
                return _pending.ContainsKey((unit, input)) ? new(DeviceEqPhase.Pending)
                    : _status.GetValueOrDefault((unit, input)) ?? DeviceEqStatus.None;
        }

        public void Sweep()
        {
            lock (this)
            {
                foreach (((string, int) key, DeviceEq eq) in _pending)
                {
                    Held[key] = eq.Bands is null && Held.TryGetValue(key, out DeviceEq? was) ? was with { Enabled = eq.Enabled } : eq;
                    _status[key] = new(DeviceEqPhase.Verified, eq);
                }
                _pending.Clear();
            }
        }
    }
}
