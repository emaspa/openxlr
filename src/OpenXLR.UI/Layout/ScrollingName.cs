using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenXLR.UI.Layout;

/// <summary>
/// A name in an application tile, clipped to the width the tile gives it. A name that fits stands still. A longer one is clipped at rest
/// and, while <see cref="Scrolls"/> is on (the pointer is over its tile), glides
/// left once to show its end and holds there; when <see cref="Scrolls"/> goes
/// off it glides back to its start. Nothing moves at rest, and the markup puts
/// the whole name in a tooltip.
///
/// The preferred width comes from a skin token. The tile can give the name
/// more or less room. This control clips its child and moves it. Avalonia exposes
/// no reduced-motion preference to follow, so the motion is kept to one slow
/// pass that only the pointer starts.
/// </summary>
public sealed class ScrollingName : Decorator
{
    public static readonly StyledProperty<double> PreferredWidthProperty =
        AvaloniaProperty.Register<ScrollingName, double>(nameof(PreferredWidth), double.PositiveInfinity);

    /// <summary>The room requested for the name before the tile shares spare width.</summary>
    public double PreferredWidth
    {
        get => GetValue(PreferredWidthProperty);
        set => SetValue(PreferredWidthProperty, value);
    }

    public static readonly StyledProperty<bool> ScrollsProperty =
        AvaloniaProperty.Register<ScrollingName, bool>(nameof(Scrolls));

    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<ScrollingName, double>(nameof(Offset));

    /// <summary>How fast the name glides towards its end, in device-independent units a second.</summary>
    internal const double Speed = 40;

    /// <summary>How much faster it glides back to its start.</summary>
    internal const double ReturnFactor = 4;

    /// <summary>How long the pointer rests on the tile before the name starts, so sweeping across tiles moves nothing.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _from, _to;
    private TimeSpan _wait, _duration;

    static ScrollingName()
    {
        AffectsMeasure<ScrollingName>(PreferredWidthProperty);
        AffectsArrange<ScrollingName>(OffsetProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ScrollingName>(true);
    }

    public ScrollingName() => _timer.Tick += (_, _) => Step();

    /// <summary>True while the name should show its end: the pointer is over its tile.</summary>
    public bool Scrolls
    {
        get => GetValue(ScrollsProperty);
        set => SetValue(ScrollsProperty, value);
    }

    /// <summary>How far the name has moved left, from 0 to <see cref="Overflow"/>.</summary>
    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    /// <summary>How much wider the name is than the room it has; 0 when it fits.</summary>
    public double Overflow => Child is { } child ? Math.Max(0, child.DesiredSize.Width - Bounds.Width) : 0;

    /// <summary>True while the name is on its way to its end or back.</summary>
    internal bool Gliding => _timer.IsEnabled;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScrollsProperty) Glide();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        // Keep the full text measurement for scrolling, even when the tile is narrow.
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        double width = double.IsFinite(PreferredWidth) ? PreferredWidth : child.DesiredSize.Width;
        return new Size(Math.Min(width, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            double offset = Math.Clamp(Offset, 0, Math.Max(0, child.DesiredSize.Width - finalSize.Width));
            child.Arrange(new Rect(-offset, 0, Math.Max(child.DesiredSize.Width, finalSize.Width), finalSize.Height));
        }
        return finalSize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        Offset = 0;
    }

    /// <summary>Starts the name towards its end while it scrolls, and back to its start otherwise.</summary>
    private void Glide()
    {
        double overflow = Overflow;
        _from = Offset;
        _to = Scrolls ? overflow : 0;
        double distance = Math.Abs(_to - _from);
        if (distance < 0.5)
        {
            _timer.Stop();
            Offset = _to;
            return;
        }
        double speed = Scrolls ? Speed : Speed * ReturnFactor;
        _duration = TimeSpan.FromSeconds(distance / speed);
        // Only the start waits; a name already moving turns straight round.
        _wait = Scrolls && _from < 0.5 ? Delay : TimeSpan.Zero;
        _clock.Restart();
        _timer.Start();
    }

    private void Step()
    {
        TimeSpan moving = _clock.Elapsed - _wait;
        if (moving <= TimeSpan.Zero) return;
        double t = Math.Min(1, moving / _duration);
        // Eased in and out, so the name starts and stops without a jolt.
        double eased = t * t * (3 - 2 * t);
        Offset = _from + (_to - _from) * eased;
        if (t >= 1) _timer.Stop();
    }
}
