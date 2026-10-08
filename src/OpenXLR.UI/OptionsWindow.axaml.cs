using OpenXLR.UI.Localization;
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OptionsWindow : Window
{
    /// <summary>
    /// As tall as this window is worth making on a screen with room to spare.
    /// Past this the eye travels further than the scroll would have, and the
    /// two columns of cards stop reading as one page.
    /// </summary>
    internal const double Comfortable = 860;

    public OptionsWindow()
    {
        InitializeComponent();
        // The window sizes itself to its cards, so without a cap it grows
        // until it runs out of desktop. Two caps, because each answers a
        // different screen: the screen's own working area keeps the bottom on
        // a short display, and Comfortable keeps a tall one from handing back
        // a window the height of the monitor. Whichever is smaller wins, and
        // the cards scroll inside it.
        Opened += (_, _) =>
        {
            double cap = Comfortable;
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                double usable = screen.WorkingArea.Height / screen.Scaling;
                if (usable > 200) cap = Math.Min(cap, usable - 60);
            }
            MaxHeight = cap;
        };
    }

    public OptionsWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadPluginSetupAsync();
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

    private void OnSkinsManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(SkinsManual);

    private const string SkinsManual = "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#skins";

    private async void OnCollectDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        DiagStatus.Text = Localizer.Text("Collecting");
        try
        {
            string path = await Diagnostics.CollectAsync(vm.Client);
            DiagStatus.Text = Localizer.Format("DiagnosticsSavedTo", path);
        }
        catch (Exception ex)
        {
            DiagStatus.Text = Localizer.Format("DiagnosticsFailed", ex.Message);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void OnWaveInterfaces(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OptionsViewModel vm) new WaveInterfacesWindow(vm.Main).Show(this);
    }

    private async void OnResetDevice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        if (await Dialogs.ConfirmAsync(this, Localizer.Text("ResetDeviceTitle"), vm.Main.ResetDescription, Localizer.Text("Reset")))
            vm.Main.ResetDevice();
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
