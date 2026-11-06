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
/// of a microphone's route, which runs the microphone's noise removal and
/// expander ahead of the EQ (native/denoise.c, native/dynamics.c) and its
/// compressor and voice changer after it (native/dynamics.c,
/// native/voice.c). Values go down the helper's command pipe; the helper
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

    /// <summary>
    /// The stage's compressor and expander meters, each in dB: the highest
    /// input and output peak and the most gain reduction since the helper's
    /// last tick. They move only while <see cref="SetDynamicsMeters"/> has
    /// switched them on; a helper from before the two has none of them.
    /// </summary>
    internal const string CompressorInputMeter = "comp_input", CompressorReductionMeter = "comp_reduction",
        CompressorOutputMeter = "comp_output", ExpanderInputMeter = "exp_input", ExpanderReductionMeter = "exp_reduction",
        ExpanderOutputMeter = "exp_output";

    internal static readonly IReadOnlySet<string> Meters = new HashSet<string>(
        [ActiveMeter, StatusMeter, LateMeter, NoiseLatencyMeter, VoiceActiveMeter, VoiceStatusMeter, VoiceLatencyMeter,
            CompressorInputMeter, CompressorReductionMeter, CompressorOutputMeter,
            ExpanderInputMeter, ExpanderReductionMeter, ExpanderOutputMeter],
        StringComparer.Ordinal);

    /// <summary>
    /// A delay meter in milliseconds at the stage's rate, or null when the
    /// stage has not reported it or the part adds nothing.
    /// </summary>
    internal static double? MeterMilliseconds(IReadOnlyDictionary<string, double> meters, string meter, int sampleRate)
        => meters.TryGetValue(meter, out double samples) && double.IsFinite(samples) && samples > 0 && sampleRate > 0
            ? Math.Round(samples * 1000.0 / sampleRate, 1) : null;

    // The voice changer's, the compressor's and the expander's controls are
    // never part of the stage's start: a helper from before them refuses to
    // start with a control it does not declare, which would take noise
    // removal and the EQ down with it. The stage starts on their own
    // defaults, which are the default settings', and a setting is written
    // live once it runs.
    private readonly Dictionary<string, double> _written = Startup(applied, noise ?? MicNoise.Default);
    private MicEq _applied = applied;

    public string Kind => "software";
    public FilterHandle Stage { get; } = stage;
    /// <summary>The node, whose playback port takes the microphone and whose capture port carries it on.</summary>
    public string Node => Stage.SinkName;
    public bool IsAlive => Stage.IsAlive;
    public int BandCount => _applied.Bands.Count;

    /// <summary>
    /// The channels the stage runs on: one for a microphone, two for a
    /// capture input's stereo pair, whose stage has the EQ alone and is never
    /// written another part's controls.
    /// </summary>
    public int Channels { get; init; } = 1;

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

    /// <summary>
    /// The helper's controls for the compressor, by the symbols
    /// native/dynamics.c declares, the switch last so switching on starts on
    /// the setting asked for.
    /// </summary>
    public static Dictionary<string, double> Controls(MicCompressor compressor) => new(StringComparer.Ordinal)
    {
        ["comp_threshold"] = compressor.Threshold,
        ["comp_ratio"] = compressor.Ratio,
        ["comp_attack"] = compressor.Attack,
        ["comp_release"] = compressor.Release,
        ["comp_knee"] = compressor.Knee,
        ["comp_makeup"] = compressor.Makeup,
        ["comp_enabled"] = compressor.Enabled ? 1 : 0,
    };

    /// <summary>The helper's controls for the expander, by the symbols native/dynamics.c declares, the switch last.</summary>
    public static Dictionary<string, double> Controls(MicExpander expander) => new(StringComparer.Ordinal)
    {
        ["exp_threshold"] = expander.Threshold,
        ["exp_ratio"] = expander.Ratio,
        ["exp_range"] = expander.Range,
        ["exp_attack"] = expander.Attack,
        ["exp_release"] = expander.Release,
        ["exp_hold"] = expander.Hold,
        ["exp_enabled"] = expander.Enabled ? 1 : 0,
    };

    /// <summary>The switches that make the stage report the compressor's and the expander's levels.</summary>
    internal const string CompressorMetersControl = "comp_meters", ExpanderMetersControl = "exp_meters";

    /// <summary>
    /// What a stage holds once started: the values it was given, and the
    /// voice changer's, the compressor's and the expander's defaults, with
    /// their meters off.
    /// </summary>
    private static Dictionary<string, double> Startup(MicEq eq, MicNoise noise)
    {
        Dictionary<string, double> controls = Controls(eq, noise);
        foreach ((string symbol, double value) in Controls(MicVoice.Default)) controls[symbol] = value;
        foreach ((string symbol, double value) in Controls(MicCompressor.Default)) controls[symbol] = value;
        foreach ((string symbol, double value) in Controls(MicExpander.Default)) controls[symbol] = value;
        controls[CompressorMetersControl] = 0;
        controls[ExpanderMetersControl] = 0;
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

    /// <summary>
    /// The microphone's compressor, live. Off, only the switch is written, and
    /// only when it was on, so a helper without one is never sent a control
    /// it lacks.
    /// </summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void ApplyCompressor(MicCompressor compressor)
        => Write(compressor.Enabled ? Controls(compressor) : new Dictionary<string, double>(StringComparer.Ordinal) { ["comp_enabled"] = 0 });

    /// <summary>The microphone's expander, live, written the way <see cref="ApplyCompressor"/> writes.</summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void ApplyExpander(MicExpander expander)
        => Write(expander.Enabled ? Controls(expander) : new Dictionary<string, double>(StringComparer.Ordinal) { ["exp_enabled"] = 0 });

    /// <summary>
    /// Switch the compressor's and the expander's level meters on while a
    /// client shows them, and off again, so the helper prints them only then.
    /// Written only while a client asks, so a helper from before the two is
    /// sent nothing it lacks unless someone opens their window.
    /// </summary>
    /// <exception cref="InvalidOperationException">The helper did not take a value.</exception>
    public void SetDynamicsMeters(bool on)
        => Write(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [CompressorMetersControl] = on ? 1 : 0,
            [ExpanderMetersControl] = on ? 1 : 0,
        });

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
