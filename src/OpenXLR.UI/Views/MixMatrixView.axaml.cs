using System;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    static MixMatrixView() => AffectsMeasure<MixMatrixView>(
        BaseCellWidthProperty, MaxCellWidthProperty, NameWidthProperty, ColumnCountProperty);

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
        double maximum = GetValue(MaxCellWidthProperty);
        int mixes = GetValue(ColumnCountProperty);
        double width = minimum;
        if (maximum > minimum && double.IsFinite(availableSize.Width))
        {
            double available = availableSize.Width - GetValue(NameWidthProperty) - mixes * SkinLayoutDefaults.MatrixSpacing;
            width = Math.Clamp(Math.Floor(available / (mixes + 1)), minimum, maximum);
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
}
