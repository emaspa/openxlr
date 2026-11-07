using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowLayoutTests
{
    /// <summary>
    /// Keep the existing narrow-window allowance as the controls reflow.
    /// </summary>
    private const double MaximumAllowedMinimumWidth = 660;

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
                PinMaterial();
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
                if (Environment.GetEnvironmentVariable("OPENXLR_MIXER_FIT_SIZES") is { Length: > 0 } sizes)
                {
                    MixerPopOutWindowTests.CheckPixelSizes(main, sizes);
                    return;
                }
                OutputControlsWindowTests.Check();
                ProfileSliderWindowTests.Check();
                ProfileRowWindowTests.Check();
                EditorRulesReconnectWindowTests.Check();
                OptionsInteractionWindowTests.Check();
                HardwareMixingWindowTests.Check();
                UserMonitorWindowTests.Check();
                MonitorOutputPickerWindowTests.Check();
                MonitorMicrophonePauseWindowTests.Check();
                InputBindingWindowTests.Check();
                InputBindingWindowTests.CheckDrivenUnit();
                DuckingWindowTests.Check();
                SoundboardWindowTests.Check();
                VoiceChangerWindowTests.Check();
                DynamicsWindowTests.Check();
                InputBindingWindowTests.CheckCaptureInputs();
                CaptureInputWindowTests.ThreeCaptureInputsFitClassicWidths();
                VolumeRangeWindowTests.Check();
                LocalizationRenderingTests.Check();
                BrowserTabsWindowTests.Check();
                OlderDaemonWindowTests.Check();
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
                ApplicationIconWindowTests.Assign(vm.Apps);
                main.DataContext = null;
                main.DataContext = vm;
                main.Show();
                PluginPresetWindowTests.Check(main);

                // The window must still allow its existing narrow size.
                Assert.InRange(main.MinWidth, 0, MaximumAllowedMinimumWidth);

                foreach (double width in new[] { 640d, 760, 1040, 1800, 2400 })
                {
                    Layout(main, width, 900);
                    Assert.Equal(width, main.ClientSize.Width);
                    MainWindowLabelTests.Check(main);
                    AssertMonitorMute(main);
                    ApplicationIconWindowTests.Check(main, $"at {width}");
                    if (width == 640) MainWindowLabelTests.CheckLongDeviceLabels(main);
                    if (width == 640)
                    {
                        var output = main.FindControl<Slider>("OutputVolumeSlider")!;
                        Assert.Equal(1, output.Maximum);
                        // Desktop state beyond unity must expand the actual
                        // bound slider before it can clamp the incoming value.
                        vm.OutputVolume = 1.5;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(1.5, output.Maximum);
                        Assert.Equal(1.5, output.Value);
                        Assert.Equal("150%", vm.OutputVolumeText);
                        var boost = main.FindControl<Avalonia.Controls.Primitives.ToggleButton>("OutputBoost")!;
                        Assert.True(boost.IsChecked);
                        boost.IsChecked = false;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(1, output.Maximum);
                        Assert.Equal(1, output.Value);
                        Assert.Equal("100%", vm.OutputVolumeText);
                        boost.IsChecked = true;
                        output.Value = 0.66;
                        Assert.Equal("66%", vm.OutputVolumeText);
                        Assert.Equal(0.66, vm.OutputVolume);

                        var mix = vm.Mixes[0];
                        var master = main.GetVisualDescendants().OfType<Slider>().Single(c => c.DataContext == mix);
                        mix.ApplyFromDaemon(JsonNode.Parse("""{"volume":1.2,"muted":false,"kind":"monitor"}""")!);
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(1.5, master.Maximum);
                        Assert.Equal(1.2, master.Value);
                        Assert.Equal("120%", mix.VolumeText);
                        mix.VolumeRange.Boost = false;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal(1, master.Value);
                        Assert.Equal(1, mix.Volume);
                    }
                    var content = main.FindControl<StackPanel>("MixerContent")!;
                    string where = $"Requested {width}, window {main.Width}/{main.Bounds.Width}, "
                        + $"client {main.ClientSize.Width}, content {content.Bounds.Width}";
                    // The cards follow the window at every width, so a wide
                    // window shows more strips before anything scrolls.
                    Assert.InRange(content.Bounds.Width, width - 72, width - 16);

                    // Every input toggle keeps a usable width and stays in the window.
                    foreach (var row in new[] { main.FindControl<WrapPanel>("InputControls")!, main.FindControl<WrapPanel>("Input2Controls")! })
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

                    // The SUBMIXER header introduces the channel tiles; only the
                    // mix masters below them carry a heading of their own.
                    Assert.Null(main.FindControl<TextBlock>("InputChannelHeading"));
                    var tiles = main.FindControl<ItemsControl>("ChannelTiles")!;
                    var outputHeading = main.FindControl<TextBlock>("OutputMixHeading")!;
                    Assert.Equal(Localizer.Text("OutputMixesHeading"), outputHeading.Text);
                    AssertInside(outputHeading, main);
                    Assert.Equal(0, tiles.FindAncestorOfType<ScrollViewer>()!.TranslatePoint(default, outputHeading.FindAncestorOfType<DockPanel>()!)!.Value.Y, 0.5);
                    Assert.True(tiles.TranslatePoint(default, main)!.Value.Y
                        < outputHeading.TranslatePoint(default, main)!.Value.Y);

                    var masters = main.GetVisualDescendants().OfType<Border>()
                        .Where(b => b.DataContext is MixViewModel && b.Name == "ClassicMasterTile").ToArray();
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
                    var appWrap = Assert.IsType<OpenXLR.UI.Layout.TileColumnsPanel>(appRows.ItemsPanelRoot);
                    AssertNoOverlap(appCards);
                    Assert.All(appCards, card => Assert.Equal(4, card.Margin.Right));
                    // Wrapping uses natural widths before the row receives spare space.
                    double used = appWrap.Children[0].DesiredSize.Width;
                    for (int i = 1; i < appCards.Length; i++)
                    {
                        Point before = appCards[i - 1].TranslatePoint(default, appWrap)!.Value;
                        Point here = appCards[i].TranslatePoint(default, appWrap)!.Value;
                        double natural = appWrap.Children[i].DesiredSize.Width;
                        if (here.Y > before.Y + .5)
                        {
                            Assert.True(used + natural > appWrap.Bounds.Width - .5);
                            Assert.Equal(0, here.X, .5);
                            used = 0;
                        }
                        else Assert.True(here.X >= before.X + appCards[i - 1].Bounds.Width - .5);
                        used += natural;
                    }
                    var appHeader = (StackPanel)main.FindControl<Expander>("ApplicationsTile")!.Header!;
                    var heading = appHeader.Children.OfType<TextBlock>().Single();
                    Assert.Same(appHeader, manageApps.Parent);
                    double headingCenter = heading.TranslatePoint(default, main)!.Value.Y + heading.Bounds.Height / 2;
                    double manageCenter = manageApps.TranslatePoint(default, main)!.Value.Y + manageApps.Bounds.Height / 2;
                    Assert.InRange(Math.Abs(headingCenter - manageCenter), 0, 1);
                    Assert.True(manageApps.TranslatePoint(default, main)!.Value.X > heading.TranslatePoint(default, main)!.Value.X + heading.Bounds.Width);
                    var editLayout = main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Localizer.Text("EditLayout"));
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

                ApplicationIconWindowTests.CheckManage(vm);

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
                    Assert.Equal(7, buttons.Length);
                    Assert.Contains(controls.FindControl<Avalonia.Controls.Primitives.ToggleButton>("PluginOwnBlock"), buttons);
                    Assert.Single(controls.GetVisualDescendants().OfType<Slider>());
                    // In one row the seven actions need more than 420 px, so
                    // there they have to wrap rather than run off the window.
                    foreach (var button in buttons)
                    {
                        Assert.True(button.Bounds.Width > 20);
                        AssertInside(button, controls);
                    }
                    var slider = controls.GetVisualDescendants().OfType<Slider>().Single();
                    AssertNoOverlap(((Grid)slider.Parent!).Children.Where(c => c.IsVisible).ToArray());
                    Capture(controls, "plugin-" + width);
                }
                // In the native host the latency line carries the measured
                // delay too. Its longest form wraps inside the narrowest window.
                insert.ApplyFromDaemon(JsonNode.Parse("""{"nativeHost":true}""")!, null, true);
                insert.LatencyMilliseconds = null;
                insert.MeasuredLatencyMilliseconds = 47.98;
                Layout(controls, 420, 560);
                var latency = controls.FindControl<TextBlock>("PluginLatency")!;
                Assert.Equal(Localizer.Format("LatencyUnreportedMeasured", 47.98), latency.Text);
                Assert.True(latency.IsVisible);
                AssertInside(latency, controls);
                Capture(controls, "plugin-latency-420");
                insert.LatencyMilliseconds = 0;
                insert.MeasuredLatencyMilliseconds = null;
                Assert.Equal(Localizer.Format("LatencyReportedMeasuring", 0.0), latency.Text);
                insert.ApplyFromDaemon(JsonNode.Parse("{}")!, null, false);
                Assert.False(controls.FindControl<Button>("ManageNativeEditors")!.IsVisible);
                insert.ApplyFromDaemon(JsonNode.Parse("""{"nativeHost":true}""")!, null, true, true, "This editor is blocked by its compatibility rule.");
                Layout(controls, 420, 560);
                var manageEditors = controls.FindControl<Button>("ManageNativeEditors")!;
                Assert.True(manageEditors.IsVisible);
                var actionRows = controls.FindControl<WrapPanel>("PluginActions")!;
                Assert.Equal(Localizer.Text("Bypass"), ((Avalonia.Controls.Primitives.ToggleButton)actionRows.Children[actionRows.Children.IndexOf(manageEditors) - 1]).Content);
                foreach (var button in actionRows.Children.Where(c => c.IsVisible)) AssertInside(button, controls);
                Capture(controls, "plugin-compatibility-420");

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
                    // Plugin UI shows only for an effect whose editor the host can open.
                    foreach (var button in actions.Children.Where(c => c.IsVisible))
                    {
                        AssertInside(button, actions);
                        AssertInside(button, chain);
                    }
                }
                AssertInside(chain.FindControl<Button>("EffectWorkflow")!, chain);
                var micSetupButton = chain.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Content as string == Localizer.Text("MicSetup"));
                Assert.True(micSetupButton.IsVisible);
                AssertInside(micSetupButton, chain);
                Capture(chain, "chain-440");

                var workflow = new EffectWorkflowWindow { DataContext = vm.Inserts };
                windows.Add(workflow);
                workflow.Show();
                Layout(workflow, workflow.MinWidth, workflow.MinHeight);
                foreach (var actions in workflow.GetVisualDescendants().OfType<WrapPanel>())
                {
                    AssertNoOverlap(actions.Children.ToArray());
                    foreach (var action in actions.Children) AssertInside(action, actions);
                }
                Assert.True(((ScrollViewer)workflow.Content!).Bounds.Height > 50);
                Capture(workflow, "effect-workflow-minimum");

                // Mic setup at its smallest, on each step: the step bar, the
                // actions and the switches wrap inside the window, and the
                // scrolled part keeps room to read. The step is shown first,
                // so the window has grown for it before the user shrinks it.
                var micSetupModel = vm.Inserts.MicSetup;
                var micSetup = new MicSetupWindow { DataContext = micSetupModel };
                windows.Add(micSetup);
                micSetup.Show();
                foreach (MicSetupStep step in Enum.GetValues<MicSetupStep>())
                {
                    micSetupModel.Step = step;
                    Settle(micSetup);
                    Layout(micSetup, micSetup.MinWidth, micSetup.MinHeight);
                    Assert.Equal(micSetup.MinHeight, micSetup.ClientSize.Height, 0.5);
                    foreach (var panel in micSetup.GetVisualDescendants().OfType<WrapPanel>().Where(p => p.IsEffectivelyVisible))
                    {
                        Control[] children = [.. panel.Children.Where(c => c.IsVisible)];
                        AssertNoOverlap(children);
                        foreach (var child in children) { AssertInside(child, panel); AssertInside(child, micSetup); }
                    }
                    foreach (string name in new[] { "MicSetupSteps", "MicSetupClose" })
                        AssertInside(micSetup.FindControl<Control>(name)!, micSetup);
                    // Shape's preset undo, add effect and each plugin's bypass, editor and remove keep to the window's width.
                    if (step == MicSetupStep.Shape)
                        foreach (Control control in micSetup.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible
                                     && c.Name is "MicSetupUndoPreset" or "MicSetupAddEffect" or "MicSetupBypass" or "MicSetupInsertEditor" or "MicSetupRemoveInsert"))
                            AssertInside(control, micSetup);
                    Assert.True(micSetup.GetVisualDescendants().OfType<ScrollViewer>().Single().Bounds.Height > 50);
                    Capture(micSetup, $"mic-setup-{step.ToString().ToLowerInvariant()}-minimum");
                }
                micSetupModel.Step = MicSetupStep.Level;

                vm.Inputs.Add(new AudioDeviceItem("test_source", "Second microphone", false));
                vm.Inputs.Add(new AudioDeviceItem("OpenXLR_stream", "Own mix", true));
                var setup = new MixerSetupWindow { DataContext = vm };
                windows.Add(setup);
                setup.Show();
                Layout(setup, 600, 640);
                typeof(MixerSetupWindow).GetMethod("OnAddCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(setup, [null, new Avalonia.Interactivity.RoutedEventArgs()]);
                Dispatcher.UIThread.RunJobs();
                var captureDialog = Assert.Single(setup.OwnedWindows);
                windows.Add(captureDialog);
                Layout(captureDialog, 360, 340);
                var sourcePicker = captureDialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CaptureSource");
                Assert.Single(sourcePicker.Items);
                Assert.Equal("test_source", ((AudioDeviceItem)sourcePicker.Items[0]!).Name);
                sourcePicker.SelectedIndex = 0;
                Layout(captureDialog, 360, 340);
                // The picker shows the source's label, never the record's text.
                Assert.Contains(sourcePicker.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text == ((AudioDeviceItem)sourcePicker.Items[0]!).Label);
                Assert.DoesNotContain(sourcePicker.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.Text?.StartsWith(nameof(AudioDeviceItem), StringComparison.Ordinal) == true);
                var portPicker = captureDialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CapturePort");
                Assert.Equal(32, portPicker.Items.OfType<InputBindingDialog.PortChoice>().Count(p => p.Stereo));
                Assert.Equal(64, portPicker.Items.OfType<InputBindingDialog.PortChoice>().Count(p => !p.Stereo));
                Assert.Equal(0, portPicker.SelectedIndex);
                var addCapture = captureDialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CreateCapture");
                addCapture.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(captureDialog.IsVisible); // incomplete input cannot submit
                Capture(captureDialog, "capture-input-360");
                captureDialog.Close();
                AssertInside(setup.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExclusiveGroupsButton"), setup);
                CheckCreateIconPickers(setup);
                CheckAppearanceDialog(setup, windows);
                setup.Close();

                var groupsVm = new MainViewModel(new DaemonClient());
                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(groupsVm, [JsonNode.Parse("""
                        {"mixes":[{"id":"monitor","name":"Monitor","volume":1}],
                         "channels":[{"id":"xlr1","name":"Microphone one","levels":{"monitor":1},"mutedIn":[]},
                                     {"id":"xlr2","name":"Microphone two","levels":{"monitor":1},"mutedIn":["monitor"]},
                                     {"id":"system","name":"System","levels":{"monitor":1},"mutedIn":[]}],
                         "exclusiveGroups":[{"id":"mics","name":"Microphones","channels":["xlr1","xlr2"]}]}
                        """)]);
                var groupsDialog = new ExclusiveGroupsWindow(groupsVm);
                windows.Add(groupsDialog);
                groupsDialog.Show();
                Layout(groupsDialog, 360, 340);
                var groupControls = groupsDialog.GetVisualDescendants().ToArray();
                var groupSave = groupControls.OfType<Button>().Single(b => b.Name == "SaveGroup");
                groupSave.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(groupsDialog.IsVisible);   // a new group needs a name and two members
                Assert.Equal(Localizer.Text("ExclusiveGroupsInvalid"), groupControls.OfType<TextBlock>().Single(b => b.Name == "GroupError").Text);
                Assert.False(groupControls.OfType<Button>().Single(b => b.Name == "DeleteGroup").IsEnabled);
                var groupPicker = groupControls.OfType<ComboBox>().Single(b => b.Name == "Groups");
                groupPicker.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Microphones", groupControls.OfType<TextBox>().Single(b => b.Name == "GroupName").Text);
                Assert.Equal(new bool?[] { true, true, false },
                    groupControls.OfType<CheckBox>().Where(b => b.Name?.StartsWith("Member_", StringComparison.Ordinal) == true).Select(b => b.IsChecked));
                Assert.True(groupControls.OfType<Button>().Single(b => b.Name == "DeleteGroup").IsEnabled);
                foreach (Button button in groupControls.OfType<Button>()) AssertInside(button, groupsDialog);
                Capture(groupsDialog, "exclusive-groups-360");
                groupsDialog.Close();

                for (int i = 0; i < 32; i++) vm.Channels.Add(new ChannelViewModel(new DaemonClient(), "key-channel" + i, "Shortcut channel " + i, []));
                vm.Outputs.Add(new AudioDeviceItem("long-output", "Very long headphone and speaker output description for narrow desktop windows", false));
                vm.Soundboard.Apply(SoundboardViewModelTests.Board(), vm.Channels);
                using var keys = new DesktopKeys(new DaemonClient());
                var keyWindow = new DesktopKeysWindow(keys, vm);
                windows.Add(keyWindow);
                keyWindow.Show();
                Layout(keyWindow, 360, 340);
                var keyScroll = Assert.Single(keyWindow.GetVisualDescendants().OfType<ScrollViewer>(), v => v.Content is StackPanel);
                Assert.True(keyScroll.Extent.Height > keyScroll.Viewport.Height);
                Capture(keyWindow, "desktop-keys-360");
                keyScroll.Offset = new Vector(0, keyScroll.Extent.Height);
                Layout(keyWindow, 360, 340);
                Assert.True(keyScroll.Extent.Width <= keyScroll.Viewport.Width + 1);
                Assert.Contains(keyWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Localizer.Text("CurrentSystemDefaultOutput"));
                Assert.DoesNotContain(keyWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("AudioDeviceItem {") == true);
                Assert.Contains(keyWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Localizer.Format("KeyPlayClip", "Drum roll"));
                Assert.Contains(keyWindow.GetVisualDescendants().OfType<CheckBox>(), b => Equals(b.Content, Localizer.Text("AddStopClipsKey")));
                Capture(keyWindow, "desktop-output-keys-360");
                keyWindow.Close();

                new UiSettings { StartMinimized = true, MinimizeToTray = true }.Save();
                var optionsVm = new OptionsViewModel(new DaemonClient(), vm);
                OptionsWindow.ForgetLastTab();
                var options = new OptionsWindow { DataContext = optionsVm };
                windows.Add(options);
                options.Show();
                Settle(options);
                // The window opens at its own size, which is the size every
                // tab has to fit, on the tab a new run opens on.
                Avalonia.Platform.Screen screen = options.Screens.ScreenFromWindow(options)!;
                Size fitted = OptionsWindow.FitTo(screen.WorkingArea.Size.ToSize(screen.Scaling));
                Assert.Equal(fitted, options.ClientSize);
                Assert.True(options.ClientSize.Height * screen.Scaling <= screen.WorkingArea.Height,
                    $"Options is {options.ClientSize} on a screen of {screen.WorkingArea} at {screen.Scaling}");
                Assert.Equal(OptionsTab.General, options.SelectedTab);
                OptionsTabsWindowTests.Check(options);
                CheckTextSize(main, options);
                var deepTrace = options.FindControl<CheckBox>("DeepDiagnostics")!;
                Assert.False(deepTrace.IsChecked);
                Assert.Equal(Localizer.Text("DeepDiagnostics"), Assert.IsType<TextBlock>(deepTrace.Content).Text);
                // Every page scrolls inside the window when it has more than
                // the window has room for: the window never grows to fit one.
                foreach (OptionsTab tab in Enum.GetValues<OptionsTab>())
                    Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        OptionsTabsWindowTests.Page(options, tab).VerticalScrollBarVisibility);
                OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
                MainWindowLabelTests.CheckFixedColumnLabels(options, Localizer.Text("DefaultInput"), Localizer.Text("DefaultOutput"));
                MainWindowLabelTests.CheckFixedColumnTranslations(options, "DefaultInput", "DefaultOutput");
                OptionsTabsWindowTests.Show(options, OptionsTab.Appearance);
                MainWindowLabelTests.CheckFixedColumnLabels(options, Localizer.Text("MixerView"));
                MainWindowLabelTests.CheckFixedColumnTranslations(options, "MixerView");
                OptionsTabsWindowTests.Show(options, OptionsTab.Plugins);
                LocalizationRenderingTests.CheckPluginActions(options);
                OptionsTabsWindowTests.Show(options, OptionsTab.General);
                Assert.Equal(Localizer.Text("DesktopKeys"), options.FindControl<Button>("DesktopKeysButton")!.Content);
                var launch = options.FindControl<ComboBox>("LaunchBehavior")!;
                var close = options.FindControl<ComboBox>("CloseBehavior")!;
                Assert.Equal(1, launch.SelectedIndex);
                Assert.Equal(0, close.SelectedIndex);
                Assert.Equal(Localizer.Text("TrayOnly"), ((ComboBoxItem)launch.SelectedItem!).Content);
                Assert.Equal(Localizer.Text("KeepRunningInTray"), ((ComboBoxItem)close.SelectedItem!).Content);
                foreach (var picker in new[] { launch, close })
                {
                    AssertInside(picker, options);
                    AssertNoOverlap(((Grid)picker.Parent!).Children.Where(c => c.IsVisible).ToArray());
                }
                foreach (string heading in new[] { "ATLOGIN", "WINDOW" }.Select(Localizer.Text))
                    Assert.Single(options.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == heading);
                Capture(options, "options-startup");
                OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
                var notes = options.FindControl<StackPanel>("SoftwareMixerNotes")!;
                var audioHeading = options.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Text == Localizer.Text("AUDIO"));
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

                optionsVm.ApplyPluginSetup(new JsonObject
                {
                    ["yabridge"] = "5.1.1",
                    ["wine"] = true,
                    ["wineTrace"] = true,
                    ["windowsEditorNote"] = "The companion package fixes Windows editor input.",
                    ["memoryLockNote"] = OpenXLR.Core.Mixing.PluginMemoryLock.Note(8388608, 8388608, true),
                    ["skippedFailedCount"] = 1,
                    ["skippedFailedBundles"] = new JsonArray(new JsonObject
                    {
                        ["path"] = "/plugins/Skipped.vst3", ["reason"] = "timed out", ["failedAt"] = "2026-09-17T10:00:00Z",
                    }),
                });
                OptionsTabsWindowTests.Show(options, OptionsTab.Plugins);
                Settle(options);
                var skippedPlugins = options.FindControl<StackPanel>("SkippedPlugins")!;
                Assert.Contains(skippedPlugins.GetVisualDescendants().OfType<TextBlock>(),
                    b => b.Text == Localizer.Format("SkippedBundlesAfterFailedScan", 1));
                Assert.Contains(skippedPlugins.GetVisualDescendants().OfType<TextBlock>(),
                    b => b.Text?.Contains("/plugins/Skipped.vst3", StringComparison.Ordinal) == true);
                var memoryWarning = options.FindControl<TextBlock>("MemoryLockWarning")!;
                Assert.True(memoryWarning.IsVisible);
                Assert.True(memoryWarning.Bounds.Height > memoryWarning.FontSize * 2);
                AssertInside(memoryWarning, (Control)memoryWarning.Parent!);
                Assert.Single(options.GetVisualDescendants().OfType<Button>(),
                    b => b.IsEffectivelyVisible && Equals(b.Content, Localizer.Text("MemoryLockSetup")));
                memoryWarning.BringIntoView();
                Settle(options);
                Capture(options, "options-plugin-runtime");
                OptionsTabsWindowTests.Show(options, OptionsTab.Support);
                var trace = options.FindControl<CheckBox>("WineTrace")!;
                Assert.True(trace.IsChecked);
                Assert.True(trace.IsEnabled);
                trace.BringIntoView();
                Settle(options);
                AssertInside(trace, (Control)trace.Parent!);
                optionsVm.ApplyPluginSetup(JsonNode.Parse("""{"wineTrace":false}"""));
                Assert.False(trace.IsChecked);
                // A click with an unavailable daemon must clear the optimistic
                // tick and disable the switch, rather than claim tracing is on.
                trace.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                trace.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Null(trace.IsChecked);
                Assert.False(trace.IsEnabled);
                optionsVm.ApplyPluginSetup(JsonNode.Parse("{}"));
                OptionsTabsWindowTests.Show(options, OptionsTab.Plugins);
                Settle(options);
                Assert.False(memoryWarning.IsEffectivelyVisible);
                Assert.Null(trace.IsChecked);
                Assert.False(trace.IsEnabled);

                var focusedRules = new NativeEditorRulesWindow(new DaemonClient(), "vst3", "ABCDEF019182FAEB4D616E75466C7665");
                windows.Add(focusedRules);
                focusedRules.ApplyRules(JsonNode.Parse("""
                    {"rules":[{"kind":"vst3","plugin":"ABCDEF019182FAEB4D616E75466C7665","name":"Elgato De-Esser","reason":"Known issue","defaultBlocked":true,"blocked":true}]}
                    """));
                Assert.Equal("Elgato De-Esser", ((NativeEditorRuleRow)focusedRules.FindControl<ListBox>("RuleList")!.SelectedItem!).Name);

                var editorRules = new NativeEditorRulesWindow();
                windows.Add(editorRules);
                editorRules.Show();
                editorRules.ApplyRules(JsonNode.Parse("""
                    {"rules":[
                      {"kind":"vst3","plugin":"ABCDEF019182FAEB4D616E75466C7665","name":"Elgato De-Esser","reason":"Its native editor freezes under Wine.","defaultBlocked":true,"blocked":true},
                      {"kind":"clap","plugin":"example.effect","name":"Example effect","reason":"You allowed this native editor.","defaultBlocked":false,"override":false,"blocked":false}
                    ]}
                    """));
                foreach (double width in new[] { 480d, 780 })
                {
                    Layout(editorRules, width, 590);
                    var rules = editorRules.FindControl<ListBox>("RuleList")!;
                    rules.SelectedIndex = 0;
                    Assert.True(editorRules.FindControl<Button>("AllowSelected")!.IsEnabled);
                    Assert.False(editorRules.FindControl<Button>("DefaultSelected")!.IsEnabled);
                    rules.SelectedIndex = 1;
                    Assert.True(editorRules.FindControl<Button>("BlockSelected")!.IsEnabled);
                    Assert.True(editorRules.FindControl<Button>("DefaultSelected")!.IsEnabled);
                    rules.SelectedIndex = 0;
                    Layout(editorRules, width, 590);
                    foreach (var button in editorRules.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
                        AssertInside(button, editorRules);
                    Capture(editorRules, "native-editor-rules-" + width);
                }

                optionsVm.ApplyPluginSetup(JsonNode.Parse("""
                    {"yabridge":"5.1.1","wine":true,"bridgeProvider":"system",
                     "windowsImportDirectory":"~/.local/share/openxlr/windows-plugins",
                     "searchDirectories":[{"kind":"lv2","path":"/usr/lib/lv2","custom":false,"exists":true},
                       {"kind":"clap","path":"/offline/custom plugins","custom":true,"exists":false}],
                     "windowsDirectories":["/home/test/.wine/drive_c/Program Files/Common Files/VST3",
                     "/home/test/Downloads/A plugin collection with a long folder name/Windows/VST3/x64"]}
                    """));
                var folders = new PluginFoldersWindow { DataContext = optionsVm };
                windows.Add(folders);
                folders.Show();
                foreach (double width in new[] { 480d, 720 })
                {
                    Layout(folders, width, width >= 720 ? 820 : 680);
                    var searchPaths = folders.FindControl<ListBox>("SearchDirectoryList")!;
                    Assert.Equal(2, searchPaths.ItemCount);
                    searchPaths.SelectedIndex = 0;
                    Assert.False(folders.FindControl<Button>("RemoveSearchPath")!.IsEnabled);
                    searchPaths.SelectedIndex = 1;
                    Assert.True(folders.FindControl<Button>("RemoveSearchPath")!.IsEnabled);
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
                    Assert.Equal(Localizer.Text("EnableInOpenXLR"), folders.FindControl<Button>("TogglePlugin")!.Content);
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
                AssertLiveLayoutOrder(main, vm);
                EffectWorkflowWindowTests.CheckControlOwnership(main, vm.Inserts.Client);
                PluginCatalogueUiTests.CheckStaleReplies();
                FaderInputWindowTests.Check(main);
                MicSetupWindowTests.CheckPendingClose();
                MicSetupWindowTests.CheckFits();
                AssertLayoutAppearance(main, vm);
                SkinLayoutWindowTests.Check();
                MixerViewWindowTests.Check();
                ChannelTileAlignmentWindowTests.Check();
                MixerPopOutWindowTests.Check(main);
                // The arrange check opens its own window, with a real pointer
                // on it; nothing else may sit in front.
                foreach (var window in windows) window.Hide();
                WindowOrderTests.Check();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(600)), "Window layout hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckTextSize(MainWindow main, OptionsWindow options)
    {
        var picker = options.FindControl<ComboBox>("TextSizePicker")!;
        var title = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("title") && t.Text == "OpenXLR");
        var action = main.FindControl<Button>("ManageApps")!;
        var desktopKeys = options.FindControl<Button>("DesktopKeysButton")!;
        var appName = main.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Google Chrome" && t.IsEffectivelyVisible);
        var normal = (Title: title.FontSize, Action: action.FontSize, Button: desktopKeys.FontSize, AppName: appName.FontSize);
        Assert.Equal(2, picker.SelectedIndex);
        int[] percentages = [80, 90, 100, 110, 120];
        for (int i = 0; i < percentages.Length; i++)
        {
            int percent = percentages[i];
            double scale = percent / 100d;
            picker.SelectedIndex = i;
            Layout(main, i == 4 ? 640 : 1280, 900);
            Settle(options);
            Assert.Equal(percent, UiSettings.Load().TextSizePercent);
            Assert.Equal(normal.Title * scale, title.FontSize, 0.01);
            Assert.Equal(normal.Action * scale, action.FontSize, 0.01);
            Assert.Equal(normal.Button * scale, desktopKeys.FontSize, 0.01);
            Assert.Equal(normal.AppName * scale, appName.FontSize, 0.01);
            Assert.Equal(Math.Ceiling(90d * percent / 100), (double)main.FindResource("UiText.Width90")!);
            Assert.Equal(Math.Ceiling(110d * percent / 100), (double)main.FindResource("UiText.Width110")!);
            AssertInside(action, main);
            if (percent == 120)
            {
                MainWindowLabelTests.Check(main);
                MainWindowLabelTests.CheckAutoColumnTranslations(main, "XLR1Gain", "XLR2Gain", "Devices", "Volume", "USBAuxIn", "MicPC", "Inserts");
                OptionsTabsWindowTests.Show(options, OptionsTab.General);
                MainWindowLabelTests.CheckAutoColumnTranslations(options, "OnLaunch", "WhenClosing");
                OptionsTabsWindowTests.CheckTranslations(options);
                OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
                MainWindowLabelTests.CheckAutoColumnTranslations(options, "DefaultOutput", "DefaultInput");
                OptionsTabsWindowTests.Show(options, OptionsTab.Appearance);
                MainWindowLabelTests.CheckAutoColumnTranslations(options, "MixerView", "Skin", "Mode", "TextSize", "Controls", "Language");
                // The input rows' labels hold a channel name, so they keep a
                // width, and that width grows with the text.
                foreach (string row in new[] { "Xlr1Binding", "Xlr2Binding" })
                    Assert.Equal((double)main.FindResource("UiText.Width90")!,
                        main.FindControl<Grid>(row)!.Children.OfType<TextBlock>().First().Width);
            }
            if (percent is 80 or 120)
            {
                Capture(main, $"text-size-main-{percent}");
                foreach (OptionsTab tab in Enum.GetValues<OptionsTab>())
                {
                    OptionsTabsWindowTests.Show(options, tab);
                    Capture(options, $"text-size-options-{percent}-{tab.ToString().ToLowerInvariant()}");
                }
                OptionsTabsWindowTests.Show(options, OptionsTab.Appearance);
            }
        }
        PinMaterial(UiSettings.Load());
        OpenXLR.UI.Skinning.SkinService.Initialize();
        Assert.Equal(120, OpenXLR.UI.Skinning.SkinService.TextSizePercent);
        picker.SelectedIndex = 2;
        Settle(options);
        Assert.Equal(normal.Title, title.FontSize);
        Assert.Equal(normal.Action, action.FontSize);
        Assert.Equal(normal.Button, desktopKeys.FontSize);
        Assert.Equal(normal.AppName, appName.FontSize);
    }

    private static void AssertLiveLayoutOrder(MainWindow main, MainViewModel model)
    {
        LiveLayoutOrderTests.Apply(model, ["game", "music"], ["monitor", "stream", "chat"]);
        Layout(main, 1040, 900);
        LiveLayoutOrderTests.Apply(model, ["music", "game"], ["monitor", "chat", "stream"]);
        Layout(main, 1040, 900);
        var channels = main.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChannelViewModel && b.Name == "ClassicChannelTile")
            .OrderBy(b => b.TranslatePoint(default, main)!.Value.X).ToArray();
        Assert.Equal(["music", "game"], channels.Select(b => ((ChannelViewModel)b.DataContext!).Id));
        foreach (var channel in channels)
        {
            var sliders = channel.GetVisualDescendants().OfType<Slider>().Where(s => s.DataContext is SendViewModel)
                .OrderBy(s => s.TranslatePoint(default, channel)!.Value.Y).ToArray();
            Assert.Equal(["monitor", "chat", "stream"], sliders.Select(s => ((SendViewModel)s.DataContext!).MixId));
            // The daemon reported no channel volume, so no tile draws one.
            Assert.DoesNotContain(channel.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible && s.DataContext is ChannelViewModel);
        }

        // A channel volume the daemon reports stands over the channel's sends.
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse("""
            {"channels":[{"id":"music","name":"music","volume":0.4},{"id":"game","name":"game","hardware":true,"volume":0.5}],
             "mixes":[{"id":"monitor","name":"monitor"},{"id":"chat","name":"chat"},{"id":"stream","name":"stream"}]}
            """)]);
        Layout(main, 1040, 900);
        Slider[] music = [.. channels[0].GetVisualDescendants().OfType<Slider>().Where(s => s.IsEffectivelyVisible)
            .OrderBy(s => s.TranslatePoint(default, channels[0])!.Value.Y)];
        Assert.Equal(4, music.Length);
        Assert.IsType<ChannelViewModel>(music[0].DataContext);
        Assert.Equal(0.4, music[0].Value);
        Assert.Equal(Localizer.Language == "en" ? "music volume" : Localizer.Format("ChannelVolumeName", "music"), Avalonia.Automation.AutomationProperties.GetName(music[0]));
        Assert.All(music[1..], s => Assert.IsType<SendViewModel>(s.DataContext));
        // A hardware input has one too, over its sends the same way.
        Slider[] game = [.. channels[1].GetVisualDescendants().OfType<Slider>().Where(s => s.IsEffectivelyVisible)
            .OrderBy(s => s.TranslatePoint(default, channels[1])!.Value.Y)];
        Assert.IsType<ChannelViewModel>(game[0].DataContext);
        Assert.Equal(0.5, game[0].Value);
        Assert.Equal(Localizer.Language == "en" ? "game volume" : Localizer.Format("ChannelVolumeName", "game"), Avalonia.Automation.AutomationProperties.GetName(game[0]));
        Assert.Equal(music[1].TranslatePoint(default, main)!.Value.Y, game[1].TranslatePoint(default, main)!.Value.Y, 0.5);
        LiveLayoutOrderTests.Apply(model, ["music", "game"], ["monitor", "chat", "stream"]);
        Layout(main, 1040, 900);
        var mixes = main.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is MixViewModel && b.Name == "ClassicMasterTile")
            .OrderBy(b => b.TranslatePoint(default, main)!.Value.Y)
            .ThenBy(b => b.TranslatePoint(default, main)!.Value.X).ToArray();
        Assert.Equal(["monitor", "chat", "stream"], mixes.Select(b => ((MixViewModel)b.DataContext!).Id));
    }

    /// <summary>A hidden channel loses its strip, and a chosen colour reaches the name over the skin's.</summary>
    private static void AssertLayoutAppearance(MainWindow main, MainViewModel model)
    {
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor A","appearance":{"icon":"◆","colour":"#12ABCD"}},
                      {"id":"stream","name":"Stream","appearance":{"defaultIcon":"broadcast"}}],
             "channels":[{"id":"game","name":"Game","levels":{"monitor":1},"appearance":{"hidden":true}},
                         {"id":"music","name":"Music","levels":{"monitor":1},"appearance":{"icon":"♫","colour":"#ABC123"}},
                         {"id":"browser","name":"Browser","levels":{"monitor":1},"appearance":{"defaultIcon":"browser","colour":"#33CC99"}},
                         {"id":"sfx","name":"SFX","levels":{"monitor":1},"appearance":{"icon":"none","defaultIcon":"burst"}}],
             "outputAppearance":{"phones1":{"icon":"star","colour":"#AA3355","defaultIcon":"headset"}}}
            """)]);
        Layout(main, 1040, 900);
        var strips = main.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is ChannelViewModel && b.Name == "ClassicChannelTile").ToArray();
        Assert.False(strips.Single(b => ((ChannelViewModel)b.DataContext!).Id == "game").IsVisible);
        Border music = strips.Single(b => ((ChannelViewModel)b.DataContext!).Id == "music");
        Assert.True(music.IsVisible);
        foreach (string text in new[] { "♫", "Music" })
        {
            TextBlock label = music.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);
            Assert.True(label.IsVisible);
            Assert.Equal(Avalonia.Media.Color.Parse("#ABC123"), ((Avalonia.Media.ISolidColorBrush)label.Foreground!).Color);
        }
        // A text glyph, a drawing and no icon at all each share the name's line.
        ChannelTileAlignmentWindowTests.AssertIconsShareTheNameLine(main, "with layout appearances at 1040");
        // A default icon is a drawing in the item's colour, before the name; "none" draws nothing.
        LayoutIconBlock browser = strips.Single(b => ((ChannelViewModel)b.DataContext!).Id == "browser")
            .GetVisualDescendants().OfType<LayoutIconBlock>().Single();
        Assert.True(browser.IsVisible && browser.DrawsPath);
        Assert.Equal("browser", browser.Shown);
        Assert.Equal(Avalonia.Media.Color.Parse("#33CC99"), ((Avalonia.Media.ISolidColorBrush)browser.Foreground!).Color);
        Assert.False(strips.Single(b => ((ChannelViewModel)b.DataContext!).Id == "sfx")
            .GetVisualDescendants().OfType<LayoutIconBlock>().Single().IsVisible);
        LayoutIconBlock stream = main.GetVisualDescendants().OfType<Border>()
            .Single(b => b.DataContext is MixViewModel { Id: "stream" } && b.Name == "ClassicMasterTile").GetVisualDescendants().OfType<LayoutIconBlock>().Single();
        Assert.True(stream.IsVisible && stream.DrawsPath);
        // The headphone jacks wear their icon before their label, in their colour; without one, the headset.
        LayoutIconBlock phones1 = main.FindControl<Grid>("Phones1Label")!.GetVisualDescendants().OfType<LayoutIconBlock>().Single();
        Assert.True(phones1.IsEffectivelyVisible && phones1.DrawsPath);
        Assert.Equal("star", phones1.Shown);
        Assert.Equal(Avalonia.Media.Color.Parse("#AA3355"), ((Avalonia.Media.ISolidColorBrush)phones1.Foreground!).Color);
        Assert.Equal("headset", main.FindControl<Grid>("Phones2Label")!.GetVisualDescendants().OfType<LayoutIconBlock>().Single().Shown);
        // Every built-in drawing is one Avalonia accepts.
        Assert.All(OpenXLR.UI.LayoutIcons.BuiltIn, icon => Assert.NotNull(LayoutIconRegistry.Geometry(icon.Name)));
        Border mix = main.GetVisualDescendants().OfType<Border>().Single(b => b.DataContext is MixViewModel { Id: "monitor" } && b.Name == "ClassicMasterTile");
        TextBlock mixName = mix.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Monitor A");
        Assert.Equal(Avalonia.Media.Color.Parse("#12ABCD"), ((Avalonia.Media.ISolidColorBrush)mixName.Foreground!).Color);
        Capture(main, "layout-appearance-1040");
        foreach (string section in new[] { "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile" })
            if (main.FindControl<Expander>(section) is { } expander) expander.IsExpanded = false;
        Layout(main, 1040, 900);
        Capture(main, "layout-appearance-tiles-1040");
        foreach (string section in new[] { "InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile" })
            if (main.FindControl<Expander>(section) is { } expander) expander.IsExpanded = true;
    }

    /// <summary>
    /// The create rows offer the icon beside the name, starting at the default,
    /// and the mix row's default follows the kind chosen beside it.
    /// </summary>
    private static void CheckCreateIconPickers(MixerSetupWindow setup)
    {
        ComboBox Named(string name) => setup.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == name);
        ComboBox channelIcon = Named("NewChannelIcon"), mixIcon = Named("NewMixIcon"), kind = Named("NewMixKind");
        Assert.Equal("", LayoutIconPicker.Chosen(channelIcon));
        Assert.Equal(Localizer.Format("IconDefaultNamed", "apps"), AutomationProperties.GetName((ComboBoxItem)channelIcon.Items[0]!));
        Assert.Equal(Localizer.Format("IconDefaultNamed", "podcast"), AutomationProperties.GetName((ComboBoxItem)mixIcon.Items[0]!));
        mixIcon.SelectedItem = mixIcon.Items.OfType<ComboBoxItem>().Single(i => i.Tag as string == "record");
        kind.SelectedIndex = 1;
        Assert.Equal(Localizer.Format("IconDefaultNamed", "headset"), AutomationProperties.GetName((ComboBoxItem)mixIcon.Items[0]!));
        Assert.Equal("record", LayoutIconPicker.Chosen(mixIcon));
        // The Text heading is a label, not a choice.
        Assert.Single(mixIcon.Items.OfType<ComboBoxItem>(), i => !i.IsEnabled && i.Tag is null);
        Layout(setup, 600, 640);
        foreach (ComboBox box in new[] { channelIcon, mixIcon, kind }) AssertInside(box, setup);
        AssertInside(setup.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Phones1AppearanceButton"), setup);
        Capture(setup, "layout-editor-600");

        // Open, every entry draws its icon beside its name; a built-in one as a drawing.
        mixIcon.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        Layout(setup, 600, 640);
        var record = mixIcon.Items.OfType<ComboBoxItem>().Single(i => i.Tag as string == "record");
        LayoutIconBlock drawn = record.GetVisualDescendants().OfType<LayoutIconBlock>().Single();
        Assert.True(drawn.DrawsPath);
        Assert.True(drawn.Bounds.Width > 8);
        if (TopLevel.GetTopLevel(record) is { } popup) Capture(popup, "layout-editor-icon-picker");
        mixIcon.IsDropDownOpen = false;
        kind.SelectedIndex = 0;
        LayoutIconPicker.Fill(mixIcon, "", "podcast");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The layout editor's appearance dialog offers the hidden flag for a channel and a mix, never for an output.</summary>
    private static void CheckAppearanceDialog(MixerSetupWindow setup, List<Window> windows)
    {
        var client = new DaemonClient();
        foreach (object item in new object[] { new ChannelViewModel(client, "game", "Game", []), new MixViewModel(client, "stream", "Stream"), "phones1" })
        {
            // A headphone output's button carries its id and the window's own data.
            Button sender = item is string output ? new Button { Tag = output } : new Button { DataContext = item };
            typeof(MixerSetupWindow).GetMethod("OnAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(setup, [sender, new Avalonia.Interactivity.RoutedEventArgs()]);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(setup.OwnedWindows);
            windows.Add(dialog);
            Layout(dialog, 440, 360);
            Control Named(string name) => dialog.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
            // Default, None, the built-in set, the Text heading and the seven glyphs.
            var picker = (ComboBox)Named("AppearanceIcon");
            Assert.Equal(OpenXLR.UI.LayoutIcons.BuiltIn.Count + OpenXLR.UI.LayoutIcons.Glyphs.Count + 3, picker.ItemCount);
            Assert.Equal("", LayoutIconPicker.Chosen(picker));
            picker.SelectedIndex = 1;
            Assert.Equal("none", LayoutIconPicker.Chosen(picker));
            picker.SelectedIndex = 0;
            CheckBox? hide = dialog.GetVisualDescendants().OfType<CheckBox>().SingleOrDefault(c => c.Name == "AppearanceHidden" && c.IsVisible);
            Assert.Equal(item is not string, hide is not null);
            if (hide is not null)
                Assert.Equal(Localizer.Text(item is MixViewModel ? "HideMix" : "HideChannelStrip"), hide.Content);
            // The swatches fill the field and follow it: a typed palette value rings its swatch.
            var colourField = (TextBox)Named("AppearanceColour");
            Button[] swatches = [.. ((Panel)Named("AppearanceSwatches")).Children.OfType<Button>()];
            Assert.Equal(OpenXLR.UI.LayoutColours.Palette.Count + 1, swatches.Length);
            Assert.All(swatches, b => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))));
            Assert.All(swatches, b => AssertInside(b, dialog));
            swatches[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(OpenXLR.UI.LayoutColours.Palette[2].Hex, colourField.Text);
            Assert.True(Ring(swatches[3]));
            Layout(dialog, 440, 360);
            Capture(dialog, "layout-appearance-swatches-" + (item is ChannelViewModel ? "channel" : item is string ? "output" : "mix"));
            colourField.Text = OpenXLR.UI.LayoutColours.Palette[0].Hex.ToLowerInvariant();
            Assert.True(Ring(swatches[1]));
            Assert.False(Ring(swatches[3]));
            swatches[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("", colourField.Text);
            Assert.True(Ring(swatches[0]));
            colourField.Text = "red";
            Assert.DoesNotContain(swatches, Ring);
            Named("AppearanceSave").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsVisible);   // a colour that is not #RRGGBB is not sent
            foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
                AssertInside(button, dialog);
            Capture(dialog, "layout-appearance-dialog-" + (item is ChannelViewModel ? "channel" : item is string ? "output" : "mix"));
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Whether a colour swatch wears the ring that marks it as the field's value.</summary>
    internal static bool Ring(Button swatch) => ((Panel)swatch.Content!).Children.OfType<Border>().Last().IsVisible;

    internal static void AddInsert(InsertsViewModel owner)
    {
        const string uri = "http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono";
        owner.PluginChoices.Add(new(uri, "LSP Graphic Equalizer x16 Mono", "EQ",
            JsonNode.Parse("""[{"symbol":"gain","name":"A very long parameter name for input gain","min":-60,"max":12,"default":0}]""")!, true, true));
        owner.Apply(JsonNode.Parse("""
            [{"insert":{"id":"eq","plugin":"http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono",
            "label":"LSP Graphic Equalizer x16 Mono with a very long preset name","nativeHost":true},"nativeHostRunning":true}]
            """));
    }

    /// <summary>
    /// Put the window at this size and lay it out. The size goes to the
    /// native surface, and a mapped X11 window takes it only when the server
    /// answers with its ConfigureNotify, so the loop runs until the server
    /// has answered every request and the window asks for nothing more: a
    /// window with a minimum, or sized to its content, asks the surface for
    /// its own size next, and the loop takes that answer too. A fixed wait
    /// was wrong both ways: a busy runner answers later than any fixed time,
    /// and a window still at its old size answers the wrong questions, with
    /// its controls stacked for a width it no longer has.
    /// </summary>
    /// <summary>
    /// Save Material as the user's choice in the redirected ui.json before the
    /// application starts. A window test that measures the classic window
    /// would otherwise start on Broadcast, which a launch with no saved skin
    /// now wears; the tests that check Broadcast put it on themselves.
    /// </summary>
    internal static void PinMaterial(UiSettings? settings = null) =>
        Assert.Null(((settings ?? new UiSettings()) with { Skin = OpenXLR.UI.Skinning.SkinPackage.DefaultId }).Save());

    internal static void Layout(Window window, double width, double height)
    {
        var asked = new Size(width, height);
        window.PlatformImpl!.GetType().GetMethod("Resize", [typeof(Size), typeof(WindowResizeReason)])!
            .Invoke(window.PlatformImpl, [asked, WindowResizeReason.User]);
        Settle(window, $"after asking for {asked}");
    }

    /// <summary>
    /// Lay the window out at the size it takes for itself, as <see cref="Layout"/>
    /// does after a resize: until the server has answered every request and
    /// the window asks for nothing more.
    /// </summary>
    internal static void Settle(Window window, string? why = null)
    {
        IntPtr display = XServer.DisplayOf(window);
        using var stop = new CancellationTokenSource();
        bool waiting = true, settled = false;
        // A round trip brings every answer the server owes into the client's
        // queue, which the loop reads after each batch of jobs; so a job that
        // finds the queue full looks again after it, and a job that finds it
        // empty after a layout, where a window that wants another size asks
        // for it, knows the size is final.
        void Settle() => Dispatcher.UIThread.Post(() =>
        {
            if (!waiting) return;
            if (XServer.HasAnswers(display)) { Settle(); return; }
            window.UpdateLayout();
            if (XServer.HasAnswers(display)) { Settle(); return; }
            settled = true;
            stop.Cancel();
        }, DispatcherPriority.Background);
        Settle();
        try
        {
            using var watchdog = DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.MainLoop(stop.Token);
        }
        finally { waiting = false; }
        Assert.True(settled, $"{window.GetType().Name} is {window.ClientSize} and wants {window.DesiredSize} {why}.");
        window.UpdateLayout();
    }

    /// <summary>The X connection behind a window, and the round trip that settles it.</summary>
    private static class XServer
    {
        [System.Runtime.InteropServices.DllImport("libX11.so.6")]
        private static extern int XSync(IntPtr display, int discard);

        [System.Runtime.InteropServices.DllImport("libX11.so.6")]
        private static extern int XPending(IntPtr display);

        /// <summary>The display the window's platform object talks to, or zero off X11.</summary>
        internal static IntPtr DisplayOf(Window window)
        {
            object impl = window.PlatformImpl!;
            object? info = impl.GetType().GetField("_x11", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(impl);
            return info?.GetType().GetProperty("Display")?.GetValue(info) is IntPtr display ? display : IntPtr.Zero;
        }

        /// <summary>
        /// True while the client holds events it has not read. The round trip
        /// first makes the server answer every request sent so far, so a
        /// ConfigureNotify still owed is in the queue when this returns.
        /// </summary>
        internal static bool HasAnswers(IntPtr display)
        {
            if (display == IntPtr.Zero) return false;
            XSync(display, 0);
            return XPending(display) != 0;
        }
    }

    /// <summary>The shared monitor action remains visible and named under every skin and width.</summary>
    internal static void AssertMonitorMute(MainWindow main)
    {
        var mute = main.FindControl<Avalonia.Controls.Primitives.ToggleButton>("MonitorMute")!;
        Assert.True(mute.IsEffectivelyVisible);
        Assert.Equal(Localizer.Text("MuteMonitoring"), AutomationProperties.GetName(mute));
        Assert.Equal(Localizer.Text("Mute"), mute.Content);
        Assert.True(mute.Bounds.Width > 40, $"Monitoring mute shrank to {mute.Bounds.Width}.");
        AssertInside(mute, main.FindControl<Expander>("MonitorTile")!);
    }

    /// <summary>
    /// The control sits inside the box of one of its ancestors, measured in
    /// that ancestor's own coordinates, so it works for a card, a panel or the
    /// window itself. Height is only checked against an immediate parent: a
    /// scrolled page is taller than its window on purpose.
    /// </summary>
    internal static void AssertInside(Control child, Visual ancestor)
    {
        Assert.True(child.Bounds.Width > 0, $"{child.GetType().Name} has no width.");
        var origin = child.TranslatePoint(default, ancestor)!.Value;
        Size box = ancestor is TopLevel top ? top.ClientSize : ancestor.Bounds.Size;
        string what = Describe(child);
        Assert.True(origin.X >= -0.1 && origin.X <= box.Width, $"{what} starts at x={origin.X:0.#}, outside {ancestor.GetType().Name} {box.Width:0.#} wide.");
        Assert.True(origin.X + child.Bounds.Width <= box.Width + 0.1,
            $"{what} ends at x={origin.X + child.Bounds.Width:0.#}, past the {box.Width:0.#} of its {ancestor.GetType().Name}.");
        if (ReferenceEquals(child.Parent, ancestor))
            Assert.True(origin.Y + child.Bounds.Height >= 0 && origin.Y + child.Bounds.Height <= box.Height + 0.1,
                $"{what} ends at y={origin.Y + child.Bounds.Height:0.#}, past the {box.Height:0.#} of its {ancestor.GetType().Name}.");
    }

    /// <summary>A control named for a failure: its type, name, text and size, plus the named ancestors above it.</summary>
    internal static string Describe(Control child)
    {
        string text = child switch
        {
            TextBlock t => $" \"{t.Text}\"",
            ContentControl { Content: string s } => $" \"{s}\"",
            _ => "",
        };
        string path = string.Join(" < ", child.GetVisualAncestors().OfType<Control>().Where(a => a.Name is not null).Take(4).Select(a => a.Name));
        return $"{child.GetType().Name}{(child.Name is null ? "" : $" {child.Name}")}{text} ({child.Bounds.Width:0.#}x{child.Bounds.Height:0.#}, under {path})";
    }

    internal static void AssertNoOverlap(IReadOnlyList<Control> controls)
    {
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                var root = (Visual)TopLevel.GetTopLevel(controls[i])!;
                var first = new Rect(controls[i].TranslatePoint(default, root)!.Value, controls[i].Bounds.Size);
                var second = new Rect(controls[j].TranslatePoint(default, root)!.Value, controls[j].Bounds.Size);
                var intersection = first.Intersect(second);
                // Fractional display scales can transform touching edges a
                // floating-point rounding error apart. Still reject subpixel overlaps.
                Assert.True(intersection.Width <= 0.000001 || intersection.Height <= 0.000001,
                    $"{Describe(controls[i])} at {first} overlaps {Describe(controls[j])} at {second}.");
            }
    }

    /// <summary>
    /// Stop a window's background connection, so a test that holds a device
    /// state of its own is not undone by the reconnect loop reporting the
    /// daemon gone.
    /// </summary>
    internal static void StopConnecting(MainWindow window)
    {
        ((DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Capture(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// The window with an open popup, a flyout's or a menu's, drawn over it
    /// where the popup stands on the screen. The picture grows to hold a
    /// popup that reaches past the window.
    /// </summary>
    internal static void Capture(TopLevel window, string name, Visual popupContent)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS") is not { Length: > 0 } directory) return;
        TopLevel popup = TopLevel.GetTopLevel(popupContent)!;
        Directory.CreateDirectory(directory);
        using var page = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        page.Render(window);
        using var layer = new RenderTargetBitmap(new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height));
        layer.Render(popup);
        PixelPoint origin = window.PointToScreen(default), at = popup.PointToScreen(default);
        var place = new Rect(at.X - origin.X, at.Y - origin.Y, popup.Bounds.Width, popup.Bounds.Height);
        using var composed = new RenderTargetBitmap(new PixelSize(
            (int)Math.Max(window.Bounds.Width, place.Right), (int)Math.Max(window.Bounds.Height, place.Bottom)));
        using (Avalonia.Media.DrawingContext context = composed.CreateDrawingContext())
        {
            context.DrawImage(page, new Rect(page.Size));
            context.DrawImage(layer, place);
        }
        composed.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
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
