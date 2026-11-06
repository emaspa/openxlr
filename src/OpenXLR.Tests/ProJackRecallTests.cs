using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A saved state's headphone levels while the Wave XLR Pro builds its jacks'
/// mixes. A profile holds the jacks as software mixing plays them, with
/// section 0 21 dB down; the mode runs section 0 at 0 dB and lowers the jacks
/// by the entry offset instead. A recall in the mode (a replug or a restart
/// with recall on connect) once wrote the saved level as it was, so the jack
/// played 21 dB louder than the profile meant, and leaving the mode added the
/// offset back on top (desk, 2026-11-04: Headphones 2 went from -18 dB to
/// 0 dB). The saved level now reaches the unit lowered by the offset and the
/// output volume, and a state saved in the mode holds where leaving would put
/// the jack.
/// </summary>
[Collection("xdg-config")]
public sealed class ProJackRecallTests
{
    private static HardwareEntry Entry(double? outputVolume, double hp1Offset = 21, double hp2Offset = 21) => new()
    {
        Jacks = new Dictionary<string, double> { ["hp1"] = -51, ["hp2"] = -39, ["lineout"] = -21 },
        Offsets = new Dictionary<string, double> { ["hp1"] = hp1Offset, ["hp2"] = hp2Offset, ["lineout"] = 21 },
        OutputVolume = outputVolume,
    };

    [Theory]
    [InlineData(-18, 21, null, -39)]
    [InlineData(-18, 21, 1.0, -39)]
    [InlineData(-18, 21, 0.8, -44.75)]
    [InlineData(-18, 21, 1.2, -34.25)]
    [InlineData(-18, 28.94, null, -47)]
    [InlineData(-50, 21, null, -60)]
    [InlineData(0, 21, 0.0, -60)]
    public void ASavedLevelReachesTheUnitLoweredByTheModeAndComesBackAsSaved(double saved, double offset, double? volume, double unit)
    {
        HardwareEntry entry = Entry(volume, offset, offset);
        DeviceState inMode = HardwareMixingPlanner.ToUnitJackLevels(new DeviceState { HpVolumeDb = saved, Hp2VolumeDb = saved }, entry);
        Assert.Equal(unit, inMode.HpVolumeDb, 3);
        Assert.Equal(unit, inMode.Hp2VolumeDb, 3);
        // Leaving gives the same level back, so a profile means one loudness in either mode.
        if (unit > -60 && volume is not 0.0)
        {
            Assert.Equal(saved, HardwareMixingPlanner.ToSavedJackLevels(inMode, entry).HpVolumeDb, 3);
            double baseDb = inMode.HpVolumeDb - Mixer.JackOutputVolumeDb(volume);
            Assert.Equal(saved, ProJackLevels.DbOf(ProJackLevels.ByteOf(HardwareMixingPlanner.LeaveLevelDb(baseDb, offset, volume))), 3);
        }
    }

    [Fact]
    public void WithTheOutputVolumeAtZeroAStateSavedInTheModeTakesTheJacksBase()
    {
        HardwareEntry entry = Entry(0.0);
        DeviceState saved = HardwareMixingPlanner.ToSavedJackLevels(new DeviceState { HpVolumeDb = -60, Hp2VolumeDb = -60 }, entry);
        Assert.Equal(-30, saved.HpVolumeDb, 3);
        Assert.Equal(-18, saved.Hp2VolumeDb, 3);
    }

    [Fact]
    public void AProfileRecalledInTheModeIsLoweredOnTheUnitAndSavedAsItWas()
    {
        WithConfigDir(() =>
        {
            var unit = new ProHardwareEqTests.Unit();
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            DeviceState live = manager.Snapshot().State!;

            // Software mixing: the saved levels go to the unit as they are.
            Assert.Null(manager.ApplyProfile(live with { HpVolumeDb = -30, Hp2VolumeDb = -18 }, restoring: true));
            Assert.Equal(120, unit.Blocks[0x0005][0]);
            Assert.Equal(72, unit.Blocks[0x0005][2]);

            // In the mode, the same profile plays at the same loudness: 21 dB
            // lower on the unit, and saved again as it was.
            HardwareEntry? entry = Entry(outputVolume: 1.0);
            manager.HardwareMixingEntry = () => entry;
            Assert.Null(manager.ApplyProfile(live with { HpVolumeDb = -30, Hp2VolumeDb = -18 }, restoring: true));
            Assert.Equal(204, unit.Blocks[0x0005][0]);
            Assert.Equal(156, unit.Blocks[0x0005][2]);
            Assert.Equal(-51, manager.Snapshot().State!.HpVolumeDb, 3);
            DeviceState saved = manager.SavedState()!;
            Assert.Equal(-30, saved.HpVolumeDb, 3);
            Assert.Equal(-18, saved.Hp2VolumeDb, 3);

            // Once the mode is left, a profile is the unit's levels again.
            entry = null;
            Assert.Equal(-51, manager.SavedState()!.HpVolumeDb, 3);
        });
    }

    // Desk, 2026-11-06: both jacks at -30 dB in the mode with the output
    // volume at 45% saved as 0 dB, held to the unit's range, and came back
    // at -41.75 dB. Leaving would put them at +11.81 dB, which only the mode
    // can play; kept above 0 dB, the profile recalls -30 dB.
    [Fact]
    public void AJackTheModePlaysLouderThanSoftwareMixingCanSavesAboveZeroAndRecallsAsItWas()
    {
        HardwareEntry entry = Entry(0.45);
        DeviceState saved = HardwareMixingPlanner.ToSavedJackLevels(new DeviceState { HpVolumeDb = -30, Hp2VolumeDb = -30 }, entry);
        Assert.Equal(11.75, saved.HpVolumeDb, 3);
        Assert.Equal(11.75, saved.Hp2VolumeDb, 3);
        DeviceState recalled = HardwareMixingPlanner.ToUnitJackLevels(saved, entry);
        Assert.Equal(-30, recalled.HpVolumeDb, 3);
        Assert.Equal(-30, recalled.Hp2VolumeDb, 3);
        // Software mixing plays such a level at its loudest.
        Assert.Equal(0, HardwareMixingPlanner.ToSoftwareJackLevels(saved).HpVolumeDb, 3);
        Assert.Equal(-12.5, HardwareMixingPlanner.ToSoftwareJackLevels(saved with { Hp2VolumeDb = -12.5 }).Hp2VolumeDb, 3);
        // The floor stays the unit's.
        Assert.Equal(-60, HardwareMixingPlanner.ToSavedJackLevels(new DeviceState { HpVolumeDb = -60 }, Entry(1.0, 0, 0)).HpVolumeDb, 3);
    }

    [Fact]
    public void AProfileSavedAboveZeroInTheModeRecallsThereAndPlaysAtZeroWithoutIt()
    {
        WithConfigDir(() =>
        {
            var unit = new ProHardwareEqTests.Unit();
            unit.Blocks[0x0005][0] = 120;
            unit.Blocks[0x0005][2] = 120;
            using var pro = new WaveXlrProDevice(unit);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            HardwareEntry? entry = Entry(outputVolume: 0.45);
            manager.HardwareMixingEntry = () => entry;
            DeviceState saved = manager.SavedState()!;
            Assert.Equal(11.75, saved.HpVolumeDb, 3);
            Assert.Equal(11.75, saved.Hp2VolumeDb, 3);

            // What the profile held before the fix, 0 dB, recalls -41.75 dB;
            // the level saved now brings the jacks back to -30 dB.
            Assert.Null(manager.ApplyProfile(saved with { HpVolumeDb = 0, Hp2VolumeDb = 0 }, restoring: true));
            Assert.Equal(167, unit.Blocks[0x0005][0]);
            Assert.Null(manager.ApplyProfile(saved, restoring: true));
            Assert.Equal(120, unit.Blocks[0x0005][0]);
            Assert.Equal(120, unit.Blocks[0x0005][2]);

            // Without the mode the jacks play at 0 dB, written once.
            entry = null;
            Assert.Null(manager.ApplyProfile(saved, restoring: true));
            Assert.Equal(0, unit.Blocks[0x0005][0]);
            Assert.Equal(0, unit.Blocks[0x0005][2]);
            int writes = HpWrites(unit);
            Assert.Null(manager.ApplyProfile(saved, restoring: true));
            Assert.Equal(writes, HpWrites(unit));
        });
    }

    private static int HpWrites(ProHardwareEqTests.Unit unit)
    {
        lock (unit) return unit.Calls.Count(c => c.Type == 0x41 && c.Block == 0x0005);
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-test-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
