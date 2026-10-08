using OpenXLR.UI.Localization;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>Saved presets for one effect: those holding exactly this plugin, in this format.</summary>
public sealed class PluginPresetViewModel(InsertViewModel target) : ViewModelBase
{
    internal InsertViewModel Target => target;
    public string Label => target.Label;
    public string Name { get; set; } = "";
    public EffectChainPreset? Selected { get; set; }
    public IReadOnlyList<EffectChainPreset> Presets { get; private set; } = [];
    public string? Error { get; private set; }

    internal bool Fits(EffectChainPreset preset) => preset.Chain.Inserts.Count == 1
        && preset.Chain.Inserts[0]?["plugin"]?.GetValue<string>() == target.Plugin
        && preset.Chain.Inserts[0]?["kind"]?.GetValue<string>() == target.Kind;

    internal void Refresh()
    {
        try { Presets = EffectChainPresets.Read().Where(Fits).ToArray(); Selected = null; Error = null; }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { Error = ex.Message; }
        Raise(null);
    }

    internal void Save()
    {
        if (!target.Owner.Items.Contains(target)) { Fail(Localizer.Text("EffectNoLongerInChain")); return; }
        try { EffectChainPresets.Save(Name, target.Owner.CaptureChain(target)); Refresh(); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { Fail(ex.Message); }
    }

    internal async Task LoadAsync()
    {
        if (Selected is not { } preset) return;
        try { await target.Owner.ApplySinglePresetAsync(target, preset.Chain); Fail(target.Owner.WorkflowError); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { Fail(ex.Message); }
    }

    /// <summary>The selected saved preset, or else this effect's live settings under the typed name.</summary>
    internal EffectChainPreset Export()
        => Selected ?? new(string.IsNullOrWhiteSpace(Name) ? Localizer.Text("CurrentEffect") : Name.Trim(), target.Owner.CaptureChain(target));

    /// <summary>Decode a preset file and save it when it holds this plugin. The live chain is not touched.</summary>
    internal async Task ImportAsync(Stream input, CancellationToken cancellationToken)
    {
        try
        {
            var preset = await EffectPresetFiles.DecodeAsync(input, cancellationToken);
            if (!Fits(preset)) throw new InvalidDataException(Localizer.Text("PresetForDifferentEffect"));
            EffectChainPresets.Save(preset.Name, preset.Chain);
            Refresh();
        }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { Fail(ex.Message); }
    }

    internal void Delete(EffectChainPreset preset)
    {
        try { EffectChainPresets.Delete(preset.Name); Refresh(); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { Fail(ex.Message); }
    }

    internal void Fail(string? error) { Error = error; Raise(nameof(Error)); }
}

/// <summary>One effect's presets. It closes when its effect leaves the chain.</summary>
public partial class PluginPresetWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed, _busy;
    private InsertViewModel? _subscribed;
    private PluginPresetViewModel? Model => DataContext as PluginPresetViewModel;

    public PluginPresetWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (Model is not { } model) return;
            model.Refresh();
            _subscribed = model.Target;
            _subscribed.Detached += OnTargetRemoved;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (_subscribed is { } target) target.Detached -= OnTargetRemoved;
        };
    }

    public PluginPresetWindow(InsertViewModel target) : this() => DataContext = new PluginPresetViewModel(target);

    private void OnTargetRemoved() => Close();
    private void OnSave(object? sender, RoutedEventArgs e) => Model?.Save();
    private async void OnLoad(object? sender, RoutedEventArgs e) { if (!_busy && Model is { } model) await model.LoadAsync(); }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Model is { Selected: { } preset } model
            && await Dialogs.ConfirmAsync(this, Localizer.Text("DeletePreset"), Localizer.Format("DeleteEffectPresetDetail", preset.Name), Localizer.Text("Delete")) && !_closed)
            model.Delete(preset);
    }

    private async void OnImport(object? sender, RoutedEventArgs e) => await RunFileAsync(async () =>
    {
        await using var input = await EffectPresetFiles.OpenImportAsync(this);
        if (input is null || _closed || Model is not { } model) return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        limit.CancelAfter(EffectPresetFiles.Timeout);
        await model.ImportAsync(input, limit.Token);
    });

    private async void OnExport(object? sender, RoutedEventArgs e) => await RunFileAsync(async () =>
    {
        if (Model is not { } model) return;
        await EffectPresetFiles.ExportAsync(this, model.Export(), _lifetime.Token);
        if (!_closed) model.Fail(null);
    });

    private async Task RunFileAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        var controls = this.FindControl<StackPanel>("PresetControls")!;
        _busy = true;
        controls.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) Model?.Fail(Localizer.Text("PresetFileTimedOut")); }
        catch (Exception ex) when (EffectPresetFiles.IsFailure(ex)) { if (!_closed) Model?.Fail(ex.Message); }
        finally { _busy = false; controls.IsEnabled = true; }
    }
}
