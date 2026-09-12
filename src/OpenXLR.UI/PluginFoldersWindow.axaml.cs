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
    public string Status => InUse ? "In use" : Enabled ? "Enabled" : "Disabled";
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
        Opened += async (_, _) => await RunAsync(vm, () => Task.FromResult(""), "Reading plugin folders…");
    }

    private void UpdateButtons()
    {
        if (DataContext is not OptionsViewModel vm) return;
        AddFolder.IsEnabled = RescanFolders.IsEnabled = !_busy && vm.CanSyncWindows;
        RemoveFolder.IsEnabled = !_busy && vm.CanManageWindows && FolderList.SelectedItem is string;
        FolderList.IsEnabled = PluginList.IsEnabled = CloseButton.IsEnabled = !_busy;
        WindowsPluginEntry? selected = SelectedPlugin;
        RemoveUses.IsVisible = selected?.InUse == true;
        RemoveUses.IsEnabled = !_busy && selected?.InUse == true;
        TogglePlugin.Content = selected?.Enabled == false ? "Enable in OpenXLR" : "Disable in OpenXLR";
        TogglePlugin.IsEnabled = !_busy && vm.CanSyncWindows && selected is { InUse: false };
        DeletePlugin.IsEnabled = !_busy && vm.CanManageWindows && selected is { InUse: false, CanDelete: true };
        WineUninstaller.IsVisible = selected?.WinePrefix is not null;
        WineUninstaller.IsEnabled = !_busy && vm.CanSyncWindows && selected is { InUse: false };
        PluginDetail.Text = selected is null ? "Select a plugin to manage it." : selected.Path
            + (selected.InUse ? "\nRemove this plugin from its insert chains before disabling or uninstalling it." : "");
    }

    private async Task RefreshCoreAsync(OptionsViewModel vm)
    {
        string? folder = FolderList.SelectedItem as string;
        await vm.LoadPluginSetupAsync();
        FolderList.SelectedItem = folder is not null && vm.WindowsDirectories.Contains(folder)
            ? folder : vm.WindowsDirectories.FirstOrDefault();
        if (!vm.CanManageWindows) Status.Text = vm.WindowsPlugins;
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
        if (reply is null) Status.Text = "The daemon did not answer the plugin-file request.";
        else if (reply["ok"]?.GetValue<bool>() != true)
            Status.Text = reply["message"]?.GetValue<string>() ?? "Could not read the plugin folder.";
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
        EmptyPlugins.Text = FolderList.SelectedItem is null ? "Select a folder to see its plugins."
            : reply?["ok"]?.GetValue<bool>() == true ? "No Windows VST3 or CLAP plugin files found."
            : "Plugin list is unavailable.";
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
            string? folder = (await PluginInstall.PickFolderAsync(this, "Add a Windows plugin folder", allowMultiple: false)).FirstOrDefault();
            if (folder is not null)
                await ChangeAsync(vm, () => vm.Client.AddWindowsPluginFolderAsync(folder, ChangeTimeout), "Adding folder…");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void OnRemoveFolder(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || FolderList.SelectedItem is not string folder) return;
        string detail = $"Remove {folder} from the scan list and remove its generated VST3 and CLAP wrappers? The original plugin files will stay where they are.";
        if (vm.SystemBridge) detail += " This also affects other applications using system yabridge.";
        if (!await Dialogs.ConfirmAsync(this, "Remove plugin folder?", detail, "Remove from list")) return;
        await ChangeAsync(vm, () => vm.Client.RemoveWindowsPluginFolderAsync(folder, ChangeTimeout), "Removing folder…");
    }

    private async void OnRemoveUses(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { InUse: true } plugin) return;
        if (!await Dialogs.ConfirmAsync(this, "Remove plugin from all chains?",
            $"Remove every occurrence of {plugin.Name} from the current input and mix chains, including bypassed inserts? Other plugins stay in their existing order. Audio may pause briefly in affected chains. Plugin files, exclusions and saved profiles are not changed.", "Remove from all chains")) return;
        await ChangeAsync(vm, () => vm.Client.RemoveWindowsPluginInsertsAsync(plugin.Path, ChangeTimeout), "Removing plugin from all chains…");
    }

    private async void OnTogglePlugin(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { } plugin) return;
        if (plugin.Enabled && !await Dialogs.ConfirmAsync(this, "Disable plugin?",
            $"Disable {plugin.Name} in the selected yabridge configuration? Its files will stay in place."
            + (vm.SystemBridge ? " This also affects other applications using system yabridge." : ""), "Disable")) return;
        await ChangeAsync(vm, () => vm.Client.SetWindowsPluginEnabledAsync(plugin.Path, !plugin.Enabled, ChangeTimeout),
            plugin.Enabled ? "Disabling plugin…" : "Enabling plugin…");
    }

    private async void OnDeletePlugin(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { CanDelete: true } plugin) return;
        if (!await Dialogs.ConfirmAsync(this, "Delete plugin file?",
            $"Permanently delete {plugin.Path} and its generated wrappers? Only this standalone file or bundle is removed; other plugins in the folder stay. This cannot be undone.", "Delete file")) return;
        await ChangeAsync(vm, () => vm.Client.DeleteWindowsPluginAsync(plugin.Path, ChangeTimeout), "Deleting plugin…");
    }

    private async void OnWineUninstaller(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not OptionsViewModel vm || SelectedPlugin is not { WinePrefix: not null } plugin
            || FolderList.SelectedItem is not string folder) return;
        if (!await Dialogs.ConfirmAsync(this, "Open Wine uninstaller?",
            $"Open the installed-apps list for {plugin.WinePrefix}? Choose the plugin's installer entry there. An uninstaller may remove several effects from the same package. Close other Wine applications and remove affected inserts first. OpenXLR will rescan when the uninstaller closes.", "Open uninstaller")) return;
        await RunAsync(vm, async () =>
        {
            JsonNode? fresh = await vm.Client.RequestWindowsPluginFilesAsync(folder, TimeSpan.FromSeconds(20));
            JsonNode? item = (fresh?["plugins"] as JsonArray)?.FirstOrDefault(p => p?["path"]?.GetValue<string>() == plugin.Path);
            if (fresh?["ok"]?.GetValue<bool>() != true || item is null) return "Could not verify the plugin before opening the uninstaller.";
            if (item["inUse"]?.GetValue<bool>() == true) return "Remove this plugin from its insert chains before uninstalling it.";
            if (item["winePrefix"]?.GetValue<string>() is not { Length: > 0 } prefix) return "This plugin no longer belongs to a Wine prefix.";
            int exit = await ProcessRunner.RunInteractiveAsync("wine", ["uninstaller"],
                new Dictionary<string, string> { ["WINEPREFIX"] = prefix });
            string result = PluginInstall.Describe(await vm.Client.SyncWindowsPluginsAsync(ChangeTimeout), "the rescan");
            return (exit == 0 ? "Wine uninstaller closed. " : $"Wine uninstaller exited with code {exit}. ") + result;
        }, "Waiting for Wine uninstaller…");
    }

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (!_busy && DataContext is OptionsViewModel vm)
            await ChangeAsync(vm, () => vm.Client.SyncWindowsPluginsAsync(ChangeTimeout), "Syncing and rescanning…");
    }

    private Task ChangeAsync(OptionsViewModel vm, Func<Task<JsonNode?>> change, string busy)
        => RunAsync(vm, async () => PluginInstall.Describe(await change(), "the plugin operation"), busy);

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
