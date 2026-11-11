using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's block 0x0001 codec against blocks read from units:
/// the desk unit on 2026-11-02 and an owner's archive (protocol notes
/// sections 12 and 13), the cell law over every int8 with the floor and the
/// ceiling the desk measured (section 15), the selectors and byte 98
/// (sections 17 to 19), the small low-level changes the unit drops, and
/// every byte the codec does not own carried as read.
/// </summary>
public sealed class ProMixSectionsTests
{
    /// <summary>The desk unit's block 0x0001 on 2026-11-02, read before each probe and restored after it.</summary>
    internal const string DeskBlock =
        "000000000000000000000000" + "2400" + "00aa" + "5501"
        + "0000c400000000c400c4c400" + "1300" + "00aa" + "5501"
        + "00c4f400c4e8c4c4f400c4c4" + "0002" + "00aa" + "5501"
        + "fafafafac4c4c4c4c4c4c4c4" + "0000" + "00aa" + "5555"
        + "000088000000000000000000" + "0000" + "00aa" + "5555"
        + "23232023" + "02020202" + "54" + "000000000000000000";

    private static byte[] Desk() => Convert.FromHexString(DeskBlock);

    /// <summary>An owner's unit (0.2.4 archive): bytes 0 to 13 and 90 to 93 as read, the rest as the desk's.</summary>
    private static byte[] Owner()
    {
        byte[] b = Desk();
        Convert.FromHexString("c80000c20000c9c9c900a6c9e80d").CopyTo(b, 0);
        Convert.FromHexString("1e232023").CopyTo(b, 90);
        return b;
    }

    private static List<string> MembersOf(byte[] block, int start)
        => Enumerable.Range(0, ProMixSections.SourceCount)
            .Where(s => ProMixSections.IsMember(block, start, s))
            .Select(ProMixSections.SourceName).ToList();

    [Fact]
    public void TheDeskBlockDecodes()
    {
        byte[] b = Desk();
        Assert.Equal(ProMixSections.Length, b.Length);
        Assert.Null(ProMixSections.Check(b));

        // Section 0: USB Aux and pair 1, the monitor return (byte 12 24).
        Assert.Equal(["USB Aux", "pair 1"], MembersOf(b, 0));
        // Section 18: byte 30 13.
        Assert.Equal(["XLR 1", "XLR 2", "pair 0"], MembersOf(b, 18));
        // Section 36: the Aux mix, pair 5 at byte 45 and byte 49 bit 1.
        Assert.Equal(["pair 5"], MembersOf(b, 36));
        Assert.Equal(0x00, ProMixSections.Level(b, 36, ProMixSections.SourceOfPair(5)));
        Assert.Empty(MembersOf(b, 54));
        Assert.Empty(MembersOf(b, 72));
        // Section 72 stores 88 for USB Aux: off, not -60 dB.
        Assert.Null(ProMixSections.DbOf(ProMixSections.Level(b, 72, ProMixSections.UsbAux)));
        Assert.Equal(-3.0, ProMixSections.DbOf(ProMixSections.Level(b, 54, ProMixSections.Xlr1)));

        // Every field 01, as Wave Link leaves them, except pair 4 in sections 0 to 36 (byte s+17 01).
        foreach (int s in ProMixSections.Starts)
            for (int pair = 0; pair < ProMixSections.PairCount; pair++)
                Assert.Equal(s < 54 && pair > 4 ? 0 : 1, ProMixSections.DuckingField(b, s, pair));

        Assert.Null(ProMixSections.SectionOf(b[ProMixSections.SelectorHp1]));
        Assert.Null(ProMixSections.SectionOf(b[ProMixSections.SelectorHp2]));
        Assert.Equal(36, ProMixSections.SectionOf(b[ProMixSections.SelectorUsbAux]));
        Assert.Null(ProMixSections.SectionOf(b[ProMixSections.SelectorLineOut]));
        Assert.Equal(-21.0, ProMixSections.Section0LevelDb(b[ProMixSections.Section0LevelOffset]));
        Assert.NotEqual(ProMixSections.CaptureTapRaw, b[ProMixSections.CaptureTapOffset]);
    }

    [Fact]
    public void TheOwnersBlockDecodes()
    {
        byte[] b = Owner();
        // Byte 12 e8, byte 13 0d: Line In and pairs 1, 2, 3, 4, 6 and 7 (section 13).
        Assert.Equal(["Line In", "pair 1", "pair 2", "pair 3", "pair 4", "pair 6", "pair 7"], MembersOf(b, 0));
        Assert.Equal(-31.0, ProMixSections.DbOf(ProMixSections.Level(b, 0, ProMixSections.LineIn)));
        Assert.Equal(0.0, ProMixSections.DbOf(ProMixSections.Level(b, 0, ProMixSections.SourceOfPair(1))));
        Assert.Equal(-27.5, ProMixSections.DbOf(ProMixSections.Level(b, 0, ProMixSections.SourceOfPair(2))));
        Assert.Equal(-45.0, ProMixSections.DbOf(ProMixSections.Level(b, 0, ProMixSections.SourceOfPair(6))));
        // XLR 1's cell c8 with its bit clear: out of the mix whatever the level.
        Assert.False(ProMixSections.IsMember(b, 0, ProMixSections.Xlr1));
        Assert.Equal(-28.0, ProMixSections.DbOf(ProMixSections.Level(b, 0, ProMixSections.Xlr1)));
        Assert.Equal(0, ProMixSections.SectionOf(b[ProMixSections.SelectorHp1]));
    }

    [Fact]
    public void TheCellLawHoldsOnEveryInt8()
    {
        for (int c = -128; c <= 127; c++)
        {
            byte cell = unchecked((byte)(sbyte)c);
            double? db = ProMixSections.DbOf(cell);
            if (c <= -120) Assert.Null(db);
            else if (c <= 24)
            {
                Assert.Equal(c / 2.0, db);
                Assert.Equal(cell, ProMixSections.CellOf(db!.Value));
            }
            else Assert.Equal(12.0, db);
        }
    }

    [Theory]
    [InlineData(0.0, 0x00)]
    [InlineData(-6.0, 0xf4)]
    [InlineData(-3.0, 0xfa)]
    [InlineData(-30.0, 0xc4)]
    [InlineData(-59.5, 0x89)]
    [InlineData(-59.7, 0x89)]
    [InlineData(-59.75, 0x88)]
    [InlineData(-60.0, 0x88)]
    [InlineData(-200.0, 0x88)]
    [InlineData(double.NegativeInfinity, 0x88)]
    [InlineData(double.NaN, 0x88)]
    [InlineData(12.0, 0x18)]
    [InlineData(20.0, 0x18)]
    [InlineData(double.PositiveInfinity, 0x18)]
    [InlineData(1e300, 0x18)]
    [InlineData(-0.24, 0x00)]
    [InlineData(-0.25, 0xff)]
    public void ALevelGoesToItsCell(double db, int cell)
        => Assert.Equal((byte)cell, ProMixSections.CellOf(db));

    [Theory]
    // Section 15: 0.5 dB steps applied down to -36 dB and were dropped from -42 and -48.
    [InlineData(-36.0, -36.5, false)]
    [InlineData(-30.0, -30.5, false)]
    [InlineData(-42.0, -42.5, true)]
    [InlineData(-48.0, -48.5, true)]
    [InlineData(-52.0, -52.5, true)]
    // 90 to 8a, 8a to 90 and 8a to 89 dropped; 3.5 dB from -48 and from 00, a0 or c4 to 8a applied.
    [InlineData(-56.0, -59.0, true)]
    [InlineData(-59.0, -56.0, true)]
    [InlineData(-59.0, -59.5, true)]
    [InlineData(-48.0, -51.5, false)]
    [InlineData(0.0, -59.0, false)]
    [InlineData(-48.0, -59.0, false)]
    [InlineData(-30.0, -59.0, false)]
    public void ASmallChangeNearTheBottomMayBeDropped(double fromDb, double toDb, bool dropped)
        => Assert.Equal(dropped, ProMixSections.MayBeDropped(ProMixSections.CellOf(fromDb), ProMixSections.CellOf(toDb)));

    [Fact]
    public void NoChangeAndOffToOffAreNotDropped()
    {
        Assert.False(ProMixSections.MayBeDropped(0x89, 0x89));
        Assert.False(ProMixSections.MayBeDropped(0x88, 0x80));
        Assert.Equal(0, ProMixSections.GainOf(0x88));
    }

    [Fact]
    public void SelectorsMapToSections()
    {
        Assert.Equal([0, 18, 36, 54, 72], ProMixSections.Starts.Select(s => ProMixSections.SectionOf(ProMixSections.SelectorOf(s))));
        Assert.Equal([0x1e, 0x1f, 0x20, 0x21, 0x22], ProMixSections.Starts.Select(s => (int)ProMixSections.SelectorOf(s)));
        Assert.Null(ProMixSections.SectionOf(0x23));
        Assert.Null(ProMixSections.SectionOf(0x1d));
        Assert.Null(ProMixSections.SectionOf(0x2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.SelectorOf(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.SelectorOf(90));
    }

    [Theory]
    [InlineData(0x54, -21.0)]
    [InlineData(0x50, -20.0)]
    [InlineData(0x60, -24.0)]
    [InlineData(0x00, 0.0)]
    public void ByteNinetyEightIsQuarterDbAttenuation(int value, double db)
    {
        Assert.Equal(db, ProMixSections.Section0LevelDb((byte)value));
        Assert.Equal((byte)value, ProMixSections.Section0LevelByte(db));
    }

    [Fact]
    public void ByteNinetyEightIsHeldToItsRange()
    {
        Assert.Equal(0, ProMixSections.Section0LevelByte(6));
        Assert.Equal(255, ProMixSections.Section0LevelByte(-90));
        Assert.Equal(255, ProMixSections.Section0LevelByte(double.NaN));
    }

    [Fact]
    public void WritingAFieldChangesOnlyItsBytes()
    {
        byte[] before = Owner();
        byte[] b = (byte[])before.Clone();
        ProMixSections.SetLevel(b, 54, ProMixSections.SourceOfPair(7), ProMixSections.CellOf(-6));
        ProMixSections.SetMember(b, 54, ProMixSections.SourceOfPair(7), true);
        ProMixSections.SetMember(b, 0, ProMixSections.LineIn, false);
        ProMixSections.SetDuckingField(b, 72, 6, ProMixSections.Ducked);

        int[] changed = Enumerable.Range(0, b.Length).Where(i => b[i] != before[i]).ToArray();
        // Byte 65 the level, byte 67 bit 3 the member, byte 12 bit 3 Line In, byte 89 pair 6's field (bits 4 and 5).
        Assert.Equal([12, 65, 67, 89], changed);
        Assert.Equal(0xf4, b[65]);
        Assert.Equal(0x08, b[67]);
        Assert.Equal(0xe0, b[12]);
        Assert.Equal(0x75, b[89]);
        Assert.Equal(ProMixSections.Ducked, ProMixSections.DuckingField(b, 72, 6));
        Assert.Equal(1, ProMixSections.DuckingField(b, 72, 7));
        Assert.Equal(1, ProMixSections.DuckingField(b, 72, 5));

        // And back: the block as read.
        ProMixSections.SetLevel(b, 54, ProMixSections.SourceOfPair(7), before[65]);
        ProMixSections.SetMember(b, 54, ProMixSections.SourceOfPair(7), false);
        ProMixSections.SetMember(b, 0, ProMixSections.LineIn, true);
        ProMixSections.SetDuckingField(b, 72, 6, 1);
        Assert.Equal(before, b);
    }

    [Fact]
    public void EveryMembershipBitAndFieldIsItsOwn()
    {
        foreach (int s in ProMixSections.Starts)
        {
            for (int source = 0; source < ProMixSections.SourceCount; source++)
            {
                byte[] b = new byte[ProMixSections.Length];
                ProMixSections.SetMember(b, s, source, true);
                Assert.Equal(1 << source, ProMixSections.Members(b, s));
                Assert.Equal(source < 8 ? s + 12 : s + 13, Array.FindIndex(b, x => x != 0));
            }
            for (int pair = 0; pair < ProMixSections.PairCount; pair++)
            {
                byte[] b = new byte[ProMixSections.Length];
                ProMixSections.SetDuckingField(b, s, pair, ProMixSections.Ducked);
                Assert.Equal(pair < 4 ? s + 16 : s + 17, Array.FindIndex(b, x => x != 0));
                Assert.Equal(Enumerable.Range(0, 8).Select(p => p == pair ? 3 : 0),
                    Enumerable.Range(0, 8).Select(p => ProMixSections.DuckingField(b, s, p)));
            }
        }
    }

    [Fact]
    public void ArgumentsOutsideTheLayoutAreRefused()
    {
        byte[] b = Desk();
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.Level(b, 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.Level(b, 90, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.Level(b, 0, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.SetMember(b, 0, -1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.DuckingField(b, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.SetDuckingField(b, 0, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.SourceOfPair(8));
        Assert.Equal(Desk(), b);
    }

    [Fact]
    public void ABlockOfAnotherLengthDoesNotDecode()
    {
        Assert.Equal("block 0x0001 is 107 bytes, not 108", ProMixSections.Check(new byte[107]));
        Assert.Equal(new Dictionary<string, string> { ["sections"] = "not decoded: block 0x0001 is 107 bytes, not 108" },
            ProMixSections.Describe(new byte[107]));
    }

    [Fact]
    public void AnInputDuckedByWaveLinkDecodes()
    {
        // Wave Link wrote ae (XLR 2's tap field at 3, after the ducker) and af
        // (XLR 1's as well) at byte 15 of a section; any value is a valid
        // set of four tap fields.
        byte[] b = Desk();
        b[15] = 0xae;
        b[51] = 0xaf;
        b[87] = 0x00;
        Assert.Null(ProMixSections.Check(b));
        Assert.True(ProMixSections.Decodes(b));
        Assert.Equal([2, 3, 2, 2], Enumerable.Range(0, 4).Select(input => ProMixSections.InputTap(b, 0, input)));
        Assert.Equal([3, 3, 2, 2], Enumerable.Range(0, 4).Select(input => ProMixSections.InputTap(b, 36, input)));
        Assert.Equal([0, 0, 0, 0], Enumerable.Range(0, 4).Select(input => ProMixSections.InputTap(b, 72, input)));
        IReadOnlyDictionary<string, string> d = ProMixSections.Describe(b);
        Assert.Equal("USB Aux 0 dB, pair 1 0 dB; ducks XLR 2; inputs ae, fields 5501; maximizer 0; byte 99 00", d["section0"]);
        Assert.Equal("pair 5 0 dB; ducks XLR 1, XLR 2; inputs af, fields 5501; maximizer 0; level 0 dB (byte 102 00), byte 103 00", d["section36"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMixSections.InputTap(b, 0, 4));
    }

    [Fact]
    public void InputDuckingSetsTheChosenInputsAndKeepsTheOnesTheUnitIsMeantToHold()
    {
        // 0xe6: XLR 2 at 1, XLR 1 at 2, USB Aux at 2, Line In at 3.
        Assert.Equal(0b10_10_11_10, ProMixSections.WithInputDucking(0b11_10_01_10, 0b0010));
        Assert.Equal(0b11_10_01_11, ProMixSections.WithInputDucking(0b11_10_01_10, 0b1001));
        Assert.Equal(0b10_10_01_10, ProMixSections.WithoutInputDucking(0b11_10_01_10));
        Assert.Equal(0b11_10_01_10, ProMixSections.WithoutInputDucking(0b11_10_01_10, kept: 0b1000));
        byte[] b = new byte[ProMixSections.Length];
        foreach (int s in ProMixSections.Starts) b[s + ProMixSections.InputTapsOffset] = 0xff;
        // Kept: XLR 2 in section 0 and Line In in section 36; the rest goes back to 2.
        Assert.True(ProMixSections.ClearInputDucking(b, new Dictionary<int, int> { [0] = 0b0010, [36] = 0b1000 }));
        Assert.Equal(0xae, b[15]);
        Assert.Equal(0xaa, b[33]);
        Assert.Equal(0xea, b[51]);
        Assert.False(ProMixSections.ClearInputDucking(b, new Dictionary<int, int> { [0] = 0b0010, [36] = 0b1000 }));
    }

    [Fact]
    public void ClearingInputDuckingPutsOnlyFieldsAtThreeBackToTwo()
    {
        byte[] b = Desk();
        b[15] = 0xae;
        b[33] = 0xaf;
        b[51] = 0x93;   // XLR 1 at 3, XLR 2 at 0, USB Aux at 1, Line In at 2
        b[69] = 0x55;
        b[87] = 0xff;
        byte[] before = (byte[])b.Clone();
        Assert.True(ProMixSections.ClearInputDucking(b));
        Assert.Equal(new byte[] { 0xaa, 0xaa, 0x92, 0x55, 0xaa }, ProMixSections.Starts.Select(s => b[s + 15]));
        for (int i = 0; i < b.Length; i++)
            if (!ProMixSections.Starts.Contains(i - 15)) Assert.Equal(before[i], b[i]);
        Assert.False(ProMixSections.ClearInputDucking(b));
        Assert.Throws<ArgumentException>(() => ProMixSections.ClearInputDucking(new byte[107]));
    }

    [Fact]
    public void DiagnosticsDescribeTheDeskBlock()
    {
        IReadOnlyDictionary<string, string> d = ProMixSections.Describe(Desk());
        Assert.Equal("USB Aux 0 dB, pair 1 0 dB; ducks nothing; inputs aa, fields 5501; maximizer 0; byte 99 00", d["section0"]);
        Assert.Equal("XLR 1 0 dB, XLR 2 0 dB, pair 0 0 dB; ducks nothing; inputs aa, fields 5501; maximizer 0; level 0 dB (byte 100 00), byte 101 00", d["section18"]);
        Assert.Equal("pair 5 0 dB; ducks nothing; inputs aa, fields 5501; maximizer 0; level 0 dB (byte 102 00), byte 103 00", d["section36"]);
        Assert.Equal("no members; ducks nothing; inputs aa, fields 5555; maximizer 0; level 0 dB (byte 104 00), byte 105 00", d["section54"]);
        Assert.Equal("no members; ducks nothing; inputs aa, fields 5555; maximizer 0; level 0 dB (byte 106 00), byte 107 00", d["section72"]);
        Assert.Equal("Headphones 1 nothing, Headphones 2 nothing, USB Aux port section 36, Line Out nothing", d["outputs"]);
        Assert.Equal("-21 dB (byte 98 54)", d["section0Level"]);
        Assert.Equal("processed (byte 94 02)", d["xlr1Capture"]);
    }

    [Fact]
    public void DiagnosticsNameDuckedPairsOffCellsAndUnknownSelectors()
    {
        byte[] b = Owner();
        ProMixSections.SetDuckingField(b, 0, 1, ProMixSections.Ducked);
        ProMixSections.SetLevel(b, 0, ProMixSections.SourceOfPair(7), 0x88);
        b[ProMixSections.SelectorHp2] = 0x2f;
        b[ProMixSections.CaptureTapOffset] = 0x00;
        b[ProMixSections.Section0LevelOffset] = 0x00;
        IReadOnlyDictionary<string, string> d = ProMixSections.Describe(b);
        Assert.Equal("Line In -31 dB, pair 1 0 dB, pair 2 -27.5 dB, pair 3 -27.5 dB, pair 4 -27.5 dB, pair 6 -45 dB, pair 7 off; "
            + "ducks pair 1; inputs aa, fields 5d01; maximizer 0; byte 99 00", d["section0"]);
        Assert.Equal("Headphones 1 section 0, Headphones 2 2f, plays nothing known, USB Aux port section 36, Line Out nothing", d["outputs"]);
        Assert.Equal("0 dB (byte 98 00)", d["section0Level"]);
        Assert.Equal("before the unit's processing (byte 94 00)", d["xlr1Capture"]);
    }

    [Fact]
    public void TheDeviceDumpCarriesTheDecodedSections()
    {
        var unit = new ProHardwareEqTests.Unit();
        unit.Blocks[0x0001] = Desk();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        int writes = unit.Calls.Count(c => c.Type == 0x41);
        IReadOnlyDictionary<string, string> dump = pro.DumpBlocks();
        Assert.Equal(DeskBlock, dump["0x0001"], ignoreCase: true);
        Assert.Equal("pair 5 0 dB; ducks nothing; inputs aa, fields 5501; maximizer 0; level 0 dB (byte 102 00), byte 103 00", dump["section36"]);
        Assert.Equal("Headphones 1 nothing, Headphones 2 nothing, USB Aux port section 36, Line Out nothing", dump["outputs"]);
        Assert.Equal("-21 dB (byte 98 54)", dump["section0Level"]);
        Assert.Equal(writes, unit.Calls.Count(c => c.Type == 0x41));
    }

    [Fact]
    public void TheDeviceDumpDecodesABlockOfZeros()
    {
        // Byte 15 of each section is four tap fields, so a block that reads
        // 00 there decodes like any other.
        var unit = new ProHardwareEqTests.Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        IReadOnlyDictionary<string, string> dump = pro.DumpBlocks();
        Assert.False(dump.ContainsKey("sections"));
        Assert.Equal("no members; ducks nothing; inputs 00, fields 0000; maximizer 0; byte 99 00", dump["section0"]);
    }
}
