using OpenXLR.Core.Mixing;

namespace OpenXLR.Core.Devices;

/// <summary>
/// A unit's compressor and expander settings in the unit's raw form, field
/// for field as its input struct holds them: thresholds in whole dB, the
/// compressor's ratio as a whole number and the expander's in sixteenths,
/// attack and release in milliseconds, the make-up gain and the range in dB.
/// </summary>
public sealed record DeviceDynamicsValues(
    int CompressorThreshold, int CompressorRatio, int CompressorAttack, int CompressorRelease, int CompressorMakeup,
    int ExpanderThreshold, int ExpanderRatio, int ExpanderAttack, int ExpanderRelease, int ExpanderRange)
{
    public override string ToString()
        => $"compressor {CompressorThreshold} dB {CompressorRatio}:1 {CompressorAttack}/{CompressorRelease} ms +{CompressorMakeup} dB; "
            + $"expander {ExpanderThreshold} dB {ExpanderRatio}/16:1 {ExpanderAttack}/{ExpanderRelease} ms range {ExpanderRange} dB";
}

/// <summary>
/// A unit's compressor and expander in its raw form: the two switches and,
/// unless <see cref="Values"/> is null, every setting. With no values it
/// names the switches alone, which is what handing them to software writes:
/// the settings stay as they are.
/// </summary>
public sealed record DeviceDynamics(bool Compressor, bool Expander, DeviceDynamicsValues? Values)
{
    /// <summary>Both switches off and nothing else: the settings the unit holds are kept.</summary>
    public static DeviceDynamics Off { get; } = new(false, false, null);

    /// <summary>Whether this names the switches alone.</summary>
    public bool SwitchOnly => Values is null;

    /// <summary>Whether the fields this value names match what a unit holds: the switches, and the settings when it names them.</summary>
    public bool HeldBy(DeviceDynamics held)
        => Compressor == held.Compressor && Expander == held.Expander && (Values is null || Values == held.Values);

    public override string ToString()
        => $"compressor {(Compressor ? "on" : "off")}, expander {(Expander ? "on" : "off")}" + (Values is null ? "" : $"; {Values}");
}

/// <summary>
/// The compressor and the expander in one 38-byte input struct of the Wave
/// FX settings block (block <c>0x0004</c>), the struct <see cref="WaveFxEq"/>
/// reads its EQ from. A unit writes the whole block for every change, with
/// no commit, and reads the new bytes back at once:
///
///   byte 1, bit 7 (0x80)   the compressor's switch
///   byte 1, bit 5 (0x20)   the expander's switch
///   byte 3                 expander threshold, int8, dB
///   byte 4                 expander ratio, uint8, sixteenths (2.0:1 is 32)
///   bytes 5 and 6          expander attack, uint16 little endian, ms
///   bytes 7 and 8          expander release, uint16 little endian, ms
///   byte 9                 expander range (the most it turns down), dB
///   byte 11                compressor threshold, int8, dB
///   byte 12                compressor ratio, uint8, a whole number
///   bytes 13 and 14        compressor attack, uint16 little endian, ms
///   bytes 15 and 16        compressor release, uint16 little endian, ms
///   byte 17                compressor make-up gain, dB
///
/// Every other bit of byte 1 (mute, the low cut, voice tune, phantom power
/// and the bits nobody has named) and every other byte (ClipGuard and the EQ
/// switch at 2, voice tune's strength at 10, the EQ bands from 18) belongs to
/// another control and is kept as read.
///
/// Verified on the Wave:3 MK.2 (firmware 1.0.1, bank 0x0103), each field
/// written from its minimum to its maximum and read back. The Wave XLR MK.2,
/// the XLR Dock MK.2 and the Wave XLR Pro (two structs, XLR 1's at 0 and
/// XLR 2's at 38) hold the same struct and the same factory values at these
/// offsets, so they are driven the same way; that their DSP runs the values
/// written there has not been heard yet.
/// </summary>
public static class WaveFxDynamics
{
    public const int FlagsOffset = 1;
    public const byte CompressorMask = 0x80;
    public const byte ExpanderMask = 0x20;
    public const int ExpanderThresholdOffset = 3;
    public const int ExpanderRatioOffset = 4;
    public const int ExpanderAttackOffset = 5;
    public const int ExpanderReleaseOffset = 7;
    public const int ExpanderRangeOffset = 9;
    public const int CompressorThresholdOffset = 11;
    public const int CompressorRatioOffset = 12;
    public const int CompressorAttackOffset = 13;
    public const int CompressorReleaseOffset = 15;
    public const int CompressorMakeupOffset = 17;

    /// <summary>How many sixteenths the expander's ratio byte counts per unit of ratio.</summary>
    public const int ExpanderRatioScale = 16;

    /// <summary>The bytes the codec reads: the struct up to the first EQ band.</summary>
    public const int Used = WaveFxEq.FirstBand;

    private static int U16(ReadOnlySpan<byte> s, int at) => s[at] | s[at + 1] << 8;

    private static void PutU16(Span<byte> s, int at, int value)
    {
        s[at] = (byte)(value & 0xFF);
        s[at + 1] = (byte)(value >> 8);
    }

    /// <summary>Read the switches and settings from an input struct, raw, whatever the values. Null when the struct is too short.</summary>
    public static DeviceDynamics? Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < Used) return null;
        byte flags = input[FlagsOffset];
        return new DeviceDynamics((flags & CompressorMask) != 0, (flags & ExpanderMask) != 0, new DeviceDynamicsValues(
            (sbyte)input[CompressorThresholdOffset], input[CompressorRatioOffset], U16(input, CompressorAttackOffset),
            U16(input, CompressorReleaseOffset), input[CompressorMakeupOffset],
            (sbyte)input[ExpanderThresholdOffset], input[ExpanderRatioOffset], U16(input, ExpanderAttackOffset),
            U16(input, ExpanderReleaseOffset), input[ExpanderRangeOffset]));
    }

    private static bool In(DynamicsRange range, double value) => value >= range.Minimum && value <= range.Maximum;

    /// <summary>
    /// Whether every setting is one the unit takes (<see cref="UnitDynamics"/>).
    /// A unit whose struct holds anything else is not driven: its compressor
    /// and expander run in software.
    /// </summary>
    public static bool Decodes(DeviceDynamics? dynamics)
        => dynamics?.Values is { } v
            && In(UnitDynamics.CompressorThreshold, v.CompressorThreshold) && In(UnitDynamics.CompressorRatio, v.CompressorRatio)
            && In(UnitDynamics.CompressorAttack, v.CompressorAttack) && In(UnitDynamics.CompressorRelease, v.CompressorRelease)
            && In(UnitDynamics.CompressorMakeup, v.CompressorMakeup)
            && In(UnitDynamics.ExpanderThreshold, v.ExpanderThreshold)
            && In(UnitDynamics.ExpanderRatio, (double)v.ExpanderRatio / ExpanderRatioScale)
            && In(UnitDynamics.ExpanderAttack, v.ExpanderAttack) && In(UnitDynamics.ExpanderRelease, v.ExpanderRelease)
            && In(UnitDynamics.ExpanderRange, v.ExpanderRange);

    /// <summary>
    /// Write the switches, and the settings when the value names them, into
    /// an input struct. Every other bit of the flag byte and every byte the
    /// codec does not own keep what the struct held.
    /// </summary>
    /// <exception cref="ArgumentException">The struct is short or a setting is outside the unit's limits.</exception>
    public static void Encode(Span<byte> input, DeviceDynamics dynamics)
    {
        if (input.Length < Used) throw new ArgumentException($"an input struct holds the compressor and expander in its first {Used} bytes", nameof(input));
        if (dynamics.Values is not null && !Decodes(dynamics))
            throw new ArgumentException("the compressor or expander holds a value outside the unit's limits", nameof(dynamics));
        byte flags = (byte)(input[FlagsOffset] & ~(CompressorMask | ExpanderMask));
        if (dynamics.Compressor) flags |= CompressorMask;
        if (dynamics.Expander) flags |= ExpanderMask;
        input[FlagsOffset] = flags;
        if (dynamics.Values is not { } v) return;
        input[ExpanderThresholdOffset] = (byte)(sbyte)v.ExpanderThreshold;
        input[ExpanderRatioOffset] = (byte)v.ExpanderRatio;
        PutU16(input, ExpanderAttackOffset, v.ExpanderAttack);
        PutU16(input, ExpanderReleaseOffset, v.ExpanderRelease);
        input[ExpanderRangeOffset] = (byte)v.ExpanderRange;
        input[CompressorThresholdOffset] = (byte)(sbyte)v.CompressorThreshold;
        input[CompressorRatioOffset] = (byte)v.CompressorRatio;
        PutU16(input, CompressorAttackOffset, v.CompressorAttack);
        PutU16(input, CompressorReleaseOffset, v.CompressorRelease);
        input[CompressorMakeupOffset] = (byte)v.CompressorMakeup;
    }

    /// <summary>
    /// Whether a unit runs its compressor and expander for OpenXLR: it runs
    /// the EQ (the same firmware gate and the same struct), and the
    /// compressor and expander fields of each input hold values the unit
    /// takes. A unit whose EQ is not ready gives the EQ's reason and note
    /// here, which clients put in their own words by the reason.
    /// </summary>
    public static DeviceEqSupport Gate(string model, DeviceEqSupport eq, Func<bool> fieldsDecode)
    {
        if (!eq.Ready || fieldsDecode()) return eq;
        return new DeviceEqSupport(false, DeviceEqSupport.UnsupportedBlock,
            $"This {model} holds compressor or expander settings OpenXLR does not recognise, so they run in software. Collect diagnostics for a device report.")
        {
            MinimumFirmware = eq.MinimumFirmware,
        };
    }
}
