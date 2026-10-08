using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// One insert's generated controls in its own window (non-modal, one per
/// insert; the main window keeps them and closes them when the insert goes).
/// </summary>
public partial class InsertControlsWindow : Window
{
    public InsertControlsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => (DataContext as InsertViewModel)?.EnsureParams();
    }

    private PluginPresetWindow? _presets;

    private void OnPluginPresets(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InsertViewModel insert) return;
        if (_presets is not null) { _presets.Activate(); return; }
        _presets = new PluginPresetWindow(insert);
        _presets.Closed += (_, _) => _presets = null;
        _presets.Show(this);
    }

    private void OnDefaults(object? sender, RoutedEventArgs e) => (DataContext as InsertViewModel)?.ResetToDefaults();

    private async void OnNativeEditor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert) await insert.Owner.ShowNativeEditorAsync(insert);
    }

    private async void OnNativeEditorRules(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert)
            await new NativeEditorRulesWindow(insert.Owner.Client, insert.Kind, insert.Plugin).ShowDialog(this);
    }

    private const string EditorManual =
        "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#plugin-editors";

    private void OnEditorManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(EditorManual);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
