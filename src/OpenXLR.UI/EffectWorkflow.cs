using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

public sealed partial class InsertsViewModel
{
    // OpenXLR's clipboard deliberately holds a snapshot, never live view models.
    private static EffectChainData? _clipboard;
    private EffectChainData? _comparisonA, _comparisonB;
    private int _connectionEpoch;
    private bool _workflowBusy;
    private string? _workflowError;
    private string _presetName = "", _comparison = "";
    public bool CanEditEffects => !_workflowBusy;
    public string? WorkflowError { get => _workflowError; private set => Set(ref _workflowError, value); }
    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }
    public string Comparison { get => _comparison; private set => Set(ref _comparison, value); }
    public ObservableCollection<EffectChainPreset> Presets { get; } = [];
    public EffectChainPreset? SelectedPreset { get; set; }

    internal EffectChainData CaptureChain(InsertViewModel? single = null)
        => new(1, _channels, JsonSerializer.SerializeToNode(single is null ? Snapshot() : [single.ToPayload()])!.AsArray());

    public void CopyEffects(InsertViewModel? single = null)
    {
        _clipboard = CaptureChain(single).Copy();
        WorkflowError = null;
    }
    public async Task PasteEffectsAsync(bool replace)
    {
        if (_clipboard is null) { WorkflowError = "Copy an effect or chain in OpenXLR first."; return; }
        var data = _clipboard.Copy(freshIds: true);
        if (!replace)
        {
            var current = CaptureChain().Inserts;
            foreach (var entry in data.Inserts) current.Add(entry!.DeepClone());
            data = data with { Inserts = current };
        }
        await ApplyEffectsAsync(data);
    }
    public void StoreComparison(bool b)
    {
        if (!CanEditEffects) return;
        if (b) _comparisonB = CaptureChain().Copy(); else _comparisonA = CaptureChain().Copy();
        Comparison = b ? "Stored B" : "Stored A";
    }
    public async Task HearComparisonAsync(bool b)
    {
        var snapshot = b ? _comparisonB : _comparisonA;
        if (snapshot is null) { WorkflowError = b ? "Store B first." : "Store A first."; return; }
        int epoch = _connectionEpoch;
        if (await ApplyEffectsAsync(snapshot.Copy()) && epoch == _connectionEpoch) Comparison = b ? "Hearing B" : "Hearing A";
    }
    public void ReadPresets()
    {
        try
        {
            var presets = EffectChainPresets.Read();
            Presets.Clear();
            foreach (var preset in presets) Presets.Add(preset);
            SelectedPreset = null;
            Raise(nameof(SelectedPreset));
            WorkflowError = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { WorkflowError = ex.Message; }
    }
    public void SavePreset()
    {
        try { EffectChainPresets.Save(PresetName, CaptureChain()); ReadPresets(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { WorkflowError = ex.Message; }
    }
    public void DeletePreset()
    {
        if (SelectedPreset is not { } preset) return;
        try { EffectChainPresets.Delete(preset.Name); ReadPresets(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { WorkflowError = ex.Message; }
    }
    public async Task LoadPresetAsync()
    {
        if (SelectedPreset is { } preset) await ApplyEffectsAsync(preset.Chain.Copy(freshIds: true));
    }
    internal async Task<bool> ApplyEffectsAsync(EffectChainData data)
    {
        if (_workflowBusy) return false;
        try { data.Validate(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        { WorkflowError = ex.Message; return false; }
        // The daemon validates each plugin against the target width. Mono and
        // stereo presets may contain plugins supporting both, so do not guess.
        int epoch = _connectionEpoch;
        _workflowBusy = true; Raise(nameof(CanEditEffects));
        try
        {
            string? error = await _client.ReplaceInsertsAsync(_channel, data.Inserts);
            if (epoch != _connectionEpoch) return false;
            WorkflowError = error;
            return error is null;
        }
        finally
        {
            if (epoch == _connectionEpoch) { _workflowBusy = false; Raise(nameof(CanEditEffects)); }
        }
    }
    public async Task RenameEffectAsync(InsertViewModel insert, string name)
    {
        if (_workflowBusy) return;
        int epoch = _connectionEpoch;
        string? error = await _client.RenameInsertAsync(_channel, insert.Id, name.Trim());
        if (epoch == _connectionEpoch) WorkflowError = error;
    }
    private void ResetEffectWorkflow()
    {
        _connectionEpoch++;
        _workflowBusy = false;
        _comparisonA = _comparisonB = null;
        Comparison = "";
        WorkflowError = null;
        Raise(nameof(CanEditEffects));
    }
}
