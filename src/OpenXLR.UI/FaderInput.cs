using System;
using System.Runtime.CompilerServices;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Automation;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>Opt-in fine dragging for mixer faders. Plugin parameters keep their own steps.</summary>
public sealed class FaderInput : AvaloniaObject
{
    public static readonly AttachedProperty<double> StepProperty =
        AvaloniaProperty.RegisterAttached<FaderInput, Slider, double>("Step", 0,
            validate: value => double.IsFinite(value) && value >= 0);

    private static readonly ConditionalWeakTable<Slider, Gesture> Gestures = new();

    static FaderInput() => StepProperty.Changed.AddClassHandler<Slider>((slider, change) =>
    {
        double step = change.GetNewValue<double>();
        if (step > 0)
        {
            slider.SetCurrentValue(RangeBase.SmallChangeProperty, step);
            slider.SetCurrentValue(RangeBase.LargeChangeProperty, step * 10);
            slider.SetCurrentValue(Slider.TickFrequencyProperty, step);
            slider.SetCurrentValue(Slider.IsSnapToTickEnabledProperty, true);
            AutomationProperties.SetHelpText(slider, Localizer.Text("FaderPrecisionHint"));
            if (ToolTip.GetTip(slider) is null)
                ToolTip.SetTip(slider, Localizer.Text("FaderPrecisionHint"));
            Gestures.GetValue(slider, s => new Gesture(s));
        }
    });

    public static double GetStep(Slider slider) => slider.GetValue(StepProperty);
    public static void SetStep(Slider slider, double value) => slider.SetValue(StepProperty, value);

    /// <summary>One tenth of normal travel, snapped to the fader's displayed units.</summary>
    internal static double FineValue(double value, double distance, double span, double min, double max, double step)
    {
        if (span <= 0 || max <= min || step <= 0) return value;
        double next = Math.Clamp(value + distance / span * (max - min) / 10, min, max);
        return Math.Clamp(min + Math.Round((next - min) / step, MidpointRounding.AwayFromZero) * step, min, max);
    }

    private sealed class Gesture
    {
        private readonly Slider _slider;
        private IPointer? _pointer;
        private Visual[] _ancestors = [];
        private Point _origin;
        private double _value, _span;

        internal Gesture(Slider slider)
        {
            _slider = slider;
            slider.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
            slider.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
            slider.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
            slider.PointerCaptureLost += (_, e) =>
            {
                if (e.Pointer == _pointer) End(release: false);
            };
            slider.DetachedFromVisualTree += (_, _) => End();
            slider.PropertyChanged += (_, e) =>
            {
                // A new device or desktop boost setting changes the drag's
                // scale. Do not continue from its old value and geometry.
                if (e.Property == RangeBase.MinimumProperty || e.Property == RangeBase.MaximumProperty
                    || e.Property == Visual.IsVisibleProperty && !slider.IsVisible
                    || e.Property == InputElement.IsEffectivelyEnabledProperty && !slider.IsEffectivelyEnabled) End();
            };
        }

        private void Pressed(object? sender, PointerPressedEventArgs e)
        {
            if (_pointer is not null)
            {
                e.PreventGestureRecognition();
                e.Handled = true;
                return;
            }
            if (GetStep(_slider) <= 0 || (e.KeyModifiers & KeyModifiers.Shift) == 0
                || !e.GetCurrentPoint(_slider).Properties.IsLeftButtonPressed) return;
            var track = _slider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
            bool horizontal = _slider.Orientation == Orientation.Horizontal;
            _span = horizontal ? (track?.Bounds.Width ?? _slider.Bounds.Width) - (track?.Thumb?.Bounds.Width ?? 0)
                : (track?.Bounds.Height ?? _slider.Bounds.Height) - (track?.Thumb?.Bounds.Height ?? 0);
            if (_span <= 0) return;
            _origin = e.GetPosition(_slider);
            _value = _slider.Value;
            _pointer = e.Pointer;
            // Effective visibility has no public change event. Observe only
            // this active drag's parents, including a hidden window or tile.
            _ancestors = _slider.GetVisualAncestors().ToArray();
            foreach (Visual ancestor in _ancestors) ancestor.PropertyChanged += OnAncestorChanged;
            _slider.Focus();
            e.Pointer.Capture(_slider);
            e.PreventGestureRecognition();
            e.Handled = true;
        }

        private void Moved(object? sender, PointerEventArgs e)
        {
            if (e.Pointer != _pointer) return;
            Point point = e.GetPosition(_slider);
            double distance = _slider.Orientation == Orientation.Horizontal ? point.X - _origin.X : _origin.Y - point.Y;
            if (_slider.IsDirectionReversed) distance = -distance;
            _slider.SetCurrentValue(RangeBase.ValueProperty,
                FineValue(_value, distance, _span, _slider.Minimum, _slider.Maximum, GetStep(_slider)));
            e.Handled = true;
        }

        private void Released(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer != _pointer || e.InitialPressMouseButton != MouseButton.Left) return;
            Moved(sender, e);
            End();
            e.Handled = true;
        }

        private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty && !e.GetNewValue<bool>()) End();
        }

        private void End(bool release = true)
        {
            IPointer? pointer = _pointer;
            _pointer = null;
            foreach (Visual ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
            _ancestors = [];
            if (release) pointer?.Capture(null);
        }
    }
}
