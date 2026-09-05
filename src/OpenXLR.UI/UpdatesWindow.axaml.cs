using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class UpdatesWindow : Window
{
    public UpdatesWindow() => InitializeComponent();

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpdatesViewModel vm) await vm.CheckAsync(manual: true);
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UpdatesViewModel { Url: { } url }) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "https" || uri.Host != "github.com") return;
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(url);
            Process.Start(start)?.Dispose();
        }
        catch (Exception) { /* opening a browser is best effort */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
