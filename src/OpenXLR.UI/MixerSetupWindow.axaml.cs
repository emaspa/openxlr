using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// The layout editor: add, rename, reorder and delete application channels
/// and virtual microphones. Every action waits for the daemon's answer, which
/// arrives after the new layout is saved; the lists update from the state
/// push that precedes it, so nothing here is optimistic.
/// </summary>
public partial class MixerSetupWindow : Window
{
    public MixerSetupWindow() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnAddChannel(object? sender, RoutedEventArgs e)
    {
        string name = ChannelName.Text?.Trim() ?? "";
        if (name.Length == 0 || Vm is not { } vm) return;
        if (await Run(vm.CreateChannel(name))) ChannelName.Text = "";
    }

    private async void OnAddMix(object? sender, RoutedEventArgs e)
    {
        string name = MixName.Text?.Trim() ?? "";
        if (name.Length == 0 || Vm is not { } vm) return;
        if (await Run(vm.CreateMix(name))) MixName.Text = "";
    }

    private void OnChannelNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAddChannel(sender, e); e.Handled = true; }
    }

    private void OnMixNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAddMix(sender, e); e.Handled = true; }
    }

    private async void OnChannelUp(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is { } c && Vm is { } vm) await Run(vm.MoveChannel(c.Id, -1));
    }

    private async void OnChannelDown(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is { } c && Vm is { } vm) await Run(vm.MoveChannel(c.Id, +1));
    }

    private async void OnMixUp(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is { } m && Vm is { } vm) await Run(vm.MoveMix(m.Id, -1));
    }

    private async void OnMixDown(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is { } m && Vm is { } vm) await Run(vm.MoveMix(m.Id, +1));
    }

    private async void OnRenameChannel(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is not { } channel || Vm is not { } vm) return;
        string? name = await PromptName($"Rename channel '{channel.Name}'", channel.Name,
            "Programs playing into this channel keep playing; the playback device shows the new name right away.");
        if (name is not null && name != channel.Name) await Run(vm.RenameChannel(channel.Id, name));
    }

    private async void OnRenameMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        string? name = await PromptName($"Rename mix '{mix.Name}'", mix.Name,
            "OpenXLR shows the new name at once. Other applications keep listing the old microphone name until the daemon restarts, so nothing that is recording from it is interrupted.");
        if (name is not null && name != mix.Name) await Run(vm.RenameMix(mix.Id, name));
    }

    private async void OnDeleteChannel(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is not { } channel || Vm is not { } vm) return;
        if (await Confirm($"Delete channel '{channel.Name}'?",
                "Programs routed to it move to the first remaining application channel. Its playback device disappears from the desktop."))
            await Run(vm.DeleteChannel(channel.Id));
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        if (await Confirm($"Delete mix '{mix.Name}'?",
                "Its virtual microphone disappears; anything recording from it loses the device. Its sends and inserts go with it."))
            await Run(vm.DeleteMix(mix.Id));
    }

    private static T? Item<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

    /// <summary>Run one edit with the window locked, and surface its error here as well as in the status line.</summary>
    private async Task<bool> Run(Task<string?> edit)
    {
        IsEnabled = false;
        try
        {
            string? error = await edit;
            Note.Text = error ?? "";
            Note.IsVisible = error is not null;
            return error is null;
        }
        finally { IsEnabled = true; }
    }

    private async Task<string?> PromptName(string title, string current, string hint)
    {
        var input = new TextBox { Text = current, MinWidth = 340, MaxLength = 60 };
        var ok = new Button { Content = "Rename", IsDefault = true };
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
                Spacing = 12,
                Children =
                {
                    input,
                    new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 11,
                        Foreground = Brush.Parse("#8b93a7") },
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
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> Confirm(string title, string message)
    {
        var yes = new Button { Content = "Delete", Background = Brush.Parse("#a03434") };
        var no = new Button { Content = "Cancel", IsCancel = true };
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
                        Children = { no, yes },
                    },
                },
            },
        };
        yes.Click += (_, _) => { done.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { done.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => done.TrySetResult(false);
        await dialog.ShowDialog(this);
        return await done.Task;
    }

    private const string FileLimitManual =
        "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#open-files";

    private void OnFileLimitManual(object? sender, RoutedEventArgs e)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", FileLimitManual) { UseShellExecute = false });

    private async void OnRestartDaemon(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.DaemonRestart.RestartAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
