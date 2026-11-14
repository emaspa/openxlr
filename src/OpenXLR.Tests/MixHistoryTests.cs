using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>The undo history alone: joining, redo, bounds and moving steps between the two sides.</summary>
public sealed class MixHistoryTests
{
    private long _now = 10_000;

    private MixHistory History() => new() { Clock = () => _now };

    private static HistoryTarget Send(string channel, string mix)
        => new("sendLevel", channel, mix, null, [SceneField.Level], _ => true);

    private static SceneChange Level(string cell, double before, double after) => new(new(SceneField.Level, cell), before, after);

    [Fact]
    public void ACommandBecomesAStepNamedInTheState()
    {
        MixHistory history = History();
        Assert.False(history.Record("a", Send("xlr1", "monitor"), "Mic level in Monitor", []));
        Assert.Null(history.State().Undo);

        Assert.True(history.Record("a", Send("xlr1", "monitor"), "Mic level in Monitor", [Level("xlr1|monitor", 1, .5)]));
        HistoryState state = history.State();
        Assert.Equal(1, state.UndoSteps);
        Assert.Equal(0, state.RedoSteps);
        Assert.Null(state.Redo);
        Assert.Equal(new HistoryStepInfo(1, "sendLevel", "xlr1", "monitor", null, 1, "Mic level in Monitor"), state.Undo);
    }

    [Fact]
    public void MovesOfOneValueWithinASecondAreOneStepWithTheFirstBeforeAndTheLastAfter()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .9)]);
        _now += 900;
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .9, .7)]);
        _now += 900;
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .7, .5)]);

        HistoryStep step = Assert.IsType<HistoryStep>(history.Peek(redo: false));
        Assert.Equal([Level("xlr1|monitor", 1, .5)], step.Changes);
        Assert.Equal(1, history.State().UndoSteps);

        // A pause longer than a second starts a new step.
        _now += 1001;
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .5, .4)]);
        Assert.Equal(2, history.State().UndoSteps);
    }

    [Fact]
    public void ABurstWithin50MsJoinsWhateverItChangesAndCountsItsTargets()
    {
        MixHistory history = History();
        history.Record("deck", Send("xlr1", "monitor"), "Mic level in Monitor", [Level("xlr1|monitor", 1, .5)]);
        _now += 30;
        history.Record("deck", Send("xlr1", "stream"), "Mic level in Stream", [Level("xlr1|stream", 1, .5)]);
        _now += 30;
        history.Record("deck", Send("xlr1", "chat"), "Mic level in Chat", [Level("xlr1|chat", 1, .5)]);

        HistoryStepInfo undo = history.State().Undo!;
        Assert.Equal(3, undo.Count);
        Assert.Equal("Mic level in Monitor and 2 more", undo.Label);
        Assert.Equal(3, history.Peek(redo: false)!.Changes.Count);

        // Past 50 ms a change of another value is a step of its own.
        _now += 51;
        history.Record("deck", Send("xlr2", "chat"), "x", [Level("xlr2|chat", 1, .5)]);
        Assert.Equal(2, history.State().UndoSteps);
    }

    [Fact]
    public void AnotherClientNeverJoins()
    {
        MixHistory history = History();
        history.Record("ui", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .9)]);
        history.Record("deck", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .9, .8)]);
        Assert.Equal(2, history.State().UndoSteps);
    }

    [Fact]
    public void AStepThatEndsWhereItBeganIsRemoved()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .9)]);
        _now += 1500;
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .9, .5)]);
        _now += 200;
        Assert.True(history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .5, .9)]));
        Assert.Equal(1, history.State().UndoSteps);
        Assert.Equal([Level("xlr1|monitor", 1, .9)], history.Peek(redo: false)!.Changes);
    }

    [Fact]
    public void UndoMovesTheStepToRedoAndANewStepClearsRedo()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5)]);
        HistoryStep step = history.Peek(redo: false)!;
        history.Complete(step, redo: false, step.Changes, note: null);
        Assert.Equal((0, 1), (history.State().UndoSteps, history.State().RedoSteps));
        Assert.Same(step, history.Peek(redo: true));

        // An undo closes the step: the next command, at once, starts a new one.
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .7)]);
        Assert.Equal((1, 0), (history.State().UndoSteps, history.State().RedoSteps));
        Assert.NotEqual(step.Id, history.Peek(redo: false)!.Id);
    }

    [Fact]
    public void ACompletedStepKeepsOnlyWhatWasDoneAndOneWithNothingDoneIsDropped()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5), Level("xlr1|stream", 1, .5)]);
        HistoryStep step = history.Peek(redo: false)!;
        history.Complete(step, redo: false, [step.Changes[1]], "Undo skipped part of x: it changed since.");
        Assert.Equal([Level("xlr1|stream", 1, .5)], history.Peek(redo: true)!.Changes);
        Assert.Equal("Undo skipped part of x: it changed since.", history.State().Note);

        history.Complete(history.Peek(redo: true)!, redo: true, [], null);
        HistoryState state = history.State();
        Assert.Equal((0, 0), (state.UndoSteps, state.RedoSteps));
        Assert.Null(state.Note);
    }

    [Fact]
    public void ANewChangeClearsTheNote()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5), Level("xlr1|stream", 1, .5)]);
        HistoryStep step = history.Peek(redo: false)!;
        history.Complete(step, redo: false, [step.Changes[0]], "note");
        history.Record("a", Send("xlr1", "chat"), "x", [Level("xlr1|chat", 1, .5)]);
        Assert.Null(history.State().Note);
    }

    [Fact]
    public void ClearEmptiesBothSides()
    {
        MixHistory history = History();
        Assert.False(history.Clear());
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5)]);
        _now += 2000;
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .5, .4)]);
        history.Complete(history.Peek(redo: false)!, redo: false, history.Peek(redo: false)!.Changes, null);
        Assert.True(history.Clear());
        Assert.Equal(new HistoryState(null, null, 0, 0, null), history.State());
    }

    [Fact]
    public void TheHistoryKeepsFiftyStepsDroppingTheOldest()
    {
        MixHistory history = History();
        for (int i = 0; i < 60; i++)
        {
            _now += 2000;
            history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", i, i + 1)]);
        }
        HistoryState state = history.State();
        Assert.Equal(MixHistory.MaxSteps, state.UndoSteps);
        Assert.Equal(60, state.Undo!.Step);
    }

    [Fact]
    public void TheHistoryKeepsTwoMegabytesDroppingTheOldest()
    {
        MixHistory history = History();
        string key = new('k', 300 * 1024);
        for (int i = 0; i < 10; i++)
        {
            _now += 2000;
            history.Record("a", Send("xlr1", "monitor"), "x", [Level(key + i, 1, .5)]);
        }
        Assert.True(history.Bytes <= MixHistory.MaxBytes);
        Assert.InRange(history.State().UndoSteps, 2, 7);
        Assert.Equal(10, history.State().Undo!.Step);
    }

    [Fact]
    public void TheStateWritesEmptyEndsAsNullAndLeavesOutIdsAStepDoesNotName()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        Assert.Equal("""{"undo":null,"redo":null,"undoSteps":0,"redoSteps":0,"note":null}""",
            System.Text.Json.JsonSerializer.Serialize(new HistoryState(null, null, 0, 0, null), options));
        Assert.Equal("""{"step":3,"kind":"mixMute","mix":"stream","count":1,"label":"Stream mute"}""",
            System.Text.Json.JsonSerializer.Serialize(new HistoryStepInfo(3, "mixMute", null, "stream", null, 1, "Stream mute"), options));
    }

    [Fact]
    public void BeginClosesTheNewestStepSoTheNextCommandStartsAnother()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5)]);
        (HistoryStep step, SceneChange[] changes, string label) = history.Begin(redo: false)!.Value;
        Assert.Equal(("x", 1), (label, changes.Length));
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", .5, .4)]);
        Assert.Equal(2, history.State().UndoSteps);
        Assert.Single(step.Changes);
        Assert.Null(History().Begin(redo: true));
    }

    [Fact]
    public void AStepABarrierRemovedDuringItsUndoStaysGone()
    {
        MixHistory history = History();
        history.Record("a", Send("xlr1", "monitor"), "x", [Level("xlr1|monitor", 1, .5)]);
        HistoryStep step = history.Peek(redo: false)!;
        history.Clear();
        history.Complete(step, redo: false, step.Changes, null);
        Assert.Equal((0, 0), (history.State().UndoSteps, history.State().RedoSteps));
    }
}
