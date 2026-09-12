using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class PluginFoldersWindow : Window
{
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(4);
    private bool _busy;

    public PluginFoldersWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_busy && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;
        };
    }

    public PluginFoldersWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
        Opened += async (_, _) => await RefreshAsync(vm);
    }

    private void UpdateButtons()
    {
        if (DataContext is not OptionsViewModel vm) return;
        AddFolder.IsEnabled = RescanFolders.IsEnabled = !_busy && vm.CanSyncWindows;
        RemoveFolder.IsEnabled = !_busy && vm.CanManageWindows && FolderList.SelectedItem is string;
        FolderList.IsEnabled = CloseButton.IsEnabled = !_busy;
    }

    private async Task RefreshAsync(OptionsViewModel vm)
    {
        _busy = true;
        UpdateButtons();
        try
        {
            await vm.LoadPluginSetupAsync();
            if (!vm.CanSyncWindows) Status.Text = vm.WindowsPlugins;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

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

    private async void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (!_busy && DataContext is OptionsViewModel vm)
            await ChangeAsync(vm, () => vm.Client.SyncWindowsPluginsAsync(ChangeTimeout), "Syncing and rescanning…");
    }

    private async Task ChangeAsync(OptionsViewModel vm, Func<Task<JsonNode?>> change, string busy)
    {
        _busy = true;
        UpdateButtons();
        Status.Text = busy;
        try { Status.Text = PluginInstall.Describe(await change(), "the folder operation"); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            try { await vm.LoadPluginSetupAsync(); }
            catch (Exception ex) { Status.Text += " " + ex.Message; }
            _busy = false;
            UpdateButtons();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
