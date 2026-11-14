using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The UI's undo keys: which presses step the history, and that a text field keeps its own.</summary>
public sealed class HistoryKeysTests
{
    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control, "Undo")]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, "Redo")]
    [InlineData(Key.Y, KeyModifiers.Control, "Redo")]
    [InlineData(Key.Z, KeyModifiers.None, "None")]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Alt, "None")]
    [InlineData(Key.Y, KeyModifiers.Control | KeyModifiers.Shift, "None")]
    public void TheKeysAreCtrlZCtrlShiftZAndCtrlY(Key key, KeyModifiers modifiers, string press)
        => Assert.Equal(press, HistoryKeys.Match(key, modifiers, source: null).ToString());

    [Fact]
    public void CtrlZInsideATextFieldSendsNothing()
    {
        var steps = new List<bool>();
        TestUi.Run(() =>
        {
            var text = new TextBox();
            var number = new NumericUpDown();
            var slider = new Slider();
            var root = new StackPanel { Children = { text, number, slider } };
            HistoryKeys.Attach(root, steps.Add);

            Press(text, Key.Z, KeyModifiers.Control);
            Press(number, Key.Y, KeyModifiers.Control);
            Assert.Empty(steps);

            Press(slider, Key.Z, KeyModifiers.Control);
            Press(slider, Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
            Press(root, Key.Y, KeyModifiers.Control);
        });
        Assert.Equal([false, true, true], steps);
    }

    [Fact]
    public async Task TheModelSendsTheStepItShowsAndNothingWhenThereIsNone()
    {
        var sent = new List<(string Command, long Step)>();
        var model = new HistoryViewModel((command, step) =>
        {
            sent.Add((command, step));
            return Task.FromResult<string?>(command == "redo" ? "redo: the history changed" : null);
        });
        Assert.Null(await model.RunAsync(redo: false));
        Assert.Empty(sent);
        Assert.False(model.IsReported);

        model.Apply(JsonNode.Parse("""
            {"undo":{"step":4,"kind":"sendLevel","channel":"xlr1","mix":"monitor","count":1,"label":"XLR 1 level in Monitor A"},
             "redo":{"step":3,"kind":"mixMute","mix":"stream","count":1,"label":"Stream mute"},
             "undoSteps":2,"redoSteps":1,"note":"Undo skipped part of Stream mute: it changed since."}
            """));
        Assert.True(model.IsReported);
        Assert.True(model.CanUndo && model.CanRedo);
        Assert.Equal("Undo skipped part of Stream mute: it changed since.", await model.RunAsync(redo: false));
        Assert.Equal("redo: the history changed", await model.RunAsync(redo: true));
        Assert.Equal([("undo", 4L), ("redo", 3L)], sent);

        model.Apply(JsonNode.Parse("""{"undo":null,"redo":null,"undoSteps":0,"redoSteps":0,"note":null}"""));
        Assert.False(model.CanUndo);
        Assert.Null(await model.RunAsync(redo: false));
        Assert.Equal(2, sent.Count);
    }

    private static void Press(Control target, Key key, KeyModifiers modifiers)
        => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });
}
