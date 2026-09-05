using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OpenXLR.UI;

public partial class MixerSetupWindow : Window
{
    public MixerSetupWindow() => InitializeComponent();

    private async void OnAddChannel(object? sender, RoutedEventArgs e)
    {
        string name = ChannelName.Text?.Trim() ?? "";
        if (name.Length == 0 || DataContext is not MainViewModel vm) return;
        if (await vm.CreateChannel(name)) ChannelName.Text = "";
    }

    private async void OnAddMix(object? sender, RoutedEventArgs e)
    {
        string name = MixName.Text?.Trim() ?? "";
        if (name.Length == 0 || DataContext is not MainViewModel vm) return;
        if (await vm.CreateMix(name)) MixName.Text = "";
    }

    private async void OnDeleteChannel(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChannelViewModel channel ||
            DataContext is not MainViewModel vm) return;
        if (await ConfirmDelete($"Delete channel ‘{channel.Name}’?",
                "Applications assigned to it will move to the first remaining application channel."))
            await vm.DeleteChannel(channel.Id);
    }

    private async void OnRenameChannel(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChannelViewModel channel ||
            DataContext is not MainViewModel vm) return;
        string? name = await PromptName($"Rename channel ‘{channel.Name}’", channel.Name);
        if (name is not null && name != channel.Name) await vm.RenameChannel(channel.Id, name);
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MixViewModel mix ||
            DataContext is not MainViewModel vm) return;
        if (await ConfirmDelete($"Delete output ‘{mix.Name}’?",
                "Its virtual microphone, sends, and insert chain will be removed from PipeWire."))
            await vm.DeleteMix(mix.Id);
    }

    private async void OnRenameMix(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MixViewModel mix ||
            DataContext is not MainViewModel vm) return;
        string? name = await PromptName($"Rename output ‘{mix.Name}’", mix.Name);
        if (name is not null && name != mix.Name) await vm.RenameMix(mix.Id, name);
    }

    private async Task<string?> PromptName(string title, string current)
    {
        var input = new TextBox { Text = current, MinWidth = 320, MaxLength = 60 };
        var save = new Button { Content = "Rename", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brush.Parse("#1d2027"),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, save },
                    },
                },
            },
        };
        save.Click += (_, _) =>
        {
            string clean = input.Text?.Trim() ?? "";
            if (clean.Length == 0) return;
            result = clean;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> ConfirmDelete(string title, string message)
    {
        var delete = new Button { Content = "Delete", Background = Brush.Parse("#a03434") };
        var cancel = new Button { Content = "Cancel" };
        var done = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Brush.Parse("#1d2027"),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, delete },
                    },
                },
            },
        };
        delete.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { done.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        await dialog.ShowDialog(this);
        return await done.Task;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
