using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenXLR.UI;

public partial class MixerSetupWindow : Window
{
    private readonly DaemonClient _client = new();
    private MainViewModel? _main;
    private bool _editorConnected;
    private bool _busy;

    public MixerSetupWindow()
    {
        InitializeComponent();
        _client.ErrorReceived += message => Dispatcher.UIThread.Post(() => StatusText.Text = message);
        _client.ConnectionChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            _editorConnected = connected;
            Refresh();
        });
        Opened += (_, _) =>
        {
            _main = DataContext as MainViewModel;
            if (_main is not null)
            {
                _main.StateApplied += Refresh;
                Refresh();
            }
            _client.Start();
        };
        Closed += (_, _) =>
        {
            if (_main is not null) _main.StateApplied -= Refresh;
            _ = _client.DisposeAsync().AsTask();
        };
    }

    private void Refresh()
    {
        if (_main is null) return;
        ChannelItems.ItemsSource = _main.Channels
            .Where(channel => channel.Id is not ("xlr1" or "xlr2" or "aux"))
            .ToList();
        MixItems.ItemsSource = _main.Mixes
            .Where(mix => mix.Kind == "virtualMic")
            .ToList();

        bool ready = _main.DaemonConnected && _main.HasMixer && _editorConnected && !_busy;
        EditorPanel.IsEnabled = ready;
        if (!_main.DaemonConnected || !_editorConnected)
            StatusText.Text = "Connecting to the daemon…";
        else if (!_main.HasMixer)
            StatusText.Text = "Enable the submixer before editing its layout.";
    }

    private async void OnAddChannel(object? sender, RoutedEventArgs e)
    {
        string name = ChannelName.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        if (await RunEdit(() => _client.CreateChannelAsync(name), "Channel added and saved."))
            ChannelName.Text = "";
    }

    private async void OnAddMix(object? sender, RoutedEventArgs e)
    {
        string name = MixName.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        if (await RunEdit(() => _client.CreateMixAsync(name),
                "Virtual output added and saved. The OpenXLR graph was rebuilt."))
            MixName.Text = "";
    }

    private async void OnRenameChannel(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChannelViewModel channel) return;
        string? name = await PromptName($"Rename channel ‘{channel.Name}’", channel.Name);
        if (name is null || name == channel.Name) return;
        await RunEdit(() => _client.RenameChannelAsync(channel.Id, name),
            "Channel label saved. Its stable ID and audio node were not recreated; the desktop device description updates after daemon restart.");
    }

    private async void OnDeleteChannel(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChannelViewModel channel) return;
        if (!await ConfirmDelete($"Delete channel ‘{channel.Name}’?",
                "Applications assigned to it will move to the first remaining application channel. OpenXLR audio may pause briefly while the matrix is rebuilt.")) return;
        await RunEdit(() => _client.DeleteChannelAsync(channel.Id),
            "Channel deleted and assignments repaired. The OpenXLR graph was rebuilt.");
    }

    private async void OnRenameMix(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MixViewModel mix) return;
        string? name = await PromptName($"Rename output ‘{mix.Name}’", mix.Name);
        if (name is null || name == mix.Name) return;
        await RunEdit(() => _client.RenameMixAsync(mix.Id, name),
            "Output label saved. Its stable ID and virtual-microphone node were not recreated; the desktop device description updates after daemon restart.");
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MixViewModel mix) return;
        if (!await ConfirmDelete($"Delete output ‘{mix.Name}’?",
                "Its sends, master state and insert chain will be removed. OpenXLR audio may pause briefly while the matrix is rebuilt.")) return;
        await RunEdit(() => _client.DeleteMixAsync(mix.Id),
            "Virtual output deleted and saved. The OpenXLR graph was rebuilt.");
    }

    private async Task<bool> RunEdit(Func<Task<string?>> action, string success)
    {
        if (_main is null || !_main.DaemonConnected || !_main.HasMixer || !_editorConnected || _busy)
            return false;
        _busy = true;
        Refresh();
        StatusText.Text = "Applying and saving layout…";
        try
        {
            string? error = await action();
            StatusText.Text = error ?? success;
            return error is null;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
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
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
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
