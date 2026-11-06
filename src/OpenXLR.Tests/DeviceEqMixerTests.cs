using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Where the mixer runs a microphone's EQ, against a fake device side: on the
/// unit when the channel carries a unit input whose unit runs it, the choice
/// is the unit, no Sound Check runs and the unit has not failed it; in
/// software otherwise. One log of the unit's EQ and the software stage the
/// mixer wants proves that no instant has both on, for the choice both ways,
/// Sound Check, a binding moving and a reconnect. A unit-run EQ starts no
/// stage and is available without the helper; values post once each; an
/// unplug posts nothing; a unit that connected again is told its EQ again,
/// and an input whose EQ runs in software holds its unit's EQ off.
/// </summary>
public sealed class DeviceEqMixerTests
{
    private const string Dock = "0fd9:00c7@0123456789abcdef";
    private const string Mk2 = "0fd9:00b6@fedcba9876543210";

    /// <summary>The device side: posts wait for <see cref="Sweep"/>, which writes them as a unit would.</summary>
    private sealed class Port : IDeviceEqPort
    {
        private readonly Dictionary<(string, int), DeviceEq> _pending = [];
        private readonly Dictionary<(string, int), DeviceEqStatus> _status = [];
        public readonly Dictionary<(string, int), DeviceEq> Held = [];
        public readonly List<(string Unit, int Input, DeviceEq Eq)> Posts = [];
        public readonly HashSet<string> Gone = [];
        public bool AutoSweep;

        public void Post(string unit, int input, DeviceEq eq, string owner)
        {
            Assert.False(string.IsNullOrEmpty(owner));
            if (Gone.Contains(unit)) return;
            lock (this)
            {
                Posts.Add((unit, input, eq));
                _pending[(unit, input)] = eq;
                _status.Remove((unit, input));
            }
            if (AutoSweep) Sweep();
        }

        public DeviceEqStatus Status(string unit, int input)
        {
            lock (this)
            {
                if (Gone.Contains(unit)) return DeviceEqStatus.None;
                if (_pending.ContainsKey((unit, input))) return new(DeviceEqPhase.Pending);
                return _status.GetValueOrDefault((unit, input)) ?? DeviceEqStatus.None;
            }
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

        public void Fail(string unit, int input)
        {
            lock (this)
            {
                _pending.Remove((unit, input));
                _status[(unit, input)] = new(DeviceEqPhase.Failed, null, "did not keep the EQ written to it");
            }
        }

        /// <summary>The unit connected again: what it holds stays, what was posted is forgotten.</summary>
        public void Reconnect(string unit)
        {
            lock (this)
            {
                foreach (var key in _status.Keys.Where(k => k.Item1 == unit).ToList()) _status.Remove(key);
                foreach (var key in _pending.Keys.Where(k => k.Item1 == unit).ToList()) _pending.Remove(key);
            }
        }

        public bool UnitOn(string unit, int input) { lock (this) return Held.TryGetValue((unit, input), out DeviceEq? eq) && eq.Enabled; }
    }

    private static UnitEqSupport Ready(string name = "Wave XLR Dock MK.2") => new(name, "1.0.15", true);

    private static Mixer Dock1(Port port, bool eqOn = true, ManualTime? time = null)
    {
        var mixer = new Mixer { Time = time ?? new ManualTime() };
        mixer.SetDrivenUnit(Dock);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() });
        if (eqOn) mixer.SetEqEnabled("xlr1", true);
        return mixer;
    }

    /// <summary>The invariant: never the unit's EQ and a software stage on the same voice at once.</summary>
    private static void NeverDoubled(Mixer mixer, Port port, string channel = "xlr1", string unit = Dock, int input = 1)
        => Assert.False(mixer.EqWantsSoftwareStage(channel) && port.UnitOn(unit, input),
            $"{channel}: the unit's EQ and a software stage are both on");

    private static MicEqState Eq(Mixer mixer, string channel = "xlr1") => mixer.Snapshot().Channels.Single(c => c.Id == channel).Eq!;

    [Fact]
    public void BandCountKeepsHardwareWhenItFitsAndReturnsAfterSoftwareFallback()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        MicEq original = mixer.EqFor("xlr1");
        MicEq small = original with { Bands = original.Bands.Take(2).ToArray() };
        mixer.SetEq("xlr1", small); port.Sweep();
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        DeviceEq held = port.Held[(Dock, 1)];
        Assert.Equal(4, held.Bands!.Count);
        Assert.All(held.Bands.Skip(2), b => Assert.False(b.Enabled));
        Assert.Equal(2, mixer.EqFor("xlr1").Bands.Count);
        MicEq large = original;
        while (large.Bands.Count < 16) large = large.WithAddedBand();
        mixer.SetEq("xlr1", large);
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        NeverDoubled(mixer, port);
        port.Sweep(); mixer.SyncDeviceEq();
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Equal("device", mixer.EqProcessingFor("xlr1"));
        Assert.False(mixer.CanRunEqOnDevice("xlr1", 16));
        Assert.True(mixer.CanRunEqOnDevice("xlr1", 2));
        var command = JsonSerializer.Deserialize<Command>(JsonSerializer.Serialize(new { cmd = "setEq", channel = "xlr1", eq = small },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(CommandValidation.Check(command, mixer, _ => null, nativeHostInstalled: false));
        mixer.SetEq("xlr1", small); port.Sweep();
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        NeverDoubled(mixer, port);
    }

    [Fact]
    public void AUnitThatRunsTheEqTakesItAndStartsNoStage()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        // Its EQ as it was when the unit took it, off, then switched on.
        Assert.Equal(2, port.Posts.Count);
        Assert.False(port.Posts[0].Eq.Enabled);
        (string unit, int input, DeviceEq eq) = port.Posts[1];
        Assert.Equal((Dock, 1), (unit, input));
        Assert.Equal(DeviceEqMapping.ToDevice(mixer.EqFor("xlr1")), eq);
        MicEqState state = Eq(mixer);
        Assert.Equal("device", state.Backend);
        Assert.True(state.Available, "a unit-run EQ needs no native helper");
        Assert.Null(state.Error);
        Assert.Equal("device", state.Processing);
        Assert.Equal(new MicEqDevice(Dock, 1, "Wave XLR Dock MK.2", "1.0.15", true), state.Device);
        // Switched off, the unit still has it: the backend stays the device.
        mixer.SetEqEnabled("xlr1", false);
        Assert.Equal("device", Eq(mixer).Backend);
        Assert.False(port.Posts[^1].Eq.Enabled);
        Assert.NotNull(port.Posts[^1].Eq.Bands);
        // XLR 2 is not carried: one input on this unit.
        Assert.Null(Eq(mixer, "xlr2").Device);
        Assert.DoesNotContain(port.Posts, p => p.Input == 2);
    }

    [Fact]
    public void ValuesPostOnceEachAndAnEqualValueIsNotPostedAgain()
    {
        var port = new Port { AutoSweep = true };
        using Mixer mixer = Dock1(port);
        int before = port.Posts.Count;
        mixer.SetEqBand("xlr1", 2, new(Gain: 6));
        mixer.SetEqBand("xlr1", 2, new(Gain: 6));
        Assert.Equal(before + 1, port.Posts.Count);
        Assert.Equal(24, port.Posts[^1].Eq.Bands![1].Gain);
        // A whole EQ, as a recall or a paste sends it: one post, nothing started, the choice kept.
        MicEq recalled = mixer.EqFor("xlr1").WithBand(3, mixer.EqFor("xlr1").Bands[2] with { Gain = -3 });
        mixer.SetEq("xlr1", recalled);
        Assert.Equal(before + 2, port.Posts.Count);
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Equal("device", mixer.EqProcessingFor("xlr1"));
        mixer.SyncDeviceEq();
        Assert.Equal(before + 2, port.Posts.Count);
    }

    [Fact]
    public void TheChoiceHandsOverOffBeforeOnBothWays()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        port.Sweep();
        NeverDoubled(mixer, port);
        Assert.True(port.UnitOn(Dock, 1));
        // Unit to software: the unit's switch off first, then software.
        mixer.SetEqProcessing("xlr1", "software");
        Assert.Equal(DeviceEq.Off, port.Posts[^1].Eq);
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"), "software waits for the unit to confirm");
        NeverDoubled(mixer, port);
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        port.Sweep();
        NeverDoubled(mixer, port);
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        // Release clears the switch only: the bands stay on the unit.
        Assert.Equal(DeviceEqMapping.ToDevice(mixer.EqFor("xlr1")).Bands, port.Held[(Dock, 1)].Bands);
        Assert.Equal("software", Eq(mixer).Processing);
        Assert.NotEqual("device", Eq(mixer).Backend);
        Assert.True(Eq(mixer).Device!.Ready);
        // Software to unit: the stage off at once, the unit after.
        mixer.SetEqProcessing("xlr1", "device");
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port);
        port.Sweep();
        NeverDoubled(mixer, port);
        Assert.True(port.UnitOn(Dock, 1));
        Assert.Equal("device", Eq(mixer).Backend);
    }

    [Fact]
    public void AUnitThatDoesNotConfirmIsLeftAfterTheWait()
    {
        var time = new ManualTime();
        var port = new Port();
        using Mixer mixer = Dock1(port, time: time);
        mixer.SetEqProcessing("xlr1", "software");
        time.Advance(Mixer.EqReleaseWait - TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
    }

    [Fact]
    public void EqReleaseStillExpiresAcrossTheLongUptimeTimestampBoundary()
    {
        var time = new ManualTime(timestampFrequency: 1_000_000_000);
        // The one-second release straddles the timestamp-to-ms overflow.
        time.Advance(TimeSpan.FromTicks(long.MaxValue / 1000 / 100 - TimeSpan.TicksPerSecond / 2));
        var port = new Port();
        using Mixer mixer = Dock1(port, time: time);
        mixer.SetEqProcessing("xlr1", "software");
        time.Advance(Mixer.EqReleaseWait - TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
    }

    [Fact]
    public async Task SoundCheckWaitsForTheUnitsOffBeforeRecordingAndAtMostTheWait()
    {
        // A unit that confirms at once: the record goes ahead without the clock moving.
        var time = new ManualTime();
        var port = new Port { AutoSweep = true };
        using (Mixer mixer = Dock1(port, time: time))
        {
            // No graph here, so the record itself is refused; the hand-over before it is what is checked.
            Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record"));
            Assert.Contains(port.Posts, p => p.Eq == DeviceEq.Off);
            // No session started: the unit takes the EQ back.
            Assert.True(mixer.EqRunsOnDevice("xlr1"));
            Assert.True(port.UnitOn(Dock, 1));
        }
        // A unit that never confirms: the record waits until the wait has passed on the clock.
        time = new ManualTime();
        port = new Port();
        using (Mixer mixer = Dock1(port, time: time))
        {
            Task record = Task.Run(() => Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record")));
            Assert.True(SpinWait.SpinUntil(() => port.Posts.Any(p => p.Eq == DeviceEq.Off), TimeSpan.FromSeconds(10)));
            time.Advance(Mixer.EqReleaseWait - TimeSpan.FromMilliseconds(1));
            Assert.NotSame(record, await Task.WhenAny(record, Task.Delay(100)));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await record.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(mixer.EqRunsOnDevice("xlr1"));
        }
    }

    [Fact]
    public void ABindingMovingXlr1OffTheUnitReleasesItAndOneBackTakesIt()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        port.Sweep();
        mixer.SetInputBinding("xlr1", new SourceBinding("alsa_input.usb-Some_Mic-00.mono-fallback", 0, 0));
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        Assert.Equal(DeviceEq.Off, port.Posts[^1].Eq);
        NeverDoubled(mixer, port);
        port.Sweep();
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Null(Eq(mixer).Device);
        Assert.False(port.UnitOn(Dock, 1));
        // A strip bound to the unit's own node at its port carries the unit.
        mixer.SetInputBinding("xlr1", new SourceBinding("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Dock_MK.2-00.pro-input-0", 0, 0) { Unit = Dock });
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        port.Sweep();
        NeverDoubled(mixer, port);
    }

    /// <summary>
    /// XLR 2 while a one-input unit is the primary, here with the jacks of the
    /// two-input unit before it still in place: the input is not the unit's,
    /// so XLR 2 is no EQ carrier. Nothing is posted to an input 2, nothing
    /// fails, and the state names no unit for XLR 2's EQ. A unit with two
    /// inputs carries both.
    /// </summary>
    [Fact]
    public void AnInputTheUnitDoesNotHaveCarriesNoEqAndNeverFails()
    {
        var port = new Port { AutoSweep = true };
        using var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetInputJacks(2, true);
        mixer.SetDrivenUnit(Dock);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() });
        mixer.SetEqEnabled("xlr1", true);
        mixer.SetEqEnabled("xlr2", true);
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        Assert.False(mixer.EqRunsOnDevice("xlr2"));
        Assert.DoesNotContain(port.Posts, p => p.Input == 2);
        MicEqState xlr2 = Eq(mixer, "xlr2");
        Assert.Null(xlr2.Device);
        Assert.True(mixer.EqWantsSoftwareStage("xlr2"));

        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() with { Inputs = 2 } });
        Assert.True(mixer.EqRunsOnDevice("xlr2"));
        Assert.Contains(port.Posts, p => p.Input == 2);
        Assert.True(Eq(mixer, "xlr2").Device!.Ready);
    }

    [Fact]
    public void AnUnplugPostsNothingAndAReconnectIsToldItsEqAgain()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        port.Sweep();
        int posts = port.Posts.Count;
        // Unplugged: the unit is no longer driven, and nothing is written to it.
        port.Gone.Add(Dock);
        mixer.SetDrivenUnit(null);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport>());
        Assert.Equal(posts, port.Posts.Count);
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
        // Back: the EQ is posted to it, and software lets go first.
        port.Gone.Remove(Dock);
        mixer.SetDrivenUnit(Dock);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() });
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Equal(posts + 1, port.Posts.Count);
        port.Sweep();
        NeverDoubled(mixer, port);
        // A reconnect the mixer never saw: the device side forgot the post, so it is made again.
        port.Reconnect(Dock);
        mixer.SyncDeviceEq();
        Assert.Equal(posts + 2, port.Posts.Count);
        Assert.Equal(DeviceEqMapping.ToDevice(mixer.EqFor("xlr1")), port.Posts[^1].Eq);
    }

    [Fact]
    public void AnInputWhoseEqRunsInSoftwareHoldsItsUnitsEqOffOncePerConnection()
    {
        var port = new Port { AutoSweep = true };
        using var mixer = new Mixer();
        mixer.SetEqProcessing("xlr1", "software");
        // Wave Link left the unit's EQ on.
        port.Held[(Dock, 1)] = DeviceEqMapping.ToDevice(MicEq.Default with { Enabled = true });
        mixer.SetDrivenUnit(Dock);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() });
        Assert.Equal(DeviceEq.Off, Assert.Single(port.Posts).Eq);
        Assert.False(port.UnitOn(Dock, 1));
        mixer.SyncDeviceEq();
        Assert.Single(port.Posts);
        port.Reconnect(Dock);
        mixer.SyncDeviceEq();
        Assert.Equal(2, port.Posts.Count);
    }

    [Fact]
    public void OlderFirmwareAndAnUncarriedUnitStayInSoftwareAndAreNeverWritten()
    {
        var port = new Port();
        using var mixer = new Mixer();
        mixer.SetDrivenUnit(Dock);
        mixer.SetDeviceEqPort(port);
        var old = new UnitEqSupport("Wave XLR Dock MK.2", "1.0.0", false, DeviceEqSupport.FirmwareOld,
            "This Wave XLR Dock MK.2 has firmware 1.0.0. From 1.0.15 the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.");
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = old });
        mixer.SetEqEnabled("xlr1", true);
        Assert.Empty(port.Posts);
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        MicEqDevice device = Eq(mixer).Device!;
        Assert.False(device.Ready);
        Assert.Equal("firmwareOld", device.Reason);
        Assert.Equal("1.0.0", device.Firmware);
        Assert.Equal("1.0.15", device.MinimumFirmware);
        Assert.Contains("1.0.15", device.Note);
        // An additional unit no channel carries is never written either.
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = old, [Mk2] = Ready("Wave XLR MK.2") });
        mixer.SetAttachedUnits([new AttachedUnit(Mk2, ["Wave_XLR_MK.2_SER5-"], 1, true, true)]);
        mixer.SyncDeviceEq();
        Assert.Empty(port.Posts);
        // Bound to the additional unit's node, XLR 2 carries it and the unit runs its EQ.
        mixer.SetInputBinding("xlr2", new SourceBinding("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_MK.2_SER5-00.pro-input-0", 0, 0) { Unit = Mk2 });
        mixer.SyncDeviceEq();
        Assert.True(mixer.EqRunsOnDevice("xlr2"));
        Assert.Equal((Mk2, 1), (port.Posts[^1].Unit, port.Posts[^1].Input));
        Assert.Equal("Wave XLR MK.2", Eq(mixer, "xlr2").Device!.Name);
    }

    [Fact]
    public void AUnitThatFailsTheEqHandsItToSoftwareUntilTheEqIsSwitchedAgain()
    {
        var port = new Port();
        using Mixer mixer = Dock1(port);
        port.Fail(Dock, 1);
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        MicEqDevice device = Eq(mixer).Device!;
        Assert.False(device.Ready);
        Assert.Equal("failed", device.Reason);
        Assert.Contains("did not keep the EQ written to it", device.Note);
        mixer.SyncDeviceEq();
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        // Off and on again: the unit is tried again.
        mixer.SetEqEnabled("xlr1", false);
        mixer.SetEqEnabled("xlr1", true);
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        Assert.True(Eq(mixer).Device!.Ready);
    }

    [Fact]
    public void WithoutTheDeviceSideEveryEqRunsInSoftwareAsBefore()
    {
        using var mixer = new Mixer();
        mixer.SetDrivenUnit(Dock);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Dock] = Ready() });
        mixer.SetEqEnabled("xlr1", true);
        Assert.False(mixer.EqRunsOnDevice("xlr1"));
        Assert.True(mixer.EqWantsSoftwareStage("xlr1"));
        Assert.Equal("device", Eq(mixer).Processing);
    }

    [Fact]
    public void TheChoiceIsSavedInTheSettingsAndNeverInAProfile()
    {
        using var mixer = new Mixer();
        Assert.Empty(mixer.ExportSettings().EqProcessing);
        mixer.SetEqProcessing("xlr2", "software");
        Assert.Equal(new Dictionary<string, string> { ["xlr2"] = "software" }, mixer.ExportSettings().EqProcessing);
        Assert.DoesNotContain("eqProcessing", JsonSerializer.Serialize(mixer.ExportScene(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        mixer.SetEqProcessing("xlr2", "device");
        Assert.Empty(mixer.ExportSettings().EqProcessing);
        Assert.Throws<ArgumentException>(() => mixer.SetEqProcessing("xlr1", "unit"));
        Assert.Throws<ArgumentException>(() => mixer.SetEqProcessing("aux", "software"));

        string dir = Directory.CreateTempSubdirectory("openxlr-eq-processing-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """{"eqProcessing":{"xlr1":"software","xlr2":"hardware","cap1":"device"}}""");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Contains("eqProcessing.xlr2", warning);
            Assert.Equal(new Dictionary<string, string> { ["xlr1"] = "software", ["cap1"] = "device" }, settings.EqProcessing);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.EqProcessing, MixerSettings.Load(path, out _)!.EqProcessing);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Pro = "0fd9:00b4@00112233445566aa";

    private static UnitEqSupport ProReady() => new("Wave XLR Pro", "1.0.14", true) { Inputs = 2, MinimumFirmware = "1.0.14" };

    /// <summary>The Wave XLR Pro as the primary, both jacks in the layout, its EQ ready on both inputs.</summary>
    private static Mixer Pro2(Port port)
    {
        var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetInputJacks(2, true);
        mixer.SetDrivenUnit(Pro);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Pro] = ProReady() });
        return mixer;
    }

    /// <summary>
    /// The Wave XLR Pro: XLR 1's EQ goes to its input 1 and XLR 2's to its
    /// input 2, each with its own switch and choice, and the state names the
    /// Pro, the input and its minimum firmware, 1.0.14. Sound Check on XLR 1
    /// moves XLR 1's EQ alone to software.
    /// </summary>
    [Fact]
    public void TheProRunsEachMicrophonesEqOnItsOwnInput()
    {
        var port = new Port { AutoSweep = true };
        using Mixer mixer = Pro2(port);
        mixer.SetEqEnabled("xlr1", true);
        mixer.SetEqEnabled("xlr2", true);
        mixer.SetEqBand("xlr2", 2, new(Gain: 12));
        Assert.True(mixer.EqRunsOnDevice("xlr1"));
        Assert.True(mixer.EqRunsOnDevice("xlr2"));
        Assert.True(port.UnitOn(Pro, 1));
        Assert.True(port.UnitOn(Pro, 2));
        Assert.Equal(48, port.Held[(Pro, 2)].Bands![1].Gain);
        Assert.NotEqual(48, port.Held[(Pro, 1)].Bands![1].Gain);
        Assert.Equal(new MicEqDevice(Pro, 2, "Wave XLR Pro", "1.0.14", true) { MinimumFirmware = "1.0.14" }, Eq(mixer, "xlr2").Device);
        Assert.Equal(1, Eq(mixer).Device!.Input);

        // XLR 2 in software: its input alone switches off, XLR 1 keeps the unit.
        int posts = port.Posts.Count;
        mixer.SetEqProcessing("xlr2", "software");
        mixer.SyncDeviceEq();
        Assert.Equal((Pro, 2, DeviceEq.Off), port.Posts[posts]);
        Assert.False(port.UnitOn(Pro, 2));
        Assert.True(port.UnitOn(Pro, 1));
        Assert.True(mixer.EqWantsSoftwareStage("xlr2"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"));
        NeverDoubled(mixer, port, "xlr2", Pro, 2);
        mixer.SetEqProcessing("xlr2", "device");
        port.Sweep();
        Assert.True(port.UnitOn(Pro, 2));

        // Sound Check on XLR 1: its EQ leaves the unit before the take; XLR 2's stays.
        posts = port.Posts.Count;
        Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record"));
        Assert.Contains(port.Posts.Skip(posts), p => p == (Pro, 1, DeviceEq.Off));
        Assert.DoesNotContain(port.Posts.Skip(posts), p => p.Input == 2);
        Assert.True(port.UnitOn(Pro, 2));
    }

    /// <summary>
    /// XLR 2 bound to another microphone: its EQ runs in software on that
    /// source and nothing of it reaches the Pro's input 2, which is held off
    /// once per connection so an EQ Wave Link left on (+12 dB on band 2) does
    /// not stay on the Pro's own paths. The same holds when the binding is in
    /// place before the Pro connects. Bound back to the Pro's own node at the
    /// second input's port, the Pro runs it again.
    /// </summary>
    [Fact]
    public void AnInputBoundToAnotherSourceNeverTakesTheProsEqAndHoldsItOff()
    {
        var waveLink = new DeviceEq(true, [new(true, 0, 0, 60, -10), new(true, 1, 48, 227, -1), new(true, 2, -12, 860, -14), new(true, 2, 7, 10237, -24)]);
        var port = new Port { AutoSweep = true };
        port.Held[(Pro, 1)] = waveLink;
        port.Held[(Pro, 2)] = waveLink;
        var mixer = new Mixer { Time = new ManualTime() };
        using (mixer)
        {
            mixer.SetInputJacks(2, true);
            mixer.SetInputBinding("xlr2", new SourceBinding("alsa_input.usb-Some_Mic-00.mono-fallback", 0, 0));
            mixer.SetEqEnabled("xlr2", true);
            mixer.SetDrivenUnit(Pro);
            mixer.SetDeviceEqPort(port);
            mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Pro] = ProReady() });
            mixer.SyncDeviceEq();
            Assert.False(mixer.EqRunsOnDevice("xlr2"));
            Assert.True(mixer.EqWantsSoftwareStage("xlr2"));
            Assert.Null(Eq(mixer, "xlr2").Device);
            // Input 2 held off, once; input 1 carries XLR 1, whose EQ is off and on the unit.
            Assert.Single(port.Posts, p => p.Input == 2);
            Assert.Equal(DeviceEq.Off, port.Posts.Single(p => p.Input == 2).Eq);
            Assert.False(port.UnitOn(Pro, 2));
            Assert.False(port.UnitOn(Pro, 1));
            Assert.Equal(waveLink.Bands, port.Held[(Pro, 2)].Bands);
            // XLR 2's edits stay in software.
            mixer.SetEqBand("xlr2", 2, new(Gain: 6));
            mixer.SyncDeviceEq();
            Assert.Single(port.Posts, p => p.Input == 2);
            // A reconnect: said again.
            port.Reconnect(Pro);
            mixer.SyncDeviceEq();
            Assert.Equal(2, port.Posts.Count(p => p.Input == 2 && p.Eq == DeviceEq.Off));

            // Bound to the Pro's own node at XLR 2's port: the Pro runs XLR 2's EQ.
            mixer.SetInputBinding("xlr2", new SourceBinding("alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_Pro-00.pro-input-0", 0, 2) { Unit = Pro });
            mixer.SyncDeviceEq();
            port.Sweep();
            mixer.SyncDeviceEq();
            Assert.True(mixer.EqRunsOnDevice("xlr2"));
            Assert.Equal(2, Eq(mixer, "xlr2").Device!.Input);
            Assert.True(port.UnitOn(Pro, 2));
            NeverDoubled(mixer, port, "xlr2", Pro, 2);
        }

        // The binding made while the Pro is driven: the move releases input 2, as on the MK.2.
        port = new Port { AutoSweep = true };
        using Mixer moved = Pro2(port);
        moved.SetEqEnabled("xlr2", true);
        Assert.True(port.UnitOn(Pro, 2));
        moved.SetInputBinding("xlr2", new SourceBinding("alsa_input.usb-Some_Mic-00.mono-fallback", 0, 0));
        moved.SyncDeviceEq();
        moved.SyncDeviceEq();
        Assert.False(port.UnitOn(Pro, 2));
        Assert.True(moved.EqWantsSoftwareStage("xlr2"));
        NeverDoubled(moved, port, "xlr2", Pro, 2);
        int posts = port.Posts.Count;
        moved.SyncDeviceEq();
        Assert.Equal(posts, port.Posts.Count);
    }

    private sealed class Layout(bool onDevice) : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2";
        public int OverrideCount => 0;
        public bool EqRunsOnDevice(string id) => onDevice && id == "xlr1";
    }

    [Theory]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1","value":"software"}""", null)]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr2","value":"device"}""", null)]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1","value":"unit"}""", "setEqProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1","value":true}""", "setEqProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"xlr1"}""", "setEqProcessing: value must be 'device' or 'software'")]
    [InlineData("""{"cmd":"setEqProcessing","channel":"aux","value":"software"}""", "setEqProcessing: 'aux' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqProcessing","value":"software"}""", "setEqProcessing: need 'channel'")]
    public void TheCommandTakesAMicrophoneAndOneOfTheTwoWords(string json, string? error)
        => Assert.Equal(error, CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
            new Layout(false), _ => null, nativeHostInstalled: true));

    [Fact]
    public void SwitchingOnAUnitRunEqNeedsNoHelper()
    {
        var on = JsonSerializer.Deserialize<Command>("""{"cmd":"setEqEnabled","channel":"xlr1","value":true}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(CommandValidation.Check(on, new Layout(true), _ => null, nativeHostInstalled: false));
        Assert.Equal(Mixer.EqNeedsHost, CommandValidation.Check(on, new Layout(false), _ => null, nativeHostInstalled: false));
    }
}
