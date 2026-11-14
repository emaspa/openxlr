using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// The header's undo and redo pair, the keys, and the flyout that names
/// what a step did. An undo can move a control in a folded section or in
/// another window, so the flyout says what changed wherever the step came
/// from: the buttons, Ctrl+Z here or in the separate mixer window.
/// </summary>
public partial class MainWindow
{
    private HistoryViewModel? _watchedHistory;

    private void SetupHistory()
    {
        HistoryKeys.Attach(this, redo => StepHistory(redo));
        DataContextChanged += (_, _) => WatchHistory();
        Closed += (_, _) => { if (_watchedHistory is not null) _watchedHistory.PropertyChanged -= OnHistoryChanged; };
        WatchHistory();
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => StepHistory(redo: false);

    private void OnRedo(object? sender, RoutedEventArgs e) => StepHistory(redo: true);

    private void StepHistory(bool redo)
    {
        if (DataContext is MainViewModel model) _ = model.StepHistoryAsync(redo);
    }

    private void WatchHistory()
    {
        HistoryViewModel? history = (DataContext as MainViewModel)?.History;
        if (ReferenceEquals(history, _watchedHistory)) return;
        if (_watchedHistory is not null) _watchedHistory.PropertyChanged -= OnHistoryChanged;
        _watchedHistory = history;
        if (history is not null) history.PropertyChanged += OnHistoryChanged;
        HistoryNoticeFlyout.Hide();
    }

    private FlyoutBase HistoryNoticeFlyout => FlyoutBase.GetAttachedFlyout(HistoryButtons)!;

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HistoryViewModel.Notice)) return;
        if (_watchedHistory?.Notice is null) { HistoryNoticeFlyout.Hide(); return; }
        if (HistoryNoticeAnchor() is { } anchor && !HistoryNoticeFlyout.IsOpen) HistoryNoticeFlyout.ShowAt(anchor);
    }

    /// <summary>
    /// Where the notice points: the pair in the row, or the overflow menu's
    /// button when a skin put the pair in the menu. None while the window is
    /// hidden or minimized, since a flyout needs a window on screen.
    /// </summary>
    internal Control? HistoryNoticeAnchor()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return null;
        if (HistoryButtons.IsEffectivelyVisible && ReferenceEquals(HistoryButtons.Parent, HeaderActions)) return HistoryButtons;
        return HeaderOverflow.IsEffectivelyVisible ? HeaderOverflow : null;
    }
}
