using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The HEADPHONES card of a Wave XLR Pro in every built-in skin, its own
/// arrangement included: Line Out's level sits under Phones 2 in the same
/// columns, its label, fader and readout lined up with the row above, and
/// Low Imp. sits under Phones 1 on the same row, all inside the card. A
/// unit without Line Out keeps Low Imp. in the row of buttons below. Run
/// from the skin tests, on their UI thread, with preferences of its own.
/// </summary>
internal static class HeadphoneAlignmentWindowTests
{
    internal static void CheckSkins() => WithWindow((window, model) =>
    {
        int checkedSkins = 0;
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            Assert.Empty(SkinService.Apply(entry));
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                foreach (double width in new[] { 640d, 1040, 1800 })
                {
                    Layout(window, width, 900);
                    var tile = window.FindControl<Expander>("HeadphonesTile");
                    if (tile is null || !((Visual)tile.Parent!).IsEffectivelyVisible) continue;
                    tile.IsExpanded = true;
                    Layout(window, width, 900);
                    string where = $"{entry.Id}, {(touch ? "touch" : "standard")} controls, at {width}";
                    AssertLineOutUnderPhones2(window, (Border)tile.Parent!, where);
                    if (width == 1040) Capture(window, $"headphones-{entry.Id}-{(touch ? "touch" : "standard")}-1040");
                    if (!touch && width == 1040) checkedSkins++;
                }
            }
            SkinService.ApplyControlSizing(false);
        }
        // Most skins show the card; one arrangement hiding it must not hide them all.
        Assert.True(checkedSkins >= SkinCatalog.BuiltIn().Count() / 2, $"the HEADPHONES card was checked in {checkedSkins} skins");

        // A unit without Line Out: no second row, Low Imp. in the buttons below.
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        model.CapOutputRouting = false;
        Layout(window, 1040, 900);
        Assert.False(window.FindControl<Slider>("LineOutSlider")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<ToggleButton>("LowImpedanceBesideLineOut")!.IsEffectivelyVisible);
        Assert.True(window.FindControl<ToggleButton>("LowImpedanceButton")!.IsEffectivelyVisible);
        // One jack: Phones 2's columns fold, and Line Out's row with them.
        model.CapOutputRouting = true;
        model.HasHp2 = false;
        Layout(window, 1040, 900);
        Assert.False(window.FindControl<Slider>("LineOutSlider")!.IsEffectivelyVisible);
        Assert.True(window.FindControl<ToggleButton>("LowImpedanceButton")!.IsEffectivelyVisible);
    });

    private static void AssertLineOutUnderPhones2(Window window, Border card, string where)
    {
        var grid = window.FindControl<Grid>("HeadphoneControls")!;
        Rect In(Control c) => new(c.TranslatePoint(default, grid)!.Value, c.Bounds.Size);
        var phones1 = window.FindControl<Grid>("Phones1Label")!;
        var phones2 = (Control)window.FindControl<Grid>("Phones2Label")!.Parent!;
        var phones2Slider = window.FindControl<Slider>("Phones2Slider")!;
        var phones2Value = window.FindControl<Control>("Phones2Value")!;
        var label = window.FindControl<TextBlock>("LineOutLabel")!;
        var slider = window.FindControl<Slider>("LineOutSlider")!;
        var value = window.FindControl<Control>("LineOutValue")!;
        var lowImp = window.FindControl<ToggleButton>("LowImpedanceBesideLineOut")!;

        foreach (Control c in new Control[] { label, slider, value, lowImp })
            Assert.True(c.IsEffectivelyVisible, $"{c.Name} is hidden in {where}");
        Assert.False(window.FindControl<ToggleButton>("LowImpedanceButton")!.IsEffectivelyVisible, $"Low Imp. shows twice in {where}");

        // Line Out's fader spans exactly Phones 2's, and its readout ends where Phones 2's does.
        Assert.Equal(In(phones2Slider).X, In(slider).X, 0.5);
        Assert.Equal(In(phones2Slider).Width, In(slider).Width, 0.5);
        Assert.Equal(In(phones2Value).Right, In(value).Right, 0.5);
        Assert.Equal(In(phones2).X, In(label).X, 0.5);
        // Low Imp. starts under Phones 1, on Line Out's row, below the first row.
        Assert.Equal(In(phones1).X, In(lowImp).X, 0.5);
        Assert.True(In(slider).Y >= In(phones2Slider).Bottom - 0.5, $"Line Out overlaps Phones 2 in {where}");
        Assert.True(In(lowImp).Bottom > In(slider).Y && In(lowImp).Y < In(slider).Bottom, $"Low Imp. is not on Line Out's row in {where}");
        Assert.True(In(lowImp).Right <= In(label).X, $"Low Imp. runs into Line Out's label in {where}");
        // Readable and inside the card.
        Assert.False(IsCutShort(label), $"\"Line Out\" is cut short in {where}");
        foreach (Control c in new Control[] { label, slider, value, lowImp })
            AssertInside(c, card);
    }

    private static bool IsCutShort(TextBlock text)
    {
        var whole = new TextBlock
        {
            Text = text.Text, FontSize = text.FontSize, FontWeight = text.FontWeight, FontFamily = text.FontFamily,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch,
        };
        whole.Measure(Size.Infinity);
        return text.Bounds.Width < whole.DesiredSize.Width - 0.5;
    }

    private static void WithWindow(Action<MainWindow, MainViewModel> check)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-headphone-alignment-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            model.HasHp2 = true;
            model.CapOutputRouting = true;
            model.CapLowImpedance = true;
            window.Show();
            Layout(window, 1040, 900);
            check(window, model);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }
}
