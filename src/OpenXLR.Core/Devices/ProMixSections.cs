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
///   s+12, s+13  membership, LE16, one bit per source; bit 12 (s+13 bit
///               4) bypasses the section's maximizer
///   s+14        the section's maximizer amount, 0 to 100 (desk, 2026-11-11:
///               about 0.2 dB of make-up gain a step, a ceiling near -1
///               dBFS); 00 with the bypass clear on every unit read so far
///   s+15 to s+17  twelve 2-bit tap fields, LE, field M for source M, the
///               point the section takes the source at: 0 before the
///               unit's processing, 1 after it, 2 after the insert return,
///               3 after the ducker. s+15 holds the four inputs' fields
///               (aa, all at 2, as Wave Link leaves them), s+16 and s+17
///               the eight pairs' (the ducking fields: the unit ducks a pair
///               only at 3)
/// The sources, as bit and cell index: 0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line
/// In, 4 + k USB playback pair k.
///
/// Wave Link makes an input ducked by writing its field at 3 (USB captures
/// of 2026-11-10: ae, XLR 2's field at 3, and af, XLR 1's and XLR 2's), so
/// s+15 takes any value.
/// OpenXLR ducks an input only as a ducking target says
/// (<see cref="ProDucking.ApplyFields"/>); every other write of the block
/// puts an input field read at 3 back to 2 unless the ducking the unit is
/// meant to hold keeps it there (<see cref="ClearInputDucking"/>).
///
/// After the sections: bytes 90 to 93 the selectors of Headphones 1,
/// Headphones 2, the USB Aux port and Line Out (1e to 22 play sections 0 to
/// 72, 23 nothing); bytes 94, 95 and 97 the USB capture taps of XLR 1, XLR 2
/// and Line In in bits 0 and 1 (<see cref="CaptureTap"/>: 00 before the
/// unit's processing), byte 96 one for USB Aux that showed no effect on the
/// desk (2026-11-11) and is not driven; byte 98 section 0's level in
/// quarter-dB attenuation (54, -21 dB, on every unit read so far); bytes
/// 100, 102, 104 and 106 the levels of sections 18 to 72 in the same
/// encoding, and bytes 99, 101, 103, 105 and 107 the mutes of sections 0 to
/// 72, bit 0 (desk, 2026-11-11: byte 100 at 28 lowered section 18 by 9.9
/// dB, byte 101 bit 0 silenced it, both live without the commit; byte 99
/// follows the pattern and was not measured). Every other byte is unknown
/// and carried as read.
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
    /// <summary>The membership bit that bypasses the section's maximizer: set, the section plays dry at any amount.</summary>
    public const int MaximizerBypassBit = 12;
    /// <summary>The byte of the section's maximizer amount, 0 to <see cref="MaximizerMaximum"/>.</summary>
    public const int MaximizerOffset = 14;
    public const int MaximizerMaximum = 100;
    /// <summary>The byte of the four inputs' 2-bit tap fields, field M for input M.</summary>
    public const int InputTapsOffset = 15;
    public const int InputCount = 4;
    public const int DuckingOffset = 16;
    /// <summary>The tap after the insert return, where Wave Link leaves every input (aa).</summary>
    public const int TapInsertReturn = 0b10;
    /// <summary>The tap after the ducker: the only field value the unit ducks a source at.</summary>
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
    /// <summary>XLR 2's capture tap (AUX2), as byte 94 is XLR 1's (desk, 2026-11-11).</summary>
    public const int Xlr2CaptureTapOffset = 95;
    /// <summary>A tap byte for USB Aux by its place; no value changed USB Aux's capture on the desk (2026-11-11). Never written.</summary>
    public const int UsbAuxCaptureTapOffset = 96;
    /// <summary>Line In's capture tap (AUX6 and AUX7): 00 as it arrives, 01 after the insert return, 02 after the ducker, 03 silence (desk, 2026-11-11).</summary>
    public const int LineInCaptureTapOffset = 97;

    /// <summary>The byte of block 0x0001 that holds an input's capture tap.</summary>
    public static int CaptureTapOffsetOf(RawCaptureInput input) => input switch
    {
        RawCaptureInput.Xlr1 => CaptureTapOffset,
        RawCaptureInput.Xlr2 => Xlr2CaptureTapOffset,
        RawCaptureInput.LineIn => LineInCaptureTapOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

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

    /// <summary>
    /// Whether the block has the layout this codec reads: its length. Every
    /// value of every byte is a valid field: byte 15 of a section, once taken
    /// for a constant aa, holds the inputs' tap fields and reads otherwise
    /// after Wave Link ducks an input.
    /// </summary>
    public static string? Check(ReadOnlySpan<byte> block)
        => block.Length != Length ? $"block 0x0001 is {block.Length} bytes, not {Length}" : null;

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

    /// <summary>The level byte of the section starting at <paramref name="start"/>: 98 for section 0, 100, 102, 104 and 106 for sections 18 to 72.</summary>
    public static int MasterOffset(int start) => Section0LevelOffset + 2 * IndexOf(start);

    /// <summary>The mute byte of the section starting at <paramref name="start"/>, the byte after its level; bit 0 silences the section.</summary>
    public static int MuteOffset(int start) => MasterOffset(start) + 1;

    public const byte MuteMask = 0x01;

    /// <summary>Whether the section's mute bit is set.</summary>
    public static bool IsMuted(ReadOnlySpan<byte> block, int start) => (block[MuteOffset(start)] & MuteMask) != 0;

    /// <summary>The section's level byte in dB, quarter-dB attenuation, as byte 98 is.</summary>
    public static double MasterDb(ReadOnlySpan<byte> block, int start) => Section0LevelDb(block[MasterOffset(start)]);

    /// <summary>The section's maximizer amount, byte 14 of the section.</summary>
    public static byte MaximizerAmount(ReadOnlySpan<byte> block, int start) => block[Base(start) + MaximizerOffset];

    /// <summary>Whether the section's maximizer is bypassed: membership bit 12.</summary>
    public static bool MaximizerBypassed(ReadOnlySpan<byte> block, int start) => (Members(block, start) & 1 << MaximizerBypassBit) != 0;

    /// <summary>The maximizer index in block 0x0002 of the section starting at <paramref name="start"/>: 0 to 4 in section order.</summary>
    public static int MaximizerOf(int start) => IndexOf(start);

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

    /// <summary>Input <paramref name="input"/>'s tap field in a section, 0 to 3 (0 XLR 1 to 3 Line In).</summary>
    public static int InputTap(ReadOnlySpan<byte> block, int start, int input)
        => block[Base(start) + InputTapsOffset] >> 2 * CheckInput(input) & 0b11;

    /// <summary>
    /// A byte of four input tap fields with each field at 3, after the
    /// ducker, put back to 2, except the inputs in <paramref name="kept"/>
    /// (bit M for input M), which stay as given; the others as given too.
    /// </summary>
    public static byte WithoutInputDucking(byte taps, int kept = 0)
    {
        int fields = taps;
        for (int input = 0; input < InputCount; input++)
            if ((kept & 1 << input) == 0 && (fields >> 2 * input & 0b11) == Ducked)
                fields = fields & ~(0b11 << 2 * input) | TapInsertReturn << 2 * input;
        return (byte)fields;
    }

    /// <summary>
    /// A byte of four input tap fields with each input in
    /// <paramref name="ducked"/> (bit M for input M) at 3, after the ducker,
    /// and every other field read at 3 put back to 2; the rest as given.
    /// </summary>
    public static byte WithInputDucking(byte taps, int ducked)
    {
        int fields = WithoutInputDucking(taps);
        for (int input = 0; input < InputCount; input++)
            if ((ducked & 1 << input) != 0) fields |= Ducked << 2 * input;
        return (byte)fields;
    }

    /// <summary>
    /// Every input tap field at 3 in the five sections put back to 2, so no
    /// input ducking another program left, or a target that left the setup,
    /// stays on; the inputs <paramref name="kept"/> names for a section
    /// (section start to a mask, bit M for input M: the ducking the unit is
    /// meant to hold) and every other value stay as read. True when a byte
    /// changed.
    /// </summary>
    public static bool ClearInputDucking(Span<byte> block, IReadOnlyDictionary<int, int>? kept = null)
    {
        if (block.Length != Length) throw new ArgumentException($"block 0x0001 is {Length} bytes", nameof(block));
        bool changed = false;
        foreach (int s in Starts)
        {
            byte taps = WithoutInputDucking(block[s + InputTapsOffset], kept?.GetValueOrDefault(s) ?? 0);
            changed |= taps != block[s + InputTapsOffset];
            block[s + InputTapsOffset] = taps;
        }
        return changed;
    }

    private static int Base(int start) => IndexOf(start) * SectionLength;
    private static int CellAt(int start, int source) => Base(start) + CheckSource(source);

    private static int CheckSource(int source)
        => source is >= 0 and < SourceCount ? source : throw new ArgumentOutOfRangeException(nameof(source));

    private static int CheckPair(int pair)
        => pair is >= 0 and < PairCount ? pair : throw new ArgumentOutOfRangeException(nameof(pair));

    private static int CheckInput(int input)
        => input is >= 0 and < InputCount ? input : throw new ArgumentOutOfRangeException(nameof(input));

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
    /// (<c>section0</c> to <c>section72</c>, with its maximizer, and its level
    /// and mute bytes), the outputs' selectors, section 0's level and the
    /// capture taps of XLR 1, XLR 2, USB Aux and Line In; or <c>sections</c>
    /// alone saying why it does not decode.
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
        lines["xlr1Capture"] = DescribeMicTap(block, CaptureTapOffset);
        lines["xlr2Capture"] = DescribeMicTap(block, Xlr2CaptureTapOffset);
        lines["usbAuxCapture"] = $"no effect seen (byte {UsbAuxCaptureTapOffset} {block[UsbAuxCaptureTapOffset]:x2})";
        byte line = block[LineInCaptureTapOffset];
        string lineTap = CaptureTap.Field(line) switch
        {
            0 => "as it arrives",
            1 => "after the insert return",
            2 => "after the ducker",
            _ => "silent",
        };
        lines["lineInCapture"] = $"{lineTap} (byte {LineInCaptureTapOffset} {line:x2})";
        return lines;
    }

    private static string DescribeMicTap(ReadOnlySpan<byte> block, int offset)
    {
        byte tap = block[offset];
        return (CaptureTap.Field(tap) == CaptureTap.Raw ? "before the unit's processing" : "processed") + $" (byte {offset} {tap:x2})";
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
        var ducked = new List<string>();
        for (int input = 0; input < InputCount; input++)
            if (InputTap(block, start, input) == Ducked) ducked.Add(SourceName(input));
        for (int pair = 0; pair < PairCount; pair++)
            if (DuckingField(block, start, pair) == Ducked) ducked.Add(SourceName(SourceOfPair(pair)));
        text.Append("; ducks ").Append(ducked.Count == 0 ? "nothing" : string.Join(", ", ducked));
        int b = start;
        text.Append(CultureInfo.InvariantCulture,
            $"; inputs {block[b + InputTapsOffset]:x2}, fields {block[b + DuckingOffset]:x2}{block[b + DuckingOffset + 1]:x2}");
        text.Append(CultureInfo.InvariantCulture, $"; maximizer {MaximizerAmount(block, start)}");
        if (MaximizerBypassed(block, start)) text.Append(" bypassed");
        int level = MasterOffset(start), mute = MuteOffset(start);
        // Section 0's level is byte 98, described on its own line; its mute
        // byte follows the pattern of the others but was never measured.
        if (start != 0)
            text.Append(CultureInfo.InvariantCulture, $"; level {Db(MasterDb(block, start))} (byte {level} {block[level]:x2})")
                .Append(IsMuted(block, start) ? ", muted" : "");
        text.Append(CultureInfo.InvariantCulture, $"{(start == 0 ? ";" : ",")} byte {mute} {block[mute]:x2}");
        return text.ToString();
    }

    private static string DescribeSelector(byte selector)
        => SectionOf(selector) is { } s ? $"section {s}"
            : selector == SelectorNothing ? "nothing" : $"{selector:x2}, plays nothing known";

    private static string Db(double db) => db.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " dB";
}
