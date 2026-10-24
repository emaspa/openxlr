using OpenXLR.UI.Localization;
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
    private readonly DaemonClient _client;
    private readonly MainViewModel _vm;
    private readonly DesktopKeys _desktopKeys;
    private readonly PlasmaVolumeRange? _volumeRange;
    private TrayIcon? _tray;
    private FlatpakTray? _flatpakTray;
    private bool _reallyExit;
    private bool _hideToTrayPending;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _automaticUpdateCheckStarted;
    private (bool Toolbar, bool Stacked)? _monitorStacked;
    private string _monitorTemplate = Skinning.SkinLayoutTemplates.OutputPanel;

    public MainWindow() : this(new DaemonClient(), syncDesktopVolume: true) { }

    /// <summary>
    /// For the window tests, which point the client at a port nothing serves
    /// so a developer's running daemon is not part of the test. They also
    /// leave the desktop's volume range alone.
    /// </summary>
    internal MainWindow(DaemonClient client, bool syncDesktopVolume = false)
    {
        _client = client;
        InitializeComponent();
        _vm = new MainViewModel(_client);
        // The sandbox has no KDE helpers to read or write Plasma's preference.
        if (syncDesktopVolume && PlasmaVolumeRange.IsPlasma && !FlatpakEnvironment.IsFlatpak)
        {
            _volumeRange = new PlasmaVolumeRange(_vm.ApplyDesktopVolumeBoost,
                error => _vm.VolumeRangeError = error, action => Dispatcher.UIThread.Post(action));
            _vm.DesktopVolumeBoostRequested += _volumeRange.Set;
            _volumeRange.Start();
            Activated += (_, _) => _volumeRange.Refresh();
        }
        _desktopKeys = new DesktopKeys(_client);
        _ = _desktopKeys.StartAsync();
        DataContext = _vm;
        _client.Start();          // connects, and keeps retrying if the daemon isn't up yet
        // Queued rather than awaited: Options calls the restart on the UI thread.
        StartupIntegration.AttachedRestart = () => { _ = _client.RestartDaemonAsync(); return _vm.DaemonConnected; };
        HeaderVersion.Text = $"v{AppVersion.Current}";
        UpdateControlSizing();
        Skinning.SkinService.Changed += UpdateControlSizing;
        SetupTray();
        RestoreSectionState();
        SetupReordering();
        Opened += async (_, _) =>
        {
            if (_automaticUpdateCheckStarted) return;
            _automaticUpdateCheckStarted = true;
            if (FlatpakEnvironment.IsFlatpak && !System.IO.File.Exists(OpenXlrPaths.ConfigFile("flatpak-notice")))
            {
                // Nothing is written to the host before the user agrees.
                if (await Dialogs.ConfirmAsync(this, "OpenXLR Flatpak", Localizer.Text("FlatpakSetup"), Localizer.Text("FlatpakSetUpButton")))
                {
                    string setupError = FlatpakSetup.Prepare();
                    var settings = UiSettings.Load();
                    bool login = settings.StartDaemonAtLogin || settings.OpenWindowAtLogin;
                    string? problem = setupError.Length > 0 ? setupError : null;
                    try
                    {
                        if (!await FlatpakBackground.ConfigureAsync(settings.StartDaemonAtLogin, settings.OpenWindowAtLogin, _lifetime.Token))
                            throw new InvalidOperationException(Localizer.Text("FlatpakBackgroundUnavailable"));
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // Without a login option the request only asks to keep
                        // running in the tray; a desktop with no portal is no
                        // reason to show this dialog at every launch.
                        if (login) problem ??= ex.Message;
                        else Console.Error.WriteLine("Background portal: " + ex.Message);
                    }
                    if (problem is null)
                    {
                        try { OpenXlrPaths.WriteAtomic(OpenXlrPaths.ConfigFile("flatpak-notice"), "1\n"); }
                        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* show again next time */ }
                    }
                    else await Dialogs.ConfirmAsync(this, "OpenXLR Flatpak", problem, Localizer.Text("Close"));
                }
            }
            await _vm.Updates.CheckAsync(manual: false, cancellation: _lifetime.Token);
        };

        // Start hidden in the tray when configured. App reads this and leaves
        // the window unshown; it is never mapped and unmapped, which is what
        // produced a hollow frame at login.
        //
        // There is no check for a usable tray here because Avalonia 12.1.3
        // offers none on Linux: constructing a TrayIcon always succeeds, and
        // the implementation behind it either talks to a StatusNotifier host
        // over D-Bus or, with no session bus, is a stub that swallows every
        // call. Neither is reachable from public API, and the D-Bus one only
        // learns whether a host exists after the constructor returns. So the
        // option does what it says and the manual says what happens on a
        // desktop with no tray.
        StartsHidden = UiSettings.Load().StartMinimized || (FlatpakEnvironment.IsFlatpak
            && Environment.GetCommandLineArgs().Contains("--background", StringComparer.Ordinal));

        Closing += (_, e) =>
        {
            // With minimize-to-tray on, the close button hides the window; the
            // tray menu's Quit (or disabling the option) exits for real. Only a
            // user-initiated window close is intercepted: cancelling an
            // OS/application shutdown request here blocks the whole system
            // from logging out or rebooting.
            // Game Mode on SteamOS and Bazzite has no tray: a hidden window
            // would leave Steam showing OpenXLR as a game still running.
            if (_vm.MinimizeToTray && !_reallyExit && !GameMode &&
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
            Skinning.SkinService.Changed -= UpdateControlSizing;
            _reallyExit = true;
            _hideToTrayPending = false;
            _mixerWindow?.Close();
            _lifetime.Cancel();
            DisposeTray();
            _desktopKeys.Dispose();
            if (_volumeRange is not null)
            {
                _vm.DesktopVolumeBoostRequested -= _volumeRange.Set;
                await _volumeRange.DisposeAsync();
            }
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
        ApplyPendingPopOut();
    }

    internal void Quit()
    {
        _hideToTrayPending = false;
        _reallyExit = true;
        Close();
    }

    private void OnMonitorSizeChanged(object? sender, SizeChangedEventArgs e) => LayOutMonitor(e.NewSize.Width);

    /// <summary>
    /// Stand MONITOR's controls as its template and its width call for. The
    /// panel puts a label before the output picker and before the volume; the
    /// toolbar drops both labels, which stay the controls' accessible names
    /// and tooltips, and keeps the picker, the volume and its readout in one
    /// row. Under <see cref="Skinning.SkinLayoutDefaults.MonitorStackWidth"/>
    /// either one stands in two rows, the picker above the volume, so the
    /// device name and a usable fader both keep their room. The controls are
    /// the same ones throughout; only their cells change.
    /// </summary>
    private void LayOutMonitor(double width)
    {
        bool toolbar = _monitorTemplate == Skinning.SkinLayoutTemplates.OutputToolbar;
        bool stacked = width < Skinning.SkinLayoutDefaults.MonitorStackWidth;
        if (_monitorStacked == (toolbar, stacked)) return;
        _monitorStacked = (toolbar, stacked);
        MonitorDevicesLabel.IsVisible = MonitorVolumeLabel.IsVisible = !toolbar;
        ToolTip.SetTip(OutputVolumeSlider, toolbar ? Localizer.Text("Volume") : null);
        MonitorControls.ColumnDefinitions = new((toolbar, stacked) switch
        {
            (false, false) => "90,*,20,90,*,64",
            (false, true) => "90,*,64",
            (true, false) => "*,12,*,64",
            (true, true) => "*,64",
        });
        MonitorControls.RowDefinitions = new(stacked ? "Auto,Auto" : "Auto");
        MonitorControls.RowSpacing = stacked ? 6 : 0;
        // Column by column: the label, the picker, the volume's label, the volume, its readout.
        (int label, int picker, int span, int volumeLabel, int volume, int readout) = (toolbar, stacked) switch
        {
            (false, false) => (0, 1, 1, 3, 4, 5),
            (false, true) => (0, 1, 2, 0, 1, 2),
            (true, false) => (0, 0, 1, 0, 2, 3),
            (true, true) => (0, 0, 2, 0, 0, 1),
        };
        Grid.SetColumn(MonitorDevicesLabel, label);
        Grid.SetColumn(MonitorDestination, picker);
        Grid.SetColumnSpan(MonitorDestination, span);
        Grid.SetColumn(MonitorVolumeLabel, volumeLabel);
        Grid.SetRow(MonitorVolumeLabel, stacked ? 1 : 0);
        Grid.SetColumn(OutputVolumeSlider, volume);
        Grid.SetRow(OutputVolumeSlider, stacked ? 1 : 0);
        Grid.SetColumn(MonitorVolumeReadout, readout);
        Grid.SetRow(MonitorVolumeReadout, stacked ? 1 : 0);
    }

    /// <summary>A gamescope session, which SteamOS and Bazzite call Game Mode.</summary>
    internal static bool GameMode => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Split(':').Contains("gamescope", StringComparer.OrdinalIgnoreCase);

    private void SetupTray()
    {
        if (FlatpakEnvironment.IsFlatpak)
        {
            _flatpakTray = new FlatpakTray(ShowMixer, Quit);
            return;
        }
        try
        {
            var menu = new NativeMenu();
            var show = new NativeMenuItem(Localizer.Text("ShowMixer"));
            show.Click += (_, _) => Dispatcher.UIThread.Post(ShowMixer);
            var quit = new NativeMenuItem(Localizer.Text("QuitOpenXLR"));
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
            // Avalonia creates the native icon only after it is attached to
            // the application, including when the mixer starts unshown.
            var app = Avalonia.Application.Current!;
            if (TrayIcon.GetIcons(app) is { } icons) icons.Add(_tray);
            else TrayIcon.SetIcons(app, new TrayIcons { _tray });
        }
        catch (Exception)
        {
            // Only the icon asset or the menu can fail here; the tray
            // implementation itself never reports a missing host.
            DisposeTray();
        }
    }

    private void DisposeTray()
    {
        _flatpakTray?.Dispose();
        _flatpakTray = null;
        if (_tray is null) return;
        if (Avalonia.Application.Current is { } app)
            TrayIcon.GetIcons(app)?.Remove(_tray);
        _tray.Dispose();
        _tray = null;
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow(new OptionsViewModel(_client, _vm) { ResetArrangement = ResetSkinArrangement }, _desktopKeys).ShowDialog(this);

    private void OnManageApps(object? sender, RoutedEventArgs e)
        => new AppsWindow { DataContext = _vm }.ShowDialog(this);

    private BrowserTabsWindow? _browserTabs;

    private void OnManageBrowserTabs(object? sender, RoutedEventArgs e) => OpenBrowserTabs();

    /// <summary>
    /// Opens the browser tabs dialog the way Manage opens its window, owned by
    /// this one. A second request while it is open brings the same window
    /// forward rather than stacking another.
    /// </summary>
    internal BrowserTabsWindow OpenBrowserTabs()
    {
        if (_browserTabs is { } open)
        {
            open.Activate();
            return open;
        }
        var window = new BrowserTabsWindow { DataContext = _vm };
        _browserTabs = window;
        window.Closed += (_, _) => { if (_browserTabs == window) _browserTabs = null; };
        _ = window.ShowDialog(this);
        return window;
    }

    private void OnEditLayout(object? sender, RoutedEventArgs e)
        => new MixerSetupWindow { DataContext = _vm }.ShowDialog(this);

    private void OnDucking(object? sender, RoutedEventArgs e)
        => DuckingWindow.Open(this, _vm.Ducking);

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
        if (exists && !await ConfirmAsync(Localizer.Text("OverwriteProfileTitle"),
                Localizer.Format("ProfileOverwriteMessage", name)))
            return;
        _vm.SaveProfile(name);
        ProfileNameBox.Text = "";
    }

    private Task<bool> ConfirmAsync(string title, string message, string? yesLabel = null)
        => Dialogs.ConfirmAsync(this, title, message, yesLabel ?? Localizer.Text("Overwrite"));

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

    /// <summary>The header's link with no interface attached: bind XLR 1 to another microphone.</summary>
    private async void OnChooseMicrophone(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await InputBindingDialog.ShowAsync(this, vm, "xlr1");
    }

    // Plugin inserts: every chain, input or mix, opens the same window,
    // and each insert's controls open in their own (see InsertWindows).
    private void OnXlrInserts(object? sender, RoutedEventArgs e)
    {
        // The button's Tag names the channel: "xlr2" for the second input.
        bool second = (sender as Control)?.Tag as string == "xlr2";
        InsertWindows.OpenChain(this, second ? _vm.Inserts2 : _vm.Inserts, second ? "xlr2" : "xlr1");
    }

    /// <summary>The EQ button of XLR 1, XLR 2 or a capture input in INPUTS: the button's Tag names the channel.</summary>
    private void OnEq(object? sender, RoutedEventArgs e)
    {
        string channel = (sender as Control)?.Tag as string ?? "xlr1";
        if (_vm.Channels.FirstOrDefault(c => c.Id == channel)?.Eq is { } eq)
            EqWindows.Show(TopLevel.GetTopLevel((Control)sender!) as Window ?? this, eq);
    }

    private async void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        // A plugin running in its own process has an editor of its own, which
        // is the better one to open. Anything else, including a native host
        // that is bypassed or not running, opens the generated controls.
        if (insert.NativeEditorAvailable) await insert.Owner.ShowNativeEditorAsync(insert);
        else InsertWindows.OpenControls(TopLevel.GetTopLevel((Control)sender!) as Window ?? this, insert);
    }

    private void OnMixInserts(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MixViewModel mix)
            InsertWindows.OpenChain(TopLevel.GetTopLevel((Control)sender!) as Window ?? this, mix.Inserts, $"mix:{mix.Id}");
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

    /// <summary>Touch sizing, or a skin's own control minimum, enlarges the targets.</summary>
    private void UpdateControlSizing() => Classes.Set("large-targets", Skinning.SkinService.LargeTargets);

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
        UiSettings settings = UiSettings.Load();
        foreach (var (skin, version) in _unsavedLayoutsSeen)
            if (!settings.HasSeenSkinLayout(skin, version)) settings = settings.WithSkinLayoutSeen(skin, version);
        string? error = (settings with { CollapsedSections = collapsed }).Save();
        if (error is null) _unsavedLayoutsSeen.Clear();
        ReportSave(WindowChoice.Folds, error is null ? null : Localizer.Format("SectionFoldsNotSaved", error));
    }
}
