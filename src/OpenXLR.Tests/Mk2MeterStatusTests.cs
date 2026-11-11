using System.Reflection;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR MK.2's meter block (0x0002) and status block (0x0006), as
/// the desk measured them on 2026-11-10 on firmware 1.0.15: the codecs
/// against blocks built from those measurements, the driver's gate (the
/// Wave XLR MK.2 from 1.0.15 only, never the XLR Dock MK.2 or the Wave:3
/// MK.2), the poll's microphone and headphone state, the device manager
/// reading the meters only while asked, and the mixer sending the unit's
/// compressor and expander levels only while a client watches them.
/// </summary>
[Collection("xdg-config")]
public sealed class Mk2MeterStatusTests
{
    /// <summary>
    /// Talking into a mono microphone: input -20 dB, the headphones 11 dB
    /// under the monitor mix, USB 6 dB over the input (the make-up gain),
    /// the expander open, the compressor reducing 0.5 dB, the monitor mix
    /// 6 dB under the input, and junk in the three bytes nobody identified.
    /// </summary>
    internal static byte[] Talking() => Convert.FromHexString(
        "5050" +      // 0, 1 input -20
        "b4b4" +      // 2, 3 headphones -45
        "3838" +      // 4, 5 USB -14
        "1234" +      // 6, 7 not identified
        "005050" +    // 8 to 10 expander: no reduction, key -20, out -20
        "02303a" +    // 11 to 13 compressor: 0.5 dB, key -12, out -14.5
        "a5" +        // 14 not identified
        "3a3a" +      // 15, 16 processed -14.5
        "3838" +      // 17, 18 after insert -14
        "6868");      // 19, 20 monitor mix -26

    /// <summary>Silence: every level at the floor, the expander closed by its 24 dB range.</summary>
    internal static byte[] Silent() => Convert.FromHexString("f0f0f0f0f0f00000" + "60f0f0" + "00f0f0" + "00" + "f0f0f0f0f0f0");

    /// <summary>A microphone and headphones plugged in, the settings stored, 48 kHz, the detection at 60.</summary>
    internal static byte[] Status(bool mic = true, bool headphones = true, bool storing = false, int detection = 60)
    {
        byte[] b = new byte[Mk2Status.Length];
        b[0] = (byte)((mic ? 1 : 0) | (headphones ? 2 : 0));
        b[10] = 0x80; b[11] = 0xbb;
        b[14] = (byte)(storing ? 1 : 0);
        b[15] = (byte)(detection & 0xff); b[16] = (byte)(detection >> 8);
        return b;
    }

    [Fact]
    public void TheMeterBlockDecodesAsTheDeskMeasuredIt()
    {
        Mk2MeterReading r = Mk2Meters.Decode(Talking())!;
        Assert.Equal(new ProStereoMeter(-20, -20), r.Input);
        Assert.Equal(new ProStereoMeter(-45, -45), r.Headphones);
        Assert.Equal(new ProStereoMeter(-14, -14), r.Usb);
        Assert.Equal(new Mk2DynamicsMeter(0, -20, -20), r.Expander);
        Assert.Equal(new Mk2DynamicsMeter(0.5, -12, -14.5), r.Compressor);
        Assert.Equal(new ProStereoMeter(-14.5, -14.5), r.Processed);
        Assert.Equal(new ProStereoMeter(-14, -14), r.AfterInsert);
        Assert.Equal(new ProStereoMeter(-26, -26), r.Monitor);

        // Nothing is read from bytes 6, 7 and 14.
        byte[] other = Talking();
        other[6] = other[7] = other[14] = 0;
        Assert.Equal(r, Mk2Meters.Decode(other));

        // In silence the levels are null and the expander reports its range.
        Mk2MeterReading quiet = Mk2Meters.Decode(Silent())!;
        Assert.Equal(new ProStereoMeter(null, null), quiet.Input);
        Assert.Equal(new Mk2DynamicsMeter(24, null, null), quiet.Expander);
        Assert.Equal(new Mk2DynamicsMeter(0, null, null), quiet.Compressor);
    }

    [Fact]
    public void ABlockOfAnotherLengthIsNotDecoded()
    {
        Assert.Null(Mk2Meters.Decode(new byte[20]));
        Assert.Null(Mk2Meters.Decode(new byte[22]));
        Assert.Equal(new Dictionary<string, string> { ["meters"] = "not decoded: block 0x0002 is 0 bytes, not 21" }, Mk2Meters.Describe([]));
        Assert.Null(Mk2Status.Decode(new byte[16]));
        Assert.Equal(new Dictionary<string, string> { ["status"] = "not decoded: block 0x0006 is 18 bytes, not 17" }, Mk2Status.Describe(new byte[18]));
    }

    [Fact]
    public void TheDiagnosticsLinesNameEachLevelAndProcessor()
    {
        IReadOnlyDictionary<string, string> lines = Mk2Meters.Describe(Talking());
        Assert.Equal("input -20/-20, headphones -45/-45, USB -14/-14, processed -14.5/-14.5, after insert -14/-14, monitor mix -26/-26",
            lines["meterLevels"]);
        Assert.Equal("expander 0 dB, key -20, out -20; compressor -0.5 dB, key -12, out -14.5", lines["meterDynamics"]);
        Assert.Equal("expander -24 dB, key -, out -; compressor 0 dB, key -, out -", Mk2Meters.Describe(Silent())["meterDynamics"]);
        Assert.Equal("microphone connected (detection 60), headphones connected, not storing, 48000 Hz", Mk2Status.Describe(Status())["status"]);
        Assert.Equal("microphone not connected (detection 373), headphones not connected, storing settings, 48000 Hz",
            Mk2Status.Describe(Status(mic: false, headphones: false, storing: true, detection: 373))["status"]);
    }

    [Fact]
    public void TheStatusBlockDecodesAsTheDeskMeasuredIt()
    {
        Assert.Equal(new Mk2StatusReading(true, true, 60, false, 48000), Mk2Status.Decode(Status()));
        Assert.Equal(new Mk2StatusReading(false, true, 373, true, 48000), Mk2Status.Decode(Status(mic: false, storing: true, detection: 373)));
        Assert.Equal(new Mk2StatusReading(true, false, 70, false, 48000), Mk2Status.Decode(Status(headphones: false, detection: 70)));
        // Bytes 1 and 2 are not decoded.
        byte[] counted = Status();
        counted[1] = 7;
        counted[2] = 1;
        Assert.Equal(Mk2Status.Decode(Status()), Mk2Status.Decode(counted));
    }

    private static Mk2HardwareEqTests.Unit Unit(byte major = 1, byte minor = 0, byte patch = 15, ushort bank = 0x0203)
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = bank };
        unit.Blocks[0x0000] = Mk2HardwareEqTests.Unit.Identity(major, minor, patch);
        unit.Blocks[Mk2Meters.Block] = Talking();
        unit.Blocks[Mk2Status.Block] = Status(mic: false);
        return unit;
    }

    private static int Reads(Mk2HardwareEqTests.Unit unit, ushort block) => unit.Calls.Count(c => c.Type == 0xC1 && c.Block == block);

    [Fact]
    public void TheWaveXlrMk2On1015ReadsItsStatusWithEachPoll()
    {
        var unit = Unit();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.True(mk2.Capabilities.HardwareDynamics);
        Assert.True(mk2.Capabilities.DynamicsMeters);
        unit.Calls.Clear();
        DeviceState state = mk2.ReadState();
        Assert.False(state.MicConnected);
        Assert.True(state.HeadphonesConnected);
        Assert.Equal(1, Reads(unit, Mk2Status.Block));
        // The meters are not polled.
        Assert.Equal(0, Reads(unit, Mk2Meters.Block));

        unit.Blocks[Mk2Status.Block] = Status(headphones: false);
        state = mk2.ReadState();
        Assert.True(state.MicConnected);
        Assert.False(state.HeadphonesConnected);

        // A status read that stalls or answers short leaves the state without
        // it, and every control is still read.
        unit.Blocks.Remove(Mk2Status.Block);
        state = mk2.ReadState();
        Assert.Null(state.MicConnected);
        Assert.Null(state.HeadphonesConnected);
        unit.Blocks[Mk2Status.Block] = new byte[5];
        Assert.Null(mk2.ReadState().MicConnected);

        // One read of the meters, decoded; a failed one gives null.
        Assert.Equal(-12, mk2.ReadInputMeters()!.Compressor.KeyDb);
        unit.Blocks.Remove(Mk2Meters.Block);
        Assert.Null(mk2.ReadInputMeters());
    }

    [Fact]
    public void TheDiagnosticsCarryBothBlocksDecoded()
    {
        var unit = Unit();
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        IReadOnlyDictionary<string, string> blocks = mk2.DumpBlocks();
        Assert.Equal(Convert.ToHexString(Talking()), blocks["meterBlock"]);
        Assert.Equal(Mk2Meters.Describe(Talking())["meterDynamics"], blocks["meterDynamics"]);
        Assert.Equal(Mk2Meters.Describe(Talking())["meterLevels"], blocks["meterLevels"]);
        Assert.Equal(Convert.ToHexString(Status(mic: false)), blocks["statusBlock"]);
        Assert.StartsWith("microphone not connected", blocks["status"]);
    }

    public static TheoryData<string> Unchecked => ["older Wave XLR MK.2", "XLR Dock MK.2", "Wave:3 MK.2", "unread version"];

    [Theory]
    [MemberData(nameof(Unchecked))]
    public void OtherModelsAndOlderFirmwareReadNeitherBlock(string which)
    {
        Mk2HardwareEqTests.Unit unit = which switch
        {
            "older Wave XLR MK.2" => Unit(patch: 14),
            "Wave:3 MK.2" => Unit(bank: 0x0103),
            _ => Unit(),
        };
        if (which == "unread version") unit.Blocks.Remove(0x0000);
        using WaveXlrMk2Device device = which switch
        {
            "XLR Dock MK.2" => new XlrDockMk2Device(unit),
            "Wave:3 MK.2" => new Wave3Mk2Device(unit),
            _ => new WaveXlrMk2Device(unit),
        };
        device.Connect();
        Assert.False(device.Capabilities.DynamicsMeters);
        DeviceState state = device.ReadState();
        Assert.Null(state.MicConnected);
        Assert.Null(state.HeadphonesConnected);
        Assert.Null(device.ReadInputMeters());
        IReadOnlyDictionary<string, string> blocks = device.DumpBlocks();
        Assert.DoesNotContain("meterBlock", blocks.Keys);
        Assert.DoesNotContain("status", blocks.Keys);
        Assert.Equal(0, Reads(unit, Mk2Meters.Block));
        Assert.Equal(0, Reads(unit, Mk2Status.Block));
    }

    [Fact]
    public void WithoutTheUnitsCompressorAndExpanderItsLevelsAreNotOffered()
    {
        // The status is still read: it does not depend on them.
        var unit = Unit();
        unit.Blocks[0x0004][12] = 0xff;   // a compressor ratio the unit does not take
        using var mk2 = new WaveXlrMk2Device(unit);
        mk2.Connect();
        Assert.False(mk2.Capabilities.HardwareDynamics);
        Assert.False(mk2.Capabilities.DynamicsMeters);
        Assert.Null(mk2.ReadInputMeters());
        Assert.False(mk2.ReadState().MicConnected);
    }

    [Fact]
    public void TheManagerPublishesWhatIsPluggedInAndSavesNoneOfIt() => WithConfigDir(() =>
    {
        var unit = Unit();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrMk2Device(unit)], time: new ManualTime());
        devices.SweepOnce();
        Assert.False(devices.Snapshot().State!.MicConnected);
        Assert.True(devices.Snapshot().Capabilities!.DynamicsMeters);

        // Plugging the microphone in changes the published state at the next poll.
        unit.Blocks[Mk2Status.Block] = Status();
        devices.SweepOnce();
        Assert.True(devices.Snapshot().State!.MicConnected);
        Assert.True(devices.Snapshot().State!.HeadphonesConnected);

        // A profile and the saved settings carry neither.
        Assert.Null(devices.SavedState()!.MicConnected);
        Assert.Null(devices.SavedState()!.HeadphonesConnected);
        Assert.Null(DeviceStateStore.Hardware(devices.Snapshot().State!).MicConnected);
    });

    [Fact]
    public void TheManagerReadsTheMetersOnlyWhileAsked() => WithConfigDir(() =>
    {
        var unit = Unit();
        var time = new ManualTime();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrMk2Device(unit)], time: time);
        devices.SweepOnce();
        Assert.True(devices.PublishedEqSupport!.DynamicsLevels);

        // Nobody asking: neither the poll nor the read between polls touches the block.
        unit.Calls.Clear();
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, Reads(unit, Mk2Meters.Block));

        // Asked: the next poll reads it once, and once more between polls.
        Assert.Null(devices.UnitDynamicsMeters(1));
        Assert.Null(devices.UnitDynamicsMeters(2));
        devices.SweepOnce();
        Assert.Equal(1, Reads(unit, Mk2Meters.Block));
        Assert.Equal(-12, devices.UnitDynamicsMeters(1)!.Compressor.KeyDb);
        unit.Blocks[Mk2Meters.Block] = Silent();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(2, Reads(unit, Mk2Meters.Block));
        Assert.Equal(24, devices.UnitDynamicsMeters(1)!.Expander.ReductionDb);

        // A read that fails leaves no reading; the next good one is back.
        unit.Blocks.Remove(Mk2Meters.Block);
        devices.SweepOnce();
        Assert.Null(devices.UnitDynamicsMeters(1));
        unit.Blocks[Mk2Meters.Block] = Talking();
        devices.SweepOnce();
        Assert.NotNull(devices.UnitDynamicsMeters(1));

        // The hold runs out: nothing more is read, and the reading is gone.
        time.Advance(DeviceManager.UnitMetersHold);
        unit.Calls.Clear();
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, Reads(unit, Mk2Meters.Block));
        Assert.Null(devices.UnitDynamicsMeters(1));
    });

    [Fact]
    public void TheManagerNeverReadsTheMetersOfAnUncheckedModel() => WithConfigDir(() =>
    {
        var unit = Unit();
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new XlrDockMk2Device(unit)], time: new ManualTime());
        devices.SweepOnce();
        Assert.Null(devices.UnitDynamicsMeters(1));
        devices.SweepOnce();
        devices.ReadUnitMetersBetweenPolls();
        Assert.Equal(0, Reads(unit, Mk2Meters.Block));
        Assert.Null(devices.Snapshot().State!.MicConnected);
    });

    private const string Mk2 = "0fd9:00b6@0123456789abcdef";

    /// <summary>A device side that keeps every post, verified at once, and whose meters the test sets, counting asks.</summary>
    private sealed class Port : IDeviceEqPort
    {
        private readonly Dictionary<int, DeviceDynamics> _held = new() { [1] = new DeviceDynamics(false, false, Factory) };
        public Mk2MeterReading? Reading;
        public int Asks;

        public void Post(string unit, int input, DeviceEq eq, string owner) { }
        public DeviceEqStatus Status(string unit, int input) => DeviceEqStatus.None;
        public void PostDynamics(string unit, int input, DeviceDynamics dynamics, string owner)
            => _held[input] = dynamics.Values is null ? _held[input] with { Compressor = dynamics.Compressor, Expander = dynamics.Expander } : dynamics;
        public DeviceDynamicsStatus DynamicsStatus(string unit, int input) => new(DeviceEqPhase.Verified, _held[input]);
        public DeviceDynamics? HeldDynamics(string unit, int input) => _held[input];

        public Mk2MeterReading? DynamicsMeters(string unit, int input)
        {
            Assert.Equal((Mk2, 1), (unit, input));
            Asks++;
            return Reading;
        }
    }

    private static readonly DeviceDynamicsValues Factory = new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24);

    private static UnitEqSupport Support(bool levels)
        => new("Wave XLR MK.2", "1.0.15", true) { Dynamics = new DeviceEqSupport(true) { MinimumFirmware = "1.0.15" }, DynamicsLevels = levels };

    private static Mixer MixerOnUnit(Port port, bool levels)
    {
        var mixer = new Mixer { Time = new ManualTime() };
        mixer.SetDrivenUnit(Mk2);
        mixer.SetDeviceEqPort(port);
        mixer.SetUnitEqSupport(new Dictionary<string, UnitEqSupport> { [Mk2] = Support(levels) });
        mixer.SetCompressor("xlr1", new MicCompressorChange(Enabled: true, Threshold: -12));
        Assert.True(mixer.DynamicsRunOnDevice("xlr1"));
        typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
        return mixer;
    }

    [Fact]
    public void TheMixerSendsTheUnitsLevelsOnlyWhileAClientWatches()
    {
        var port = new Port();
        using Mixer mixer = MixerOnUnit(port, levels: true);
        MicCompressorState compressor = mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").Compressor!;
        Assert.True(compressor.Device!.Levels);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").Eq!.Device!.Levels);

        // Nobody watches: the unit is not asked.
        Assert.DoesNotContain("comp:xlr1", mixer.ReadMeters().Keys);
        Assert.Equal(0, port.Asks);

        // Watched: asked each frame; the first has no reading yet.
        mixer.WatchDynamics("xlr1");
        Assert.DoesNotContain("comp:xlr1", mixer.ReadMeters().Keys);
        Assert.Equal(1, port.Asks);
        port.Reading = Mk2Meters.Decode(Talking());
        IReadOnlyDictionary<string, double[]> frame = mixer.ReadMeters();
        Assert.Equal([-12, 0.5, -14.5], frame["comp:xlr1"]);
        Assert.Equal([-20, 0, -20], frame["exp:xlr1"]);
        port.Reading = Mk2Meters.Decode(Silent());
        frame = mixer.ReadMeters();
        Assert.Equal([-120, 24, -120], frame["exp:xlr1"]);
        Assert.Equal([-120, 0, -120], frame["comp:xlr1"]);

        // The lease runs out: the sweep drops the watch and the unit is no longer asked.
        var watch = (Dictionary<string, long>)typeof(Mixer).GetField("_dynamicsWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        watch["xlr1"] = 0;
        typeof(Mixer).GetMethod("EnsureDynamicsMetersLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mixer, null);
        int asks = port.Asks;
        Assert.DoesNotContain("comp:xlr1", mixer.ReadMeters().Keys);
        Assert.Equal(asks, port.Asks);
    }

    [Fact]
    public void AUnitThatReportsNoLevelsIsNeverAsked()
    {
        var port = new Port { Reading = Mk2Meters.Decode(Talking()) };
        using Mixer mixer = MixerOnUnit(port, levels: false);
        Assert.False(mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").Compressor!.Device!.Levels);
        mixer.WatchDynamics("xlr1");
        Assert.DoesNotContain("comp:xlr1", mixer.ReadMeters().Keys);
        Assert.Equal(0, port.Asks);
    }

    [Fact]
    public void TheUnitsLevelsKeepTheirQuarterDecibels()
    {
        Assert.Equal([-12.2, 0.25, -14.8], Mixer.UnitDynamicsLevels(new Mk2DynamicsMeter(0.25, -12.25, -14.75)));
        Assert.Equal([-120, 60, -120], Mixer.UnitDynamicsLevels(new Mk2DynamicsMeter(60, null, null)));
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-mk2-meters-").FullName;
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
