using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;
using Path = Avalonia.Controls.Shapes.Path;

namespace OpenXLR.Tests;

/// <summary>
/// The header's undo and redo pair on a real window: hidden with a daemon
/// from before undo, a side with nothing on it disabled and drawn as
/// disabled while its tooltip still says why, the step named in the
/// tooltip, a click and Ctrl+Z sending the step the UI showed, and the
/// flyout that names it for three seconds, under the pair or under the
/// overflow menu's button when a skin put the pair there. Run from the
/// window layout test, on its UI thread.
/// </summary>
internal static class HistoryButtonsWindowTests
{
    internal static void Check()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4}}]}
                """)]);
            window.Show();
            Layout(window, 1040, 900);

            var pair = window.FindControl<StackPanel>("HistoryButtons")!;
            var undo = window.FindControl<Button>("UndoButton")!;
            var redo = window.FindControl<Button>("RedoButton")!;
            var actions = window.FindControl<WrapPanel>("HeaderActions")!;
            Assert.Same(pair, actions.Children[0]);
            // A daemon from before undo reports no history: no pair.
            Assert.False(pair.IsEffectivelyVisible);

            model.History.Apply(JsonNode.Parse("""
                {"undo":{"step":7,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume"},
                 "redo":null,"undoSteps":1,"redoSteps":0,"note":null}
                """));
            Layout(window, 1040, 900);
            Assert.True(pair.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Undo"), AutomationProperties.GetName(undo));
            Assert.Equal(Localizer.Text("Redo"), AutomationProperties.GetName(redo));
            Assert.True(undo.IsEnabled);
            Assert.False(redo.IsEnabled);
            string stream = Localizer.Format("HistoryMixVolume", "Stream");
            Assert.Equal(Localizer.Format("UndoStep", stream), ToolTip.GetTip(undo));
            Assert.Equal(Localizer.Text("NothingToRedo"), ToolTip.GetTip(redo));
            Assert.True(ToolTip.GetShowOnDisabled(redo));
            // The pair stands in the row's line, as tall as its neighbours.
            var flow = window.FindControl<Button>("FlowButton")!;
            Assert.Equal(flow.Bounds.Height, undo.Bounds.Height, tolerance: 1);
            Assert.Equal(flow.TranslatePoint(default, window)!.Value.Y, undo.TranslatePoint(default, window)!.Value.Y, tolerance: 1);

            // The arrows are drawn in each button's lettering colour, so the
            // disabled one looks disabled the way a disabled label does.
            IBrush Lettering(Button button) => button.GetVisualDescendants().OfType<ContentPresenter>()
                .First(p => p.Name == "PART_ContentPresenter").Foreground!;
            Path undoGlyph = window.FindControl<Path>("UndoGlyph")!, redoGlyph = window.FindControl<Path>("RedoGlyph")!;
            Assert.Same(Lettering(undo), undoGlyph.Stroke);
            Assert.Same(Lettering(redo), redoGlyph.Stroke);
            Assert.True(undoGlyph.Bounds.Width > 0 && undoGlyph.Bounds.Height > 0);
            Color Seen(Button button, Path glyph) => ((ISolidColorBrush)glyph.Stroke!).Color is var c
                ? Color.FromArgb((byte)(c.A * glyph.Opacity * button.Opacity), c.R, c.G, c.B) : default;
            Assert.NotEqual(Seen(undo, undoGlyph), Seen(redo, redoGlyph));

            // Redo becomes possible: enabled, named, and drawn like undo.
            model.History.Apply(JsonNode.Parse("""
                {"undo":{"step":7,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume"},
                 "redo":{"step":6,"kind":"sendMute","channel":"music","mix":"monitor","count":1,"label":"Music mute in Monitor A"},
                 "undoSteps":1,"redoSteps":1,"note":null}
                """));
            Dispatcher.UIThread.RunJobs();
            Assert.True(redo.IsEnabled);
            Assert.Equal(Localizer.Format("RedoStep", Localizer.Format("HistorySendMute", "Music", "Monitor A")), ToolTip.GetTip(redo));
            Assert.Equal(((ISolidColorBrush)undoGlyph.Stroke!).Color, ((ISolidColorBrush)redoGlyph.Stroke!).Color);

            // A click sends the step the button named, and the flyout names it until its hold ends.
            var sent = new List<(string Command, long Step)>();
            var holds = new List<Action>();
            model.History.Send = (command, step) => { sent.Add((command, step)); return Task.FromResult<string?>(null); };
            model.History.StartNoticeHold = (_, elapsed) => { holds.Add(elapsed); return new Hold(); };
            var flyout = (Flyout)FlyoutBase.GetAttachedFlyout(pair)!;
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([("undo", 7L)], sent);
            Assert.True(flyout.IsOpen);
            Assert.Same(pair, flyout.Target);
            var notice = Assert.IsType<TextBlock>(flyout.Content);
            Assert.Equal(Localizer.Format("UndidStep", stream), notice.Text);
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal(FlyoutShowMode.Transient, flyout.ShowMode);
            Assert.True(flyout.OverlayDismissEventPassThrough);
            holds[^1]();
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);

            // Ctrl+Shift+Z on the window does the same for redo.
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift, Source = window });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(("redo", 6L), sent[^1]);
            Assert.Equal(Localizer.Format("RedidStep", Localizer.Format("HistorySendMute", "Music", "Monitor A")), notice.Text);
            Assert.True(flyout.IsOpen);
            holds[^1]();
            Dispatcher.UIThread.RunJobs();

            // A skin that puts the pair in the overflow menu: the notice points at the menu's button.
            MethodInfo header = typeof(MainWindow).GetMethod("ApplyHeaderActions", BindingFlags.NonPublic | BindingFlags.Instance)!;
            header.Invoke(window, [new SkinHeaderOptions([], [SkinHeaderActions.History])]);
            Layout(window, 1040, 900);
            var overflow = window.FindControl<DropDownButton>("HeaderOverflow")!;
            Assert.Same(window.FindControl<StackPanel>("HeaderOverflowItems"), pair.Parent);
            Assert.True(overflow.IsEffectivelyVisible);
            Assert.Same(overflow, window.HistoryNoticeAnchor());
            model.History.Send = (_, _) => Task.FromResult<string?>("undo: the history changed");
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
            Dispatcher.UIThread.RunJobs();
            Assert.True(flyout.IsOpen);
            Assert.Same(overflow, flyout.Target);
            Assert.Equal("undo: the history changed", notice.Text);
            holds[^1]();
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            header.Invoke(window, [SkinHeaderOptions.Default]);
            Layout(window, 1040, 900);
            Assert.Same(actions, pair.Parent);
            Assert.Same(pair, window.HistoryNoticeAnchor());
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class Hold : IDisposable
    {
        public void Dispose() { }
    }
}
