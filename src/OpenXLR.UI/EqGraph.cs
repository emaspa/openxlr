using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// The response graph's geometry: frequency on a log axis from 20 Hz to
/// 20 kHz, gain from -20 to +20 dB, and the curve sampled from
/// <see cref="EqResponse"/>, so what is drawn is what the daemon runs.
/// </summary>
public static class EqCurve
{
    /// <summary>The rate the curve is computed at, the graph's usual rate.</summary>
    public const double SampleRate = 48000;

    public const double MinFrequency = 20, MaxFrequency = 20000;

    /// <summary>The gain axis runs from minus this to plus this, the range of a band's gain.</summary>
    public const double GainRange = MicEq.MaxGain;

    /// <summary>The gain axis has a line every this many dB and a label on every other line.</summary>
    public const double GainGridStep = 5;

    /// <summary>The frequencies the axis is labelled at, which are also where the six regions meet.</summary>
    public static readonly double[] Edges = [20, 80, 300, 1400, 5000, 12000, 20000];

    /// <summary>How many regions lie between the edges.</summary>
    public const int RegionCount = 6;

    /// <summary>A region's name, in our own words, from the lowest.</summary>
    public static string RegionName(int region) => region switch
    {
        0 => Localizer.Text("EqRegionRumble"),
        1 => Localizer.Text("EqRegionBody"),
        2 => Localizer.Text("EqRegionRoom"),
        3 => Localizer.Text("EqRegionPresence"),
        4 => Localizer.Text("EqRegionSibilance"),
        _ => Localizer.Text("EqRegionAir"),
    };

    /// <summary>What a region holds in a voice, for its tooltip.</summary>
    public static string RegionHint(int region) => region switch
    {
        0 => Localizer.Text("EqRegionRumbleTip"),
        1 => Localizer.Text("EqRegionBodyTip"),
        2 => Localizer.Text("EqRegionRoomTip"),
        3 => Localizer.Text("EqRegionPresenceTip"),
        4 => Localizer.Text("EqRegionSibilanceTip"),
        _ => Localizer.Text("EqRegionAirTip"),
    };

    private static readonly double LogMin = Math.Log10(MinFrequency), LogMax = Math.Log10(MaxFrequency);

    public static double X(double hz, Rect plot) => plot.Left + (Math.Log10(hz) - LogMin) / (LogMax - LogMin) * plot.Width;

    public static double FrequencyAt(double x, Rect plot) =>
        Math.Pow(10, LogMin + Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * (LogMax - LogMin));

    public static double Y(double db, Rect plot) => plot.Top + (GainRange - Math.Clamp(db, -60, 60)) / (2 * GainRange) * plot.Height;

    public static double GainAt(double y, Rect plot) => GainRange - (y - plot.Top) / plot.Height * 2 * GainRange;

    /// <summary>The frequency of point <paramref name="i"/> of <paramref name="count"/>, evenly spaced on the axis.</summary>
    public static double FrequencyOf(int i, int count) => Math.Pow(10, LogMin + (LogMax - LogMin) * i / (count - 1));

    /// <summary>The cascade of the bands that are on, as points across the plot.</summary>
    public static Point[] Sum(IReadOnlyList<EqBand> bands, Rect plot, int count)
    {
        EqCoefficients[] coefficients = [.. bands.Where(b => b.Enabled).Select(b => EqResponse.Coefficients(b, SampleRate))];
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            double hz = FrequencyOf(i, count);
            return new Point(X(hz, plot), Y(coefficients.Sum(c => EqResponse.MagnitudeDb(c, SampleRate, hz)), plot));
        })];
    }

    /// <summary>One band's own curve.</summary>
    public static Point[] Band(EqBand band, Rect plot, int count)
    {
        EqCoefficients c = EqResponse.Coefficients(band, SampleRate);
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            double hz = FrequencyOf(i, count);
            return new Point(X(hz, plot), Y(EqResponse.MagnitudeDb(c, SampleRate, hz), plot));
        })];
    }

    /// <summary>Where a band's handle sits: its frequency, at its gain, or on the 0 dB line for a type without one.</summary>
    public static Point Handle(EqBand band, Rect plot) =>
        new(X(band.Frequency, plot), Y(MicEq.UsesGain(band.Type) ? band.Gain : 0, plot));

    /// <summary>An axis label: 80, 300, 1.4k.</summary>
    public static string Label(double hz) => hz >= 1000
        ? (hz / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k"
        : hz.ToString("0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The EQ's response graph: the six regions shaded in turn and named, a grid,
/// each band's curve thin in its colour, the sum of the four thick over a
/// light fill to 0 dB, and a handle per band. A handle is dragged sideways
/// for frequency and up and down for gain, or for Q on a type without a
/// gain; the wheel steps Q, a double click switches the band and a right
/// click, or the menu key, picks its type. Every colour is a skin token,
/// read as the skin service's live brushes.
/// </summary>
public sealed class EqGraph : Panel
{
    public static readonly StyledProperty<double> CurveThicknessProperty =
        AvaloniaProperty.Register<EqGraph, double>(nameof(CurveThickness), 2.5);

    public static readonly StyledProperty<double> BandCurveThicknessProperty =
        AvaloniaProperty.Register<EqGraph, double>(nameof(BandCurveThickness), 1.25);

    public static readonly StyledProperty<double> HandleSizeProperty =
        AvaloniaProperty.Register<EqGraph, double>(nameof(HandleSize), 18);

    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<EqGraph, double>(nameof(LabelFontSize), 10);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<EqGraph, CornerRadius>(nameof(CornerRadius), new CornerRadius(6));

    /// <summary>The smallest target a handle offers the pointer: Touch sizing raises it.</summary>
    public static readonly StyledProperty<double> MinimumTargetProperty =
        AvaloniaProperty.Register<EqGraph, double>(nameof(MinimumTarget));

    /// <summary>A press moves a handle only once the pointer has gone this far, so a click that selects never nudges.</summary>
    public const double DragThreshold = 4;

    /// <summary>The radius around a handle's centre that takes the pointer, as other parametric editors give it.</summary>
    public const double GrabRadius = 20;

    private MicEqViewModel? _model;
    private readonly EqBackdrop _backdrop;
    private readonly EqRegion[] _regions;
    private EqHandle[] _handles = [];
    private DragState? _drag;

    private sealed class DragState(EqHandle handle, Point start, Vector offset, double startQ)
    {
        public EqHandle Handle { get; } = handle;
        public Point Start { get; } = start;
        public Vector Offset { get; } = offset;
        public double StartQ { get; } = startQ;
        public bool Moving { get; set; }
    }

    static EqGraph()
    {
        foreach (AvaloniaProperty property in new AvaloniaProperty[] { CurveThicknessProperty, BandCurveThicknessProperty, LabelFontSizeProperty, CornerRadiusProperty, HandleSizeProperty })
            property.Changed.AddClassHandler<EqGraph>((graph, _) => graph.Redraw());
        AffectsArrange<EqGraph>(HandleSizeProperty, LabelFontSizeProperty, MinimumTargetProperty);
    }

    public EqGraph()
    {
        ClipToBounds = true;
        _backdrop = new EqBackdrop(this);
        Children.Add(_backdrop);
        _regions = [.. Enumerable.Range(0, EqCurve.RegionCount).Select(i => new EqRegion(i))];
        foreach (EqRegion region in _regions) Children.Add(region);
    }

    public double CurveThickness { get => GetValue(CurveThicknessProperty); set => SetValue(CurveThicknessProperty, value); }
    public double BandCurveThickness { get => GetValue(BandCurveThicknessProperty); set => SetValue(BandCurveThicknessProperty, value); }
    public double HandleSize { get => GetValue(HandleSizeProperty); set => SetValue(HandleSizeProperty, value); }
    public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double MinimumTarget { get => GetValue(MinimumTargetProperty); set => SetValue(MinimumTargetProperty, value); }

    private void Redraw()
    {
        _backdrop.InvalidateVisual();
        foreach (EqHandle handle in _handles) handle.InvalidateVisual();
    }

    /// <summary>The handles, in band order.</summary>
    public IReadOnlyList<EqHandle> Handles => _handles;

    internal IReadOnlyList<EqRegion> RegionLabels => _regions;

    /// <summary>A drag is moving a handle.</summary>
    public bool IsDragging => _drag?.Moving == true;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as MicEqViewModel);
    }

    private void Attach(MicEqViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        Listen(false);
        foreach (EqHandle handle in _handles) Children.Remove(handle);
        _model = model;
        _handles = model is null ? [] : [.. model.Bands.Select(b => new EqHandle(this, b))];
        foreach (EqHandle handle in _handles) Children.Add(handle);
        Listen(this.IsAttachedToVisualTree());
        InvalidateArrange();
        _backdrop.InvalidateVisual();
    }

    private bool _listening;

    /// <summary>Follow the model only while the graph is shown, so a closed editor holds on to nothing.</summary>
    private void Listen(bool on)
    {
        if (_listening == on || _model is null && on) return;
        _listening = on;
        if (on)
        {
            _model!.Changed += OnModelChanged;
            SkinService.Changed += OnModelChanged;
        }
        else
        {
            if (_model is not null) _model.Changed -= OnModelChanged;
            SkinService.Changed -= OnModelChanged;
        }
    }

    private void OnModelChanged()
    {
        if (_model is { } model && (_handles.Length != model.Bands.Count || _handles.Where((h, i) => !ReferenceEquals(h.Band, model.Bands[i])).Any()))
        {
            EndDrag();
            foreach (EqHandle handle in _handles) Children.Remove(handle);
            _handles = [.. model.Bands.Select(b => new EqHandle(this, b))];
            foreach (EqHandle handle in _handles) Children.Add(handle);
        }
        InvalidateArrange();
        _backdrop.InvalidateVisual();
        foreach (EqHandle handle in _handles) handle.InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
        OnModelChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Listen(false);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The axes' area: the window's size less the gain labels on the left and the frequency labels under it.</summary>
    public Rect Plot => PlotFor(Bounds.Size);

    /// <summary>The square a handle takes the pointer in.</summary>
    internal double Target => Math.Max(Math.Max(HandleSize, 2 * GrabRadius), MinimumTarget);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children) child.Measure(Size.Infinity);
        return new Size(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect plot = PlotFor(finalSize);
        _backdrop.Arrange(new Rect(finalSize));
        double strip = Math.Ceiling(LabelFontSize * 1.6) + 4;
        for (int i = 0; i < _regions.Length; i++)
        {
            double left = EqCurve.X(EqCurve.Edges[i], plot), right = EqCurve.X(EqCurve.Edges[i + 1], plot);
            _regions[i].Arrange(new Rect(left, plot.Top, Math.Max(0, right - left), strip));
        }
        double target = Target;
        foreach (EqHandle handle in _handles)
        {
            Point centre = EqCurve.Handle(handle.Band.Band, plot);
            centre = new Point(Math.Clamp(centre.X, plot.Left, plot.Right), Math.Clamp(centre.Y, plot.Top, plot.Bottom));
            handle.Arrange(new Rect(centre.X - target / 2, centre.Y - target / 2, target, target));
            handle.ZIndex = handle.Band.IsSelected ? 2 : 1;
        }
        return finalSize;
    }

    private Rect PlotFor(Size size)
    {
        double label = LabelFontSize;
        double left = Math.Ceiling(label * 2.4) + 8, bottom = Math.Ceiling(label * 1.4) + 6;
        return new Rect(left, 6, Math.Max(1, size.Width - left - 12), Math.Max(1, size.Height - bottom - 6));
    }

    /// <summary>How many points a curve is drawn with at this width.</summary>
    internal int PointCount => (int)Math.Clamp(Plot.Width / 2, 64, 512);

    private static IBrush Live(string token) => SkinService.LiveBrush(token);

    internal static readonly string[] BandTokens = ["Ox.Eq.Band1", "Ox.Eq.Band2", "Ox.Eq.Band3", "Ox.Eq.Band4"];

    internal static string BandToken(int index) => BandTokens[Math.Max(0, index) % BandTokens.Length];

    /// <summary>The graph's drawing, under the region labels and the handles; a panel's own rendering is sealed.</summary>
    internal void Draw(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size), plot = Plot;
        CornerRadius radius = CornerRadius;
        context.DrawRectangle(Live("Ox.Eq.Background"), null, new RoundedRect(bounds, radius));

        // The regions, shaded in turn.
        for (int i = 0; i + 1 < EqCurve.Edges.Length; i++)
        {
            double left = EqCurve.X(EqCurve.Edges[i], plot), right = EqCurve.X(EqCurve.Edges[i + 1], plot);
            context.DrawRectangle(Live(i % 2 == 0 ? "Ox.Eq.ZoneA" : "Ox.Eq.ZoneB"), null, new Rect(left, plot.Top, right - left, plot.Height));
        }

        // The grid: every 5 dB across, labelled every 10, the region edges down, the 0 dB line marked.
        var grid = new Pen(Live("Ox.Eq.Grid"), 1);
        var zero = new Pen(Live("Ox.Eq.GridLabel"), 1);
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        IBrush label = Live("Ox.Eq.GridLabel");
        for (double db = -EqCurve.GainRange; db <= EqCurve.GainRange; db += EqCurve.GainGridStep)
        {
            double y = Math.Round(EqCurve.Y(db, plot)) + 0.5;
            context.DrawLine(db == 0 ? zero : grid, new Point(plot.Left, y), new Point(plot.Right, y));
            if (db % (2 * EqCurve.GainGridStep) != 0) continue;
            var text = Text(db > 0 ? $"+{db:0}" : $"{db:0}", typeface, label);
            context.DrawText(text, new Point(plot.Left - 6 - text.Width, y - text.Height / 2));
        }
        foreach (double hz in EqCurve.Edges)
        {
            double x = Math.Round(EqCurve.X(hz, plot)) + 0.5;
            context.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var text = Text(EqCurve.Label(hz), typeface, label);
            // A graph narrower than a label, as one in a step not laid out yet, keeps it at the left edge.
            double left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width));
            context.DrawText(text, new Point(left, plot.Bottom + 3));
        }

        if (_model is null) return;
        int count = PointCount;
        EqBand[] bands = [.. _model.Bands.Select(b => b.Band)];
        using (context.PushClip(plot))
        {
            // Each band that is on, thin in its colour, where it shapes the
            // sound: its flat stretches would only paint over the 0 dB line.
            double zeroLine = EqCurve.Y(0, plot);
            for (int i = 0; i < bands.Length; i++)
            {
                if (!bands[i].Enabled) continue;
                if (Shaping(EqCurve.Band(bands[i], plot, count), zeroLine) is { } shape)
                    context.DrawGeometry(null, new Pen(Live(BandToken(i)), BandCurveThickness), shape);
            }

            // The sum, over a light fill to 0 dB. While the EQ is off or compared, it is drawn in the off colour.
            Point[] sum = EqCurve.Sum(bands, plot, count);
            double zeroY = EqCurve.Y(0, plot);
            var fill = new StreamGeometry();
            using (StreamGeometryContext g = fill.Open())
            {
                g.BeginFigure(new Point(sum[0].X, zeroY), true);
                foreach (Point p in sum) g.LineTo(p);
                g.LineTo(new Point(sum[^1].X, zeroY));
                g.EndFigure(true);
            }
            bool running = _model.Enabled && !_model.Comparing;
            if (running) context.DrawGeometry(Live("Ox.Eq.CurveFill"), null, fill);
            context.DrawGeometry(null, new Pen(Live(running ? "Ox.Eq.Curve" : "Ox.Eq.HandleOff"), CurveThickness,
                lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Line(sum));
        }

        // The selected band's values in the corner, as a drag moves them.
        if (_model.SelectedBand is { } selected)
        {
            string readout = selected.UsesGain
                ? Localizer.Format("EqReadout", selected.Number, selected.FrequencyText, selected.GainText, selected.QText)
                : Localizer.Format("EqReadoutNoGain", selected.Number, selected.FrequencyText, selected.QText);
            var text = Text(readout, typeface, label);
            double strip = Math.Ceiling(LabelFontSize * 1.6) + 4;
            var box = new Rect(plot.Right - text.Width - 12, plot.Top + strip + 4, text.Width + 8, text.Height + 4);
            if (box.Left > plot.Left)
            {
                context.DrawRectangle(Live("Ox.Eq.Background"), new Pen(Live("Ox.Eq.Grid"), 1), new RoundedRect(box, radius.TopLeft / 2));
                context.DrawText(text, new Point(box.Left + 4, box.Top + 2));
            }
        }
    }

    private FormattedText Text(string text, Typeface typeface, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelFontSize, brush);

    /// <summary>A band's curve without its stretches on the 0 dB line, or null when it is flat throughout.</summary>
    private static StreamGeometry? Shaping(Point[] points, double zero)
    {
        StreamGeometry? geometry = null;
        StreamGeometryContext? g = null;
        bool open = false;
        for (int i = 0; i < points.Length; i++)
        {
            bool flat = Math.Abs(points[i].Y - zero) < 0.5;
            bool nextFlat = i + 1 >= points.Length || Math.Abs(points[i + 1].Y - zero) < 0.5;
            bool previousFlat = i == 0 || Math.Abs(points[i - 1].Y - zero) < 0.5;
            // A point on the line is kept where the curve leaves it or comes back to it.
            if (flat && nextFlat && previousFlat)
            {
                if (open) { g!.EndFigure(false); open = false; }
                continue;
            }
            if (!open)
            {
                if (geometry is null) { geometry = new StreamGeometry(); g = geometry.Open(); }
                g!.BeginFigure(points[i], false);
                open = true;
            }
            else g!.LineTo(points[i]);
        }
        if (open) g!.EndFigure(false);
        g?.Dispose();
        return geometry;
    }

    private static StreamGeometry Line(Point[] points)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        g.BeginFigure(points[0], false);
        for (int i = 1; i < points.Length; i++) g.LineTo(points[i]);
        g.EndFigure(false);
        return geometry;
    }

    // --- the pointer: handlers on the graph, for presses that land on a handle ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is null || HandleOf(e.Source) is not { } handle) return;
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        handle.Focus(NavigationMethod.Pointer);
        _model.SelectedBand = handle.Band;
        if (e.ClickCount >= 2)
        {
            _drag = null;
            handle.Band.Enabled = !handle.Band.Enabled;
            e.Handled = true;
            return;
        }
        StartDrag(handle, point.Position);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;
        DragTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        EqBandViewModel? band = HandleOf(e.Source)?.Band ?? _model?.SelectedBand;
        if (band is null || e.Delta.Y == 0) return;
        Wheel(band, e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>One wheel notch moves Q one step of the daemon's grid; up narrows the band.</summary>
    internal void Wheel(EqBandViewModel band, double delta)
    {
        _model!.SelectedBand = band;
        band.StepQ(Math.Sign(delta) * Math.Max(1, (int)Math.Round(Math.Abs(delta))));
    }

    private static EqHandle? HandleOf(object? source) =>
        source as EqHandle ?? (source as Visual)?.FindAncestorOfType<EqHandle>();

    /// <summary>A press on a handle at a point of the graph: what a drag starts from.</summary>
    internal void StartDrag(EqHandle handle, Point at)
    {
        Point centre = EqCurve.Handle(handle.Band.Band, Plot);
        _drag = new DragState(handle, at, at - centre, handle.Band.Q);
    }

    /// <summary>
    /// The pointer moved during a press. Past the threshold, the handle
    /// follows it: sideways is frequency on the log axis, up and down is gain,
    /// or Q on a type without a gain, the whole height being Q's whole range.
    /// </summary>
    internal void DragTo(Point at)
    {
        if (_drag is null) return;
        if (!_drag.Moving)
        {
            if (Math.Abs(at.X - _drag.Start.X) < DragThreshold && Math.Abs(at.Y - _drag.Start.Y) < DragThreshold) return;
            _drag.Moving = true;
            _drag.Handle.Band.BeginDrag();
        }
        Rect plot = Plot;
        EqBandViewModel band = _drag.Handle.Band;
        Point centre = at - _drag.Offset;
        double hz = EqCurve.FrequencyAt(centre.X, plot);
        if (band.UsesGain) band.DragTo(hz, EqCurve.GainAt(centre.Y, plot));
        else
        {
            EqLimits.EqQRange q = band.Owner.Limits.Q;
            double span = Math.Log10(q.Max) - Math.Log10(q.Min);
            double log = Math.Log10(_drag.StartQ) + (_drag.Start.Y - at.Y) / plot.Height * span;
            band.DragToWithQ(hz, Math.Pow(10, log));
        }
    }

    internal void EndDrag()
    {
        DragState? drag = _drag;
        _drag = null;
        if (drag?.Moving == true) drag.Handle.Band.EndDrag();
        _backdrop.InvalidateVisual();
    }
}

/// <summary>The graph's drawing surface, behind everything the graph holds.</summary>
internal sealed class EqBackdrop : Control
{
    private readonly EqGraph _graph;

    public EqBackdrop(EqGraph graph)
    {
        _graph = graph;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context) => _graph.Draw(context);
}

/// <summary>
/// A band's handle: a numbered circle in the band's colour, hollow while the
/// band is off, with a ring while it is selected. It takes the keyboard:
/// the arrows move it, Page Up and Page Down step Q, Space switches the band
/// and the menu key opens its type menu.
/// </summary>
public sealed class EqHandle : Control
{
    private readonly EqGraph _graph;

    internal EqHandle(EqGraph graph, EqBandViewModel band)
    {
        _graph = graph;
        Band = band;
        Focusable = true;
        IsTabStop = true;
        TabIndex = band.Number;
        Cursor = new Cursor(StandardCursorType.Hand);
        Name = $"EqHandle{band.Number}";
        ContextMenu = TypeMenu(band);
        Describe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Band.PropertyChanged += OnBandChanged;
        Describe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Band.PropertyChanged -= OnBandChanged;
        base.OnDetachedFromVisualTree(e);
    }

    public EqBandViewModel Band { get; }

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        Describe();
        InvalidateVisual();
    }

    private void Describe()
    {
        AutomationProperties.SetName(this, Band.Description);
        ToolTip.SetTip(this, Band.Tip);
        if (ContextMenu is { } menu)
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                item.IsChecked = item.Tag is EqTypeChoice choice ? choice.Type == Band.Type : Band.Enabled;
    }

    /// <summary>The six types with their icons, then the band's switch.</summary>
    private static ContextMenu TypeMenu(EqBandViewModel band)
    {
        var menu = new ContextMenu();
        foreach (EqTypeChoice choice in EqTypeChoice.All)
        {
            var item = new MenuItem
            {
                Header = choice.Name, Tag = choice, ToggleType = MenuItemToggleType.Radio,
                Icon = EqIcons.Create(choice), IsChecked = choice.Type == band.Type,
            };
            ToolTip.SetTip(item, choice.Hint);
            item.Click += (_, _) => band.Type = choice.Type;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var on = new MenuItem { Header = Localizer.Text("EqBandOn"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = band.Enabled };
        on.Click += (_, _) => band.Enabled = !band.Enabled;
        menu.Items.Add(on);
        return menu;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Band.Owner.SelectedBand = Band;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left: Band.StepFrequency(fine ? -1.0 / 48 : -1.0 / 12); break;
            case Key.Right: Band.StepFrequency(fine ? 1.0 / 48 : 1.0 / 12); break;
            case Key.Up when Band.UsesGain: Band.StepGain(fine ? 0.25 : 1); break;
            case Key.Down when Band.UsesGain: Band.StepGain(fine ? -0.25 : -1); break;
            case Key.Up or Key.PageUp: Band.StepQ(1); break;
            case Key.Down or Key.PageDown: Band.StepQ(-1); break;
            case Key.Space: Band.Enabled = !Band.Enabled; break;
            case Key.Enter: ContextMenu?.Open(this); break;
            default: return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        double size = _graph.HandleSize, r = size / 2;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        IBrush colour = SkinService.LiveBrush(EqGraph.BandToken(Band.Number - 1));
        IBrush background = SkinService.LiveBrush("Ox.Eq.Background");
        if (Band.IsSelected || IsFocused)
            context.DrawEllipse(null, new Pen(SkinService.LiveBrush("Ox.Eq.HandleRing"), 2), centre, r + 3, r + 3);
        IBrush number;
        if (Band.Enabled)
        {
            context.DrawEllipse(colour, null, centre, r, r);
            number = background;
        }
        else
        {
            IBrush off = SkinService.LiveBrush("Ox.Eq.HandleOff");
            context.DrawEllipse(background, new Pen(colour, 2), centre, r - 1, r - 1);
            number = off;
        }
        var text = new FormattedText(Band.Number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), weight: FontWeight.Bold), Math.Max(8, size * 0.6), number);
        context.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }
}

/// <summary>A region of the graph: its name along the top, and what it holds in a tooltip.</summary>
internal sealed class EqRegion : Border
{
    public EqRegion(int index)
    {
        Index = index;
        Name = $"EqRegion{index + 1}";
        Background = Brushes.Transparent;
        var label = new TextBlock
        {
            Text = EqCurve.RegionName(index), TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Ox.Eq.ZoneLabel"));
        label.Bind(TextBlock.FontSizeProperty, label.GetResourceObservable("Ox.Eq.LabelFontSize"));
        Child = label;
        ToolTip.SetTip(this, EqCurve.RegionHint(index));
        AutomationProperties.SetName(this, EqCurve.RegionName(index));
        AutomationProperties.SetHelpText(this, EqCurve.RegionHint(index));
    }

    /// <summary>0 for the lowest region, 5 for the highest.</summary>
    public int Index { get; }

    public TextBlock Label => (TextBlock)Child!;
}

/// <summary>The type icons: our own line drawings, stroked in the text colour.</summary>
public static class EqIcons
{
    public static Avalonia.Controls.Shapes.Path Create(EqTypeChoice choice)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(choice.Icon), StrokeThickness = 2, Width = 16, Height = 16, Stretch = Stretch.Uniform,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, path.GetResourceObservable("Ox.Text.Primary"));
        return path;
    }
}
