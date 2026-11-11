using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Hardware mixing's on-demand pairs: a unit input software processes plays
/// back to the unit on a return pair of its own, and soundboard auditions on
/// the cue pair, from the pairs no channel takes, in that order. A claim that
/// finds no pair falls back with a note: the input raw, Mic setup or
/// auditions refused. Pure: nothing touches a unit or a graph.
/// </summary>
public sealed class HardwareMixingClaimsTests
{
    private static readonly MixerConfig Layout = MixerConfig.Default();

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    private static readonly Dictionary<string, double> Heard = new()
    {
        ["xlr1|monitor"] = 0, ["xlr1|auxout"] = -6, ["xlr2|monitor"] = -3, ["game|monitor"] = -10,
    };

    /// <summary>The default layout on a ready Pro, Headphones 1 on Monitor A, the USB Aux port on.</summary>
    private static HardwareMixingSetup Setup(params ChannelDefinition[] extra) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = [.. Layout.Channels, .. extra],
        Mixes = Layout.Mixes,
        LevelsDb = Heard,
        MixVolumesDb = new Dictionary<string, double> { ["monitor"] = -4, ["monitor2"] = -8, ["auxout"] = 0 },
        Jacks = [("hp1", "monitor")],
        AuxPort = true,
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
    };

    /// <summary>The desk's layout: the default channels and a Brio capture input, which takes pair 7.</summary>
    private static HardwareMixingSetup Desk() => Setup(new ChannelDefinition("brio", "Brio") { CaptureSource = "alsa_input.brio" });

    private static HardwareSectionContent Section(HardwareMixingPlan plan, int section) => plan.Content.Single(c => c.Section == section);

    private static bool Member(HardwareMixingPlan plan, int section, int source) => (Section(plan, section).Members & 1 << source) != 0;

    [Fact]
    public void WithNothingProcessedTheCueTakesTheFirstFreePair()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup());
        Assert.Empty(plan.ReturnPairs);
        Assert.Equal(7, plan.CuePair);
        Assert.DoesNotContain("audition", plan.Disabled.Keys);
        Assert.DoesNotContain(plan.Disabled.Keys, k => k.StartsWith("soundCheck", StringComparison.Ordinal));
        // The cue is in the jack's section at Monitor A's volume, never in the Aux mix's.
        int cue = ProMixSections.SourceOfPair(7);
        Assert.True(Member(plan, 0, cue));
        Assert.Equal(ProMixSections.CellOf(-4), Section(plan, 0).Cells[cue]);
        Assert.False(Member(plan, HardwareMixingPlanner.AuxSection, cue));
        Assert.Equal(ProMixSections.CellOff, Section(plan, HardwareMixingPlanner.AuxSection).Cells[cue]);
        // XLR 1 is the unit's own input, raw.
        Assert.True(Member(plan, 0, ProMixSections.Xlr1));
    }

    [Fact]
    public void AProcessedMicrophonePlaysFromItsReturnPairAndNeverRawBesideIt()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup() with { Processed = new HashSet<string> { "xlr1" } });
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, plan.ReturnPairs);
        Assert.Equal(5, plan.CuePair);
        Assert.DoesNotContain(7, plan.Pairs.Values);
        int back = ProMixSections.SourceOfPair(7);
        foreach (int section in new[] { 0, HardwareMixingPlanner.AuxSection })
        {
            // One write holds both: the raw bit clear and the return pair in.
            Assert.False(Member(plan, section, ProMixSections.Xlr1));
            Assert.Equal(ProMixSections.CellOff, Section(plan, section).Cells[ProMixSections.Xlr1]);
            Assert.True(Member(plan, section, back));
        }
        // At XLR 1's own sends: 0 dB on Monitor A, -6 dB on the Aux mix.
        Assert.Equal(ProMixSections.CellOf(0), Section(plan, 0).Cells[back]);
        Assert.Equal(ProMixSections.CellOf(-6), Section(plan, HardwareMixingPlanner.AuxSection).Cells[back]);
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
    }

    [Fact]
    public void ProcessedInputsClaimBeforeTheCueInSourceOrder()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup() with { Processed = new HashSet<string> { "xlr2", "xlr1" } });
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7, ["xlr2"] = 5 }, plan.ReturnPairs);
        Assert.Null(plan.CuePair);
        Assert.Equal("Auditions need a free USB pair on the Wave XLR Pro, and every pair is in use.", plan.Disabled["audition"]);
        // Both microphones already play back on pairs, so Mic setup is not refused.
        Assert.DoesNotContain(plan.Disabled.Keys, k => k.StartsWith("soundCheck", StringComparison.Ordinal));
    }

    /// <summary>The desk's channels on the pairs they kept, Brio on 7: a processed XLR 1 takes pair 5, the one left.</summary>
    [Fact]
    public void OnTheDesksLayoutXlr1WithNoiseRemovalTakesPairFiveAndAuditionsAreRefused()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Desk() with { Processed = new HashSet<string> { "xlr1" }, SavedPairs = DeskPairs });
        Assert.Equal(new Dictionary<string, int>
        {
            ["system"] = 0, ["game"] = 1, ["music"] = 2, ["browser"] = 3, ["voicechat"] = 4, ["sfx"] = 6, ["brio"] = 7,
        }, plan.Pairs);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 5 }, plan.ReturnPairs);
        Assert.Null(plan.CuePair);
        Assert.Contains("audition", plan.Disabled.Keys);
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
        // XLR 1 runs Mic setup on its return pair; XLR 2 has none left.
        Assert.DoesNotContain("soundCheck:xlr1", plan.Disabled.Keys);
        Assert.Contains("XLR 2", plan.Disabled["soundCheck:xlr2"]);
        Assert.DoesNotContain("soundCheck", plan.Disabled.Keys);
        Assert.True(Member(plan, 0, ProMixSections.SourceOfPair(5)));
        Assert.False(Member(plan, 0, ProMixSections.Xlr1));
    }

    [Fact]
    public void OnTheDesksLayoutWithNothingProcessedTheCueTakesPairFive()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Desk());
        Assert.Empty(plan.ReturnPairs);
        Assert.Equal(5, plan.CuePair);
        Assert.DoesNotContain("audition", plan.Disabled.Keys);
        Assert.DoesNotContain(plan.Disabled.Keys, k => k.StartsWith("soundCheck", StringComparison.Ordinal));
    }

    /// <summary>The pairs the desk's channels kept while the mode ran.</summary>
    private static readonly Dictionary<string, int> DeskPairs = new(StringComparer.Ordinal)
    {
        ["system"] = 0, ["game"] = 1, ["music"] = 2, ["browser"] = 3, ["voicechat"] = 4, ["sfx"] = 6, ["brio"] = 7,
    };

    /// <summary>
    /// The desk on 2026-11-11: every channel on the pair it kept, XLR 1's
    /// processed voice back on pair 5 for the Aux mix in section 36, and a
    /// capture input added for a second interface. The new channel finds no
    /// pair: it stays out of the unit's mixes with a note, and XLR 1 keeps
    /// its return pair, so the USB Aux port goes on hearing it processed.
    /// </summary>
    [Fact]
    public void ANewChannelNeverTakesAProcessedInputsReturnPair()
    {
        HardwareMixingSetup desk = Desk() with { Processed = new HashSet<string> { "xlr1" }, SavedPairs = DeskPairs };
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 5 }, HardwareMixingPlanner.Plan(desk).ReturnPairs);
        var added = new ChannelDefinition("desk-test-mk2", "Desk test MK2") { CaptureSource = "alsa_input.mk2", CaptureMonoChannel = 0 };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(desk with
        {
            Channels = [.. desk.Channels, added],
            LevelsDb = new Dictionary<string, double>(Heard) { ["desk-test-mk2|auxout"] = 0 },
        });
        Assert.True(plan.Active);
        Assert.Equal(DeskPairs, plan.Pairs);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 5 }, plan.ReturnPairs);
        Assert.Equal(["desk-test-mk2"], plan.Unpaired);
        Assert.Equal("The mixes the Wave XLR Pro builds leave Desk test MK2 out: every USB pair is in use.", plan.Disabled["pair:desk-test-mk2"]);
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
        // Pair 5 in section 36 is XLR 1 at its Aux send, processed; the raw input stays out.
        int back = ProMixSections.SourceOfPair(5);
        Assert.True(Member(plan, HardwareMixingPlanner.AuxSection, back));
        Assert.Equal(ProMixSections.CellOf(-6), Section(plan, HardwareMixingPlanner.AuxSection).Cells[back]);
        Assert.False(Member(plan, HardwareMixingPlanner.AuxSection, ProMixSections.Xlr1));
        // The state gives the new channel no pair.
        Assert.DoesNotContain("desk-test-mk2", HardwareMixingState.Of(plan).Pairs.Keys);
    }

    /// <summary>
    /// A built-in channel made again after a delete would take its own pair
    /// back. While the mode runs and a processed input holds that pair as its
    /// return, the input keeps it and the channel waits for a free one.
    /// </summary>
    [Fact]
    public void ABuiltInChannelMadeAgainLeavesTheReturnPairItsOwnPairBecame()
    {
        HardwareMixingSetup setup = Desk() with
        {
            Channels = [.. Desk().Channels.Where(c => c.Id != "music"), new ChannelDefinition("discord", "Discord")],
            Processed = new HashSet<string> { "xlr1" },
            // Deleting Music dropped its kept pair.
            SavedPairs = new Dictionary<string, int>(DeskPairs.Where(p => p.Key != "music")) { ["discord"] = 5 },
        };
        HardwareMixingPlan running = HardwareMixingPlanner.Plan(setup);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 2 }, running.ReturnPairs);
        HardwareMixingSetup again = setup with { Channels = [.. setup.Channels, new ChannelDefinition("music", "Music")], HeldReturns = running.ReturnPairs };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(again);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 2 }, plan.ReturnPairs);
        Assert.Equal(["music"], plan.Unpaired);
        // Outside the mode nothing is held, and Music takes its own pair.
        Assert.Equal(2, HardwareMixingPlanner.Plan(again with { HeldReturns = new Dictionary<string, int>() }).Pairs["music"]);
    }

    /// <summary>
    /// The same layout entered for the first time, nothing kept: the built-in
    /// channels take their own pairs, the processed input the first free
    /// one, and the added channels what is left in layout order, so the
    /// channel past that is the one left out, never the input.
    /// </summary>
    [Fact]
    public void WithNothingKeptTheLastChannelGivesWayToTheReturn()
    {
        HardwareMixingSetup full = Desk() with { Processed = new HashSet<string> { "xlr1" } };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(full with { Channels = [.. full.Channels, new ChannelDefinition("discord", "Discord")] });
        Assert.True(plan.Active);
        Assert.Equal(5, plan.Pairs["brio"]);
        Assert.DoesNotContain("discord", plan.Pairs.Keys);
        Assert.Equal(["discord"], plan.Unpaired);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, plan.ReturnPairs);
        Assert.Contains("pair:discord", plan.Disabled.Keys);
        Assert.All(plan.Disabled.Values, sentence => Assert.DoesNotContain("\u2014", sentence));
    }

    /// <summary>
    /// A channel that already holds a pair keeps it when an input's
    /// processing starts later: the input finds no pair and falls back to
    /// the raw input with its note.
    /// </summary>
    [Fact]
    public void AChannelHoldingAPairKeepsItAndTheClaimFallsBackWithANote()
    {
        HardwareMixingSetup full = Desk() with
        {
            Processed = new HashSet<string> { "xlr1" },
            SavedPairs = new Dictionary<string, int>(DeskPairs) { ["discord"] = 5 },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(full with { Channels = [.. full.Channels, new ChannelDefinition("discord", "Discord")] });
        Assert.True(plan.Active);
        Assert.Empty(plan.Unpaired);
        Assert.Equal(5, plan.Pairs["discord"]);
        Assert.Empty(plan.ReturnPairs);
        Assert.Null(plan.CuePair);
        Assert.Equal("The Wave XLR Pro's mixes take XLR 1 as it comes in, without the processing on this computer: no USB pair is free to play it back.",
            plan.Disabled["processedMic:xlr1"]);
        Assert.Contains("audition", plan.Disabled.Keys);
        Assert.Equal("Mic setup needs a free USB pair on the Wave XLR Pro to play a take back, and every pair is in use.", plan.Disabled["soundCheck"]);
        Assert.Contains("soundCheck:xlr1", plan.Disabled.Keys);
        Assert.Contains("soundCheck:xlr2", plan.Disabled.Keys);
        // The fallback is the raw input: its bit in, the channel on pair 5.
        Assert.True(Member(plan, 0, ProMixSections.Xlr1));
        Assert.All(plan.Disabled.Values, sentence => Assert.DoesNotContain("—", sentence));
    }

    [Fact]
    public void ANewChannelTakesTheCuesPairAndLeavesTheReturnPairAlone()
    {
        // Pair 7 free and 5 free: XLR 1 on 7, the cue on 5. A new channel
        // takes 5, the pair left after the return, and the cue goes; XLR 1
        // stays on 7, so its voice never moves.
        HardwareMixingSetup setup = Setup() with { Processed = new HashSet<string> { "xlr1" } };
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, HardwareMixingPlanner.Plan(setup).ReturnPairs);
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup with { Channels = [.. setup.Channels, new ChannelDefinition("discord", "Discord")] });
        Assert.Equal(5, plan.Pairs["discord"]);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, plan.ReturnPairs);
        Assert.Empty(plan.Unpaired);
        Assert.Null(plan.CuePair);
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
    }

    [Fact]
    public void TheMonitoringPauseTakesTheCueAndTheReturnPairOutOfTheJacksOnly()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(Setup() with { Processed = new HashSet<string> { "xlr1" }, MonitorMuted = true });
        Assert.Equal(0, Section(plan, 0).Sources);
        Assert.True(Member(plan, HardwareMixingPlanner.AuxSection, ProMixSections.SourceOfPair(7)));
    }

    [Fact]
    public void AProcessedInputOnAReturnPairGainsNothingOnEntering()
    {
        // Through a pair before, through a pair in the mode: only byte 98's 21 dB.
        HardwareMixingSetup setup = Setup() with { Processed = new HashSet<string> { "xlr1" }, LevelsDb = new Dictionary<string, double> { ["xlr1|monitor"] = 0 } };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.Equal(21, plan.EntryOffsetsDb["hp1"], 3);
        // Raw for want of a pair, it would gain the pair's 7.94 dB.
        Assert.Equal(28.94, HardwareMixingPlanner.EntryOffsetDb(setup, "monitor"), 3);
    }

    [Fact]
    public void TheStateCarriesTheClaimsWhileTheModeRuns()
    {
        HardwareMixingState state = HardwareMixingState.Of(HardwareMixingPlanner.Plan(Setup() with { Processed = new HashSet<string> { "xlr1" } }));
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, state.ReturnPairs);
        Assert.Equal(5, state.CuePair);
        HardwareMixingState waiting = HardwareMixingState.Of(HardwareMixingPlanner.Plan(Setup() with { Unit = Pro with { Connected = false } }));
        Assert.Empty(waiting.ReturnPairs);
        Assert.Null(waiting.CuePair);
    }
}
