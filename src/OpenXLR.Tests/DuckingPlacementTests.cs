using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class DuckingPlacementTests
{
    internal static readonly UnitDuckingSupport Pro = new("Wave XLR Pro", "1.0.14", true);
    private static DuckingSetup Setup => DuckingSetup.Default(MixerConfig.Default()) with { Enabled = true };

    /// <summary>The Pro's four inputs on their default strips.</summary>
    internal static IReadOnlyDictionary<string, int> ProInputs { get; } = new Dictionary<string, int>
    {
        ["xlr1"] = ProMixSections.Xlr1, ["xlr2"] = ProMixSections.Xlr2, ["aux"] = ProMixSections.UsbAux, ["line"] = ProMixSections.LineIn,
    };

    private static DuckingView View => new(true,
    [
        new("monitor", "Monitor A", MixKind.Monitor, true, false, [],
            MixerConfig.Default().Channels.Where(c => c.IsApplication).ToDictionary(c => c.Id, c => c.Name), []),
        new("monitor2", "Monitor B", MixKind.Monitor, false, false, [], new Dictionary<string, string>(), []),
        new("auxout", "Aux", MixKind.AuxPort, false, true, [], new Dictionary<string, string>(), []),
        new("stream", "Stream", MixKind.VirtualMic, false, false, [], new Dictionary<string, string>(), []),
    ], ["monitor"])
    {
        UnitSources = ProInputs,
        ChannelNames = new Dictionary<string, string>(MixerConfig.Default().Channels.ToDictionary(c => c.Id, c => c.Name)) { ["mic"] = "Mic" },
    };

    [Theory]
    [InlineData("mixerOff", "The software mixer is off, so ducking does not run.")]
    [InlineData("keyNotSupported", "Mic is not one of the Wave XLR Pro's inputs, so the unit cannot use it as a key.")]
    [InlineData("monitorReturn", "The jacks' feed is not in the Wave XLR Pro's headphone mix, where it ducks, so this computer ducks.")]
    [InlineData("mixElsewhere", "Monitor A also plays on Speakers, which the Wave XLR Pro cannot duck.")]
    [InlineData("sharedOutput", "The jacks play Monitor A and Monitor B together, and only Monitor A is ducked.")]
    [InlineData("partialMix", "Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output.")]
    [InlineData("keyInMix", "XLR 1 is heard in Monitor A through its software send, so the unit would lower the key too.")]
    [InlineData("failed", "The Wave XLR Pro did not keep the ducking written to it. Switch ducking off and on to try again.")]
    [InlineData("unsupportedBlock", "The Wave XLR Pro's ducking block holds values OpenXLR does not know.")]
    public void EachRuleReportsItsReasonAndNote(string reason, string note)
    {
        DuckingSetup setup = Setup;
        DuckingView view = View with { SoftwareRunning = true };
        UnitDuckingSupport support = Pro;
        switch (reason)
        {
            case "mixerOff": view = view with { MixerRunning = false }; break;
            case "keyNotSupported": setup = setup with { Keys = ["xlr1", "mic"] }; break;
            case "monitorReturn": support = Pro with { ReachesJacks = false }; break;
            case "mixElsewhere": view = EditMonitor(view, m => m with { OtherConsumers = ["Speakers"] }); break;
            case "sharedOutput": view = view with { JackFeed = ["monitor", "monitor2"] }; break;
            case "partialMix": setup = setup with { Channels = [.. setup.Channels.Where(c => c != "voicechat")] }; break;
            case "keyInMix": view = EditMonitor(view, m => m with
                { KeysInSoftware = ["xlr1"], OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["xlr1"] = "XLR 1" } }); break;
            case "failed": view = view with { Failed = true }; break;
            case "unsupportedBlock": support = Pro with { Ready = false, Reason = "unsupportedBlock" }; break;
        }
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, view, support);
        Assert.False(verdict.Ready);
        Assert.Equal(reason, verdict.Reason);
        Assert.Equal(note, verdict.Note);
        Assert.Equal(reason == "mixerOff" ? null : "software", verdict.Backend);
        Assert.Equal(["monitor"], verdict.Outputs);
    }

    [Theory]
    [InlineData("monitor", "monitor")]
    [InlineData("auxout", "aux")]
    [InlineData("monitor+auxout", "monitor+aux")]
    public void TheDefaultProLayoutFitsAndDerivesItsOutputPairs(string mixes, string outputs)
    {
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = mixes.Split('+') }, View, Pro);
        Assert.True(verdict.Ready);
        Assert.Null(verdict.Reason);
        Assert.Null(verdict.Note);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal(outputs.Split('+'), verdict.Outputs);
        Assert.Equal(outputs.Split('+').Select(o => o == "monitor" ? ProReturnPairs.Monitor : ProReturnPairs.Aux),
            verdict.Outputs.Select(ProDucking.PairOf));
        Assert.Equal(ProDucking.KeyXlr1Mask, verdict.Keys);
        Assert.Empty(verdict.Inputs);
    }

    [Fact]
    public void EveryInputOfTheUnitCanKeyItAndTheLoudestWins()
    {
        // XLR 1, XLR 2, USB Aux and Line In together: one bit each.
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Keys = ["xlr2", "aux", "line", "xlr1"] }, View, Pro);
        Assert.True(verdict.Ready);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal(0b1111, verdict.Keys);
        // XLR 2 bound to another strip keys the unit's XLR 2 just the same.
        DuckingView bound = View with { UnitSources = new Dictionary<string, int> { ["xlr1"] = 0, ["mic"] = 1 } };
        Assert.Equal(0b11, DuckingPlacement.Evaluate(Setup with { Keys = ["xlr1", "mic"] }, bound, Pro).Keys);
    }

    [Theory]
    [InlineData("voicechat", "Voice Chat is not one of the Wave XLR Pro's inputs, so the unit cannot use it as a key.")]
    [InlineData("mic", "Mic is not one of the Wave XLR Pro's inputs, so the unit cannot use it as a key.")]
    public void OutsideHardwareMixingOnlyTheUnitsInputsCanKeyIt(string key, string note)
    {
        // An application channel streams inside a mix's pair, and a capture
        // microphone never reaches the unit, so neither can key it.
        DuckingSetup setup = Setup with { Keys = ["xlr1", key], Channels = [.. Setup.Channels.Where(c => c != key)] };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, View with { SoftwareRunning = true }, Pro);
        Assert.False(verdict.Ready);
        Assert.Equal("keyNotSupported", verdict.Reason);
        Assert.Equal(note, verdict.Note);
        Assert.Equal("software", verdict.Backend);
        Assert.Equal(0, verdict.Keys);
    }

    [Fact]
    public void TheUnitsInputsAmongTheChannelsAreDuckedAtTheirInputFields()
    {
        // XLR 2 and Line In heard in Monitor A beside the applications.
        DuckingView view = EditMonitor(View, m => m with
            { OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["xlr2"] = "XLR 2", ["line"] = "Line In" } });
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Channels = [.. Setup.Channels, "line", "xlr2"] }, view, Pro);
        Assert.True(verdict.Ready);
        Assert.Equal([ProMixSections.Xlr2, ProMixSections.LineIn], verdict.Inputs);
        Assert.Equal([ProReturnPairs.Monitor], verdict.Pairs);
        // Left out of the channels, the inputs it hears there make a partial mix.
        Assert.Equal("partialMix", DuckingPlacement.Evaluate(Setup, view, Pro).Reason);
    }

    [Fact]
    public void AnyKeyHeardThroughItsSoftwareSendKeepsTheUnitOff()
    {
        DuckingView view = EditMonitor(View, m => m with
            { KeysInSoftware = ["aux"], OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["aux"] = "Aux In" } });
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Keys = ["xlr1", "aux"] }, view, Pro);
        Assert.Equal("keyInMix", verdict.Reason);
        Assert.Equal("Aux In is heard in Monitor A through its software send, so the unit would lower the key too.", verdict.Note);
    }

    [Fact]
    public void TheFirstFailureWinsEvenWhileDisabledOrSoftwareIsChosen()
    {
        DuckingSetup setup = Setup with { Enabled = false, Processing = "software", Channels = [] };
        DuckingView view = EditMonitor(View, m => m with { OtherConsumers = ["Speakers"], KeysInSoftware = ["xlr1"] })
            with { MixerRunning = false, UnitSources = new Dictionary<string, int>(), Failed = true, JackFeed = ["monitor", "monitor2"] };
        Assert.Equal("mixerOff", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { MixerRunning = true };
        Assert.Equal("keyNotSupported", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { UnitSources = ProInputs };
        Assert.Equal("mixElsewhere", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = EditMonitor(view, m => m with { OtherConsumers = [] });
        Assert.Equal("sharedOutput", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { JackFeed = ["monitor"] };
        Assert.Equal("partialMix", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        setup = setup with { Channels = Setup.Channels };
        Assert.Equal("keyInMix", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = EditMonitor(view, m => m with { KeysInSoftware = [] });
        Assert.Equal("failed", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
    }

    /// <summary>
    /// Hardware mixing with the default pairs, Monitor A on section 0 and
    /// Monitor B on section 18, the jacks' feed outside the mix the unit used
    /// to duck in, and XLR 1 heard in Monitor A: none of that matters to a
    /// unit that ducks each channel's pair.
    /// </summary>
    private static DuckingView Mode => EditMonitor(View, m => m with
        {
            KeysInSoftware = ["xlr1"],
            OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["xlr1"] = "XLR 1" },
        }) with
        {
            HardwareMixing = true,
            SoftwareRunning = true,
            UnitMixSections = new Dictionary<string, IReadOnlyList<int>> { ["monitor"] = [0], ["monitor2"] = [18] },
            ChannelPairs = new Dictionary<string, int> { ["system"] = 0, ["game"] = 1, ["music"] = 2, ["browser"] = 3, ["voicechat"] = 4, ["sfx"] = 6 },
            ChannelNames = new Dictionary<string, string>(MixerConfig.Default().Channels.ToDictionary(c => c.Id, c => c.Name)) { ["mic"] = "Mic" },
        };

    private static string Sections(DuckingVerdict verdict)
        => verdict.Sections is null ? "none"
            : string.Join("; ", verdict.Sections.OrderBy(s => s.Key).Select(s => $"{s.Key}: {string.Join(" ", s.Value.Order())}"));

    [Fact]
    public void InHardwareMixingTheUnitDucksEachChannelInTheSectionsOfItsMixes()
    {
        DuckingSetup setup = Setup with { Channels = ["music", "game"], Mixes = ["monitor", "monitor2", "stream"] };
        // Voice Chat plays in Monitor A and is not ducked, the key is heard
        // there, and the jacks' feed is outside section 0's bit 5: all fine.
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, Mode, Pro with { ReachesJacks = false });
        Assert.True(verdict.Ready);
        Assert.Null(verdict.Reason);
        Assert.Null(verdict.Note);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal("0: 1 2; 18: 1 2", Sections(verdict));
        Assert.Equal([1, 2], verdict.Pairs);
        Assert.Equal(["monitor"], verdict.Outputs);
        Assert.Equal(ProDucking.KeyXlr1Mask, verdict.Keys);
        // Stream is the computer's to duck, beside the unit.
        Assert.Equal(["stream"], verdict.SoftwareMixes!.Order());

        // The unit ducks whatever processing says: nothing here can lower a mix it builds.
        Assert.Equal("device", DuckingPlacement.Evaluate(setup with { Processing = "software" }, Mode, Pro).Backend);
    }

    [Fact]
    public void InHardwareMixingOnlyTheTargetMixesSectionsAreDucked()
    {
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor"] }, Mode, Pro);
        Assert.Equal("0: 0 1 2 3 4 6", Sections(verdict));
        Assert.Empty(verdict.SoftwareMixes!);

        // A summed feed's section holds both its mixes and is ducked for either.
        DuckingView summed = Mode with { UnitMixSections = new Dictionary<string, IReadOnlyList<int>> { ["monitor"] = [0], ["monitor2"] = [0, 18] } };
        Assert.Equal("0: 0 1 2 3 4 6; 18: 0 1 2 3 4 6", Sections(DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor2"] }, summed, Pro)));
    }

    [Fact]
    public void InHardwareMixingTheUnitsOwnInputsAreDuckedAtTheirInputFields()
    {
        DuckingSetup setup = Setup with { Channels = ["music", "xlr2", "line", "aux"] };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, Mode, Pro);
        Assert.True(verdict.Ready);
        Assert.Null(verdict.Reason);
        Assert.Null(verdict.Note);
        Assert.Equal("0: 2", Sections(verdict));
        Assert.Equal([ProMixSections.Xlr2, ProMixSections.UsbAux, ProMixSections.LineIn], verdict.Inputs);
        // An input alone still ducks in the target mix's section, with no pair.
        DuckingVerdict alone = DuckingPlacement.Evaluate(setup with { Channels = ["xlr2"] }, Mode, Pro);
        Assert.Equal("0: ", Sections(alone));
        Assert.Equal([ProMixSections.Xlr2], alone.Inputs);
        Assert.Empty(alone.Pairs);
    }

    [Fact]
    public void InHardwareMixingAChannelOnAPairCanKeyTheUnit()
    {
        // Voice Chat on pair 4 is source 8; Game on pair 1 is source 5.
        DuckingSetup setup = Setup with { Keys = ["voicechat", "xlr1", "game"], Channels = ["music", "xlr2"] };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, Mode, Pro);
        Assert.True(verdict.Ready);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal(1 << 0 | 1 << 5 | 1 << 8, verdict.Keys);
        Assert.Equal("0: 2", Sections(verdict));
        Assert.Equal([ProMixSections.Xlr2], verdict.Inputs);
    }

    [Fact]
    public void InHardwareMixingAProcessedInputIsDuckedOnItsReturnPairToo()
    {
        // XLR 2 runs through software and plays back to the unit on pair 7.
        DuckingView view = Mode with { ReturnPairs = new Dictionary<string, int> { ["xlr2"] = 7 } };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Channels = ["music", "xlr2"] }, view, Pro);
        Assert.Equal("0: 2 7", Sections(verdict));
        Assert.Equal([2, 7], verdict.Pairs);
        Assert.Equal([ProMixSections.Xlr2], verdict.Inputs);
    }

    [Fact]
    public void InHardwareMixingTheAuxMixTheUnitBuildsIsDuckedInSectionThirtySix()
    {
        // The USB Aux port on: the unit builds the Aux mix in section 36, and
        // a channel beyond the defaults streams on pair 5 like any other.
        DuckingView view = Mode with
        {
            UnitMixSections = new Dictionary<string, IReadOnlyList<int>> { ["monitor"] = [0], ["auxout"] = [36] },
            ChannelPairs = new Dictionary<string, int>(Mode.ChannelPairs) { ["discord"] = 5 },
        };
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Channels = ["music", "discord"], Mixes = ["monitor", "auxout"] }, view, Pro);
        Assert.True(verdict.Ready);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal("0: 2 5; 36: 2 5", Sections(verdict));
        Assert.Equal([2, 5], verdict.Pairs);
        Assert.Equal(["monitor", "aux"], verdict.Outputs);
        // Nothing of the computer's is heard on the port: no software ducking for it.
        Assert.Empty(verdict.SoftwareMixes!);

        // The Aux mix alone ducks section 36 alone, heard on the port.
        DuckingVerdict aux = DuckingPlacement.Evaluate(Setup with { Channels = ["music"], Mixes = ["auxout"] }, view, Pro);
        Assert.Equal("36: 2", Sections(aux));
        Assert.Equal(["aux"], aux.Outputs);
    }

    [Fact]
    public void InHardwareMixingAComputerCopyHeardElsewhereIsDuckedInSoftwareToo()
    {
        DuckingView view = EditMonitor(Mode, m => m with { OtherConsumers = ["Speakers"] });
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor", "auxout"] }, view, Pro);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal("0: 0 1 2 3 4 6", Sections(verdict));
        Assert.Equal(["auxout", "monitor"], verdict.SoftwareMixes!.Order());
    }

    [Theory]
    [InlineData("keyNotSupported", "Mic is neither one of the Wave XLR Pro's inputs nor on one of its USB pairs, so the unit cannot use it as a key and the mixes it builds for its outputs are not ducked.")]
    [InlineData("failed", "The Wave XLR Pro did not keep the ducking written to it. Switch ducking off and on to try again.")]
    [InlineData("unsupportedBlock", "The Wave XLR Pro's ducking block holds values OpenXLR does not know.")]
    public void InHardwareMixingAUnitThatCannotDuckLeavesItsMixesUnduckedAndTheRestToSoftware(string reason, string note)
    {
        DuckingView view = Mode;
        UnitDuckingSupport support = Pro;
        DuckingSetup setup = Setup with { Mixes = ["monitor", "stream"] };
        switch (reason)
        {
            case "keyNotSupported": setup = setup with { Keys = ["xlr1", "mic"] }; break;
            case "failed": view = view with { Failed = true }; break;
            case "unsupportedBlock": support = Pro with { Ready = false, Reason = "unsupportedBlock" }; break;
        }
        DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, view, support);
        Assert.False(verdict.Ready);
        Assert.Equal(reason, verdict.Reason);
        Assert.Equal(note, verdict.Note);
        Assert.Equal("software", verdict.Backend);
        Assert.Null(verdict.Sections);
        Assert.Equal(["stream"], verdict.SoftwareMixes!.Order());
    }

    [Fact]
    public void InHardwareMixingATargetTheUnitDoesNotBuildFollowsTheUsualRules()
    {
        // Only the Aux mix, which the computer still builds on pair 5.
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = ["auxout"] }, Mode, Pro);
        Assert.True(verdict.Ready);
        Assert.Equal("device", verdict.Backend);
        Assert.Equal(["aux"], verdict.Outputs);
        Assert.Null(verdict.Sections);
        Assert.Null(verdict.SoftwareMixes);
        // And outside the mode a view with sections is not read.
        DuckingVerdict software = DuckingPlacement.Evaluate(Setup, View with { UnitMixSections = Mode.UnitMixSections }, Pro);
        Assert.Null(software.Sections);
        Assert.Equal([ProReturnPairs.Monitor], software.Pairs);
    }

    [Fact]
    public void UnheardMixesAreIgnoredButVirtualMicrophonesAlwaysPreventDevicePlacement()
    {
        DuckingView view = View with { Mixes = [.. View.Mixes.Select(m => m.Id == "monitor2"
            ? m with { OpenChannels = new Dictionary<string, string> { ["xlr2"] = "XLR 2" }, KeysInSoftware = ["xlr2"] } : m)] };
        Assert.True(DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor", "monitor2"] }, view, Pro).Ready);
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor", "stream"] }, view, Pro);
        Assert.Equal("mixElsewhere", verdict.Reason);
        Assert.Equal(["monitor"], verdict.Outputs);
    }

    [Fact]
    public void EveryMixInASharedJackFeedMustBeSelected()
    {
        DuckingView view = View with { JackFeed = ["monitor", "monitor2"],
            Mixes = [.. View.Mixes.Select(m => m.Id == "monitor2" ? m with { OnJacks = true } : m)] };
        Assert.Equal("sharedOutput", DuckingPlacement.Evaluate(Setup, view, Pro).Reason);
        Assert.True(DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor", "monitor2"] }, view, Pro).Ready);
    }

    [Fact]
    public void EachJackFeedIsDuckedOnItsOwnPair()
    {
        DuckingView view = View with
        {
            JackFeed = ["monitor", "monitor2"],
            JackFeeds = [new(ProReturnPairs.Monitor, ["monitor"]), new(2, ["monitor2"])],
            Mixes = [.. View.Mixes.Select(m => m.Id == "monitor2" ? m with { OnJacks = true } : m)],
        };
        // Only the feed that plays a ducked mix dips; the other jack is not a shared output.
        DuckingVerdict one = DuckingPlacement.Evaluate(Setup, view, Pro);
        Assert.True(one.Ready);
        Assert.Equal([ProReturnPairs.Monitor], one.Pairs);
        DuckingVerdict both = DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor", "monitor2", "auxout"] }, view, Pro);
        Assert.True(both.Ready);
        Assert.Equal([ProReturnPairs.Monitor, 2, ProReturnPairs.Aux], both.Pairs);
        Assert.Equal([2], DuckingPlacement.Evaluate(Setup with { Mixes = ["monitor2"] }, view, Pro).Pairs);
        // A third feed summing a ducked mix with one that is not.
        view = view with { JackFeeds = [.. view.JackFeeds!, new(3, ["monitor", "monitor2"])] };
        DuckingVerdict summed = DuckingPlacement.Evaluate(Setup, view, Pro);
        Assert.Equal("sharedOutput", summed.Reason);
        Assert.Equal("The jacks play Monitor A and Monitor B together, and only Monitor A is ducked.", summed.Note);
        Assert.Equal([ProReturnPairs.Monitor, 3], summed.Pairs);
    }

    [Fact]
    public void AHardwareKeyPathIsAllowedAndSoftwareChoiceKeepsTheDeviceVerdict()
    {
        DuckingView view = EditMonitor(View, m => m with
            { OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["xlr1"] = "XLR 1" } });
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Processing = "software" }, view, Pro);
        Assert.True(verdict.Ready);
        Assert.Null(verdict.Backend);
        Assert.Null(verdict.Reason);
        Assert.Equal("software", DuckingPlacement.Evaluate(Setup, view with { SoftwareRunning = true }, null).Backend);
        Assert.Null(DuckingPlacement.Evaluate(Setup, view, null).Reason);
        Assert.Null(DuckingPlacement.Evaluate(Setup, view, null).Backend);
    }

    private static DuckingView EditMonitor(DuckingView view, Func<DuckingMixView, DuckingMixView> edit)
        => view with { Mixes = [.. view.Mixes.Select(m => m.Id == "monitor" ? edit(m) : m)] };
}
