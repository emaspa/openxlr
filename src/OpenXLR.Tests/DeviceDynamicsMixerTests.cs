using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Where the mixer runs a microphone's compressor and expander, against a
/// fake device side, by the EQ's rule: on the unit when the channel carries
/// a unit input whose unit runs them, the choice is the unit, no Sound Check
/// runs and the unit has not failed them; in software otherwise. One log of
/// the unit's switches and of what the software stage wants proves that no
/// instant has both on, for the choice both ways, a failure and a binding
/// moving. A unit-run compressor needs no helper, reports the values the unit
/// holds and posts each change once; software holds the unit's switches off;
/// a microphone with no setting of its own takes what its unit holds, once
/// the unit has been read; the Pro's two inputs are driven apart; and the
/// choice is machine wiring, saved and never in a profile.
/// </summary>
public sealed class DeviceDynamicsMixerTests
{
    private const string W3 = "0fd9:00ca@0123456789abcdef";
    private const string Pro = "0fd9:00b4@00112233445566aa";

    /// <summary>The device side: posts wait for <see cref="Sweep"/>, which writes them as a unit would.</summary>
    private sealed class Port : IDeviceEqPort
    {
        private readonly Dictionary<(string, int), DeviceDynamics> _pending = [];
        private readonly Dictionary<(string, int), DeviceDynamicsStatus> _status = [];
        public readonly Dictionary<(string, int), DeviceDynamics> Held = [];
        public readonly List<(string Unit, int Input, DeviceDynamics Value)> Posts = [];
        public readonly List<(string Unit, int Input)> Forgotten = [];
        public bool AutoSweep;
        /// <summary>Whether the unit has been read, so what it holds is known.</summary>
        public bool Read = true;

        public void Post(string unit, int input, DeviceEq eq, string owner) { }
        public DeviceEqStatus Status(string unit, int input) => DeviceEqStatus.None;

        public void PostDynamics(string unit, int input, DeviceDynamics dynamics, string owner)
        {
            Assert.False(string.IsNullOrEmpty(owner));
            lock (this)
            {
                Posts.Add((unit, input, dynamics));
                _pending[(unit, input)] = dynamics;
                _status.Remove((unit, input));
            }
            if (AutoSweep) Sweep();
        }

        public DeviceDynamicsStatus DynamicsStatus(string unit, int input)
        {
            lock (this)
            {
                if (_pending.ContainsKey((unit, input))) return new(DeviceEqPhase.Pending);
                return _status.GetValueOrDefault((unit, input)) ?? DeviceDynamicsStatus.None;
            }
        }

        public DeviceDynamics? HeldDynamics(string unit, int input) { lock (this) return Read ? Held.GetValueOrDefault((unit, input)) : null; }

        public void ForgetDynamics(string unit, int input)
        {
            lock (this)
            {
                Forgotten.Add((unit, input));
                _pending.Remove((unit, input));
                _status.Remove((unit, input));
            }
        }

        public void Sweep()
        {
            lock (this)
            {
                foreach (((string, int) key, DeviceDynamics value) in _pending)
                {
                    Held[key] = value.Values is null && Held.TryGetValue(key, out DeviceDynamics? was)
                        ? was with { Compressor = value.Compressor, Expander = value.Expander } : value;
                    _status[key] = new(DeviceEqPhase.Verified, value);
                }
                _pending.Clear();
            }
        }

        public void Fail(string unit, int input)
        {
            lock (this)
            {
                _pending.Remove((unit, input));
                _status[(unit, input)] = new(DeviceEqPhase.Failed, null, "did not keep the compressor and expander written to it");
            }
        }

        public bool UnitOn(string unit, int input)
        {
            lock (this) return Held.TryGetValue((unit, input), out DeviceDynamics? held) && (held.Compressor || held.Expander);
        }
    }

    private static readonly DeviceDynamicsValues Factory = new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24);

    private static UnitEqSupport Ready(bool dynamics = true, string? reason = null)
        => new("Wave:3 MK.2", "1.0.1", true) { MinimumFirmware = "0.2.3", Dynamics = new DeviceEqSupport(dynamics, reason) { MinimumFirmware = "0.2.3" } };

    /// <summary>A Wave:3 MK.2 as the primary unit, its compressor and expander ready, holding its factory settings switched off.</summary>
    private static Mixer Wave3(Port port, UnitEqSupport? support = null)
    {
        port.Held.TryAdd((W3, 1), new DeviceDynamics(false, false, Factory));
        var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetDrivenUnit(W3);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [W3] = support ?? Ready() });
        return mixer;
    }

    /// <summary>The invariant: never the unit's compressor or expander and a software one on the same voice at once.</summary>
    private static void NeverDoubled(Mixer mixer, Port port, string channel = "xlr1", string unit = W3, int input = 1)
        => Assert.False(mixer.DynamicsWantsSoftwareStage(channel) && port.UnitOn(unit, input),
            $"{channel}: the unit's dynamics and the software stage's are both on");

    private static MicCompressorState Compressor(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Compressor!;

    private static MicExpanderState Expander(Mixer mixer, string channel = "xlr1")
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Expander!;

    [Fact]
    public void AUnitThatRunsThemTakesThemAndReportsWhatItHolds()
    {
        var port = new Port { AutoSweep = true };
        using Mixer mixer = Wave3(port);
        // Nothing of its own: the microphone took the unit's settings, both off, and wrote nothing new.
        Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.Equal(MicCompressor.Default with { Threshold = -12, Ratio = 4, Attack = 10, Release = 100, Makeup = 6 }, mixer.CompressorFor("xlr1"));
        Assert.Equal(MicExpander.Default with { Threshold = -44, Ratio = 2, Attack = 10, Release = 200, Range = 24 }, mixer.ExpanderFor("xlr1"));
        Assert.Equal(new DeviceDynamics(false, false, Factory), port.Posts.Single().Value);

        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true, Ratio: 3.5, Attack: 0.5, Release: 2000, Makeup: 2.5));
        Assert.Equal(new DeviceDynamics(true, false, Factory with { CompressorRatio = 4, CompressorAttack = 1, CompressorRelease = 1000, CompressorMakeup = 3 }),
            port.Posts[^1].Value);
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"));
        Assert.True(port.UnitOn(W3, 1));
        // The setting keeps what was asked for; the state reports what the unit holds.
        Assert.Equal(3.5, mixer.CompressorFor("xlr1").Ratio);
        MicCompressorState state = Compressor(mixer);
        Assert.Equal((true, 4.0, 1.0, 1000.0, 3.0), (state.Enabled, state.Ratio, state.Attack, state.Release, state.Makeup));
        Assert.Equal("device", state.Backend);
        Assert.Equal("device", state.Processing);
        Assert.True(state.Available, "a unit-run compressor needs no native helper");
        Assert.Null(state.Error);
        Assert.Equal(new MicEqDevice(W3, 1, "Wave:3 MK.2", "1.0.1", true) { MinimumFirmware = "0.2.3", Levels = false }, state.Device);
        Assert.Equal("device", Expander(mixer).Backend);
        // An equal value is not posted again.
        int posts = port.Posts.Count;
        mixer.SetCompressor("xlr1", new MicCompressorChange(Ratio: 3.6));
        mixer.SyncDeviceEq();
        Assert.Equal(posts, port.Posts.Count);
        // The expander shares the post.
        mixer.SetExpander("xlr1", new MicExpanderChange(Enabled: true, Ratio: 2.4, Range: 80));
        Assert.Equal(new DeviceDynamics(true, true, Factory with
        {
            CompressorRatio = 4, CompressorAttack = 1, CompressorRelease = 1000, CompressorMakeup = 3, ExpanderRatio = 38, ExpanderRange = 60,
        }), port.Posts[^1].Value);
        Assert.Equal(60, Expander(mixer).Range);
    }

    [Fact]
    public void AMicrophoneWithNothingOfItsOwnWaitsForTheUnitsReadAndTakesWhatItHolds()
    {
        var port = new Port { AutoSweep = true, Read = false };
        port.Held[(W3, 1)] = new DeviceDynamics(true, false, Factory with { CompressorThreshold = -30, CompressorAttack = 300 });
        using Mixer mixer = Wave3(port);
        // Not read yet: nothing is written, so the switch it is about to take stays on.
        Assert.Empty(port.Posts);
        Assert.False(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.True(port.UnitOn(W3, 1));
        port.Read = true;
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.True(mixer.CompressorFor("xlr1").Enabled);
        Assert.Equal(-30, mixer.CompressorFor("xlr1").Threshold);
        Assert.Equal(200, mixer.CompressorFor("xlr1").Attack);   // the software's longest; the unit then holds 200
        Assert.True(port.UnitOn(W3, 1));
        Assert.Equal(200, port.Held[(W3, 1)].Values!.CompressorAttack);

        // A microphone with a setting of its own keeps it and the unit takes it.
        var other = new Port { AutoSweep = true };
        other.Held[(W3, 1)] = new DeviceDynamics(true, true, Factory);
        using var mine = new Mixer { Time = new ManualTime() };
        mine.SetCompressor("xlr1", new MicCompressorChange(Threshold: -24));
        mine.SetDrivenUnit(W3);
        mine.SetDeviceEqPort(other);
        mine.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [W3] = Ready() });
        Assert.False(mine.CompressorFor("xlr1").Enabled);
        Assert.False(other.UnitOn(W3, 1));
        Assert.Equal(-24, other.Held[(W3, 1)].Values!.CompressorThreshold);
    }

    /// <summary>
    /// Raw capture on the unit's input: the computer hears it before the
    /// unit's compressor and expander, so they leave the unit, switched off
    /// first, and run in software, and the state says why.
    /// </summary>
    [Fact]
    public void RawCaptureMovesThemToSoftware()
    {
        var port = new Port();
        using Mixer mixer = Wave3(port);
        port.Sweep();
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
        port.Sweep();
        Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.True(port.UnitOn(W3, 1));

        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [W3] = Ready() with { RawCaptureInputs = 1 } });
        Assert.Equal(DeviceDynamics.Off, port.Posts[^1].Value);
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"), "software waits for the unit to confirm");
        NeverDoubled(mixer, port);
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.False(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        Assert.False(Compressor(mixer).Device!.Ready);
        Assert.Equal("rawCapture", Compressor(mixer).Device!.Reason);
        Assert.Equal("device", Compressor(mixer).Processing);
    }

    [Fact]
    public void TheChoiceHandsOverOffBeforeOnBothWays()
    {
        var port = new Port();
        using Mixer mixer = Wave3(port);
        port.Sweep();
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
        port.Sweep();
        NeverDoubled(mixer, port);
        Assert.True(port.UnitOn(W3, 1));
        // Unit to software: the unit's switches off first, then software.
        mixer.SetDynamicsProcessing("xlr1", "software");
        Assert.Equal(DeviceDynamics.Off, port.Posts[^1].Value);
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"), "software waits for the unit to confirm");
        NeverDoubled(mixer, port);
        mixer.SyncDeviceEq();
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"));
        port.Sweep();
        NeverDoubled(mixer, port);
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        // The release keeps the unit's settings.
        Assert.Equal(DeviceDynamicsMapping.ToDevice(mixer.CompressorFor("xlr1"), mixer.ExpanderFor("xlr1")).Values, port.Held[(W3, 1)].Values);
        Assert.Equal("software", Compressor(mixer).Processing);
        Assert.NotEqual("device", Compressor(mixer).Backend);
        Assert.True(Compressor(mixer).Device!.Ready);
        // Software to unit: the stage's off at once, the unit after.
        mixer.SetDynamicsProcessing("xlr1", "device");
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        port.Sweep();
        NeverDoubled(mixer, port);
        Assert.True(port.UnitOn(W3, 1));
        Assert.Equal("device", Compressor(mixer).Backend);
        Assert.Throws<ArgumentException>(() => mixer.SetDynamicsProcessing("xlr1", "unit"));
    }

    [Fact]
    public void AUnitThatDoesNotConfirmIsLeftAfterTheWait()
    {
        var time = new ManualTime();
        var port = new Port();
        using Mixer mixer = Wave3(port);
        mixer.Time = time;
        mixer.SetExpander("xlr1", new MicExpanderChange(Enabled: true));
        mixer.SetDynamicsProcessing("xlr1", "software");
        time.Advance(Mixer.EqReleaseWait - TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
    }

    [Fact]
    public void SoftwareHoldsTheUnitsSwitchesOffOncePerConnection()
    {
        var port = new Port { AutoSweep = true };
        port.Held[(W3, 1)] = new DeviceDynamics(true, true, Factory);
        using var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetDynamicsProcessing("xlr1", "software");
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
        mixer.SetDrivenUnit(W3);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [W3] = Ready() });
        Assert.Equal((W3, 1, DeviceDynamics.Off), port.Posts.Single());
        Assert.False(port.UnitOn(W3, 1));
        Assert.Equal(Factory, port.Held[(W3, 1)].Values);
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        mixer.SyncDeviceEq();
        Assert.Single(port.Posts);
        Assert.Null(Compressor(mixer).Backend);   // no graph here, so no stage runs
        Assert.Equal("software", Compressor(mixer).Processing);
    }

    [Fact]
    public void AUnitThatCannotRunThemOrFailsLeavesThemToSoftware()
    {
        // Older firmware: no target, nothing written, the reason in the state.
        var port = new Port { AutoSweep = true };
        using (Mixer mixer = Wave3(port, Ready(dynamics: false, reason: DeviceEqSupport.FirmwareOld)))
        {
            mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
            Assert.False(mixer.DynamicsRunOnDevice("xlr1"));
            Assert.Empty(port.Posts);
            Assert.Equal(DeviceEqSupport.FirmwareOld, Compressor(mixer).Device!.Reason);
            Assert.False(Compressor(mixer).Device!.Ready);
            Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        }
        // A model without them: no device in the state at all.
        port = new Port { AutoSweep = true };
        using (Mixer mixer = Wave3(port, new UnitEqSupport("Wave XLR MK.2", "1.0.15", true)))
        {
            Assert.Null(Compressor(mixer).Device);
            Assert.Empty(port.Posts);
        }
        // A unit that does not keep them: software, the reason "failed", and switching on tries again.
        port = new Port();
        using (Mixer mixer = Wave3(port))
        {
            port.Sweep();
            mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
            port.Fail(W3, 1);
            mixer.SyncDeviceEq();
            Assert.False(mixer.DynamicsRunOnDevice("xlr1"));
            Assert.Equal(Mixer.EqDeviceFailedReason, Compressor(mixer).Device!.Reason);
            Assert.False(Compressor(mixer).Device!.Ready);
            // Its switches are written off, and software takes over once that is confirmed.
            Assert.Equal(DeviceDynamics.Off, port.Posts[^1].Value);
            port.Sweep();
            mixer.SyncDeviceEq();
            Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
            NeverDoubled(mixer, port);
            mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: false));
            mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
            Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        }
        // Without the device side every compressor runs in software, as before.
        using (var mixer = new Mixer())
        {
            mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
            Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
            Assert.Null(Compressor(mixer).Device);
            Assert.Equal("device", Compressor(mixer).Processing);
        }
    }

    [Fact]
    public void ABindingMovingXlr1OffTheUnitHandsThemToSoftware()
    {
        var port = new Port();
        using Mixer mixer = Wave3(port);
        port.Sweep();
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
        port.Sweep();
        mixer.SetInputBinding("xlr1", new SourceBinding("alsa_input.usb-Some_Mic-00.mono-fallback", 0, 0));
        mixer.SyncDeviceEq();
        Assert.False(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.Equal(DeviceDynamics.Off, port.Posts[^1].Value);
        NeverDoubled(mixer, port);
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        Assert.Null(Compressor(mixer).Device);
        Assert.False(port.UnitOn(W3, 1));
        // The primary's input stays held off while no microphone carries it.
        Assert.Empty(port.Forgotten);
    }

    /// <summary>
    /// The primary moves to another unit and the one XLR 1 ran on stays
    /// driven as an additional interface: its switches are written off,
    /// and once it confirms that, the mixer forgets the input, so the unit
    /// offers its own switches again. Software chosen on a unit that still
    /// carries the microphone keeps them held off.
    /// </summary>
    [Fact]
    public void AUnitNoMicrophoneHoldsAfterTheHandOverIsForgotten()
    {
        var port = new Port();
        using Mixer mixer = Wave3(port);
        port.Sweep();
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true));
        port.Sweep();
        mixer.SetDynamicsProcessing("xlr1", "software");
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        Assert.Empty(port.Forgotten);
        mixer.SetDynamicsProcessing("xlr1", "device");
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        Assert.True(port.UnitOn(W3, 1));

        mixer.SetDrivenUnit(Pro);
        mixer.SyncDeviceEq();
        Assert.Equal((W3, 1, DeviceDynamics.Off), port.Posts[^1]);
        Assert.Empty(port.Forgotten);   // not before the unit confirms
        NeverDoubled(mixer, port);
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.Equal([(W3, 1)], port.Forgotten);
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr1"));
        Assert.False(port.UnitOn(W3, 1));
        // Nothing more is posted to it.
        int posts = port.Posts.Count;
        mixer.SyncDeviceEq();
        Assert.Equal(posts, port.Posts.Count);
    }

    [Fact]
    public void TheProRunsEachMicrophonesOnItsOwnInput()
    {
        var port = new Port { AutoSweep = true };
        port.Held[(Pro, 1)] = new DeviceDynamics(true, false, Factory);
        port.Held[(Pro, 2)] = new DeviceDynamics(false, false, Factory);
        using var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetInputJacks(2, true);
        mixer.SetDrivenUnit(Pro);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport>
        {
            [Pro] = new("Wave XLR Pro", "1.0.14", true) { Inputs = 2, MinimumFirmware = "1.0.14", Dynamics = new DeviceEqSupport(true) { MinimumFirmware = "1.0.14" } },
        });
        // XLR 1 took the Pro's compressor that was on; XLR 2 its, off.
        Assert.True(mixer.CompressorFor("xlr1").Enabled);
        Assert.False(mixer.CompressorFor("xlr2").Enabled);
        Assert.True(port.UnitOn(Pro, 1));
        mixer.SetExpander("xlr2", new MicExpanderChange(Enabled: true, Threshold: -50));
        Assert.Equal((Pro, 2), (port.Posts[^1].Unit, port.Posts[^1].Input));
        Assert.Equal(-50, port.Held[(Pro, 2)].Values!.ExpanderThreshold);
        Assert.Equal(-44, port.Held[(Pro, 1)].Values!.ExpanderThreshold);
        Assert.Equal(2, Expander(mixer, "xlr2").Device!.Input);
        // XLR 2 in software: its input alone switches off.
        mixer.SetDynamicsProcessing("xlr2", "software");
        mixer.SyncDeviceEq();
        Assert.Equal((Pro, 2, DeviceDynamics.Off), port.Posts[^1]);
        Assert.True(port.UnitOn(Pro, 1));
        Assert.False(port.UnitOn(Pro, 2));
        Assert.True(mixer.DynamicsWantsSoftwareStage("xlr2"));
        Assert.False(mixer.DynamicsWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port, "xlr2", Pro, 2);
    }

    [Fact]
    public void TheChoiceIsSavedInTheSettingsAndNeverInAProfile()
    {
        using var mixer = new Mixer();
        Assert.Empty(mixer.ExportSettings().DynamicsProcessing);
        mixer.SetDynamicsProcessing("xlr2", "software");
        Assert.Equal(new Dictionary<string, string> { ["xlr2"] = "software" }, mixer.ExportSettings().DynamicsProcessing);
        Assert.DoesNotContain("dynamicsProcessing", JsonSerializer.Serialize(mixer.ExportScene(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        mixer.SetDynamicsProcessing("xlr2", "device");
        Assert.Empty(mixer.ExportSettings().DynamicsProcessing);
        Assert.Throws<ArgumentException>(() => mixer.SetDynamicsProcessing("aux", "software"));

        string dir = Directory.CreateTempSubdirectory("openxlr-dynamics-processing-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """{"dynamicsProcessing":{"xlr1":"software","xlr2":"hardware"}}""");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Contains("dynamicsProcessing.xlr2", warning);
            Assert.Equal(new Dictionary<string, string> { ["xlr1"] = "software" }, settings.DynamicsProcessing);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.DynamicsProcessing, MixerSettings.Load(path, out _)!.DynamicsProcessing);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class Layout(bool onDevice, bool capture = false) : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" || capture && id == "guest";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2";
        public int OverrideCount => 0;
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" || capture && id == "guest";
        public bool EqRunsInSoftwareOnly(string id) => capture && id == "guest";
        public bool DynamicsRunOnDevice(string id) => onDevice && id == "xlr1";
    }

    private static string? Check(string json, Layout layout, bool host)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
            layout, _ => null, nativeHostInstalled: host);

    [Theory]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"xlr1","value":"software"}""", null)]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"xlr2","value":"device"}""", null)]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"xlr1","value":"unit"}""", "setDynamicsProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"xlr1","value":true}""", "setDynamicsProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"xlr1"}""", "setDynamicsProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"aux","value":"software"}""", "setDynamicsProcessing: 'aux' is not a microphone input")]
    [InlineData("""{"cmd":"setDynamicsProcessing","value":"software"}""", "setDynamicsProcessing: need 'channel'")]
    [InlineData("""{"cmd":"setDynamicsProcessing","channel":"guest","value":"device"}""", "setDynamicsProcessing: 'guest' is a capture input, whose compressor and expander always run on this computer")]
    public void TheCommandTakesXlr1OrXlr2AndOneOfTheTwoWords(string json, string? error)
        => Assert.Equal(error, Check(json, new Layout(false, capture: true), host: true));

    [Fact]
    public void SwitchingOnAUnitRunCompressorOrExpanderNeedsNoHelper()
    {
        foreach (string json in new[]
        {
            """{"cmd":"setCompressor","channel":"xlr1","enabled":true}""", """{"cmd":"setExpander","channel":"xlr1","enabled":true}""",
            // A unit that reports their levels sends them without the helper too.
            """{"cmd":"watchDynamics","channel":"xlr1"}""",
        })
        {
            Assert.Null(Check(json, new Layout(true), host: false));
            Assert.Equal(Mixer.DynamicsNeedsHost, Check(json, new Layout(false), host: false));
        }
        // A value outside the software's range is refused whichever side runs it.
        Assert.Equal("setCompressor: ratio must be 1 to 20 to 1", Check("""{"cmd":"setCompressor","channel":"xlr1","ratio":21}""", new Layout(true), host: false));
    }
}
