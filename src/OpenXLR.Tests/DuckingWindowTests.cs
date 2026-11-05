using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Ducking window against a fake daemon: Options no longer carries a
/// ducking group and opens the window from its AUDIO card, the layout editor
/// opens the same window beside Exclusive groups, every control sends its
/// command and shows the daemon's answer, the key leaves the channel list,
/// "Runs on" follows the unit with the badge and the note, a refused change
/// comes back with its reason, the reduction bar follows the duck meters, the
/// sliders send numbers on a mark's click, a key step and the end of a drag
/// with their marks under the thumb's travel, and the whole window fits at
/// 640 and 2400 logical pixels with nine channels and five mixes, in the font
/// <c>OPENXLR_LAYOUT_FONT</c> names. Run from the window layout test, on its
/// UI thread.
/// </summary>
internal static class DuckingWindowTests
{
    private sealed class Daemon
    {
        public bool HasUnit = true;
        public JsonObject Ducking = new()
        {
            ["enabled"] = false, ["key"] = "xlr1",
            ["channels"] = new JsonArray("game", "music", "browser", "system", "voicechat"), ["mixes"] = new JsonArray("monitor"),
            ["sensitivity"] = "medium", ["amount"] = "medium", ["speed"] = "medium", ["processing"] = "device",
        };

        public JsonNode State()
        {
            JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
            state["type"] = "state";
            state["capabilities"]!["ducking"] = HasUnit;
            JsonObject mixer = DuckingViewModelTests.Mixer();
            mixer["inserts"] = new JsonObject();
            state["mixer"] = mixer;
            var ducking = (JsonObject)Ducking.DeepClone();
            bool keyed = ducking["key"]!.GetValue<string>() == "xlr1";
            bool onUnit = HasUnit && keyed && ducking["processing"]!.GetValue<string>() == "device";
            ducking["backend"] = onUnit ? "device" : "software";
            ducking["available"] = true;
            ducking["error"] = null;
            ducking["device"] = !HasUnit ? null : new JsonObject
            {
                ["unit"] = "0fd9:00b4@1", ["name"] = "Wave XLR Pro", ["firmware"] = null, ["ready"] = keyed,
                ["reason"] = keyed ? null : "keyNotSupported", ["note"] = keyed ? null : "The Wave XLR Pro ducks only with XLR 1 as the key.",
                ["outputs"] = new JsonArray("monitor"), ["held"] = true,
            };
            state["ducking"] = ducking;
            return state;
        }

        /// <summary>Apply a command as the daemon would, or refuse it.</summary>
        public string? Handle(JsonNode cmd)
        {
            JsonNode value = cmd["value"]!;
            if (cmd["cmd"]!.GetValue<string>() == "setDuckingProcessing")
            {
                Ducking["processing"] = value.GetValue<string>();
                return null;
            }
            // Chat is refused, so the window shows a refusal and puts the daemon's mixes back.
            if (value["mixes"] is JsonArray mixes && mixes.Any(m => m!.GetValue<string>() == "chat"))
                return "setDucking: refused for the test";
            foreach ((string field, JsonNode? v) in value.AsObject()) Ducking[field] = v?.DeepClone();
            return null;
        }
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The ducking window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new Daemon();
        FakeSocket? live = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var holder = new FakeSocket(socket, stop);
            await holder.Push(daemon.State());
            live = holder;
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() is not ("setDucking" or "setDuckingProcessing")) continue;
                string? error = daemon.Handle(cmd);
                commands.Enqueue(cmd);
                // As the daemon does: the state first, then the answer to the request.
                await holder.Push(daemon.State());
                if (cmd["requestId"]?.GetValue<string>() is { } id)
                    await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = error });
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
        var layout = new MixerSetupWindow { DataContext = main };
        DuckingWindow? ducking = null;
        try
        {
            options.Show();
            await Until(() => main.Ducking.IsReported && main.Ducking.Channels.Count == 8);

            // The submixer header opens it beside Edit layout.
            window.Show();
            Button fromMixer = Find<Button>(window, "DuckingButton");
            await Until(() => fromMixer.IsEffectivelyVisible);
            fromMixer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(main.Ducking, window.OwnedWindows.OfType<DuckingWindow>().Single().DataContext);

            // Options: the interface card's group is gone, and AUDIO opens the window.
            OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
            Layout(options, OptionsWindow.DesignWidth, 900);
            foreach (string gone in new[] { "Ducking", "DuckingEnabled", "DuckingSensitivity", "DuckingStatus", "DuckingHardwareBadge" })
                Assert.DoesNotContain(options.GetVisualDescendants().OfType<Control>(), c => c.Name == gone);
            Button fromOptions = Find<Button>(options, "DuckingButton");
            Assert.True(fromOptions.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Ducking"), fromOptions.Content);
            AssertInside(fromOptions, options);
            fromOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ducking = options.OwnedWindows.OfType<DuckingWindow>().Single();
            Assert.Same(main.Ducking, ducking.DataContext);

            // The layout editor opens the same window beside Exclusive groups, under itself.
            layout.Show();
            Layout(layout, 760, 640);
            Button fromLayout = Find<Button>(layout, "DuckingButton");
            Button groups = Find<Button>(layout, "ExclusiveGroupsButton");
            Assert.True(fromLayout.IsEffectivelyVisible);
            AssertInside(fromLayout, layout);
            AssertNoOverlap([groups, fromLayout]);
            fromLayout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(options.OwnedWindows.OfType<DuckingWindow>());
            ducking = layout.OwnedWindows.OfType<DuckingWindow>().Single();
            if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
                ducking.FontFamily = new Avalonia.Media.FontFamily(font);

            CheckBox enabled = Find<CheckBox>(ducking, "DuckingEnabled");
            ComboBox key = Find<ComboBox>(ducking, "DuckingKey");
            DuckingSliderView sensitivity = Find<DuckingSliderView>(ducking, "DuckingSensitivity"),
                amount = Find<DuckingSliderView>(ducking, "DuckingAmount"), attack = Find<DuckingSliderView>(ducking, "DuckingAttack"),
                release = Find<DuckingSliderView>(ducking, "DuckingRelease");
            DuckingSliderView[] sliders = [sensitivity, amount, attack, release];
            ToggleButton onUnit = Find<ToggleButton>(ducking, "DuckingOnUnit"), onComputer = Find<ToggleButton>(ducking, "DuckingOnComputer");
            Border badge = Find<Border>(ducking, "DuckingHardwareBadge");
            TextBlock status = Find<TextBlock>(ducking, "DuckingStatus"), note = Find<TextBlock>(ducking, "DuckingDeviceNote"),
                refusal = Find<TextBlock>(ducking, "DuckingRefusal");
            Grid reduction = Find<Grid>(ducking, "DuckingReduction");
            LevelMeter meter = Find<LevelMeter>(ducking, "DuckingReductionMeter");
            CheckBox Channel(string name) => ducking.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => c.Name == "DuckingChannel" && ((DuckingChoice)c.DataContext!).Name == name);
            CheckBox Mix(string name) => ducking.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => c.Name == "DuckingMix" && ((DuckingChoice)c.DataContext!).Name == name);
            string[] ChannelNames() => [.. ducking.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "DuckingChannel")
                .Select(c => ((DuckingChoice)c.DataContext!).Name)];

            // The daemon's setup, and nothing sent for it.
            Layout(ducking, 640, 900);
            Assert.False(enabled.IsChecked);
            Assert.Equal("XLR 1", (key.SelectedItem as DuckingKeyChoice)?.Name);
            Assert.Equal(["XLR 2", "Aux In", "Game", "Music", "Browser", "System", "Voice Chat", "Headset"], ChannelNames());
            Assert.True(Channel("Music").IsChecked);
            Assert.False(Channel("Aux In").IsChecked);
            Assert.True(Mix("Monitor A").IsChecked);
            Assert.False(Mix("Stream").IsChecked);
            Assert.Equal([Localizer.Format("DuckingDbfs", "-12"), Localizer.Format("DynamicsDecibels", "12"), Localizer.Format("DynamicsMilliseconds", "80"),
                Localizer.Format("DynamicsMilliseconds", "1200")], sliders.Select(s => Part<TextBlock>(s, "DuckingSliderValue").Text));
            Assert.Equal([12d, 12, Math.Log10(80), Math.Log10(1200)], sliders.Select(s => Part<Slider>(s, "DuckingSlider").Value));
            Assert.True(onUnit.IsEffectivelyVisible);
            Assert.True(onUnit.IsChecked);
            Assert.False(onComputer.IsChecked);
            Assert.True(badge.IsEffectivelyVisible);
            Assert.False(note.IsEffectivelyVisible);
            Assert.False(reduction.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("DuckingStatusOff"), status.Text);
            Assert.Empty(commands);

            // Each control sends its field, and the window shows the daemon's answer.
            async Task<JsonNode> Sent(Action change)
            {
                int before = commands.Count;
                change();
                await Until(() => commands.Count == before + 1);
                return commands.ToArray()[^1];
            }
            Assert.True((await Sent(() => enabled.IsChecked = true))["value"]!["enabled"]!.GetValue<bool>());
            await Until(() => status.Text == Localizer.Format("DuckingStatusOnUnit", "Wave XLR Pro"));
            JsonNode ticked = await Sent(() => Channel("Aux In").IsChecked = true);
            Assert.Equal("""["aux","game","music","browser","system","voicechat"]""", ticked["value"]!["channels"]!.ToJsonString());
            await Until(() => daemon.Ducking["channels"]!.AsArray().Count == 6 && Channel("Aux In").IsChecked == true);
            // A mark's name sets its preset's number exactly.
            Assert.Equal(-24, (await Sent(() => Mark(sensitivity, "DuckingHigh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))
                ["value"]!["sensitivity"]!.GetValue<int>());
            // A key step on the focused slider sends it.
            Slider amountSlider = Part<Slider>(amount, "DuckingSlider");
            amountSlider.Focus();
            Assert.Equal(11, (await Sent(() => amountSlider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left })))
                ["value"]!["amount"]!.GetValue<int>());
            // A drag shows its value as it goes, lands on a mark near it, and sends once it ends.
            Slider attackSlider = Part<Slider>(attack, "DuckingSlider");
            main.Ducking.Attack.BeginDrag();
            attackSlider.SetCurrentValue(RangeBase.ValueProperty, Math.Log10(33));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((30, Math.Log10(30)), (main.Ducking.Attack.Value, attackSlider.Value));
            Assert.Equal(Localizer.Format("DynamicsMilliseconds", "30"), Part<TextBlock>(attack, "DuckingSliderValue").Text);
            attackSlider.SetCurrentValue(RangeBase.ValueProperty, Math.Log10(45));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Localizer.Format("DynamicsMilliseconds", "45"), Part<TextBlock>(attack, "DuckingSliderValue").Text);
            Assert.Equal(4, commands.Count);
            Assert.Equal("""{"attack":45,"release":1200}""", (await Sent(() => main.Ducking.Attack.EndDrag()))["value"]!["speed"]!.ToJsonString());
            Assert.Equal("""["monitor","stream"]""", (await Sent(() => Mix("Stream").IsChecked = true))["value"]!["mixes"]!.ToJsonString());
            await Until(() => main.Ducking.Attack.Value == 45 && main.Ducking.Amount.Value == 11 && main.Ducking.Sensitivity.Value == -24
                && Mix("Stream").IsChecked == true && daemon.Ducking["speed"] is JsonObject);
            Assert.Equal(6, commands.Count);
            // Back to the presets the rest of the test expects, by their marks.
            await Sent(() => Mark(amount, "DuckingMedium").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Sent(() => Mark(attack, "DuckingMedium").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => main.Ducking.Attack.Value == 80 && main.Ducking.Amount.Value == 12);

            // The buttons under the channels: none, then every application channel.
            Assert.Equal("[]", (await Sent(() => Find<Button>(ducking, "DuckingNoChannels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))
                ["value"]!["channels"]!.ToJsonString());
            await Until(() => daemon.Ducking["channels"]!.AsArray().Count == 0 && Channel("Music").IsChecked == false
                && status.Text == Localizer.Text("DuckingNothingChosen"));
            Assert.Equal("""["game","music","browser","system","voicechat"]""",
                (await Sent(() => Find<Button>(ducking, "DuckingAllApplications").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))
                ["value"]!["channels"]!.ToJsonString());
            await Until(() => daemon.Ducking["channels"]!.AsArray().Count == 5 && Channel("Music").IsChecked == true
                && Channel("Aux In").IsChecked == false);

            // A refused change comes back with its reason, and the daemon's mixes.
            await Sent(() => Mix("Chat").IsChecked = true);
            await Until(() => refusal.IsEffectivelyVisible && Mix("Chat").IsChecked == false);
            Assert.Equal("setDucking: refused for the test", refusal.Text);

            // The key: XLR 2 leaves the channels in the same command, and the unit cannot run it.
            Channel("XLR 2").IsChecked = true;
            await Until(() => daemon.Ducking["channels"]!.AsArray().Any(c => c!.GetValue<string>() == "xlr2"));
            int sentBefore = commands.Count;
            key.SelectedItem = main.Ducking.Keys.Single(k => k.Id == "xlr2");
            await Until(() => commands.Count == sentBefore + 1);
            JsonNode picked = commands.ToArray()[^1]["value"]!;
            Assert.Equal("xlr2", picked["key"]!.GetValue<string>());
            Assert.DoesNotContain("xlr2", picked["channels"]!.AsArray().Select(c => c!.GetValue<string>()));
            await Until(() => !badge.IsEffectivelyVisible && note.IsEffectivelyVisible && reduction.IsEffectivelyVisible);
            // The channel rows were rebuilt for the new key, and letting the old ones go sent nothing.
            Assert.Equal(sentBefore + 1, commands.Count);
            Assert.False(refusal.IsEffectivelyVisible);
            Assert.Equal(["XLR 1", "Aux In", "Game", "Music", "Browser", "System", "Voice Chat", "Headset"], ChannelNames());
            Assert.Equal(Localizer.Format("DuckingDeviceKeyNotSupported", "Wave XLR Pro"), note.Text);
            Assert.Equal(Localizer.Format("DuckingStatusOnComputer", "XLR 2"), status.Text);
            // The switch lights where it runs: this computer, with the unit's side off and saying why.
            Assert.True(onComputer.IsChecked);
            Assert.False(onUnit.IsChecked);
            Assert.False(onUnit.IsEnabled);
            Assert.Equal(note.Text, ToolTip.GetTip(onUnit));

            // The reduction bar reads the largest duck meter.
            await live!.Push(JsonNode.Parse("""{"type":"meters","levels":{"duck:monitor":[12.0],"duck:stream":[6.0],"mix:monitor":[0.3,0.3]}}""")!);
            await Until(() => Math.Abs(meter.Level - 0.5) < 1e-9);
            Assert.Equal(Localizer.Format("DuckingReductionValue", 12.0), Find<TextBlock>(ducking, "DuckingReductionValue").Text);

            // The whole window fits, narrow and wide, with the badge-less footer at its fullest.
            foreach (double width in new[] { 640d, 2400 })
            {
                Layout(ducking, width, 1000);
                AssertFits(ducking, $"at {width}");
                AssertMarks(sliders, $"at {width}");
                Capture(ducking, $"ducking-software-{(int)width}");
            }

            // Runs on this computer, by choice: the choice is sent, and the note stays while the unit cannot run it.
            JsonNode processing = await Sent(() => onComputer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Equal(("setDuckingProcessing", "software"), (processing["cmd"]!.GetValue<string>(), processing["value"]!.GetValue<string>()));
            await Until(() => main.Ducking.Processing == "software" && onComputer.IsChecked == true && onUnit.IsChecked == false
                && note.IsEffectivelyVisible && !onUnit.IsEnabled);

            // Back to XLR 1 and the unit: the badge returns and the bar hides.
            sentBefore = commands.Count;
            key.SelectedItem = main.Ducking.Keys.Single(k => k.Id == "xlr1");
            await Until(() => commands.Count == sentBefore + 1);
            // The unit can run it again: its side can be pressed, and software by choice needs no note.
            await Until(() => onUnit.IsEnabled && onComputer.IsChecked == true && !note.IsEffectivelyVisible);
            await Sent(() => onUnit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await Until(() => badge.IsEffectivelyVisible && !reduction.IsEffectivelyVisible && onUnit.IsChecked == true
                && status.Text == Localizer.Format("DuckingStatusOnUnit", "Wave XLR Pro"));
            foreach (double width in new[] { 640d, 2400 })
            {
                Layout(ducking, width, 1000);
                AssertFits(ducking, $"on the unit at {width}");
                AssertMarks(sliders, $"on the unit at {width}");
                Capture(ducking, $"ducking-unit-{(int)width}");
            }

            // A device without hardware ducking: no "Runs on" and no badge, software runs it.
            daemon.HasUnit = false;
            await live.Push(daemon.State());
            await Until(() => !main.Ducking.HasProcessingChoice && !Find<WrapPanel>(ducking, "DuckingProcessing").IsEffectivelyVisible);
            Assert.False(badge.IsEffectivelyVisible);
            await Until(() => status.Text == Localizer.Format("DuckingStatusOnComputer", "XLR 1"));
            Layout(ducking, 640, 1000);
            AssertFits(ducking, "without a unit");
            // The narrowest the window goes.
            Layout(ducking, ducking.MinWidth, 1000);
            AssertFits(ducking, "at the minimum width");
            AssertMarks(sliders, "at the minimum width");
        }
        finally
        {
            ducking?.Close();
            layout.Close();
            options.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Part<T>(Control view, string name) where T : Control
        => view.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>A slider's mark by its preset's catalogue key.</summary>
    private static Button Mark(DuckingSliderView view, string key)
        => view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DuckingMark" && (string?)b.Content == Localizer.Text(key));

    /// <summary>
    /// Under every slider each preset's tick stands where the thumb's centre
    /// stands at that preset, within a pixel; each name is centred under its
    /// tick unless the slider's end holds it in; and no two names overlap.
    /// </summary>
    private static void AssertMarks(DuckingSliderView[] views, string where)
    {
        foreach (DuckingSliderView view in views)
        {
            var model = (DuckingSliderViewModel)view.DataContext!;
            Slider slider = Part<Slider>(view, "DuckingSlider");
            DuckingMarks marks = Part<DuckingMarks>(view, "DuckingSliderMarks");
            Thumb thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
            double was = slider.Value;
            Button[] labels = [.. marks.GetVisualChildren().OfType<Button>()];
            Assert.Equal(3, labels.Length);
            foreach (Button label in labels)
            {
                var mark = (DuckingMark)label.Tag!;
                Avalonia.Controls.Shapes.Rectangle tick = marks.GetVisualChildren().OfType<Avalonia.Controls.Shapes.Rectangle>()
                    .ElementAt(Array.IndexOf(labels, label));
                // Move the thumb alone, as a layout check: the value is not the model's.
                slider.SetCurrentValue(RangeBase.ValueProperty, mark.Position);
                slider.UpdateLayout();
                double thumbCentre = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), marks)!.Value.X;
                double tickAt = tick.Bounds.Center.X;
                Assert.True(Math.Abs(thumbCentre - tickAt) <= 1.0,
                    $"{where}: {model.Name} {label.Content}: tick at {tickAt:0.0}, thumb at {thumbCentre:0.0}.");
                double centre = label.Bounds.Center.X;
                Assert.True(Math.Abs(centre - tickAt) <= 1.0 || label.Bounds.X <= 0.5 || label.Bounds.Right >= marks.Bounds.Width - 0.5,
                    $"{where}: {model.Name} {label.Content} is not under its tick.");
                Assert.InRange(label.Bounds.X, -0.5, marks.Bounds.Width - label.Bounds.Width + 0.5);
            }
            slider.SetCurrentValue(RangeBase.ValueProperty, was);
            for (int i = 0; i < labels.Length; i++)
                for (int j = i + 1; j < labels.Length; j++)
                    Assert.False(labels[i].Bounds.Intersects(labels[j].Bounds),
                        $"{where}: {model.Name}: {labels[i].Content} overlaps {labels[j].Content}.");
        }
    }

    /// <summary>Every visible control and text sits inside the window's width, and nothing scrolls sideways.</summary>
    private static void AssertFits(DuckingWindow window, string where)
    {
        foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or ToggleButton or CheckBox or ComboBox or LevelMeter or Slider))
        {
            if (control is TextBlock { Text.Length: 0 } || control is TextBlock { Text: null }) continue;
            // A list's off-screen rows are inside the scroller, which is what has to fit.
            if (control.GetVisualAncestors().OfType<Popup>().Any()) continue;
            try { AssertInside(control, window); }
            catch (Xunit.Sdk.XunitException ex) { throw new Xunit.Sdk.XunitException($"{where}: {ex.Message}"); }
        }
        Assert.True(Find<ScrollViewer>(window, "DuckingScroll").Extent.Width <= Find<ScrollViewer>(window, "DuckingScroll").Viewport.Width + 0.5,
            $"{where}: the setup scrolls sideways.");
        // The overlay scroll bar paints over the scroller's right edge; the setup stays clear of it.
        ScrollViewer scroll = Find<ScrollViewer>(window, "DuckingScroll");
        Rect setup = Find<StackPanel>(window, "DuckingSetup").Bounds;
        Assert.True(setup.Right <= scroll.Viewport.Width - 12, $"{where}: the setup runs under the scroll bar.");
    }

    /// <summary>The fake daemon's socket: one send at a time, from its loop or from the test.</summary>
    private sealed class FakeSocket(System.Net.WebSockets.WebSocket socket, CancellationToken stop)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task Push(JsonNode message)
        {
            await _gate.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, message, stop); }
            finally { _gate.Release(); }
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
