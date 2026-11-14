using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The header pair's model: each kind named in the UI's language with the
/// names the UI shows, the tooltips, and the notice an undo or redo leaves
/// for three seconds, which turns into the daemon's note once the state
/// that carries it arrives.
/// </summary>
public sealed class HistoryViewModelTests
{
    private static readonly HistoryNames Names = new(
        id => id switch { "xlr1" => "XLR 1", "music" => "Music", _ => null },
        id => id switch { "monitor" => "Monitor A", "stream" => "Stream", _ => null },
        id => id == "sources" ? "Sources" : null)
    {
        Insert = (chain, id) => (chain, id) switch { ("xlr1", "comp") => "Comp", ("mix:stream", "wide") => "Widener", _ => null },
    };

    private static HistoryStepItem Step(string kind, string? channel = null, string? mix = null, string? group = null, int count = 1,
        string? insert = null)
        => new(1, kind, "English label", channel, mix, group, count, insert);

    [Fact]
    public void EveryKindIsNamedInTheUisLanguageWithTheNamesItShows()
    {
        var model = new HistoryViewModel(null, Names);
        Assert.Equal(Localizer.Format("HistorySendLevel", "XLR 1", "Monitor A"), model.Name(Step("sendLevel", "xlr1", "monitor")));
        Assert.Equal(Localizer.Format("HistoryChannelVolume", "Music"), model.Name(Step("channelVolume", "music")));
        Assert.Equal(Localizer.Format("HistoryMixVolume", "Stream"), model.Name(Step("mixVolume", mix: "stream")));
        Assert.Equal(Localizer.Format("HistorySendMute", "Music", "Stream"), model.Name(Step("sendMute", "music", "stream")));
        Assert.Equal(Localizer.Format("HistoryMixMute", "Monitor A"), model.Name(Step("mixMute", mix: "monitor")));
        Assert.Equal(Localizer.Format("HistoryExclusiveGroup", "Sources"), model.Name(Step("exclusiveGroup", group: "sources")));
        // The processing kinds.
        Assert.Equal(Localizer.Format("HistoryEq", "XLR 1"), model.Name(Step("eq", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryMixEq", "Stream"), model.Name(Step("mixEq", mix: "stream")));
        Assert.Equal(Localizer.Format("HistoryNoiseRemoval", "XLR 1"), model.Name(Step("noiseRemoval", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryVoiceChanger", "XLR 1"), model.Name(Step("voiceChanger", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryCompressor", "XLR 1"), model.Name(Step("compressor", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryExpander", "XLR 1"), model.Name(Step("expander", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryLowCut", "XLR 1"), model.Name(Step("lowCut", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryClipGuard", "XLR 1"), model.Name(Step("clipGuard", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryMonitorDry", "Monitor A"), model.Name(Step("monitorDry", mix: "monitor")));
        Assert.Equal(Localizer.Text("HistoryDucking"), model.Name(Step("ducking")));
        // The insert kinds name the insert in its channel's or mix's chain.
        Assert.Equal(Localizer.Format("HistoryInsertParam", "Comp", "XLR 1"), model.Name(Step("insertParam", "xlr1", insert: "comp")));
        Assert.Equal(Localizer.Format("HistoryInsertBypass", "Widener", "Stream"), model.Name(Step("insertBypass", mix: "stream", insert: "wide")));
        Assert.Equal(Localizer.Format("HistoryInsertName", "Comp", "XLR 1"), model.Name(Step("insertName", "xlr1", insert: "comp")));
        Assert.Equal(Localizer.Format("HistoryInsertChain", "Stream"), model.Name(Step("insertChain", mix: "stream")));
        // An insert the chain no longer holds: the daemon's label.
        Assert.Equal("English label", model.Name(Step("insertParam", "xlr1", insert: "gone")));
        // A burst of five: the first and how many more.
        Assert.Equal(Localizer.Format("HistoryMore", Localizer.Format("HistorySendLevel", "Music", "Monitor A"), 4),
            model.Name(Step("sendLevel", "music", "monitor", count: 5)));
        // A kind from a later daemon, an id the UI cannot name, or no names at all: the daemon's label.
        Assert.Equal("English label", model.Name(Step("laterKind", "xlr1")));
        Assert.Equal("English label", model.Name(Step("sendLevel", "gone", "monitor")));
        Assert.Equal("English label", new HistoryViewModel(null).Name(Step("mixVolume", mix: "stream")));
    }

    [Fact]
    public void TheTooltipsNameTheStepAndTheKeyOrSayThereIsNone()
    {
        var model = new HistoryViewModel(null, Names);
        Assert.Equal(Localizer.Text("NothingToUndo"), model.UndoTip);
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":7,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume"},
             "redo":null,"undoSteps":1,"redoSteps":0,"note":null}
            """));
        Assert.True(model.IsReported && model.CanUndo && !model.CanRedo);
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryMixVolume", "Stream")), model.UndoTip);
        Assert.Equal(Localizer.Text("NothingToRedo"), model.RedoTip);
        Assert.Contains("Ctrl+Z", model.UndoTip, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Shift+Z", model.RedoTip, StringComparison.Ordinal);

        // Burst counts and the other ids come through from the state.
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":8,"kind":"sendMute","channel":"music","mix":"monitor","count":3,"label":"Music mute in Monitor A and 2 more"},
             "redo":{"step":7,"kind":"exclusiveGroup","group":"sources","count":1,"label":"Sources switch"},"undoSteps":1,"redoSteps":1,"note":null}
            """));
        Assert.Equal(new HistoryStepItem(8, "sendMute", "Music mute in Monitor A and 2 more", "music", "monitor", null, 3), model.Undo);
        Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistoryExclusiveGroup", "Sources")), model.RedoTip);
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":9,"kind":"insertBypass","channel":"xlr1","insert":"comp","count":1,"label":"Comp bypass on XLR 1"},
             "redo":null,"undoSteps":2,"redoSteps":0,"note":null}
            """));
        Assert.Equal(new HistoryStepItem(9, "insertBypass", "Comp bypass on XLR 1", "xlr1", Insert: "comp"), model.Undo);
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryInsertBypass", "Comp", "XLR 1")), model.UndoTip);

        model.Reset();
        Assert.False(model.IsReported || model.CanUndo || model.CanRedo);
        Assert.Equal(Localizer.Text("NothingToUndo"), model.UndoTip);
    }

    [Fact]
    public async Task TheNoticeNamesTheStepThenTheNoteAndEndsAfterItsHold()
    {
        var holds = new List<(TimeSpan After, Action Elapsed)>();
        string? refusal = null;
        var model = new HistoryViewModel((_, _) => Task.FromResult(refusal), Names)
        {
            StartNoticeHold = (after, elapsed) => { holds.Add((after, elapsed)); return new Hold(); },
        };
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":5,"kind":"sendLevel","channel":"xlr1","mix":"stream","count":1,"label":"XLR 1 level in Stream"},
             "redo":null,"undoSteps":1,"redoSteps":0,"note":"Undo skipped part of an older step: it changed since."}
            """));
        string name = Localizer.Format("HistorySendLevel", "XLR 1", "Stream");

        // The reply comes first: the old note is not this undo's, so the step is named.
        Assert.Equal(Localizer.Format("UndidStep", name), await model.RunAsync(redo: false));
        Assert.Equal(Localizer.Format("UndidStep", name), model.Notice);
        Assert.Equal(HistoryViewModel.NoticeHold, Assert.Single(holds).After);
        Assert.Equal(TimeSpan.FromSeconds(3), HistoryViewModel.NoticeHold);
        // Then the state: the step moved to redo with a note, which replaces the name.
        model.Apply(JsonNode.Parse("""
            {"undo":null,"redo":{"step":5,"kind":"sendLevel","channel":"xlr1","mix":"stream","count":1,"label":"XLR 1 level in Stream"},
             "undoSteps":0,"redoSteps":1,"note":"Undo skipped part of XLR 1 level in Stream: it changed since."}
            """));
        Assert.Equal("Undo skipped part of XLR 1 level in Stream: it changed since.", model.Notice);
        holds[^1].Elapsed();
        Assert.Null(model.Notice);

        // A state that arrives before the reply: its note is this redo's at once.
        model.Send = (_, _) =>
        {
            model.Apply(JsonNode.Parse("""
                {"undo":{"step":5,"kind":"sendLevel","channel":"xlr1","mix":"stream","count":1,"label":"XLR 1 level in Stream"},
                 "redo":null,"undoSteps":1,"redoSteps":0,"note":"Redo skipped part of XLR 1 level in Stream: it changed since."}
                """));
            return Task.FromResult<string?>(null);
        };
        Assert.Equal("Redo skipped part of XLR 1 level in Stream: it changed since.", await model.RunAsync(redo: true));
        Assert.Equal("Redo skipped part of XLR 1 level in Stream: it changed since.", model.Notice);
        // Without a note there, the step is named.
        model.Send = (_, _) =>
        {
            model.Apply(JsonNode.Parse("""
                {"undo":null,"redo":{"step":5,"kind":"sendLevel","channel":"xlr1","mix":"stream","count":1,"label":"XLR 1 level in Stream"},
                 "undoSteps":0,"redoSteps":1,"note":null}
                """));
            return Task.FromResult<string?>(null);
        };
        Assert.Equal(Localizer.Format("UndidStep", name), await model.RunAsync(redo: false));
        model.Send = (_, _) => Task.FromResult(refusal);

        // The reply first and no state yet: the redo is named.
        model.Apply(JsonNode.Parse("""
            {"undo":null,"redo":{"step":5,"kind":"sendLevel","channel":"xlr1","mix":"stream","count":1,"label":"XLR 1 level in Stream"},
             "undoSteps":0,"redoSteps":1,"note":null}
            """));
        Assert.Equal(Localizer.Format("RedidStep", name), await model.RunAsync(redo: true));

        // A refusal is the notice, and a state after it changes nothing.
        refusal = "redo: XLR 1 level in Stream was skipped: it changed since";
        Assert.Equal(refusal, await model.RunAsync(redo: true));
        Assert.Equal(refusal, model.Notice);
        model.Apply(JsonNode.Parse("""{"undo":null,"redo":null,"undoSteps":0,"redoSteps":0,"note":null}"""));
        Assert.Equal(refusal, model.Notice);
        holds[^1].Elapsed();
        Assert.Null(model.Notice);
    }

    [Fact]
    public void TheUnitsControlsAreNamedAndAnAdditionalInterfaceReadsTheDaemonsLabel()
    {
        var model = new HistoryViewModel(null, Names);
        HistoryStepItem Unit(string kind, string control, string? channel = null, string? unit = null)
            => new(1, kind, "English label", channel, Control: control, Unit: unit);
        Assert.Equal(Localizer.Format("HistoryUnitGain", "XLR 1"), model.Name(Unit("unitGain", "gain", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryLowCut", "XLR 1"), model.Name(Unit("unitLowCut", "lowCut", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryClipGuard", "XLR 1"), model.Name(Unit("unitClipGuard", "clipGuard", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryExpander", "XLR 1"), model.Name(Unit("unitExpander", "expander", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryCompressor", "XLR 1"), model.Name(Unit("unitCompressor", "compressor", "xlr1")));
        Assert.Equal(Localizer.Format("HistoryVoiceTune", "XLR 1"), model.Name(Unit("unitVoiceTune", "voiceTuneStrength", "xlr1")));
        Assert.Equal(Localizer.Text("HistoryHeadphones"), model.Name(Unit("unitHeadphones", "hpVolumeDb")));
        Assert.Equal(Localizer.Text("HistoryHeadphones2"), model.Name(Unit("unitHeadphones", "hp2VolumeDb")));
        Assert.Equal(Localizer.Text("HistoryCrossfade"), model.Name(Unit("unitCrossfade", "crossfade")));
        Assert.Equal(Localizer.Text("HistoryLowImpedance"), model.Name(Unit("unitLowImpedance", "lowImpedance")));
        Assert.Equal(Localizer.Text("HistoryAuxLevel"), model.Name(Unit("unitAuxLevel", "auxLevelDb")));
        Assert.Equal(Localizer.Text("HistoryLineInGain"), model.Name(Unit("unitLineInGain", "lineInGain")));
        // A step on an additional interface carries its unit and no channel.
        Assert.Equal("English label", model.Name(Unit("unitGain", "gain", unit: "0fd9:0070@0123456789abcdef")));
        Assert.Equal("English label", model.Name(Unit("unitHeadphones", "hpVolumeDb", unit: "0fd9:0070@0123456789abcdef")));

        model.Apply(JsonNode.Parse("""
            {"undo":{"step":9,"kind":"unitHeadphones","control":"hp2VolumeDb","count":1,"label":"Headphone 2 volume"},
             "redo":null,"undoSteps":1,"redoSteps":0,"note":null}
            """));
        Assert.Equal(new HistoryStepItem(9, "unitHeadphones", "Headphone 2 volume", Control: "hp2VolumeDb"), model.Undo);
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Text("HistoryHeadphones2")), model.UndoTip);
    }

    [Fact]
    public void ATooltipNamesTheValuesAStepOfOneValueMovesBetween()
    {
        var model = new HistoryViewModel(null, Names);
        string stream = Localizer.Format("HistoryMixVolume", "Stream");
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":7,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume","from":1.0,"to":0.75},
             "redo":{"step":8,"kind":"mixMute","mix":"stream","count":1,"label":"Stream mute","from":false,"to":true},
             "undoSteps":1,"redoSteps":1,"note":null}
            """));
        Assert.Equal(new HistoryStepItem(7, "mixVolume", "Stream volume", Mix: "stream", From: 1.0, To: 0.75), model.Undo);
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryUndoValues", stream, "100%", "75%")), model.UndoTip);
        Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistoryRedoValues", Localizer.Format("HistoryMixMute", "Stream"),
            Localizer.Text("ParamOff"), Localizer.Text("ParamOn"))), model.RedoTip);
        Assert.DoesNotContain('\n', model.UndoTip);

        // The hardware values read as the UI shows them: the gain in dB, the headphones as their slider's percentage.
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":9,"kind":"unitGain","channel":"xlr1","control":"gain","count":1,"label":"XLR 1 gain","from":32,"to":28},
             "redo":{"step":10,"kind":"unitHeadphones","control":"hpVolumeDb","count":1,"label":"Headphone volume","from":-30,"to":0},
             "undoSteps":1,"redoSteps":1,"note":null}
            """));
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryUndoValues", Localizer.Format("HistoryUnitGain", "XLR 1"), "32 dB", "28 dB")),
            model.UndoTip);
        Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistoryRedoValues", Localizer.Text("HistoryHeadphones"), "50%", "100%")),
            model.RedoTip);
        HistoryStepItem Of(string kind, string? control = null) => new(1, kind, "x", Control: control);
        Assert.Equal(Localizer.Format("CrossfadeMic", 20), HistoryViewModel.Value(Of("unitCrossfade"), 80.0));
        Assert.Equal(Localizer.Text("CrossfadeCentre"), HistoryViewModel.Value(Of("unitCrossfade"), 100.0));
        Assert.Equal("80 Hz", HistoryViewModel.Value(Of("lowCut"), 80.0));
        Assert.Equal(Localizer.Text("ParamOff"), HistoryViewModel.Value(Of("lowCut"), 0.0));
        Assert.Equal("40%", HistoryViewModel.Value(Of("unitVoiceTune", "voiceTuneStrength"), 40.0));
        Assert.Equal(Localizer.Text("ParamOn"), HistoryViewModel.Value(Of("unitVoiceTune", "voiceTune"), true));
        // A kind with no readout of its own, such as an EQ, or a value of the wrong shape: none.
        Assert.Null(HistoryViewModel.Value(Of("eq"), 1.0));
        Assert.Null(HistoryViewModel.Value(Of("mixMute"), 1.0));
    }

    [Fact]
    public void AStepOfSeveralValuesKeepsItsPlainNameAndNoValues()
    {
        var model = new HistoryViewModel(null, Names);
        // A burst carries no values from the daemon; one that did would still read only its name and count.
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":8,"kind":"sendMute","channel":"music","mix":"monitor","count":3,"label":"Music mute in Monitor A and 2 more","from":true,"to":false},
             "redo":{"step":7,"kind":"exclusiveGroup","group":"sources","count":1,"label":"Sources switch"},"undoSteps":1,"redoSteps":1,"note":null}
            """));
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryMore", Localizer.Format("HistorySendMute", "Music", "Monitor A"), 2)),
            model.UndoTip);
        Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistoryExclusiveGroup", "Sources")), model.RedoTip);
    }

    [Fact]
    public void ASecondLineNamesTheNextThreeStepsAndHowManyMoreFollow()
    {
        var model = new HistoryViewModel(null, Names);
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":9,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume"},
             "undoNext":[{"step":8,"kind":"sendMute","channel":"music","mix":"stream","count":2,"label":"Music mute in Stream and 1 more"},
                         {"step":7,"kind":"mixMute","mix":"monitor","count":1,"label":"Monitor A mute","from":true,"to":false},
                         {"step":6,"kind":"laterKind","count":1,"label":"Something later"}],
             "redo":{"step":10,"kind":"mixMute","mix":"stream","count":1,"label":"Stream mute"},
             "redoNext":[{"step":11,"kind":"eq","channel":"xlr1","count":1,"label":"XLR 1 EQ"}],
             "undoSteps":7,"redoSteps":2,"note":null}
            """));
        Assert.Equal([8L, 7L, 6L], model.UndoNext.Select(s => s.Step));
        // The next steps go by name alone, without a burst's count or their values.
        string names = string.Join(", ", Localizer.Format("HistorySendMute", "Music", "Stream"), Localizer.Format("HistoryMixMute", "Monitor A"),
            "Something later");
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryMixVolume", "Stream")) + "\n" + Localizer.Format("HistoryThenMore", names, 3),
            model.UndoTip);
        Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistoryMixMute", "Stream")) + "\n" + Localizer.Format("HistoryThen",
            Localizer.Format("HistoryEq", "XLR 1")), model.RedoTip);
        Assert.Equal("Then: Music mute in Stream, Monitor A mute, Something later, and 3 more",
            Localizer.Format("HistoryThenMore", names, 3));

        // Nothing on a side still says so, and a daemon that names no next steps gives one line.
        model.Apply(JsonNode.Parse("""
            {"undo":{"step":9,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume"},
             "redo":null,"undoSteps":4,"redoSteps":0,"note":null}
            """));
        Assert.Empty(model.UndoNext);
        Assert.Equal(Localizer.Format("UndoStep", Localizer.Format("HistoryMixVolume", "Stream")), model.UndoTip);
        Assert.Equal(Localizer.Text("NothingToRedo"), model.RedoTip);
        model.Apply(JsonNode.Parse("""{"undo":null,"redo":null,"undoSteps":0,"redoSteps":0,"note":null}"""));
        Assert.Equal(Localizer.Text("NothingToUndo"), model.UndoTip);
    }

    private sealed class Hold : IDisposable
    {
        public void Dispose() { }
    }
}
