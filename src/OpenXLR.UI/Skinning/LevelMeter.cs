using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenXLR.UI.Skinning;

/// <summary>How a level meter presents its value.</summary>
public enum MeterPresentation
{
    /// <summary>One bar that grows, which is what the window has always drawn.</summary>
    Continuous,
    /// <summary>A ladder of cells that light one after another, as on a console.</summary>
    Segmented,
}

/// <summary>
/// A stereo channel's level, drawn by the application rather than assembled
/// from borders and a scale transform.
///
/// The meter owns its drawing so a skin can choose a presentation that markup
/// cannot express: a continuous bar or a segmented ladder. Both come from the
/// same three colours and the same peak threshold, so a skin picks a shape and
/// tints it rather than supplying any drawing of its own.
///
/// The brushes are the skin service's live indicator brushes, which repaint in
/// place rather than being replaced, so the control listens for a skin change
/// instead of relying on a property change to invalidate it.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    public static readonly StyledProperty<MeterPresentation> PresentationProperty =
        AvaloniaProperty.Register<LevelMeter, MeterPresentation>(nameof(Presentation));

    // Held as a double, like every other number a skin sets: a resource is
    // taken as the type it was written as, and a whole number in a skin file
    // is a JSON double.
    public static readonly StyledProperty<double> SegmentsProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Segments), 16);

    public static readonly StyledProperty<double> SegmentGapProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(SegmentGap), 2);

    public static readonly StyledProperty<double> SegmentWidthProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(SegmentWidth));

    public static readonly StyledProperty<double> GlowProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Glow));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> WarningProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Warning));

    public static readonly StyledProperty<IBrush?> HotProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Hot));

    public static readonly StyledProperty<double> WarningLevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(WarningLevel), 0.7);

    public static readonly StyledProperty<double> HotLevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(HotLevel), 0.9);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<LevelMeter, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<LevelMeter, Orientation>(nameof(Orientation));

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty, PresentationProperty, SegmentsProperty,
            SegmentGapProperty, SegmentWidthProperty, GlowProperty, TrackProperty, FillProperty, WarningProperty, HotProperty,
            WarningLevelProperty, HotLevelProperty, CornerRadiusProperty, OrientationProperty);
        AffectsMeasure<LevelMeter>(PresentationProperty, OrientationProperty);
    }

    /// <summary>
    /// Which way the scale runs: from the left to the right, as the window has
    /// always drawn it, or from the bottom to the top for a meter a strip
    /// template stands up. Both read the same scale with the same zones; only
    /// the axis turns.
    /// </summary>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <summary>0 is silence, 1 is the top of the scale.</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    public MeterPresentation Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    /// <summary>How many cells a segmented meter has.</summary>
    public double Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    /// <summary>The gap between those cells, in pixels.</summary>
    public double SegmentGap { get => GetValue(SegmentGapProperty); set => SetValue(SegmentGapProperty, value); }

    /// <summary>
    /// The width of every cell in pixels, or 0 to share the length between
    /// <see cref="Segments"/> cells. With a width, the meter holds as many
    /// cells as fit its length, like the fixed pitch of a display tube's bar.
    /// </summary>
    public double SegmentWidth { get => GetValue(SegmentWidthProperty); set => SetValue(SegmentWidthProperty, value); }

    /// <summary>
    /// How strongly the lit part glows, 0 for none to 1: a halo in each lit
    /// cell's own colour that bleeds into the gaps around it, as the phosphor
    /// of a display tube does.
    /// </summary>
    public double Glow { get => GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>The colour of the part of the scale between the two thresholds.</summary>
    public IBrush? Warning { get => GetValue(WarningProperty); set => SetValue(WarningProperty, value); }

    /// <summary>The colour of the top of the scale.</summary>
    public IBrush? Hot { get => GetValue(HotProperty); set => SetValue(HotProperty, value); }

    /// <summary>
    /// Where the warning zone starts, on the meter's own 0 to 1 scale. That
    /// scale is RMS dBFS with 0 at -60 and 1 at 0 (OpenXLR.Core MeterReader),
    /// so the default 0.7 is -18 dBFS.
    /// </summary>
    public double WarningLevel { get => GetValue(WarningLevelProperty); set => SetValue(WarningLevelProperty, value); }

    /// <summary>Where the top zone starts on the same scale; the default 0.9 is -6 dBFS.</summary>
    public double HotLevel { get => GetValue(HotLevelProperty); set => SetValue(HotLevelProperty, value); }

    /// <summary>
    /// The colour for one point on the scale. A meter is coloured by position,
    /// not by its reading: the loud end is red whether or not the signal has
    /// reached it, and reaching it never repaints the quiet end.
    /// </summary>
    private IBrush? ZoneAt(double fraction) =>
        fraction > HotLevel ? Hot ?? Fill
            : fraction > WarningLevel ? Warning ?? Fill
            : Fill;

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SkinService.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SkinService.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The length of the scale: the width of a flat meter, the height of an upright one.</summary>
    private double Length(Rect bounds) => Orientation == Orientation.Vertical ? bounds.Height : bounds.Width;

    /// <summary>
    /// The part of the meter between two points along its scale, measured
    /// from the quiet end: from the left of a flat meter, from the bottom of
    /// an upright one. Every presentation draws through this one mapping, so
    /// the two orientations cannot disagree about where a zone starts.
    /// </summary>
    private Rect Along(Rect bounds, double start, double end) => Orientation == Orientation.Vertical
        ? new Rect(0, bounds.Height - end, bounds.Width, end - start)
        : new Rect(start, 0, end - start, bounds.Height);

    /// <summary>The corners of a span: rounded at the quiet end when <paramref name="starts"/>, at the loud end when <paramref name="ends"/>.</summary>
    private CornerRadius Corners(bool starts, bool ends) => Orientation == Orientation.Vertical
        ? new CornerRadius(ends ? CornerRadius.TopLeft : 0, ends ? CornerRadius.TopRight : 0,
            starts ? CornerRadius.BottomRight : 0, starts ? CornerRadius.BottomLeft : 0)
        : new CornerRadius(starts ? CornerRadius.TopLeft : 0, ends ? CornerRadius.TopRight : 0,
            ends ? CornerRadius.BottomRight : 0, starts ? CornerRadius.BottomLeft : 0);

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double level = Math.Clamp(Level, 0, 1);
        double warn = Math.Clamp(WarningLevel, 0, 1);
        double hot = Math.Clamp(Math.Max(HotLevel, warn), 0, 1);

        if (Presentation == MeterPresentation.Continuous)
        {
            Bar(context, bounds, level, warn, hot);
            return;
        }

        double length = Math.Floor(Length(bounds));
        int cells = (int)Math.Clamp(Math.Round(Segments), 2, 64);
        int gap = (int)Math.Round(Math.Clamp(SegmentGap, 0, 8));
        int pitch = (int)Math.Round(Math.Clamp(SegmentWidth, 0, 16));
        if (pitch > 0)
        {
            // Cells of a fixed width with every gap the same: as many as the
            // length holds, the few pixels left over split between the two
            // ends, so no gap is wider than the rest.
            int fit = (int)((length + gap) / (pitch + gap));
            if (fit < 2)
            {
                Bar(context, bounds, level, warn, hot);
                return;
            }
            double inset = Math.Floor((length - fit * pitch - gap * (fit - 1)) / 2.0);
            Ladder(context, bounds, level, fit, pitch, i => inset + i * (pitch + gap));
            return;
        }
        // Every cell is the same whole number of pixels. The pixels that do
        // not divide evenly go to the gaps, at most one more to a gap and
        // spread along the ladder, so the first cell starts at the quiet edge
        // and the last ends at the loud one. Rounding each cell's own edges
        // instead made thin cells alternate between one size and the next,
        // which on a ladder of slim lines read as a ragged scale.
        double cell = Math.Floor((length - gap * (cells - 1)) / cells);
        if (cell < 1)
        {
            // Too narrow to draw a ladder in: one bar reads better than a smear.
            Bar(context, bounds, level, warn, hot);
            return;
        }
        int spare = (int)(length - cell * cells - gap * (cells - 1));
        double Start(int i) => i * (cell + gap) + Math.Floor((double)i * spare / (cells - 1));

        Ladder(context, bounds, level, cells, cell, Start);
    }

    /// <summary>A ladder of <paramref name="cells"/> cells, each <paramref name="cell"/> pixels long from <paramref name="start"/>.</summary>
    private void Ladder(DrawingContext context, Rect bounds, double level, int cells, double cell, Func<int, double> start)
    {
        // A cell lights when the level reaches its far edge, so a silent meter
        // is dark and a meter at the top is full.
        int on = (int)Math.Ceiling(level * cells - 1e-9);
        // A cell's colour is its own place on the scale, so the ladder shows
        // its green, warning and top zones whether or not they are lit.
        if (Glow > 0)
            for (int i = 0; i < on; i++)
                Halo(context, ZoneAt((i + 1.0) / cells), Along(bounds, start(i), start(i) + cell));
        for (int i = 0; i < cells; i++)
        {
            IBrush? brush = i < on ? ZoneAt((i + 1.0) / cells) : Track;
            Draw(context, brush, Along(bounds, start(i), start(i) + cell));
        }
    }

    /// <summary>
    /// The glow round a lit part: one faint layer in its own colour, at most a
    /// pixel and a half wide, drawn under the cells so it never dims a cell
    /// beside it and only tints the gaps rather than filling them. The meter
    /// does not clip, so the halo may reach past its edges.
    /// </summary>
    private void Halo(DrawingContext context, IBrush? brush, Rect rect)
    {
        double glow = Math.Clamp(Glow, 0, 1);
        if (brush is null || glow <= 0) return;
        double spread = 0.5 + glow;
        using (context.PushOpacity(0.3 * glow))
            context.DrawRectangle(brush, null, new RoundedRect(rect.Inflate(spread), spread));
    }

    /// <summary>
    /// The track, and over it one span per zone the bar has reached, each
    /// painted where it sits on the scale. The quiet end keeps its colour
    /// however loud the signal gets.
    ///
    /// The lit part is one bar, so only its own two ends are rounded and the
    /// zones meet on a whole pixel. Rounding every span notched the bar at
    /// each threshold, and a zone edge on a fraction of a pixel left a paler
    /// seam there: with the shipped colours, where all three zones are the
    /// same green, the mixes read as a long bar followed by a short one.
    /// </summary>
    private void Bar(DrawingContext context, Rect bounds, double level, double warn, double hot)
    {
        Draw(context, Track, bounds, CornerRadius);
        double length = Length(bounds);
        double end = length * level;
        double first = Math.Round(length * warn);
        double second = Math.Round(length * hot);
        if (Glow > 0 && end > 0)
        {
            Halo(context, Fill, Along(bounds, 0, Math.Min(end, first)));
            if (end > first) Halo(context, Warning ?? Fill, Along(bounds, first, Math.Min(end, second)));
            if (end > second) Halo(context, Hot ?? Fill, Along(bounds, second, end));
            Draw(context, Track, bounds, CornerRadius);
        }
        DrawSpan(context, bounds, 0, Math.Min(end, first), Fill, end);
        DrawSpan(context, bounds, first, Math.Min(end, second), Warning ?? Fill, end);
        DrawSpan(context, bounds, second, end, Hot ?? Fill, end);
    }

    /// <summary>
    /// Paint the part of the bar between two positions along the scale,
    /// rounded at the quiet end only if it starts the bar and at the loud end
    /// only if it ends it.
    /// </summary>
    private void DrawSpan(DrawingContext context, Rect bounds, double start, double stop, IBrush? brush, double end)
    {
        if (stop <= start) return;
        bool starts = start <= 0, ends = stop >= end - 1e-9;
        Draw(context, brush, Along(bounds, start, stop), Corners(starts, ends));
    }

    private void Draw(DrawingContext context, IBrush? brush, Rect rect) =>
        Draw(context, brush, rect, CornerRadius);

    private static void Draw(DrawingContext context, IBrush? brush, Rect rect, CornerRadius corners)
    {
        if (brush is null || rect.Width <= 0 || rect.Height <= 0) return;
        // A corner cannot take more than half the shape, or the curves of one
        // side meet and eat the straight part between them.
        double limit = Math.Min(rect.Width, rect.Height) / 2;
        CornerRadius radius = new(Math.Min(corners.TopLeft, limit), Math.Min(corners.TopRight, limit),
            Math.Min(corners.BottomRight, limit), Math.Min(corners.BottomLeft, limit));
        if (radius.TopLeft <= 0 && radius.TopRight <= 0 && radius.BottomRight <= 0 && radius.BottomLeft <= 0)
            context.DrawRectangle(brush, null, rect);
        else context.DrawRectangle(brush, null, new RoundedRect(rect, radius));
    }
}
