using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static partial class MixerPopOutWindowTests
{
    internal static void CheckPixelSizes(MainWindow main, string sizes)
    {
        var dimensions = sizes.Split(',').Select(value =>
        {
            string[] parts = value.Split('x');
            return new PixelSize(int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        }).ToArray();

        var vm = (MainViewModel)main.DataContext!;
        JsonNode fixture = JsonNode.Parse(Mixer)!;
        fixture["mixes"]!.AsArray().Insert(1, JsonNode.Parse("""{"id":"monitor2","name":"Monitor B","kind":"monitor"}"""));
        foreach (string id in new[] { "system", "browser", "voicechat", "mic" })
            fixture["channels"]!.AsArray().Add(JsonNode.Parse($$"""
                {"id":"{{id}}","name":"{{id}}","levels":{"monitor":0.5,"monitor2":0.7,"stream":0.5,"chat":0.3} }
                """));
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [fixture]);

        main.Show();
        var panel = Assert.IsType<MixerPanel>(main.FindControl<ContentControl>("MixerHost")!.Content);
        foreach (string skin in new[] { "wave", "broadcast", "compact-desk" })
        {
            Layout(main, 1040, 900);
            Wear(skin);
            Layout(main, 1040, 900);
            MixerWindow popped = main.PopOutMixer();
            try
            {
                double scale = popped.RenderScaling;
                double requestedScale = double.Parse(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR")!,
                    CultureInfo.InvariantCulture);
                Assert.Equal(requestedScale, scale, 0.001);
                popped.FindControl<ComboBox>("MixerScalePicker")!.SelectedIndex = 0;
                var outer = popped.FindControl<ScrollViewer>("MixerScroll")!;
                MixMatrixView matrix = panel.MatrixItems;
                var lines = matrix.FindControl<StackPanel>("MatrixLines")!;

                foreach (bool touch in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(touch);
                    string mode = touch ? "touch" : "standard";
                    foreach (PixelSize size in dimensions)
                    {
                        string scenario = $"{skin}, {mode}, {scale:P0}, {size.Width}x{size.Height} px";
                        double width = Math.Max(popped.MinWidth, size.Width / scale);
                        double height = Math.Max(popped.MinHeight, size.Height / scale);
                        try { Layout(popped, width, height); }
                        catch (Exception ex) { throw new InvalidOperationException(scenario, ex); }
                        Assert.True(Math.Abs(popped.ClientSize.Width - width) * scale <= 1.01, scenario);
                        Assert.True(Math.Abs(popped.ClientSize.Height - height) * scale <= 1.01, scenario);
                        Assert.Equal(4, panel.Matrix!.Columns.Count);
                        Assert.Equal(MixerScale.Fit, popped.ScaleChoice);

                        double outerOverflow = (outer.Extent.Width - outer.Viewport.Width) * scale;
                        if (outer.Viewport.Width < panel.MinimumWidth() + 8)
                        {
                            Assert.Equal(1, popped.Scale);
                            Assert.True(outerOverflow > 0, $"{scenario}: a narrow window needs horizontal scrolling");
                        }
                        else
                        {
                            double gap = (matrix.Scroller.Viewport.Width - lines.Bounds.Width) * scale;
                            double innerOverflow = (matrix.Scroller.Extent.Width - matrix.Scroller.Viewport.Width) * scale;
                            Assert.True(innerOverflow <= 0.5, $"{scenario}: {innerOverflow:0.##} pixels of matrix overflow");
                            Assert.True(gap is >= -0.5 and <= 6, $"{scenario}: {gap:0.##} spare pixels inside the matrix");
                            Assert.True(outerOverflow <= 0.5, $"{scenario}: {outerOverflow:0.##} pixels of horizontal overflow");
                        }

                        Capture(popped, $"mixer-fit-{skin}-{mode}-{size.Width}x{size.Height}");
                    }
                }
            }
            finally
            {
                popped.Close();
                Settle(main);
            }
        }
    }
}
