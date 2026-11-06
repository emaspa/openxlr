using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// The audio clock in the open windows, run inside the skin test's process
/// (Avalonia sets its platform up once per process): Flow names the clock
/// and the resampled microphone under its title, and the main window's
/// warning offers the manual section and the action that makes the
/// microphone's interface the clock beside the daemon's sentence, and the
/// Audio tab of Options holds the clock choice with the restart while the
/// choice waits for one. It runs last, since it replaces the fixture's
/// state with a daemon's.
/// </summary>
internal static class AudioClockWindowTests
{
    private const string Mic = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.mono-fallback";
    private const string Card = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";

    internal static void Check(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        var vm = (MainViewModel)main.DataContext!;
        var clock = new MixerClock(new ClockDriver(Card, "Elgato 4K X", 2109) { Role = "capture" },
            [new ClockFollower(Mic, "Wave XLR", "unitInput", ["xlr1"], true, 2100, true)]);
        string warning = clock.Warning()!;
        Apply(vm, State(clock, warning));
        Assert.False(main.FindControl<Button>("MakeClock")!.IsVisible, "an older daemon offers no clock choice");
        Apply(vm, State(clock, warning, Choice(null, pending: false)));

        TextBlock line = flow.FindControl<TextBlock>("FlowClock")!, alert = flow.FindControl<TextBlock>("FlowClockWarning")!;
        Assert.True(line.IsVisible);
        Assert.Equal(Localizer.Format("FlowClock", "Elgato 4K X"), line.Text);
        Assert.True(alert.IsVisible);
        Assert.Equal(Localizer.Format("FlowClockResampled", "Wave XLR"), alert.Text);
        Button manual = main.FindControl<Button>("ClockManual")!;
        Assert.True(manual.IsEffectivelyVisible);
        Assert.False(main.FindControl<Button>("CardProfileManual")!.IsVisible);
        Assert.Contains(main.GetVisualDescendantsOfType<TextBlock>(), t => t.Text == warning && t.IsEffectivelyVisible);

        // The action names the microphone's interface and asks before
        // anything changes: restart now, later, or cancel.
        Button make = main.FindControl<Button>("MakeClock")!;
        Assert.True(make.IsEffectivelyVisible);
        Assert.Equal(Localizer.Format("MakeDeviceTheClock", "Wave XLR"), make.Content);
        Window? asked = null;
        bool opened = false, closed = false;
        using var done = new CancellationTokenSource();
        void Answer(Window dialog)
        {
            asked = dialog;
            Button[] buttons = [.. dialog.GetLogicalDescendants().OfType<Button>()];
            Assert.Contains(buttons, b => b.Name == "MakeClockLater");
            Assert.Contains(buttons, b => b.Name == "MakeClockRestart");
            // Answered once it shows; a window closed before it shows cannot be shown.
            dialog.Opened += (_, _) =>
            {
                opened = true;
                Dispatcher.UIThread.Post(() => buttons.Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            };
            dialog.Closed += (_, _) => { closed = true; done.Cancel(); };
        }
        Dialogs.ConfirmOpening += Answer;
        try
        {
            make.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (DispatcherTimer.RunOnce(done.Cancel, TimeSpan.FromSeconds(10))) Dispatcher.UIThread.MainLoop(done.Token);
            Assert.True(opened && closed, "the clock question did not show and close");
        }
        finally { Dialogs.ConfirmOpening -= Answer; }
        Assert.Equal(Localizer.Format("MakeDeviceTheClock", "Wave XLR"), Assert.IsType<Window>(asked).Title);
        Assert.Contains(Localizer.Format("MakeClockConfirm", "Wave XLR"), asked!.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

        // Options, Audio: Automatic naming the device that drives now, then
        // the devices, in the AUDIO card, nothing waiting.
        OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
        Dispatcher.UIThread.RunJobs();
        ComboBox picker = options.FindControl<ComboBox>("AudioClock")!;
        Assert.True(picker.IsEffectivelyVisible);
        Assert.Equal([Localizer.Format("AudioClockAutomaticDriver", "Elgato 4K X"), Localizer.Format("AudioClockInput", "Wave XLR"),
            Localizer.Format("AudioClockInput", "Elgato 4K X")], picker.Items.OfType<AudioClockOptionItem>().Select(o => o.Label));
        Assert.Equal(0, picker.SelectedIndex);
        Assert.False(options.FindControl<WrapPanel>("AudioClockPending")!.IsVisible);
        Border card = picker.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
        Assert.Contains(card.GetVisualDescendantsOfType<TextBlock>(), t => t.Text == Localizer.Text("AUDIO"));

        // Chosen and waiting for the restart: the note and the button show, and
        // the warning still offers the action, which then restarts.
        Apply(vm, State(clock, warning, Choice(Mic, pending: true)));
        options.UpdateLayout();
        Assert.Equal(1, picker.SelectedIndex);
        Assert.True(options.FindControl<WrapPanel>("AudioClockPending")!.IsEffectivelyVisible);
        Assert.True(options.FindControl<Button>("RestartAudio")!.IsEffectivelyVisible);
        Assert.True(make.IsVisible);

        // Chosen and applied while the microphone still follows: no action to offer.
        Apply(vm, State(clock, warning, Choice(Mic, pending: false)));
        Assert.False(make.IsVisible);
        Assert.False(options.FindControl<WrapPanel>("AudioClockPending")!.IsVisible);

        // The Flatpak's daemon reports no choice: Options shows none.
        Apply(vm, State(clock, warning));
        options.UpdateLayout();
        Assert.False(options.FindControl<StackPanel>("AudioClockChoice")!.IsVisible);

        // The unit as the clock: the line stays, the warning and the button go.
        Apply(vm, State(new MixerClock(new ClockDriver(Mic, "Wave XLR", 2200) { Role = "unitInput" },
            [new ClockFollower(Card, "Elgato 4K X", "capture", ["console"], false, 2109, true)]), ""));
        Assert.Equal(Localizer.Format("FlowClock", "Wave XLR"), line.Text);
        Assert.False(alert.IsVisible);
        Assert.False(manual.IsVisible);

        // An older daemon reports no clock, and nothing is drawn for it.
        Apply(vm, State(null, ""));
        Assert.False(line.IsVisible);
        Assert.False(alert.IsVisible);
    }

    private static AudioClockChoice Choice(string? node, bool pending)
        => new(node, pending, false, [AudioClockOption.For(Mic, "Wave XLR"), AudioClockOption.For(Card, "Elgato 4K X")]);

    private static string State(MixerClock? clock, string warning, AudioClockChoice? choice = null)
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string extra = (clock is null ? "" : ",\"clock\":" + JsonSerializer.Serialize(clock, web))
            + (choice is null ? "" : ",\"clockChoice\":" + JsonSerializer.Serialize(choice, web));
        JsonNode node = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)), mixerExtra: extra))!;
        if (warning.Length > 0) node["warning"] = warning;
        return node.ToJsonString();
    }

    private static void Apply(MainViewModel vm, string state)
    {
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [JsonNode.Parse(state)!]);
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : Avalonia.Visual
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
