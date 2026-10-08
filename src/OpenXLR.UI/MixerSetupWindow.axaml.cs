using System;
using System.Linq;
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

    private async void OnAddCapture(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var name = new TextBox { Name = "CaptureName", MaxLength = 60, PlaceholderText = "Channel name" };
        var source = new ComboBox { Name = "CaptureSource", PlaceholderText = "Capture source", ItemsSource = vm.Inputs.Where(d => !d.IsOwn).ToArray(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var pair = new ComboBox { Name = "CapturePair", ItemsSource = Enumerable.Range(1, 32).Select(n => $"Pair {n}").ToArray(), SelectedIndex = 0 };
        var add = new Button { Name = "CreateCapture", Content = "Add input", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = "Add capture input", Width = 480, Height = 340, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "dialog" },
            Content = new ScrollViewer { Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 12,
                Children = { name, source, pair,
                    new TextBlock { Text = "Choose a microphone, capture card or another Wave interface. Pair 1 also works for mono sources. The new input starts muted in every mix and stays silent while its source is offline.", TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { cancel, add } } },
            } },
        };
        bool accepted = false;
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || source.SelectedItem is not AudioDeviceItem || pair.SelectedIndex < 0) return;
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        if (accepted && source.SelectedItem is AudioDeviceItem selected)
            await Run(vm.CreateCaptureChannel(name.Text!.Trim(), selected.Name, pair.SelectedIndex));
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
            channel.IsApplication ? "Programs playing into this channel keep playing; the playback device shows the new name right away."
                : "The capture source and the channel's routing stay unchanged.");
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
                channel.IsApplication ? "Programs routed to it move to the first remaining application channel. Its playback device disappears from the desktop."
                    : "The capture input and its sends are removed. The source device remains available to other applications."))
            await Run(vm.DeleteChannel(channel.Id));
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        if (await Confirm($"Delete mix '{mix.Name}'?",
                "Its virtual microphone disappears; anything recording from it loses the device. Its sends and inserts go with it."))
            await Run(vm.DeleteMix(mix.Id));
    }

    private async void OnAppearance(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var channel = Item<ChannelViewModel>(sender);
        var mix = Item<MixViewModel>(sender);
        var appearance = channel?.Appearance ?? mix?.Appearance;
        if (appearance is null) return;
        var icon = new ComboBox { ItemsSource = LayoutAppearanceViewModel.Icons, SelectedItem = appearance.Icon };
        var colour = new TextBox { Text = appearance.Colour ?? "", PlaceholderText = "#RRGGBB, blank uses the skin", MaxLength = 7 };
        var hidden = new CheckBox { Content = "Hide channel in the full mixer (audio keeps playing)", IsChecked = appearance.Hidden, IsVisible = channel is not null };
        var save = new Button { Content = "Save", IsDefault = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var dialog = new Window
        {
            Title = "Mixer appearance", Width = 430, Height = 330, MinWidth = 350, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "dialog" },
            Content = new ScrollViewer { Content = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 12,
                Children = { new TextBlock { Text = "Icon" }, icon, new TextBlock { Text = "Colour" }, colour, hidden, note, save } } },
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                string? error = await vm.SetLayoutAppearance(channel?.Id ?? mix!.Id, mix is not null,
                    icon.SelectedItem as string ?? "", string.IsNullOrWhiteSpace(colour.Text) ? null : colour.Text.Trim(), hidden.IsChecked == true);
                if (error is null) dialog.Close(); else note.Text = error;
            }
            finally { save.IsEnabled = true; }
        };
        await dialog.ShowDialog(this);
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
        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> Confirm(string title, string message)
    {
        var yes = new Button { Content = "Delete", Classes = { "danger" } };
        var no = new Button { Content = "Cancel", IsCancel = true };
        var done = new TaskCompletionSource<bool>();
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

    private void OnFileLimitManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(FileLimitManual);

    private async void OnRestartDaemon(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.DaemonRestart.RestartAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
