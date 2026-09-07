using System.Threading.Tasks;
using Avalonia.Controls;

namespace OpenXLR.UI;

/// <summary>The window's small confirmation dialog, shared by every window that asks before acting.</summary>
internal static class Dialogs
{
    /// <summary>True when the user accepts; closing the dialog any other way is a no.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string yesLabel)
    {
        var yes = new Button { Content = yesLabel, Background = Avalonia.Media.Brush.Parse("#a03434") };
        var no = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brush.Parse("#1d2027"),
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
