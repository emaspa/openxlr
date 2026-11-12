using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OpenXLR.UI.Layout;

/// <summary>
/// Tiles in columns of at most <see cref="PerColumn"/>, top to bottom, the next tile
/// starting the next column to the right. A column starts at the width of
/// its widest tile. A column that does not fit goes on the next band, whole.
/// Flexible columns use fewer rows when more columns fit across the panel.
/// Their tiles share the remaining width up to the skin's limit.
/// When the columns would not stand side by side, the tiles' names narrow, no
/// further than <see cref="NameFloor"/>, so that as many columns as they can
/// make room for stand beside each other instead of on bands below.
/// </summary>
public sealed class TileColumnsPanel : Panel
{
    public static readonly StyledProperty<int> PerColumnProperty =
        AvaloniaProperty.Register<TileColumnsPanel, int>(nameof(PerColumn), Skinning.SkinFormat.DefaultApplicationsPerColumn,
            coerce: (_, value) => Math.Max(1, value));

    public static readonly StyledProperty<double> MaxTileWidthProperty =
        AvaloniaProperty.Register<TileColumnsPanel, double>(nameof(MaxTileWidth));

    public static readonly StyledProperty<double> BaseTileWidthProperty =
        AvaloniaProperty.Register<TileColumnsPanel, double>(nameof(BaseTileWidth));

    static TileColumnsPanel() => AffectsMeasure<TileColumnsPanel>(PerColumnProperty, BaseTileWidthProperty, MaxTileWidthProperty);

    /// <summary>How many chips one column holds.</summary>
    public int PerColumn
    {
        get => GetValue(PerColumnProperty);
        set => SetValue(PerColumnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Classic mixer tiles have an eight-unit right margin. Measure them
        // at their base width so a long name cannot push other tiles away.
        double width = BaseTileWidth > 0 ? Math.Min(availableSize.Width, BaseTileWidth + 8) : availableSize.Width;
        var constraint = new Size(width, double.PositiveInfinity);
        foreach (Control child in Children) child.Measure(constraint);
        if (Wrap && double.IsFinite(availableSize.Width)) Narrow(availableSize.Width);
        return Place(availableSize.Width, null);
    }

    /// <summary>The narrowest a tile's name gets to make room for another column.</summary>
    public const double NameFloor = 64;

    // Remeasures the tiles at the width that puts the most columns side by
    // side, from all of them down to two, when their names can give up that
    // much. A tile without a scrolling name has nothing to give up.
    private void Narrow(double width)
    {
        List<Control> shown = Shown();
        if (shown.Count <= PerColumn) return;
        double[] slack = [.. shown.Select(child => child.GetVisualDescendants().OfType<ScrollingName>()
            .Sum(name => Math.Max(0, name.DesiredSize.Width - NameFloor)))];
        if (slack.All(s => s <= 0)) return;
        double widest = shown.Max(child => child.DesiredSize.Width);
        for (int across = (shown.Count - 1) / PerColumn + 1; across >= 2; across--)
        {
            double room = Math.Floor(width / across);
            if (widest <= room) return;
            if (shown.Where((child, i) => child.DesiredSize.Width - slack[i] > room).Any()) continue;
            var narrow = new Size(room, double.PositiveInfinity);
            foreach (Control child in shown) child.Measure(narrow);
            return;
        }
    }

    private List<Control> Shown()
    {
        var shown = new List<Control>();
        foreach (Control child in Children)
            if (child.IsVisible && child.DesiredSize.Width > 0 && child.DesiredSize.Height > 0) shown.Add(child);
        return shown;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, finalSize.Width);
        return finalSize;
    }

    /// <summary>The classic tile's base width, or zero to measure its contents at their natural width.</summary>
    public double BaseTileWidth
    {
        get => GetValue(BaseTileWidthProperty);
        set => SetValue(BaseTileWidthProperty, value);
    }

    /// <summary>How far a tile can grow into spare width. Zero keeps its natural width.</summary>
    public double MaxTileWidth
    {
        get => GetValue(MaxTileWidthProperty);
        set => SetValue(MaxTileWidthProperty, value);
    }

    /// <summary>Allow whole columns to wrap. The classic channel row scrolls instead.</summary>
    public bool Wrap { get; set; } = true;

    private readonly record struct Column(int Start, int End, double Width, double Height);

    private readonly record struct Band(int First, int End, double Used, double Height);

    // Group whole columns into bands using their measured sizes. Every column
    // grows by the same amount, the most the fullest band leaves room for, so
    // a short last band keeps the width and the picker places of the bands
    // above it instead of spreading its few tiles across the row.
    private Size Place(double width, double? arrange)
    {
        List<Control> shown = Shown();
        int perColumn = PerColumn;
        if (perColumn > 1 && MaxTileWidth > 0 && shown.Count > 1 && double.IsFinite(width))
        {
            double widestTile = shown.Max(child => child.DesiredSize.Width);
            if (widestTile > 0)
            {
                int across = (int)Math.Clamp(Math.Floor(width / widestTile), 1, shown.Count);
                perColumn = Math.Min(perColumn, (shown.Count - 1) / across + 1);
            }
        }

        var columns = new List<Column>();
        for (int start = 0; start < shown.Count; start += perColumn)
        {
            int end = Math.Min(start + perColumn, shown.Count);
            double columnWidth = 0, columnHeight = 0;
            for (int i = start; i < end; i++)
            {
                columnWidth = Math.Max(columnWidth, shown[i].DesiredSize.Width);
                columnHeight += shown[i].DesiredSize.Height;
            }
            columns.Add(new Column(start, end, columnWidth, columnHeight));
        }

        var bands = new List<Band>();
        double top = 0, widest = 0;
        for (int first = 0; first < columns.Count;)
        {
            int end = first + 1;
            double used = columns[first].Width, height = columns[first].Height;
            while (end < columns.Count && (!Wrap || used + columns[end].Width <= width))
            {
                used += columns[end].Width;
                height = Math.Max(height, columns[end].Height);
                end++;
            }
            bands.Add(new Band(first, end, used, height));
            widest = Math.Max(widest, used);
            top += height;
            first = end;
        }
        if (arrange is not { } room) return new Size(widest, top);

        double extra = MaxTileWidth > 0 && bands.Count > 0
            ? bands.Min(b => Math.Max(0, room - b.Used) / (b.End - b.First)) : 0;
        double y0 = 0;
        foreach (Band band in bands)
        {
            double x = 0;
            for (int col = band.First; col < band.End; col++)
            {
                Column column = columns[col];
                double tileWidth = column.Width + Math.Min(extra, Math.Max(0, MaxTileWidth - column.Width));
                tileWidth = Math.Min(tileWidth, Math.Max(0, room - x));
                double y = y0;
                for (int i = column.Start; i < column.End; i++)
                {
                    double childWidth = MaxTileWidth > 0 ? tileWidth : Math.Min(shown[i].DesiredSize.Width, tileWidth);
                    double childHeight = shown[i].DesiredSize.Height;
                    shown[i].Arrange(new Rect(x, y, childWidth, childHeight));
                    y += childHeight;
                }
                x += tileWidth;
            }
            y0 += band.Height;
        }
        return new Size(widest, top);
    }
}
