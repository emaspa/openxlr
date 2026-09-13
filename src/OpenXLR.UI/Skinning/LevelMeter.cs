using System;
using Avalonia;
using Avalonia.Controls;
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

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty, PresentationProperty, SegmentsProperty,
            SegmentGapProperty, TrackProperty, FillProperty, WarningProperty, HotProperty,
            WarningLevelProperty, HotLevelProperty, CornerRadiusProperty);
        AffectsMeasure<LevelMeter>(PresentationProperty);
    }

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

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double level = Math.Clamp(Level, 0, 1);
        double warn = Math.Clamp(WarningLevel, 0, 1);
        double hot = Math.Clamp(Math.Max(HotLevel, warn), 0, 1);

        if (Presentation == MeterPresentation.Continuous)
        {
            Draw(context, Track, bounds);
            // One span per zone the bar has reached, each painted where it sits
            // on the scale. The quiet end keeps its colour however loud it gets.
            DrawSpan(context, bounds, 0, Math.Min(level, warn), Fill);
            DrawSpan(context, bounds, warn, Math.Min(level, hot), Warning ?? Fill);
            DrawSpan(context, bounds, hot, level, Hot ?? Fill);
            return;
        }

        int cells = (int)Math.Clamp(Math.Round(Segments), 2, 64);
        double gap = Math.Clamp(SegmentGap, 0, 8);
        double cell = (bounds.Width - gap * (cells - 1)) / cells;
        if (cell < 1)
        {
            // Too narrow to draw a ladder in: one bar reads better than a smear.
            Draw(context, Track, bounds);
            DrawSpan(context, bounds, 0, Math.Min(level, warn), Fill);
            DrawSpan(context, bounds, warn, Math.Min(level, hot), Warning ?? Fill);
            DrawSpan(context, bounds, hot, level, Hot ?? Fill);
            return;
        }

        // A cell lights when the level reaches its far edge, so a silent meter
        // is dark and a meter at the top is full.
        int on = (int)Math.Ceiling(level * cells - 1e-9);
        // Cells are laid out on whole pixels. Left to the fractional positions
        // the arithmetic gives, the first and last cell came out a fraction
        // narrower than the rest and read as clipped.
        double slot = (bounds.Width + gap) / cells;
        for (int i = 0; i < cells; i++)
        {
            double left = Math.Round(i * slot);
            double right = Math.Min(bounds.Width, Math.Round((i + 1) * slot - gap));
            if (right <= left) right = Math.Min(bounds.Width, left + 1);
            // A cell's colour is its own place on the scale, so the ladder shows
            // its green, warning and top zones whether or not they are lit.
            IBrush? brush = i < on ? ZoneAt((i + 1.0) / cells) : Track;
            Draw(context, brush, new Rect(left, 0, right - left, bounds.Height));
        }
    }

    /// <summary>Paint the part of the bar between two points on the scale.</summary>
    private void DrawSpan(DrawingContext context, Rect bounds, double from, double to, IBrush? brush)
    {
        if (to <= from) return;
        double left = bounds.Width * from;
        Draw(context, brush, new Rect(left, 0, bounds.Width * to - left, bounds.Height));
    }

    private void Draw(DrawingContext context, IBrush? brush, Rect rect)
    {
        if (brush is null || rect.Width <= 0) return;
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(rect.Width, rect.Height) / 2);
        if (radius > 0) context.DrawRectangle(brush, null, rect, radius, radius);
        else context.DrawRectangle(brush, null, rect);
    }
}
