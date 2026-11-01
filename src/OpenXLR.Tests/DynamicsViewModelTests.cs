using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The compressor's and expander's model, with no window: what it offers, the
/// one field each change sends, a fresh press kept against an older report,
/// the levels a meter push carries into the meters and the history, and the
/// line that says why nothing shows.
/// </summary>
[Collection("xdg-config")]
public sealed class DynamicsViewModelTests
{
    private sealed class Recorder : IDynamicsCommands
    {
        public readonly ConcurrentQueue<(string Processor, string Channel, string Field, object Value)> Sent = new();
        public int Watches;

        public Task SetDynamicsAsync(string processor, string channel, string field, object value)
        {
            Sent.Enqueue((processor, channel, field, value));
            return Task.CompletedTask;
        }

        public Task WatchDynamicsAsync(string channel)
        {
            Interlocked.Increment(ref Watches);
            return Task.CompletedTask;
        }

        public int InputWatches;

        public Task WatchInputMeterAsync(string channel)
        {
            Interlocked.Increment(ref InputWatches);
            return Task.CompletedTask;
        }

        public readonly ConcurrentQueue<(string Channel, string Value)> Processing = new();

        public Task SetDynamicsProcessingAsync(string channel, string value)
        {
            Processing.Enqueue((channel, value));
            return Task.CompletedTask;
        }
    }

    private static JsonNode Compressor(bool enabled = false, double threshold = -18, bool available = true, string? error = null)
        => JsonNode.Parse($$"""
            {"enabled":{{(enabled ? "true" : "false")}},"threshold":{{threshold}},"ratio":3,"attack":5,"release":150,"knee":6,"makeup":0,
             "available":{{(available ? "true" : "false")}},"error":{{(error is null ? "null" : $"\"{error}\"")}}}
            """)!;

    private static JsonNode Expander(bool enabled = false, double range = 24)
        => JsonNode.Parse($$"""
            {"enabled":{{(enabled ? "true" : "false")}},"threshold":-50,"ratio":3,"range":{{range}},"attack":1,"release":150,"hold":50,
             "available":true,"error":null}
            """)!;

    [Fact]
    public void EachProcessorOffersItsOwnSettingsInSignalOrder() => TestUi.Run(() =>
    {
        var pair = new ChannelDynamicsViewModel(new Recorder(), "xlr1", "XLR 1", new ManualTime());
        MicDynamicsViewModel compressor = pair.Compressor, expander = pair.Expander;
        Assert.Equal(["ratio", "attack", "release", "knee"], compressor.Advanced.Select(p => p.Field));
        Assert.Equal(["ratio", "range", "attack", "hold", "release"], expander.Advanced.Select(p => p.Field));
        Assert.True(compressor.HasMakeup);
        Assert.False(expander.HasMakeup);
        Assert.Equal(DynamicsGraphStyle.Envelope, compressor.GraphStyle);
        Assert.Equal(DynamicsGraphStyle.Line, expander.GraphStyle);
        Assert.Equal(-60, compressor.FloorDb);
        Assert.Equal(-80, expander.FloorDb);
        Assert.Equal(Localizer.Format("CompressorWindowTitle", "XLR 1"), compressor.Title);
        Assert.Equal(Localizer.Format("DynamicsSubtitle", "XLR 1"), expander.Subtitle);
        Assert.Equal(Localizer.Text("CompressorNote"), compressor.Note);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-18"), compressor.Threshold.Text);
        Assert.Equal(Localizer.Format("DynamicsRatioValue", "3"), compressor.Advanced[0].Text);
        Assert.Equal(Localizer.Format("DynamicsMilliseconds", "50"), expander.Advanced[3].Text);
        pair.ChannelName = "Main mic";
        Assert.Equal(Localizer.Format("ExpanderWindowTitle", "Main mic"), expander.Title);
    });

    [Fact]
    public void AChangeSendsItsOneFieldAndAFreshPressBeatsAnOlderReport() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var time = new ManualTime();
        var pair = new ChannelDynamicsViewModel(sent, "guest", "Guest", time);
        pair.Apply(Compressor(), Expander());
        Assert.False(pair.IsLit);
        Assert.Equal(Localizer.Format("DynamicsButtonNameOff", "Guest"), pair.ButtonName);

        pair.Compressor.Enabled = true;
        Assert.True(sent.Sent.TryDequeue(out var press));
        Assert.Equal(("compressor", "guest", "enabled", (object)true), press);
        // An older report still says off: the switch stays where the user put it.
        pair.Apply(Compressor(enabled: false), Expander());
        Assert.True(pair.Compressor.Enabled);
        Assert.True(pair.IsLit);
        time.Advance(MicDynamicsViewModel.EchoWait);
        pair.Apply(Compressor(enabled: true), Expander());
        Assert.True(pair.Compressor.Enabled);
        Assert.Equal(Localizer.Format("DynamicsButtonNameOn", "Guest"), pair.ButtonName);

        // Sliders send on the trailing edge, snapped and inside the range.
        pair.Expander.Threshold.Value = -44.4;
        pair.Expander.Advanced[1].Value = 200;
        pair.Compressor.Makeup!.Value = 6.2;
        SliderSync.FlushPending();
        Assert.Equal(-44, pair.Expander.Threshold.Value);
        Assert.Equal(80, pair.Expander.Advanced[1].Value);
        var rest = sent.Sent.ToArray();
        Assert.Contains(("expander", "guest", "threshold", (object)-44d), rest);
        Assert.Contains(("expander", "guest", "range", (object)80d), rest);
        Assert.Contains(("compressor", "guest", "makeup", (object)6d), rest);
        Assert.Equal(3, rest.Length);

        // A daemon that cannot run it says so on the switch's button.
        time.Advance(MicDynamicsViewModel.EchoWait);
        pair.Apply(Compressor(enabled: true, error: "no host here"), Expander());
        Assert.True(pair.IsFaulty);
        Assert.Equal("no host here", pair.Compressor.Status);
        Assert.Equal(Localizer.Format("DynamicsButtonNameUnavailable", "Guest"), pair.ButtonName);

        // Detached, nothing more goes out.
        pair.Detach();
        sent.Sent.Clear();
        pair.Compressor.Enabled = false;
        pair.Compressor.Threshold.Value = -30;
        SliderSync.FlushPending();
        Assert.Empty(sent.Sent);
    });

    [Fact]
    public void LevelsFillTheMetersAndTheHistoryAndLapseWhenTheyStop() => TestUi.Run(() =>
    {
        var time = new ManualTime();
        var sent = new Recorder();
        var pair = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", time);
        pair.Apply(Compressor(enabled: true), Expander(enabled: true, range: 40));
        MicDynamicsViewModel expander = pair.Expander;
        Assert.False(expander.HasLevels);
        Assert.Equal(Localizer.Text("DynamicsNoLevels"), expander.Status);
        Assert.Equal(Localizer.Text("DynamicsSilent"), expander.InputText);

        expander.ApplyLevels(new JsonArray(-40.0, 10.0, -50.0));
        Assert.True(expander.HasLevels);
        Assert.Equal(DynamicsLevelSource.Stage, expander.Source);
        // The line says where the processor's own input is measured.
        Assert.Equal(Localizer.Text("ExpanderInputPoint"), expander.Status);
        Assert.False(expander.HasError);
        Assert.Equal(0.5, expander.InputLevel);
        Assert.Equal(0.25, expander.ReductionLevel);   // 10 of a 40 dB range
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-40"), expander.InputText);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "10"), expander.ReductionText);
        Assert.Equal(Localizer.Format("DynamicsInputName", expander.InputText), expander.InputName);
        // Each push adds to the history, below the floor held at the floor.
        expander.ApplyLevels(new JsonArray(-120.0, 40.0, -120.0));
        Assert.Equal(2, expander.History.Count);
        Assert.Equal(-40, expander.History[0]);
        Assert.Equal(-80, expander.History[1]);
        for (int i = 0; i < DynamicsHistory.Capacity + 5; i++) expander.ApplyLevels(new JsonArray(-30.0, 0.0, -30.0));
        Assert.Equal(DynamicsHistory.Capacity, expander.History.Count);
        // A malformed push is ignored.
        expander.ApplyLevels(new JsonArray(-10.0, "x", -10.0));
        Assert.Equal(-30, expander.InputDb);
        // The compressor's reduction meter fills at 30 dB.
        pair.Compressor.ApplyLevels(new JsonArray(-10.0, 15.0, -25.0));
        Assert.Equal(0.5, pair.Compressor.ReductionLevel);

        time.Advance(TimeSpan.FromSeconds(1));
        expander.CheckLevels();
        Assert.True(expander.HasLevels);
        time.Advance(TimeSpan.FromSeconds(1));
        expander.CheckLevels();
        Assert.False(expander.HasLevels);
        Assert.Equal(0, expander.History.Count);
        Assert.Equal(0, expander.InputLevel);
        Assert.Equal(Localizer.Text("DynamicsNoLevels"), expander.Status);

        _ = expander.KeepLevelsAsync();
        Assert.Equal(1, sent.Watches);
    });

    [Fact]
    public void WithoutTheStageTheMeterBeforeTheChainFeedsTheInputAndTheStageTakesOverOnceItRuns() => TestUi.Run(() =>
    {
        var time = new ManualTime();
        var sent = new Recorder();
        var pair = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", time);
        pair.Apply(Compressor(), Expander());
        MicDynamicsViewModel expander = pair.Expander;

        // Nothing in the stage runs: the window asks for both, and the meter before the chain answers.
        _ = expander.KeepLevelsAsync();
        Assert.Equal((1, 1), (sent.Watches, sent.InputWatches));
        expander.ApplyInputLevel(new JsonArray(0.4, 0.5, 0));   // rms, peak, clip on the -60 to 0 dBFS scale
        Assert.Equal(DynamicsLevelSource.Raw, expander.Source);
        Assert.True(expander.HasLevels);
        Assert.Equal(-30, expander.InputDb);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-30"), expander.InputText);
        Assert.Equal(0.625, expander.InputLevel);   // -30 on the expander's -80 dB floor
        Assert.Equal(0, expander.ReductionDb);
        Assert.Equal(0, expander.ReductionLevel);
        Assert.Equal(Localizer.Text("DynamicsNoReading"), expander.OutputText);
        Assert.Equal(0, expander.OutputLevel);
        Assert.Equal(Localizer.Text("DynamicsRawInput"), expander.Status);
        Assert.Equal([-30d], Enumerable.Range(0, expander.History.Count).Select(i => expander.History[i]));
        // Silence on the meter reads as silence, held at the graph's floor.
        expander.ApplyInputLevel(new JsonArray(0, 0, 0));
        Assert.Equal(Localizer.Text("DynamicsSilent"), expander.InputText);
        Assert.Equal(-80, expander.History[1]);

        // The stage starts: its own levels win, the history starts again, and the
        // meter before the chain is no longer asked for or taken.
        expander.ApplyLevels(new JsonArray(-35.0, 6.0, -41.0));
        Assert.Equal(DynamicsLevelSource.Stage, expander.Source);
        Assert.Equal(1, expander.History.Count);
        expander.ApplyInputLevel(new JsonArray(0.9, 0.9, 0));
        Assert.Equal(-35, expander.InputDb);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-41"), expander.OutputText);
        Assert.Equal(Localizer.Text("ExpanderInputPoint"), expander.Status);
        pair.Compressor.ApplyLevels(new JsonArray(-20.0, 3.0, -23.0));
        Assert.Equal(Localizer.Text("CompressorInputPoint"), pair.Compressor.Status);
        _ = expander.KeepLevelsAsync();
        Assert.Equal((2, 1), (sent.Watches, sent.InputWatches));

        // The stage stops reporting: after the wait the raw meter feeds the window again.
        time.Advance(MicDynamicsViewModel.LevelsWait);
        expander.ApplyInputLevel(new JsonArray(0.4, 0.5, 0));
        Assert.Equal(DynamicsLevelSource.Raw, expander.Source);
        Assert.Equal([-30d], Enumerable.Range(0, expander.History.Count).Select(i => expander.History[i]));
        _ = expander.KeepLevelsAsync();
        Assert.Equal((3, 2), (sent.Watches, sent.InputWatches));
    });

    /// <summary>A processor's state with where it runs: the backend, the choice and the unit the channel carries.</summary>
    private static JsonNode OnUnit(JsonNode processor, string? backend, string processing = "device", bool ready = true,
        string? reason = null, string name = "Wave:3 MK.2")
    {
        JsonNode copy = JsonNode.Parse(processor.ToJsonString())!;
        copy["backend"] = backend;
        copy["processing"] = processing;
        copy["device"] = new JsonObject
        {
            ["unit"] = "0fd9:00ca@1", ["input"] = 1, ["name"] = name, ["firmware"] = ready ? "1.0.1" : "0.2.2", ["ready"] = ready,
            ["reason"] = reason, ["note"] = null, ["minimumFirmware"] = "0.2.3",
        };
        return copy;
    }

    [Fact]
    public void OnTheUnitTheControlsKeepToItsLimitsAndTheSwitchSendsTheChoice() => TestUi.Run(() =>
    {
        var time = new ManualTime();
        var sent = new Recorder();
        var pair = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", time);
        MicDynamicsViewModel compressor = pair.Compressor, expander = pair.Expander;
        // A channel on no such unit offers no choice and keeps the software ranges.
        pair.Apply(Compressor(), Expander());
        Assert.False(compressor.HasProcessingChoice);
        Assert.False(compressor.OnDevice);
        Assert.Equal((-60, 0, 1), (compressor.Threshold.Minimum, compressor.Threshold.Maximum, compressor.Threshold.Step));
        Assert.Equal(Localizer.Text("DynamicsHeading"), pair.Heading);

        // The unit runs them: its limits and steps, no knee and no hold, its own words.
        pair.Apply(OnUnit(Compressor(enabled: true, threshold: -12), "device"), OnUnit(Expander(), "device"));
        Assert.True(compressor.OnDevice);
        Assert.True(compressor.HasProcessingChoice);
        Assert.True(compressor.ProcessOnDevice);
        Assert.False(compressor.ProcessInSoftware);
        Assert.True(compressor.IsLit);
        Assert.Equal((-60, -1, 1), (compressor.Threshold.Minimum, compressor.Threshold.Maximum, compressor.Threshold.Step));
        Assert.Equal((1, 20, 1), (compressor.Advanced[0].Minimum, compressor.Advanced[0].Maximum, compressor.Advanced[0].Step));
        Assert.Equal((1, 200), (compressor.Advanced[1].Minimum, compressor.Advanced[1].Maximum));     // attack: the software's 200 ms within the unit's 300
        Assert.Equal((50, 1000), (compressor.Advanced[2].Minimum, compressor.Advanced[2].Maximum));   // release
        Assert.Equal((0, 20, 1), (compressor.Makeup!.Minimum, compressor.Makeup.Maximum, compressor.Makeup.Step));
        Assert.Equal([true, true, true, false], compressor.Advanced.Select(p => p.Shown));            // no knee
        Assert.Equal((1, 3, 0.1), (expander.Advanced[0].Minimum, expander.Advanced[0].Maximum, expander.Advanced[0].Step));
        Assert.Equal((0, 60), (expander.Advanced[1].Minimum, expander.Advanced[1].Maximum));          // range
        Assert.Equal([true, true, true, false, true], expander.Advanced.Select(p => p.Shown));       // no hold
        Assert.Equal(-60, expander.FloorDb);
        Assert.Equal(Localizer.Format("DynamicsSubtitleDevice", "XLR 1", "Wave:3 MK.2"), compressor.Subtitle);
        Assert.Equal(Localizer.Format("CompressorNoteDevice", "Wave:3 MK.2"), compressor.Note);
        Assert.Equal(Localizer.Format("ExpanderNoteDevice", "Wave:3 MK.2"), expander.Note);
        Assert.Equal(Localizer.Format("DynamicsHeadingDevice", "Wave:3 MK.2"), pair.Heading);
        Assert.Equal(Localizer.Format("DynamicsHintDevice", "Wave:3 MK.2"), pair.Hint);
        Assert.Equal(Localizer.Format("DynamicsUnitLevels", "Wave:3 MK.2"), compressor.Status);
        Assert.False(compressor.ShowsStageMeters);
        Assert.Null(compressor.DeviceNote);
        // A drag past the unit's limit stops at it, on the unit's step.
        compressor.Advanced[0].Value = 3.4;
        Assert.Equal(3, compressor.Advanced[0].Value);
        compressor.Threshold.Value = 0;
        Assert.Equal(-1, compressor.Threshold.Value);

        // The unit reports no input or reduction: the meter before the chain is
        // the microphone leaving the unit, so it is the output and the history.
        _ = compressor.KeepLevelsAsync();
        Assert.Equal((0, 1), (sent.Watches, sent.InputWatches));
        compressor.ApplyLevels(new JsonArray(-20.0, 3.0, -23.0));
        Assert.False(compressor.HasLevels);
        compressor.ApplyInputLevel(new JsonArray(0.4, 0.5, 0));
        Assert.Equal(-30, compressor.OutputDb);
        Assert.Equal(Localizer.Format("DynamicsDecibels", "-30"), compressor.OutputText);
        Assert.Equal(0.5, compressor.OutputLevel);
        Assert.Equal(Localizer.Text("DynamicsNoReading"), compressor.InputText);
        Assert.Equal(0, compressor.ReductionLevel);
        Assert.Equal([-30d], Enumerable.Range(0, compressor.History.Count).Select(i => compressor.History[i]));

        // The software side sends the choice for the channel; the unit's side, chosen, sends nothing.
        compressor.ChooseProcessing("device");
        Assert.Empty(sent.Processing);
        expander.ChooseProcessing("software");
        Assert.True(sent.Processing.TryDequeue(out var choice));
        Assert.Equal(("xlr1", "software"), choice);
        Assert.True(expander.ProcessInSoftware);
        // A report from before the press does not undo it.
        pair.Apply(OnUnit(Compressor(enabled: true), "device"), OnUnit(Expander(), "device"));
        Assert.True(expander.ProcessInSoftware);
        time.Advance(MicDynamicsViewModel.EchoWait);
        pair.Apply(OnUnit(Compressor(enabled: true), "software", "software"), OnUnit(Expander(), "software", "software"));
        Assert.False(compressor.OnDevice);
        Assert.True(compressor.ProcessInSoftware);
        Assert.True(compressor.HasProcessingChoice);
        Assert.Equal((-60, 0), (compressor.Threshold.Minimum, compressor.Threshold.Maximum));
        Assert.Equal([true, true, true, true], compressor.Advanced.Select(p => p.Shown));
        Assert.True(compressor.ShowsStageMeters);
        Assert.Equal(0, compressor.History.Count);   // the levels start again on the new side
        Assert.Null(compressor.DeviceNote);           // software by choice: the switch says so
    });

    [Fact]
    public void AUnitThatCannotTakeThemSaysWhyAndOffersOnlySoftware() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var pair = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", new ManualTime());
        MicDynamicsViewModel compressor = pair.Compressor;
        pair.Apply(OnUnit(Compressor(), null, ready: false, reason: "firmwareOld"), OnUnit(Expander(), null, ready: false, reason: "firmwareOld"));
        Assert.True(compressor.HasProcessingChoice);
        Assert.False(compressor.CanProcessOnDevice);
        Assert.False(compressor.ProcessOnDevice);
        Assert.True(compressor.ProcessInSoftware);
        Assert.Equal(Localizer.Format("DynamicsDeviceFirmwareOld", "Wave:3 MK.2", "0.2.2", "0.2.3"), compressor.DeviceNote);
        Assert.Equal(compressor.DeviceNote, compressor.ProcessOnDeviceTip);
        compressor.ChooseProcessing("device");
        Assert.Empty(sent.Processing);
        pair.Apply(OnUnit(Compressor(), "device", ready: false, reason: "failed"), OnUnit(Expander(), null, ready: false, reason: "failed"));
        Assert.Equal(Localizer.Format("DynamicsDeviceFailed", "Wave:3 MK.2"), pair.Expander.DeviceNote);
        pair.Apply(OnUnit(Compressor(), null, ready: false, reason: "unsupportedBlock"), OnUnit(Expander(), null, ready: false, reason: "firmwareUnknown"));
        Assert.Equal(Localizer.Format("DynamicsDeviceUnsupported", "Wave:3 MK.2"), compressor.DeviceNote);
        Assert.Equal(Localizer.Format("DynamicsDeviceFirmwareUnknown", "Wave:3 MK.2", "0.2.3"), pair.Expander.DeviceNote);
        // A ready unit's side names it.
        var ready = new ChannelDynamicsViewModel(sent, "xlr2", "XLR 2", new ManualTime());
        ready.Apply(OnUnit(Compressor(), "device"), OnUnit(Expander(), "device"));
        Assert.Equal(Localizer.Format("DynamicsProcessingDeviceTip", "Wave:3 MK.2"), ready.Compressor.ProcessOnDeviceTip);
    });
}
