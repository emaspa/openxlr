using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// Standard and Touch sizing on the real mixer window, under every built-in
/// skin. Runs on the UI thread <see cref="SkinWindowTests"/> owns, inside its
/// Xvfb run, and drives the faders with X server input so a target that only
/// looks large but does not take the pointer fails.
/// </summary>
internal static class TouchControlsWindowTests
{
    private const double Target = SkinTokens.TouchTarget;

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");

    internal static void Check(MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        string? savedText = File.Exists(UiJson) ? File.ReadAllText(UiJson) : null;
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        double width = main.Width, height = main.Height;
        options.Hide();
        flow.Hide();
        try
        {
            var picker = options.FindControl<ComboBox>("ControlSizingPicker")!;
            var vm = (OptionsViewModel)options.DataContext!;
            ControlSizingChoice standard = vm.ControlSizingChoices.Single(c => !c.Touch);
            ControlSizingChoice large = vm.ControlSizingChoices.Single(c => c.Touch);
            SkinService.ApplyControlSizing(false);
            TouchOverridesEarlierCompactClassStyles(main);
            // Every skin is measured; the pointer is driven once for each set
            // of control appearances, since those decide what a fader is.
            var drawn = new HashSet<string>(StringComparer.Ordinal);
            foreach (SkinEntry entry in SkinCatalog.BuiltIn())
                EachSkinTakesTouchAndComesBack(main, picker, standard, large, entry,
                    drive: drawn.Add(string.Join(";", entry.Package.Controls.OrderBy(c => c.Key, StringComparer.Ordinal))));
            SkinService.Apply(new(SkinPackage.Default, []));
            ASizingThatCannotBeSavedIsWornAndReported(main, vm, picker, standard, large);
            ALargerSkinStaysLarger(main);
        }
        finally
        {
            if (savedText is null) File.Delete(UiJson);
            else OpenXlrPaths.WriteAtomic(UiJson, savedText);
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Layout(main, width, height);
            options.Show();
            flow.Show();
        }
    }

    private static void EachSkinTakesTouchAndComesBack(MainWindow main, ComboBox picker,
        ControlSizingChoice standard, ControlSizingChoice large, SkinEntry entry, bool drive)
    {
        SkinService.Apply(entry);
        Layout(main, 1040, 1000);
        var slider = main.FindControl<Slider>("OutputVolumeSlider")!;
        Assert.Equal(30, slider.Bounds.Height);
        Assert.DoesNotContain("large-targets", main.Classes);
        if (drive) DragFader(main, slider, largeTarget: false);

        object background = Application.Current!.Resources["Ox.Window.Background"]!;
        double level = slider.Value;
        picker.SelectedItem = large;
        Pump(main);
        Assert.True(UiSettings.Load().TouchControls);
        Assert.Contains("large-targets", main.Classes);
        Assert.Equal(level, slider.Value);
        // Sizing writes the sizing resources and nothing else.
        Assert.Same(background, Application.Current.Resources["Ox.Window.Background"]);
        foreach (double size in new[] { 640d, 1800d })
        {
            Layout(main, size, 1000);
            CheckTargets(main, entry.Id);
        }
        if (drive)
        {
            DragFader(main, slider);
            CheckPopup(main);
            foreach (Slider send in main.GetVisualDescendants().OfType<Slider>()
                .Where(s => s.DataContext is SendViewModel or MixViewModel)
                .GroupBy(s => s.DataContext!.GetType()).Select(g => g.First()))
                CheckThumbCorners(main, send);
        }

        level = slider.Value;
        picker.SelectedItem = standard;
        Pump(main);
        Assert.Equal(30, slider.Bounds.Height);
        Assert.Equal(level, slider.Value);
        Assert.False(UiSettings.Load().TouchControls);
        Assert.DoesNotContain("large-targets", main.Classes);
    }

    /// <summary>
    /// A style that shrinks a button and comes before the sizing styles still
    /// loses to Touch, and is back once Standard is chosen again.
    /// </summary>
    private static void TouchOverridesEarlierCompactClassStyles(MainWindow main)
    {
        var button = main.FindControl<Button>("OptionsButton")!;
        var style = new Style(s => s.OfType<Button>().Class("small-target-probe"));
        style.Setters.Add(new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 24d));
        style.Setters.Add(new Setter(Avalonia.Layout.Layoutable.MinWidthProperty, 24d));
        main.Styles.Insert(0, style);
        button.Classes.Add("small-target-probe");
        try
        {
            Pump(main);
            Assert.Equal(24, button.MinHeight);
            SkinService.ApplyControlSizing(true);
            Pump(main);
            Assert.True(button.Bounds.Width >= Target && button.Bounds.Height >= Target,
                $"Touch must override earlier compact class styles: {button.Bounds.Size}.");
            SkinService.ApplyControlSizing(false);
            Pump(main);
            Assert.Equal(24, button.MinHeight);
            Assert.Equal(24, button.MinWidth);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            button.Classes.Remove("small-target-probe");
            main.Styles.Remove(style);
            Pump(main);
        }
    }

    private static void CheckTargets(MainWindow main, string skin)
    {
        var sliders = main.GetVisualDescendants().OfType<Slider>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width > 0).ToArray();
        Assert.NotEmpty(sliders);
        foreach (Slider slider in sliders)
        {
            Assert.True(slider.Bounds.Height >= Target, $"{skin}: slider height {slider.Bounds.Height}");
            Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
            Assert.True(thumb.Bounds.Width >= Target && thumb.Bounds.Height >= Target,
                $"{skin}: thumb target {thumb.Bounds.Size}");
            Assert.True(thumb.Bounds.Height <= slider.Bounds.Height, $"{skin}: the cap is taller than its slider");
        }
        // The window's own buttons; a scroll bar's arrows are the framework's.
        foreach (Button button in main.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not RepeatButton && b.IsEffectivelyVisible && b.Bounds.Width > 0
                        && !b.GetVisualAncestors().OfType<ScrollBar>().Any()))
            Assert.True(button.Bounds.Width >= Target && button.Bounds.Height >= Target,
                $"{skin}: {button.GetType().Name} {button.Content} is {button.Bounds.Size}");

        var inserts = main.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Classes.Contains("insertmini") && c.IsEffectivelyVisible).ToArray();
        Assert.NotEmpty(inserts);
        foreach (Control control in inserts)
        {
            Assert.True(control.Bounds.Width >= Target && control.Bounds.Height >= Target,
                $"{skin}: insert target {control.Bounds.Size}");
            var row = Assert.IsType<Grid>(control.Parent);
            Assert.True(control.Bounds.Right <= row.Bounds.Width + .1,
                $"{skin}: an insert action escaped its row: {control.Bounds}, row {row.Bounds.Size}");
        }

        foreach (Border strip in main.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("tile") && b.IsEffectivelyVisible))
        {
            if (strip.DataContext is ChannelViewModel) Assert.Equal(180, strip.Bounds.Width);
            else if (strip.DataContext is MixViewModel) Assert.Equal(280, strip.Bounds.Width);
        }
    }

    private static void CheckPopup(MainWindow main)
    {
        ComboBox combo = main.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.IsEffectivelyVisible && c.Items.Count > 1);
        combo.BringIntoView();
        Pump(main);
        combo.IsDropDownOpen = true;
        WaitForInput();
        try
        {
            Popup popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var rows = popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>().ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.True(row.Bounds.Height >= Target, $"popup row {row.Bounds.Height}"));
        }
        finally { combo.IsDropDownOpen = false; Pump(main); }
    }

    private static void CheckThumbCorners(MainWindow main, Slider slider)
    {
        slider.BringIntoView(new Rect(-4, -32, slider.Bounds.Width + 8, slider.Bounds.Height + 64));
        WaitForInput();
        main.UpdateLayout();
        WaitForInput();
        Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        Point[] corners = [new(3, 3), new(thumb.Bounds.Width - 3, 3),
            new(3, thumb.Bounds.Height - 3), new(thumb.Bounds.Width - 3, thumb.Bounds.Height - 3)];
        bool HitsThumb(Point corner)
        {
            Point edge = thumb.TranslatePoint(corner, main)!.Value;
            var hit = main.InputHitTest(edge) as Visual;
            return hit == thumb || hit?.GetVisualAncestors().Contains(thumb) == true;
        }
        // Hit testing follows the committed render tree, which can lag the
        // layout by a frame after scrolling.
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!corners.All(HitsThumb) && wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            WaitForInput();
            main.UpdateLayout();
        }
        Assert.True(corners.All(HitsThumb),
            $"All four corners of an enlarged cap must take input; skin={SkinService.Current.Id}, "
            + $"data={slider.DataContext?.GetType().Name}, thumb={thumb.Bounds}.");
    }

    /// <summary>
    /// The fader still answers the keyboard, and a press on its cap followed
    /// by a drag to the right raises it, with real X server input.
    /// </summary>
    private static void DragFader(MainWindow main, Slider slider, bool largeTarget = true)
    {
        main.Activate();
        slider.BringIntoView();
        WaitForInput();
        Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        if (largeTarget) CheckThumbCorners(main, slider);
        using var pointer = new XPointer();
        pointer.Focus(main.TryGetPlatformHandle()!.Handle);
        slider.Value = .4;
        WaitForInput();
        PixelPoint centre = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        pointer.MoveTo(centre.X, centre.Y);
        WaitForInput();
        pointer.Click();
        WaitForInput();
        slider.Focus();
        WaitForInput();
        Assert.True(slider.IsKeyboardFocusWithin, "The keyboard check needs the fader focused.");
        slider.Value = .4;
        WaitForInput();
        pointer.Key(0xff53); // Right
        WaitForInput();
        Assert.True(slider.Value > .4, $"The fader (touch={largeTarget}) no longer answers the keyboard: {slider.Value}.");

        slider.Value = .4;
        WaitForInput();
        PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        double before = slider.Value;
        pointer.MoveTo(point.X, point.Y);
        WaitForInput();
        pointer.SetButton(true);
        WaitForInput();
        pointer.MoveTo(point.X + 60, point.Y);
        WaitForInput();
        pointer.SetButton(false);
        WaitForInput();
        Assert.True(slider.Value > before, $"A drag did not raise the fader (touch={largeTarget}): {before} to {slider.Value}.");
    }

    private static void ASizingThatCannotBeSavedIsWornAndReported(MainWindow main, OptionsViewModel vm,
        ComboBox picker, ControlSizingChoice standard, ControlSizingChoice large)
    {
        picker.SelectedItem = standard;
        Pump(main);
        string original = File.ReadAllText(UiJson);
        try
        {
            // A file the window cannot read is left exactly as it is.
            OpenXlrPaths.WriteAtomic(UiJson, "{ \"skin\": ");
            picker.SelectedItem = large;
            Pump(main);
            Assert.True(SkinService.TouchControls);
            Assert.Contains("large-targets", main.Classes);
            Assert.Contains("ui.json", vm.PreferenceError);
            Assert.Equal("{ \"skin\": ", File.ReadAllText(UiJson));

            // A file that cannot be written: the choice is still worn.
            File.Delete(UiJson);
            Directory.CreateDirectory(UiJson);
            picker.SelectedItem = standard;
            Pump(main);
            Assert.False(SkinService.TouchControls);
            Assert.StartsWith("Could not save ui.json.", vm.PreferenceError);
        }
        finally
        {
            if (Directory.Exists(UiJson)) Directory.Delete(UiJson);
            OpenXlrPaths.WriteAtomic(UiJson, original);
        }

        picker.SelectedItem = large;
        Pump(main);
        Assert.Null(vm.PreferenceError);
        Assert.True(UiSettings.Load().TouchControls);
    }

    /// <summary>A skin that sizes larger than Touch keeps its sizes in both sizings.</summary>
    private static void ALargerSkinStaysLarger(MainWindow main)
    {
        var targetOnly = SkinPackage.Default with
        {
            Id = "touch-large-targets", Origin = SkinOrigin.User,
            Tokens = new Dictionary<string, SkinValue> { ["Ox.Mixer.ControlMinSize"] = new SkinNumber(64) },
        };
        foreach (bool touch in new[] { true, false })
        {
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(new(targetOnly, []));
            Layout(main, 1040, 1000);
            Assert.Contains("large-targets", main.Classes);
            var targetSlider = main.FindControl<Slider>("OutputVolumeSlider")!;
            var targetThumb = Assert.Single(targetSlider.GetVisualDescendants().OfType<Thumb>());
            Assert.Equal(64, targetThumb.Bounds.Height);
            Assert.True(targetSlider.Bounds.Height >= targetThumb.Bounds.Height);
            CheckThumbCorners(main, targetSlider);
        }

        SkinService.ApplyControlSizing(true);
        var custom = SkinPackage.Default with
        {
            Id = "touch-large-faders", Origin = SkinOrigin.User,
            Tokens = new Dictionary<string, SkinValue>
            {
                ["Ox.Mixer.ControlMinSize"] = new SkinNumber(60),
                ["Ox.Mixer.InsertControlMinSize"] = new SkinNumber(60),
                ["Ox.Mixer.ChannelWidth"] = new SkinNumber(320),
                ["Ox.Mixer.MixWidth"] = new SkinNumber(400),
                ["Ox.Fader.Thumb.Width"] = new SkinNumber(64),
                ["Ox.Fader.Thumb.Height"] = new SkinNumber(64),
            },
        };
        SkinService.Apply(new(custom, []));
        Layout(main, 1040, 1000);
        Slider slider = main.FindControl<Slider>("OutputVolumeSlider")!;
        Assert.True(slider.Bounds.Height >= 64);
        Thumb thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        Assert.Equal(64, thumb.Bounds.Width);
        Assert.Equal(64, thumb.Bounds.Height);
        Assert.Equal(320d, Application.Current!.Resources["Ox.Mixer.ChannelWidth"]);
        Assert.Equal(400d, Application.Current.Resources["Ox.Mixer.MixWidth"]);
        CheckThumbCorners(main, slider);
        SkinService.ApplyControlSizing(false);
        Assert.Equal(60d, Application.Current.Resources["Ox.Mixer.ControlMinSize"]);
        Assert.Equal(320d, Application.Current.Resources["Ox.Mixer.ChannelWidth"]);
    }

    private static void WaitForInput()
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(70));
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private static void Layout(Window window, double width, double height)
    {
        window.PlatformImpl!.GetType().GetMethod("Resize", [typeof(Size), typeof(WindowResizeReason)])!
            .Invoke(window.PlatformImpl, [new Size(width, height), WindowResizeReason.User]);
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(60));
        Dispatcher.UIThread.MainLoop(stop.Token);
        Pump(window);
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
