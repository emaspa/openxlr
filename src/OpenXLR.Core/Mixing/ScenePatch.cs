namespace OpenXLR.Core.Mixing;

/// <summary>
/// The mixer values an undo step can carry: sends, volumes and mutes, the
/// processing of microphones and mixes, and insert chains. A later field is added here,
/// read in <see cref="Mixer.ReadSceneValues"/> and written in
/// <see cref="Mixer.ApplyScenePatch"/>. A processing field holds a whole
/// setting, so one EQ is one value however many bands a drag moved.
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
    /// <summary>The EQ of a microphone or capture input, keyed by channel id; a <see cref="MicEq"/>.</summary>
    Eq,
    /// <summary>A mix's output EQ, keyed by mix id; a <see cref="MicEq"/>.</summary>
    MixEq,
    /// <summary>A microphone's noise removal, keyed by channel id; a <see cref="MicNoise"/>.</summary>
    NoiseRemoval,
    /// <summary>A microphone's voice changer, keyed by channel id; a <see cref="MicVoice"/>.</summary>
    VoiceChanger,
    /// <summary>A microphone's compressor, keyed by channel id; a <see cref="MicCompressor"/>.</summary>
    Compressor,
    /// <summary>A microphone's expander, keyed by channel id; a <see cref="MicExpander"/>.</summary>
    Expander,
    /// <summary>The software low cut, keyed by <see cref="SceneKeys.FirstMicrophone"/>; an int, 0, 80 or 120.</summary>
    LowCut,
    /// <summary>The software ClipGuard, keyed by <see cref="SceneKeys.FirstMicrophone"/>; a boolean.</summary>
    SoftClipGuard,
    /// <summary>Whether a monitor mix hears the microphones dry, keyed by mix id; a boolean.</summary>
    MonitorDry,
    /// <summary>The ducking setup without where it runs, keyed by <see cref="SceneKeys.Ducking"/>; a <see cref="DuckingScene"/>.</summary>
    Ducking,
    /// <summary>One insert chain, keyed by its chain key (a channel id or <c>mix:&lt;id&gt;</c>); an <see cref="InsertChain"/>.</summary>
    Inserts,
}

/// <summary>
/// One insert chain as an undo step holds it: each insert in order, with its
/// plugin, name, bypass, host and control values, copied so a later move of
/// a control cannot reach the step. Two chains are the same when all of
/// that is. Whether an insert runs on its own block is where it runs, like
/// the processing switches, so it is carried but not compared, and a chain
/// put back keeps the choice each insert has now.
/// </summary>
public sealed record InsertChain
{
    public InsertChain(IEnumerable<InsertDefinition> inserts)
        => Inserts = [.. inserts.Select(i => i with { Params = new Dictionary<string, double>(i.Params) })];

    public IReadOnlyList<InsertDefinition> Inserts { get; }

    public bool Equals(InsertChain? other)
        => other is not null && Inserts.Count == other.Inserts.Count && Inserts.Zip(other.Inserts).All(p => SameInsert(p.First, p.Second));

    public override int GetHashCode() => Inserts.Count;

    private static bool SameInsert(InsertDefinition a, InsertDefinition b)
        => a.Id == b.Id && a.Kind == b.Kind && a.Plugin == b.Plugin && a.Label == b.Label && a.Bypass == b.Bypass
            && a.NativeHost == b.NativeHost && a.Params.Count == b.Params.Count
            && a.Params.All(p => b.Params.TryGetValue(p.Key, out double value) && value.Equals(p.Value));
}

/// <summary>The keys of the scene values that are not a channel's, a cell's or a mix's.</summary>
public static class SceneKeys
{
    /// <summary>The software low cut and ClipGuard work on XLR 1 alone.</summary>
    public const string FirstMicrophone = "xlr1";

    /// <summary>The one ducking setup.</summary>
    public const string Ducking = "ducking";
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
    /// <summary>
    /// The write was refused: the audio server refusing a mute, a ClipGuard
    /// whose plugin is missing, an output EQ the unit holds while it builds
    /// the mix.
    /// </summary>
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
    public static bool Near(object a, object b) => a is double x && b is double y ? Math.Abs(x - y) <= Tolerance : Same(a, b);

    /// <summary>
    /// Exactly the same value, for telling whether a command changed
    /// anything. The settings are records compared field by field; a ducking
    /// setup's lists are compared by their contents.
    /// </summary>
    public static bool Same(object a, object b)
        => a is DuckingScene x && b is DuckingScene y ? SameDucking(x, y) : Equals(a, b);

    private static bool SameDucking(DuckingScene a, DuckingScene b)
        => a.Enabled == b.Enabled && Sequence(DuckingScene.KeysOf(a), DuckingScene.KeysOf(b))
            && Sequence(a.Channels, b.Channels) && Sequence(a.Mixes, b.Mixes)
            && Equals(a.Sensitivity, b.Sensitivity) && Equals(a.Amount, b.Amount) && Equals(a.Speed, b.Speed);

    private static bool Sequence(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
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
    /// Only the sends the patch touches are written to the audio server. A
    /// processing setting is written through its command's setter.
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
                // A refused master write or setting leaves that value where it
                // was, as the command would; the rest of the patch still goes on.
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
        SceneField.MixVolume or SceneField.MixMuted or SceneField.MixEq => _config.Mixes.Select(m => new SceneKey(field, m.Id)),
        SceneField.Eq => _config.Channels.Where(CarriesEq).Select(c => new SceneKey(field, c.Id)),
        SceneField.NoiseRemoval or SceneField.VoiceChanger or SceneField.Compressor or SceneField.Expander
            => _config.Channels.Where(CarriesMicrophoneStage).Select(c => new SceneKey(field, c.Id)),
        SceneField.MonitorDry => _config.Mixes.Where(m => m.Kind == MixKind.Monitor).Select(m => new SceneKey(field, m.Id)),
        SceneField.LowCut or SceneField.SoftClipGuard => [new SceneKey(field, SceneKeys.FirstMicrophone)],
        SceneField.Ducking => [new SceneKey(field, SceneKeys.Ducking)],
        SceneField.Inserts => _config.Channels.Where(c => c.ChainWidth is not null).Select(c => new SceneKey(field, c.Id))
            .Concat(_config.Mixes.Select(m => new SceneKey(field, MixKey(m)))),
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
        SceneField.Eq => CarriesEqLocked(key.Key) ? EqLocked(key.Key) : null,
        SceneField.MixEq => _config.Mixes.Any(m => m.Id == key.Key) ? MixEqLocked(key.Key) : null,
        SceneField.NoiseRemoval => CarriesMicrophoneStageLocked(key.Key) ? NoiseLocked(key.Key) : null,
        SceneField.VoiceChanger => CarriesMicrophoneStageLocked(key.Key) ? VoiceLocked(key.Key) : null,
        SceneField.Compressor => CarriesMicrophoneStageLocked(key.Key) ? CompressorLocked(key.Key) : null,
        SceneField.Expander => CarriesMicrophoneStageLocked(key.Key) ? ExpanderLocked(key.Key) : null,
        SceneField.LowCut => key.Key == SceneKeys.FirstMicrophone ? _lowCutHz : null,
        SceneField.SoftClipGuard => key.Key == SceneKeys.FirstMicrophone ? _softClipGuard : null,
        SceneField.MonitorDry => MonitorMixLocked(key.Key) is not null ? _dryMonitors.Contains(key.Key) : null,
        SceneField.Ducking => key.Key == SceneKeys.Ducking ? DuckingSetupLocked().ToScene() : null,
        SceneField.Inserts => IsInsertChannel(key.Key) ? new InsertChain(InsertsFor(key.Key)) : null,
        _ => null,
    };

    /// <summary>
    /// Store one value. A send or a channel volume only collects what has to
    /// be written; the masters go through their setters, which write the
    /// mix's sink or its sends themselves. A processing setting goes through
    /// the setter its command uses, whole, so it reaches a running stage
    /// live, starts or stops the stage as the command would, and an EQ or a
    /// compressor that runs on the unit is posted to the unit. A setter that
    /// refuses throws, and the value is left as it is.
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
            case SceneField.Eq:
                SetEq(key.Key, (MicEq)value);
                break;
            case SceneField.MixEq:
                SetMixEq(key.Key, (MicEq)value);
                break;
            case SceneField.NoiseRemoval:
            {
                var noise = (MicNoise)value;
                SetNoiseRemoval(key.Key, new MicNoiseChange(noise.Enabled, noise.Engine, noise.Strength));
                break;
            }
            case SceneField.VoiceChanger:
            {
                var voice = (MicVoice)value;
                SetVoiceChanger(key.Key, new MicVoiceChange(voice.Enabled, voice.Voice, voice.Pitch, voice.Formant));
                break;
            }
            case SceneField.Compressor:
            {
                var c = (MicCompressor)value;
                SetCompressor(key.Key, new MicCompressorChange(c.Enabled, c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Makeup));
                break;
            }
            case SceneField.Expander:
            {
                var e = (MicExpander)value;
                SetExpander(key.Key, new MicExpanderChange(e.Enabled, e.Threshold, e.Ratio, e.Range, e.Attack, e.Release, e.Hold));
                break;
            }
            case SceneField.LowCut:
                SetLowCutHz((int)value);
                break;
            case SceneField.SoftClipGuard:
                SetSoftClipGuard((bool)value);
                break;
            case SceneField.MonitorDry:
                SetMonitorDry(key.Key, (bool)value);
                break;
            case SceneField.Ducking:
                RestoreDuckingLocked((DuckingScene)value);
                break;
            case SceneField.Inserts:
                RestoreChainLocked(key.Key, (InsertChain)value);
                break;
        }
    }

    /// <summary>
    /// Put one insert chain back. A chain whose structure stays the same
    /// (the same plugins in the same order, each with the same bypass and
    /// host) keeps its running plugins: new control values are pushed into
    /// them live, and a name changes nothing that runs. Any other chain is
    /// built again, as <c>setInserts</c> builds it, so a plugin that comes
    /// back starts from its saved control values. An insert keeps the own
    /// block choice it has now; one that comes back takes the one it had.
    /// </summary>
    private void RestoreChainLocked(string key, InsertChain chain)
    {
        List<InsertDefinition> before = InsertsFor(key);
        _inserts[key] = [.. chain.Inserts.Select(i => i with
        {
            Params = new Dictionary<string, double>(i.Params),
            NativeHost = i.RunsNatively,
            OwnBlock = before.FirstOrDefault(b => b.Id == i.Id && b.Kind == i.Kind && b.Plugin == i.Plugin)?.OwnBlock ?? i.OwnBlock,
        })];
        if (!_built) return;
        bool rebuild;
        if (MixForKey(key) is not null)
        {
            (ChainStructure structure, bool chained) = MixChainLocked(key);
            rebuild = ChainNeedsRebuildLocked(key, structure, chained);
        }
        else
        {
            InputRoute route = InputRouteLocked(_config.Channels.First(c => c.Id == key), null);
            rebuild = ChainNeedsRebuildLocked(key, route.Structure, route.Chained);
        }
        if (rebuild || !PushChainParamsLocked(key, before)) RewireInsertKeyLocked(key);
    }

    /// <summary>
    /// Put a ducking setup back as a profile recall does: where it runs stays
    /// this machine's choice, and a key, channel or mix the layout no longer
    /// has is dropped. The debounced save keeps it, as after a recall.
    /// </summary>
    private void RestoreDuckingLocked(DuckingScene scene)
    {
        DuckingSetup previous = DuckingSetupLocked();
        DuckingSetup next = DuckingSetup.Prune(DuckingSetup.FromScene(scene, previous.Processing), _config).Copy();
        if (DuckingSetup.Validate(next, this) is { } error) throw new ArgumentException($"setDucking: {error}");
        _duckingSetup = next;
        if (next.Enabled && !previous.Enabled) _duckingFailed.Clear();
        SyncDuckingLocked();
    }
}
