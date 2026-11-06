using System;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Views;

/// <summary>The submixer as a mix matrix. Its data context is a <see cref="MixMatrixViewModel"/>.</summary>
public partial class MixMatrixView : UserControl
{
    private static readonly StyledProperty<double> BaseCellWidthProperty =
        AvaloniaProperty.Register<MixMatrixView, double>("BaseCellWidth");
    private static readonly StyledProperty<double> MaxCellWidthProperty =
        AvaloniaProperty.Register<MixMatrixView, double>("MaxCellWidth");
    private static readonly StyledProperty<double> NameWidthProperty =
        AvaloniaProperty.Register<MixMatrixView, double>("NameWidth");
    private static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<MixMatrixView, int>("ColumnCount");

    private static readonly StyledProperty<bool> FillWidthProperty =
        AvaloniaProperty.Register<MixMatrixView, bool>(nameof(FillWidth));

    static MixMatrixView() => AffectsMeasure<MixMatrixView>(
        BaseCellWidthProperty, MaxCellWidthProperty, NameWidthProperty, ColumnCountProperty, FillWidthProperty);

    internal bool FillWidth { get => GetValue(FillWidthProperty); set => SetValue(FillWidthProperty, value); }

    public MixMatrixView()
    {
        InitializeComponent();
        Bind(BaseCellWidthProperty, new DynamicResourceExtension("Ox.Matrix.CellWidth"));
        Bind(MaxCellWidthProperty, new DynamicResourceExtension("Ox.Matrix.CellMaxWidth"));
        Bind(NameWidthProperty, new DynamicResourceExtension("Ox.Matrix.NameWidth"));
        Bind(ColumnCountProperty, new Binding("Columns.Count"));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double minimum = GetValue(BaseCellWidthProperty);
        // The detached mixer's Fit mode can use all the available width.
        double maximum = FillWidth ? double.PositiveInfinity : GetValue(MaxCellWidthProperty);
        int mixes = GetValue(ColumnCountProperty);
        double width = minimum;
        if (maximum > minimum && double.IsFinite(availableSize.Width))
        {
            // The row head is the only tile whose border lies outside its
            // columns' widths. Leave room for it at the current display scale.
            Thickness border = MatrixCorner.BorderThickness;
            double scale = UseLayoutRounding ? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1 : 1;
            double frame = LayoutHelper.RoundLayoutValue(border.Left, scale)
                + LayoutHelper.RoundLayoutValue(border.Right, scale);
            double names = LayoutHelper.RoundLayoutValueUp(GetValue(NameWidthProperty), scale);
            // The outer gap and the gaps inside the items panel are measured
            // by different stacks, each of which rounds its width up.
            double gap = SkinLayoutDefaults.MatrixSpacing;
            double spacing = mixes == 0 ? 0 : LayoutHelper.RoundLayoutValueUp(gap, scale)
                + LayoutHelper.RoundLayoutValueUp((mixes - 1) * gap, scale);
            double available = availableSize.Width - names - frame - spacing;
            // Divide in display pixels so rounding every cell cannot make
            // the row wider than the viewport at fractional display scales.
            double fitted = Math.Floor(available * scale / (mixes + 1)) / scale;
            width = Math.Clamp(fitted, minimum, maximum);
        }
        if (!Resources.TryGetValue("MatrixCellWidth", out object? previous) || previous is not double old || old != width)
            Resources["MatrixCellWidth"] = width;
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The scroller the matrix moves sideways in when it is wider than its card, for the tests.</summary>
    internal ScrollViewer Scroller => MatrixScroll;

    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { Inserts: { } inserts, InsertsKey: { } key }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, inserts, key);
    }

    private void OnEq(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { Channel.Eq: { } eq }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            EqWindows.Show(window, eq);
    }

    /// <summary>XLR 1's software low cut in the processing flyout: off, 80 Hz, 120 Hz, as in the INPUTS panel.</summary>
    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel row) row.Main.CycleSoftLowCut();
    }

    /// <summary>A capture input's Own clock button, the switch alone: turn it on or off.</summary>
    private void OnToggleOwnClock(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel { ShowsOwnClockButton: true } row)
            row.Channel.OwnClock = !row.Channel.OwnClock;
    }

    /// <summary>An input row's name and binding line: open its binding dialog.</summary>
    private void OnBinding(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixRowViewModel row
            && (row.Channel.IsBindable || row.Channel.CaptureSource is not null))
            _ = InputBindingDialog.OpenFrom((Control)sender!, row.Channel.Id);
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixColumnViewModel column && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, column.Mix.Inserts, $"mix:{column.Mix.Id}");
    }

    private void OnMixEq(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MatrixColumnViewModel { Mix.Eq: { CanOpen: true } eq }
            && TopLevel.GetTopLevel((Control)sender!) is Window window)
            EqWindows.Show(window, eq);
    }
}
