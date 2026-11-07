using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class OutputControlsWindowTests
{
    internal static void Check()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(model, [JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
            window.Show();
            // Nyan Pink stops its faders at a maximum track width. The default
            // skin has none, so the same tracks then take the whole row; it
            // comes second so the limit is seen to go when the skin changes.
            foreach (string skin in new[] { "nyan-pink", SkinPackage.DefaultId })
            {
                SkinLayoutWindowTests.Wear(skin);
                double maximum = window.TryFindResource("Ox.Main.FaderMaxWidth", out object? limit) && limit is double cap
                    ? cap : double.PositiveInfinity;
                Assert.Equal(skin == SkinPackage.DefaultId, double.IsInfinity(maximum));
                foreach (double width in new[] { 640d, 1280, 1600, 2560 })
                {
                    model.HasHp2 = false;
                    Layout(window, width, 900);
                    var phones = window.FindControl<Grid>("HeadphoneControls")!;
                    Slider[] faders = [.. phones.Children.OfType<Slider>()];
                    void CheckSingleFader()
                    {
                        double expected = Math.Min(maximum, phones.Bounds.Width - faders[0].Bounds.X - 64);
                        Assert.InRange(faders[0].Bounds.Width, expected - 1, expected + 1);
                        var readout = phones.Children.OfType<TextBlock>().First();
                        Assert.InRange(readout.Bounds.Left - faders[0].Bounds.Right, -1, 64);
                    }
                    CheckSingleFader();
                    model.HasHp2 = true;
                    Layout(window, width, 900);
                    // The two star columns can split an odd number of display pixels.
                    double scale = window.RenderScaling;
                    int firstPixels = (int)Math.Round(faders[0].Bounds.Width * scale);
                    int secondPixels = (int)Math.Round(faders[1].Bounds.Width * scale);
                    Assert.InRange(Math.Abs(firstPixels - secondPixels), 0, 1);
                    AssertNoOverlap(faders);
                    Assert.All(faders, fader => Assert.True(fader.Bounds.Width <= maximum + 1));
                    model.HasHp2 = false;
                    Layout(window, width, 900);
                    CheckSingleFader();

                    const string label = "CORSAIR VIRTUOSO MAX WIRELESS Gaming Receiver + Monitor with a long saved name";
                    typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(model, [JsonNode.Parse("""[{"name":"headset","kind":0,"description":"LABEL"}]""".Replace("LABEL", label)),
                            JsonNode.Parse("""{"monitorOutputs":["headset"]}""")]);
                    Layout(window, width, 900);
                    var slider = window.FindControl<Slider>("OutputVolumeSlider")!;
                    var controls = window.FindControl<Grid>("MonitorControls")!;
                    Assert.True(slider.Bounds.Width >= Math.Min(maximum, Math.Max(80, controls.Bounds.Width * .25)) - 1,
                        $"A long output name squeezes the volume slider to {slider.Bounds.Width} at {width}.");
                    AssertInside(slider, controls);
                    Assert.True(slider.Bounds.Width <= maximum + 1);
                    var value = window.FindControl<TextBlock>("MonitorVolumeReadout")!;
                    Assert.InRange(value.Bounds.Left - slider.Bounds.Right, -1, 64);
                }
            }
        }
        finally
        {
            window.Quit();
            SkinLayoutWindowTests.Wear(SkinPackage.DefaultId);
        }
    }
}
