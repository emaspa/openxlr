using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OpenXLR.UI;

public partial class MainWindow : Window
{
    private readonly DaemonClient _client = new();
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _reallyExit;
    private bool _hideToTrayPending;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _automaticUpdateCheckStarted;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(_client);
        DataContext = _vm;
        _client.Start();          // connects, and keeps retrying if the daemon isn't up yet
        HeaderVersion.Text = $"v{AppVersion.Current}";
        SetupTray();
        RestoreSectionState();
        Opened += async (_, _) =>
        {
            if (_automaticUpdateCheckStarted) return;
            _automaticUpdateCheckStarted = true;
            await _vm.Updates.CheckAsync(manual: false, cancellation: _lifetime.Token);
        };

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
            if (_vm.MinimizeToTray && _tray is not null && !_reallyExit &&
                e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                if (_hideToTrayPending) return;
                _hideToTrayPending = true;
                // Finish the native close callback before unmapping the
                // window and stopping its renderer.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_hideToTrayPending) return;
                    _hideToTrayPending = false;
                    Hide();
                });
            }
        };
        Closed += async (_, _) =>
        {
            _reallyExit = true;
            _hideToTrayPending = false;
            _lifetime.Cancel();
            _tray?.Dispose();
            await _client.DisposeAsync();
            _lifetime.Dispose();
            // A window that started hidden is not the lifetime's MainWindow,
            // so closing it for real must end the process explicitly.
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.ShutdownMode == ShutdownMode.OnExplicitShutdown)
                desktop.Shutdown();
        };
    }

    /// <summary>True when the window should stay unshown until the tray asks for it.</summary>
    public bool StartsHidden { get; }

    internal void ShowMixer()
    {
        _hideToTrayPending = false;
        if (_reallyExit) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void Quit()
    {
        _hideToTrayPending = false;
        _reallyExit = true;
        Close();
    }

    private void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem("Show mixer");
            show.Click += (_, _) => Dispatcher.UIThread.Post(ShowMixer);
            var quit = new NativeMenuItem("Quit OpenXLR");
            quit.Click += (_, _) => Dispatcher.UIThread.Post(Quit);
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OpenXLR.UI/Assets/icon.png"))),
                ToolTipText = "OpenXLR",
                Menu = menu,
            };
            _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowMixer);
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

    private void OnEditLayout(object? sender, RoutedEventArgs e)
        => new MixerSetupWindow { DataContext = _vm }.ShowDialog(this);

    private void OnAbout(object? sender, RoutedEventArgs e)
        => new AboutWindow().ShowDialog(this);

    private void OnUpdates(object? sender, RoutedEventArgs e)
        => new UpdatesWindow { DataContext = _vm.Updates }.ShowDialog(this);

    private void OnDismissUpdate(object? sender, RoutedEventArgs e)
        => _vm.Updates.DismissBanner();

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

    private Task<bool> ConfirmAsync(string title, string message, string yesLabel = "Overwrite")
        => Dialogs.ConfirmAsync(this, title, message, yesLabel);

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

    // Plugin inserts: every chain, input or mix, opens the same window,
    // and each insert's controls open in their own (see InsertWindows).
    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        // The button's Tag names the channel: "xlr2" for the second input.
        bool second = (sender as Control)?.Tag as string == "xlr2";
        InsertWindows.OpenChain(this, second ? _vm.Inserts2 : _vm.Inserts, second ? "xlr2" : "xlr1");
    }

    private async void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        // A plugin running in its own process has an editor of its own, which
        // is the better one to open. Anything else, including a native host
        // that is bypassed or not running, opens the generated controls.
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else InsertWindows.OpenControls(this, insert);
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
