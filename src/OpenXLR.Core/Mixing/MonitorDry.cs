using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A monitor mix that hears the microphones dry: its sends take each
/// microphone before its software effects, so the owner hears their own
/// voice early, while every other mix, other monitor mixes included, keeps
/// the processed voice.
///
///   source -> [OpenXLR_eq_&lt;id&gt;] -> [chain] -> OpenXLR_ch_&lt;id&gt; -> every mix
///   source -> OpenXLR_ch_&lt;id&gt;.dry -> the monitor mixes only
///
/// The dry route is a hidden combine of its own, linked from the same
/// capture port the channel's route starts at and beside it, so it touches
/// no stage, chain or plugin host. Its pattern names every monitor mix by
/// its exact node name, so it never grows a send into a mix that is
/// recorded or streamed; a microphone has the route while any monitor mix
/// is dry, and its legs into the other monitor mixes stay closed. While it
/// carries a dry monitor mix, the channel's own
/// send into that mix (and its duck bus leg) is held closed and the dry send
/// takes the send level, the channel volume and the mutes instead; the mix
/// master is the monitor sink's own volume, so it applies to both alike.
///
/// The dry send carries a mix only while the route is ready (every monitor
/// mix has its leg) and fed. Until then, and while Sound Check runs on the
/// microphone, the processed send plays as before, so the microphone is
/// never missing from the monitor. A switch closes the send it leaves
/// before it opens the other, so the voice is never heard twice.
/// </summary>
public sealed partial class Mixer
{
    private sealed class DryRoute(uint module, string pattern)
    {
        public uint Module { get; } = module;
        /// <summary>The monitor mixes the combine was loaded for, as its slaves pattern.</summary>
        public string Pattern { get; } = pattern;
        /// <summary>Every monitor mix had its leg and each leg was closed, so the route may carry.</summary>
        public bool Ready { get; set; }
        public PortLink? Feed { get; set; }
        public string? Device { get; set; }
    }

    // The monitor mixes that hear the microphones dry (saved, and in
    // profiles), the set the cells were last pushed for, the running routes
    // by microphone, and each dry leg's sink-input index by cell.
    private readonly HashSet<string> _dryMonitors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dryApplied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DryRoute> _dryRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dryLegIndex = new(StringComparer.Ordinal);
    // When a route last failed to load, so a sweep does not try it every second.
    private readonly Dictionary<string, long> _dryFailedAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan DryRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DryLegWait = TimeSpan.FromSeconds(1);

    /// <summary>Whether a monitor mix hears the microphones dry.</summary>
    public bool MonitorDry(string mix) { lock (_gate) return _dryMonitors.Contains(mix); }

    /// <summary>
    /// Have a monitor mix hear every microphone dry, or through its effects
    /// again. Only the microphones' sends into that mix move; every other
    /// mix, stage and plugin host stays as it is.
    /// </summary>
    public void SetMonitorDry(string mix, bool on)
    {
        lock (_gate)
        {
            if (MonitorMixLocked(mix) is null) throw new ArgumentException($"'{mix}' is not a monitor mix", nameof(mix));
            if (on ? !_dryMonitors.Add(mix) : !_dryMonitors.Remove(mix)) return;
            _dryFailedAt.Clear();
            SyncDryRoutesLocked();
        }
    }

    /// <summary>The monitor mixes that are dry now, in layout order, for the hardware plan.</summary>
    private HashSet<string> DryMonitorsLocked()
        => _config.Mixes.Where(m => m.Kind == MixKind.Monitor && _dryMonitors.Contains(m.Id)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The switches as the settings file keeps them: the monitor mixes that are on.</summary>
    private Dictionary<string, bool> ExportMonitorDryLocked()
        => _dryMonitors.Order(StringComparer.Ordinal).ToDictionary(m => m, _ => true, StringComparer.Ordinal);

    /// <summary>Every monitor mix's switch, for a profile: a recall then names every monitor mix.</summary>
    private Dictionary<string, bool> SceneMonitorDryLocked()
        => _config.Mixes.Where(m => m.Kind == MixKind.Monitor).ToDictionary(m => m.Id, m => _dryMonitors.Contains(m.Id), StringComparer.Ordinal);

    /// <summary>Take the recalled switches; an id that is not a monitor mix of this layout is ignored.</summary>
    private void RecallMonitorDryLocked(IReadOnlyDictionary<string, bool>? recalled)
    {
        HashSet<string> next = RecallMonitorDry(_dryMonitors, recalled);
        next.RemoveWhere(m => MonitorMixLocked(m) is null);
        _dryMonitors.Clear();
        _dryMonitors.UnionWith(next);
    }

    /// <summary>
    /// The switches after a recall: each mix the recalled data names takes
    /// its value, every other keeps what it has, so a recall without the
    /// field (null) changes nothing.
    /// </summary>
    internal static HashSet<string> RecallMonitorDry(IReadOnlySet<string> current, IReadOnlyDictionary<string, bool>? recalled)
    {
        var next = new HashSet<string>(current, StringComparer.Ordinal);
        foreach ((string mix, bool on) in recalled ?? new Dictionary<string, bool>())
            if (on) next.Add(mix);
            else next.Remove(mix);
        return next;
    }

    /// <summary>
    /// Whether a microphone should have its dry route now: a monitor mix is
    /// dry, the channel is a microphone and is present, and Sound Check is not
    /// playing its take through it, which the monitor must hear processed.
    /// </summary>
    private bool DryWantedLocked(ChannelDefinition channel)
        => _built && _dryMonitors.Any(m => MonitorMixLocked(m) is not null) && CarriesMicrophoneStage(channel) && ChannelPresentLocked(channel)
            && !(_soundCheck is not null && _soundCheckChannel == channel.Id);

    /// <summary>Whether the dry route carries this channel's send into this mix, and the channel's own send is held closed for it.</summary>
    private bool DryCarriesLocked(string channel, string mix)
        => _dryMonitors.Contains(mix) && _dryRoutes.TryGetValue(channel, out DryRoute? route) && route.Ready && route.Feed is not null
            && _dryLegIndex.ContainsKey(Cell(channel, mix)) && _cells.Contains(Cell(channel, mix))
            && MonitorMixLocked(mix) is not null;

    /// <summary>
    /// Whether a microphone is heard dry wherever a monitor feed plays: every
    /// mix of the feed is a dry monitor mix, so its raw input may stand in
    /// for the processed send there. A null feed means every monitor mix.
    /// </summary>
    private bool DryHeardInLocked(string channel, string? feed)
    {
        if (!CarriesMicrophoneStageLocked(channel)) return false;
        IReadOnlyList<string> parts = feed is null
            ? [.. _config.Mixes.Where(m => m.Kind == MixKind.Monitor).Select(m => m.Id)]
            : MonitorFeed.Parts(feed);
        return parts.Count > 0 && parts.All(m => MonitorMixLocked(m) is not null && _dryMonitors.Contains(m));
    }

    /// <summary>
    /// What a dry leg should carry: the send as the channel's own leg would
    /// carry it, closed while the microphones are paused with the monitoring,
    /// as their channel sinks are; closed while the route does not carry.
    /// </summary>
    private (double Level, bool Muted) DryLegTargetLocked(string channel, string mix)
    {
        if (!DryCarriesLocked(channel, mix)) return (0, true);
        (double level, bool muted) = CellTargetLocked(channel, mix, out _);
        return (level, muted || MicrophonesPausedLocked);
    }

    /// <summary>
    /// The combine's slaves pattern: an anchored regular expression naming
    /// each monitor mix's sink exactly, so no recorded mix and no duck bus
    /// ever gets a leg from the dry route.
    /// </summary>
    internal static string DryPattern(IEnumerable<MixDefinition> mixes)
    {
        var pattern = new StringBuilder("~^(");
        bool first = true;
        foreach (MixDefinition mix in mixes.Where(m => m.Kind == MixKind.Monitor))
        {
            if (!first) pattern.Append('|');
            first = false;
            foreach (char c in mix.SinkName)
                pattern.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c.ToString() : $"[{c}]");
        }
        return pattern.Append(")$").ToString();
    }

    /// <summary>
    /// Bring every dry route in line with the switches, the layout and the
    /// sources: start the routes that are wanted, finish one whose legs were
    /// late, follow each microphone's source, and remove the routes no longer
    /// wanted or loaded for another set of monitor mixes. A route that stays
    /// while a monitor mix's switch moved has its sends into that mix pushed
    /// again. True when the graph changed.
    /// </summary>
    private bool SyncDryRoutesLocked()
    {
        if (!_built) return false;
        bool changed = SyncDryRoutesCoreLocked();
        HashSet<string> moved = [.. _dryMonitors.Except(_dryApplied).Concat(_dryApplied.Except(_dryMonitors))];
        _dryApplied.Clear();
        _dryApplied.UnionWith(_dryMonitors);
        foreach (string channel in _dryRoutes.Keys.ToList())
            foreach (string mix in moved)
                if (MonitorMixLocked(mix) is not null && _cells.Contains(Cell(channel, mix))) ApplyCellLocked(channel, mix);
        return changed;
    }

    private bool SyncDryRoutesCoreLocked()
    {
        bool changed = false;
        string pattern = DryPattern(_config.Mixes);
        foreach ((string id, DryRoute route) in _dryRoutes.ToList())
        {
            ChannelDefinition? channel = _config.Channels.FirstOrDefault(c => c.Id == id);
            if (channel is null || !DryWantedLocked(channel) || route.Pattern != pattern)
            {
                RemoveDryRouteLocked(id);
                changed = true;
            }
            else if (_pw.SinkExists(channel.DrySinkName) == false)
            {
                // Removed outside the daemon: its module id may be someone
                // else's by now, so it is only forgotten.
                _pw.ForgetModule(route.Module);
                ForgetDryRouteLocked(id);
                changed = true;
            }
        }
        foreach (ChannelDefinition channel in _config.Channels.Where(DryWantedLocked).ToList())
        {
            try
            {
                if (!_dryRoutes.TryGetValue(channel.Id, out DryRoute? route))
                {
                    if (_dryFailedAt.TryGetValue(channel.Id, out long failed) && Clock() - failed < (long)DryRetry.TotalMilliseconds) continue;
                    StartDryRouteLocked(channel, pattern);
                    changed = true;
                }
                else if (!route.Ready) changed |= PrepareDryRouteLocked(channel, route, DryLegWait);
                else changed |= LinkDryFeedLocked(channel, route);
                _dryFailedAt.Remove(channel.Id);
            }
            catch (InvalidOperationException ex)
            {
                if (!_dryFailedAt.ContainsKey(channel.Id))
                    _pw.Note($"The dry monitor route of {channel.Id} could not be set up, so the monitor hears it through its effects: {ex.Message}");
                _dryFailedAt[channel.Id] = Clock();
                if (_dryRoutes.TryGetValue(channel.Id, out DryRoute? half) && !half.Ready) RemoveDryRouteLocked(channel.Id);
            }
        }
        return changed;
    }

    /// <summary>Load a microphone's dry combine and, once its legs are up, close them and feed it.</summary>
    private void StartDryRouteLocked(ChannelDefinition channel, string pattern)
    {
        int monitors = _config.Mixes.Count(m => m.Kind == MixKind.Monitor);
        EnsurePulseHeadroomLocked(newStreams: monitors, newNodes: 1);
        uint module = _pw.CreateCombineSink(channel.DrySinkName, pattern, $"OpenXLR {channel.Name} dry", visible: false);
        var route = new DryRoute(module, pattern);
        _dryRoutes[channel.Id] = route;
        PrepareDryRouteLocked(channel, route, DryLegWait);
    }

    /// <summary>
    /// Make a route ready once every monitor mix has its leg: each leg is
    /// closed before the feed is linked, so a leg the session manager opened
    /// at a remembered level never plays. A route whose legs are still late
    /// is left for the next sweep. True when the route became ready.
    /// </summary>
    private bool PrepareDryRouteLocked(ChannelDefinition channel, DryRoute route, TimeSpan wait)
    {
        string[] wanted = [.. _config.Mixes.Where(m => m.Kind == MixKind.Monitor).Select(m => m.SinkName)];
        long deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        IReadOnlyDictionary<string, int>? legs;
        while (true)
        {
            legs = _pw.TryFindCombineLegs(route.Module);
            if (legs is not null && wanted.All(legs.ContainsKey)) break;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(50);
        }
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
        {
            int index = legs[mix.SinkName];
            _dryLegIndex[Cell(channel.Id, mix.Id)] = index;
            _pw.SetSinkInputVolume(index, 0);
            _pw.SetSinkInputMuted(index, true);
        }
        route.Ready = true;
        LinkDryFeedLocked(channel, route);
        return true;
    }

    /// <summary>
    /// Feed a ready route from the microphone's source as it is now, the
    /// node the channel's own route starts at. A healthy link to the same
    /// source is kept. When the route starts or stops carrying, the
    /// channel's monitor sends move between it and the processed path. True
    /// when the feed changed.
    /// </summary>
    private bool LinkDryFeedLocked(ChannelDefinition channel, DryRoute route)
    {
        string? device = MicrophoneSourceLocked(channel);
        if (route.Feed is not null && route.Device == device && _pw.EnsureLinks(route.Feed) != LinkHealth.Broken) return false;
        bool carried = route.Feed is not null;
        if (route.Feed is not null) _pw.Unlink(route.Feed);
        route.Feed = null;
        route.Device = device;
        if (device is not null)
        {
            PortLink feed = RouteInputLocked(device, channel.DrySinkName, channel);
            if (feed.Pairs.Count > 0) route.Feed = feed;
        }
        if (carried != route.Feed is not null) ReapplyDryCellsLocked(channel.Id);
        return carried || route.Feed is not null;
    }

    /// <summary>
    /// Take a route down: its feed first, so it falls silent, then the
    /// channel's own monitor sends open again, then its combine goes.
    /// </summary>
    private void RemoveDryRouteLocked(string id)
    {
        if (!_dryRoutes.TryGetValue(id, out DryRoute? route)) return;
        if (route.Feed is not null) _pw.Unlink(route.Feed);
        route.Feed = null;
        ForgetDryRouteLocked(id);
        try { _pw.UnloadModule(route.Module); }
        catch (InvalidOperationException ex) { _pw.Note($"The dry monitor route of {id} could not be unloaded: {ex.Message}"); }
    }

    /// <summary>Drop a route's bookkeeping and give the channel's monitor sends back to its processed path.</summary>
    private void ForgetDryRouteLocked(string id)
    {
        _dryRoutes.Remove(id);
        foreach (string cell in _dryLegIndex.Keys.Where(c => c.StartsWith(id + "|", StringComparison.Ordinal)).ToList())
            _dryLegIndex.Remove(cell);
        ReapplyDryCellsLocked(id);
    }

    /// <summary>Push a channel's monitor sends again, after its dry route started or stopped carrying.</summary>
    private void ReapplyDryCellsLocked(string channel)
    {
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
            if (_cells.Contains(Cell(channel, mix.Id))) ApplyCellLocked(channel, mix.Id);
    }

    /// <summary>Map the dry combines' streams to their cells (part of <see cref="DiscoverLegsLocked"/>).</summary>
    private void DiscoverDryLegsLocked()
    {
        _dryLegIndex.Clear();
        foreach ((string channel, DryRoute route) in _dryRoutes)
        {
            IReadOnlyDictionary<string, int> legs = _pw.FindCombineLegs(route.Module);
            foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
                if (legs.TryGetValue(mix.SinkName, out int index)) _dryLegIndex[Cell(channel, mix.Id)] = index;
        }
    }

    /// <summary>The routes go with the graph; the adapter unloads their combines with the rest.</summary>
    private void DropDryRoutesLocked()
    {
        foreach (DryRoute route in _dryRoutes.Values)
            if (route.Feed is not null) _pw.Unlink(route.Feed);
        _dryRoutes.Clear();
        _dryLegIndex.Clear();
        _dryFailedAt.Clear();
    }

    /// <summary>The microphones whose dry route carries the monitor mixes now, for tests and diagnostics.</summary>
    internal IReadOnlyCollection<string> DryCarrying
    {
        get
        {
            lock (_gate)
                return [.. _dryRoutes.Where(r => r.Value.Ready && r.Value.Feed is not null).Select(r => r.Key)];
        }
    }
}
