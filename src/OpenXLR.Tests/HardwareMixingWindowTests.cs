using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
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
    private const string MaximizerSentence = "The Wave XLR Pro builds Monitor A summed with another mix, so it runs no maximizer on it. Give a jack Monitor A alone to use one.";
    private const string EqSentence = "The Wave XLR Pro builds Monitor A for its jacks, so its EQ is off while hardware mixing runs. It comes back with software mixing.";
    private const string MixEq = """, "eq": { "enabled": true, "bands": [{ "enabled": true, "type": "highShelf", "frequency": 4500, "gain": -4, "q": 0.707 }], "backend": "software", "available": true, "processing": "software" }""";

    private static JsonNode State(bool capable, JsonObject? mode, bool built = false) => JsonNode.Parse($$"""
        {
          "type": "state", "connected": true, "daemonVersion": "0.5.1",
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 2, "hpOutputs": 2, "outputRouting": true, "hardwareMixing": {{(capable ? "true" : "false")}} },
          "state": { "gainDb": 40, "mute": false },
          "devices": [], "profiles": [],
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor", "hardwareSection": {{(built ? "0" : "null")}}, "maximizer": 40{{MixEq}} },
              { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic"{{MixEq}} }
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
            ["inserts:mix:monitor"] = InsertSentence, ["eq:mix:monitor"] = EqSentence, ["latencyCompensation"] = LatencySentence,
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
            // The mix's EQ is held off: its button greyed and dark, the sentence as its tip and status.
            MicEqViewModel monitorEq = monitor.Eq!, streamEq = vm.Mixes.Single(m => m.Id == "stream").Eq!;
            Assert.Equal(EqSentence, monitorEq.UnitNote);
            Assert.Equal(EqSentence, monitorEq.ButtonTip);
            Assert.Equal(EqSentence, monitorEq.Status);
            Assert.False(monitorEq.CanOpen);
            Assert.False(monitorEq.IsLit);
            Assert.True(monitorEq.Enabled);
            Assert.Null(streamEq.UnitNote);
            Assert.True(streamEq.CanOpen);
            Assert.True(streamEq.IsLit);
            Assert.Equal(Localizer.Text("EqButtonTipMix"), streamEq.ButtonTip);
            Assert.All(vm.MicSetups(), setup => Assert.Equal(SoundCheckSentence, setup.UnitNote));
            Assert.False(vm.Inserts.MicSetup.CanRecord);
            Assert.Equal(AuditionSentence, vm.Soundboard.AuditionNote);
            var latency = options.FindControl<TextBlock>("MixLatencyUnitNote")!;
            Assert.True(latency.IsEffectivelyVisible);
            Assert.Equal(LatencySentence, latency.Text);
            AssertInside(latency, card);

            // The unit's maximizer on the mix it builds: its amount, whether
            // its button on the mix's master shows, and the reduction the
            // unit reports.
            Assert.Equal(40, monitor.Maximizer);
            Assert.True(monitor.ShowMaximizer);
            Assert.True(monitor.CanMaximize);
            Assert.True(monitor.MaximizerOn);
            Assert.Equal("40%", monitor.MaximizerText);
            Assert.Equal(Localizer.Text("MaximizerTip"), monitor.MaximizerTip);
            Assert.False(vm.Mixes.Single(m => m.Id == "stream").ShowMaximizer);
            MethodInfo meters = typeof(MainViewModel).GetMethod("ApplyMeters", BindingFlags.Instance | BindingFlags.NonPublic)!;
            meters.Invoke(vm, [JsonNode.Parse("""{"max:monitor":[6.5]}""")!]);
            Assert.Equal(6.5, monitor.MaximizerReduction);
            meters.Invoke(vm, [JsonNode.Parse("""{"mix:monitor":[0.5,0.5]}""")!]);
            Assert.Equal(0, monitor.MaximizerReduction);
            // A matrix skin shows the button in the mix's column head, on the
            // name line where a monitor mix has its dry switch, for the mix the
            // unit builds only, so the column's EQ and effects keep their places.
            Size size = main.ClientSize;
            SkinLayoutWindowTests.Wear("broadcast");
            try
            {
                Layout(main, 1600, 1100);
                Button matrix = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixMixMaximizer" && b.IsEffectivelyVisible);
                Assert.Equal("monitor", ((MatrixColumnViewModel)matrix.DataContext!).Mix.Id);
                Border head = matrix.GetVisualAncestors().OfType<Border>().First(b => b.Name == "MatrixColumn");
                AssertInside(matrix, head);
                TextBlock name = head.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == monitor.Name && t.IsEffectivelyVisible);
                double middle(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), head)!.Value.Y;
                Assert.True(Math.Abs(middle(matrix) - middle(name)) <= 1);
                Button effects = head.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("matrixEffects"));
                Assert.Same(effects.Parent, head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixMixEq").Parent);
                Assert.Equal(2, ((Panel)effects.Parent!).Children.Count);
                Assert.Contains("on", matrix.Classes);
                Assert.Equal(Localizer.Text("Maximizer"), matrix.Content);
                // The amount reads under the chip while the maximizer is on.
                TextBlock level = head.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixMixMaximizerLevel");
                Assert.True(level.IsEffectivelyVisible);
                Assert.Equal(monitor.MaximizerText, level.Text);
                AssertInside(level, head);
                double centre(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, 0), head)!.Value.X;
                Assert.True(Math.Abs(centre(level) - centre(matrix)) <= 1);
                Assert.True(level.TranslatePoint(default, head)!.Value.Y >= matrix.TranslatePoint(new Point(0, matrix.Bounds.Height), head)!.Value.Y - 0.5);
                Assert.Equal(Localizer.Format("MaximizerName", monitor.Name), AutomationProperties.GetName(matrix));
                SkinLayoutWindowTests.CheckMatrixCells(main, main.MixMatrix!, "maximizer");
            }
            finally { SkinLayoutWindowTests.Wear(SkinPackage.DefaultId); Layout(main, size.Width, size.Height); }
            // A mix the unit builds only summed: greyed, with the daemon's sentence.
            JsonObject summed = Running();
            summed["disabled"]!["maximizer:monitor"] = MaximizerSentence;
            Push(State(capable: true, mode: summed, built: true));
            Settle(main);
            Assert.False(monitor.CanMaximize);
            Assert.Equal(MaximizerSentence, monitor.MaximizerTip);
            Push(State(capable: true, mode: Running(), built: true));

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
            Assert.Null(monitorEq.UnitNote);
            Assert.True(monitorEq.CanOpen);
            Assert.True(monitorEq.IsLit);
            Assert.Equal(Localizer.Text("EqStatusSoftwareMix"), monitorEq.Status);
            Assert.Null(vm.Soundboard.AuditionNote);
            Assert.All(vm.MicSetups(), setup => Assert.Null(setup.UnitNote));
            Assert.Null(monitor.HardwareSection);
            Assert.False(monitor.ShowMaximizer);
            Assert.True(monitor.CanMaximize);
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
