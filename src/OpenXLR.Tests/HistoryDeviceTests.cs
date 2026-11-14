using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Undo and redo of the unit's own controls: what <c>set</c> and
/// <c>setWaveControl</c> record, and the safety rules. No step writes phantom
/// power, a locked gain is not moved, a level lock is respected, and a step
/// for a unit that is absent, or another unit in its place, is dropped.
/// </summary>
[Collection("xdg-config")]
public sealed class HistoryDeviceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-history-device-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public HistoryDeviceTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
        new DaemonSettings { Submixer = false }.Save();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void TheListHoldsTheUnitsControlsAndNeverPhantomTheMuteOrALock()
    {
        string[] expected =
        [
            "auxLevelDb", "clipGuard", "clipGuard2", "compressor", "compressor2", "crossfade", "expander", "expander2",
            "gain", "gain2", "hp2VolumeDb", "hpVolumeDb", "lineInGain", "lowCut", "lowCut2", "lowImpedance",
            "voiceTune", "voiceTune2", "voiceTuneStrength", "voiceTuneStrength2",
        ];
        Assert.Equal(expected, DeviceHistory.Controls.Keys.Order(StringComparer.Ordinal));
        foreach (string control in new[]
                 {
                     "phantom", "phantom2", "mute", "mute2", "gainLock", "auxLevelLock", "lineInLevelLock",
                     "outHp1", "outHp2", "outUsbAux", "outLineOut", "rawCapture", "rawCapture2", "lineInRawCapture",
                 })
            Assert.False(DeviceHistory.Allowed(control), control);
        Assert.All(DeviceHistory.Kinds, kind => Assert.Contains(DeviceHistory.Controls.Values, c => c.Kind == kind));
        Assert.All(DeviceHistory.Kinds, kind => Assert.Contains(kind, HistoryPolicy.Kinds));
        Assert.Equal(HistoryClass.Recorded, HistoryPolicy.ClassOf("set"));
        Assert.Equal(HistoryClass.Recorded, HistoryPolicy.ClassOf("setWaveControl"));
    }

    // Phantom power, the mute, the locks, the stamped and read-only fields:
    // flipping every one of them changes no value a step can carry.
    [Fact]
    public void NoControlOnTheListReadsAFieldOutsideIt()
    {
        string[] carried =
        [
            "GainDb", "Gain2Db", "LowCut", "LowCut2", "ClipGuard", "ClipGuard2", "Expander", "Expander2",
            "Compressor", "Compressor2", "VoiceTune", "VoiceTune2", "VoiceTuneStrength", "VoiceTuneStrength2",
            "HpVolumeDb", "Hp2VolumeDb", "Crossfade", "LowImpedance", "AuxLevelDb", "LineInGainDb",
        ];
        var plain = new DeviceState { LineInGainDb = 10 };
        object flipped = plain with { };
        foreach (PropertyInfo property in typeof(DeviceState).GetProperties().Where(p => p.CanWrite && !carried.Contains(p.Name)))
        {
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? other = type == typeof(bool) ? true : type == typeof(int) ? 7 : type == typeof(double) ? -7.0
                : type == typeof(string) ? "ff" : type == typeof(DeviceLed) ? new DeviceLed() : null;
            Assert.True(other is not null, $"{property.Name} has a type this test does not flip");
            property.SetValue(flipped, other);
        }
        var changed = (DeviceState)flipped;
        Assert.True(changed.Phantom && changed.Phantom2 && changed.Mute && changed.AuxLevelLock && changed.GainLocked);
        foreach ((string control, DeviceControl read) in DeviceHistory.Controls)
            Assert.Equal(read.Read(plain), read.Read(changed));
    }

    [Fact]
    public async Task TheGainIsUndoneAndRedoneAndADialTurnIsOneStep()
    {
        using var f = new Fixture();
        foreach (int gain in new[] { 32, 36, 40 })
        {
            await f.Ok($$"""{"cmd":"set","control":"gain","value":{{gain}}}""");
            f.Now += 80;
        }
        HistoryState history = f.History();
        Assert.Equal(1, history.UndoSteps);
        HistoryStepInfo step = history.Undo!;
        Assert.Equal(("unitGain", "xlr1", "gain", null), (step.Kind, step.Channel, step.Control, step.Unit));
        Assert.EndsWith(" gain", step.Label, StringComparison.Ordinal);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(30, f.Pro.State.GainDb);
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(40, f.Pro.State.GainDb);
    }

    [Fact]
    public async Task HeadphonesAndTheProsOtherControlsAreUndone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"hpVolumeDb","value":-12.5}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"hp2VolumeDb","value":-30}""");
        Assert.Equal(("unitHeadphones", "hp2VolumeDb", "Headphone 2 volume"),
            (f.History().Undo!.Kind, f.History().Undo!.Control, f.History().Undo!.Label));
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"lowCut2","value":true}""");
        Assert.Equal(("unitLowCut", "xlr2"), (f.History().Undo!.Kind, f.History().Undo!.Channel));
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"lineInGain","value":20}""");
        Assert.Equal("Line In gain", f.History().Undo!.Label);

        for (int i = 0; i < 4; i++) await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal((-20.0, -20.0, false, 0), (f.Pro.State.HpVolumeDb, f.Pro.State.Hp2VolumeDb, f.Pro.State.LowCut2, f.Pro.State.LineInGainDb));
        Assert.Equal("undo: nothing to undo", await f.Refused("""{"cmd":"undo"}"""));
    }

    [Fact]
    public async Task PhantomPowerAndTheMuteAreNeverRecordedAndNoStepWritesPhantom()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"phantom","value":true}""");
        await f.Ok("""{"cmd":"set","control":"phantom2","value":true}""");
        await f.Ok("""{"cmd":"set","control":"mute","value":true}""");
        await f.Ok("""{"cmd":"set","control":"gainLock","value":true}""");
        await f.Ok("""{"cmd":"set","control":"gainLock","value":false}""");
        Assert.Equal((0, 0), (f.History().UndoSteps, f.History().RedoSteps));

        // A gain step around phantom switches: undo and redo write the gain alone.
        await f.Ok("""{"cmd":"set","control":"gain","value":50}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"phantom","value":false}""");
        int phantomWrites = f.Pro.PhantomWrites;
        await f.Ok("""{"cmd":"undo"}""");
        await f.Ok("""{"cmd":"redo"}""");
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal((30, false, true), (f.Pro.State.GainDb, f.Pro.State.Phantom, f.Pro.State.Phantom2));
        Assert.Equal(phantomWrites, f.Pro.PhantomWrites);

        // The second hold: the patch itself writes nothing off the list.
        IReadOnlyList<DevicePatchOutcome> outcomes = f.Devices.ApplyPatch(f.Pro.Info.InstanceId,
            [new("phantom", false, true), new("phantom2", true, false), new("mute", true, false), new("gainLock", false, true)]);
        Assert.All(outcomes, outcome => Assert.Equal(DevicePatchOutcome.Failed, outcome));
        Assert.Equal(phantomWrites, f.Pro.PhantomWrites);
        Assert.Equal((false, true, true), (f.Pro.State.Phantom, f.Pro.State.Phantom2, f.Pro.State.Mute));
        Assert.False(f.Devices.Snapshot().State!.GainLocked);
    }

    [Fact]
    public async Task ALockedGainIsNotMovedAndTheLockStays()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"gain","value":50}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"gain2","value":45}""");
        await f.Ok("""{"cmd":"set","control":"gainLock","value":true}""");

        Assert.Equal($"undo: {f.ChannelName(2)} gain was skipped: it is locked", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal((50, 45), (f.Pro.State.GainDb, f.Pro.State.Gain2Db));
        Assert.StartsWith("undo:", await f.Refused("""{"cmd":"undo"}"""), StringComparison.Ordinal);
        Assert.Equal((50, 45), (f.Pro.State.GainDb, f.Pro.State.Gain2Db));
        Assert.True(f.Devices.Snapshot().State!.GainLocked);
        Assert.Equal((0, 0), (f.History().UndoSteps, f.History().RedoSteps));

        // The unit's own gain lock holds it too, with OpenXLR's lock off.
        await f.Ok("""{"cmd":"set","control":"gainLock","value":false}""");
        await f.Ok("""{"cmd":"set","control":"gain","value":20}""");
        f.Pro.State = f.Pro.State with { UnitGainLock = true };
        f.Devices.SweepOnce();
        Assert.EndsWith("it is locked", await f.Refused("""{"cmd":"undo"}"""), StringComparison.Ordinal);
        Assert.Equal(20, f.Pro.State.GainDb);
    }

    [Fact]
    public async Task ALevelLockIsRespected()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"auxLevelDb","value":-10}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"lineInGain","value":24}""");
        await f.Ok("""{"cmd":"set","control":"lineInLevelLock","value":true}""");
        await f.Ok("""{"cmd":"set","control":"auxLevelLock","value":true}""");
        Assert.Equal(2, f.History().UndoSteps);

        Assert.Equal("undo: Line In gain was skipped: it is locked", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal("undo: USB Aux level was skipped: it is locked", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal((-10.0, 24, true, true),
            (f.Pro.State.AuxLevelDb, f.Pro.State.LineInGainDb, f.Pro.State.AuxLevelLock, f.Pro.State.LineInLevelLock));
    }

    [Fact]
    public async Task AValueMovedOnTheUnitSinceIsLeftAlone()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"gain","value":50}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"hpVolumeDb","value":-6}""");
        // The dial on the unit, read by the poll: not a step of its own.
        f.Pro.State = f.Pro.State with { HpVolumeDb = -9 };
        f.Devices.SweepOnce();
        Assert.Equal(2, f.History().UndoSteps);

        Assert.Equal("undo: Headphone volume was skipped: it changed since", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal(-9.0, f.Pro.State.HpVolumeDb);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(30, f.Pro.State.GainDb);
    }

    [Fact]
    public async Task AStepForAnAbsentUnitOrAnotherUnitInItsPlaceIsDropped()
    {
        using var f = new Fixture(second: true);
        await f.Ok("""{"cmd":"set","control":"gain","value":50}""");
        f.Now += 2000;
        await f.Ok("""{"cmd":"set","control":"crossfade","value":150}""");

        // Unplugged: the step is dropped and the reply names the unit.
        f.Pro.Unplug();
        f.Attached.Remove(f.Pro);
        Assert.Equal("undo: Mic/PC blend was skipped: Elgato Wave XLR Pro is not connected", await f.Refused("""{"cmd":"undo"}"""));

        // Another unit of the same model stands in: it gets nothing.
        f.Devices.SweepOnce();
        Assert.Equal(f.Other!.Info.InstanceId, f.Devices.ActiveInfo!.InstanceId);
        Assert.EndsWith("Elgato Wave XLR Pro is not connected", await f.Refused("""{"cmd":"undo"}"""), StringComparison.Ordinal);
        Assert.Equal((30, 100), (f.Other.State.GainDb, f.Other.State.Crossfade));
        Assert.Equal(0, f.Other.Writes);
        Assert.Equal((0, 0), (f.History().UndoSteps, f.History().RedoSteps));
    }

    [Fact]
    public async Task TheSameUnitBackWithTheSameValuesTakesTheStep()
    {
        using var f = new Fixture();
        await f.Ok("""{"cmd":"set","control":"gain","value":50}""");
        f.Pro.Unplug();
        f.Attached.Remove(f.Pro);
        f.Devices.SweepOnce();
        Assert.Null(f.Devices.ActiveInfo);
        f.Attached.Add(f.Pro);
        f.Devices.SweepOnce();
        Assert.Equal(f.Pro.Info.InstanceId, f.Devices.ActiveInfo!.InstanceId);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(30, f.Pro.State.GainDb);
    }

    [Fact]
    public async Task AStepOnAnAdditionalInterfaceGoesToThatUnitAlone()
    {
        using var f = new Fixture(second: true);
        using var interfaces = new WaveInterfaces(f.Devices, NullLogger<DeviceManager>.Instance,
            new ConfigurationBuilder().Build(), () => [.. f.Attached]);
        await interfaces.StartAsync(CancellationToken.None);
        try
        {
            WebSocketHub hub = f.HubWith(interfaces);
            string other = f.Other!.Info.InstanceId;
            Wait(() => interfaces.Snapshot().Count == 2);
            Assert.Null(interfaces.SetEnabled(other, true));
            Wait(() => interfaces.Snapshot().Any(s => s.Id == other && s.Connected && s.State is not null));

            await Ok(hub, $$"""{"cmd":"setWaveControl","device":"{{other}}","control":"gain","value":55}""");
            HistoryStepInfo step = hub.Snapshot().History!.Undo!;
            Assert.Equal(("unitGain", null, "gain", other, "Elgato Wave XLR Pro gain"),
                (step.Kind, step.Channel, step.Control, step.Unit, step.Label));
            await Ok(hub, $$"""{"cmd":"setWaveControl","device":"{{other}}","control":"phantom","value":true}""");
            Assert.Equal(1, hub.Snapshot().History!.UndoSteps);

            await Ok(hub, """{"cmd":"undo"}""");
            Assert.Equal((30, 30), (f.Other.State.GainDb, f.Pro.State.GainDb));
            await Ok(hub, """{"cmd":"redo"}""");
            Assert.Equal((55, 30), (f.Other.State.GainDb, f.Pro.State.GainDb));

            // No longer driven: the step goes, the primary untouched.
            Assert.Null(interfaces.SetEnabled(other, false));
            Wait(() => interfaces.Session(other) is null);
            ApiCommandResult refused = await hub.ExecuteForApiAsync("""{"cmd":"undo"}""");
            Assert.False(refused.Ok);
            Assert.Equal("undo: Elgato Wave XLR Pro gain was skipped: Elgato Wave XLR Pro is not connected",
                Assert.Single(refused.Messages.OfType<ErrorMessage>()).Message);
            Assert.Equal(30, f.Pro.State.GainDb);
        }
        finally { await interfaces.StopAsync(CancellationToken.None); }
    }

    private static async Task Ok(WebSocketHub hub, string json)
    {
        ApiCommandResult result = await hub.ExecuteForApiAsync(json);
        Assert.True(result.Ok, string.Join("; ", result.Messages.OfType<ErrorMessage>().Select(e => e.Message)));
    }

    private static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));

    /// <summary>
    /// A Wave XLR Pro on the primary's scan, with another one beside it when
    /// asked, a mixer that is not built, and the hub over both, its history
    /// on a clock the test moves.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly MixerService _service;
        private readonly List<WebSocketHub> _hubs = [];
        public FakePro Pro { get; } = new("PRO1");
        public FakePro? Other { get; }
        public List<IAudioDevice> Attached { get; }
        public DeviceManager Devices { get; }
        public WebSocketHub Hub { get; }
        public long Now { get; set; } = 100_000;

        public Fixture(bool second = false)
        {
            Other = second ? new FakePro("PRO2", address: 3) : null;
            Attached = Other is null ? [Pro] : [Pro, Other];
            Devices = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [.. Attached]);
            Devices.SweepOnce();
            Assert.Equal(Pro.Info.InstanceId, Devices.ActiveInfo?.InstanceId);
            _service = new MixerService(NullLogger<MixerService>.Instance, new ConfigurationBuilder().Build(), Devices);
            Hub = HubWith(null);
        }

        public WebSocketHub HubWith(WaveInterfaces? interfaces)
        {
            var hub = new WebSocketHub(Devices, _service, NullLogger<WebSocketHub>.Instance, new Lifetime(), interfaces);
            hub.History.Clock = () => Now;
            _hubs.Add(hub);
            return hub;
        }

        public string ChannelName(int input) => _service.LayoutConfig.Channels.FirstOrDefault(c => c.Id == $"xlr{input}")?.Name ?? $"XLR {input}";

        public HistoryState History() => Hub.Snapshot().History!;

        public async Task Ok(string json) => await HistoryDeviceTests.Ok(Hub, json);

        public async Task<string> Refused(string json)
        {
            ApiCommandResult result = await Hub.ExecuteForApiAsync(json);
            Assert.False(result.Ok);
            return Assert.Single(result.Messages.OfType<ErrorMessage>()).Message;
        }

        public void Dispose()
        {
            Devices.SuspendSession();
            _service.Dispose();
            Devices.Dispose();
        }
    }

    /// <summary>A Wave XLR Pro's controls held as a state, with the phantom writes counted.</summary>
    private sealed class FakePro(string serial, byte address = 2) : IAudioDevice
    {
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4) { Location = new(1, address, $"1-{address}", serial) };
        public DeviceCapabilities Capabilities { get; } = new()
        {
            Gain = true, Mute = true, LowCut = true, Expander = true, VoiceTune = true, HpVolume = true, LowImpedance = true,
            Crossfade = true, Phantom = true, ClipGuard = true, Compressor = true, AuxInput = true, LineInput = true,
            LineInGainMaxDb = 60, XlrInputs = 2, HpOutputs = 2, RetainsSettings = true,
        };
        public DeviceState State { get; set; } = new()
        {
            GainDb = 30, Gain2Db = 30, HpVolumeDb = -20, Hp2VolumeDb = -20, Crossfade = 100, Phantom2 = true,
            AuxLevelDb = -20, LineInGainDb = 0, LineInLevelLock = false,
        };
        public int PhantomWrites;
        public int Writes;
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public void Unplug() => Connected = false;
        public DeviceState ReadState() => State;
        private void Write(Func<DeviceState, DeviceState> change) { Writes++; State = change(State); }
        public void SetGainDb(int db) => Write(s => s with { GainDb = db });
        public void SetGain2Db(int db) => Write(s => s with { Gain2Db = db });
        public void SetMute(bool on) => Write(s => s with { Mute = on });
        public void SetLowCut(bool on) => Write(s => s with { LowCut = on });
        public void SetLowCut2(bool on) => Write(s => s with { LowCut2 = on });
        public void SetExpander(bool on) => Write(s => s with { Expander = on });
        public void SetVoiceTune(bool on) => Write(s => s with { VoiceTune = on });
        public void SetVoiceTuneStrength(int value) => Write(s => s with { VoiceTuneStrength = value });
        public void SetHpVolumeDb(double db) => Write(s => s with { HpVolumeDb = db });
        public void SetHp2VolumeDb(double db) => Write(s => s with { Hp2VolumeDb = db });
        public void SetLowImpedance(bool on) => Write(s => s with { LowImpedance = on });
        public void SetCrossfade(int value) => Write(s => s with { Crossfade = value });
        public void SetPhantom(bool on) { PhantomWrites++; Write(s => s with { Phantom = on }); }
        public void SetPhantom2(bool on) { PhantomWrites++; Write(s => s with { Phantom2 = on }); }
        public void SetClipGuard(bool on) => Write(s => s with { ClipGuard = on });
        public void SetCompressor(bool on) => Write(s => s with { Compressor = on });
        public void SetAuxLevelDb(double db) => Write(s => s with { AuxLevelDb = db });
        public void SetAuxLevelLock(bool on) => Write(s => s with { AuxLevelLock = on });
        public int SetLineInGainDb(int db) { Write(s => s with { LineInGainDb = db }); return db; }
        public void SetLineInLevelLock(bool on) => Write(s => s with { LineInLevelLock = on });
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
