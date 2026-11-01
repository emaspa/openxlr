using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>How a processor's graph draws the input's history.</summary>
public enum DynamicsGraphStyle
{
    /// <summary>One line, 0 dBFS at the top and the floor at the bottom: the expander's.</summary>
    Line,
    /// <summary>
    /// A filled envelope around the middle, which is the floor, reaching 0 dBFS
    /// at the top and the bottom: the compressor's, which reads like a waveform.
    /// </summary>
    Envelope,
}

/// <summary>
/// A compressor's or expander's input over the last seconds, with its
/// threshold across it. The threshold's handle on the right edge, or a press
/// anywhere on the graph, moves the threshold; the arrow keys step it by a
/// decibel. Its colours are the skin service's live brushes, so it repaints
/// when the skin changes rather than when a property does.
/// </summary>
public sealed class DynamicsGraph : Control
{
    public static readonly StyledProperty<DynamicsHistory?> HistoryProperty =
        AvaloniaProperty.Register<DynamicsGraph, DynamicsHistory?>(nameof(History));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Threshold), -18,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> FloorProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(Floor), -80);

    public static readonly StyledProperty<double> MaximumThresholdProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(MaximumThreshold));

    public static readonly StyledProperty<DynamicsGraphStyle> GraphStyleProperty =
        AvaloniaProperty.Register<DynamicsGraph, DynamicsGraphStyle>(nameof(GraphStyle));

    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(LabelFontSize), 10);

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(LineThickness), 1.5);

    public static readonly StyledProperty<double> HandleSizeProperty =
        AvaloniaProperty.Register<DynamicsGraph, double>(nameof(HandleSize), 12);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DynamicsGraph, CornerRadius>(nameof(CornerRadius), new CornerRadius(6));

    private DynamicsHistory? _listening;
    private bool _skinListening, _dragging;

    static DynamicsGraph()
    {
        AffectsRender<DynamicsGraph>(HistoryProperty, ThresholdProperty, FloorProperty, GraphStyleProperty, LabelFontSizeProperty,
            LineThicknessProperty, HandleSizeProperty, CornerRadiusProperty);
        FocusableProperty.OverrideDefaultValue<DynamicsGraph>(true);
        HistoryProperty.Changed.AddClassHandler<DynamicsGraph>((graph, _) => graph.Listen(graph.IsAttachedToVisualTree()));
    }

    public DynamicsHistory? History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    /// <summary>The level at the bottom of the line, or in the middle of the envelope, in dBFS.</summary>
    public double Floor { get => GetValue(FloorProperty); set => SetValue(FloorProperty, value); }
    /// <summary>The highest threshold a drag may set.</summary>
    public double MaximumThreshold { get => GetValue(MaximumThresholdProperty); set => SetValue(MaximumThresholdProperty, value); }
    public DynamicsGraphStyle GraphStyle { get => GetValue(GraphStyleProperty); set => SetValue(GraphStyleProperty, value); }
    public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }
    public double HandleSize { get => GetValue(HandleSizeProperty); set => SetValue(HandleSizeProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>A drag is moving the threshold.</summary>
    public bool IsDragging => _dragging;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Listen(false);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Follow the history and the skin only while shown, so a closed window holds on to nothing.</summary>
    private void Listen(bool on)
    {
        DynamicsHistory? want = on ? History : null;
        if (!ReferenceEquals(_listening, want))
        {
            if (_listening is not null) _listening.Changed -= InvalidateVisual;
            _listening = want;
            if (want is not null) want.Changed += InvalidateVisual;
        }
        if (_skinListening != on)
        {
            if (on) SkinService.Changed += InvalidateVisual;
            else SkinService.Changed -= InvalidateVisual;
            _skinListening = on;
        }
        InvalidateVisual();
    }

    /// <summary>Where the history is drawn, inside the axis labels on the left and the handle on the right.</summary>
    internal Rect Plot
    {
        get
        {
            double left = Math.Ceiling(LabelFontSize * 2.4) + 8, right = Math.Max(HandleSize, 8) + 4;
            return new Rect(left, 6, Math.Max(1, Bounds.Width - left - right), Math.Max(1, Bounds.Height - 12));
        }
    }

    /// <summary>The height a level is drawn at; in the envelope, its upper half.</summary>
    internal double Y(double db, Rect plot)
    {
        double floor = Math.Min(Floor, -1);
        double t = Math.Clamp((db - floor) / -floor, 0, 1);
        return GraphStyle == DynamicsGraphStyle.Envelope
            ? plot.Center.Y - t * plot.Height / 2
            : plot.Bottom - t * plot.Height;
    }

    /// <summary>The level at a height, the inverse of <see cref="Y"/>, the envelope's lower half mirrored.</summary>
    internal double LevelAt(double y, Rect plot)
    {
        double floor = Math.Min(Floor, -1);
        double t = GraphStyle == DynamicsGraphStyle.Envelope
            ? Math.Abs(plot.Center.Y - y) / (plot.Height / 2)
            : (plot.Bottom - y) / plot.Height;
        return floor + Math.Clamp(t, 0, 1) * -floor;
    }

    private static IBrush Live(string token) => SkinService.LiveBrush(token);

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size), plot = Plot;
        context.DrawRectangle(Live("Ox.Eq.Background"), null, new RoundedRect(bounds, CornerRadius));

        // The grid every 20 dB, labelled, and the floor.
        var grid = new Pen(Live("Ox.Eq.Grid"), 1);
        IBrush labels = Live("Ox.Eq.GridLabel");
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        double floor = Math.Min(Floor, -1);
        for (double db = 0; db >= floor - 0.01; db -= 20)
        {
            foreach (double y in Heights(db, plot))
            {
                context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
                var text = new FormattedText(db.ToString("0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, LabelFontSize, labels);
                context.DrawText(text, new Point(plot.Left - text.Width - 6, Math.Clamp(y - text.Height / 2, 0, Math.Max(0, bounds.Height - text.Height))));
            }
        }

        // The history, newest at the right edge, one push per step.
        if (History is { Count: > 1 } history)
        {
            double step = plot.Width / (DynamicsHistory.Capacity - 1);
            double x0 = plot.Right - (history.Count - 1) * step;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                if (GraphStyle == DynamicsGraphStyle.Envelope)
                {
                    g.BeginFigure(new Point(x0, Y(history[0], plot)), true);
                    for (int i = 1; i < history.Count; i++) g.LineTo(new Point(x0 + i * step, Y(history[i], plot)));
                    for (int i = history.Count - 1; i >= 0; i--)
                        g.LineTo(new Point(x0 + i * step, 2 * plot.Center.Y - Y(history[i], plot)));
                    g.EndFigure(true);
                }
                else
                {
                    g.BeginFigure(new Point(x0, Y(history[0], plot)), false);
                    for (int i = 1; i < history.Count; i++) g.LineTo(new Point(x0 + i * step, Y(history[i], plot)));
                    g.EndFigure(false);
                }
            }
            if (GraphStyle == DynamicsGraphStyle.Envelope)
                context.DrawGeometry(Live("Ox.Dynamics.LevelFill"), new Pen(Live("Ox.Dynamics.Level"), LineThickness), geometry);
            else
                context.DrawGeometry(null, new Pen(Live("Ox.Dynamics.Level"), LineThickness, lineJoin: PenLineJoin.Round), geometry);
        }

        // The threshold across the graph, and its handle on the right edge of the upper line.
        IBrush thresholdBrush = Live("Ox.Dynamics.Threshold");
        var threshold = new Pen(thresholdBrush, Math.Max(1, LineThickness * 0.75));
        double[] lines = Heights(Threshold, plot);
        foreach (double y in lines) context.DrawLine(threshold, new Point(plot.Left, y), new Point(plot.Right, y));
        double size = Math.Max(HandleSize, 6), top = lines[0];
        var handle = new StreamGeometry();
        using (StreamGeometryContext g = handle.Open())
        {
            // A tab pointing left at the line.
            g.BeginFigure(new Point(plot.Right, top), true);
            g.LineTo(new Point(plot.Right + size * 0.4, top - size / 2));
            g.LineTo(new Point(plot.Right + size, top - size / 2));
            g.LineTo(new Point(plot.Right + size, top + size / 2));
            g.LineTo(new Point(plot.Right + size * 0.4, top + size / 2));
            g.EndFigure(true);
        }
        context.DrawGeometry(thresholdBrush, null, handle);
        if (IsFocused)
            context.DrawRectangle(null, new Pen(labels, 1), new Rect(plot.Right - 1, top - size / 2 - 2, size + 3, size + 4));
    }

    /// <summary>Where a level's line is drawn: once on the line, twice on the envelope, the upper one first.</summary>
    private double[] Heights(double db, Rect plot)
    {
        double y = Y(db, plot);
        return GraphStyle == DynamicsGraphStyle.Envelope && Math.Abs(y - plot.Center.Y) > 0.5 ? [y, 2 * plot.Center.Y - y] : [y];
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) MoveTo(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.Key switch
        {
            Key.Up or Key.Right => 1,
            Key.Down or Key.Left => -1,
            Key.PageUp => 6,
            Key.PageDown => -6,
            _ => 0,
        };
        if (step == 0) return;
        SetThreshold(Threshold + step);
        e.Handled = true;
    }

    private void MoveTo(double y) => SetThreshold(LevelAt(y, Plot));

    /// <summary>A whole decibel inside the threshold's range.</summary>
    internal void SetThreshold(double db)
        => Threshold = Math.Clamp(Math.Round(db, MidpointRounding.AwayFromZero), Math.Min(Floor, -1), MaximumThreshold);
}
