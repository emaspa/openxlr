namespace OpenXLR.Core.Mixing;

/// <summary>
/// Linked sends: a channel whose sends all play at one level. While a
/// channel is linked, a level written to any of its sends is written to
/// every send it has, in one hold of the mixer lock, each through the same
/// cell write a single send takes, so the audio server and the Wave XLR
/// Pro's hardware mixing see every mix change. Every mix counts, the Aux mix
/// and a hidden mix included; the mutes stay per mix, so a send that is
/// muted stays silent at the linked level. Linking takes the level of the
/// first mix in the layout's order that the channel sends to, and
/// unlinking changes no level. A mix added while a channel is linked starts
/// that channel's send at the linked level.
/// </summary>
public sealed partial class Mixer
{
    // channel ids whose sends are linked; an absent channel is not
    private readonly HashSet<string> _linkedSends = new(StringComparer.Ordinal);

    /// <summary>Whether a channel's sends are linked.</summary>
    public bool SendsLinked(string channelId)
    {
        lock (_gate) return _linkedSends.Contains(channelId);
    }

    /// <summary>
    /// Link or unlink a channel's sends. Linking sets every send to the
    /// level of the first mix the channel sends to; unlinking leaves every
    /// level where it is. An unknown channel throws.
    /// </summary>
    public void SetLinkedSends(string channelId, bool linked)
    {
        lock (_gate)
        {
            if (!HasChannelVolumeLocked(channelId)) throw new ArgumentException($"unknown channel '{channelId}'", nameof(channelId));
            if (!linked)
            {
                _linkedSends.Remove(channelId);
                return;
            }
            _linkedSends.Add(channelId);
            if (LinkedLevelLocked(channelId) is double level) WriteLinkedLevelLocked(channelId, level);
        }
    }

    /// <summary>
    /// The level every send of a linked channel holds: the first mix's in
    /// the layout's order that the channel sends to. Null when the channel
    /// is not linked or sends to no mix.
    /// </summary>
    private double? LinkedLevelLocked(string channelId)
    {
        if (!_linkedSends.Contains(channelId)) return null;
        foreach (MixDefinition mix in _config.Mixes)
        {
            string cell = Cell(channelId, mix.Id);
            if (_cells.Contains(cell)) return _levels.GetValueOrDefault(cell, 0.0);
        }
        return null;
    }

    /// <summary>Write one level to every send of a channel, each send that moves through its cell write.</summary>
    private void WriteLinkedLevelLocked(string channelId, double level)
    {
        level = Math.Clamp(level, 0.0, 1.0);
        foreach (MixDefinition mix in _config.Mixes)
        {
            string cell = Cell(channelId, mix.Id);
            if (!_cells.Contains(cell)) continue;
            bool moved = _levels.GetValueOrDefault(cell, 0.0) != level;
            _levels[cell] = level;
            if (moved && _built) ApplyCellLocked(channelId, mix.Id);
        }
    }

    /// <summary>
    /// Store one level for every send of a linked channel without writing
    /// any of them; the caller pushes the cells it reports.
    /// </summary>
    private void StoreLinkedLevelLocked(string channelId, double level, List<string> cells)
    {
        level = Math.Clamp(level, 0.0, 1.0);
        foreach (MixDefinition mix in _config.Mixes)
        {
            string cell = Cell(channelId, mix.Id);
            if (!_cells.Contains(cell)) continue;
            _levels[cell] = level;
            cells.Add(cell);
        }
    }

    /// <summary>The linked channels of the layout, sorted, for the settings file.</summary>
    private List<string> ExportLinkedSendsLocked()
        => [.. _config.Channels.Where(c => _linkedSends.Contains(c.Id)).Select(c => c.Id).Order(StringComparer.Ordinal)];

    /// <summary>Every channel's link, for a profile.</summary>
    private Dictionary<string, bool> SceneLinkedSendsLocked()
        => _config.Channels.ToDictionary(c => c.Id, c => _linkedSends.Contains(c.Id), StringComparer.Ordinal);

    /// <summary>
    /// Take the links a settings file carries: exactly the channels of the
    /// layout it lists. A name that is not a channel of the layout is skipped.
    /// </summary>
    private void RestoreLinkedSendsLocked(IEnumerable<string>? saved)
    {
        _linkedSends.Clear();
        var listed = new HashSet<string>(saved ?? [], StringComparer.Ordinal);
        foreach (ChannelDefinition channel in _config.Channels)
            if (listed.Contains(channel.Id)) _linkedSends.Add(channel.Id);
    }

    /// <summary>
    /// Take the links a profile carries: each channel it names is linked or
    /// unlinked as it says, and a channel it does not name keeps its link.
    /// Null belongs to a profile saved before linked sends, which leaves
    /// every link alone.
    /// </summary>
    private void RecallLinkedSendsLocked(IReadOnlyDictionary<string, bool>? recalled)
    {
        if (recalled is null) return;
        foreach (ChannelDefinition channel in _config.Channels)
        {
            if (!recalled.TryGetValue(channel.Id, out bool linked)) continue;
            if (linked) _linkedSends.Add(channel.Id); else _linkedSends.Remove(channel.Id);
        }
    }

    /// <summary>
    /// Bring every linked channel's sends to its linked level after levels
    /// came from a file or a profile that may hold them apart, such as a
    /// profile saved before the channel was linked. The caller pushes the
    /// sends.
    /// </summary>
    private void AlignLinkedSendsLocked()
    {
        foreach (ChannelDefinition channel in _config.Channels)
            if (LinkedLevelLocked(channel.Id) is double level)
                StoreLinkedLevelLocked(channel.Id, level, []);
    }
}
