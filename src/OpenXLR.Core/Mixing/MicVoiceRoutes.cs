namespace OpenXLR.Core.Mixing;

/// <summary>
/// The voice changer on the microphones that carry the EQ: XLR 1, XLR 2 and
/// every capture input on one mono port. It runs last in the same native
/// stage as noise removal and the software EQ, so the EQ shapes the human
/// voice and the character goes on after it:
///
///   source -> OpenXLR_eq_&lt;id&gt;_&lt;gen&gt; (noise removal, EQ, voice) -> chain or channel
///
/// The stage at a microphone's head is wanted while any of the three is on.
/// Every change goes to a running stage live: the switch, the voice, and the
/// custom voice's pitch and formant are controls of the stage, which
/// crossfades between voices itself. A microphone with none of the three on
/// when its route is built gets no stage.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What a voice changer that cannot run says when this build has no native helper.</summary>
    public const string VoiceNeedsHost =
        "The voice changer needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What a helper from before the voice changer says, once its stage runs and has no voice meter.</summary>
    public const string VoiceNotBuilt = "This installation's plugin host has no voice changer. Update OpenXLR.";

    /// <summary>What the stage the restart policy has given up on says on the voice changer.</summary>
    internal const string VoiceGivenUp = "the voice changer kept failing and is off; switch it off and on to try again";

    // Saved settings by channel. A microphone absent here has the voice
    // changer off; an entry for any other channel, read from a file, is kept.
    private readonly Dictionary<string, MicVoice> _voice = new(StringComparer.Ordinal);

    /// <summary>A channel's voice changer: what was set or recalled, else off.</summary>
    public MicVoice VoiceFor(string channel) { lock (_gate) return VoiceLocked(channel); }

    private MicVoice VoiceLocked(string channel) => _voice.GetValueOrDefault(channel) ?? MicVoice.Default;

    /// <summary>
    /// Change part of a microphone's voice changer. Turning it on clears a
    /// stage the restart policy gave up on.
    /// </summary>
    public void SetVoiceChanger(string channel, MicVoiceChange change)
    {
        if (change.IsEmpty) throw new ArgumentException("setVoiceChanger: need at least one of 'enabled', 'voice', 'pitch' and 'formant'");
        lock (_gate)
        {
            RequireMicrophoneLocked(channel);
            MicVoice was = VoiceLocked(channel);
            MicVoice next = change.ApplyTo(was);
            if (MicVoice.Check(next) is string problem) throw new ArgumentException($"setVoiceChanger: {problem}");
            if (next.Enabled && !was.Enabled) _restarts.Forget(EqKey(channel));
            _voice[channel] = MicVoice.Snap(next);
            ApplySoftwareEqLocked(channel);
        }
    }

    /// <summary>Store recalled settings, snapped, for the channels the recall names.</summary>
    private void RecallVoiceLocked(IReadOnlyDictionary<string, MicVoice> recalled)
    {
        foreach ((string channel, MicVoice voice) in RecallVoice(_voice, recalled))
        {
            if (voice.Enabled && !VoiceLocked(channel).Enabled) _restarts.Forget(EqKey(channel));
            _voice[channel] = voice;
        }
    }

    /// <summary>
    /// The settings after a recall: each channel the recalled data names takes
    /// its setting, snapped; every other channel keeps what it has, so a
    /// recall without the voice changer (null) changes nothing.
    /// </summary>
    internal static Dictionary<string, MicVoice> RecallVoice(IReadOnlyDictionary<string, MicVoice> current,
        IReadOnlyDictionary<string, MicVoice>? recalled)
    {
        var next = new Dictionary<string, MicVoice>(current, StringComparer.Ordinal);
        foreach ((string channel, MicVoice voice) in recalled ?? new Dictionary<string, MicVoice>())
            if (MicVoice.Check(voice) is null) next[channel] = MicVoice.Snap(voice);
        return next;
    }

    /// <summary>Every microphone's voice changer, for a profile: a recall then names every microphone.</summary>
    private Dictionary<string, MicVoice> SceneVoiceLocked()
        => _config.Channels.Where(CarriesEq).ToDictionary(c => c.Id, c => VoiceLocked(c.Id), StringComparer.Ordinal);

    /// <summary>
    /// A microphone's voice changer for the state: the setting, the voice
    /// heard now, the delay it adds and why what was asked for does not run.
    /// The running stage reports its voice, its status and its delay as meters.
    /// </summary>
    private MicVoiceState VoiceStateLocked(string channel)
    {
        MicVoice voice = VoiceLocked(channel);
        bool installed = _pw.EqHostInstalled;
        bool blocked = _restarts.Blocked(EqKey(channel));
        NativePluginHost? host = _eqStages.TryGetValue(channel, out SoftwareEqBackend? stage) && stage.IsAlive ? stage.Stage.NativeHost : null;
        IReadOnlyDictionary<string, double> meters = host?.Meters ?? new Dictionary<string, double>();
        string? active = ActiveVoice(meters);
        double? latency = active is null || host is null ? null
            : SoftwareEqBackend.MeterMilliseconds(meters, SoftwareEqBackend.VoiceLatencyMeter, host.SampleRate);
        string? error = null;
        if (voice.Enabled)
        {
            // Every stage reports each of its meters once it runs, so one that
            // has reported noise removal's and not the voice changer's has none.
            bool lacking = meters.ContainsKey(SoftwareEqBackend.ActiveMeter) && !meters.ContainsKey(SoftwareEqBackend.VoiceActiveMeter);
            error = !installed ? VoiceNeedsHost
                : blocked ? _restarts.Message(EqKey(channel), VoiceGivenUp)
                : lacking ? VoiceNotBuilt
                : VoiceStatusText(meters.GetValueOrDefault(SoftwareEqBackend.VoiceStatusMeter)) ?? _eqErrors.GetValueOrDefault(channel);
        }
        return new MicVoiceState(voice.Enabled, voice.Voice, voice.Pitch, voice.Formant)
        {
            Active = active,
            LatencyMs = latency,
            Available = installed && !blocked,
            Error = error,
        };
    }

    /// <summary>The voice the stage's <c>voice_active</c> meter names, or null for none or a number this daemon does not know.</summary>
    internal static string? ActiveVoice(IReadOnlyDictionary<string, double> meters)
    {
        if (!meters.TryGetValue(SoftwareEqBackend.VoiceActiveMeter, out double value) || !double.IsFinite(value)) return null;
        int index = (int)Math.Round(value);
        return index >= 0 && index < MicVoice.Voices.Count ? MicVoice.Voices[index] : null;
    }

    // What each running stage last said about its voice changer, so the sweep
    // tells clients when a voice starts, stops or changes, and its delay.
    private readonly Dictionary<string, (double Active, double Status, double Latency)> _voiceReports = new(StringComparer.Ordinal);

    /// <summary>The sweep's part: true when a stage's voice or status changed since the last sweep.</summary>
    private bool TrackVoiceReportsLocked()
    {
        bool changed = false;
        foreach ((string channel, SoftwareEqBackend stage) in _eqStages)
        {
            IReadOnlyDictionary<string, double> meters = (stage.IsAlive ? stage.Stage.NativeHost : null)?.Meters ?? new Dictionary<string, double>();
            var report = (meters.GetValueOrDefault(SoftwareEqBackend.VoiceActiveMeter, -1), meters.GetValueOrDefault(SoftwareEqBackend.VoiceStatusMeter),
                meters.GetValueOrDefault(SoftwareEqBackend.VoiceLatencyMeter));
            if (_voiceReports.TryGetValue(channel, out var was) && was == report) continue;
            _voiceReports[channel] = report;
            changed = true;
        }
        foreach (string gone in _voiceReports.Keys.Where(k => !_eqStages.ContainsKey(k)).ToArray())
        {
            _voiceReports.Remove(gone);
            changed = true;
        }
        return changed;
    }

    /// <summary>The sentence for the stage's <c>voice_status</c> meter (native/voice.c), or null when it runs.</summary>
    internal static string? VoiceStatusText(double status) => (int)status switch
    {
        1 => "The voice changer could not start. Switch it off and on to try again.",
        2 => "The voice changer could not keep up on this computer. Switch it off and on to try again, or choose another voice.",
        _ => null,
    };
}
