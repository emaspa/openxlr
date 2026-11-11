using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The MK.2 family's hardware EQ in the driver, against a unit modelled as
/// its blocks: the firmware gate on block 0x0000 bytes 5 to 7 and the EQ
/// fields decoding, the EQ written through a read of the settings block and
/// one write of it, nothing written that the unit already holds, no EQ field
/// ever written on older firmware, and every transfer on the bank the unit
/// answers. The blocks are the ones the captures recorded.
/// </summary>
public sealed class Mk2HardwareEqTests
{
    /// <summary>Block 0x0004 of the Wave XLR MK.2 on 1.0.15 as it came (capture 09), EQ switch off.</summary>
    internal const string FactoryBlock = "251000d4200a00c8001800f4040a0064000601003c00f6030de3000005f45c03f22507fd27e8";

    internal sealed class Unit : IUsbTransport
    {
        public bool IsOpen { get; private set; }
        public ushort Bank = 0x0203;
        public bool StallOtherBank = true;
        public readonly Dictionary<ushort, byte[]> Blocks = new()
        {
            [0x0000] = Identity(1, 0, 15),
            [0x0001] = Convert.FromHexString("640002030200"),
            [0x0004] = Convert.FromHexString(FactoryBlock),
            [0x0005] = Convert.FromHexString("2d00"),
        };
        public readonly List<(byte Type, ushort Block, ushort Bank, byte[] Data)> Calls = [];

        public static byte[] Identity(byte major, byte minor, byte patch)
        {
            byte[] block = new byte[117];
            block[0] = 0x02; block[1] = 0x05; block[2] = 0x04;
            block[5] = major; block[6] = minor; block[7] = patch;
            return block;
        }

        public bool Open(ushort vendor, ushort product) { IsOpen = true; return true; }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();

        public int ControlTransfer(byte type, byte request, ushort block, ushort bank, byte[] data, ushort length, uint timeout)
        {
            Assert.True(IsOpen);
            Calls.Add((type, block, bank, data.Take(length).ToArray()));
            if (bank != Bank) return StallOtherBank ? -9 : length;
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

        public IEnumerable<byte[]> SettingsWrites => Calls.Where(c => c.Type == 0x41 && c.Block == 4).Select(c => c.Data);
    }

    private static DeviceEq Eq(bool on, int gain = 48, int frequency = 703)
        => new(on, [new(true, 0, 0, 60, -10), new(true, 1, 13, 227, 0), new(true, 2, gain, frequency, -14), new(true, 2, 7, 10237, -24)]);

    [Fact]
    public void Firmware1015RunsTheEqOnTheUnit()
    {
        var unit = new Unit();
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        Assert.Equal("1.0.15", dock.Firmware);
        Assert.True(dock.Capabilities.HardwareEq);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "1.0.15" }, dock.EqSupport);
        // The dock on 1.0.15 stalls at 0x0103 and answers at 0x0203; the
        // three blocks found the bank and every transfer after stays on it.
        unit.Calls.Clear();
        dock.ReadState();
        Assert.False(dock.ReadEq(1)!.Enabled);
        Assert.True(dock.SetEq(1, Eq(true)));
        Assert.All(unit.Calls, c => Assert.Equal(0x0203, c.Bank));
        Assert.Equal(new byte[] { 0xC1, 0x41 }, unit.Calls.Skip(3).Select(c => c.Type));
        // One whole block, the EQ fields changed and nothing else.
        byte[] written = Assert.Single(unit.SettingsWrites);
        Assert.Equal(38, written.Length);
        Assert.Equal("251001d4200a00c8001800f4040a0064000601003c00f6030de3000005" + "30bf02f2" + "2507fd27e8",
            Convert.ToHexString(written).ToLowerInvariant());
        Assert.Equal(Eq(true), dock.ReadEq(1));
        Assert.Null(dock.ReadEq(2));
        Assert.Throws<InvalidOperationException>(() => dock.SetEq(2, Eq(true)));
    }

    [Fact]
    public void TheWaveXlrMk2IsWrittenTwentyDecibelsAndTheDockTwelve()
    {
        var unit = new Unit();
        using (var mk2 = new WaveXlrMk2Device(unit))
        {
            mk2.Connect();
            Assert.Equal(20, mk2.EqSupport!.MaxGain);
            // The bytes the desk measured, read back as written: 0x50 is +20 dB, 0xb0 -20 dB.
            Assert.True(mk2.SetEq(1, Eq(true, gain: 80)));
            Assert.Equal(0x50, unit.SettingsWrites.Last()[29]);
            Assert.True(mk2.SetEq(1, Eq(true, gain: -80)));
            Assert.Equal(0xb0, unit.SettingsWrites.Last()[29]);
            Assert.Equal(Eq(true, gain: -80), mk2.ReadEq(1));
        }
        unit = new Unit();
        // A dock whose band 3 another program left at +15 dB still runs the EQ.
        unit.Blocks[0x0004][29] = 0x3c;
        using (var dock = new XlrDockMk2Device(unit))
        {
            dock.Connect();
            Assert.True(dock.Capabilities.HardwareEq);
            Assert.DoesNotContain(WaveFxEq.UnsupportedBlockNote, dock.ConnectionNote ?? "");
            Assert.Equal(12, dock.EqSupport!.MaxGain);
            Assert.Equal(60, dock.ReadEq(1)!.Bands![2].Gain);
            Assert.Throws<ArgumentException>(() => dock.SetEq(1, Eq(true, gain: 49)));
            Assert.Empty(unit.SettingsWrites);
            Assert.True(dock.SetEq(1, Eq(true, gain: -48)));
            Assert.Equal(0xd0, unit.SettingsWrites.Last()[29]);
        }
    }

    [Fact]
    public void AnEqTheUnitAlreadyHoldsIsNotWrittenAgain()
    {
        var unit = new Unit();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.HardwareEq);
        Assert.True(mk2.SetEq(1, Eq(true)));
        unit.Calls.Clear();
        Assert.False(mk2.SetEq(1, Eq(true)));
        Assert.Empty(unit.SettingsWrites);
        // The switch alone: byte 2 bit 0 and nothing else.
        Assert.True(mk2.SetEq(1, DeviceEq.Off));
        byte[] off = unit.SettingsWrites.Single();
        Assert.Equal(0x00, off[2]);
        Assert.Equal(48, (sbyte)off[29]);
        Assert.False(mk2.SetEq(1, DeviceEq.Off));
    }

    [Theory]
    [InlineData(1, 0, 15, true)]
    [InlineData(1, 0, 16, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(2, 0, 0, true)]
    [InlineData(1, 0, 14, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 9, 99, false)]
    public void TheGateIsTheVersionFromBlockZero(byte major, byte minor, byte patch, bool runs)
    {
        var unit = new Unit();
        unit.Blocks[0x0000] = Unit.Identity(major, minor, patch);
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        Assert.Equal($"{major}.{minor}.{patch}", dock.Firmware);
        Assert.Equal(runs, dock.Capabilities.HardwareEq);
        Assert.Equal(runs, dock.EqSupport!.Ready);
        if (runs) return;
        Assert.Equal(DeviceEqSupport.FirmwareOld, dock.EqSupport.Reason);
        Assert.Equal($"This Wave XLR Dock MK.2 has firmware {major}.{minor}.{patch}. From 1.0.15 the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.",
            dock.EqSupport.Note);
        // The note is the EQ's own: the connection itself is unremarkable.
        Assert.DoesNotContain("firmware", dock.ConnectionNote ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnOlderFirmwareNoEqFieldIsEverWritten()
    {
        var unit = new Unit();
        unit.Blocks[0x0000] = Unit.Identity(1, 0, 0);
        byte[] factory = (byte[])unit.Blocks[0x0004].Clone();
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        dock.ReadState();
        Assert.Null(dock.ReadEq(1));
        Assert.Throws<InvalidOperationException>(() => dock.SetEq(1, Eq(true)));
        Assert.Throws<InvalidOperationException>(() => dock.SetEq(1, DeviceEq.Off));
        Assert.Empty(unit.SettingsWrites);
        // The other setters write the block, and keep its EQ bytes as they were.
        dock.SetGainDb(40);
        dock.SetMute(true);
        dock.SetClipGuard(false);
        Assert.Equal(3, unit.SettingsWrites.Count());
        byte[] now = unit.Blocks[0x0004];
        Assert.Equal(factory[2] & 0x01, now[2] & 0x01);
        Assert.Equal(factory[18..], now[18..]);
    }

    [Fact]
    public void AnUnreadableVersionOrBlockLeavesTheEqToSoftware()
    {
        var unit = new Unit();
        unit.Blocks.Remove(0x0000);
        using (var dock = new XlrDockMk2Device(unit))
        {
            dock.Connect();
            Assert.True(dock.Connected);
            Assert.Null(dock.Firmware);
            Assert.False(dock.Capabilities.HardwareEq);
            Assert.Equal(DeviceEqSupport.FirmwareUnknown, dock.EqSupport!.Reason);
        }
        unit = new Unit();
        // A frequency of 0 Hz in band 1: not a value the EQ takes.
        unit.Blocks[0x0004][20] = 0; unit.Blocks[0x0004][21] = 0;
        using (var dock = new XlrDockMk2Device(unit))
        {
            dock.Connect();
            Assert.Equal("1.0.15", dock.Firmware);
            Assert.False(dock.Capabilities.HardwareEq);
            Assert.Equal(DeviceEqSupport.UnsupportedBlock, dock.EqSupport!.Reason);
            Assert.Contains("diagnostics", dock.ConnectionNote);
            Assert.Throws<InvalidOperationException>(() => dock.SetEq(1, Eq(true)));
        }
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void AReconnectDecidesAgainAsAFirmwareUpdateReEnumeratesTheUnit()
    {
        var unit = new Unit { Bank = 0x0103 };
        unit.Blocks[0x0000] = Unit.Identity(1, 0, 0);
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        Assert.False(dock.Capabilities.HardwareEq);
        dock.Disconnect();
        Assert.False(dock.Capabilities.HardwareEq);
        Assert.Null(dock.Firmware);
        // After the update: 1.0.15, and the bank moved with it.
        unit.Bank = 0x0203;
        unit.Blocks[0x0000] = Unit.Identity(1, 0, 15);
        dock.Connect();
        Assert.True(dock.Capabilities.HardwareEq);
        Assert.Equal("1.0.15", dock.Firmware);
    }

    [Fact]
    public void DiagnosticsShowTheIdentityBlockAndTheDecodedEq()
    {
        var unit = new Unit();
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        IReadOnlyDictionary<string, string> blocks = dock.DumpBlocks();
        Assert.StartsWith("020504000001000F", blocks["identity"]);
        Assert.Equal(FactoryBlock.ToUpperInvariant(), blocks["settings"]);
        Assert.StartsWith("off; band 1 on type 0 gain 0 dB 60 Hz q -10", blocks["eq"]);
        Assert.Equal("1.0.15", blocks["firmware"]);
        Assert.Equal("on the unit", blocks["eqSupport"]);
        Assert.Empty(unit.SettingsWrites);
    }
}
