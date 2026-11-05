namespace OpenXLR.Core.Mixing;

public sealed record MonitorEqStatus(string? Preset, string? Error);

public sealed partial class Mixer
{
    private readonly Dictionary<string, string> _monitorEqPresets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monitorEqErrors = new(StringComparer.Ordinal);

    public void SetMonitorEqPreset(string mixId, string? preset, Func<MixerSettings, string?> persist)
    {
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            MixDefinition mix = MonitorMixLocked(mixId) ?? throw new InvalidOperationException("need a monitor mix");
            if (preset is not null && MonitorEqPresets.Find(preset) is null)
                throw new InvalidOperationException("unknown monitor EQ preset");
            string? previous = _monitorEqPresets.GetValueOrDefault(mixId);
            if (previous == preset && !_monitorEqErrors.ContainsKey(mixId)) return;
            if (preset is null) _monitorEqPresets.Remove(mixId);
            else _monitorEqPresets[mixId] = preset;
            try { PersistLocked(persist); }
            catch
            {
                if (previous is null) _monitorEqPresets.Remove(mixId);
                else _monitorEqPresets[mixId] = previous;
                throw;
            }
            _restarts.Forget(MixKey(mix));
            WireMixChainLocked(mix);
        }
    }

    private void RestoreMonitorEqLocked(Dictionary<string, string>? saved)
    {
        foreach (string id in _monitorEqPresets.Keys.Where(id => MonitorMixLocked(id) is null).ToArray())
        {
            _monitorEqPresets.Remove(id);
            _monitorEqErrors.Remove(id);
        }
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
        {
            string? preset = MonitorEqPresets.Find(saved?.GetValueOrDefault(mix.Id))?.Id;
            if (preset == _monitorEqPresets.GetValueOrDefault(mix.Id)) continue;
            if (preset is null) _monitorEqPresets.Remove(mix.Id);
            else _monitorEqPresets[mix.Id] = preset;
            WireMixChainLocked(mix);
        }
    }
}
