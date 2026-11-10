using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// ClipGuard in byte 2 of the MK.2 family's settings block. From firmware
/// 1.0.15 Wave Link writes 0x10 for ClipGuard off and 0x04 for the unit's
/// gain lock (captured on a Wave XLR MK.2 on 1.0.15); older firmware, an
/// unread version and the Wave:3 MK.2 keep 0x04 as ClipGuard off.
/// </summary>
public sealed class Mk2ClipGuardTests
{
    private static Mk2HardwareEqTests.Unit UnitWithByte2(byte value, byte major = 1, byte minor = 0, byte patch = 15)
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        unit.Blocks[0x0004][2] = value;
        return unit;
    }

    [Fact]
    public void OnFirmware1015ClipGuardIsTheLimiterBitWaveLinkWrites()
    {
        // The EQ switch (0x01) is on, so the write must keep it.
        var unit = UnitWithByte2(0x01);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.ReadState().ClipGuard);

        mk2.SetClipGuard(false);
        Assert.Equal(0x11, unit.SettingsWrites.Last()[2]);
        Assert.False(mk2.ReadState().ClipGuard);

        mk2.SetClipGuard(true);
        Assert.Equal(0x01, unit.SettingsWrites.Last()[2]);
        Assert.True(mk2.ReadState().ClipGuard);
    }

    [Fact]
    public void OnFirmware1015TheGainLockAnEarlierReleaseLeftIsClearedAndNotReadAsClipGuard()
    {
        // Earlier releases wrote 0x04 for ClipGuard off: the unit took it as
        // its gain lock and kept ClipGuard on.
        var unit = UnitWithByte2(0x04);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.ReadState().ClipGuard);

        mk2.SetClipGuard(false);
        Assert.Equal(0x10, unit.SettingsWrites.Last()[2]);

        unit.Blocks[0x0004][2] = 0x0c;   // gain lock and the limiter's other mode bit
        mk2.SetClipGuard(true);
        Assert.Equal(0x00, unit.SettingsWrites.Last()[2]);
    }

    [Theory]
    [InlineData(1, 0, 14)]
    [InlineData(1, 0, 0)]
    public void OlderFirmwareKeepsTheEarlierBit(byte major, byte minor, byte patch)
    {
        var unit = UnitWithByte2(0x00, major, minor, patch);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        mk2.SetClipGuard(false);
        Assert.Equal(0x04, unit.SettingsWrites.Last()[2]);
        Assert.False(mk2.ReadState().ClipGuard);

        // 0x10 means nothing to ClipGuard here and is left alone.
        unit.Blocks[0x0004][2] = 0x10;
        Assert.True(mk2.ReadState().ClipGuard);
        mk2.SetClipGuard(true);
        Assert.Equal(0x10, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void AnUnreadVersionKeepsTheEarlierBit()
    {
        var unit = UnitWithByte2(0x00);
        unit.Blocks.Remove(0x0000);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.Null(mk2.Firmware);
        mk2.SetClipGuard(false);
        Assert.Equal(0x04, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void TheDockFollowsTheWaveXlrMk2OnTheSameFirmware()
    {
        var unit = UnitWithByte2(0x00);
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        dock.SetClipGuard(false);
        Assert.Equal(0x10, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void AReconnectReadsTheFirmwareAgain()
    {
        var unit = UnitWithByte2(0x00);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 0);
        mk2.Connect();
        mk2.SetClipGuard(false);
        Assert.Equal(0x04, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void TheWave3Mk2KeepsTheEarlierBit()
    {
        // Its byte 2 has not been read from Wave Link yet.
        var unit = UnitWithByte2(0x00, 0, 2, 3);
        unit.Bank = 0x0103;
        using var wave3 = new Wave3Mk2Device(unit);
        wave3.Connect();
        wave3.SetClipGuard(false);
        Assert.Equal(0x04, unit.SettingsWrites.Last()[2]);
    }
}
