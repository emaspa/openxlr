using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using OpenXLR.UI;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class OutputControlsWindowTests
{
    internal static void Check()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var window = new MainWindow(client);
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(model, [JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
            window.Show();
            var mix = new MixViewModel(client, "monitor", "Monitor A");
            mix.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","monitorEq":{"preset":"fps"}}""")!);
            var editor = new MixInsertsWindow { DataContext = mix.Inserts };
            try
            {
                editor.Show(window);
                Layout(editor, 440, 270);
                var picker = editor.FindControl<ComboBox>("MonitorEqPreset")!;
                Assert.True(picker.IsEffectivelyVisible);
                Assert.Equal("fps", Assert.IsType<MonitorEqOption>(picker.SelectedItem).Id);
                AssertInside(picker, editor);
                mix.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor"}""")!);
                Layout(editor, 440, 270);
                Assert.False(picker.IsEffectivelyVisible);
            }
            finally { editor.Close(); }
            foreach (double width in new[] { 640d, 1280, 1600 })
            {
                model.HasHp2 = false;
                Layout(window, width, 900);
                var phones = window.FindControl<Grid>("HeadphoneControls")!;
                Slider[] faders = [.. phones.Children.OfType<Slider>()];
                Assert.True(faders[0].Bounds.Width > phones.Bounds.Width * .6,
                    $"A single headphone output leaves its second jack's columns reserved at {width}.");
                model.HasHp2 = true;
                Layout(window, width, 900);
                // The two star columns can split an odd number of display pixels.
                double scale = window.RenderScaling;
                int firstPixels = (int)Math.Round(faders[0].Bounds.Width * scale);
                int secondPixels = (int)Math.Round(faders[1].Bounds.Width * scale);
                Assert.InRange(Math.Abs(firstPixels - secondPixels), 0, 1);
                AssertNoOverlap(faders);
                model.HasHp2 = false;
                Layout(window, width, 900);
                Assert.True(faders[0].Bounds.Width > phones.Bounds.Width * .6);

                const string label = "CORSAIR VIRTUOSO MAX WIRELESS Gaming Receiver + Monitor with a long saved name";
                typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(model, [JsonNode.Parse("""[{"name":"headset","kind":0,"description":"LABEL"}]""".Replace("LABEL", label)),
                        JsonNode.Parse("""{"monitorOutputs":["headset"]}""")]);
                Layout(window, width, 900);
                var slider = window.FindControl<Slider>("OutputVolumeSlider")!;
                var controls = window.FindControl<Grid>("MonitorControls")!;
                Assert.True(slider.Bounds.Width >= Math.Max(80, controls.Bounds.Width * .25),
                    $"A long output name squeezes the volume slider to {slider.Bounds.Width} at {width}.");
                AssertInside(slider, controls);
            }
        }
        finally { window.Quit(); }
    }
}
