using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Line In beside Aux In on a real window, at every width the layout test
/// draws: in the classic INPUTS panel a row of its own with its gain and its
/// level lock, in the input rack a strip with its gain over its volume and
/// its lock under them, and in the mix matrix a row head with the gain and
/// the lock over the volume. A capture input with
/// a gain of its own is drawn the same way in the rack and the matrix. Each
/// strip's readout sits inside its strip or name column, reads the gain, and
/// reads the volume while that fader moves, back to the gain after the hold.
/// The capture input's own mute sits in the rack's switches part and the
/// matrix head's mute part, inside the strip at every width.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static void CheckLineIn()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            StopConnecting(window);
            model.CapAuxInput = true;
            model.CapLineInput = true;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"},
                          {"id":"auxout","name":"Aux","kind":"auxPort"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":1,"stream":1},"volume":1},
                             {"id":"aux","name":"Aux In","hardware":true,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.8},
                             {"id":"line","name":"Line In","hardware":true,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.6},
                             {"id":"brio","name":"Brio","captureSource":"alsa_input.usb-webcam","captureConnected":true,"microphone":false,"chainWidth":2,
                              "levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.7,"captureGain":{"gainDb":36,"minDb":18,"maxDb":54,"hardware":true,"muted":true}},
                             {"id":"music","name":"Music","levels":{"monitor":1,"stream":1},"volume":1}],
                 "inserts":{}}
                """)]);
            model.RefreshBindings();
            model.AuxLevelDb = -12;
            model.LineInGainDb = 20;
            var holds = new Dictionary<string, ManualHold>();
            foreach (string id in new[] { "aux", "line", "brio" })
                model.Channels.Single(c => c.Id == id).StartReadoutHold = (holds[id] = new ManualHold()).Start;
            window.Show();

            // The classic panel: a Line In row of its own under Aux In's, both readouts inside their rows.
            Wear(OpenXLR.UI.Skinning.SkinPackage.DefaultId);
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1200);
                foreach ((string row, string readout, string gain, string name) in new[]
                         {
                             ("AuxInRow", "AuxInReadout", "-12 dB", Localizer.Text("USBAuxIn")),
                             ("LineInRow", "LineInReadout", "20 dB", Localizer.Text("LineInGain")),
                         })
                {
                    Grid grid = window.FindControl<Grid>(row)!;
                    TextBlock text = window.FindControl<TextBlock>(readout)!;
                    Assert.True(grid.IsEffectivelyVisible, $"{row} at {width}");
                    Assert.Equal(gain, text.Text);
                    Assert.Equal(name, AutomationProperties.GetName(text));
                    AssertInside(text, grid);
                    foreach (Control inner in grid.Children.Where(c => c.IsEffectivelyVisible)) AssertInside(inner, grid);
                }
                AssertNoOverlap([window.FindControl<Grid>("AuxInRow")!, window.FindControl<Grid>("LineInRow")!]);
            }
            // Line In's tile volume moves: its row reads the volume, Aux In's keeps its level.
            ItemsControl tiles = window.FindControl<ItemsControl>("ChannelTiles")!;
            Slider tileVolume = tiles.GetVisualDescendants().OfType<Slider>()
                .First(s => s.DataContext is ChannelViewModel { Id: "line" } && s.Maximum == 1 && FaderInput.GetStep(s) > 0);
            FaderInput.SetIsAdjusting(tileVolume, true);
            Assert.Equal("60%", window.FindControl<TextBlock>("LineInReadout")!.Text);
            Assert.Equal(Localizer.Format("ChannelVolumeName", "Line In"), AutomationProperties.GetName(window.FindControl<TextBlock>("LineInReadout")!));
            Assert.Equal("-12 dB", window.FindControl<TextBlock>("AuxInReadout")!.Text);
            FaderInput.SetIsAdjusting(tileVolume, false);
            Assert.Equal("60%", window.FindControl<TextBlock>("LineInReadout")!.Text);
            holds["line"].Handles[^1].Elapse();
            Assert.Equal("20 dB", window.FindControl<TextBlock>("LineInReadout")!.Text);
            // A daemon that reports the strip absent takes the row away.
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"},
                          {"id":"auxout","name":"Aux","kind":"auxPort"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":1,"stream":1},"volume":1},
                             {"id":"aux","name":"Aux In","hardware":true,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.8},
                             {"id":"line","name":"Line In","hardware":true,"present":false,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.6},
                             {"id":"brio","name":"Brio","captureSource":"alsa_input.usb-webcam","captureConnected":true,"microphone":false,"chainWidth":2,
                              "levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.7,"captureGain":{"gainDb":36,"minDb":18,"maxDb":54,"hardware":true,"muted":true}},
                             {"id":"music","name":"Music","levels":{"monitor":1,"stream":1},"volume":1}],
                 "inserts":{}}
                """)]);
            model.RefreshBindings();
            Layout(window, 1040, 1200);
            Assert.False(window.FindControl<Grid>("LineInRow")!.IsEffectivelyVisible);
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"},
                          {"id":"auxout","name":"Aux","kind":"auxPort"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":1,"stream":1},"volume":1},
                             {"id":"aux","name":"Aux In","hardware":true,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.8},
                             {"id":"line","name":"Line In","hardware":true,"levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.6},
                             {"id":"brio","name":"Brio","captureSource":"alsa_input.usb-webcam","captureConnected":true,"microphone":false,"chainWidth":2,
                              "levels":{"monitor":1,"stream":1},"mutedIn":["monitor"],"volume":0.7,"captureGain":{"gainDb":36,"minDb":18,"maxDb":54,"hardware":true,"muted":true}},
                             {"id":"music","name":"Music","levels":{"monitor":1,"stream":1},"volume":1}],
                 "inserts":{}}
                """)]);
            model.RefreshBindings();

            // The input rack: Line In's strip has its gain over its volume and its own lock.
            Wear("banks");
            var rack = window.FindControl<InputRackView>("InputRack")!;
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1200);
                string where = $"in the rack at {width}";
                foreach ((string id, string gain) in new[] { ("aux", "-12 dB"), ("line", "20 dB"), ("brio", "36 dB") })
                {
                    Border strip = RackStrip(rack, id);
                    Assert.True(strip.IsEffectivelyVisible, $"{id} {where}");
                    AssertPartsApart(strip);
                    TextBlock readout = strip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackGainReadout");
                    Assert.True(readout.IsEffectivelyVisible, $"{id} {where}");
                    Assert.Equal(gain, readout.Text);
                    AssertInside(readout, Part(strip, OpenXLR.UI.Skinning.SkinStripParts.Gain));
                    Slider[] faders = [.. Part(strip, OpenXLR.UI.Skinning.SkinStripParts.Gain).GetVisualDescendants().OfType<Slider>()
                        .Where(s => s.IsEffectivelyVisible).OrderBy(s => s.TranslatePoint(default, strip)!.Value.Y)];
                    Assert.Equal(2, faders.Length);
                    Assert.Equal(id switch { "aux" => Localizer.Text("USBAuxIn"), "line" => Localizer.Text("LineInGain"), _ => Localizer.Format("CaptureGainName", "Brio") },
                        AutomationProperties.GetName(faders[0]));
                    Assert.Equal(Localizer.Format("ChannelVolumeName", id switch { "aux" => "Aux In", "line" => "Line In", _ => "Brio" }),
                        AutomationProperties.GetName(faders[1]));
                }
                foreach (string id in new[] { "aux", "line" })
                {
                    ToggleButton lockButton = Assert.Single(RackStrip(rack, id).GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible);
                    AssertInside(lockButton, Part(RackStrip(rack, id), OpenXLR.UI.Skinning.SkinStripParts.HardwareControls));
                }
                ToggleButton rackMute = RackStrip(rack, "brio").GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RackCaptureMute");
                Assert.True(rackMute.IsEffectivelyVisible, $"brio mute {where}");
                Assert.True(rackMute.IsChecked);
                AssertInside(rackMute, Part(RackStrip(rack, "brio"), OpenXLR.UI.Skinning.SkinStripParts.HardwareControls));
            }
            Slider rackVolume = RackStrip(rack, "line").GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RackVolume")
                .GetVisualDescendants().OfType<Slider>().Single();
            TextBlock rackReadout = RackStrip(rack, "line").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackGainReadout");
            FaderInput.SetIsAdjusting(rackVolume, true);
            Assert.Equal("60%", rackReadout.Text);
            rackVolume.Value = 0.35;
            Assert.Equal("35%", rackReadout.Text);
            FaderInput.SetIsAdjusting(rackVolume, false);
            holds["line"].Handles[^1].Elapse();
            Assert.Equal("20 dB", rackReadout.Text);
            // The capture input's readout does the same with its own gain.
            Slider brioVolume = RackStrip(rack, "brio").GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RackVolume")
                .GetVisualDescendants().OfType<Slider>().Single();
            TextBlock brioReadout = RackStrip(rack, "brio").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackGainReadout");
            FaderInput.SetIsAdjusting(brioVolume, true);
            Assert.Equal("70%", brioReadout.Text);
            FaderInput.SetIsAdjusting(brioVolume, false);
            holds["brio"].Handles[^1].Elapse();
            Assert.Equal("36 dB", brioReadout.Text);

            // The mix matrix: both heads stack the gain over the volume, the readout in the name column.
            Wear("broadcast");
            MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                string where = $"in the matrix at {width}";
                foreach (string id in new[] { "aux", "line", "brio" })
                {
                    MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == id);
                    Border head = RowHead(window, id);
                    Assert.True(row.Stacked, $"{id} {where}");
                    Assert.True(row.ShowsReadout, $"{id} {where}");
                    AssertPartsApart(head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixSource"));
                    CheckLevels(head, row, where);
                    CheckProcessingPlace(head, row, where);
                }
                ToggleButton matrixMute = RowHead(window, "brio").GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MatrixCaptureMute");
                Assert.True(matrixMute.IsEffectivelyVisible, $"brio mute {where}");
                Assert.True(matrixMute.IsChecked);
                AssertInside(matrixMute, RowHead(window, "brio"));
                // Line In's lock sits at the end of its gain's line, inside the head.
                ToggleButton lineLock = RowHead(window, "line").GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MatrixLineInLock");
                Assert.True(lineLock.IsEffectivelyVisible, where);
                AssertInside(lineLock, RowHead(window, "line"));
            }
            Border lineHead = RowHead(window, "line");
            Slider matrixVolume = lineHead.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixVolume");
            TextBlock matrixReadout = lineHead.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixReadout");
            Assert.Equal("20 dB", matrixReadout.Text);
            FaderInput.SetIsAdjusting(matrixVolume, true);
            Assert.Equal(model.Channels.Single(c => c.Id == "line").VolumeText, matrixReadout.Text);
            FaderInput.SetIsAdjusting(matrixVolume, false);
            holds["line"].Handles[^1].Elapse();
            Assert.Equal("20 dB", matrixReadout.Text);
        }
        finally
        {
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }
}
