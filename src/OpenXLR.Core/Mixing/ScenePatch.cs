namespace OpenXLR.Core.Mixing;

/// <summary>
/// The mixer values an undo step can carry. The first version covers sends,
/// volumes and mutes; a later field is added here, read in
/// <see cref="Mixer.ReadSceneValues"/> and written in
/// <see cref="Mixer.ApplyScenePatch"/>.
/// </summary>
public enum SceneField
{
    /// <summary>A send level, keyed by its cell <c>channel|mix</c>; a number from 0 to 1.</summary>
    Level,
    /// <summary>A channel's own volume, keyed by channel id; a number from 0 to 1.</summary>
    ChannelVolume,
    /// <summary>A mix master, keyed by mix id; a number.</summary>
    MixVolume,
    /// <summary>A send mute, keyed by its cell <c>channel|mix</c>; a boolean.</summary>
    ChannelMuted,
    /// <summary>A mix mute, keyed by mix id; a boolean.</summary>
    MixMuted,
}

/// <summary>One value of the scene: which field, and which channel, cell or mix.</summary>
public readonly record struct SceneKey(SceneField Field, string Key);

/// <summary>
/// One entry of a patch: write <see cref="Target"/> where the live value is
/// still <see cref="Expected"/>. Values are boxed numbers or booleans, as
/// <see cref="SceneValues"/> compares them.
/// </summary>
public sealed record ScenePatchEntry(SceneKey Key, object Expected, object Target);

/// <summary>What became of one entry of a patch.</summary>
public enum ScenePatchOutcome
{
    /// <summary>The live value was the expected one and the target was written.</summary>
    Applied,
    /// <summary>The live value was already the target; nothing was written.</summary>
    AlreadyThere,
    /// <summary>Something else moved the value since; it was left alone.</summary>
    Changed,
    /// <summary>The channel, cell or mix no longer exists.</summary>
    Gone,
    /// <summary>The write was refused, as the audio server refusing a mute.</summary>
    Failed,
}

/// <summary>How two scene values compare.</summary>
public static class SceneValues
{
    /// <summary>
    /// How far a number read back may stray and still count as the value a
    /// step left. A monitor master goes through the desktop's whole
    /// percentages and comes back up to half a percent away.
    /// </summary>
    public const double Tolerance = 0.006;

    /// <summary>The same value within <see cref="Tolerance"/>, for the check before a step is applied.</summary>
    public static bool Near(object a, object b) => a is double x && b is double y ? Math.Abs(x - y) <= Tolerance : Equals(a, b);

    /// <summary>Exactly the same value, for telling whether a command changed anything.</summary>
    public static bool Same(object a, object b) => Equals(a, b);
}

public sealed partial class Mixer
{
    /// <summary>
    /// The live values of the given fields whose keys <paramref name="keep"/>
    /// accepts, read under the mixer lock. Cached values only: no helper runs.
    /// </summary>
    public Dictionary<SceneKey, object> ReadSceneValues(IReadOnlyCollection<SceneField> fields, Func<SceneKey, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(keep);
        var values = new Dictionary<SceneKey, object>();
        lock (_gate)
        {
            foreach (SceneField field in fields)
                foreach (SceneKey key in KeysLocked(field))
                    if (keep(key) && LiveValueLocked(key) is { } value) values[key] = value;
        }
        return values;
    }

    /// <summary>
    /// The members of the exclusive group <paramref name="channel"/> is in,
    /// the channel included, or the channel alone when it is in none.
    /// </summary>
    public IReadOnlyList<string> ExclusivePeersOf(string channel)
    {
        lock (_gate) return GroupForChannelLocked(channel) is { } group ? [.. group.Channels] : [channel];
    }

    /// <summary>The members of an exclusive group by its id, or none.</summary>
    public IReadOnlyList<string> ExclusiveGroupMembers(string group)
    {
        lock (_gate) return _config.ExclusiveGroups.FirstOrDefault(g => g.Id == group) is { } found ? [.. found.Channels] : [];
    }

    /// <summary>
    /// Write a sparse patch under one hold of the mixer lock. Each entry is
    /// written only where the live value is still its expected value; one
    /// that already holds its target counts as done, and any other is left
    /// alone. Volumes and levels go first, then the mutes that close, then
    /// those that open, so unmuting a member of an exclusive group closes
    /// its peers as a command would: the group never has two members heard.
    /// Only the sends the patch touches are written to the audio server.
    /// The caller pushes the hardware paths, the state and the save.
    /// </summary>
    public IReadOnlyList<ScenePatchOutcome> ApplyScenePatch(IReadOnlyList<ScenePatchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            var outcomes = new ScenePatchOutcome[entries.Count];
            var writes = new List<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                ScenePatchEntry entry = entries[i];
                object? live = LiveValueLocked(entry.Key);
                if (live is null || entry.Target.GetType() != live.GetType()) outcomes[i] = ScenePatchOutcome.Gone;
                else if (SceneValues.Near(live, entry.Target)) outcomes[i] = ScenePatchOutcome.AlreadyThere;
                else if (SceneValues.Near(live, entry.Expected)) writes.Add(i);
                else outcomes[i] = ScenePatchOutcome.Changed;
            }

            var cells = new List<string>();
            var channels = new List<string>();
            int Order(int i) => entries[i].Key.Field switch
            {
                SceneField.ChannelMuted or SceneField.MixMuted => entries[i].Target is true ? 1 : 2,
                _ => 0,
            };
            foreach (int i in writes.OrderBy(Order))
            {
                ScenePatchEntry entry = entries[i];
                try
                {
                    WriteLocked(entry.Key, entry.Target, cells, channels);
                    outcomes[i] = ScenePatchOutcome.Applied;
                }
                // A refused master write leaves that value where it was, as
                // the command would; the rest of the patch still goes on.
                catch (Exception) { outcomes[i] = ScenePatchOutcome.Failed; }
            }
            foreach (string channel in channels.Distinct(StringComparer.Ordinal))
                foreach (MixDefinition mix in _config.Mixes) cells.Add(Cell(channel, mix.Id));
            foreach (string cell in cells.Distinct(StringComparer.Ordinal))
            {
                int bar = cell.IndexOf('|', StringComparison.Ordinal);
                if (_cells.Contains(cell)) ApplyCellLocked(cell[..bar], cell[(bar + 1)..]);
            }
            return outcomes;
        }
    }

    private IEnumerable<SceneKey> KeysLocked(SceneField field) => field switch
    {
        SceneField.Level or SceneField.ChannelMuted => _cells.Select(cell => new SceneKey(field, cell)),
        SceneField.ChannelVolume => _config.Channels.Select(c => new SceneKey(field, c.Id)),
        SceneField.MixVolume or SceneField.MixMuted => _config.Mixes.Select(m => new SceneKey(field, m.Id)),
        _ => [],
    };

    /// <summary>The value one key holds now, or null when its channel, cell or mix is gone.</summary>
    private object? LiveValueLocked(SceneKey key) => key.Field switch
    {
        SceneField.Level => _cells.Contains(key.Key) ? _levels.GetValueOrDefault(key.Key, 0.0) : null,
        SceneField.ChannelMuted => _cells.Contains(key.Key) ? _muted.Contains(key.Key) : null,
        SceneField.ChannelVolume => HasChannelVolumeLocked(key.Key) ? ChannelVolumeLocked(key.Key) : null,
        SceneField.MixVolume => _config.Mixes.Any(m => m.Id == key.Key) && _mixVolume.TryGetValue(key.Key, out double volume) ? volume : null,
        SceneField.MixMuted => _config.Mixes.Any(m => m.Id == key.Key) ? _mixMuted.Contains(key.Key) : null,
        _ => null,
    };

    /// <summary>
    /// Store one value. A send or a channel volume only collects what has to
    /// be written; the masters go through their setters, which write the
    /// mix's sink or its sends themselves.
    /// </summary>
    private void WriteLocked(SceneKey key, object value, List<string> cells, List<string> channels)
    {
        switch (key.Field)
        {
            case SceneField.Level:
                _levels[key.Key] = Math.Clamp((double)value, 0, 1);
                cells.Add(key.Key);
                break;
            case SceneField.ChannelVolume:
                _channelVolume[key.Key] = Math.Clamp((double)value, 0, 1);
                channels.Add(key.Key);
                break;
            case SceneField.MixVolume:
                SetMixVolume(key.Key, (double)value);
                break;
            case SceneField.MixMuted:
                SetMixMuted(key.Key, (bool)value);
                break;
            case SceneField.ChannelMuted:
                if (value is true) _muted.Add(key.Key);
                else
                {
                    CloseExclusivePeersLocked(key.Key[..key.Key.IndexOf('|', StringComparison.Ordinal)]);
                    _muted.Remove(key.Key);
                }
                cells.Add(key.Key);
                break;
        }
    }
}
