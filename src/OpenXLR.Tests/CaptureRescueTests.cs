using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The way back from a Wave:3 MK.2 capture stalled under its own playback:
/// the mixes come off its output, the unit is reset on USB, its capture comes
/// back and only then its playback. Asked for by a client, the capture links
/// are made again first and the reset follows only for a capture the kernel
/// still holds stopped. Models without the capability are never touched.
/// </summary>
[Collection("xdg-config")]
public sealed class CaptureRescueTests
{
    private static readonly DeviceInfo Mk2 = new("Elgato", "Wave:3 MK.2", 0x0fd9, 0x00ca);
    private static readonly DeviceInfo Pro = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
    private static readonly DeviceCapabilities Recovers = new() { Gain = true, CaptureRecovery = true };
    private static readonly DeviceCapabilities Plain = new() { Gain = true };
    private const string Source = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_X-00.analog-stereo";
    private const string Sink = "alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_X-00.analog-stereo";

    /// <summary>A mixer that records what the rescue asks of it, in order.</summary>
    private sealed class Mixer(List<string> calls) : ICaptureRecoveryMixer
    {
        public CaptureRun Run = new(10, true, true, false);
        public UnitPlayback? Playing = new(Source, Sink);
        public UnitCaptureProgress? Progress;
        public bool CaptureRecoveryActive { get; private set; }
        public UnitPlayback? DrivenUnitPlayback() => CaptureRecoveryActive ? null : Playing;
        public CaptureRun ProbeCapture(string source) { Assert.Equal(Source, source); return Run; }
        public UnitCaptureProgress? DrivenCaptureProgress() => Progress;
        public bool? ReopenDrivenCapture(UnitCaptureProgress expected)
        {
            Assert.Equal(Progress, expected);
            calls.Add("reopen " + expected.Source);
            Progress = null;
            return true;
        }
        /// <summary>What the capture reads once its links are made again; null keeps it as it was.</summary>
        public CaptureRun? AfterRelink;
        public UnitRelink? RelinkDrivenUnitCapture()
        {
            calls.Add("relink");
            if (AfterRelink is { } after) Run = after;
            return new UnitRelink(Source, 2);
        }
        public int? BeginCaptureRecovery() { calls.Add("playback off"); CaptureRecoveryActive = true; return 1; }
        public void EndCaptureRecovery() { calls.Add("playback held no more"); CaptureRecoveryActive = false; }
        /// <summary>What the mixer does when the reset card's capture runs: the routes go back.</summary>
        public void CaptureBack() { calls.Add("playback on"); CaptureRecoveryActive = false; }
    }

    private static (CaptureRescue Rescue, Mixer Mixer, List<string> Calls, Func<long, long> Advance) Build(
        DeviceCapabilities capabilities, Func<string, string?>? reset = null)
    {
        var calls = new List<string>();
        var mixer = new Mixer(calls);
        long now = 0;
        var watch = new CaptureWatch(() => now);
        var rescue = new CaptureRescue(NullLogger.Instance, mixer, () => Mk2, () => capabilities,
            reset ?? (unit => { calls.Add("reset " + unit); return null; }), watch,
            wait => { Assert.Equal(CaptureRescue.RelinkSettle, wait); calls.Add("settle"); });
        return (rescue, mixer, calls, ms => now += ms);
    }

    [Fact]
    public void AStalledCaptureIsRecoveredOnceInTheRightOrder()
    {
        (CaptureRescue rescue, Mixer mixer, List<string> calls, Func<long, long> advance) = Build(Recovers);
        for (int i = 0; i < 4; i++, advance(1000)) rescue.Sweep();
        Assert.Equal(["playback off", "reset 0fd9:00ca"], calls);

        // The mixer holds the playback until the card is back; nothing more
        // is asked meanwhile, however long the old capture reads stopped.
        for (int i = 0; i < 20; i++, advance(1000)) rescue.Sweep();
        Assert.Equal(2, calls.Count);
        mixer.CaptureBack();
        // The stall comes straight back: the next reset waits for the spacing.
        for (int i = 0; i < 60; i++, advance(1000)) rescue.Sweep();
        Assert.Equal(["playback off", "reset 0fd9:00ca", "playback on"], calls);
        for (int i = 0; i < 70; i++, advance(1000)) rescue.Sweep();
        Assert.Equal(["playback off", "reset 0fd9:00ca", "playback on", "playback off", "reset 0fd9:00ca"], calls);
    }

    [Fact]
    public void AModelWithoutTheCapabilityIsNeverTouched()
    {
        (CaptureRescue rescue, _, List<string> calls, Func<long, long> advance) = Build(Plain);
        for (int i = 0; i < 600; i++, advance(1000)) rescue.Sweep();
        Assert.NotNull(rescue.Recover());
        Assert.Empty(calls);
    }

    [Fact]
    public void ARunningDockWithAFrozenCursorReopensOnlyItsSource()
    {
        long now = 0;
        var calls = new List<string>();
        const string source = "alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock";
        var mixer = new Mixer(calls) { Playing = null, Progress = new(source,
            new CaptureRun(46, true, true, true, new CaptureCursor(2, 0, 0, "19.095039214", 228280655))) };
        var rescue = new CaptureRescue(NullLogger.Instance, mixer,
            () => new DeviceInfo("Elgato", "XLR Dock", 0x0fd9, 0x00a6), () => Plain,
            _ => throw new InvalidOperationException("The Dock must not be reset on USB."),
            progressWatch: new CaptureWatch(() => now));
        for (int i = 0; i < 3; i++, now += 1000) rescue.Sweep();
        Assert.Empty(calls);
        rescue.Sweep();
        Assert.Equal(["reopen " + source], calls);
        Assert.False(mixer.CaptureRecoveryActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASourceIsAlwaysResumedEvenIfSuspendingFails(bool suspendSucceeds)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-reopen-capture-").FullName;
        string log = Path.Combine(directory, "calls");
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), $$"""
                printf '%s\n' "$*" >> {{OpenXLR.UI.InstallationDoctor.Quote(log)}}
                if [ "$3" = 1 ]; then exit {{(suspendSucceeds ? 0 : 1)}}; fi
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            Assert.Equal(suspendSucceeds, new PipeWireAdapter().ReopenCaptureSource("test_capture"));
            Assert.Equal(["suspend-source test_capture 1", "suspend-source test_capture 0"], File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AskedForTheCaptureLinksAreMadeAgainAndARunningCaptureIsNotReset()
    {
        // A buzz on a capture the kernel runs: the relink is the whole cure
        // tried, and the headphones keep playing.
        (CaptureRescue rescue, Mixer mixer, List<string> calls, _) = Build(Recovers);
        mixer.Run = new(10, true, true, true);
        Assert.Null(rescue.Recover());
        Assert.Equal(["relink", "settle"], calls);
        Assert.False(mixer.CaptureRecoveryActive);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("missing")]
    public void AskedForACaptureStillDownAfterTheRelinkIsResetOnUsb(string after)
    {
        (CaptureRescue rescue, Mixer mixer, List<string> calls, _) = Build(Recovers);
        mixer.AfterRelink = after == "stopped" ? new CaptureRun(10, true, true, false) : CaptureRun.Missing;
        Assert.Null(rescue.Recover());
        Assert.Equal(["relink", "settle", "playback off", "reset 0fd9:00ca"], calls);
    }

    [Fact]
    public void ARelinkThatBringsTheCaptureBackSavesTheReset()
    {
        (CaptureRescue rescue, Mixer mixer, List<string> calls, _) = Build(Recovers);
        mixer.AfterRelink = new CaptureRun(10, true, true, true);
        Assert.Null(rescue.Recover());
        Assert.Equal(["relink", "settle"], calls);
    }

    [Fact]
    public void AFailedResetPutsThePlaybackBackAndAManualRecoveryWaitsForTheFirst()
    {
        (CaptureRescue rescue, _, List<string> calls, _) = Build(Recovers, _ => "the USB reset failed (libusb error -12)");
        Assert.Contains("failed", rescue.Recover(), StringComparison.Ordinal);
        Assert.Equal(["relink", "settle", "playback off", "playback held no more"], calls);

        (rescue, _, calls, _) = Build(Recovers);
        Assert.Null(rescue.Recover());
        Assert.Contains("under way", rescue.Recover(), StringComparison.Ordinal);
        Assert.Equal(["relink", "settle", "playback off", "reset 0fd9:00ca"], calls);
    }

    /// <summary>A unit that counts its USB resets; it goes off the bus for the reset.</summary>
    private sealed class Unit(DeviceInfo info, DeviceCapabilities capabilities) : IAudioDevice
    {
        public int Resets, Connects;
        public DeviceInfo Info { get; } = info;
        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool Connected { get; private set; }
        public void Connect() { Connects++; Connected = true; }
        public void Disconnect() => Connected = false;
        public void Dispose() => Connected = false;
        public int ResetUsb() { Resets++; return -5; }
        public DeviceState ReadState() => new() { GainDb = 30 };
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    [Fact]
    public void TheResetUnitIsReconnectedWithNoOtherUnitStandingInMeanwhile()
    {
        WithConfigDir(() =>
        {
            TimeSpan previous = DeviceManager.ResetReopenDelay;
            DeviceManager.ResetReopenDelay = TimeSpan.Zero;
            try
            {
                var mk2 = new Unit(Mk2, Recovers);
                var pro = new Unit(Pro, Plain);
                bool mk2OnBus = true;
                using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(),
                    () => mk2OnBus ? [mk2, pro] : [pro], preferredModel: 0x00ca);
                manager.SweepOnce();
                Assert.True(mk2.Connected);

                Assert.Null(manager.ResetUnitUsb(Mk2.InstanceId));
                Assert.Equal(1, mk2.Resets);
                Assert.False(mk2.Connected);
                // Off the bus while it re-enumerates: the Pro is not taken.
                mk2OnBus = false;
                for (int i = 0; i < 5; i++) manager.SweepOnce();
                Assert.Equal(0, pro.Connects);
                Assert.Null(manager.ActiveInfo);

                mk2OnBus = true;
                manager.SweepOnce();
                Assert.True(mk2.Connected);
                Assert.Equal(2, mk2.Connects);
                Assert.Equal(0, pro.Connects);

                // Another unit, or one the manager no longer drives, is never reset.
                Assert.NotNull(manager.ResetUnitUsb(Pro.InstanceId));
                Assert.Equal(1, mk2.Resets);
            }
            finally { DeviceManager.ResetReopenDelay = previous; }
        });
    }

    [Fact]
    public void AModelWithoutTheCapabilityIsNotResetOnUsb()
    {
        WithConfigDir(() =>
        {
            var pro = new Unit(Pro, Plain);
            using var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [pro]);
            manager.SweepOnce();
            Assert.NotNull(manager.ResetUnitUsb(Pro.InstanceId));
            Assert.Equal(0, pro.Resets);
            Assert.True(pro.Connected);
            // The Wave XLR MK.2 backend shares the reset path; only the Wave:3 MK.2 has the capability.
            using var wave3Mk2 = new Wave3Mk2Device();
            using var waveXlrMk2 = new WaveXlrMk2Device();
            Assert.True(wave3Mk2.Capabilities.CaptureRecovery);
            Assert.False(waveXlrMk2.Capabilities.CaptureRecovery);
            Assert.Throws<InvalidOperationException>(() => waveXlrMk2.ResetUsb());
        });
    }

    private static void WithConfigDir(Action body)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-rescue-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
        try { body(); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
