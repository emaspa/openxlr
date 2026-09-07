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

    private void OnDefaults(object? sender, RoutedEventArgs e) => (DataContext as InsertViewModel)?.ResetToDefaults();

    private async void OnNativeEditor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InsertViewModel insert) await insert.Owner.ShowNativeEditorAsync(insert);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
