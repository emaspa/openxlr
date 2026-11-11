using System.Globalization;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Core.Devices;

/// <summary>One of the unit's twelve duckers: how far it lowers its source now, the level of its key, and its output.</summary>
public sealed record ProDuckerMeter(double ReductionDb, double? KeyDb, double? LeftDb, double? RightDb);

/// <summary>A stereo level in dBFS; null at the floor, silence.</summary>
public sealed record ProStereoMeter(double? LeftDb, double? RightDb);

/// <summary>One of the unit's five maximizers, one per mix section: how far it lowers the section now, and its input level.</summary>
public sealed record ProMaximizerMeter(double ReductionDb, double? InputDb);

/// <summary>Block 0x0002 decoded: the duckers, the 35 stereo levels and the four outputs.</summary>
public sealed record ProMeterReading(IReadOnlyList<ProDuckerMeter> Duckers, IReadOnlyList<ProStereoMeter> Levels,
    IReadOnlyList<ProStereoMeter> Outputs)
{
    /// <summary>The five maximizers, maximizer M the one of section M in block 0x0001's order (0, 18, 36, 54, 72); empty when not decoded.</summary>
    public IReadOnlyList<ProMaximizerMeter> Maximizers { get; init; } = [];

    /// <summary>
    /// The largest reduction across <paramref name="entries"/>, in dB: the
    /// duckers in block 0x0008's numbering (0 to 3 the inputs, 4 + k pair
    /// k). 0 when none is given; an entry out of range is skipped.
    /// </summary>
    public double DuckReductionDb(IEnumerable<int> entries)
    {
        double largest = 0;
        foreach (int entry in entries)
            if (entry >= 0 && entry < Duckers.Count) largest = Math.Max(largest, Duckers[entry].ReductionDb);
        return largest;
    }
}

/// <summary>
/// The Wave XLR Pro's meters, block 0x0002: 150 bytes, read only, never
/// written.
///
///   0 to 9      five maximizers, two bytes each: reduction, input level.
///               Maximizer M is the one of mix section M (0, 18, 36, 54,
///               72), measured for section 18 (2026-11-11, bytes 2 and 3)
///   10 to 57    twelve duckers, four bytes each, ducker N at 10 + 4N:
///               reduction, key level, output left, output right. Ducker N
///               is block 0x0008's entry N (0 XLR 1, 1 XLR 2, 2 USB Aux,
///               3 Line In, 4 + k USB playback pair k)
///   58 to 63    two expanders, three bytes each: reduction, key level,
///               output level
///   64 to 69    two compressors, laid out as the expanders
///   70, 71      Voice Tune's reduction on XLR 1 and XLR 2
///   72 to 141   35 stereo levels, channel K at 72 + 2K (left, right):
///               0 to 3 XLR 1 before the unit's processing, after it, after
///               the insert return, after the ducker; 4 to 7 XLR 2 the
///               same; 8 to 10 USB Aux as it arrives, after the insert
///               return, after the ducker; 11 to 13 Line In the same;
///               14 + 2k and 15 + 2k USB playback pair k as it arrives and
///               after its ducker; 30 to 34 the five mix sections of block
///               0x0001 (0, 18, 36, 54, 72)
///   142 to 149  the level at Headphones 1, Headphones 2, Line Out and the
///               USB Aux port, left and right each
///
/// A level byte is -4 times dBFS, f0 the floor (silence); a reduction byte
/// is 4 times dB. Measured on the desk (2026-11-10): a level is a peak
/// reading, rises within about 40 ms, holds nothing and falls in a straight
/// line at about 34 dB a second. A maximizer's reduction is the section
/// maximizer's meter in hardware mixing; the expanders, compressors and
/// Voice Tune bytes are decoded for diagnostics only.
/// </summary>
public static class ProMeters
{
    public const ushort Block = 0x0002;
    public const int Length = 150;

    public const int MaximizerOffset = 0;
    public const int MaximizerCount = 5;
    public const int MaximizerLength = 2;

    public const int DuckerOffset = 10;
    public const int DuckerCount = 12;
    public const int DuckerLength = 4;

    public const int ExpanderOffset = 58;
    public const int CompressorOffset = 64;
    public const int DynamicsCount = 2;
    public const int DynamicsLength = 3;

    public const int VoiceTuneOffset = 70;

    public const int LevelOffset = 72;
    public const int LevelCount = 35;

    public const int OutputOffset = 142;
    public const int OutputCount = 4;

    /// <summary>The level byte of silence; this and anything above it reads as no level.</summary>
    public const byte Floor = 0xF0;

    /// <summary>The first level channel of each input: its four (XLR) or three (USB Aux, Line In) taps follow.</summary>
    public const int Xlr1Channel = 0, Xlr2Channel = 4, UsbAuxChannel = 8, LineInChannel = 11;
    /// <summary>The level channel of pair 0 as it arrives; pair k is 14 + 2k, after its ducker 15 + 2k.</summary>
    public const int FirstPairChannel = 14;
    /// <summary>The level channel of mix section 0; the five sections follow in block 0x0001's order.</summary>
    public const int FirstSectionChannel = 30;

    public static string? Check(ReadOnlySpan<byte> block)
        => block.Length != Length ? $"block 0x0002 is {block.Length} bytes, not {Length}" : null;

    /// <summary>A level byte in dBFS; null at the floor.</summary>
    public static double? LevelDb(byte level) => level >= Floor ? null : -level / 4.0;

    /// <summary>A reduction byte in dB, 0 or more.</summary>
    public static double ReductionDb(byte reduction) => reduction / 4.0;

    /// <summary>The level channel of USB playback pair <paramref name="pair"/> (0 to 7), as it arrives or after its ducker.</summary>
    public static int PairChannel(int pair, bool afterDucker = false)
        => pair is >= 0 and < ProMixSections.PairCount ? FirstPairChannel + 2 * pair + (afterDucker ? 1 : 0)
            : throw new ArgumentOutOfRangeException(nameof(pair));

    /// <summary>The level channel of the mix section starting at <paramref name="start"/> in block 0x0001.</summary>
    public static int SectionChannel(int start)
        => ProMixSections.Starts.ToList().IndexOf(start) is int at and >= 0 ? FirstSectionChannel + at
            : throw new ArgumentOutOfRangeException(nameof(start));

    /// <summary>
    /// The duckers a target runs, in block 0x0008's numbering: its ducked
    /// inputs and its pairs' entries. None while it is off.
    /// </summary>
    public static IReadOnlyList<int> DuckedEntries(ProDuckingTarget target)
        => !target.Enabled ? []
            : [.. target.Inputs.Where(input => input is >= 0 and < ProMixSections.InputCount)
                .Concat(target.Pairs.Where(pair => pair is >= 0 and < ProMixSections.PairCount).Select(ProMixSections.SourceOfPair))
                .Distinct().Order()];

    /// <summary>The block decoded; null when it is not 150 bytes.</summary>
    public static ProMeterReading? Decode(ReadOnlySpan<byte> block)
    {
        if (Check(block) is not null) return null;
        var duckers = new ProDuckerMeter[DuckerCount];
        for (int n = 0; n < DuckerCount; n++)
        {
            int at = DuckerOffset + DuckerLength * n;
            duckers[n] = new(ReductionDb(block[at]), LevelDb(block[at + 1]), LevelDb(block[at + 2]), LevelDb(block[at + 3]));
        }
        var levels = new ProStereoMeter[LevelCount];
        for (int k = 0; k < LevelCount; k++) levels[k] = Stereo(block, LevelOffset + 2 * k);
        var outputs = new ProStereoMeter[OutputCount];
        for (int o = 0; o < OutputCount; o++) outputs[o] = Stereo(block, OutputOffset + 2 * o);
        var maximizers = new ProMaximizerMeter[MaximizerCount];
        for (int m = 0; m < MaximizerCount; m++)
        {
            int at = MaximizerOffset + MaximizerLength * m;
            maximizers[m] = new(ReductionDb(block[at]), LevelDb(block[at + 1]));
        }
        return new(duckers, levels, outputs) { Maximizers = maximizers };
    }

    private static ProStereoMeter Stereo(ReadOnlySpan<byte> block, int at) => new(LevelDb(block[at]), LevelDb(block[at + 1]));

    /// <summary>What level channel <paramref name="channel"/> (0 to 34) carries, in words.</summary>
    public static string ChannelName(int channel) => channel switch
    {
        < 0 or >= LevelCount => throw new ArgumentOutOfRangeException(nameof(channel)),
        < Xlr2Channel => "XLR 1 " + XlrTap(channel - Xlr1Channel),
        < UsbAuxChannel => "XLR 2 " + XlrTap(channel - Xlr2Channel),
        < LineInChannel => "USB Aux " + PortTap(channel - UsbAuxChannel),
        < FirstPairChannel => "Line In " + PortTap(channel - LineInChannel),
        < FirstSectionChannel => $"pair {(channel - FirstPairChannel) / 2} " + ((channel - FirstPairChannel) % 2 == 0 ? "in" : "ducked"),
        _ => $"section {ProMixSections.Starts[channel - FirstSectionChannel]}",
    };

    private static string XlrTap(int tap) => tap switch { 0 => "raw", 1 => "processed", 2 => "after insert", _ => "ducked" };

    private static string PortTap(int tap) => tap switch { 0 => "in", 1 => "after insert", _ => "ducked" };

    private static readonly string[] OutputNames = ["Headphones 1", "Headphones 2", "Line Out", "USB Aux port"];

    /// <summary>
    /// The block decoded for tester diagnostics, each line naming only what
    /// is above the floor or reducing: <c>meterDucking</c> (each ducker
    /// that reduces or hears its key), <c>meterLevels</c> (each level
    /// channel with a level), <c>meterOutputs</c>, <c>meterMaximizers</c>
    /// and <c>meterDynamics</c> (the expanders, compressors and Voice Tune).
    /// A block of another length gives <c>meters</c> alone, saying why.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Describe(ReadOnlySpan<byte> block)
    {
        var lines = new Dictionary<string, string>();
        if (Check(block) is { } problem)
        {
            lines["meters"] = $"not decoded: {problem}";
            return lines;
        }
        ProMeterReading reading = Decode(block)!;

        var ducking = new List<string>();
        for (int n = 0; n < DuckerCount; n++)
        {
            ProDuckerMeter d = reading.Duckers[n];
            if (d.ReductionDb == 0 && d.KeyDb is null) continue;
            ducking.Add($"{ProMixSections.SourceName(n)} {Cut(d.ReductionDb)}, key {Level(d.KeyDb)}, out {Level(d.LeftDb)}/{Level(d.RightDb)}");
        }
        lines["meterDucking"] = ducking.Count == 0 ? "nothing ducked, no key heard" : string.Join("; ", ducking);

        var levels = new List<string>();
        for (int k = 0; k < LevelCount; k++)
            if (reading.Levels[k] is { } level && (level.LeftDb is not null || level.RightDb is not null))
                levels.Add($"{ChannelName(k)} {Level(level.LeftDb)}/{Level(level.RightDb)}");
        lines["meterLevels"] = levels.Count == 0 ? "all silent" : string.Join(", ", levels);

        lines["meterOutputs"] = string.Join(", ", OutputNames.Select((name, o) => $"{name} {Level(reading.Outputs[o].LeftDb)}/{Level(reading.Outputs[o].RightDb)}"));

        var maximizers = new List<string>();
        for (int m = 0; m < MaximizerCount; m++)
        {
            int at = MaximizerOffset + MaximizerLength * m;
            maximizers.Add($"{m + 1} {Cut(ReductionDb(block[at]))} at {Level(LevelDb(block[at + 1]))}");
        }
        lines["meterMaximizers"] = string.Join(", ", maximizers);

        var dynamics = new List<string>();
        foreach ((string name, int offset) in new[] { ("expander", ExpanderOffset), ("compressor", CompressorOffset) })
            for (int i = 0; i < DynamicsCount; i++)
            {
                int at = offset + DynamicsLength * i;
                dynamics.Add($"{name} {i + 1} {Cut(ReductionDb(block[at]))}, key {Level(LevelDb(block[at + 1]))}, out {Level(LevelDb(block[at + 2]))}");
            }
        dynamics.Add($"Voice Tune XLR 1 {Cut(ReductionDb(block[VoiceTuneOffset]))}, XLR 2 {Cut(ReductionDb(block[VoiceTuneOffset + 1]))}");
        lines["meterDynamics"] = string.Join("; ", dynamics);
        return lines;
    }

    private static string Cut(double db) => db == 0 ? "0 dB" : $"-{Number(db)} dB";

    private static string Level(double? db) => db is { } value ? Number(value) : "-";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
