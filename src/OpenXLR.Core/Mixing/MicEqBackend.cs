using System.Globalization;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// What runs one microphone's EQ. There is one model and, in time, two
/// backends: <see cref="SoftwareEqBackend"/> in the native helper, and a
/// device backend for an interface whose own DSP runs the EQ, written once
/// that hardware's protocol is known. Nothing about the device one is
/// guessed here.
/// </summary>
internal interface IMicEqBackend
{
    /// <summary>"software" or "device", as the state reports it.</summary>
    string Kind { get; }
    /// <summary>The whole EQ, at start and on setEq or a recall.</summary>
    void Apply(MicEq eq);
    /// <summary>One existing band, live.</summary>
    void ApplyBand(int band, EqBand value);
    void SetEnabled(bool on);
    /// <summary>The input stops using this backend.</summary>
    void Release();
}

/// <summary>
/// The EQ in the native helper's <c>eq</c> backend: a mono node at the head
/// of a microphone's route, which runs the microphone's noise removal ahead
/// of the EQ (native/denoise.c) and its voice changer after it
/// (native/voice.c). Values go down the helper's command pipe; the helper
/// takes them at its next block, keeping its filter state, so a change spawns
/// nothing and builds no node. Only the controls that changed are written.
/// </summary>
internal sealed class SoftwareEqBackend(PipeWireAdapter pw, FilterHandle stage, MicEq applied, MicNoise? noise = null) : IMicEqBackend
{
    /// <summary>
    /// The stage's noise removal meters: the engine that plays (-1 none,
    /// 0 RNNoise, 1 DeepFilterNet), why one asked for does not, how many
    /// blocks DeepFilterNet answered late, and the delay in samples it adds.
    /// </summary>
    internal const string ActiveMeter = "denoise_active", StatusMeter = "denoise_status", LateMeter = "denoise_late",
        NoiseLatencyMeter = "denoise_latency";

    /// <summary>
    /// The stage's voice changer meters: the voice that plays (-1 none, else
    /// its number in <see cref="MicVoice.Voices"/>), why one asked for
    /// does not, and the delay in samples it adds.
    /// </summary>
    internal const string VoiceActiveMeter = "voice_active", VoiceStatusMeter = "voice_status", VoiceLatencyMeter = "voice_latency";

    internal static readonly IReadOnlySet<string> Meters = new HashSet<string>(
        [ActiveMeter, StatusMeter, LateMeter, NoiseLatencyMeter, VoiceActiveMeter, VoiceStatusMeter, VoiceLatencyMeter],
        StringComparer.Ordinal);

    /// <summary>
    /// A delay meter in milliseconds at the stage's rate, or null when the
    /// stage has not reported it or the part adds nothing.
    /// </summary>
    internal static double? MeterMilliseconds(IReadOnlyDictionary<string, double> meters, string meter, int sampleRate)
        => meters.TryGetValue(meter, out double samples) && double.IsFinite(samples) && samples > 0 && sampleRate > 0
            ? Math.Round(samples * 1000.0 / sampleRate, 1) : null;

    // The voice changer's controls are never part of the stage's start: a
    // helper from before the voice changer refuses to start with a control it
    // does not declare, which would take noise removal and the EQ down with
    // it. The stage starts on the voice changer's own defaults, which are
    // the default setting's, and a voice is written live once it runs.
    private readonly Dictionary<string, double> _written = Startup(applied, noise ?? MicNoise.Default);
    private MicEq _applied = applied;

    public string Kind => "software";
    public FilterHandle Stage { get; } = stage;
    /// <summary>The node, whose playback port takes the microphone and whose capture port carries it on.</summary>
    public string Node => Stage.SinkName;
    public bool IsAlive => Stage.IsAlive;
    public int BandCount => _applied.Bands.Count;

    /// <summary>The helper's controls for an EQ, by the symbols native/eq.c declares.</summary>
    public static Dictionary<string, double> Controls(MicEq eq)
    {
        var controls = new Dictionary<string, double>(StringComparer.Ordinal) { ["enabled"] = eq.Enabled ? 1 : 0 };
        for (int i = 0; i < eq.Bands.Count; i++)
        {
            EqBand band = eq.Bands[i];
            string prefix = "band" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_";
            controls[prefix + "enabled"] = band.Enabled ? 1 : 0;
            controls[prefix + "type"] = MicEq.TypeIndex(band.Type);
            controls[prefix + "frequency"] = band.Frequency;
            controls[prefix + "gain"] = band.Gain;
            controls[prefix + "qlog10"] = MicEq.QLog10(band.Q);
        }
        return controls;
    }

    /// <summary>The helper's controls for noise removal, by the symbols native/denoise.c declares.</summary>
    public static Dictionary<string, double> Controls(MicNoise noise) => new(StringComparer.Ordinal)
    {
        ["denoise_enabled"] = noise.Enabled ? 1 : 0,
        ["denoise_engine"] = Math.Max(0, MicNoise.EngineIndex(noise.Engine)),
        ["denoise_strength"] = noise.Strength,
    };

    /// <summary>Every control of the stage the daemon starts it with: the EQ's and noise removal's.</summary>
    public static Dictionary<string, double> Controls(MicEq eq, MicNoise noise)
    {
        Dictionary<string, double> controls = Controls(eq);
        foreach ((string symbol, double value) in Controls(noise)) controls[symbol] = value;
        return controls;
    }

    /// <summary>
    /// The helper's controls for the voice changer, by the symbols
    /// native/voice.c declares, in the order they are written: the voice
    /// before the switch, so switching on starts on the voice asked for.
    /// </summary>
    public static Dictionary<string, double> Controls(MicVoice voice) => new(StringComparer.Ordinal)
    {
        ["voice_preset"] = Math.Max(0, MicVoice.VoiceIndex(voice.Voice)),
        ["voice_pitch"] = voice.Pitch,
        ["voice_formant"] = voice.Formant,
        ["voice_enabled"] = voice.Enabled ? 1 : 0,
    };

    /// <summary>What a stage holds once started: the values it was given, and the voice changer's defaults.</summary>
    private static Dictionary<string, double> Startup(MicEq eq, MicNoise noise)
    {
        Dictionary<string, double> controls = Controls(eq, noise);
        foreach ((string symbol, double value) in Controls(MicVoice.Default)) controls[symbol] = value;
        return controls;
    }

    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void Apply(MicEq eq)
    {
        Write(Controls(eq));
        _applied = eq;
    }

    /// <summary>The microphone's noise removal, live.</summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void ApplyNoise(MicNoise noise) => Write(Controls(noise));

    /// <summary>
    /// The microphone's voice changer, live. Off, only the switch is written,
    /// and only when it was on: a voice changer that was never switched on
    /// writes nothing, so a helper without one is never sent a control it
    /// lacks.
    /// </summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void ApplyVoice(MicVoice voice)
        => Write(voice.Enabled ? Controls(voice) : new Dictionary<string, double>(StringComparer.Ordinal) { ["voice_enabled"] = 0 });

    private void Write(Dictionary<string, double> controls)
    {
        foreach ((string symbol, double value) in controls)
        {
            if (_written.TryGetValue(symbol, out double was) && was == value) continue;
            Stage.NativeHost!.SetControl(symbol, value);
            _written[symbol] = value;
        }
    }

    public void ApplyBand(int band, EqBand value) => Apply(_applied.WithBand(band, value));

    public void SetEnabled(bool on) => Apply(_applied with { Enabled = on });

    public void Release() => pw.StopFilter(Stage);
}
