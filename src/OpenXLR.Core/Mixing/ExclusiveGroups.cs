namespace OpenXLR.Core.Mixing;

/// <summary>At most one member's send is open in each mix. Levels are preserved.</summary>
public sealed record ExclusiveGroupDefinition(string Id, string Name, IReadOnlyList<string> Channels);

public static class ExclusiveGroupsModel
{
    public const int MaxGroups = 16;
    public const int MaxMembers = MixerConfig.MaxApplicationChannels + 3;

    public static bool ValidId(string? id) => id is { Length: > 0 and <= 36 }
        && id[0] is >= 'a' and <= 'z'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public static string? Validate(ExclusiveGroupDefinition? group)
    {
        if (group is null || !ValidId(group.Id)) return "invalid group ID";
        if (group.Name?.Trim() is not { Length: > 0 and <= 60 } || group.Name.Any(char.IsControl))
            return "group name must contain 1 to 60 printable characters";
        if (group.Channels is not { Count: >= 2 and <= MaxMembers }
            || group.Channels.Any(id => !ValidId(id))
            || group.Channels.Distinct(StringComparer.Ordinal).Count() != group.Channels.Count)
            return $"a group needs 2 to {MaxMembers} distinct channel IDs";
        return null;
    }

    public static List<ExclusiveGroupDefinition> Copy(IEnumerable<ExclusiveGroupDefinition> groups)
        => groups.Select(g => g with { Channels = g.Channels.ToArray() }).ToList();

    /// <summary>Heal stale membership without allowing overlapping or unbounded groups.</summary>
    public static IReadOnlyList<ExclusiveGroupDefinition> Restore(
        IEnumerable<ExclusiveGroupDefinition>? saved, IReadOnlyList<ChannelDefinition> channels)
    {
        var result = new List<ExclusiveGroupDefinition>();
        var known = channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExclusiveGroupDefinition group in saved ?? [])
        {
            if (Validate(group) is not null || ids.Contains(group.Id)) continue;
            string[] members = group.Channels.Where(known.Contains).ToArray();
            if (members.Length < 2 || members.Any(used.Contains)) continue;
            result.Add(group with { Name = group.Name.Trim(), Channels = members });
            ids.Add(group.Id);
            used.UnionWith(members);
            if (result.Count == MaxGroups) break;
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
        for (int i = 0; i < _config.ExclusiveGroups.Count; i++)
        {
            ExclusiveGroupDefinition group = _config.ExclusiveGroups[i];
            for (int j = 0; j < group.Channels.Count; j++)
                if (group.Channels[j] == channel) return group;
        }
        return null;
    }

    public void SetExclusiveGroup(string? id, string name, IReadOnlyList<string> channels,
        Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (id is not null && !_config.ExclusiveGroups.Any(g => g.Id == id))
                throw new InvalidOperationException("unknown exclusive group");
            var group = new ExclusiveGroupDefinition(id ?? MixerConfig.NewId(name, "group",
                _config.ExclusiveGroups.Select(g => g.Id)), name, channels);
            if (ExclusiveGroupsModel.Validate(group) is { } error) throw new InvalidOperationException(error);
            if (id is null && _config.ExclusiveGroups.Count >= ExclusiveGroupsModel.MaxGroups)
                throw new InvalidOperationException("exclusive group limit reached");
            if (channels.Any(ch => !_config.Channels.Any(c => c.Id == ch)))
                throw new InvalidOperationException("unknown channel in exclusive group");
            if (_config.ExclusiveGroups.Any(g => g.Id != id && g.Channels.Any(channels.Contains)))
                throw new InvalidOperationException("a channel can belong to only one exclusive group");
            group = group with { Name = name.Trim(), Channels = channels.ToArray() };
            IReadOnlyList<ExclusiveGroupDefinition> next = id is null
                ? [.. _config.ExclusiveGroups, group]
                : _config.ExclusiveGroups.Select(g => g.Id == id ? group : g).ToArray();
            SaveExclusiveGroupsLocked(next, persist);
        }
    }

    public void DeleteExclusiveGroup(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (!_config.ExclusiveGroups.Any(g => g.Id == id)) throw new InvalidOperationException("unknown exclusive group");
            SaveExclusiveGroupsLocked(_config.ExclusiveGroups.Where(g => g.Id != id).ToArray(), persist);
        }
    }

    private void SaveExclusiveGroupsLocked(IReadOnlyList<ExclusiveGroupDefinition> groups,
        Func<MixerSettings, string?> persist)
    {
        MixerConfig previous = _config;
        var previousMuted = new HashSet<string>(_muted);
        _config = _config with { ExclusiveGroups = groups };
        NormalizeExclusiveGroupsLocked();
        try { PersistLocked(persist); }
        catch
        {
            _config = previous;
            _muted.Clear();
            _muted.UnionWith(previousMuted);
            throw;
        }
        foreach (MixDefinition mix in _config.Mixes) ReapplyCellsLocked(mix.Id);
    }

    /// <summary>Resolve the next member under the mixer lock, including rapid key presses.</summary>
    public void CycleExclusiveGroup(string id, string mix)
    {
        lock (_gate)
        {
            var group = _config.ExclusiveGroups.FirstOrDefault(g => g.Id == id)
                ?? throw new InvalidOperationException("unknown exclusive group");
            if (!_built || !_config.Mixes.Any(m => m.Id == mix)) throw new InvalidOperationException("unknown mix");
            int active = -1;
            for (int i = 0; i < group.Channels.Count; i++)
                if (!_muted.Contains(Cell(group.Channels[i], mix))) { active = i; break; }
            SetChannelMuted(group.Channels[(active + 1) % group.Channels.Count], mix, false);
        }
    }

    // Conflicting saved/default sends close together. Picking an arbitrary
    // member could open a microphone the user did not intend to broadcast.
    private void NormalizeExclusiveGroupsLocked()
    {
        foreach (ExclusiveGroupDefinition group in _config.ExclusiveGroups)
            foreach (MixDefinition mix in _config.Mixes)
                if (group.Channels.Count(ch => !_muted.Contains(Cell(ch, mix.Id))) > 1)
                    foreach (string ch in group.Channels) _muted.Add(Cell(ch, mix.Id));
    }

    private void CloseExclusivePeersLocked(string channel, string mix)
    {
        if (GroupForChannelLocked(channel) is not { } group) return;
        foreach (string peer in group.Channels)
        {
            if (peer == channel) continue;
            _muted.Add(Cell(peer, mix));
            ApplyCellLocked(peer, mix);
        }
    }

    private bool ExclusivePeerPendingLocked(string channel, string mix)
    {
        if (_pendingCells.Count == 0 || GroupForChannelLocked(channel) is not { } group) return false;
        foreach (string peer in group.Channels)
            if (peer != channel && _pendingCells.Contains(Cell(peer, mix))) return true;
        return false;
    }

    private void ReapplyCellsLocked(string mix)
    {
        // Apply the closed sends first on recall/rebuild. An active member
        // remains pending and silent until all of its peers confirm the mute.
        if (_config.ExclusiveGroups.Count > 0)
            foreach (ChannelDefinition ch in _config.Channels)
                if (_muted.Contains(Cell(ch.Id, mix))) ApplyCellLocked(ch.Id, mix);
        foreach (ChannelDefinition ch in _config.Channels)
            if (_config.ExclusiveGroups.Count == 0 || !_muted.Contains(Cell(ch.Id, mix))) ApplyCellLocked(ch.Id, mix);
    }
}
