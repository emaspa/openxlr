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

    private static StripSpec BuildCellSpec(SkinMatrixOptions options) => new(options.Cell.Shown, Orientation.Horizontal, null,
        SkinLabelStyle.Text, Orientation.Horizontal, false, Orientation.Horizontal);

    /// <summary>The parts a row head can draw for a channel of this kind, its name included.</summary>
    public static IReadOnlyCollection<string> RowPartsOf(InputStripKind kind) => kind switch
    {
        InputStripKind.Xlr1 or InputStripKind.Xlr2 => SkinMatrixParts.RowHead,
        InputStripKind.AuxIn => [SkinStripParts.Identity, SkinStripParts.Meter, SkinStripParts.Gain],
        _ => [SkinStripParts.Identity, SkinStripParts.Meter],
    };

    /// <summary>True while the matrix draws a row for the channel: the view shows it, and XLR 2 needs an interface with two inputs.</summary>
    internal bool ShowsRow(ChannelViewModel channel) =>
        channel.DisplayVisible && (InputRackViewModel.KindOf(channel) != InputStripKind.Xlr2 || _main.HasXlr2);

    /// <summary>
    /// How many of the window's monitor outputs hear this mix now: the ones
    /// ticked in MONITOR whose feed is the mix, alone or summed with another.
    /// </summary>
    internal int OutputsOf(MixViewModel mix) => _main.MonitorOutputs.Count(o =>
        o.IsSelected && o.Feed is { } feed && feed.Id.Split('+').Contains(mix.Id, StringComparer.Ordinal));

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
        if (e.PropertyName == nameof(MainViewModel.GenericMicrophone))
            foreach (MatrixRowViewModel row in Rows) row.RefreshSpec();
    }

    private void OnMixChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MixViewModel.DisplayVisible)) Sync();
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.DisplayVisible) or nameof(ChannelViewModel.IsHardware)
            or nameof(ChannelViewModel.CaptureSource)) Sync();
    }

    private void OnOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchOutputs();
        RefreshOutputs();
    }

    private void OnOutputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorOutputItem.IsSelected) or nameof(MonitorOutputItem.Feed)) RefreshOutputs();
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

    /// <summary>True when this column head shows no Arrange handle: Monitor A and B and Aux keep their place.</summary>
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
        string[] parts = [.. head.Shown.Where(p => p != SkinStripParts.Identity && available.Contains(p)
            && !(Main.GenericMicrophone && Kind == InputStripKind.Xlr1 && p is SkinStripParts.Gain or SkinStripParts.Mute))];
        // Without hardware gain the standalone meter remains visible, even
        // when the skin normally draws it under the gain fader.
        if (Matrix.MeterUnderFader && parts.Contains(SkinStripParts.Gain)) parts = [.. parts.Where(p => p != SkinStripParts.Meter)];
        return new StripSpec(parts, Orientation.Horizontal, null, head.Label, Orientation.Horizontal, false, Orientation.Horizontal);
    }

    internal void RefreshSpec()
    {
        Spec = CreateSpec();
        Raise(nameof(Spec));
    }

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

    /// <summary>The input's plugin chain, for the two XLR inputs; null for every other channel.</summary>
    public InsertsViewModel? Inserts => Kind switch
    {
        InputStripKind.Xlr1 => Main.Inserts,
        InputStripKind.Xlr2 => Main.Inserts2,
        _ => null,
    };

    /// <summary>The chain's key, as the chain window names it.</summary>
    public string? InsertsKey => Kind switch
    {
        InputStripKind.Xlr1 => "xlr1",
        InputStripKind.Xlr2 => "xlr2",
        _ => null,
    };

    /// <summary>True when the head draws this part.</summary>
    public bool Has(string part) => part == SkinStripParts.Identity || Spec.Parts.Contains(part, StringComparer.Ordinal);

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
                foreach (MatrixCellViewModel cell in Cells) cell.RefreshNames();
                break;
        }
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
    private bool _detached;

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
