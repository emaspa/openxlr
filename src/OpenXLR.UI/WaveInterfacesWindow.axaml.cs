using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>Additional interfaces: enable units next to the primary one, control them, add their microphones.</summary>
public partial class WaveInterfacesWindow : Window
{
    public WaveInterfacesWindow() => InitializeComponent();
    public WaveInterfacesWindow(MainViewModel main) : this() => DataContext = main;

    private async void OnAddInput(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WaveInterfaceViewModel unit) await unit.AddInputAsync();
    }
}
