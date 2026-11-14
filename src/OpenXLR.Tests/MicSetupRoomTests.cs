using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Measure room noise's threshold as a pure function: the loud end of the
/// room's peaks, the margin over it, whole dB inside the expander's range,
/// and the outcomes that offer no threshold or a capped one.
/// </summary>
public sealed class MicSetupRoomTests
{
    /// <summary>The expander's threshold range in software and on a unit.</summary>
    private const double Min = -80, Max = 0, UnitMin = -60, UnitMax = -1;

    /// <summary>The meter before the chain, and the expander's own meter.</summary>
    private const double InputFloor = -60, StageFloor = -120;

    private static List<double> Steady(double db, int count = 75) => [.. Enumerable.Repeat(db, count)];

    [Fact]
    public void AQuietSteadyRoomGetsAThresholdFourDbOverItsLoudEnd()
    {
        RoomSuggestion s = RoomSuggestion.From(Steady(-50), InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.Set, s.Outcome);
        Assert.Equal(-50, s.NoiseDb);
        Assert.Equal(0, s.SpreadDb);
        Assert.Equal(-46, s.ThresholdDb);
        Assert.True(s.HasThreshold);
        Assert.Equal(75, s.Readings);
        Assert.Equal(MicSetupRoomLevels.MarginDb, -46 - -50);

        // Whole dB, rounded up so the margin is never less than 4 dB.
        Assert.Equal(-46, RoomSuggestion.From(Steady(-50.3), InputFloor, -20, Min, Max).ThresholdDb);
        Assert.Equal(-46, RoomSuggestion.From(Steady(-50.0000000001), InputFloor, -20, Min, Max).ThresholdDb);
        Assert.Equal(-45, RoomSuggestion.From(Steady(-49.6), InputFloor, -20, Min, Max).ThresholdDb);
    }

    [Fact]
    public void TheNoiseIsTheNinetyFifthPercentileSoALoneClickDoesNotDecide()
    {
        // 72 readings at -50 and three clicks at -30: 4 percent, under the 5 the noise figure leaves out.
        List<double> readings = [.. Steady(-50, 72), -30, -30, -30];
        RoomSuggestion s = RoomSuggestion.From(readings, InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.Set, s.Outcome);
        Assert.Equal(-50, s.NoiseDb);
        Assert.Equal(-46, s.ThresholdDb);
        // A room that varies by a few dB takes its loud end.
        List<double> fan = [.. Enumerable.Range(0, 75).Select(i => -52 + i % 5)];
        RoomSuggestion f = RoomSuggestion.From(fan, InputFloor, -20, Min, Max);
        Assert.Equal(-48, f.NoiseDb);
        Assert.Equal(4, f.SpreadDb);
        Assert.Equal(-44, f.ThresholdDb);
    }

    [Fact]
    public void TalkOrALoudSoundAsksForAnotherMeasurement()
    {
        // A second of talk at speech level in five seconds of room.
        List<double> talk = [.. Steady(-52, 60), .. Steady(-15, 15)];
        RoomSuggestion s = RoomSuggestion.From(talk, InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.Talking, s.Outcome);
        Assert.Null(s.ThresholdDb);
        Assert.False(s.HasThreshold);
        Assert.True(s.SpreadDb > MicSetupRoomLevels.SpreadDb);
        // A door, and the meter falling after it at 20 dB a second.
        List<double> door = [.. Steady(-55, 40), .. Enumerable.Range(0, 35).Select(i => -6 - i * 20.0 / 15)];
        Assert.Equal(RoomOutcome.Talking, RoomSuggestion.From(door, InputFloor, -20, Min, Max).Outcome);
        // Under -60 dBFS a wide spread is noise removal's leftovers, not a voice.
        List<double> cleaned = [.. Enumerable.Range(0, 75).Select(i => i % 2 == 0 ? -95.0 : -70)];
        RoomSuggestion c = RoomSuggestion.From(cleaned, StageFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.QuietRoom, c.Outcome);
        Assert.Equal(-66, c.ThresholdDb);
    }

    [Fact]
    public void SilenceAndAMeterAtItsFloorOfferNothingWithoutAVoice()
    {
        // Digital silence on the expander's own meter: nothing reaches it.
        RoomSuggestion silent = RoomSuggestion.From(Steady(-120), StageFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.NotHeard, silent.Outcome);
        Assert.False(silent.AtFloor);
        Assert.Null(silent.ThresholdDb);
        // The meter before the chain stops at -60 dBFS: with no voice measured,
        // a reading there may be a quiet room or no microphone at all.
        RoomSuggestion floor = RoomSuggestion.From(Steady(-60), InputFloor, null, Min, Max);
        Assert.Equal(RoomOutcome.NotHeard, floor.Outcome);
        Assert.True(floor.AtFloor);
        // With a voice measured the microphone works, so the room is very quiet.
        RoomSuggestion quiet = RoomSuggestion.From(Steady(-60), InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.QuietRoom, quiet.Outcome);
        Assert.True(quiet.AtFloor);
        Assert.Equal(-56, quiet.ThresholdDb);
        // A reading that is no number counts as the floor.
        List<double> odd = [.. Steady(-50, 74), double.NaN];
        Assert.Equal(-46, RoomSuggestion.From(odd, InputFloor, -20, Min, Max).ThresholdDb);
    }

    [Fact]
    public void TheThresholdStaysInsideTheExpandersRange()
    {
        // A very quiet room on the expander's own meter, in software and on a unit.
        RoomSuggestion soft = RoomSuggestion.From(Steady(-90), StageFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.QuietRoom, soft.Outcome);
        Assert.Equal(-80, soft.ThresholdDb);
        RoomSuggestion unit = RoomSuggestion.From(Steady(-70), StageFloor, -20, UnitMin, UnitMax);
        Assert.Equal(-60, unit.ThresholdDb);
        Assert.Equal(-66, RoomSuggestion.From(Steady(-70), StageFloor, -20, Min, Max).ThresholdDb);
    }

    [Fact]
    public void ALoudRoomGetsTheHighestSafeThresholdUnderTheVoice()
    {
        // Noise peaks at -38 with the voice at -20: -34 would sit within 15 dB of the voice.
        RoomSuggestion s = RoomSuggestion.From(Steady(-38), InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.LoudRoom, s.Outcome);
        Assert.Equal((int)(-20 - MicSetupRoomLevels.SpeechGapDb), s.ThresholdDb);
        // A quieter voice lowers that ceiling with it.
        RoomSuggestion lower = RoomSuggestion.From(Steady(-46), InputFloor, -30, Min, Max);
        Assert.Equal(RoomOutcome.LoudRoom, lower.Outcome);
        Assert.Equal(-45, lower.ThresholdDb);
        Assert.Equal(-30, lower.SpeechDb);
        // With no voice measured, the level Suggest gain aims for stands in.
        RoomSuggestion assumed = RoomSuggestion.From(Steady(-38), InputFloor, null, Min, Max);
        Assert.Equal(RoomOutcome.LoudRoom, assumed.Outcome);
        Assert.Equal((int)(MicSetupLevels.TargetSpeechDb - MicSetupRoomLevels.SpeechGapDb), assumed.ThresholdDb);
        Assert.Null(assumed.SpeechDb);
        // Just under the ceiling is still an ordinary threshold.
        Assert.Equal(RoomOutcome.Set, RoomSuggestion.From(Steady(-39), InputFloor, -20, Min, Max).Outcome);
    }

    [Fact]
    public void AVoiceUnderTheRangesBottomOffersNoThreshold()
    {
        // A unit's expander stops at -60 dB, 15 dB under a voice at -50 would be -65.
        RoomSuggestion s = RoomSuggestion.From(Steady(-80), StageFloor, -50, UnitMin, UnitMax);
        Assert.Equal(RoomOutcome.VoiceTooQuiet, s.Outcome);
        Assert.Null(s.ThresholdDb);
        // In software the range reaches lower, so the same voice gets one.
        Assert.Equal(-76, RoomSuggestion.From(Steady(-80), StageFloor, -50, Min, Max).ThresholdDb);
    }

    [Fact]
    public void TooFewReadingsAreNotJudged()
    {
        RoomSuggestion s = RoomSuggestion.From(Steady(-50, MicSetupLevels.MinReadings - 1), InputFloor, -20, Min, Max);
        Assert.Equal(RoomOutcome.TooFewReadings, s.Outcome);
        Assert.Null(s.ThresholdDb);
        Assert.Equal(RoomOutcome.Set, RoomSuggestion.From(Steady(-50, MicSetupLevels.MinReadings), InputFloor, -20, Min, Max).Outcome);
    }
}

/// <summary>
/// Measure room noise in Mic setup with no window: it reads the expander's
/// own meter when the daemon sends it and the meter before the chain
/// otherwise, writes nothing until applied, applies through the expander's
/// own model, and gives way to a session, a gain change and a unit's expander.
/// </summary>
[Collection("xdg-config")]
public sealed class MicSetupRoomModelTests : IDisposable
{
    public MicSetupRoomModelTests() => SliderSync.ReleaseTouchGuards();

    public void Dispose() => SliderSync.ReleaseTouchGuards();

    private sealed class Recorder : IDynamicsCommands, INoiseCommands
    {
        public readonly ConcurrentQueue<(string Processor, string Field, object Value)> Sent = new();

        public Task SetDynamicsAsync(string processor, string channel, string field, object value)
        {
            Sent.Enqueue((processor, field, value));
            return Task.CompletedTask;
        }

        public Task WatchDynamicsAsync(string channel) => Task.CompletedTask;
        public Task WatchInputMeterAsync(string channel) => Task.CompletedTask;
        public Task SetDynamicsProcessingAsync(string channel, string value) => Task.CompletedTask;
        public Task SetNoiseRemovalAsync(string channel, bool? enabled = null, string? engine = null, double? strength = null) => Task.CompletedTask;
    }

    private static JsonNode Expander(bool enabled = false, string? backend = "software", bool? levels = null, string name = "Wave:3 MK.2")
    {
        var node = new JsonObject
        {
            ["enabled"] = enabled, ["threshold"] = -50, ["ratio"] = 3, ["range"] = 24, ["attack"] = 1, ["release"] = 150, ["hold"] = 50,
            ["available"] = true, ["error"] = null, ["backend"] = backend, ["processing"] = backend ?? "device",
        };
        if (backend == "device")
            node["device"] = new JsonObject { ["unit"] = "0fd9:00ca@1", ["input"] = 1, ["name"] = name, ["ready"] = true, ["levels"] = levels };
        return node;
    }

    private static JsonNode Compressor(bool enabled = false, double makeup = 0, string? backend = "software")
    {
        var node = new JsonObject
        {
            ["enabled"] = enabled, ["threshold"] = -18, ["ratio"] = 3, ["attack"] = 5, ["release"] = 150, ["knee"] = 6, ["makeup"] = makeup,
            ["available"] = true, ["error"] = null, ["backend"] = backend, ["processing"] = backend ?? "device",
        };
        if (backend == "device")
            node["device"] = new JsonObject { ["unit"] = "0fd9:00ca@1", ["input"] = 1, ["name"] = "Wave:3 MK.2", ["ready"] = true, ["levels"] = false };
        return node;
    }

    private static void Tick(ManualTime time) => time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));

    /// <summary>Five seconds of the meter before the chain at this peak, and, when given, of the expander's own input.</summary>
    private static void Room(MicSetupViewModel setup, ManualTime time, double peakDb, double? stageDb = null)
    {
        for (int i = 0; i < 80 && setup.MeasuringRoom; i++)
        {
            Tick(time);
            if (stageDb is double stage) setup.ApplyExpanderLevel(new JsonArray(stage, 0.0, stage));
            setup.ApplyInputLevel(new JsonArray(0.1, (peakDb + 60) / 60, 0));
        }
        Assert.False(setup.MeasuringRoom);
    }

    private static (MicSetupViewModel Setup, ChannelDynamicsViewModel Dynamics, ManualTime Time) Setup(DaemonClient client, Recorder sent)
    {
        var time = new ManualTime();
        var dynamics = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", time);
        dynamics.Apply(Compressor(), Expander());
        var setup = new MicSetupViewModel(client, "xlr1") { Time = time, Dynamics = dynamics };
        return (setup, dynamics, time);
    }

    [Fact]
    public async Task AMeasurementWritesNothingUntilAppliedThenTheThresholdGoesBeforeTheSwitch()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        TestUi.Run(() =>
        {
            var sent = new Recorder();
            (MicSetupViewModel setup, ChannelDynamicsViewModel dynamics, ManualTime time) = Setup(client, sent);
            Assert.Same(dynamics.Expander, setup.Expander);
            Assert.True(setup.HasExpander && setup.CanMeasureRoom);
            Assert.Null(setup.RoomBlocked);

            setup.StartRoomMeasure();
            Assert.True(setup.MeasuringRoom);
            Assert.False(setup.CanMeasureRoom || setup.CanSuggestGain);
            Assert.Equal(Localizer.Format("MicSetupRoomListening", 5.0), setup.RoomAdvice);
            Room(setup, time, -50);
            Assert.Equal(RoomOutcome.Set, setup.RoomSuggestion!.Outcome);
            Assert.Equal(-46, setup.RoomSuggestion.ThresholdDb);
            Assert.Equal(Localizer.Format("MicSetupRoomMeasured", -50.0), setup.RoomMeasured);
            // No voice measured: the advice says what the threshold was checked against.
            Assert.Equal(Localizer.Format("MicSetupRoomSet", -46) + " " + Localizer.Text("MicSetupRoomNoVoice"), setup.RoomAdvice);
            Assert.True(setup.CanApplyRoom && setup.CanSuggestGain);
            Assert.Empty(sent.Sent);
            Assert.False(dynamics.Expander.Enabled);

            setup.ApplyRoom();
            Assert.Equal([("expander", "threshold", (object)-46d), ("expander", "enabled", true)], sent.Sent.ToArray());
            Assert.Equal(-46, dynamics.Expander.Threshold.Value);
            Assert.True(dynamics.Expander.Enabled);
            Assert.Null(setup.RoomSuggestion);
            Assert.False(setup.CanApplyRoom);
            Assert.Equal(Localizer.Format("MicSetupRoomApplied", -46), setup.RoomAdvice);
            setup.DiscardRoom();
            Assert.Null(setup.RoomAdvice);
            Assert.False(setup.CanDiscardRoom);

            // Talk during the measurement offers nothing to apply.
            setup.StartRoomMeasure();
            for (int i = 0; i < 80 && setup.MeasuringRoom; i++)
            {
                Tick(time);
                setup.ApplyInputLevel(new JsonArray(0.1, ((i % 4 == 0 ? -12.0 : -52) + 60) / 60, 0));
            }
            Assert.Equal(RoomOutcome.Talking, setup.RoomSuggestion!.Outcome);
            Assert.Equal(Localizer.Text("MicSetupRoomTalking"), setup.RoomAdvice);
            Assert.Null(setup.RoomMeasured);
            Assert.False(setup.CanApplyRoom);

            // With no reading at all, the window's clock ends it.
            setup.StartRoomMeasure();
            time.Advance(MicSetupLevels.MeasureTime);
            setup.MeasureTick();
            Assert.Equal(RoomOutcome.TooFewReadings, setup.RoomSuggestion!.Outcome);
            Assert.Equal(Localizer.Text("MicSetupSuggestTooFew"), setup.RoomAdvice);

            // A closed window drops it.
            setup.StartRoomMeasure();
            setup.CancelMeasures();
            Assert.False(setup.MeasuringRoom);
        });
    }

    [Fact]
    public async Task TheExpandersOwnMeterWinsAndNoiseRemovalChangesMakeItStale()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        TestUi.Run(() =>
        {
            var sent = new Recorder();
            (MicSetupViewModel setup, _, ManualTime time) = Setup(client, sent);
            var noise = new MicNoiseViewModel(sent, "xlr1", "XLR 1", time);
            noise.Apply(JsonNode.Parse("""{"enabled":true,"engine":"rnnoise","strength":100,"available":true,"engines":["rnnoise"]}"""));
            setup.Noise = noise;

            // Noise removal on: the raw microphone reads -40, the expander hears -64 after it.
            setup.StartRoomMeasure();
            Room(setup, time, -40, stageDb: -64);
            Assert.Equal(-64, setup.RoomSuggestion!.NoiseDb);
            Assert.Equal(RoomOutcome.QuietRoom, setup.RoomSuggestion.Outcome);
            Assert.Equal(-60, setup.RoomSuggestion.ThresholdDb);
            Assert.Equal(Localizer.Format("MicSetupRoomMeasured", -64.0), setup.RoomMeasured);

            // Without the expander's meter, the meter before the chain stands in and says it read before noise removal.
            setup.StartRoomMeasure();
            Room(setup, time, -40);
            Assert.Equal(-36, setup.RoomSuggestion!.ThresholdDb);
            Assert.Equal(Localizer.Format("MicSetupRoomMeasuredRaw", -40.0), setup.RoomMeasured);

            // Noise removal changes what the expander hears: the measurement goes.
            noise.Strength = 50;
            Assert.Null(setup.RoomSuggestion);
            Assert.False(setup.CanApplyRoom);
            Assert.Equal(Localizer.Text("MicSetupRoomStale"), setup.RoomAdvice);
        });
    }

    [Fact]
    public async Task ASessionAnywhereBlocksTheMeasurementAndStopsOneUnderWay()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        TestUi.Run(() =>
        {
            (MicSetupViewModel setup, _, ManualTime time) = Setup(client, new Recorder());
            // Another microphone's take plays in the room too.
            setup.Apply(JsonNode.Parse("""{"channel":"xlr2","mode":"looping","seconds":2}"""));
            Assert.False(setup.CanMeasureRoom);
            Assert.Equal(Localizer.Text("MicSetupRoomSession"), setup.RoomBlocked);
            setup.StartRoomMeasure();
            Assert.False(setup.MeasuringRoom);
            setup.Apply(JsonNode.Parse("""{"channel":"xlr2","mode":"idle"}"""));
            Assert.True(setup.CanMeasureRoom);

            setup.StartRoomMeasure();
            Tick(time);
            setup.ApplyInputLevel(new JsonArray(0.1, 0.2, 0));
            setup.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":0.5}"""));
            Assert.False(setup.MeasuringRoom);
            Assert.Null(setup.RoomSuggestion);
            Assert.Equal(Localizer.Text("MicSetupRoomInterrupted"), setup.RoomAdvice);

            // Suggest gain and the room never listen at once.
            setup.Apply(null);
            setup.StartGainMeasure();
            Assert.False(setup.CanMeasureRoom);
            setup.StartRoomMeasure();
            Assert.False(setup.MeasuringRoom);
        });
    }

    [Fact]
    public async Task OnAUnitTheMeterReadsAfterItsExpanderAndCompressor()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        TestUi.Run(() =>
        {
            var sent = new Recorder();
            (MicSetupViewModel setup, ChannelDynamicsViewModel dynamics, ManualTime time) = Setup(client, sent);

            // A unit that reports no level runs the expander, and it is on: the meter reads the room turned down.
            dynamics.Apply(Compressor(backend: "device"), Expander(enabled: true, backend: "device", levels: false));
            Assert.True(dynamics.Expander.OnDevice);
            Assert.False(setup.CanMeasureRoom);
            Assert.Equal(Localizer.Format("MicSetupRoomGated", "Wave:3 MK.2"), setup.RoomBlocked);

            // Off, it measures, and the compressor's make-up gain comes back out of the reading.
            time.Advance(MicDynamicsViewModel.EchoWait);
            dynamics.Apply(Compressor(enabled: true, makeup: 6, backend: "device"), Expander(backend: "device", levels: false));
            Assert.True(setup.CanMeasureRoom);
            setup.StartRoomMeasure();
            Room(setup, time, -44, stageDb: -10);
            Assert.Equal(-50, setup.RoomSuggestion!.NoiseDb, 6);
            Assert.Equal(-46, setup.RoomSuggestion.ThresholdDb);
            setup.ApplyRoom();
            Assert.Equal([("expander", "threshold", (object)-46d), ("expander", "enabled", true)], sent.Sent.ToArray());
            // A later change to the make-up gain changes what the meter means.
            dynamics.Compressor.Makeup!.Value = 3;
            Assert.Equal(Localizer.Text("MicSetupRoomStale"), setup.RoomAdvice);

            // A Wave XLR MK.2 reports its expander's input itself, so the expander may be on.
            time.Advance(MicDynamicsViewModel.EchoWait);
            dynamics.Apply(Compressor(backend: "device"), Expander(enabled: true, backend: "device", levels: true, name: "Wave XLR MK.2"));
            Assert.True(setup.CanMeasureRoom);
            setup.StartRoomMeasure();
            Room(setup, time, -70, stageDb: -55);
            Assert.Equal(-51, setup.RoomSuggestion!.ThresholdDb);
            // Its threshold range is the unit's.
            setup.StartRoomMeasure();
            Room(setup, time, -70, stageDb: -75);
            Assert.Equal(-60, setup.RoomSuggestion!.ThresholdDb);
        });
    }

    [Fact]
    public async Task TheVoiceFromSuggestGainCapsTheThresholdAndAGainChangeMakesItStale()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1))))!;
        state["capabilities"]!["gainMaxDb"] = 80;
        state["state"] = new JsonObject { ["gainDb"] = 40, ["gainLocked"] = false };
        JsonNode channel = state["mixer"]!["channels"]![0]!;
        channel["compressor"] = Compressor();
        channel["expander"] = Expander();
        CaptureInputWindowTests.Apply(main, state);
        MicSetupViewModel setup = main.Inserts.MicSetup;
        var time = new ManualTime();
        setup.Time = time;
        Assert.Same(main.Channels.Single(c => c.Id == "xlr1").Dynamics, setup.Dynamics);

        // The voice at -30 dBFS at 40 dB, and the suggested 50 dB accepted: it is at -20 now.
        setup.StartGainMeasure();
        for (int i = 0; i < 75; i++)
        {
            Tick(time);
            setup.ApplyInputLevel(new JsonArray(0.5, 0.7, 0));
        }
        setup.AcceptGain();
        Assert.Equal(50, main.GainDb);

        // Noise peaks at -38: the threshold stops 15 dB under that voice.
        setup.StartRoomMeasure();
        Room(setup, time, -38);
        Assert.Equal(RoomOutcome.LoudRoom, setup.RoomSuggestion!.Outcome);
        Assert.Equal(-20, setup.RoomSuggestion.SpeechDb);
        Assert.Equal(-35, setup.RoomSuggestion.ThresholdDb);
        Assert.Equal(Localizer.Format("MicSetupRoomLoud", -35), setup.RoomAdvice);

        // A gain change before applying makes the measurement stale.
        main.GainDb = 45;
        Assert.Null(setup.RoomSuggestion);
        Assert.Equal(Localizer.Text("MicSetupRoomStale"), setup.RoomAdvice);

        // At 45 dB the voice is at -25 dBFS, so the ceiling follows it down.
        setup.StartRoomMeasure();
        Room(setup, time, -60);
        Assert.Equal(RoomOutcome.QuietRoom, setup.RoomSuggestion!.Outcome);
        Assert.True(setup.RoomSuggestion.AtFloor);
        Assert.Equal(-25, setup.RoomSuggestion.SpeechDb);
        Assert.Equal(-56, setup.RoomSuggestion.ThresholdDb);
        Assert.Equal(Localizer.Format("MicSetupRoomMeasuredFloor", -60.0), setup.RoomMeasured);
    }
}
