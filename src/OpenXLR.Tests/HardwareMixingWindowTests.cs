using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Hardware mixing in the UI, from fixed daemon states: the "Runs on" choice
/// in Options' AUDIO card shows on a Wave XLR Pro only and lights the
/// daemon's side; This unit opens the confirm window, which runs the dry run
/// and says so when the daemon does not answer; the confirm window lists
/// what the layout loses and each jack's drop inside its own width, and has
/// no switch for a layout over the pair budget; while the mode runs the
/// SUBMIXER badge shows, the mix chain, Mic setup and the soundboard take the
/// daemon's sentences, and Options says what latency compensation leaves
/// out. Run from the window layout test, on its UI thread.
/// </summary>
internal static class HardwareMixingWindowTests
{
    private const string LatencySentence = "Latency compensation leaves out the mixes the Wave XLR Pro builds.";
    private const string InsertSentence = "The Wave XLR Pro builds Monitor A for its jacks, so no insert runs on it there.";
    private const string SoundCheckSentence = "Mic setup's checks are not available while the Wave XLR Pro builds the mixes.";
    private const string AuditionSentence = "Auditions are not available while the Wave XLR Pro builds the mixes.";

    private static JsonNode State(bool capable, JsonObject? mode, bool built = false) => JsonNode.Parse($$"""
        {
          "type": "state", "connected": true, "daemonVersion": "0.5.1",
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 2, "hpOutputs": 2, "outputRouting": true, "hardwareMixing": {{(capable ? "true" : "false")}} },
          "state": { "gainDb": 40, "mute": false },
          "devices": [], "profiles": [],
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor", "hardwareSection": {{(built ? "0" : "null")}} },
              { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "hardware": true, "microphone": true, "levels": { "monitor": 1, "stream": 1 }, "mutedIn": [] },
              { "id": "game", "name": "Game", "levels": { "monitor": 1, "stream": 1 }, "mutedIn": [], "volume": 1 }
            ],
            "monitorOutputs": [], "monitorFeeds": {}, "inserts": {}
          }
        }
        """)!.Also(node => { if (mode is not null) node["mixer"]!["hardwareMixing"] = mode; });

    private static JsonNode Also(this JsonNode node, Action<JsonNode> change)
    {
        change(node);
        return node;
    }

    private static JsonObject Running() => new()
    {
        ["wanted"] = true, ["active"] = true, ["reason"] = null, ["note"] = null,
        ["disabled"] = new JsonObject
        {
            ["inserts:mix:monitor"] = InsertSentence, ["latencyCompensation"] = LatencySentence,
            ["soundCheck"] = SoundCheckSentence, ["audition"] = AuditionSentence,
        },
    };

    internal static void Check()
    {
        UiSettings previous = UiSettings.Load();
        var main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            StopConnecting(main);
            var vm = (MainViewModel)main.DataContext!;
            MethodInfo apply = typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(JsonNode)])!;
            void Push(JsonNode state) { apply.Invoke(vm, [state]); Dispatcher.UIThread.RunJobs(); }

            // A unit that cannot run it: no choice in Options.
            Push(State(capable: false, mode: null));
            main.Show();
            Layout(main, 1040, 900);
            main.FindControl<Button>("OptionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            OptionsWindow options = Assert.Single(main.OwnedWindows.OfType<OptionsWindow>());
            OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
            Settle(options);
            Assert.False(options.FindControl<StackPanel>("HardwareMixing")!.IsEffectivelyVisible);

            // The Pro, mode off: the choice shows with this computer lit, inside the card.
            Push(State(capable: true, mode: new JsonObject { ["wanted"] = false, ["active"] = false }));
            Settle(options);
            var unit = options.FindControl<ToggleButton>("HardwareMixingUnit")!;
            var computer = options.FindControl<ToggleButton>("HardwareMixingComputer")!;
            Assert.True(unit.IsEffectivelyVisible);
            Assert.False(unit.IsChecked);
            Assert.True(computer.IsChecked);
            Border card = unit.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
            AssertInside(unit, card);
            AssertInside(computer, card);
            AssertInside(options.FindControl<StackPanel>("HardwareMixing")!, card);
            Assert.False(main.FindControl<Border>("HardwareMixingBadge")!.IsEffectivelyVisible);
            Capture(options, "hardware-mixing-options");

            // This unit opens the confirm window and runs the dry run, which no daemon answers here.
            unit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HardwareMixingWindow confirm = WaitFor(() => options.OwnedWindows.OfType<HardwareMixingWindow>().FirstOrDefault());
            var model = (HardwareMixingConfirmViewModel)confirm.DataContext!;
            WaitFor(() => model.Status == Localizer.Text("HardwareMixingCheckFailed") ? model : null);
            Assert.False(confirm.FindControl<Button>("HardwareMixingConfirmSwitch")!.IsEnabled);
            Assert.False(unit.IsChecked);
            confirm.Close();
            Dispatcher.UIThread.RunJobs();

            // The confirm window with a dry run: what changes, each jack's drop, all inside it.
            var plan = new HardwareMixingConfirmViewModel(vm.HardwareMixing);
            var window = new HardwareMixingWindow(plan);
            window.Show(options);
            plan.Show(PlanFrom("""
                { "type": "mixingPlan", "wanted": true, "active": true, "reason": null, "note": null,
                  "disabled": { "audition": "Auditions are not available while the Wave XLR Pro builds the mixes.",
                                "processedMic:xlr1": "The Wave XLR Pro's mixes take XLR 1 as it comes in, without the processing on this computer.",
                                "soundCheck": "Mic setup's checks are not available while the Wave XLR Pro builds the mixes." },
                  "entryOffsetsDb": { "hp1": 21, "hp2": 28.94, "lineout": 21 } }
                """));
            Settle(window);
            Assert.Equal(6, plan.Lines.Count);
            Assert.Contains(Localizer.Format("HardwareMixingJackDrop", Localizer.Text("HardwareMixingJackHp2"), 28.9.ToString("0.#")), plan.Lines);
            Assert.True(window.FindControl<Button>("HardwareMixingConfirmSwitch")!.IsEnabled);
            foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }))
                AssertInside(text, window);
            Capture(window, "hardware-mixing-confirm");

            // Over the pair budget: the daemon's sentence and no switch.
            plan.Show(PlanFrom("""
                { "type": "mixingPlan", "wanted": true, "active": false, "reason": "budget",
                  "note": "Hardware mixing on the Wave XLR Pro streams at most 7 channels; this layout has 8 that need a USB pair.",
                  "disabled": {}, "entryOffsetsDb": {} }
                """));
            Settle(window);
            Assert.False(window.FindControl<Button>("HardwareMixingConfirmSwitch")!.IsEnabled);
            var note = window.FindControl<TextBlock>("HardwareMixingConfirmNote")!;
            Assert.True(note.IsEffectivelyVisible);
            Assert.Contains("at most 7 channels", note.Text, StringComparison.Ordinal);
            Assert.Contains("warn", note.Classes);
            window.Close();

            // Wanted and waiting: the daemon's reason under the switch, the unit's side lit.
            Push(State(capable: true, mode: new JsonObject
            {
                ["wanted"] = true, ["active"] = false, ["reason"] = "unitAway", ["note"] = "The Wave XLR Pro is not connected.",
            }));
            Settle(options);
            Assert.True(unit.IsChecked);
            Assert.False(computer.IsChecked);
            Assert.Equal("The Wave XLR Pro is not connected.", options.FindControl<TextBlock>("HardwareMixingNote")!.Text);
            Assert.False(main.FindControl<Border>("HardwareMixingBadge")!.IsEffectivelyVisible);

            // Running: the badge, the chain's note and tip, Mic setup and auditions held, latency's note.
            Push(State(capable: true, mode: Running(), built: true));
            Settle(options);
            Settle(main);
            Assert.True(main.FindControl<Border>("HardwareMixingBadge")!.IsEffectivelyVisible);
            AssertInside(main.FindControl<Border>("HardwareMixingBadge")!, main);
            MixViewModel monitor = vm.Mixes.Single(m => m.Id == "monitor");
            Assert.Equal(0, monitor.HardwareSection);
            Assert.Equal(InsertSentence, monitor.Inserts.UnitNote);
            Assert.Equal(InsertSentence, monitor.Inserts.ButtonTip);
            Assert.False(monitor.Inserts.CanAddPlugins);
            Assert.Null(vm.Mixes.Single(m => m.Id == "stream").Inserts.UnitNote);
            Assert.All(vm.MicSetups(), setup => Assert.Equal(SoundCheckSentence, setup.UnitNote));
            Assert.False(vm.Inserts.MicSetup.CanRecord);
            Assert.Equal(AuditionSentence, vm.Soundboard.AuditionNote);
            var latency = options.FindControl<TextBlock>("MixLatencyUnitNote")!;
            Assert.True(latency.IsEffectivelyVisible);
            Assert.Equal(LatencySentence, latency.Text);
            AssertInside(latency, card);

            var chain = new MixInsertsWindow { DataContext = monitor.Inserts };
            chain.Show(main);
            Settle(chain);
            var chainNote = chain.FindControl<TextBlock>("MixInsertsUnitNote")!;
            Assert.True(chainNote.IsEffectivelyVisible);
            AssertInside(chainNote, chain);
            Assert.False(chain.FindControl<Button>("AddInsert")!.IsEnabled);
            chain.Close();

            // Back to this computer: everything is released.
            Push(State(capable: true, mode: new JsonObject { ["wanted"] = false, ["active"] = false }));
            Assert.Null(monitor.Inserts.UnitNote);
            Assert.True(monitor.Inserts.CanAddPlugins);
            Assert.Null(vm.Soundboard.AuditionNote);
            Assert.All(vm.MicSetups(), setup => Assert.Null(setup.UnitNote));
            Assert.Null(monitor.HardwareSection);
            options.Close();
        }
        finally { main.Quit(); Assert.Null(previous.Save()); }
    }

    private static HardwareMixingPlan PlanFrom(string json) => HardwareMixingPlan.From(JsonNode.Parse(json)!);

    private static T WaitFor<T>(Func<T?> found) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (found() is { } value) return value;
            Thread.Sleep(5);
        }
        throw new Xunit.Sdk.XunitException("The window did not reach the expected state.");
    }
}
