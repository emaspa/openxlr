using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenXLR.UI;

public partial class MainWindow
{
    private const string ArrangeHint = "Drag a handle to another tile, or focus it and use arrow keys. Escape cancels a drag.";
    private Button? _dragHandle;
    private Button? _dropHandle;
    private Button[] _dragTargets = [];
    private IPointer? _dragPointer;
    private Point _dragOrigin, _dragPoint;
    private bool _dragging, _dropAfter, _reorderSaving;
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void SetupReordering()
    {
        ApplySectionOrder(UiSettings.Load().SectionOrder);
        // Intercept handles before the expander's press-to-toggle header. Other
        // controls, including faders and the header's chevron, keep their input.
        AddHandler(PointerPressedEvent, OnReorderPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnReorderMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReorderReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => CancelReorder(), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnReorderKey, RoutingStrategies.Tunnel);
        _dragScroll.Tick += (_, _) => ScrollReorder();
        Deactivated += (_, _) => CancelReorder();
        Closed += (_, _) => CancelReorder();
    }

    private void OnArrange(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        bool arranging = ArrangeButton.IsChecked == true;
        Classes.Set("arranging", arranging);
        ResetSectionsButton.IsVisible = arranging;
        ReportArrangement(null);
    }

    private void OnResetSections(object? sender, RoutedEventArgs e)
    {
        CancelReorder();
        SaveSectionOrder(SectionTiles);
    }

    private void ReportArrangement(string? error)
    {
        ArrangementNote.Text = error ?? ArrangeHint;
        ArrangementNote.IsVisible = error is not null || ArrangeButton.IsChecked == true;
    }

    private void ApplySectionOrder(IEnumerable<string>? order)
    {
        foreach (var (id, index) in DisplayOrder.Complete(order, SectionTiles).Select((id, index) => (id, index)))
            if (this.FindControl<Expander>(id)?.Parent is Control card)
                SectionCards.Children.Move(SectionCards.Children.IndexOf(card), index);
    }

    private string[] CurrentSections() => SectionCards.Children.OfType<Border>()
        .Select(card => ((Expander)card.Child!).Name!).ToArray();

    private void SaveSectionOrder(IReadOnlyList<string> order)
    {
        try
        {
            // A failed save leaves the displayed order as it was. Reload the
            // other preferences so a skin change or collapsed tile stays saved.
            (UiSettings.Load() with { SectionOrder = order }).SaveChecked();
            ApplySectionOrder(order);
            ReportArrangement(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportArrangement("Could not save the section order: " + ex.Message);
        }
    }

    private static Button? Handle(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("reorderHandle")) : null;

    private static int Group(Button handle) => handle.Tag switch
    {
        string => 0, ChannelViewModel => 1, MixViewModel => 2, _ => -1,
    };

    private static string Id(Button handle) => handle.Tag switch
    {
        string id => id, ChannelViewModel channel => channel.Id, MixViewModel mix => mix.Id, _ => "",
    };

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
        _dragTargets = Handles(Group(handle)).ToArray();
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
            bool after = Group(source) == 0 ? _dragPoint.Y > bounds.Center.Y : _dragPoint.X > bounds.Center.X;
            // Timer ticks and pointer moves often keep the same destination.
            // Do not invalidate its styling and content unless it changes.
            if (ReferenceEquals(_dropHandle, target) && _dropAfter == after) return;
            if (!ReferenceEquals(_dropHandle, target))
            {
                ClearDropTarget();
                _dropHandle = target;
                target.Classes.Add("reorderTarget");
            }
            _dropAfter = after;
            target.Content = Group(source) == 0 ? (_dropAfter ? "↓" : "↑") : (_dropAfter ? "→" : "←");
            return;
        }
        ClearDropTarget();
    }

    private void ClearDropTarget()
    {
        if (_dropHandle is not { } target) return;
        target.Classes.Remove("reorderTarget");
        target.Content = Group(target) == 0 ? "↕" : "↔";
        _dropHandle = null;
    }

    private async void OnReorderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragHandle is not { } source || e.Pointer != _dragPointer) return;
        e.Handled = true;
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
        int direction = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (direction == 0) return;
        e.Handled = true;
        if (!CanReorder(source) || _dragHandle is not null) return;
        var handles = Handles(Group(source)).ToList();
        int target = handles.IndexOf(source) + direction;
        if (target >= 0 && target < handles.Count)
        {
            await PlaceTile(source, handles[target], after: direction > 0);
            source.BringIntoView();
        }
    }

    private async Task PlaceTile(Button source, Button target, bool after)
    {
        if (!CanReorder(source) || !source.IsEffectivelyVisible || !target.IsEffectivelyVisible
            || TopLevel.GetTopLevel(source) != this || TopLevel.GetTopLevel(target) != this || Group(source) != Group(target)) return;
        if (Group(source) == 0)
        {
            string[] order = CurrentSections();
            string[] updated = DisplayOrder.Place(order, Id(source), Id(target), after);
            if (!order.SequenceEqual(updated)) SaveSectionOrder(updated);
            return;
        }
        _reorderSaving = true;
        try
        {
            if (DataContext is MainViewModel vm)
            {
                string? error = await vm.PlaceDisplayItem(Id(source), Id(target), after, isMix: Group(source) == 2);
                ReportArrangement(error);
            }
        }
        finally { _reorderSaving = false; }
    }

    private void ScrollReorder()
    {
        if (_dragHandle is not { } handle || !CanReorder(handle) || !handle.IsEffectivelyVisible)
        { CancelReorder(); return; }
        // Scroll only while dragging near an edge. No idle timer or audio work.
        // Channels have their own horizontal viewport inside the page viewport.
        foreach (var scroll in handle.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroll.TranslatePoint(default, this) is not { } origin) continue;
            double top = Math.Max(0, origin.Y), bottom = Math.Min(Bounds.Height, origin.Y + scroll.Bounds.Height);
            double left = Math.Max(0, origin.X), right = Math.Min(Bounds.Width, origin.X + scroll.Bounds.Width);
            if (_dragPoint.X < left || _dragPoint.X > right || _dragPoint.Y < top || _dragPoint.Y > bottom) continue;
            double x = scroll.Offset.X, y = scroll.Offset.Y;
            if (scroll.Extent.Width > scroll.Viewport.Width && Group(handle) == 1)
                x += _dragPoint.X < left + 32 ? -16 : _dragPoint.X > right - 32 ? 16 : 0;
            if (scroll.Extent.Height > scroll.Viewport.Height)
                y += _dragPoint.Y < top + 32 ? -16 : _dragPoint.Y > bottom - 32 ? 16 : 0;
            scroll.Offset = new Vector(Math.Clamp(x, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                Math.Clamp(y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        UpdateDropTarget();
    }
}
