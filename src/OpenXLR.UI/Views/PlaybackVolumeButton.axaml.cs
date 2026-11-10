using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

public partial class PlaybackVolumeButton : UserControl
{
    public PlaybackVolumeButton() => InitializeComponent();

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaybackVolumeViewModel volume) volume.Reset();
    }
}
