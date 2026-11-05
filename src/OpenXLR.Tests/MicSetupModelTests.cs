using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Mic setup's Shape and Position steps with no window: the four steps in
/// order, starting on Position; the presets are valid gentle EQs that the
/// manual lists as they are; a preset goes out as one setEq and its undo as
/// another; and the Position tips, phantom power and filters follow the unit
/// the channel carries.
/// </summary>
public sealed class MicSetupModelTests
{
    private sealed class Recorder : IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();

        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }

        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }

        public Task SetEqProcessingAsync(string channel, string value) { Sent.Enqueue(("setEqProcessing", channel, value)); return Task.CompletedTask; }

        public Task SetEqBandAsync(string channel, int band, EqBandChange change) { Sent.Enqueue(("setEqBand", channel, change)); return Task.CompletedTask; }

        public MicEq Eq()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Equal("setEq", sent.Command);
            Assert.Empty(Sent);
            return (MicEq)sent.Value;
        }
    }

    [Fact]
    public async Task TheStepsAreFourAndASetupStartsOnPosition()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var check = new MicSetupViewModel(client, "xlr1");
        Assert.Equal([MicSetupStep.Position, MicSetupStep.Level, MicSetupStep.Listen, MicSetupStep.Shape], check.Steps.Select(s => s.Step));
        Assert.Equal([Localizer.Text("MicSetupStepPosition"), Localizer.Text("MicSetupStepLevel"), Localizer.Text("MicSetupStepListen"),
            Localizer.Text("MicSetupStepShape")], check.Steps.Select(s => s.Name));
        Assert.True(check.ShowsPosition);
        check.Steps[3].IsSelected = true;
        Assert.True(check.ShowsShape);
        Assert.False(check.ShowsPosition || check.ShowsLevel || check.ShowsListen);
        // The step shown cannot be unchosen.
        check.Steps[3].IsSelected = false;
        Assert.True(check.ShowsShape);
    }

    [Fact]
    public void ThePresetsAreGentleValidEqsWithALowCutFirst()
    {
        Assert.Equal(["clear", "warm", "broadcast", "podcast", "deep-voice", "soft-sibilants"],
            MicSetupPresets.All.Select(p => p.Id));
        Assert.Equal(MicSetupPresets.All.Count, MicSetupPresets.All.Select(p => p.Name).Distinct().Count());
        foreach (MicSetupPreset preset in MicSetupPresets.All)
        {
            Assert.Null(MicEq.Check(preset.Eq));
            Assert.True(preset.Eq.Enabled);
            Assert.All(preset.Eq.Bands, b => Assert.True(b.Enabled));
            Assert.Equal(MicEq.LowCut, preset.Eq.Bands[0].Type);
            Assert.InRange(preset.Eq.Bands[0].Frequency, 60, 100);
            // A starting point, not a sound: no band moves more than 3 dB.
            Assert.All(preset.Eq.Bands.Skip(1), b => Assert.InRange(b.Gain, -3, 3));
            // Already on the daemon's grid, so what is sent is what is kept.
            Assert.Equal(preset.Eq, MicEq.Snap(preset.Eq));
            Assert.Equal(preset.Eq, preset.For(EqLimits.Instance));
            Assert.NotEqual(Localizer.Text("MicSetupPresetHint"), preset.Hint);
        }
        // A daemon with a narrower range gets the preset held inside it.
        EqLimits narrow = EqLimits.Instance with { Gain = new(-2, 2, 0.25), Frequency = new(100, 10000, 1) };
        MicEq held = MicSetupPresets.Broadcast.For(narrow);
        Assert.All(held.Bands, b => Assert.InRange(b.Gain, -2, 2));
        Assert.All(held.Bands, b => Assert.InRange(b.Frequency, 100, 10000));
    }

    [Theory]
    [InlineData("podcast", 1900, 120, 3)]
    [InlineData("deep-voice", 4800, 170, 3)]
    [InlineData("soft-sibilants", 2000, 6600, 2)]
    public void NewPresetsShapeTheIntendedRange(string id, int featuredHz, int reducedHz, double minimumDifference)
    {
        MicEq eq = Assert.Single(MicSetupPresets.All, preset => preset.Id == id).Eq;
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            double featured = EqResponse.MagnitudeDb(eq, rate, featuredHz);
            double reduced = EqResponse.MagnitudeDb(eq, rate, reducedHz);
            Assert.InRange(featured - reduced, minimumDifference, 8);
            Assert.InRange(featured, -1, 3);
        }
    }

    /// <summary>The manual's table is the presets as the code holds them, band by band.</summary>
    [Fact]
    public void TheManualListsThePresetsAsTheyAre()
    {
        string manual = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "docs", "manual.md"));
        foreach (MicSetupPreset preset in MicSetupPresets.All)
        {
            string name = EqPresets.FindId(EqProfileIds.Builtin(preset.Id))!.Name;
            string row = $"| {name} | " + string.Join(" | ", preset.Eq.Bands.Select(Describe)) + " |";
            Assert.Contains(row, manual);
        }

        static string Describe(EqBand b)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string hz = b.Frequency >= 1000 ? (b.Frequency / 1000).ToString("0.#", c) + " kHz" : b.Frequency.ToString("0", c) + " Hz";
            string db = b.Gain.ToString("+0.##;-0.##;0", c) + " dB";
            return b.Type switch
            {
                MicEq.LowCut => $"high pass {hz}",
                MicEq.LowShelf => $"low shelf {hz} {db}",
                MicEq.HighShelf => $"high shelf {hz} {db}",
                MicEq.Peak => $"bell {hz} {db}, Q {b.Q.ToString("0.00", c)}",
                _ => throw new InvalidOperationException(b.Type),
            };
        }
    }

    [Fact]
    public async Task APresetIsOneSetEqAndItsUndoPutsTheEqBackInOne()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var sent = new Recorder();
        using var eq = new MicEqViewModel(sent, "xlr1", "XLR 1", TimeSpan.Zero, new ManualTime());
        var check = new MicSetupViewModel(client, "xlr1");
        Assert.False(check.CanApplyPreset);
        check.ApplyPreset(MicSetupPresets.Clear);
        Assert.Empty(sent.Sent);

        check.Eq = eq;
        MicEq before = eq.Current;
        Assert.False(before.Enabled);
        Assert.True(check.CanApplyPreset);
        Assert.False(check.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicSetupPresetHint"), check.PresetStatus);

        check.ApplyPreset(MicSetupPresets.Clear);
        Assert.Equal(MicSetupPresets.Clear.Eq, sent.Eq());
        Assert.Equal(MicSetupPresets.Clear.Eq, eq.Current);
        Assert.True(eq.Enabled);
        Assert.True(check.CanUndoPreset);
        Assert.Equal(Localizer.Format("MicSetupPresetApplied", MicSetupPresets.Clear.Name), check.PresetStatus);

        // A second preset replaces the first; the undo goes back one step.
        check.ApplyPreset(MicSetupPresets.Broadcast);
        Assert.Equal(MicSetupPresets.Broadcast.Eq, sent.Eq());
        check.UndoPreset();
        Assert.Equal(MicSetupPresets.Clear.Eq, sent.Eq());
        Assert.Equal(MicSetupPresets.Clear.Eq, eq.Current);
        Assert.False(check.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicSetupPresetUndone"), check.PresetStatus);
        check.UndoPreset();
        Assert.Empty(sent.Sent);

        // The EQ from before the first preset, switch included, comes back whole.
        check.ApplyPreset(MicSetupPresets.Warm);
        sent.Eq();
        check.Eq = null;
        check.Eq = eq;
        Assert.False(check.CanUndoPreset);
        check.ApplyPreset(MicSetupPresets.Warm);
        sent.Eq();
        check.Reset();
        Assert.False(check.CanUndoPreset);
        check.UndoPreset();
        Assert.Empty(sent.Sent);
        eq.Replace(before);
        sent.Eq();
        check.ApplyPreset(MicSetupPresets.Warm);
        sent.Eq();
        check.UndoPreset();
        Assert.Equal(before, sent.Eq());
        Assert.False(eq.Enabled);
    }

    [Fact]
    public async Task PositionAndTheFiltersFollowTheUnitTheChannelCarries()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        string Xlr1 = InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1));
        string Xlr2 = InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2));
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState($"{Xlr1},{Xlr2}"))!;
        state["mixer"]!["channels"]![0]!["eq"] = CaptureInputWindowTests.CaptureEq(enabled: true);
        CaptureInputWindowTests.Apply(main, state);
        MicSetupViewModel first = main.Inserts.MicSetup, second = main.Inserts2.MicSetup;
        ChannelViewModel xlr1 = main.Channels.Single(c => c.Id == "xlr1");

        // The Pro's XLR inputs: the unit by name and input, its phantom power,
        // its own low cut and ClipGuard, and XLR 1's EQ and software stage.
        Assert.Equal(MicSetupUnitKind.XlrJack, first.UnitKind);
        Assert.Equal(Localizer.Format("MicSetupPositionXlr", "Elgato Wave XLR Pro", 1), first.PositionIntro);
        Assert.Equal(Localizer.Format("MicSetupPositionXlr", "Elgato Wave XLR Pro", 2), second.PositionIntro);
        Assert.Equal(Localizer.Text("MicSetupTipXlrAddress"), first.PositionTips[0]);
        Assert.Contains(Localizer.Text("MicSetupTipAngle"), first.PositionTips);
        Assert.True(first.ShowsPhantom && first.HasPhantom && !first.PhantomElsewhere);
        Assert.True(first.HasUnitFilters);
        Assert.Same(main, first.Software);
        Assert.Null(second.Software);
        Assert.Same(xlr1.Eq, first.Eq);
        Assert.NotNull(first.Eq);
        Assert.Same(main.Inserts, first.Chain);
        Assert.Same(main.Inserts2, second.Chain);

        // A Wave:3's capsule: its own tips, and no phantom power to speak of.
        state["capabilities"]!["xlrJacks"] = false;
        state["capabilities"]!["phantom"] = false;
        state["capabilities"]!["lowCut"] = false;
        state["capabilities"]!["clipGuard"] = false;
        state["device"]!["model"] = "Wave:3";
        CaptureInputWindowTests.Apply(main, state);
        Assert.Equal(MicSetupUnitKind.Capsule, first.UnitKind);
        Assert.Equal(Localizer.Format("MicSetupPositionCapsule", "Elgato Wave:3"), first.PositionIntro);
        Assert.Equal([Localizer.Text("MicSetupTipCapsuleFront"), Localizer.Text("MicSetupTipCapsuleRoom")], first.PositionTips.Take(2));
        Assert.False(first.ShowsPhantom);
        Assert.False(first.HasUnitFilters);

        // XLR 1 bound to a webcam, with the Pro's microphone on a capture
        // channel: XLR 1 now carries no unit, and the capture channel does.
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingModelTests.ProState(
            $"{InputBindingModelTests.BoundXlr1(true)},{Xlr2},{InputBindingModelTests.ProMic(InputBindingModelTests.Input(1))}",
            mixerExtra: ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true"))!);
        Assert.Equal(MicSetupUnitKind.None, first.UnitKind);
        Assert.Equal(Localizer.Text("MicSetupPositionNone"), first.PositionIntro);
        Assert.Equal(Localizer.Text("MicSetupTipOtherDevice"), first.PositionTips[0]);
        Assert.True(first.ShowsPhantom && !first.HasPhantom && first.PhantomElsewhere);
        Assert.False(first.HasUnitFilters);
        // XLR 1's software low cut and ClipGuard stay with XLR 1 whatever feeds it.
        Assert.True(first.HasSoftwareFilters);
        Assert.False(first.NoFilters);
        Assert.Null(first.Eq);
    }

    // --- Suggest gain ---

    /// <summary>Five seconds of the 15 Hz meter: the same RMS and peak in every reading.</summary>
    private static List<LevelReading> Steady(double rmsDb, double peakDb, int count = 75)
        => [.. Enumerable.Repeat(new LevelReading(rmsDb, peakDb, false), count)];

    [Fact]
    public void SuggestGainBringsSpeechToTheTargetWithThePeakInTheBand()
    {
        // Speech 10 dB under the target, the highest peak 12 dB under the
        // band's top: the speech level decides, 10 dB up.
        GainSuggestion s = GainSuggestion.From(Steady(-30, -18), 40, 80);
        Assert.Equal(GainOutcome.Change, s.Outcome);
        Assert.Equal(10, s.ChangeDb, 6);
        Assert.Equal((40, 50), (s.FromDb, s.ToDb));
        Assert.True(s.ChangesGain);
        Assert.False(s.Clipped);

        // A peaky voice: the peak decides, and stays at the band's top.
        s = GainSuggestion.From(Steady(-30, -12), 40, 80);
        Assert.Equal((40, 46), (s.FromDb, s.ToDb));
        Assert.True(s.PeakDb + (s.ToDb - s.FromDb) <= MicSetupLevels.BandHighDb);

        // Whole dB rounded down where rounding up would lift the peak over the top.
        s = GainSuggestion.From(Steady(-30, -15.5), 40, 80);
        Assert.Equal(9.5, s.ChangeDb, 6);
        Assert.Equal(49, s.ToDb);
        // Elsewhere it rounds to the nearest.
        s = GainSuggestion.From(Steady(-29.4, -25), 40, 80);
        Assert.Equal(49, s.ToDb);

        // Downwards too, and on a unit with a smaller range.
        s = GainSuggestion.From(Steady(-12, -3), 30, 40);
        Assert.Equal((30, 22), (s.FromDb, s.ToDb));
    }

    [Fact]
    public void SpeechIsTheNinetyFifthPercentileSoAFewLoudMomentsDoNotDecide()
    {
        // Ten loud readings in a hundred: the top five percent are among them.
        List<LevelReading> readings = [.. Steady(-40, -30, 90), .. Steady(-25, -15, 10)];
        Assert.Equal(-25, GainSuggestion.From(readings, 40, 80).SpeechDb);
        // Four are not: the speech level is the rest of the voice, and the
        // loud moments still count through the peak.
        readings = [.. Steady(-40, -30, 96), .. Steady(-25, -9, 4)];
        GainSuggestion s = GainSuggestion.From(readings, 40, 80);
        Assert.Equal(-40, s.SpeechDb);
        Assert.Equal(-9, s.PeakDb);
        Assert.Equal(3, s.ChangeDb, 6);
    }

    [Fact]
    public void SuggestGainKeepsAGainThatIsCloseAndStaysInsideTheRange()
    {
        GainSuggestion s = GainSuggestion.From(Steady(-21, -10), 40, 80);
        Assert.Equal(GainOutcome.Keep, s.Outcome);
        Assert.Equal(40, s.ToDb);
        Assert.False(s.ChangesGain);

        // At the top already: nothing to change, and how far short it is.
        s = GainSuggestion.From(Steady(-40, -30), 75, 75);
        Assert.Equal(GainOutcome.AtMaximum, s.Outcome);
        Assert.False(s.ChangesGain);
        Assert.Equal(20, s.ShortfallDb, 6);
        // Short of the top: up to it.
        s = GainSuggestion.From(Steady(-40, -30), 70, 75);
        Assert.Equal((GainOutcome.AtMaximum, 75), (s.Outcome, s.ToDb));
        Assert.Equal(15, s.ShortfallDb, 6);
        // Within half a dB of the top is an ordinary change to it.
        s = GainSuggestion.From(Steady(-30.3, -25), 70, 80);
        Assert.Equal((GainOutcome.Change, 80), (s.Outcome, s.ToDb));

        s = GainSuggestion.From(Steady(-8, -1), 3, 80);
        Assert.Equal((GainOutcome.AtMinimum, 0), (s.Outcome, s.ToDb));
        Assert.Equal(9, s.ShortfallDb, 6);
    }

    [Fact]
    public void AClipLowersTheGainAndAnOldClipFlagDoesNotCount()
    {
        GainSuggestion s = GainSuggestion.From(Steady(-14, -0.1), 50, 80);
        Assert.True(s.Clipped);
        Assert.True(s.ChangeDb <= -5.9);
        Assert.Equal(44, s.ToDb);

        // The daemon holds the flag for a second: set when listening starts
        // and falling, it is an older clip.
        List<LevelReading> readings = Steady(-21, -10);
        for (int i = 0; i < 10; i++) readings[i] = readings[i] with { Clip = true };
        s = GainSuggestion.From(readings, 40, 80);
        Assert.False(s.Clipped);
        Assert.Equal(GainOutcome.Keep, s.Outcome);
        // Rising while listening, it is a clip even when no reading caught the peak.
        readings[40] = readings[40] with { Clip = true };
        s = GainSuggestion.From(readings, 40, 80);
        Assert.True(s.Clipped);
        Assert.NotEqual(GainOutcome.Keep, s.Outcome);
    }

    [Fact]
    public void SuggestGainDoesNotGuessFromSilenceOrTooFewReadings()
    {
        GainSuggestion s = GainSuggestion.From(Steady(-60, -60), 40, 80);
        Assert.Equal(GainOutcome.NoVoice, s.Outcome);
        Assert.False(s.ChangesGain);
        s = GainSuggestion.From(Steady(-30, -18, MicSetupLevels.MinReadings - 1), 40, 80);
        Assert.Equal(GainOutcome.TooFewReadings, s.Outcome);
        Assert.False(s.ChangesGain);

        // No gain OpenXLR sets: the change is said, never made.
        s = GainSuggestion.From(Steady(-30, -18), null, 0);
        Assert.Equal(GainOutcome.Elsewhere, s.Outcome);
        Assert.Equal(10, s.ChangeDb, 6);
        Assert.Null(s.ToDb);
        Assert.False(s.ChangesGain);
        Assert.Equal(GainOutcome.Keep, GainSuggestion.From(Steady(-21, -10), null, 0).Outcome);
    }

    /// <summary>The band Suggest gain aims for is the one the Level step draws and names.</summary>
    [Fact]
    public void SuggestGainAimsForTheBandTheLevelStepDraws()
    {
        string window = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "src", "OpenXLR.UI", "MicSetupWindow.axaml"));
        static double Share(double from, double to) => (to - from) / 6;
        string columns = $"ColumnDefinitions=\"{Share(MicSetupLevels.MeterFloorDb, MicSetupLevels.BandLowDb)}*,"
            + $"{Share(MicSetupLevels.BandLowDb, MicSetupLevels.BandHighDb)}*,{Share(MicSetupLevels.BandHighDb, 0)}*\"";
        int target = window.IndexOf("Name=\"MicSetupTarget\"", StringComparison.Ordinal);
        Assert.True(target > 0);
        Assert.Equal(window.LastIndexOf("ColumnDefinitions=", target, StringComparison.Ordinal),
            window.LastIndexOf(columns, target, StringComparison.Ordinal));
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, "{0} and {1} dBFS", MicSetupLevels.BandLowDb, MicSetupLevels.BandHighDb),
            Localizer.Text("MicSetupTargetTip"));
        // Speech at the target leaves room for its peaks inside the band.
        Assert.InRange(MicSetupLevels.TargetSpeechDb, MicSetupLevels.BandLowDb - 6, MicSetupLevels.BandLowDb);
    }


    // --- the chosen preset ---

    [Fact]
    public async Task PresetUndoRestoresEveryBandAdvertisedByTheDaemon()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var sent = new Recorder();
        using var eq = new MicEqViewModel(sent, "xlr1", "Mic", TimeSpan.Zero, new ManualTime());
        JsonNode limits = JsonSerializer.SerializeToNode(EqLimits.Instance, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        int count = limits["maxBands"]?.GetValue<int>() ?? limits["bands"]!.GetValue<int>();
        MicEq before = new() { Enabled = true, Bands = [.. Enumerable.Range(0, count)
            .Select(i => MicEq.Default.Bands[i % MicEq.BandCount] with { Gain = i * .25 })] };
        Assert.Null(MicEq.Check(before));
        eq.Apply(Reported(before), limits);
        var setup = new MicSetupViewModel(client, "xlr1") { Eq = eq };
        setup.ApplyPreset(MicSetupPresets.Warm);
        Assert.Equal(MicSetupPresets.Warm.Eq, sent.Eq());
        setup.UndoPreset();
        Assert.Equal(before, sent.Eq());
        Assert.Equal(before, eq.Current);
        Assert.Equal(count, eq.Bands.Count);
    }

    /// <summary>A channel's <c>eq</c> as the daemon reports it, with these bands.</summary>
    private static JsonNode Reported(MicEq eq) => new JsonObject
    {
        ["enabled"] = eq.Enabled,
        ["bands"] = new JsonArray([.. eq.Bands.Select(b => (JsonNode)new JsonObject
        {
            ["enabled"] = b.Enabled, ["type"] = b.Type, ["frequency"] = b.Frequency, ["gain"] = b.Gain, ["q"] = b.Q,
        })]),
        ["backend"] = "software",
        ["available"] = true,
    };

    [Fact]
    public async Task ThePresetTheEqHoldsIsLitUntilAnEditUndoOrTheSwitchLeavesIt()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var sent = new Recorder();
        using var eq = new MicEqViewModel(sent, "xlr1", "XLR 1", TimeSpan.Zero, new ManualTime());
        var setup = new MicSetupViewModel(client, "xlr1") { Eq = eq };
        MicSetupPresetItem Item(MicSetupPreset preset) => setup.Presets.Single(p => p.Preset == preset);
        Assert.Equal(MicSetupPresets.All, setup.Presets.Select(p => p.Preset));
        Assert.Null(setup.ActivePreset);
        Assert.All(setup.Presets, p => Assert.False(p.IsActive));

        var raised = new List<string>();
        Item(MicSetupPresets.Warm).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        setup.ChoosePreset(Item(MicSetupPresets.Warm));
        sent.Eq();
        Assert.Same(MicSetupPresets.Warm, setup.ActivePreset);
        Assert.True(Item(MicSetupPresets.Warm).IsActive);
        Assert.False(Item(MicSetupPresets.Clear).IsActive);
        Assert.Contains(nameof(MicSetupPresetItem.IsActive), raised);

        // Pressing the lit preset again sends nothing and keeps the undo.
        setup.ChoosePreset(Item(MicSetupPresets.Warm));
        Assert.Empty(sent.Sent);
        Assert.True(setup.CanUndoPreset);
        Assert.True(Item(MicSetupPresets.Warm).IsActive);

        // The daemon reports the preset back: still lit.
        eq.Apply(Reported(MicSetupPresets.Warm.Eq), null);
        Assert.Same(MicSetupPresets.Warm, setup.ActivePreset);

        // A band moved off the preset's value clears it, and moving it back lights it again.
        eq.Bands[1].SetGain(eq.Bands[1].Gain + 1);
        Assert.Null(setup.ActivePreset);
        Assert.False(Item(MicSetupPresets.Warm).IsActive);
        eq.Bands[1].SetGain(eq.Bands[1].Gain - 1);
        Assert.Same(MicSetupPresets.Warm, setup.ActivePreset);

        // The EQ switched off is not the preset.
        eq.Enabled = false;
        Assert.Null(setup.ActivePreset);
        eq.Enabled = true;
        Assert.Same(MicSetupPresets.Warm, setup.ActivePreset);
        sent.Sent.Clear();

        // Another preset moves the light; the undo puts back Warm, lit again.
        setup.ChoosePreset(Item(MicSetupPresets.Broadcast));
        sent.Eq();
        Assert.True(Item(MicSetupPresets.Broadcast).IsActive);
        Assert.False(Item(MicSetupPresets.Warm).IsActive);
        setup.UndoPreset();
        sent.Eq();
        Assert.Same(MicSetupPresets.Warm, setup.ActivePreset);

        // An undo to an EQ that is no preset leaves none lit, and so does losing the EQ.
        eq.Replace(MicEq.Default);
        sent.Eq();
        setup.ChoosePreset(Item(MicSetupPresets.Clear));
        sent.Eq();
        setup.UndoPreset();
        sent.Eq();
        Assert.Null(setup.ActivePreset);
        setup.ChoosePreset(Item(MicSetupPresets.Clear));
        sent.Eq();
        Assert.True(Item(MicSetupPresets.Clear).IsActive);
        setup.Eq = null;
        Assert.Null(setup.ActivePreset);
        Assert.False(Item(MicSetupPresets.Clear).IsActive);
    }

    // --- the microphone picker ---

    [Fact]
    public async Task OneMicrophoneShowsNoPicker()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        Assert.False(main.MicSetupInputs.Any);
        Assert.Null(main.MicSetupInputs.Primary);
        // A Pro whose second input the device does not have: XLR 1 alone.
        string xlr2 = InputBindingModelTests.Xlr("xlr2", 1, null).Replace("\"present\":true", "\"present\":false", StringComparison.Ordinal);
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingModelTests.ProState(
            $"{InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1))},{xlr2}"))!);
        MicSetupInput only = Assert.Single(main.MicSetupInputs.Items);
        Assert.Equal(("XLR 1", "xlr1"), (only.Name, only.Setup.Channel));
        Assert.Same(main.Inserts.MicSetup, only.Setup);
        Assert.Same(only, main.MicSetupInputs.Primary);
        Assert.True(main.MicSetupInputs.Any);
        Assert.False(main.MicSetupInputs.Many);
        Assert.False(main.Inserts.MicSetup.ShowsPicker);
        Assert.Same(only, main.Inserts.MicSetup.Picked);
        // A setup made outside the main window's model has no list at all.
        Assert.False(new MicSetupViewModel(client, "xlr1").ShowsPicker);
    }

    [Fact]
    public async Task AProListsBothXlrInputsAndOnlyMicrophones()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        string xlr1 = InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1));
        string xlr2 = InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2));
        // A stereo capture input is no microphone, so Mic setup does not offer it.
        const string Stereo = """
            {"id":"camera","name":"Camera","captureSource":"cam","capturePair":0,"captureConnected":true,
             "microphone":false,"chainWidth":2,"levels":{"monitor":1}}
            """;
        string State(string second) => InputBindingModelTests.ProState($"{xlr1},{second},{Stereo}");
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(State(xlr2))!);
        Assert.Equal([("XLR 1", "xlr1"), ("XLR 2", "xlr2")], main.MicSetupInputs.Items.Select(i => (i.Name, i.Setup.Channel)));
        Assert.True(main.MicSetupInputs.Many);
        MicSetupViewModel first = main.Inserts.MicSetup, second = main.Inserts2.MicSetup;
        Assert.True(first.ShowsPicker && second.ShowsPicker);
        Assert.Same(main.MicSetupInputs.Items[0], first.Picked);
        Assert.Same(main.MicSetupInputs.Items[1], second.Picked);
        Assert.Same(first, main.MicSetupInputs.Primary!.Setup);
        // Each one's Position names its own XLR input.
        Assert.Equal(Localizer.Format("MicSetupPositionXlr", "Elgato Wave XLR Pro", 2), second.PositionIntro);

        // A state that changes nothing keeps the same entries; a rename shows.
        MicSetupInput kept = main.MicSetupInputs.Items[1];
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(State(xlr2))!);
        Assert.Same(kept, main.MicSetupInputs.Items[1]);
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(State(xlr2.Replace("XLR 2", "Guest", StringComparison.Ordinal)))!);
        Assert.Equal(["XLR 1", "Guest"], main.MicSetupInputs.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task AnAdditionalInterfacesMicrophonesAreListedWithTheirOwnUnit()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        // XLR 2 bound to the Wave:3 driven beside the Pro; the Pro's second
        // input kept on a capture channel the daemon does not call a microphone.
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingWindowTests.DrivenState(withMix: false))!);
        Assert.Equal([("XLR 1", "xlr1"), ("Mic 2", "xlr2")], main.MicSetupInputs.Items.Select(i => (i.Name, i.Setup.Channel)));
        MicSetupViewModel xlr2 = main.MicSetupInputs.Items[1].Setup;
        Assert.Same(main.Channels.Single(c => c.Id == "xlr2").ExtraUnitInput, xlr2.ExtraUnit);
        Assert.Equal(MicSetupUnitKind.Capsule, xlr2.UnitKind);

        // The Wave:3 on a capture input of its own: listed by the channel's name, with its own Mic setup.
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingWindowTests.DrivenState(withMix: false, captureCarried: true))!);
        Assert.Equal(["XLR 1", "Mic 2", "Wave:3 microphone"], main.MicSetupInputs.Items.Select(i => i.Name));
        MicSetupInput wave3 = main.MicSetupInputs.Items[2];
        Assert.Same(main.Channels.Single(c => c.Id == "wave3mic").Inserts!.MicSetup, wave3.Setup);
        Assert.NotNull(wave3.Setup.ExtraUnit);
        Assert.Equal(MicSetupUnitKind.Capsule, wave3.Setup.UnitKind);
        Assert.True(wave3.Setup.ShowsPicker);
        Assert.Same(wave3, wave3.Setup.Picked);
        Assert.Same(main.Inserts.MicSetup, main.MicSetupInputs.Primary!.Setup);
    }

    [Fact]
    public async Task MovingToAnotherMicrophoneStopsTheSessionHereAndKeepsTheStep()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "watchInputMeter") continue;
                commands.Enqueue(command);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var first = new MicSetupViewModel(client, "xlr1") { Step = MicSetupStep.Level };
        var second = new MicSetupViewModel(client, "guest");
        Assert.Equal(MicSetupStep.Position, second.Step);
        first.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":0,"hearMyself":true}"""));
        Assert.True(first.Active && first.HearMyself);
        first.StartGainMeasure();
        Assert.True(first.Measuring);

        // A session runs here: it stops first, the measurement ends, and the
        // next microphone opens on the same step with nothing of its own started.
        await first.HandOverAsync(second);
        Assert.False(first.Measuring);
        Assert.Equal(MicSetupStep.Level, second.Step);
        JsonNode stopped = Assert.Single(commands);
        Assert.Equal(("soundCheck", "xlr1", "stop"),
            (stopped["cmd"]!.GetValue<string>(), stopped["channel"]!.GetValue<string>(), stopped["action"]!.GetValue<string>()));

        // Nothing runs on the second: moving back sends nothing, and neither does moving to itself.
        second.Step = MicSetupStep.Shape;
        await second.HandOverAsync(first);
        Assert.Equal(MicSetupStep.Shape, first.Step);
        await first.HandOverAsync(first);
        // A request made after them arrives next, so nothing was sent in between.
        await first.RunAsync("live");
        Assert.Equal(["xlr1:stop", "xlr1:live"], commands.Select(c => $"{c["channel"]}:{c["action"]}"));
    }
}

/// <summary>
/// Suggest gain through the main window's model. An accepted gain goes out
/// through the gain slider's shared debounce: its dispatcher timer and its
/// touch guard are static, so this class runs in the collection of the other
/// tests that write a gain that way, and clears the guard before and after.
/// </summary>
[Collection("xdg-config")]
public sealed class MicSetupGainModelTests : IDisposable
{
    public MicSetupGainModelTests() => SliderSync.ReleaseTouchGuards();

    public void Dispose() => SliderSync.ReleaseTouchGuards();

    private static void Meter(MicSetupViewModel check, ManualTime time, double rmsDb, double peakDb)
    {
        time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));
        check.ApplyInputLevel(new JsonArray((rmsDb + 60) / 60, (peakDb + 60) / 60, 0));
    }

    [Fact]
    public async Task SuggestGainWritesNothingUntilAcceptedAndThenThroughTheInputsOwnGain()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        string Xlr1 = InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1));
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(Xlr1))!;
        state["capabilities"]!["gainMaxDb"] = 80;
        state["state"] = new JsonObject { ["gainDb"] = 40, ["gainLocked"] = false };
        CaptureInputWindowTests.Apply(main, state);
        MicSetupViewModel check = main.Inserts.MicSetup;
        var time = new ManualTime();
        check.Time = time;
        Assert.NotNull(check.Unit);
        Assert.Equal(40, main.GainDb);

        check.StartGainMeasure();
        Assert.True(check.Measuring);
        Assert.False(check.CanSuggestGain);
        Assert.Equal(Localizer.Format("MicSetupSuggestListening", 5.0), check.SuggestAdvice);
        for (int i = 0; i < 74; i++) Meter(check, time, -30, -18);
        Assert.True(check.Measuring);
        Meter(check, time, -30, -18);
        Assert.False(check.Measuring);
        Assert.Equal(GainOutcome.Change, check.Suggestion!.Outcome);
        Assert.Equal(50, check.Suggestion.ToDb);
        Assert.Equal(Localizer.Format("MicSetupSuggestChange", 40, 50), check.SuggestAdvice);
        Assert.Equal(Localizer.Format("MicSetupSuggestMeasured", -30.0, -18.0), check.SuggestMeasured);
        // Measured, not written.
        Assert.Equal(40, main.GainDb);
        Assert.True(check.CanAcceptGain);
        Assert.Null(check.SuggestBlocked);

        // The gain lock holds it back, and says so.
        state["state"]!["gainLocked"] = true;
        CaptureInputWindowTests.Apply(main, state);
        Assert.False(check.CanAcceptGain);
        Assert.Equal(Localizer.Text("MicSetupSuggestLocked"), check.SuggestBlocked);
        check.AcceptGain();
        Assert.Equal(40, main.GainDb);
        state["state"]!["gainLocked"] = false;
        CaptureInputWindowTests.Apply(main, state);

        check.AcceptGain();
        Assert.Equal(50, main.GainDb);
        Assert.Null(check.Suggestion);
        Assert.Equal(Localizer.Format("MicSetupSuggestSet", 50), check.SuggestAdvice);
        Assert.True(check.CanDiscardGain);
        check.DiscardGain();
        Assert.Null(check.SuggestAdvice);

        // A gain moved after the measurement is not overwritten.
        check.StartGainMeasure();
        for (int i = 0; i < 75; i++) Meter(check, time, -35, -25);
        Assert.Equal(65, check.Suggestion!.ToDb);
        main.GainDb = 45;
        check.AcceptGain();
        Assert.Equal(45, main.GainDb);
        Assert.Equal(Localizer.Text("MicSetupSuggestStale"), check.SuggestAdvice);

        // Or while listening.
        check.StartGainMeasure();
        Meter(check, time, -35, -25);
        main.GainDb = 47;
        for (int i = 0; i < 75; i++) Meter(check, time, -35, -25);
        Assert.Null(check.Suggestion);
        Assert.Equal(Localizer.Text("MicSetupSuggestStale"), check.SuggestAdvice);

        // With no reading at all, the window's clock ends it.
        check.StartGainMeasure();
        time.Advance(TimeSpan.FromSeconds(4));
        check.MeasureTick();
        Assert.Equal(Localizer.Format("MicSetupSuggestListening", 1.0), check.SuggestAdvice);
        time.Advance(TimeSpan.FromSeconds(1));
        check.MeasureTick();
        Assert.False(check.Measuring);
        Assert.Equal(GainOutcome.TooFewReadings, check.Suggestion!.Outcome);
        Assert.Null(check.SuggestMeasured);
        Assert.Equal(Localizer.Text("MicSetupSuggestTooFew"), check.SuggestAdvice);

        // A closed window drops the measurement, and a lost connection the suggestion.
        check.StartGainMeasure();
        check.CancelGainMeasure();
        Assert.False(check.Measuring);
        check.StartGainMeasure();
        for (int i = 0; i < 75; i++) Meter(check, time, -35, -25);
        Assert.NotNull(check.Suggestion);
        check.Reset();
        Assert.Null(check.Suggestion);
        Assert.Null(check.SuggestAdvice);

        // A microphone OpenXLR sets no gain for: the change is said, with no Accept.
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingModelTests.ProState(InputBindingModelTests.BoundXlr1(true)))!);
        Assert.Null(check.Unit);
        check.StartGainMeasure();
        for (int i = 0; i < 75; i++) Meter(check, time, -30, -18);
        Assert.Equal(GainOutcome.Elsewhere, check.Suggestion!.Outcome);
        Assert.Equal(Localizer.Format("MicSetupSuggestElsewhere", 10.0), check.SuggestAdvice);
        Assert.False(check.CanAcceptGain);
        Assert.Null(check.SuggestBlocked);
    }
}
