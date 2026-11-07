using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

internal static class MicSetupWindowTests
{
    internal static async Task CheckCaptureChain(MainViewModel main, MixInsertsWindow chain, ConcurrentQueue<JsonNode> commands,
        ConcurrentQueue<JsonNode> meterWatches, ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands,
        Action<JsonNode?> reportCaptureGain, ConcurrentQueue<JsonNode> gainCommands)
    {
        InsertsViewModel inserts = main.CaptureInputs.Single(c => c.Id == "guest").Inserts!;
        MicSetupViewModel model = inserts.MicSetup;
        Click(chain, "MicSetup");
        await Until(() => chain.OwnedWindows.OfType<MicSetupWindow>().Any(w => w.IsVisible));
        MicSetupWindow window = chain.OwnedWindows.OfType<MicSetupWindow>().Single(w => w.IsVisible);
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.Same(model, window.DataContext);
        Assert.False(main.CaptureInputs.Single(c => c.Id == "camera").Inserts!.CanMicSetup);
        try
        {
            // Position first: a USB microphone on no unit OpenXLR drives gets
            // the general tips and is told to switch phantom power elsewhere.
            Assert.True(model.ShowsPosition);
            Assert.False(window.FindControl<Control>("MicSetupLevel")!.IsVisible);
            Assert.Equal(MicSetupUnitKind.None, model.UnitKind);
            Assert.Equal(Localizer.Text("MicSetupPositionNone"), window.FindControl<TextBlock>("MicSetupPositionIntro")!.Text);
            Assert.Equal(4, model.PositionTips.Count);
            Assert.True(window.FindControl<Control>("MicSetupPhantomTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("MicSetupPhantom")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("MicSetupPhantomElsewhere")!.IsEffectivelyVisible);

            // Then Level: the window keeps the meter before the chain
            // running, and its reading and clip reach this microphone only.
            Step(window, "MicSetupStepLevel");
            Assert.True(model.ShowsLevel);
            await Until(() => meterWatches.Any(c => c["channel"]?.GetValue<string>() == "guest") && model.Clipped);
            Assert.Equal(.8, model.InputPeak);
            Assert.Equal(.5, model.InputLevel);
            Assert.Equal(0, main.Inserts.MicSetup.InputPeak);
            Assert.Equal(Localizer.Format("MicSetupPeak", .8 * 60 - 60), window.FindControl<TextBlock>("MicSetupPeakValue")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicSetupNoGain")!.IsEffectivelyVisible);
            window.FindControl<Button>("MicSetupClip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.Clipped);
            // Suggest gain on a microphone OpenXLR sets no gain for: it
            // listens, says the change on the device, and offers no Accept.
            var time = new ManualTime();
            model.Time = time;
            Click(window, "MicSetupSuggestGain");
            Assert.True(model.Measuring);
            Assert.False(window.FindControl<Button>("MicSetupSuggestGain")!.IsEnabled);
            await Until(() => window.FindControl<TextBlock>("MicSetupSuggestAdvice")!.IsEffectivelyVisible);
            for (int i = 0; i < 100 && model.Measuring; i++)
            {
                time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));
                model.ApplyInputLevel(new JsonArray(.5, .7, 0));
            }
            Assert.Equal(GainOutcome.Elsewhere, model.Suggestion!.Outcome);
            await Until(() => window.FindControl<Button>("MicSetupSuggestGain")!.IsEnabled
                && window.FindControl<TextBlock>("MicSetupSuggestMeasured")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("MicSetupAcceptGain")!.IsVisible);
            Assert.True(window.FindControl<Button>("MicSetupDiscardGain")!.IsEffectivelyVisible);
            Click(window, "MicSetupDiscardGain");
            await Until(() => !window.FindControl<TextBlock>("MicSetupSuggestAdvice")!.IsVisible);
            Assert.False(window.FindControl<Control>("MicSetupListen")!.IsVisible);

            var capture = main.CaptureInputs.Single(c => c.Id == "guest");
            reportCaptureGain(JsonNode.Parse("""{"gainDb":-6,"minDb":-60,"maxDb":10,"hardware":false}"""));
            await Until(() => capture.ShowsCaptureGain);
            var gain = window.FindControl<NumericUpDown>("MicSetupCaptureGainValue");
            Assert.NotNull(gain);
            Assert.True(gain.IsEffectivelyVisible);
            Assert.Equal((-60m, 10m, -6m), (gain.Minimum, gain.Maximum, gain.Value!.Value));
            Assert.False(window.FindControl<TextBlock>("MicSetupNoGain")!.IsEffectivelyVisible);
            Assert.Empty(gainCommands);
            EnterGain(gain, (-12.5).ToString(CultureInfo.CurrentCulture));
            await Until(() => !gainCommands.IsEmpty);
            JsonNode captureWrite = Assert.Single(gainCommands);
            Assert.Equal("setCaptureGain", captureWrite["cmd"]!.GetValue<string>());
            Assert.Equal("guest", captureWrite["channel"]!.GetValue<string>());
            Assert.Equal(-12.5, captureWrite["value"]!.GetValue<double>());
            Assert.Equal(-12.5, capture.CaptureGainDb);
            var captureSlider = window.FindControl<Slider>("MicSetupCaptureGain")!;
            Assert.Equal(-12.5, captureSlider.Value);
            captureSlider.Value = -14;
            await Until(() => gainCommands.Count == 2);
            Assert.Equal(-14, gainCommands.Last()["value"]!.GetValue<double>());
            Assert.Equal(-14m, gain.Value);
            reportCaptureGain(null);
            await Until(() => !gain.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("MicSetupNoGain")!.IsEffectivelyVisible);

            // Then Listen, from the step bar.
            Step(window, "MicSetupStepListen");
            Assert.True(model.ShowsListen);
            Assert.False(window.FindControl<Control>("MicSetupLevel")!.IsVisible);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            Assert.False(main.Inserts.MicSetup.Active);
            Assert.False(main.Inserts2.MicSetup.Active);
            Assert.False(main.CaptureInputs.Single(c => c.Id == "offline").Inserts!.MicSetup.Active);
            Assert.Equal(Localizer.Format("SoundCheckRecording", 1.5), window.FindControl<TextBlock>("MicSetupStatus")!.Text);
            Assert.False(model.HasTake);
            Click(window, "MicSetupLoopTake");
            // The state names a take this view has not seen, so it asks for it.
            await Until(() => model.Status == Localizer.Format("MicSetupLooping", 1.5) && model.HasTake);
            Assert.Equal(200, model.Peaks!.Count);
            Assert.Same(model.Peaks, window.FindControl<TakeWaveform>("MicSetupWaveform")!.Peaks);
            Assert.Equal(Localizer.Format("MicSetupHearMyselfLatency", 42.7), window.FindControl<TextBlock>("MicSetupLatency")!.Text);
            Assert.True(window.FindControl<TextBlock>("MicSetupNotHeard")!.IsEffectivelyVisible);

            // Raw and processed: the lit side is the daemon's.
            window.FindControl<ToggleButton>("MicSetupRaw")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.Raw && window.FindControl<ToggleButton>("MicSetupRaw")!.IsChecked == true
                && window.FindControl<ToggleButton>("MicSetupProcessed")!.IsChecked == false);
            window.FindControl<ToggleButton>("MicSetupProcessed")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !model.Raw && window.FindControl<ToggleButton>("MicSetupProcessed")!.IsChecked == true);
            // Hear myself, and the note that the microphone is not heard goes.
            window.FindControl<CheckBox>("MicSetupHearMyself")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => model.HearMyself && !model.NotHeard);
            Assert.False(window.FindControl<TextBlock>("MicSetupNotHeard")!.IsEffectivelyVisible);

            await CheckShape(window, main, model, eqCommands, bypassCommands);
            Step(window, "MicSetupStepListen");

            Click(window, "MicSetupLiveMic");
            await Until(() => model.Status == Localizer.Format("MicSetupReady", 1.5) && model.CanLoop);
            Click(window, "StopAndDiscard");
            await Until(() => !model.Active && model.CanRecord && !model.HasTake);
            Click(window, "Record");
            await Until(() => model.Active && model.CanLoop);
            window.Close();
            await Until(() => !window.IsVisible && !model.Active);
            Assert.Equal(["record", "loop", "raw:True", "raw:False", "hear:True", "live", "stop", "record", "stop"],
                commands.Select(c => c["cmd"]!.GetValue<string>() switch
                {
                    "setSoundCheckRaw" => $"raw:{c["value"]!.GetValue<bool>()}",
                    "setSoundCheckHearMyself" => $"hear:{c["value"]!.GetValue<bool>()}",
                    _ => c["action"]!.GetValue<string>(),
                }));
            Assert.All(commands, c => Assert.Equal("guest", c["channel"]!.GetValue<string>()));

            // A reopened check starts on its last step with nothing running.
            Click(chain, "MicSetup");
            await Until(() => chain.OwnedWindows.OfType<MicSetupWindow>().Any(w => w.IsVisible));
            window = chain.OwnedWindows.OfType<MicSetupWindow>().Single(w => w.IsVisible);
            WindowLayoutTests.Layout(window, 600, 560);
            Assert.Same(model, window.DataContext);
            Assert.True(model.CanRecord);
            await CheckPicker(main, chain, window, model, commands, meterWatches);
            gainCommands.Clear();
            WindowLayoutTests.Settle(window);
            Step(window, "MicSetupStepLevel");
            WindowLayoutTests.Settle(window);
            var unitGain = window.FindControl<NumericUpDown>("MicSetupUnitGainValue")!;
            Assert.True(unitGain.IsEffectivelyVisible);
            Assert.False(window.FindControl<NumericUpDown>("MicSetupCaptureGainValue")!.IsEffectivelyVisible);
            EnterGain(unitGain, "27");
            await Until(() => !gainCommands.IsEmpty);
            JsonNode unitWrite = Assert.Single(gainCommands);
            Assert.Equal("gain", unitWrite["control"]!.GetValue<string>());
            Assert.Equal(27, unitWrite["value"]!.GetValue<int>());
            Assert.Equal(27, main.GainDb);
            window.Close();
            await Until(() => !window.IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The picker on Position lists every microphone the mixer can test by
    /// its channel's name, the guest's chosen. Choosing XLR 1 stops the
    /// guest's session and moves the same window to XLR 1 on the same step,
    /// its meter running; the mixer header's button moves it back to XLR 1
    /// from another microphone rather than opening a second window.
    /// </summary>
    private static async Task CheckPicker(MainViewModel main, MixInsertsWindow chain, MicSetupWindow window, MicSetupViewModel guest,
        ConcurrentQueue<JsonNode> commands, ConcurrentQueue<JsonNode> meterWatches)
    {
        Step(window, "MicSetupStepPosition");
        ComboBox picker = window.FindControl<ComboBox>("MicSetupInput")!;
        await Until(() => picker.IsEffectivelyVisible && ReferenceEquals((picker.SelectedItem as MicSetupInput)?.Setup, guest));
        Assert.Equal(["XLR 1", "XLR 2", "Guest microphone with a long name", "Disconnected microphone"],
            picker.Items.Cast<MicSetupInput>().Select(i => i.Name));
        WindowLayoutTests.Settle(window);
        WindowLayoutTests.AssertInside(picker, window);

        Click(window, "MicSetupLiveMic");
        await Until(() => guest.Active);
        commands.Clear();
        meterWatches.Clear();
        MicSetupViewModel xlr1 = main.Inserts.MicSetup;
        picker.SelectedItem = picker.Items.Cast<MicSetupInput>().Single(i => i.Setup == xlr1);
        await Until(() => ReferenceEquals(window.DataContext, xlr1) && window.IsEnabled && !guest.Active);
        JsonNode stopped = Assert.Single(commands);
        Assert.Equal(("guest", "stop"), (stopped["channel"]!.GetValue<string>(), stopped["action"]!.GetValue<string>()));
        Assert.Equal(MicSetupStep.Position, xlr1.Step);
        Assert.Same(xlr1, (picker.SelectedItem as MicSetupInput)?.Setup);
        await Until(() => meterWatches.Any(c => c["channel"]?.GetValue<string>() == "xlr1"));
        Assert.Equal(Localizer.Format("MicSetupPositionXlr", "Elgato Wave XLR Pro", 1),
            window.FindControl<TextBlock>("MicSetupPositionIntro")!.Text);

        // Back to the guest from the picker, then the header's button: the one window moves to XLR 1.
        picker.SelectedItem = picker.Items.Cast<MicSetupInput>().Single(i => i.Setup == guest);
        await Until(() => ReferenceEquals(window.DataContext, guest) && window.IsEnabled);
        var mainWindow = (MainWindow)chain.Owner!;
        Button header = mainWindow.FindControl<Button>("MicSetupButton")!;
        Assert.True(header.IsEffectivelyVisible);
        Assert.Equal(Localizer.Text("MicSetup"), header.Content);
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ReferenceEquals(window.DataContext, xlr1) && window.IsEnabled);
        Assert.Single(chain.OwnedWindows.OfType<MicSetupWindow>().Concat(mainWindow.OwnedWindows.OfType<MicSetupWindow>()), w => w.IsVisible);
        Assert.Single(commands);
    }

    private static void EnterGain(NumericUpDown editor, string value)
    {
        editor.ApplyTemplate();
        var input = Assert.Single(editor.GetVisualDescendants().OfType<TextBox>());
        input.Focus();
        input.Text = value;
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
    }

    /// <summary>
    /// Shape while the take loops: the graph is the guest's own EQ, a preset
    /// goes out as one setEq and its undo as another with the EQ from before,
    /// a bypass reaches the guest's insert, the editor button opens that
    /// insert's controls under the check, and a capture input says it has no
    /// low cut or ClipGuard of its own.
    /// </summary>
    private static async Task CheckShape(MicSetupWindow window, MainViewModel main, MicSetupViewModel model,
        ConcurrentQueue<JsonNode> eqCommands, ConcurrentQueue<JsonNode> bypassCommands)
    {
        MicEqViewModel eq = main.CaptureInputs.Single(c => c.Id == "guest").Eq!;
        Step(window, "MicSetupStepShape");
        WindowLayoutTests.Layout(window, 600, 560);
        Assert.True(model.ShowsShape);
        Assert.False(window.FindControl<Control>("MicSetupListen")!.IsVisible);
        Assert.Same(eq, model.Eq);
        Assert.Same(eq, window.FindControl<EqGraph>("MicSetupEqGraph")!.DataContext);
        Assert.Equal(4, window.FindControl<EqGraph>("MicSetupEqGraph")!.Handles.Count);
        Assert.True(window.FindControl<TextBlock>("MicSetupNoFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicSetupUnitFilters")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Control>("MicSetupSoftwareFilters")!.IsEffectivelyVisible);

        eqCommands.Clear();
        MicEq before = eq.Current;
        Assert.False(model.CanUndoPreset);
        ToggleButton[] presets = [.. window.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Name == "MicSetupPreset")];
        Assert.Equal(MicSetupPresets.All.Select(p => p.Name), presets.Select(b => b.Content));
        Assert.All(presets, b => Assert.False(b.IsChecked));
        MicSetupPreset podcastPreset = MicSetupPresets.All.Single(p => p.Id == "podcast");
        ToggleButton podcast = Preset(window, "MicSetupPresetPodcast");
        podcast.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => eqCommands.Count == 1);
        JsonNode applied = eqCommands.Single();
        Assert.Equal("setEq", applied["cmd"]!.GetValue<string>());
        Assert.Equal("guest", applied["channel"]!.GetValue<string>());
        Assert.True(applied["eq"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(podcastPreset.Eq.Bands.Select(b => b.Frequency),
            applied["eq"]!["bands"]!.AsArray().Select(b => b!["frequency"]!.GetValue<double>()));
        Assert.Equal(podcastPreset.Eq, eq.Current);
        Assert.Equal(Localizer.Format("MicSetupPresetApplied", podcastPreset.Name),
            window.FindControl<TextBlock>("MicSetupPresetStatus")!.Text);
        // The chosen preset is lit as the step bar's chosen step is, with the
        // same toggle and the same checked colour, and it stays lit once the
        // daemon has reported the preset back.
        await Until(() => podcast.IsChecked == true);
        Assert.All(presets.Where(b => b != podcast), b => Assert.False(b.IsChecked));
        ToggleButton step = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, Localizer.Text("MicSetupStepShape")));
        Assert.True(step.IsChecked);
        Assert.Same(step.Theme, podcast.Theme);
        // The theme paints the checked state on the template's presenter, not on the control.
        static Avalonia.Controls.Presenters.ContentPresenter Face(ToggleButton b)
            => b.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        window.UpdateLayout();
        Assert.Equal(Face(step).Background, Face(podcast).Background);
        Assert.Equal(Face(step).Foreground, Face(podcast).Foreground);
        Assert.NotEqual(Face(podcast).Background, Face(presets[0]).Background);
        // Pressed again it sends nothing and stays lit.
        podcast.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(eqCommands);
        Assert.True(podcast.IsChecked);
        Assert.True(window.FindControl<Button>("MicSetupUndoPreset")!.IsEffectivelyEnabled);
        window.FindControl<Button>("MicSetupUndoPreset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => eqCommands.Count == 2);
        Assert.Equal(before, eq.Current);
        await Until(() => presets.All(b => b.IsChecked == false));
        Assert.Equal(before.Bands.Select(b => b.Type), eqCommands.Last()["eq"]!["bands"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()));
        Assert.False(model.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicSetupPresetUndone"), window.FindControl<TextBlock>("MicSetupPresetStatus")!.Text);
        eqCommands.Clear();

        ToggleButton bypass = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MicSetupBypass");
        Assert.Equal(Localizer.Format("MicSetupBypassName", "Guest compressor with a long label"),
            Avalonia.Automation.AutomationProperties.GetName(bypass));
        bypass.IsChecked = true;
        await Until(() => bypassCommands.Any());
        JsonNode sent = Assert.Single(bypassCommands);
        Assert.Equal(("guest", "compressor", true),
            (sent["channel"]!.GetValue<string>(), sent["insertId"]!.GetValue<string>(), sent["value"]!.GetValue<bool>()));
        await Until(() => model.Chain!.Items.Single().Bypass);
        bypass.IsChecked = false;
        await Until(() => bypassCommands.Count == 2 && !model.Chain!.Items.Single().Bypass);

        // No plugin host runs the insert here, so the editor is the generated controls, owned by the check.
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MicSetupInsertEditor")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.OwnedWindows.OfType<InsertControlsWindow>().Any(w => w.IsVisible));
        InsertControlsWindow controls = window.OwnedWindows.OfType<InsertControlsWindow>().Single(w => w.IsVisible);
        Assert.Same(model.Chain!.Items.Single(), controls.DataContext);
        controls.Close();
        await Until(() => !controls.IsVisible);

        // Remove takes the plugin out of the chain, as the chain window's does.
        Button remove = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MicSetupRemoveInsert");
        Assert.Equal(Localizer.Format("MicSetupRemoveName", "Guest compressor with a long label"),
            Avalonia.Automation.AutomationProperties.GetName(remove));
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !model.Chain!.HasItems);
        Assert.True(window.FindControl<TextBlock>("MicSetupNoEffects")!.IsEffectivelyVisible);
    }

    private static void Click(Window window, string key)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text(key)) && b is not ToggleButton)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static ToggleButton Preset(Window window, string key)
        => window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "MicSetupPreset" && Equals(b.Content, Localizer.Text(key)));

    private static void Step(Window window, string key)
        => window.GetVisualDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, Localizer.Text(key)))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(ready(), "The capture Mic setup window did not settle.");
    }

    /// <summary>
    /// Every step fits the window it opens in, in English and German, at
    /// standard and touch sizing: nothing scrolls, nothing reaches past the
    /// window's edges, and the window stays inside the screen. The steps
    /// carry everything they can show at once: the picker, the unit's gain
    /// with a gain suggestion, a session with a take and an error, the EQ,
    /// both kinds of filter and a plugin. A size the user chose then stays
    /// on a shorter step, and a window the user shrank grows again for the
    /// next step.
    /// </summary>
    internal static void CheckFits()
    {
        FieldInfo culture = typeof(Localizer).GetField("_culture", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = culture.GetValue(null);
        try
        {
            foreach (string language in new[] { "en", "de" })
            foreach (bool touch in new[] { false, true })
            {
                culture.SetValue(null, CultureInfo.GetCultureInfo(language));
                SkinService.ApplyControlSizing(touch);
                CheckFitsAt($"{language}-{(touch ? "touch" : "standard")}");
            }
        }
        finally
        {
            culture.SetValue(null, previous);
            SkinService.ApplyControlSizing(false);
        }
    }

    private static void CheckFitsAt(string suffix)
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        JsonNode state = CaptureInputWindowTests.State();
        state["mixer"]!["channels"]![0]!["eq"] = CaptureInputWindowTests.CaptureEq(enabled: true);
        state["mixer"]!["softLowCutApplicable"] = true;
        state["mixer"]!["softClipGuardApplicable"] = true;
        state["capabilities"]!["gainMaxDb"] = 80;
        state["state"] = new JsonObject { ["gainDb"] = 40, ["gainLocked"] = false };
        CaptureInputWindowTests.Apply(main, state);
        WindowLayoutTests.AddInsert(main.Inserts);
        MicSetupViewModel setup = main.Inserts.MicSetup;
        Assert.True(setup.ShowsPicker && setup.CarriesUnit && setup.HasEq && setup.HasUnitFilters && setup.HasSoftwareFilters);
        setup.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5,"error":"The session could not start because the capture port went away.","take":1,"monitorLatencyMs":42}"""));
        setup.ApplyTake(JsonNode.Parse("""{"type":"soundCheckTake","channel":"xlr1","take":1,"seconds":1.5,"peaks":[0.1,0.5,0.9,0.3]}"""));
        // A suggestion with every line of it showing: what was measured, a clip, and the change.
        var time = new ManualTime();
        setup.Time = time;
        setup.StartGainMeasure();
        for (int i = 0; i < 100 && setup.Measuring; i++)
        {
            time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));
            setup.ApplyInputLevel(new JsonArray(.5, 1, i == 0 ? 1 : 0));
        }
        Assert.True(setup.CanAcceptGain && setup.SuggestMeasured is not null && setup.SuggestAdvice is not null, suffix);
        setup.ChoosePreset(setup.Presets[1]);

        var window = new MicSetupWindow { DataContext = setup };
        try
        {
            window.Show();
            foreach (MicSetupStep step in Enum.GetValues<MicSetupStep>())
            {
                setup.Step = step;
                WindowLayoutTests.Settle(window, $"on {step}, {suffix}");
                AssertFits(window, $"{step}, {suffix}");
                WindowLayoutTests.Capture(window, $"mic-setup-fit-{step.ToString().ToLowerInvariant()}-{suffix}");
            }

            // The user makes it larger: a shorter step keeps that size.
            setup.Step = MicSetupStep.Shape;
            WindowLayoutTests.Settle(window);
            double width = window.ClientSize.Width + 60, height = window.ClientSize.Height + 80;
            WindowLayoutTests.Layout(window, width, height);
            setup.Step = MicSetupStep.Position;
            WindowLayoutTests.Settle(window);
            Assert.Equal((width, height), (window.ClientSize.Width, window.ClientSize.Height));
            // The user makes it smaller: the next step grows it to its content again, at the user's width.
            WindowLayoutTests.Layout(window, width, window.MinHeight);
            setup.Step = MicSetupStep.Shape;
            WindowLayoutTests.Settle(window);
            Assert.Equal(width, window.ClientSize.Width);
            AssertFits(window, $"Shape after a shrink, {suffix}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>The step shown needs no scrolling, and every control of it is inside the window and the window inside the screen.</summary>
    private static void AssertFits(MicSetupWindow window, string what)
    {
        ScrollViewer scroll = window.FindControl<ScrollViewer>("MicSetupScroll")!;
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5,
            $"{what}: the step is {scroll.Extent.Height:0.#} tall in a {scroll.Viewport.Height:0.#} view of a {window.ClientSize.Height:0.#} window.");
        Screen screen = window.Screens.ScreenFromWindow(window)!;
        Assert.True(window.ClientSize.Height <= screen.WorkingArea.Height / screen.Scaling + 0.5, $"{what}: {window.ClientSize} past the screen.");
        foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Name?.StartsWith("MicSetup", StringComparison.Ordinal) == true))
        {
            WindowLayoutTests.AssertInside(control, window);
            double bottom = control.TranslatePoint(new Point(0, control.Bounds.Height), window)!.Value.Y;
            Assert.True(bottom <= window.ClientSize.Height + 0.5,
                $"{what}: {WindowLayoutTests.Describe(control)} ends at y={bottom:0.#}, below the {window.ClientSize.Height:0.#} window.");
        }
    }

    // Use the existing layout test's real X11 application and dispatcher.
    internal static void CheckPendingClose()
    {
        Task check = VerifyCloseAndMove();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Mic setup close test hung.");
        check.GetAwaiter().GetResult();
    }

    private static async Task VerifyCloseAndMove()
    {
        await Verify(reopen: true);
        await Verify(reopen: false);
        await VerifyRefusedMove();
    }

    private static async Task VerifyRefusedMove()
    {
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "watchInputMeter") continue;
                await SocketTestServer.Send(socket, new { type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(), error = "The microphone could not be restored." }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var current = new MicSetupViewModel(client, "xlr1");
        var next = new MicSetupViewModel(client, "guest");
        var inputs = new MicSetupInputs();
        inputs.Update([new("Current", current), new("Guest", next)]);
        current.Inputs = next.Inputs = inputs;
        current.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":1}"""));
        var window = new MicSetupWindow { DataContext = current };
        try
        {
            window.Show();
            ComboBox picker = window.FindControl<ComboBox>("MicSetupInput")!;
            await Until(() => ReferenceEquals((picker.SelectedItem as MicSetupInput)?.Setup, current));
            picker.SelectedItem = inputs.Items[1];
            await Until(() => window.IsEnabled && current.Error is not null);
            Assert.Same(current, window.DataContext);
            Assert.Same(current, (picker.SelectedItem as MicSetupInput)?.Setup);
            Assert.Equal("The microphone could not be restored.", current.Error);
            Assert.True(picker.IsEffectivelyEnabled);
            Assert.True(window.FindControl<TextBlock>("MicSetupError")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    private static async Task Verify(bool reopen)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "watchInputMeter") continue;
                Assert.Equal("stop", command["action"]!.GetValue<string>());
                received.TrySetResult();
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var setup = new MicSetupViewModel(client, "xlr1") { Step = MicSetupStep.Level };
        var owner = new Window();
        owner.Show();
        MicSetupWindow window = MicSetupWindow.Open(owner, setup);
        MicSetupWindow? replacement = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            Click(window, "MicSetupSuggestGain");
            Assert.True(setup.Measuring);
            window.Close();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(window.IsVisible);
            Assert.False(window.IsEnabled);
            Assert.All(window.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEffectivelyEnabled));
            bool cancelledAtClose = !setup.Measuring;
            // Isolate a new measurement from the old one, even on the broken implementation.
            setup.CancelGainMeasure();
            if (reopen)
            {
                replacement = MicSetupWindow.Open(owner, setup);
                Assert.NotSame(window, replacement);
                Click(replacement, "MicSetupSuggestGain");
                Assert.True(setup.Measuring);
            }
            release.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(window.IsVisible);
            Assert.Equal(reopen, setup.Measuring);
            Assert.True(cancelledAtClose, "Closing should end this window's measurement before waiting for stop.");
        }
        finally { release.TrySetResult(); window.Close(); replacement?.Close(); owner.Close(); }
    }
}
