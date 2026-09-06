using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
    }

    public OptionsWindow(OptionsViewModel vm) : this()
    {
        DataContext = vm;
    }

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
