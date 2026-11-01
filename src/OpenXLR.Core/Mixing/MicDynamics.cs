using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

// Shared with the UI and the terminal mixer, which offer the same ranges and
// steps the daemon takes.
#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// One number of a compressor or expander setting: its name in the API, its
/// range, the step it is snapped onto and its unit.
/// </summary>
public sealed record DynamicsRange(string Name, double Minimum, double Maximum, double Step, double Default, string Unit)
{
    public double Snap(double value) => Math.Round(Math.Round(value / Step, MidpointRounding.AwayFromZero) * Step, 3);

    public bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;

    /// <summary>The refusal for a value outside the range, written the same way whatever the locale.</summary>
    public string Problem => string.Create(CultureInfo.InvariantCulture, $"{Name} must be {Minimum} to {Maximum} {Unit}");
}

/// <summary>
/// A microphone's compressor on this computer: an on switch, the threshold
/// above which it turns the voice down, the ratio, how fast it reacts and
/// recovers, the soft knee and the make-up gain after it. It runs in the
/// native helper's microphone stage after the EQ and before the voice
/// changer, on XLR 1, XLR 2 and every capture input on one mono port. It is
/// not the unit's own Compressor switch. Off by default, so an upgrade
/// changes nobody's sound.
/// </summary>
public sealed record MicCompressor
{
    public static readonly DynamicsRange ThresholdRange = new("threshold", -60, 0, 1, -18, "dB");
    public static readonly DynamicsRange RatioRange = new("ratio", 1, 20, 0.1, 3, "to 1");
    public static readonly DynamicsRange AttackRange = new("attack", 0.1, 200, 0.1, 5, "ms");
    public static readonly DynamicsRange ReleaseRange = new("release", 10, 2000, 1, 150, "ms");
    public static readonly DynamicsRange KneeRange = new("knee", 0, 24, 1, 6, "dB");
    public static readonly DynamicsRange MakeupRange = new("makeup", 0, 24, 0.5, 0, "dB");

    public bool Enabled { get; init; }
    /// <summary>dBFS: the level above which the voice is turned down.</summary>
    public double Threshold { get; init; } = ThresholdRange.Default;
    /// <summary>How many dB over the threshold go in for each dB that comes out.</summary>
    public double Ratio { get; init; } = RatioRange.Default;
    /// <summary>Milliseconds to reach 63 percent of a new, larger reduction.</summary>
    public double Attack { get; init; } = AttackRange.Default;
    /// <summary>Milliseconds to recover 63 percent of the way once the voice is quieter.</summary>
    public double Release { get; init; } = ReleaseRange.Default;
    /// <summary>dB: the width of the soft knee around the threshold, 0 for a hard one.</summary>
    public double Knee { get; init; } = KneeRange.Default;
    /// <summary>dB added after the compressor.</summary>
    public double Makeup { get; init; } = MakeupRange.Default;

    /// <summary>Off, -18 dB, 3 to 1, 5 ms, 150 ms, a 6 dB knee, no make-up gain.</summary>
    public static MicCompressor Default { get; } = new();

    public static MicCompressor Snap(MicCompressor c) => c with
    {
        Threshold = ThresholdRange.Snap(c.Threshold),
        Ratio = RatioRange.Snap(c.Ratio),
        Attack = AttackRange.Snap(c.Attack),
        Release = ReleaseRange.Snap(c.Release),
        Knee = KneeRange.Snap(c.Knee),
        Makeup = MakeupRange.Snap(c.Makeup),
    };

    /// <summary>Why a setting cannot be taken, or null. Values outside a range are refused, never clamped.</summary>
    public static string? Check(MicCompressor? c)
    {
        if (c is null) return Fields;
        foreach ((DynamicsRange range, double value) in new[]
        {
            (ThresholdRange, c.Threshold), (RatioRange, c.Ratio), (AttackRange, c.Attack),
            (ReleaseRange, c.Release), (KneeRange, c.Knee), (MakeupRange, c.Makeup),
        })
            if (!range.Contains(value)) return range.Problem;
        return null;
    }

    internal const string Fields = "need 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'";

    /// <summary>Read a whole setting from an object, as a state push holds it. Null with <paramref name="error"/> set when it cannot be taken.</summary>
    public static MicCompressor? Read(JsonElement json, out string? error)
    {
        error = null;
        if (!DynamicsJson.Bool(json, "enabled", out bool enabled)
            || !DynamicsJson.Number(json, "threshold", out double threshold) || !DynamicsJson.Number(json, "ratio", out double ratio)
            || !DynamicsJson.Number(json, "attack", out double attack) || !DynamicsJson.Number(json, "release", out double release)
            || !DynamicsJson.Number(json, "knee", out double knee) || !DynamicsJson.Number(json, "makeup", out double makeup))
        {
            error = Fields;
            return null;
        }
        var value = new MicCompressor
        {
            Enabled = enabled, Threshold = threshold, Ratio = ratio, Attack = attack, Release = release, Knee = knee, Makeup = makeup,
        };
        if (Check(value) is string problem)
        {
            error = problem;
            return null;
        }
        return Snap(value);
    }
}

/// <summary>
/// A microphone's expander on this computer: below the threshold it turns
/// the microphone down further, by the ratio and at most by the range, so
/// room noise and breaths between words drop away. Attack is how fast it
/// opens when the voice comes back, hold how long it waits once the voice
/// stops, release how fast it then closes. It runs in the native helper's
/// microphone stage after noise removal and before the EQ. It is not the
/// unit's own Expander switch. Off by default.
/// </summary>
public sealed record MicExpander
{
    public static readonly DynamicsRange ThresholdRange = new("threshold", -80, 0, 1, -50, "dB");
    public static readonly DynamicsRange RatioRange = new("ratio", 1, 20, 0.1, 3, "to 1");
    public static readonly DynamicsRange RangeRange = new("range", 0, 80, 1, 24, "dB");
    public static readonly DynamicsRange AttackRange = new("attack", 0.1, 200, 0.1, 1, "ms");
    public static readonly DynamicsRange ReleaseRange = new("release", 10, 2000, 1, 150, "ms");
    public static readonly DynamicsRange HoldRange = new("hold", 0, 500, 1, 50, "ms");

    public bool Enabled { get; init; }
    /// <summary>dBFS: below this level the microphone is turned down.</summary>
    public double Threshold { get; init; } = ThresholdRange.Default;
    /// <summary>How many dB come out for each dB the input falls below the threshold.</summary>
    public double Ratio { get; init; } = RatioRange.Default;
    /// <summary>dB: the most it turns the microphone down.</summary>
    public double Range { get; init; } = RangeRange.Default;
    /// <summary>Milliseconds to open 63 percent of the way when the voice returns.</summary>
    public double Attack { get; init; } = AttackRange.Default;
    /// <summary>Milliseconds to close 63 percent of the way once the hold has passed.</summary>
    public double Release { get; init; } = ReleaseRange.Default;
    /// <summary>Milliseconds the input must stay below the threshold before it starts closing.</summary>
    public double Hold { get; init; } = HoldRange.Default;

    /// <summary>Off, -50 dB, 3 to 1, 24 dB range, 1 ms, 150 ms, 50 ms hold.</summary>
    public static MicExpander Default { get; } = new();

    public static MicExpander Snap(MicExpander e) => e with
    {
        Threshold = ThresholdRange.Snap(e.Threshold),
        Ratio = RatioRange.Snap(e.Ratio),
        Range = RangeRange.Snap(e.Range),
        Attack = AttackRange.Snap(e.Attack),
        Release = ReleaseRange.Snap(e.Release),
        Hold = HoldRange.Snap(e.Hold),
    };

    /// <summary>Why a setting cannot be taken, or null. Values outside a range are refused, never clamped.</summary>
    public static string? Check(MicExpander? e)
    {
        if (e is null) return Fields;
        foreach ((DynamicsRange range, double value) in new[]
        {
            (ThresholdRange, e.Threshold), (RatioRange, e.Ratio), (RangeRange, e.Range),
            (AttackRange, e.Attack), (ReleaseRange, e.Release), (HoldRange, e.Hold),
        })
            if (!range.Contains(value)) return range.Problem;
        return null;
    }

    internal const string Fields = "need 'enabled', 'threshold', 'ratio', 'range', 'attack', 'release' and 'hold'";

    /// <summary>Read a whole setting from an object, as a state push holds it. Null with <paramref name="error"/> set when it cannot be taken.</summary>
    public static MicExpander? Read(JsonElement json, out string? error)
    {
        error = null;
        if (!DynamicsJson.Bool(json, "enabled", out bool enabled)
            || !DynamicsJson.Number(json, "threshold", out double threshold) || !DynamicsJson.Number(json, "ratio", out double ratio)
            || !DynamicsJson.Number(json, "range", out double range) || !DynamicsJson.Number(json, "attack", out double attack)
            || !DynamicsJson.Number(json, "release", out double release) || !DynamicsJson.Number(json, "hold", out double hold))
        {
            error = Fields;
            return null;
        }
        var value = new MicExpander
        {
            Enabled = enabled, Threshold = threshold, Ratio = ratio, Range = range, Attack = attack, Release = release, Hold = hold,
        };
        if (Check(value) is string problem)
        {
            error = problem;
            return null;
        }
        return Snap(value);
    }
}

internal static class DynamicsJson
{
    public static bool Bool(JsonElement json, string name, out bool value)
    {
        value = false;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out JsonElement e)
            || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = e.GetBoolean();
        return true;
    }

    public static bool Number(JsonElement json, string name, out double value)
    {
        value = 0;
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out JsonElement e)
            && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value);
    }
}

/// <summary>
/// Part of a compressor setting, as <c>setCompressor</c> carries it: each
/// field present replaces the setting's, each absent one keeps it.
/// </summary>
public sealed record MicCompressorChange(bool? Enabled = null, double? Threshold = null, double? Ratio = null,
    double? Attack = null, double? Release = null, double? Knee = null, double? Makeup = null)
{
    public bool IsEmpty => Enabled is null && Threshold is null && Ratio is null && Attack is null && Release is null
        && Knee is null && Makeup is null;

    /// <summary>The setting with this change, unsnapped; check and snap the result.</summary>
    public MicCompressor ApplyTo(MicCompressor c) => c with
    {
        Enabled = Enabled ?? c.Enabled,
        Threshold = Threshold ?? c.Threshold,
        Ratio = Ratio ?? c.Ratio,
        Attack = Attack ?? c.Attack,
        Release = Release ?? c.Release,
        Knee = Knee ?? c.Knee,
        Makeup = Makeup ?? c.Makeup,
    };
}

/// <summary>
/// Part of an expander setting, as <c>setExpander</c> carries it: each field
/// present replaces the setting's, each absent one keeps it.
/// </summary>
public sealed record MicExpanderChange(bool? Enabled = null, double? Threshold = null, double? Ratio = null,
    double? Range = null, double? Attack = null, double? Release = null, double? Hold = null)
{
    public bool IsEmpty => Enabled is null && Threshold is null && Ratio is null && Range is null && Attack is null
        && Release is null && Hold is null;

    /// <summary>The setting with this change, unsnapped; check and snap the result.</summary>
    public MicExpander ApplyTo(MicExpander e) => e with
    {
        Enabled = Enabled ?? e.Enabled,
        Threshold = Threshold ?? e.Threshold,
        Ratio = Ratio ?? e.Ratio,
        Range = Range ?? e.Range,
        Attack = Attack ?? e.Attack,
        Release = Release ?? e.Release,
        Hold = Hold ?? e.Hold,
    };
}

/// <summary>
/// A microphone's compressor as clients see it: the setting, whether it can
/// run at all, and why what was asked for does not run.
/// </summary>
public sealed record MicCompressorState(bool Enabled, double Threshold, double Ratio, double Attack, double Release,
    double Knee, double Makeup)
{
    /// <summary>The native helper is installed, so the compressor can be switched on.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A microphone's expander as clients see it: the setting, whether it can run
/// at all, and why what was asked for does not run.
/// </summary>
public sealed record MicExpanderState(bool Enabled, double Threshold, double Ratio, double Range, double Attack,
    double Release, double Hold)
{
    /// <summary>The native helper is installed, so the expander can be switched on.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }
}
