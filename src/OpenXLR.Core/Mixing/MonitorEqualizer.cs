namespace OpenXLR.Core.Mixing;

public sealed record MonitorEqStatus(string? Preset, string? Error);

public sealed partial class Mixer
{
    private readonly Dictionary<string, string> _monitorEqPresets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monitorEqErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (FilterHandle Filter, PortLink Tap)> _monitorEqStages = new(StringComparer.Ordinal);

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
            WireMonitorEqLocked(mix);
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
            WireMonitorEqLocked(mix);
        }
    }

    private void WireMonitorEqLocked(MixDefinition mix)
    {
        RemoveMonitorEqStageLocked(mix.Id);
        _monitorEqErrors.Remove(mix.Id);
        if (MonitorEqPresets.Find(_monitorEqPresets.GetValueOrDefault(mix.Id)) is { } preset)
        {
            FilterHandle? filter = null;
            try
            {
                EnsurePulseHeadroomLocked(newStreams: 0, newNodes: 2);
                filter = _pw.CreateMonitorEq(mix.Id, $"OpenXLR {mix.Name} Monitor EQ", preset);
                _monitorEqStages[mix.Id] = (filter, LinkMonitorEqLocked(mix, filter));
            }
            catch (Exception ex)
            {
                if (filter is not null) _pw.StopFilter(filter);
                _monitorEqErrors[mix.Id] = ex.Message;
            }
        }
        RewireMixConsumersLocked(mix);
    }

    private PortLink LinkMonitorEqLocked(MixDefinition mix, FilterHandle filter)
    {
        (string node, string prefix) = MixInsertTapLocked(mix);
        PortLink tap = _pw.LinkNodes(node, prefix, filter.SinkName, "playback");
        if (tap.Complete && tap.Pairs.Count == 2) return tap;
        _pw.Unlink(tap);
        throw new InvalidOperationException("The stereo link to the monitor EQ could not be made.");
    }

    private void RelinkMonitorEqLocked(MixDefinition mix)
    {
        if (!_monitorEqStages.Remove(mix.Id, out var stage)) return;
        _pw.Unlink(stage.Tap);
        try { _monitorEqStages[mix.Id] = (stage.Filter, LinkMonitorEqLocked(mix, stage.Filter)); }
        catch (Exception ex)
        {
            _pw.StopFilter(stage.Filter);
            _monitorEqErrors[mix.Id] = ex.Message;
        }
    }

    private bool EnsureMonitorEqLocked(MixDefinition mix)
    {
        if (!_monitorEqStages.TryGetValue(mix.Id, out var stage)) return false;
        if (!stage.Filter.IsAlive)
        {
            RemoveMonitorEqStageLocked(mix.Id);
            _monitorEqErrors[mix.Id] = "The monitor EQ stopped. Turn it off and on to retry.";
        }
        else
        {
            LinkHealth health = _pw.EnsureLinks(stage.Tap);
            if (health != LinkHealth.Broken) return health == LinkHealth.Relinked;
            RelinkMonitorEqLocked(mix);
        }
        if (!_monitorEqStages.ContainsKey(mix.Id)) RewireMixConsumersLocked(mix);
        return true;
    }

    private void RemoveMonitorEqStageLocked(string id)
    {
        if (!_monitorEqStages.Remove(id, out var stage)) return;
        _pw.Unlink(stage.Tap);
        _pw.StopFilter(stage.Filter);
    }
}
