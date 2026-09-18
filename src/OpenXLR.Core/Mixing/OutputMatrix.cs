using System.Security.Cryptography;
using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>A non-unity gain on one selected output's mix feed. Zero disconnects a route.</summary>
public sealed record OutputRouteLevel(string Device, string Mix, double Level)
{
    public const int MaxCount = 16 * (MixerConfig.MaxVirtualMixes + 3);
    internal static bool IsValid(OutputRouteLevel? route)
        => route is { Device.Length: > 0 and <= 256, Mix.Length: > 0 and <= 36 }
            && double.IsFinite(route.Level) && route.Level is > 0 and <= 1;
}

public sealed partial class Mixer
{
    private readonly Dictionary<(string Output, string Mix), double> _outputRouteLevels = [];
    private readonly Dictionary<(string Output, string Mix), RouteGain> _routeGains = [];
    private readonly HashSet<string> _incompleteMonitorRoutes = [];
    private sealed record RouteGain(string Node, uint Module);

    /// <summary>Direct hardware monitoring bypasses software route gains.</summary>
    public bool JackRoutesAtUnity
    {
        get
        {
            lock (_gate)
                return !_monitorOutputs.Where(output => output.Contains('#')).Any(output =>
                    MixesForOutputLocked(output).Any(mix => _outputRouteLevels.ContainsKey((OutputRouteKey(output), mix.Id))));
        }
    }

    /// <summary>Shared identity for output grouping, route gains and saved route restoration.</summary>
    internal static string OutputRouteKey(string output)
    {
        int marker = output.IndexOf('#');
        return marker < 0 ? output : output[..marker] + "#bus";
    }

    private List<OutputRouteLevel> ExportOutputRoutesLocked()
        => [.. _outputRouteLevels.Select(pair => new OutputRouteLevel(pair.Key.Output, pair.Key.Mix, pair.Value))];

    private void RecallOutputRoutesLocked(IEnumerable<OutputRouteLevel>? routes)
    {
        _outputRouteLevels.Clear();
        if (routes is null) return;
        foreach (OutputRouteLevel route in routes.Take(OutputRouteLevel.MaxCount))
            if (OutputRouteLevel.IsValid(route) && route.Level < 1
                && _config.Mixes.Any(m => m.Id == route.Mix))
                _outputRouteLevels[(OutputRouteKey(route.Device), route.Mix)] = route.Level;
    }

    /// <summary>
    /// Add, adjust or disconnect one mix-to-output route. The device must
    /// already be selected. Shared hardware jacks change as one output bus.
    /// Existing gain nodes change in place, without interrupting other routes.
    /// </summary>
    public string? SetOutputRoute(string output, string mixId, double level)
    {
        lock (_gate)
        {
            if (!_built) return "mixer not built";
            if (!double.IsFinite(level) || level is < 0 or > 1) return "route level must be between 0 and 1";
            if (!_config.Mixes.Any(m => m.Id == mixId)) return "unknown mix";
            List<string> affected = [.. MonitorOutputsForLocked(output)];
            if (affected.Count == 0) return "output is not selected";
            string key = OutputRouteKey(affected[0]);
            var routeKey = (key, mixId);
            var mixes = MixesForOutputLocked(affected[0]).Select(m => m.Id).ToHashSet();
            bool active = mixes.Contains(mixId);
            if (active && level > 0 && _routeGains.TryGetValue(routeKey, out RouteGain? gain))
            {
                // Record the intent first: the next sweep repairs the node
                // from it, so a write that fails or echoes late is retried
                // rather than reverted.
                if (level == 1) _outputRouteLevels.Remove(routeKey);
                else _outputRouteLevels[routeKey] = level;
                _pw.SetSinkVolume(gain.Node, level);
                return null;
            }
            if ((active && level == 1 && !_outputRouteLevels.ContainsKey(routeKey)) || (!active && level == 0)) return null;

            var previousFeeds = new Dictionary<string, string>(_monitorFeeds);
            var previousLevels = new Dictionary<(string Output, string Mix), double>(_outputRouteLevels);
            if (level > 0) mixes.Add(mixId); else mixes.Remove(mixId);
            string feed = MonitorFeed.Join(_config.Mixes.Where(m => mixes.Contains(m.Id)).Select(m => m.Id));
            foreach (string device in affected) _monitorFeeds[device] = feed;
            if (level is > 0 and < 1) _outputRouteLevels[routeKey] = level;
            else _outputRouteLevels.Remove(routeKey);
            try
            {
                RewireOutputLocked(key, affected[0]);
                PruneOutputRoutesLocked();
                RefreshHardwareMicCellsLocked();
            }
            catch
            {
                _monitorFeeds.Clear();
                foreach (var pair in previousFeeds) _monitorFeeds[pair.Key] = pair.Value;
                _outputRouteLevels.Clear();
                foreach (var pair in previousLevels) _outputRouteLevels[pair.Key] = pair.Value;
                // The normal reconciliation pass repairs the restored intent
                // if a device vanished while its route was being changed.
                try { RewireOutputLocked(key, affected[0]); PruneOutputRoutesLocked(); }
                catch (InvalidOperationException) { }
                throw;
            }
            return null;
        }
    }

    private void RewireOutputLocked(string key, string target)
    {
        if (_monitorRoutes.Remove(key, out PortLink? previous)) _pw.Unlink(previous);
        _monitorRoutes[key] = RouteFeedLocked(target) ?? new PortLink([]);
    }

    private void PruneOutputRoutesLocked()
    {
        var active = MonitorRouteTargetsLocked().SelectMany(output =>
            MixesForOutputLocked(output.Target).Select(mix => (output.Key, mix.Id))).ToHashSet();
        foreach (var key in _outputRouteLevels.Keys.Where(key => !active.Contains(key)).ToArray())
            _outputRouteLevels.Remove(key);
        foreach (var key in _routeGains.Keys.Where(key => !active.Contains(key)).ToArray())
        {
            RouteGain gain = _routeGains[key];
            if (_pw.FindNodeId(gain.Node) is null) _pw.ForgetModule(gain.Module);
            else _pw.UnloadModule(gain.Module);
            _routeGains.Remove(key);
        }
    }

    private PortLink RouteMixToOutputLocked(MixDefinition mix, string target)
    {
        (string tap, string prefix) = MixTapLocked(mix);
        var key = (OutputRouteKey(target), mix.Id);
        double level = _outputRouteLevels.GetValueOrDefault(key, 1);
        _routeGains.TryGetValue(key, out RouteGain? gain);
        if (gain is not null && _pw.FindNodeId(gain.Node) is null)
        {
            _pw.ForgetModule(gain.Module);
            _routeGains.Remove(key);
            gain = null;
        }
        if (gain is null)
        {
            if (level == 1) return _pw.RouteTapToOutput(tap, prefix, target);
            EnsurePulseHeadroomLocked(newStreams: 0, newNodes: 1);
            // Device names are data, never part of a module argument. The hash
            // gives a stable, bounded node name without ambiguous delimiters.
            string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Item1)))[..24].ToLowerInvariant();
            string node = $"OpenXLR_route_{suffix}_{mix.Id}";
            uint module = _pw.CreateNullSink(node, $"OpenXLR output route {mix.Name}", visible: false);
            gain = new RouteGain(node, module);
            _routeGains[key] = gain;
            try
            {
                _pw.SetSinkMuted(node, true);
                _pw.SetSinkVolume(node, level);
            }
            catch
            {
                try { _pw.UnloadModule(module); _routeGains.Remove(key); }
                catch (InvalidOperationException) { /* retain ownership for reconciliation or teardown */ }
                throw;
            }
        }
        else _pw.SetSinkVolume(gain.Node, level);

        PortLink onward = _pw.RouteTapToOutput(gain.Node, "monitor", target);
        if (onward.Pairs.Count == 0) return onward;
        PortLink into = new([]);
        try
        {
            into = _pw.LinkNodes(tap, prefix, gain.Node, "playback");
            if (into.Pairs.Count == 0) { _pw.Unlink(onward); return into; }
            _pw.SetSinkMuted(gain.Node, false);
        }
        catch { _pw.Unlink(into); _pw.Unlink(onward); throw; }
        return new PortLink([.. into.Pairs, .. onward.Pairs]);
    }
}
