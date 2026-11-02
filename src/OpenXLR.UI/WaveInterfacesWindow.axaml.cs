using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>Additional interfaces: enable units next to the primary one, control them, add their microphones.</summary>
public partial class WaveInterfacesWindow : Window
{
    public WaveInterfacesWindow() => InitializeComponent();
    public WaveInterfacesWindow(MainViewModel main) : this() => DataContext = main;

    private async void OnEnable(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: WaveInterfaceViewModel unit } toggle) return;
        bool enabled = toggle.IsChecked == true;
        await unit.EnableAsync(enabled, () => Dialogs.KeepCaptureChannelsAsync(this, unit.Name, unit.CanKeepCaptureChannels));
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, unit.Enabled);
    }

    private void OnCardProfileManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(MainWindow.CardProfileManualLink);

    private async void OnAddInput(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WaveInterfaceViewModel unit) await unit.AddInputAsync();
    }
}
