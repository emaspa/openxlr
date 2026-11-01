using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The device manager's compressor and expander writer, against the drivers
/// over units modelled as their blocks: each input's post lands in its own
/// struct and is verified by the poll; once the mixer drives them, the unit's
/// own <c>compressor</c> and <c>expander</c> controls leave the published
/// capabilities and <c>set</c> refuses them, until the unit goes away; a
/// change another program makes is written back and logged once; a unit
/// that does not keep a write gets one rewrite, then the input fails; and
/// what each input held is known after a read without a transfer.
/// </summary>
[Collection("xdg-config")]
public sealed class DeviceDynamicsWriterTests
{
    private static T WithConfigDir<T>(Func<T> body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-dynamics-writer-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { return body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DeviceManager Manager(IAudioDevice unit, ManualTime time, DeviceEqWriterTests.Lines? log = null)
    {
        var manager = new DeviceManager(log ?? new DeviceEqWriterTests.Lines(), new ConfigurationBuilder().Build(), () => [unit], time: time);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    private static readonly DeviceDynamicsValues Factory = new(-12, 4, 10, 100, 6, -44, 32, 10, 200, 24);
    private static readonly DeviceDynamics Mine = new(true, true, new(-20, 6, 15, 250, 3, -50, 40, 5, 300, 30));

    [Fact]
    public void TheProsInputsAreWrittenInTheirOwnStructsAndTheUnitsSwitchesBecomeTheMicrophones() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var usb = new ProHardwareEqTests.Unit();
        var log = new DeviceEqWriterTests.Lines();
        using DeviceManager manager = Manager(new WaveXlrProDevice(usb), time, log);
        Assert.Equal(new DeviceEqSupport(true) { MinimumFirmware = "1.0.14" }, manager.PublishedEqSupport!.Dynamics);
        Assert.True(manager.Snapshot().Capabilities!.HardwareDynamics);
        // Before the mixer drives them the unit's own switches are offered as ever.
        Assert.True(manager.Snapshot().Capabilities!.Compressor);
        Assert.True(manager.Snapshot().Capabilities!.Expander);
        Assert.Equal(new DeviceDynamics(true, false, Factory), manager.HeldDynamics(1));
        Assert.Equal(new DeviceDynamics(false, false, Factory), manager.HeldDynamics(2));

        manager.PostDynamics(1, DeviceDynamics.Off, "XLR 1");
        manager.PostDynamics(2, Mine, "XLR 2");
        manager.SweepOnce();
        byte[] block = usb.Blocks[0x0004];
        Assert.Equal(DeviceDynamics.Off with { Values = Factory }, WaveFxDynamics.Decode(block.AsSpan(0, 38)));
        Assert.Equal(Mine, WaveFxDynamics.Decode(block.AsSpan(38, 38)));
        Assert.Equal(0x58, block[1]);   // the low cut, voice tune and the unnamed bit kept
        Assert.Equal(new DeviceDynamicsStatus(DeviceEqPhase.Verified, Mine), manager.DynamicsStatus(2));
        Assert.Equal(new DeviceDynamicsStatus(DeviceEqPhase.Verified, DeviceDynamics.Off), manager.DynamicsStatus(1));
        Assert.Empty(usb.Commits);
        Assert.Single(log.Written, l => l == "XLR 1's compressor and expander are not the Wave XLR Pro's to run, so its own are switched off.");
        Assert.Single(log.Written, l => l == "XLR 2's compressor and expander were written to the Wave XLR Pro, replacing what it held.");
        Assert.Equal(Mine, manager.HeldDynamics(2));

        // The switches are the microphone's now: not offered, and refused.
        Assert.False(manager.Snapshot().Capabilities!.Compressor);
        Assert.False(manager.Snapshot().Capabilities!.Expander);
        using JsonDocument on = JsonDocument.Parse("true");
        foreach (string control in new[] { "compressor", "expander", "compressor2", "expander2" })
            Assert.Equal($"control '{control}' is not supported by the active device", manager.Apply(control, on.RootElement));
        Assert.Equal(Mine, WaveFxDynamics.Decode(usb.Blocks[0x0004].AsSpan(38, 38)));
        // Every other control still works and keeps the fields.
        Assert.Null(manager.Apply("lowCut2", on.RootElement));
        Assert.Equal(Mine, WaveFxDynamics.Decode(usb.Blocks[0x0004].AsSpan(38, 38)));
        return 0;
    });

    [Fact]
    public void AChangeAnotherProgramMakesIsWrittenBackAndAReconnectOffersTheSwitchesAgain() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var usb = new Mk2HardwareEqTests.Unit
        {
            Bank = 0x0103,
            Blocks = { [0x0000] = Mk2HardwareEqTests.Unit.Identity(1, 0, 1) },
        };
        var log = new DeviceEqWriterTests.Lines();
        var device = new Wave3Mk2Device(usb);
        using DeviceManager manager = Manager(device, time, log);
        var mine = new DeviceDynamics(true, false, Factory with { CompressorThreshold = -30 });
        manager.PostDynamics(1, mine, "XLR 1");
        manager.SweepOnce();
        Assert.Equal(DeviceEqPhase.Verified, manager.DynamicsStatus(1).Phase);
        // Another program switches the compressor off and moves its threshold.
        usb.Blocks[0x0004][WaveFxDynamics.FlagsOffset] &= unchecked((byte)~WaveFxDynamics.CompressorMask);
        usb.Blocks[0x0004][WaveFxDynamics.CompressorThresholdOffset] = unchecked((byte)(sbyte)-10);
        for (int i = 0; i < 4; i++)
        {
            time.Advance(DeviceManager.EqWriteSpacing);
            manager.SweepOnce();
        }
        Assert.Equal(mine, WaveFxDynamics.Decode(usb.Blocks[0x0004]));
        Assert.Single(log.Written, l => l.StartsWith("Another program changed the compressor or expander XLR 1 runs on the Wave:3 MK.2", StringComparison.Ordinal));
        Assert.False(manager.Snapshot().Capabilities!.Compressor);

        // Unplugged and back: nothing posted yet, so the unit's own switches are offered again.
        device.Disconnect();
        manager.SweepOnce();
        manager.SweepOnce();
        Assert.True(device.Connected);
        Assert.Equal(DeviceDynamicsStatus.None, manager.DynamicsStatus(1));
        Assert.True(manager.Snapshot().Capabilities!.Compressor);
        return 0;
    });

    /// <summary>A unit whose writes are answered but not kept.</summary>
    private sealed class Forgetful(DeviceDynamics held) : IAudioDevice
    {
        public int Writes;
        public void Dispose() { }
        public DeviceInfo Info { get; } = new("Elgato", "Wave:3 MK.2", 0x0fd9, 0x00ca);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, Compressor = true, Expander = true, HardwareEq = true, HardwareDynamics = true };
        public DeviceEqSupport? EqSupport { get; } = new(true);
        public DeviceEqSupport? DynamicsSupport { get; } = new(true);
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public DeviceState ReadState() => new();
        public DeviceDynamics? ReadDynamics(int input) => input == 1 ? held : null;
        public bool SetDynamics(int input, DeviceDynamics dynamics) { Writes++; return true; }
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void AWriteTheUnitDoesNotKeepIsTriedOnceMoreThenTheInputFails() => WithConfigDir(() =>
    {
        var time = new ManualTime();
        var unit = new Forgetful(new DeviceDynamics(false, false, Factory));
        using DeviceManager manager = Manager(unit, time);
        manager.PostDynamics(1, Mine, "XLR 1");
        for (int i = 0; i < 6; i++)
        {
            manager.SweepOnce();
            time.Advance(DeviceManager.EqWriteSpacing);
        }
        Assert.Equal(2, unit.Writes);
        DeviceDynamicsStatus status = manager.DynamicsStatus(1);
        Assert.Equal(DeviceEqPhase.Failed, status.Phase);
        Assert.Equal(DeviceManager.DynamicsNotKept, status.Reason);
        return 0;
    });
}
