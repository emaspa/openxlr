namespace OpenXLR.Core.Mixing;

/// <summary>
/// Noise removal on the microphones that carry the EQ: XLR 1, XLR 2 and
/// every capture input on one mono port. It runs in the same native stage as
/// the software EQ, ahead of it, so the stage at a microphone's head is
/// wanted while either is on:
///
///   source -> OpenXLR_eq_&lt;id&gt;_&lt;gen&gt; (noise removal, then EQ) -> chain or channel
///
/// Every change goes to a running stage live: the switch, the engine and the
/// strength are controls of the stage, which fades between engines itself and
/// loads DeepFilterNet's model in the background. Where the unit runs the EQ,
/// the stage runs noise removal alone with its own EQ off. A microphone with
/// neither on when its route is built gets no stage.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What noise removal that cannot run says when this build has no native helper.</summary>
    public const string NoiseNeedsHost =
        "Noise removal needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What the stage the restart policy has given up on says on noise removal.</summary>
    internal const string NoiseGivenUp = "noise removal kept failing and is off; switch it off and on to try again";

    // Saved settings by channel. A microphone absent here has noise removal
    // off; an entry for any other channel, read from a file, is kept.
    private readonly Dictionary<string, MicNoise> _noise = new(StringComparer.Ordinal);

    /// <summary>A channel's noise removal: what was set or recalled, else off.</summary>
    public MicNoise NoiseFor(string channel) { lock (_gate) return NoiseLocked(channel); }

    private MicNoise NoiseLocked(string channel) => _noise.GetValueOrDefault(channel) ?? MicNoise.Default;

    /// <summary>
    /// Whether a route built now would put the microphone stage at the
    /// channel's head: the channel carries the EQ, and its software EQ, its
    /// noise removal or its voice changer is on.
    /// </summary>
    private bool StageWantedLocked(string channel)
        => CarriesEqLocked(channel)
            && (SoftwareEqLocked(channel).Enabled || NoiseLocked(channel).Enabled || VoiceLocked(channel).Enabled);

    /// <summary>The engines this installation's helper carries, in <see cref="MicNoise.Engines"/> order.</summary>
    public IReadOnlyList<string> NoiseEngines => _pw.NoiseEngines;

    /// <summary>
    /// Change part of a microphone's noise removal. An engine this
    /// installation's helper lacks is refused; turning noise removal on clears
    /// a stage the restart policy gave up on.
    /// </summary>
    public void SetNoiseRemoval(string channel, MicNoiseChange change)
    {
        if (change.IsEmpty) throw new ArgumentException("setNoiseRemoval: need at least one of 'enabled', 'engine' and 'strength'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicNoise was = NoiseLocked(channel);
            MicNoise next = change.ApplyTo(was);
            if (MicNoise.Check(next) is string problem) throw new ArgumentException($"setNoiseRemoval: {problem}");
            if (change.Engine is string engine && engine != was.Engine && !_pw.NoiseEngines.Contains(engine))
                throw new ArgumentException(NoiseEngineMissing(engine));
            if (next.Enabled && !was.Enabled) _restarts.Forget(EqKey(channel));
            _noise[channel] = MicNoise.Snap(next);
            ApplySoftwareEqLocked(channel);
        }
    }

    /// <summary>What an engine the helper was built without says.</summary>
    public static string NoiseEngineMissing(string engine)
        => $"setNoiseRemoval: this installation's plugin host was built without {EngineName(engine)}";

    private static string EngineName(string engine) => engine == MicNoise.DeepFilterNet ? "DeepFilterNet" : "RNNoise";

    /// <summary>Store recalled settings, snapped, for the channels the recall names.</summary>
    private void RecallNoiseLocked(IReadOnlyDictionary<string, MicNoise> recalled)
    {
        foreach ((string channel, MicNoise noise) in RecallNoise(_noise, recalled))
        {
            if (noise.Enabled && !NoiseLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _noise[channel] = noise;
        }
    }

    /// <summary>
    /// The settings after a recall: each channel the recalled data names takes
    /// its setting, snapped; every other channel keeps what it has, so a
    /// recall without noise removal (null) changes nothing.
    /// </summary>
    internal static Dictionary<string, MicNoise> RecallNoise(IReadOnlyDictionary<string, MicNoise> current,
        IReadOnlyDictionary<string, MicNoise>? recalled)
    {
        var next = new Dictionary<string, MicNoise>(current, StringComparer.Ordinal);
        foreach ((string channel, MicNoise noise) in recalled ?? new Dictionary<string, MicNoise>())
            if (MicNoise.Check(noise) is null) next[channel] = MicNoise.Snap(noise);
        return next;
    }

    /// <summary>Every microphone's noise removal, for a profile: a recall then names every microphone.</summary>
    private Dictionary<string, MicNoise> SceneNoiseLocked()
        => _config.Channels.Where(CarriesEq).ToDictionary(c => c.Id, c => NoiseLocked(c.Id), StringComparer.Ordinal);

    /// <summary>
    /// A microphone's noise removal for the state: the setting, what runs, the
    /// delay it adds and why what was asked for does not run. The running
    /// stage reports its engine, its status and its latency as meters.
    /// </summary>
    private MicNoiseState NoiseStateLocked(string channel)
    {
        MicNoise noise = NoiseLocked(channel);
        bool installed = _pw.EqHostInstalled;
        IReadOnlyList<string> engines = installed ? _pw.NoiseEngines : [];
        bool blocked = _restarts.Blocked(EqKey(channel));
        NativePluginHost? host = _eqStages.TryGetValue(channel, out SoftwareEqBackend? stage) && stage.IsAlive ? stage.Stage.NativeHost : null;
        IReadOnlyDictionary<string, double> meters = host?.Meters ?? new Dictionary<string, double>();
        string? active = meters.GetValueOrDefault(SoftwareEqBackend.ActiveMeter, -1) switch
        {
            0 => MicNoise.RnNoise,
            1 => MicNoise.DeepFilterNet,
            _ => null,
        };
        double? latency = active is null || host is null ? null : NoiseLatency(meters, host);
        string? error = null;
        if (noise.Enabled)
        {
            error = !installed ? NoiseNeedsHost
                : blocked ? _restarts.Message(EqKey(channel), NoiseGivenUp)
                : !engines.Contains(noise.Engine) && engines.Count > 0 ? $"This installation's plugin host was built without {EngineName(noise.Engine)}."
                : NoiseStatusText(noise.Engine, meters.GetValueOrDefault(SoftwareEqBackend.StatusMeter), host?.SampleRate)
                    ?? _eqErrors.GetValueOrDefault(channel);
        }
        return new MicNoiseState(noise.Enabled, noise.Engine, noise.Strength)
        {
            Engines = engines,
            Active = active,
            LatencyMs = latency,
            Available = installed && !blocked,
            Error = error,
        };
    }

    /// <summary>
    /// Noise removal's own delay. The stage's latency also holds the voice
    /// changer's, so it stands in only for a host from before the stage
    /// reported each part.
    /// </summary>
    private static double? NoiseLatency(IReadOnlyDictionary<string, double> meters, NativePluginHost host)
        => meters.ContainsKey(SoftwareEqBackend.NoiseLatencyMeter)
            ? SoftwareEqBackend.MeterMilliseconds(meters, SoftwareEqBackend.NoiseLatencyMeter, host.SampleRate)
            : host.LatencyMilliseconds is double ms && ms > 0 ? Math.Round(ms, 1) : null;

    // What each running stage last said about its noise removal, so the sweep
    // tells clients when an engine starts, stops or changes its delay.
    private readonly Dictionary<string, (double Active, double Status, double? Latency)> _noiseReports = new(StringComparer.Ordinal);

    /// <summary>The sweep's part: true when a stage's engine, status or delay changed since the last sweep.</summary>
    private bool TrackNoiseReportsLocked()
    {
        bool changed = false;
        foreach ((string channel, SoftwareEqBackend stage) in _eqStages)
        {
            NativePluginHost? host = stage.IsAlive ? stage.Stage.NativeHost : null;
            IReadOnlyDictionary<string, double> meters = host?.Meters ?? new Dictionary<string, double>();
            var report = (meters.GetValueOrDefault(SoftwareEqBackend.ActiveMeter, -1), meters.GetValueOrDefault(SoftwareEqBackend.StatusMeter),
                host is null ? null : NoiseLatency(meters, host));
            if (_noiseReports.TryGetValue(channel, out var was) && was == report) continue;
            _noiseReports[channel] = report;
            changed = true;
        }
        foreach (string gone in _noiseReports.Keys.Where(k => !_eqStages.ContainsKey(k)).ToArray())
        {
            _noiseReports.Remove(gone);
            changed = true;
        }
        return changed;
    }

    /// <summary>The sentence for the stage's <c>denoise_status</c> meter (native/denoise.h), or null when it runs.</summary>
    internal static string? NoiseStatusText(string engine, double status, int? graphRate) => (int)status switch
    {
        1 => graphRate is int rate
            ? $"Noise removal runs at 48 kHz, and the audio graph runs at {rate} Hz. It passes the microphone through untouched."
            : "Noise removal runs at 48 kHz, and the audio graph runs at another rate. It passes the microphone through untouched.",
        2 => $"This installation's plugin host was built without {EngineName(engine)}.",
        3 => $"{EngineName(engine)} could not start. Switch it off and on to try again, or choose the other engine.",
        4 => $"{EngineName(engine)} stopped or could not keep up on this computer and is off. Switch it off and on to try again, or choose the other engine.",
        _ => null,
    };
}
