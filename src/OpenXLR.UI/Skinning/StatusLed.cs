using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI.Skinning;

/// <summary>How an indicator lamp presents itself.</summary>
public enum LedPresentation
{
    /// <summary>A flat filled dot, which is what the window has always drawn.</summary>
    Flat,
    /// <summary>A lamp in a bezel with a halo around it when lit, as on a panel.</summary>
    Lamp,
}

/// <summary>What an indicator means when it is not lit.</summary>
public enum LedKind
{
    /// <summary>Not connected: the lamp is simply dark.</summary>
    Connection,
    /// <summary>Bypassed or failed: the lamp shows the alert colour.</summary>
    Insert,
}

/// <summary>
/// The small lamp beside a device, an application or a plugin.
///
/// It was an ellipse filled through a value converter, which no skin could
/// give a bezel or a halo. Drawing it here lets a skin choose a presentation
/// and tint it, and lets the lamp carry its own accessible name instead of
/// being a shape with no meaning.
/// </summary>
public sealed class StatusLed : Control
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<StatusLed, bool>(nameof(IsOn));

    public static readonly StyledProperty<LedKind> KindProperty =
        AvaloniaProperty.Register<StatusLed, LedKind>(nameof(Kind));

    public static readonly StyledProperty<LedPresentation> PresentationProperty =
        AvaloniaProperty.Register<StatusLed, LedPresentation>(nameof(Presentation));

    public static readonly StyledProperty<IBrush?> OnBrushProperty =
        AvaloniaProperty.Register<StatusLed, IBrush?>(nameof(OnBrush));

    public static readonly StyledProperty<IBrush?> OffBrushProperty =
        AvaloniaProperty.Register<StatusLed, IBrush?>(nameof(OffBrush));

    public static readonly StyledProperty<IBrush?> BezelProperty =
        AvaloniaProperty.Register<StatusLed, IBrush?>(nameof(Bezel));

    public static readonly StyledProperty<double> BezelThicknessProperty =
        AvaloniaProperty.Register<StatusLed, double>(nameof(BezelThickness), 1.5);

    public static readonly StyledProperty<double> GlowProperty =
        AvaloniaProperty.Register<StatusLed, double>(nameof(Glow), 0.55);

    public static readonly StyledProperty<double> CoreScaleProperty =
        AvaloniaProperty.Register<StatusLed, double>(nameof(CoreScale), 1);

    static StatusLed()
    {
        AffectsRender<StatusLed>(IsOnProperty, KindProperty, PresentationProperty, OnBrushProperty,
            OffBrushProperty, BezelProperty, BezelThicknessProperty, GlowProperty, CoreScaleProperty);
    }

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    public LedKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public LedPresentation Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public IBrush? OnBrush { get => GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }

    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    /// <summary>The ring around the lamp, drawn by the lamp presentation only.</summary>
    public IBrush? Bezel { get => GetValue(BezelProperty); set => SetValue(BezelProperty, value); }

    public double BezelThickness
    {
        get => GetValue(BezelThicknessProperty);
        set => SetValue(BezelThicknessProperty, value);
    }

    /// <summary>How strong the halo around a lit lamp is, 0 for none.</summary>
    public double Glow { get => GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    /// <summary>
    /// How much of the control's box the lit core fills, leaving the rest for
    /// the bezel and the halo. 1 fills the box, which with no bezel and no halo
    /// is the flat dot.
    /// </summary>
    public double CoreScale { get => GetValue(CoreScaleProperty); set => SetValue(CoreScaleProperty, value); }

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOnProperty || change.Property == KindProperty)
            AutomationProperties.SetHelpText(this, IsOn ? "on" : Kind == LedKind.Insert ? "bypassed" : "off");
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        double diameter = Math.Min(bounds.Width, bounds.Height);
        if (diameter <= 0) return;
        var centre = new Point(bounds.Width / 2, bounds.Height / 2);
        IBrush? lamp = IsOn ? OnBrush : OffBrush;
        if (lamp is null) return;

        if (Presentation == LedPresentation.Flat)
        {
            context.DrawEllipse(lamp, null, centre, diameter / 2, diameter / 2);
            return;
        }

        // Everything the lamp draws stays inside the box the layout gave it.
        // The halo used to be drawn wider than the control, which a rounded
        // card or a clipping row then cut through: a lamp at the left edge of
        // an insert row came out with a slice missing.
        double limit = diameter / 2;
        double core = Math.Clamp(CoreScale, 0.1, 1) * limit;
        // The bezel is stroked outside the core, and half a pixel is left for
        // the antialiased edge so the ring is never shaved by the boundary.
        double bezel = Math.Clamp(BezelThickness, 0, Math.Max(0, limit - core - 0.5));

        double glow = Math.Clamp(Glow, 0, 1);
        if (IsOn && glow > 0 && limit > core)
        {
            // No blur is available in the drawing context, so the halo is two
            // soft rings rather than a gaussian one. It reads the same at this
            // size, and it fills the room between the core and the box edge.
            using (context.PushOpacity(glow * 0.28))
                context.DrawEllipse(lamp, null, centre, limit, limit);
            using (context.PushOpacity(glow * 0.42))
                context.DrawEllipse(lamp, null, centre, (core + limit) / 2, (core + limit) / 2);
        }
        context.DrawEllipse(lamp, bezel > 0 && Bezel is not null ? new Pen(Bezel, bezel) : null,
            centre, core, core);
    }
}
