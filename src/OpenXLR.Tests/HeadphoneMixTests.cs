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

    /// <summary>A Wave XLR Pro that counts the headphone-mix writes it takes.</summary>
    private sealed class Pro(bool monitorReturn, bool micDirect) : IAudioDevice
    {
        public bool MonitorReturn = monitorReturn, MicDirect = micDirect, Mic2Direct;
        public double MicLevelDb = -14, Mic2LevelDb = -14;   // what Wave Link might leave
        public int ReturnWrites, DirectWrites, LevelWrites;
        public readonly List<string> Order = [];
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, OutputRouting = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState() => new()
        {
            GainDb = 30, HpMixMonitorReturn = MonitorReturn, HpMixMicDirect = MicDirect, HpMixMicLevelDb = MicLevelDb,
            HpMixMic2Direct = Mic2Direct, HpMixMic2LevelDb = Mic2LevelDb,
        };
        public void SetHpMixMonitorReturn(bool on) { ReturnWrites++; MonitorReturn = on; Order.Add("return"); }
        public void SetHpMixMicDirect(bool on) { DirectWrites++; MicDirect = on; Order.Add("direct"); }
        public void SetHpMixMicLevelDb(double db) { LevelWrites++; MicLevelDb = Math.Round(Math.Clamp(db, -60, 12) * 2) / 2; Order.Add("level"); }
        public void SetHpMixMic2Direct(bool on) { Mic2Direct = on; Order.Add("direct2"); }
        public void SetHpMixMic2LevelDb(double db) { Mic2LevelDb = Math.Round(Math.Clamp(db, -60, 12) * 2) / 2; Order.Add("level2"); }
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

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

        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
        Assert.Equal((monitorReturn, 0), (unit.MonitorReturn, unit.ReturnWrites));
        Assert.False(manager.Snapshot().State!.HpMixMicDirect);

        // Later sweeps find it off: nothing is written, so the playback
        // stream is not restarted again.
        for (int i = 0; i < 3; i++)
        {
            manager.SweepOnce();
            Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        }
        Assert.Equal((1, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void APathAlreadyOffIsNotWritten()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.Equal((0, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void AnotherProgramTurningThePathOnAgainIsUndoneOnTheNextSweep()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        unit.MicDirect = true;   // written by something outside OpenXLR
        manager.SweepOnce();
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
    }

    [Fact]
    public void WithAJackInUseTheReturnPairIsAssertedAndThePathFollowsTheMixer()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true));
        Assert.Equal((true, true), (unit.MonitorReturn, unit.MicDirect));
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true));
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false));
        Assert.Equal((true, false), (unit.MonitorReturn, unit.MicDirect));
        Assert.Equal((1, 2), (unit.ReturnWrites, unit.DirectWrites));
    }

    [Fact]
    public void ThePathOpensAtTheSendsLevelAndALevelChangeAloneNeedsNoRestart()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);

        // The level goes in before the path opens, so the jacks never play
        // the microphone at the level another program left.
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true, micLevelDb: 0));
        Assert.Equal(["level", "direct"], unit.Order);
        Assert.Equal(0, manager.Snapshot().State!.HpMixMicLevelDb);

        // A send or volume change moves the level only: no membership write,
        // so the caller does not restart the playback stream.
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true, micLevelDb: -18.06));
        Assert.Equal(-18, unit.MicLevelDb);
        Assert.Equal((2, 1), (unit.LevelWrites, unit.DirectWrites));

        // Within the cell's half-dB step, nothing is written; a send pulled
        // all the way down takes the floor.
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true, micLevelDb: -18.1));
        Assert.Equal(2, unit.LevelWrites);
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true, micLevelDb: double.NegativeInfinity));
        Assert.Equal((3, -60.0), (unit.LevelWrites, unit.MicLevelDb));

        // With the path off the level is left as it is.
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false, micLevelDb: 0));
        Assert.Equal((3, -60.0), (unit.LevelWrites, unit.MicLevelDb));
    }

    [Fact]
    public void Xlr2sPathOpensAtItsOwnLevelBesideXlr1s()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);

        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false, mic2Direct: true, mic2LevelDb: -6));
        Assert.Equal(["level2", "direct2"], unit.Order);
        Assert.Equal((true, -6.0, false, -14.0), (unit.Mic2Direct, unit.Mic2LevelDb, unit.MicDirect, unit.MicLevelDb));
        Assert.Equal((true, -6.0), (manager.Snapshot().State!.HpMixMic2Direct, manager.Snapshot().State!.HpMixMic2LevelDb));

        // A level change alone needs no restart; closing the path leaves the level.
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false, mic2Direct: true, mic2LevelDb: -12));
        Assert.Equal(-12, unit.Mic2LevelDb);
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false, mic2Direct: false, mic2LevelDb: 0));
        Assert.Equal((false, -12.0), (unit.Mic2Direct, unit.Mic2LevelDb));
    }
}
