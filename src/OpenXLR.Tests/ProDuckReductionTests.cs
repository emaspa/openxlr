using Microsoft.Extensions.Configuration;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;
using static OpenXLR.Tests.ProMetersTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's own ducking reduction: the device manager reads block
/// 0x0002 only while the unit holds an enabled target and the reduction was
/// asked for within the hold, reports the largest of the ducked entries,
/// and the mixer carries it under each ducked mix's <c>duck:</c> key.
/// </summary>
[Collection("xdg-config")]
public sealed class ProDuckReductionTests
{
    private const string Unit = "0fd9:00b4";

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-meters-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheManagerReadsTheMetersOnlyWhileTheUnitDucksAndSomeoneAsks() => WithConfigDir(() =>
    {
        byte[] meters = Idle();
        meters[ProMeters.DuckerOffset + 4 * 5] = 0x18;   // pair 1 lowered by 6 dB
        meters[ProMeters.DuckerOffset + 4 * 9] = 0x30;   // pair 5 by 12 dB, not ducked by this target
        var unit = ProUnit(meters);
        var time = new ManualTime();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: time);
        devices.SweepOnce();

        // Nothing ducks on the unit: an ask reads nothing.
        unit.Calls.Clear();
        Assert.Null(devices.UnitDuckReduction(Unit));
        devices.SweepOnce();
        Assert.Equal(0, MeterReads(unit));

        // Ducking held on the unit, nobody asking: still nothing.
        devices.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        devices.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, devices.DuckingStatus(Unit).Phase);
        time.Advance(TimeSpan.FromSeconds(2));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(0, MeterReads(unit));

        // Asked: the next poll reads the block once and reports the ducked pair's ducker alone.
        Assert.Null(devices.UnitDuckReduction(Unit));
        devices.SweepOnce();
        Assert.Equal(1, MeterReads(unit));
        Assert.Equal(6, devices.UnitDuckReduction(Unit));
        Assert.Null(devices.UnitDuckReduction("0fd9:00b4@elsewhere"));

        // A reading follows the unit; each poll within the hold reads once.
        unit.Blocks[ProMeters.Block][ProMeters.DuckerOffset + 4 * 5] = 0x07;
        devices.SweepOnce();
        Assert.Equal(2, MeterReads(unit));
        Assert.Equal(1.75, devices.UnitDuckReduction(Unit));

        // The hold runs out: the poll stops reading and the last reading is gone.
        time.Advance(DeviceManager.UnitMetersHold);
        devices.SweepOnce();
        Assert.Equal(2, MeterReads(unit));
        Assert.Null(devices.UnitDuckReduction(Unit));

        // Ducking switched off: asked or not, nothing is read.
        devices.PostDucking(Unit, ProDuckingTarget.Off with { Pairs = new HashSet<int> { 1 } }, "XLR 1");
        devices.SweepOnce();
        devices.SweepOnce();
        Assert.Null(devices.UnitDuckReduction(Unit));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(0, MeterReads(unit));
    });

    [Fact]
    public void AUnitThatRefusesItsMetersKeepsAnsweringAndReportsNothing() => WithConfigDir(() =>
    {
        var unit = ProUnit(meters: null);
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: new ManualTime());
        devices.SweepOnce();
        devices.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        devices.SweepOnce();
        Assert.Null(devices.UnitDuckReduction(Unit));
        devices.SweepOnce();
        Assert.Null(devices.UnitDuckReduction(Unit));
        Assert.NotNull(devices.Snapshot().Capabilities);
        Assert.Equal(DeviceDuckingPhase.Verified, devices.DuckingStatus(Unit).Phase);
    });

    [Fact]
    public void TheMixerReportsTheUnitsReductionUnderEachDuckedMix() => WithConfigDir(() =>
    {
        byte[] meters = Idle();
        meters[ProMeters.DuckerOffset + 4 * 5] = 0x30;   // pair 1, the jacks' feed, lowered by 12 dB
        var unit = ProUnit(meters);
        unit.Blocks[0x0001][12] |= 0x20;                 // the jacks' feed is in the unit's mix
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: new ManualTime());
        devices.SweepOnce();
        var port = new DeviceDuckingPort(devices);
        using Mixer mixer = DuckingDaemonTests.ProMixer(port, register: false);
        (string id, UnitDuckingSupport support) = port.Primary()!.Value;
        mixer.SetUnitDuckingSupport(id, support);

        // Off: no duck key, and nothing asks the unit, so the poll reads no meters.
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("duck:", StringComparison.Ordinal));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(0, MeterReads(unit));

        DuckingSetup setup = DuckingSetup.Default(mixer.Config) with { Enabled = true };
        mixer.SetDuckingSetup(setup, _ => null);
        devices.SweepOnce();
        mixer.SyncDucking();
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Assert.Equal(0, MeterReads(unit));

        // The first frame asks and carries nothing yet; the poll after it reads; the next frame carries it.
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("duck:", StringComparison.Ordinal));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(1, MeterReads(unit));
        IReadOnlyDictionary<string, double[]> frame = mixer.ReadMeters();
        Assert.NotEmpty(setup.Mixes);
        foreach (string mix in setup.Mixes) Assert.Equal([12.0], frame["duck:" + mix]);
        Assert.Equal(setup.Mixes.Count, frame.Keys.Count(k => k.StartsWith("duck:", StringComparison.Ordinal)));

        // Software chosen: the unit lets go and the key goes with it.
        mixer.SetDuckingSetup(setup with { Processing = "software" }, _ => null);
        devices.SweepOnce();
        mixer.SyncDucking();
        Assert.NotEqual("device", mixer.DuckingSnapshot().Backend);
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("duck:", StringComparison.Ordinal));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(0, MeterReads(unit));
    });
}
