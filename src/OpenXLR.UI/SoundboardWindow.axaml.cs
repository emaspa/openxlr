using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// The Soundboard window, opened from the Soundboard button in the SUBMIXER
/// header, from the tray menu and from Options. One is open at a time: a second
/// request raises it, or reopens it under the window that asked, since an
/// owned window cannot change its owner.
/// </summary>
public partial class SoundboardWindow : Window
{
    private static SoundboardWindow? _open;

    public SoundboardWindow()
    {
        InitializeComponent();
        // A key or a finger that comes up in another window never reaches the pad it pressed.
        Deactivated += (_, _) => ReleaseHeldPads();
        // Touch sizing, or a skin's own control minimum, enlarges the targets here as in the mixer.
        UpdateControlSizing();
        Skinning.SkinService.Changed += UpdateControlSizing;
        Closed += (_, _) => Skinning.SkinService.Changed -= UpdateControlSizing;
        // Sound files and folders from a file manager go through the same import as Add clips.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool takes = Board is { CanAdd: true } && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = takes ? DragDropEffects.Copy : DragDropEffects.None;
        if (Board is { } board) board.Dropping = takes;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (Board is { } board) board.Dropping = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Board is not { } board) return;
        board.Dropping = false;
        if (!board.CanAdd || e.DataTransfer.TryGetFiles() is not { Length: > 0 } items) return;
        e.DragEffects = DragDropEffects.Copy;
        // An item the desktop hands over without a path on this machine cannot be copied; say so by name.
        await board.ImportDroppedAsync([.. items.Select(LocalPath).OfType<string>()],
            [.. items.Where(i => LocalPath(i) is null).Select(i => i.Name)]);
    }

    private void UpdateControlSizing() => Classes.Set("large-targets", Skinning.SkinService.LargeTargets);

    /// <summary>End every press still holding a pad down, so no hold clip is left playing.</summary>
    internal void ReleaseHeldPads()
    {
        foreach (SoundPad pad in this.GetVisualDescendants().OfType<SoundPad>()) pad.ReleaseHeld();
    }

    private SoundboardViewModel? Board => DataContext as SoundboardViewModel;

    /// <summary>The open window, for the tests.</summary>
    internal static SoundboardWindow? Current => _open;

    /// <summary>
    /// Open the window centred over <paramref name="near"/>, or in the middle of the screen when the
    /// tray asks while the mixer is hidden. The window has no owner: an owned window is minimized
    /// with the mixer, and the window manager raises the mixer each time a pad is pressed.
    /// </summary>
    public static SoundboardWindow Open(Window? near, SoundboardViewModel board)
    {
        if (_open is { } open)
        {
            if (open.DataContext == board)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return open;
            }
            open.Close();
        }
        var window = new SoundboardWindow { DataContext = board };
        window.Closed += (_, _) => { if (_open == window) _open = null; };
        _open = window;
        if (near is { IsVisible: true } && near.WindowState != WindowState.Minimized)
        {
            double scale = near.DesktopScaling;
            Size frame = near.FrameSize ?? near.ClientSize;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                near.Position.X + (int)((frame.Width - window.Width) * scale / 2),
                near.Position.Y + (int)((frame.Height - window.Height) * scale / 2));
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return window;
    }

    private void OnStopAll(object? sender, RoutedEventArgs e) => Board?.StopAll();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnCancelImport(object? sender, RoutedEventArgs e) => Board?.CancelImport();

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanAdd: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Text("SoundboardPickFiles"),
            AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Text("SoundboardFileType"))
                {
                    Patterns = [.. board.Formats.Select(f => "*." + f)],
                    MimeTypes = [.. board.Formats.Select(f => f == "mp3" ? "audio/mpeg" : "audio/" + f)],
                },
                FilePickerFileTypes.All,
            ],
        });
        // A pick the desktop hands over without a path on this machine cannot be copied; say so by name.
        await board.ImportAsync([.. files.Select(LocalPath).OfType<string>()],
            [.. files.Where(f => LocalPath(f) is null).Select(f => f.Name)]);
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanAdd: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Music);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Text("SoundboardPickFolder"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 0) return;
        if (LocalPath(folders[0]) is { } folder) await board.ImportFolderAsync(folder);
        else await board.ImportAsync([], [folders[0].Name]);
    }

    private void OnPage(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundPageViewModel page) Board?.ShowPage(page);
    }

    /// <summary>The shown page's menu: rename it, or move its clips to the default page.</summary>
    private void OnPageMenu(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanEditPage: true } board) return;
        PageMenu(board).ShowAt((Control)sender!);
    }

    internal MenuFlyout PageMenu(SoundboardViewModel board)
    {
        var menu = new MenuFlyout();
        var rename = new MenuItem { Name = "SoundboardRenamePage", Header = Localizer.Text("SoundboardRenamePage") };
        rename.Click += async (_, _) =>
        {
            if (await Dialogs.NameAsync(this, Localizer.Format("SoundboardRenamePageTitle", board.SelectedPageTitle), board.SelectedPage,
                    Localizer.Text("SoundboardRenamePageHint"), SoundboardViewModel.MaxPageLength) is { } name)
                await board.RenamePageAsync(name);
        };
        var remove = new MenuItem { Name = "SoundboardRemovePage", Header = Localizer.Format("SoundboardRemovePage", SoundboardViewModel.PageTitle("")) };
        remove.Click += async (_, _) => await board.RemovePageAsync();
        menu.Items.Add(rename);
        menu.Items.Add(remove);
        return menu;
    }

    private async void OnExportLibrary(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanExport: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Localizer.Text("SoundboardPickExport"),
            SuggestedStartLocation = start,
            SuggestedFileName = "soundboard.zip",
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [ArchiveType()],
        });
        if (file is null) return;
        if (LocalPath(file) is not { } path)
        {
            board.ShowReport(Localizer.Format("SoundboardFileUnreadable", file.Name), true);
            return;
        }
        // The name is the one picked, which the overwrite question and a
        // desktop portal's grant were for; one without .zip is refused there.
        await board.ExportLibraryAsync(path);
    }

    private async void OnImportLibrary(object? sender, RoutedEventArgs e)
    {
        if (Board is not { CanAdd: true } board) return;
        IStorageFolder? start = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Text("SoundboardPickImport"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [ArchiveType(), FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        if (LocalPath(files[0]) is not { } path)
        {
            board.ShowReport(Localizer.Format("SoundboardFileUnreadable", files[0].Name), true);
            return;
        }
        // An empty board has nothing to keep, so the clips are added without asking.
        bool? merge = board.HasClips ? await Dialogs.ImportModeAsync(this, files[0].Name, board.Clips.Count) : true;
        if (merge is bool adding) await board.ImportLibraryAsync(path, adding);
    }

    private async void OnRestoreStarter(object? sender, RoutedEventArgs e)
    {
        if (Board is { CanAdd: true } board) await board.RestoreStarterSoundsAsync();
    }

    private static FilePickerFileType ArchiveType() => new(Localizer.Text("SoundboardArchiveType"))
    {
        Patterns = ["*.zip"],
        MimeTypes = ["application/zip"],
    };

    private static string? LocalPath(IStorageItem item)
    {
        try { return item.TryGetLocalPath(); }
        catch (Exception) { return null; }
    }

    /// <summary>A pad's menu: edit, audition, stop, move earlier or later, delete.</summary>
    private void OnPadMenu(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SoundClipViewModel clip || Board is not { } board) return;
        PadMenu(board, clip).ShowAt((Control)sender!);
    }

    /// <summary>The menu a pad's button opens, built for that clip.</summary>
    internal MenuFlyout PadMenu(SoundboardViewModel board, SoundClipViewModel clip)
    {
        var menu = new MenuFlyout();
        MenuItem Item(string name, string header, bool enabled, Action act)
        {
            var item = new MenuItem { Name = name, Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
            return item;
        }
        Item("SoundClipEdit", Localizer.Text("SoundClipEdit"), true, () => _ = EditAsync(clip));
        // The clip in the monitor mixes alone, to hear it before the stream does.
        // The entry does what it said when the menu opened, even if the
        // audition ended or started meanwhile.
        bool stopsAudition = clip.Auditioned;
        MenuItem audition = Item("SoundClipAudition", stopsAudition ? Localizer.Text("SoundClipStopAudition") : Localizer.Text("SoundClipAudition"),
            stopsAudition || clip.CanPlay && board.AuditionNote is null, () => { if (stopsAudition) board.StopAudition(); else board.Audition(clip); });
        // While the Wave XLR Pro builds the mixes its jacks play, the entry says why it is off.
        if (!stopsAudition && board.AuditionNote is { } why)
        {
            ToolTip.SetTip(audition, why);
            ToolTip.SetShowOnDisabled(audition, true);
        }
        // Stop ends the clip's voices and its audition alike.
        Item("SoundClipStop", Localizer.Text("SoundClipStop"), clip.Playing || clip.Auditioned, () => board.Stop(clip));
        Item("SoundClipEarlier", Localizer.Text("SoundClipMoveEarlier"), board.CanMove(clip, -1), () => _ = MoveAsync(board, clip, -1));
        Item("SoundClipLater", Localizer.Text("SoundClipMoveLater"), board.CanMove(clip, +1), () => _ = MoveAsync(board, clip, +1));
        Item("SoundClipDelete", Localizer.Text("Delete"), true, () => _ = DeleteAsync(clip));
        return menu;
    }

    /// <summary>Move a clip and keep the keyboard on its pad, wherever the pad now stands.</summary>
    internal async Task MoveAsync(SoundboardViewModel board, SoundClipViewModel clip, int delta)
    {
        Task moving = board.Move(clip, delta);
        FocusPad(clip);
        await moving;
        FocusPad(clip);
    }

    private void FocusPad(SoundClipViewModel clip) => Dispatcher.UIThread.Post(() =>
        this.GetVisualDescendants().OfType<SoundPad>().FirstOrDefault(p => p.DataContext == clip)?.Focus(), DispatcherPriority.Loaded);

    /// <summary>The clip's edit window, owned by this one.</summary>
    internal Task EditAsync(SoundClipViewModel clip) => new SoundClipWindow(clip).ShowDialog(this);

    private async Task DeleteAsync(SoundClipViewModel clip)
    {
        if (Board is not { } board) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("SoundClipDeleteTitle"),
                Localizer.Format("SoundClipDeleteMessage", clip.Name), Localizer.Text("Delete")))
            await board.DeleteAsync(clip);
    }
}
