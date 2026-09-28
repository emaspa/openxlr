namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    // A channel without inserts keeps the original combine sink. A split
    // input and send bus are needed only while it owns an effect chain.
    private readonly Dictionary<string, uint> _channelInputModules = [];
    private readonly Dictionary<string, PortLink> _channelTaps = [];
    private readonly Dictionary<string, PortLink> _channelOuts = [];
    private string ChannelBus(ChannelDefinition channel)
        => _channelInputModules.ContainsKey(channel.Id) ? $"OpenXLR_bus_{channel.Id}" : channel.SinkName;
    private bool IsHardwareInsert(string key) => _config.Channels.Any(c => c.Id == key && c.InputPair is not null);

    private uint CreateChannelNodesLocked(ChannelDefinition channel, bool? split = null)
    {
        bool effects = split ?? (channel.InputPair is null && InsertsFor(channel.Id).Count > 0);
        uint? input = null;
        try
        {
            if (effects) input = _pw.CreateNullSink(channel.SinkName, $"OpenXLR {channel.Name}", visible: channel.IsApplication);
            uint combine = _pw.CreateCombineSink(effects ? $"OpenXLR_bus_{channel.Id}" : channel.SinkName,
                MixSinkPattern, $"OpenXLR {channel.Name}", visible: !effects && channel.IsApplication);
            if (input is uint module) _channelInputModules[channel.Id] = module;
            return combine;
        }
        catch
        {
            if (input is uint module) _pw.UnloadModule(module);
            throw;
        }
    }

    private void RemoveChannelChainLocked(string key)
    {
        if (_channelTaps.Remove(key, out PortLink? tap)) _pw.Unlink(tap);
        if (_channelOuts.Remove(key, out PortLink? output)) _pw.Unlink(output);
        if (_chains.Remove(key, out FilterHandle? chain)) _pw.StopFilter(chain);
    }

    private void SetChannelShapeLocked(ChannelDefinition channel, bool split)
    {
        bool before = _channelInputModules.ContainsKey(channel.Id);
        if (before == split) return;
        if (split) EnsurePulseHeadroomLocked(newStreams: _config.Mixes.Count, newNodes: 3);
        var streams = _pw.StreamSerialsOnSink(channel.SinkName).ToHashSet();
        foreach (var placed in _streams.Where(p => p.Value.ChannelId == channel.Id).ToArray())
        {
            streams.Add(placed.Value.Serial);
            _streams.Remove(placed.Key);
        }
        RemoveChannelChainLocked(channel.Id);
        RemoveCaptureFeedLocked(channel.Id);
        _meters.Remove($"ch:{channel.Id}");
        if (_combineModules.Remove(channel.Id, out uint combine)) _pw.UnloadModule(combine);
        if (_channelInputModules.Remove(channel.Id, out uint input)) _pw.UnloadModule(input);
        try { _combineModules[channel.Id] = CreateChannelNodesLocked(channel, split); }
        catch (Exception failure)
        {
            try
            {
                _combineModules[channel.Id] = CreateChannelNodesLocked(channel, before);
                RestoreChannelFeedsLocked(channel, streams);
            }
            catch (Exception rollback) { throw new AggregateException("The channel nodes could not be replaced or restored.", failure, rollback); }
            throw;
        }
        RestoreChannelFeedsLocked(channel, streams);
    }

    private void RestoreChannelFeedsLocked(ChannelDefinition channel, IEnumerable<int> streams)
    {
        bool ready = WaitForLegsLocked([_combineModules[channel.Id]], _config.Mixes.Select(m => m.SinkName));
        foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(channel.Id, mix.Id);
        foreach (int serial in streams)
        {
            try { _pw.MoveStreamToSink(serial, channel.SinkName); }
            catch (InvalidOperationException) { /* The application may have closed. */ }
        }
        _meters.Add($"ch:{channel.Id}", ChannelBus(channel));
        EnsureCaptureFeedsLocked();
        if (!ready) throw new InvalidOperationException("The channel's sends did not return after changing its effect path.");
    }

    private void WireChannelChainLocked(ChannelDefinition channel)
    {
        string key = channel.Id;
        RemoveChannelChainLocked(key);
        _insertErrors.Remove(key);
        var inserts = InsertsFor(key);
        try
        {
            SetChannelShapeLocked(channel, inserts.Count > 0);
            if (!_channelInputModules.ContainsKey(key)) return;
            bool active = inserts.Any(i => !i.Bypass && PluginCatalog.Find(i) is { } plugin && plugin.Fits(2));
            if (active && _restarts.Blocked(key)) _insertErrors[key] = RestartPolicy.GivenUp;
            else if (active)
            {
                var chain = _pw.CreateMixChain($"channel_{key}", $"OpenXLR {channel.Name} Inserts", inserts);
                _chains[key] = chain;
                _channelTaps[key] = _pw.LinkStereoNodes(channel.SinkName, "monitor", chain.SinkName, "playback");
                _channelOuts[key] = _pw.LinkStereoNodes(chain.SourceName, "capture", ChannelBus(channel), "playback");
                return;
            }
        }
        catch (Exception ex)
        {
            _insertErrors[key] = ex.Message;
            _restarts.Failed(key);
            RemoveChannelChainLocked(key);
        }
        // A failed fallback belongs to this channel, not the whole mixer.
        if (!_channelInputModules.ContainsKey(key)) return;
        try { _channelTaps[key] = _pw.LinkStereoNodes(channel.SinkName, "monitor", ChannelBus(channel), "playback"); }
        catch (Exception ex)
        {
            _insertErrors[key] = (_insertErrors.GetValueOrDefault(key) is { } prior ? prior + " " : "") + "Direct audio route failed: " + ex.Message;
            _restarts.Failed(key);
        }
    }

    private bool EnsureChannelChainsLocked()
    {
        bool changed = false;
        foreach (var channel in _config.Channels.Where(c => c.InputPair is null))
        {
            string key = channel.Id;
            bool split = _channelInputModules.ContainsKey(key);
            bool required = InsertsFor(key).Count > 0;
            if (!split && !required) continue;
            if (_restarts.Blocked(key)) continue;
            bool dead = _chains.TryGetValue(key, out var chain) && !chain.IsAlive;
            bool broken = split != required || !_channelTaps.TryGetValue(key, out var tap) || _pw.EnsureLinks(tap) == LinkHealth.Broken
                || (_channelOuts.TryGetValue(key, out var output) && _pw.EnsureLinks(output) == LinkHealth.Broken);
            // A healthy direct fallback must not suppress retries of the
            // failed processing chain. The restart budget still bounds them.
            if (!dead && !broken && !_insertErrors.ContainsKey(key)) continue;
            if (dead) _restarts.Failed(key);
            WireChannelChainLocked(channel);
            changed = true;
        }
        return changed;
    }

    private HashSet<string> ChangedSoftwareChains(IReadOnlyDictionary<string, List<InsertDefinition>> incoming, bool replace)
        => _config.Channels.Where(c => c.InputPair is null && (replace || incoming.ContainsKey(c.Id))
            && !SameInsertChain(InsertsFor(c.Id), incoming.GetValueOrDefault(c.Id) ?? [])).Select(c => c.Id).ToHashSet();

    private static bool SameInsertChain(IReadOnlyList<InsertDefinition> first, IReadOnlyList<InsertDefinition> second)
        => first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind && pair.First.Plugin == pair.Second.Plugin
            && pair.First.Label == pair.Second.Label && pair.First.Bypass == pair.Second.Bypass && pair.First.NativeHost == pair.Second.NativeHost
            && pair.First.Params.Count == pair.Second.Params.Count
            && pair.First.Params.All(p => pair.Second.Params.TryGetValue(p.Key, out double value) && value == p.Value));
}
