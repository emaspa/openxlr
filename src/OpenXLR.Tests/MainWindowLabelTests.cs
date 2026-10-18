using System.Globalization;
using Avalonia;
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
    internal static void Check(MainWindow main)
    {
        var failures = new List<string>();
        var header = main.FindControl<Grid>("WindowHeader")!;
        TextBlock title = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "OpenXLR");
        CheckText(title);
        CheckText(main.FindControl<TextBlock>("HeaderVersion")!);
        var actions = header.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible && b.Name != "DevicePicker").ToArray();
        Assert.Equal(new[] { "⚙", Localizer.Text("Profiles"), Localizer.Text("Arrange"), Localizer.Text("Flow"), Localizer.Text("RestartDaemon"), Localizer.Text("About") }, actions.Select(b => b.Content as string));
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

    private static double TextWidth(TextBlock text)
        => new FormattedText(text.Text ?? "", CultureInfo.InvariantCulture, text.FlowDirection,
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
