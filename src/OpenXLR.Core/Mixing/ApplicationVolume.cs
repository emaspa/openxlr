namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private const int AppVolumeWritesPerSweep = 8;
    // A failed helper that took almost its whole deadline may mean the
    // server is stalled. Stop this pass and let the next one retry.
    private const int AppVolumeSlowFailureMs = 200;
    // How long a write may take to show in the registry. Until then a
    // different level on the stream is the old one, not somebody's change.
    private const int AppVolumeSettleMs = 1500;
    // The session manager restores a new stream's remembered level, and a
    // program can set its own, a moment after the stream appears. A write
    // that came first is repeated; past this count the stream keeps the
    // level it insists on and the saved one follows it.
    private const int AppVolumeAttempts = 3;

    private Dictionary<string, double> _appVolumes = new(StringComparer.OrdinalIgnoreCase);
    // A reset can outlive its streams if WirePlumber is still holding their old level.
    private OrderedDictionary<string, bool> _appVolumeResetsOnLaunch = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _hasAppVolumeWork;
    private long _appVolumeRevision;
    // What was last written to each playback stream, by registry id.
    private readonly Dictionary<int, AppliedAppVolume> _appVolumeApplied = [];
    // The playback streams of the last pass, so a command reaches them at once.
    private readonly Dictionary<int, AudioStream> _appVolumeStreams = [];
    // The level each application plays at now, by the key its volume is saved under.
    private Dictionary<string, double> _appLiveVolumes = new(StringComparer.OrdinalIgnoreCase);
    private bool _appVolumesChanged;

    internal bool HasAppVolumeWork => _hasAppVolumeWork;

    private sealed record AppliedAppVolume(int Serial, string Key, double Target, double Written, long At)
    {
        public bool Confirmed { get; init; }
        public int Attempts { get; init; } = 1;
        /// <summary>When a write the sound server refused is tried again; 0 after one it took.</summary>
        public long RetryAt { get; init; }
    }

    public const int MaxAppVolumeIdentityLength = 256;

    public static bool ValidAppVolumeIdentity(string? identity)
        => identity is { Length: > 0 and <= MaxAppVolumeIdentityLength }
            && !string.IsNullOrWhiteSpace(identity) && !identity.Any(char.IsControl)
            && !PipeWireAdapter.IsPlumbingIdentity(identity)
            && AppVolumeKey(identity) is { } key && !string.IsNullOrWhiteSpace(key)
            && !PipeWireAdapter.IsPlumbingIdentity(key);

    /// <summary>
    /// The key an application's volume is saved under: the one its channel
    /// assignment uses, so both follow the application together.
    /// </summary>
    internal static string AppVolumeKey(string identity) => StreamMatcher.MigrateIdentity(identity);

    /// <summary>
    /// Set an application's playback volume, or clear it with null. The level
    /// is written to playing streams within a short budget. The sweep
    /// handles remaining writes and streams that start later. Clearing
    /// restores full volume the next time the application plays if it has
    /// no stream now.
    /// </summary>
    public void SetAppVolume(string identity, double? volume)
    {
        if (!ValidAppVolumeIdentity(identity)) throw new ArgumentException("invalid application identity");
        if (volume is double level && !ValidPlaybackVolume(level))
            throw new ArgumentOutOfRangeException(nameof(volume), "playback volume must be between 0 and 1");
        string key = AppVolumeKey(identity);
        lock (_gate)
        {
            if (!_apps.ContainsKey(key) && !_apps.ContainsKey(identity))
                throw new InvalidOperationException("the application is no longer listed");
            if (volume is not double value)
            {
                ResetAppVolumeLocked(key);
                return;
            }
            if (!_appVolumes.ContainsKey(key) && _appVolumes.Count >= MaxAppOverrides)
                throw new InvalidOperationException($"at most {MaxAppOverrides} application volumes can be remembered");
            double target = SnapPlaybackVolume(value);
            bool canceledReset = _appVolumeResetsOnLaunch.Remove(key);
            _appVolumes[key] = target;
            _hasAppVolumeWork = true;
            _appVolumeRevision++;
            int budget = AppVolumeWritesPerSweep;
            foreach (AudioStream stream in _appVolumeStreams.Values)
            {
                if (AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    if (canceledReset) _appVolumeApplied.Remove(stream.Id);
                    WriteAppVolumeLocked(stream, key, target, ref budget);
                }
            }
        }
    }

    /// <summary>
    /// True once after a stream's level moved from outside and the saved
    /// level followed it, or the last stream of a pending reset reached full
    /// volume, so the daemon knows to save.
    /// </summary>
    public bool TakeAppVolumesChanged()
    {
        lock (_gate)
        {
            bool changed = _appVolumesChanged;
            _appVolumesChanged = false;
            return changed;
        }
    }

    /// <summary>
    /// Give playback streams their application's saved level without waiting
    /// for the sweep. Called when the registry reports a new stream, so the
    /// stream plays at the level the session manager restored for as short a
    /// time as a helper takes to start. True when the state clients see changed.
    /// </summary>
    public bool SyncAppVolumes()
    {
        if (!_built || !HasAppVolumeWork) return false;
        long revision = Volatile.Read(ref _appVolumeRevision);
        IReadOnlyList<AudioStream> live;
        try { live = _pw.ListStreams(); }
        catch (InvalidOperationException) { return false; }
        // A graph build holds the gate for seconds; the sweep that follows
        // it does this work.
        if (!Monitor.TryEnter(_gate, 200)) return false;
        try { return _built && revision == _appVolumeRevision && SyncAppVolumesLocked(live); }
        finally { Monitor.Exit(_gate); }
    }

    private void ResetAppVolumeLocked(string key)
    {
        if (!_appVolumes.Remove(key)) return;
        if (_appVolumeResetsOnLaunch.Count >= MaxAppOverrides) _appVolumeResetsOnLaunch.RemoveAt(0);
        _appVolumeResetsOnLaunch.Add(key, true);
        _appVolumeRevision++;
        int budget = AppVolumeWritesPerSweep;
        foreach (AudioStream stream in _appVolumeStreams.Values)
        {
            if (!AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            bool settled = _appVolumeApplied.Remove(stream.Id, out AppliedAppVolume? applied)
                && applied.Confirmed && applied.Serial == stream.Serial && applied.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
            WriteAppVolumeLocked(stream, key, 1, ref budget, settled: settled);
        }
    }

    private void RestoreAppVolumesLocked(MixerSettings settings)
    {
        _appVolumes = SavedMixerValidation.AppVolumes(settings.AppVolumes, null);
        _appVolumeResetsOnLaunch = SavedMixerValidation.AppVolumeResets(settings.AppVolumeResets, _appVolumes, null);
        _hasAppVolumeWork = _appVolumes.Count != 0 || _appVolumeResetsOnLaunch.Count != 0;
        _appVolumeRevision++;
    }

    private void ClearAppVolumeStreamsLocked()
    {
        _appVolumeApplied.Clear();
        _appVolumeStreams.Clear();
        _appLiveVolumes.Clear();
        _appVolumeRevision++;
    }

    private void WriteAppVolumeLocked(AudioStream stream, string key, double target, ref int budget,
        int attempt = 1, bool settled = false)
    {
        if (budget == 0) return;
        budget--;
        long started = Clock();
        try
        {
            _pw.SetApplicationStreamVolume(stream.Serial, target);
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, target, Clock()) { Attempts = attempt, Confirmed = settled };
        }
        catch (InvalidOperationException)
        {
            // A stalled server costs one failure per pass. A quick refusal
            // only affects this input, and repeated refusals leave room in
            // the next sweep for streams later in the registry.
            long now = Clock();
            bool stalled = now - started >= AppVolumeSlowFailureMs;
            bool repeated = _appVolumeApplied.TryGetValue(stream.Id, out AppliedAppVolume? previous)
                && previous.Serial == stream.Serial && previous.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                && previous.Target == target && previous.RetryAt != 0;
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, double.NaN, now)
                { Attempts = attempt, RetryAt = now + (stalled || repeated ? 3000 : 250) };
            if (stalled) budget = 0;
        }
    }

    private static bool SameAppVolume(double a, double b) => Math.Abs(a - b) < 0.006;

    private bool SyncAppVolumesLocked(IReadOnlyList<AudioStream> live)
    {
        _appVolumeRevision++;
        _appVolumeStreams.Clear();
        var playing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int budget = AppVolumeWritesPerSweep;
        // A reset stays pending while its app is stopped or any of its
        // streams has not settled. The next launch can restore the old
        // level after our first write, so watch for it on the same passes.
        Dictionary<string, bool>? resetDone = _appVolumeResetsOnLaunch.Count == 0 ? null : new(StringComparer.OrdinalIgnoreCase);
        bool adopted = false;
        foreach (AudioStream stream in live)
        {
            _appVolumeStreams[stream.Id] = stream;
            string key = AppVolumeKey(stream.Identity);
            if (stream.Volume is double observed) playing[key] = observed;
            if (!_appVolumes.TryGetValue(key, out double target))
            {
                if (resetDone is not null && _appVolumeResetsOnLaunch.ContainsKey(key))
                {
                    bool settled = SyncAppVolumeResetLocked(stream, key, ref budget);
                    resetDone[key] = settled && resetDone.GetValueOrDefault(key, true);
                }
                else _appVolumeApplied.Remove(stream.Id);
                continue;
            }
            long now = Clock();
            if (!_appVolumeApplied.TryGetValue(stream.Id, out AppliedAppVolume? applied) || applied.Serial != stream.Serial
                || !applied.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                // A stream not seen before. One already at its level, as
                // after a daemon restart, is left alone.
                if (stream.Volume is double found && SameAppVolume(found, target))
                    _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, found, now) { Confirmed = true };
                else WriteAppVolumeLocked(stream, key, target, ref budget);
                continue;
            }
            if (applied.Target != target)
            {
                WriteAppVolumeLocked(stream, key, target, ref budget);
                continue;
            }
            if (applied.RetryAt != 0)
            {
                if (now >= applied.RetryAt) WriteAppVolumeLocked(stream, key, target, ref budget, applied.Attempts);
                continue;
            }
            if (stream.Volume is not double level) continue;
            if (SameAppVolume(level, applied.Written))
            {
                // Held for a moment before it counts: a restore that lands
                // just after the write must not read as a later change.
                if (!applied.Confirmed && now - applied.At >= 250) _appVolumeApplied[stream.Id] = applied with { Confirmed = true };
                continue;
            }
            if (!applied.Confirmed)
            {
                if (now - applied.At < AppVolumeSettleMs) continue;
                if (applied.Attempts < AppVolumeAttempts)
                {
                    WriteAppVolumeLocked(stream, key, target, ref budget, applied.Attempts + 1);
                    continue;
                }
            }
            // The desktop's volume control or the program itself moved the
            // stream. The saved level follows, as an output's volume does,
            // and the program's other streams get it on this pass or the next.
            double followed = SnapPlaybackVolume(Math.Min(level, 1));
            _appVolumes[key] = followed;
            _appVolumeApplied[stream.Id] = applied with { Target = followed, Written = level, Confirmed = true };
            _appVolumesChanged = adopted = true;
        }

        foreach (int gone in _appVolumeApplied.Keys.Where(id => !_appVolumeStreams.ContainsKey(id)).ToArray())
            _appVolumeApplied.Remove(gone);

        if (resetDone is not null)
        {
            foreach ((string key, bool done) in resetDone)
                if (done && _appVolumeResetsOnLaunch.Remove(key)) _appVolumesChanged = true;
            _hasAppVolumeWork = _appVolumes.Count != 0 || _appVolumeResetsOnLaunch.Count != 0;
        }
        bool moved = playing.Count != _appLiveVolumes.Count
            || playing.Any(p => !_appLiveVolumes.TryGetValue(p.Key, out double before) || before != p.Value);
        _appLiveVolumes = playing;
        return adopted || moved;
    }

    private bool SyncAppVolumeResetLocked(AudioStream stream, string key, ref int budget)
    {
        if (!_appVolumeApplied.TryGetValue(stream.Id, out AppliedAppVolume? applied) || applied.Serial != stream.Serial
            || !applied.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || applied.Target != 1)
        {
            WriteAppVolumeLocked(stream, key, 1, ref budget);
            return false;
        }
        if (applied.Confirmed) return true;
        long now = Clock();
        if (applied.RetryAt != 0)
        {
            if (now >= applied.RetryAt) WriteAppVolumeLocked(stream, key, 1, ref budget, applied.Attempts);
            return false;
        }
        if (now - applied.At < AppVolumeSettleMs) return false;
        if (stream.Volume is not double level || SameAppVolume(level, 1) || applied.Attempts >= AppVolumeAttempts)
        {
            _appVolumeApplied[stream.Id] = applied with { Confirmed = true };
            return true;
        }
        WriteAppVolumeLocked(stream, key, 1, ref budget, applied.Attempts + 1);
        return false;
    }
}
