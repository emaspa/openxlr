using System.Reflection;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;
using static OpenXLR.Tests.ProMetersTests;

namespace OpenXLR.Tests;

/// <summary>
/// The mix meters of the mixes a Wave XLR Pro builds in hardware mixing,
/// from the unit's own meters: which section level stands for which mix,
/// the level law into the meters' scale, the device manager's gate (the
/// block is read only while the mixer holds the mode and asks within the
/// hold, once per poll whoever asks), the read between polls, and the
/// computer's copy kept for any frame without a reading.
/// </summary>
[Collection("xdg-config")]
public sealed class ProHardwareMixMetersTests
{
    private const string Unit = "0fd9:00b4";

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    /// <summary>The default layout on a ready Pro with the given jacks, the USB Aux port on or off.</summary>
    private static HardwareMixingSetup ProSetup(bool auxPort, params (string Jack, string Feed)[] jacks) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = MixerConfig.Default().Channels,
        Mixes = MixerConfig.Default().Mixes,
        LevelsDb = new Dictionary<string, double> { ["xlr1|monitor"] = -3, ["game|monitor"] = -10 },
        MixVolumesDb = new Dictionary<string, double> { ["monitor"] = 0, ["monitor2"] = 0, ["auxout"] = 0 },
        Jacks = jacks,
        AuxPort = auxPort,
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
    };

    /// <summary>A meters block with section <paramref name="start"/> at the given level bytes, everything else silent.</summary>
    private static byte[] WithSection(int start, byte left, byte right, byte[]? block = null)
    {
        block ??= Idle();
        int at = ProMeters.LevelOffset + 2 * ProMeters.SectionChannel(start);
        block[at] = left;
        block[at + 1] = right;
        return block;
    }

    [Fact]
    public void EachMixTheUnitBuildsAloneReadsItsSectionAndASummedFeedStandsForNoMix()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(ProSetup(auxPort: true, ("hp1", "monitor"), ("hp2", "monitor+monitor2")));
        Assert.True(plan.Active);
        IReadOnlyDictionary<string, int> channels = UnitMixMeters.Channels(plan);
        // Monitor A plays alone on Headphones 1; the Aux mix is section 36's.
        Assert.Equal(ProMeters.SectionChannel(plan.Sections["monitor"]), channels["monitor"]);
        Assert.Equal(ProMeters.FirstSectionChannel + 2, channels["auxout"]);
        // Headphones 2 plays a sum: neither part is read from its section.
        Assert.Equal(["auxout", "monitor"], channels.Keys.Order(StringComparer.Ordinal));

        // The Aux mix only while the port plays it; nothing while the plan does not run.
        Assert.Equal(["monitor"], UnitMixMeters.Channels(HardwareMixingPlanner.Plan(ProSetup(auxPort: false, ("hp1", "monitor")))).Keys);
        Assert.Empty(UnitMixMeters.Channels(HardwareMixingPlanner.Plan(ProSetup(auxPort: true, ("hp1", "monitor")) with { Wanted = false })));
        Assert.Empty(UnitMixMeters.Channels(HardwareMixingPlanner.Plan(ProSetup(auxPort: true, ("hp1", "monitor")) with { Unit = Pro with { Connected = false } })));
    }

    [Fact]
    public void TheUnitsLevelsGoIntoTheMetersScaleAndNoReadingKeepsTheComputersCopy()
    {
        Assert.Equal(1, MeterReader.DisplayOfDb(0));
        Assert.Equal(0.667, MeterReader.DisplayOfDb(-20));
        Assert.Equal(0.5, MeterReader.DisplayOfDb(-30));
        Assert.Equal(0, MeterReader.DisplayOfDb(-60));
        Assert.Equal(0, MeterReader.DisplayOfDb(-63.75));
        Assert.Equal(0, MeterReader.DisplayOfDb(-59.9));   // under the meters' own cut-off
        Assert.Equal(0, MeterReader.DisplayOfDb(null));

        var channels = new Dictionary<string, int> { ["monitor"] = ProMeters.SectionChannel(0), ["auxout"] = ProMeters.SectionChannel(36) };
        // Section 0 at -20 dBFS left and -6 right; section 36 silent.
        ProMeterReading reading = ProMeters.Decode(WithSection(0, 0x50, 0x18))!;
        var meters = new Dictionary<string, double[]>
        {
            ["mix:monitor"] = [0.3, 0.3], ["mix:auxout"] = [0.4, 0.4], ["mix:stream"] = [0.2, 0.2], ["ch:game"] = [0.1, 0.1],
        };
        UnitMixMeters.Apply(meters, channels, reading);
        Assert.Equal([0.667, 0.9], meters["mix:monitor"]);
        Assert.Equal([0.0, 0.0], meters["mix:auxout"]);
        // The computer builds Stream; channels keep their own meters.
        Assert.Equal([0.2, 0.2], meters["mix:stream"]);
        Assert.Equal([0.1, 0.1], meters["ch:game"]);

        // No reading: every mix keeps the computer's copy for the frame.
        var fallback = new Dictionary<string, double[]> { ["mix:monitor"] = [0.3, 0.3] };
        UnitMixMeters.Apply(fallback, channels, null);
        Assert.Equal([0.3, 0.3], fallback["mix:monitor"]);
    }

    [Fact]
    public void TheManagerReadsTheMetersOnlyInHardwareMixingWhileAsked() => WithConfigDir(() =>
    {
        var unit = ProUnit(WithSection(0, 0x50, 0x50));
        var time = new ManualTime();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: time);
        devices.SweepOnce();
        HardwareEntry? entry = null;
        devices.HardwareMixingEntry = () => entry;

        // Software mixing: an ask reads nothing, between polls either.
        unit.Calls.Clear();
        Assert.Null(devices.UnitMeters(Unit));
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, MeterReads(unit));
        Assert.Null(devices.UnitMeters(Unit));

        // Hardware mixing with nobody asking: nothing.
        entry = new HardwareEntry();
        time.Advance(TimeSpan.FromSeconds(2));
        unit.Calls.Clear();
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, MeterReads(unit));

        // Asked: the next poll reads the block once, and once more between polls.
        Assert.Null(devices.UnitMeters(Unit));
        devices.SweepOnce();
        Assert.Equal(1, MeterReads(unit));
        Assert.Equal(-20, devices.UnitMeters(Unit)!.Levels[ProMeters.SectionChannel(0)].LeftDb);
        Assert.Null(devices.UnitMeters("0fd9:00b4@elsewhere"));
        unit.Blocks[ProMeters.Block] = WithSection(0, 0x28, 0x28);
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(2, MeterReads(unit));
        Assert.Equal(-10, devices.UnitMeters(Unit)!.Levels[ProMeters.SectionChannel(0)].RightDb);

        // A read that fails leaves no reading, so the mixer keeps its own copy; the next good one is back.
        unit.Blocks.Remove(ProMeters.Block);
        devices.SweepOnce();
        Assert.Null(devices.UnitMeters(Unit));
        Assert.NotNull(devices.Snapshot().Capabilities);
        unit.Blocks[ProMeters.Block] = WithSection(0, 0x50, 0x50);
        devices.SweepOnce();
        Assert.NotNull(devices.UnitMeters(Unit));

        // The hold runs out: the poll stops reading, between polls too, and the reading is gone.
        time.Advance(DeviceManager.UnitMetersHold);
        unit.Calls.Clear();
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, MeterReads(unit));
        Assert.Null(devices.UnitMeters(Unit));

        // The ask just made keeps the next poll reading; then the mode leaves and nothing is read or reported.
        devices.SweepOnce();
        Assert.Equal(1, MeterReads(unit));
        entry = new HardwareEntry { Leaving = true };
        devices.HardwareMixingEntry = () => entry is { Leaving: false } held ? held : null;
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(1, MeterReads(unit));
        Assert.Null(devices.UnitMeters(Unit));
    });

    [Fact]
    public void DuckingAndTheMixMetersShareOneReadPerPoll() => WithConfigDir(() =>
    {
        byte[] meters = WithSection(0, 0x50, 0x50);
        meters[ProMeters.DuckerOffset + 4 * 5] = 0x18;   // pair 1 lowered by 6 dB
        var unit = ProUnit(meters);
        var time = new ManualTime();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: time);
        devices.SweepOnce();
        devices.PostDucking(Unit, On(ProReturnPairs.Monitor), "XLR 1");
        devices.SweepOnce();
        Assert.Equal(DeviceDuckingPhase.Verified, devices.DuckingStatus(Unit).Phase);
        devices.HardwareMixingEntry = () => new HardwareEntry();

        Assert.Null(devices.UnitDuckReduction(Unit));
        Assert.Null(devices.UnitMeters(Unit));
        unit.Calls.Clear();
        devices.SweepOnce();
        Assert.Equal(1, MeterReads(unit));
        Assert.Equal(6, devices.UnitDuckReduction(Unit));
        Assert.Equal(-20, devices.UnitMeters(Unit)!.Levels[ProMeters.SectionChannel(0)].LeftDb);
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(2, MeterReads(unit));
    });

    [Fact]
    public void TheMixerShowsTheUnitsLevelForEachMixItBuildsOnlyWhileTheUnitBuildsThem()
    {
        var port = new MetersPort();
        using var mixer = new Mixer();
        mixer.SetDrivenUnit(Unit);
        mixer.SetDeviceDuckingPort(port);

        // Software mixing: nothing asks the unit, and no mix key is made up.
        Assert.DoesNotContain(mixer.ReadMeters().Keys, k => k.StartsWith("mix:", StringComparison.Ordinal));
        Assert.Equal(0, port.Asks);

        // A plan that runs is not enough: the unit builds the jacks' mixes only once the graph entered the mode.
        mixer.NoteHardwarePlan(HardwareMixingPlanner.Plan(ProSetup(auxPort: true, ("hp1", "monitor"))));
        mixer.BeginHardwareMixing(new HardwareEntry());
        mixer.ReadMeters();
        Assert.Equal(0, port.Asks);
        typeof(Mixer).GetField("_hardwareGraphEntered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);

        // Asked each frame; the first has no reading yet, so the computer's copy stays.
        Assert.DoesNotContain("mix:monitor", mixer.ReadMeters().Keys);
        Assert.Equal(1, port.Asks);
        Assert.Equal(Unit, port.Unit);
        port.Reading = ProMeters.Decode(WithSection(36, 0x28, 0x50, WithSection(0, 0x50, 0x18)));
        IReadOnlyDictionary<string, double[]> frame = mixer.ReadMeters();
        Assert.Equal([0.667, 0.9], frame["mix:monitor"]);
        Assert.Equal([0.833, 0.667], frame["mix:auxout"]);
        Assert.DoesNotContain("mix:stream", frame.Keys);

        // Leaving: the unit is no longer asked.
        mixer.LeaveHardwareMixing();
        int asks = port.Asks;
        Assert.DoesNotContain("mix:monitor", mixer.ReadMeters().Keys);
        Assert.Equal(asks, port.Asks);
    }

    /// <summary>A device side whose meters the test sets, counting asks.</summary>
    private sealed class MetersPort : IDeviceDuckingPort
    {
        public ProMeterReading? Reading;
        public int Asks;
        public string? Unit;

        public void Post(string unit, ProDuckingTarget target, string owner) { }

        public DeviceDuckingStatus Status(string unit) => DeviceDuckingStatus.None;

        public ProMeterReading? Meters(string unit)
        {
            Asks++;
            Unit = unit;
            return Reading;
        }
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-mix-meters-").FullName;
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
