namespace OpenXLR.Core.Mixing;

/// <summary>
/// A named set of channels of which at most one is heard. A member is open
/// while any of its sends is unmuted; opening one mutes every send of the
/// others, in every mix. Levels are never touched.
/// </summary>
public sealed record ExclusiveGroupDefinition(string Id, string Name, IReadOnlyList<string> Channels);

public static class ExclusiveGroupsModel
{
    public const int MaxGroups = 16;
    public const int MaxMembers = 36;

    public static bool ValidId(string? id) => id is { Length: > 0 and <= 36 }
        && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>Null when the group is well formed, else the reason it is not.</summary>
    public static string? Validate(ExclusiveGroupDefinition? group)
    {
        if (group is null || !ValidId(group.Id)) return "invalid group id";
        if (group.Name?.Trim() is not { Length: > 0 and <= 60 } || group.Name.Any(char.IsControl))
            return "group name must contain 1 to 60 printable characters";
        if (group.Channels is not { Count: >= 2 and <= MaxMembers }
            || group.Channels.Any(id => !ValidId(id))
            || group.Channels.Distinct(StringComparer.Ordinal).Count() != group.Channels.Count)
            return $"a group needs 2 to {MaxMembers} distinct channel ids";
        return null;
    }

    public static List<ExclusiveGroupDefinition> Copy(IEnumerable<ExclusiveGroupDefinition> groups)
        => [.. groups.Select(g => g with { Channels = g.Channels.ToArray() })];

    /// <summary>
    /// The saved groups that still make sense for these channels: malformed
    /// entries and repeated ids are dropped, members that no longer exist are
    /// pruned, a group left with fewer than two members goes, and a channel
    /// already in an earlier group keeps that one.
    /// </summary>
    public static IReadOnlyList<ExclusiveGroupDefinition> Restore(
        IEnumerable<ExclusiveGroupDefinition>? saved, IReadOnlyList<ChannelDefinition> channels)
    {
        var result = new List<ExclusiveGroupDefinition>();
        var known = channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExclusiveGroupDefinition group in saved ?? [])
        {
            if (result.Count == MaxGroups) break;
            if (Validate(group) is not null || ids.Contains(group.Id)) continue;
            string[] members = [.. group.Channels.Where(known.Contains)];
            if (members.Length < 2 || members.Any(used.Contains)) continue;
            result.Add(group with { Name = group.Name.Trim(), Channels = members });
            ids.Add(group.Id);
            used.UnionWith(members);
        }
        return result;
    }
}

public sealed partial class Mixer
{
    public bool IsChannelGrouped(string channel)
    {
        lock (_gate) return GroupForChannelLocked(channel) is not null;
    }

    private ExclusiveGroupDefinition? GroupForChannelLocked(string channel)
    {
        foreach (ExclusiveGroupDefinition group in _config.ExclusiveGroups)
            if (group.Channels.Contains(channel)) return group;
        return null;
    }

    /// <summary>
    /// Create a group (<paramref name="id"/> null) or replace a known group's
    /// name and members. Saved before it returns; a failed save leaves the
    /// groups and the mutes as they were.
    /// </summary>
    public void SetExclusiveGroup(string? id, string name, IReadOnlyList<string> channels,
        Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (id is not null && !_config.ExclusiveGroups.Any(g => g.Id == id))
                throw new InvalidOperationException($"'{id}' is not an exclusive group");
            if (id is null && _config.ExclusiveGroups.Count >= ExclusiveGroupsModel.MaxGroups)
                throw new InvalidOperationException($"{ExclusiveGroupsModel.MaxGroups} exclusive groups already");
            var group = new ExclusiveGroupDefinition(
                id ?? MixerConfig.NewId(name ?? "", "group", _config.ExclusiveGroups.Select(g => g.Id)),
                name ?? "", channels ?? []);
            if (ExclusiveGroupsModel.Validate(group) is { } error) throw new InvalidOperationException(error);
            if (group.Channels.FirstOrDefault(ch => !_config.Channels.Any(c => c.Id == ch)) is { } unknown)
                throw new InvalidOperationException($"'{unknown}' is not a channel");
            if (_config.ExclusiveGroups.FirstOrDefault(g => g.Id != group.Id && g.Channels.Any(group.Channels.Contains)) is { } other)
                throw new InvalidOperationException($"a channel can belong to one group only, and one of these is in '{other.Name}'");
            group = group with { Name = group.Name.Trim(), Channels = group.Channels.ToArray() };
            SaveExclusiveGroupsLocked(id is null
                ? [.. _config.ExclusiveGroups, group]
                : [.. _config.ExclusiveGroups.Select(g => g.Id == id ? group : g)], persist);
        }
    }

    /// <summary>Remove a group. Every send keeps the mute it has.</summary>
    public void DeleteExclusiveGroup(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (!_config.ExclusiveGroups.Any(g => g.Id == id))
                throw new InvalidOperationException($"'{id}' is not an exclusive group");
            SaveExclusiveGroupsLocked([.. _config.ExclusiveGroups.Where(g => g.Id != id)], persist);
        }
    }

    private void SaveExclusiveGroupsLocked(IReadOnlyList<ExclusiveGroupDefinition> groups,
        Func<MixerSettings, string?> persist)
    {
        MixerConfig previous = _config;
        var previousMuted = new HashSet<string>(_muted);
        bool previousHardwareMic = _hardwareMicMonitor;
        _config = _config with { ExclusiveGroups = groups };
        NormalizeExclusiveGroupsLocked();
        if (GroupForChannelLocked("xlr1") is not null) _hardwareMicMonitor = false;
        try { PersistLocked(persist); }
        catch
        {
            _config = previous;
            _muted.Clear();
            _muted.UnionWith(previousMuted);
            _hardwareMicMonitor = previousHardwareMic;
            throw;
        }
        ReapplyCellsLocked([.. _config.Mixes.Select(m => m.Id)]);
    }

    /// <summary>
    /// Hand the group over to its next member: that member opens in the mixes
    /// the open member was heard in, and the open member closes everywhere.
    /// Resolved under the mixer lock, so quick presses advance in order.
    /// </summary>
    public void CycleExclusiveGroup(string id)
    {
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            ExclusiveGroupDefinition group = _config.ExclusiveGroups.FirstOrDefault(g => g.Id == id)
                ?? throw new InvalidOperationException($"'{id}' is not an exclusive group");
            int open = -1;
            for (int i = 0; i < group.Channels.Count && open < 0; i++)
                if (IsOpenLocked(group.Channels[i])) open = i;
            if (open < 0)
                throw new InvalidOperationException($"no member of '{group.Name}' is unmuted; unmute one first");
            string current = group.Channels[open];
            string next = group.Channels[(open + 1) % group.Channels.Count];
            string[] mixes = [.. _config.Mixes.Select(m => m.Id)
                .Where(mix => _cells.Contains(Cell(current, mix)) && !_muted.Contains(Cell(current, mix)))];
            CloseExclusivePeersLocked(next);
            foreach (string mix in mixes)
            {
                string cell = Cell(next, mix);
                if (!_cells.Contains(cell)) continue;
                _muted.Remove(cell);
                ApplyCellLocked(next, mix);
            }
        }
    }

    private bool IsOpenLocked(string channel)
    {
        foreach (MixDefinition mix in _config.Mixes)
        {
            string cell = Cell(channel, mix.Id);
            if (_cells.Contains(cell) && !_muted.Contains(cell)) return true;
        }
        return false;
    }

    // A group with more than one member open (a new group, a profile saved
    // before it, a hand-edited file) closes entirely. Picking one member
    // could put a microphone on air the user did not choose.
    private void NormalizeExclusiveGroupsLocked()
    {
        foreach (ExclusiveGroupDefinition group in _config.ExclusiveGroups)
        {
            if (group.Channels.Count(IsOpenLocked) <= 1) continue;
            foreach (string ch in group.Channels)
                foreach (MixDefinition mix in _config.Mixes)
                    if (_cells.Contains(Cell(ch, mix.Id))) _muted.Add(Cell(ch, mix.Id));
        }
    }

    /// <summary>Mute every send of the other members of the channel's group.</summary>
    private void CloseExclusivePeersLocked(string channel)
    {
        if (GroupForChannelLocked(channel) is not { } group) return;
        foreach (string peer in group.Channels)
        {
            if (peer == channel) continue;
            foreach (MixDefinition mix in _config.Mixes)
            {
                string cell = Cell(peer, mix.Id);
                if (!_cells.Contains(cell)) continue;
                if (_muted.Add(cell) || _pendingCells.Contains(cell)) ApplyCellLocked(peer, mix.Id);
            }
        }
    }

    /// <summary>
    /// Whether a peer's mute has not reached PipeWire yet. The member stays
    /// silent until it has, so two members are never heard at once.
    /// </summary>
    private bool ExclusivePeerPendingLocked(string channel)
    {
        if (_pendingCells.Count == 0 || GroupForChannelLocked(channel) is not { } group) return false;
        foreach (string peer in group.Channels)
        {
            if (peer == channel) continue;
            foreach (MixDefinition mix in _config.Mixes)
                if (_pendingCells.Contains(Cell(peer, mix.Id))) return true;
        }
        return false;
    }

    /// <summary>
    /// Apply the sends into these mixes, and with <paramref name="masters"/>
    /// the monitor mixes' sink volume and mute first. With groups present the
    /// muted sends go before the open ones, so a member never opens while a
    /// peer is still heard.
    /// </summary>
    private void ReapplyCellsLocked(IReadOnlyList<string> mixes, bool masters = false)
    {
        if (masters)
            foreach (string mix in mixes)
                if (MonitorMixLocked(mix) is { } monitor)
                {
                    _pw.SetSinkVolume(monitor.SinkName, _mixVolume.GetValueOrDefault(mix, 1));
                    _pw.SetSinkMuted(monitor.SinkName, _mixMuted.Contains(mix));
                }
        bool grouped = _config.ExclusiveGroups.Count > 0;
        if (grouped)
            foreach (string mix in mixes)
                foreach (ChannelDefinition ch in _config.Channels)
                    if (_muted.Contains(Cell(ch.Id, mix))) ApplyCellLocked(ch.Id, mix);
        foreach (string mix in mixes)
            foreach (ChannelDefinition ch in _config.Channels)
                if (!grouped || !_muted.Contains(Cell(ch.Id, mix))) ApplyCellLocked(ch.Id, mix);
    }
}
