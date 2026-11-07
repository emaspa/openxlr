using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI.Layout;

/// <summary>
/// The submixer by mix: a read-only projection over
/// <see cref="MainViewModel.Channels"/> and <see cref="MainViewModel.Mixes"/>
/// that a skin's <c>mix-banks</c> template draws. A bank shows the mix its
/// picker chose, that mix's master, and one cell per channel holding the
/// channel's own <see cref="SendViewModel"/> into that mix. Nothing here
/// holds a level or a mute of its own: a cell hands out the existing send,
/// a bank the existing mix, and every write goes through their setters.
///
/// The banks follow the collections they project and never change them.
/// Channel and mix order stay the mixer's; choosing a bank's mix is a window
/// preference, raised through <see cref="SelectionChosen"/> for the window
/// to keep in ui.json, and never a daemon command. Which channels, mixes and
/// sends show is the view's decision (<see cref="MainViewModel"/>'s
/// DisplayVisible), so a skin cannot bring back what the mini view or a
/// hidden channel leaves out.
/// </summary>
public sealed class MixerPresentationViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _main;
    private readonly HashSet<MixViewModel> _watchedMixes = [];
    private readonly HashSet<ChannelViewModel> _watchedChannels = [];
    private SkinSubmixerOptions _options = SkinSubmixerOptions.Default;
    private IReadOnlyList<string> _preferred = [];
    private bool _ownsHandles = true;
    private bool _reconciling;

    public MixerPresentationViewModel(MainViewModel main)
    {
        _main = main;
        _main.Mixes.CollectionChanged += OnMixesChanged;
        _main.Channels.CollectionChanged += OnChannelsChanged;
        WatchMixes();
        WatchChannels();
        BuildSpecs();
        Reconcile();
    }

    /// <summary>The window's model, whose collections this projects.</summary>
    public MainViewModel Main => _main;

    /// <summary>The banks, the first on the left or at the top.</summary>
    public ObservableCollection<MixBankViewModel> Banks { get; } = [];

    /// <summary>
    /// Every mix the view shows, in the mixer's order: what each bank's
    /// picker lists. The same <see cref="MixViewModel"/> objects, so a rename
    /// shows in every picker at once.
    /// </summary>
    public ObservableCollection<MixViewModel> Available { get; } = [];

    /// <summary>The skin's options in force.</summary>
    public SkinSubmixerOptions Options => _options;

    /// <summary>How a cell is drawn.</summary>
    public StripSpec StripSpec { get; private set; } = null!;

    /// <summary>How a bank's master is drawn.</summary>
    public StripSpec MasterSpec { get; private set; } = null!;

    /// <summary>The way the banks follow one another.</summary>
    public Orientation BankOrientation => StripSpec.Axis(_options.Orientation);

    /// <summary>The way the cells of one bank follow one another: across for upright strips, down for a send list.</summary>
    public Orientation CellOrientation =>
        _options.Strip.Template == SkinLayoutTemplates.SendList ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>
    /// True when the first bank carries the channels' Arrange handles; false
    /// while an input rack shows, which carries them instead. A channel has
    /// one handle in the window, never one per bank.
    /// </summary>
    public bool OwnsHandles
    {
        get => _ownsHandles;
        set
        {
            if (_ownsHandles == value) return;
            _ownsHandles = value;
            foreach (MixBankViewModel bank in Banks) bank.RefreshHandles();
        }
    }

    /// <summary>The columns of the grid the banks stand in: one per bank side by side, one for banks one above the other.</summary>
    public int BankColumns => _options.Orientation == SkinLayoutOrientation.Horizontal ? Math.Max(1, Banks.Count) : 1;

    /// <summary>The rows of that grid.</summary>
    public int BankRows => _options.Orientation == SkinLayoutOrientation.Horizontal ? 1 : Math.Max(1, Banks.Count);

    /// <summary>How many channels a bank draws a strip for now.</summary>
    public int VisibleChannels => _main.Channels.Count(c => c.DisplayVisible);

    /// <summary>
    /// The mixes the banks show, first bank first, when the user picks one
    /// from a bank's picker. Nothing else raises it: a bank that moves on
    /// because its mix went away does not overwrite the user's choice.
    /// </summary>
    public event Action<IReadOnlyList<string>>? SelectionChosen;

    /// <summary>Raised when what a bank shows changes in a way that can change the width the banks need.</summary>
    public event Action? ShapeChanged;

    /// <summary>The mix ids the banks show now, first bank first.</summary>
    public IReadOnlyList<string> Selection => [.. Banks.Select(b => b.Mix?.Id).OfType<string>()];

    /// <summary>
    /// Take a skin's options and the mixes to start the banks on: the user's
    /// saved choice when there is one, otherwise the skin's
    /// <c>selectionDefaults</c>. Ids with no mix are skipped. Options that
    /// draw alike leave the banks as they are.
    /// </summary>
    public void Configure(SkinSubmixerOptions options, IReadOnlyList<string>? saved)
    {
        IReadOnlyList<string> preferred = saved is { Count: > 0 } ? saved : options.SelectionDefaults;
        if (options.Description() == _options.Description() && preferred.SequenceEqual(_preferred)) return;
        _options = options;
        _preferred = [.. preferred];
        BuildSpecs();
        Raise(nameof(Options));
        Raise(nameof(BankOrientation));
        Raise(nameof(CellOrientation));
        // The banks start again from the preferred mixes; the cells hand out the same sends.
        foreach (MixBankViewModel bank in Banks) bank.Detach();
        Banks.Clear();
        Reconcile();
    }

    private void BuildSpecs()
    {
        SkinStripOptions strip = _options.Strip;
        StripSpec = StripSpec.From(strip,
            strip.Template == SkinLayoutTemplates.SendList ? Orientation.Horizontal : Orientation.Vertical,
            SkinStripParts.Send, SkinStripParts.Fader);
        MasterSpec = StripSpec.From(_options.Master);
        Raise(nameof(StripSpec));
        Raise(nameof(MasterSpec));
    }

    /// <summary>
    /// A user's pick from one bank's picker. A mix another bank already shows
    /// trades places with this bank's, so two banks never show the same mix.
    /// </summary>
    internal void Pick(MixBankViewModel bank, MixViewModel mix)
    {
        if (_reconciling || ReferenceEquals(bank.Mix, mix) || !Available.Contains(mix) || !Banks.Contains(bank)) return;
        MixBankViewModel? other = Banks.FirstOrDefault(b => !ReferenceEquals(b, bank) && ReferenceEquals(b.Mix, mix));
        if (other is not null) other.Show(bank.Mix, _main.Mixes);
        bank.Show(mix, _main.Mixes);
        SelectionChosen?.Invoke(Selection);
    }

    private void OnMixesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchMixes();
        Reconcile();
    }

    private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchChannels();
        foreach (MixBankViewModel bank in Banks) bank.SyncCells(_main.Channels);
        ShapeChanged?.Invoke();
    }

    private void OnMixChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MixViewModel.DisplayVisible)) Reconcile();
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.DisplayVisible)) ShapeChanged?.Invoke();
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

    /// <summary>
    /// Bring the pickers and the banks in line with the mixes the view
    /// shows. A bank keeps its mix while that mix shows. A new bank takes the
    /// next preferred mix, then the next one in the mixer's order. A bank
    /// whose mix went away moves on to the next mix in the mixer's order from
    /// where its mix was, past the ones the other banks show. There are as
    /// many banks as the skin asks for, and never more than there are mixes.
    /// </summary>
    private void Reconcile()
    {
        if (_reconciling) return;
        _reconciling = true;
        try
        {
            MixViewModel[] available = [.. _main.Mixes.Where(m => m.DisplayVisible)];
            SyncAvailable(available);
            int count = available.Length == 0 ? 0 : Math.Clamp(_options.VisibleBanks, 1, available.Length);
            int existing = Math.Min(Banks.Count, count);
            var chosen = new MixViewModel?[count];
            var used = new HashSet<MixViewModel>();
            for (int i = 0; i < existing; i++)
                if (Banks[i].Mix is { } mix && available.Contains(mix) && used.Add(mix)) chosen[i] = mix;
            var preferred = new Queue<MixViewModel>(_preferred
                .Select(id => available.FirstOrDefault(m => m.Id == id)).OfType<MixViewModel>().Distinct());
            for (int i = existing; i < count; i++)
            {
                while (preferred.Count > 0 && used.Contains(preferred.Peek())) preferred.Dequeue();
                if (preferred.Count > 0 && used.Add(preferred.Peek())) chosen[i] = preferred.Dequeue();
            }
            for (int i = 0; i < count; i++)
            {
                if (chosen[i] is not null) continue;
                int start = i < existing ? Math.Clamp(Banks[i].LastIndex, 0, Math.Max(0, _main.Mixes.Count - 1)) : 0;
                for (int step = 0; step < _main.Mixes.Count && chosen[i] is null; step++)
                {
                    MixViewModel candidate = _main.Mixes[(start + step) % _main.Mixes.Count];
                    if (candidate.DisplayVisible && used.Add(candidate)) chosen[i] = candidate;
                }
            }

            while (Banks.Count > count)
            {
                Banks[^1].Detach();
                Banks.RemoveAt(Banks.Count - 1);
            }
            for (int i = 0; i < count; i++)
            {
                if (i >= Banks.Count) Banks.Add(new MixBankViewModel(this, i, _main.Channels));
                Banks[i].Show(chosen[i], _main.Mixes);
            }
        }
        finally { _reconciling = false; }
        Raise(nameof(BankColumns));
        Raise(nameof(BankRows));
        ShapeChanged?.Invoke();
    }

    /// <summary>Make the pickers' list the shown mixes, in order, moving the entries it has rather than replacing them.</summary>
    private void SyncAvailable(IReadOnlyList<MixViewModel> wanted)
    {
        for (int i = Available.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Available[i])) Available.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Available.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) Available.Move(at, i);
            else Available.Insert(i, wanted[i]);
        }
    }

    public void Dispose()
    {
        _main.Mixes.CollectionChanged -= OnMixesChanged;
        _main.Channels.CollectionChanged -= OnChannelsChanged;
        foreach (MixViewModel mix in _watchedMixes) mix.PropertyChanged -= OnMixChanged;
        foreach (ChannelViewModel channel in _watchedChannels) channel.PropertyChanged -= OnChannelChanged;
        _watchedMixes.Clear();
        _watchedChannels.Clear();
        foreach (MixBankViewModel bank in Banks) bank.Detach();
        Banks.Clear();
        Available.Clear();
    }
}

/// <summary>One bank: the mix its picker chose, that mix's master, and a cell per channel.</summary>
public sealed class MixBankViewModel : ViewModelBase
{
    internal MixBankViewModel(MixerPresentationViewModel presentation, int index, IEnumerable<ChannelViewModel> channels)
    {
        Presentation = presentation;
        Index = index;
        SyncCells(channels);
        presentation.Available.CollectionChanged += OnAvailableChanged;
        SyncChoices();
    }

    public MixerPresentationViewModel Presentation { get; }

    /// <summary>The bank's place, 0 for the first.</summary>
    public int Index { get; }

    /// <summary>Where the bank's mix stood in the mixer's order when it was last seen, which is where a fallback starts looking.</summary>
    internal int LastIndex { get; private set; }

    /// <summary>The mixes this bank's picker lists: every mix the view shows.</summary>
    public ObservableCollection<MixViewModel> Available => Presentation.Available;

    /// <summary>What a screen reader calls the picker.</summary>
    public string PickerName => Localizer.Format("BankPickerName", Index + 1);

    /// <summary>True when the skin draws the picker as a row of keys rather than a dropdown.</summary>
    public bool PickerButtons => Presentation.Options.PickerButtons;

    /// <summary>The opposite of <see cref="PickerButtons"/>, for the dropdown's visibility.</summary>
    public bool PickerDropdown => !PickerButtons;

    /// <summary>One key per mix in <see cref="Available"/>, in its order, for a picker drawn as keys.</summary>
    public ObservableCollection<BankChoiceViewModel> Choices { get; } = [];

    private MixViewModel? _mix;
    /// <summary>
    /// The mix this bank shows, the same object the mixer holds. Setting it
    /// is a pick from the picker; a pick of a mix another bank shows trades
    /// the two.
    /// </summary>
    public MixViewModel? Mix
    {
        get => _mix;
        set { if (value is not null) Presentation.Pick(this, value); }
    }

    private BankMasterViewModel? _master;
    /// <summary>
    /// The bank's master: a new object whenever the bank shows another mix,
    /// so its template is built again rather than rebound from one mix's
    /// range to another's.
    /// </summary>
    public BankMasterViewModel? Master => _master;

    /// <summary>One cell per channel, in the mixer's channel order.</summary>
    public ObservableCollection<BankCellViewModel> Cells { get; } = [];

    /// <summary>The way the cells follow one another.</summary>
    public Orientation CellOrientation => Presentation.CellOrientation;

    /// <summary>Upright strips scroll sideways inside the bank when the bank is narrower than they are; a send list never needs to.</summary>
    public ScrollBarVisibility CellsScrollAcross =>
        CellOrientation == Orientation.Horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

    /// <summary>A master strip ends a row of upright strips on its right; a master card, and the master of a send list, goes under the cells.</summary>
    public Dock MasterDock =>
        Presentation.Options.Master.Template == SkinLayoutTemplates.MasterStrip && CellOrientation == Orientation.Horizontal
            ? Dock.Right : Dock.Bottom;

    /// <summary>The gap between the master and the cells, on the side the master is.</summary>
    public Thickness MasterMargin => MasterDock == Dock.Right ? new Thickness(4, 0, 0, 0) : new Thickness(0, 8, 0, 0);

    /// <summary>True for the bank whose cells carry the channels' Arrange handles.</summary>
    internal bool CarriesHandles => Index == 0 && Presentation.OwnsHandles;

    internal void Show(MixViewModel? mix, IList<MixViewModel> order)
    {
        if (mix is not null) LastIndex = order.IndexOf(mix);
        if (ReferenceEquals(_mix, mix)) return;
        _mix = mix;
        _master = mix is null ? null : new BankMasterViewModel(this, mix);
        Raise(nameof(Mix));
        Raise(nameof(Master));
        foreach (BankCellViewModel cell in Cells) cell.Refresh();
        foreach (BankChoiceViewModel choice in Choices) choice.Refresh();
    }

    private void OnAvailableChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncChoices();

    /// <summary>Follow the picker's list: keep each mix's key, add new ones, drop the gone, in order.</summary>
    private void SyncChoices()
    {
        IList<MixViewModel> wanted = Presentation.Available;
        for (int i = Choices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Choices[i].Mix)) Choices.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < Choices.Count; j++)
                if (ReferenceEquals(Choices[j].Mix, wanted[i])) { at = j; break; }
            if (at == i) continue;
            if (at >= 0) Choices.Move(at, i);
            else Choices.Insert(i, new BankChoiceViewModel(this, wanted[i]));
        }
    }

    /// <summary>Follow the mixer's channels: keep each channel's cell, add new ones, drop the gone, in order.</summary>
    internal void SyncCells(IEnumerable<ChannelViewModel> channels)
    {
        ChannelViewModel[] wanted = [.. channels];
        for (int i = Cells.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Cells[i].Channel))
            {
                Cells[i].Detach();
                Cells.RemoveAt(i);
            }
        for (int i = 0; i < wanted.Length; i++)
        {
            int at = -1;
            for (int j = 0; j < Cells.Count; j++)
                if (ReferenceEquals(Cells[j].Channel, wanted[i])) { at = j; break; }
            if (at == i) continue;
            if (at >= 0) Cells.Move(at, i);
            else Cells.Insert(i, new BankCellViewModel(this, wanted[i]));
        }
    }

    internal void RefreshHandles()
    {
        foreach (BankCellViewModel cell in Cells) cell.RefreshHandle();
    }

    internal void Detach()
    {
        Presentation.Available.CollectionChanged -= OnAvailableChanged;
        Choices.Clear();
        foreach (BankCellViewModel cell in Cells) cell.Detach();
        Cells.Clear();
    }
}

/// <summary>
/// One key of a bank's picker drawn as keys: a mix the bank can show. The key
/// is lit while the bank shows that mix, and pressing it is the same pick the
/// dropdown makes, trading places with a bank that shows the mix already.
/// </summary>
public sealed class BankChoiceViewModel(MixBankViewModel bank, MixViewModel mix) : ViewModelBase
{
    public MixBankViewModel Bank { get; } = bank;

    /// <summary>The mix, the same object the mixer holds, so a rename relabels the key.</summary>
    public MixViewModel Mix { get; } = mix;

    /// <summary>True while the bank shows this mix.</summary>
    public bool IsSelected => ReferenceEquals(Bank.Mix, Mix);

    /// <summary>
    /// The key was pressed: show its mix. Pressing the lit key changes
    /// nothing, and it stays lit, since a bank always shows a mix.
    /// </summary>
    public void Select()
    {
        Bank.Mix = Mix;
        Refresh();
    }

    internal void Refresh() => Raise(nameof(IsSelected));
}

/// <summary>A bank's master: the mix the bank shows, drawn as a card or as a strip.</summary>
public sealed class BankMasterViewModel(MixBankViewModel bank, MixViewModel mix)
{
    public MixBankViewModel Bank { get; } = bank;

    /// <summary>The mix, the same object the mixer holds: its level, mute and chain are bound to it.</summary>
    public MixViewModel Mix { get; } = mix;

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Mix.Appearance;

    public StripSpec Spec => Bank.Presentation.MasterSpec;
}

/// <summary>
/// One channel in one bank: the channel and its own send into the bank's mix,
/// found among the channel's sends by mix id. A reference, never a copy: the
/// fader, the value and the mute are bound to that send.
/// </summary>
public sealed class BankCellViewModel : ViewModelBase
{
    private SendViewModel? _send;
    private bool _detached;

    internal BankCellViewModel(MixBankViewModel bank, ChannelViewModel channel)
    {
        Bank = bank;
        Channel = channel;
        channel.PropertyChanged += OnChannelChanged;
        channel.Sends.CollectionChanged += OnSendsChanged;
        channel.Appearance.PropertyChanged += OnAppearanceChanged;
        Refresh();
    }

    public MixBankViewModel Bank { get; }

    public ChannelViewModel Channel { get; }

    /// <summary>The item's appearance, where the <c>layoutAccent</c> style looks for the colour the user chose.</summary>
    public LayoutAppearanceViewModel Appearance => Channel.Appearance;

    /// <summary>The channel's send into the bank's mix, or null while the channel has none yet.</summary>
    public SendViewModel? Send => _send;

    /// <summary>How the cell is drawn.</summary>
    public StripSpec Spec => Bank.Presentation.StripSpec;

    /// <summary>True while the view draws both the channel and its send into this mix.</summary>
    public bool Shown => Channel.DisplayVisible && _send is { DisplayVisible: true };

    /// <summary>
    /// True when this cell shows no Arrange handle: the channel keeps its
    /// place, or another bank, or the input rack, carries its handle.
    /// </summary>
    public bool HandleHidden => !Channel.IsEditable || !Bank.CarriesHandles;

    public bool ShowsIcon => Spec.WantsIcon && Channel.Appearance.HasIcon;

    public bool ShowsName => Spec.WantsName || !Channel.Appearance.HasIcon;

    /// <summary>The fader's name for a screen reader: whose send, into which mix.</summary>
    public string SendName => Localizer.Format("BankSendName", Channel.Name, MixName);

    /// <summary>The mute's name for a screen reader.</summary>
    public string MuteName => Localizer.Format("BankMuteName", Channel.Name, MixName);

    /// <summary>The meters' names. They read the channel, not the send, and say so.</summary>
    public string LeftLevelName => Localizer.Format("ChannelLevelLeft", Channel.Name);

    public string RightLevelName => Localizer.Format("ChannelLevelRight", Channel.Name);

    private string MixName => Bank.Mix?.Name ?? _send?.MixName ?? "";

    /// <summary>Find the send again: the bank's mix changed, or the channel's sends did.</summary>
    internal void Refresh()
    {
        if (_detached) return;
        SendViewModel? send = Bank.Mix is { } mix ? Channel.Sends.FirstOrDefault(s => s.MixId == mix.Id) : null;
        if (!ReferenceEquals(send, _send))
        {
            if (_send is not null) _send.PropertyChanged -= OnSendChanged;
            _send = send;
            if (_send is not null) _send.PropertyChanged += OnSendChanged;
            Raise(nameof(Send));
        }
        Raise(nameof(Shown));
        Raise(nameof(SendName));
        Raise(nameof(MuteName));
    }

    internal void RefreshHandle() => Raise(nameof(HandleHidden));

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChannelViewModel.DisplayVisible): Raise(nameof(Shown)); break;
            case nameof(ChannelViewModel.IsEditable): Raise(nameof(HandleHidden)); break;
            case nameof(ChannelViewModel.Name):
                Raise(nameof(SendName));
                Raise(nameof(MuteName));
                Raise(nameof(LeftLevelName));
                Raise(nameof(RightLevelName));
                break;
        }
    }

    private void OnSendChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SendViewModel.DisplayVisible)) Raise(nameof(Shown));
        else if (e.PropertyName is nameof(SendViewModel.MixName)) { Raise(nameof(SendName)); Raise(nameof(MuteName)); }
    }

    private void OnSendsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

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
        if (_send is not null) _send.PropertyChanged -= OnSendChanged;
    }
}
