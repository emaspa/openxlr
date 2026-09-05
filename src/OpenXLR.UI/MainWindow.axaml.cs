using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace OpenXLR.UI;

public partial class MainWindow : Window
{
    private readonly DaemonClient _client = new();
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(_client);
        DataContext = _vm;
        _client.Start();          // connects, and keeps retrying if the daemon isn't up yet
        HeaderVersion.Text = $"v{AppVersion.Current}";
        SetupTray();
        RestoreSectionState();

        // Start hidden in the tray when configured (and a tray actually
        // exists; otherwise the window must show or nothing is reachable).
        // App reads this and leaves the window unshown; it is never mapped
        // and unmapped, which is what produced a hollow frame at login.
        StartsHidden = UiSettings.Load().StartMinimized && _tray is not null;

        Closing += (_, e) =>
        {
            // With minimize-to-tray on, the close button hides the window; the
            // tray menu's Quit (or disabling the option) exits for real. Only a
            // user-initiated window close is intercepted: cancelling an
            // OS/application shutdown request here blocks the whole system
            // from logging out or rebooting.
            if (_vm.MinimizeToTray && !_reallyExit &&
                e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += async (_, _) =>
        {
            _tray?.Dispose();
            await _client.DisposeAsync();
            // A window that started hidden is not the lifetime's MainWindow,
            // so closing it for real must end the process explicitly.
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown)
                desktop.Shutdown();
        };
    }

    /// <summary>True when the window should stay unshown until the tray asks for it.</summary>
    public bool StartsHidden { get; }

    private void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem("Show mixer");
            show.Click += (_, _) => { Show(); Activate(); };
            var quit = new NativeMenuItem("Quit OpenXLR");
            quit.Click += (_, _) => { _reallyExit = true; Close(); };
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenXLR.UI/Assets/icon.png"))),
                ToolTipText = "OpenXLR",
                Menu = menu,
            };
            _tray.Clicked += (_, _) => { Show(); Activate(); };
        }
        catch (Exception)
        {
            // No tray host available: the option simply has no effect.
            _tray = null;
        }
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow(new OptionsViewModel(_client, _vm)).ShowDialog(this);

    private void OnManageApps(object? sender, RoutedEventArgs e)
        => new AppsWindow { DataContext = _vm }.ShowDialog(this);

    private void OnMixerSetup(object? sender, RoutedEventArgs e)
        => new MixerSetupWindow { DataContext = _vm }.ShowDialog(this);

    private void OnAbout(object? sender, RoutedEventArgs e)
        => new AboutWindow().ShowDialog(this);

    private async void OnProfileSave(object? sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        bool exists = _vm.Profiles.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (exists && !await ConfirmAsync("Overwrite profile?",
                $"A profile named \"{name}\" already exists for this device.\n" +
                "Saving will replace it with the current scene."))
            return;
        _vm.SaveProfile(name);
        ProfileNameBox.Text = "";
    }

    /// <summary>Small in-app confirmation dialog; true when the user accepts.</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = "Overwrite", Background = Avalonia.Media.Brush.Parse("#a03434") };
        var no = new Button { Content = "Cancel" };
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
        await dialog.ShowDialog(this);
        return await done.Task;
    }

    private void OnProfileLoad(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string name) _vm.LoadProfile(name);
    }

    private void OnProfileDelete(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string name) _vm.DeleteProfile(name);
    }

    private void OnPickDevice(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DetectedDeviceItem d) _vm.SelectDevice(d);
        DevicePicker.Flyout?.Hide();
    }

    private void OnCycleSoftLowCut(object? sender, RoutedEventArgs e) => _vm.CycleSoftLowCut();

    // Plugin inserts: the picker is a modal dialog; each insert's controls
    // and each mix's chain live in their own windows (see InsertWindows).
    private async void OnAddInsert(object? sender, RoutedEventArgs e)
    {
        // The button's Tag names the channel: "xlr2" for the second input.
        InsertsViewModel chain = (sender as Control)?.Tag as string == "xlr2" ? _vm.Inserts2 : _vm.Inserts;
        var picker = new PluginPickerWindow { DataContext = chain };
        PluginChoice? choice = await picker.ShowDialog<PluginChoice?>(this);
        if (choice is not null) chain.Add(choice);
    }

    private void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) InsertWindows.OpenControls(this, ins);
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MixViewModel mix) InsertWindows.OpenChain(this, mix.Inserts, $"mix:{mix.Id}");
    }

    private void OnInsertUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, -1);
    }

    private void OnInsertDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, +1);
    }

    private void OnInsertRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Remove(ins);
    }

    private FlowWindow? _flow;

    private void OnFlow(object? sender, RoutedEventArgs e)
    {
        // Non-modal so it can sit on another monitor while mixing; one at a time.
        if (_flow is { IsVisible: true }) { _flow.Activate(); return; }
        _flow = new FlowWindow(_vm);
        _flow.Show(this);
    }

    private async void OnRestartDaemon(object? sender, RoutedEventArgs e)
        => await _vm.DaemonRestart.RestartAsync();

    // ---- collapsed tiles, remembered in ui.json ----
    private static readonly string[] SectionTiles =
        ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];
    private bool _restoringSections;

    private void RestoreSectionState()
    {
        var collapsed = new HashSet<string>(UiSettings.Load().CollapsedSections, StringComparer.Ordinal);
        _restoringSections = true;
        try
        {
            foreach (string name in SectionTiles)
            {
                if (this.FindControl<Expander>(name) is not Expander tile) continue;
                tile.IsExpanded = !collapsed.Contains(name);
                tile.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Expander.IsExpandedProperty && !_restoringSections) SaveSectionState();
                };
            }
        }
        finally { _restoringSections = false; }
    }

    private void SaveSectionState()
    {
        List<string> collapsed = [];
        foreach (string name in SectionTiles)
            if (this.FindControl<Expander>(name) is { IsExpanded: false }) collapsed.Add(name);
        (UiSettings.Load() with { CollapsedSections = collapsed }).Save();
    }
}
