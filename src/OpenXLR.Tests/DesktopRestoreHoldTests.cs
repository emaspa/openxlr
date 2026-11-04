using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// When a Wave:3's card appears, WirePlumber writes the mute and volume it
/// saved for the card's routes, saved only when a desktop application made
/// the change, and the unit takes them as its own (issue #680). For a few
/// seconds after each arrival the daemon writes back the values OpenXLR
/// had. The manager keeps its last settings in the configuration directory,
/// which these tests redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class DesktopRestoreHoldTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-desktop-restore-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly TimeSpan _previousDelay = DeviceManager.PollRetryDelay;
    private readonly ManualTime _time = new();

    public DesktopRestoreHoldTests()
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

    /// <summary>A Wave:3 whose card comes and goes as the test says.</summary>
    private sealed class Unit(bool desktopRestores = true) : IAudioDevice
    {
        public bool Mute;
        public double Hp = -23.5;
        public bool Card = true;
        public long Arrivals;
        public volatile bool Failing;
        public readonly List<string> Writes = [];
        public DeviceInfo Info { get; } = new("Elgato", "Wave:3", 0x0fd9, 0x0070);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Mute = true, HpVolume = true, RetainsSettings = true };
        public bool DesktopRestoresControls => desktopRestores;
        public bool HasCard => Card;
        public long CardArrivals => Arrivals;
        public bool Connected { get; private set; }
        public void Connect() { Connected = true; Arrivals = Card ? 1 : 0; }
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState()
        {
            if (Failing) throw new InvalidOperationException("read config block: No such device (it may have been disconnected)");
            return new DeviceState { GainDb = 40, Mute = Mute, HpVolumeDb = Hp };
        }
        public void SetMute(bool on) { Mute = on; Writes.Add($"mute {on}"); }
        public void SetHpVolumeDb(double db) { Hp = db; Writes.Add($"hp {db}"); }
        public void SetGainDb(int db) { }
        public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private DeviceManager Manager(Unit unit, RecordingLogger<DeviceManager>? log = null)
        => new(log ?? new RecordingLogger<DeviceManager>(), new ConfigurationBuilder().Build(), () => [unit], time: _time);

    [Fact]
    public void ADesktopWriteRightAfterTheConnectIsWrittenBack()
    {
        var unit = new Unit();
        var log = new RecordingLogger<DeviceManager>();
        using var manager = Manager(unit, log);
        manager.SweepOnce();
        Assert.Equal(-23.5, manager.Snapshot().State!.HpVolumeDb);

        // WirePlumber's saved route: unmuted, and the volume of an earlier day.
        _time.Advance(TimeSpan.FromSeconds(1));
        unit.Hp = 0;
        manager.SweepOnce();

        Assert.Equal(["hp -23.5"], unit.Writes);
        Assert.Equal(-23.5, manager.Snapshot().State!.HpVolumeDb);
        Assert.Contains(log.Entries, e => e.Message.Contains("the desktop restored older values"));
    }

    [Fact]
    public void AfterTheHoldTheUnitsOwnChangesStay()
    {
        var unit = new Unit();
        using var manager = Manager(unit);
        manager.SweepOnce();
        _time.Advance(DeviceManager.DesktopRestoreHold);

        unit.Mute = true;   // the mute pad
        unit.Hp = -12;      // the dial on headphones
        manager.SweepOnce();

        Assert.Empty(unit.Writes);
        Assert.True(manager.Snapshot().State!.Mute);
        Assert.Equal(-12, manager.Snapshot().State!.HpVolumeDb);
    }

    [Fact]
    public void ACardThatComesBackGetsTheValuesFromBeforeItWentAway()
    {
        var unit = new Unit { Mute = true, Hp = -30 };
        using var manager = Manager(unit);
        manager.SweepOnce();
        _time.Advance(TimeSpan.FromMinutes(1));

        // The kernel replaces the card while USB stays up, as a USB reset can.
        unit.Card = false;
        manager.SweepOnce();
        _time.Advance(TimeSpan.FromSeconds(2));
        unit.Card = true;
        unit.Arrivals = 2;
        unit.Mute = false;
        unit.Hp = 0;
        manager.SweepOnce();

        Assert.Equal(["mute True", "hp -30"], unit.Writes);
        Assert.True(manager.Snapshot().State!.Mute);
        Assert.Equal(-30, manager.Snapshot().State!.HpVolumeDb);
    }

    [Fact]
    public void AChangeMadeThroughOpenXlrDuringTheHoldStays()
    {
        var unit = new Unit();
        using var manager = Manager(unit);
        manager.SweepOnce();

        Assert.Null(manager.Apply(ControlNames.HpVolumeDb, JsonDocument.Parse("-10").RootElement));
        manager.SweepOnce();
        Assert.Equal(["hp -10"], unit.Writes);

        unit.Hp = 0;
        manager.SweepOnce();
        Assert.Equal(["hp -10", "hp -10"], unit.Writes);
        Assert.Equal(-10, manager.Snapshot().State!.HpVolumeDb);
    }

    [Fact]
    public void AtTheFirstConnectTheLastSavedValuesAreWrittenBack()
    {
        // An audio service restart: WirePlumber wrote its saved route before
        // the daemon, restarted with it, opened the unit again.
        DeviceStateStore.SaveLast("0fd9:0070", new DeviceState { GainDb = 40, Mute = true, HpVolumeDb = -30 });
        var unit = new Unit { Mute = false, Hp = 0 };
        using var manager = Manager(unit);
        manager.SweepOnce();

        Assert.Equal(["mute True", "hp -30"], unit.Writes);
        Assert.True(manager.Snapshot().State!.Mute);
        Assert.Equal(-30, manager.Snapshot().State!.HpVolumeDb);
    }

    [Fact]
    public void TheLastValuesAreSavedWhenTheUnitGoesAway()
    {
        var unit = new Unit();
        using var manager = Manager(unit);
        manager.SweepOnce();
        _time.Advance(DeviceManager.DesktopRestoreHold);
        unit.Mute = true;
        manager.SweepOnce();

        unit.Failing = true;   // the cable is pulled
        manager.SweepOnce();

        DeviceState? saved = DeviceStateStore.LoadLast("0fd9:0070");
        Assert.NotNull(saved);
        Assert.True(saved.Mute);
        Assert.Equal(-23.5, saved.HpVolumeDb);
    }

    [Fact]
    public void AUnitTheDesktopDoesNotWriteToIsLeftAlone()
    {
        DeviceStateStore.SaveLast("0fd9:0070", new DeviceState { GainDb = 40, Mute = true, HpVolumeDb = -30 });
        var unit = new Unit(desktopRestores: false);
        using var manager = Manager(unit);
        manager.SweepOnce();
        unit.Hp = 0;
        manager.SweepOnce();

        Assert.Empty(unit.Writes);
        Assert.Equal(0, manager.Snapshot().State!.HpVolumeDb);
    }
}
