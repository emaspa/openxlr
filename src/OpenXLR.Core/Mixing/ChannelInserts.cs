namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    // Public sinks remain stable while effects change. Their monitor feeds a
    // hidden combine bus, through the stereo chain when one is enabled.
    private readonly Dictionary<string, uint> _channelInputModules = [];
    private readonly Dictionary<string, PortLink> _channelTaps = [];
    private readonly Dictionary<string, PortLink> _channelOuts = [];
    private static string ChannelBus(ChannelDefinition channel)
        => channel.InputPair is null ? $"OpenXLR_bus_{channel.Id}" : channel.SinkName;
    private bool IsHardwareInsert(string key) => _config.Channels.Any(c => c.Id == key && c.InputPair is not null);

    private uint CreateChannelNodesLocked(ChannelDefinition channel)
    {
        uint? input = null;
        try
        {
            if (channel.InputPair is null)
                input = _pw.CreateNullSink(channel.SinkName, $"OpenXLR {channel.Name}", visible: channel.IsApplication);
            uint combine = _pw.CreateCombineSink(ChannelBus(channel), MixSinkPattern, $"OpenXLR {channel.Name} sends", visible: false);
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

    private void WireChannelChainLocked(ChannelDefinition channel)
    {
        string key = channel.Id;
        RemoveChannelChainLocked(key);
        _insertErrors.Remove(key);
        var inserts = InsertsFor(key);
        bool active = inserts.Any(i => !i.Bypass && PluginCatalog.Find(i) is { } plugin && plugin.Fits(2));
        if (active && _restarts.Blocked(key)) _insertErrors[key] = RestartPolicy.GivenUp;
        else if (active)
        {
            try
            {
                var chain = _pw.CreateMixChain($"channel_{key}", $"OpenXLR {channel.Name} Inserts", inserts);
                _chains[key] = chain;
                _channelTaps[key] = _pw.LinkStereoNodes(channel.SinkName, "monitor", chain.SinkName, "playback");
                _channelOuts[key] = _pw.LinkStereoNodes(chain.SourceName, "capture", ChannelBus(channel), "playback");
                return;
            }
            catch (Exception ex)
            {
                _insertErrors[key] = ex.Message;
                RemoveChannelChainLocked(key);
            }
        }
        // Failed or bypassed effects leave the channel audible. Keep this link
        // tracked too, so a session manager removing it cannot silence the bus.
        _channelTaps[key] = _pw.LinkStereoNodes(channel.SinkName, "monitor", ChannelBus(channel), "playback");
    }

    private bool EnsureChannelChainsLocked()
    {
        bool changed = false;
        foreach (var channel in _config.Channels.Where(c => c.InputPair is null))
        {
            string key = channel.Id;
            bool dead = _chains.TryGetValue(key, out var chain) && !chain.IsAlive;
            bool broken = !_channelTaps.TryGetValue(key, out var tap) || _pw.EnsureLinks(tap) == LinkHealth.Broken
                || (_channelOuts.TryGetValue(key, out var output) && _pw.EnsureLinks(output) == LinkHealth.Broken);
            if (!dead && !broken) continue;
            if (dead) _restarts.Failed(key);
            WireChannelChainLocked(channel);
            changed = true;
        }
        return changed;
    }
}
