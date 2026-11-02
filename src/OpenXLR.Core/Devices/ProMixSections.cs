using System.Globalization;
using System.Text;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The Wave XLR Pro's five hardware mixes in block 0x0001, as the desk unit
/// on firmware 1.0.14 holds them (protocol notes, sections 12 and 15 to 19).
///
/// Block 0x0001, 108 bytes, starts with five sections of 18 bytes at 0, 18,
/// 36, 54 and 72. For a section starting at s:
///   s to s+11   one level cell per source, int8 in half dB, 00 unity. 89
///               (-59.5 dB) and up play their value, held at +12 dB (18)
///               and above; 88 and below are off, digital zero.
///   s+12, s+13  membership, LE16, one bit per source
///   s+14        unknown, 00 on every unit read so far, kept
///   s+15        aa on every unit read so far, the decode check
///   s+16, s+17  the ducking fields, 2 bits per USB pair, LE16, field k
///               for pair k; the unit ducks a pair only at 11
/// The sources, as bit and cell index: 0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line
/// In, 4 + k USB playback pair k.
///
/// After the sections: bytes 90 to 93 the selectors of Headphones 1,
/// Headphones 2, the USB Aux port and Line Out (1e to 22 play sections 0 to
/// 72, 23 nothing); byte 94 XLR 1's capture tap (00 the microphone before
/// the unit's low cut, compressor and Voice Tune, 01 to 03 after them);
/// byte 98 section 0's level in quarter-dB attenuation (54, -21 dB, on
/// every unit read so far). Every other byte is unknown and carried as
/// read.
///
/// A level or membership write applies live, with or without the 0x0003
/// commit, ramped by the unit over 12 to 16 ms. A change in a cell's linear
/// gain under about 0.0005 is dropped by the unit while the block reads back
/// the new value (<see cref="MayBeDropped"/>). Values only: no Elgato code.
/// </summary>
public static class ProMixSections
{
    public const ushort Block = 0x0001;
    public const int Length = 108;
    public const int SectionLength = 18;
    public const int SectionCount = 5;

    /// <summary>Where each section starts, in selector order.</summary>
    public static IReadOnlyList<int> Starts { get; } = [0, 18, 36, 54, 72];

    public const int Xlr1 = 0;
    public const int Xlr2 = 1;
    public const int UsbAux = 2;
    public const int LineIn = 3;
    public const int FirstPair = 4;
    public const int PairCount = 8;
    public const int SourceCount = FirstPair + PairCount;

    /// <summary>The source index, bit and cell, of USB playback pair <paramref name="pair"/> (0 to 7).</summary>
    public static int SourceOfPair(int pair)
        => pair is >= 0 and < PairCount ? FirstPair + pair : throw new ArgumentOutOfRangeException(nameof(pair));

    public const int MaskOffset = 12;
    public const int Byte14Offset = 14;
    public const int MarkerOffset = 15;
    public const byte Marker = 0xAA;
    public const int DuckingOffset = 16;
    /// <summary>The only field value the unit ducks a pair at.</summary>
    public const int Ducked = 0b11;

    public const int SelectorHp1 = 90;
    public const int SelectorHp2 = 91;
    public const int SelectorUsbAux = 92;
    public const int SelectorLineOut = 93;
    public const byte FirstSelector = 0x1E;
    public const byte SelectorNothing = 0x23;

    public const int CaptureTapOffset = 94;
    /// <summary>Byte 94's value that gives XLR 1's capture before the unit's processing.</summary>
    public const byte CaptureTapRaw = 0x00;

    public const int Section0LevelOffset = 98;

    /// <summary>The highest cell that is off; every cell at or below it is digital zero.</summary>
    public const byte CellOff = 0x88;
    /// <summary>The lowest cell that plays, -59.5 dB.</summary>
    public const int CellFloor = -119;
    /// <summary>The highest cell the unit plays as written, +12 dB; anything above is held there.</summary>
    public const int CellCeiling = 24;

    /// <summary>
    /// The smallest change in a cell's linear gain the desk unit was seen to
    /// take (0.00089, -36 to -36.5 dB); 0.00046 (-42 to -42.5 dB) was dropped.
    /// A change under this one may be dropped.
    /// </summary>
    public const double SmallestTakenGainChange = 0.00088;

    /// <summary>Whether the block has the layout this codec reads: its length and the aa at each section's byte 15.</summary>
    public static string? Check(ReadOnlySpan<byte> block)
    {
        if (block.Length != Length) return $"block 0x0001 is {block.Length} bytes, not {Length}";
        foreach (int s in Starts)
            if (block[s + MarkerOffset] != Marker)
                return $"section {s} holds {block[s + MarkerOffset]:x2} at byte {s + MarkerOffset}, not aa";
        return null;
    }

    public static bool Decodes(ReadOnlySpan<byte> block) => Check(block) is null;

    // --- the cell law ---

    /// <summary>A cell's level in dB, held at +12 dB; null when the cell is off.</summary>
    public static double? DbOf(byte cell)
    {
        int c = (sbyte)cell;
        return c < CellFloor ? null : Math.Min(c, CellCeiling) / 2.0;
    }

    /// <summary>The cell for a level, to the nearest half dB, held at +12 dB; below -59.75 dB, or not a number, off.</summary>
    public static byte CellOf(double db)
    {
        if (!double.IsFinite(db)) return db > 0 ? (byte)CellCeiling : CellOff;
        int c = (int)Math.Round(Math.Clamp(db, -1000, 1000) * 2, MidpointRounding.AwayFromZero);
        return c < CellFloor ? CellOff : unchecked((byte)(sbyte)Math.Min(c, CellCeiling));
    }

    /// <summary>A cell's linear gain, 0 when off.</summary>
    public static double GainOf(byte cell) => DbOf(cell) is { } db ? Math.Pow(10, db / 20) : 0;

    /// <summary>
    /// Whether the unit may keep the old gain when a cell goes from
    /// <paramref name="from"/> to <paramref name="to"/>: the change in linear
    /// gain is under <see cref="SmallestTakenGainChange"/>. The block reads
    /// back the new cell either way.
    /// </summary>
    public static bool MayBeDropped(byte from, byte to)
        => GainOf(from) != GainOf(to) && Math.Abs(GainOf(to) - GainOf(from)) < SmallestTakenGainChange;

    // --- section 0's level and the selectors ---

    /// <summary>Byte 98 as dB, quarter-dB attenuation.</summary>
    public static double Section0LevelDb(byte value) => 0 - value / 4.0;

    /// <summary>The byte 98 value for a level, 0 to -63.75 dB to the nearest quarter dB.</summary>
    public static byte Section0LevelByte(double db)
        => double.IsFinite(db) ? (byte)Math.Clamp((int)Math.Round(-db * 4, MidpointRounding.AwayFromZero), 0, 255) : (byte)255;

    /// <summary>The section a selector value plays: its start, or null for 23 and values that played nothing.</summary>
    public static int? SectionOf(byte selector)
        => selector >= FirstSelector && selector < FirstSelector + SectionCount ? Starts[selector - FirstSelector] : null;

    /// <summary>The selector value that plays the section starting at <paramref name="start"/>.</summary>
    public static byte SelectorOf(int start)
    {
        int i = IndexOf(start);
        return (byte)(FirstSelector + i);
    }

    private static int IndexOf(int start)
    {
        int i = start / SectionLength;
        return start % SectionLength == 0 && i is >= 0 and < SectionCount
            ? i : throw new ArgumentOutOfRangeException(nameof(start), start, "not a section start");
    }

    // --- one section's fields ---

    public static byte Level(ReadOnlySpan<byte> block, int start, int source) => block[CellAt(start, source)];

    public static void SetLevel(Span<byte> block, int start, int source, byte cell) => block[CellAt(start, source)] = cell;

    public static ushort Members(ReadOnlySpan<byte> block, int start)
        => (ushort)(block[Base(start) + MaskOffset] | block[Base(start) + MaskOffset + 1] << 8);

    public static bool IsMember(ReadOnlySpan<byte> block, int start, int source)
        => (Members(block, start) & 1 << CheckSource(source)) != 0;

    public static void SetMember(Span<byte> block, int start, int source, bool on)
    {
        int bit = CheckSource(source), at = Base(start) + MaskOffset + bit / 8;
        byte mask = (byte)(1 << bit % 8);
        block[at] = (byte)(on ? block[at] | mask : block[at] & ~mask);
    }

    /// <summary>Pair <paramref name="pair"/>'s ducking field in a section, 0 to 3.</summary>
    public static int DuckingField(ReadOnlySpan<byte> block, int start, int pair)
    {
        int at = Base(start) + DuckingOffset;
        int fields = block[at] | block[at + 1] << 8;
        return fields >> 2 * CheckPair(pair) & 0b11;
    }

    public static void SetDuckingField(Span<byte> block, int start, int pair, int value)
    {
        if (value is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(value));
        int at = Base(start) + DuckingOffset, shift = 2 * CheckPair(pair);
        int fields = (block[at] | block[at + 1] << 8) & ~(0b11 << shift) | value << shift;
        block[at] = (byte)fields;
        block[at + 1] = (byte)(fields >> 8);
    }

    private static int Base(int start) => IndexOf(start) * SectionLength;
    private static int CellAt(int start, int source) => Base(start) + CheckSource(source);

    private static int CheckSource(int source)
        => source is >= 0 and < SourceCount ? source : throw new ArgumentOutOfRangeException(nameof(source));

    private static int CheckPair(int pair)
        => pair is >= 0 and < PairCount ? pair : throw new ArgumentOutOfRangeException(nameof(pair));

    // --- diagnostics ---

    public static string SourceName(int source) => source switch
    {
        Xlr1 => "XLR 1",
        Xlr2 => "XLR 2",
        UsbAux => "USB Aux",
        LineIn => "Line In",
        _ => $"pair {source - FirstPair}",
    };

    /// <summary>
    /// The block decoded for tester diagnostics: one entry per section
    /// (<c>section0</c> to <c>section72</c>), the outputs' selectors, section
    /// 0's level and XLR 1's capture tap; or <c>sections</c> alone saying why
    /// it does not decode.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Describe(ReadOnlySpan<byte> block)
    {
        var lines = new Dictionary<string, string>();
        if (Check(block) is { } problem)
        {
            lines["sections"] = $"not decoded: {problem}";
            return lines;
        }
        foreach (int s in Starts) lines[$"section{s}"] = DescribeSection(block, s);
        var outputs = new List<string>();
        foreach ((string name, int offset) in new (string, int)[]
                 {
                     ("Headphones 1", SelectorHp1), ("Headphones 2", SelectorHp2),
                     ("USB Aux port", SelectorUsbAux), ("Line Out", SelectorLineOut),
                 })
            outputs.Add($"{name} {DescribeSelector(block[offset])}");
        lines["outputs"] = string.Join(", ", outputs);
        lines["section0Level"] = $"{Db(Section0LevelDb(block[Section0LevelOffset]))} (byte 98 {block[Section0LevelOffset]:x2})";
        byte tap = block[CaptureTapOffset];
        lines["xlr1Capture"] = (tap == CaptureTapRaw ? "before the unit's processing" : "processed") + $" (byte 94 {tap:x2})";
        return lines;
    }

    private static string DescribeSection(ReadOnlySpan<byte> block, int start)
    {
        var text = new StringBuilder();
        var members = new List<string>();
        for (int source = 0; source < SourceCount; source++)
            if (IsMember(block, start, source))
            {
                byte cell = Level(block, start, source);
                members.Add($"{SourceName(source)} {(DbOf(cell) is { } db ? Db(db) : "off")}");
            }
        text.Append(members.Count == 0 ? "no members" : string.Join(", ", members));
        var ducked = new List<int>();
        for (int pair = 0; pair < PairCount; pair++)
            if (DuckingField(block, start, pair) == Ducked) ducked.Add(pair);
        text.Append("; ducks ").Append(ducked.Count == 0 ? "no pair" : string.Join(", ", ducked.Select(p => $"pair {p}")));
        int b = start;
        text.Append(CultureInfo.InvariantCulture,
            $"; fields {block[b + DuckingOffset]:x2}{block[b + DuckingOffset + 1]:x2}, byte {b + Byte14Offset} {block[b + Byte14Offset]:x2}");
        return text.ToString();
    }

    private static string DescribeSelector(byte selector)
        => SectionOf(selector) is { } s ? $"section {s}"
            : selector == SelectorNothing ? "nothing" : $"{selector:x2}, plays nothing known";

    private static string Db(double db) => db.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " dB";
}
