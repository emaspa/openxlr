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
        Assert.Equal(new HistoryStepInfo(1, "sendLevel", "xlr1", "monitor", null, 1, "Mic level in Monitor") { From = .5, To = 1.0 },
            state.Undo);
        Assert.Null(state.UndoNext);
        Assert.Null(state.RedoNext);
    }

    [Fact]
    public void TheStateNamesUpToThreeStepsAfterEachEndInTheOrderTheyWouldBeTaken()
    {
        MixHistory history = History();
        for (int i = 1; i <= 6; i++)
        {
            history.Record("a", Send($"ch{i}", "monitor"), $"Step {i}", [Level($"ch{i}|monitor", 1, .5)]);
            _now += 2000;
        }
        HistoryState state = history.State();
        Assert.Equal(6, state.Undo!.Step);
        Assert.Equal([5L, 4L, 3L], state.UndoNext!.Select(s => s.Step));
        Assert.Equal(MixHistory.NextSteps, state.UndoNext!.Count);
        Assert.Null(state.RedoNext);

        // Two undone: redo takes 5 first, then 6.
        for (int i = 0; i < 2; i++)
        {
            var (step, changes, _) = history.Begin(redo: false)!.Value;
            history.Complete(step, redo: false, changes, null);
        }
        state = history.State();
        Assert.Equal(5, state.Redo!.Step);
        Assert.Equal([6L], state.RedoNext!.Select(s => s.Step));
        Assert.Equal(4, state.Undo!.Step);
        Assert.Equal([3L, 2L, 1L], state.UndoNext!.Select(s => s.Step));
        // The steps after the ends carry their values in the direction they would be taken.
        Assert.Equal((.5, 1.0), ((double)state.UndoNext![0].From!, (double)state.UndoNext[0].To!));
        Assert.Equal((1.0, .5), ((double)state.RedoNext![0].From!, (double)state.RedoNext[0].To!));
    }

    [Fact]
    public void AStepOfOneNumberOrSwitchNamesTheValuesUndoAndRedoMoveBetween()
    {
        MixHistory history = History();
        history.Record("a", new HistoryTarget("mixMute", null, "stream", null, [SceneField.MixMuted], _ => true), "Stream mute",
            [new SceneChange(new(SceneField.MixMuted, "stream"), false, true)]);
        HistoryStepInfo undo = history.State().Undo!;
        Assert.Equal(true, undo.From);
        Assert.Equal(false, undo.To);

        var (step, changes, _) = history.Begin(redo: false)!.Value;
        history.Complete(step, redo: false, changes, null);
        HistoryStepInfo redo = history.State().Redo!;
        Assert.Equal(false, redo.From);
        Assert.Equal(true, redo.To);

        // A hardware value: the gain, an int in dB.
        _now += 2000;
        history.Record("a", new HistoryTarget("unitGain", "xlr1", null, null, [], _ => false) { Control = "gain" }, "XLR 1 gain", [],
            [new DeviceChange("unit-1", "Wave XLR", "gain", 28, 32)]);
        undo = history.State().Undo!;
        Assert.Equal(32, undo.From);
        Assert.Equal(28, undo.To);

        var options = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(undo, options);
        Assert.Contains("\"from\":32,\"to\":28", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepOfSeveralValuesOrOfAWholeSettingNamesNoValues()
    {
        MixHistory history = History();
        // A burst over two sends.
        history.Record("deck", Send("xlr1", "monitor"), "Mic level in Monitor", [Level("xlr1|monitor", 1, .5)]);
        _now += 30;
        history.Record("deck", Send("xlr1", "stream"), "Mic level in Stream", [Level("xlr1|stream", 1, .5)]);
        HistoryStepInfo burst = history.State().Undo!;
        Assert.Equal(2, burst.Count);
        Assert.Null(burst.From);
        Assert.Null(burst.To);

        // One command that changed two values, as an exclusive group does.
        _now += 2000;
        history.Record("a", new HistoryTarget("exclusiveGroup", null, null, "sources", [SceneField.ChannelMuted], _ => true), "Sources switch",
            [new SceneChange(new(SceneField.ChannelMuted, "a|stream"), true, false), new SceneChange(new(SceneField.ChannelMuted, "b|stream"), false, true)]);
        Assert.Null(history.State().Undo!.From);

        // A whole setting is not a plain value.
        _now += 2000;
        history.Record("a", new HistoryTarget("eq", "xlr1", null, null, [SceneField.Eq], _ => true), "XLR 1 EQ",
            [new SceneChange(new(SceneField.Eq, "xlr1"), new MicEq(), new MicEq { Enabled = true })]);
        Assert.Null(history.State().Undo!.From);
        Assert.Null(history.State().Undo!.To);
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
