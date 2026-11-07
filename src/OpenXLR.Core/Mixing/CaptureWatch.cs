namespace OpenXLR.Core.Mixing;

/// <summary>What one look at a unit's capture decided.</summary>
/// <param name="Recover">Try the recovery now.</param>
/// <param name="Reason">What was seen, for the journal.</param>
/// <param name="Note">Said once when recoveries are given up for a while.</param>
public readonly record struct CaptureVerdict(bool Recover, string? Reason, string? Note);

/// <summary>
/// Watches the capture of a unit that recovers by a USB reset
/// (<see cref="Devices.DeviceCapabilities.CaptureRecovery"/>) while a mix
/// plays into its own sink, which is when the Wave:3 MK.2's capture stalled.
/// The one sign taken is the kernel keeping the capture stopped while
/// PipeWire runs the source, for <see cref="StopWindow"/>; a quiet room or a
/// muted microphone never shows it. Recoveries are spaced by
/// <see cref="RecoverySpacing"/>, and after <see cref="MaxRecoveries"/>
/// within <see cref="RecoveryWindow"/> the watch stops resetting the unit and
/// says so once.
/// </summary>
public sealed class CaptureWatch
{
    internal static readonly TimeSpan StopWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan RecoverySpacing = TimeSpan.FromMinutes(2);
    internal const int MaxRecoveries = 3;
    internal static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(30);

    private readonly Func<long> _clock;
    private long? _stoppedSince;
    private (UnitCaptureProgress Value, long Since)? _stationary;
    private readonly Queue<long> _recoveries = new();
    private bool _gaveUpSaid;

    /// <param name="clock">Milliseconds; tests move it on.</param>
    public CaptureWatch(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>
    /// One sweep's look. <paramref name="playback"/> is null while the unit is
    /// not at risk (no recovery capability, no mix on its own sink), which
    /// starts the count over.
    /// </summary>
    public CaptureVerdict Observe(UnitPlayback? playback, Func<string, CaptureRun> probe)
    {
        long now = _clock();
        if (playback is not { } unit)
        {
            Forget();
            return default;
        }
        CaptureRun run = probe(unit.Source);
        if (!run.NodeRunning || run.KernelRunning != false)
        {
            _stoppedSince = null;
            return default;
        }
        _stoppedSince ??= now;
        if (now - _stoppedSince < (long)StopWindow.TotalMilliseconds) return default;
        string reason = $"the kernel has kept the capture of {unit.Source} stopped for {StopWindow.TotalSeconds:0} seconds while PipeWire runs it and {unit.Sink} plays";
        return TryRecover(now, reason, "reset the unit", "Unplug it and plug it back in, or move the monitor output off its headphones");
    }

    /// <summary>A running capture is stalled only when its hardware position stays unchanged.</summary>
    public CaptureVerdict ObserveProgress(UnitCaptureProgress? progress)
    {
        if (progress is not { Run: { NodeRunning: true, Cursor: not null } } current)
        {
            _stationary = null;
            return default;
        }
        long now = _clock();
        if (_stationary is not { } previous || previous.Value != current)
        {
            _stationary = (current, now);
            return default;
        }
        if (now - previous.Since < (long)StopWindow.TotalMilliseconds) return default;
        string reason = $"the hardware capture position of {current.Source} has not moved for {StopWindow.TotalSeconds:0} seconds";
        return TryRecover(now, reason, "tried reopening the capture", "Unplug the interface and plug it back in");
    }

    private CaptureVerdict TryRecover(long now, string reason, string action, string advice)
    {
        while (_recoveries.Count > 0 && now - _recoveries.Peek() >= (long)RecoveryWindow.TotalMilliseconds) _recoveries.Dequeue();
        if (_recoveries.Count >= MaxRecoveries)
        {
            if (_gaveUpSaid) return default;
            _gaveUpSaid = true;
            return new CaptureVerdict(false, reason,
                $"{reason}; OpenXLR has {action} {MaxRecoveries} times in {RecoveryWindow.TotalMinutes:0} minutes and stops for now. " + advice);
        }
        if (_recoveries.Count > 0 && now - _recoveries.Last() < (long)RecoverySpacing.TotalMilliseconds) return default;
        _recoveries.Enqueue(now);
        _gaveUpSaid = false;
        Forget();
        return new CaptureVerdict(true, reason, null);
    }

    /// <summary>A recovery ran, asked for or not: every count starts over.</summary>
    public void Recovered() => Forget();

    private void Forget()
    {
        _stoppedSince = null;
        _stationary = null;
    }
}
