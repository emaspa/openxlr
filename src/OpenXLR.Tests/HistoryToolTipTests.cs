using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Resting the pointer on the header's undo or redo button names the step it
/// would take with the value it goes back to, and on a second line the steps
/// after it; a side with nothing on it says so even while disabled. A
/// real pointer moves through XTEST, as in <see cref="ToolTipInputTests"/>,
/// because a hover posted into the window would not show what the user sees.
/// </summary>
[Collection("xdg-config")]
public sealed class HistoryToolTipTests
{
    private const string DeadDaemon = "ws://127.0.0.1:37899/ws";

    [ToolTipFact]
    public void TheUndoAndRedoButtonsNameTheirStepOnHover()
    {
        string config = Path.Combine(Path.GetTempPath(), "openxlr-history-tip-" + Guid.NewGuid());
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        MainWindow? main = null;
        MainViewModel? vm = null;
        var ready = new TaskCompletionSource();
        var quit = new CancellationTokenSource();
        Exception? failure = null;

        var ui = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                WindowLayoutTests.PinMaterial();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                main = new MainWindow(new DaemonClient(DeadDaemon));
                vm = new MainViewModel(new DaemonClient(DeadDaemon));
                main.DataContext = vm;
                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [JsonNode.Parse("""
                        {"mixes":[{"id":"stream","name":"Stream","kind":"virtualMic"},{"id":"monitor","name":"Monitor A","kind":"monitor"}],
                         "channels":[],"inserts":{}}
                        """)]);
                vm.History.Apply(JsonNode.Parse("""
                    {"undo":{"step":7,"kind":"mixVolume","mix":"stream","count":1,"label":"Stream volume","from":1.0,"to":0.75},
                     "undoNext":[{"step":6,"kind":"mixMute","mix":"monitor","count":1,"label":"Monitor A mute"},
                                 {"step":5,"kind":"mixVolume","mix":"monitor","count":1,"label":"Monitor A volume"},
                                 {"step":4,"kind":"mixMute","mix":"stream","count":1,"label":"Stream mute"}],
                     "redo":null,"undoSteps":6,"redoSteps":0,"note":null}
                    """));
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Position = new PixelPoint(40, 40);
                main.Width = 1400;
                main.Height = 700;
                main.Show();
                ready.SetResult();
                Dispatcher.UIThread.MainLoop(quit.Token);
            }
            catch (Exception ex) { failure = ex; ready.TrySetResult(); }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            }
        }) { IsBackground = true };
        ui.Start();
        Assert.True(ready.Task.Wait(TimeSpan.FromSeconds(60)), "The window never opened.");
        if (failure is not null) throw failure;

        using var pointer = new XPointer();
        try
        {
            Thread.Sleep(500);
            Button undo = Ui(() => main!.FindControl<Button>("UndoButton")!);
            Button redo = Ui(() => main!.FindControl<Button>("RedoButton")!);
            Assert.True(Ui(() => undo.IsEffectivelyVisible && undo.IsEnabled), "The undo button is not shown and enabled.");
            Assert.False(Ui(() => redo.IsEnabled));

            Hover(pointer, main!, undo);
            Assert.True(Wait(() => Ui(() => ToolTip.GetIsOpen(undo))), "No tooltip appeared over the undo button.");
            string step = Localizer.Format("UndoStep",
                Localizer.Format("HistoryUndoValues", Localizer.Format("HistoryMixVolume", "Stream"), "100%", "75%"));
            string next = Localizer.Format("HistoryThenMore", string.Join(", ", Localizer.Format("HistoryMixMute", "Monitor A"),
                Localizer.Format("HistoryMixVolume", "Monitor A"), Localizer.Format("HistoryMixMute", "Stream")), 2);
            Assert.Equal(step + "\n" + next, Ui(() => ToolTip.GetTip(undo) as string));
            // The open tooltip draws the step and its values on one line and the next steps below it.
            TextBlock shown = Ui(() => Shown(undo));
            Assert.Equal(step + "\n" + next, Ui(() => shown.Text));
            Assert.True(Ui(() => shown.TextLayout.TextLines.Count) >= 2, "The tooltip drew the next steps on the step's line.");
            Assert.True(Ui(() => shown.Bounds.Width > 0 && shown.IsEffectivelyVisible), "The tooltip's text is not shown.");

            Hover(pointer, main!, redo);
            Assert.True(Wait(() => Ui(() => ToolTip.GetIsOpen(redo))), "No tooltip appeared over the disabled redo button.");
            Assert.Equal(Localizer.Text("NothingToRedo"), Ui(() => ToolTip.GetTip(redo) as string));
        }
        finally
        {
            Ui(() => { main!.Close(); return true; });
            quit.Cancel();
            ui.Join(TimeSpan.FromSeconds(10));
            try { Directory.Delete(config, recursive: true); } catch (IOException) { }
        }
    }

    // The tooltip the framework opened over a control, and the text it draws.
    private static TextBlock Shown(Control target)
    {
        var property = (AvaloniaProperty)typeof(ToolTip).GetField("ToolTipProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var tip = Assert.IsType<ToolTip>(target.GetValue(property));
        return Assert.Single(tip.GetVisualDescendants().OfType<TextBlock>(), t => !string.IsNullOrEmpty(t.Text));
    }

    private static void Hover(XPointer pointer, MainWindow main, Control target)
    {
        // Park the pointer clear of the window first, so entering the control is a fresh hover.
        PixelRect screen = Ui(() => (main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary ?? main.Screens.All[0]).Bounds);
        pointer.MoveTo(screen.X + screen.Width - 1, screen.Y + screen.Height - 1);
        Thread.Sleep(300);
        PixelRect box = Ui(() => new PixelRect(target.PointToScreen(default), PixelSize.FromSize(target.Bounds.Size, main.RenderScaling)));
        pointer.MoveTo(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    private static T Ui<T>(Func<T> work) => Dispatcher.UIThread.Invoke(work);

    private static bool Wait(Func<bool> done)
    {
        for (int i = 0; i < 80; i++)
        {
            if (done()) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
