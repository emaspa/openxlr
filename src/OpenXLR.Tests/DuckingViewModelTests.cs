using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The Ducking window's model, with no window: the daemon's setup shows as
/// it is and sends nothing back, each control sends the one field it changes,
/// the key is left out of the channels both in the list and in the command
/// that picks it, a daemon from before the per-channel setup keeps the switch
/// and the presets, an older echo does not undo an edit, a refused edit puts
/// the daemon's setup back, the "Runs on" choice follows the unit, the note
/// and the line that says what runs it and where it sits in the signal, and
/// the reduction bar follows the meters.
/// </summary>
[Collection("xdg-config")]
public sealed class DuckingViewModelTests
{
    private sealed class Recorder : IDuckingCommands
    {
        public readonly ConcurrentQueue<(string Command, JsonNode Value)> Sent = new();
        public string? Refuse;

        public Task<string?> SetDuckingAsync(IReadOnlyDictionary<string, object> fields)
        {
            Sent.Enqueue(("setDucking", JsonSerializer.SerializeToNode(fields)!));
            return Task.FromResult(Refuse);
        }

        public Task<string?> SetDuckingProcessingAsync(string value)
        {
            Sent.Enqueue(("setDuckingProcessing", JsonValue.Create(value)));
            return Task.FromResult(Refuse);
        }

        /// <summary>The one setDucking sent since the last look, as its fields.</summary>
        public JsonObject One()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Equal("setDucking", sent.Command);
            Assert.Empty(Sent);
            return sent.Value.AsObject();
        }
    }

    /// <summary>The default layout's nine channels, a capture microphone in place of SFX, and its five mixes.</summary>
    internal static JsonObject Mixer(bool microphoneField = true, bool xlr2Present = true)
    {
        string Mic(bool on) => microphoneField ? $",\"microphone\":{(on ? "true" : "false")}" : "";
        return JsonNode.Parse($$"""
        {"channels":[
          {"id":"xlr1","name":"XLR 1","hardware":true,"present":true{{Mic(true)}}},
          {"id":"xlr2","name":"XLR 2","hardware":true,"present":{{(xlr2Present ? "true" : "false")}}{{Mic(true)}}},
          {"id":"aux","name":"Aux In","hardware":true,"present":true{{Mic(false)}}},
          {"id":"game","name":"Game"{{Mic(false)}}},
          {"id":"music","name":"Music"{{Mic(false)}}},
          {"id":"browser","name":"Browser"{{Mic(false)}}},
          {"id":"system","name":"System"{{Mic(false)}}},
          {"id":"voicechat","name":"Voice Chat"{{Mic(false)}}},
          {"id":"headset","name":"Headset","captureSource":"usb-headset","captureMonoChannel":0{{Mic(true)}}}],
         "mixes":[
          {"id":"monitor","name":"Monitor A","kind":"monitor"},
          {"id":"monitor2","name":"Monitor B","kind":"monitor"},
          {"id":"stream","name":"Stream","kind":"virtualMic"},
          {"id":"chat","name":"Chat","kind":"virtualMic"},
          {"id":"auxout","name":"Aux","kind":"auxPort"}]}
        """)!.AsObject();
    }

    /// <summary>A ducking state; <paramref name="device"/> is the <c>device</c> object's JSON, or null.</summary>
    internal static JsonObject Ducking(bool enabled = false, string key = "xlr1", string? channels = "\"game\",\"music\",\"browser\",\"system\",\"voicechat\"",
        string? mixes = "\"monitor\"", string backend = "null", string processing = "device", string? device = null,
        bool available = true, string? error = null) => JsonNode.Parse($$"""
        {"enabled":{{(enabled ? "true" : "false")}},"key":"{{key}}",
         {{(channels is null ? "" : $"\"channels\":[{channels}],")}}{{(mixes is null ? "" : $"\"mixes\":[{mixes}],")}}
         "sensitivity":"medium","amount":"high","speed":"fast","processing":"{{processing}}","backend":{{backend}},
         "available":{{(available ? "true" : "false")}},"error":{{(error is null ? "null" : $"\"{error}\"")}},
         "device":{{device ?? "null"}}}
        """)!.AsObject();

    internal static string Pro(bool ready = true, string? reason = null, string? note = null, bool held = true) =>
        $$"""{"unit":"0fd9:00b4@1","name":"Wave XLR Pro","firmware":null,"ready":{{(ready ? "true" : "false")}},"reason":{{(reason is null ? "null" : $"\"{reason}\"")}},"note":{{(note is null ? "null" : $"\"{note}\"")}},"outputs":["monitor"],"held":{{(held ? "true" : "false")}}}""";

    private static (DuckingViewModel Model, Recorder Sent, ManualTime Time) Model(JsonNode? ducking = null, JsonNode? mixer = null)
    {
        var sent = new Recorder();
        var time = new ManualTime();
        var model = new DuckingViewModel(sent, time);
        model.Apply(ducking ?? Ducking(), mixer ?? Mixer());
        return (model, sent, time);
    }

    private static string[] Ids(JsonNode? node) => [.. node!.AsArray().Select(n => n!.GetValue<string>())];

    [Fact]
    public void TheDaemonsSetupShowsAsItIsAndSendsNothingBack()
    {
        var (model, sent, _) = Model(Ducking(enabled: true, channels: "\"music\",\"xlr2\"", mixes: "\"monitor\",\"stream\""));

        Assert.True(model.IsReported);
        Assert.True(model.CanEdit);
        Assert.True(model.Enabled);
        Assert.True(model.HasTargets);
        // Every microphone by name, the capture input on one mono port too; never Aux In or an application channel.
        Assert.Equal(["xlr1", "xlr2", "headset"], model.Keys.Select(k => k.Id));
        Assert.Equal(["XLR 1", "XLR 2", "Headset"], model.Keys.Select(k => k.Name));
        Assert.Equal("xlr1", model.SelectedKey?.Id);
        // Every channel but the key, in layout order.
        Assert.Equal(["xlr2", "aux", "game", "music", "browser", "system", "voicechat", "headset"], model.Channels.Select(c => c.Id));
        Assert.Equal(["xlr2", "music"], model.Channels.Where(c => c.IsChecked).Select(c => c.Id));
        Assert.Equal(["monitor", "monitor2", "stream", "chat", "auxout"], model.Mixes.Select(m => m.Id));
        Assert.Equal(["Monitor A", "Monitor B", "Stream", "Chat", "Aux"], model.Mixes.Select(m => m.Name));
        Assert.Equal(["monitor", "stream"], model.Mixes.Where(m => m.IsChecked).Select(m => m.Id));
        // Preset words show as their numbers on the sliders.
        Assert.Equal((-12, 24, 30, 200), (model.Sensitivity.Value, model.Amount.Value, model.Attack.Value, model.Release.Value));
        Assert.Equal((Localizer.Format("DuckingDbfs", "-12"), Localizer.Format("DynamicsDecibels", "24"), Localizer.Format("DynamicsMilliseconds", "200")),
            (model.Sensitivity.Text, model.Amount.Text, model.Release.Text));
        // No unit with hardware ducking: no choice of where it runs and no badge.
        Assert.False(model.HasProcessingChoice);
        Assert.False(model.OnDevice);
        Assert.Empty(sent.Sent);

        // A second push with the same setup keeps the same rows and sends nothing.
        DuckingChoice music = model.Channels.Single(c => c.Id == "music");
        model.Apply(Ducking(enabled: true, channels: "\"music\",\"xlr2\"", mixes: "\"monitor\",\"stream\""), Mixer());
        Assert.Same(music, model.Channels.Single(c => c.Id == "music"));
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void EachControlSendsTheOneFieldItChanges()
    {
        var (model, sent, _) = Model();

        model.Enabled = true;
        Assert.Equal("""{"enabled":true}""", sent.One().ToJsonString());

        // A tick sends the whole list, in layout order.
        model.Channels.Single(c => c.Id == "aux").IsChecked = true;
        JsonObject channels = sent.One();
        Assert.Equal(["channels"], channels.Select(f => f.Key));
        Assert.Equal(["aux", "game", "music", "browser", "system", "voicechat"], Ids(channels["channels"]));
        model.Channels.Single(c => c.Id == "music").IsChecked = false;
        Assert.Equal(["aux", "game", "browser", "system", "voicechat"], Ids(sent.One()["channels"]));

        model.Mixes.Single(m => m.Id == "stream").IsChecked = true;
        JsonObject mixes = sent.One();
        Assert.Equal(["mixes"], mixes.Select(f => f.Key));
        Assert.Equal(["monitor", "stream"], Ids(mixes["mixes"]));

        // The sliders send numbers; the speed sends both its times.
        model.Sensitivity.Choose(model.Sensitivity.Marks[2]);
        Assert.Equal("""{"sensitivity":-24}""", sent.One().ToJsonString());
        model.Amount.Step(-1, large: false);
        Assert.Equal("""{"amount":23}""", sent.One().ToJsonString());
        model.Release.Choose(model.Release.Marks[0]);
        Assert.Equal("""{"speed":{"attack":30,"release":2000}}""", sent.One().ToJsonString());

        // The two buttons: every application channel and nothing else, then none.
        model.ChooseNoChannels();
        Assert.Empty(Ids(sent.One()["channels"]));
        Assert.All(model.Channels, c => Assert.False(c.IsChecked));
        model.ChooseAllApplications();
        Assert.Equal(["game", "music", "browser", "system", "voicechat"], Ids(sent.One()["channels"]));
        Assert.Equal(["game", "music", "browser", "system", "voicechat"], model.Channels.Where(c => c.IsChecked).Select(c => c.Id));
        // The same choice again sends nothing.
        model.ChooseAllApplications();
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void TheKeyIsLeftOutOfTheChannelsInTheListAndInTheCommandThatPicksIt()
    {
        var (model, sent, _) = Model(Ducking(channels: "\"xlr2\",\"music\""));
        Assert.DoesNotContain(model.Channels, c => c.Id == "xlr1");
        DuckingChoice[] stale = [.. model.Channels.Where(c => c.Id == "xlr2")];

        model.SelectedKey = model.Keys.Single(k => k.Id == "xlr2");
        JsonObject picked = sent.One();
        Assert.Equal("xlr2", picked["key"]!.GetValue<string>());
        Assert.Equal(["music"], Ids(picked["channels"]));
        // XLR 1 is a channel again, unticked; XLR 2 left the list.
        Assert.Equal(["xlr1", "aux", "game", "music", "browser", "system", "voicechat", "headset"], model.Channels.Select(c => c.Id));
        Assert.False(model.Channels.Single(c => c.Id == "xlr1").IsChecked);

        // A row the new key replaced, written back as its list lets it go, sends nothing.
        DuckingChoice gone = Assert.Single(stale);
        gone.IsChecked = !gone.IsChecked;
        Assert.Empty(sent.Sent);

        // A key that is not among the channels goes alone.
        model.SelectedKey = model.Keys.Single(k => k.Id == "headset");
        Assert.Equal("""{"key":"headset"}""", sent.One().ToJsonString());

        // A list being refilled clears its selection: that is not a choice.
        model.SelectedKey = null;
        Assert.Empty(sent.Sent);
        Assert.Equal("headset", model.SelectedKey?.Id);
    }

    [Fact]
    public void AnOlderDaemonWithoutChannelsKeepsTheSwitchAndThePresets()
    {
        // The shape before the per-channel setup: targets, no channels or mixes, no microphone field.
        JsonObject older = Ducking(enabled: true, channels: null, mixes: null, backend: "\"device\"", device: Pro());
        older["targets"] = new JsonArray("monitor");
        var (model, sent, _) = Model(older, Mixer(microphoneField: false));

        Assert.True(model.IsReported);
        Assert.False(model.HasTargets);
        Assert.False(model.NothingChosen);
        Assert.True(model.Enabled);
        Assert.True(model.OnDevice);
        Assert.Equal(Localizer.Format("DuckingStatusOnUnit", "Wave XLR Pro"), model.Status);
        // Without the field, XLR 1 and XLR 2 are the microphones.
        Assert.Equal(["xlr1", "xlr2"], model.Keys.Select(k => k.Id));

        model.Amount.Choose(model.Amount.Marks[1]);
        Assert.Equal("""{"amount":12}""", sent.One().ToJsonString());
        model.Enabled = false;
        Assert.Equal("""{"enabled":false}""", sent.One().ToJsonString());

        // A daemon that reports no ducking at all: the buttons hide.
        model.Apply(null, Mixer());
        Assert.False(model.IsReported);
        Assert.False(model.CanEdit);
    }

    [Fact]
    public void AnOlderReportDoesNotUndoAnEditButALaterOneWins()
    {
        var (model, sent, time) = Model();
        model.Mixes.Single(m => m.Id == "chat").IsChecked = true;
        sent.One();

        // The state from before the edit arrives after it.
        model.Apply(Ducking(), Mixer());
        Assert.True(model.Mixes.Single(m => m.Id == "chat").IsChecked);
        // Its echo clears the wait.
        model.Apply(Ducking(mixes: "\"monitor\",\"chat\""), Mixer());
        Assert.True(model.Mixes.Single(m => m.Id == "chat").IsChecked);

        // A change from elsewhere a second after the edit wins.
        model.Enabled = true;
        sent.One();
        time.Advance(DuckingViewModel.EchoWait);
        model.Apply(Ducking(enabled: false, mixes: "\"monitor\",\"chat\""), Mixer());
        Assert.False(model.Enabled);
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void ARefusedEditPutsTheDaemonsSetupBackAndSaysWhy()
    {
        var (model, sent, _) = Model();
        sent.Refuse = "setDucking: mixes must be distinct mix ids";
        model.Mixes.Single(m => m.Id == "stream").IsChecked = true;
        sent.One();

        Assert.Equal(sent.Refuse, model.Refusal);
        Assert.True(model.HasRefusal);
        Assert.False(model.Mixes.Single(m => m.Id == "stream").IsChecked);

        // A refusal gives back only the fields it carried: a tick still waiting
        // for its echo stays, and the report from before it does not undo it.
        sent.Refuse = null;
        model.Channels.Single(c => c.Id == "aux").IsChecked = true;
        sent.One();
        sent.Refuse = "setDucking: refused for the test";
        model.Mixes.Single(m => m.Id == "chat").IsChecked = true;
        sent.One();
        Assert.True(model.Channels.Single(c => c.Id == "aux").IsChecked);
        Assert.False(model.Mixes.Single(m => m.Id == "chat").IsChecked);

        // The next change clears it.
        sent.Refuse = null;
        model.Enabled = true;
        sent.One();
        Assert.Null(model.Refusal);
    }

    [Fact]
    public void RunsOnFollowsTheUnitAndTheLinesSayWhereEachSideSits()
    {
        // On the unit, holding it: the badge, and the line places the unit before USB and after the computer.
        var (model, sent, _) = Model(Ducking(enabled: true, backend: "\"device\"", device: Pro()));
        Assert.True(model.HasProcessingChoice);
        Assert.True(model.ProcessOnDevice);
        Assert.False(model.ProcessInSoftware);
        Assert.True(model.OnDevice);
        Assert.Equal(Localizer.Format("DuckingHardwareBadgeTip", "Wave XLR Pro"), model.BadgeTip);
        Assert.Equal(Localizer.Format("DuckingStatusOnUnit", "Wave XLR Pro"), model.Status);
        Assert.False(model.Warns);
        Assert.Null(model.Note);
        Assert.False(model.ShowReduction);
        Assert.Equal(Localizer.Format("DuckingProcessingDeviceTip", "Wave XLR Pro"), model.ProcessOnDeviceTip);
        Assert.Contains("XLR 1", model.ProcessOnDeviceTip);

        // Not holding it yet.
        model.Apply(Ducking(enabled: true, backend: "\"device\"", device: Pro(held: false)), Mixer());
        Assert.Equal(Localizer.Text("DuckingNotHeld"), model.Status);
        Assert.True(model.Warns);

        // Chosen but not ready: the catalogue's sentence for a reason it knows, software running it. The
        // switch lights this computer, the unit's side cannot be pressed, and its tooltip says why.
        model.Apply(Ducking(enabled: true, key: "xlr2", channels: "\"music\"", backend: "\"software\"",
            device: Pro(ready: false, reason: "keyNotSupported", note: "The Wave XLR Pro ducks only with XLR 1 as the key.")), Mixer());
        Assert.False(model.OnDevice);
        Assert.Equal("device", model.Processing);
        Assert.False(model.ProcessOnDevice);
        Assert.True(model.ProcessInSoftware);
        Assert.False(model.CanProcessOnDevice);
        Assert.Equal(Localizer.Format("DuckingDeviceKeyNotSupported", "Wave XLR Pro"), model.Note);
        Assert.Equal(model.Note, model.ProcessOnDeviceTip);
        Assert.Equal(Localizer.Format("DuckingStatusOnComputer", "XLR 2"), model.Status);
        Assert.False(model.Warns);
        Assert.True(model.ShowReduction);

        // A reason that names channels and mixes, or one the window does not know: the daemon's sentence.
        const string partial = "Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output.";
        model.Apply(Ducking(enabled: true, backend: "\"software\"", device: Pro(ready: false, reason: "partialMix", note: partial)), Mixer());
        Assert.Equal(partial, model.Note);
        model.Apply(Ducking(enabled: true, backend: "\"software\"", device: Pro(ready: false, reason: "somethingNew", note: "A new reason.")), Mixer());
        Assert.Equal("A new reason.", model.Note);

        // Software by choice while the unit cannot run it: the note stays, saying why the unit's side is off.
        model.ChooseProcessing("software");
        Assert.True(sent.Sent.TryDequeue(out var chosen));
        Assert.Equal(("setDuckingProcessing", "software"), (chosen.Command, chosen.Value.GetValue<string>()));
        Assert.True(model.ProcessInSoftware);
        Assert.Equal("A new reason.", model.Note);
        // The side already chosen sends nothing, and the unit's side cannot be asked for.
        model.ChooseProcessing("software");
        model.ChooseProcessing("device");
        Assert.Empty(sent.Sent);

        // Software by choice on a unit that can run it: no note, the switch says so, and the unit's side can be pressed.
        model.Apply(Ducking(enabled: true, backend: "\"software\"", processing: "software", device: Pro()), Mixer());
        Assert.True(model.ProcessInSoftware);
        Assert.True(model.CanProcessOnDevice);
        Assert.Null(model.Note);
        Assert.Equal(Localizer.Format("DuckingProcessingDeviceTip", "Wave XLR Pro"), model.ProcessOnDeviceTip);

        // No unit with hardware ducking: no choice, whatever the processing word says.
        model.Apply(Ducking(enabled: true, backend: "\"software\""), Mixer());
        Assert.False(model.HasProcessingChoice);
        model.ChooseProcessing("device");
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void TheStatusSaysWhyNothingRunsIt()
    {
        var (model, sent, _) = Model(Ducking(enabled: false, backend: "\"software\""));
        Assert.Equal(Localizer.Text("DuckingStatusOff"), model.Status);
        Assert.False(model.Warns);

        // On with no mix: nothing is lowered, which is a hint, not a fault.
        model.Apply(Ducking(enabled: true, mixes: ""), Mixer());
        Assert.True(model.NothingChosen);
        Assert.Equal(Localizer.Text("DuckingNothingChosen"), model.Status);
        Assert.False(model.Warns);

        // The software side cannot run: its error, as a warning.
        model.Apply(Ducking(enabled: true, available: false, error: "Ducking needs the OpenXLR native plugin host."), Mixer());
        Assert.Equal("Ducking needs the OpenXLR native plugin host.", model.Status);
        Assert.True(model.Warns);

        // Hardware-control mode: the setup shows, nothing can change it, and nothing is sent.
        model.Apply(Ducking(enabled: true, device: Pro(ready: false, reason: "mixerOff", note: "The software mixer is off, so ducking does not run.")), null);
        Assert.False(model.CanEdit);
        Assert.Equal(Localizer.Text("DuckingMixerOff"), model.Status);
        Assert.False(model.Warns);
        Assert.Null(model.Note);
        model.ChooseAllApplications();
        model.ChooseProcessing("software");
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void AChannelTheListDoesNotShowStaysInTheSetup()
    {
        // XLR 2 is ducked, and the unit now in use has no second input.
        var (model, sent, _) = Model(Ducking(channels: "\"xlr2\",\"music\""), Mixer(xlr2Present: false));
        // A ducked channel without its jack still shows, ticked, so it can be unticked.
        Assert.Contains(model.Channels, c => c.Id == "xlr2" && c.IsChecked);
        Assert.DoesNotContain(model.Keys, k => k.Id == "xlr2");

        model.Apply(Ducking(channels: "\"music\""), Mixer(xlr2Present: false));
        Assert.DoesNotContain(model.Channels, c => c.Id == "xlr2");
        // A channel named by the setup but not drawn is kept when another is ticked.
        model.Apply(Ducking(channels: "\"music\",\"gone\""), Mixer());
        model.Channels.Single(c => c.Id == "game").IsChecked = true;
        Assert.Equal(["game", "music", "gone"], Ids(sent.One()["channels"]));
    }

    [Fact]
    public void NumbersInTheStateShowAsTheyAreAndAnUnknownValueLeavesTheSlider()
    {
        JsonObject state = Ducking();
        state["sensitivity"] = -37;
        state["amount"] = 31;
        state["speed"] = new JsonObject { ["attack"] = 7, ["release"] = 4321 };
        var (model, sent, _) = Model(state);
        Assert.Equal((-37, 31, 7, 4321), (model.Sensitivity.Value, model.Amount.Value, model.Attack.Value, model.Release.Value));
        Assert.Equal(Math.Log10(4321), model.Release.Position, 9);
        Assert.Equal(37, model.Sensitivity.Position);
        Assert.Empty(sent.Sent);

        state["sensitivity"] = "loud";
        state["amount"] = 12.5;
        state["speed"] = new JsonObject { ["attack"] = 7 };
        model.Apply(state, Mixer());
        Assert.Equal((-37, 31, 7, 4321), (model.Sensitivity.Value, model.Amount.Value, model.Attack.Value, model.Release.Value));
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void ADragShowsItsValueSnapsToMarksAndSendsOnlyWhenItEnds()
    {
        var (model, sent, _) = Model();
        DuckingSliderViewModel attack = model.Attack;
        // Without a drag the slider's own value changes nothing: a range clamped as it is built is not an edit.
        attack.Position = Math.Log10(500);
        Assert.Equal(30, attack.Value);
        Assert.Empty(sent.Sent);

        attack.BeginDrag();
        attack.Position = Math.Log10(500);
        Assert.Equal((500, Localizer.Format("DynamicsMilliseconds", "500")), (attack.Value, attack.Text));
        // Near Slow's 100 ms it lands on 100 exactly, and the thumb with it.
        attack.Position = Math.Log10(107);
        Assert.Equal(100, attack.Value);
        Assert.Equal(Math.Log10(100), attack.Position, 9);
        // Between Medium and Slow, the nearer mark wins.
        attack.Position = Math.Log10(84);
        Assert.Equal(80, attack.Value);
        attack.Position = Math.Log10(250);
        Assert.Equal(250, attack.Value);
        Assert.Empty(sent.Sent);
        // A report while the pointer holds it waits.
        model.Apply(Ducking(), Mixer());
        Assert.Equal(250, attack.Value);
        attack.EndDrag();
        Assert.Equal("""{"speed":{"attack":250,"release":200}}""", sent.One().ToJsonString());
        // A second release, or a drag that comes back where it started, sends nothing.
        attack.EndDrag();
        attack.BeginDrag();
        attack.Position = Math.Log10(600);
        attack.Position = Math.Log10(250);
        attack.EndDrag();
        Assert.Empty(sent.Sent);

        // The echo of the speed object clears the wait; the older report before it does not undo the edit.
        model.Apply(Ducking(), Mixer());
        Assert.Equal(250, attack.Value);
        JsonObject echo = Ducking();
        echo["speed"] = new JsonObject { ["attack"] = 250, ["release"] = 200 };
        model.Apply(echo, Mixer());
        model.Apply(Ducking(), Mixer());
        Assert.Equal(30, attack.Value);
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void KeysStepEachSliderAndSendEachStep()
    {
        var (model, sent, _) = Model();
        // Sensitivity rises to the right, so its threshold falls.
        model.Sensitivity.Step(1, large: false);
        Assert.Equal("""{"sensitivity":-13}""", sent.One().ToJsonString());
        model.Sensitivity.Step(-1, large: true);
        Assert.Equal("""{"sensitivity":-8}""", sent.One().ToJsonString());
        model.Sensitivity.StepToEnd(right: true);
        Assert.Equal("""{"sensitivity":-60}""", sent.One().ToJsonString());
        model.Sensitivity.Step(1, large: false);
        Assert.Empty(sent.Sent);

        // A logarithmic slider steps by a share of its value, at least one.
        model.Release.Step(1, large: false);
        Assert.Equal("""{"speed":{"attack":30,"release":210}}""", sent.One().ToJsonString());
        model.Release.Step(1, large: true);
        Assert.Equal("""{"speed":{"attack":30,"release":263}}""", sent.One().ToJsonString());
        model.Attack.StepToEnd(right: false);
        Assert.Equal("""{"speed":{"attack":1,"release":263}}""", sent.One().ToJsonString());
        model.Attack.Step(1, large: false);
        Assert.Equal("""{"speed":{"attack":2,"release":263}}""", sent.One().ToJsonString());
        model.Amount.StepToEnd(right: true);
        Assert.Equal("""{"amount":40}""", sent.One().ToJsonString());
        // Each slider's marks are the Wave XLR Pro's presets.
        Assert.Equal([-3, -12, -24], model.Sensitivity.Marks.Select(m => m.Value));
        Assert.Equal([6, 12, 24], model.Amount.Marks.Select(m => m.Value));
        Assert.Equal([100, 80, 30], model.Attack.Marks.Select(m => m.Value));
        Assert.Equal([2000, 1200, 200], model.Release.Marks.Select(m => m.Value));
        Assert.Equal(Localizer.Format("DuckingMarkTip", Localizer.Text("DuckingMedium"), Localizer.Format("DuckingDbfs", "-12")),
            model.Sensitivity.Marks[1].Tip);
        // A mark already held sends nothing.
        model.Amount.Choose(model.Amount.Marks[2]);
        sent.One();
        model.Amount.Choose(model.Amount.Marks[2]);
        Assert.Empty(sent.Sent);
    }

    [Fact]
    public void TheReductionBarTakesTheLargestDuckMeter()
    {
        var (model, _, _) = Model(Ducking(enabled: true, backend: "\"software\""));
        Assert.True(model.ShowReduction);
        model.ApplyMeters(JsonNode.Parse("""{"ch:music":[0.5,0.5],"duck:monitor":[12.0],"duck:stream":[6.5],"mix:monitor":[0.4,0.4]}""")!.AsObject());
        Assert.Equal(12, model.Reduction);
        Assert.Equal(0.5, model.ReductionLevel);
        Assert.Equal(Localizer.Format("DuckingReductionValue", 12.0), model.ReductionText);
        // Past the deepest preset the bar stays full; a frame without duck meters reads none.
        model.ApplyMeters(JsonNode.Parse("""{"duck:monitor":[30.0]}""")!.AsObject());
        Assert.Equal(1, model.ReductionLevel);
        model.ApplyMeters(JsonNode.Parse("""{"ch:music":[0.5,0.5]}""")!.AsObject());
        Assert.Equal(0, model.Reduction);
        // A deeper amount than the deepest preset fills the bar at that depth.
        JsonObject deep = Ducking(enabled: true, backend: "\"software\"");
        deep["amount"] = 40;
        model.Apply(deep, Mixer());
        model.ApplyMeters(JsonNode.Parse("""{"duck:monitor":[30.0]}""")!.AsObject());
        Assert.Equal(0.75, model.ReductionLevel);

        // The unit gives no reading: the bar hides while it runs ducking.
        model.Apply(Ducking(enabled: true, backend: "\"device\"", device: Pro()), Mixer());
        Assert.False(model.ShowReduction);
    }
}
