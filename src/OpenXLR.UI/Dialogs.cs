using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>The window's small confirmation and naming dialogs, shared by every window that asks before acting.</summary>
internal static class Dialogs
{
    /// <summary>The trimmed new name, or null when the user cancels. An empty name cannot be submitted.</summary>
    public static async Task<string?> NameAsync(Window owner, string title, string current, string hint, int maxLength)
    {
        var input = new TextBox { Text = current, MinWidth = 340, MaxLength = maxLength };
        var ok = new Button { Content = "Rename", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? result = null;
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
                    new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 11,
                        Classes = { "hint" } },
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

    /// <summary>True when the user accepts; closing the dialog any other way is a no.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string yesLabel)
    {
        var yes = new Button { Content = yesLabel, Classes = { "danger" } };
        var no = new Button { Content = "Cancel", IsCancel = true };
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
        await dialog.ShowDialog(owner);
        return await done.Task;
    }
}
