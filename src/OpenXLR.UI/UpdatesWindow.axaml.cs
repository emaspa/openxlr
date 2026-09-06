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
        if (DataContext is UpdatesViewModel { Url: { } url }) ExternalLink.Open(url);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
