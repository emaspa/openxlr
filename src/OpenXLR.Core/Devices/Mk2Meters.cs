using System.Globalization;

namespace OpenXLR.Core.Devices;

/// <summary>One of the Wave XLR MK.2's two level processors: how far it turns the microphone down now, the level its detector hears, and its output.</summary>
public sealed record Mk2DynamicsMeter(double ReductionDb, double? KeyDb, double? OutputDb);

/// <summary>Block 0x0002 of the Wave XLR MK.2 decoded: its stereo levels and its expander and compressor.</summary>
public sealed record Mk2MeterReading(
    ProStereoMeter Input, ProStereoMeter Headphones, ProStereoMeter Usb, Mk2DynamicsMeter Expander, Mk2DynamicsMeter Compressor,
    ProStereoMeter Processed, ProStereoMeter AfterInsert, ProStereoMeter Monitor);

/// <summary>
/// The Wave XLR MK.2's meters, block 0x0002: 21 bytes, read only, never
/// written. Measured on the desk on 2026-11-10 (firmware 1.0.15, a mono
/// microphone, so each left and right pair read equal):
///
///   0, 1     the input, before the unit's processing
///   2, 3     the headphone output: the monitor mix less the headphone
///            attenuation
///   4, 5     what the computer receives over USB
///   8 to 10  the expander: reduction, key level, output level. Its output
///            is the compressor's key
///   11 to 13 the compressor: reduction, key level, output level
///   15, 16   after the unit's processing
///   17, 18   after the insert return, equal to the USB level with nothing
///            inserted
///   19, 20   the monitor mix
///
/// Bytes 6, 7 and 14 are not identified and nothing is decoded from them.
/// The bytes use the Pro's encoding (<see cref="ProMeters"/>): a level byte
/// is -4 times dBFS, f0 the floor (silence); a reduction byte is 4 times dB.
/// On the desk the expander's reduction reached its configured range (24 dB)
/// in silence and 0 while talking; the compressor's reduction read 0.25 to
/// 0.75 dB only near full scale at a threshold of -12 dB, and its key level
/// moved more slowly than the input peak.
/// </summary>
public static class Mk2Meters
{
    public const ushort Block = 0x0002;
    public const int Length = 21;

    public const int InputOffset = 0;
    public const int HeadphonesOffset = 2;
    public const int UsbOffset = 4;
    public const int ExpanderOffset = 8;
    public const int CompressorOffset = 11;
    public const int ProcessedOffset = 15;
    public const int AfterInsertOffset = 17;
    public const int MonitorOffset = 19;

    public static string? Check(ReadOnlySpan<byte> block)
        => block.Length != Length ? $"block 0x0002 is {block.Length} bytes, not {Length}" : null;

    /// <summary>The block decoded; null when it is not 21 bytes.</summary>
    public static Mk2MeterReading? Decode(ReadOnlySpan<byte> block)
    {
        if (Check(block) is not null) return null;
        return new(Stereo(block, InputOffset), Stereo(block, HeadphonesOffset), Stereo(block, UsbOffset),
            Dynamics(block, ExpanderOffset), Dynamics(block, CompressorOffset),
            Stereo(block, ProcessedOffset), Stereo(block, AfterInsertOffset), Stereo(block, MonitorOffset));
    }

    private static ProStereoMeter Stereo(ReadOnlySpan<byte> block, int at) => new(ProMeters.LevelDb(block[at]), ProMeters.LevelDb(block[at + 1]));

    private static Mk2DynamicsMeter Dynamics(ReadOnlySpan<byte> block, int at)
        => new(ProMeters.ReductionDb(block[at]), ProMeters.LevelDb(block[at + 1]), ProMeters.LevelDb(block[at + 2]));

    /// <summary>
    /// The block decoded for tester diagnostics: <c>meterLevels</c> (every
    /// level, left/right, a dash for silence) and <c>meterDynamics</c> (the
    /// expander and the compressor). A block of another length gives
    /// <c>meters</c> alone, saying why.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Describe(ReadOnlySpan<byte> block)
    {
        var lines = new Dictionary<string, string>();
        if (Check(block) is { } problem)
        {
            lines["meters"] = $"not decoded: {problem}";
            return lines;
        }
        Mk2MeterReading r = Decode(block)!;
        lines["meterLevels"] = string.Join(", ",
            Pair("input", r.Input), Pair("headphones", r.Headphones), Pair("USB", r.Usb), Pair("processed", r.Processed),
            Pair("after insert", r.AfterInsert), Pair("monitor mix", r.Monitor));
        lines["meterDynamics"] = $"{Processor("expander", r.Expander)}; {Processor("compressor", r.Compressor)}";
        return lines;
    }

    private static string Pair(string name, ProStereoMeter level) => $"{name} {Level(level.LeftDb)}/{Level(level.RightDb)}";

    private static string Processor(string name, Mk2DynamicsMeter m)
        => $"{name} {(m.ReductionDb == 0 ? "0 dB" : $"-{Number(m.ReductionDb)} dB")}, key {Level(m.KeyDb)}, out {Level(m.OutputDb)}";

    private static string Level(double? db) => db is { } value ? Number(value) : "-";

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
