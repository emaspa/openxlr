using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

internal static class MainWindowLabelTests
{
    /// <summary>
    /// A button's text: its string content, the screen reader's name of a
    /// button drawn with a glyph, such as undo and redo, or the first line of
    /// a panel such as Profiles with its active profile.
    /// </summary>
    private static string? Label(Button button)
        => button.Content as string ?? (AutomationProperties.GetName(button) is { Length: > 0 } name ? name : null)
           ?? (button.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

    internal static void Check(MainWindow main)
    {
        var failures = new List<string>();
        var header = main.FindControl<Grid>("WindowHeader")!;
        TextBlock title = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "OpenXLR");
        CheckText(title);
        CheckText(main.FindControl<TextBlock>("HeaderVersion")!);
        var actions = header.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Name != "DevicePicker").ToArray();
        Assert.Equal(new[] { "⚙", Localizer.Text("Undo"), Localizer.Text("Redo"), Localizer.Text("Profiles"), Localizer.Text("Arrange"), Localizer.Text("Flow"), Localizer.Text("RestartDaemon"), Localizer.Text("About") }, actions.Select(Label));
        foreach (Button action in actions)
            foreach (TextBlock label in action.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible))
                CheckText(label);
        foreach (string name in new[] { "InputControls", "Input2Controls" })
        {
            var row = main.FindControl<WrapPanel>(name)!;
            var toggles = row.Children.OfType<ToggleButton>().Where(b => b.IsVisible).ToArray();
            Assert.NotEmpty(toggles);
            Assert.Single(toggles, b => b.Content as string == Localizer.Text("Compressor"));
            CheckSeparate(toggles);
            foreach (ToggleButton button in toggles)
                foreach (TextBlock label in button.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible))
                    CheckText(label);
        }
        CheckSeparate([title, .. actions]);
        if (main.ClientSize.Width >= 1040)
        {
            var toolbar = main.FindControl<WrapPanel>("HeaderActions")!;
            Assert.InRange(toolbar.Bounds.Height, 0, toolbar.Children.Max(c => c.Bounds.Height) + 1);
        }
        var vm = (MainViewModel)main.DataContext!;
        int blend = vm.Crossfade;
        try
        {
            foreach (int value in new[] { 0, 100, 200 })
            {
                vm.Crossfade = value;
                Dispatcher.UIThread.RunJobs();
                main.UpdateLayout();
                var readout = main.FindControl<TextBlock>("CrossfadeValue")!;
                CheckText(readout, ((Grid)readout.Parent!).ColumnDefinitions[2].ActualWidth);
            }
        }
        finally { vm.Crossfade = blend; Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); }
        CheckFixedColumnLabels(main, Localizer.Text("XLR1Gain"), Localizer.Text("XLR2Gain"),
            Localizer.Text("USBAuxIn"), Localizer.Text("Phones1"), Localizer.Text("Phones2"),
            Localizer.Text("MicPC"), Localizer.Text("Devices"), Localizer.Text("Volume"));
        CheckFixedColumnTranslations(main, "XLR1Gain", "XLR2Gain", "USBAuxIn", "Phones1", "Phones2",
            "MicPC", "Devices", "Volume");
        Assert.Empty(failures);

        void CheckText(TextBlock text, double maximumWidth = double.PositiveInfinity)
        {
            double measured = TextWidth(text);
            double available = Math.Min(text.Bounds.Width, maximumWidth);
            if (measured > available + 1)
                failures.Add($"'{text.Text}' needs {measured:F1}px but has {available:F1}px at window width {main.ClientSize.Width}.");
            Point origin = text.TranslatePoint(default, main)!.Value;
            if (origin.X < 0 || origin.X + text.Bounds.Width > main.ClientSize.Width + 1)
                failures.Add($"'{text.Text}' leaves the window at width {main.ClientSize.Width}.");
        }

        void CheckSeparate(IReadOnlyList<Control> controls)
        {
            for (int i = 0; i < controls.Count; i++)
                for (int j = i + 1; j < controls.Count; j++)
                {
                    Rect a = new(controls[i].TranslatePoint(default, main)!.Value, controls[i].Bounds.Size);
                    Rect b = new(controls[j].TranslatePoint(default, main)!.Value, controls[j].Bounds.Size);
                    Rect intersection = a.Intersect(b);
                    if (intersection.Width > 0 && intersection.Height > 0)
                        failures.Add($"{controls[i].GetType().Name} overlaps {controls[j].GetType().Name}.");
                }
        }
    }

    internal static void CheckFixedColumnLabels(Window window, params string[] labels)
    {
        foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && labels.Contains(t.Text)))
        {
            var grid = Assert.IsType<Grid>(text.Parent);
            double available = Math.Min(text.Bounds.Width, grid.ColumnDefinitions[Grid.GetColumn(text)].ActualWidth);
            double measured = TextWidth(text);
            Assert.True(measured <= available + 1,
                $"'{text.Text}' needs {measured:F1}px but has {available:F1}px at window width {window.ClientSize.Width}.");
        }
    }

    /// <summary>
    /// The same fixed-width labels in every shipped language, measured in the
    /// font and column of the label the running language shows, so a
    /// translation that would clip fails whichever language the test runs in.
    /// </summary>
    internal static void CheckFixedColumnTranslations(Window window, params string[] keys)
    {
        var failures = new List<string>();
        foreach (string key in keys)
            foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text == Localizer.Text(key)))
            {
                var grid = Assert.IsType<Grid>(text.Parent);
                ColumnDefinition column = grid.ColumnDefinitions[Grid.GetColumn(text)];
                if (column.Width.IsAuto) continue;
                double available = column.ActualWidth - text.Margin.Left - text.Margin.Right;
                foreach (LanguageChoice language in Localizer.Languages)
                {
                    string translated = Localizer.Get(key, CultureInfo.GetCultureInfo(language.Id!));
                    double measured = TextWidth(text, translated);
                    if (measured > available + 1)
                        failures.Add($"{language.Id} '{translated}' needs {measured:F1}px but has {available:F1}px at window width {window.ClientSize.Width}.");
                }
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
    }

    /// <summary>Lay out automatic columns with the widest shipped translation in the active font.</summary>
    internal static void CheckAutoColumnTranslations(Window window, params string[] keys)
    {
        var originals = new List<(TextBlock Block, string Text)>();
        try
        {
            foreach (string key in keys)
            foreach (TextBlock block in window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible && block.Text == Localizer.Text(key)))
            {
                if (block.Parent is not Grid grid || !grid.ColumnDefinitions[Grid.GetColumn(block)].Width.IsAuto) continue;
                originals.Add((block, block.Text!));
                string widest = Localizer.Languages.Select(language => Localizer.Get(key, CultureInfo.GetCultureInfo(language.Id!)))
                    .MaxBy(text => TextWidth(block, text))!;
                block.SetCurrentValue(TextBlock.TextProperty, widest);
            }
            Assert.NotEmpty(originals);
            window.UpdateLayout();
            CheckFixedColumnLabels(window, [.. originals.Select(entry => entry.Block.Text!)]);
        }
        finally
        {
            foreach (var (block, text) in originals) block.SetCurrentValue(TextBlock.TextProperty, text);
            window.UpdateLayout();
        }
    }

    /// <summary>Each label shows its whole text in the width it was given.</summary>
    internal static void CheckWhole(IEnumerable<TextBlock> labels, string where)
    {
        var failures = new List<string>();
        foreach (TextBlock label in labels)
            if (TextWidth(label) > label.Bounds.Width + 1)
                failures.Add($"'{label.Text}' needs {TextWidth(label):F1}px but has {label.Bounds.Width:F1}px");
        Assert.True(failures.Count == 0, $"{where}: {string.Join("; ", failures)}");
    }

    private static double TextWidth(TextBlock text) => TextWidth(text, text.Text ?? "");

    private static double TextWidth(TextBlock text, string value)
        => new FormattedText(value, CultureInfo.InvariantCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground).Width;

    internal static void CheckLongDeviceLabels(MainWindow main)
    {
        var vm = (MainViewModel)main.DataContext!;
        bool connected = vm.DaemonConnected, device = vm.DeviceConnected, multiple = vm.HasMultipleDevices;
        string previousName = vm.DeviceName, activeName = vm.ActiveDeviceName;
        string longName = string.Concat(Enumerable.Repeat("Long interface name ", 20));
        try
        {
            Set(nameof(vm.DaemonConnected), true);
            Set(nameof(vm.DeviceConnected), true);
            Set(nameof(vm.DeviceName), longName);
            vm.HasMultipleDevices = false;
            Refresh();
            Check(main);
            var status = main.FindControl<TextBlock>("DeviceStatus")!;
            Assert.Equal(longName, status.Text);
            Assert.Equal(longName, ToolTip.GetTip(status));
            Assert.Contains(status.TextLayout.TextLines, line => line.HasCollapsed);

            vm.HasMultipleDevices = true;
            vm.ActiveDeviceName = longName;
            Refresh();
            Check(main);
            var picker = main.FindControl<DropDownButton>("DevicePicker")!;
            var label = picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longName);
            Assert.Contains(label.TextLayout.TextLines, line => line.HasCollapsed);
            Assert.Contains(longName, Assert.IsType<string>(ToolTip.GetTip(picker)));
            Point origin = picker.TranslatePoint(default, main)!.Value;
            Assert.InRange(origin.X + picker.Bounds.Width, 0, main.ClientSize.Width);
        }
        finally
        {
            Set(nameof(vm.DaemonConnected), connected);
            Set(nameof(vm.DeviceConnected), device);
            Set(nameof(vm.DeviceName), previousName);
            vm.HasMultipleDevices = multiple;
            vm.ActiveDeviceName = activeName;
            Refresh();
        }

        void Set(string property, object value) => typeof(MainViewModel).GetProperty(property)!.SetValue(vm, value);
        void Refresh() { Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); }
    }
}
