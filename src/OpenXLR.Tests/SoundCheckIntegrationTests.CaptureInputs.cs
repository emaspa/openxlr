using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class SoundCheckIntegrationTests
{
    private const string CaptureSource = "Wave_XLR_soundcheck_capture";

    [MonitorPipeWireFact]
    public Task AMonoCaptureTakeLoopsWithoutInsertsAndStopRestoresItsDirectFeed() => CheckCaptureLoop(withInsert: false);

    [MonitorPipeWireFact]
    public Task AMonoCaptureTakeLoopsThroughLiveInsertEdits() => CheckCaptureLoop(withInsert: true);

    private static async Task CheckCaptureLoop(bool withInsert)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        if (withInsert) mixer.SetInserts("capture", [CaptureChainFixture.Gain(1, 1)]);
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        Task<ProcessResult> first = PlayCapture(.25, firstStop.Token);
        Task<ProcessResult>? second = null;
        try
        {
            AssertCapture(.25);
            mixer.SoundCheck("capture", "record");
            CaptureChainFixture.Wait(() =>
            {
                mixer.EnsureFilterRoutes(); mixer.EnsureCellLevels();
                return mixer.Snapshot().SoundCheck.Seconds >= .5;
            });
            mixer.SoundCheck("capture", "loop");
            CaptureChainFixture.Wait(() => mixer.Snapshot().SoundCheck.Mode == "looping");
            Assert.DoesNotContain($"{CaptureSource} -> OpenXLR_ch_capture", CaptureLinks());
            firstStop.Cancel(); await first;
            second = PlayCapture(.75, secondStop.Token);
            AssertCapture(.25);
            // A sweep must not heal the direct feed back over the loop.
            for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
            AssertCapture(.25);
            if (withInsert)
            {
                Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
                mixer.SetInsertParam("capture", "gain", "gain", .5);
                AssertCapture(.125);
                mixer.SetInsertBypass("capture", "gain", true);
                AssertCapture(.25);
                mixer.SetInsertBypass("capture", "gain", false);
                AssertCapture(.125);
            }
            double gain = withInsert ? .5 : 1;
            mixer.SoundCheck("capture", "live");
            AssertCapture(.75 * gain);
            mixer.SoundCheck("capture", "loop");
            AssertCapture(.25 * gain);
            mixer.SoundCheck("capture", "stop");
            AssertCapture(.75 * gain);
            Assert.Null(mixer.SoundCheckChannel);
            CaptureChainFixture.Wait(() => pw.FindNodeId("OpenXLR_soundcheck_capture") is null);
            if (withInsert)
            {
                Assert.NotEmpty(CaptureChainFixture.Nodes(pw, "capture"));
                mixer.SetInserts("capture", []);
                AssertCapture(.75);
            }
            Assert.Empty(CaptureChainFixture.Nodes(pw, "capture"));
            Assert.Contains($"{CaptureSource} -> OpenXLR_ch_capture", CaptureLinks());
            // The EQ is offered and off, so no stage ever joined the route.
            Assert.False(mixer.Snapshot().Channels.Single(c => c.Id == "capture").Eq!.Enabled);
            Assert.DoesNotContain(pw.DumpNodes(), n => n.Name.StartsWith("OpenXLR_eq_capture_", StringComparison.Ordinal));
        }
        finally
        {
            firstStop.Cancel(); secondStop.Cancel();
            await first;
            if (second is not null) await second;
        }
    }

    [MonitorPipeWireFact]
    public void DeletingACaptureInputEndsItsSoundCheckAfterTheSave()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        mixer.SoundCheck("capture", "record");
        Assert.Throws<IOException>(() => mixer.DeleteApplicationChannel("capture", _ => "disk full"));
        Assert.Equal("capture", mixer.SoundCheckChannel);
        Assert.NotNull(pw.FindNodeId("OpenXLR_soundcheck_capture"));
        mixer.DeleteApplicationChannel("capture", _ => null);
        Assert.Null(mixer.SoundCheckChannel);
        SoundCheckState state = mixer.Snapshot().SoundCheck;
        Assert.Equal("capture", state.Channel);
        Assert.Equal("idle", state.Mode);
        Assert.Contains("deleted", state.Error);
        Assert.False(mixer.IsSoundCheckChannel("capture"));
        CaptureChainFixture.Wait(() => pw.FindNodeId("OpenXLR_soundcheck_capture") is null
            && pw.FindNodeId("OpenXLR_ch_capture") is null);
        mixer.SoundCheck("xlr1", "record");
        Assert.Equal("xlr1", mixer.SoundCheckChannel);
    }

    [MonitorPipeWireFact]
    public void ACaptureSessionAndAnXlrSessionRefuseEachOther()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        foreach ((string owner, string other, string name) in new[]
            { ("xlr1", "capture", "Interface microphone"), ("capture", "xlr1", "Guest microphone") })
        {
            mixer.SoundCheck(owner, "record");
            foreach (string action in new[] { "record", "loop", "live" })
                Assert.Equal($"Stop Sound Check on {name} first.",
                    Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck(other, action)).Message);
            mixer.SoundCheck(other, "stop");
            Assert.Equal(owner, mixer.SoundCheckChannel);
            Assert.Null(pw.FindNodeId("OpenXLR_soundcheck_" + other));
            mixer.SoundCheck(owner, "stop");
        }
    }

    [MonitorPipeWireFact]
    public void CaptureRecordingRefusesAMissingSourceOrPort()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        // A capture input is only added on a source that is there; this one
        // goes away afterwards.
        uint absent = pw.CreateVirtualMic("soundcheck_absent", "soundcheck_capture_signal.monitor", "Absent microphone");
        CaptureChainFixture.Wait(() => pw.ListDevices().Any(d => d.Name == "soundcheck_absent"));
        mixer.CreateCaptureChannel("Missing microphone", "soundcheck_absent", 0, _ => null, monoChannel: 0);
        pw.UnloadModule(absent);
        CaptureChainFixture.Wait(() =>
        {
            // The native removal can precede the registry's source removal.
            if (pw.ListDevices().Any(d => d.Name == "soundcheck_absent")) return false;
            mixer.EnsureInputFeeds();
            ChannelStatus channel = mixer.Snapshot().Channels.Single(c => c.CaptureSource == "soundcheck_absent");
            return !channel.CaptureConnected && channel.BindingWarning is not null;
        });
        ChannelStatus missing = mixer.Snapshot().Channels.Single(c => c.CaptureSource == "soundcheck_absent");
        string error = Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck(missing.Id, "record")).Message;
        Assert.Contains("The microphone is not connected.", error);
        Assert.Contains(missing.Name, error);
        Assert.Contains(missing.BindingWarning!, error);
        mixer.CreateCaptureChannel("Missing port", CaptureSource, 0, _ => null, monoChannel: 63);
        ChannelStatus port = mixer.Snapshot().Channels.Single(c => c.CaptureMonoChannel == 63);
        Assert.Contains("capture port is unavailable", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck(port.Id, "record")).Message);
        Assert.Null(mixer.SoundCheckChannel);
        CaptureChainFixture.Wait(() => pw.FindNodeId("OpenXLR_soundcheck_" + port.Id) is null);
    }

    [MonitorPipeWireFact]
    public void LosingACaptureSourceEndsItsSessionAndReplugRestoresTheDirectFeed()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out uint source);
        mixer.SoundCheck("capture", "record");
        pw.UnloadModule(source);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes();
            return mixer.SoundCheckChannel is null;
        });
        Assert.NotNull(mixer.Snapshot().SoundCheck.Error);
        Assert.Equal("capture", mixer.Snapshot().SoundCheck.Channel);
        pw.CreateVirtualMic(CaptureSource, "soundcheck_capture_signal.monitor", "Capture microphone");
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected;
        });
        Assert.Contains($"{CaptureSource} -> OpenXLR_ch_capture", CaptureLinks());
        Assert.Null(pw.FindNodeId("OpenXLR_soundcheck_capture"));
    }

    private static Mixer BuildCapture(PipeWireAdapter pw, out uint source)
    {
        pw.CreateNullSink("soundcheck_capture_signal", "Capture signal");
        source = pw.CreateVirtualMic(CaptureSource, "soundcheck_capture_signal.monitor", "Capture microphone");
        CaptureChainFixture.Wait(() => pw.ListDevices().Any(d => d.Name == CaptureSource));
        var mixer = new Mixer(pw);
        try
        {
            mixer.SetInputDeviceHint(CaptureSource);
            mixer.Build(new MixerConfig
            {
                Channels = [new("system", "System"), new("xlr1", "Interface microphone") { InputPair = 0 },
                    new("capture", "Guest microphone") { CaptureSource = CaptureSource, CaptureMonoChannel = 1,
                        Levels = new Dictionary<string, double> { ["monitor"] = 1 } }],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            });
            CaptureChainFixture.Wait(() =>
            {
                mixer.EnsureInputFeeds(); mixer.EnsureCellLevels();
                return mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected;
            });
            return mixer;
        }
        catch { mixer.Dispose(); throw; }
    }

    private static string[] CaptureLinks() => CaptureChainFixture.Topology(([], [])).Links;

    private static void AssertCapture(double expected)
    {
        ProcessResult result = CaptureSound(expected, target: "OpenXLR_ch_capture");
        Assert.True(result.Ok, result.StdoutText + result.Stderr);
    }

    private static Task<ProcessResult> PlayCapture(double value, CancellationToken cancel)
        => ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess, struct, sys
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }',
                '--target', 'soundcheck_capture_signal', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', .1, float(sys.argv[1])) * (48000 * 120), timeout=125)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, value.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(130), cancel: cancel);
}
