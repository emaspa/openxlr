using System;
using OpenXLR.UI.Localization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>The window's small confirmation and naming dialogs, shared by every window that asks before acting.</summary>
internal static class Dialogs
{
    /// <summary>Raised with each confirmation dialog before it shows, so window tests can answer it.</summary>
    internal static event Action<Window>? ConfirmOpening;

    /// <summary>
    /// The trimmed new name, or null when the user cancels. An empty name
    /// cannot be submitted. The button says Rename unless <paramref name="okLabel"/> names it.
    /// </summary>
    public static async Task<string?> NameAsync(Window owner, string title, string current, string hint, int maxLength, string? okLabel = null)
    {
        var input = new TextBox { Text = current, MinWidth = 340, MaxLength = maxLength, Name = "NameInput" };
        var ok = new Button { Content = okLabel ?? Localizer.Text("Rename"), IsDefault = true, Name = "NameOk" };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        string? result = null;
        var detail = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Classes = { "hint" } };
        detail.Bind(TextBlock.FontSizeProperty, detail.GetResourceObservable("UiText.FontSize11"));
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 12,
                Children =
                {
                    input,
                    detail,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            string clean = input.Text?.Trim() ?? "";
            if (clean.Length == 0) return;
            result = clean;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Whether to keep the capture channels, or null when disabling is cancelled.</summary>
    public static async Task<bool?> KeepCaptureChannelsAsync(Window owner, string unit, bool canKeep)
    {
        var keep = new CheckBox { IsVisible = canKeep,
            Content = new TextBlock { Text = Localizer.Text("KeepInterfaceCaptureChannels"), TextWrapping = TextWrapping.Wrap } };
        var disable = new Button { Content = Localizer.Text("DisableInterface"), IsDefault = true, Classes = { "danger" } };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = Localizer.Text("DisableInterface"),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 14, MaxWidth = 380,
                Children =
                {
                    new TextBlock { Text = canKeep ? Localizer.Format("DisableInterfaceCaptureDetail", unit)
                        : Localizer.Format("DisableInterfaceCaptureDeleteDetail", unit), TextWrapping = TextWrapping.Wrap },
                    keep,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, disable },
                    },
                },
            },
        };
        bool? result = null;
        disable.Click += (_, _) => { result = keep.IsChecked == true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// How to import a library archive over the clips there are: true adds
    /// its clips, false replaces the clips with them, null cancels.
    /// </summary>
    public static async Task<bool?> ImportModeAsync(Window owner, string file, int clips)
    {
        var add = new Button { Name = "SoundboardImportAdd", Content = Localizer.Text("SoundboardImportAdd"), IsDefault = true };
        var replace = new Button { Name = "SoundboardImportReplace", Content = Localizer.Text("SoundboardImportReplace"), Classes = { "danger" } };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = Localizer.Text("SoundboardImportLibrary"),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 14, MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = Localizer.Format("SoundboardImportModeMessage", file, clips), TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, replace, add },
                    },
                },
            },
        };
        bool? result = null;
        add.Click += (_, _) => { result = true; dialog.Close(); };
        replace.Click += (_, _) => { result = false; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Making <paramref name="device"/> the audio clock: true to choose it
    /// and restart the audio now, false to choose it and leave the restart
    /// for later, null to cancel.
    /// </summary>
    public static async Task<bool?> MakeClockAsync(Window owner, string device)
    {
        var now = new Button { Name = "MakeClockRestart", Content = Localizer.Text("RestartAudioNow"), IsDefault = true, Classes = { "danger" } };
        var later = new Button { Name = "MakeClockLater", Content = Localizer.Text("MakeClockLater") };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = Localizer.Format("MakeDeviceTheClock", device),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 14, MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = Localizer.Format("MakeClockConfirm", device), TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, later, now },
                    },
                },
            },
        };
        bool? result = null;
        now.Click += (_, _) => { result = true; dialog.Close(); };
        later.Click += (_, _) => { result = false; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        ConfirmOpening?.Invoke(dialog);
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>True when the user accepts; closing the dialog any other way is a no.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string yesLabel, string? noLabel = null)
    {
        var yes = new Button { Content = yesLabel, Classes = { "danger" } };
        var no = new Button { Content = noLabel ?? Localizer.Text("Cancel"), IsCancel = true, IsDefault = noLabel is not null };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { no, yes },
                    },
                },
            },
        };
        var done = new TaskCompletionSource<bool>();
        yes.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { done.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        ConfirmOpening?.Invoke(dialog);
        await dialog.ShowDialog(owner);
        return await done.Task;
    }
}
