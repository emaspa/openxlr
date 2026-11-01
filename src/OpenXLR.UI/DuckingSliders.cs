using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>How a ducking slider's travel maps to its value.</summary>
public enum DuckingScale
{
    /// <summary>The value itself, rising to the right.</summary>
    Linear,
    /// <summary>The value falling to the right: a lower threshold is a higher sensitivity.</summary>
    Reversed,
    /// <summary>The value's logarithm, so short and long times both get room.</summary>
    Log,
}

/// <summary>One of the Wave XLR Pro's presets on a ducking slider: a click sets its value exactly.</summary>
public sealed class DuckingMark
{
    private readonly DuckingSliderViewModel _owner;

    internal DuckingMark(DuckingSliderViewModel owner, int value, string label)
    {
        _owner = owner;
        Value = value;
        Label = label;
    }

    public int Value { get; }

    /// <summary>The preset's name: Low, Medium, Slow and so on.</summary>
    public string Label { get; }

    /// <summary>Where the mark sits on the slider's travel.</summary>
    public double Position => _owner.PositionOf(Value);

    /// <summary>The name with the value: "Medium, -12 dBFS".</summary>
    public string Tip => Localizer.Format("DuckingMarkTip", Label, _owner.Format(Value));

    public void Choose() => _owner.Choose(this);
}

/// <summary>
/// One ducking slider: a whole number between its bounds, with the presets
/// as marks. A drag shows its value as it moves and lands on a mark within
/// <see cref="SnapShare"/> of the travel; the value goes to the daemon when
/// the drag ends, on each key step and on a mark's click, never while the
/// pointer moves. A report from the daemon moves it unless a drag holds it.
/// </summary>
public sealed class DuckingSliderViewModel : ViewModelBase
{
    /// <summary>How near a mark, as a share of the slider's travel, a drag lands on it.</summary>
    public const double SnapShare = 0.03;

    private readonly Func<int, string> _format;
    private readonly Action<DuckingSliderViewModel> _changed;
    private int _value, _committed;
    private double _position;
    private bool _dragging;

    internal DuckingSliderViewModel(string name, string tip, int minimum, int maximum, DuckingScale scale, Func<int, string> format,
        int value, IEnumerable<(int Value, string Label)> marks, Action<DuckingSliderViewModel> changed, bool isPart = false)
    {
        Name = name;
        Tip = tip;
        Minimum = minimum;
        Maximum = maximum;
        Scale = scale;
        IsPart = isPart;
        _format = format;
        _changed = changed;
        Marks = [.. marks.Select(m => new DuckingMark(this, m.Value, m.Label))];
        _value = _committed = Math.Clamp(value, minimum, maximum);
        _position = PositionOf(_value);
    }

    public string Name { get; }

    public string Tip { get; }

    /// <summary>One of a group's sliders, Speed's attack or release: its name is set in.</summary>
    public bool IsPart { get; }

    public int Minimum { get; }

    public int Maximum { get; }

    public DuckingScale Scale { get; }

    public IReadOnlyList<DuckingMark> Marks { get; }

    public double MinimumPosition => Math.Min(PositionOf(Minimum), PositionOf(Maximum));

    public double MaximumPosition => Math.Max(PositionOf(Minimum), PositionOf(Maximum));

    /// <summary>The whole number the slider stands at.</summary>
    public int Value => _value;

    /// <summary>The value with its unit: "-12 dBFS", "12 dB", "80 ms".</summary>
    public string Text => Format(_value);

    /// <summary>A pointer holds the slider: reports from the daemon wait, and nothing is sent until it lets go.</summary>
    public bool IsDragging => _dragging;

    internal string Format(int value) => _format(value);

    public double PositionOf(int value) => Scale switch
    {
        DuckingScale.Log => Math.Log10(Math.Max(1, value)),
        DuckingScale.Reversed => -value,
        _ => value,
    };

    /// <summary>The whole number at a point of the travel.</summary>
    public int ValueAt(double position)
    {
        double value = Scale switch
        {
            DuckingScale.Log => Math.Pow(10, position),
            DuckingScale.Reversed => -position,
            _ => position,
        };
        return Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), Minimum, Maximum);
    }

    /// <summary>
    /// The slider's own value. A drag moves it, landing on a mark close
    /// enough; it sends nothing. The keys step through <see cref="Step"/>, so
    /// a range the slider clamps while it is built changes no value.
    /// </summary>
    public double Position
    {
        get => _position;
        set
        {
            if (!_dragging || !double.IsFinite(value) || value == _position) return;
            double position = Math.Clamp(value, MinimumPosition, MaximumPosition);
            int next = ValueAt(position);
            if (Nearest(position) is { } mark)
            {
                next = mark.Value;
                position = mark.Position;
            }
            _position = position;
            if (position != value) Raise();
            Move(next);
        }
    }

    /// <summary>The mark within snapping distance of a point of the travel, the nearest first.</summary>
    internal DuckingMark? Nearest(double position)
    {
        double reach = (MaximumPosition - MinimumPosition) * SnapShare;
        return Marks.Where(m => Math.Abs(m.Position - position) <= reach).OrderBy(m => Math.Abs(m.Position - position)).FirstOrDefault();
    }

    /// <summary>A pointer pressed the slider.</summary>
    public void BeginDrag() => _dragging = true;

    /// <summary>The pointer let go: the value goes to the daemon when it moved.</summary>
    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Commit();
    }

    /// <summary>
    /// A key step: one unit on a straight slider, five with a large step; a
    /// twentieth of the value on a logarithmic one (a quarter with a large
    /// step), at least one. <paramref name="direction"/> is +1 to the right.
    /// </summary>
    public void Step(int direction, bool large)
    {
        int size = Scale == DuckingScale.Log
            ? Math.Max(1, (int)Math.Round(_value * (large ? 0.25 : 0.05), MidpointRounding.AwayFromZero)) : large ? 5 : 1;
        int sign = Scale == DuckingScale.Reversed ? -direction : direction;
        Set(_value + sign * size);
    }

    /// <summary>The Home and End keys: the left or right end of the travel.</summary>
    public void StepToEnd(bool right) => Set(ValueAt(right ? MaximumPosition : MinimumPosition));

    /// <summary>A mark's label was clicked: its value exactly.</summary>
    public void Choose(DuckingMark mark) => Set(mark.Value);

    private void Set(int value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        _position = PositionOf(value);
        Raise(nameof(Position));
        Move(value);
        Commit();
    }

    private void Move(int value)
    {
        if (value == _value) return;
        _value = value;
        Raise(nameof(Value));
        Raise(nameof(Text));
    }

    private void Commit()
    {
        if (_value == _committed) return;
        _committed = _value;
        _changed(this);
    }

    /// <summary>Show what the daemon holds, sending nothing; a drag in progress keeps its own value.</summary>
    internal void Show(int value)
    {
        if (_dragging) return;
        value = Math.Clamp(value, Minimum, Maximum);
        _committed = value;
        _position = PositionOf(value);
        Raise(nameof(Position));
        Move(value);
    }

    internal static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// The presets under a ducking slider: a short tick at each one's place on
/// the travel and its name, a button, centred below the tick. A name that
/// would run into the one before it drops to the next line, so close presets
/// stay readable at any width, and none passes either end.
/// </summary>
public sealed class DuckingMarks : Panel
{
    public static readonly StyledProperty<IReadOnlyList<DuckingMark>?> MarksProperty =
        AvaloniaProperty.Register<DuckingMarks, IReadOnlyList<DuckingMark>?>(nameof(Marks));

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<DuckingMarks, IBrush?>(nameof(TickBrush));

    /// <summary>The tick's length above the names.</summary>
    public const double TickLength = 5;

    private const double Gap = 6;

    private Slider? _slider;
    private (double Left, double Right) _inset;

    static DuckingMarks()
    {
        MarksProperty.Changed.AddClassHandler<DuckingMarks>((panel, _) => panel.Rebuild());
        TickBrushProperty.Changed.AddClassHandler<DuckingMarks>((panel, change) =>
        {
            foreach ((_, _, Rectangle tick) in panel._items) tick.Fill = change.GetNewValue<IBrush?>();
        });
    }

    private readonly List<(DuckingMark Mark, Button Label, Rectangle Tick)> _items = [];

    public IReadOnlyList<DuckingMark>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /// <summary>The slider the marks line up with; its thumb's travel sets where each one sits.</summary>
    public Slider? Slider
    {
        get => _slider;
        set
        {
            if (_slider is not null) _slider.LayoutUpdated -= OnSliderLayout;
            _slider = value;
            if (_slider is not null) _slider.LayoutUpdated += OnSliderLayout;
            InvalidateMeasure();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        _items.Clear();
        foreach (DuckingMark mark in Marks ?? [])
        {
            var label = new Button { Name = "DuckingMark", Content = mark.Label, Tag = mark, Focusable = false };
            label.Classes.Add("duckingMark");
            ToolTip.SetTip(label, mark.Tip);
            Avalonia.Automation.AutomationProperties.SetName(label, mark.Tip);
            label.Click += (_, e) =>
            {
                mark.Choose();
                e.Handled = true;
            };
            var tick = new Rectangle { Width = 1, Height = TickLength, Fill = TickBrush, IsHitTestVisible = false };
            _items.Add((mark, label, tick));
            Children.Add(tick);
            Children.Add(label);
        }
        InvalidateMeasure();
    }

    // The thumb's centre travels from half a thumb inside the track's left
    // edge to half a thumb inside its right; the panel spans the slider.
    private void OnSliderLayout(object? sender, EventArgs e)
    {
        (double Left, double Right) inset = Inset();
        if (Math.Abs(inset.Left - _inset.Left) < 0.5 && Math.Abs(inset.Right - _inset.Right) < 0.5) return;
        _inset = inset;
        InvalidateMeasure();
    }

    private (double Left, double Right) Inset()
    {
        if (_slider is null) return (0, 0);
        Track? track = _slider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track is null || track.Bounds.Width <= 0) return (0, 0);
        double origin = track.TranslatePoint(new Point(0, 0), _slider)?.X ?? 0;
        double half = (track.Thumb?.Bounds.Width ?? 0) / 2;
        return (origin + half, Math.Max(0, _slider.Bounds.Width - origin - track.Bounds.Width) + half);
    }

    private double X(DuckingMark mark, double width)
    {
        double min = _slider?.Minimum ?? 0, max = _slider?.Maximum ?? 1;
        double span = Math.Max(0, width - _inset.Left - _inset.Right);
        double share = max > min ? Math.Clamp((mark.Position - min) / (max - min), 0, 1) : 0;
        return _inset.Left + share * span;
    }

    /// <summary>Each name's left edge and line: centred under its tick, kept inside, dropped a line past a neighbour.</summary>
    private List<(Button Label, double Left, int Line)> Place(double width)
    {
        var placed = new List<(Button, double, int)>();
        var lineEnds = new List<double>();
        foreach ((DuckingMark mark, Button label, _) in _items.OrderBy(i => X(i.Mark, width)))
        {
            double w = label.DesiredSize.Width;
            double left = Math.Clamp(X(mark, width) - w / 2, 0, Math.Max(0, width - w));
            int line = lineEnds.FindIndex(end => end + Gap <= left);
            if (line < 0)
            {
                line = lineEnds.Count;
                lineEnds.Add(0);
            }
            lineEnds[line] = left + w;
            placed.Add((label, left, line));
        }
        return placed;
    }

    private double LineHeight => _items.Count == 0 ? 0 : _items.Max(i => i.Label.DesiredSize.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children) child.Measure(Size.Infinity);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _items.Sum(i => i.Label.DesiredSize.Width + Gap);
        int lines = Place(width).Select(p => p.Line + 1).DefaultIfEmpty(0).Max();
        return new Size(double.IsFinite(availableSize.Width) ? 0 : width, TickLength + lines * LineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The slider above is arranged by now: a travel that differs from
        // the one the measure assumed measures again.
        (double Left, double Right) inset = Inset();
        if (Math.Abs(inset.Left - _inset.Left) >= 0.5 || Math.Abs(inset.Right - _inset.Right) >= 0.5)
        {
            _inset = inset;
            InvalidateMeasure();
        }
        foreach ((DuckingMark mark, _, Rectangle tick) in _items)
            tick.Arrange(new Rect(Math.Round(X(mark, finalSize.Width)), 0, 1, TickLength));
        double line = LineHeight;
        foreach ((Button label, double left, int index) in Place(finalSize.Width))
            label.Arrange(new Rect(left, TickLength + index * line, label.DesiredSize.Width, label.DesiredSize.Height));
        return finalSize;
    }
}

/// <summary>
/// The pointer and keys of a ducking slider, seen before the slider's own
/// handling: a press starts a drag and its release or lost capture ends it,
/// and the arrow, page, Home and End keys step the value, each step a
/// change the daemon is told of.
/// </summary>
internal static class DuckingSliderInput
{
    internal static void Attach(Control host, Slider slider)
    {
        DuckingSliderViewModel? Model() => host.DataContext as DuckingSliderViewModel;
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed && slider.IsEffectivelyEnabled
                && e.Source is Visual source && (source == slider || slider.IsVisualAncestorOf(source)))
                Model()?.BeginDrag();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Model()?.EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) =>
        {
            // Capture passing to the thumb inside the slider is still the same drag.
            if (e.Pointer.Captured is Visual to && (to == slider || slider.IsVisualAncestorOf(to))) return;
            Model()?.EndDrag();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        slider.DetachedFromVisualTree += (_, _) => Model()?.EndDrag();
        host.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (Model() is not { } model || e.Source is not Visual source || (source != slider && !slider.IsVisualAncestorOf(source))) return;
            switch (e.Key)
            {
                case Key.Left or Key.Down: model.Step(-1, false); break;
                case Key.Right or Key.Up: model.Step(1, false); break;
                case Key.PageDown: model.Step(-1, true); break;
                case Key.PageUp: model.Step(1, true); break;
                case Key.Home: model.StepToEnd(false); break;
                case Key.End: model.StepToEnd(true); break;
                default: return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
