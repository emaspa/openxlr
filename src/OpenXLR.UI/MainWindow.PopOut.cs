using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

/// <summary>
/// Pop out: the one mixer panel moves into its own window and back, with its
/// data context held and the same daemon client. Where the user puts it is a
/// preference in ui.json (<see cref="UiSettings.MixerDocked"/>), written only
/// by the user's own Pop out and Dock. A skin's pop-out default comes after
/// it, in this precedence:
///
/// 1. the window's own reasons: no mixer, nothing to pop out;
/// 2. the user's docking choice, once there is one;
/// 3. the skin's <c>popOut.default</c>, applied once per skin and layout
///    version when that skin is put on with the main window showing;
/// 4. docked, as the mixer has always started.
///
/// Reading a skin again, or a change of colours or sizing, never opens or
/// closes the mixer's window.
/// </summary>
public partial class MainWindow
{
    private MixerWindow? _mixerWindow;
    private string? _wornSkin;
    private bool _popOutPending;

    /// <summary>The mixer's own window while the panel is in it, for the tests.</summary>
    internal MixerWindow? DetachedMixer => _mixerWindow;

    private void OnPopOutMixer(object? sender, RoutedEventArgs e)
    {
        PopOutMixer();
        SaveDocking(docked: false);
    }

    internal MixerWindow PopOutMixer()
    {
        if (_mixerWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        // A drag belongs to its original window and cannot survive moving the panel.
        CancelReorder();
        ArrangeButton.IsChecked = false;
        OnArrange(this, new RoutedEventArgs());
        var panel = (Control)MixerHost.Content!;
        // Keep inherited bindings alive while the panel has no parent.
        panel.DataContext = DataContext;
        MixerHost.Content = null;
        // Finish the old window's layout while the panel has no parent.
        // Pending work there must not arrange it under the new window.
        UpdateLayout();
        var window = new MixerWindow();
        _mixerWindow = window;
        window.Attach(panel, DataContext, this);
        // As in the main window: the last pointer pressed, so a skin switch can end a fader drag here too.
        window.AddHandler(PointerPressedEvent, (_, e) => _lastMixerPointer = e.Pointer,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        bool restoreMain = true;
        IReadOnlyList<Action<Window>> handover = [];
        window.Closing += (_, e) =>
        {
            restoreMain = e.CloseReason == WindowCloseReason.WindowClosing;
            // Plugin and EQ windows opened from the detached panel close with it;
            // note them so they open again under the main window.
            if (!_reallyExit && restoreMain) handover = [.. InsertWindows.Handover(window), .. EqWindows.Handover(window)];
        };
        window.Closed += (_, _) =>
        {
            MixerHost.Content = window.Detach();
            panel.ClearValue(DataContextProperty);
            _mixerWindow = null;
            _lastMixerPointer = null;
            DetachedMixerNotice.IsVisible = false;
            if (_reallyExit || !restoreMain) return;
            // Closing the mixer's window, by its Dock button or its frame, is the user docking it.
            SaveDocking(docked: true);
            ShowMixer();
            foreach (Action<Window> reopen in handover) reopen(this);
        };
        DetachedMixerNotice.IsVisible = true;
        window.Show();
        return window;
    }

    private void OnDockMixer(object? sender, RoutedEventArgs e) => _mixerWindow?.Close();

    /// <summary>Keep where the user put the mixer; a failed save is said once under Arrange.</summary>
    private void SaveDocking(bool docked)
    {
        UiSettings settings = UiSettings.Load();
        string? error = settings.MixerDocked == docked ? null : (settings with { MixerDocked = docked }).Save();
        ReportSaveIfChanged(WindowChoice.Docking, error is null ? null : Localizer.Format("DockingNotSaved", error));
    }

    /// <summary>
    /// Open the mixer's own window if the skin just put on asks for it and
    /// nothing the user chose stands in the way (see the precedence above).
    /// It waits while the main window is hidden and while there is no mixer,
    /// and is recorded once it has opened, so it opens once per skin layout.
    /// </summary>
    private void ApplyPendingPopOut()
    {
        if (!_popOutPending || _reallyExit || !IsVisible) return;
        if (_arrangement is not { Skin: { } skin, Version: { } version, Layout.PopOut: SkinMixerPlacement.Detached })
        {
            _popOutPending = false;
            return;
        }
        if (DataContext is not MainViewModel { HasMixer: true }) return;
        _popOutPending = false;
        UiSettings settings = UiSettings.Load();
        if (_mixerWindow is not null || settings.MixerDocked is not null || settings.HasAppliedSkinPopOut(skin, version)) return;
        PopOutMixer();
        string? error = settings.WithSkinPopOutApplied(skin, version).Save();
        if (error is not null) ReportSave(WindowChoice.Docking, Localizer.Format("DockingNotSaved", error));
    }
}
