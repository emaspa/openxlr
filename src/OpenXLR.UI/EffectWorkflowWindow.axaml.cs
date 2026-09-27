using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class EffectWorkflowWindow : Window
{
    public EffectWorkflowWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Chain?.ReadPresets();
    }
    private InsertsViewModel? Chain => DataContext as InsertsViewModel;
    private void OnCopy(object? sender, RoutedEventArgs e) => Chain?.CopyEffects();
    private async void OnPaste(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.PasteEffectsAsync(false); }
    private async void OnReplace(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.PasteEffectsAsync(true); }
    private void OnStoreA(object? sender, RoutedEventArgs e) => Chain?.StoreComparison(false);
    private void OnStoreB(object? sender, RoutedEventArgs e) => Chain?.StoreComparison(true);
    private async void OnHearA(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.HearComparisonAsync(false); }
    private async void OnHearB(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.HearComparisonAsync(true); }
    private void OnSave(object? sender, RoutedEventArgs e) => Chain?.SavePreset();
    private async void OnLoad(object? sender, RoutedEventArgs e) { if (Chain is { } chain) await chain.LoadPresetAsync(); }
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Chain is { SelectedPreset: { } preset } chain
            && await Dialogs.ConfirmAsync(this, "Delete preset", $"Delete the saved chain '{preset.Name}'? The live chain is kept.", "Delete"))
            chain.DeletePreset();
    }
    private void OnRefresh(object? sender, RoutedEventArgs e) => Chain?.ReadPresets();
}
