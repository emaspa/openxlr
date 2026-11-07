using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI.Layout;

/// <summary>
/// APPLICATIONS' <c>"application-columns"</c> template: the chips in
/// columns of at most <see cref="PerColumn"/>, top to bottom, the next tile
/// starting the next column to the right. Each tile starts with the size it asks
/// for; the tile template gives every tile the same width, its name a fixed
/// width and its channel picker a width shared by all the tiles under this
/// panel, which is their shared size scope. A column starts at the width of
/// its widest tile. A column that does not fit goes on the next band, whole.
/// Flexible columns use fewer rows when more columns fit across the panel.
/// Their tiles share the remaining width up to the skin's limit.
/// </summary>
public sealed class ApplicationColumnsPanel : Panel
{
    public static readonly StyledProperty<int> PerColumnProperty =
        AvaloniaProperty.Register<ApplicationColumnsPanel, int>(nameof(PerColumn), Skinning.SkinFormat.DefaultApplicationsPerColumn,
            coerce: (_, value) => Math.Max(1, value));

    static ApplicationColumnsPanel() => AffectsMeasure<ApplicationColumnsPanel>(PerColumnProperty);

    /// <summary>The tiles' channel pickers share one width across the whole panel.</summary>
    public ApplicationColumnsPanel() => Grid.SetIsSharedSizeScope(this, true);

    /// <summary>How many chips one column holds.</summary>
    public int PerColumn
    {
        get => GetValue(PerColumnProperty);
        set => SetValue(PerColumnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (Control child in Children) child.Measure(constraint);
        return Place(availableSize.Width, null);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, finalSize.Width);
        return finalSize;
    }

    /// <summary>How far a tile can grow into spare width. Zero keeps its natural width.</summary>
    public double MaxTileWidth { get; init; }

    private readonly record struct Column(int Start, int End, double Width, double Height);

    private readonly record struct Band(int First, int End, double Used, double Height);

    // Group whole columns into bands using their measured sizes. Every column
    // grows by the same amount, the most the fullest band leaves room for, so
    // a short last band keeps the width and the picker places of the bands
    // above it instead of spreading its few tiles across the row.
    private Size Place(double width, double? arrange)
    {
        var shown = new List<Control>();
        foreach (Control child in Children)
            if (child.IsVisible) shown.Add(child);
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
            while (end < columns.Count && used + columns[end].Width <= width)
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
