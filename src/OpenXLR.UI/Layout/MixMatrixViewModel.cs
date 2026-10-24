using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// The submixer as a matrix: a read-only projection over
/// <see cref="MainViewModel.Channels"/> and <see cref="MainViewModel.Mixes"/>
/// that a skin's <c>mix-matrix</c> template draws. A row stands for a
/// channel, a column for a mix, and the cell where they cross holds the
/// channel's own <see cref="SendViewModel"/> into that mix. Nothing here holds
/// a level or a mute of its own: a cell hands out the existing send, a column
/// the existing mix, a row head the window's own hardware properties, and
/// every write goes through their setters.
///
/// The rows are the channels the view shows and the columns the mixes it
/// shows (<see cref="MainViewModel"/>'s DisplayVisible), both in the mixer's
/// order, so the compact view has one row and the mini view one row and one
/// column. There is nothing to pick and nothing to save: the matrix always
/// shows every mix the view shows, and it never changes a collection it
/// projects.
/// </summary>
public sealed class MixMatrixViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _main;
    private readonly HashSet<MixViewModel> _watchedMixes = [];
    private readonly HashSet<ChannelViewModel> _watchedChannels = [];
    private readonly HashSet<MonitorOutputItem> _watchedOutputs = [];
    private SkinMatrixOptions _options = SkinMatrixOptions.Default;
    private bool _ownsHandles = true;
    private bool _disposed;

    public MixMatrixViewModel(MainViewModel main)
    {
        _main = main;
        _main.Mixes.CollectionChanged += OnMixesChanged;
        _main.Channels.CollectionChanged += OnChannelsChanged;
        _main.MonitorOutputs.CollectionChanged += OnOutputsChanged;
        _main.PropertyChanged += OnMainChanged;
        WatchMixes();
        WatchChannels();
        WatchOutputs();
        CellSpec = BuildCellSpec(_options);
        Sync();
    }

    /// <summary>The window's model, whose collections this projects.</summary>
    public MainViewModel Main => _main;

    /// <summary>One row per channel the view shows, in the mixer's order.</summary>
    public ObservableCollection<MatrixRowViewModel> Rows { get; } = [];

    /// <summary>One column per mix the view shows, in the mixer's order: the same <see cref="MixViewModel"/> objects.</summary>
    public ObservableCollection<MatrixColumnViewModel> Columns { get; } = [];

    /// <summary>The skin's options in force.</summary>
    public SkinMatrixOptions Options => _options;

    /// <summary>How every cell is drawn: the cell's parts in a row, the fader taking what the others leave.</summary>
    public StripSpec CellSpec { get; private set; }

    /// <summary>True when every fader in a row draws the channel's meter as its track.</summary>
    public bool MeterUnderFader => _options.Meter == SkinMatrixMeter.UnderFader;

    /// <summary>
    /// True when the row heads carry the channels' Arrange handles; false
    /// while an input rack shows, which carries them instead. A channel has
    /// one handle in the window.
    /// </summary>
    public bool OwnsHandles
    {
        get => _ownsHandles;
        set
        {
            if (_ownsHandles == value) return;
            _ownsHandles = value;
            foreach (MatrixRowViewModel row in Rows) row.RefreshHandle();
        }
    }

    /// <summary>Raised when the rows or columns the matrix shows change, which changes the width it needs.</summary>
    public event Action? ShapeChanged;

    /// <summary>
    /// Take a skin's matrix options. Options that draw alike leave the matrix
    /// as it is; others build its rows, columns and cells again, which hand
    /// out the same channels, mixes and sends.
    /// </summary>
    public void Configure(SkinMatrixOptions options)
    {
        if (options.Description() == _options.Description()) return;
        _options = options;
        CellSpec = BuildCellSpec(options);
        Raise(nameof(Options));
        Raise(nameof(CellSpec));
        Raise(nameof(MeterUnderFader));
        foreach (MatrixRowViewModel row in Rows) row.Detach();
        Rows.Clear();
        foreach (MatrixColumnViewModel column in Columns) column.Detach();
        Columns.Clear();
        Sync();
    }

    // A value drawn over the fader is not a part beside it.
    private static StripSpec BuildCellSpec(SkinMatrixOptions options) => new(
        [.. options.Cell.Shown.Where(p => options.Value == SkinMatrixValue.Beside || p != SkinStripParts.Value)], Orientation.Horizontal, null,
        SkinLabelStyle.Text, Orientation.Horizontal, false, Orientation.Horizontal);

    /// <summary>The parts a row head can draw for a channel of this kind, its name included.</summary>
    public static IReadOnlyCollection<string> RowPartsOf(InputStripKind kind) => kind switch
    {
        InputStripKind.Xlr1 or InputStripKind.Xlr2 => SkinMatrixParts.RowHead,
        InputStripKind.AuxIn => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain],
        // A capture channel's gain part holds its channel volume, under the
        // interface's gain while it carries the interface's microphone, which
        // also brings the interface's mute and switches. Its source and its
        // chain follow the binding and effects parts; see Available and ShowsBinding.
        InputStripKind.Capture => SkinMatrixParts.RowHead,
        // An application channel's gain part holds its channel volume alone.
        _ => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain],
    };

    /// <summary>
    /// True while the matrix draws a row for the channel: the view shows it,
    /// and XLR 2 needs an interface with two inputs or a source of its own.
    /// </summary>
    internal bool ShowsRow(ChannelViewModel channel) =>
        channel.DisplayVisible && (InputRackViewModel.KindOf(channel) != InputStripKind.Xlr2 || _main.HasXlr2 || channel.IsSourceBound);

    /// <summary>
    /// How many of the window's monitor outputs hear this mix now: the ones
    /// ticked in MONITOR whose feed is the mix, alone or summed with another,
    /// and whose device is there.
    /// </summary>
    internal int OutputsOf(MixViewModel mix) => _main.MonitorOutputs.Count(o =>
        o.IsSelected && o.IsPresent && o.Feed is { } feed && feed.Id.Split('+').Contains(mix.Id, StringComparer.Ordinal));

    private void OnMixesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchMixes();
        Sync();
    }

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchChannels();
        Sync();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasXlr2)) Sync();
    }

    private void OnMixChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MixViewModel.DisplayVisible)) Sync();
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.DisplayVisible) or nameof(ChannelViewModel.IsHardware)
            or nameof(ChannelViewModel.CaptureSource) or nameof(ChannelViewModel.IsSourceBound)) Sync();
    }

    private void OnOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchOutputs();
        RefreshOutputs();
    }

    private void OnOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorOutputItem.IsSelected) or nameof(MonitorOutputItem.Feed)
            or nameof(MonitorOutputItem.IsPresent)) RefreshOutputs();
    }

    private void RefreshOutputs()
    {
        foreach (MatrixColumnViewModel column in Columns) column.RefreshOutputs();
    }

    private void WatchMixes()
    {
        foreach (MixViewModel mix in _watchedMixes.Where(m => !_main.Mixes.Contains(m)).ToList())
        {
            mix.PropertyChanged -= OnMixChanged;
            _watchedMixes.Remove(mix);
        }
        foreach (MixViewModel mix in _main.Mixes)
            if (_watchedMixes.Add(mix)) mix.PropertyChanged += OnMixChanged;
    }

    private void WatchChannels()
    {
        foreach (ChannelViewModel channel in _watchedChannels.Where(c => !_main.Channels.Contains(c)).ToList())
        {
            channel.PropertyChanged -= OnChannelChanged;
            _watchedChannels.Remove(channel);
        }
        foreach (ChannelViewModel channel in _main.Channels)
            if (_watchedChannels.Add(channel)) channel.PropertyChanged += OnChannelChanged;
    }

    private void WatchOutputs()
    {
        foreach (MonitorOutputItem output in _watchedOutputs.Where(o => !_main.MonitorOutputs.Contains(o)).ToList())
        {
            output.PropertyChanged -= OnOutputChanged;
            _watchedOutputs.Remove(output);
        }
        foreach (MonitorOutputItem output in _main.MonitorOutputs)
            if (_watchedOutputs.Add(output)) output.PropertyChanged += OnOutputChanged;
    }

    /// <summary>
    /// Bring the columns, the rows and every row's cells in line with what
    /// the view shows. A mix or channel that stays keeps its column or row,
    /// moved to its new place; a row whose channel became another kind of
    /// channel is made again, since its head draws other controls.
    /// </summary>
    private void Sync()
    {
        if (_disposed) return;
        int columns = Columns.Count, rows = Rows.Count;
        MixViewModel[] mixes = [.. _main.Mixes.Where(m => m.DisplayVisible)];
        for (int i = Columns.Count - 1; i >= 0; i--)
            if (!mixes.Contains(Columns[i].Mix))
            {
                Columns[i].Detach();
                Columns.RemoveAt(i);
            }
        for (int i = 0; i < mixes.Length; i++)
        {
            int at = IndexOf(Columns, c => ReferenceEquals(c.Mix, mixes[i]));
            if (at == i) continue;
            if (at >= 0) Columns.Move(at, i);
            else Columns.Insert(i, new MatrixColumnViewModel(this, mixes[i]));
        }

        ChannelViewModel[] channels = [.. _main.Channels.Where(ShowsRow)];
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!channels.Contains(Rows[i].Channel) || Rows[i].Kind != InputRackViewModel.KindOf(Rows[i].Channel))
            {
                Rows[i].Detach();
                Rows.RemoveAt(i);
            }
        for (int i = 0; i < channels.Length; i++)
        {
            int at = IndexOf(Rows, r => ReferenceEquals(r.Channel, channels[i]));
            if (at == i) continue;
            if (at >= 0) Rows.Move(at, i);
            else Rows.Insert(i, new MatrixRowViewModel(this, channels[i]));
        }
        foreach (MatrixRowViewModel row in Rows) row.SyncCells(Columns);
        if (columns != Columns.Count || rows != Rows.Count) ShapeChanged?.Invoke();
    }

    private static int IndexOf<T>(IList<T> list, Func<T, bool> match)
    {
        for (int i = 0; i < list.Count; i++)
            if (match(list[i])) return i;
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.Mixes.CollectionChanged -= OnMixesChanged;
        _main.Channels.CollectionChanged -= OnChannelsChanged;
        _main.MonitorOutputs.CollectionChanged -= OnOutputsChanged;
        _main.PropertyChanged -= OnMainChanged;
        foreach (MixViewModel mix in _watchedMixes) mix.PropertyChanged -= OnMixChanged;
        foreach (ChannelViewModel channel in _watchedChannels) channel.PropertyChanged -= OnChannelChanged;
        foreach (MonitorOutputItem output in _watchedOutputs) output.PropertyChanged -= OnOutputChanged;
        _watchedMixes.Clear();
        _watchedChannels.Clear();
        _watchedOutputs.Clear();
        foreach (MatrixRowViewModel row in Rows) row.Detach();
        Rows.Clear();
        foreach (MatrixColumnViewModel column in Columns) column.Detach();
        Columns.Clear();
    }
}

/// <summary>
/// One mix's column head: the mix itself, the same object the mixer holds,
/// so its master level, mute and plugin chain are bound to it, and the
/// outputs line under its name.
/// </summary>
public sealed class MatrixColumnViewModel : ViewModelBase
{
    private static readonly string[] TextParts = [SkinStripParts.Identity, SkinMatrixParts.Outputs];
    private static readonly string[] ControlParts = [SkinMatrixParts.Master, SkinStripParts.Mute, SkinStripParts.Effects];
    private bool _detached;

    internal MatrixColumnViewModel(MixMatrixViewModel matrix, MixViewModel mix)
    {
        Matrix = matrix;
        Mix = mix;
        SkinMatrixPartOptions head = matrix.Options.ColumnHead;
        TextSpec = new StripSpec([.. head.Shown.Where(TextParts.Contains)], Orientation.Vertical, null, head.Label,
            Orientation.Horizontal, false, Orientation.Horizontal);
        ControlSpec = new StripSpec([.. head.Shown.Where(ControlParts.Contains)], Orientation.Horizontal, null, head.Label,
            Orientation.Horizontal, false, Orientation.Horizontal);
        mix.PropertyChanged += OnMixChanged;
        mix.Appearance.PropertyChanged += OnAppearanceChanged;
    }

    public MixMatrixViewModel Matrix { get; }

    /// <summary>The mix, the same object the mixer holds.</summary>
    public MixViewModel Mix { get; }

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Mix.Appearance;

    /// <summary>The head's lines: the name and the outputs line, in the skin's order.</summary>
    public StripSpec TextSpec { get; }

    /// <summary>The head's one row of controls: the master, the mute and the chain button, in the skin's order.</summary>
    public StripSpec ControlSpec { get; }

    /// <summary>The controls row goes above the lines when the skin orders a control first, under them otherwise.</summary>
    public Dock ControlsDock =>
        Matrix.Options.ColumnHead.Shown.FirstOrDefault() is { } first && ControlParts.Contains(first) ? Dock.Top : Dock.Bottom;

    /// <summary>True when the controls row has anything in it.</summary>
    public bool HasControls => ControlSpec.Parts.Count > 0;

    /// <summary>True when this column head shows no Arrange handle: Aux keeps its place.</summary>
    public bool HandleHidden => !Mix.IsEditable;

    public bool ShowsIcon => TextSpec.WantsIcon && Mix.Appearance.HasIcon;

    public bool ShowsName => TextSpec.WantsName || !Mix.Appearance.HasIcon;

    /// <summary>How many monitor outputs hear this mix now.</summary>
    public int Outputs => Matrix.OutputsOf(Mix);

    /// <summary>
    /// The line under the name: how many monitor outputs hear the mix, or,
    /// when none does, what the mix is for: a monitor mix with no output, a
    /// virtual microphone, or the USB Aux port.
    /// </summary>
    public string OutputsText => Outputs switch
    {
        1 => Localizer.Text("MatrixOneOutput"),
        > 1 => Localizer.Format("MatrixOutputs", Outputs),
        _ when Mix.IsMonitor => Localizer.Text("MatrixNoOutput"),
        _ when Mix.Kind == "auxPort" || Mix.IsAuxPort => Localizer.Text("UsbAuxPortKind"),
        _ when Mix.Kind == "virtualMic" => Localizer.Text("VirtualMicrophone"),
        _ => Mix.KindLabel,
    };

    public string MasterName => Localizer.Format("MixFaderName", Mix.Name);

    public string MuteName => Localizer.Format("MixMuteName", Mix.Name);

    internal void RefreshOutputs()
    {
        Raise(nameof(Outputs));
        Raise(nameof(OutputsText));
    }

    private void OnMixChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MixViewModel.Name):
                Raise(nameof(MasterName));
                Raise(nameof(MuteName));
                foreach (MatrixRowViewModel row in Matrix.Rows) row.RefreshNames(this);
                break;
            case nameof(MixViewModel.Kind) or nameof(MixViewModel.IsEditable):
                Raise(nameof(HandleHidden));
                RefreshOutputs();
                break;
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ShowsIcon));
        Raise(nameof(ShowsName));
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Mix.PropertyChanged -= OnMixChanged;
        Mix.Appearance.PropertyChanged -= OnAppearanceChanged;
    }
}

/// <summary>
/// One channel's row: its head, which stands for the controls the INPUTS
/// panel has always shown for the channel, bound to the same
/// <see cref="MainViewModel"/> properties under the same capability, lock and
/// phantom settling rules, and one cell per column.
/// </summary>
public sealed class MatrixRowViewModel : ViewModelBase
{
    private bool _detached;

    internal MatrixRowViewModel(MixMatrixViewModel matrix, ChannelViewModel channel)
    {
        Matrix = matrix;
        Channel = channel;
        Kind = InputRackViewModel.KindOf(channel);
        Spec = CreateSpec();
        channel.PropertyChanged += OnChannelChanged;
        channel.Sends.CollectionChanged += OnSendsChanged;
        channel.Appearance.PropertyChanged += OnAppearanceChanged;
        // Aux In's level comes and goes with the interface's stage, and an
        // input's switches with the interface's capabilities and XLR 1's
        // software low cut and ClipGuard.
        if (Kind != InputStripKind.Application) matrix.Main.PropertyChanged += OnMainChanged;
        WatchUnits();
    }

    public MixMatrixViewModel Matrix { get; }

    public ChannelViewModel Channel { get; }

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Channel.Appearance;

    public InputStripKind Kind { get; }

    /// <summary>The window's model: the head's hardware controls bind to its properties, exactly as the INPUTS panel does.</summary>
    public MainViewModel Main => Matrix.Main;

    /// <summary>The head's source controls beside the name, in the skin's order, less what this kind of channel does not have.</summary>
    public StripSpec Spec { get; private set; }

    private StripSpec CreateSpec()
    {
        SkinMatrixPartOptions head = Matrix.Options.RowHead;
        IReadOnlyCollection<string> available = MixMatrixViewModel.RowPartsOf(Kind);
        // The binding and the switches stand in the name column, not among the source controls.
        string[] parts = [.. head.Shown.Where(p => p != SkinStripParts.Identity && p != SkinStripParts.Binding
            && p != SkinMatrixParts.Processing && available.Contains(p) && Available(p))];
        // Without hardware gain the standalone meter remains visible, even
        // when the skin normally draws it under the gain fader.
        if (Matrix.MeterUnderFader && parts.Contains(SkinStripParts.Gain)) parts = [.. parts.Where(p => p != SkinStripParts.Meter)];
        return new StripSpec(parts, Orientation.Horizontal, null, head.Label, Orientation.Horizontal, false, Orientation.Horizontal);
    }

    /// <summary>
    /// Whether this channel has the part now. The gain, the mute and the
    /// switches belong to the interface's input: an XLR row bound to another
    /// source has none of them, and a capture channel carrying the
    /// interface's microphone has them all. The switches also need an
    /// interface that has one, and XLR 1 keeps the part for its software low
    /// cut and ClipGuard, whatever feeds it, as the INPUTS panel does.
    /// The gain part also holds the channel's own volume, which every channel
    /// has once the daemon reports one, so a row keeps the part for its
    /// volume when it has no gain.
    /// </summary>
    private bool Available(string part) => (Kind, part) switch
    {
        (InputStripKind.Xlr1 or InputStripKind.Xlr2 or InputStripKind.Capture, SkinStripParts.Mute) => CarriesUnit || CarriesExtraUnit,
        (InputStripKind.Xlr1 or InputStripKind.Xlr2 or InputStripKind.Capture, SkinMatrixParts.Processing) =>
            Unit is { HasSwitches: true } || ExtraUnit is { HasSwitches: true } || ShowsSoftware,
        (InputStripKind.AuxIn, SkinStripParts.Gain) => Main.CapAuxInput || Channel.HasVolume,
        (_, SkinStripParts.Gain) => CarriesUnit || CarriesExtraUnit || Channel.HasVolume,
        (InputStripKind.Capture, SkinStripParts.Effects) => Channel.HasInserts && CapturePartOrdered(part),
        _ => true,
    };

    private bool CapturePartOrdered(string part) => Matrix.Options.RowHead.ExplicitOrder is not { } order || order.Contains(part);

    internal void RefreshSpec()
    {
        Spec = CreateSpec();
        Raise(nameof(Spec));
        Raise(nameof(HasGainFader));
        Raise(nameof(ShowsVolume));
        Raise(nameof(VolumeShowsMeter));
        Raise(nameof(Stacked));
        Raise(nameof(ShowsReadout));
        Raise(nameof(Tall));
        RaiseReadout();
    }

    private void RaiseReadout()
    {
        Raise(nameof(Readout));
        Raise(nameof(ReadoutName));
    }

    /// <summary>The interface's input this row carries, whose gain and mute its head draws, or null.</summary>
    public UnitInputViewModel? Unit => Channel.ShownUnitInput is 1 or 2 ? Main.UnitInputOf(Channel.ShownUnitInput) : null;

    /// <summary>The head draws the interface's gain and mute.</summary>
    public bool CarriesUnit => Unit is not null;

    /// <summary>An additional interface's input this row carries, whose gain and mute its head draws, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit => Channel.ExtraUnitInput;

    /// <summary>The head draws an additional interface's gain and mute.</summary>
    public bool CarriesExtraUnit => Channel.CarriesExtraUnit;

    /// <summary>
    /// The head draws the processing button, at the end of the name column:
    /// the skin shows the part and the input the row carries has a switch, or
    /// the row is XLR 1 with its software low cut or ClipGuard.
    /// </summary>
    public bool ShowsProcessing => Matrix.Options.RowHead.Shows(SkinMatrixParts.Processing)
        && MixMatrixViewModel.RowPartsOf(Kind).Contains(SkinMatrixParts.Processing) && Available(SkinMatrixParts.Processing);

    /// <summary>The processing part holds XLR 1's software low cut or ClipGuard: the row is XLR 1 and one of them applies.</summary>
    public bool ShowsSoftware => Kind == InputStripKind.Xlr1 && Main.ShowSoftwareControls;

    /// <summary>
    /// The processing button is lit: a switch the input's interface has is
    /// on, or XLR 1's software low cut or ClipGuard is.
    /// </summary>
    public bool ProcessingOn => Unit is { SwitchesOn: true } || ExtraUnit is { SwitchesOn: true }
        || ShowsSoftware && (Main.ShowSoftLowCut && Main.SoftLowCutOn || Main.ShowSoftClipGuard && Main.SoftClipGuard);

    /// <summary>The processing button says that phantom power is on, or settling, without being opened.</summary>
    public bool PhantomOn => Unit is { PhantomShown: true } || ExtraUnit is { PhantomShown: true };

    /// <summary>The input's interface has phantom power, so the processing button carries its lamp.</summary>
    public bool HasPhantom => Unit is { CapPhantom: true } || ExtraUnit is { HasPhantom: true };

    /// <summary>The processing button's name for a screen reader, with the channel's name and phantom power when it is on.</summary>
    public string ProcessingName => PhantomOn
        ? Localizer.Format("MatrixProcessingPhantomName", Channel.Name)
        : Localizer.Format("MatrixProcessingName", Channel.Name);

    /// <summary>
    /// The head's gain part draws a gain: the interface's while the row
    /// carries its input, an additional interface's, or Aux In's level where
    /// the interface has the stage.
    /// </summary>
    public bool HasGainFader => CarriesUnit || CarriesExtraUnit || Kind == InputStripKind.AuxIn && Main.CapAuxInput;

    /// <summary>The head's gain part draws the channel's own volume: the daemon reports one.</summary>
    public bool ShowsVolume => Channel.HasVolume;

    /// <summary>
    /// The channel's meter runs under the volume fader: the skin puts meters
    /// under faders and no gain fader above already carries it.
    /// </summary>
    public bool VolumeShowsMeter => MeterUnderFader && !HasGainFader;

    /// <summary>
    /// The head stacks a gain over the channel's volume, two faders in one
    /// part, the gain first as the signal meets it. The row is then as tall as
    /// the two need and at least the skin's row height, and its cells follow;
    /// every other row keeps the row height. A row with one of the two keeps
    /// one fader where it always was.
    /// </summary>
    public bool Stacked => HasGainFader && ShowsVolume && Spec.Parts.Contains(SkinStripParts.Gain, StringComparer.Ordinal);

    /// <summary>
    /// The effects part draws the microphone's EQ button, over the chain's:
    /// the daemon reports an EQ for the channel and the mixer is there.
    /// </summary>
    public bool ShowsEq => Channel.HasEq && Main.HasMixer;

    /// <summary>
    /// The head stacks two controls in one part, the gain over the volume or
    /// the EQ over the chain, so the row is as tall as the two need and at
    /// least the skin's row height, and its cells follow.
    /// </summary>
    public bool Tall => Stacked || ShowsReadout || ShowsEq && Spec.Parts.Contains(SkinStripParts.Effects, StringComparer.Ordinal);

    /// <summary>
    /// The name column draws a readout under the processing button: the row
    /// carries an interface's input and the head draws its gain. The row is
    /// then tall, so the button and the readout fit at every control size.
    /// </summary>
    public bool ShowsReadout => (CarriesUnit || CarriesExtraUnit) && Spec.Parts.Contains(SkinStripParts.Gain, StringComparer.Ordinal);

    private bool _volumeAdjusting;
    /// <summary>True while the user is moving the row's volume fader; the fader writes it.</summary>
    public bool VolumeAdjusting
    {
        get => _volumeAdjusting;
        set
        {
            if (!Set(ref _volumeAdjusting, value)) return;
            Raise(nameof(Readout));
            Raise(nameof(ReadoutName));
        }
    }

    /// <summary>The readout's text: the input's gain, or the channel's volume while its fader moves.</summary>
    public string Readout => _volumeAdjusting ? Channel.VolumeText : Unit?.GainText ?? ExtraUnit?.GainText ?? "";

    /// <summary>What the readout shows, for a screen reader and its tooltip.</summary>
    public string ReadoutName => _volumeAdjusting
        ? Localizer.Format("ChannelVolumeName", Channel.Name)
        : Unit?.GainName ?? ExtraUnit?.GainName ?? "";

    /// <summary>The head draws the input's binding under the name, unless the skin hides it.</summary>
    public bool ShowsBinding => Kind is InputStripKind.Xlr1 or InputStripKind.Xlr2 or InputStripKind.Capture
        && Matrix.Options.RowHead.Shows(SkinStripParts.Binding)
        && (Kind != InputStripKind.Capture || CapturePartOrdered(SkinStripParts.Binding));

    /// <summary>A capture row whose skin leaves the binding out keeps the connection line it had before.</summary>
    public bool ShowsCaptureLabel => Kind == InputStripKind.Capture && !ShowsBinding;

    /// <summary>One cell per column, in the columns' order.</summary>
    public ObservableCollection<MatrixCellViewModel> Cells { get; } = [];

    /// <summary>The name stands on the left of the head, unless the skin orders it after every other part.</summary>
    public Dock IdentityDock
    {
        get
        {
            IReadOnlyList<string> shown = Matrix.Options.RowHead.Shown;
            int identity = Array.IndexOf([.. shown], SkinStripParts.Identity);
            return identity > 0 && identity == shown.Count - 1 ? Dock.Right : Dock.Left;
        }
    }

    /// <summary>True when the meter runs under this row's faders.</summary>
    public bool MeterUnderFader => Matrix.MeterUnderFader;

    /// <summary>The input's plugin chain, when the daemon supports one.</summary>
    public InsertsViewModel? Inserts => Kind switch
    {
        InputStripKind.Xlr1 => Main.Inserts,
        InputStripKind.Xlr2 => Main.Inserts2,
        InputStripKind.Capture => Channel.Inserts,
        _ => null,
    };

    /// <summary>The chain's key, as the chain window names it.</summary>
    public string? InsertsKey => Kind switch
    {
        InputStripKind.Xlr1 => "xlr1",
        InputStripKind.Xlr2 => "xlr2",
        InputStripKind.Capture when Channel.HasInserts => Channel.Id,
        _ => null,
    };

    /// <summary>True when the head draws this part.</summary>
    public bool Has(string part) => part == SkinStripParts.Identity || Spec.Parts.Contains(part, StringComparer.Ordinal)
        || part == SkinMatrixParts.Processing && ShowsProcessing;

    /// <summary>True when this row shows no Arrange handle: the channel keeps its place, or the input rack carries its handle.</summary>
    public bool HandleHidden => !Channel.IsEditable || !Matrix.OwnsHandles;

    public bool ShowsIcon => Spec.WantsIcon && Channel.Appearance.HasIcon;

    public bool ShowsName => Spec.WantsName || !Channel.Appearance.HasIcon;

    public string LeftLevelName => Localizer.Format("ChannelLevelLeft", Channel.Name);

    public string RightLevelName => Localizer.Format("ChannelLevelRight", Channel.Name);

    /// <summary>Follow the columns: keep each mix's cell, add new ones, drop the gone, in order.</summary>
    internal void SyncCells(IList<MatrixColumnViewModel> columns)
    {
        for (int i = Cells.Count - 1; i >= 0; i--)
            if (!columns.Contains(Cells[i].Column))
            {
                Cells[i].Detach();
                Cells.RemoveAt(i);
            }
        for (int i = 0; i < columns.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < Cells.Count; j++)
                if (ReferenceEquals(Cells[j].Column, columns[i])) { at = j; break; }
            if (at == i) continue;
            if (at >= 0) Cells.Move(at, i);
            else Cells.Insert(i, new MatrixCellViewModel(this, columns[i]));
        }
    }

    internal void RefreshHandle() => Raise(nameof(HandleHidden));

    internal void RefreshNames(MatrixColumnViewModel column)
    {
        foreach (MatrixCellViewModel cell in Cells.Where(c => ReferenceEquals(c.Column, column))) cell.RefreshNames();
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChannelViewModel.IsEditable): Raise(nameof(HandleHidden)); break;
            case nameof(ChannelViewModel.Name):
                Raise(nameof(LeftLevelName));
                Raise(nameof(RightLevelName));
                Raise(nameof(ProcessingName));
                if (_volumeAdjusting) Raise(nameof(ReadoutName));
                foreach (MatrixCellViewModel cell in Cells) cell.RefreshNames();
                break;
            case nameof(ChannelViewModel.VolumeText) when _volumeAdjusting: Raise(nameof(Readout)); break;
            case nameof(ChannelViewModel.HasVolume): RefreshSpec(); break;
            case nameof(ChannelViewModel.Inserts): Raise(nameof(Inserts)); Raise(nameof(InsertsKey)); RefreshSpec(); break;
            case nameof(ChannelViewModel.HasEq): Raise(nameof(ShowsEq)); Raise(nameof(Tall)); break;
            case nameof(ChannelViewModel.ShownUnitInput):
                Raise(nameof(Unit)); Raise(nameof(CarriesUnit)); WatchUnits(); RefreshSpec(); RaiseSwitches();
                break;
            case nameof(ChannelViewModel.ExtraUnitInput):
                Raise(nameof(ExtraUnit)); Raise(nameof(CarriesExtraUnit)); WatchUnits(); RefreshSpec(); RaiseSwitches();
                break;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CapAuxInput) when Kind == InputStripKind.AuxIn: RefreshSpec(); break;
            case nameof(MainViewModel.HasMixer): Raise(nameof(ShowsEq)); Raise(nameof(Tall)); break;
            case nameof(MainViewModel.ShowSoftLowCut) or nameof(MainViewModel.ShowSoftClipGuard) when Kind == InputStripKind.Xlr1:
                Raise(nameof(ShowsSoftware)); RaiseSwitches(); break;
            case nameof(MainViewModel.SoftLowCutHz) or nameof(MainViewModel.SoftClipGuard) when Kind == InputStripKind.Xlr1:
                Raise(nameof(ProcessingOn)); break;
        }
    }

    // The input view models this row's switches come from: the primary
    // unit's, which live as long as the window's model, and an additional
    // unit's while the row carries it.
    private UnitInputViewModel? _watchedUnit;
    private WaveUnitInputViewModel? _watchedExtra;

    private void WatchUnits()
    {
        UnitInputViewModel? unit = _detached ? null : Unit;
        if (!ReferenceEquals(_watchedUnit, unit))
        {
            if (_watchedUnit is not null) _watchedUnit.PropertyChanged -= OnUnitChanged;
            _watchedUnit = unit;
            if (unit is not null) unit.PropertyChanged += OnUnitChanged;
        }
        WaveUnitInputViewModel? extra = _detached ? null : ExtraUnit;
        if (!ReferenceEquals(_watchedExtra, extra))
        {
            if (_watchedExtra is not null) _watchedExtra.PropertyChanged -= OnUnitChanged;
            _watchedExtra = extra;
            if (extra is not null) extra.PropertyChanged += OnUnitChanged;
        }
    }

    private void OnUnitChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An additional unit raises every name at once after a state.
        if (e.PropertyName is null or nameof(UnitInputViewModel.HasSwitches) or nameof(UnitInputViewModel.SwitchesOn)
            or nameof(UnitInputViewModel.PhantomShown)) RaiseSwitches();
        if (e.PropertyName is null or nameof(UnitInputViewModel.GainText) or nameof(UnitInputViewModel.GainName)) RaiseReadout();
    }

    private void RaiseSwitches()
    {
        Raise(nameof(ShowsProcessing));
        Raise(nameof(ProcessingOn));
        Raise(nameof(PhantomOn));
        Raise(nameof(HasPhantom));
        Raise(nameof(ProcessingName));
    }

    private void OnSendsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (MatrixCellViewModel cell in Cells) cell.Refresh();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ShowsIcon));
        Raise(nameof(ShowsName));
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Channel.PropertyChanged -= OnChannelChanged;
        Channel.Sends.CollectionChanged -= OnSendsChanged;
        Channel.Appearance.PropertyChanged -= OnAppearanceChanged;
        if (Kind != InputStripKind.Application) Matrix.Main.PropertyChanged -= OnMainChanged;
        WatchUnits();
        foreach (MatrixCellViewModel cell in Cells) cell.Detach();
        Cells.Clear();
    }
}

/// <summary>
/// One channel in one mix: the channel's own send into the column's mix,
/// found among the channel's sends by mix id. A reference, never a copy: the
/// fader, the value and the mute are bound to that send. A channel with no
/// send into the mix, or one the view does not draw, has an empty cell.
/// </summary>
public sealed class MatrixCellViewModel : ViewModelBase
{
    private SendViewModel? _send;
    private bool _detached, _adjusting;

    internal MatrixCellViewModel(MatrixRowViewModel row, MatrixColumnViewModel column)
    {
        Row = row;
        Column = column;
        Refresh();
    }

    public MatrixRowViewModel Row { get; }

    public MatrixColumnViewModel Column { get; }

    public ChannelViewModel Channel => Row.Channel;

    public MixViewModel Mix => Column.Mix;

    /// <summary>The channel's send into the column's mix, or null while the channel has none.</summary>
    public SendViewModel? Send => _send;

    /// <summary>How the cell is drawn.</summary>
    public StripSpec Spec => Row.Matrix.CellSpec;

    /// <summary>True when the cell draws nothing: no send, or one the view does not draw.</summary>
    public bool IsEmpty => _send is not { DisplayVisible: true };

    /// <summary>True when the cell draws its send.</summary>
    public bool HasSend => !IsEmpty;

    /// <summary>True when the send is muted and the skin tints a muted cell.</summary>
    public bool Tinted => !IsEmpty && _send!.Muted && Row.Matrix.Options.Muted == SkinMatrixMuted.Tint;

    /// <summary>True when the send is muted and the skin fades a muted cell.</summary>
    public bool Dimmed => !IsEmpty && _send!.Muted && Row.Matrix.Options.Muted == SkinMatrixMuted.Dim;

    /// <summary>True when the fader draws the channel's meter as its track.</summary>
    public bool MeterUnderFader => Row.Matrix.MeterUnderFader;

    /// <summary>True when the send's level is drawn over the fader while it moves instead of beside it.</summary>
    public bool ValueOverFader => Row.Matrix.Options.Value == SkinMatrixValue.OverFader
        && Row.Matrix.Options.Cell.Shows(SkinStripParts.Value);

    /// <summary>True while the user is moving the cell's fader; the fader writes it.</summary>
    public bool Adjusting
    {
        get => _adjusting;
        set
        {
            if (_adjusting == value) return;
            _adjusting = value;
            Raise(nameof(Adjusting));
            Raise(nameof(ShowsValueChip));
        }
    }

    /// <summary>True when the level shows over the fader now: the skin puts it there and the fader is moving.</summary>
    public bool ShowsValueChip => ValueOverFader && HasSend && _adjusting;

    /// <summary>The fader's name for a screen reader: whose send, into which mix.</summary>
    public string SendName => Localizer.Format("BankSendName", Channel.Name, Mix.Name);

    /// <summary>The mute's name for a screen reader.</summary>
    public string MuteName => Localizer.Format("BankMuteName", Channel.Name, Mix.Name);

    /// <summary>The meters' names. They read the channel, not the send, and say so.</summary>
    public string LeftLevelName => Row.LeftLevelName;

    public string RightLevelName => Row.RightLevelName;

    /// <summary>Find the send again: the channel's sends changed.</summary>
    internal void Refresh()
    {
        if (_detached) return;
        SendViewModel? send = Channel.Sends.FirstOrDefault(s => s.MixId == Mix.Id);
        if (!ReferenceEquals(send, _send))
        {
            if (_send is not null) _send.PropertyChanged -= OnSendChanged;
            _send = send;
            if (_send is not null) _send.PropertyChanged += OnSendChanged;
            Raise(nameof(Send));
        }
        RaiseState();
    }

    internal void RefreshNames()
    {
        Raise(nameof(SendName));
        Raise(nameof(MuteName));
        Raise(nameof(LeftLevelName));
        Raise(nameof(RightLevelName));
    }

    private void RaiseState()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(HasSend));
        Raise(nameof(Tinted));
        Raise(nameof(Dimmed));
        Raise(nameof(ShowsValueChip));
    }

    private void OnSendChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SendViewModel.Muted) or nameof(SendViewModel.DisplayVisible)) RaiseState();
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        if (_send is not null) _send.PropertyChanged -= OnSendChanged;
    }
}
