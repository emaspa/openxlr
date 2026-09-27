using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class SoundCheckWindow : Window
{
    private bool _closing, _closed;
    public SoundCheckWindow()
    {
        InitializeComponent();
        Closing += async (_, e) =>
        {
            if (_closed || DataContext is not SoundCheckViewModel vm) return;
            if (e.CloseReason != WindowCloseReason.WindowClosing)
            {
                _ = vm.StopOnCloseAsync();
                return;
            }
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            IsEnabled = false; // Do not queue a new recording behind the pending stop.
            await vm.StopOnCloseAsync();
            _closed = true;
            Close();
        };
    }
    private async void OnRecord(object? sender, RoutedEventArgs e) { if (DataContext is SoundCheckViewModel vm) await vm.RunAsync("record"); }
    private async void OnLoop(object? sender, RoutedEventArgs e) { if (DataContext is SoundCheckViewModel vm) await vm.RunAsync("loop"); }
    private async void OnLive(object? sender, RoutedEventArgs e) { if (DataContext is SoundCheckViewModel vm) await vm.RunAsync("live"); }
    private async void OnStop(object? sender, RoutedEventArgs e) { if (DataContext is SoundCheckViewModel vm) await vm.RunAsync("stop"); }
}
