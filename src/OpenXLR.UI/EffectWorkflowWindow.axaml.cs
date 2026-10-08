using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>Copy and paste, A/B comparison and saved presets for one insert chain.</summary>
public partial class EffectWorkflowWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _filesBusy, _closed;

    public EffectWorkflowWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Chain?.ReadPresets();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
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
    private void OnRefresh(object? sender, RoutedEventArgs e) => Chain?.ReadPresets();

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Chain is { SelectedPreset: { } preset } chain
            && await Dialogs.ConfirmAsync(this, "Delete preset", $"Delete the saved preset '{preset.Name}'? The live chain is kept.", "Delete")
            && !_closed && ReferenceEquals(chain.SelectedPreset, preset))
            chain.DeletePreset();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_closed || _filesBusy || Chain is not { } chain || !chain.CanEditEffects) return;
        _filesBusy = true;
        try
        {
            await using var input = await EffectPresetFiles.OpenImportAsync(this);
            if (input is null || _closed) return;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            limit.CancelAfter(EffectPresetFiles.Timeout);
            await chain.ImportPresetAsync(input, limit.Token);
        }
        catch (OperationCanceledException) { if (!_closed) chain.ReportWorkflowError("The preset import timed out."); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { if (!_closed) chain.ReportWorkflowError(ex.Message); }
        finally { _filesBusy = false; }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_closed || _filesBusy || Chain is not { } chain || !chain.CanEditEffects) return;
        _filesBusy = true;
        try
        {
            // The selected saved preset, or else the live chain under the typed name.
            var preset = chain.SelectedPreset ?? new EffectChainPreset(
                string.IsNullOrWhiteSpace(chain.PresetName) ? "Current chain" : chain.PresetName.Trim(), chain.CaptureChain());
            await EffectPresetFiles.ExportAsync(this, preset, _lifetime.Token);
            if (!_closed) chain.ReportWorkflowError(null);
        }
        catch (OperationCanceledException) { if (!_closed) chain.ReportWorkflowError("The preset export timed out."); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { if (!_closed) chain.ReportWorkflowError(ex.Message); }
        finally { _filesBusy = false; }
    }
}
