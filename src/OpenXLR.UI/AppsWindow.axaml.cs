using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class AppsWindow : Window
{
    public AppsWindow()
    {
        InitializeComponent();
        // The scan is quick (a few hundred small files); done once per open.
        InstalledPicker.ItemsSource = DesktopApps.Scan();
        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                ChannelPicker.ItemsSource = vm.Channels.Where(c => c.AcceptsApps)
                    .Select(c => new ChannelOption(c.Id, c.Name)).ToList();
        };
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (InstalledPicker.SelectedItem is not InstalledApp app) return;
        if (ChannelPicker.SelectedItem is not ChannelOption channel) return;
        vm.AddApp(app.Identity, app.Name, channel.Id);
        InstalledPicker.SelectedItem = null;
    }

    private void OnForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is AppStreamViewModel app) app.Forget();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
