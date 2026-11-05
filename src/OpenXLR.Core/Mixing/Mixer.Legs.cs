namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    // Sweeps in a row in which a channel's known combine leg was missing
    // from the registry, and when each channel's sink was reloaded for it.
    private readonly Dictionary<string, int> _legMissing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> _legReloads = new(StringComparer.Ordinal);

    /// <summary>How many sweeps a leg stays missing before its channel is looked at.</summary>
    internal int LegMissingSweeps { get; init; } = 5;

    /// <summary>Reloads of one channel's sink for lost legs within <see cref="LegReloadWindow"/>.</summary>
    internal const int LegReloadLimit = 3;

    /// <summary>The window <see cref="LegReloadLimit"/> counts in.</summary>
    internal static readonly TimeSpan LegReloadWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bring back the sends of a channel whose combine lost a leg, the
    /// stream that carries the channel into one mix. A combine makes a leg
    /// when a mix sink appears and never again for a sink that stays, and
    /// the legs do not reconnect, so a leg that ended or that nothing links
    /// leaves that send silent for good. A restarted WirePlumber links the
    /// whole graph again, and on a private PipeWire 1.6 with WirePlumber 0.5
    /// about every other restart logged one failed link and left that leg
    /// unlinked; neither a later rescan nor a new node linked it.
    ///
    /// <paramref name="missing"/> are the channels with a known leg the
    /// registry did not show in this sweep or showed without a link, and
    /// channels with a send waiting for a leg it never found. After
    /// <see cref="LegMissingSweeps"/> sweeps in a row one pactl call reads
    /// the legs again, since a leg can also be back under another index. A
    /// channel still short of a linked leg has its sink reloaded, as a
    /// rename does, which makes every leg again and puts its streams back; at
    /// most <see cref="LegReloadLimit"/> times in <see cref="LegReloadWindow"/>.
    /// A send whose leg is still missing waits with the other pending sends.
    /// </summary>
    /// <returns>The channels whose sink was reloaded.</returns>
    private List<string> RestoreLostLegsLocked(HashSet<string> missing)
    {
        foreach (string channel in _legMissing.Keys.Where(c => !missing.Contains(c)).ToList()) _legMissing.Remove(channel);
        foreach (string channel in missing) _legMissing[channel] = _legMissing.GetValueOrDefault(channel) + 1;
        List<string> due = [.. _legMissing.Where(e => e.Value >= LegMissingSweeps).Select(e => e.Key)];
        if (due.Count == 0) return [];
        DiscoverLegsLocked();
        (IReadOnlyDictionary<int, (int Percent, bool Muted)> legs, IReadOnlySet<int> unlinked) = _pw.CombineLegs();
        var reloaded = new List<string>();
        long now = Environment.TickCount64;
        foreach (string id in due)
        {
            _legMissing.Remove(id);
            if (_config.Channels.FirstOrDefault(c => c.Id == id) is not { } channel || !_combineModules.ContainsKey(id)) continue;
            string[] mixes = [.. _config.Mixes.Select(m => m.Id).Where(mix => _cells.Contains(Cell(id, mix)))];
            List<long> recent = _legReloads.TryGetValue(id, out List<long>? times) ? times : _legReloads[id] = [];
            recent.RemoveAll(at => now - at >= (long)LegReloadWindow.TotalMilliseconds);
            if (mixes.All(mix => _legIndex.TryGetValue(Cell(id, mix), out int index) && legs.ContainsKey(index) && !unlinked.Contains(index))
                || recent.Count >= LegReloadLimit)
            {
                // Back and linked, maybe under another index, or reloaded
                // often enough: the faders go onto the legs there are, and a
                // missing one waits.
                foreach (string mix in mixes) ApplyCellLocked(id, mix);
                continue;
            }
            recent.Add(now);
            // As a rename does it: the ducking buses step aside while the
            // sink is away. The sweep links a capture strip's feed and chain
            // into the new sink, as after a replug.
            SuspendDuckBusesLocked();
            try { ReloadChannelSinkLocked(channel, channel.Name); }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or IOException)
            {
                // A send still missing is pending now and tried again with the others.
            }
            finally { SyncDuckBusesLocked(); }
            SyncDuckingLocked();
            reloaded.Add(id);
        }
        return reloaded;
    }
}
