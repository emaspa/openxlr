using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

// Shared with the UI and the terminal mixer, which offer the same voices,
// range and steps the daemon takes.
#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// A microphone's voice changer: an on switch, the voice, and the pitch and
/// formant shift the custom voice uses. It runs last in the native helper's
/// microphone stage, after noise removal and the EQ, on XLR 1, XLR 2 and
/// every capture input on one mono port. Off by default, so an upgrade
/// changes nobody's sound.
/// </summary>
public sealed record MicVoice
{
    /// <summary>The voice whose pitch and formant come from the setting rather than a preset.</summary>
    public const string Custom = "custom";

    /// <summary>The voices, in the order the native stage numbers them (native/voice.c).</summary>
    public static readonly IReadOnlyList<string> Voices =
        [Custom, "deep", "high", "robot", "radio", "alien", "dracula", "frankenstein", "mummy", "werewolf", "witch"];

    /// <summary>The voice a fresh setting holds, the one the native stage starts on.</summary>
    public const string DefaultVoice = "deep";

    /// <summary>Pitch and formant shift, in semitones, on half-semitone steps.</summary>
    public const double MinShift = -12, MaxShift = 12, ShiftStep = 0.5;

    public bool Enabled { get; init; }
    /// <summary>One of <see cref="Voices"/>.</summary>
    public string Voice { get; init; } = DefaultVoice;
    /// <summary>The custom voice's pitch shift in semitones; the presets carry their own.</summary>
    public double Pitch { get; init; }
    /// <summary>The custom voice's formant shift in semitones; the presets carry their own.</summary>
    public double Formant { get; init; }

    /// <summary>Off, on the deep voice, no custom shift.</summary>
    public static MicVoice Default { get; } = new();

    /// <summary>The native stage's number for a voice, or -1 for an unknown one.</summary>
    public static int VoiceIndex(string? voice)
    {
        for (int i = 0; i < Voices.Count; i++)
            if (Voices[i] == voice) return i;
        return -1;
    }

    public static double SnapShift(double semitones) => Math.Round(semitones / ShiftStep, MidpointRounding.AwayFromZero) * ShiftStep;

    public static MicVoice Snap(MicVoice voice) => voice with { Pitch = SnapShift(voice.Pitch), Formant = SnapShift(voice.Formant) };

    /// <summary>
    /// Why a setting cannot be taken, or null. Values outside the range are
    /// refused, never clamped, and the reason is written the same way
    /// whatever the daemon's locale.
    /// </summary>
    public static string? Check(MicVoice? voice)
    {
        if (voice is null) return "need 'enabled', 'voice', 'pitch' and 'formant'";
        if (VoiceIndex(voice.Voice) < 0) return $"voice must be one of {string.Join(", ", Voices)}";
        if (!InRange(voice.Pitch)) return string.Create(CultureInfo.InvariantCulture, $"pitch must be {MinShift} to {MaxShift} semitones");
        if (!InRange(voice.Formant)) return string.Create(CultureInfo.InvariantCulture, $"formant must be {MinShift} to {MaxShift} semitones");
        return null;
    }

    private static bool InRange(double semitones) => double.IsFinite(semitones) && semitones is >= MinShift and <= MaxShift;

    /// <summary>
    /// Read a whole setting from an object with <c>enabled</c>, <c>voice</c>,
    /// <c>pitch</c> and <c>formant</c>, as a state push holds it. Null with
    /// <paramref name="error"/> set when it cannot be taken.
    /// </summary>
    public static MicVoice? Read(JsonElement json, out string? error)
    {
        error = null;
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !json.TryGetProperty("voice", out JsonElement voice) || voice.ValueKind != JsonValueKind.String
            || !json.TryGetProperty("pitch", out JsonElement pitch) || pitch.ValueKind != JsonValueKind.Number || !pitch.TryGetDouble(out double p)
            || !json.TryGetProperty("formant", out JsonElement formant) || formant.ValueKind != JsonValueKind.Number || !formant.TryGetDouble(out double f))
        {
            error = "need 'enabled', 'voice', 'pitch' and 'formant'";
            return null;
        }
        var value = new MicVoice { Enabled = enabled.GetBoolean(), Voice = voice.GetString()!, Pitch = p, Formant = f };
        if (Check(value) is string problem)
        {
            error = problem;
            return null;
        }
        return Snap(value);
    }
}

/// <summary>
/// Part of a voice changer setting, as <c>setVoiceChanger</c> carries it:
/// each field present replaces the setting's, each absent one keeps it.
/// </summary>
public sealed record MicVoiceChange(bool? Enabled = null, string? Voice = null, double? Pitch = null, double? Formant = null)
{
    public bool IsEmpty => Enabled is null && Voice is null && Pitch is null && Formant is null;

    /// <summary>The setting with this change, unsnapped; check and snap the result.</summary>
    public MicVoice ApplyTo(MicVoice voice) => voice with
    {
        Enabled = Enabled ?? voice.Enabled,
        Voice = Voice ?? voice.Voice,
        Pitch = Pitch ?? voice.Pitch,
        Formant = Formant ?? voice.Formant,
    };
}

/// <summary>
/// A microphone's voice changer as clients see it: the setting, the voice
/// heard now (null while none runs), whether the voice changer can run at
/// all, and why what was asked for does not run.
/// </summary>
public sealed record MicVoiceState(bool Enabled, string Voice, double Pitch, double Formant)
{
    /// <summary>The voice whose output plays now, or null.</summary>
    public string? Active { get; init; }

    /// <summary>The delay the running voice adds in milliseconds, or null while none runs.</summary>
    public double? LatencyMs { get; init; }

    /// <summary>The native helper is installed, so the voice changer can be switched on.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }
}
