using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's ducking bytes, against the blocks the Windows captures
/// of 2026-10-21 recorded (24 to 31) and the key masks measured on
/// 2026-11-10: every preset in all twelve entries of block 0x0008, each
/// entry's key mask written exactly (XLR 1 alone on a ducked pair, no key
/// elsewhere, Wave Link's leftover keys cleared), both bits of a ducked
/// pair's 2-bit field in all three mix sections of block 0x0001 and the
/// high bit alone cleared on release, no input left after the ducker, every
/// other byte of that block kept, and the decode of what Wave Link leaves
/// behind.
/// </summary>
public sealed class ProDuckingTests
{
    /// <summary>
    /// Byte 6 of each entry, the key mask's low byte, in Wave Link's state
    /// with no key chosen (capture 24, before the write): leftover keys, XLR
    /// 2 (02) and XLR 1 with XLR 2 (03).
    /// </summary>
    internal static readonly byte[] IdleFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00];

    /// <summary>Byte 6 after Wave Link made XLR 1 the key for Browser, Music, Game and System (pairs 1, 5, 6, 7).</summary>
    internal static readonly byte[] WaveLinkFlags = [0x00, 0x02, 0x00, 0x00, 0x03, 0x01, 0x00, 0x03, 0x03, 0x01, 0x01, 0x01];

    /// <summary>Byte 6 with no key on any entry, as OpenXLR leaves a unit with nothing ducked.</summary>
    internal static readonly byte[] NoKeys = new byte[12];

    /// <summary>Byte 6 with XLR 1 alone keying the entries given, as OpenXLR writes them.</summary>
    internal static byte[] Xlr1Keys(params int[] entries)
    {
        var keys = new byte[12];
        foreach (int e in entries) keys[e] = 0x01;
        return keys;
    }

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
    /// bytes 12 and 13, the inputs' tap fields at aa in each of the five
    /// sections (15, 33, 51, 69 and 87) with the pairs' 55 55 after them, the aux
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
    public void OffClearsEveryKeyWaveLinkLeft()
    {
        // The unit as Wave Link left it on the desk: Low, Low, Slow on pairs 1, 5, 6 and 7.
        byte[] b8 = Block8(LowLowSlowBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);
        byte[] before1 = (byte[])b1.Clone();
        ProDucking.Apply(b8, b1, Off);

        // No key on any entry, the leftover XLR 2 keys (02, 03) included;
        // the presets are not written while ducking is off.
        Assert.Equal(Block8(LowLowSlowBytes, NoKeys), b8);
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x55, 0x55 }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Empty(unit.Pairs);
        Assert.Empty(unit.Entries);
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

        Assert.Equal(Block8(MediumBytes, Xlr1Keys(entry)), b8);
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
        Assert.Equal([5, 9], ProDucking.Decode(b8, b1).Entries);

        // On with no pairs, as for a setup whose mixes nobody hears: the
        // presets are written and nothing is ducked or keyed.
        ProDucking.Apply(b8, b1, On());
        Assert.Equal(Block8(MediumBytes, NoKeys), b8);
        Assert.Equal(Block1(), b1);
        Assert.True(ProDucking.Holds(ProDucking.Decode(b8, b1), On()));
    }

    [Fact]
    public void OffDucksNoPairWhateverPairsItCarries()
    {
        byte[] b8 = Block8(LowLowSlowBytes, WaveLinkFlags);
        byte[] b1 = Block1(0x5d, 0xfd);
        ProDucking.Apply(b8, b1, Off with { Pairs = new HashSet<int> { 1, 5 } });
        Assert.Equal(Block8(LowLowSlowBytes, NoKeys), b8);
        Assert.Empty(ProDucking.Decode(b8, b1).Pairs);
    }

    [Fact]
    public void TheJournalNamesThePairsAndPresetsOfATarget()
    {
        Assert.Equal("off", Off.ToString());
        Assert.Equal("on, pairs 1 5, keys XLR 1, sensitivity medium, amount medium, speed medium", On(5, 1).ToString());
        Assert.Equal("on, pairs 1 5, inputs XLR 2 and Line In, keys XLR 1, pair 4, sensitivity medium, amount medium, speed medium",
            (On(5, 1) with { Inputs = new HashSet<int> { 3, 1 }, Keys = 1 << 0 | 1 << 8 }).ToString());
        Assert.NotEqual(On(1), On(1) with { Keys = 1 << 1 });
        Assert.NotEqual(On(1), On(1) with { Inputs = new HashSet<int> { 1 } });
        Assert.Equal(On(1) with { Inputs = new HashSet<int> { 1, 3 } }, On(1) with { Inputs = new HashSet<int> { 3, 1 } });
        Assert.Equal("on, pairs none, keys XLR 1, sensitivity high, amount low, speed fast",
            (On() with { Sensitivity = "high", Amount = "low", Speed = "fast" }).ToString());
        Assert.Equal(On(1, 5), On(5, 1));
        Assert.NotEqual(On(1), On(1, 5));
        Assert.Equal("on, pairs 1 2 in section 0; 1 in section 18, keys XLR 1, sensitivity medium, amount medium, speed medium",
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
        // One depth for every pair: the entries of pairs 1, 2 and 6 keyed by XLR 1.
        Assert.Equal(Block8(MediumBytes, Xlr1Keys(5, 6, 10)), b8);
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
        for (int e = 0; e < 12; e++) b8[e * 8 + 7] = (byte)(0x40 + e);   // the key masks' high bytes: pairs as keys
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, s);
        for (int e = 0; e < 12; e++)
        {
            Assert.Equal(new[] { threshold, depth, times[0], times[1], times[2], times[3] }, b8[(e * 8)..(e * 8 + 6)]);
            Assert.Equal(new byte[] { 0x00, 0x00 }, b8[(e * 8 + 6)..(e * 8 + 8)]);
        }
        Assert.Equal(ProDucking.SettingBytes(sensitivity, amount, speed), b8[..6]);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal((sensitivity, amount, speed), (unit.Sensitivity, unit.Amount, unit.Speed));
        Assert.True(ProDucking.Holds(unit, s));
    }

    [Fact]
    public void TheKeyMaskIsWrittenExactlyAndLeftoverKeysAreCleared()
    {
        // Keys in both bytes of every mask: XLR 2 (bit 1), Music (pair 5,
        // bit 9) and others, as Wave Link leaves them (it never clears a
        // bit); 0x0203 on pairs 1 and 5 keyed them by XLR 1, XLR 2 and Music
        // on the desk. Fields whose low bit is clear.
        ushort[] masks = [0x00fc, 0x02fe, 0x00f4, 0x0f85, 0x0203, 0x0203, 0x0004, 0xfffe, 0x0200, 0x0203, 0x0010, 0x0003];
        byte[] b8 = Block8(MediumBytes, NoKeys);
        for (int e = 0; e < 12; e++)
        {
            b8[e * 8 + 6] = (byte)masks[e];
            b8[e * 8 + 7] = (byte)(masks[e] >> 8);
        }
        byte[] b1 = Block1(0x00, 0xa0);
        byte[] before1 = (byte[])b1.Clone();
        DuckingReadback left = ProDucking.Decode(b8, b1);
        // Every entry holds a key beside its own bit, and between them every bit is set.
        Assert.Equal(Enumerable.Range(0, 12), left.Entries);
        Assert.Equal(Enumerable.Range(0, 16), left.Keys);
        Assert.False(ProDucking.Holds(left, On(1, 5)));

        ProDucking.Apply(b8, b1, On(1, 5));
        // XLR 1 alone on pairs 1 and 5 (entries 5 and 9), no key anywhere else.
        for (int e = 0; e < 12; e++)
            Assert.Equal(e is 5 or 9 ? new byte[] { 0x01, 0x00 } : new byte[] { 0x00, 0x00 }, b8[(e * 8 + 6)..(e * 8 + 8)]);
        Assert.Equal(Block8(MediumBytes, Xlr1Keys(5, 9)), b8);
        // Both bits set for pairs 1 and 5, the high bit cleared for 6 and 7 (0xa0: field 6 = 10, field 7 = 10).
        foreach (int at in ProDucking.MixFieldOffsets) Assert.Equal(new byte[] { 0x0c, 0x0c }, b1[at..(at + 2)]);
        AssertOnlyMixFieldsChanged(before1, b1);
        Assert.Equal([5, 9], ProDucking.Decode(b8, b1).Entries);
    }

    [Fact]
    public void AUnitWrittenByOpenXlrReadsBackEqualAndOneWithALeftoverKeyDoesNot()
    {
        // OpenXLR's own write of pair 1, from a unit with nothing on it.
        byte[] fresh8 = Block8(MediumBytes, NoKeys);
        byte[] fresh1 = Block1();
        ProDucking.Apply(fresh8, fresh1, On(ProReturnPairs.Monitor));
        DuckingReadback written = ProDucking.Decode(fresh8, fresh1);
        Assert.True(ProDucking.Holds(written, On(ProReturnPairs.Monitor)));

        // The same write over Wave Link's leftovers reads back byte for byte the same.
        byte[] left8 = Block8(MediumBytes, IdleFlags);
        left8[5 * 8 + 7] = 0x02;    // Music (pair 5, bit 9) left as a key of pair 1 too
        byte[] left1 = Block1();
        ProDucking.Apply(left8, left1, On(ProReturnPairs.Monitor));
        Assert.Equal(written, ProDucking.Decode(left8, left1));

        // The pairs and the entries XLR 1 keys are the same with an XLR 2 key
        // beside it, but the unit does not hold OpenXLR's setting: talking
        // into XLR 2 would duck pair 1.
        fresh8[5 * 8 + 6] = 0x03;
        DuckingReadback xlr2 = ProDucking.Decode(fresh8, fresh1);
        Assert.Equal(written.Pairs, xlr2.Pairs);
        Assert.Equal(written.Entries, xlr2.Entries);
        Assert.NotEqual(written, xlr2);
        Assert.False(ProDucking.Holds(xlr2, On(ProReturnPairs.Monitor)));
        fresh8[5 * 8 + 6] = 0x01;
        fresh8[5 * 8 + 7] = 0x02;
        Assert.False(ProDucking.Holds(ProDucking.Decode(fresh8, fresh1), On(ProReturnPairs.Monitor)));
        // A key on an entry OpenXLR does not duck is not held either.
        fresh8[5 * 8 + 7] = 0x00;
        fresh8[1 * 8 + 6] = 0x01;
        Assert.False(ProDucking.Holds(ProDucking.Decode(fresh8, fresh1), On(ProReturnPairs.Monitor)));
    }

    [Theory]
    [InlineData(0xae, 0xaa)]   // Wave Link made XLR 2 ducked
    [InlineData(0xaf, 0xaa)]   // and XLR 1
    [InlineData(0xff, 0xaa)]
    [InlineData(0x93, 0x92)]   // XLR 1 at 3, XLR 2 at 0, USB Aux at 1, Line In at 2
    [InlineData(0xaa, 0xaa)]
    [InlineData(0x55, 0x55)]
    [InlineData(0x00, 0x00)]
    public void EveryWritePutsAnInputAfterTheDuckerBackAndKeepsTheOtherTaps(byte read, byte written)
    {
        byte[] b8 = Block8(MediumBytes, NoKeys);
        byte[] b1 = Block1();
        foreach (int s in ProMixSections.Starts) b1[s + 15] = read;
        byte[] before1 = (byte[])b1.Clone();
        Assert.Null(ProMixSections.Check(b1));
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal(read == written, ProDucking.Holds(unit, Off));
        Assert.Equal(read == written, ProDucking.Holds(unit, On()));

        foreach (ProDuckingTarget target in new[] { Off, On(ProReturnPairs.Monitor), InSections(new() { [54] = [3] }) })
        {
            b1 = (byte[])before1.Clone();
            ProDucking.Apply(b8, b1, target);
            foreach (int s in ProMixSections.Starts) Assert.Equal(written, b1[s + 15]);
            for (int i = 0; i < b1.Length; i++)
                if (!ProDucking.SectionFieldOffsets.Any(at => i == at || i == at + 1 || i == at - 1))
                    Assert.True(before1[i] == b1[i], $"block 0x0001 byte {i} changed from {before1[i]:x2} to {b1[i]:x2}");
            Assert.True(ProDucking.Holds(ProDucking.Decode(b8, b1), target));
        }
    }

    [Fact]
    public void AnInputAfterTheDuckerInOneSectionIsNotHeld()
    {
        byte[] b8 = Block8(MediumBytes, NoKeys);
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, On(ProReturnPairs.Monitor));
        DuckingReadback held = ProDucking.Decode(b8, b1);
        Assert.True(ProDucking.Holds(held, On(ProReturnPairs.Monitor)));
        b1[72 + 15] = 0xae;   // the fifth section, which a whole-output target does not duck in
        DuckingReadback moved = ProDucking.Decode(b8, b1);
        Assert.NotEqual(held, moved);
        Assert.False(ProDucking.Holds(moved, On(ProReturnPairs.Monitor)));
        ProDucking.Apply(b8, b1, On(ProReturnPairs.Monitor));
        Assert.Equal(held, ProDucking.Decode(b8, b1));
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
        Assert.Equal("""{"sensitivity":-30,"amount":"custom","speed":{"attack":45,"release":900},"pairs":[1],"entries":[4],"inputs":[],"keys":[]}""", json);
        json = System.Text.Json.JsonSerializer.Serialize(readback with { Inputs = [1], Keys = [0, 9] },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.EndsWith("\"inputs\":[1],\"keys\":[0,9]}", json);
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
        byte[] b8 = Block8(MediumBytes, NoKeys);
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

    /// <summary>The key mask an entry holds in block 0x0008, LE16.</summary>
    private static int MaskOf(byte[] b8, int entry) => b8[entry * 8 + 6] | b8[entry * 8 + 7] << 8;

    [Fact]
    public void SeveralKeysAreWrittenAsTheirUnionLessEachEntrysOwnBit()
    {
        // Keyed by XLR 1, XLR 2 and Voice Chat on pair 4 (source 8); pairs 1
        // and 4 ducked, and XLR 2 ducked as an input: its own bit is left off
        // its own entry, as is pair 4's on entry 8.
        ushort keys = 1 << 0 | 1 << 1 | 1 << 8;
        var target = On(1, 4) with { Keys = keys, Inputs = new HashSet<int> { 1 } };
        byte[] b8 = Block8(MediumBytes, IdleFlags);
        for (int e = 0; e < 12; e++) b8[e * 8 + 7] = 0x0f;   // leftover keys in the high bytes too
        byte[] b1 = Block1();
        ProDucking.Apply(b8, b1, target);
        for (int e = 0; e < 12; e++)
        {
            int wanted = e switch { 1 => keys & ~(1 << 1), 5 => keys, 8 => keys & ~(1 << 8), _ => 0 };
            Assert.Equal(wanted, MaskOf(b8, e));
        }
        // XLR 2 at 3 in section 0, the section pair 1 plays as a whole output
        // (pair 4 plays none), every other input field as read.
        Assert.Equal(0xae, b1[15]);
        foreach (int at in new[] { 33, 51, 69, 87 }) Assert.Equal(0xaa, b1[at]);
        DuckingReadback unit = ProDucking.Decode(b8, b1);
        Assert.Equal([1, 5, 8], unit.Entries);
        Assert.Equal([0, 1, 8], unit.Keys);
        Assert.Equal([1], unit.Inputs);
        Assert.Equal([1, 4], unit.Pairs);
        Assert.True(ProDucking.Holds(unit, target));
        // Another key beside them is not held, and neither is a missing input.
        Assert.False(ProDucking.Holds(unit, target with { Keys = 1 << 0 | 1 << 1 }));
        Assert.False(ProDucking.Holds(unit, target with { Inputs = new HashSet<int>() }));

        // Off clears every mask and puts the input back to 2.
        ProDucking.Apply(b8, b1, Off);
        for (int e = 0; e < 12; e++) Assert.Equal(0, MaskOf(b8, e));
        foreach (int at in new[] { 15, 33, 51, 69, 87 }) Assert.Equal(0xaa, b1[at]);
        Assert.Empty(ProDucking.Decode(b8, b1).Inputs);
    }

    [Fact]
    public void AnInputIsDuckedInEverySectionAPairOfTheTargetPlaysAsAWholeOutput()
    {
        // Pairs 1, 2, 3 and 5: the jacks' three feeds and the Aux mix.
        var target = On(1, 2, 3, 5) with { Inputs = new HashSet<int> { 2, 3 } };
        Assert.Equal(new Dictionary<int, int> { [0] = 0b1100, [18] = 0b1100, [36] = 0b1100, [54] = 0b1100 }, ProDucking.InputSections(target));
        byte[] b8 = Block8(MediumBytes, NoKeys), b1 = Block1();
        ProDucking.Apply(b8, b1, target);
        // USB Aux and Line In (fields 2 and 3) at 3: aa becomes fa.
        foreach (int at in new[] { 15, 33, 51, 69 }) Assert.Equal(0xfa, b1[at]);
        Assert.Equal(0xaa, b1[87]);   // section 72 plays no pair of the target
        Assert.Equal(1, MaskOf(b8, 2));
        Assert.Equal(1, MaskOf(b8, 3));
        Assert.Equal([2, 3], ProDucking.Decode(b8, b1).Inputs);

        // Pair 1 alone ducks the inputs in section 0 alone; no pair, nowhere,
        // and then the inputs' entries take no key either.
        Assert.Equal(new Dictionary<int, int> { [0] = 0b1100 }, ProDucking.InputSections(On(1) with { Inputs = new HashSet<int> { 2, 3 } }));
        var none = On() with { Inputs = new HashSet<int> { 2, 3 } };
        Assert.Empty(ProDucking.InputSections(none));
        ProDucking.Apply(b8, b1, none);
        Assert.Equal(0, MaskOf(b8, 2));
        foreach (int at in new[] { 15, 33, 51, 69, 87 }) Assert.Equal(0xaa, b1[at]);
        // Switched off, a target ducks no input.
        Assert.Empty(ProDucking.InputSections(target with { Enabled = false }));
    }

    [Fact]
    public void InHardwareMixingAnInputIsDuckedInTheSectionsTheTargetNames()
    {
        // Section 18 holds no ducked pair but is a target mix's section all the same.
        var target = InSections(new() { [0] = [2], [18] = [] }) with { Inputs = new HashSet<int> { 0, 1 } };
        Assert.Equal(new Dictionary<int, int> { [0] = 0b11, [18] = 0b11 }, ProDucking.InputSections(target));
        byte[] b8 = Block8(MediumBytes, NoKeys), b1 = Block1();
        b1[51] = 0xab;   // XLR 1 left at 3 in section 36 by another program
        ProDucking.Apply(b8, b1, target with { Keys = 1 << ProMixSections.FirstPair + 4 });
        Assert.Equal(0xaf, b1[15]);
        Assert.Equal(0xaf, b1[33]);
        Assert.Equal(0xaa, b1[51]);
        Assert.Equal(1 << 8, MaskOf(b8, 0));
        Assert.Equal(1 << 8, MaskOf(b8, 1));
        Assert.Equal(1 << 8, MaskOf(b8, 6));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void AnInputOrKeyTheUnitDoesNotHaveIsRefused(int input)
    {
        Assert.NotNull(ProDucking.Valid(On(1) with { Inputs = new HashSet<int> { input } }));
        Assert.NotNull(ProDucking.Valid(On(1) with { Keys = 1 << 12 }));
        Assert.Null(ProDucking.Valid(On(1) with { Keys = ProDucking.AllSourcesMask, Inputs = new HashSet<int> { 0, 1, 2, 3 } }));
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
