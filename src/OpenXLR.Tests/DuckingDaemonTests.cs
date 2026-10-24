using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.ProDuckingTests;

namespace OpenXLR.Tests;

/// <summary>
/// Ducking through the daemon without an audio server: the state carries
/// <c>ducking</c> whatever is attached, with <c>device</c> null on a model
/// without hardware ducking and the mixer-off verdict for a Pro in
/// hardware-control mode; the serialized shape of a Pro's verdict with its
/// outputs; the hub handing both commands to the mixer, never to the unit;
/// and the real device port carrying the mixer's targets to a modelled Pro
/// and back as held and read.
/// </summary>
[Collection("xdg-config")]
public sealed class DuckingDaemonTests
{
    private const string Pro = "0fd9:00b4";

    private static void WithConfigDir(Action<string> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-ducking-daemon-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>The state as a client reads it, through the hub's own serializer options.</summary>
    internal static JsonNode Serialized(object message)
    {
        var options = (JsonSerializerOptions)typeof(WebSocketHub).GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return JsonNode.Parse(JsonSerializer.Serialize(message, message.GetType(), options))!;
    }

    private static (DeviceManager Devices, MixerService Mixer, WebSocketHub Hub) Daemon(params IAudioDevice[] units)
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => units);
        devices.SweepOnce();
        var mixer = new MixerService(NullLogger<MixerService>.Instance, config, devices);
        return (devices, mixer, new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime()));
    }

    [Fact]
    public void TheStateCarriesDuckingWithNoDeviceAttached() => WithConfigDir(_ =>
    {
        (DeviceManager devices, MixerService mixer, WebSocketHub hub) = Daemon();
        using (devices)
        using (mixer)
        {
            StateMessage state = hub.Snapshot();
            Assert.False(state.Connected);
            DuckingState ducking = state.Ducking!;
            Assert.Equal((false, "xlr1", "medium", "device"), (ducking.Enabled, ducking.Key, ducking.Speed, ducking.Processing));
            Assert.Equal(["game", "music", "browser", "system", "voicechat", "sfx"], ducking.Channels);
            Assert.Equal(["monitor"], ducking.Mixes);
            Assert.Null(ducking.Backend);
            Assert.Null(ducking.Device);

            JsonNode json = Serialized(state)["ducking"]!;
            Assert.Equal(["enabled", "key", "channels", "mixes", "sensitivity", "amount", "speed", "processing", "backend", "available", "error", "device"],
                json.AsObject().Select(p => p.Key));
            Assert.Null(json["backend"]);
            Assert.Null(json["device"]);
            Assert.Equal(ducking.Available, json["available"]!.GetValue<bool>());
        }
    });

    [Fact]
    public void WithoutAGraphTheStateShowsTheSavedSetup() => WithConfigDir(_ =>
    {
        var saved = new MixerSettings
        {
            Ducking = DuckingSetup.Default(MixerConfig.Default()) with
            {
                Enabled = true, Key = "xlr2", Channels = ["music", "nothing"], Mixes = ["stream"], Amount = "high", Processing = "software",
            },
        };
        Assert.Null(saved.Save());
        (DeviceManager devices, MixerService mixer, WebSocketHub hub) = Daemon();
        using (devices)
        using (mixer)
        {
            DuckingState ducking = hub.Snapshot().Ducking!;
            Assert.Equal((true, "xlr2", "high", "software"), (ducking.Enabled, ducking.Key, ducking.Amount, ducking.Processing));
            Assert.Equal(["music"], ducking.Channels);   // an id the layout lacks is pruned
            Assert.Equal(["stream"], ducking.Mixes);
        }
    });

    [Fact]
    public void AModelWithoutHardwareDuckingHasNoDeviceBlock() => WithConfigDir(_ =>
    {
        (DeviceManager devices, MixerService mixer, WebSocketHub hub) = Daemon(new XlrDockMk2Device(new Mk2HardwareEqTests.Unit()));
        using (devices)
        using (mixer)
        {
            StateMessage state = hub.Snapshot();
            Assert.True(state.Connected);
            Assert.NotNull(state.Ducking);
            Assert.Null(state.Ducking.Device);
            Assert.Null(Serialized(state)["ducking"]!["device"]);
        }
    });

    [Fact]
    public void AProInHardwareControlModeIsToldTheMixerIsOffAndIsNotWritten() => WithConfigDir(_ =>
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(
            "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" +
            "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200");
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        (DeviceManager devices, MixerService mixer, WebSocketHub hub) = Daemon(new WaveXlrProDevice(unit));
        using (devices)
        using (mixer)
        {
            devices.SweepOnce();
            DuckingDevice device = hub.Snapshot().Ducking!.Device!;
            Assert.Equal((Pro, "Wave XLR Pro", false, "mixerOff"), (device.Unit, device.Name, device.Ready, device.Reason));
            Assert.Equal("The software mixer is off, so ducking does not run.", device.Note);
            Assert.Empty(device.Outputs);
            Assert.False(device.Held);
            Assert.Equal([1, 5, 6, 7], device.Readback!.Pairs);

            // Both commands go to the mixer, which is not built, and nothing reaches the unit.
            foreach (string command in new[]
            {
                """{"cmd":"setDucking","value":{"enabled":true},"requestId":"a"}""",
                """{"cmd":"setDuckingProcessing","value":"software","requestId":"b"}""",
            })
            {
                ApiCommandResult result = hub.ExecuteForApiAsync(command).GetAwaiter().GetResult();
                Assert.False(result.Ok);
                Assert.Equal("mixer not built (start the daemon with --mixer)",
                    result.Messages.OfType<CommandResultMessage>().Single().Error);
            }
            devices.SweepOnce();
            Assert.DoesNotContain(unit.Calls, c => c.Type == 0x41 && c.Block == 0x0008);
            Assert.False(File.Exists(Path.Combine(OpenXlrPaths.ConfigDir, "mixer.json")));
        }
    });

    /// <summary>The Pro's verdict with its outputs, as a Core mixer with the jacks on Monitor A computes it, in the state's shape.</summary>
    [Fact]
    public void TheSerializedStateOfAProShowsItsVerdictAndOutputs()
    {
        var port = new RecordingPort();
        using Mixer mixer = ProMixer(port);
        mixer.SetDuckingSetup(DuckingSetup.Default(mixer.Config) with { Enabled = true }, _ => null);
        port.Settle();
        mixer.SyncDucking();

        JsonNode json = Serialized(new StateMessage { Ducking = DuckingState.From(mixer.DuckingSnapshot()) })["ducking"]!;
        Assert.Equal("device", json["backend"]!.GetValue<string>());
        Assert.False(json.AsObject().ContainsKey("targets"));
        JsonNode device = json["device"]!;
        Assert.Equal(["unit", "name", "firmware", "ready", "reason", "note", "outputs", "held", "readback"], device.AsObject().Select(p => p.Key));
        Assert.Equal(["monitor"], device["outputs"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.True(device["ready"]!.GetValue<bool>());
        Assert.True(device["held"]!.GetValue<bool>());
        Assert.Null(device["reason"]);
        Assert.Equal(DuckingPlacementTests.Pro.Firmware, device["firmware"]!.GetValue<string>());

        // Keyed by XLR 2: software, the unit's reason and the outputs it would have ducked.
        mixer.SetDuckingSetup(mixer.DuckingSnapshot().Setup with { Key = "xlr2", Channels = ["music"] }, _ => null);
        device = Serialized(new StateMessage { Ducking = DuckingState.From(mixer.DuckingSnapshot()) })["ducking"]!["device"]!;
        Assert.Equal("keyNotSupported", device["reason"]!.GetValue<string>());
        Assert.Equal("The Wave XLR Pro ducks only with XLR 1 as the key.", device["note"]!.GetValue<string>());
        Assert.Equal(["monitor"], device["outputs"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.False(port.Posts[^1].Enabled);
    }

    [Fact]
    public void TheRealPortCarriesTheMixersTargetsToTheUnit() => WithConfigDir(_ =>
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        unit.Blocks[0x0001] = Block1(0x5d, 0xfd);
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(
            "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" +
            "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200");
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(LowLowSlowBytes, WaveLinkFlags);
        var log = new DeviceEqWriterTests.Lines();
        using var devices = new DeviceManager(log, new ConfigurationBuilder().Build(), () => [new WaveXlrProDevice(unit)], time: new ManualTime());
        devices.SweepOnce();
        var port = new DeviceDuckingPort(devices);
        int settled = 0;
        port.StatusChanged += () => Interlocked.Increment(ref settled);
        using Mixer mixer = ProMixer(port, register: false);
        (string id, UnitDuckingSupport support) = port.Primary()!.Value;
        Assert.Equal(Pro, id);
        // The jacks' feed reads as a member of the mix the jacks play (byte
        // 12 bit 5), so the unit reaches them; the Aux port is unchecked. The
        // write path below is exercised as if the unit reached both.
        Assert.Equal((true, false), (support.ReachesJacks, support.ReachesAux));
        mixer.SetUnitDuckingSupport(id, support with { ReachesAux = true });

        // On: the jacks' pair, written by the device loop and read back held.
        mixer.SetDuckingSetup(DuckingSetup.Default(mixer.Config) with { Enabled = true, Amount = "high", Speed = "fast" }, _ => null);
        Assert.Equal(DeviceDuckingPhase.Pending, port.Status(Pro).Phase);
        devices.SweepOnce();
        Assert.True(settled > 0);
        mixer.SyncDucking();
        DuckingStateSnapshot on = mixer.DuckingSnapshot();
        Assert.Equal("device", on.Backend);
        Assert.True(on.Device!.Held);
        Assert.Equal([1], on.Device.Readback!.Pairs);
        Assert.Equal(("medium", "high", "fast"), (on.Device.Readback.Sensitivity, on.Device.Readback.Amount, on.Device.Readback.Speed));
        Assert.Contains(log.Written, line => line.StartsWith(
            "ducking (update) on Elgato Wave XLR Pro: on, pairs 1, sensitivity medium, amount high, speed fast, keyed by XLR 1.", StringComparison.Ordinal));

        // Software chosen: the unit is written off and reads back without pairs.
        mixer.SetDuckingSetup(on.Setup with { Processing = "software" }, _ => null);
        devices.SweepOnce();
        mixer.SyncDucking();
        DuckingStateSnapshot off = mixer.DuckingSnapshot();
        Assert.NotEqual("device", off.Backend);
        Assert.Empty(off.Device!.Readback!.Pairs);
        Assert.True(off.Device.Held);
        Assert.Contains(log.Written, line => line.StartsWith("ducking (update) on Elgato Wave XLR Pro: off.", StringComparison.Ordinal));
    });

    [Fact]
    public void TheJacksAreReachedOnlyWhileTheirFeedIsInTheUnitsMix() => WithConfigDir(_ =>
    {
        var unit = new Mk2HardwareEqTests.Unit { Bank = 0x0103 };
        unit.Blocks.Clear();
        byte[] block1 = Block1();
        block1[12] &= unchecked((byte)~0x20);
        unit.Blocks[0x0001] = block1;
        unit.Blocks[0x0003] = new byte[12];
        unit.Blocks[0x0004] = Convert.FromHexString(
            "34d800d4200a00c8001832f4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" +
            "1e0004d4200a00c800180af4040a00640006" + "01003c00f6030de300ff05f45c03f20507fd27e8" + "14000200");
        unit.Blocks[0x0005] = Convert.FromHexString("0000000050000000");
        unit.Blocks[0x0008] = Block8(MediumBytes, IdleFlags);
        using var devices = new DeviceManager(new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(),
            () => [new WaveXlrProDevice(unit)], time: new ManualTime());
        devices.SweepOnce();
        var port = new DeviceDuckingPort(devices);
        Assert.False(port.Primary()!.Value.Support.ReachesJacks);

        // Another program, or the daemon's own headphone mix sync, puts the
        // feed back: the next poll reads it and the unit reaches the jacks.
        unit.Blocks[0x0001][12] |= 0x20;
        devices.SweepOnce();
        Assert.True(port.Primary()!.Value.Support.ReachesJacks);
    });

    /// <summary>A Core mixer without a graph that sees the Pro's jacks playing Monitor A, as the hand-over tests build it.</summary>
    private static Mixer ProMixer(IDeviceDuckingPort port, bool register = true)
    {
        var mixer = new Mixer(new PipeWireAdapter { DuckHost = "/missing-openxlr-duck-host" }) { Time = new ManualTime() };
        mixer.SetInputJacks(2, true);
        mixer.SetDrivenUnit(Pro);
        mixer.SetInputDeviceHint("Wave_XLR_Pro", hardwareOutputRouting: true);
        ((List<string>)typeof(Mixer).GetField("_monitorOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!)
            .Add("alsa_output.usb-Wave_XLR_Pro-00.pro-output-0#hp1");
        mixer.SetDeviceDuckingPort(port);
        if (register) mixer.SetUnitDuckingSupport(Pro, DuckingPlacementTests.Pro);
        return mixer;
    }

    /// <summary>A device side that takes every post and holds it once settled.</summary>
    internal sealed class RecordingPort : IDeviceDuckingPort
    {
        public readonly List<ProDuckingTarget> Posts = [];
        private DeviceDuckingStatus _status = DeviceDuckingStatus.None;

        public void Post(string unit, ProDuckingTarget target, string owner)
        {
            lock (Posts)
            {
                Posts.Add(target);
                _status = new(DeviceDuckingPhase.Pending, target);
            }
        }

        public DeviceDuckingStatus Status(string unit) { lock (Posts) return _status; }

        public void Settle()
        {
            lock (Posts)
                if (_status.Value is { } held)
                    _status = new(DeviceDuckingPhase.Verified, held)
                    {
                        Readback = new DuckingReadback(held.Sensitivity, held.Amount, held.Speed, held.Enabled ? [.. held.Pairs.Order()] : [], []),
                    };
        }
    }
}
