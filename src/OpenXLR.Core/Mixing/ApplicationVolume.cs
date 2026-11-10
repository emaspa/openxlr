namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private const int AppVolumeWritesPerSweep = 8;
    // How long a write may take to show in the registry. Until then a
    // different level on the stream is the old one, not somebody's change.
    private const int AppVolumeSettleMs = 1500;
    // The session manager restores a new stream's remembered level, and a
    // program can set its own, a moment after the stream appears. A write
    // that came first is repeated; past this count the stream keeps the
    // level it insists on and the saved one follows it.
    private const int AppVolumeAttempts = 3;

    private Dictionary<string, double> _appVolumes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _hasAppVolumes;
    // What was last written to each playback stream, by registry id.
    private readonly Dictionary<int, AppliedAppVolume> _appVolumeApplied = [];
    // The playback streams of the last pass, so a command reaches them at once.
    private readonly Dictionary<int, AudioStream> _appVolumeStreams = [];
    // The level each application plays at now, by the key its volume is saved under.
    private Dictionary<string, double> _appLiveVolumes = new(StringComparer.OrdinalIgnoreCase);
    // Streams still owed full volume after a reset whose write failed: id to serial.
    private readonly Dictionary<int, int> _appVolumeResets = [];
    private bool _appVolumesChanged;

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
            && !PipeWireAdapter.IsPlumbingIdentity(AppVolumeKey(identity));

    /// <summary>
    /// The key an application's volume is saved under: the one its channel
    /// assignment uses, so both follow the application together.
    /// </summary>
    internal static string AppVolumeKey(string identity) => StreamMatcher.MigrateIdentity(identity);

    /// <summary>
    /// Set an application's playback volume, or clear it with null. The level
    /// is written to the application's playing streams before this returns;
    /// the sweep gives it to streams that start later and repeats a write
    /// that failed. Clearing puts the playing streams back to full volume.
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
            _appVolumes[key] = target;
            _hasAppVolumes = true;
            int budget = AppVolumeWritesPerSweep;
            foreach (AudioStream stream in _appVolumeStreams.Values)
                if (AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase))
                    WriteAppVolumeLocked(stream, key, target, ref budget);
        }
    }

    /// <summary>
    /// True once after a stream's level moved from outside and the saved
    /// level followed it, so the daemon knows to save.
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
        if (!_built || !_hasAppVolumes) return false;
        IReadOnlyList<AudioStream> live;
        try { live = _pw.ListStreams(); }
        catch (InvalidOperationException) { return false; }
        // A graph build holds the gate for seconds; the sweep that follows
        // it does this work.
        if (!Monitor.TryEnter(_gate, 200)) return false;
        try { return _built && SyncAppVolumesLocked(live); }
        finally { Monitor.Exit(_gate); }
    }

    private void ResetAppVolumeLocked(string key)
    {
        if (!_appVolumes.Remove(key)) return;
        _hasAppVolumes = _appVolumes.Count > 0;
        foreach (AudioStream stream in _appVolumeStreams.Values)
        {
            if (!AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            _appVolumeApplied.Remove(stream.Id);
            // Without this the stream keeps the last level, and the session
            // manager restores it to every later stream of the program.
            try { _pw.SetSinkInputVolume(stream.Serial, 1); }
            catch (InvalidOperationException) { _appVolumeResets[stream.Id] = stream.Serial; }
        }
    }

    private void WriteAppVolumeLocked(AudioStream stream, string key, double target, ref int budget, int attempt = 1)
    {
        if (budget <= 0) return;
        budget--;
        _appVolumeResets.Remove(stream.Id);
        long started = Clock();
        try
        {
            _pw.SetSinkInputVolume(stream.Serial, target);
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, target, Clock()) { Attempts = attempt };
        }
        catch (InvalidOperationException)
        {
            // A stalled pipewire-pulse should cost one failed write per
            // pass, so the other streams wait for the next one. A stream it
            // does not list yet is refused at once and tried again sooner.
            long now = Clock();
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, double.NaN, now)
                { Attempts = attempt, RetryAt = now + (now - started < 250 ? 250 : 3000) };
            budget = 0;
        }
    }

    private static bool SameAppVolume(double a, double b) => Math.Abs(a - b) < 0.006;

    private bool SyncAppVolumesLocked(IReadOnlyList<AudioStream> live)
    {
        _appVolumeStreams.Clear();
        var playing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioStream stream in live)
        {
            _appVolumeStreams[stream.Id] = stream;
            if (stream.Volume is double level) playing[AppVolumeKey(stream.Identity)] = level;
        }
        foreach (int gone in _appVolumeApplied.Keys.Where(id => !_appVolumeStreams.ContainsKey(id)).ToArray())
            _appVolumeApplied.Remove(gone);

        int budget = AppVolumeWritesPerSweep;
        foreach ((int id, int serial) in _appVolumeResets.ToArray())
        {
            if (!_appVolumeStreams.TryGetValue(id, out AudioStream? owed) || owed.Serial != serial)
            {
                _appVolumeResets.Remove(id);
                continue;
            }
            if (budget <= 0) continue;
            budget--;
            try
            {
                _pw.SetSinkInputVolume(serial, 1);
                _appVolumeResets.Remove(id);
            }
            catch (InvalidOperationException) { budget = 0; }
        }

        bool adopted = false;
        foreach (AudioStream stream in live)
        {
            string key = AppVolumeKey(stream.Identity);
            if (!_appVolumes.TryGetValue(key, out double target))
            {
                _appVolumeApplied.Remove(stream.Id);
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
            if (applied.RetryAt != 0)
            {
                if (now >= applied.RetryAt) WriteAppVolumeLocked(stream, key, target, ref budget, applied.Attempts);
                continue;
            }
            if (applied.Target != target)
            {
                WriteAppVolumeLocked(stream, key, target, ref budget);
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

        bool moved = playing.Count != _appLiveVolumes.Count
            || playing.Any(p => !_appLiveVolumes.TryGetValue(p.Key, out double before) || before != p.Value);
        _appLiveVolumes = playing;
        return adopted || moved;
    }
}
