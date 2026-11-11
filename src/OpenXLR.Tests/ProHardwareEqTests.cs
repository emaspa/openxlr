using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's hardware EQ in the driver, against a unit modelled as
/// its blocks, held to Wave Link 3.5's own writes on firmware 1.0.14
/// (captures 16 to 20 of 2026-10-21): block 0x0004 holds XLR 1's struct at 0
/// and XLR 2's at 38, each with the MK.2 family's switch and four bands; the
/// recorded block decodes to Wave Link's saved EQ on both inputs; each
/// logged change encoded onto the block reproduces the bytes Wave Link
/// wrote; an EQ write is one read and one whole-block write with no commit,
/// keeping ClipGuard, the flag bits the EQ does not own, the other input's
/// struct and the aux tail; the gate is firmware 1.0.14 or newer in block
/// 0x0000 bytes 5 to 7 with both structs decoding; and no EQ field is ever
/// written when the gate fails. Values only: no Elgato code.
/// </summary>
public sealed class ProHardwareEqTests
{
    /// <summary>
    /// Block 0x0004 as capture 16 read it, constant over 801 reads: both EQs
    /// off, ClipGuard off on XLR 2 (byte 40 bit 2), the aux tail at 76.
    /// </summary>
    internal const string IdleBlock =
        "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8"
        + "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200";

    internal sealed class Unit : IUsbTransport
    {
        public const ushort Bank = 0x0103;
        public bool IsOpen { get; private set; }
        public readonly Dictionary<ushort, byte[]> Blocks = new()
        {
            [0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 14),
            [0x0001] = new byte[108],
            [0x0003] = new byte[12],
            [0x0004] = Convert.FromHexString(IdleBlock),
            [0x0005] = new byte[8],
        };
        public readonly List<(byte Type, ushort Block, ushort Bank, byte[] Data)> Calls = [];

        public bool Open(ushort vendor, ushort product) { IsOpen = true; return true; }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();

        public int ControlTransfer(byte type, byte request, ushort block, ushort bank, byte[] data, ushort length, uint timeout)
        {
            Assert.True(IsOpen);
            Assert.Equal(Bank, bank);
            lock (this)
            {
                Calls.Add((type, block, bank, data.Take(length).ToArray()));
                if (!Blocks.TryGetValue(block, out byte[]? held)) return -9;
                if (type == 0xC1)
                {
                    int n = Math.Min(length, held.Length);
                    Array.Copy(held, data, n);
                    return n;
                }
                Blocks[block] = data.Take(length).ToArray();
                return length;
            }
        }

        public IEnumerable<byte[]> SettingsWrites => Calls.Where(c => c.Type == 0x41 && c.Block == 4).Select(c => c.Data);
        public IEnumerable<byte[]> Commits => Calls.Where(c => c.Type == 0x41 && c.Block == 3).Select(c => c.Data);
    }

    /// <summary>Wave Link's saved EQ for XLR Mic 1 and XLR Mic 2 in capture 16: the four bands as the unit holds them.</summary>
    private static readonly DeviceEqBand[] SavedBands =
    [
        new(true, 0, 0, 60, -10),      // high pass 60 Hz, QFactor -0.152
        new(true, 1, 13, 227, -1),     // low shelf +3.25 dB 227 Hz, -0.008
        new(true, 2, -12, 860, -14),   // bell -3 dB 860 Hz, -0.216
        new(true, 2, 7, 10237, -24),   // bell +1.75 dB 10237 Hz, -0.376
    ];

    private static WaveXlrProDevice Connected(Unit unit)
    {
        var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        return pro;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void TheRecordedBlockDecodesToWaveLinksEqOnBothInputs()
    {
        byte[] block = Convert.FromHexString(IdleBlock);
        for (int input = 1; input <= 2; input++)
        {
            DeviceEq eq = WaveFxEq.Decode(block.AsSpan(WaveFxEq.InputBase(input), WaveFxEq.StructLength))!;
            Assert.False(eq.Enabled);
            Assert.Equal(SavedBands, eq.Bands);
            Assert.True(WaveFxEq.Decodes(eq));
        }
        Assert.Equal(0, WaveFxEq.InputBase(1));
        Assert.Equal(38, WaveFxEq.InputBase(2));
    }

    [Fact]
    public void Firmware1014RunsTheEqOnBothInputs()
    {
        var unit = new Unit();
        using WaveXlrProDevice pro = Connected(unit);
        Assert.Equal("1.0.14", pro.Firmware);
        Assert.True(pro.Capabilities.HardwareEq);
        Assert.Equal(2, pro.Capabilities.XlrInputs);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "1.0.14" }, pro.EqSupport);
        Assert.Null(pro.ConnectionNote);
        // Read at connect, before any poll.
        Assert.Equal(SavedBands, pro.ReadEq(1)!.Bands);
        Assert.Equal(SavedBands, pro.ReadEq(2)!.Bands);
        Assert.Null(pro.ReadEq(3));
        Assert.Throws<InvalidOperationException>(() => pro.SetEq(3, DeviceEq.Off));
        Assert.Throws<InvalidOperationException>(() => pro.SetEq(0, DeviceEq.Off));
        // Measured to 12 dB only: a band beyond it is never written.
        Assert.Equal(12, pro.EqSupport!.MaxGain);
        Assert.Throws<ArgumentException>(() => pro.SetEq(2, new DeviceEq(true, [.. SavedBands.Select(b => b with { Gain = -49 })])));
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void AProWhoseBandsHoldMoreThanTwelveDecibelsStillRunsTheEq()
    {
        var unit = new Unit();
        // XLR 1's band 1 at +15 dB (byte 19) and XLR 2's at -20 dB (38 + 19):
        // values another program may leave, read but never written by OpenXLR.
        unit.Blocks[0x0004][19] = 0x3c;
        unit.Blocks[0x0004][57] = 0xb0;
        using WaveXlrProDevice pro = Connected(unit);
        Assert.True(pro.Capabilities.HardwareEq);
        Assert.True(pro.EqSupport!.Ready);
        Assert.Null(pro.ConnectionNote);
        Assert.Equal(60, pro.ReadEq(1)!.Bands![0].Gain);
        Assert.Equal(-80, pro.ReadEq(2)!.Bands![0].Gain);
        // The switch alone keeps the stored bands.
        Assert.True(pro.SetEq(1, DeviceEq.Off with { Enabled = true }));
        Assert.Equal(60, pro.ReadEq(1)!.Bands![0].Gain);
    }

    /// <summary>
    /// Captures 17 and 19: Wave Link switched XLR Mic 1's EQ on with one write
    /// of byte 2 <c>00</c> to <c>01</c>, and XLR Mic 2's with byte 40
    /// <c>04</c> to <c>05</c>, ClipGuard's bit kept, and no commit came with
    /// either. The driver writes the same bytes the same way.
    /// </summary>
    [Fact]
    public void TheSwitchOnEachInputIsWaveLinksOneByteWriteWithNoCommit()
    {
        var unit = new Unit();
        using WaveXlrProDevice pro = Connected(unit);
        unit.Calls.Clear();
        Assert.True(pro.SetEq(1, DeviceEq.Off with { Enabled = true }));
        Assert.Equal(new byte[] { 0xC1, 0x41 }, unit.Calls.Select(c => c.Type));
        byte[] first = Assert.Single(unit.SettingsWrites);
        byte[] expected = Convert.FromHexString(IdleBlock);
        expected[2] = 0x01;
        Assert.Equal(Hex(expected), Hex(first));

        unit.Calls.Clear();
        Assert.True(pro.SetEq(2, DeviceEq.Off with { Enabled = true }));
        expected[40] = 0x05;
        Assert.Equal(Hex(expected), Hex(Assert.Single(unit.SettingsWrites)));
        Assert.Empty(unit.Commits);
        Assert.True(pro.ReadEq(1)!.Enabled);
        Assert.True(pro.ReadEq(2)!.Enabled);

        // Off again: bit 0 alone, ClipGuard's bit 2 on XLR 2 stays set.
        Assert.True(pro.SetEq(2, DeviceEq.Off));
        Assert.Equal(0x04, unit.Blocks[0x0004][40]);
        Assert.Equal(0x01, unit.Blocks[0x0004][2]);
        Assert.False(pro.SetEq(2, DeviceEq.Off));
    }

    /// <summary>
    /// Capture 18 (XLR Mic 1) and 20 (XLR Mic 2): band 2's gain dragged from
    /// +3.25 to +12 dB. Each gain Wave Link logged went to byte 24 (62) in
    /// quarter decibels and each frequency to bytes 25 and 26 (63 and 64)
    /// little endian, the carry at 256 Hz included, with nothing else in the
    /// block changed.
    /// </summary>
    [Theory]
    [InlineData(1, 24, new[] { 3.75, 4.5, 5, 6, 7.5, 8, 8.5, 9.25, 9.75, 10, 11, 11.5, 12 },
        "0f1214181e20222527282c2e30", new[] { 225, 227, 230, 223, 217, 221, 238, 240, 232, 242, 256, 245 },
        "e100e300e600df00d900dd00ee00f000e800f2000001f500")]
    [InlineData(2, 62, new[] { 3.75, 4.5, 5.25, 5.75, 6.5, 8, 8.75, 9.5, 10, 10.25, 11.25, 11.5, 12 },
        "0f1215171a20232628292d2e30", new[] { 225, 232, 234, 230, 227 }, "e100e800ea00e600e300")]
    public void EachLoggedBand2ChangeReproducesWaveLinksWrite(int input, int gainAt, double[] gains, string gainBytes, int[] frequencies, string frequencyBytes)
    {
        byte[] expectedGains = Convert.FromHexString(gainBytes);
        byte[] expectedFrequencies = Convert.FromHexString(frequencyBytes);
        var unit = new Unit();
        using WaveXlrProDevice pro = Connected(unit);
        Assert.True(pro.SetEq(input, pro.ReadEq(input)! with { Enabled = true }));
        MicEq eq = DeviceEqMapping.FromDevice(pro.ReadEq(input)!)!;
        for (int i = 0; i < gains.Length; i++)
        {
            byte[] before = (byte[])unit.Blocks[0x0004].Clone();
            eq = eq.WithBand(2, eq.Bands[1] with { Gain = gains[i] });
            Assert.True(pro.SetEq(input, DeviceEqMapping.ToDevice(eq)));
            byte[] after = unit.SettingsWrites.Last();
            Assert.Equal(expectedGains[i], after[gainAt]);
            before[gainAt] = expectedGains[i];
            Assert.Equal(Hex(before), Hex(after));
        }
        for (int i = 0; i < frequencies.Length; i++)
        {
            byte[] before = (byte[])unit.Blocks[0x0004].Clone();
            eq = eq.WithBand(2, eq.Bands[1] with { Frequency = frequencies[i] });
            Assert.True(pro.SetEq(input, DeviceEqMapping.ToDevice(eq)));
            byte[] after = unit.SettingsWrites.Last();
            before[gainAt + 1] = expectedFrequencies[2 * i];
            before[gainAt + 2] = expectedFrequencies[2 * i + 1];
            Assert.Equal(Hex(before), Hex(after));
        }
        Assert.Empty(unit.Commits);
        // The state Wave Link left on the unit: band 2 a +12 dB low shelf.
        Assert.Equal(new DeviceEqBand(true, 1, 48, frequencies[^1], -1), pro.ReadEq(input)!.Bands![1]);
    }

    [Fact]
    public void AnEqWriteKeepsEveryByteAndBitItDoesNotOwn()
    {
        var random = new Random(1014);
        for (int round = 0; round < 200; round++)
        {
            var unit = new Unit();
            byte[] block = new byte[80];
            random.NextBytes(block);
            // Bands that decode, so the gate passes; every other byte random.
            foreach (int at in new[] { 0, 38 })
                for (int band = 0; band < 4; band++)
                {
                    int b = at + 18 + band * 5;
                    block[b] = (byte)(block[b] & 0xF1 | random.Next(6) << 1);
                    block[b + 1] = (byte)(sbyte)random.Next(-48, 49);
                    int frequency = random.Next(20, 20001);
                    block[b + 2] = (byte)frequency;
                    block[b + 3] = (byte)(frequency >> 8);
                    block[b + 4] = (byte)(sbyte)random.Next(-64, 65);
                }
            unit.Blocks[0x0004] = (byte[])block.Clone();
            using WaveXlrProDevice pro = Connected(unit);
            int input = 1 + round % 2;
            var eq = new DeviceEq(random.Next(2) == 1, [.. Enumerable.Range(0, 4).Select(_ => new DeviceEqBand(random.Next(2) == 1,
                random.Next(6), random.Next(-48, 49), random.Next(20, 20001), random.Next(-64, 65)))]);
            pro.SetEq(input, eq);
            byte[] now = unit.Blocks[0x0004];
            int own = WaveFxEq.InputBase(input);
            for (int i = 0; i < 80; i++)
            {
                int at = i - own;
                bool owned = at >= 0 && at < 38 && (at == 2 || at >= 18);
                if (!owned) Assert.True(block[i] == now[i], $"round {round}: byte {i} changed");
                else if (at == 2) Assert.Equal(block[i] & 0xFE, now[i] & 0xFE);
                else if ((at - 18) % 5 == 0) Assert.Equal(block[i] & 0xF0, now[i] & 0xF0);
            }
            Assert.Equal(block[76..], now[76..]);
            Assert.Equal(eq, pro.ReadEq(input));
            Assert.Empty(unit.Commits);
        }
    }

    [Fact]
    public void TheOtherSettersKeepTheEqBytesAndTheEqKeepsTheirs()
    {
        var unit = new Unit();
        using WaveXlrProDevice pro = Connected(unit);
        var eq = new DeviceEq(true, [.. SavedBands.Select(b => b with { Gain = 48 })]);
        Assert.True(pro.SetEq(1, eq));
        Assert.True(pro.SetEq(2, eq));
        byte[] eqBytes = unit.Blocks[0x0004];
        pro.SetGainDb(40);
        pro.SetGain2Db(30);
        pro.SetMute2(true);
        pro.SetClipGuard(false);
        pro.SetClipGuard2(true);
        pro.SetAuxLevelDb(-6);
        pro.SetAuxLevelLock(true);
        byte[] now = unit.Blocks[0x0004];
        Assert.Equal(eqBytes[18..38], now[18..38]);
        Assert.Equal(eqBytes[56..76], now[56..76]);
        Assert.Equal(0x01, now[2] & 0x01);
        Assert.Equal(0x01, now[40] & 0x01);
        // And the EQ, written after them, keeps what they wrote.
        Assert.True(pro.SetEq(1, DeviceEq.Off));
        Assert.Equal(40, unit.Blocks[0x0004][0]);
        Assert.Equal(30, unit.Blocks[0x0004][38]);
        Assert.Equal(0x04, unit.Blocks[0x0004][2] & 0x04);
        Assert.Equal(0x00, unit.Blocks[0x0004][40] & 0x04);
        Assert.Equal(now[76..], unit.Blocks[0x0004][76..]);
        // The poll keeps the block ReadEq decodes.
        pro.ReadState();
        Assert.False(pro.ReadEq(1)!.Enabled);
        Assert.True(pro.ReadEq(2)!.Enabled);
    }

    [Theory]
    [InlineData(1, 0, 14, true)]
    [InlineData(1, 0, 15, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(2, 0, 0, true)]
    [InlineData(1, 0, 13, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 9, 99, false)]
    public void TheGateIsVersion1014OrNewerFromBlockZero(byte major, byte minor, byte patch, bool runs)
    {
        var unit = new Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        byte[] held = (byte[])unit.Blocks[0x0004].Clone();
        using WaveXlrProDevice pro = Connected(unit);
        Assert.Equal($"{major}.{minor}.{patch}", pro.Firmware);
        Assert.Equal(runs, pro.Capabilities.HardwareEq);
        Assert.Equal(runs, pro.EqSupport!.Ready);
        Assert.Equal("1.0.14", pro.EqSupport.MinimumFirmware);
        if (runs) return;
        Assert.Equal(DeviceEqSupport.FirmwareOld, pro.EqSupport.Reason);
        Assert.Equal($"This Wave XLR Pro has firmware {major}.{minor}.{patch}. From 1.0.14 the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.",
            pro.EqSupport.Note);
        // No EQ field is ever written on it, and nothing reads as its EQ.
        pro.ReadState();
        Assert.Null(pro.ReadEq(1));
        Assert.Null(pro.ReadEq(2));
        Assert.Throws<InvalidOperationException>(() => pro.SetEq(1, DeviceEq.Off));
        Assert.Throws<InvalidOperationException>(() => pro.SetEq(2, DeviceEq.Off));
        Assert.Empty(unit.SettingsWrites);
        pro.SetGainDb(20);
        Assert.Equal(held[2..], unit.Blocks[0x0004][2..]);
    }

    [Fact]
    public void AnUnreadableVersionOrAStructThatDoesNotDecodeLeavesTheEqToSoftware()
    {
        var unit = new Unit();
        unit.Blocks.Remove(0x0000);
        using (WaveXlrProDevice pro = Connected(unit))
        {
            Assert.True(pro.Connected);
            Assert.Null(pro.Firmware);
            Assert.False(pro.Capabilities.HardwareEq);
            Assert.Equal(DeviceEqSupport.FirmwareUnknown, pro.EqSupport!.Reason);
            Assert.Null(pro.ConnectionNote);
        }
        // XLR 2's band 1 at 0 Hz: not a value the EQ takes, so neither input runs it.
        unit = new Unit();
        unit.Blocks[0x0004][58] = 0; unit.Blocks[0x0004][59] = 0;
        using (WaveXlrProDevice pro = Connected(unit))
        {
            Assert.Equal("1.0.14", pro.Firmware);
            Assert.False(pro.Capabilities.HardwareEq);
            Assert.Equal(DeviceEqSupport.UnsupportedBlock, pro.EqSupport!.Reason);
            Assert.Contains("diagnostics", pro.ConnectionNote);
            Assert.Throws<InvalidOperationException>(() => pro.SetEq(1, DeviceEq.Off));
        }
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void AReconnectDecidesAgainAndDiagnosticsShowTheFirmwareAndBothEqs()
    {
        var unit = new Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 13);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.False(pro.Capabilities.HardwareEq);
        pro.Disconnect();
        Assert.Null(pro.Firmware);
        Assert.Null(pro.EqSupport);
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 14);
        pro.Connect();
        Assert.True(pro.Capabilities.HardwareEq);
        IReadOnlyDictionary<string, string> dump = pro.DumpBlocks();
        Assert.Equal("1.0.14", dump["firmware"]);
        Assert.Equal("on the unit", dump["eqSupport"]);
        Assert.StartsWith("0205040000" + "01000e", dump["0x0000"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IdleBlock, dump["0x0004"], ignoreCase: true);
        Assert.Equal(WaveFxEq.Decode(Convert.FromHexString(IdleBlock))!.ToString(), dump["eq1"]);
        Assert.StartsWith("off; band 1 on type 0 gain 0 dB 60 Hz q -10", dump["eq2"]);
        Assert.Empty(unit.SettingsWrites);
    }
}
