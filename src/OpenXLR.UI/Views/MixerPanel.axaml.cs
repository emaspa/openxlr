using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Views;

/// <summary>
/// The submixer's panel: the one control Pop out moves between the main
/// window and the mixer window, with whichever presentation the skin chose.
/// The channel tiles are markup of the main window's, handed in through
/// <see cref="Tiles"/> so their names and handlers stay the main window's;
/// the mix banks and the mix matrix are this panel's own. Only one shows at
/// a time, and the banks and the matrix are bound only while they show.
/// </summary>
public partial class MixerPanel : UserControl
{
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<MixerPanel, object?>(nameof(Header));

    public static readonly StyledProperty<object?> TilesProperty =
        AvaloniaProperty.Register<MixerPanel, object?>(nameof(Tiles));

    public MixerPanel()
    {
        InitializeComponent();
    }

    /// <summary>What every presentation shows above it: the placeholder and the compact view's pickers.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <summary>The channel tiles and the mix masters under them, the classic presentation.</summary>
    public object? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    /// <summary>The banks shown now, or null while the channel tiles show.</summary>
    public MixerPresentationViewModel? Banks { get; private set; }

    /// <summary>The matrix shown now, or null while the channel tiles or the banks show.</summary>
    public MixMatrixViewModel? Matrix { get; private set; }

    /// <summary>The list the banks are drawn in, for the tests.</summary>
    internal ItemsControl BankItems => BankList;

    /// <summary>The view the matrix is drawn in, for the tests.</summary>
    internal MixMatrixView MatrixItems => MatrixView;

    internal void SetMatrixFillWidth(bool fill) => MatrixView.FillWidth = fill;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeaderProperty) HeaderPresenter.Content = change.NewValue;
        else if (change.Property == TilesProperty) TilesPresenter.Content = change.NewValue;
    }

    /// <summary>
    /// Show the mix banks of <paramref name="banks"/>, the mix matrix of
    /// <paramref name="matrix"/>, or the channel tiles when both are null.
    /// Nothing in any of them is rebuilt but the banks' or the matrix's own
    /// cells; the tiles and every view model stay as they are.
    /// </summary>
    public void Show(MixerPresentationViewModel? banks, MixMatrixViewModel? matrix = null)
    {
        if (banks is not null) matrix = null;
        if (ReferenceEquals(Banks, banks) && ReferenceEquals(Matrix, matrix)
            && BankList.IsVisible == banks is not null && MatrixView.IsVisible == matrix is not null) return;
        Banks = banks;
        Matrix = matrix;
        TilesPresenter.IsVisible = banks is null && matrix is null;
        BankList.IsVisible = banks is not null;
        BankList.DataContext = banks;
        MatrixView.IsVisible = matrix is not null;
        MatrixView.DataContext = matrix;
    }

    /// <summary>
    /// The narrowest this panel can be drawn without clipping a control, by
    /// the presentation it shows: the wider of a channel tile and a mix tile
    /// for the classic one, the width the banks need, which is reckoned as
    /// the window reckons the submixer region's, or the whole matrix, every
    /// column beside the row heads.
    /// </summary>
    public double MinimumWidth()
    {
        double Token(string key, double fallback) => this.TryFindResource(key, out object? value) && value is double d ? d : fallback;
        double mix = Token("Ox.Mixer.MixWidth", 232);
        if (Matrix is { } matrix)
            return SkinLayoutDefaults.MatrixWidth(new RegionSizes(0, mix, 0, 0, 0,
                Token("Ox.Matrix.CellWidth", 168), Token("Ox.Matrix.NameWidth", 112)), matrix.Columns.Count);
        if (Banks is not { } banks)
            return Math.Max(mix, Token("Ox.Mixer.ChannelWidth", 132));
        var layout = new SkinLayout(SkinFormat.LayoutVersion, SkinLayoutDefaults.Classic.Main) { Submixer = banks.Options };
        var sizes = new RegionSizes(Token("Ox.Mixer.StripWidth", 76), mix, Token("Ox.Mixer.BankSpacing", 12),
            banks.Banks.Count, banks.VisibleChannels);
        return SkinLayoutDefaults.RegionMinWidth(SkinLayoutRegions.Submixer, layout, sizes) - SkinLayoutDefaults.CardPadding;
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BankMasterViewModel master && TopLevel.GetTopLevel((Control)sender!) is Window window)
            InsertWindows.OpenChain(window, master.Mix.Inserts, $"mix:{master.Mix.Id}");
    }

    private async void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        // As in the main window: a plugin running in its own process opens its own editor.
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else if (TopLevel.GetTopLevel((Control)sender!) is Window window) InsertWindows.OpenControls(window, insert);
    }
}
