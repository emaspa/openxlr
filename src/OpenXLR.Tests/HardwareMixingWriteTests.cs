using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// What hardware mixing writes to the Wave XLR Pro: the sections the unit
/// builds as their cells and mask exactly, the Aux mix in section 36 with
/// the port's selector alone, byte 98, sections 0 and 36 put back before today's parts on
/// leaving, and the jacks' levels in block 0x0005, lowered before block
/// 0x0001 changes and raised after it. A plan the unit holds needs nothing.
/// </summary>
public sealed class HardwareMixingWriteTests
{
    private static byte[] Apply(ProMixPlan plan, byte[] held, out ProMixChanges changes)
    {
        var into = new byte[ProMixSections.Length];
        changes = plan.Apply(held, into);
        return into;
    }

    private static byte[] Unit(params (int Source, byte Cell, bool Member)[] sources)
    {
        var bytes = Enumerable.Repeat(ProMixSections.CellOff, ProMixPlan.UnitSectionLength).ToArray();
        ushort mask = 0;
        foreach ((int source, byte cell, bool member) in sources)
        {
            bytes[source] = cell;
            if (member) mask |= (ushort)(1 << source);
        }
        bytes[ProMixSections.MaskOffset] = (byte)mask;
        bytes[ProMixSections.MaskOffset + 1] = (byte)(mask >> 8);
        return bytes;
    }

    [Fact]
    public void AUnitSectionIsWrittenExactlyAndTheRestOfTheSectionStays()
    {
        byte[] held = ProMixPlanTests.Neutral();
        held[0 + 14] = 0x07;
        held[18 + 5] = 0x0c;
        byte[] section0 = Unit((ProMixSections.Xlr1, 0x00, true), (ProMixSections.SourceOfPair(1), 0xdc, true), (ProMixSections.LineIn, 0x0c, false));
        var plan = new ProMixPlan
        {
            UnitSections = new Dictionary<int, byte[]> { [0] = section0 },
            Section0Level = 0x00,
        };
        Assert.Equal(ProMixChanges.UnitSections | ProMixChanges.Section0Level, plan.Needs(held));
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.UnitSections | ProMixChanges.Section0Level, changes);
        Assert.True(changes.Commits());   // byte 98 goes with the commit, as a selector does
        Assert.Equal(section0, after[..ProMixPlan.UnitSectionLength]);
        Assert.Equal(0x07, after[14]);
        Assert.Equal(0xaa, after[15]);
        Assert.Equal(0x55, after[16]);
        Assert.Equal(0x0c, after[18 + 5]);        // another section as read
        Assert.Equal(0x00, after[ProMixSections.Section0LevelOffset]);
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
        // Cells alone apply live: no commit.
        Assert.False(ProMixChanges.UnitSections.Commits());
    }

    [Fact]
    public void TheAuxMixTheUnitBuildsIsWrittenAndThePortOnlyMovesItsSelector()
    {
        byte[] held = ProMixPlanTests.Neutral();
        held[ProMixSections.SelectorUsbAux] = ProMixSections.SelectorNothing;
        // The Aux mix from the channels' pairs: Game on pair 1 at -6 dB, a
        // channel on pair 5 at -12 dB, XLR 1 at 0 dB; no pair 5 stream at 0 dB.
        byte[] section36 = Unit((ProMixSections.Xlr1, 0x00, true), (ProMixSections.SourceOfPair(1), 0xf4, true),
            (ProMixSections.SourceOfPair(5), 0xe8, true));
        var plan = new ProMixPlan { UsbAux = true, UnitSections = new Dictionary<int, byte[]> { [36] = section36 } };
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.Selectors | ProMixChanges.UnitSections, changes);
        Assert.Equal(0x20, after[ProMixSections.SelectorUsbAux]);
        Assert.Equal(section36, after[36..(36 + ProMixPlan.UnitSectionLength)]);
        // Today's port rule (pair 5 at 0 dB, XLR 1 and Line In out) does not
        // touch the unit's Aux mix, so the plan holds once written.
        Assert.Equal(0xe8, after[36 + 9]);
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
        // The port off moves its selector alone and leaves the section.
        var off = new ProMixPlan { UsbAux = false };
        byte[] quiet = Apply(off, after, out ProMixChanges offChanges);
        Assert.Equal(ProMixChanges.Selectors, offChanges);
        Assert.Equal(ProMixSections.SelectorNothing, quiet[ProMixSections.SelectorUsbAux]);
        Assert.Equal(section36, quiet[36..(36 + ProMixPlan.UnitSectionLength)]);
    }

    [Fact]
    public void ASectionNoOutputPlaysIsNotAUnitSection()
        => Assert.Throws<ArgumentException>(() => Apply(new ProMixPlan { UnitSections = new Dictionary<int, byte[]> { [72] = Unit() } },
            ProMixPlanTests.Neutral(), out _));

    [Fact]
    public void ARestoreOfASectionTheUnitBuildsIsSkipped()
    {
        byte[] held = ProMixPlanTests.Neutral();
        byte[] section18 = Unit((ProMixSections.SourceOfPair(2), 0x00, true));
        var plan = new ProMixPlan
        {
            UnitSections = new Dictionary<int, byte[]> { [18] = section18 },
            Restore = new Dictionary<int, string> { [18] = "0C0000000000000000000000C10000AA" },
        };
        byte[] after = Apply(plan, held, out _);
        Assert.Equal(section18, after[18..(18 + ProMixPlan.UnitSectionLength)]);
    }

    [Fact]
    public void LeavingPutsSectionsZeroAndThirtySixBackBeforeTodaysParts()
    {
        // Before the mode: pair 1 in section 0 at 0 dB, XLR 1's path at -6 dB,
        // byte 98 at -21 dB; section 36 with pair 5 at 0 dB.
        byte[] before = ProMixPlanTests.Neutral();
        before[5] = 0x00; before[12] = 0x21; before[0] = 0xf4;
        before[36 + 9] = 0x00; before[36 + 13] = 0x02;
        string section0 = ProMixPlan.RestoreOf(before, 0), section36 = ProMixPlan.RestoreOf(before, 36);
        // In the mode: Game on pair 1 at -18 dB with Music on pair 2, byte 98 at 0 dB.
        byte[] mode = (byte[])before.Clone();
        Unit((ProMixSections.SourceOfPair(1), 0xdc, true), (ProMixSections.SourceOfPair(2), 0x00, true)).CopyTo(mode, 0);
        mode[98] = 0x00;
        mode[36 + 13] = 0x00;
        var leave = new ProMixPlan
        {
            Hp1 = true,
            MonitorReturn = true,
            Mic1 = new ProMicPath(true, -3),
            EntryRestore = new Dictionary<int, string> { [0] = section0, [36] = section36 },
            Section0Level = 0x54,
        };
        byte[] after = Apply(leave, mode, out ProMixChanges changes);
        Assert.True(changes.Commits());
        Assert.Equal(0x00, after[5]);                 // pair 1 back at 0 dB
        Assert.Equal(0x21, after[12]);                // pair 1 and XLR 1's path, Music gone
        Assert.Equal(0x00, after[13]);
        Assert.Equal(0xfa, after[0]);                 // today's level for the path, -3 dB, over the restore
        Assert.Equal(0x02, after[36 + 13]);
        Assert.Equal(0x54, after[98]);
        Assert.Equal(0x1e, after[90]);
        // Held: a second pass would change nothing.
        Assert.Equal(ProMixChanges.None, leave.Needs(after));
        Assert.NotEqual(ProMixChanges.None, leave.Needs(mode));
    }

    [Fact]
    public void AnEntryRestoreOnlyTakesSectionsZeroAndThirtySix()
        => Assert.Throws<ArgumentException>(() => new ProMixPlan
        {
            EntryRestore = new Dictionary<int, string> { [18] = "0C0000000000000000000000C10000AA" },
        }.Needs(ProMixPlanTests.Neutral()));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-21.0, 84)]
    [InlineData(-59.9, 240)]
    [InlineData(-75.0, 240)]
    [InlineData(3.0, 0)]
    [InlineData(double.NegativeInfinity, 240)]
    public void AJackLevelIsMinusFourTimesItsDecibels(double db, byte value)
        => Assert.Equal(value, ProJackLevels.ByteOf(db));

    [Fact]
    public void LevelsGoDownFirstAndUpAfter()
    {
        byte[] held = [40, 0x02, 60, 0, 20, 0, 0, 0];
        var levels = new ProJackLevels(-21, -5, null);
        Assert.Equal(new byte[] { 84, 0x02, 60, 0, 20, 0, 0, 0 }, levels.Lowered(held));
        Assert.Equal(new byte[] { 84, 0x02, 20, 0, 20, 0, 0, 0 }, levels.All(held));
        Assert.True(levels.HeldBy(new DeviceState { HpVolumeDb = -21, Hp2VolumeDb = -5, LineOutLevelDb = -40 }));
        Assert.False(levels.HeldBy(new DeviceState { HpVolumeDb = -20, Hp2VolumeDb = -5 }));
    }

    /// <summary>The driver writes a lowering before block 0x0001 and a raise after it, under one lock.</summary>
    [Fact]
    public void TheDriverLowersTheJacksBeforeTheMixesAndRaisesThemAfter()
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = ProMixPlanTests.Neutral();
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(ProDuckingDeviceTests.SettingsBlock);
        unit.Blocks[0x0005] = [40, 0x02, 40, 0, 20, 0, 0, 0];
        unit.Blocks[0x0008] = new byte[ProDucking.Length];
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.True(pro.Capabilities.HardwareMixing);
        unit.Calls.Clear();

        // Entering: Headphones 1 down 21 dB, byte 98 up 21 dB.
        ProMixWrite write = pro.WriteMix(new ProMixPlan { Section0Level = 0x00, JackLevels = new ProJackLevels(-31, null, null) })!;
        Assert.Equal(new ushort[] { 0x0005, 0x0001, 0x0003 },
            unit.Calls.Where(c => c.Type == 0x41).Select(c => c.Block));
        Assert.Equal(124, unit.Blocks[0x0005][0]);
        Assert.Equal("0 28->7c", write.Change5);
        Assert.Equal(-31, pro.ReadState().HpVolumeDb);

        // Leaving: byte 98 back, then the jack up.
        unit.Calls.Clear();
        pro.WriteMix(new ProMixPlan { Section0Level = 0x54, JackLevels = new ProJackLevels(-10, null, null) });
        Assert.Equal(new ushort[] { 0x0001, 0x0003, 0x0005 },
            unit.Calls.Where(c => c.Type == 0x41).Select(c => c.Block));
        Assert.Equal(40, unit.Blocks[0x0005][0]);

        // Nothing to change: nothing written.
        unit.Calls.Clear();
        Assert.False(pro.WriteMix(new ProMixPlan { Section0Level = 0x54, JackLevels = new ProJackLevels(-10, null, null) })!.Wrote);
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41);
    }

    [Fact]
    public void ThePairsOwnLinkTakesExactlyThatPair()
    {
        Assert.Equal(["a0", "a1"], PipeWireAdapter.SelectPair(["a0", "a1", "a2", "a3"], 0));
        Assert.Equal(["a6", "a7"], PipeWireAdapter.SelectPair(["a0", "a1", "a2", "a3", "a4", "a5", "a6", "a7"], 3));
    }

    [Fact]
    public void ASavedEntryIsKeptWholeOrDropped()
    {
        var entry = new HardwareEntry
        {
            Section0 = "000000000000000000000000200000aa".ToUpperInvariant(),
            Section36 = "000000000000000000000000000200AA",
            Section0Level = 0x54,
            Jacks = new() { ["hp1"] = -51, ["hp2"] = -81, ["lineout"] = -21 },
            OutputVolume = 0.7,
        };
        Assert.True(entry.Valid);
        MixerSettings kept = SavedMixerValidation.Sanitize(new MixerSettings { HardwareEntry = entry }, out IReadOnlyList<string> dropped);
        Assert.Equal(entry.Jacks, kept.HardwareEntry!.Jacks);
        Assert.Empty(dropped);
        MixerSettings bad = SavedMixerValidation.Sanitize(new MixerSettings { HardwareEntry = entry with { Section36 = "00" } }, out dropped);
        Assert.Null(bad.HardwareEntry);
        Assert.Contains("hardwareEntry: not readable, dropped", dropped);
        Assert.False((entry with { Jacks = new() { ["hp3"] = -1 } }).Valid);
        Assert.False((entry with { OutputVolume = 2 }).Valid);
    }

    /// <summary>Block 0x0001 with Wave Link's input ducking left in it: XLR 2 after the ducker in section 0, XLR 1 and XLR 2 in 18, mixed taps in 72.</summary>
    private static byte[] InputsDucked()
    {
        byte[] held = ProMixPlanTests.Neutral();
        held[0 + 15] = 0xae;
        held[18 + 15] = 0xaf;
        held[72 + 15] = 0x93;   // XLR 1 at 3, XLR 2 at 0, USB Aux at 1, Line In at 2
        return held;
    }

    private static void AssertInputsPutBack(byte[] held, byte[] after)
    {
        Assert.Equal(new byte[] { 0xaa, 0xaa, 0xaa, 0xaa, 0x92 }, ProMixSections.Starts.Select(s => after[s + 15]));
        for (int i = 0; i < after.Length; i++)
            if (!ProMixSections.Starts.Contains(i - 15)) Assert.Equal(held[i], after[i]);
    }

    [Fact]
    public void HardwareMixingPutsAnInputAfterTheDuckerBackInEverySection()
    {
        byte[] held = InputsDucked();
        // The unit already builds section 0 as wanted: only the taps change.
        var plan = new ProMixPlan { UnitSections = new Dictionary<int, byte[]> { [0] = held[..ProMixPlan.UnitSectionLength] } };
        Assert.Equal(ProMixChanges.UnitSections, plan.Needs(held));
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.UnitSections, changes);
        Assert.False(changes.Commits());
        AssertInputsPutBack(held, after);
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
    }

    [Fact]
    public void ADuckingWritePutsAnInputAfterTheDuckerBackInEverySection()
    {
        byte[] held = InputsDucked();
        var plan = new ProMixPlan { Ducking = ProDuckingTarget.Off };
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.DuckingFields, changes);
        Assert.False(changes.Commits());
        AssertInputsPutBack(held, after);
    }

    [Fact]
    public void APlanWithoutDuckingOrUnitSectionsLeavesTheTaps()
    {
        byte[] held = InputsDucked();
        Assert.Equal(ProMixChanges.None, ProMixPlan.Nothing.Needs(held));
        Assert.Equal(held, Apply(ProMixPlan.Nothing, held, out _));
    }

    [Fact]
    public void ARestoreKeptWithAnInputAfterTheDuckerPutsItBackAtTwo()
    {
        // A restore kept by an earlier version, with XLR 2 after the ducker.
        const string kept = "0C0000000000000000000000C10000AE";
        byte[] held = ProMixPlanTests.Neutral();
        var plan = new ProMixPlan { Restore = new Dictionary<int, string> { [18] = kept } };
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.FeedSections, changes);
        Assert.Equal(Convert.FromHexString("0C0000000000000000000000C10000AA"), after[18..34]);
        Assert.True(ProMixPlan.HoldsRestore(after, 18, kept));
        Assert.False(ProMixPlan.HoldsRestore(held, 18, kept));
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
        // What a restore keeps from a block is what it writes back.
        byte[] ducked = (byte[])after.Clone();
        ducked[18 + 15] = 0xae;
        Assert.Equal("0C0000000000000000000000C10000AA", ProMixPlan.RestoreOf(ducked, 18));
    }

    [Fact]
    public void HardwareMixingLeavesTheInputsTheDuckingIsMeantToHoldAndNeverSetsOne()
    {
        // The ducking posted ducks XLR 2 in sections 0 and 18; Wave Link left
        // XLR 1 after the ducker in 18 and 72, which goes back to 2.
        byte[] held = InputsDucked();
        var plan = new ProMixPlan
        {
            UnitSections = new Dictionary<int, byte[]> { [0] = held[..ProMixPlan.UnitSectionLength] },
            DuckedInputs = new Dictionary<int, int> { [0] = 0b10, [18] = 0b10, [36] = 0b10 },
        };
        byte[] after = Apply(plan, held, out ProMixChanges changes);
        Assert.Equal(ProMixChanges.UnitSections, changes);
        // Section 36's XLR 2 read at 2 stays at 2: only a ducking write sets 3.
        Assert.Equal(new byte[] { 0xae, 0xae, 0xaa, 0xaa, 0x92 }, ProMixSections.Starts.Select(s => after[s + 15]));
        // Held as it is now: no write every sweep.
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
        // With the ducking target in the same write, it sets its inputs as it wants them.
        var both = plan with { Ducking = ProDuckingTests.InSections(new() { [0] = [1], [36] = [1] }) with { Inputs = new HashSet<int> { 1 } } };
        after = Apply(both, held, out _);
        Assert.Equal(new byte[] { 0xae, 0xaa, 0xae, 0xaa, 0x92 }, ProMixSections.Starts.Select(s => after[s + 15]));
    }

    [Fact]
    public void ARestoreLeavesAnInputTheDuckingIsMeantToHold()
    {
        const string kept = "0C0000000000000000000000C10000AA";
        byte[] held = ProMixPlanTests.Neutral();
        held[18 + 15] = 0xae;   // XLR 2 ducked in section 18 by the ducking posted
        var plan = new ProMixPlan
        {
            Restore = new Dictionary<int, string> { [18] = kept },
            DuckedInputs = new Dictionary<int, int> { [18] = 0b10 },
        };
        byte[] after = Apply(plan, held, out _);
        Assert.Equal(Convert.FromHexString("0C0000000000000000000000C10000AE"), after[18..34]);
        Assert.Equal(ProMixChanges.None, plan.Needs(after));
        // Without that ducking the restore puts it back at 2.
        after = Apply(plan with { DuckedInputs = null }, held, out _);
        Assert.Equal(0xaa, after[18 + 15]);
    }
}
