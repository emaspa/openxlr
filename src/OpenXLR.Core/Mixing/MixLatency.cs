namespace OpenXLR.Core.Mixing;

/// <summary>
/// Latency compensation across mixes: the plugin latency each insert reports,
/// summed per mix, and the delay that brings every mix level with the
/// slowest one. It aligns the algorithmic latency of mix inserts only, not
/// devices, transports or an effect whose delay is the point of it.
/// </summary>
internal static class MixLatency
{
    /// <summary>The longest delay one mix is given; a plan that needs more applies none.</summary>
    internal const double MaxMilliseconds = 2000;

    /// <summary>
    /// What one insert adds to its chain, in milliseconds, or null when it
    /// gives no figure. A bypassed insert is out of the chain. A chain that
    /// is not running reports nothing. A plugin in the native host reports
    /// what the host last read from it. An LV2 plugin in the PipeWire filter
    /// chain has no way to report, so it is zero only when its metadata
    /// declares no latency port.
    /// </summary>
    internal static double? InsertMilliseconds(bool bypass, bool running, bool hosted, double? hostReport, PluginInfo? plugin)
    {
        if (bypass) return 0;
        if (!running) return null;
        if (hosted && hostReport is double reported) return reported;
        return plugin is { Kind: "lv2", ReportsLatency: false } ? 0 : null;
    }

    /// <summary>
    /// A mix's total: the sum of its inserts' figures, with an insert that
    /// gives none counted as zero. Compensation never waits for a figure
    /// that may not come; the insert shows as unknown instead.
    /// </summary>
    internal static double Total(IEnumerable<double?> inserts) => inserts.Sum(ms => ms ?? 0);

    /// <summary>
    /// The delay each mix needs to arrive with the slowest. A total that is
    /// not a finite figure from zero to the limit cancels the whole plan, so
    /// no mix is half aligned, and the error says why.
    /// </summary>
    internal static Dictionary<string, double> Plan(IReadOnlyDictionary<string, double> totals, out string? error)
    {
        error = null;
        if (totals.Values.Any(v => !double.IsFinite(v) || v < 0))
            error = "A mix's plugin latency is not a usable figure; no mix is delayed.";
        else if (totals.Values.Any(v => v > MaxMilliseconds))
            error = "A mix's plugin latency is over two seconds; no mix is delayed.";
        if (error is not null) return totals.ToDictionary(p => p.Key, _ => 0.0);
        double slowest = totals.Count > 0 ? totals.Values.Max() : 0;
        return totals.ToDictionary(p => p.Key, p => slowest - p.Value);
    }
}

public sealed partial class Mixer
{
    private bool _compensateMixLatency;
    private string? _mixLatencyError;
    // The delay each mix should get, the stage that applies it, the link
    // into that stage, the value the stage holds now, and why a stage could
    // not be built or set.
    private readonly Dictionary<string, double> _mixDelayPlan = [];
    private readonly Dictionary<string, FilterHandle> _mixDelays = [];
    private readonly Dictionary<string, PortLink> _mixDelayInputs = [];
    private readonly Dictionary<string, double> _mixDelayValues = [];
    private readonly Dictionary<string, string> _mixDelayErrors = [];
    // Every insert's last figure, so a sweep tells clients when one changes.
    private readonly Dictionary<(string Chain, string Insert), double?> _latencyReports = [];

    /// <summary>Whether mixes are delayed to line up with the slowest mix's inserts.</summary>
    public bool CompensateMixLatency { get { lock (_gate) return _compensateMixLatency; } }

    /// <summary>
    /// Turn latency compensation on or off. Either way every insert chain is
    /// rebuilt: with it on, an LV2 plugin that declares a latency port runs
    /// in the native host, where its figure can be read, and goes back to
    /// the filter chain when it is turned off.
    /// </summary>
    public void SetMixLatencyCompensation(bool enabled)
    {
        lock (_gate)
        {
            if (_compensateMixLatency == enabled) return;
            bool previous = _compensateMixLatency;
            _compensateMixLatency = enabled;
            _pw.MeasurePluginLatency = enabled;
            if (!_built) return;
            try { WireInputFeedsLocked(); }
            catch
            {
                // The input path swaps only once the new graph is complete,
                // so the old one is still what plays.
                _compensateMixLatency = previous;
                _pw.MeasurePluginLatency = previous;
                throw;
            }
            foreach (MixDefinition mix in _config.Mixes)
            {
                _restarts.Forget(DelayKey(mix.Id));
                WireMixChainLocked(mix);
            }
            UpdateMixLatencyLocked();
        }
    }

    private static string DelayKey(string mixId) => "delay:" + mixId;

    private double? InsertLatencyLocked(string key, InsertDefinition insert)
    {
        bool running = !_insertErrors.ContainsKey(key) && _chains.TryGetValue(key, out FilterHandle? chain) && chain.IsAlive;
        NativePluginHost? host = running ? _chains[key].InsertStages.FirstOrDefault(s => s.Id == insert.Id).Stage?.NativeHost : null;
        return MixLatency.InsertMilliseconds(insert.Bypass, running, host is not null,
            host is { IsHealthy: true } ? host.LatencyMilliseconds : null,
            insert.Bypass || !running ? null : PluginCatalog.Find(insert));
    }

    private double MixLatencyLocked(MixDefinition mix)
    {
        string key = MixKey(mix);
        return MixLatency.Total(InsertsFor(key).Select(insert => InsertLatencyLocked(key, insert)));
    }

    private void RemoveMixDelayLocked(string id)
    {
        if (_mixDelayInputs.Remove(id, out PortLink? link)) _pw.Unlink(link);
        if (_mixDelays.Remove(id, out FilterHandle? filter)) _pw.StopFilter(filter);
        _mixDelayValues.Remove(id);
        _mixDelayErrors.Remove(id);
    }

    private void RemoveMixDelaysLocked()
    {
        foreach (string id in _mixDelays.Keys.ToArray()) RemoveMixDelayLocked(id);
        _mixDelayErrors.Clear();
        _mixDelayPlan.Clear();
        _mixLatencyError = null;
    }

    /// <summary>
    /// Put a delay stage after the mix's inserts when the plan gives it one.
    /// Called while the mix's consumers are being re-pointed, so they read
    /// the stage from then on. A stage that cannot be built leaves the mix
    /// undelayed and says why.
    /// </summary>
    private void WireMixDelayLocked(MixDefinition mix)
    {
        RemoveMixDelayLocked(mix.Id);
        if (!_compensateMixLatency || _mixDelayPlan.GetValueOrDefault(mix.Id) <= 0) return;
        string key = DelayKey(mix.Id);
        if (_restarts.Blocked(key))
        {
            _mixDelayErrors[mix.Id] = "The delay for this mix kept failing and is off; turn compensation off and on again to retry.";
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
            _pw.SetMixDelay(filter, _mixDelayPlan[mix.Id]);
            _mixDelays[mix.Id] = filter;
            _mixDelayInputs[mix.Id] = link;
            _mixDelayValues[mix.Id] = _mixDelayPlan[mix.Id];
        }
        catch (Exception ex)
        {
            if (link is not null) _pw.Unlink(link);
            if (filter is not null) _pw.StopFilter(filter);
            _mixDelayErrors[mix.Id] = ex.Message;
            _restarts.Failed(key, ex.Message);
        }
    }

    private bool TrackLatencyReportsLocked()
    {
        bool changed = false;
        var seen = new HashSet<(string, string)>();
        foreach ((string key, List<InsertDefinition> inserts) in _inserts)
            foreach (InsertDefinition insert in inserts)
            {
                var identity = (key, insert.Id);
                seen.Add(identity);
                double? value = InsertLatencyLocked(key, insert);
                if (_latencyReports.TryGetValue(identity, out double? old) && old == value) continue;
                _latencyReports[identity] = value;
                changed = true;
            }
        foreach (var identity in _latencyReports.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            _latencyReports.Remove(identity);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Read every insert's figure, plan the mix delays, and bring the stages
    /// in line: build one a mix newly needs, drop one it no longer needs,
    /// rebuild one that died, and set the rest to the planned value without
    /// touching the plugins. True when anything a client sees changed.
    /// </summary>
    private bool UpdateMixLatencyLocked()
    {
        bool changed = TrackLatencyReportsLocked();
        string? error = null;
        Dictionary<string, double> plan = _compensateMixLatency
            ? MixLatency.Plan(_config.Mixes.ToDictionary(m => m.Id, MixLatencyLocked), out error)
            : [];
        _mixDelayPlan.Clear();
        foreach ((string id, double delay) in plan)
            if (delay > 0) _mixDelayPlan[id] = delay;

        foreach (MixDefinition mix in _config.Mixes)
        {
            bool required = _mixDelayPlan.ContainsKey(mix.Id);
            bool exists = _mixDelays.TryGetValue(mix.Id, out FilterHandle? filter);
            if (!required)
            {
                _restarts.Forget(DelayKey(mix.Id));
                if (exists || _mixDelayErrors.ContainsKey(mix.Id)) { RewireMixConsumersLocked(mix); changed = true; }
                continue;
            }
            if (exists && filter!.IsAlive && _pw.EnsureLinks(_mixDelayInputs[mix.Id]) != LinkHealth.Broken) continue;
            if (!exists && _restarts.Blocked(DelayKey(mix.Id))) continue;
            if (exists) _restarts.Failed(DelayKey(mix.Id), "the mix delay stopped");
            RewireMixConsumersLocked(mix);
            changed = true;
        }

        foreach ((string id, FilterHandle filter) in _mixDelays)
        {
            double delay = _mixDelayPlan.GetValueOrDefault(id);
            if (_mixDelayValues.GetValueOrDefault(id) == delay) continue;
            try
            {
                _pw.SetMixDelay(filter, delay);
                _mixDelayValues[id] = delay;
                _mixDelayErrors.Remove(id);
            }
            catch (Exception ex) { _mixDelayErrors[id] = ex.Message; }
            changed = true;
        }

        error ??= _mixDelayErrors.Values.FirstOrDefault();
        changed |= _mixLatencyError != error;
        _mixLatencyError = error;
        return changed;
    }
}
