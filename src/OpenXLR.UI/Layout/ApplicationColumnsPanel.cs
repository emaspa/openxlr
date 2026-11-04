using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace OpenXLR.UI.Layout;

/// <summary>
/// APPLICATIONS' <c>"application-columns"</c> template: the chips in
/// columns of at most <see cref="PerColumn"/>, top to bottom, the next tile
/// starting the next column to the right. Each tile keeps the size it asks
/// for; the tile template gives every tile the same width, its name a fixed
/// width and its channel picker a width shared by all the tiles under this
/// panel, which is their shared size scope. A column starts at the width of
/// its widest tile. A column that does not fit goes on the next band, whole.
/// A skin can let columns in each band share the unused width.
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

    // Group whole columns into bands using their measured sizes, then share
    // the extra width in each band when arranging it.
    private Size Place(double width, double? arrange)
    {
        var shown = new List<Control>();
        foreach (Control child in Children)
            if (child.IsVisible) shown.Add(child);
        int perColumn = PerColumn;
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
            if (arrange is { } room)
            {
                double extra = MaxTileWidth > 0 ? Math.Max(0, room - used) / (end - first) : 0;
                double x = 0;
                for (int col = first; col < end; col++)
                {
                    Column column = columns[col];
                    double tileWidth = column.Width + Math.Min(extra, Math.Max(0, MaxTileWidth - column.Width));
                    tileWidth = Math.Min(tileWidth, Math.Max(0, room - x));
                    double y = top;
                    for (int i = column.Start; i < column.End; i++)
                    {
                        double childWidth = MaxTileWidth > 0 ? tileWidth : Math.Min(shown[i].DesiredSize.Width, tileWidth);
                        double childHeight = shown[i].DesiredSize.Height;
                        shown[i].Arrange(new Rect(x, y, childWidth, childHeight));
                        y += childHeight;
                    }
                    x += tileWidth;
                }
            }
            widest = Math.Max(widest, used);
            top += height;
            first = end;
        }
        return new Size(widest, top);
    }
}
