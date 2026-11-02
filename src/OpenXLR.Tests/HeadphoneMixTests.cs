using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's headphone-mix bits as the device manager drives them:
/// a write only when the unit holds something else, and the return pair left
/// as the unit holds it when the caller has no jack in use. The manager reads
/// gain locks and daemon settings from the configuration directory, which
/// these tests redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class HeadphoneMixTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-headphone-mix-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public HeadphoneMixTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// A Wave XLR Pro modelled as its block 0x0001, which takes the plan's
    /// edits as the driver makes them and counts the headphone-mix writes:
    /// each byte the plan changed, by part.
    /// </summary>
    private sealed class Pro : IAudioDevice
    {
        public readonly byte[] Block = ProMixPlanTests.Neutral();
        public int ReturnWrites, DirectWrites, LevelWrites, Writes;
        public readonly List<string> Order = [];

        public Pro(bool monitorReturn, bool micDirect)
        {
            if (monitorReturn) Block[12] |= 0x20;
            if (micDirect) Block[12] |= 0x01;
            // What Wave Link might leave: both microphones at -14 dB.
            Block[0] = Block[1] = ProMixPlan.LevelCell(-14);
        }

        public bool MonitorReturn => ProMixPlan.Read(Block).MonitorReturn;
        public bool MicDirect
        {
            get => ProMixPlan.Read(Block).Mic1Direct;
            set => Block[12] = (byte)(value ? Block[12] | 0x01 : Block[12] & ~0x01);
        }
        public bool Mic2Direct => ProMixPlan.Read(Block).Mic2Direct;
        public double MicLevelDb => ProMixPlan.Read(Block).Mic1LevelDb;
        public double Mic2LevelDb => ProMixPlan.Read(Block).Mic2LevelDb;

        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, OutputRouting = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState() => ProMixPlanTests.StateOf(Block);

        public ProMixWrite? WriteMix(ProMixPlan plan)
        {
            byte[] before = (byte[])Block.Clone();
            ProMixChanges changes = plan.Apply(before, Block);
            if (Block.AsSpan().SequenceEqual(before)) return new ProMixWrite(changes, false, "not read", false, "no change", false);
            Writes++;
            if (before[0] != Block[0]) { LevelWrites++; Order.Add("level"); }
            if (before[1] != Block[1]) Order.Add("level2");
            if (((before[12] ^ Block[12]) & 0x20) != 0) { ReturnWrites++; Order.Add("return"); }
            if (((before[12] ^ Block[12]) & 0x01) != 0) { DirectWrites++; Order.Add("direct"); }
            if (((before[12] ^ Block[12]) & 0x02) != 0) Order.Add("direct2");
            return new ProMixWrite(changes, false, "not read", true, ProDucking.Diff(before, Block), changes.Commits());
        }

        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    /// <summary>The plan the mixer posts for the headphone mix, with no selector.</summary>
    private static ProMixPlan Mix(bool? monitorReturn, bool micDirect, double micLevelDb = 0, bool mic2Direct = false, double mic2LevelDb = 0)
        => new() { MonitorReturn = monitorReturn, Mic1 = new(micDirect, micLevelDb), Mic2 = new(mic2Direct, mic2LevelDb) };

    private static DeviceManager Connect(Pro unit)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADirectPathLeftOnIsClearedOnceAndTheReturnPairIsLeftAlone(bool monitorReturn)
    {
        var unit = new Pro(monitorReturn, micDirect: true);
        using DeviceManager manager = Connect(unit);

        Assert.True(manager.EnsureMix(Mix(monitorReturn: null, micDirect: false)));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
        Assert.Equal((monitorReturn, 0), (unit.MonitorReturn, unit.ReturnWrites));
        Assert.False(manager.Snapshot().State!.HpMixMicDirect);

        // Later sweeps find it off: nothing is written, so the playback
        // stream is not restarted again.
        for (int i = 0; i < 3; i++)
        {
            manager.SweepOnce();
            Assert.False(manager.EnsureMix(Mix(monitorReturn: null, micDirect: false)));
        }
        Assert.Equal((1, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void APathAlreadyOffIsNotWritten()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureMix(Mix(monitorReturn: null, micDirect: false)));
        Assert.Equal((0, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void AnotherProgramTurningThePathOnAgainIsUndoneOnTheNextSweep()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureMix(Mix(monitorReturn: null, micDirect: false)));
        unit.MicDirect = true;   // written by something outside OpenXLR
        manager.SweepOnce();
        Assert.True(manager.EnsureMix(Mix(monitorReturn: null, micDirect: false)));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
    }

    [Fact]
    public void WithAJackInUseTheReturnPairIsAssertedAndThePathFollowsTheMixer()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true)));
        Assert.Equal((true, true), (unit.MonitorReturn, unit.MicDirect));
        Assert.False(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true)));
        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: false)));
        Assert.Equal((true, false), (unit.MonitorReturn, unit.MicDirect));
        Assert.Equal((1, 2), (unit.ReturnWrites, unit.DirectWrites));
    }

    [Fact]
    public void ThePathOpensAtTheSendsLevelAndALevelChangeAloneNeedsNoRestart()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);

        // The level goes in with the path, in the same write, so the jacks
        // never play the microphone at the level another program left.
        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true, micLevelDb: 0)));
        Assert.Equal(["level", "direct"], unit.Order);
        Assert.Equal(1, unit.Writes);
        Assert.Equal(0, manager.Snapshot().State!.HpMixMicLevelDb);

        // A send or volume change moves the level only: no membership write,
        // so the caller does not restart the playback stream.
        Assert.False(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true, micLevelDb: -18.06)));
        Assert.Equal(-18, unit.MicLevelDb);
        Assert.Equal((2, 1), (unit.LevelWrites, unit.DirectWrites));

        // Within the cell's half-dB step, nothing is written; a send pulled
        // all the way down takes the floor.
        Assert.False(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true, micLevelDb: -18.1)));
        Assert.Equal(2, unit.LevelWrites);
        Assert.False(manager.EnsureMix(Mix(monitorReturn: true, micDirect: true, micLevelDb: double.NegativeInfinity)));
        Assert.Equal((3, -60.0), (unit.LevelWrites, unit.MicLevelDb));

        // With the path off the level is left as it is.
        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: false, micLevelDb: 0)));
        Assert.Equal((3, -60.0), (unit.LevelWrites, unit.MicLevelDb));
    }

    [Fact]
    public void Xlr2sPathOpensAtItsOwnLevelBesideXlr1s()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);

        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: false, mic2Direct: true, mic2LevelDb: -6)));
        Assert.Equal(["level2", "direct2"], unit.Order);
        Assert.Equal(1, unit.Writes);
        Assert.Equal((true, -6.0, false, -14.0), (unit.Mic2Direct, unit.Mic2LevelDb, unit.MicDirect, unit.MicLevelDb));
        Assert.Equal((true, -6.0), (manager.Snapshot().State!.HpMixMic2Direct, manager.Snapshot().State!.HpMixMic2LevelDb));

        // A level change alone needs no restart; closing the path leaves the level.
        Assert.False(manager.EnsureMix(Mix(monitorReturn: true, micDirect: false, mic2Direct: true, mic2LevelDb: -12)));
        Assert.Equal(-12, unit.Mic2LevelDb);
        Assert.True(manager.EnsureMix(Mix(monitorReturn: true, micDirect: false, mic2Direct: false, mic2LevelDb: 0)));
        Assert.Equal((false, -12.0), (unit.Mic2Direct, unit.Mic2LevelDb));
    }
}
