using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
        // The window sizes itself to its cards, so on a short screen it can be
        // taller than the desktop, with its bottom off the edge and no way to
        // resize it. Cap it to the screen it opens on; the cards scroll.
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is null) return;
            double usable = screen.WorkingArea.Height / screen.Scaling;
            if (usable > 200) MaxHeight = usable - 60;
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
        await PluginStepAsync(() => PluginInstall.InstallAsync(vm.Client, paths), "Installing…", vm);
    }

    private async void OnBridgeWinePlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm || vm.WineFolders.Count == 0) return;
        await PluginStepAsync(() => PluginInstall.InstallAsync(vm.Client, vm.WineFolders), "Bridging Wine's plugins…", vm);
    }

    private async void OnSyncWindowsPlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        await PluginStepAsync(async () => PluginInstall.Describe(
            await vm.Client.SyncWindowsPluginsAsync(TimeSpan.FromMinutes(4)), "the sync"), "Bridging Windows plugins…", vm);
    }

    private async void OnRescanPlugins(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        await PluginStepAsync(async () => PluginInstall.Describe(
            await vm.Client.RescanPluginsAsync(TimeSpan.FromMinutes(4)), "the scan"), "Scanning…", vm);
    }

    private async System.Threading.Tasks.Task PluginStepAsync(Func<System.Threading.Tasks.Task<string>> step, string busy, OptionsViewModel vm)
    {
        InstallFile.IsEnabled = InstallFolder.IsEnabled = Rescan.IsEnabled = false;
        bool sync = SyncWindows.IsEnabled;
        SyncWindows.IsEnabled = BridgeWine.IsEnabled = false;
        PluginStatus.Text = busy;
        try { PluginStatus.Text = await step(); }
        catch (Exception ex) { PluginStatus.Text = ex.Message; }
        finally
        {
            InstallFile.IsEnabled = InstallFolder.IsEnabled = Rescan.IsEnabled = true;
            SyncWindows.IsEnabled = sync;
            BridgeWine.IsEnabled = true;
            await vm.LoadPluginSetupAsync();
        }
    }

    private void OnPluginsManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(PluginInstall.Manual);

    private async void OnCollectDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        DiagStatus.Text = "collecting…";
        try
        {
            string path = await Diagnostics.CollectAsync(vm.Client);
            DiagStatus.Text = $"saved to {path}";
        }
        catch (Exception ex)
        {
            DiagStatus.Text = $"failed: {ex.Message}";
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private async void OnResetDevice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OptionsViewModel vm) return;
        if (await Dialogs.ConfirmAsync(this, "Reset device to defaults?", vm.Main.ResetDescription, "Reset"))
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
