namespace OpenXLR.Core.Mixing;

/// <summary>Align algorithmic mix-insert latency, not devices, transports or intentional delay effects.</summary>
internal static class MixLatency
{
    internal const double MaxMilliseconds = 2000;

    internal static Dictionary<string, double> Plan(IReadOnlyDictionary<string, double?> totals, out string? error)
    {
        error = null;
        if (totals.Values.Any(v => v is null || !double.IsFinite(v.Value) || v.Value < 0))
            error = "Waiting for a valid latency report from every active mix insert.";
        else if (totals.Values.Any(v => v > MaxMilliseconds))
            error = "Mix latency exceeds the two-second compensation limit.";
        double maximum = error is null && totals.Count > 0 ? totals.Values.Max(v => v!.Value) : 0;
        bool valid = error is null;
        return totals.ToDictionary(p => p.Key, p => valid ? maximum - p.Value!.Value : 0);
    }
}

public sealed partial class Mixer
{
    private readonly Dictionary<(string Chain, string Insert), double?> _latencyReports = [];
    private bool _compensateMixLatency;
    private string? _mixLatencyError;
    private readonly Dictionary<string, FilterHandle> _mixDelays = [];
    private readonly Dictionary<string, PortLink> _mixDelayInputs = [];
    private readonly Dictionary<string, double> _mixDelayValues = [];
    private readonly Dictionary<string, string> _mixDelayErrors = [];

    public void SetMixLatencyCompensation(bool enabled, Func<MixerSettings, string?>? persist = null)
    {
        lock (_gate)
        {
            if (_compensateMixLatency == enabled) return;
            bool previous = _compensateMixLatency;
            _compensateMixLatency = enabled;
            try { if (persist is not null) PersistLocked(persist); }
            catch { _compensateMixLatency = previous; throw; }
            _pw.MeasurePluginLatency = enabled;
            if (_built)
            {
                try { WireInputFeedsLocked(); }
                catch
                {
                    // Input rewiring keeps the previous graph on failure. Restore
                    // the setting too, before a failed toggle can be acknowledged.
                    _compensateMixLatency = previous;
                    _pw.MeasurePluginLatency = previous;
                    if (persist is not null) PersistLocked(persist);
                    throw;
                }
                // Use the same insert dispatch as ordinary edits, including
                // software and external channels when the layout supports them.
                foreach (ChannelDefinition channel in _config.Channels)
                    if (channel.InputPair is null && IsInsertChannel(channel.Id))
                        RewireInsertKeyLocked(channel.Id);
                foreach (MixDefinition mix in _config.Mixes)
                {
                    _restarts.Forget("delay:" + mix.Id);
                    WireMixChainLocked(mix);
                }
                UpdateMixLatencyLocked();
            }
        }
    }

    private double? InsertLatencyLocked(string key, InsertDefinition insert)
    {
        if (insert.Bypass) return 0;
        if (_insertErrors.ContainsKey(key) || !_chains.TryGetValue(key, out FilterHandle? chain) || !chain.IsAlive) return null;
        var host = chain.InsertStages.FirstOrDefault(s => s.Id == insert.Id).Stage?.NativeHost;
        if (host is not null) return host.IsHealthy ? host.LatencyMilliseconds : null;
        return PluginCatalog.Find(insert) is { Kind: "lv2", ReportsLatency: false } ? 0 : null;
    }

    private double? MixLatencyLocked(MixDefinition mix)
    {
        double total = 0;
        foreach (InsertDefinition insert in InsertsFor(MixKey(mix)))
        {
            double? latency = InsertLatencyLocked(MixKey(mix), insert);
            if (latency is null) return null;
            total += latency.Value;
        }
        return total;
    }

    private void RemoveMixDelayLocked(string id)
    {
        if (_mixDelayInputs.Remove(id, out PortLink? link)) _pw.Unlink(link);
        if (_mixDelays.Remove(id, out FilterHandle? filter)) _pw.StopFilter(filter);
        _mixDelayValues.Remove(id);
        _mixDelayErrors.Remove(id);
    }

    private void WireMixDelayLocked(MixDefinition mix)
    {
        if (!_compensateMixLatency) return;
        if (_restarts.Blocked("delay:" + mix.Id))
        {
            _mixDelayErrors[mix.Id] = "The mix delay kept failing; toggle latency compensation to retry.";
            return;
        }
        FilterHandle? filter = null;
        PortLink? link = null;
        try
        {
            (string node, string prefix) = MixTapLocked(mix);
            filter = _pw.CreateMixDelay(mix.Id);
            link = _pw.LinkNodes(node, prefix, filter.SinkName, "playback");
            if (link.Pairs.Count != 2) throw new InvalidOperationException("The mix delay did not connect both audio channels.");
            _mixDelays[mix.Id] = filter;
            _mixDelayInputs[mix.Id] = link;
            _mixDelayValues[mix.Id] = 0;
        }
        catch (Exception ex)
        {
            if (link is not null) _pw.Unlink(link);
            if (filter is not null) _pw.StopFilter(filter);
            _mixDelayErrors[mix.Id] = ex.Message;
            _restarts.Failed("delay:" + mix.Id);
        }
    }

    private bool TrackLatencyReportsLocked()
    {
        bool changed = false;
        int count = 0;
        foreach ((string key, List<InsertDefinition> inserts) in _inserts)
            foreach (InsertDefinition insert in inserts)
            {
                count++;
                var identity = (key, insert.Id);
                double? value = InsertLatencyLocked(key, insert);
                if (_latencyReports.TryGetValue(identity, out double? old) && old == value) continue;
                _latencyReports[identity] = value;
                changed = true;
            }
        if (count != _latencyReports.Count)
            foreach (var key in _latencyReports.Keys.Where(k => !_inserts.TryGetValue(k.Chain, out var list) || !list.Any(i => i.Id == k.Insert)).ToArray())
            { _latencyReports.Remove(key); changed = true; }
        return changed;
    }

    private bool UpdateMixLatencyLocked()
    {
        bool reportsChanged = TrackLatencyReportsLocked();
        if (!_compensateMixLatency) { _mixLatencyError = null; return reportsChanged; }
        var totals = _config.Mixes.ToDictionary(m => m.Id, MixLatencyLocked);
        var plan = MixLatency.Plan(totals, out string? error);
        if (_mixDelays.Count != _config.Mixes.Count || _mixDelayErrors.Count > 0)
            error = _mixDelayErrors.Values.FirstOrDefault() ?? "Not all mix delay paths are available.";
        if (error is not null)
            foreach (string id in plan.Keys) plan[id] = 0;
        bool changed = reportsChanged || _mixLatencyError != error;
        _mixLatencyError = error;
        foreach ((string id, double delay) in plan)
        {
            if (!_mixDelays.TryGetValue(id, out FilterHandle? filter) || _mixDelayValues.GetValueOrDefault(id) == delay) continue;
            try
            {
                _pw.SetMixDelay(filter, delay);
                _mixDelayValues[id] = delay;
                changed = true;
            }
            catch (Exception ex)
            {
                _mixLatencyError = ex.Message;
                _mixDelayErrors[id] = ex.Message;
                changed = true;
            }
        }
        return changed;
    }
}
