using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// OpenXLR's gain lock holds the unit's own lock on a unit that has one
/// (the MK.2 family from firmware 1.0.15, not the Wave:3 MK.2): set and
/// cleared with the lock, and brought to it at every connect, whoever set
/// the bit before. The software lock stays in force next to it.
/// </summary>
// The manager keeps gain locks under the configuration directory, which
// these tests redirect.
[Collection("xdg-config")]
public sealed class Mk2GainLockDaemonTests
{
    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-unit-gain-lock-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(IAudioDevice unit)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    private static Mk2HardwareEqTests.Unit UnitWithByte2(byte value, byte major = 1, byte minor = 0, byte patch = 15)
    {
        var unit = new Mk2HardwareEqTests.Unit();
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        unit.Blocks[0x0004][2] = value;
        return unit;
    }

    private static string? Lock(DeviceManager manager, bool on)
    {
        using var value = JsonDocument.Parse(on ? "true" : "false");
        return manager.Apply("gainLock", value.RootElement);
    }

    [Fact]
    public void TogglingTheGainLockSetsAndClearsTheUnitsLock() => WithConfigDir(() =>
    {
        var usb = UnitWithByte2(0x10);
        using DeviceManager manager = Manager(new XlrDockMk2Device(usb));
        Assert.False(manager.Snapshot().State!.UnitGainLock);

        Assert.Null(Lock(manager, true));
        Assert.Equal(0x14, usb.Blocks[0x0004][2]);
        Assert.True(manager.Snapshot().State!.GainLocked);
        Assert.True(manager.Snapshot().State!.UnitGainLock);

        // The software lock still refuses a client's gain change.
        using (var gain = JsonDocument.Parse("20"))
            Assert.Equal("gain is locked", manager.Apply("gain", gain.RootElement));

        // A ClipGuard switch keeps the unit's lock.
        using (var on = JsonDocument.Parse("true"))
            Assert.Null(manager.Apply("clipGuard", on.RootElement));
        Assert.Equal(0x04, usb.Blocks[0x0004][2]);

        Assert.Null(Lock(manager, false));
        Assert.Equal(0x00, usb.Blocks[0x0004][2]);
        Assert.False(manager.Snapshot().State!.GainLocked);
        Assert.False(manager.Snapshot().State!.UnitGainLock);
    });

    [Fact]
    public void AUnitThatConnectsWithTheLockOnGetsItsLockBack() => WithConfigDir(() =>
    {
        var usb = UnitWithByte2(0x00);
        using (DeviceManager first = Manager(new WaveXlrMk2Device(usb)))
            Assert.Null(Lock(first, true));
        // Cleared elsewhere while OpenXLR was not running.
        usb.Blocks[0x0004][2] = 0x00;

        using DeviceManager manager = Manager(new WaveXlrMk2Device(usb));

        Assert.Equal(0x04, usb.Blocks[0x0004][2]);
        Assert.True(manager.Snapshot().State!.GainLocked);
    });

    [Fact]
    public void WithTheLockOffAUnitLockedElsewhereIsReleasedAtConnect() => WithConfigDir(() =>
    {
        // Set in Wave Link, or by an earlier release's ClipGuard switch.
        var usb = UnitWithByte2(0x15);

        using DeviceManager manager = Manager(new WaveXlrMk2Device(usb));

        Assert.Equal(0x11, usb.Blocks[0x0004][2]);
        Assert.False(manager.Snapshot().State!.GainLocked);
    });

    [Fact]
    public void AUnitThatAlreadyHoldsTheLockIsNotWrittenAtConnect() => WithConfigDir(() =>
    {
        var usb = UnitWithByte2(0x00);
        using DeviceManager manager = Manager(new WaveXlrMk2Device(usb));
        Assert.Empty(usb.SettingsWrites);
    });

    [Fact]
    public void OlderFirmwareKeepsTheSoftwareLockAlone() => WithConfigDir(() =>
    {
        // 0x04 is ClipGuard off on 1.0.14 and must stay as it is.
        var usb = UnitWithByte2(0x04, 1, 0, 14);
        using DeviceManager manager = Manager(new WaveXlrMk2Device(usb));

        Assert.Null(Lock(manager, true));
        Assert.Null(Lock(manager, false));
        Assert.Null(Lock(manager, true));

        Assert.Empty(usb.SettingsWrites);
        Assert.Equal(0x04, usb.Blocks[0x0004][2]);
        Assert.True(manager.Snapshot().State!.GainLocked);
        Assert.Null(manager.Snapshot().State!.UnitGainLock);
    });

    [Fact]
    public void TheWave3Mk2KeepsTheSoftwareLockAlone() => WithConfigDir(() =>
    {
        var usb = UnitWithByte2(0x00, 0, 2, 3);
        usb.Bank = 0x0103;
        using DeviceManager manager = Manager(new Wave3Mk2Device(usb));

        Assert.Null(Lock(manager, true));

        Assert.DoesNotContain(usb.SettingsWrites, write => (write[2] & 0x04) != 0);
        Assert.True(manager.Snapshot().State!.GainLocked);
        Assert.Null(manager.Snapshot().State!.UnitGainLock);
    });
}
