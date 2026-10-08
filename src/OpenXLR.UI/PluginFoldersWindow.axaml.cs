using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public sealed record WindowsPluginEntry(string Path, string Name, string Format, bool Enabled,
    bool CanDelete, string? WinePrefix, bool InUse)
{
    public string Status => InUse ? Localizer.Text("PluginInUse") : Enabled ? Localizer.Text("PluginEnabled") : Localizer.Text("PluginDisabled");
    public string FormatLabel => Format.ToUpperInvariant();
}

public partial class PluginFoldersWindow : Window
{
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(4);
    private readonly ObservableCollection<WindowsPluginEntry> _plugins = [];
    private bool _busy;
    private WindowsPluginEntry? SelectedPlugin => PluginList.SelectedItem as WindowsPluginEntry;

    public PluginFoldersWindow()
    {
        InitializeComponent();
        PluginList.ItemsSource = _plugins;
        SearchFormat.ItemsSource = new[] { "LV2", "CLAP", "VST3" };
        SearchFormat.SelectedIndex = 0;
        Closing += (_, e) =>
        {
            if (_busy && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;
        };
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is null) return;
            double height = screen.WorkingArea.Height / screen.Scaling - 60;
            if (height > 300) { MinHeight = Math.Min(MinHeight, height); MaxHeight = height; }
        };
    }

    public PluginFoldersWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await RunAsync(vm, () => Task.FromResult(""), Localizer.Text("ReadingPluginFolders"));
    }

    private void UpdateButtons()
    {
        if (DataContext is not OptionsViewModel vm) return;
        AddSearchPath.IsEnabled = RescanAll.IsEnabled = SearchDirectoryList.IsEnabled = SearchFormat.IsEnabled = !_busy;
        RemoveSearchPath.IsEnabled = !_busy && SearchDirectoryList.SelectedItem is PluginSearchDirectoryItem { Custom: true };
        AddFolder.IsEnabled = RescanFolders.IsEnabled = !_busy && vm.CanSyncWindows;
        RemoveFolder.IsEnabled = !_busy && vm.CanManageWindows && FolderList.SelectedItem is string;
        FolderList.IsEnabled = PluginList.IsEnabled = CloseButton.IsEnabled = !_busy;
        WindowsPluginEntry? selected = SelectedPlugin;
        RemoveUses.IsVisible = selected?.InUse == true;
        RemoveUses.IsEnabled = !_busy && selected?.InUse == true;
        TogglePlugin.Content = selected?.Enabled == false ? Localizer.Text("EnableInOpenXLR") : Localizer.Text("DisableInOpenXLR");
        TogglePlugin.IsEnabled = !_busy && vm.CanSyncWindows && selected is { InUse: false };
        DeletePlugin.IsEnabled = !_busy && vm.CanManageWindows && selected is { InUse: false, CanDelete: true };
        WineUninstaller.IsVisible = selected?.WinePrefix is not null;
        WineUninstaller.IsEnabled = !_busy && vm.CanSyncWindows && selected is { InUse: false };
        PluginDetail.Text = selected is null ? Localizer.Text("SelectAPluginToManageIt") : selected.Path
            + (selected.InUse ? "\n" + Localizer.Text("PluginInUseDetail") : "");
    }

    private async Task RefreshCoreAsync(OptionsViewModel vm)
    {
        string? folder = FolderList.SelectedItem as string;
        await vm.LoadPluginSetupAsync();
        FolderList.SelectedItem = folder is not null && vm.WindowsDirectories.Contains(folder)
            ? folder : vm.WindowsDirectories.FirstOrDefault();
        await LoadFilesAsync(vm);
    }

    private async Task LoadFilesAsync(OptionsViewModel vm)
    {
        if (FolderList.SelectedItem is not string folder)
        {
            ApplyPluginFiles(null);
            return;
        }
        JsonNode? reply = await vm.Client.RequestWindowsPluginFilesAsync(folder, TimeSpan.FromSeconds(20));
        if (FolderList.SelectedItem as string != folder) return;
        ApplyPluginFiles(reply);
        if (reply is null) Status.Text = Localizer.Text("PluginFilesNoAnswer");
        else if (reply["ok"]?.GetValue<bool>() != true)
            Status.Text = reply["message"]?.GetValue<string>() ?? Localizer.Text("CouldNotReadPluginFolder");
        else if (reply["message"]?.GetValue<string>() is { Length: > 0 } note)
            Status.Text = string.IsNullOrEmpty(Status.Text) ? note : Status.Text + " " + note;
    }

    internal void ApplyPluginFiles(JsonNode? reply)
    {
        string? selected = SelectedPlugin?.Path;
        _plugins.Clear();
        if (reply?["ok"]?.GetValue<bool>() == true)
            foreach (JsonNode item in (reply["plugins"] as JsonArray ?? []).OfType<JsonNode>())
                _plugins.Add(new(item["path"]!.GetValue<string>(), item["name"]!.GetValue<string>(),
                    item["format"]!.GetValue<string>(), item["enabled"]!.GetValue<bool>(),
                    item["canDelete"]!.GetValue<bool>(), item["winePrefix"]?.GetValue<string>(),
                    item["inUse"]?.GetValue<bool>() ?? false));
        PluginList.SelectedItem = _plugins.FirstOrDefault(p => p.Path == selected);
        EmptyPlugins.IsVisible = _plugins.Count == 0;
        EmptyPlugins.Text = FolderList.SelectedItem is null ? Localizer.Text("SelectAFolderToSeeItsPlugins")
            : reply?["ok"]?.GetValue<bool>() == true ? Localizer.Text("NoWindowsPluginFiles")
            : Localizer.Text("PluginListUnavailable");
        UpdateButtons();
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm) return;
        _busy = true;
        UpdateButtons();
        Status.Text = "";
        try { await LoadFilesAsync(vm); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private void OnPluginSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm) return;
        try
        {
            string? folder = (await PluginInstall.PickFolderAsync(this, Localizer.Text("AddWindowsPluginFolder"), allowMultiple: false)).FirstOrDefault();
            if (folder is not null)
                await ChangeAsync(vm, () => vm.Client.AddWindowsPluginFolderAsync(folder, ChangeTimeout), Localizer.Text("AddingFolder"));
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void OnRemoveFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || FolderList.SelectedItem is not string folder) return;
        string detail = Localizer.Format("RemoveFolderDetail", folder);
        if (vm.SystemBridge) detail += " " + Localizer.Text("AffectsSystemYabridge");
        if (!await Dialogs.ConfirmAsync(this, Localizer.Text("RemovePluginFolderTitle"), detail, Localizer.Text("RemoveFromList"))) return;
        await ChangeAsync(vm, () => vm.Client.RemoveWindowsPluginFolderAsync(folder, ChangeTimeout), Localizer.Text("RemovingFolder"));
    }

    private async void OnRemoveUses(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { InUse: true } plugin) return;
        if (!await Dialogs.ConfirmAsync(this, Localizer.Text("RemovePluginUsesTitle"),
            Localizer.Format("RemovePluginUsesDetail", plugin.Name), Localizer.Text("RemoveFromAllChains"))) return;
        await ChangeAsync(vm, () => vm.Client.RemoveWindowsPluginInsertsAsync(plugin.Path, ChangeTimeout), Localizer.Text("RemovingPluginUses"));
    }

    private async void OnTogglePlugin(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { } plugin) return;
        if (plugin.Enabled && !await Dialogs.ConfirmAsync(this, Localizer.Text("DisablePluginTitle"),
            Localizer.Format("DisablePluginDetail", plugin.Name)
            + (vm.SystemBridge ? " " + Localizer.Text("AffectsSystemYabridge") : ""), Localizer.Text("Disable"))) return;
        await ChangeAsync(vm, () => vm.Client.SetWindowsPluginEnabledAsync(plugin.Path, !plugin.Enabled, ChangeTimeout),
            plugin.Enabled ? Localizer.Text("DisablingPlugin") : Localizer.Text("EnablingPlugin"));
    }

    private async void OnDeletePlugin(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { CanDelete: true } plugin) return;
        if (!await Dialogs.ConfirmAsync(this, Localizer.Text("DeletePluginFileTitle"),
            Localizer.Format("DeletePluginFileDetail", plugin.Path), Localizer.Text("DeleteFile"))) return;
        await ChangeAsync(vm, () => vm.Client.DeleteWindowsPluginAsync(plugin.Path, ChangeTimeout), Localizer.Text("DeletingPlugin"));
    }

    private async void OnWineUninstaller(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { WinePrefix: not null } plugin
            || FolderList.SelectedItem is not string folder) return;
        if (!await Dialogs.ConfirmAsync(this, Localizer.Text("OpenWineUninstallerTitle"),
            Localizer.Format("OpenWineUninstallerDetail", plugin.WinePrefix), Localizer.Text("OpenUninstaller"))) return;
        await RunAsync(vm, async () =>
        {
            JsonNode? fresh = await vm.Client.RequestWindowsPluginFilesAsync(folder, TimeSpan.FromSeconds(20));
            JsonNode? item = (fresh?["plugins"] as JsonArray)?.FirstOrDefault(p => p?["path"]?.GetValue<string>() == plugin.Path);
            if (fresh?["ok"]?.GetValue<bool>() != true || item is null) return Localizer.Text("CouldNotVerifyPlugin");
            if (item["inUse"]?.GetValue<bool>() == true) return Localizer.Text("RemoveUsesBeforeUninstalling");
            if (item["winePrefix"]?.GetValue<string>() is not { Length: > 0 } prefix) return Localizer.Text("NoLongerInWinePrefix");
            int exit = await ProcessRunner.RunInteractiveAsync("wine", ["uninstaller"],
                new Dictionary<string, string> { ["WINEPREFIX"] = prefix });
            string result = PluginInstall.Describe(await vm.Client.SyncWindowsPluginsAsync(ChangeTimeout), Localizer.Text("TheRescan"));
            return (exit == 0 ? Localizer.Text("WineUninstallerClosed") : Localizer.Format("WineUninstallerExited", exit)) + " " + result;
        }, Localizer.Text("WaitingForWineUninstaller"));
    }

    private void OnSearchPathSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void OnAddSearchPath(object? sender, RoutedEventArgs e)
        => await AddSearchPathAsync(() => PluginInstall.PickFolderAsync(this, Localizer.Text("AddAPluginFolder"), allowMultiple: false));

    /// <summary>
    /// The window counts as busy from the moment the picker opens, so a
    /// second click cannot open another picker over it.
    /// </summary>
    internal async Task AddSearchPathAsync(Func<Task<IReadOnlyList<string>>> pickFolder)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SearchFormat.SelectedItem is not string format) return;
        string kind = format.ToLowerInvariant();
        _busy = true;
        UpdateButtons();
        try
        {
            var folders = await pickFolder();
            if (folders.Count == 1)
                await ChangeAsync(vm, () => vm.Client.ChangePluginSearchPathAsync(kind, folders[0], true, ChangeTimeout), Localizer.Text("AddingFolderAndScanning"));
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void OnRemoveSearchPath(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SearchDirectoryList.SelectedItem is not PluginSearchDirectoryItem { Custom: true } item) return;
        await ChangeAsync(vm, () => vm.Client.ChangePluginSearchPathAsync(item.Kind, item.Path, false, ChangeTimeout), Localizer.Text("RemovingFolderAndScanning"));
    }

    private async void OnRescanAll(object? sender, RoutedEventArgs e)
    {
        if (!_busy && DataContext is OptionsViewModel vm)
            await ChangeAsync(vm, () => vm.Client.RescanPluginsAsync(ChangeTimeout), Localizer.Text("RescanningAllPlugins"));
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (!_busy && DataContext is OptionsViewModel vm)
            await ChangeAsync(vm, () => vm.Client.SyncWindowsPluginsAsync(ChangeTimeout), Localizer.Text("SyncingAndRescanning"));
    }

    private Task ChangeAsync(OptionsViewModel vm, Func<Task<JsonNode?>> change, string busy)
        => RunAsync(vm, async () => PluginInstall.Describe(await change(), Localizer.Text("ThePluginOperation")), busy);

    private async Task RunAsync(OptionsViewModel vm, Func<Task<string>> operation, string busy)
    {
        _busy = true;
        UpdateButtons();
        Status.Text = busy;
        try { Status.Text = await operation(); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            try { await RefreshCoreAsync(vm); }
            catch (Exception ex) { Status.Text += " " + ex.Message; }
            _busy = false;
            UpdateButtons();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
