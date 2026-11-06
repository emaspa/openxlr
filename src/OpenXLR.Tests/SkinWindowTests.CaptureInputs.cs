using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;

namespace OpenXLR.Tests;

public sealed partial class SkinWindowTests
{
    private static void CaptureChainsUseTheRackAndMatrixParts()
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-capture-skins-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        WindowLayoutTests.StopConnecting(window);
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            foreach ((string id, string strip, string head) in new[]
            {
                ("capture-default", "{}", "{}"),
                ("capture-ordered", """{"partsOrder":["gain","identity","meter"]}""", """{"parts":["gain","identity","meter"]}"""),
                ("capture-effects", """{"partsOrder":["effects","binding","identity","gain","meter"]}""", """{"parts":["identity","effects","binding","gain","meter"]}"""),
            })
            {
                string folder = Path.Combine(data, "openxlr", "skins", id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "skin.json"), """
                    {"schema":1,"name":"NAME","tokens":{},"layout":{"version":1,"regions":{
                      "inputs":{"template":"input-rack","strip":STRIP},
                      "submixer":{"template":"mix-matrix","matrix":{"rowHead":HEAD}}}}}
                    """.Replace("NAME", id).Replace("STRIP", strip).Replace("HEAD", head));
            }
            var main = (MainViewModel)window.DataContext!;
            CaptureInputWindowTests.Apply(main, CaptureInputWindowTests.State());
            window.Show();
            foreach (string id in new[] { "capture-default", "capture-ordered", "capture-effects", "broadcast", "wave" })
            {
                bool shown = id != "capture-ordered";
                SkinEntry entry = Assert.Single(SkinCatalog.Discover(), e => e.Id == id);
                Assert.Empty(entry.Errors);
                Assert.Empty(SkinService.Apply(entry));
                Layout(window, 1040, 1200);
                if (id.StartsWith("capture-", StringComparison.Ordinal))
                {
                    InputStripViewModel model = window.InputRackModel!.Strips.Single(s => s.Channel.Id == "guest");
                    Border strip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "InputStrip" && b.DataContext == model);
                    Assert.Equal(shown, strip.GetVisualDescendants().OfType<CaptureSourceChip>().Single().IsEffectivelyVisible);
                    // A part the skin leaves out is never drawn, so its view may hold no button at all.
                    Assert.Equal(shown, strip.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "CaptureInserts" && b.IsEffectivelyVisible));
                    // The microphone's EQ comes first in the effects part, inside the strip.
                    Button? rackEq = strip.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "RackEq" && b.IsEffectivelyVisible);
                    Assert.Equal(shown, rackEq is not null);
                    if (rackEq is not null)
                    {
                        WindowLayoutTests.AssertInside(rackEq, strip);
                        Button inserts = strip.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CaptureInserts");
                        Assert.True(rackEq.TranslatePoint(default, strip)!.Value.Y < inserts.TranslatePoint(default, strip)!.Value.Y);
                    }
                    // The stereo pair's EQ comes first in its effects part too, ahead of its plugins.
                    InputStripViewModel camera = window.InputRackModel!.Strips.Single(s => s.Channel.Id == "camera");
                    Border cameraStrip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "InputStrip" && b.DataContext == camera);
                    Button? cameraEq = cameraStrip.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "RackEq" && b.IsEffectivelyVisible);
                    Assert.Equal(shown, cameraEq is not null);
                    if (cameraEq is not null)
                    {
                        WindowLayoutTests.AssertInside(cameraEq, cameraStrip);
                        Button inserts = cameraStrip.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CaptureInserts");
                        Assert.True(cameraEq.TranslatePoint(default, cameraStrip)!.Value.Y < inserts.TranslatePoint(default, cameraStrip)!.Value.Y);
                    }
                    // One bar: the spare keeps its room and draws nothing.
                    LevelMeter[] bars = [.. strip.GetVisualDescendants().OfType<LevelMeter>().Where(m => m.IsEffectivelyVisible)];
                    Assert.Single(bars, m => m.Opacity > 0);
                    Assert.Single(bars, m => m.Classes.Contains("monoSpare") && m.Opacity == 0);
                    Assert.Equal(!shown, strip.GetVisualDescendants().OfType<TextBlock>()
                        .Any(t => t.Text == model.Channel.CaptureLabel && t.IsEffectivelyVisible));
                    // XLR 1 keeps the pair of bars it has always drawn.
                    InputStripViewModel xlr = window.InputRackModel!.Strips.Single(s => s.Channel.Id == "xlr1");
                    Border xlrStrip = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "InputStrip" && b.DataContext == xlr);
                    Assert.Equal(2, xlrStrip.GetVisualDescendants().OfType<LevelMeter>().Count(m => m.IsEffectivelyVisible && m.Opacity > 0));
                    if (id == "capture-ordered") Assert.Equal(["gain", "identity", "meter"], model.Spec.Parts);
                    if (id == "capture-effects") Assert.Equal(["effects", "binding", "identity", "gain", "meter"], model.Spec.Parts);
                }
                foreach (string channel in new[] { "guest", "camera", "offline" })
                {
                    MatrixRowViewModel row = window.MixMatrix!.Rows.Single(r => r.Channel.Id == channel);
                    Border head = SkinLayoutWindowTests.RowHead(window, channel);
                    Assert.Equal(shown, row.ShowsBinding);
                    Assert.Equal(shown, row.Has(SkinStripParts.Effects));
                    Assert.Equal(shown, head.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MatrixCaptureBinding").IsEffectivelyVisible);
                    Assert.Equal(!shown, head.GetVisualDescendants().OfType<TextBlock>()
                        .Any(t => t.Text == row.Channel.CaptureLabel && t.IsEffectivelyVisible));
                    Button button = head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixCaptureInserts");
                    Assert.Equal(shown, button.IsEffectivelyVisible);
                    if (shown) WindowLayoutTests.AssertInside(button, head);
                    // Each capture input's EQ over its chain, the stereo pair's too.
                    Button eq = head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixEq");
                    Assert.Equal(shown, eq.IsEffectivelyVisible);
                    if (eq.IsEffectivelyVisible) WindowLayoutTests.AssertInside(eq, head);
                    MeterFader volume = head.GetVisualDescendants().OfType<MeterFader>().Single(f => f.IsEffectivelyVisible);
                    Assert.Equal(channel != "camera", volume.Mono);
                    Assert.Equal(channel == "camera" ? 2 : 1, volume.Meters.Children.Count(m => m.IsVisible));
                }
                if (shown)
                {
                    // The capture chain's FX and EQ buttons are XLR 1's.
                    Button xlrFx = SkinLayoutWindowTests.RowHead(window, "xlr1").GetVisualDescendants().OfType<Button>()
                        .Single(b => b.Classes.Contains("matrixEffects") && b.IsEffectivelyVisible);
                    Button captureFx = SkinLayoutWindowTests.RowHead(window, "guest").GetVisualDescendants().OfType<Button>()
                        .Single(b => b.Name == "MatrixCaptureInserts");
                    Assert.Equal(xlrFx.FontSize, captureFx.FontSize);
                    Assert.Equal(xlrFx.Padding, captureFx.Padding);
                    Assert.Equal(xlrFx.Bounds.Height, captureFx.Bounds.Height);
                    // This state gives XLR 1 no EQ, so the guest's EQ is held to its own FX button, which is XLR 1's.
                    Button captureEq = SkinLayoutWindowTests.RowHead(window, "guest").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixEq");
                    Assert.True(captureEq.IsEffectivelyVisible);
                    Assert.Equal(captureFx.FontSize, captureEq.FontSize);
                    Assert.Equal(captureFx.Padding, captureEq.Padding);
                    Assert.Equal(captureFx.Bounds.Height, captureEq.Bounds.Height);
                    Assert.Equal(captureFx.Bounds.Width, captureEq.Bounds.Width);
                    Assert.True(captureEq.TranslatePoint(default, captureFx)!.Value.Y < 0, "the EQ sits over the chain");
                }
                Capture(window, $"skin-{id}-capture-chain");
                if (id == "capture-default")
                {
                    SkinService.ApplyControlSizing(true);
                    Layout(window, 1040, 1200);
                    Capture(window, $"skin-{id}-capture-chain-touch");
                    SkinService.ApplyControlSizing(false);
                }
                if (id is "broadcast" or "wave")
                {
                    SkinService.ApplyControlSizing(true);
                    foreach (double width in new[] { 640d, 2400 })
                    {
                        Layout(window, width, 1200);
                        Border head = SkinLayoutWindowTests.RowHead(window, "guest");
                        WindowLayoutTests.AssertInside(head.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MatrixCaptureBinding"), head);
                        WindowLayoutTests.AssertInside(head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixCaptureInserts"), head);
                        WindowLayoutTests.AssertInside(head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixEq"), head);
                        Capture(window, $"skin-{id}-capture-chain-touch-{width}");
                    }
                    SkinService.ApplyControlSizing(false);
                }
            }
            // The capture chip wears XLR 1's chip face in the classic panel, on a flat
            // and a capped button, at both control sizes.
            foreach (string id in new[] { SkinPackage.DefaultId, "nord" })
            {
                Assert.Empty(SkinService.Apply(SkinCatalog.Find(id)!));
                foreach (bool touch in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(touch);
                    Layout(window, 1040, 1200);
                    Button xlr = window.GetVisualDescendants().OfType<BindingChip>().First(c => c.IsEffectivelyVisible).FindControl<Button>("Chip")!;
                    Button capture = window.GetVisualDescendants().OfType<CaptureSourceChip>().First(c => c.IsEffectivelyVisible).FindControl<Button>("Chip")!;
                    Assert.True(capture.Focusable);
                    Assert.True(capture.IsHitTestVisible);
                    Assert.Equal(xlr.Bounds.Height, capture.Bounds.Height);
                    Assert.Equal(xlr.Padding, capture.Padding);
                    Assert.Equal(xlr.CornerRadius, capture.CornerRadius);
                    Assert.Equal(xlr.BorderThickness, capture.BorderThickness);
                    Assert.Equal(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(xlr.Background).Color,
                        Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(capture.Background).Color);
                    Assert.Equal(ChipText(xlr).FontSize, ChipText(capture).FontSize);
                    Assert.Equal(id == "nord", HasBevel(xlr));
                    Assert.Equal(HasBevel(xlr), HasBevel(capture));
                    capture.BringIntoView();
                    Layout(window, 1040, 1200);
                    Capture(window, $"classic-{id}-capture-inputs-{(touch ? "touch" : "standard")}");
                }
                SkinService.ApplyControlSizing(false);
            }
            static TextBlock ChipText(Button chip) => chip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipText");
            static bool HasBevel(Button chip) => chip.GetVisualDescendants().Any(c => c.Name == "Bevel");
            // Older state on the same row models removes the effects, leaving the binding.
            var old = CaptureInputWindowTests.State();
            foreach (var channel in old["mixer"]!["channels"]!.AsArray()) channel!.AsObject().Remove("chainWidth");
            CaptureInputWindowTests.Apply(main, old);
            SkinService.Apply(SkinCatalog.Find("capture-default")!);
            Layout(window, 1040, 1200);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                b => b.Name is "MatrixCaptureInserts" or "CaptureInserts" && b.IsEffectivelyVisible);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
}
