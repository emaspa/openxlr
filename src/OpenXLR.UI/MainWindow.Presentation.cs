using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// The templates a skin's layout picks for INPUTS and the submixer: the
/// hardware panel or an input rack, the channel tiles, mix banks or a mix
/// matrix. The rack, the banks and the matrix are projections over the
/// window's own view models, made while they show and dropped when they
/// stop; switching between templates moves no audio, sends no command and
/// replaces no channel, mix or send.
/// </summary>
public partial class MainWindow
{
    private InputRackViewModel? _rack;
    private MixerPresentationViewModel? _banks;
    private MixMatrixViewModel? _matrix;

    /// <summary>The input rack shown now, or null while INPUTS is the hardware panel.</summary>
    internal InputRackViewModel? InputRackModel => _rack;

    /// <summary>The mix banks shown now, or null while the submixer is the channel tiles or a matrix.</summary>
    internal MixerPresentationViewModel? MixBanks => _banks;

    /// <summary>The mix matrix shown now, or null while the submixer is the channel tiles or banks.</summary>
    internal MixMatrixViewModel? MixMatrix => _matrix;

    /// <summary>
    /// Put on the templates <paramref name="arrangement"/>'s layout picks,
    /// keeping a rack or banks that already belong to the window's model.
    /// </summary>
    private void ApplyTemplates(SectionArrangement arrangement)
    {
        SkinLayout? layout = arrangement.Layout;
        var model = DataContext as MainViewModel;

        bool rack = layout?.Inputs.IsRack == true && model is not null;
        if (_rack is not null && (!rack || !ReferenceEquals(_rack.Main, model)))
        {
            InputRack.DataContext = null;
            _rack.Dispose();
            _rack = null;
        }
        if (rack)
        {
            _rack ??= new InputRackViewModel(model!);
            _rack.Configure(layout!.Inputs.Strip);
            InputRack.DataContext = _rack;
        }
        InputRack.IsVisible = rack;
        HardwarePanel.IsVisible = !rack;

        bool banks = layout?.Submixer.IsBanks == true && model is not null;
        if (_banks is not null && (!banks || !ReferenceEquals(_banks.Main, model)))
        {
            MixerPanel.Show(null);
            _banks.SelectionChosen -= SaveBanks;
            _banks.ShapeChanged -= OnBanksShapeChanged;
            _banks.Dispose();
            _banks = null;
        }
        if (banks)
        {
            if (_banks is null)
            {
                _banks = new MixerPresentationViewModel(model!);
                _banks.SelectionChosen += SaveBanks;
                _banks.ShapeChanged += OnBanksShapeChanged;
            }
            _banks.Configure(layout!.Submixer, UiSettings.Load().SkinBanksFor(arrangement.Skin!, arrangement.Version!.Value));
            // One handle per channel: the rack's while it shows, otherwise the first bank's.
            _banks.OwnsHandles = !rack || arrangement.Hidden.Contains(SkinLayoutRegions.Inputs);
        }

        bool matrix = layout?.Submixer.IsMatrix == true && model is not null;
        if (_matrix is not null && (!matrix || !ReferenceEquals(_matrix.Main, model))) DropMatrix();
        if (matrix)
        {
            if (_matrix is null)
            {
                _matrix = new MixMatrixViewModel(model!);
                _matrix.ShapeChanged += OnBanksShapeChanged;
            }
            _matrix.Configure(layout!.Submixer.Matrix);
            // One handle per channel: the rack's while it shows, otherwise the row head's.
            _matrix.OwnsHandles = !rack || arrangement.Hidden.Contains(SkinLayoutRegions.Inputs);
        }
        MixerPanel.Show(_banks, _matrix);
        _mixerWindow?.FitPresentation();

        // MONITOR and APPLICATIONS: the same controls and the same
        // application objects, in other cells or another item template.
        _monitorTemplate = layout?.Monitor ?? SkinLayoutTemplates.OutputPanel;
        LayOutMonitor(MonitorControls.Bounds.Width > 0 ? MonitorControls.Bounds.Width : double.PositiveInfinity);
        string applications = layout?.Applications ?? SkinLayoutTemplates.ApplicationChips;
        int perColumn = layout?.ApplicationsPerColumn ?? SkinFormat.DefaultApplicationsPerColumn;
        double maxTileWidth = (double)this.FindResource("Ox.AppColumns.TileMaxWidth")!;
        string shape = applications == SkinLayoutTemplates.ApplicationColumns ? $"{applications}:{perColumn}:{maxTileWidth}" : applications;
        if (_applicationsShape != shape)
        {
            _applicationsShape = shape;
            bool list = applications == SkinLayoutTemplates.ApplicationList;
            // Columns hold tiles of one width, so the tiles of a column line up.
            ApplicationRows.ItemsPanel = applications == SkinLayoutTemplates.ApplicationColumns
                ? new FuncTemplate<Panel?>(() =>
                {
                    var panel = new TileColumnsPanel { PerColumn = perColumn, MaxTileWidth = maxTileWidth };
                    Grid.SetIsSharedSizeScope(panel, true);
                    return panel;
                })
                : (ITemplate<Panel?>)this.FindResource(list ? "ApplicationListPanel" : "ApplicationChipsPanel")!;
            ApplicationRows.ItemTemplate = (Avalonia.Controls.Templates.IDataTemplate)this.FindResource(
                list ? "ApplicationLine" : applications == SkinLayoutTemplates.ApplicationColumns ? "ApplicationTile" : "ApplicationChip")!;
        }
    }

    /// <summary>The template APPLICATIONS wears now, with a column's length for <see cref="SkinLayoutTemplates.ApplicationColumns"/>.</summary>
    private string _applicationsShape = SkinLayoutTemplates.ApplicationChips;

    /// <summary>The template MONITOR wears now: one of <see cref="SkinLayoutTemplates.Monitor"/>.</summary>
    internal string MonitorTemplate => _monitorTemplate;

    /// <summary>A pick from a bank's picker, kept for the skin in force.</summary>
    private void SaveBanks(IReadOnlyList<string> banks)
    {
        if (_arrangement is not { Skin: { } skin, Version: { } version }) return;
        string? error = UiSettings.Load().WithSkinBanks(skin, version, banks).Save();
        ReportSaveIfChanged(WindowChoice.Banks, error is null ? null : Localizer.Format("BankChoiceNotSaved", error));
    }

    private void OnBanksShapeChanged()
    {
        _layoutHost.UpdateWidths();
        _mixerWindow?.UpdatePanelWidth();
    }

    /// <summary>
    /// The narrowest a region is drawn where its container gives it a width
    /// of its own, by the template it wears now (see
    /// <see cref="SkinLayoutDefaults.RegionMinWidth"/>).
    /// </summary>
    private double RegionMinWidth(string region)
    {
        double Token(string key, double fallback) => this.TryFindResource(key, out object? value) && value is double d ? d : fallback;
        var sizes = new RegionSizes(Token("Ox.Mixer.StripWidth", 76), Token("Ox.Mixer.MixWidth", 232),
            Token("Ox.Mixer.BankSpacing", 12), _banks?.Banks.Count ?? 1, _banks?.VisibleChannels ?? 0,
            Token("Ox.Matrix.CellWidth", 168), Token("Ox.Matrix.NameWidth", 112), _matrix?.Columns.Count ?? 0);
        return SkinLayoutDefaults.RegionMinWidth(region, _arrangement?.Layout, sizes);
    }

    private void DropMatrix()
    {
        if (_matrix is null) return;
        MixerPanel.Show(_banks);
        _matrix.ShapeChanged -= OnBanksShapeChanged;
        _matrix.Dispose();
        _matrix = null;
    }

    private void DisposePresentations()
    {
        DropMatrix();
        if (_rack is not null) { InputRack.DataContext = null; _rack.Dispose(); _rack = null; }
        if (_banks is not null)
        {
            MixerPanel.Show(null);
            _banks.SelectionChosen -= SaveBanks;
            _banks.ShapeChanged -= OnBanksShapeChanged;
            _banks.Dispose();
            _banks = null;
        }
    }
}
