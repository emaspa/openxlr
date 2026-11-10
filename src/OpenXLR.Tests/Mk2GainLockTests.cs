using OpenXLR.Core.Devices;

namespace OpenXLR.Tests;

/// <summary>
/// The unit's own gain lock in byte 2 bit 0x04 of the MK.2 family's settings
/// block, from firmware 1.0.15, where Wave Link writes it for its gain lock
/// (measured on a Wave XLR MK.2 on 1.0.15: the unit then ignores ALSA
/// capture volume writes and still takes byte 0). Older firmware, an unread
/// version and the Wave:3 MK.2 have no such lock: there 0x04 is ClipGuard
/// off, and it is never written for the lock.
/// </summary>
public sealed class Mk2GainLockTests
{
    private static Mk2HardwareEqTests.Unit UnitWithByte2(byte value, byte major = 1, byte minor = 0, byte patch = 15)
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        unit.Blocks[0x0004][2] = value;
        return unit;
    }

    [Fact]
    public void OnFirmware1015TheLockIsSetAndClearedAloneInByte2()
    {
        // The EQ switch and ClipGuard off are held and must stay.
        var unit = UnitWithByte2(0x11);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.HardwareGainLock);
        Assert.False(mk2.ReadState().UnitGainLock);
        byte[] before = (byte[])unit.Blocks[0x0004].Clone();

        Assert.True(mk2.SetUnitGainLock(true));
        byte[] written = unit.SettingsWrites.Last();
        Assert.Equal(0x15, written[2]);
        before[2] = 0x15;
        Assert.Equal(before, written);
        Assert.True(mk2.ReadState().UnitGainLock);
        Assert.False(mk2.ReadState().ClipGuard);

        Assert.True(mk2.SetUnitGainLock(false));
        Assert.Equal(0x11, unit.SettingsWrites.Last()[2]);
        Assert.False(mk2.ReadState().UnitGainLock);
    }

    [Fact]
    public void NothingIsWrittenWhenTheUnitAlreadyHoldsIt()
    {
        var unit = UnitWithByte2(0x04);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.SetUnitGainLock(true));
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void ClipGuardWritesKeepTheLockWhileItIsHeld()
    {
        var unit = UnitWithByte2(0x01);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        mk2.SetUnitGainLock(true);

        mk2.SetClipGuard(false);
        Assert.Equal(0x15, unit.SettingsWrites.Last()[2]);
        mk2.SetClipGuard(true);
        Assert.Equal(0x05, unit.SettingsWrites.Last()[2]);
        Assert.True(mk2.ReadState().UnitGainLock);

        // Cleared by something else meanwhile: the next ClipGuard write puts it back.
        unit.Blocks[0x0004][2] = 0x09;
        mk2.SetClipGuard(true);
        Assert.Equal(0x05, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void ClipGuardWritesStillClearALockOpenXlrDoesNotHold()
    {
        var unit = UnitWithByte2(0x00);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        mk2.SetUnitGainLock(true);
        mk2.SetUnitGainLock(false);

        unit.Blocks[0x0004][2] = 0x04;
        mk2.SetClipGuard(false);
        Assert.Equal(0x10, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void AReconnectForgetsTheHeldLock()
    {
        // The daemon applies its lock again after every connect; until then
        // a ClipGuard write keeps the earlier cleanup.
        var unit = UnitWithByte2(0x00);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        mk2.SetUnitGainLock(true);
        mk2.Connect();
        mk2.SetClipGuard(true);
        Assert.Equal(0x00, unit.SettingsWrites.Last()[2]);
    }

    [Fact]
    public void TheDockIsDrivenTheSameWay()
    {
        var unit = UnitWithByte2(0x00);
        using var dock = new XlrDockMk2Device(unit);
        dock.Connect();
        Assert.True(dock.Capabilities.HardwareGainLock);
        Assert.True(dock.SetUnitGainLock(true));
        Assert.Equal(0x04, unit.SettingsWrites.Last()[2]);
        Assert.True(dock.ReadState().UnitGainLock);
    }

    [Theory]
    [InlineData(1, 0, 14)]
    [InlineData(1, 0, 0)]
    public void OlderFirmwareHasNoLockAndNothingIsWritten(byte major, byte minor, byte patch)
    {
        var unit = UnitWithByte2(0x04, major, minor, patch);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.Capabilities.HardwareGainLock);
        // 0x04 is ClipGuard off here, not a lock.
        Assert.Null(mk2.ReadState().UnitGainLock);
        Assert.False(mk2.ReadState().ClipGuard);
        Assert.Throws<InvalidOperationException>(() => mk2.SetUnitGainLock(true));
        Assert.Throws<InvalidOperationException>(() => mk2.SetUnitGainLock(false));
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void AnUnreadVersionHasNoLock()
    {
        var unit = UnitWithByte2(0x00);
        unit.Blocks.Remove(0x0000);
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.Capabilities.HardwareGainLock);
        Assert.Null(mk2.ReadState().UnitGainLock);
        Assert.Throws<InvalidOperationException>(() => mk2.SetUnitGainLock(true));
        Assert.Empty(unit.SettingsWrites);
    }

    [Fact]
    public void TheWave3Mk2HasNoLock()
    {
        // Its byte 2 has not been read from Wave Link.
        var unit = UnitWithByte2(0x00, 0, 2, 3);
        unit.Bank = 0x0103;
        using var wave3 = new Wave3Mk2Device(unit);
        wave3.Connect();
        Assert.False(wave3.Capabilities.HardwareGainLock);
        Assert.Null(wave3.ReadState().UnitGainLock);
        Assert.Throws<InvalidOperationException>(() => wave3.SetUnitGainLock(true));
        Assert.Empty(unit.SettingsWrites);
    }
}
