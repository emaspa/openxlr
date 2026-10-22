using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// Arrange mode: drag handles on the five sections and on the channel and
/// mix tiles. Where the sections stand is a window preference in ui.json,
/// kept per skin when the skin sets its own layout (see
/// <see cref="SectionArrangement"/>); the skin's tree, built by
/// <see cref="DesktopLayoutHost"/>, decides whether a drop lands beside a
/// section or trades places with it, and the arrow keys and the autoscroll
/// follow. Channel and mix order is the layout's one order, so a tile move
/// is a <c>setLayoutOrder</c> and the tiles follow the state the daemon
/// publishes after saving it; nothing moves here before that.
/// </summary>
public partial class MainWindow
{
    private static readonly string ArrangeHint =
        Localizer.Text("DragAHandleOntoAnotherSectionOr");
    private static readonly string SwapHint =
        Localizer.Text("ArrangeSwapHint");
    private Button? _dragHandle;
    private Button? _dropHandle;
    private Button[] _dragTargets = [];
    private IPointer? _dragPointer;
    private Point _dragOrigin, _dragPoint;
    private bool _dragging, _dropAfter, _reorderSaving;
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private SectionArrangement? _arrangement;
    private DesktopLayoutHost _layoutHost = null!;
    private IPointer? _lastPointer;
    private HashSet<(string Skin, int Version)> _layoutsSeen = [];
    private readonly HashSet<(string Skin, int Version)> _unsavedLayoutsSeen = [];

    /// <summary>The arrangement the regions stand in now.</summary>
    internal SectionArrangement Arrangement => _arrangement!;

    /// <summary>What builds the window's tree from the arrangement.</summary>
    internal DesktopLayoutHost LayoutHost => _layoutHost;

    /// <summary>The menu Enter or Space on a section's handle opened last, for the tests.</summary>
    internal MenuFlyout? SwapMenu { get; private set; }

    private void SetupReordering()
    {
        var hosts = new Dictionary<string, Control>(StringComparer.Ordinal) { [SkinLayoutRegions.Header] = HeaderCard };
        foreach (string region in SkinLayoutRegions.Sections)
            hosts[region] = (Control)this.FindControl<Expander>(SectionArrangement.TileFor(region))!.Parent!;
        _layoutHost = new DesktopLayoutHost(hosts, MixerContent, SectionCards, SectionScroll, RegionMinWidth);
        _layoutsSeen = [.. UiSettings.Load().SkinLayoutsSeen.Select(k => (k.Skin, k.Version))];
        // The overflow menu's actions keep the window's data context while the menu is closed.
        HeaderOverflowItems.Bind(DataContextProperty, this.GetObservable(DataContextProperty));
        HeaderOverflowItems.AddHandler(Button.ClickEvent, OnOverflowAction);
        ApplyArrangement(ResolveArrangement(out string? note));
        if (note is not null) ReportArrangement(note);
        SkinService.Changed += OnSkinArrangement;
        // The last pointer that pressed anywhere in the window, so a skin
        // switch can end a fader drag before it moves the sections.
        AddHandler(PointerPressedEvent, (_, e) => _lastPointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += (_, _) => ApplyPendingPopOut();
        _wornSkin = SkinService.Current.Id;
        foreach (Control host in hosts.Values)
            host.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty) _layoutHost.UpdateWidths();
            };
        SectionScroll.SizeChanged += (_, _) => _layoutHost.UpdateWidths();
        // Tunnel so a handle is seen before the expander header's
        // press-to-toggle; every other control keeps its own input.
        AddHandler(PointerPressedEvent, OnReorderPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnReorderMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReorderReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => CancelReorder(), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnReorderKey, RoutingStrategies.Tunnel);
        _dragScroll.Tick += (_, _) => ScrollReorder();
        Deactivated += (_, _) => CancelReorder();
        Closed += (_, _) =>
        {
            SkinService.Changed -= OnSkinArrangement;
            CancelReorder();
            DisposePresentations();
        };
        DataContextChanged += (_, _) =>
        {
            WatchMixerView();
            if (_arrangement is { } arrangement) ApplyTemplates(arrangement);
        };
        WatchMixerView();
    }

    private MainViewModel? _watchedView;

    /// <summary>The mini view sets most sections aside, so it ends Arrange mode.</summary>
    private void WatchMixerView()
    {
        if (_watchedView is not null) _watchedView.PropertyChanged -= OnMixerViewChanged;
        _watchedView = DataContext as MainViewModel;
        if (_watchedView is not null) _watchedView.PropertyChanged += OnMixerViewChanged;
    }

    private void OnMixerViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A skin's pop-out default waits for a mixer to pop out.
        if (e.PropertyName == nameof(MainViewModel.HasMixer)) ApplyPendingPopOut();
        if (e.PropertyName != nameof(MainViewModel.IsMiniView) || _watchedView?.IsMiniView != true
            || ArrangeButton.IsChecked != true) return;
        ArrangeButton.IsChecked = false;
        OnArrange(this, new RoutedEventArgs());
    }

    private void OnArrange(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        bool arranging = ArrangeButton.IsChecked == true;
        Classes.Set("arranging", arranging);
        ResetSectionsButton.IsVisible = arranging;
        UpdateHiddenSections();
        ReportArrangement(null);
    }

    /// <summary>An action chosen from the overflow menu closes it; Profiles opens its own menu instead.</summary>
    private void OnOverflowAction(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not DropDownButton) HeaderOverflow.Flyout?.Hide();
    }

    /// <summary>
    /// Reset puts back what the skin in force starts from: the usual order
    /// for a skin without a layout, the skin's own placement and visibility
    /// for one with. Only that skin's arrangement is cleared; another
    /// skin's, and the legacy order under a skin with a layout, stay as they are.
    /// </summary>
    private void OnResetSections(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        SectionArrangement arrangement = Arrangement;
        ApplyArrangement(arrangement.Restored());
        string? error = arrangement.Reset(UiSettings.Load()).Save();
        ReportArrangement(error is null ? null : Localizer.Format("SectionOrderNotSaved", error));
    }

    /// <summary>
    /// Options' "Reset this skin's arrangement": what Reset sections does,
    /// and the bank picks, the docking choice and the record that the skin's
    /// pop-out default was applied forgotten too, so the skin starts again as
    /// its author drew it. Nothing opens or closes now; the skin's pop-out
    /// default applies the next time the skin is put on. Null when it was
    /// saved, otherwise why not.
    /// </summary>
    internal string? ResetSkinArrangement()
    {
        CancelReorder();
        SectionArrangement arrangement = Arrangement;
        UiSettings settings = arrangement.Reset(UiSettings.Load());
        settings = arrangement is { Skin: { } skin, Version: { } version }
            ? settings.WithoutSkinChoices(skin, version)
            : settings with { MixerDocked = null };
        string? error = settings.Save();
        // The banks start again from the skin's defaults, as a skin put on afresh.
        _banks?.Configure(_banks.Options, null);
        ApplyArrangement(arrangement.Restored());
        ReportArrangement(error is null ? null : Localizer.Format("SectionOrderNotSaved", error));
        return error;
    }

    /// <summary>The note under the Arrange button: the hint, or why the last move failed.</summary>
    private void ReportArrangement(string? error)
    {
        ArrangementNote.Text = error ?? (_arrangement?.Layout is null ? ArrangeHint : SwapHint);
        ArrangementNote.IsVisible = error is not null || ArrangeButton.IsChecked == true;
    }

    /// <summary>
    /// In Arrange mode, one button for each section the skin hides, which
    /// shows it again and keeps that for the skin.
    /// </summary>
    private void UpdateHiddenSections()
    {
        IReadOnlyList<string> hidden = _arrangement?.Hidden ?? [];
        string[] listed = [.. HiddenSections.Children.OfType<Button>().Select(b => (string)b.Tag!)];
        if (!listed.SequenceEqual(hidden, StringComparer.Ordinal))
        {
            while (HiddenSections.Children.Count > 1) HiddenSections.Children.RemoveAt(1);
            foreach (string region in hidden)
            {
                var show = new Button
                {
                    Content = Localizer.Format("ShowSection", SectionTitle(region)),
                    Tag = region,
                    Padding = new Thickness(8, 1),
                };
                show.Bind(MinHeightProperty, show.GetResourceObservable("Ox.Mixer.SmallControlMinSize"));
                show.Bind(FontSizeProperty, show.GetResourceObservable("Ox.Label.FontSize"));
                show.Click += OnShowSection;
                HiddenSections.Children.Add(show);
            }
        }
        HiddenSections.IsVisible = ArrangeButton.IsChecked == true && hidden.Count > 0;
    }

    private void OnShowSection(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string region) return;
        CancelReorder();
        SaveArrangement(Arrangement.Show(region));
    }

    /// <summary>A section's name as its header shows it.</summary>
    private static string SectionTitle(string region) => region switch
    {
        SkinLayoutRegions.Inputs => Localizer.Text("INPUTS"),
        SkinLayoutRegions.Headphones => Localizer.Text("HEADPHONES"),
        SkinLayoutRegions.Monitor => Localizer.Text("MONITOR"),
        SkinLayoutRegions.Applications => Localizer.Text("APPLICATIONS"),
        _ => Localizer.Text("SUBMIXER"),
    };

    /// <summary>
    /// A saved order made whole: known sections in their saved order, then
    /// any it leaves out in their usual order. Unknown and repeated names go.
    /// </summary>
    internal static string[] CompleteSectionOrder(IEnumerable<string>? saved) =>
        CompleteSectionOrder(saved, SectionTiles);

    /// <summary>The same, made whole against another order.</summary>
    internal static string[] CompleteSectionOrder(IEnumerable<string>? saved, IReadOnlyList<string> usual) =>
        [.. (saved ?? []).Concat(usual).Where(usual.Contains).Distinct(StringComparer.Ordinal)];

    /// <summary>The order with <paramref name="source"/> moved to just before or after <paramref name="target"/>.</summary>
    internal static string[] PlaceSection(IReadOnlyList<string> order, string source, string target, bool after)
    {
        var result = order.ToList();
        if (source == target || !result.Contains(source) || !result.Contains(target)) return [.. result];
        result.Remove(source);
        result.Insert(result.IndexOf(target) + (after ? 1 : 0), source);
        return [.. result];
    }

    /// <summary>
    /// The arrangement for the skin in force. A saved arrangement that no
    /// longer fits the skin's tree is forgotten here, and
    /// <paramref name="note"/> says so once.
    /// </summary>
    private SectionArrangement ResolveArrangement(out string? note)
    {
        note = null;
        SectionArrangement next = SectionArrangement.Resolve(SkinService.Current.Id, SkinService.Layout,
            UiSettings.Load(), out bool dropped);
        if (dropped)
        {
            string? error = next.Reset(UiSettings.Load()).Save();
            note = Localizer.Text("ArrangementDropped");
            if (error is not null) note += " " + Localizer.Format("SectionOrderNotSaved", error);
        }
        return next;
    }

    /// <summary>
    /// A skin was put on, or the sizing changed. Only a skin whose layout
    /// differs from what the window shows moves anything; a change of colours
    /// alone stays the resource update it has always been. While the
    /// arrangement still belongs to the same skin and starts from the same
    /// layout, the one on screen is kept, so a move that could not be saved
    /// lasts the run.
    /// </summary>
    private void OnSkinArrangement()
    {
        SectionArrangement next = ResolveArrangement(out string? note);
        if (_arrangement is { } current && current.SameOrigin(next))
            next = next with { Placement = current.Placement, Shown = current.Shown };
        ApplyArrangement(next);
        // An input rack's and the banks' narrowest widths follow the strip
        // width, which Touch sizing and a skin's tokens change without
        // changing the arrangement.
        _layoutHost.UpdateWidths();
        if (note is not null) ReportArrangement(note);
        // Only putting on another skin can open the mixer's own window; the
        // same skin again, read afresh or with other sizing, never does.
        string worn = SkinService.Current.Id;
        if (worn != _wornSkin) _popOutPending = true;
        _wornSkin = worn;
        ApplyPendingPopOut();
    }

    /// <summary>
    /// Stand the regions as <paramref name="next"/> says, in the order a
    /// structural switch needs (see <see cref="SwitchState"/>). The cards,
    /// their expanders and everything inside them are the same objects before
    /// and after; only the containers around them change, and a template
    /// that changed builds its own controls again. A drag in progress, of a
    /// section or of a fader in either window, ends first, since the geometry
    /// it was measured against is about to change. The mixer panel changes
    /// presentation in whichever window holds it, and focus, the scroll
    /// offsets and the sections' folds come through as the switch found them.
    /// </summary>
    private void ApplyArrangement(SectionArrangement next)
    {
        SectionArrangement? previous = _arrangement;
        _arrangement = next;
        ToolTip.SetTip(ResetSectionsButton, next.Skin is null
            ? Localizer.Text("PutTheFiveSectionsBackInTheir") : Localizer.Text("PutTheSectionsBackInTheSkinsOrder"));
        UpdateHiddenSections();
        FoldOnFirstSight(next);
        if (previous is not null && previous.SameStructure(next)) return;
        SwitchState? kept = previous is null ? null : SwitchState.Capture(this);

        ApplyTemplates(next);
        _layoutHost.Apply(next);
        ApplyHeaderActions(next.Header);
        foreach (Button handle in SectionHandles()) ClearDropTarget(handle);
        bool newTree = previous is null || previous.Tree.Description() != next.Tree.Description();
        if (kept is not null) kept.Restore(newTree);
        else if (newTree) SectionScroll.Offset = default;
    }

    /// <summary>
    /// The header's actions in the order the skin gives, the ones it names
    /// nowhere after them in their usual order, and the ones it puts in the
    /// overflow in the menu at the end. The controls are the same ones,
    /// moved; Options is never among them.
    /// </summary>
    private void ApplyHeaderActions(SkinHeaderOptions header)
    {
        Control Action(string id) => id switch
        {
            SkinHeaderActions.Profiles => ProfilesButton,
            SkinHeaderActions.Arrange => ArrangeButton,
            SkinHeaderActions.Flow => FlowButton,
            SkinHeaderActions.RestartDaemon => RestartDaemonButton,
            SkinHeaderActions.Version => HeaderVersion,
            _ => AboutButton,
        };
        Control[] row = [.. header.Row.Select(Action), HeaderOverflow];
        Control[] overflow = [.. header.Overflow.Select(Action)];
        foreach (Control action in SkinHeaderActions.All.Select(Action))
        {
            Panel goes = overflow.Contains(action) ? HeaderOverflowItems : HeaderActions;
            if (action.Parent is Panel parent && !ReferenceEquals(parent, goes)) parent.Children.Remove(action);
            if (ReferenceEquals(goes, HeaderOverflowItems))
                action.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            else action.ClearValue(HorizontalAlignmentProperty);
        }
        DesktopLayoutHost.SetChildren(HeaderActions, row);
        DesktopLayoutHost.SetChildren(HeaderOverflowItems, overflow);
        HeaderOverflow.IsVisible = overflow.Length > 0;
    }

    /// <summary>
    /// The first time the window wears a skin layout, fold the sections it
    /// starts collapsed. That goes into the collapsed sections like a fold of
    /// the user's own, so from then on the user's choice holds. First means
    /// not in ui.json when the window opened and not folded by it since, so
    /// a second window open at the time folds its own sections too.
    /// </summary>
    private void FoldOnFirstSight(SectionArrangement next)
    {
        if (next.Skin is not { } skin || next.Layout is not { } layout) return;
        string[] collapsed = [.. SkinLayoutRegions.Sections
            .Where(r => layout.OptionsFor(r).Visibility == SkinRegionVisibility.Collapsed)];
        if (collapsed.Length == 0 || !_layoutsSeen.Add((skin, layout.Version))) return;
        _unsavedLayoutsSeen.Add((skin, layout.Version));
        bool restoring = _restoringSections;
        _restoringSections = true;
        try
        {
            foreach (string region in collapsed)
                if (this.FindControl<Expander>(SectionArrangement.TileFor(region)) is { } tile) tile.IsExpanded = false;
        }
        finally { _restoringSections = restoring; }
        SaveSectionState();
    }

    /// <summary>The handle in each section's header.</summary>
    private IEnumerable<Button> SectionHandles() => SectionTiles.Select(SectionHandle).OfType<Button>();

    private Button? SectionHandle(string tile) =>
        (this.FindControl<Expander>(tile)?.Header as Panel)?.Children.OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("reorderHandle"));

    /// <summary>
    /// Show this arrangement for the run and save it, under the skin in
    /// force when it sets a layout and as the legacy order otherwise; a
    /// failed save is said once in the note.
    /// </summary>
    private void SaveArrangement(SectionArrangement next)
    {
        ApplyArrangement(next);
        string? error = next.Save(UiSettings.Load()).Save();
        ReportArrangement(error is null ? null : Localizer.Format("SectionOrderNotSaved", error));
    }

    private static Button? Handle(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("reorderHandle"))
        : null;

    /// <summary>0 for a section, 1 for a channel tile, 2 for a mix tile: a handle only drops within its own group.</summary>
    private static int Group(Button handle) => handle.Tag switch
    {
        string => 0, ChannelViewModel => 1, MixViewModel => 2, _ => -1,
    };

    private static string Id(Button handle) => handle.Tag switch
    {
        string id => id, ChannelViewModel channel => channel.Id, MixViewModel mix => mix.Id, _ => "",
    };

    /// <summary>The region a section's handle moves.</summary>
    private static string RegionOf(Button handle) => SectionArrangement.RegionFor(Id(handle)) ?? "";

    private IEnumerable<Button> Handles(int group) => this.GetVisualDescendants().OfType<Button>()
        .Where(h => h.Classes.Contains("reorderHandle") && h.IsEffectivelyVisible && Group(h) == group);

    private static Border? Card(Button handle) => handle.GetVisualAncestors().OfType<Border>()
        .FirstOrDefault(b => b.Classes.Contains(Group(handle) == 0 ? "card" : "tile"));

    private bool CanReorder(Button handle) => !_reorderSaving && ArrangeButton.IsChecked == true
        && (Group(handle) == 0 || DataContext is MainViewModel { CanEditLayout: true });

    private void OnReorderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Handle(e.Source) is not { } handle || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!CanReorder(handle)) return;
        CancelReorder();
        _dragHandle = handle;
        _dragTargets = [.. Handles(Group(handle))];
        _dragPointer = e.Pointer;
        _dragPoint = _dragOrigin = e.GetPosition(this);
        handle.Focus();
        e.Pointer.Capture(handle);
    }

    private void OnReorderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragHandle is null || e.Pointer != _dragPointer) return;
        e.Handled = true;
        _dragPoint = e.GetPosition(this);
        if (!_dragging && new Vector(_dragPoint.X - _dragOrigin.X, _dragPoint.Y - _dragOrigin.Y).Length < 6) return;
        _dragging = true;
        _dragScroll.Start();
        UpdateDropTarget();
    }

    /// <summary>What dropping one handle on another does: sections may trade places, tiles always land beside.</summary>
    private SectionDrop DropFor(Button source, Button target) =>
        Group(source) == 0 ? Arrangement.DropFor(RegionOf(source), RegionOf(target)) : SectionDrop.Place;

    /// <summary>
    /// Mark the handle of the tile under the pointer, with an arrow for the
    /// side the dragged one lands on, or the swap sign when the two trade
    /// places. A tile only counts where it can be seen.
    /// </summary>
    private void UpdateDropTarget()
    {
        if (_dragHandle is not { } source || !CanReorder(source) || !source.IsEffectivelyVisible)
        { ClearDropTarget(); return; }
        foreach (Button target in _dragTargets)
        {
            if (ReferenceEquals(source, target) || !target.IsEffectivelyVisible || TopLevel.GetTopLevel(target) != this
                || Card(target) is not { } card
                || card.TranslatePoint(default, this) is not { } origin) continue;
            var bounds = new Rect(origin, card.Bounds.Size);
            Rect visible = bounds.Intersect(new Rect(Bounds.Size));
            foreach (Control ancestor in card.GetVisualAncestors().OfType<Control>().Where(c => c.ClipToBounds))
                if (ancestor.TranslatePoint(default, this) is { } clip)
                    visible = visible.Intersect(new Rect(clip, ancestor.Bounds.Size));
            if (!visible.Contains(_dragPoint)) continue;
            bool swap = DropFor(source, target) == SectionDrop.Swap;
            bool along = AlongY(source);
            bool after = !swap && (along ? _dragPoint.Y > bounds.Center.Y : _dragPoint.X > bounds.Center.X);
            // Pointer moves and scroll ticks mostly land on the same side of
            // the same tile; leave its style and arrow alone then.
            if (ReferenceEquals(_dropHandle, target) && _dropAfter == after) return;
            if (!ReferenceEquals(_dropHandle, target))
            {
                ClearDropTarget();
                _dropHandle = target;
                target.Classes.Add("reorderTarget");
            }
            _dropAfter = after;
            target.Content = swap ? "⇄" : along ? (after ? "↓" : "↑") : (after ? "→" : "←");
            return;
        }
        ClearDropTarget();
    }

    private void ClearDropTarget()
    {
        if (_dropHandle is not { } target) return;
        ClearDropTarget(target);
        _dropHandle = null;
    }

    /// <summary>
    /// A handle at rest: no target style, and an arrow along the way its
    /// group moves, or the swap sign for a section in a grid or a dock, where
    /// every move is a trade of places.
    /// </summary>
    private void ClearDropTarget(Button target)
    {
        target.Classes.Remove("reorderTarget");
        target.Content = Group(target) == 0 && _arrangement is { } arrangement
            && arrangement.OrientationOf(RegionOf(target)) is null ? "⇄" : AlongY(target) ? "↕" : "↔";
    }

    /// <summary>
    /// True when a handle's group runs down the window: a section in a
    /// vertical stack, or a channel in the input rack or a send list. Sections
    /// in a row, and the channel and mix tiles, run across.
    /// </summary>
    private bool AlongY(Button handle) => Group(handle) == 0
        ? _arrangement?.OrientationOf(RegionOf(handle)) is null or SkinLayoutOrientation.Vertical
        : handle.Classes.Contains("alongY");

    private async void OnReorderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragHandle is not { } source || e.Pointer != _dragPointer) return;
        e.Handled = true;
        // Only the left button finishes a drag; another button released
        // meanwhile leaves it running.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        _dragPoint = e.GetPosition(this);
        UpdateDropTarget();
        Button? target = _dragging ? _dropHandle : null;
        bool after = _dropAfter;
        CancelReorder();
        if (target is not null) await PlaceTile(source, target, after);
    }

    private void CancelReorder()
    {
        _dragScroll.Stop();
        ClearDropTarget();
        IPointer? pointer = _dragPointer;
        _dragPointer = null;
        _dragHandle = null;
        _dragTargets = [];
        _dragging = false;
        pointer?.Capture(null);
    }

    private async void OnReorderKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragHandle is not null)
        {
            CancelReorder();
            e.Handled = true;
            return;
        }
        if (Handle(e.Source) is not { } source || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Enter or Key.Space && Group(source) == 0 && _arrangement?.Layout is not null)
        {
            e.Handled = true;
            if (CanReorder(source) && _dragHandle is null) OpenSwapMenu(source);
            return;
        }
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        e.Handled = true;
        if (!CanReorder(source) || _dragHandle is not null) return;
        if (Group(source) == 0 && _arrangement?.Layout is not null)
        {
            // Under a skin layout the arrows trade places with the next
            // section that way in the same container.
            string region = RegionOf(source);
            if (Arrangement.Neighbour(region, dx, dy, r => SectionHandle(SectionArrangement.TileFor(r))?.IsEffectivelyVisible == true)
                is { } neighbour)
            {
                SaveArrangement(Arrangement.Swap(region, neighbour));
                source.BringIntoView();
            }
            return;
        }
        int direction = dx + dy;
        var handles = Handles(Group(source)).ToList();
        int target = handles.IndexOf(source) + direction;
        if (target >= 0 && target < handles.Count)
        {
            await PlaceTile(source, handles[target], after: direction > 0);
            source.BringIntoView();
        }
    }

    /// <summary>
    /// A menu of every other section, by name, that the focused handle's
    /// section can trade places with: how a section crosses from one
    /// container to another from the keyboard.
    /// </summary>
    private void OpenSwapMenu(Button handle)
    {
        string source = RegionOf(handle);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (string target in Arrangement.Order.Select(t => SectionArrangement.RegionFor(t)!))
        {
            if (target == source || SectionHandle(SectionArrangement.TileFor(target))?.IsEffectivelyVisible != true) continue;
            var item = new MenuItem { Header = Localizer.Format("SwapWithSection", SectionTitle(target)), Tag = target };
            item.Click += (_, _) =>
            {
                if (!CanReorder(handle)) return;
                SaveArrangement(Arrangement.Swap(source, target));
                handle.BringIntoView();
                handle.Focus();
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        SwapMenu?.Hide();
        SwapMenu = menu;
        menu.ShowAt(handle);
    }

    private async Task PlaceTile(Button source, Button target, bool after)
    {
        if (!CanReorder(source) || !source.IsEffectivelyVisible || !target.IsEffectivelyVisible
            || TopLevel.GetTopLevel(source) != this || TopLevel.GetTopLevel(target) != this || Group(source) != Group(target)) return;
        if (Group(source) == 0)
        {
            SectionArrangement moved = Arrangement.Move(RegionOf(source), RegionOf(target), after);
            if (!Arrangement.SameStructure(moved)) SaveArrangement(moved);
            return;
        }
        if (DataContext is not MainViewModel vm) return;
        // One tile move at a time: the next one starts from the order the
        // daemon published for this one.
        _reorderSaving = true;
        try { ReportArrangement(await vm.PlaceInLayoutOrder(Id(source), Id(target), after, isMix: Group(source) == 2)); }
        finally { _reorderSaving = false; }
    }

    /// <summary>
    /// While a handle is held near the edge of a scrolled view, scroll it:
    /// the page vertically, and sideways the channel row or a tree of
    /// sections wider than the window, whichever the drag is in.
    /// </summary>
    private void ScrollReorder()
    {
        if (_dragHandle is not { } handle || !CanReorder(handle) || !handle.IsEffectivelyVisible)
        { CancelReorder(); return; }
        foreach (var scroll in handle.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroll.TranslatePoint(default, this) is not { } origin) continue;
            double top = Math.Max(0, origin.Y), bottom = Math.Min(Bounds.Height, origin.Y + scroll.Bounds.Height);
            double left = Math.Max(0, origin.X), right = Math.Min(Bounds.Width, origin.X + scroll.Bounds.Width);
            if (_dragPoint.X < left || _dragPoint.X > right || _dragPoint.Y < top || _dragPoint.Y > bottom) continue;
            double x = scroll.Offset.X, y = scroll.Offset.Y;
            if (scroll.Extent.Width > scroll.Viewport.Width && Group(handle) is 0 or 1)
                x += _dragPoint.X < left + 32 ? -16 : _dragPoint.X > right - 32 ? 16 : 0;
            if (scroll.Extent.Height > scroll.Viewport.Height)
                y += _dragPoint.Y < top + 32 ? -16 : _dragPoint.Y > bottom - 32 ? 16 : 0;
            scroll.Offset = new Vector(Math.Clamp(x, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                Math.Clamp(y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        UpdateDropTarget();
    }
}
