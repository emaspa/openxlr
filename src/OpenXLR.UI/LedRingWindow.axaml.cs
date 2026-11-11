using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>The LED ring window, opened from the LEDs button beside the headphone controls.</summary>
public partial class LedRingWindow : Window
{
    public LedRingWindow() => InitializeComponent();

    public LedRingWindow(LedRingViewModel led) : this()
    {
        DataContext = led;
        ColourSwatches.Fill(LedGainSwatches, LedGainColour, LedRingViewModel.Swatches);
        ColourSwatches.Fill(LedHeadphoneSwatches, LedHeadphoneColour, LedRingViewModel.Swatches);
        ColourSwatches.Fill(LedMixSwatches, LedMixColour, LedRingViewModel.Swatches);
        // The unit can leave while the window is open: there is nothing left to set.
        led.PropertyChanged += Left;
        Closed += (_, _) => led.PropertyChanged -= Left;

        void Left(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LedRingViewModel.Available) && !led.Available) Close();
        }
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LedRingViewModel led) return;
        LedRingReset.IsEnabled = false;
        try { await led.ResetAsync(); }
        finally { LedRingReset.IsEnabled = true; }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
