namespace OpenXLR.Daemon;

/// <summary>
/// The mixer settings writer, debounced. It persists a moment after the last
/// change, so dragging a fader writes once instead of on every pixel of
/// travel. A failed write (a full disk, a read-only home) stays pending and
/// is retried with backoff, and the reason is reported once per distinct
/// message so clients can say the change would not survive a restart.
///
/// The last rule is the important one at shutdown: once <see cref="Close"/>
/// has run, no later write may happen. The graph is torn down right after the
/// final save, and a torn-down mixer exports empty levels, mutes and monitor
/// routing, so a debounced tick still in flight, or the flush in Dispose,
/// would otherwise replace the user's settings with that empty state.
/// </summary>
internal sealed class SettingsSaver : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Func<string?> _write;   // export and save; null on success, else the reason
    private readonly Action<string?> _report;
    private readonly Action? _changed;
    private Timer? _debounce;
    private bool _dirty;
    private bool _closed;
    private TimeSpan _retry = SaveDelay;
    private string? _error;

    /// <param name="write">Export the current settings and save them; null on success.</param>
    /// <param name="report">Called when the failure reason changes, outside the gate.</param>
    /// <param name="changed">Called when clients should see the new failure state, outside the gate.</param>
    public SettingsSaver(Func<string?> write, Action<string?> report, Action? changed = null)
    {
        _write = write;
        _report = report;
        _changed = changed;
    }

    /// <summary>Why the settings are not on disk, or null while they are.</summary>
    public string? Error { get { lock (_gate) return _error; } }

    /// <summary>Whether a change is waiting to be written.</summary>
    public bool Pending { get { lock (_gate) return _dirty; } }

    /// <summary>Whether the writer is closed and will not write again.</summary>
    public bool Closed { get { lock (_gate) return _closed; } }

    /// <summary>Note a change and start the debounce.</summary>
    public void Schedule()
    {
        lock (_gate)
        {
            if (_closed) return;
            _dirty = true;
            _debounce ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Run a change that writes the settings itself (the layout commands save
    /// under this gate and succeed only once the file is there), then treat
    /// the settings as written: nothing is pending and the debounce is off.
    /// The change runs under the gate, so no debounced write can interleave.
    /// </summary>
    public void RunSaved(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        bool cleared = false;
        lock (_gate)
        {
            change();
            _dirty = false;
            _retry = SaveDelay;
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
            if (_error is not null) { _error = null; cleared = true; }
        }
        if (cleared) _changed?.Invoke();
    }

    /// <summary>Write a pending change now. Called by the debounce and by Dispose.</summary>
    public void Flush()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_closed || !_dirty) return;
            string? error = _write();
            if (error is null)
            {
                _dirty = false;
                _retry = SaveDelay;
                if (_error is not null) { _report(null); _error = null; changed = true; }
            }
            else
            {
                if (error != _error) { _report(error); _error = error; changed = true; }
                _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetryDelay.Ticks));
                _debounce?.Change(_retry, Timeout.InfiniteTimeSpan);
            }
        }
        if (changed) _changed?.Invoke();
    }

    /// <summary>
    /// The final write, and the end of writing. The debounce is stopped first
    /// so no tick can run against a mixer that is about to be torn down, the
    /// pending change is written while the graph is still up, and every later
    /// call is refused. Returns the reason the last write failed, or null.
    /// </summary>
    public string? Close(bool write)
    {
        lock (_gate)
        {
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
            string? error = write && !_closed ? _write() : null;
            _dirty = false;
            _closed = true;
            return error;
        }
    }

    public void Dispose()
    {
        Timer? debounce;
        lock (_gate) debounce = _debounce;
        debounce?.Dispose();
        Flush();
    }
}
