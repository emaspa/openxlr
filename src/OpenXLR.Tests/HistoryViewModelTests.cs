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

    private sealed class Hold : IDisposable
    {
        public void Dispose() { }
    }
}
