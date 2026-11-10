using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's meters in block 0x0002: the codec against synthetic
/// blocks (the length check, the level law with its floor, the reductions,
/// each ducker and level channel at its offset, the diagnostics lines), the
/// driver's one read, and the diagnostics.
/// </summary>
public sealed class ProMetersTests
{
    /// <summary>A block with nothing playing and nothing ducked: every level at the floor, every reduction 0.</summary>
    internal static byte[] Idle()
    {
        byte[] b = new byte[ProMeters.Length];
        for (int m = 0; m < ProMeters.MaximizerCount; m++) b[ProMeters.MaximizerOffset + 2 * m + 1] = ProMeters.Floor;
        for (int n = 0; n < ProMeters.DuckerCount; n++)
            for (int i = 1; i < ProMeters.DuckerLength; i++) b[ProMeters.DuckerOffset + 4 * n + i] = ProMeters.Floor;
        foreach (int offset in new[] { ProMeters.ExpanderOffset, ProMeters.CompressorOffset })
            for (int d = 0; d < ProMeters.DynamicsCount; d++)
            {
                b[offset + 3 * d + 1] = ProMeters.Floor;
                b[offset + 3 * d + 2] = ProMeters.Floor;
            }
        b.AsSpan(ProMeters.LevelOffset).Fill(ProMeters.Floor);
        return b;
    }

    [Fact]
    public void TheLayoutCoversTheWholeBlock()
    {
        Assert.Equal(ProMeters.DuckerOffset, ProMeters.MaximizerOffset + ProMeters.MaximizerCount * ProMeters.MaximizerLength);
        Assert.Equal(ProMeters.ExpanderOffset, ProMeters.DuckerOffset + ProMeters.DuckerCount * ProMeters.DuckerLength);
        Assert.Equal(ProMeters.CompressorOffset, ProMeters.ExpanderOffset + ProMeters.DynamicsCount * ProMeters.DynamicsLength);
        Assert.Equal(ProMeters.VoiceTuneOffset, ProMeters.CompressorOffset + ProMeters.DynamicsCount * ProMeters.DynamicsLength);
        Assert.Equal(ProMeters.LevelOffset, ProMeters.VoiceTuneOffset + 2);
        Assert.Equal(ProMeters.OutputOffset, ProMeters.LevelOffset + 2 * ProMeters.LevelCount);
        Assert.Equal(ProMeters.Length, ProMeters.OutputOffset + 2 * ProMeters.OutputCount);
        // One ducker per block 0x0008 entry.
        Assert.Equal(ProDucking.EntryCount, ProMeters.DuckerCount);
    }

    [Fact]
    public void ABlockOfAnotherLengthIsNotDecoded()
    {
        Assert.Null(ProMeters.Check(Idle()));
        Assert.Equal("block 0x0002 is 149 bytes, not 150", ProMeters.Check(new byte[149]));
        Assert.Null(ProMeters.Decode(new byte[149]));
        Assert.Null(ProMeters.Decode(new byte[151]));
        Assert.Equal(new Dictionary<string, string> { ["meters"] = "not decoded: block 0x0002 is 0 bytes, not 150" },
            ProMeters.Describe([]));
    }

    [Theory]
    [InlineData(0x00, 0.0)]
    [InlineData(0x01, -0.25)]
    [InlineData(0x50, -20.0)]
    [InlineData(0xef, -59.75)]
    public void ALevelIsMinusAQuarterDbPerStep(byte level, double db) => Assert.Equal(db, ProMeters.LevelDb(level));

    [Theory]
    [InlineData(0xf0)]
    [InlineData(0xf1)]
    [InlineData(0xff)]
    public void TheFloorAndAboveAreSilence(byte level) => Assert.Null(ProMeters.LevelDb(level));

    [Theory]
    [InlineData(0x00, 0.0)]
    [InlineData(0x18, 6.0)]
    [InlineData(0x30, 12.0)]
    [InlineData(0x61, 24.25)]
    public void AReductionIsAQuarterDbPerStep(byte reduction, double db) => Assert.Equal(db, ProMeters.ReductionDb(reduction));

    [Fact]
    public void AToneOnAPairReadsOnItsChannelAndNothingElseMoves()
    {
        // -20 dBFS on pair 1, nothing ducked: channel 16 (14 + 2) reads 50 50.
        byte[] b = Idle();
        int channel = ProMeters.PairChannel(1);
        Assert.Equal(16, channel);
        b[ProMeters.LevelOffset + 2 * channel] = 0x50;
        b[ProMeters.LevelOffset + 2 * channel + 1] = 0x50;
        ProMeterReading reading = ProMeters.Decode(b)!;
        for (int k = 0; k < ProMeters.LevelCount; k++)
            Assert.Equal(k == channel ? new ProStereoMeter(-20, -20) : new ProStereoMeter(null, null), reading.Levels[k]);
        Assert.All(reading.Outputs, output => Assert.Equal(new ProStereoMeter(null, null), output));
        Assert.All(reading.Duckers, ducker => Assert.Equal(new ProDuckerMeter(0, null, null, null), ducker));
        Assert.Equal(0, reading.DuckReductionDb(Enumerable.Range(0, ProMeters.DuckerCount)));
    }

    [Fact]
    public void EachDuckerIsItsEntryAndTheReductionIsTheLargestOfTheOnesAsked()
    {
        byte[] b = Idle();
        // Pair 1's ducker (entry 5) lowering by 6 dB, its key XLR 1 at -12 dBFS, its output at -30/-31;
        // pair 5's (entry 9) by 12 dB; XLR 2's (entry 1) by 3 dB.
        b[26 + 4] = 0x18;
        b[26 + 5] = 0x30;
        b[26 + 6] = 0x78;
        b[26 + 7] = 0x7c;
        b[ProMeters.DuckerOffset + 4 * 9] = 0x30;
        b[ProMeters.DuckerOffset + 4 * 1] = 0x0c;
        ProMeterReading reading = ProMeters.Decode(b)!;
        Assert.Equal(new ProDuckerMeter(6, -12, -30, -31), reading.Duckers[5]);
        Assert.Equal(12, reading.Duckers[9].ReductionDb);
        Assert.Equal(3, reading.Duckers[1].ReductionDb);
        Assert.Equal(6, reading.DuckReductionDb([5]));
        Assert.Equal(12, reading.DuckReductionDb([5, 9]));
        Assert.Equal(3, reading.DuckReductionDb([1, 4]));
        Assert.Equal(0, reading.DuckReductionDb([]));
        Assert.Equal(6, reading.DuckReductionDb([-1, 5, 12]));
    }

    [Fact]
    public void ATargetDucksItsInputsAndItsPairsEntries()
    {
        Assert.Equal([5], ProMeters.DuckedEntries(On(ProReturnPairs.Monitor)));
        Assert.Equal([1, 2, 5, 9], ProMeters.DuckedEntries(On(ProReturnPairs.Aux, ProReturnPairs.Monitor) with { Inputs = new HashSet<int> { 2, 1 } }));
        Assert.Empty(ProMeters.DuckedEntries(On(ProReturnPairs.Monitor) with { Enabled = false }));
        Assert.Empty(ProMeters.DuckedEntries(ProDuckingTarget.Off));
    }

    [Fact]
    public void EveryLevelChannelHasItsNameAndPlace()
    {
        string[] names =
        [
            "XLR 1 raw", "XLR 1 processed", "XLR 1 after insert", "XLR 1 ducked",
            "XLR 2 raw", "XLR 2 processed", "XLR 2 after insert", "XLR 2 ducked",
            "USB Aux in", "USB Aux after insert", "USB Aux ducked",
            "Line In in", "Line In after insert", "Line In ducked",
            "pair 0 in", "pair 0 ducked", "pair 1 in", "pair 1 ducked", "pair 2 in", "pair 2 ducked", "pair 3 in", "pair 3 ducked",
            "pair 4 in", "pair 4 ducked", "pair 5 in", "pair 5 ducked", "pair 6 in", "pair 6 ducked", "pair 7 in", "pair 7 ducked",
            "section 0", "section 18", "section 36", "section 54", "section 72",
        ];
        Assert.Equal(names, Enumerable.Range(0, ProMeters.LevelCount).Select(ProMeters.ChannelName));
        Assert.Equal(29, ProMeters.PairChannel(7, afterDucker: true));
        Assert.Equal(32, ProMeters.SectionChannel(36));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMeters.ChannelName(35));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMeters.PairChannel(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProMeters.SectionChannel(17));

        // Each channel reads its own two bytes, left then right.
        byte[] b = Idle();
        for (int k = 0; k < ProMeters.LevelCount; k++)
        {
            b[ProMeters.LevelOffset + 2 * k] = (byte)k;
            b[ProMeters.LevelOffset + 2 * k + 1] = (byte)(k + 100);
        }
        for (int o = 0; o < ProMeters.OutputCount; o++)
        {
            b[ProMeters.OutputOffset + 2 * o] = (byte)(4 * o);
            b[ProMeters.OutputOffset + 2 * o + 1] = (byte)(4 * o + 2);
        }
        ProMeterReading reading = ProMeters.Decode(b)!;
        for (int k = 0; k < ProMeters.LevelCount; k++) Assert.Equal(new ProStereoMeter(-k / 4.0, -(k + 100) / 4.0), reading.Levels[k]);
        Assert.Equal([new(0, -0.5), new(-1, -1.5), new(-2, -2.5), new(-3, -3.5)], reading.Outputs);
    }

    [Fact]
    public void DiagnosticsNameOnlyWhatMoves()
    {
        IReadOnlyDictionary<string, string> idle = ProMeters.Describe(Idle());
        Assert.Equal("nothing ducked, no key heard", idle["meterDucking"]);
        Assert.Equal("all silent", idle["meterLevels"]);
        Assert.Equal("Headphones 1 -/-, Headphones 2 -/-, Line Out -/-, USB Aux port -/-", idle["meterOutputs"]);
        Assert.Equal("1 0 dB at -, 2 0 dB at -, 3 0 dB at -, 4 0 dB at -, 5 0 dB at -", idle["meterMaximizers"]);
        Assert.Equal("expander 1 0 dB, key -, out -; expander 2 0 dB, key -, out -; compressor 1 0 dB, key -, out -; "
            + "compressor 2 0 dB, key -, out -; Voice Tune XLR 1 0 dB, XLR 2 0 dB", idle["meterDynamics"]);

        byte[] b = Idle();
        b[26 + 4] = 0x18;           // pair 1 lowered by 6 dB
        b[26 + 5] = 0x30;           // its key at -12
        b[26 + 6] = 0x60;
        b[26 + 7] = 0x61;
        b[ProMeters.LevelOffset] = 0x30;              // XLR 1 raw, left only
        b[ProMeters.LevelOffset + 2 * 17] = 0x60;     // pair 1 after its ducker
        b[ProMeters.LevelOffset + 2 * 17 + 1] = 0x61;
        b[ProMeters.OutputOffset] = 0x62;
        b[ProMeters.OutputOffset + 1] = 0x63;
        b[0] = 0x05;
        b[1] = 0x09;
        IReadOnlyDictionary<string, string> d = ProMeters.Describe(b);
        Assert.Equal("pair 1 -6 dB, key -12, out -24/-24.25", d["meterDucking"]);
        Assert.Equal("XLR 1 raw -12/-, pair 1 ducked -24/-24.25", d["meterLevels"]);
        Assert.Equal("Headphones 1 -24.5/-24.75, Headphones 2 -/-, Line Out -/-, USB Aux port -/-", d["meterOutputs"]);
        Assert.StartsWith("1 -1.25 dB at -2.25, 2 0 dB at -", d["meterMaximizers"]);
    }

    // --- the driver and the device manager ---

    internal static Mk2HardwareEqTests.Unit ProUnit(byte[]? meters)
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Block1();
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(ProDuckingDeviceTests.SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(MediumBytes, IdleFlags);
        if (meters is not null) unit.Blocks[ProMeters.Block] = meters;
        return unit;
    }

    internal static int MeterReads(Mk2HardwareEqTests.Unit unit) => unit.Calls.Count(c => c.Type == 0xC1 && c.Block == ProMeters.Block);

    [Fact]
    public void TheDriverReadsTheBlockOnceAndAUnitThatRefusesItGivesNothing()
    {
        byte[] meters = Idle();
        meters[30] = 0x18;
        var unit = ProUnit(meters);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        unit.Calls.Clear();
        Assert.Equal(6, pro.ReadMeters()!.Duckers[5].ReductionDb);
        Assert.Equal(1, MeterReads(unit));
        Assert.Equal(new ushort[] { 0x0103 }, unit.Calls.Select(c => c.Bank).Distinct());
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41);

        // A poll of the controls does not read the meters.
        unit.Calls.Clear();
        pro.ReadState();
        Assert.Equal(0, MeterReads(unit));

        unit.Blocks.Remove(ProMeters.Block);
        Assert.Null(pro.ReadMeters());
        Assert.True(pro.Connected);
    }

    [Fact]
    public void DiagnosticsCarryTheDecodedMeters()
    {
        byte[] meters = Idle();
        meters[30] = 0x18;
        var unit = ProUnit(meters);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        IReadOnlyDictionary<string, string> blocks = pro.DumpBlocks();
        Assert.Equal(Convert.ToHexString(meters), blocks["0x0002"]);
        Assert.Equal("pair 1 -6 dB, key -, out -/-", blocks["meterDucking"]);
        Assert.Equal("all silent", blocks["meterLevels"]);
        Assert.Contains("meterOutputs", blocks.Keys);
    }
}
