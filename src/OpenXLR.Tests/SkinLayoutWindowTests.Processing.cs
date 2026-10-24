using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// A matrix row head's microphone switches on the Broadcast window: the
/// <c>processing</c> part is one button on the rows whose input has
/// switches, lit while one is on and marked 48V while phantom power is, and
/// its flyout holds exactly the switches the classic INPUTS panel shows
/// beside the mute for that input, with the same lettering and styling,
/// bound to the same properties, so a toggle there is the classic button's
/// write. An interface with phantom power only keeps the part for it and for
/// XLR 1's software low cut and ClipGuard, and an interface with none of
/// them leaves the part out.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static void CheckProcessing(MainWindow window, MainViewModel model, string skin)
    {
        MixMatrixViewModel matrix = window.MixMatrix!;
        Layout(window, 1040, 1400);

        // Only XLR 1, which carries the interface's microphone, has switches here.
        foreach (MatrixRowViewModel row in matrix.Rows)
            Assert.Equal(row.Channel.Id == "xlr1", ProcessingButton(window, row.Channel.Id) is not null);
        Button button = ProcessingButton(window, "xlr1")!;
        Assert.Equal(Localizer.Format("MatrixProcessingName", "XLR 1"), AutomationProperties.GetName(button));
        Assert.Equal(Localizer.Text("MatrixProcessingTip"), ToolTip.GetTip(button));
        Assert.DoesNotContain("fxOn", button.Classes);
        // Under it the gain, which reads the volume while the volume fader moves.
        MatrixRowViewModel xlr1 = matrix.Rows.Single(r => r.Channel.Id == "xlr1");
        TextBlock readout = RowHead(window, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixReadout");
        Assert.True(readout.IsEffectivelyVisible);
        Assert.Equal(model.GainText, readout.Text);
        Slider volume = RowHead(window, "xlr1").GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixVolume");
        FaderInput.SetIsAdjusting(volume, true);
        Assert.True(xlr1.VolumeAdjusting);
        Assert.Equal(xlr1.Channel.VolumeText, readout.Text);
        FaderInput.SetIsAdjusting(volume, false);
        Assert.Equal(model.GainText, readout.Text);
        // The interface has phantom power, so its lamp is there, dark.
        Assert.True(Phantom(button).IsEffectivelyVisible);
        Assert.False(Phantom(button).IsOn);

        // The flyout holds the classic set beside the mute, and no mute: the head has its own.
        ToggleButton[] switches = OpenSwitches(window, button);
        Assert.Equal(ClassicSwitches(window, "InputControls"), switches.Select(Lettering));
        Assert.Equal(Localizer.Language == "en"
            ? ["Low Cut", "Expander", "Voice Tune", "Phantom", "ClipGuard", "Compressor"]
            : new[] { Localizer.Text("LowCut"), Localizer.Text("Expander"), Localizer.Text("VoiceTune"), Localizer.Text("Phantom"), Localizer.Text("ClipGuard"), Localizer.Text("Compressor") }, switches.Select(Lettering));
        Assert.DoesNotContain(FlyoutPanel(button).GetVisualDescendants().OfType<ToggleButton>(), t => t.Classes.Contains("mute") && t.IsEffectivelyVisible);
        // Styled as the classic ones.
        ToggleButton classic = ClassicToggle(window, "InputControls", Localizer.Language == "en" ? "Low Cut" : Localizer.Text("LowCut"));
        foreach (ToggleButton toggle in switches)
        {
            Assert.Equal(classic.FontSize, toggle.FontSize);
            Assert.Equal(classic.Padding, toggle.Padding);
            Assert.Equal(classic.MinHeight, toggle.MinHeight);
            Assert.Same(classic.Theme, toggle.Theme);
            AssertInside(toggle, FlyoutPanel(button));
        }

        // A toggle there is the classic button's own write; the button lights while one is on.
        ToggleButton lowCut = switches.Single(t => Lettering(t) == (Localizer.Language == "en" ? "Low Cut" : Localizer.Text("LowCut")));
        lowCut.IsChecked = true;
        Assert.True(model.LowCut);
        Layout(window, 1040, 1400);
        Assert.True(classic.IsChecked);
        Assert.Contains("fxOn", button.Classes);
        Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Led.On")!).Color, ((ISolidColorBrush)button.Foreground!).Color);
        // Voice Tune brings its strength, bound to the same value.
        Grid strength = FlyoutPanel(button).GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MatrixVoiceTuneStrength");
        Assert.False(strength.IsEffectivelyVisible);
        switches.Single(t => Lettering(t) == (Localizer.Language == "en" ? "Voice Tune" : Localizer.Text("VoiceTune"))).IsChecked = true;
        Assert.True(model.VoiceTune);
        Layout(window, 1040, 1400);
        Assert.True(strength.IsEffectivelyVisible);
        strength.GetVisualDescendants().OfType<Slider>().Single().Value = 40;
        Assert.Equal(40, model.VoiceTuneStrength);
        AssertInside(strength, FlyoutPanel(button));

        // Phantom power: the closed button's lamp lights in the alert colour, and a screen reader hears it.
        switches.Single(t => Lettering(t) == (Localizer.Language == "en" ? "Phantom" : Localizer.Text("Phantom"))).IsChecked = true;
        Assert.True(model.Phantom);
        Capture(window, $"skin-{skin}-processing-open-1040", FlyoutPanel(button));
        button.Flyout!.Hide();
        Layout(window, 1040, 1400);
        Assert.True(Phantom(button).IsOn);
        Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Led.Alert")!).Color, ((ISolidColorBrush)Phantom(button).OnBrush!).Color);
        Assert.Equal(Localizer.Format("MatrixProcessingPhantomName", "XLR 1"), AutomationProperties.GetName(button));

        // Lit, with its lamp on, it fits its head at every size.
        foreach (bool enlarged in new[] { false, true })
        {
            SkinService.ApplyControlSizing(enlarged);
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                string where = $"{skin} processing, {(enlarged ? "touch" : "standard")} at {width}";
                CheckMatrixCells(window, matrix, where);
                Border head = RowHead(window, "xlr1");
                button = ProcessingButton(window, "xlr1")!;
                Assert.True(Phantom(button).IsOn, where);
                // The name keeps its room beside it, untrimmed.
                AssertWhole(head.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "XLR 1"), where);
                if (width == 1040)
                {
                    Capture(window, $"skin-{skin}-processing-{(enlarged ? "touch" : "standard")}-1040");
                    OpenSwitches(window, button);
                    foreach (ToggleButton toggle in FlyoutPanel(button).GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible))
                        AssertInside(toggle, FlyoutPanel(button));
                    Capture(window, $"skin-{skin}-processing-open-{(enlarged ? "touch" : "standard")}-1040", FlyoutPanel(button));
                    button.Flyout!.Hide();
                }
            }
        }
        SkinService.ApplyControlSizing(false);
        model.Phantom = false;
        model.VoiceTune = false;
        model.LowCut = false;
        Layout(window, 1040, 1400);
        button = ProcessingButton(window, "xlr1")!;
        Assert.DoesNotContain("fxOn", button.Classes);
        Assert.False(Phantom(button).IsOn);
        // Settling phantom power lights it too.
        model.PhantomSettling = true;
        Layout(window, 1040, 1400);
        Assert.True(Phantom(button).IsOn);
        model.PhantomSettling = false;

        // An interface with phantom power only and no hardware low cut or
        // ClipGuard, as the Wave XLR's first model: Phantom, and XLR 1's
        // software low cut and ClipGuard, as INPUTS shows them.
        bool[] caps = [model.CapLowCut, model.CapExpander, model.CapVoiceTune, model.CapClipGuard, model.CapCompressor];
        model.CapLowCut = model.CapExpander = model.CapVoiceTune = model.CapClipGuard = model.CapCompressor = false;
        SetSoftware(model, true);
        Layout(window, 1040, 1400);
        button = ProcessingButton(window, "xlr1")!;
        switches = OpenSwitches(window, button);
        Assert.Equal(ClassicSwitches(window, "InputControls").Concat(ClassicSwitches(window, "SoftwareControls", skipMute: false)),
            switches.Select(Lettering));
        Assert.Equal(Localizer.Language == "en"
            ? ["Phantom", model.SoftLowCutText, "ClipGuard"]
            : new[] { Localizer.Text("Phantom"), model.SoftLowCutText, Localizer.Text("ClipGuard") }, switches.Select(Lettering));
        Capture(window, $"skin-{skin}-processing-phantom-only-1040", FlyoutPanel(button));
        button.Flyout!.Hide();
        // Neither switches nor software processing: no part.
        model.CapPhantom = false;
        SetSoftware(model, false);
        Layout(window, 1040, 1400);
        Assert.Null(ProcessingButton(window, "xlr1"));
        CheckMatrixCells(window, matrix, $"{skin} without switches");
        model.CapPhantom = true;
        (model.CapLowCut, model.CapExpander, model.CapVoiceTune, model.CapClipGuard, model.CapCompressor) = (caps[0], caps[1], caps[2], caps[3], caps[4]);
        Layout(window, 1040, 1400);
        Assert.NotNull(ProcessingButton(window, "xlr1"));
    }

    /// <summary>
    /// The processing button stands at the end of the name column while the
    /// row shows it, inside the column and the head's height, clear of the
    /// name, the binding line and the binding target, and only then.
    /// </summary>
    internal static void CheckProcessingPlace(Border head, MatrixRowViewModel model, string where)
    {
        Button? button = head.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "MatrixProcessing" && b.IsEffectivelyVisible);
        Assert.True(model.ShowsProcessing == button is not null, $"{model.Channel.Id}: processing {model.ShowsProcessing}, drawn {button is not null} {where}");
        Border name = head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixRowName");
        // The readout under the button: inside the name column and the head, below the button.
        TextBlock? readout = head.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Name == "MatrixReadout" && t.IsEffectivelyVisible);
        Assert.True(model.ShowsReadout == readout is not null, $"{model.Channel.Id}: readout {model.ShowsReadout}, drawn {readout is not null} {where}");
        if (readout is not null)
        {
            Assert.Equal(model.Readout, readout.Text);
            AssertInside(readout, name);
            Point top = readout.TranslatePoint(default, head)!.Value;
            Assert.True(top.Y >= -0.5 && top.Y + readout.Bounds.Height <= head.Bounds.Height + 0.5,
                $"{model.Channel.Id}: the readout runs from {top.Y} to {top.Y + readout.Bounds.Height} in a head {head.Bounds.Height} tall {where}");
            if (button is not null)
                Assert.True(top.Y >= button.TranslatePoint(default, head)!.Value.Y + button.Bounds.Height - 0.5,
                    $"{model.Channel.Id}: the readout at {top.Y} is not under the processing button {where}");
        }
        if (button is null) return;
        AssertInside(button, name);
        Point at = button.TranslatePoint(default, head)!.Value;
        Assert.True(at.Y >= -0.5 && at.Y + button.Bounds.Height <= head.Bounds.Height + 0.5,
            $"{model.Channel.Id}: the processing button runs from {at.Y} to {at.Y + button.Bounds.Height} in a head {head.Bounds.Height} tall {where}");
        foreach (Control text in name.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is TextBlock or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0 && !ReferenceEquals(c, button)
                         && !c.GetVisualAncestors().Contains(button)))
            AssertNoOverlap([text, button]);
        foreach (Control inner in button.GetVisualDescendants().OfType<Control>().Where(c => c is TextBlock or StatusLed && c.IsEffectivelyVisible))
            AssertInside(inner, button);
    }

    /// <summary>XLR 2 on an interface with two inputs: its own set, the classic panel's second one, and its own writes.</summary>
    private static void CheckProcessingXlr2(MainWindow window, MainViewModel model)
    {
        Button button = ProcessingButton(window, "xlr2")!;
        Assert.NotNull(button);
        ToggleButton[] switches = OpenSwitches(window, button);
        Assert.Equal(ClassicSwitches(window, "Input2Controls"), switches.Select(Lettering));
        Assert.Equal(6, switches.Length);
        switches.Single(t => Lettering(t) == (Localizer.Language == "en" ? "Compressor" : Localizer.Text("Compressor"))).IsChecked = true;
        Assert.True(model.Compressor2);
        Assert.False(model.Compressor);
        switches.Single(t => Lettering(t) == (Localizer.Language == "en" ? "Compressor" : Localizer.Text("Compressor"))).IsChecked = false;
        Assert.False(model.Compressor2);
        button.Flyout!.Hide();
        Layout(window, 1040, 1400);
    }

    /// <summary>A label drawn at its whole width, not trimmed to an ellipsis.</summary>
    internal static void AssertWhole(TextBlock label, string where)
    {
        var whole = new TextBlock
        {
            Text = label.Text, FontSize = label.FontSize, FontWeight = label.FontWeight, FontFamily = label.FontFamily,
            FontStyle = label.FontStyle, FontStretch = label.FontStretch,
        };
        whole.Measure(Size.Infinity);
        Assert.True(label.Bounds.Width >= whole.DesiredSize.Width - 0.5,
            $"\"{label.Text}\" is {label.Bounds.Width:0.#} wide of the {whole.DesiredSize.Width:0.#} it needs {where}");
    }

    private static void SetSoftware(MainViewModel model, bool shown)
    {
        foreach (string name in new[] { nameof(MainViewModel.ShowSoftLowCut), nameof(MainViewModel.ShowSoftClipGuard) })
            typeof(MainViewModel).GetProperty(name)!.SetValue(model, shown);
    }

    /// <summary>A row head's processing button while it is drawn, or null.</summary>
    internal static Button? ProcessingButton(Visual root, string channel) =>
        RowHead(root, channel).GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "MatrixProcessing" && b.IsEffectivelyVisible);

    private static StatusLed Phantom(Button button) =>
        button.GetVisualDescendants().OfType<StatusLed>().Single(t => t.Name == "MatrixPhantom");

    /// <summary>The panel the processing button's flyout holds.</summary>
    internal static StackPanel FlyoutPanel(Button button) =>
        ((Control)((Flyout)button.Flyout!).Content!).GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "MatrixProcessingPanel");

    /// <summary>Open the processing flyout and return its shown switches, in order.</summary>
    internal static ToggleButton[] OpenSwitches(Window window, Button button)
    {
        button.Flyout!.ShowAt(button);
        Layout(window, window.ClientSize.Width, window.ClientSize.Height);
        Assert.True(button.Flyout.IsOpen);
        return [.. FlyoutPanel(button).GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible)];
    }

    /// <summary>The switches the classic INPUTS panel shows in one of its rows, by their lettering, the mute left out.</summary>
    internal static string[] ClassicSwitches(Window window, string row, bool skipMute = true) =>
        [.. window.FindControl<WrapPanel>(row)!.Children.OfType<ToggleButton>().Where(t => t.IsVisible && !(skipMute && t.Classes.Contains("mute")))
            .Select(Lettering)];

    private static ToggleButton ClassicToggle(Window window, string row, string lettering) =>
        window.FindControl<WrapPanel>(row)!.Children.OfType<ToggleButton>().Single(t => Lettering(t) == lettering);

    internal static string Lettering(ToggleButton toggle) => toggle.Content as string ?? "";
}
