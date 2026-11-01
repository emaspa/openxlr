using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// One microphone's EQ editor. Closing it ends a comparison, so the EQ is
/// never left bypassed by a window that is gone. Its profile row has a model
/// of its own over the saved profiles, made for the EQ the editor shows.
/// </summary>
public partial class EqWindow : Window
{
    private bool _closed;

    public EqWindow()
    {
        InitializeComponent();
        // Until the editor has its EQ the row has no model, rather than the window's.
        ProfileRow.DataContext = null;
        Closing += (_, _) => (DataContext as MicEqViewModel)?.EndComparison();
        Closed += (_, _) =>
        {
            _closed = true;
            Profiles?.Dispose();
        };
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
        Opened += (_, _) => Dispatcher.UIThread.Post(FitBands, DispatcherPriority.Loaded);
        // The file may have been edited by hand meanwhile: read it again when the editor comes forward.
        Activated += (_, _) => Profiles?.Reread();
        DataContextChanged += (_, _) =>
        {
            Profiles?.Dispose();
            ProfileRow.DataContext = DataContext is MicEqViewModel eq && !_closed ? new EqProfilesViewModel(eq) : null;
        };
    }

    /// <summary>Below this width the number fields drop their spin buttons, so three of them keep their values whole.</summary>
    public const double NarrowWidth = 760;

    /// <summary>The response graph.</summary>
    public EqGraph Graph => this.FindControl<EqGraph>("EqGraph")!;

    private StackPanel ProfileRow => this.FindControl<StackPanel>("EqProfileRow")!;

    private ScrollViewer BandScroll => this.FindControl<ScrollViewer>("EqBandScroll")!;

    /// <summary>
    /// Grow the editor once, as it opens, until every band row shows without
    /// scrolling, never past the screen it is on, and keep its frame inside
    /// that screen. The graph and the bands share the height the other rows
    /// leave in equal parts, so the window grows by twice what the bands lack.
    /// Past the screen's height the bands scroll.
    /// </summary>
    internal void FitBands()
    {
        if (_closed) return;
        UpdateLayout();
        double missing = BandScroll.Extent.Height - BandScroll.Viewport.Height;
        if (missing < 1) return;
        double wanted = ClientSize.Height + 2 * missing;
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            Size frame = FrameSize ?? ClientSize;
            double decoration = Math.Max(0, frame.Height - ClientSize.Height);
            wanted = Math.Min(wanted, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling - decoration));
            Height = Math.Ceiling(wanted);
            int height = (int)Math.Ceiling((Height + decoration) * screen.Scaling);
            PixelRect area = screen.WorkingArea;
            if (Position.Y + height > area.Bottom) Position = Position.WithY(Math.Max(area.Y, area.Bottom - height));
        }
        else Height = Math.Ceiling(wanted);
    }

    /// <summary>The saved profiles as this editor lists them; null before it has an EQ.</summary>
    public EqProfilesViewModel? Profiles => ProfileRow.DataContext as EqProfilesViewModel;

    private void OnProcessOnUnit(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.ChooseProcessing("device");

    private void OnProcessInSoftware(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.ChooseProcessing("software");

    private void OnAddBand(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.AddBand();

    private void OnRemoveBand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EqBandViewModel band }) band.Owner.RemoveBand(band);
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => (DataContext as MicEqViewModel)?.Paste();

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MicEqViewModel eq) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("EqReset"), Localizer.Format("EqResetConfirm", eq.ChannelName), Localizer.Text("EqReset")))
            eq.Reset();
    }

    private void OnProfileLoad(object? sender, RoutedEventArgs e) => Profiles?.Load();

    /// <summary>Ask for a name, the chosen profile's to start with, and before replacing a profile of that name.</summary>
    private async void OnProfileSave(object? sender, RoutedEventArgs e)
    {
        if (Profiles is not { } profiles) return;
        string? name = await Dialogs.NameAsync(this, Localizer.Text("EqProfileSaveTitle"), profiles.Selected?.Name ?? "",
            Localizer.Format("EqProfileNameHint", EqProfiles.MaximumNameLength), EqProfiles.MaximumNameLength, Localizer.Text("Save"));
        if (name is null || _closed) return;
        bool replace = false;
        if (profiles.Find(name) is { } existing)
        {
            if (!await Dialogs.ConfirmAsync(this, Localizer.Text("EqProfileReplaceTitle"),
                    Localizer.Format("EqProfileReplaceConfirm", existing.Name), Localizer.Text("EqProfileReplace")) || _closed)
                return;
            replace = true;
        }
        profiles.Save(name, replace);
    }

    private async void OnProfileRename(object? sender, RoutedEventArgs e)
    {
        if (Profiles is not { Selected: { } profile } profiles) return;
        string? name = await Dialogs.NameAsync(this, Localizer.Format("EqProfileRenameTitle", profile.Name), profile.Name,
            Localizer.Format("EqProfileNameHint", EqProfiles.MaximumNameLength), EqProfiles.MaximumNameLength);
        if (name is null || _closed || name == profile.Name) return;
        profiles.Rename(profile, name);
    }

    private async void OnProfileDelete(object? sender, RoutedEventArgs e)
    {
        if (Profiles is not { Selected: { } profile } profiles) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("EqProfileDeleteTitle"), Localizer.Format("EqProfileDeleteConfirm", profile.Name),
                Localizer.Text("Delete")) && !_closed)
            profiles.Delete(profile);
    }
}
