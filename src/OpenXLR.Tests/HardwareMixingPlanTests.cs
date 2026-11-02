using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Hardware mixing's plan from the mixer's model: which channels stream on
/// which USB pair and keep it, the budget sentence, every reason the mode
/// cannot run, the cells and memberships of each section a jack plays, the
/// levels held at the ceiling, what the mode turns off, and how far each
/// jack's level drops on entering. Pure: nothing touches a unit or a graph.
/// </summary>
public sealed class HardwareMixingPlanTests
{
    private static readonly MixerConfig Layout = MixerConfig.Default();

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    /// <summary>The default layout on a ready Pro, XLR 1 and Line In carried raw, Headphones 1 on Monitor A.</summary>
    private static HardwareMixingSetup Setup(Dictionary<string, double>? levels = null) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = Layout.Channels,
        Mixes = Layout.Mixes,
        LevelsDb = levels ?? new Dictionary<string, double>(),
        Jacks = [("hp1", "monitor")],
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
        DirectPath = new HashSet<string> { "xlr1" },
    };

    private static byte CellAt(HardwareMixingPlan plan, int section, int source)
        => plan.Content.Single(c => c.Section == section).Cells[source];

    private static bool MemberAt(HardwareMixingPlan plan, int section, int source)
        => (plan.Content.Single(c => c.Section == section).Members & 1 << source) != 0;

    [Fact]
    public void TheDefaultLayoutTakesTheDefaultPairsAndLeavesPairFiveToTheAuxMix()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup());
        Assert.True(plan.Active);
        Assert.Null(plan.Reason);
        Assert.Equal(new Dictionary<string, int>
        {
            ["game"] = 1, ["music"] = 2, ["browser"] = 3, ["system"] = 0, ["voicechat"] = 4, ["sfx"] = 6,
        }, plan.Pairs);
        Assert.DoesNotContain(HardwareMixingPlanner.AuxPair, plan.Pairs.Values);
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 0 }, plan.Sections);
        Assert.Equal(new Dictionary<string, int> { ["hp1"] = 0 }, plan.Jacks);
    }

    [Fact]
    public void AnAddedChannelTakesPairSevenThenTheSavedPairsStay()
    {
        HardwareMixingSetup setup = Setup() with
        {
            Channels = [.. Layout.Channels, new ChannelDefinition("discord", "Discord")],
            SavedPairs = new Dictionary<string, int> { ["music"] = 7, ["game"] = 5, ["browser"] = 9 },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        // Music keeps its saved pair; pair 5 and pair 9 are not channel
        // pairs, so Game and Browser take their defaults; Discord, new, takes
        // the first pair left in the order 7, 0, 1, 2, 3, 4, 6, which is 2.
        Assert.Equal(7, plan.Pairs["music"]);
        Assert.Equal(1, plan.Pairs["game"]);
        Assert.Equal(3, plan.Pairs["browser"]);
        Assert.Equal(2, plan.Pairs["discord"]);
        Assert.Equal(plan.Pairs.Count, plan.Pairs.Values.Distinct().Count());
    }

    [Fact]
    public void ABoundStripNeedsAPairAndTheUnitsMicrophoneDoesNot()
    {
        // XLR 1 bound to a USB microphone: the strip streams on a pair, and
        // the capture channel kept for the Pro's own microphone is source 0.
        HardwareMixingSetup setup = Setup() with
        {
            Channels = [.. Layout.Channels, new ChannelDefinition("promic", "Pro mic") { CaptureSource = "alsa_input.pro", CaptureMonoChannel = 0 }],
            UnitSources = new Dictionary<int, string> { [0] = "promic", [1] = "xlr2", [2] = "aux", [3] = "line" },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.Equal(7, plan.Pairs["xlr1"]);
        Assert.False(plan.Pairs.ContainsKey("promic"));
    }

    [Fact]
    public void ALayoutPastThePairsIsRefusedWithTheBudgetSentence()
    {
        HardwareMixingSetup setup = Setup() with
        {
            Channels = [.. Layout.Channels, new ChannelDefinition("u1", "One"), new ChannelDefinition("u2", "Two")],
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.False(plan.Active);
        Assert.Equal("budget", plan.Reason);
        Assert.Equal("Hardware mixing on the Wave XLR Pro streams at most 7 channels; this layout has 8 that need a USB pair.", plan.Note);
        Assert.Empty(plan.Pairs);
        Assert.Empty(plan.Content);
    }

    public static TheoryData<string, HardwareMixingSetup> Reasons => new()
    {
        { "mixerOff", Setup() with { MixerRunning = false } },
        { "notSupported", Setup() with { Unit = null } },
        { "notSupported", Setup() with { Unit = Pro with { Pro = false } } },
        { "unitAway", Setup() with { Unit = Pro with { Connected = false } } },
        { "firmware", Setup() with { Unit = Pro with { Firmware = "1.0.13" } } },
        { "firmware", Setup() with { Unit = Pro with { Firmware = null } } },
        { "unsupportedBlock", Setup() with { Unit = Pro with { SectionsDecode = false } } },
        { "captureLayout", Setup() with { SinkPorts = 2 } },
        { "captureLayout", Setup() with { SourcePorts = null } },
        { "failed", Setup() with { Failed = true } },
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void EachThingInTheWayIsNamedWithASentence(string reason, HardwareMixingSetup setup)
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.False(plan.Active);
        Assert.Equal(reason, plan.Reason);
        Assert.False(string.IsNullOrWhiteSpace(plan.Note));
        Assert.DoesNotContain("\u2014", plan.Note);
        // Not wanted, the reason still says whether entering would work.
        Assert.Equal(reason, HardwareMixingPlanner.Plan(setup with { Wanted = false }).Reason);
    }

    [Fact]
    public void NewerFirmwareIsTaken()
    {
        Assert.True(HardwareMixingPlanner.Plan(Setup() with { Unit = Pro with { Firmware = "1.1.0" } }).Active);
        Assert.False(HardwareMixingPlanner.Plan(Setup() with { Wanted = false }).Active);
    }

    [Fact]
    public void CellsComeFromTheFadersAndAPairStopsAtZeroDecibels()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup(new Dictionary<string, double>
        {
            ["xlr1|monitor"] = 0,
            ["line|monitor"] = 6,                                // a unit input may go above 0 dB
            ["game|monitor"] = 60 * Math.Log10(0.5),             // -18.06 dB
            ["music|monitor"] = 60 * Math.Log10(1.5),            // +10.57 dB: Monitor A at 150%
            ["browser|monitor"] = double.NegativeInfinity,       // muted
            ["system|monitor2"] = 0,                             // not in this feed
        }));
        Assert.Equal(0x00, CellAt(plan, 0, ProMixSections.Xlr1));
        Assert.True(MemberAt(plan, 0, ProMixSections.Xlr1));
        Assert.Equal(0x0C, CellAt(plan, 0, ProMixSections.LineIn));
        Assert.Equal(unchecked((byte)(sbyte)-36), CellAt(plan, 0, ProMixSections.SourceOfPair(1)));
        Assert.Equal(0x00, CellAt(plan, 0, ProMixSections.SourceOfPair(2)));
        Assert.True(MemberAt(plan, 0, ProMixSections.SourceOfPair(2)));
        Assert.Equal(ProMixSections.CellOff, CellAt(plan, 0, ProMixSections.SourceOfPair(3)));
        Assert.False(MemberAt(plan, 0, ProMixSections.SourceOfPair(3)));
        Assert.False(MemberAt(plan, 0, ProMixSections.SourceOfPair(0)));
        // XLR 2 and USB Aux have no level in Monitor A: out.
        Assert.False(MemberAt(plan, 0, ProMixSections.Xlr2));
        Assert.False(MemberAt(plan, 0, ProMixSections.UsbAux));
        // Pair 5 is never a member of a jack's section.
        Assert.False(MemberAt(plan, 0, ProMixSections.SourceOfPair(HardwareMixingPlanner.AuxPair)));
        HardwareMixingClamp clamp = Assert.Single(plan.Clamped);
        Assert.Equal(new HardwareMixingClamp("music", "monitor", 10.57, 0), clamp);
    }

    [Fact]
    public void ASummedFeedAddsTheMixesAmplitudesInASectionOfItsOwn()
    {
        HardwareMixingSetup setup = Setup(new Dictionary<string, double>
        {
            ["xlr1|monitor"] = 0, ["xlr1|monitor2"] = 0,       // two unity parts: +6.02 dB
            ["game|monitor"] = -12, ["game|monitor2"] = double.NegativeInfinity,
        }) with { Jacks = [("hp1", "monitor"), ("hp2", "monitor+monitor2")] };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.Equal(new Dictionary<string, int> { ["monitor"] = 0, ["monitor+monitor2"] = 18 }, plan.Sections);
        Assert.Equal(new Dictionary<string, int> { ["hp1"] = 0, ["hp2"] = 18 }, plan.Jacks);
        Assert.Equal(0x0C, CellAt(plan, 18, ProMixSections.Xlr1));
        Assert.Equal(0x00, CellAt(plan, 0, ProMixSections.Xlr1));
        Assert.Equal(unchecked((byte)(sbyte)-24), CellAt(plan, 18, ProMixSections.SourceOfPair(1)));
    }

    [Fact]
    public void TheMonitoringPauseClearsEveryMembershipAndKeepsTheCells()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup(new Dictionary<string, double> { ["xlr1|monitor"] = 0, ["game|monitor"] = -6 })
            with { MonitorMuted = true });
        HardwareSectionContent section = Assert.Single(plan.Content);
        Assert.Equal(0, section.Members);
        Assert.Equal(0x00, section.Cells[ProMixSections.Xlr1]);
        Assert.Equal(unchecked((byte)(sbyte)-12), section.Cells[ProMixSections.SourceOfPair(1)]);
    }

    [Fact]
    public void AFeedKeepsItsSavedSectionAndANewOneTakesTheFirstFree()
    {
        HardwareMixingSetup setup = Setup() with
        {
            Jacks = [("hp1", "monitor"), ("hp2", "monitor2"), ("lineout", "stream")],
            SavedSections = new Dictionary<string, int> { ["monitor2"] = 0, ["monitor"] = 36 },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        // Section 36 is the Aux mix's, so Monitor A takes the first free.
        Assert.Equal(new Dictionary<string, int> { ["monitor2"] = 0, ["monitor"] = 18, ["stream"] = 54 }, plan.Sections);
        Assert.Equal(3, plan.Content.Count);
    }

    [Fact]
    public void WhatTheModeTurnsOffIsNamedForTheMixesItBuilds()
    {
        HardwareMixingSetup setup = Setup() with
        {
            Jacks = [("hp1", "monitor+monitor2")],
            Processed = new HashSet<string> { "xlr1" },
        };
        IReadOnlyDictionary<string, string> disabled = HardwareMixingPlanner.Plan(setup).Disabled;
        Assert.Equal(
        [
            "audition", "ducking:mix:monitor", "ducking:mix:monitor2", "inserts:mix:monitor", "inserts:mix:monitor2",
            "latencyCompensation", "processedMic:xlr1", "soundCheck",
        ], disabled.Keys.Order(StringComparer.Ordinal));
        Assert.All(disabled.Values, sentence => Assert.DoesNotContain("\u2014", sentence));
        Assert.Contains("Monitor A", disabled["inserts:mix:monitor"]);
        Assert.Contains("XLR 1", disabled["processedMic:xlr1"]);
    }

    /// <summary>
    /// The ear-safety table: on entering, a jack's level drops by byte 98's
    /// 21 dB, and by the pair's 7.94 dB more while a unit input it heard
    /// through the Monitor mix is in its feed. In the mode the output volume
    /// only lowers a jack; leaving gives back the 21 dB less what the Pro
    /// sink above 100% will add to the pairs again.
    /// </summary>
    [Theory]
    [InlineData(false, -21, 21.0)]
    [InlineData(true, -21, 28.94)]
    [InlineData(false, -24, 24.0)]
    [InlineData(false, -18, 21.0)]
    public void EnteringLowersEachJackSoNothingGetsLouder(bool lineInHeard, double section0Db, double offset)
    {
        var levels = new Dictionary<string, double> { ["xlr1|monitor"] = 0, ["game|monitor"] = -6 };
        if (lineInHeard) levels["line|monitor"] = -3;
        HardwareMixingSetup setup = Setup(levels) with { Section0LevelDb = section0Db };
        Assert.Equal(offset, HardwareMixingPlanner.Plan(setup).EntryOffsetsDb["hp1"], 3);
    }

    [Fact]
    public void AMicrophoneTheDirectPathDidNotCarryCountsAsHeardThroughAPair()
    {
        var levels = new Dictionary<string, double> { ["xlr1|monitor"] = 0 };
        Assert.Equal(21, HardwareMixingPlanner.EntryOffsetDb(Setup(levels), "monitor"), 3);
        Assert.Equal(28.94, HardwareMixingPlanner.EntryOffsetDb(Setup(levels) with { DirectPath = new HashSet<string>() }, "monitor"), 3);
    }

    [Theory]
    [InlineData(-30, null, -30, -9)]
    [InlineData(-30, 1.0, -30, -9)]
    [InlineData(-30, 0.5, -48.062, -9)]
    [InlineData(-30, 1.5, -30, -19.565)]
    [InlineData(-30, 0.0, double.NegativeInfinity, -9)]
    public void InTheModeTheOutputVolumeOnlyLowersAJack(double baseDb, double? volume, double inMode, double afterLeaving)
    {
        Assert.Equal(inMode, HardwareMixingPlanner.JackLevelDb(baseDb, volume), 3);
        Assert.Equal(afterLeaving, HardwareMixingPlanner.LeaveLevelDb(baseDb, volume), 3);
    }

    [Fact]
    public void TheModeIsSavedAndCheckedAndNeverInAProfile()
    {
        using var mixer = new Mixer();
        Assert.Equal(MixingMode.Software, mixer.ExportSettings().Mixing);
        MixerSettings saved = mixer.ExportSettings() with
        {
            Mixing = MixingMode.Hardware,
            HardwarePairs = new Dictionary<string, int> { ["music"] = 7 },
            HardwareSections = new Dictionary<string, int> { ["monitor"] = 18 },
        };
        using (var other = new Mixer())
        {
            other.ApplySettings(saved);
            Assert.Equal(MixingMode.Hardware, other.Mixing);
            Assert.Equal(7, other.ExportSettings().HardwarePairs["music"]);
            Assert.Equal(18, other.ExportSettings().HardwareSections["monitor"]);
            // Not built: the plan says the mixer is off.
            Assert.Equal("mixerOff", other.PlanHardwareMixing(new HardwareMixingUnit { Pro = true, Connected = true }).Reason);
        }
        string scene = JsonSerializer.Serialize(mixer.ExportScene(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("hardwarePairs", scene);
        Assert.DoesNotContain("\"mixing\"", scene);
    }

    [Fact]
    public void ASavedFileWithBadModeEntriesKeepsTheRest()
    {
        MixerSettings settings = new MixerSettings() with
        {
            Mixing = "unit",
            HardwarePairs = new Dictionary<string, int> { ["music"] = 2, ["game"] = 5, ["browser"] = 2, ["system"] = 0 },
            HardwareSections = new Dictionary<string, int> { ["monitor"] = 36, ["monitor2"] = 54 },
        };
        MixerSettings kept = SavedMixerValidation.Sanitize(settings, out IReadOnlyList<string> dropped);
        Assert.Equal(MixingMode.Software, kept.Mixing);
        Assert.Equal(new Dictionary<string, int> { ["music"] = 2, ["system"] = 0 }, kept.HardwarePairs);
        Assert.Equal(new Dictionary<string, int> { ["monitor2"] = 54 }, kept.HardwareSections);
        Assert.Contains("mixing: must be 'software' or 'hardware'", dropped);
        Assert.Contains(dropped, d => d.StartsWith("hardwarePairs.game", StringComparison.Ordinal));
        Assert.Contains(dropped, d => d.StartsWith("hardwarePairs.browser", StringComparison.Ordinal));
        Assert.Contains(dropped, d => d.StartsWith("hardwareSections.monitor:", StringComparison.Ordinal));
    }

    private static Command Mixing(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return JsonSerializer.Deserialize<Command>(doc.RootElement.GetRawText())!;
    }

    [Theory]
    [InlineData("""{"cmd":"setMixing","value":"hardware"}""", null)]
    [InlineData("""{"cmd":"setMixing","value":"software","check":true}""", null)]
    [InlineData("""{"cmd":"setMixing","value":"hardware","check":false}""", null)]
    [InlineData("""{"cmd":"setMixing","value":"unit"}""", "setMixing: value must be 'software' or 'hardware'")]
    [InlineData("""{"cmd":"setMixing","value":true}""", "setMixing: value must be 'software' or 'hardware'")]
    [InlineData("""{"cmd":"setMixing","value":"hardware","check":"yes"}""", "setMixing: check must be a boolean")]
    public void SetMixingTakesAModeAndABooleanCheck(string json, string? error)
        => Assert.Equal(error, CommandValidation.CheckMixing(Mixing(json)));

    [Fact]
    public void TheStateShowsTheMapsOnlyWhileTheModeRuns()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup());
        HardwareMixingState running = HardwareMixingState.Of(plan);
        Assert.True(running.Active);
        Assert.Equal(plan.Pairs, running.Pairs);
        Assert.NotEmpty(running.Disabled);
        HardwareMixingState waiting = HardwareMixingState.Of(HardwareMixingPlanner.Plan(Setup() with { Unit = Pro with { Connected = false } }));
        Assert.True(waiting.Wanted);
        Assert.False(waiting.Active);
        Assert.Equal("unitAway", waiting.Reason);
        Assert.Empty(waiting.Pairs);
        Assert.Empty(waiting.Disabled);
    }
}
