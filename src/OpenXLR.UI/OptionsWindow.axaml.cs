using OpenXLR.UI.Localization;
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>The tabs of Options, in the order the strip shows them.</summary>
internal enum OptionsTab { General, Audio, Plugins, Appearance, Support }

public partial class OptionsWindow : Window
{
    /// <summary>
    /// Wide enough for the widest card in one column, with the longest
    /// translation at the largest text size, and for the row of tabs.
    /// </summary>
    internal const double DesignWidth = 640;

    /// <summary>
    /// How tall the window is on a screen with room to spare: about the
    /// height of the Audio tab, the tallest, in English at Normal text. A
    /// tab taller than this scrolls inside it, and a shorter one leaves room
    /// below its last card.
    /// </summary>
    internal const double Comfortable = 760;

    /// <summary>
    /// The tab the last Options window of this run was showing, so Options
    /// opens again where the user left it. It is kept in memory only: a new
    /// run opens on General.
    /// </summary>
    private static OptionsTab _lastTab = OptionsTab.General;

    public OptionsWindow()
    {
        InitializeComponent();
        // One size whichever tab is shown, so the window does not jump as the
        // tabs change: a window that sized itself to its content would grow
        // and shrink under the pointer with every tab. On a screen too small
        // for that size the window takes what the screen has and the tab
        // scrolls inside it.
        Width = DesignWidth;
        Height = Comfortable;
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is not { } screen) return;
            Size size = FitTo(screen.WorkingArea.Size.ToSize(screen.Scaling));
            Width = size.Width;
            Height = size.Height;
        };
        OptionsTabs.SelectedIndex = (int)_lastTab;
        OptionsTabs.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectingItemsControl.SelectedIndexProperty && OptionsTabs.SelectedIndex >= 0)
                _lastTab = (OptionsTab)OptionsTabs.SelectedIndex;
        };
        // Ctrl+Tab and Ctrl+Page Down move to the next tab from anywhere in
        // the window, as they do in a browser; the arrow keys move along the
        // row of tabs once it has the focus.
        AddHandler(KeyDownEvent, OnTabKeys, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The window's size on a screen with this much room, in device
    /// independent units: its own size, or less where the screen is smaller,
    /// leaving room for a panel and the window's title bar. A screen that
    /// reports almost nothing is not believed.
    /// </summary>
    internal static Size FitTo(Size usable) => new(
        usable.Width > 200 ? Math.Min(DesignWidth, usable.Width - 32) : DesignWidth,
        usable.Height > 200 ? Math.Min(Comfortable, usable.Height - 60) : Comfortable);

    /// <summary>The tab on show. Setting it shows that tab.</summary>
    internal OptionsTab SelectedTab
    {
        get => (OptionsTab)Math.Max(0, OptionsTabs.SelectedIndex);
        set => OptionsTabs.SelectedIndex = (int)value;
    }

    /// <summary>Forget the tab this run last showed, for a test that opens Options as a new run would.</summary>
    internal static void ForgetLastTab() => _lastTab = OptionsTab.General;

    private void OnTabKeys(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        int step = e.Key switch
        {
            Key.Tab => (e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1,
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0,
        };
        if (step == 0) return;
        int count = OptionsTabs.ItemCount;
        OptionsTabs.SelectedIndex = (OptionsTabs.SelectedIndex + step + count) % count;
        // The control that had the focus went with the tab it was on.
        OptionsTabs.ContainerFromIndex(OptionsTabs.SelectedIndex)?.Focus(NavigationMethod.Tab);
        e.Handled = true;
    }

    private readonly DesktopKeys? _desktopKeys;

    public OptionsWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadPluginSetupAsync();
        Activated += (_, _) => vm.RefreshMixerView();
    }

    // The desktop keys belong to the main window, which keeps them running
    // while this window is closed.
    internal OptionsWindow(OptionsViewModel vm, DesktopKeys desktopKeys) : this(vm)
    {
        _desktopKeys = desktopKeys;
    }

    internal event Action? WelcomeGuideRequested;

    private void OnWelcomeGuide(object? sender, RoutedEventArgs e) => WelcomeGuideRequested?.Invoke();

    /// <summary>
    /// Show the tab that holds a card and bring the card into view: the
    /// welcome guide's shortcuts name the card's heading.
    /// </summary>
    internal void ShowGuideSection(string heading)
    {
        if (this.FindControl<Control>(heading) is not { } section) return;
        if (section.FindLogicalAncestorOfType<TabItem>() is { } tab) OptionsTabs.SelectedItem = tab;
        // The tab's content joins the window on the next layout pass, and
        // only then does it have a scroll view to be brought into view in.
        Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Loaded);
    }

    // Plugins: the same install flow as the picker, plus yabridge's sync and a rescan.
    private async void OnInstallPluginFile(object? sender, RoutedEventArgs e)
        => await InstallPluginsAsync(await PluginInstall.PickFilesAsync(this));

    private async void OnInstallPluginFolder(object? sender, RoutedEventArgs e)
        => await InstallPluginsAsync(await PluginInstall.PickFolderAsync(this));

    private async System.Threading.Tasks.Task InstallPluginsAsync(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || DataContext is not OptionsViewModel vm) return;
        await PluginStepAsync(() => PluginInstall.InstallAsync(vm.Client, paths), Localizer.Text("Installing"), vm);
    }

    private async void OnBridgeWinePlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm || vm.WineFolders.Count == 0) return;
        await PluginStepAsync(() => PluginInstall.InstallAsync(vm.Client, vm.WineFolders), Localizer.Text("BridgingWinePlugins"), vm);
    }

    private async void OnSyncWindowsPlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        await PluginStepAsync(async () => PluginInstall.Describe(
            await vm.Client.SyncWindowsPluginsAsync(TimeSpan.FromMinutes(4)), Localizer.Text("TheSync")), Localizer.Text("BridgingWindowsPlugins"), vm);
    }

    private async void OnNativeEditorRules(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm)
            await new NativeEditorRulesWindow(vm.Client).ShowDialog(this);
    }

    private async void OnManagePluginFolders(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm)
            await new PluginFoldersWindow(vm).ShowDialog(this);
    }

    private async void OnRescanPlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        await PluginStepAsync(async () => PluginInstall.Describe(
            await vm.Client.RescanPluginsAsync(TimeSpan.FromMinutes(4)), Localizer.Text("TheScan")), Localizer.Text("Scanning"), vm);
    }

    private async void OnPluginWineTrace(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm && sender is CheckBox check)
            await vm.SetPluginWineTraceAsync(check.IsChecked == true);
    }

    private async System.Threading.Tasks.Task PluginStepAsync(Func<System.Threading.Tasks.Task<string>> step, string busy, OptionsViewModel vm)
    {
        InstallFile.IsEnabled = InstallFolder.IsEnabled = Rescan.IsEnabled = ManageFolders.IsEnabled = false;
        bool sync = SyncWindows.IsEnabled;
        SyncWindows.IsEnabled = BridgeWine.IsEnabled = false;
        PluginStatus.Text = busy;
        try { PluginStatus.Text = await step(); }
        catch (Exception ex) { PluginStatus.Text = ex.Message; }
        finally
        {
            InstallFile.IsEnabled = InstallFolder.IsEnabled = Rescan.IsEnabled = ManageFolders.IsEnabled = true;
            SyncWindows.IsEnabled = sync;
            BridgeWine.IsEnabled = true;
            await vm.LoadPluginSetupAsync();
        }
    }

    private async void OnExportSettings(object? sender, RoutedEventArgs e) => await ExportBackupAsync(profilesOnly: false);
    private async void OnExportProfiles(object? sender, RoutedEventArgs e) => await ExportBackupAsync(profilesOnly: true);

    private async System.Threading.Tasks.Task ExportBackupAsync(bool profilesOnly)
    {
        BackupButtons.IsEnabled = false;
        BackupStatus.Text = "";
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = profilesOnly ? Localizer.Text("ExportProfileBackup") : Localizer.Text("ExportSettingsBackup"),
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            string directory = folders[0].TryGetLocalPath() ?? throw new System.IO.IOException(Localizer.Text("BackupNeedsLocalFolder"));
            string stem = profilesOnly ? "openxlr-profiles" : "openxlr-settings";
            string name = $"{stem}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.{SettingsBackup.Extension}";
            string path = System.IO.Path.Combine(directory, name);
            int count = await System.Threading.Tasks.Task.Run(() => SettingsBackup.Export(path, profilesOnly));
            BackupStatus.Text = Localizer.Format("SettingsBackupSaved", count, path);
        }
        catch (Exception ex) when (SettingsBackup.IsFileFailure(ex))
        {
            BackupStatus.Text = Localizer.Format("SettingsBackupFailed", ex.Message);
        }
        finally { BackupButtons.IsEnabled = true; }
    }

    private void OnBackupHelp(object? sender, RoutedEventArgs e)
        => ExternalLink.Open("https://github.com/emaspa/openxlr/blob/main/docs/manual.md#settings-backups");

    private void OnPluginsManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(PluginInstall.Manual);
    private void OnWindowsEditorHelp(object? sender, RoutedEventArgs e)
        => ExternalLink.Open("https://github.com/emaspa/openxlr/blob/main/docs/manual.md#windows-editor-input");
    private void OnMemoryLockHelp(object? sender, RoutedEventArgs e)
        => ExternalLink.Open("https://github.com/emaspa/openxlr/blob/main/docs/manual.md#memlock");

    // Appearance: the picker applies on selection; this reads the folders again
    // for a skin added or edited while the window was open.
    private void OnReloadSkins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) vm.ReloadSkins();
    }

    private void OnResetSkinArrangement(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) vm.ResetSkinArrangement();
    }

    private void OnSkinsManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(SkinsManual);

    private const string SkinsManual = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#skins";

    private async void OnCollectDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        bool deepTrace = DeepDiagnostics.IsChecked == true;
        DeepDiagnostics.IsChecked = false;
        DeepDiagnostics.IsEnabled = false;
        DiagStatus.Text = Localizer.Text("Collecting");
        try
        {
            string path = await Diagnostics.CollectAsync(vm.Client, deepTrace);
            DiagStatus.Text = Localizer.Format("DiagnosticsSavedTo", path);
        }
        catch (Exception ex)
        {
            DiagStatus.Text = Localizer.Format("DiagnosticsFailed", ex.Message);
        }
        finally
        {
            DeepDiagnostics.IsEnabled = true;
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async void OnDesktopKeys(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm && _desktopKeys is not null)
            await new DesktopKeysWindow(_desktopKeys, vm.Main).ShowDialog(this);
    }

    private void OnDucking(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) DuckingWindow.Open(this, vm.Main.Ducking);
    }

    /// <summary>
    /// "This unit": the dry run first, then the confirm window that lists what
    /// this layout loses; only its own button switches. The side already lit
    /// sends nothing and stays lit.
    /// </summary>
    private async void OnHardwareMixingUnit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        HardwareMixingViewModel mixing = vm.Main.HardwareMixing;
        mixing.Reassert();
        if (mixing.OnUnit || !mixing.CanSwitch) return;
        await HardwareMixingWindow.ConfirmAsync(this, mixing);
    }

    /// <summary>"This computer": back to today's mixing, with nothing to confirm; the unit is put back as it was.</summary>
    private async void OnHardwareMixingComputer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        HardwareMixingViewModel mixing = vm.Main.HardwareMixing;
        mixing.Reassert();
        if (mixing.OnComputer || !mixing.CanSwitch) return;
        await mixing.SwitchAsync(hardware: false);
    }

    private void OnSoundboard(object? sender, RoutedEventArgs e)
    {
        // Over Options, owned by neither: the board stays open when Options closes.
        if (DataContext is OptionsViewModel vm) SoundboardWindow.Open(this, vm.Main.Soundboard);
    }

    private void OnWaveInterfaces(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) new WaveInterfacesWindow(vm.Main).Show(this);
    }

    /// <summary>Restart WirePlumber so the chosen audio clock applies, after a confirm that says the audio stops.</summary>
    private async void OnRestartAudio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("RestartAudioTitle"), Localizer.Text("RestartAudioConfirm"), Localizer.Text("RestartAudioNow")))
            await vm.Main.RestartAudioAsync();
    }

    private async void OnResetDevice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("ResetDeviceTitle"), vm.Main.ResetDescription, Localizer.Text("Reset")))
            vm.Main.ResetDevice();
    }

    private void OnRecoverMicrophone(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) vm.Main.RecoverCapture();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) await vm.Updates.CheckAsync(manual: true);
    }

    private void OnUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm)
            new UpdatesWindow { DataContext = vm.Updates }.ShowDialog(this);
    }
}
