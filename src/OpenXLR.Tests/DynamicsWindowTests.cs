using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The compressor and expander windows: the Dynamics button stands on XLR 1's
/// input line after Denoise, lights while either runs and opens a flyout
/// whose Open buttons open one window per processor, owned by the mixer. Each
/// window has its meters, graph and readouts inside it at its size and at
/// its narrowest, the compressor's make-up gain and not the expander's, the
/// advanced settings behind the settings button, levels that fill the meters
/// and the graph, and a threshold that the graph's keys move and send. Run
/// from the window layout test, on its UI thread. <see cref="CheckSkins"/>
/// runs from the skin test: the button and both windows in several skins.
/// </summary>
internal static class DynamicsWindowTests
{
    private sealed class Recorder : IDynamicsCommands
    {
        public readonly ConcurrentQueue<(string Processor, string Field, object Value)> Sent = new();

        public Task SetDynamicsAsync(string processor, string channel, string field, object value)
        {
            Sent.Enqueue((processor, field, value));
            return Task.CompletedTask;
        }

        public Task WatchDynamicsAsync(string channel) => Task.CompletedTask;
    }

    /// <summary>A Wave XLR Pro's state with XLR 1 reporting its compressor and expander.</summary>
    internal static JsonNode State(bool compressorOn)
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
        state["type"] = "state";
        var board = new JsonObject
        {
            ["channel"] = "sfx", ["enabled"] = true, ["available"] = true, ["error"] = null, ["formats"] = new JsonArray("wav"),
            ["clips"] = new JsonArray(), ["playing"] = new JsonArray(), ["auditioning"] = null, ["pages"] = new JsonArray(),
        };
        JsonObject mixer = SoundboardWindowTests.Mixer(true, board);
        var xlr1 = (JsonObject)mixer["channels"]![0]!;
        xlr1["microphone"] = true;
        xlr1["noise"] = new JsonObject
        {
            ["enabled"] = false, ["engine"] = "rnnoise", ["strength"] = 100, ["engines"] = new JsonArray("rnnoise"),
            ["active"] = null, ["latencyMs"] = null, ["available"] = true, ["error"] = null,
        };
        xlr1["compressor"] = new JsonObject
        {
            ["enabled"] = compressorOn, ["threshold"] = -18, ["ratio"] = 3, ["attack"] = 5, ["release"] = 150, ["knee"] = 6,
            ["makeup"] = 0, ["available"] = true, ["error"] = null,
        };
        xlr1["expander"] = new JsonObject
        {
            ["enabled"] = false, ["threshold"] = -50, ["ratio"] = 3, ["range"] = 24, ["attack"] = 1, ["release"] = 150,
            ["hold"] = 50, ["available"] = true, ["error"] = null,
        };
        state["mixer"] = mixer;
        // Through text, as the socket hands it over: numbers then read as any number type.
        return JsonNode.Parse(state.ToJsonString())!;
    }

    /// <summary>The Dynamics button on XLR 1's input line: shown, after Denoise, inside the window and clear of the rest.</summary>
    private static Button AssertButton(Window window, string where)
    {
        Button button = Find<Button>(window, "Xlr1Dynamics");
        Button noise = Find<Button>(window, "Xlr1Noise");
        Assert.True(button.IsEffectivelyVisible, $"{where}: XLR 1 has no Dynamics button.");
        Assert.Equal(Localizer.Text("Dynamics"), button.Content);
        Assert.Equal(Localizer.Text("DynamicsButtonTip"), ToolTip.GetTip(button));
        Assert.Same(noise.Parent, button.Parent);
        Point n = noise.TranslatePoint(default, window)!.Value, b = button.TranslatePoint(default, window)!.Value;
        Assert.True(b.X >= n.X + noise.Bounds.Width - 0.5, $"{where}: Dynamics at {b} does not follow Denoise at {n}.");
        AssertInside(button, window);
        AssertNoOverlap([.. ((Grid)button.Parent!).Children.Where(c => c.IsEffectivelyVisible)]);
        return button;
    }

    /// <summary>Every part of the body inside the window, the meters and graph with height to show.</summary>
    private static void AssertBody(DynamicsWindow window, string where)
    {
        foreach (string name in new[] { "DynamicsSwitch", "DynamicsSettings", "DynamicsName", "DynamicsGraph", "DynamicsInputMeter",
                     "DynamicsReductionMeter", "DynamicsOutputMeter", "DynamicsInputText", "DynamicsThresholdText",
                     "DynamicsReductionText", "DynamicsOutputText", "DynamicsNote" })
        {
            Control control = Find<Control>(window, name);
            Assert.True(control.IsEffectivelyVisible, $"{where}: {name} hidden");
            AssertInside(control, window);
        }
        Assert.True(Find<Control>(window, "DynamicsGraph").Bounds.Height >= 100, $"{where}: the graph is too short");
        Assert.True(Find<Control>(window, "DynamicsInputMeter").Bounds.Height >= 100, $"{where}: the meters are too short");
        AssertNoOverlap([Find<Control>(window, "DynamicsInputMeter"), Find<Control>(window, "DynamicsGraph"),
            Find<Control>(window, "DynamicsReductionMeter"), Find<Control>(window, "DynamicsOutputMeter")]);
    }

    internal static void Check()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        var opened = new List<DynamicsWindow>();
        try
        {
            string? font = Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } named ? named : null;
            if (font is not null) window.FontFamily = new FontFamily(font);
            var main = (MainViewModel)window.DataContext!;
            CaptureInputWindowTests.Apply(main, State(compressorOn: false));
            window.Show();
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 900);
                Button button = AssertButton(window, $"classic at {width}");
                Assert.DoesNotContain("fxOn", button.Classes);
            }
            CaptureInputWindowTests.Apply(main, State(compressorOn: true));
            Layout(window, 1040, 900);
            Button dynamics = AssertButton(window, "classic, compressor on");
            Assert.Contains("fxOn", dynamics.Classes);
            Assert.Equal(Localizer.Format("DynamicsButtonNameOn", "XLR 1"), Avalonia.Automation.AutomationProperties.GetName(dynamics));

            // The flyout's Open opens the compressor's window under the mixer; a second press raises it.
            ChannelDynamicsViewModel pair = main.Channels.Single(c => c.Id == "xlr1").Dynamics!;
            var flyout = (Flyout)dynamics.Flyout!;
            flyout.ShowAt(dynamics);
            Dispatcher.UIThread.RunJobs();
            var controls = (Control)((StackPanel)flyout.Content!).Children.OfType<DynamicsControls>().Single();
            Capture(window, "dynamics-flyout", controls);
            Button open = controls.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CompressorOpen");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            DynamicsWindow compressor = DynamicsWindows.Find(pair.Compressor)!;
            opened.Add(compressor);
            Assert.Same(window, compressor.Owner);
            Assert.Equal(Localizer.Format("CompressorWindowTitle", "XLR 1"), compressor.Title);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows.OfType<DynamicsWindow>());
            flyout.Hide();
            compressor.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(DynamicsWindows.Find(pair.Compressor));

            // Windows over a recorder: both processors at their size and their narrowest.
            var sent = new Recorder();
            var models = new ChannelDynamicsViewModel(sent, "xlr1", "XLR 1", new ManualTime());
            JsonNode state = State(compressorOn: true)["mixer"]!["channels"]![0]!;
            models.Apply(state["compressor"], state["expander"]);
            foreach (MicDynamicsViewModel model in new[] { models.Expander, models.Compressor })
            {
                DynamicsWindow processor = DynamicsWindows.Show(window, model);
                opened.Add(processor);
                if (font is not null) processor.FontFamily = new FontFamily(font);
                model.ApplyLevels(new JsonArray(-28.0, 6.0, -34.0));
                foreach ((double width, double height) in new[] { (760.0, 520.0), (560.0, 420.0) })
                {
                    Layout(processor, width, height);
                    string where = $"{model.Kind} at {width}x{height}";
                    AssertBody(processor, where);
                    Slider makeup = Find<Slider>(processor, "DynamicsMakeup");
                    Assert.Equal(model.IsCompressor, makeup.IsEffectivelyVisible);
                    if (model.IsCompressor) AssertInside(makeup, processor);
                    Assert.False(Find<ItemsControl>(processor, "DynamicsAdvanced").IsVisible, $"{where}: settings shown before asked for");
                    Assert.Equal(Localizer.Format("DynamicsDecibels", "-28"), Find<TextBlock>(processor, "DynamicsInputText").Text);
                    Assert.Equal(Localizer.Format("DynamicsDecibels", "6"), Find<TextBlock>(processor, "DynamicsReductionText").Text);
                    Capture(processor, $"dynamics-{model.Kind}-{width:0}");
                }

                // The settings button shows the advanced settings, every row inside the window.
                Find<ToggleButton>(processor, "DynamicsSettings").IsChecked = true;
                Layout(processor, 760, 640);
                Slider[] rows = [.. processor.GetVisualDescendants().OfType<Slider>().Where(s => s.Name == "DynamicsParameter")];
                Assert.Equal(model.Advanced.Count, rows.Length);
                foreach (Slider row in rows) AssertInside(row, processor);
                foreach (TextBlock text in processor.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "DynamicsParameterText"))
                    AssertInside(text, processor);
                Capture(processor, $"dynamics-{model.Kind}-settings");

                // The graph takes the keyboard: up a decibel, sent on the trailing edge.
                DynamicsGraph graph = processor.Graph;
                double before = model.Threshold.Value;
                graph.Focus();
                graph.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up });
                Assert.Equal(before + 1, model.Threshold.Value);
                // A press low on the graph drags the threshold down to its floor at the most.
                graph.SetThreshold(-500);
                Assert.Equal(model.Threshold.Minimum, model.Threshold.Value);
                SliderSync.FlushPending();
                Assert.Contains((model.Kind, "threshold", (object)model.Threshold.Minimum), sent.Sent);
                Assert.Equal(Localizer.Format("DynamicsDecibels", model.Threshold.Minimum.ToString("0", System.Globalization.CultureInfo.CurrentCulture)),
                    Find<TextBlock>(processor, "DynamicsThresholdText").Text);

                // The switch sends the switch alone.
                sent.Sent.Clear();
                Find<ToggleButton>(processor, "DynamicsSwitch").IsChecked = !model.Enabled;
                Assert.True(sent.Sent.TryDequeue(out var press));
                Assert.Equal((model.Kind, "enabled", (object)model.Enabled), press);
            }
        }
        finally
        {
            foreach (DynamicsWindow processor in opened) processor.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The Dynamics button and both windows in several skins' faces.</summary>
    internal static void CheckSkins()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        var opened = new List<DynamicsWindow>();
        try
        {
            var main = (MainViewModel)window.DataContext!;
            CaptureInputWindowTests.Apply(main, State(compressorOn: true));
            window.Show();
            ChannelDynamicsViewModel pair = main.Channels.Single(c => c.Id == "xlr1").Dynamics!;
            foreach (string skin in new[] { "default", "opendeck", "broadcast", "wave" })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                Layout(window, 1280, 1200);
                foreach (MicDynamicsViewModel model in new[] { pair.Expander, pair.Compressor })
                {
                    DynamicsWindow processor = DynamicsWindows.Show(window, model);
                    opened.Add(processor);
                    model.ApplyLevels(new JsonArray(-20.0, 4.0, -24.0));
                    Layout(processor, 760, 520);
                    foreach (TextBlock text in processor.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is { Length: > 0 }))
                        Assert.True(text.Foreground is not null, $"{skin}: \"{text.Text}\" has no colour.");
                    Assert.True(Find<ToggleButton>(processor, "DynamicsSwitch").IsChecked == model.Enabled, $"{skin}: the switch does not show the state");
                    Capture(processor, $"dynamics-{model.Kind}-{skin}");
                    processor.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            foreach (DynamicsWindow processor in opened) processor.Close();
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
