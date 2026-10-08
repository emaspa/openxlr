using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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
        }
        finally { chain.Close(); }
    }

    private static FlowDirection Direction(TextBlock text, string value, Window host)
    {
        text.Text = value;
        host.UpdateLayout();
        TextBounds bounds = Assert.Single(text.TextLayout.TextLines[0].GetTextBounds(0, value.Length));
        return bounds.FlowDirection;
    }
}
