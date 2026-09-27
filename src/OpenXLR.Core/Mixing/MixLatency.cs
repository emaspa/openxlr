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
    private readonly HashSet<string> _requiredMixDelays = [];

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
                catch (Exception failure)
                {
                    _compensateMixLatency = previous;
                    _pw.MeasurePluginLatency = previous;
                    try { if (persist is not null) PersistLocked(persist); }
                    catch (Exception rollback)
                    {
                        // The first save still stands. Keep state consistent
                        // with it and report both failures to the caller.
                        _compensateMixLatency = enabled;
                        _pw.MeasurePluginLatency = enabled;
                        throw new AggregateException("Audio rewiring failed and the saved compensation setting could not be restored.", failure, rollback);
                    }
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
        if (!_compensateMixLatency || !_requiredMixDelays.Contains(mix.Id)) return;
        if (_restarts.Blocked("delay:" + mix.Id))
        {
            _mixDelayErrors[mix.Id] = "The mix delay kept failing; automatic retry is paused until the recovery window expires.";
            return;
        }
        FilterHandle? filter = null;
        PortLink? link = null;
        try
        {
            EnsurePulseHeadroomLocked(newStreams: 0, newNodes: 2);
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
        foreach ((string key, List<InsertDefinition> inserts) in _inserts)
            foreach (InsertDefinition insert in inserts)
            {
                var identity = (key, insert.Id);
                double? value = InsertLatencyLocked(key, insert);
                if (_latencyReports.TryGetValue(identity, out double? old) && old == value) continue;
                _latencyReports[identity] = value;
                changed = true;
            }
        foreach (var key in _latencyReports.Keys.Where(k => !_inserts.TryGetValue(k.Chain, out var list) || !list.Any(i => i.Id == k.Insert)).ToArray())
            { _latencyReports.Remove(key); changed = true; }
        return changed;
    }

    private bool UpdateMixLatencyLocked()
    {
        bool reportsChanged = TrackLatencyReportsLocked();
        if (!_compensateMixLatency) { _requiredMixDelays.Clear(); _mixLatencyError = null; return reportsChanged; }
        var totals = _config.Mixes.ToDictionary(m => m.Id, MixLatencyLocked);
        var plan = MixLatency.Plan(totals, out string? error);
        _requiredMixDelays.Clear();
        if (error is null)
            foreach ((string id, double delay) in plan)
                if (delay > 0) _requiredMixDelays.Add(id);
        foreach (MixDefinition mix in _config.Mixes)
        {
            bool required = _requiredMixDelays.Contains(mix.Id);
            bool exists = _mixDelays.TryGetValue(mix.Id, out FilterHandle? filter);
            if (!required)
            {
                _mixDelayErrors.Remove(mix.Id);
                _restarts.Forget("delay:" + mix.Id);
                if (exists) { WireMixConsumersLocked(mix); reportsChanged = true; }
            }
            else if (!_restarts.Blocked("delay:" + mix.Id) && (!exists || !filter!.IsAlive ||
                _mixDelayErrors.ContainsKey(mix.Id) || _pw.EnsureLinks(_mixDelayInputs[mix.Id]) == LinkHealth.Broken))
            {
                if (exists) _restarts.Failed("delay:" + mix.Id);
                WireMixConsumersLocked(mix);
                reportsChanged = true;
            }
        }
        if (_mixDelayErrors.Count > 0)
            error = _mixDelayErrors.Values.First();
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
