using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

internal static class FaderInputWindowTests
{
    internal static void Check(MainWindow main)
    {
        FaderInputTests.StepsDoNotQuantizeIncomingValuesOrPluginControls();
        main.Activate();
        SkinEntry saved = SkinService.Current;
        try
        {
            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                WindowLayoutTests.Layout(main, 640, 900);
                var slider = main.FindControl<Slider>("OutputVolumeSlider")!;
                slider.BringIntoView();
                Pump();
                using var input = new XPointer();
                input.Focus(main.TryGetPlatformHandle()!.Handle);
                slider.Focus();
                slider.Value = .79;
                input.Key(0xff53); // Right
                Pump();
                Assert.Equal(.8, slider.Value, 10);
                input.Key(0xff55); // Page Up
                Pump();
                Assert.Equal(.9, slider.Value, 10);
                slider.Value = 1.49;
                input.Key(0xff53);
                input.Key(0xff53);
                Pump();
                Assert.Equal(1.5, slider.Value, 10);

                // Actual pointer travel through each control template.
                double fine = Drag(slider, input, fine: true);
                double normal = Drag(slider, input, fine: false);
                Assert.InRange(fine, .51, .59);
                Assert.True(normal - .5 > (fine - .5) * 5, $"Normal {normal}, fine {fine}.");
                Assert.Equal(Math.Round(fine * 100), fine * 100, 8);
                Drag(slider, input, fine: true, cancel: s =>
                {
                    s.IsEnabled = false;
                    Pump();
                    s.IsEnabled = true;
                });
                Drag(slider, input, fine: true, cancel: s =>
                {
                    var parent = (Panel)s.Parent!;
                    int index = parent.Children.IndexOf(s);
                    parent.Children.RemoveAt(index);
                    parent.Children.Insert(index, s);
                });
                Assert.InRange(Drag(slider, input, fine: true), .51, .59);
            }
        }
        finally { SkinService.Apply(saved); }
    }

    private static double Drag(Slider slider, XPointer input, bool fine, Action<Slider>? cancel = null)
    {
        slider.Value = .5;
        Pump();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        PixelPoint point = thumb.PointToScreen(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
        input.MoveTo(point.X, point.Y);
        if (fine) input.SetKey(0xffe1, true); // Shift
        try
        {
            Pump();
            input.SetButton(true);
            Pump();
            input.MoveTo(point.X + 60, point.Y);
            Pump();
            if (cancel is not null)
            {
                double stopped = slider.Value;
                cancel(slider);
                Pump();
                input.MoveTo(point.X + 90, point.Y);
                Pump();
                Assert.Equal(stopped, slider.Value);
            }
        }
        finally
        {
            input.SetButton(false);
            input.SetKey(0xffe1, false);
            Pump();
        }
        return slider.Value;
    }

    private static void Pump()
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(100));
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}
