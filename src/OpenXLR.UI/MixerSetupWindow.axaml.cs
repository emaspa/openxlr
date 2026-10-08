using OpenXLR.UI.Localization;
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
/// and user mixes. Every action waits for the daemon's answer, which
/// arrives after the new layout is saved; the lists update from the state
/// push that precedes it, so nothing here is optimistic.
/// </summary>
public partial class MixerSetupWindow : Window
{
    public MixerSetupWindow() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnExclusiveGroups(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await new ExclusiveGroupsWindow(vm).ShowDialog(this);
    }

    private async void OnAddChannel(object? sender, RoutedEventArgs e)
    {
        string name = ChannelName.Text?.Trim() ?? "";
        if (name.Length == 0 || Vm is not { } vm) return;
        if (await Run(vm.CreateChannel(name))) ChannelName.Text = "";
    }

    private async void OnAddCapture(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var name = new TextBox { Name = "CaptureName", MaxLength = 60, PlaceholderText = Localizer.Text("ChannelNamePlaceholder") };
        var source = new ComboBox { Name = "CaptureSource", PlaceholderText = Localizer.Text("CaptureSourcePlaceholder"), ItemsSource = vm.Inputs.Where(d => !d.IsOwn).ToArray(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var pair = new ComboBox { Name = "CapturePair", ItemsSource = Enumerable.Range(1, 32).Select(n => Localizer.Format("CapturePairNumber", n)).ToArray(), SelectedIndex = 0 };
        var add = new Button { Name = "CreateCapture", Content = Localizer.Text("AddInput"), IsDefault = true };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var dialog = new Window
        {
            Title = Localizer.Text("AddCaptureInput"), Width = 480, Height = 340, MinWidth = 360, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "dialog" },
            Content = new ScrollViewer { Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 12,
                Children = { name, source, pair,
                    new TextBlock { Text = Localizer.Text("AddCaptureInputHint"), TextWrapping = TextWrapping.Wrap, Classes = { "hint" } },
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
        if (await Run(vm.CreateMix(name, NewMixKind.SelectedIndex == 1 ? "monitor" : "virtualMic"))) MixName.Text = "";
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
        string? name = await PromptName(Localizer.Format("RenameChannelTitle", channel.Name), channel.Name,
            channel.IsApplication ? Localizer.Text("RenameAppChannelHint")
                : Localizer.Text("RenameCaptureChannelHint"));
        if (name is not null && name != channel.Name) await Run(vm.RenameChannel(channel.Id, name));
    }

    private async void OnRenameMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        string? name = await PromptName(Localizer.Format("RenameMixTitle", mix.Name), mix.Name,
            Localizer.Text("RenameMixHint"));
        if (name is not null && name != mix.Name) await Run(vm.RenameMix(mix.Id, name));
    }

    private async void OnDeleteChannel(object? sender, RoutedEventArgs e)
    {
        if (Item<ChannelViewModel>(sender) is not { } channel || Vm is not { } vm) return;
        if (await Confirm(Localizer.Format("DeleteChannelTitle", channel.Name),
                channel.IsApplication ? Localizer.Format("DeleteAppChannelDetail", FallbackAfterDeleting(vm, channel))
                    : Localizer.Text("DeleteCaptureChannelDetail")))
            await Run(vm.DeleteChannel(channel.Id));
    }

    private async void OnDeleteMix(object? sender, RoutedEventArgs e)
    {
        if (Item<MixViewModel>(sender) is not { } mix || Vm is not { } vm) return;
        if (await Confirm(Localizer.Format("DeleteMixTitle", mix.Name),
                mix.IsMonitor
                    ? Localizer.Text("DeleteMonitorMixDetail")
                    : Localizer.Text("DeleteVirtualMicDetail")))
            await Run(vm.DeleteMix(mix.Id));
    }

    /// <summary>The channel the daemon moves a deleted channel's programs to: System, else the first by id.</summary>
    private static string FallbackAfterDeleting(MainViewModel vm, ChannelViewModel deleted)
    {
        var rest = vm.Channels.Where(c => c.IsApplication && c.Id != deleted.Id).ToList();
        ChannelViewModel? fallback = rest.FirstOrDefault(c => c.Id == "system") ?? rest.MinBy(c => c.Id, StringComparer.Ordinal);
        return fallback?.Name ?? Localizer.Text("AnotherApplicationChannel");
    }

    private async void OnAppearance(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        ChannelViewModel? channel = Item<ChannelViewModel>(sender);
        MixViewModel? mix = Item<MixViewModel>(sender);
        LayoutAppearanceViewModel? appearance = channel?.Appearance ?? mix?.Appearance;
        if (appearance is null) return;
        string name = channel?.Name ?? mix!.Name;
        var icon = new ComboBox { Name = "AppearanceIcon", ItemsSource = LayoutAppearanceViewModel.Icons,
            SelectedItem = appearance.Icon, MinWidth = 120 };
        var colour = new TextBox { Name = "AppearanceColour", Text = appearance.Colour ?? "", MaxLength = 7,
            PlaceholderText = Localizer.Text("ColourPlaceholder") };
        var hidden = new CheckBox { Name = "AppearanceHidden", IsChecked = appearance.Hidden, IsVisible = channel is not null,
            Content = Localizer.Text("HideChannelStrip") };
        var save = new Button { Name = "AppearanceSave", Content = Localizer.Text("Save"), IsDefault = true };
        var cancel = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "hint" } };
        var dialog = new Window
        {
            Title = Localizer.Format("AppearanceOfTitle", name), Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, Classes = { "dialog" },
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Localizer.Text("Icon") }, icon,
                    new TextBlock { Text = Localizer.Text("Colour") }, colour,
                    hidden,
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "hint" },
                        Text = Localizer.Text("AppearanceDialogHint") },
                    note,
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, save } },
                },
            },
        };
        save.Click += async (_, _) =>
        {
            string? chosen = string.IsNullOrWhiteSpace(colour.Text) ? null : colour.Text.Trim();
            if (chosen is not null && !IsHexColour(chosen))
            {
                note.Text = Localizer.Text("ColourFormatHint");
                return;
            }
            save.IsEnabled = false;
            try
            {
                string? error = await vm.SetLayoutAppearance(channel?.Id ?? mix!.Id, mix is not null,
                    icon.SelectedItem as string ?? "", chosen, channel is not null && hidden.IsChecked == true);
                if (error is null) dialog.Close(); else note.Text = error;
            }
            finally { save.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static bool IsHexColour(string text) =>
        text.Length == 7 && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

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

    private Task<string?> PromptName(string title, string current, string hint)
        => Dialogs.NameAsync(this, title, current, hint, 60);

    private async Task<bool> Confirm(string title, string message)
    {
        var yes = new Button { Content = Localizer.Text("Delete"), Classes = { "danger" } };
        var no = new Button { Content = Localizer.Text("Cancel"), IsCancel = true };
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
