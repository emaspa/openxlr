using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Called inside the window layout fixture once its platform is up: text
/// aligns by its own script while the window keeps its left-to-right layout,
/// and a binding that formats with catalogue text gets the text.
/// </summary>
internal static class LocalizationRenderingTests
{
    internal static void Check()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var host = new Window { Width = 600, Height = 200, Content = text };
        try
        {
            host.Show();
            Assert.Equal(TextAlignment.DetectFromContent, text.TextAlignment);
            Assert.Equal(FlowDirection.LeftToRight, host.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, Direction(text, Localizer.Text("Language"), host));
            // A channel named in Arabic: the text runs right to left, the window does not.
            Assert.Equal(FlowDirection.RightToLeft, Direction(text, "الميكروفون", host));
            Assert.Equal(FlowDirection.LeftToRight, host.FlowDirection);
        }
        finally { host.Close(); }

        var inserts = new InsertsViewModel(new DaemonClient(), "xlr1", 1, "XLR 1");
        var chain = new MixInsertsWindow { DataContext = inserts };
        try
        {
            chain.Show();
            Assert.Equal(Localizer.Format("InsertsTitle", "XLR 1"), chain.Title);

            // Each effect whose editor the host can open has Plugin UI before
            // Controls; an effect without one shows no such button.
            inserts.PluginChoices.Add(new PluginChoice("urn:editor", "Editor", "", new JsonArray(),
                NativeEditorAvailable: true, NativeEditorSupported: true, Kind: "vst3"));
            inserts.PluginChoices.Add(new PluginChoice("urn:plain", "Plain", "", new JsonArray(), Kind: "lv2"));
            inserts.Apply(JsonNode.Parse("""
                [{"insert":{"id":"a","kind":"vst3","plugin":"urn:editor","bypass":false,"params":{}},"nativeHostRunning":true},
                 {"insert":{"id":"b","kind":"lv2","plugin":"urn:plain","bypass":false,"params":{}}}]
                """));
            chain.UpdateLayout();
            Button[] pluginUi = [.. chain.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "InsertPluginUi")];
            Assert.Equal(2, pluginUi.Length);
            Assert.True(pluginUi[0].IsEffectivelyVisible);
            Assert.True(pluginUi[0].IsEnabled);
            Assert.Equal(Localizer.Text("PluginUI"), pluginUi[0].Content);
            var row = (Panel)pluginUi[0].Parent!;
            Assert.Equal(Localizer.Text("Controls"), ((Button)row.Children[1]).Content);
            Assert.False(pluginUi[1].IsVisible);
        }
        finally { chain.Close(); }
    }

    // Exercise long captions in English CI too, without changing the labels
    // kept by the surrounding language captures.
    internal static void CheckPluginActions(OptionsWindow options)
    {
        static void Fits(Panel row)
        {
            foreach (Control action in row.Children) WindowLayoutTests.AssertInside(action, (Control)row.Parent!);
            WindowLayoutTests.AssertNoOverlap(row.Children.ToArray());
        }

        foreach (string name in new[] { "InstallFile", "ManageFolders" })
            Fits((Panel)options.FindControl<Button>(name)!.Parent!);
        Button[] buttons = [options.FindControl<Button>("InstallFile")!, options.FindControl<Button>("InstallFolder")!, options.FindControl<Button>("Rescan")!];
        object?[] captions = [.. buttons.Select(button => button.Content)];
        string[] longCaptions = ["Install a file containing a plugin", "Install a folder containing plugins", "Search again for installed plugins"];
        try
        {
            for (int i = 0; i < buttons.Length; i++) buttons[i].Content = longCaptions[i];
            options.UpdateLayout();
            Fits((Panel)buttons[0].Parent!);
        }
        finally
        {
            for (int i = 0; i < buttons.Length; i++) buttons[i].Content = captions[i];
            WindowLayoutTests.Layout(options, options.ClientSize.Width, options.ClientSize.Height);
        }
    }

    internal static void CheckLongMixerActions(MainWindow window, Border card)
    {
        Button[] buttons = [window.FindControl<Button>("PopOutMixerButton")!, window.FindControl<Button>("MicSetupButton")!];
        object?[] captions = [.. buttons.Select(button => button.Content)];
        try
        {
            buttons[0].Content = "Open the mixer in its own window";
            buttons[1].Content = "Set up the microphone";
            window.UpdateLayout();
            Panel row = (Panel)buttons[0].Parent!;
            Control[] shown = [.. row.Children.Where(c => c.IsEffectivelyVisible)];
            foreach (Control action in shown) WindowLayoutTests.AssertInside(action, card);
            WindowLayoutTests.AssertNoOverlap(shown);
        }
        finally
        {
            for (int i = 0; i < buttons.Length; i++) buttons[i].Content = captions[i];
            window.UpdateLayout();
        }
    }

    private static FlowDirection Direction(TextBlock text, string value, Window host)
    {
        text.Text = value;
        host.UpdateLayout();
        TextBounds bounds = Assert.Single(text.TextLayout.TextLines[0].GetTextBounds(0, value.Length));
        return bounds.FlowDirection;
    }
}
