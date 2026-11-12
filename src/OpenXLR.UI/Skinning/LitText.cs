using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// Lettering that glows: a readout, a channel or mix name, a title. It is a
/// text block in every way a skin or a test sees. A segment display skin can
/// draw the unlit cells behind any text block (<see cref="GhostProperty"/>,
/// from <c>Ox.Text.Ghost</c> and <c>Ox.Value.Ghost</c>); a plain text block
/// takes them as its background. A halo is an effect, though, and an effect
/// wraps the background too, so lettering that glows is a <see cref="LitText"/>:
/// while it has unlit cells, its lit lettering is drawn by a child visual of
/// its own that wears the halo, above the cells the block draws itself.
/// </summary>
public class LitText : TextBlock
{
    /// <summary>The colour of the unlit cells behind a text block's lettering, or null for none.</summary>
    public static readonly AttachedProperty<IBrush?> GhostProperty =
        AvaloniaProperty.RegisterAttached<LitText, TextBlock, IBrush?>("Ghost");

    public static IBrush? GetGhost(TextBlock text) => text.GetValue(GhostProperty);
    public static void SetGhost(TextBlock text, IBrush? value) => text.SetValue(GhostProperty, value);

    static LitText()
    {
        // The halo a style gives lettering moves to the lit layer while the
        // block draws unlit cells, so the cells do not glow.
        EffectProperty.OverrideMetadata<LitText>(new StyledPropertyMetadata<IEffect?>(
            coerce: (o, effect) => ((LitText)o).TakeHalo(effect)));
        GhostProperty.Changed.AddClassHandler<TextBlock>((text, _) => GhostCells.Changed(text));
    }

    private LitLayer? _lit;
    private IEffect? _halo;
    private Point _origin;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>Whether the lit lettering is drawn by its own layer: true while the block has unlit cells.</summary>
    internal bool Layered => _lit is not null;

    private IEffect? TakeHalo(IEffect? effect)
    {
        _halo = effect;
        if (_lit is null) return effect;
        _lit.Effect = effect;
        return null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GhostProperty)
        {
            Layer(GetGhost(this) is not null);
            CoerceValue(EffectProperty);
            InvalidateVisual();
        }
        // Whatever changes the lettering changes what the layer draws.
        _lit?.InvalidateVisual();
    }

    private void Layer(bool on)
    {
        if (on == (_lit is not null)) return;
        if (on)
        {
            _lit = new LitLayer(this) { IsHitTestVisible = false, Effect = _halo };
            VisualChildren.Add(_lit);
        }
        else
        {
            VisualChildren.Remove(_lit!);
            _lit = null;
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        _lit?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        _lit?.Arrange(new Rect(size));
        return size;
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (_lit is null)
        {
            base.RenderTextLayout(context, origin);
            return;
        }
        _origin = origin;
        if (GetGhost(this) is { } ghost) GhostCells.Paint(context, this, ghost, origin);
    }

    private void DrawLit(DrawingContext context) => base.RenderTextLayout(context, _origin);

    /// <summary>The lit lettering, drawn above the cells and wearing the halo.</summary>
    private sealed class LitLayer(LitText owner) : Control
    {
        public override void Render(DrawingContext context) => owner.DrawLit(context);
    }
}

/// <summary>
/// The unlit cells behind a text block's lettering. On a segment face every
/// cell is the same width and one character lights every segment, so the
/// lettering with each of its cells turned into that character lies exactly
/// over the lettering it stands behind, line breaks and all.
/// </summary>
internal static class GhostCells
{
    private static readonly AttachedProperty<bool> PendingProperty =
        AvaloniaProperty.RegisterAttached<LitText, TextBlock, bool>("GhostPending");

    private static readonly AttachedProperty<IBrush?> DrawnProperty =
        AvaloniaProperty.RegisterAttached<LitText, TextBlock, IBrush?>("GhostDrawn");

    // What changes where a plain text block's lettering lands.
    private static readonly HashSet<AvaloniaProperty> Moves =
    [
        Visual.BoundsProperty, TextBlock.TextProperty, TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty,
        TextBlock.FontStyleProperty, TextBlock.FontWeightProperty, TextBlock.FontStretchProperty,
        TextBlock.TextAlignmentProperty, TextBlock.TextWrappingProperty, TextBlock.TextTrimmingProperty,
        TextBlock.PaddingProperty, TextBlock.LineHeightProperty, TextBlock.LineSpacingProperty, TextBlock.LetterSpacingProperty,
        TextBlock.MaxLinesProperty, Visual.FlowDirectionProperty,
    ];

    /// <summary>A text block's ghost colour changed: a plain one draws its cells as its background.</summary>
    internal static void Changed(TextBlock text)
    {
        if (text is LitText or LayoutIconBlock) return;
        if (LitText.GetGhost(text) is null)
        {
            text.PropertyChanged -= OnMoved;
            if (text.GetValue(DrawnProperty) is not null)
            {
                text.ClearValue(TextBlock.BackgroundProperty);
                text.ClearValue(DrawnProperty);
            }
            return;
        }
        text.PropertyChanged -= OnMoved;
        text.PropertyChanged += OnMoved;
        Schedule(text);
    }

    private static void OnMoved(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is TextBlock text && Moves.Contains(e.Property)) Schedule(text);
    }

    // Drawn once the layout has settled, since the cells follow the lettering's layout.
    private static void Schedule(TextBlock text)
    {
        if (text.GetValue(PendingProperty)) return;
        text.SetValue(PendingProperty, true);
        Dispatcher.UIThread.Post(() =>
        {
            text.SetValue(PendingProperty, false);
            Draw(text);
        }, DispatcherPriority.Loaded);
    }

    private static void Draw(TextBlock text)
    {
        // A background the block has of its own is left alone.
        if (text.Background is { } own && !ReferenceEquals(own, text.GetValue(DrawnProperty))) return;
        IBrush? ghost = LitText.GetGhost(text);
        Size size = text.Bounds.Size;
        if (ghost is null || size.Width <= 0 || size.Height <= 0 || string.IsNullOrEmpty(text.Text))
        {
            if (text.GetValue(DrawnProperty) is not null)
            {
                text.ClearValue(TextBlock.BackgroundProperty);
                text.ClearValue(DrawnProperty);
            }
            return;
        }
        // Drawn once into a picture at the screen's scale and laid over the
        // block one to one; a brush that drew the text on every frame cost
        // the renderer whole frames.
        double scale = TopLevel.GetTopLevel(text)?.RenderScaling ?? 1;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        var picture = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        using (DrawingContext context = picture.CreateDrawingContext())
            Paint(context, text, ghost, Origin(text));
        IDisposable? old = (text.GetValue(DrawnProperty) as ImageBrush)?.Source as IDisposable;
        var brush = new ImageBrush(picture)
        {
            Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, pixels.Width / scale, pixels.Height / scale, RelativeUnit.Absolute),
        };
        text.SetValue(DrawnProperty, brush);
        text.Background = brush;
        // The picture it replaces goes once the renderer has moved on to the new one.
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }

    // Where the text block puts its lettering: inside its padding, and
    // aligned within its height when the lettering is taller than the block.
    private static Point Origin(TextBlock text)
    {
        double top = text.Padding.Top;
        double height = text.TextLayout.Height;
        if (text.Bounds.Height < height)
            top += text.VerticalAlignment switch
            {
                VerticalAlignment.Center => (text.Bounds.Height - height) / 2,
                VerticalAlignment.Bottom => text.Bounds.Height - height,
                _ => 0,
            };
        return new Point(text.Padding.Left, top);
    }

    /// <summary>
    /// Draws the cells behind a text block's lettering, its lettering's top
    /// left at <paramref name="origin"/>. Each character the face draws as a
    /// full cell becomes the face's every-segment character (<c>~</c> on a
    /// fourteen-segment face, <c>8</c> on a seven-segment one). Points and
    /// colons keep their own narrower glyphs, and a character from a fallback
    /// face keeps its room unpainted. Lettering that wraps is drawn line by
    /// line where the block's own lines fall: a space becomes a cell too, so
    /// the cells laid out as one text would have no word to break at.
    /// </summary>
    internal static void Paint(DrawingContext context, TextBlock text, IBrush ghost, Point origin)
    {
        string? shown = text.Text;
        if (string.IsNullOrEmpty(shown)) return;
        if (text is Avalonia.Controls.Primitives.AccessText) shown = shown.Replace("__", "\u0001").Replace("_", "").Replace('\u0001', '_');
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        if (!FontManager.Current.TryGetGlyphTypeface(typeface, out GlyphTypeface? face) || face is null) return;
        char full = Glyph(face, '~') is not null ? '~' : '8';
        if (Glyph(face, full) is not { } fullGlyph || !face.TryGetHorizontalGlyphAdvance(fullGlyph, out ushort cell)) return;
        TextLayout lit = text.TextLayout;

        if (text.TextWrapping == TextWrapping.NoWrap || lit.TextLines.Count < 2)
        {
            using TextLayout whole = Cells(text, typeface, face, full, cell, shown, ghost, text.TextAlignment,
                text.TextWrapping, text.TextTrimming, lit.MaxWidth, lit.MaxHeight);
            whole.Draw(context, origin);
            return;
        }
        foreach (TextLine line in lit.TextLines)
        {
            int first = line.FirstTextSourceIndex;
            int length = Math.Min(line.Length - line.TrailingWhitespaceLength, shown.Length - first);
            if (first >= shown.Length || length <= 0) continue;
            Rect at = lit.HitTestTextPosition(first);
            using TextLayout row = Cells(text, typeface, face, full, cell, shown.Substring(first, length), ghost,
                TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, double.PositiveInfinity, double.PositiveInfinity);
            row.Draw(context, origin + new Vector(at.X, at.Y));
        }
    }

    // The lettering with each full cell turned into the every-segment character.
    private static TextLayout Cells(TextBlock text, Typeface typeface, GlyphTypeface face, char full, ushort cell,
        string shown, IBrush ghost, TextAlignment alignment, TextWrapping wrapping, TextTrimming trimming,
        double maxWidth, double maxHeight)
    {
        char[] cells = shown.ToCharArray();
        var hidden = new List<ValueSpan<TextRunProperties>>();
        for (int i = 0; i < cells.Length; i++)
        {
            char c = cells[i];
            if (c is '.' or ':' or '\n' or '\r') continue;
            if (Glyph(face, c) is { } glyph && face.TryGetHorizontalGlyphAdvance(glyph, out ushort advance) && advance == cell)
                cells[i] = full;
            else
                hidden.Add(new ValueSpan<TextRunProperties>(i, 1,
                    new GenericTextRunProperties(typeface, fontRenderingEmSize: text.FontSize, foregroundBrush: Brushes.Transparent)));
        }
        return new TextLayout(new string(cells), typeface, text.FontSize, ghost, alignment, wrapping,
            trimming, null, text.FlowDirection, maxWidth, maxHeight, text.LineHeight, text.LetterSpacing,
            text.MaxLines, text.FontFeatures, hidden.Count > 0 ? hidden : null);
    }

    private static ushort? Glyph(GlyphTypeface face, char c) =>
        face.CharacterToGlyphMap.TryGetGlyph(c, out ushort glyph) && glyph != 0 ? glyph : null;
}
