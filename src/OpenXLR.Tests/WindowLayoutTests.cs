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
    /// <summary>
    /// The widest fixed row in the mixer, the seven input toggles, needs about
    /// 660 logical pixels. The window must not refuse to go narrower than that.
    /// </summary>
    private const double WidestFixedRow = 660;

    /// <summary>Faders and dropdowns stop growing here, however wide the screen is.</summary>
    private const double ContentCap = 1300;

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
                if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
                    main.FontFamily = new Avalonia.Media.FontFamily(font);
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
                foreach (string name in new[] { "Google Chrome", "Discord", "Karere", "Signal", "Steam", "Balatro" })
                {
                    var app = new AppStreamViewModel(new DaemonClient(), name, name,
                        [new ChannelChoice("browser", "Browser"), new ChannelChoice("voicechat", "Voice Chat"), new ChannelChoice("game", "Game")]);
                    app.ApplyFromDaemon(name == "Google Chrome" ? "browser" : name is "Steam" or "Balatro" ? "game" : "voicechat", true, true);
                    vm.Apps.Add(app);
                    vm.ActiveApps.Add(app);
                }
                main.DataContext = null;
                main.DataContext = vm;
                main.Show();

                // No floor above the widest row: the window squeezes to 640.
                Assert.InRange(main.MinWidth, 0, WidestFixedRow);

                foreach (double width in new[] { 640d, 760, 1040, 1800, 2400 })
                {
                    Layout(main, width, 900);
                    Assert.Equal(width, main.ClientSize.Width);
                    var content = main.FindControl<StackPanel>("MixerContent")!;
                    string where = $"Requested {width}, window {main.Width}/{main.Bounds.Width}, "
                        + $"client {main.ClientSize.Width}, content {content.Bounds.Width}";
                    // Below the cap the cards follow the window; above it they
                    // stop, so a fader does not stretch across an ultrawide.
                    Assert.True(content.Bounds.Width <= ContentCap, where);
                    if (width <= ContentCap) Assert.InRange(content.Bounds.Width, width - 72, width - 16);
                    else Assert.InRange(content.Bounds.Width, ContentCap / 2, ContentCap);

                    // Every input toggle keeps a usable width and stays in the window.
                    foreach (var row in main.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>())
                        foreach (var toggle in row.Children.Where(c => c.IsVisible))
                        {
                            Assert.True(toggle.Bounds.Width > 40, $"A toggle shrank to {toggle.Bounds.Width}. {where}");
                            AssertInside(toggle, main);
                        }

                    // XLR 1, XLR 2, and the chain line inside each of the eight
                    // mix cards. A template that loses the class loses a row here.
                    var names = main.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Classes.Contains("insertName")).ToArray();
                    Assert.Equal(10, names.Length);
                    foreach (var name in names)
                    {
                        AssertInside(name, (Control)name.Parent!);
                        AssertInside(name, main);
                        AssertNoOverlap(((Grid)name.Parent!).Children.Where(c => c.IsVisible).ToArray());
                    }

                    var masters = main.GetVisualDescendants().OfType<Border>()
                        .Where(b => b.DataContext is MixViewModel && b.Width == 232).ToArray();
                    Assert.Equal(8, masters.Length);
                    AssertNoOverlap(masters);
                    foreach (var master in masters)
                    {
                        AssertInside(master, content);
                        // The card's own contents: the mix name, its fader and
                        // its chain line all have to fit inside the card.
                        var inside = master.GetVisualDescendants().OfType<Control>()
                            .Where(c => c is TextBlock or Slider or Button && c.IsVisible && c.Bounds.Width > 0).ToArray();
                        Assert.NotEmpty(inside);
                        foreach (var child in inside) AssertInside(child, master);
                        Assert.Single(master.GetVisualDescendants().OfType<TextBlock>(),
                            t => t.Classes.Contains("insertName"));
                        Assert.Single(master.GetVisualDescendants().OfType<Slider>());
                    }
                    var appRows = main.FindControl<ItemsControl>("ApplicationRows")!;
                    var manageApps = main.FindControl<Button>("ManageApps")!;
                    var appCards = appRows.GetVisualDescendants().OfType<Border>()
                        .Where(b => b.DataContext is AppStreamViewModel && b.Padding == new Thickness(8, 6)).ToArray();
                    Assert.Equal(6, appCards.Length);
                    var appWrap = appRows.GetVisualDescendants().OfType<WrapPanel>().Single();
                    foreach (var appCard in appCards) Assert.Equal(4, appCard.Margin.Right);
                    // Font metrics differ across desktops. A chip stays on
                    // the preceding row exactly when its measured width fits.
                    for (int i = 1; i < appCards.Length; i++)
                    {
                        var previous = appCards[i - 1];
                        var current = appCards[i];
                        var before = previous.TranslatePoint(default, appWrap)!.Value;
                        var here = current.TranslatePoint(default, appWrap)!.Value;
                        double end = before.X + previous.Bounds.Width + previous.Margin.Right
                            + current.Margin.Left + current.Bounds.Width + current.Margin.Right;
                        if (end <= appWrap.Bounds.Width)
                            Assert.Equal(before.Y, here.Y);
                        else
                            Assert.True(here.Y > before.Y, "An app wider than the remaining space did not wrap.");
                    }
                    var appHeader = (StackPanel)main.FindControl<Expander>("ApplicationsTile")!.Header!;
                    var heading = appHeader.Children.OfType<TextBlock>().Single();
                    Assert.Same(appHeader, manageApps.Parent);
                    double headingCenter = heading.TranslatePoint(default, main)!.Value.Y + heading.Bounds.Height / 2;
                    double manageCenter = manageApps.TranslatePoint(default, main)!.Value.Y + manageApps.Bounds.Height / 2;
                    Assert.InRange(Math.Abs(headingCenter - manageCenter), 0, 1);
                    Assert.True(manageApps.TranslatePoint(default, main)!.Value.X > heading.TranslatePoint(default, main)!.Value.X + heading.Bounds.Width);
                    var editLayout = main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Edit layout…");
                    Assert.Equal(editLayout.Padding, manageApps.Padding);
                    Assert.Equal(editLayout.FontSize, manageApps.FontSize);
                    Assert.Equal(editLayout.MinHeight, manageApps.MinHeight);
                    Assert.True(manageApps.IsEnabled);
                    AssertInside(manageApps, main);
                    foreach (var appCard in appCards)
                    {
                        AssertInside(appCard, main);
                        AssertNoOverlap([appCard, manageApps]);
                    }
                    Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.Text?.StartsWith("Running audio-capable apps appear here", StringComparison.Ordinal) == true);
                    Capture(main, "mixer-" + width);
                    var page = main.GetVisualDescendants().OfType<ScrollViewer>().First();
                    page.Offset = new Vector(0, page.Extent.Height);
                    main.UpdateLayout();
                    Capture(main, "mixes-" + width);
                    page.Offset = default;
                }

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
                    var buttons = actions.Children.Where(c => c.IsVisible).ToArray();
                    Assert.Equal(5, buttons.Length);
                    Assert.Single(controls.GetVisualDescendants().OfType<Slider>());
                    // In one row the five actions need about 454 px, so at 420
                    // they have to wrap rather than run off the window.
                    foreach (var button in buttons)
                    {
                        Assert.True(button.Bounds.Width > 20);
                        AssertInside(button, controls);
                    }
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
                AssertInside(label, chain);
                foreach (var actions in chain.GetVisualDescendants().OfType<WrapPanel>())
                {
                    Assert.NotEmpty(actions.Children);
                    foreach (var button in actions.Children)
                    {
                        AssertInside(button, actions);
                        AssertInside(button, chain);
                    }
                }
                Capture(chain, "chain-440");

                new UiSettings { StartMinimized = true, MinimizeToTray = true }.Save();
                var optionsVm = new OptionsViewModel(new DaemonClient(), vm);
                var options = new OptionsWindow { DataContext = optionsVm };
                windows.Add(options);
                options.Show();
                Layout(options, 980, 800);
                var launch = options.FindControl<ComboBox>("LaunchBehavior")!;
                var close = options.FindControl<ComboBox>("CloseBehavior")!;
                Assert.Equal(1, launch.SelectedIndex);
                Assert.Equal(0, close.SelectedIndex);
                Assert.Equal("Tray only", ((ComboBoxItem)launch.SelectedItem!).Content);
                Assert.Equal("Keep running in tray", ((ComboBoxItem)close.SelectedItem!).Content);
                foreach (var picker in new[] { launch, close })
                {
                    AssertInside(picker, options);
                    AssertNoOverlap(((Grid)picker.Parent!).Children.Where(c => c.IsVisible).ToArray());
                }
                foreach (string heading in new[] { "AT LOGIN", "WINDOW", "AUDIO" })
                    Assert.Single(options.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == heading);
                var notes = options.FindControl<StackPanel>("SoftwareMixerNotes")!;
                var audioHeading = options.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == "AUDIO");
                Assert.Equal(audioHeading.TranslatePoint(default, options)!.Value.X,
                    notes.TranslatePoint(default, options)!.Value.X);
                var noteLines = notes.Children.OfType<TextBlock>().Where(t => t.IsVisible).ToArray();
                Assert.Equal(2, noteLines.Length);
                foreach (var line in noteLines)
                {
                    Assert.Equal(notes.TranslatePoint(default, options)!.Value.X,
                        line.TranslatePoint(default, options)!.Value.X);
                    AssertInside(line, notes);
                }
                launch.SelectedIndex = 0;
                close.SelectedIndex = 1;
                Assert.False(UiSettings.Load().StartMinimized);
                Assert.False(UiSettings.Load().MinimizeToTray);
                Assert.False(vm.MinimizeToTray);
                Capture(options, "options-startup");

                optionsVm.ApplyPluginSetup(JsonNode.Parse("""
                    {"yabridge":"5.1.1","wine":true,"bridgeProvider":"system",
                     "windowsImportDirectory":"~/.local/share/openxlr/windows-plugins",
                     "windowsDirectories":["/home/test/.wine/drive_c/Program Files/Common Files/VST3",
                     "/home/test/Downloads/A plugin collection with a long folder name/Windows/VST3/x64"]}
                    """));
                var folders = new PluginFoldersWindow { DataContext = optionsVm };
                windows.Add(folders);
                folders.Show();
                foreach (double width in new[] { 480d, 720 })
                {
                    Layout(folders, width, width >= 720 ? 820 : 680);
                    var list = folders.FindControl<ListBox>("FolderList")!;
                    Assert.Equal(2, list.ItemCount);
                    Assert.InRange(list.Bounds.Height, 220, 240);
                    list.SelectedIndex = 0;
                    Assert.True(folders.FindControl<Button>("RemoveFolder")!.IsEnabled);
                    folders.ApplyPluginFiles(JsonNode.Parse("""
                        {"ok":true,"plugins":[
                          {"path":"/imports/EQ.vst3","name":"Elgato EQ","format":"vst3","enabled":true,"canDelete":true,"inUse":false},
                          {"path":"/wine/TDR.clap","name":"TDR plugin","format":"clap","enabled":false,"canDelete":false,"winePrefix":"/wine","inUse":false},
                          {"path":"/wine/Active.vst3","name":"Active effect","format":"vst3","enabled":true,"canDelete":false,"winePrefix":"/wine","inUse":true}
                        ]}
                        """));
                    var files = folders.FindControl<ListBox>("PluginList")!;
                    Assert.Equal(3, files.ItemCount);
                    files.SelectedIndex = 0;
                    Assert.True(folders.FindControl<Button>("DeletePlugin")!.IsEnabled);
                    Assert.False(folders.FindControl<Button>("RemoveUses")!.IsVisible);
                    Assert.False(folders.FindControl<Button>("WineUninstaller")!.IsVisible);
                    files.SelectedIndex = 1;
                    Assert.False(folders.FindControl<Button>("DeletePlugin")!.IsEnabled);
                    Assert.Equal("Enable in OpenXLR", folders.FindControl<Button>("TogglePlugin")!.Content);
                    Assert.True(folders.FindControl<Button>("WineUninstaller")!.IsEnabled);
                    files.SelectedIndex = 2;
                    Assert.False(folders.FindControl<Button>("TogglePlugin")!.IsEnabled);
                    Assert.False(folders.FindControl<Button>("WineUninstaller")!.IsEnabled);
                    Assert.True(folders.FindControl<Button>("RemoveUses")!.IsVisible);
                    Assert.True(folders.FindControl<Button>("RemoveUses")!.IsEnabled);
                    folders.FindControl<TextBlock>("Status")!.Text = "";   // the fixture replaces the disconnected query
                    Layout(folders, width, width >= 720 ? 820 : 680);
                    foreach (var button in folders.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
                        AssertInside(button, folders);
                    AssertInside(list, folders);
                    Capture(folders, "plugin-folders-" + width);
                }
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

    /// <summary>
    /// The control sits inside the box of one of its ancestors, measured in
    /// that ancestor's own coordinates, so it works for a card, a panel or the
    /// window itself. Height is only checked against an immediate parent: a
    /// scrolled page is taller than its window on purpose.
    /// </summary>
    private static void AssertInside(Control child, Visual ancestor)
    {
        Assert.True(child.Bounds.Width > 0, $"{child.GetType().Name} has no width.");
        var origin = child.TranslatePoint(default, ancestor)!.Value;
        Size box = ancestor is TopLevel top ? top.ClientSize : ancestor.Bounds.Size;
        Assert.InRange(origin.X, -0.1, box.Width);
        Assert.InRange(origin.X + child.Bounds.Width, 0, box.Width + 0.1);
        if (ReferenceEquals(child.Parent, ancestor))
            Assert.InRange(origin.Y + child.Bounds.Height, 0, box.Height + 0.1);
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
