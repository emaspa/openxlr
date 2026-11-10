namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private const int AppVolumeWritesPerSweep = 8;
    // How long one pass may go on starting writes. With the helper's own
    // limit, PipeWireAdapter.ApplicationStreamVolumeTimeout, a slow sound
    // server holds the mixer lock for half a second at most; the sweep
    // writes what is left.
    private const int AppVolumeWriteWindowMs = 250;
    // A write the sound server refused at once is tried again after the
    // first wait, one that timed out or was refused twice after the second.
    private const int AppVolumeRetryMs = 250;
    private const int AppVolumeBackOffMs = 3000;
    // Reads of the registry a pass makes when a command or another pass
    // acted after its read, before it leaves the work to the sweep.
    private const int AppVolumeReads = 3;
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
    // Raised under the lock by every change to what the level work knows: a
    // command, a pass, restored settings, a teardown. A registry read taken
    // before a raise can show a stream at a level that is no longer current.
    private long _appVolumeRevision;
    // What was last written to each playback stream, by registry id.
    private readonly Dictionary<int, AppliedAppVolume> _appVolumeApplied = [];
    // The playback streams of the last pass, so a command reaches them at once.
    private readonly Dictionary<int, AudioStream> _appVolumeStreams = [];
    // The level each application plays at now, by the key its volume is saved under.
    private Dictionary<string, double> _appLiveVolumes = new(StringComparer.OrdinalIgnoreCase);
    // Streams still owed full volume after a reset whose write failed or
    // did not fit in the pass, by registry id.
    private readonly Dictionary<int, PendingAppVolumeReset> _appVolumeResets = [];
    private int _appVolumesChanged;

    private sealed record AppliedAppVolume(int Serial, string Key, double Target, double Written, long At)
    {
        public bool Confirmed { get; init; }
        public int Attempts { get; init; } = 1;
        /// <summary>When a write the sound server refused is tried again; 0 after one it took.</summary>
        public long RetryAt { get; init; }
    }

    /// <summary>A reset still owed to a stream, by its serial, and when it may be written.</summary>
    private sealed record PendingAppVolumeReset(int Serial, long RetryAt, bool Refused);

    /// <summary>
    /// The writes one pass may make: a fixed number, started within
    /// <see cref="AppVolumeWriteWindowMs"/> of the pass's start.
    /// </summary>
    private struct AppVolumeWriteBudget(long start)
    {
        private int _remaining = AppVolumeWritesPerSweep;

        public bool TryUse(long now)
        {
            if (_remaining == 0 || now - start >= AppVolumeWriteWindowMs) return false;
            _remaining--;
            return true;
        }
    }

    public const int MaxAppVolumeIdentityLength = 256;

    // An identity whose key is empty, such as " (deleted)", would save a
    // level under no application at all.
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
    /// is written to the application's playing streams before this returns,
    /// as many as one pass's budget allows; the sweep writes the rest, gives
    /// the level to streams that start later and repeats a write that failed.
    /// Clearing puts the playing streams back to full volume.
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
            _appVolumeRevision++;
            var budget = new AppVolumeWriteBudget(Clock());
            foreach (AudioStream stream in _appVolumeStreams.Values)
                if (AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase))
                    WriteAppVolumeLocked(stream, key, target, ref budget);
        }
    }

    /// <summary>
    /// True once after a stream's level moved from outside and the saved
    /// level followed it, so the daemon knows to save. Consuming this signal
    /// does not wait for a busy mixer.
    /// </summary>
    public bool TakeAppVolumesChanged() => Interlocked.Exchange(ref _appVolumesChanged, 0) != 0;

    /// <summary>
    /// Give playback streams their application's saved level without waiting
    /// for the sweep. Called when the registry reports a new stream, so the
    /// stream plays at the level the session manager restored for as short a
    /// time as a helper takes to start. True when the state clients see changed.
    /// </summary>
    public bool SyncAppVolumes() => SyncAppVolumes(read =>
    {
        // The registry subscription is always current; a one-shot dump kept
        // for a moment could predate what made the last read stale.
        if (read > 0) _pw.InvalidateDump();
        return _pw.ListStreams();
    });

    /// <summary>
    /// <see cref="SyncAppVolumes()"/> with the registry read supplied;
    /// <paramref name="read"/> gets the number of reads made before it.
    /// </summary>
    internal bool SyncAppVolumes(Func<int, IReadOnlyList<AudioStream>> read)
    {
        if (!_built || !_hasAppVolumes) return false;
        for (int attempt = 0; attempt < AppVolumeReads; attempt++)
        {
            long revision = Interlocked.Read(ref _appVolumeRevision);
            IReadOnlyList<AudioStream> live;
            try { live = read(attempt); }
            catch (InvalidOperationException) { return false; }
            // A graph build holds the gate for seconds; the sweep that follows
            // it does this work.
            if (!Monitor.TryEnter(_gate, 200)) return false;
            try
            {
                if (!_built) return false;
                // A command or another pass acted after the read, and the read
                // can show a level older than the one it set or followed.
                // Read again rather than drop the pass: the stream that
                // started it still gets its level now, not on the next sweep.
                if (revision == _appVolumeRevision) return SyncAppVolumesLocked(live);
            }
            finally { Monitor.Exit(_gate); }
        }
        return false;
    }

    private void ResetAppVolumeLocked(string key)
    {
        if (!_appVolumes.Remove(key)) return;
        _hasAppVolumes = _appVolumes.Count > 0;
        _appVolumeRevision++;
        var budget = new AppVolumeWriteBudget(Clock());
        foreach (AudioStream stream in _appVolumeStreams.Values)
        {
            if (!AppVolumeKey(stream.Identity).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            _appVolumeApplied.Remove(stream.Id);
            // Without this the stream keeps the last level, and the session
            // manager restores it to every later stream of the program.
            ResetStreamVolumeLocked(stream.Id, stream.Serial, ref budget, refused: false);
        }
    }

    private void ResetStreamVolumeLocked(int id, int serial, ref AppVolumeWriteBudget budget, bool refused)
    {
        long started = Clock();
        if (!budget.TryUse(started))
        {
            _appVolumeResets[id] = new(serial, 0, refused);
            return;
        }
        try
        {
            _pw.SetApplicationStreamVolume(serial, 1);
            _appVolumeResets.Remove(id);
        }
        catch (InvalidOperationException)
        {
            long now = Clock();
            _appVolumeResets[id] = new(serial, now + RetryDelay(now - started, refused), Refused: true);
        }
    }

    // A refusal that took the helper's whole time means the sound server is
    // stalled; one repeated means this stream is refused for good. Either way
    // the stream waits longer, so it does not take every pass's first write.
    private static long RetryDelay(long took, bool refusedBefore)
        => took >= AppVolumeWriteWindowMs || refusedBefore ? AppVolumeBackOffMs : AppVolumeRetryMs;

    private void WriteAppVolumeLocked(AudioStream stream, string key, double target, ref AppVolumeWriteBudget budget, int attempt = 1)
    {
        _appVolumeResets.Remove(stream.Id);
        long started = Clock();
        if (!budget.TryUse(started)) return;
        try
        {
            _pw.SetApplicationStreamVolume(stream.Serial, target);
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, target, Clock()) { Attempts = attempt };
        }
        catch (InvalidOperationException)
        {
            // A stream pipewire-pulse does not list yet is refused at once
            // and tried again soon. The rest of the pass goes on: a stall has
            // used up its time already.
            long now = Clock();
            bool refusedBefore = _appVolumeApplied.TryGetValue(stream.Id, out AppliedAppVolume? previous)
                && previous.RetryAt != 0 && previous.Serial == stream.Serial
                && previous.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && previous.Target == target;
            _appVolumeApplied[stream.Id] = new(stream.Serial, key, target, double.NaN, now)
                { Attempts = attempt, RetryAt = now + RetryDelay(now - started, refusedBefore) };
        }
    }

    /// <summary>Forget every stream: after a teardown their ids and serials may belong to others.</summary>
    private void ClearAppVolumeStreamsLocked()
    {
        _appVolumeApplied.Clear();
        _appVolumeStreams.Clear();
        _appVolumeResets.Clear();
        _appLiveVolumes = new(StringComparer.OrdinalIgnoreCase);
        _appVolumeRevision++;
    }

    private static bool SameAppVolume(double a, double b) => Math.Abs(a - b) < 0.006;

    private bool SyncAppVolumesLocked(IReadOnlyList<AudioStream> live)
    {
        _appVolumeRevision++;
        _appVolumeStreams.Clear();
        var playing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioStream stream in live)
        {
            _appVolumeStreams[stream.Id] = stream;
            if (stream.Volume is double level) playing[AppVolumeKey(stream.Identity)] = level;
        }
        foreach (int gone in _appVolumeApplied.Keys.Where(id => !_appVolumeStreams.ContainsKey(id)).ToArray())
            _appVolumeApplied.Remove(gone);

        var budget = new AppVolumeWriteBudget(Clock());
        foreach ((int id, PendingAppVolumeReset owed) in _appVolumeResets.ToArray())
        {
            if (!_appVolumeStreams.TryGetValue(id, out AudioStream? stream) || stream.Serial != owed.Serial)
            {
                _appVolumeResets.Remove(id);
                continue;
            }
            if (Clock() >= owed.RetryAt) ResetStreamVolumeLocked(id, owed.Serial, ref budget, owed.Refused);
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
            Volatile.Write(ref _appVolumesChanged, 1);
            adopted = true;
        }

        bool moved = playing.Count != _appLiveVolumes.Count
            || playing.Any(p => !_appLiveVolumes.TryGetValue(p.Key, out double before) || before != p.Value);
        _appLiveVolumes = playing;
        return adopted || moved;
    }
}
