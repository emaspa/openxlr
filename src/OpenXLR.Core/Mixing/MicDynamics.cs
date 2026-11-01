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
/// changer, on XLR 1, XLR 2 and every capture input on one mono port. It runs
/// on the unit instead where the unit runs one (UnitDynamics). Off by default, so an upgrade
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
/// microphone stage after noise removal and before the EQ, or on the unit
/// where the unit runs one (UnitDynamics). Off by default.
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
/// run at all, why what was asked for does not run, and where it runs. While
/// the unit runs it the numbers are the ones the unit holds
/// (<see cref="UnitDynamics.Compressor"/>).
/// </summary>
public sealed record MicCompressorState(bool Enabled, double Threshold, double Ratio, double Attack, double Release,
    double Knee, double Makeup)
{
    /// <summary>It can be switched on: the unit runs it, or the native helper is installed.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }

    /// <summary><c>device</c> while the unit runs it, on or off; <c>software</c> while a stage runs; null while neither does.</summary>
    public string? Backend { get; init; }

    /// <summary>The channel's choice for its compressor and expander, <c>device</c> (the default) or <c>software</c>.</summary>
    public string Processing { get; init; } = UnitDynamics.Device;

    /// <summary>The unit input the channel carries, while that unit's model runs a compressor and an expander; null otherwise.</summary>
    public MicEqDevice? Device { get; init; }
}

/// <summary>
/// A microphone's expander as clients see it: the setting, whether it can run
/// at all, why what was asked for does not run, and where it runs. While the
/// unit runs it the numbers are the ones the unit holds
/// (<see cref="UnitDynamics.Expander"/>).
/// </summary>
public sealed record MicExpanderState(bool Enabled, double Threshold, double Ratio, double Range, double Attack,
    double Release, double Hold)
{
    /// <summary>It can be switched on: the unit runs it, or the native helper is installed.</summary>
    public bool Available { get; init; }

    /// <summary>Why the setting does not run as asked, or null.</summary>
    public string? Error { get; init; }

    /// <summary><c>device</c> while the unit runs it, on or off; <c>software</c> while a stage runs; null while neither does.</summary>
    public string? Backend { get; init; }

    /// <summary>The channel's choice for its compressor and expander, <c>device</c> (the default) or <c>software</c>.</summary>
    public string Processing { get; init; } = UnitDynamics.Device;

    /// <summary>The unit input the channel carries, while that unit's model runs a compressor and an expander; null otherwise.</summary>
    public MicEqDevice? Device { get; init; }
}

/// <summary>
/// The compressor and the expander a unit runs on its own DSP: the Wave:3
/// MK.2, and by the same settings layout the Wave XLR MK.2, the XLR Dock
/// MK.2 and each input of the Wave XLR Pro. This is the one table of what
/// the unit takes: each field's limits and step as the unit holds it, and
/// the unit's factory value as the default. The device side and the clients
/// read it from here.
///
/// A setting goes to the unit through <see cref="Compressor"/> and
/// <see cref="Expander"/>: each number is rounded onto the unit's step and
/// clamped into its limits where the software stage takes a wider range.
/// The unit has no knee and no hold; those stay with the setting for
/// software. While the unit runs them the state reports the numbers it holds,
/// and the UI limits its controls to <see cref="CompressorControl"/> and
/// <see cref="ExpanderControl"/>.
/// </summary>
public static class UnitDynamics
{
    /// <summary>The choices of where a microphone's compressor and expander run, as for the EQ.</summary>
    public const string Device = "device", Software = "software";

    public static bool IsValid(string? value) => value is Device or Software;

    public static readonly DynamicsRange CompressorThreshold = new("threshold", -60, -1, 1, -12, "dB");
    public static readonly DynamicsRange CompressorRatio = new("ratio", 1, 20, 1, 4, "to 1");
    public static readonly DynamicsRange CompressorAttack = new("attack", 1, 300, 1, 10, "ms");
    public static readonly DynamicsRange CompressorRelease = new("release", 50, 1000, 1, 100, "ms");
    public static readonly DynamicsRange CompressorMakeup = new("makeup", 0, 20, 1, 6, "dB");
    public static readonly DynamicsRange ExpanderThreshold = new("threshold", -60, -1, 1, -44, "dB");
    /// <summary>Held in sixteenths, 16 to 48; set in steps of 0.1, each of which has its own sixteenth.</summary>
    public static readonly DynamicsRange ExpanderRatio = new("ratio", 1, 3, 0.1, 2, "to 1");
    public static readonly DynamicsRange ExpanderAttack = new("attack", 1, 200, 1, 10, "ms");
    public static readonly DynamicsRange ExpanderRelease = new("release", 10, 1000, 1, 200, "ms");
    public static readonly DynamicsRange ExpanderRange = new("range", 0, 60, 1, 24, "dB");

    private static double Hold(DynamicsRange unit, double value) => Math.Clamp(unit.Snap(value), unit.Minimum, unit.Maximum);

    /// <summary>A compressor setting as the unit runs it: each number on the unit's step and in its limits; the switch and the knee as set.</summary>
    public static MicCompressor Compressor(MicCompressor c) => c with
    {
        Threshold = Hold(CompressorThreshold, c.Threshold),
        Ratio = Hold(CompressorRatio, c.Ratio),
        Attack = Hold(CompressorAttack, c.Attack),
        Release = Hold(CompressorRelease, c.Release),
        Makeup = Hold(CompressorMakeup, c.Makeup),
    };

    /// <summary>An expander setting as the unit runs it, by the rule of <see cref="Compressor"/>; the hold as set.</summary>
    public static MicExpander Expander(MicExpander e) => e with
    {
        Threshold = Hold(ExpanderThreshold, e.Threshold),
        Ratio = Hold(ExpanderRatio, e.Ratio),
        Range = Hold(ExpanderRange, e.Range),
        Attack = Hold(ExpanderAttack, e.Attack),
        Release = Hold(ExpanderRelease, e.Release),
    };

    /// <summary>
    /// What a compressor field's control offers while the unit runs it: the
    /// software range narrowed to the unit's limits, on the coarser step.
    /// Null for the knee, which the unit does not have.
    /// </summary>
    public static DynamicsRange? CompressorControl(DynamicsRange software) => software.Name switch
    {
        "threshold" => Narrow(software, CompressorThreshold),
        "ratio" => Narrow(software, CompressorRatio),
        "attack" => Narrow(software, CompressorAttack),
        "release" => Narrow(software, CompressorRelease),
        "makeup" => Narrow(software, CompressorMakeup),
        _ => null,
    };

    /// <summary>What an expander field's control offers while the unit runs it; null for the hold.</summary>
    public static DynamicsRange? ExpanderControl(DynamicsRange software) => software.Name switch
    {
        "threshold" => Narrow(software, ExpanderThreshold),
        "ratio" => Narrow(software, ExpanderRatio),
        "range" => Narrow(software, ExpanderRange),
        "attack" => Narrow(software, ExpanderAttack),
        "release" => Narrow(software, ExpanderRelease),
        _ => null,
    };

    private static DynamicsRange Narrow(DynamicsRange software, DynamicsRange unit)
    {
        double min = Math.Max(software.Minimum, unit.Minimum), max = Math.Min(software.Maximum, unit.Maximum);
        return new DynamicsRange(software.Name, min, max, Math.Max(software.Step, unit.Step), Math.Clamp(software.Default, min, max), software.Unit);
    }
}
