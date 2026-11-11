using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's section levels, mutes and maximizers in hardware mixing
/// (desk measurements of 2026-11-11): where the bytes are and how the
/// diagnostics read them; a plan's writes, which keep every other byte and
/// need no commit; the planner's split of a mix's volume and mute into the
/// section's own bytes for a mix built alone in sections 18 to 54; the
/// maximizer on a mix built alone, bypassed everywhere else, with its
/// command, its saved amount and its <c>max:&lt;id&gt;</c> meter.
/// </summary>
public sealed class ProMixMaximizerTests
{
    private const string Unit = "0fd9:00b4";

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The default layout on a ready Pro: Headphones 1 plays Monitor A on
    /// section 0, Headphones 2 Monitor B on section 18 at a fader of -6 dB.
    /// XLR 1 sends at -3 dB and Game at -10 dB into both.
    /// </summary>
    private static HardwareMixingSetup Setup(double monitor2Db = -6, bool muted = false) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = MixerConfig.Default().Channels,
        Mixes = MixerConfig.Default().Mixes,
        Jacks = [("hp1", "monitor"), ("hp2", "monitor2")],
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
        SendsDb = new Dictionary<string, double>
        {
            ["xlr1|monitor"] = -3, ["game|monitor"] = -10, ["xlr1|monitor2"] = -3, ["game|monitor2"] = -10, ["music|monitor2"] = 0,
        },
        LevelsDb = new Dictionary<string, double>
        {
            ["xlr1|monitor"] = -3, ["game|monitor"] = -10,
            ["xlr1|monitor2"] = muted ? double.NegativeInfinity : -3 + monitor2Db,
            ["game|monitor2"] = muted ? double.NegativeInfinity : -10 + monitor2Db,
            ["music|monitor2"] = muted ? double.NegativeInfinity : monitor2Db,
        },
        MixFadersDb = new Dictionary<string, double> { ["monitor"] = 0, ["monitor2"] = monitor2Db, ["auxout"] = 0 },
        MixVolumesDb = new Dictionary<string, double> { ["monitor"] = 0, ["monitor2"] = muted ? double.NegativeInfinity : monitor2Db, ["auxout"] = 0 },
        MutedMixes = muted ? new HashSet<string> { "monitor2" } : new HashSet<string>(),
    };

    private static HardwareSectionContent Section(HardwareMixingPlan plan, int section) => plan.Content.Single(c => c.Section == section);

    private static byte Cell(double db) => ProMixSections.CellOf(db);

    // --- the bytes ---

    [Fact]
    public void EachSectionHasALevelAndAMuteByteAfterSectionZerosAndAMaximizerOfItsOwn()
    {
        Assert.Equal([98, 100, 102, 104, 106], ProMixSections.Starts.Select(ProMixSections.MasterOffset));
        Assert.Equal([99, 101, 103, 105, 107], ProMixSections.Starts.Select(ProMixSections.MuteOffset));
        Assert.Equal([0, 1, 2, 3, 4], ProMixSections.Starts.Select(ProMixSections.MaximizerOf));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.MasterOffset(17));

        byte[] b = ProMixPlanTests.Neutral();
        b[100] = 0x28;
        b[101] = 0x01;
        b[18 + 14] = 50;
        b[18 + 13] |= 0x10;
        Assert.Equal(-10, ProMixSections.MasterDb(b, 18));
        Assert.True(ProMixSections.IsMuted(b, 18));
        Assert.False(ProMixSections.IsMuted(b, 36));
        Assert.Equal(50, ProMixSections.MaximizerAmount(b, 18));
        Assert.True(ProMixSections.MaximizerBypassed(b, 18));
        Assert.False(ProMixSections.MaximizerBypassed(b, 0));
        // The bypass is no source: no source reads as a member for it.
        Assert.All(Enumerable.Range(0, ProMixSections.SourceCount), source => Assert.False(ProMixSections.IsMember(b, 18, source)));

        IReadOnlyDictionary<string, string> d = ProMixSections.Describe(b);
        Assert.Equal("no members; ducks nothing; inputs aa, fields 5555; maximizer 50 bypassed; level -10 dB (byte 100 28), muted, byte 101 01", d["section18"]);
        Assert.Equal("no members; ducks nothing; inputs aa, fields 5555; maximizer 0; byte 99 00", d["section0"]);
    }

    [Fact]
    public void TheMetersCarryEachSectionsMaximizerReductionAndInput()
    {
        byte[] block = ProMetersTests.Idle();
        // Section 18's maximizer, bytes 2 and 3: 9.25 dB down, input at -1 dBFS.
        block[2] = 0x25;
        block[3] = 0x04;
        ProMeterReading reading = ProMeters.Decode(block)!;
        Assert.Equal(ProMeters.MaximizerCount, reading.Maximizers.Count);
        Assert.Equal(new ProMaximizerMeter(9.25, -1), reading.Maximizers[ProMixSections.MaximizerOf(18)]);
        Assert.Equal(new ProMaximizerMeter(0, null), reading.Maximizers[0]);
    }

    // --- the plan's writes ---

    [Fact]
    public void APlanWritesTheLevelsMutesAndAmountsAndKeepsEveryOtherByte()
    {
        byte[] held = ProMixPlanTests.Neutral();
        held[101] = 0x82;   // bits the plan does not own
        held[103] = 0x01;
        var plan = new ProMixPlan
        {
            SectionMasters = new Dictionary<int, ProSectionMaster> { [18] = new(0x28, true), [36] = ProSectionMaster.Unity },
            UnitMaximizers = new Dictionary<int, byte> { [18] = 50, [0] = 0 },
        };
        Assert.Equal(ProMixChanges.SectionMasters | ProMixChanges.Maximizers, plan.Needs(held));
        byte[] into = new byte[ProMixSections.Length];
        ProMixChanges changes = plan.Apply(held, into);
        Assert.Equal(ProMixChanges.SectionMasters | ProMixChanges.Maximizers, changes);
        // Both apply live: no commit (desk, 2026-11-11).
        Assert.False(changes.Commits());
        Assert.False(changes.Membership());
        Assert.Equal(0x28, into[100]);
        Assert.Equal(0x83, into[101]);
        Assert.Equal(0x00, into[103]);
        Assert.Equal(50, into[18 + 14]);
        for (int i = 0; i < into.Length; i++)
            if (i is not (100 or 101 or 103 or 32)) Assert.Equal(held[i], into[i]);
        Assert.Equal(ProMixChanges.None, plan.Needs(into));

        // Unmuted again: bit 0 alone goes.
        byte[] again = new byte[ProMixSections.Length];
        new ProMixPlan { SectionMasters = new Dictionary<int, ProSectionMaster> { [18] = new(0x28, false) } }.Apply(into, again);
        Assert.Equal(0x82, again[101]);
    }

    [Fact]
    public void APlanTakesNoLevelByteForSectionZeroAndNoAmountOverOneHundred()
    {
        byte[] held = ProMixPlanTests.Neutral();
        byte[] into = new byte[ProMixSections.Length];
        Assert.Throws<ArgumentException>(() => new ProMixPlan { SectionMasters = new Dictionary<int, ProSectionMaster> { [0] = ProSectionMaster.Unity } }.Apply(held, into));
        Assert.Throws<ArgumentException>(() => new ProMixPlan { SectionMasters = new Dictionary<int, ProSectionMaster> { [20] = ProSectionMaster.Unity } }.Apply(held, into));
        Assert.Throws<ArgumentException>(() => new ProMixPlan { UnitMaximizers = new Dictionary<int, byte> { [18] = 101 } }.Apply(held, into));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(-10, 0x28)]
    [InlineData(-21, 0x54)]
    [InlineData(-63.75, 0xff)]
    [InlineData(3, 0x00)]
    [InlineData(double.NegativeInfinity, 0xff)]
    public void ASectionsLevelIsQuarterDecibelsOfAttenuation(double db, byte level) => Assert.Equal(level, ProSectionMaster.LevelOf(db));

    // --- the planner ---

    [Fact]
    public void AMixBuiltAloneInSectionEighteenHasItsVolumeInTheSectionsLevelAndItsSendsInTheCells()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup());
        Assert.True(plan.Active, plan.Note);
        Assert.Equal(18, plan.Sections["monitor2"]);
        HardwareSectionContent b = Section(plan, 18);
        Assert.Equal(new ProSectionMaster(0x18, false), b.Master);
        Assert.Equal(Cell(-3), b.Cells[ProMixSections.Xlr1]);
        Assert.Equal(Cell(-10), b.Cells[ProMixSections.SourceOfPair(1)]);
        Assert.Equal(Cell(0), b.Cells[ProMixSections.SourceOfPair(2)]);
        // The cue plays at the section's level, so its cell is at 0 dB.
        Assert.Equal(Cell(0), b.Cells[ProMixSections.SourceOfPair(plan.CuePair!.Value)]);
        // Section 0 keeps the mix's volume in its cells: its level byte is 98's.
        HardwareSectionContent a = Section(plan, 0);
        Assert.Null(a.Master);
        Assert.Equal(Cell(-3), a.Cells[ProMixSections.Xlr1]);

        // A fader move is one byte: the cells stay as they are.
        HardwareSectionContent lower = Section(HardwareMixingPlanner.Plan(Setup(monitor2Db: -30)), 18);
        Assert.Equal(new ProSectionMaster(120, false), lower.Master);
        Assert.Equal(b.Cells, lower.Cells);
        Assert.Equal(b.Members, lower.Members);
    }

    [Fact]
    public void AMutedMixKeepsItsCellsAndMembersBehindTheSectionsMuteBit()
    {
        HardwareSectionContent open = Section(HardwareMixingPlanner.Plan(Setup()), 18);
        HardwareSectionContent muted = Section(HardwareMixingPlanner.Plan(Setup(muted: true)), 18);
        Assert.Equal(new ProSectionMaster(0x18, true), muted.Master);
        Assert.Equal(open.Cells, muted.Cells);
        Assert.Equal(open.Members, muted.Members);
        Assert.NotEqual(0, muted.Sources);
    }

    [Theory]
    [InlineData(double.NegativeInfinity, 0xff, true)]
    [InlineData(-70, 0xff, true)]
    [InlineData(-63.75, 0xff, false)]
    [InlineData(-0.1, 0x00, false)]
    public void AFaderUnderTheLevelBytesFloorMutesTheSection(double db, byte level, bool muted)
        => Assert.Equal(new ProSectionMaster(level, muted), Section(HardwareMixingPlanner.Plan(Setup(monitor2Db: db)), 18).Master);

    [Fact]
    public void AFaderAboveZeroDecibelsGoesIntoTheCellsWhichHoldAPairAtZero()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup(monitor2Db: 3));
        HardwareSectionContent b = Section(plan, 18);
        Assert.Equal(ProSectionMaster.Unity, b.Master);
        Assert.Equal(Cell(0), b.Cells[ProMixSections.Xlr1]);
        Assert.Equal(Cell(-7), b.Cells[ProMixSections.SourceOfPair(1)]);
        Assert.Equal(Cell(0), b.Cells[ProMixSections.SourceOfPair(2)]);
        Assert.Contains(new HardwareMixingClamp("music", "monitor2", 3, 0), plan.Clamped);
    }

    [Fact]
    public void ASummedFeedKeepsEveryVolumeInItsCellsAndRunsNoMaximizer()
    {
        HardwareMixingSetup setup = Setup() with
        {
            Jacks = [("hp1", "monitor"), ("hp2", "monitor+monitor2")],
            Maximizers = new Dictionary<string, int> { ["monitor"] = 60, ["monitor2"] = 30 },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        HardwareSectionContent sum = Section(plan, 18);
        Assert.Equal(ProSectionMaster.Unity, sum.Master);
        Assert.Equal(0, sum.Maximizer);
        Assert.NotEqual(0, sum.Members & 1 << ProMixSections.MaximizerBypassBit);
        // Monitor A alone on Headphones 1 runs its own; Monitor B has none anywhere.
        HardwareSectionContent a = Section(plan, 0);
        Assert.Equal(60, a.Maximizer);
        Assert.Equal(0, a.Members & 1 << ProMixSections.MaximizerBypassBit);
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 0 }, HardwareMixingPlanner.RunningMaximizers(plan));
        Assert.Equal("The Wave XLR Pro builds Monitor B summed with another mix, so it runs no maximizer on it. Give a jack Monitor B alone to use one.",
            plan.Disabled["maximizer:monitor2"]);
        Assert.DoesNotContain("maximizer:monitor", plan.Disabled.Keys);
        // Mixes the computer builds have no entry: there is no maximizer to turn off.
        Assert.DoesNotContain("maximizer:stream", plan.Disabled.Keys);
    }

    [Fact]
    public void AMaximizerAtZeroIsBypassedAndOneThatIsOnClearsTheBypass()
    {
        HardwareMixingPlan off = HardwareMixingPlanner.Plan(Setup());
        Assert.All(off.Content, c =>
        {
            Assert.Equal(0, c.Maximizer);
            Assert.NotEqual(0, c.Members & 1 << ProMixSections.MaximizerBypassBit);
        });
        Assert.Empty(HardwareMixingPlanner.RunningMaximizers(off));

        HardwareMixingPlan on = HardwareMixingPlanner.Plan(Setup() with { Maximizers = new Dictionary<string, int> { ["monitor2"] = 100, ["stream"] = 50 } });
        HardwareSectionContent b = Section(on, 18);
        Assert.Equal(100, b.Maximizer);
        Assert.Equal(0, b.Members & 1 << ProMixSections.MaximizerBypassBit);
        Assert.Equal(new Dictionary<string, int> { ["monitor2"] = 18 }, HardwareMixingPlanner.RunningMaximizers(on));
        // Nothing runs while the plan does not.
        Assert.Empty(HardwareMixingPlanner.RunningMaximizers(HardwareMixingPlanner.Plan(Setup() with
        {
            Maximizers = new Dictionary<string, int> { ["monitor2"] = 100 }, Unit = Pro with { Connected = false },
        })));
    }

    [Fact]
    public void TheAuxMixHasItsVolumeInSectionThirtySixsLevel()
    {
        HardwareMixingSetup setup = Setup() with
        {
            AuxPort = true,
            SendsDb = new Dictionary<string, double>(Setup().SendsDb) { ["game|auxout"] = -6 },
            MixFadersDb = new Dictionary<string, double>(Setup().MixFadersDb) { ["auxout"] = -12 },
        };
        HardwareSectionContent aux = Section(HardwareMixingPlanner.Plan(setup), HardwareMixingPlanner.AuxSection);
        Assert.Equal(new ProSectionMaster(48, false), aux.Master);
        Assert.Equal(Cell(-6), aux.Cells[ProMixSections.SourceOfPair(1)]);
    }

    [Fact]
    public void TheModeHoldsTheLevelsOfSectionsEighteenToFiftyFourAndLeavingPutsThemAtZero()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup(muted: true));
        Assert.Equal(new Dictionary<int, ProSectionMaster>
        {
            [18] = new(0x18, true), [36] = ProSectionMaster.Unity, [54] = ProSectionMaster.Unity,
        }, MixerService.SectionMastersOf(plan));
        Assert.Equal(new Dictionary<int, ProSectionMaster>
        {
            [18] = ProSectionMaster.Unity, [36] = ProSectionMaster.Unity, [54] = ProSectionMaster.Unity,
        }, MixerService.SectionMastersOf(null));
    }

    // --- the mixer, its command and its settings ---

    [Fact]
    public void TheMixerKeepsAnAmountPerMixSavesItAndRefusesWhatIsNoMix()
    {
        using var mixer = new Mixer();
        Assert.All(mixer.Snapshot().Mixes, m => Assert.Equal(0, m.Maximizer));
        mixer.SetMixMaximizer("monitor", 40);
        mixer.SetMixMaximizer("stream", 100);
        Assert.Equal(40, mixer.MixMaximizer("monitor"));
        Assert.Equal(40, mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Maximizer);
        Assert.Throws<ArgumentException>(() => mixer.SetMixMaximizer("nope", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetMixMaximizer("monitor", 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.SetMixMaximizer("monitor", -1));
        mixer.SetMixMaximizer("stream", 0);
        MixerSettings saved = mixer.ExportSettings();
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 40 }, saved.MixMaximizer);
        string json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"mixMaximizer\":{\"monitor\":40}", json);

        using var other = new Mixer();
        other.ApplySettings(saved with { MixMaximizer = new Dictionary<string, int> { ["monitor"] = 40, ["gone"] = 20 } });
        Assert.Equal(40, other.MixMaximizer("monitor"));
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 40 }, other.ExportSettings().MixMaximizer);
    }

    [Fact]
    public void ASavedAmountOutOfRangeIsDroppedAndNamed()
    {
        MixerSettings settings = new MixerSettings() with
        {
            MixMaximizer = new Dictionary<string, int> { ["monitor"] = 50, ["stream"] = 0, ["chat"] = 101 },
        };
        MixerSettings kept = SavedMixerValidation.Sanitize(settings, out IReadOnlyList<string> dropped);
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 50 }, kept.MixMaximizer);
        Assert.Contains("mixMaximizer.stream: 0 is not 1 to 100", dropped);
        Assert.Contains("mixMaximizer.chat: 101 is not 1 to 100", dropped);
        Assert.Empty(SavedMixerValidation.Sanitize(new MixerSettings() with { MixMaximizer = null! }, out _).MixMaximizer);
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "game";
        public bool HasMix(string id) => id is "monitor" or "stream";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => false;
        public bool IsMicrophoneChannel(string id) => id is "xlr1";
        public int OverrideCount => 0;
    }

    [Theory]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":40}""", null)]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"stream","value":0}""", null)]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":100}""", null)]
    [InlineData("""{"cmd":"setMixMaximizer","value":40}""", "setMixMaximizer: need 'mix'")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"nope","value":40}""", "setMixMaximizer: 'nope' is not a mix")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":101}""", "setMixMaximizer: value must be a whole number from 0 to 100")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":-1}""", "setMixMaximizer: value must be a whole number from 0 to 100")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":12.5}""", "setMixMaximizer: value must be a whole number from 0 to 100")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor","value":true}""", "setMixMaximizer: value must be a whole number from 0 to 100")]
    [InlineData("""{"cmd":"setMixMaximizer","mix":"monitor"}""", "setMixMaximizer: value must be a whole number from 0 to 100")]
    public void TheCommandIsChecked(string json, string? error)
        => Assert.Equal(error, CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, false));

    // --- the meter ---

    [Fact]
    public void ARunningMaximizerReportsItsReductionUnderItsMixWhileTheUnitBuildsIt()
    {
        var port = new MetersPort();
        using var mixer = new Mixer();
        mixer.SetDrivenUnit(Unit);
        mixer.SetDeviceDuckingPort(port);
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup() with { Maximizers = new Dictionary<string, int> { ["monitor2"] = 80 } });
        mixer.NoteHardwarePlan(plan);
        mixer.BeginHardwareMixing(new HardwareEntry());
        typeof(Mixer).GetField("_hardwareGraphEntered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);

        // No reading yet: no key.
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("max:", StringComparison.Ordinal));
        byte[] block = ProMetersTests.Idle();
        block[2] = 0x25;   // section 18's maximizer, 9.25 dB
        block[0] = 0x10;   // section 0's, which runs nothing here
        port.Reading = ProMeters.Decode(block);
        IReadOnlyDictionary<string, double[]> frame = mixer.ReadMeters();
        Assert.Equal([9.25], frame["max:monitor2"]);
        Assert.DoesNotContain("max:monitor", frame.Keys);

        // Leaving: no key.
        mixer.LeaveHardwareMixing();
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("max:", StringComparison.Ordinal));

        // With no reading the keys stay out.
        var meters = new Dictionary<string, double[]>();
        UnitMixMeters.ApplyMaximizers(meters, new Dictionary<string, int> { ["monitor2"] = 18 }, null);
        Assert.Empty(meters);
    }

    /// <summary>A device side whose meters the test sets.</summary>
    private sealed class MetersPort : IDeviceDuckingPort
    {
        public ProMeterReading? Reading;

        public void Post(string unit, ProDuckingTarget target, string owner) { }

        public DeviceDuckingStatus Status(string unit) => DeviceDuckingStatus.None;

        public ProMeterReading? Meters(string unit) => Reading;
    }
}
