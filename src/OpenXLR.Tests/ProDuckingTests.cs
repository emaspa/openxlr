using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's ducking bytes, against the blocks the Windows captures
/// of 2026-10-21 recorded (24 to 31): every preset in all twelve entries of
/// block 0x0008, the ducked flag set and cleared per entry with bit 1 and
/// the other unknown bits kept, both bits of a ducked pair's 2-bit field in
/// all three mix sections of block 0x0001 and the high bit alone cleared on
/// release, every other byte of that block kept, and the decode of what Wave
/// Link leaves behind.
/// </summary>
public sealed class ProDuckingTests
{
    /// <summary>Byte 6 of each entry in Wave Link's state with no key (capture 24, before the write).</summary>
    internal static readonly byte[] IdleFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00];

    /// <summary>Byte 6 after Wave Link made XLR 1 the key for Browser, Music, Game and System (pairs 1, 5, 6, 7).</summary>
    internal static readonly byte[] WaveLinkFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x01, 0x00, 0x03, 0x03, 0x01, 0x01, 0x01];

    internal static byte[] Block8(byte[] preset, byte[] flags)
    {
        var block = new byte[ProDucking.Length];
        for (int e = 0; e < ProDucking.EntryCount; e++)
        {
            preset.CopyTo(block, e * 8);
            block[e * 8 + 6] = flags[e];
        }
        return block;
    }

    internal static readonly byte[] MediumBytes = [0xf4, 0x0c, 0x50, 0x00, 0xb0, 0x04];
    internal static readonly byte[] LowLowSlowBytes = [0xfd, 0x06, 0x64, 0x00, 0xd0, 0x07];
    internal static readonly byte[] HighHighFastBytes = [0xe8, 0x18, 0x1e, 0x00, 0xc8, 0x00];

    /// <summary>
    /// A block 0x0001 with the bytes the protocol notes name: crossfade at 0,
    /// the jacks' Monitor return level at 5 (unity), the head membership
    /// bytes 12 and 13, the aa that opens each of the
    /// five sections at 15, 33, 51, 69 and 87 with 55 55 after it, the aux
    /// cells at 45, 48 and 49 and the output selectors at 90 to 93. Every
    /// other byte holds a pattern, so a write that touches it shows.
    /// </summary>
    internal static byte[] Block1(byte b16 = 0x55, byte b17 = 0x55)
    {
        var block = new byte[ProDucking.MixBlockLength];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)(0x80 + i * 7);
        block[0] = 0x64;
        block[5] = 0x00;
        block[12] = 0x24;
        // No USB pair but the Monitor's in the jacks' mix, as the daemon
        // leaves it; the desk unit held pairs 6 and 7 (0c) before that.
        block[13] = 0x00;
        foreach (int at in new[] { 15, 33, 51, 69, 87 })
        {
            block[at] = 0xaa;
            block[at + 1] = 0x55;
            block[at + 2] = 0x55;
        }
        foreach (int at in ProDucking.MixFieldOffsets)
        {
            block[at] = b16;
            block[at + 1] = b17;
        }
        block[45] = 0x00;
        block[48] = 0x01;
        block[49] = 0x02;
        block[90] = 0x23; block[91] = 0x23; block[92] = 0x20; block[93] = 0x23;
        return block;
    }

    internal static ProDuckingTarget On(params int[] pairs) => new(true, "medium", "medium", "medium", pairs.ToHashSet());

    private static ProDuckingTarget Off => ProDuckingTarget.Off;

    private static void AssertOnlyMixFieldsChanged(byte[] before, byte[] after)
    {
        for (int i = 0; i < before.Length; i++)
            if (!ProDucking.MixFieldOffsets.Any(at => i == at || i == at + 1))
                Assert.True(before[i] == after[i], $"block 0x0001 byte {i} changed from {before[i]:x2} to {after[i]:x2}");
    }

    [Fact]
    public void TheMonitorAndAuxPairsAreThoseTheGraphStreamsTo()
    {
        // PipeWireAdapter routes "#usbaux" to pair 5 and the jacks to pair 1
        // through these constants; ducking names the same pairs.
        Assert.Equal(1, ProReturnPairs.Monitor);
        Assert.Equal(5, ProReturnPairs.Aux);
        Assert.Equal(ProReturnPairs.Monitor, ProDucking.PairOf("monitor"));
        Assert.Equal(ProReturnPairs.Aux, ProDucking.PairOf("aux"));
    }

    [Fact]
    public void WaveLinksDuckingDecodesToItsFourPairs()
    {
        byte[] b8 = Block8(MediumBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);   // capture 24: 55 55 -> 5d fd in each section
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal(("medium", "medium", "medium"), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.Equal([1, 5, 6, 7], unit.Pairs);
        Assert.Equal([4, 5, 7, 8, 9, 10, 11], unit.Entries);

        DuckingReadback left = ProDucking.Decode(Block8(LowLowSlowBytes, WaveLinkFlags), b1);
        Assert.Equal(("low", "low", "slow"), (left.Sensitivity, left.Amount, left.Speed));
        DuckingReadback high = ProDucking.Decode(Block8(HighHighFastBytes, IdleFlags), Block1());
        Assert.Equal(("high", "high", "fast"), (high.Sensitivity, high.Amount, high.Speed));
        Assert.Empty(high.Pairs);
    }

    [Fact]
    public void OffClearsWhatWaveLinkLeftAndKeepsTheUnknownEntries()
    {
        // The unit as Wave Link left it on the desk: Low, Low, Slow on pairs 1, 5, 6 and 7.
        byte[] b8 = Block8(LowLowSlowBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, Off);

        // Wave Link's own state with no key: the 02 and 03 entries stay, the
        // presets are not written while ducking is off.
        Assert.Equal(Block8(LowLowSlowBytes, IdleFlags), b8);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Empty(unit.Pairs);
        Assert.Equal([4, 7, 8], unit.Entries);
        Assert.True(ProDucking.Holds(unit, Off));
    }

    [Theory]
    [InlineData(ProReturnPairs.Monitor, 0x5d, 0x55, 5)]
    [InlineData(ProReturnPairs.Aux, 0x55, 0x5d, 9)]
    public void OnePairIsDuckedInEveryMixSection(int pair, byte b16, byte b17, int entry)
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, On(pair));

        byte[] flags = (byte[])IdleFlags.Clone();
        flags[entry] |= 0x01;
        Assert.Equal(Block8(MediumBytes, flags), b8);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new[] { b16, b17 }, b1[at..(at + 2)]);
        // The fourth and fifth sections, and every other byte, stay.
        AssertOnlyMixFieldsChanged(before1, b1);
        Assert.Equal([pair], ProDucking.Decode(b8, b1).Pairs);
    }

    [Fact]
    public void BothOutputsDuckPairsOneAndFive()
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, On(ProReturnPairs.Monitor, ProReturnPairs.Aux));
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x5d, 0x5d }, b1[at..(at + 2)]);
        Assert.Equal([4, 5, 7, 8, 9], ProDucking.Decode(b8, b1).Entries);

        // On with no pairs, as for a setup whose mixes nobody hears: the
        // presets are written and nothing is ducked, the idle state byte for byte.
        ProDucking.Apply(b8, b1, On());
        Assert.Equal(Block8(MediumBytes, IdleFlags), b8);
        Assert.Equal(Block1(), b1);
        Assert.True(ProDucking.Holds(ProDucking.Decode(b8, b1), On()));
    }

    [Fact]
    public void OffDucksNoPairWhateverPairsItCarries()
    {
        byte[] b8 = Block8(LowLowSlowBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);
        ProDucking.Apply(b8, b1, Off with { Pairs = new HashSet<int> { 1, 5 } });
        Assert.Equal(Block8(LowLowSlowBytes, IdleFlags), b8);
        Assert.Empty(ProDucking.Decode(b8, b1).Pairs);
    }

    [Fact]
    public void TheJournalNamesThePairsAndPresetsOfATarget()
    {
        Assert.Equal("off", Off.ToString());
        Assert.Equal("on, pairs 1 5, sensitivity medium, amount medium, speed medium", On(5, 1).ToString());
        Assert.Equal("on, pairs none, sensitivity high, amount low, speed fast",
            (On() with { Sensitivity = "high", Amount = "low", Speed = "fast" }).ToString());
        Assert.Equal(On(1, 5), On(5, 1));
        Assert.NotEqual(On(1), On(1, 5));
        Assert.Equal("on, pairs 1 2 in section 0; 1 in section 18, sensitivity medium, amount medium, speed medium",
            InSections(new() { [18] = [1], [0] = [2, 1] }).ToString());
        Assert.Equal(InSections(new() { [0] = [1, 2], [18] = [1] }), InSections(new() { [18] = [1], [0] = [2, 1] }));
        Assert.NotEqual(InSections(new() { [0] = [1] }), On(1));
        Assert.NotEqual(InSections(new() { [0] = [1] }), InSections(new() { [18] = [1] }));
    }

    /// <summary>A hardware mixing target: each section's pairs, the union as its pairs.</summary>
    internal static ProDuckingTarget InSections(Dictionary<int, int[]> sections)
        => On([.. sections.Values.SelectMany(p => p).Distinct()]) with
        {
            Sections = sections.ToDictionary(p => p.Key, p => (IReadOnlySet<int>)p.Value.ToHashSet()),
        };

    private static void AssertOnlySectionFieldsChanged(byte[] before, byte[] after)
    {
        for (int i = 0; i < before.Length; i++)
            if (!ProDucking.SectionFieldOffsets.Any(at => i == at || i == at + 1))
                Assert.True(before[i] == after[i], $"block 0x0001 byte {i} changed from {before[i]:x2} to {after[i]:x2}");
    }

    [Fact]
    public void InHardwareMixingEachPairIsDuckedOnlyInTheSectionsNamedForIt()
    {
        // Music (pair 2), Game (1) and SFX (6) ducked in Monitor A on section
        // 0, only Game in Monitor B on section 18.
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        byte[] before1 = (byte[])b1.Clone();
        ProDuckingTarget target = InSections(new() { [0] = [1, 2, 6], [18] = [1] });
        ProDucking.Apply(b8, b1, target);

        Assert.Equal(new byte[] { 0x7d, 0x75 }, b1[16..18]);
        Assert.Equal(new byte[] { 0x5d, 0x55 }, b1[34..36]);
        foreach (int at in new[] { 52, 70, 88 }) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
        AssertOnlySectionFieldsChanged(before1, b1);
        // One depth for every pair: the entries of pairs 1, 2 and 6 flagged.
        byte[] flags = (byte[])IdleFlags.Clone();
        foreach (int entry in new[] { 5, 6, 10 }) flags[entry] |= 0x01;
        Assert.Equal(Block8(MediumBytes, flags), b8);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal([1, 2, 6], unit.Pairs);
        Assert.True(ProDucking.Holds(unit, target));
        Assert.False(ProDucking.Holds(unit, InSections(new() { [0] = [1, 2, 6], [18] = [1, 2] })));
        Assert.False(ProDucking.Holds(unit, On(1, 2, 6)));
    }

    [Fact]
    public void ASectionsTargetCoversTheFifthSectionAndAWholeOutputTargetLeavesIt()
    {
        // Every field of every section at 11, as if another program ducked everything.
        byte[] all = Block1(0xff, 0xff);
        all[88] = all[89] = 0xff;

        byte[] b1 = (byte[])all.Clone();
        ProDucking.ApplyFields(b1, On(ProReturnPairs.Monitor));
        Assert.Equal(new byte[] { 0x5d, 0x55 }, b1[16..18]);
        Assert.Equal(new byte[] { 0xff, 0xff }, b1[88..90]);

        b1 = (byte[])all.Clone();
        ProDucking.ApplyFields(b1, InSections(new() { [54] = [3] }));
        foreach (int at in new[] { 16, 34, 52, 88 }) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
        Assert.Equal(new byte[] { 0xd5, 0x55 }, b1[70..72]);

        // Off with sections clears the high bit in all five.
        b1 = (byte[])all.Clone();
        ProDucking.ApplyFields(b1, InSections(new() { [54] = [3] }) with { Enabled = false });
        foreach (int at in ProDucking.SectionFieldOffsets) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
    }

    [Fact]
    public void ASectionsTargetNamesOnlySectionStartsAndItsOwnPairs()
    {
        Assert.Null(ProDucking.Valid(InSections(new() { [0] = [1], [72] = [7] })));
        Assert.NotNull(ProDucking.Valid(InSections(new() { [5] = [1] })));
        Assert.NotNull(ProDucking.Valid(On(1) with { Sections = new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { 2 } } }));
        Assert.Throws<ArgumentException>(() => ProDucking.ApplyFields(Block1(), InSections(new() { [17] = [1] })));
    }

    public static TheoryData<string, string, string> Presets()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string s in ProDucking.Sensitivities)
            foreach (string a in ProDucking.Amounts)
                foreach (string v in ProDucking.Speeds)
                    data.Add(s, a, v);
        return data;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void EveryPresetGoesToAllTwelveEntriesAsWaveLinkWritesIt(string sensitivity, string amount, string speed)
    {
        // The table from captures 25 to 31: each field moved alone, in every entry.
        byte threshold = sensitivity switch { "low" => 0xfd, "medium" => 0xf4, _ => 0xe8 };
        byte depth = amount switch { "low" => 0x06, "medium" => 0x0c, _ => 0x18 };
        byte[] times = speed switch
        {
            "slow" => [0x64, 0x00, 0xd0, 0x07],
            "medium" => [0x50, 0x00, 0xb0, 0x04],
            _ => [0x1e, 0x00, 0xc8, 0x00],
        };
        var s = new ProDuckingTarget(true, sensitivity, amount, speed, new HashSet<int>());
        byte[] b8 = Block8(LowLowSlowBytes, IdleFlags);
        for (int e = 0; e < 12; e++) b8[e * 8 + 7] = (byte)(0x40 + e);   // byte 7 is not ours either
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, s);
        for (int e = 0; e < 12; e++)
        {
            Assert.Equal(new[] { threshold, depth, times[0], times[1], times[2], times[3] }, b8[(e * 8)..(e * 8 + 6)]);
            Assert.Equal((byte)(0x40 + e), b8[e * 8 + 7]);
        }
        Assert.Equal(ProDucking.SettingBytes(sensitivity, amount, speed), b8[..6]);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal((sensitivity, amount, speed), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.True(ProDucking.Holds(unit, s));
    }

    [Fact]
    public void UnknownBitsAreKept()
    {
        // Flag bits 2 to 7 on every entry, bit 1 on some; fields whose low bit is clear.
        byte[] flags = [0xfc, 0xfe, 0xf4, 0x85, 0xff, 0x7c, 0x04, 0xfe, 0x00, 0xfd, 0x10, 0x03];
        byte[] b8 = Block8(MediumBytes, flags);
        byte[] b1 = Block1(0x00, 0xa0);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, On(1, 5));
        byte[] wanted = (byte[])flags.Clone();
        wanted[5] |= 0x01;   // pair 1
        wanted[9] |= 0x01;   // pair 5
        // Entries without bit 1 lose a ducked bit they should not have; those with it keep theirs.
        wanted[3] = 0x84;    // Line In: bit 1 clear, so its ducked bit goes
        wanted[11] = 0x03;   // pair 7 has bit 1: kept as read
        Assert.Equal(wanted, Enumerable.Range(0, 12).Select(e => b8[e * 8 + 6]));
        // Both bits set for pairs 1 and 5, the high bit cleared for 6 and 7 (0xa0: field 6 = 10, field 7 = 10).
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x0c, 0x0c }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
    }

    [Fact]
    public void AFieldWhoseLowBitIsClearGetsBothBitsAndKeepsItsLowBitOnRelease()
    {
        // The desk unit under OpenXLR: pair 1's field reads 00 (0x51), and
        // setting its high bit alone gave 10, which the unit ignored. The
        // jacks dipped only at 11 (desk probe of 2026-10-23).
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1(0x51, 0x01);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, On(ProReturnPairs.Monitor));
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x5d, 0x01 }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
        Assert.Equal([ProReturnPairs.Monitor], ProDucking.Decode(b8, b1).Pairs);

        ProDucking.Apply(b8, b1, Off);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x55, 0x01 }, b1[at..(at + 2)]);
        Assert.Empty(ProDucking.Decode(b8, b1).Pairs);
    }

    [Fact]
    public void AHighBitAloneIsNotDuckedAndIsRewritten()
    {
        // What the daemon wrote before it set both bits: pair 1 at 10.
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        b8[5 * 8 + 6] |= 0x01;
        byte[] b1 = Block1(0x59, 0x01);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Empty(unit.Pairs);
        Assert.False(ProDucking.Holds(unit, On(ProReturnPairs.Monitor)));
        ProDucking.Apply(b8, b1, On(ProReturnPairs.Monitor));
        Assert.True(ProDucking.Holds(ProDucking.Decode(b8, b1), On(ProReturnPairs.Monitor)));
    }

    [Fact]
    public void CustomNumbersGoIntoTheSameBytesAndReadBackAsNumbers()
    {
        // Never seen on a unit: the same layout with values no preset writes.
        var target = new ProDuckingTarget(true, -37, 31, DuckingSpeed.Times(7, 4321), new HashSet<int> { 1 });
        Assert.Null(ProDucking.Valid(target));
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, target);
        for (int e = 0; e < 12; e++)
            Assert.Equal(new byte[] { 0xdb, 0x1f, 0x07, 0x00, 0xe1, 0x10 }, b8[(e * 8)..(e * 8 + 6)]);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal((DuckingLevel.Number(-37), DuckingLevel.Number(31), DuckingSpeed.Times(7, 4321)), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.True(ProDucking.Holds(unit, target));
        Assert.False(ProDucking.Holds(unit, target with { Amount = 30 }));

        // The bounds fit the bytes: a signed threshold, one depth byte, little-endian times.
        target = target with { Sensitivity = -60, Amount = 40, Speed = DuckingSpeed.Times(1000, 10000) };
        ProDucking.Apply(b8, b1, target);
        Assert.Equal(new byte[] { 0xc4, 0x28, 0xe8, 0x03, 0x10, 0x27 }, b8[..6]);
        Assert.True(ProDucking.Holds(ProDucking.Decode(b8, b1), target));

        // A number a preset writes reads back as the preset's word, a word written reads the same.
        ProDucking.Apply(b8, b1, target with { Sensitivity = -12, Amount = "high", Speed = DuckingSpeed.Times(30, 200) });
        unit = ProDucking.Decode(b8, b1);
        Assert.Equal(("medium", "high", "fast"), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.True(ProDucking.Holds(unit, target with { Sensitivity = "medium", Amount = 24, Speed = "fast" }));
    }

    [Fact]
    public void TheReadbackSerializesNumbersAndTheSpeedObject()
    {
        var readback = new DuckingReadback(-30, "custom", DuckingSpeed.Times(45, 900), [1], [4]);
        string json = System.Text.Json.JsonSerializer.Serialize(readback,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Equal("""{"sensitivity":-30,"amount":"custom","speed":{"attack":45,"release":900},"pairs":[1],"entries":[4]}""", json);
    }

    [Theory]
    [InlineData(1, 12, 80, 1200)]
    [InlineData(-61, 12, 80, 1200)]
    [InlineData(-12, 0, 80, 1200)]
    [InlineData(-12, 41, 80, 1200)]
    [InlineData(-12, 12, 0, 1200)]
    [InlineData(-12, 12, 1001, 1200)]
    [InlineData(-12, 12, 80, 9)]
    [InlineData(-12, 12, 80, 10001)]
    public void NumbersOutOfRangeAreRefused(int threshold, int depth, int attack, int release)
    {
        var target = new ProDuckingTarget(true, threshold, depth, DuckingSpeed.Times(attack, release), new HashSet<int> { 1 });
        Assert.NotNull(ProDucking.Valid(target));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[96], new byte[108], target));
    }

    [Fact]
    public void DisagreeingEntriesReadAsCustomAndAreNotHeld()
    {
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        b8[8 * 3] = 0xe8;               // one entry more sensitive than the rest
        b8[8 * 5 + 1] = 0x09;           // a depth no preset writes, in one entry
        b8[8 * 7 + 4] = 0xb1;           // a release that is not 1200
        DuckingReadback unit = ProDucking.Decode(b8, Block1());
        Assert.Equal(("custom", "custom", "custom"), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.False(ProDucking.Holds(unit, On(1)));
        // Off leaves the presets alone, so that unit holds "off" all the same.
        Assert.True(ProDucking.Holds(unit, Off));
        Assert.False(ProDucking.Holds(new DuckingReadback("medium", "medium", "medium", [], []), Off));
    }

    [Theory]
    [InlineData(8, "medium", "medium", "medium")]
    [InlineData(-1, "medium", "medium", "medium")]
    [InlineData(1, "loud", "medium", "medium")]
    [InlineData(1, "medium", "slow", "medium")]
    [InlineData(1, "medium", "medium", "high")]
    [InlineData(1, "custom", "medium", "medium")]
    public void ValuesTheUnitDoesNotTakeAreRefused(int pair, string sensitivity, string amount, string speed)
    {
        var target = new ProDuckingTarget(true, sensitivity, amount, speed, new HashSet<int> { pair });
        Assert.NotNull(ProDucking.Valid(target));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[96], new byte[108], target));
        Assert.False(ProDucking.Holds(ProDucking.Decode(new byte[96], new byte[108]), target));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[95], new byte[108], On()));
        Assert.Throws<ArgumentException>(() => ProDucking.Apply(new byte[96], new byte[107], On()));
    }
}
