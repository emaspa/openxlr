using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowLayoutTests
{
    [LayoutFact]
    public void NarrowPluginWindowsKeepActionsSeparateAndMixerFillsWideWindows()
    {
        string config = Path.Combine(Path.GetTempPath(), "openxlr-layout-" + Guid.NewGuid());
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            List<Window> windows = [];
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                var main = new MainWindow();
                windows.Add(main);
                // Stop the window's background connection before supplying a fixed fixture.
                ((DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(main)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
                Dispatcher.UIThread.RunJobs();
                var vm = new MainViewModel(new DaemonClient());
                main.DataContext = vm;
                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [JsonNode.Parse("""{"mixes":[],"channels":[],"inserts":{}}""")]);
                AddInsert(vm.Inserts);
                AddInsert(vm.Inserts2);
                for (int i = 0; i < 8; i++)
                {
                    var mix = new MixViewModel(new DaemonClient(), "mix" + i, "Monitor " + i);
                    AddInsert(mix.Inserts);
                    vm.Mixes.Add(mix);
                }
                main.Show();
                foreach (double width in new[] { 760d, 1040, 1800, 2400 })
                {
                    Layout(main, width, 900);
                    var content = main.FindControl<StackPanel>("MixerContent")!;
                    Assert.True(Math.Abs(content.Bounds.Width - (width - 32)) < 28,
                        $"Requested {width}, window {main.Width}/{main.Bounds.Width}, client {main.ClientSize.Width}, content {content.Bounds.Width}");
                    foreach (var name in main.GetVisualDescendants().OfType<TextBlock>()
                                 .Where(t => t.Classes.Contains("insertName")))
                    {
                        AssertInside(name, (Control)name.Parent!);
                        AssertNoOverlap(((Grid)name.Parent!).Children.Where(c => c.IsVisible).ToArray());
                    }
                    var masters = main.GetVisualDescendants().OfType<Border>()
                        .Where(b => b.DataContext is MixViewModel && b.Width == 232).ToArray();
                    Assert.Equal(8, masters.Length);
                    AssertNoOverlap(masters);
                    foreach (var master in masters) AssertInside(master, (Control)master.Parent!);
                    Capture(main, "mixer-" + width);
                    var page = main.GetVisualDescendants().OfType<ScrollViewer>().First();
                    page.Offset = new Vector(0, page.Extent.Height);
                    main.UpdateLayout();
                    Capture(main, "mixes-" + width);
                    page.Offset = default;
                }
                Assert.True(main.MinWidth >= 700);

                var insert = vm.Inserts.Items[0];
                var controls = new InsertControlsWindow { DataContext = insert };
                windows.Add(controls);
                controls.Show();
                foreach (double width in new[] { 420d, 620, 1000 })
                {
                    Layout(controls, width, 560);
                    var title = controls.FindControl<TextBlock>("PluginTitle")!;
                    var actions = controls.FindControl<WrapPanel>("PluginActions")!;
                    Assert.True(title.TranslatePoint(default, controls)!.Value.Y + title.Bounds.Height <=
                                actions.TranslatePoint(default, controls)!.Value.Y);
                    Assert.Equal(5, actions.Children.Count(c => c.IsVisible));
                    Assert.Single(controls.GetVisualDescendants().OfType<Slider>());
                    foreach (var button in actions.Children.Where(c => c.IsVisible))
                        AssertInside(button, actions);
                    Assert.True(actions.Children.Where(c => c.IsVisible).All(c => c.Bounds.Width > 20));
                    AssertNoOverlap(actions.Children.Where(c => c.IsVisible).ToArray());
                    var slider = controls.GetVisualDescendants().OfType<Slider>().Single();
                    AssertNoOverlap(((Grid)slider.Parent!).Children.Where(c => c.IsVisible).ToArray());
                    Capture(controls, "plugin-" + width);
                }
                insert.ApplyFromDaemon(JsonNode.Parse("{\"nativeHost\":true}")!,
                    "The plugin could not start. " + new string('x', 160), false);
                Layout(controls, 420, controls.MinHeight);
                Assert.True(controls.GetVisualDescendants().OfType<ScrollViewer>().First().Bounds.Height > 50);
                Capture(controls, "plugin-error-minimum");

                var chain = new MixInsertsWindow { DataContext = vm.Inserts };
                windows.Add(chain);
                chain.Show();
                Layout(chain, 440, 360);
                var label = chain.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Classes.Contains("insertName"));
                AssertInside(label, (Control)label.Parent!);
                foreach (var actions in chain.GetVisualDescendants().OfType<WrapPanel>())
                    foreach (var button in actions.Children)
                        AssertInside(button, actions);
                Capture(chain, "chain-440");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var window in windows) window.Close();
                Dispatcher.UIThread.RunJobs();
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
                if (Directory.Exists(config)) Directory.Delete(config, true);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Window layout hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AddInsert(InsertsViewModel owner)
    {
        const string uri = "http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono";
        owner.PluginChoices.Add(new(uri, "LSP Graphic Equalizer x16 Mono", "EQ",
            JsonNode.Parse("""[{"symbol":"gain","name":"A very long parameter name for input gain","min":-60,"max":12,"default":0}]""")!, true, true));
        owner.Apply(JsonNode.Parse("""
            [{"insert":{"id":"eq","plugin":"http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono",
            "label":"LSP Graphic Equalizer x16 Mono with a very long preset name","nativeHost":true},"nativeHostRunning":true}]
            """));
    }

    private static void Layout(Window window, double width, double height)
    {
        // Resize the native surface, then let its size notification run.
        window.PlatformImpl!.GetType().GetMethod("Resize", [typeof(Size), typeof(WindowResizeReason)])!
            .Invoke(window.PlatformImpl, [new Size(width, height), WindowResizeReason.User]);
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(100));
        Dispatcher.UIThread.MainLoop(stop.Token);
        window.UpdateLayout();
    }

    private static void AssertInside(Control child, Control parent)
    {
        Assert.True(child.Bounds.Width > 0);
        Assert.InRange(child.Bounds.Left, -0.1, parent.Bounds.Width);
        Assert.InRange(child.Bounds.Right, 0, parent.Bounds.Width + 0.1);
        Assert.InRange(child.Bounds.Bottom, 0, parent.Bounds.Height + 0.1);
    }

    private static void AssertNoOverlap(IReadOnlyList<Control> controls)
    {
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                var root = (Visual)TopLevel.GetTopLevel(controls[i])!;
                var first = new Rect(controls[i].TranslatePoint(default, root)!.Value, controls[i].Bounds.Size);
                var second = new Rect(controls[j].TranslatePoint(default, root)!.Value, controls[j].Bounds.Size);
                var intersection = first.Intersect(second);
                Assert.True(intersection.Width <= 0 || intersection.Height <= 0,
                    $"{controls[i].GetType().Name} overlaps {controls[j].GetType().Name}.");
            }
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

public sealed class LayoutFactAttribute : FactAttribute
{
    public LayoutFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_LAYOUT") != "1")
            Skip = "Run separately with OPENXLR_TEST_LAYOUT=1 under xvfb-run, filtered to WindowLayoutTests.";
    }
}
