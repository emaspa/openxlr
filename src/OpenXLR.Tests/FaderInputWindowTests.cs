using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
                var vm = (MainViewModel)main.DataContext!;
                Drag(slider, input, fine: true, cancel: _ => vm.OutputVolumeRange.Apply(false));
                Drag(slider, input, fine: true, cancel: _ => vm.OutputVolumeRange.Apply(true));
                Drag(slider, input, fine: true, cancel: s =>
                {
                    var parent = (Control)s.Parent!;
                    parent.IsVisible = false;
                    Pump();
                    parent.IsVisible = true;
                });
                Assert.InRange(Drag(slider, input, fine: true), .51, .59);
                CheckPointerOwnership(main, slider);
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
                cancel(slider);
                Pump();
                double stopped = slider.Value;
                input.MoveTo(point.X + 120, point.Y);
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

    private static void CheckPointerOwnership(Window window, Slider slider)
    {
        // Logical touch pointers check ownership separately from the XTEST mouse.
        using var first = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        using var second = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, false);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        Point origin = slider.TranslatePoint(new Point(slider.Bounds.Width / 2, slider.Bounds.Height / 2), window)!.Value;
        void Press(IPointer pointer) => slider.RaiseEvent(new PointerPressedEventArgs(
            slider, pointer, window, origin, 0, properties, KeyModifiers.Shift));
        void Move(double distance) => slider.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, slider, first, window, origin + new Vector(distance, 0),
            0, properties, KeyModifiers.Shift));

        slider.Value = .5;
        Press(first);
        Assert.Same(slider, first.Captured);
        Press(second);
        Assert.Null(second.Captured);
        slider.RaiseEvent(new PointerCaptureLostEventArgs(slider, second));
        Move(60);
        Assert.InRange(slider.Value, .51, .59);

        double stopped = slider.Value;
        var next = (InputElement)slider.Parent!;
        first.Capture(next);
        Assert.Same(next, first.Captured);
        Move(120);
        Assert.Equal(stopped, slider.Value);
        first.Capture(null);
        Press(first);
        Move(60);
        Assert.True(slider.Value > stopped);
    }
}
