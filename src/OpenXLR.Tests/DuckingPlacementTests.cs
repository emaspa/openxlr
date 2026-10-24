using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class DuckingPlacementTests
{
    internal static readonly UnitDuckingSupport Pro = new("Wave XLR Pro", "1.0.14", true);
    private static DuckingSetup Setup => DuckingSetup.Default(MixerConfig.Default()) with { Enabled = true };

    private static DuckingView View => new(true, true,
    [
        new("monitor", "Monitor A", MixKind.Monitor, true, false, [],
            MixerConfig.Default().Channels.Where(c => c.IsApplication).ToDictionary(c => c.Id, c => c.Name), false),
        new("monitor2", "Monitor B", MixKind.Monitor, false, false, [], new Dictionary<string, string>(), false),
        new("auxout", "Aux", MixKind.AuxPort, false, true, [], new Dictionary<string, string>(), false),
        new("stream", "Stream", MixKind.VirtualMic, false, false, [], new Dictionary<string, string>(), false),
    ], ["monitor"]);

    [Theory]
    [InlineData("mixerOff", "The software mixer is off, so ducking does not run.")]
    [InlineData("keyNotSupported", "The Wave XLR Pro ducks only with XLR 1 as the key.")]
    [InlineData("monitorReturn", "The jacks' feed is not in the Wave XLR Pro's headphone mix, where it ducks, so this computer ducks.")]
    [InlineData("mixElsewhere", "Monitor A also plays on Speakers, which the Wave XLR Pro cannot duck.")]
    [InlineData("sharedOutput", "The jacks play Monitor A and Monitor B together, and only Monitor A is ducked.")]
    [InlineData("partialMix", "Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output.")]
    [InlineData("keyInMix", "XLR 1 is heard in Monitor A through its software send, so the unit would lower the voice too.")]
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
            case "keyNotSupported": view = view with { KeyIsUnitInput1 = false }; break;
            case "monitorReturn": support = Pro with { ReachesJacks = false }; break;
            case "mixElsewhere": view = EditMonitor(view, m => m with { OtherConsumers = ["Speakers"] }); break;
            case "sharedOutput": view = view with { JackFeed = ["monitor", "monitor2"] }; break;
            case "partialMix": setup = setup with { Channels = [.. setup.Channels.Where(c => c != "voicechat")] }; break;
            case "keyInMix": view = EditMonitor(view, m => m with
                { KeyInSoftware = true, OpenChannels = new Dictionary<string, string>(m.OpenChannels) { ["xlr1"] = "XLR 1" } }); break;
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

    [Fact]
    public void TheAuxPortRunsInSoftwareUntilTheUnitIsKnownToReachIt()
    {
        DuckingVerdict verdict = DuckingPlacement.Evaluate(Setup with { Mixes = ["auxout"] },
            View with { SoftwareRunning = true }, Pro with { ReachesAux = false });
        Assert.Equal(("auxNotChecked", "software", false), (verdict.Reason, verdict.Backend, verdict.Ready));
        Assert.Equal("Ducking on the Wave XLR Pro's USB Aux port has not been checked by ear yet, so this computer ducks.", verdict.Note);
        Assert.Equal(["aux"], verdict.Outputs);
        // A unit that reaches the jacks but not the Aux port still runs a jacks-only setup.
        Assert.Equal("device", DuckingPlacement.Evaluate(Setup, View, Pro with { ReachesAux = false }).Backend);
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
    }

    [Fact]
    public void TheFirstFailureWinsEvenWhileDisabledOrSoftwareIsChosen()
    {
        DuckingSetup setup = Setup with { Enabled = false, Processing = "software", Channels = [] };
        DuckingView view = EditMonitor(View, m => m with { OtherConsumers = ["Speakers"], KeyInSoftware = true })
            with { MixerRunning = false, KeyIsUnitInput1 = false, Failed = true, JackFeed = ["monitor", "monitor2"] };
        Assert.Equal("mixerOff", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { MixerRunning = true };
        Assert.Equal("keyNotSupported", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { KeyIsUnitInput1 = true };
        Assert.Equal("mixElsewhere", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = EditMonitor(view, m => m with { OtherConsumers = [] });
        Assert.Equal("sharedOutput", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = view with { JackFeed = ["monitor"] };
        Assert.Equal("partialMix", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        setup = setup with { Channels = Setup.Channels };
        Assert.Equal("keyInMix", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
        view = EditMonitor(view, m => m with { KeyInSoftware = false });
        Assert.Equal("failed", DuckingPlacement.Evaluate(setup, view, Pro).Reason);
    }

    [Fact]
    public void UnheardMixesAreIgnoredButVirtualMicrophonesAlwaysPreventDevicePlacement()
    {
        DuckingView view = View with { Mixes = [.. View.Mixes.Select(m => m.Id == "monitor2"
            ? m with { OpenChannels = new Dictionary<string, string> { ["xlr2"] = "XLR 2" }, KeyInSoftware = true } : m)] };
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
