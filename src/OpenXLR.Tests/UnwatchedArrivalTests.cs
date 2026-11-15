using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A daemon start finds a unit that may have been used while no daemon ran.
/// One that stayed plugged in since the daemon last opened it, in the same
/// boot at the same USB address, keeps a mute and a lower headphone level it
/// holds; one back from a power cycle, a reboot or a replug gets its saved
/// settings back. The manager keeps its files in the configuration
/// directory, which these tests redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class UnwatchedArrivalTests : IDisposable
{
    private const string Mk1 = "0fd9:007d";
    private const string Wave3 = "0fd9:0070";
    private static readonly UsbLocation Port = new(1, 5, "1-5", "SER1");

    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-unwatched-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly TimeSpan _previousDelay = DeviceManager.PollRetryDelay;
    private readonly ManualTime _time = new();

    public UnwatchedArrivalTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        DeviceManager.PollRetryDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        DeviceManager.PollRetryDelay = _previousDelay;
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// An original Wave XLR, which OpenXLR gives its last settings back on
    /// connect, or a Wave:3, whose card the desktop writes to when it appears.
    /// </summary>
    private sealed class Unit(bool memoryless, UsbLocation? location = null) : IAudioDevice
    {
        public int GainDb = 40;
        public bool Mute;
        public double Hp = -20;
        public long Arrivals;
        public readonly List<string> Writes = [];
        public DeviceInfo Info { get; } = memoryless
            ? new("Elgato", "Wave XLR", 0x0fd9, 0x007d) { Location = location ?? Port }
            : new("Elgato", "Wave:3", 0x0fd9, 0x0070) { Location = location ?? Port };
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, HpVolume = true, RetainsSettings = !memoryless };
        public bool DesktopRestoresControls => !memoryless;
        public bool HasCard => true;
        public long CardArrivals => Arrivals;
        public bool Connected { get; private set; }
        public void Connect() { Connected = true; Arrivals++; }
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState() => new() { GainDb = GainDb, Mute = Mute, HpVolumeDb = Hp };
        public void SetGainDb(int db) { GainDb = db; Writes.Add($"gain {db}"); }
        public void SetMute(bool on) { Mute = on; Writes.Add($"mute {on}"); }
        public void SetHpVolumeDb(double db) { Hp = db; Writes.Add($"hp {db}"); }
        public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private DeviceManager Manager(Func<IReadOnlyList<IAudioDevice>> detect)
        => new(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), detect, time: _time);

    /// <summary>
    /// One daemon run driving <paramref name="unit"/>: connect, the restore
    /// the hub runs on arrival, a poll, and the stop that saves.
    /// </summary>
    private void Run(Unit unit, Action<DeviceManager>? meanwhile = null)
    {
        using DeviceManager manager = Manager(() => [unit]);
        manager.SweepOnce();
        manager.RestoreLastState();
        manager.MarkRestored();
        manager.SweepOnce();
        meanwhile?.Invoke(manager);
        manager.SuspendSession();
    }

    private static readonly DeviceState Saved = new() { GainDb = 55, Mute = false, HpVolumeDb = -20 };

    [Fact]
    public void AMuteSetOnTheUnitWhileTheDaemonWasStoppedStays()
    {
        DeviceStateStore.SaveLast(Mk1, Saved);
        var unit = new Unit(memoryless: true);
        Run(unit);
        Assert.Equal(55, unit.GainDb);

        unit.Mute = true;   // the mute pad, with the daemon stopped
        unit.Writes.Clear();
        Run(unit);

        Assert.True(unit.Mute);
        Assert.DoesNotContain("mute False", unit.Writes);
        Assert.True(DeviceStateStore.LoadLast(Mk1)!.Mute);
    }

    [Fact]
    public void AHeadphoneLevelTurnedDownWhileTheDaemonWasStoppedStays()
    {
        DeviceStateStore.SaveLast(Mk1, Saved);
        var unit = new Unit(memoryless: true);
        Run(unit);

        unit.Hp = -45;      // the dial on headphones, with the daemon stopped
        unit.Writes.Clear();
        Run(unit);

        Assert.Equal(-45, unit.Hp);
        Assert.Empty(unit.Writes);
        Assert.Equal(-45, DeviceStateStore.LoadLast(Mk1)!.HpVolumeDb);
    }

    [Fact]
    public void AMuteAndALowerLevelSavedAreStillWritten()
    {
        DeviceStateStore.SaveLast(Mk1, Saved);
        var unit = new Unit(memoryless: true);
        Run(unit, manager =>
        {
            Assert.Null(manager.Apply(ControlNames.Mute, System.Text.Json.JsonDocument.Parse("true").RootElement));
            Assert.Null(manager.Apply(ControlNames.HpVolumeDb, System.Text.Json.JsonDocument.Parse("-40").RootElement));
        });

        unit.Mute = false;
        unit.Hp = -10;
        Run(unit);

        Assert.True(unit.Mute);
        Assert.Equal(-40, unit.Hp);
    }

    [Fact]
    public void AUnitAtItsPowerOnValuesGetsItsSettingsBack()
    {
        // It lost power without leaving the bus while the daemon was stopped.
        DeviceStateStore.SaveLast(Mk1, Saved);
        DeviceStateStore.SaveDefaults(Mk1, new DeviceState { GainDb = 30, Mute = true, HpVolumeDb = -60 });
        var unit = new Unit(memoryless: true);
        Run(unit);

        unit.GainDb = 30;
        unit.Mute = true;
        unit.Hp = -60;
        Run(unit);

        Assert.Equal(55, unit.GainDb);
        Assert.False(unit.Mute);
        Assert.Equal(-20, unit.Hp);
    }

    [Theory]
    [InlineData("reboot")]
    [InlineData("replug")]
    [InlineData("first")]
    public void AUnitBackFromAPowerCycleGetsItsSettingsBack(string since)
    {
        DeviceStateStore.SaveLast(Mk1, Saved);
        var unit = new Unit(memoryless: true);
        Run(unit);

        // Muted and quiet, as the unit powers on.
        unit.Mute = true;
        unit.Hp = -60;
        if (since == "reboot")
            DeviceStateStore.SaveSighting(Mk1, DeviceStateStore.LoadSighting(Mk1)! with { Boot = "an-earlier-boot" });
        else if (since == "replug")
            unit = new Unit(memoryless: true, Port with { Address = 9 }) { Mute = true, Hp = -60 };
        else
            File.Delete(Path.Combine(OpenXlrPaths.ConfigDir, "devices", "0fd9-007d", "seen.json"));   // from before this check
        Run(unit);

        Assert.False(unit.Mute);
        Assert.Equal(-20, unit.Hp);
    }

    [Fact]
    public void AUnitRepluggedWhileTheDaemonRunsGetsItsSettingsBack()
    {
        DeviceStateStore.SaveLast(Mk1, Saved);
        var unit = new Unit(memoryless: true);
        Run(unit);

        var attached = new List<IAudioDevice> { unit };
        using DeviceManager manager = Manager(() => [.. attached]);
        manager.SweepOnce();
        manager.RestoreLastState();
        manager.MarkRestored();
        unit.Disconnect();
        attached.Clear();
        manager.SweepOnce();
        unit.Mute = true;
        unit.Hp = -60;
        attached.Add(unit);
        manager.SweepOnce();
        manager.RestoreLastState();

        Assert.False(unit.Mute);
        Assert.Equal(-20, unit.Hp);
        manager.SuspendSession();
    }

    [Fact]
    public void AtADaemonStartAWave3KeepsTheMuteAndLowerLevelSetOnIt()
    {
        DeviceStateStore.SaveLast(Wave3, Saved);
        var unit = new Unit(memoryless: false);
        Run(unit);

        unit.Mute = true;
        unit.Hp = -45;
        unit.Writes.Clear();
        Run(unit);

        Assert.Empty(unit.Writes);
        Assert.True(unit.Mute);
        Assert.Equal(-45, unit.Hp);
    }

    [Fact]
    public void AtADaemonStartTheDesktopsUnmuteAndLouderLevelOnAWave3AreStillUndone()
    {
        // An audio service restart: WirePlumber wrote its saved route before
        // the daemon, restarted with it, opened the unit again.
        DeviceStateStore.SaveLast(Wave3, Saved with { Mute = true, HpVolumeDb = -30 });
        var unit = new Unit(memoryless: false) { Mute = true, Hp = -30 };
        Run(unit);

        unit.Mute = false;
        unit.Hp = 0;
        unit.Writes.Clear();
        Run(unit);

        Assert.Equal(["mute True", "hp -30"], unit.Writes);
    }
}
