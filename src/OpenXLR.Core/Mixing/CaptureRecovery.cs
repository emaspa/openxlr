namespace OpenXLR.Core.Mixing;

/// <summary>
/// The driven unit's capture and its own sink while a mix plays into that
/// sink: what the daemon's capture watch looks at.
/// </summary>
public readonly record struct UnitPlayback(string Source, string Sink);

/// <summary>The driven unit's source and its current capture position.</summary>
public readonly record struct UnitCaptureProgress(string Source, CaptureRun Run);

/// <summary>The driven unit's capture source and how many of its port links were dropped and made again.</summary>
public readonly record struct UnitRelink(string Source, int Links);

/// <summary>What a capture recovery needs from the mixer; the <see cref="Mixer"/> is one, tests fake it.</summary>
public interface ICaptureRecoveryMixer
{
    /// <inheritdoc cref="Mixer.DrivenUnitPlayback"/>
    UnitPlayback? DrivenUnitPlayback();
    /// <inheritdoc cref="Mixer.ProbeCapture"/>
    CaptureRun ProbeCapture(string source);
    /// <inheritdoc cref="Mixer.DrivenCaptureProgress"/>
    UnitCaptureProgress? DrivenCaptureProgress();
    /// <inheritdoc cref="Mixer.ReopenDrivenCapture"/>
    bool? ReopenDrivenCapture(UnitCaptureProgress expected);
    /// <inheritdoc cref="Mixer.RelinkDrivenUnitCapture"/>
    UnitRelink? RelinkDrivenUnitCapture();
    /// <inheritdoc cref="Mixer.BeginCaptureRecovery"/>
    int? BeginCaptureRecovery();
    /// <inheritdoc cref="Mixer.EndCaptureRecovery"/>
    void EndCaptureRecovery();
    /// <inheritdoc cref="Mixer.CaptureRecoveryActive"/>
    bool CaptureRecoveryActive { get; }
}

/// <summary>
/// Capture first, and the way back when a unit's capture stalls under its
/// own playback. The hold that keeps a route out of a unit's sink until its
/// capture runs lives in <c>HoldPlaybackLocked</c>; this part probes the
/// capture and takes a unit's playback off while the daemon resets it.
/// </summary>
public sealed partial class Mixer : ICaptureRecoveryMixer
{
    /// <summary>
    /// How long the routes into a unit's sink wait for its capture after a
    /// USB reset before they are made anyway. The Wave:3 MK.2 came back from a
    /// reset with its capture running in about twelve seconds.
    /// </summary>
    internal static readonly TimeSpan CaptureRecoveryLimit = TimeSpan.FromSeconds(30);

    /// <summary>The capture probe; tests replace it. Null asks the graph and the kernel (<see cref="PipeWireAdapter.CaptureRunOf"/>).</summary>
    internal Func<string, CaptureRun>? CaptureProbe { get; init; }

    private CaptureRun ProbeCaptureLocked(string source) => CaptureProbe is { } probe ? probe(source) : _pw.CaptureRunOf(source);

    /// <summary>A capture source as the graph and the kernel show it now (see <see cref="CaptureRun"/>).</summary>
    public CaptureRun ProbeCapture(string source)
    {
        lock (_gate) return ProbeCaptureLocked(source);
    }

    /// <summary>The live USB capture of the driven unit, while its own playback is off.</summary>
    public UnitCaptureProgress? DrivenCaptureProgress()
    {
        lock (_gate) return DrivenCaptureProgressLocked();
    }

    private UnitCaptureProgress? DrivenCaptureProgressLocked()
    {
        if (!_built || _soundCheck is not null || _inputDevice is not { } source ||
            !source.StartsWith("alsa_input.usb-", StringComparison.Ordinal) ||
            !_inputHints.Any(hint => hint.Length > 0 && source.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
            !_config.Channels.Any(c => c.InputPair is not null && CarriesMicrophoneStage(c) && !_bindings.ContainsKey(c.Id)
                && _inputFeeds.GetValueOrDefault(c.Id) is { Pairs.Count: > 0 }) ||
            UnitRoutesLocked(InterfaceNodes.CardKey(source)).Count > 0)
            return null;
        CaptureRun run = _pw.CaptureRunOf(source, readCursor: true);
        return run.Cursor is null ? null : new UnitCaptureProgress(source, run);
    }

    /// <summary>Reopen a still-stalled capture. Null if the source, cursor or playback state changed.</summary>
    public bool? ReopenDrivenCapture(UnitCaptureProgress expected)
    {
        lock (_gate)
            return DrivenCaptureProgressLocked() == expected ? _pw.ReopenCaptureSource(expected.Source) : null;
    }

    /// <summary>The source node a strip is fed from now, or before it is wired the driven unit's.</summary>
    private string? CaptureSourceLocked(string strip)
        => _stripSources.GetValueOrDefault(strip) ?? (_bindings.ContainsKey(strip) ? null : _inputDevice);

    // The unit whose playback is off while it recovers: its card, the id its
    // capture source had when the recovery began, and when it began.
    private sealed record Recovery(string Card, int? Node, long Since);
    private Recovery? _captureRecovery;

    /// <summary>Whether a unit's playback is held off while its capture recovers.</summary>
    public bool CaptureRecoveryActive { get { lock (_gate) return _captureRecovery is not null; } }

    /// <summary>
    /// The driven unit's capture source and the sink of it that a monitor
    /// route plays into, while both are linked; null otherwise.
    /// </summary>
    public UnitPlayback? DrivenUnitPlayback()
    {
        lock (_gate)
        {
            if (!_built || _inputDevice is not { } source) return null;
            if (!_config.Channels.Any(c => c.InputPair is not null && !_bindings.ContainsKey(c.Id) && _inputFeeds.ContainsKey(c.Id)))
                return null;
            string? sink = UnitRoutesLocked(InterfaceNodes.CardKey(source)).Select(r => r.Target).FirstOrDefault();
            return sink is null ? null : new UnitPlayback(source, StripMarker(sink));
        }
    }

    /// <summary>The monitor routes that play into a card's sinks, with pairs linked.</summary>
    private List<(string Key, string Target)> UnitRoutesLocked(string card)
        => [.. MonitorRouteTargetsLocked().Where(r => StripMarker(r.Target) is var bare
                && bare.StartsWith("alsa_output.", StringComparison.Ordinal) && InterfaceNodes.CardKey(bare) == card
                && _monitorRoutes.GetValueOrDefault(r.Key) is { Pairs.Count: > 0 })];

    /// <summary>
    /// Take every mix off the driven unit's own sink and keep it off until
    /// the unit's capture comes back under a new node and runs, which a USB
    /// reset of the unit brings about, or until <see cref="CaptureRecoveryLimit"/>
    /// has passed. The number of routes taken off; null when no unit is
    /// driven.
    /// </summary>
    public int? BeginCaptureRecovery()
    {
        lock (_gate)
        {
            if (!_built || _inputDevice is not { } source) return null;
            string card = InterfaceNodes.CardKey(source);
            List<(string Key, string Target)> routes = UnitRoutesLocked(card);
            foreach ((string key, _) in routes)
            {
                _pw.Unlink(_monitorRoutes[key]);
                _monitorRoutes[key] = new PortLink([]);
            }
            _captureRecovery = new Recovery(card, ProbeCaptureLocked(source).NodeId, Clock());
            ReleasePlaybackHoldLocked(card);
            return routes.Count;
        }
    }

    /// <summary>
    /// Drop every link the mixer made out of the driven unit's capture
    /// source (into its strips, an EQ stage at a strip's head, a capture
    /// input on the same card) and make each again at once. The playback
    /// into the unit is left alone, so the headphones keep playing. On the
    /// maintainer's Wave:3 MK.2 a buzz on the capture stopped the moment its
    /// link into the strip was deleted, which a USB reset never did. A pair
    /// that cannot be made again is left to the sweep, which heals the feed.
    /// Null when no unit is driven or the mixer is not built.
    /// </summary>
    public UnitRelink? RelinkDrivenUnitCapture()
    {
        lock (_gate)
        {
            if (!_built || _inputDevice is not { } source) return null;
            string prefix = source + ":";
            List<(string From, string To)> pairs = [.. _inputFeeds.Values.Concat(_eqInputs.Values).Concat(_captureFeeds.Values)
                .SelectMany(l => l.Pairs).Where(p => p.From.StartsWith(prefix, StringComparison.Ordinal)).Distinct()];
            return new UnitRelink(source, _pw.Relink(pairs));
        }
    }

    /// <summary>Stop holding the unit's playback for a recovery, as when its reset failed.</summary>
    public void EndCaptureRecovery()
    {
        lock (_gate) _captureRecovery = null;
    }
}
