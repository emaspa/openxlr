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
                ChannelPicker.ItemsSource = vm.Channels.Select(c => c.Id).Append(AppStreamViewModel.Ignore).ToList();
            ConstrainToScreen();
        };
    }

    /// <summary>
    /// Keep the window inside the screen it opens on. It sizes itself to its
    /// cards and cannot be resized, so on a short screen, or with a long list
    /// of known applications, its bottom would otherwise fall off the desktop
    /// and take the add controls and Close with it. The list scrolls instead.
    /// </summary>
    private void ConstrainToScreen()
    {
        var screen = Screens.ScreenFromWindow(Owner as Window ?? this) ?? Screens.Primary;
        if (screen is null) return;
        double usable = screen.WorkingArea.Height / screen.Scaling;
        if (usable > 240) MaxHeight = System.Math.Min(MaxHeight, usable - 60);
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (InstalledPicker.SelectedItem is not InstalledApp app) return;
        if (ChannelPicker.SelectedItem is not string channel || channel.Length == 0) return;
        vm.AddApp(app.Identity, app.Name, channel);
        InstalledPicker.SelectedItem = null;
    }

    private void OnForget(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is AppStreamViewModel app) app.Forget();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
