using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The compressor and the expander on the microphones that carry the EQ:
/// XLR 1, XLR 2 and every capture input on one mono port. Both run on this
/// computer in the same native stage as noise removal, the software EQ and
/// the voice changer, in this order:
///
///   source -> OpenXLR_eq_&lt;id&gt;_&lt;gen&gt; (noise removal, expander, EQ, compressor, voice) -> chain or channel
///
/// They are not the unit's own Compressor and Expander switches, which the
/// device state carries; the two can run together and then stack. Every
/// change goes to a running stage live. A client that shows their levels
/// asks for them with <see cref="WatchDynamics"/> and keeps asking; while it
/// does and the stage runs, the levels go out with the other meters as
/// <c>exp:</c> and <c>comp:</c> and the channel.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What a compressor or expander that cannot run says when this build has no native helper.</summary>
    public const string DynamicsNeedsHost =
        "The compressor and the expander need the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What a helper from before the compressor and expander says, once its stage runs and has no meter of theirs.</summary>
    public const string DynamicsNotBuilt = "This installation's plugin host has no compressor or expander. Update OpenXLR.";

    /// <summary>What the stage the restart policy has given up on says on the compressor and the expander.</summary>
    internal const string DynamicsGivenUp = "the microphone's processing kept failing and is off; switch it off and on to try again";

    /// <summary>How long the levels keep coming after the last request for them.</summary>
    internal static readonly TimeSpan DynamicsMeterLease = TimeSpan.FromSeconds(15);

    // Saved settings by channel. A microphone absent here has the processor
    // off; an entry for any other channel, read from a file, is kept.
    private readonly Dictionary<string, MicCompressor> _compressor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MicExpander> _expander = new(StringComparer.Ordinal);

    // The channels a client shows the levels of, until when; and their
    // stages' helpers for the meter push, which reads them without the lock.
    private readonly Dictionary<string, long> _dynamicsWatch = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NativePluginHost> _dynamicsMeterHosts = new(StringComparer.Ordinal);

    /// <summary>A channel's compressor: what was set or recalled, else off.</summary>
    public MicCompressor CompressorFor(string channel) { lock (_gate) return CompressorLocked(channel); }

    /// <summary>A channel's expander: what was set or recalled, else off.</summary>
    public MicExpander ExpanderFor(string channel) { lock (_gate) return ExpanderLocked(channel); }

    private MicCompressor CompressorLocked(string channel) => _compressor.GetValueOrDefault(channel) ?? MicCompressor.Default;

    private MicExpander ExpanderLocked(string channel) => _expander.GetValueOrDefault(channel) ?? MicExpander.Default;

    /// <summary>
    /// The stage is wanted for the compressor or the expander: one is on. A
    /// client showing their levels does not start it, since a stage in the
    /// route takes the Pro's direct microphone path off.
    /// </summary>
    private bool DynamicsWantedLocked(string channel) => CompressorLocked(channel).Enabled || ExpanderLocked(channel).Enabled;

    /// <summary>Change part of a microphone's compressor. Turning it on clears a stage the restart policy gave up on.</summary>
    public void SetCompressor(string channel, MicCompressorChange change)
    {
        if (change.IsEmpty)
            throw new ArgumentException("setCompressor: need at least one of 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicCompressor was = CompressorLocked(channel);
            MicCompressor next = change.ApplyTo(was);
            if (MicCompressor.Check(next) is string problem) throw new ArgumentException($"setCompressor: {problem}");
            if (next.Enabled && !was.Enabled) _restarts.Forget(EqKey(channel));
            _compressor[channel] = MicCompressor.Snap(next);
            ApplySoftwareEqLocked(channel);
        }
    }

    /// <summary>Change part of a microphone's expander. Turning it on clears a stage the restart policy gave up on.</summary>
    public void SetExpander(string channel, MicExpanderChange change)
    {
        if (change.IsEmpty)
            throw new ArgumentException("setExpander: need at least one of 'enabled', 'threshold', 'ratio', 'range', 'attack', 'release' and 'hold'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicExpander was = ExpanderLocked(channel);
            MicExpander next = change.ApplyTo(was);
            if (MicExpander.Check(next) is string problem) throw new ArgumentException($"setExpander: {problem}");
            if (next.Enabled && !was.Enabled) _restarts.Forget(EqKey(channel));
            _expander[channel] = MicExpander.Snap(next);
            ApplySoftwareEqLocked(channel);
        }
    }

    /// <summary>Store recalled settings, snapped, for the channels the recall names.</summary>
    private void RecallCompressorLocked(IReadOnlyDictionary<string, MicCompressor> recalled)
    {
        foreach ((string channel, MicCompressor compressor) in RecallCompressor(_compressor, recalled))
        {
            if (compressor.Enabled && !CompressorLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _compressor[channel] = compressor;
        }
    }

    /// <summary>Store recalled settings, snapped, for the channels the recall names.</summary>
    private void RecallExpanderLocked(IReadOnlyDictionary<string, MicExpander> recalled)
    {
        foreach ((string channel, MicExpander expander) in RecallExpander(_expander, recalled))
        {
            if (expander.Enabled && !ExpanderLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _expander[channel] = expander;
        }
    }

    /// <summary>
    /// The settings after a recall: each channel the recalled data names takes
    /// its setting, snapped; every other channel keeps what it has, so a
    /// recall without the compressor (null) changes nothing.
    /// </summary>
    internal static Dictionary<string, MicCompressor> RecallCompressor(IReadOnlyDictionary<string, MicCompressor> current,
        IReadOnlyDictionary<string, MicCompressor>? recalled)
    {
        var next = new Dictionary<string, MicCompressor>(current, StringComparer.Ordinal);
        foreach ((string channel, MicCompressor compressor) in recalled ?? new Dictionary<string, MicCompressor>())
            if (MicCompressor.Check(compressor) is null) next[channel] = MicCompressor.Snap(compressor);
        return next;
    }

    /// <summary>The expander settings after a recall, by the rule of <see cref="RecallCompressor"/>.</summary>
    internal static Dictionary<string, MicExpander> RecallExpander(IReadOnlyDictionary<string, MicExpander> current,
        IReadOnlyDictionary<string, MicExpander>? recalled)
    {
        var next = new Dictionary<string, MicExpander>(current, StringComparer.Ordinal);
        foreach ((string channel, MicExpander expander) in recalled ?? new Dictionary<string, MicExpander>())
            if (MicExpander.Check(expander) is null) next[channel] = MicExpander.Snap(expander);
        return next;
    }

    /// <summary>Every microphone's compressor, for a profile: a recall then names every microphone.</summary>
    private Dictionary<string, MicCompressor> SceneCompressorLocked()
        => _config.Channels.Where(CarriesEq).ToDictionary(c => c.Id, c => CompressorLocked(c.Id), StringComparer.Ordinal);

    /// <summary>Every microphone's expander, for a profile.</summary>
    private Dictionary<string, MicExpander> SceneExpanderLocked()
        => _config.Channels.Where(CarriesEq).ToDictionary(c => c.Id, c => ExpanderLocked(c.Id), StringComparer.Ordinal);

    /// <summary>A microphone's compressor for the state: the setting, and why it does not run as asked.</summary>
    private MicCompressorState CompressorStateLocked(string channel)
    {
        MicCompressor c = CompressorLocked(channel);
        return new MicCompressorState(c.Enabled, c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Makeup)
        {
            Available = DynamicsAvailableLocked(channel),
            Error = c.Enabled ? DynamicsErrorLocked(channel) : null,
        };
    }

    /// <summary>A microphone's expander for the state: the setting, and why it does not run as asked.</summary>
    private MicExpanderState ExpanderStateLocked(string channel)
    {
        MicExpander e = ExpanderLocked(channel);
        return new MicExpanderState(e.Enabled, e.Threshold, e.Ratio, e.Range, e.Attack, e.Release, e.Hold)
        {
            Available = DynamicsAvailableLocked(channel),
            Error = e.Enabled ? DynamicsErrorLocked(channel) : null,
        };
    }

    private bool DynamicsAvailableLocked(string channel) => _pw.EqHostInstalled && !_restarts.Blocked(EqKey(channel));

    /// <summary>
    /// Why a compressor or expander that is on does not run: no host, a stage
    /// given up on, a helper from before them, or the stage's own failure.
    /// </summary>
    private string? DynamicsErrorLocked(string channel)
    {
        if (!_pw.EqHostInstalled) return DynamicsNeedsHost;
        if (_restarts.Blocked(EqKey(channel))) return _restarts.Message(EqKey(channel), DynamicsGivenUp);
        NativePluginHost? host = _eqStages.TryGetValue(channel, out SoftwareEqBackend? stage) && stage.IsAlive ? stage.Stage.NativeHost : null;
        IReadOnlyDictionary<string, double> meters = host?.Meters ?? new Dictionary<string, double>();
        // Every stage reports each of its meters once it runs, so one that has
        // reported noise removal's and not the compressor's has neither.
        if (meters.ContainsKey(SoftwareEqBackend.ActiveMeter) && !meters.ContainsKey(SoftwareEqBackend.CompressorReductionMeter))
            return DynamicsNotBuilt;
        return _eqErrors.GetValueOrDefault(channel);
    }

    /// <summary>
    /// Send this microphone's compressor and expander levels for another
    /// <see cref="DynamicsMeterLease"/>. They go out with the other meters as
    /// <c>exp:</c> and <c>comp:</c> and the channel while the microphone's
    /// stage runs, which needs something in it on; nothing about the route
    /// changes. Never saved.
    /// </summary>
    public void WatchDynamics(string channel)
    {
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            if (!_built) throw new InvalidOperationException("The compressor and expander levels need the software mixer to be running.");
            _dynamicsWatch[channel] = Stopwatch.GetTimestamp() + (long)(DynamicsMeterLease.TotalSeconds * Stopwatch.Frequency);
            FollowDynamicsMetersLocked(channel);
        }
    }

    /// <summary>The channels whose compressor and expander levels go out now.</summary>
    public IReadOnlyCollection<string> DynamicsMeterChannels { get { lock (_gate) return [.. _dynamicsWatch.Keys]; } }

    /// <summary>
    /// Switch a channel's stage meters on while a client watches it and off
    /// once none does, and keep the meter push pointed at the stage that runs.
    /// </summary>
    private void FollowDynamicsMetersLocked(string channel)
    {
        bool watched = _dynamicsWatch.ContainsKey(channel);
        if (!_eqStages.TryGetValue(channel, out SoftwareEqBackend? stage) || !stage.IsAlive)
        {
            _dynamicsMeterHosts.TryRemove(channel, out _);
            return;
        }
        try
        {
            stage.SetDynamicsMeters(watched);
        }
        catch (InvalidOperationException ex)
        {
            _pw.Note($"The compressor and expander levels on {channel} could not be switched: {ex.Message}");
        }
        if (watched) _dynamicsMeterHosts[channel] = stage.Stage.NativeHost!;
        else _dynamicsMeterHosts.TryRemove(channel, out _);
    }

    /// <summary>The sweep's part: end leases that ran out, and follow a stage that was replaced.</summary>
    private void EnsureDynamicsMetersLocked()
    {
        long now = Stopwatch.GetTimestamp();
        foreach ((string channel, long until) in _dynamicsWatch.ToList())
            if (now >= until || !CarriesEqLocked(channel)) _dynamicsWatch.Remove(channel);
        foreach (string channel in _dynamicsWatch.Keys.Union(_dynamicsMeterHosts.Keys).ToList())
            FollowDynamicsMetersLocked(channel);
    }

    /// <summary>
    /// Each watched microphone's levels as <c>exp:</c> and <c>comp:</c> and the
    /// channel, each <c>[input, reduction, output]</c> in dB: the highest
    /// input and output peak in dBFS, -120 for silence, and the most gain
    /// reduction, 0 or more, since the stage's last report. Absent while the
    /// stage does not run or its helper has no compressor.
    /// </summary>
    private void AddDynamicsLevels(Dictionary<string, double[]> meters)
    {
        foreach ((string channel, NativePluginHost host) in _dynamicsMeterHosts)
        {
            if (!host.IsHealthy) continue;
            IReadOnlyDictionary<string, double> values = host.Meters;
            if (!values.ContainsKey(SoftwareEqBackend.CompressorReductionMeter)) continue;
            meters[$"exp:{channel}"] = DynamicsLevels(values, SoftwareEqBackend.ExpanderInputMeter,
                SoftwareEqBackend.ExpanderReductionMeter, SoftwareEqBackend.ExpanderOutputMeter);
            meters[$"comp:{channel}"] = DynamicsLevels(values, SoftwareEqBackend.CompressorInputMeter,
                SoftwareEqBackend.CompressorReductionMeter, SoftwareEqBackend.CompressorOutputMeter);
        }
    }

    internal static double[] DynamicsLevels(IReadOnlyDictionary<string, double> values, string input, string reduction, string output)
        => [Level(values.GetValueOrDefault(input, -120)), Math.Round(Math.Clamp(values.GetValueOrDefault(reduction), 0, 120), 1),
            Level(values.GetValueOrDefault(output, -120))];

    private static double Level(double dbfs) => double.IsFinite(dbfs) ? Math.Round(Math.Clamp(dbfs, -120, 24), 1) : -120;
}
