using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Pro's Monitor return on the headphone jacks at the byte level: Wave
/// Link keeps a fader level in the return's cell of the jacks' mix, which
/// leaves the Monitor mix that far down on Linux with the membership bit
/// already set, and can leave the Line In jack a member of the same mix
/// (byte 12 bit 3), which plays it on the jacks beside the Monitor mix that
/// already carries the Line In channel. Other USB playback pairs can be
/// members too (pairs 6 and 7 on the desk unit, pair 0 on an owner's), and
/// so can XLR 2. The return reads as on only at unity with XLR 2, Line In and
/// every other pair out of the mix, and turning it on puts the cell back to 0 dB and clears those bits
/// without touching the rest of the block.
/// </summary>
[Collection("xdg-config")]
public sealed class ProHeadphoneMixDeviceTests
{
    /// <summary>Block 0x0001 of a dual-booting owner's Pro: pair 1 a member of the jacks' mix at -27.5 dB.</summary>
    private const string LeftByWaveLink =
        "C80000C200C9C9C9C900A6C9E80D00AA55550000C4C2C4C9C9C4C9C4A6D6690D00EA555500C4F4C4C4E8C4C4F400C4C4000200EA5555" +
        "00FAFAC2C4C9C9C4C9C4C4D6690900EAFDF7000088000000000000000000000000AA55551E2320230202020200000000000000000000";

    /// <summary>
    /// The same unit's block as an owner's archive on 0.2.4 read it: the
    /// return at unity, as OpenXLR leaves it, with Line In still a member of
    /// the jacks' mix (byte 12 `e8`) and Headphones 1 playing it (byte 90 `1e`).
    /// </summary>
    private static string ReturnAtUnity()
    {
        byte[] block = Convert.FromHexString(LeftByWaveLink);
        block[5] = 0x00;
        return Convert.ToHexString(block);
    }

    private static Mk2HardwareEqTests.Unit Unit(string block1 = LeftByWaveLink)
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Convert.FromHexString(block1);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(ProDuckingDeviceTests.SettingsBlock);
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        return unit;
    }

    [Fact]
    public void AReturnBelowUnityReadsAsOffAndTurningItOnOpensOnlyItsCellAndTakesLineInAndTheOtherPairsOut()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        byte[] before = unit.Blocks[0x0001];
        Assert.Equal(0xe8, before[12]);
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        byte[] after = unit.Blocks[0x0001];
        Assert.Equal(0x00, after[5]);
        // Bit 5 was already set; Line In's bit 3 goes, and so do bits 6 and 7,
        // USB pairs 2 and 3. Bit 0, XLR 1's path, is left to its own setter.
        Assert.Equal(0x20, after[12]);
        // Wave Link left USB pairs 4, 6 and 7 in the mix too (byte 13 `0d`).
        Assert.Equal((0x0d, 0x00), (before[13], after[13]));
        for (int i = 0; i < after.Length; i++)
            if (i is not (5 or 12 or 13)) Assert.Equal(before[i], after[i]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void TurningTheReturnOnCommitsTheBlock()
    {
        var unit = Unit(ReturnAtUnity());
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        unit.Calls.Clear();

        pro.SetHpMixMonitorReturn(true);

        Assert.Equal(new[] { (0x41, 0x0001), (0x41, 0x0003) },
            unit.Calls.Where(c => c.Type == 0x41).Select(c => ((int)c.Type, (int)c.Block)));
    }

    [Fact]
    public void AReturnAtUnityWithLineInInTheMixReadsAsOff()
    {
        var unit = Unit(ReturnAtUnity());
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.Equal((0x00, 0xe8, 0x1e), (unit.Blocks[0x0001][5], unit.Blocks[0x0001][12], unit.Blocks[0x0001][90]));
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        Assert.Equal(0x20, unit.Blocks[0x0001][12]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void OtherUsbPairsInTheMixReadAsOffAndLeaveWithTheReturn()
    {
        // The desk unit on 2026-10-31: the return at unity with Line In out,
        // and USB pairs 6 and 7 members (byte 13 `0c`). XLR 2 (bit 1) and USB
        // Aux (bit 2) are set here too: XLR 2 leaves, USB Aux stays.
        byte[] block = Convert.FromHexString(ReturnAtUnity());
        block[12] = 0x26;
        block[13] = 0x0c;
        var unit = Unit(Convert.ToHexString(block));
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMonitorReturn(true);
        byte[] after = unit.Blocks[0x0001];
        Assert.Equal((0x24, 0x00), (after[12], after[13]));
        for (int i = 0; i < after.Length; i++)
            if (i is not (12 or 13)) Assert.Equal(block[i], after[i]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);

        // Bits above pair 7 in byte 13 are no pair the sink has and stay as read.
        block[12] = 0x24;
        block[13] = 0xf0;
        var high = Unit(Convert.ToHexString(block));
        using var other = new WaveXlrProDevice(high);
        other.Connect();
        Assert.True(other.ReadState().HpMixMonitorReturn);
    }

    [Fact]
    public void TurningTheReturnOffClearsItsBitAndLeavesTheLevelAndLineIn()
    {
        var unit = Unit();
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        pro.SetHpMixMonitorReturn(true);
        pro.SetHpMixMonitorReturn(false);
        byte[] block = unit.Blocks[0x0001];
        Assert.Equal((0x00, 0x00), (block[5], block[12]));
        Assert.False(pro.ReadState().HpMixMonitorReturn);

        // A unit that still holds Line In in the mix keeps it when the return goes off.
        var held = Unit(ReturnAtUnity());
        using var other = new WaveXlrProDevice(held);
        other.Connect();
        other.SetHpMixMonitorReturn(false);
        Assert.Equal(0xc8, held.Blocks[0x0001][12]);
    }

    [Fact]
    public void TheDaemonTakesLineInOutOfAUnitThatConnectsWithTheReturnAlreadyOn()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try
        {
            new DaemonSettings { Submixer = false }.Save();
            var unit = Unit(ReturnAtUnity());
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.False(manager.Snapshot().State!.HpMixMonitorReturn);

            // A jack is the monitor output: the return is asserted, which
            // writes the bit out, and the caller restarts the playback stream.
            bool mic = (unit.Blocks[0x0001][13] & 0x02) != 0;
            Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: mic));
            Assert.Equal((0x20, 0x00), (unit.Blocks[0x0001][12], unit.Blocks[0x0001][13]));
            Assert.True(manager.Snapshot().State!.HpMixMonitorReturn);

            // Later passes find it set and write nothing.
            manager.SweepOnce();
            Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: mic));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Block 0x0001 from an owner's archive (2026-10-30) whose microphone was
    /// missing from the jacks: XLR 1 out of the jacks' mix (byte 12 `30`),
    /// the Aux mix's stream in it (byte 13 bit 1) at Wave Link's floor (byte
    /// 9 `88`), and byte 0, XLR 1's level, at `64`, the crossfade's centre.
    /// </summary>
    private const string MicMissing =
        "64888888E700888888888888300200FF5555008800888888880088888888850000BA5555008888888888888888008888000200FE5555" +
        "E788DB88D7F9EBE7EE888888F50100FDFF570088E788D78888ED88888888950000FEFF571E2320230202020200000000000000000000";

    [Fact]
    public void TheMicrophonesPathIsXlr1sMembershipAndTheAuxMixLeavesWithTheReturn()
    {
        var unit = Unit(MicMissing);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        Assert.False(pro.Capabilities.Crossfade);
        DeviceState state = pro.ReadState();
        // The bit OpenXLR set before is the Aux mix, not the microphone; the
        // level reads as the firmware plays it, held at +12 dB.
        Assert.False(state.HpMixMicDirect);
        Assert.Equal(12, state.HpMixMicLevelDb);
        Assert.False(state.HpMixMonitorReturn);

        byte[] before = unit.Blocks[0x0001];
        pro.SetHpMixMonitorReturn(true);
        byte[] after = unit.Blocks[0x0001];
        // Pair 5's bit goes from byte 13, and pair 0's bit 4 from byte 12.
        Assert.Equal((0x20, 0x00), (after[12], after[13]));
        for (int i = 0; i < after.Length; i++)
            if (i is not (12 or 13)) Assert.Equal(before[i], after[i]);
        Assert.True(pro.ReadState().HpMixMonitorReturn);

        pro.SetHpMixMicLevelDb(0);
        pro.SetHpMixMicDirect(true);
        Assert.Equal((0x00, 0x21), (unit.Blocks[0x0001][0], unit.Blocks[0x0001][12]));
        state = pro.ReadState();
        Assert.Equal((true, 0.0, true), (state.HpMixMicDirect, state.HpMixMicLevelDb, state.HpMixMonitorReturn));

        pro.SetHpMixMicDirect(false);
        Assert.Equal(0x20, unit.Blocks[0x0001][12]);
        Assert.False(pro.ReadState().HpMixMicDirect);
    }

    [Theory]
    [InlineData(0.0, 0x00)]
    [InlineData(-6.0, 0xf4)]
    [InlineData(-28.0, 0xc8)]
    [InlineData(-18.06, 0xdc)]
    [InlineData(10.6, 0x15)]
    [InlineData(30.0, 0x18)]
    [InlineData(-80.0, 0x88)]
    [InlineData(double.NegativeInfinity, 0x88)]
    [InlineData(double.NaN, 0x88)]
    public void TheMicLevelIsWrittenInHalfDecibelsBetweenTheFloorAndTheCap(double db, int expected)
    {
        var unit = Unit(MicMissing);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        byte[] before = unit.Blocks[0x0001];
        unit.Calls.Clear();
        pro.SetHpMixMicLevelDb(db);
        byte[] after = unit.Blocks[0x0001];
        Assert.Equal(expected, after[0]);
        for (int i = 1; i < after.Length; i++) Assert.Equal(before[i], after[i]);
        // A level applies at once on the unit: no commit follows.
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41 && c.Block == 0x0003);
    }

    [Fact]
    public void TheCrossfadeNoLongerWritesTheMicLevel()
    {
        var unit = Unit(MicMissing);
        using var pro = new WaveXlrProDevice(unit);
        pro.Connect();
        unit.Calls.Clear();
        pro.SetCrossfade(200);
        Assert.Equal(0x64, unit.Blocks[0x0001][0]);
        Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41);
    }
}
