using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

// Shared with the window, which draws and edits the EQ with the same model
// and snapping the daemon uses.
#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// One band of the microphone EQ. <see cref="Gain"/> is kept for every
/// type and ignored by <c>lowCut</c>, <c>notch</c> and <c>highCut</c>, so a
/// band changed to one of those and back keeps its gain.
/// </summary>
public sealed record EqBand
{
    public bool Enabled { get; init; }
    /// <summary>One of <see cref="MicEq.Types"/>.</summary>
    public string Type { get; init; } = MicEq.Peak;
    /// <summary>Hz, 20 to 20000, on whole hertz.</summary>
    public double Frequency { get; init; } = 1000;
    /// <summary>dB, -12 to +12, in quarter decibels.</summary>
    public double Gain { get; init; }
    /// <summary>Linear Q, 0.1 to 10, on the log10 grid of <see cref="MicEq.QLog10Step"/>.</summary>
    public double Q { get; init; } = MicEq.DefaultQ;
}

/// <summary>
/// A microphone's EQ: an on switch and one to sixteen bands with Wave Link's
/// types, ranges, steps and defaults. The daemon snaps every value it takes
/// to those steps, so the files, every client and a later hardware backend
/// hold the same numbers. Off by default, so an upgrade changes nobody's
/// sound.
/// </summary>
public sealed record MicEq
{
    public const string LowCut = "lowCut", LowShelf = "lowShelf", Peak = "peak", Notch = "notch",
        HighShelf = "highShelf", HighCut = "highCut";

    /// <summary>The band types in the order the native backend numbers them.</summary>
    public static readonly IReadOnlyList<string> Types = [LowCut, LowShelf, Peak, Notch, HighShelf, HighCut];

    public const int BandCount = 4;
    public const int MinimumBands = 1, MaximumBands = 16;
    public const double MinFrequency = 20, MaxFrequency = 20000, FrequencyStep = 1;
    public const double MinGain = -12, MaxGain = 12, GainStep = 0.25;
    public const double MinQ = 0.1, MaxQ = 10, QLog10Step = 0.016;

    /// <summary>Wave Link's Q of 0.707 on the grid: 10^(-1 + 53 x 0.016) = 10^-0.152.</summary>
    public static readonly double DefaultQ = SnapQ(0.707);

    public bool Enabled { get; init; }
    public IReadOnlyList<EqBand> Bands { get; init; } = DefaultBands;

    private static readonly IReadOnlyList<EqBand> DefaultBands =
    [
        new() { Enabled = true, Type = LowCut, Frequency = 36, Q = DefaultQ },
        new() { Enabled = true, Type = Peak, Frequency = 310, Q = DefaultQ },
        new() { Enabled = true, Type = Peak, Frequency = 2172, Q = DefaultQ },
        new() { Enabled = false, Type = HighCut, Frequency = 15049, Q = DefaultQ },
    ];

    /// <summary>Wave Link's band table with the EQ off.</summary>
    public static MicEq Default { get; } = new();

    public bool Equals(MicEq? other)
        => other is not null && Enabled == other.Enabled
            && (ReferenceEquals(Bands, other.Bands) || Bands is not null && other.Bands is not null && Bands.SequenceEqual(other.Bands));

    public override int GetHashCode() => HashCode.Combine(Enabled, Bands?.Count ?? 0);

    /// <summary>The same EQ with band <paramref name="band"/> replaced.</summary>
    public MicEq WithBand(int band, EqBand value)
    {
        if (band < 1 || band > Bands.Count) throw new ArgumentOutOfRangeException(nameof(band));
        var bands = Bands.ToArray();
        bands[band - 1] = value;
        return this with { Bands = bands };
    }

    /// <summary>Add a neutral bell in the largest frequency gap, keeping existing bands unchanged.</summary>
    public MicEq WithAddedBand()
    {
        if (Bands.Count >= MaximumBands) throw new InvalidOperationException($"The EQ has at most {MaximumBands} bands.");
        double[] frequencies = [.. Bands.Select(b => b.Frequency).Append(MinFrequency).Append(MaxFrequency).Order()];
        var gap = frequencies.Zip(frequencies.Skip(1), (a, b) => (Low: a, High: b)).MaxBy(g => g.High / g.Low);
        return this with { Bands = [.. Bands, new EqBand { Enabled = true, Frequency = SnapFrequency(Math.Sqrt(gap.Low * gap.High)) }] };
    }

    /// <summary>Remove one numbered band; following bands take the preceding number.</summary>
    public MicEq WithoutBand(int band)
    {
        if (band < 1 || band > Bands.Count) throw new ArgumentOutOfRangeException(nameof(band));
        if (Bands.Count <= MinimumBands) throw new InvalidOperationException("The EQ needs at least one band.");
        return this with { Bands = [.. Bands.Where((_, index) => index != band - 1)] };
    }

    /// <summary>The native backend's number for a type, or -1 for an unknown one.</summary>
    public static int TypeIndex(string? type)
    {
        for (int i = 0; i < Types.Count; i++)
            if (Types[i] == type) return i;
        return -1;
    }

    /// <summary>Whether a type uses its gain.</summary>
    public static bool UsesGain(string type) => type is LowShelf or Peak or HighShelf;

    public static double SnapFrequency(double hz) => Math.Round(hz / FrequencyStep, MidpointRounding.AwayFromZero) * FrequencyStep;

    public static double SnapGain(double db) => Math.Round(db / GainStep, MidpointRounding.AwayFromZero) * GainStep;

    /// <summary>The grid step k of a Q: log10(Q) is nearest to -1 + 0.016 k.</summary>
    private static int QStep(double q) => (int)Math.Round((Math.Log10(q) + 1) / QLog10Step, MidpointRounding.AwayFromZero);

    /// <summary>log10 of a Q on the grid, as the native backend takes it.</summary>
    public static double QLog10(double q) => -1 + QLog10Step * QStep(q);

    /// <summary>A Q snapped to the grid; a value already on it comes back unchanged.</summary>
    public static double SnapQ(double q) => Math.Pow(10, QLog10(q));

    public static EqBand Snap(EqBand band) => band with
    {
        Frequency = SnapFrequency(band.Frequency),
        Gain = SnapGain(band.Gain),
        Q = SnapQ(band.Q),
    };

    public static MicEq Snap(MicEq eq) => eq with { Bands = [.. eq.Bands.Select(Snap)] };

    /// <summary>
    /// This EQ holds <paramref name="target"/>: the same switch and band
    /// count, and every band with the target's switch and type and its values
    /// within half a step of the grid, so a value that came back through the
    /// daemon still counts.
    /// </summary>
    public bool Holds(MicEq target)
    {
        if (Enabled != target.Enabled || Bands.Count != target.Bands.Count) return false;
        for (int i = 0; i < Bands.Count; i++)
        {
            EqBand a = Bands[i], b = target.Bands[i];
            if (a.Enabled != b.Enabled || a.Type != b.Type
                || Math.Abs(a.Frequency - b.Frequency) >= FrequencyStep / 2
                || Math.Abs(a.Gain - b.Gain) >= GainStep / 2
                || Math.Abs(QLog10(a.Q) - QLog10(b.Q)) >= QLog10Step / 2)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Why a band cannot be taken, or null. Values outside a range are refused,
    /// never clamped. The reason is protocol text, so its numbers are written
    /// the same way whatever the daemon's locale ("0.1", never "0,1").
    /// </summary>
    public static string? Check(EqBand? band)
    {
        if (band is null) return "a band is missing";
        if (TypeIndex(band.Type) < 0) return $"type must be one of {string.Join(", ", Types)}";
        if (!double.IsFinite(band.Frequency) || band.Frequency is < MinFrequency or > MaxFrequency)
            return string.Create(CultureInfo.InvariantCulture, $"frequency must be {MinFrequency} to {MaxFrequency} Hz");
        if (!double.IsFinite(band.Gain) || band.Gain is < MinGain or > MaxGain)
            return string.Create(CultureInfo.InvariantCulture, $"gain must be {MinGain} to +{MaxGain} dB");
        if (!double.IsFinite(band.Q) || band.Q is < MinQ or > MaxQ)
            return string.Create(CultureInfo.InvariantCulture, $"q must be {MinQ} to {MaxQ}");
        return null;
    }

    /// <summary>Why an EQ cannot be taken, or null.</summary>
    public static string? Check(MicEq? eq)
    {
        if (eq?.Bands is null) return "need 'enabled' and 'bands'";
        if (eq.Bands.Count is < MinimumBands or > MaximumBands) return $"need {MinimumBands} to {MaximumBands} bands";
        for (int i = 0; i < eq.Bands.Count; i++)
            if (Check(eq.Bands[i]) is string error) return $"band {i + 1}: {error}";
        return null;
    }

    /// <summary>
    /// Read an EQ from a command's <c>eq</c> object, which must hold
    /// <c>enabled</c> and one to sixteen <c>bands</c>, each with every field. Fields a
    /// client copied from the state beside them (<c>backend</c>,
    /// <c>available</c>, <c>error</c>) are ignored. Null with
    /// <paramref name="error"/> set when it cannot be taken.
    /// </summary>
    public static MicEq? Read(JsonElement json, out string? error)
    {
        error = null;
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !json.TryGetProperty("bands", out JsonElement bands) || bands.ValueKind != JsonValueKind.Array)
        {
            error = "need 'eq' with 'enabled' and 'bands'";
            return null;
        }
        if (bands.GetArrayLength() is < MinimumBands or > MaximumBands)
        {
            error = $"need {MinimumBands} to {MaximumBands} bands";
            return null;
        }
        var read = new List<EqBand>();
        foreach (JsonElement band in bands.EnumerateArray())
        {
            int index = read.Count + 1;
            if (band.ValueKind != JsonValueKind.Object
                || !band.TryGetProperty("enabled", out JsonElement on) || on.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !band.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
                || Number(band, "frequency") is not double frequency
                || Number(band, "gain") is not double gain
                || Number(band, "q") is not double q)
            {
                error = $"band {index}: need 'enabled', 'type', 'frequency', 'gain' and 'q'";
                return null;
            }
            var value = new EqBand { Enabled = on.GetBoolean(), Type = type.GetString()!, Frequency = frequency, Gain = gain, Q = q };
            if (Check(value) is string problem)
            {
                error = $"band {index}: {problem}";
                return null;
            }
            read.Add(value);
        }
        return Snap(new MicEq { Enabled = enabled.GetBoolean(), Bands = read });

        static double? Number(JsonElement band, string name)
            => band.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double number) ? number : null;
    }
}

/// <summary>
/// Part of one band, as <c>setEqBand</c> carries it: each field present
/// replaces the band's, each absent one keeps it.
/// </summary>
public sealed record EqBandChange(bool? Enabled = null, string? Type = null, double? Frequency = null,
    double? Gain = null, double? Q = null)
{
    public bool IsEmpty => Enabled is null && Type is null && Frequency is null && Gain is null && Q is null;

    /// <summary>The band with this change, unsnapped; check and snap the result.</summary>
    public EqBand ApplyTo(EqBand band) => band with
    {
        Enabled = Enabled ?? band.Enabled,
        Type = Type ?? band.Type,
        Frequency = Frequency ?? band.Frequency,
        Gain = Gain ?? band.Gain,
        Q = Q ?? band.Q,
    };
}

/// <summary>The EQ's ranges and steps, sent in the state so clients draw from the daemon's numbers.</summary>
public sealed record EqLimits
{
    public sealed record EqRange(double Min, double Max, double Step);

    /// <summary>Q is linear on the wire and stepped in log10(Q): the grid is 10^(-1 + 0.016 k).</summary>
    public sealed record EqQRange(double Min, double Max, double Log10Step);

    public int Bands { get; init; } = MicEq.BandCount;
    public int MinBands { get; init; } = MicEq.BandCount;
    public int MaxBands { get; init; } = MicEq.BandCount;
    public IReadOnlyList<string> Types { get; init; } = MicEq.Types;
    public EqRange Frequency { get; init; } = new(MicEq.MinFrequency, MicEq.MaxFrequency, MicEq.FrequencyStep);
    public EqRange Gain { get; init; } = new(MicEq.MinGain, MicEq.MaxGain, MicEq.GainStep);
    public EqQRange Q { get; init; } = new(MicEq.MinQ, MicEq.MaxQ, MicEq.QLog10Step);

    public static EqLimits Instance { get; } = new() { MinBands = MicEq.MinimumBands, MaxBands = MicEq.MaximumBands };
}

/// <summary>
/// A microphone channel's EQ as clients see it: the values, which backend
/// runs them (<c>device</c> while the unit's own DSP runs it, on or off;
/// <c>software</c> while a stage runs it on this computer; null while the
/// EQ is off and nothing runs it), whether it can run, and why not.
/// </summary>
public sealed record MicEqState(bool Enabled, IReadOnlyList<EqBand> Bands, string? Backend, bool Available, string? Error)
{
    /// <summary>The channel's choice, <c>device</c> (the default) or <c>software</c>: where the EQ runs when the unit can run it.</summary>
    public string Processing { get; init; } = MicEqProcessing.Device;

    /// <summary>The unit input the channel carries, while that unit's model has a hardware EQ; null otherwise.</summary>
    public MicEqDevice? Device { get; init; }
}

/// <summary>The choices of where a microphone's EQ runs.</summary>
public static class MicEqProcessing
{
    public const string Device = "device", Software = "software";

    public static bool IsValid(string? value) => value is Device or Software;
}

/// <summary>
/// The unit a microphone channel carries, when its model has a hardware EQ:
/// its id and input, its name and firmware, whether it runs the EQ now
/// (<see cref="Ready"/>), and when it cannot, why: <see cref="Reason"/> is
/// <c>firmwareOld</c>, <c>firmwareUnknown</c>, <c>unsupportedBlock</c> or
/// <c>failed</c>, and <see cref="Note"/> the daemon's sentence.
/// <see cref="MinimumFirmware"/> is the first version the unit runs it on.
/// </summary>
public sealed record MicEqDevice(string Unit, int Input, string Name, string? Firmware, bool Ready,
    string? Reason = null, string? Note = null, string MinimumFirmware = "1.0.15");
