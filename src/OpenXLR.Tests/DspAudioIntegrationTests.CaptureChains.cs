using System.Globalization;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public void MonoCaptureGainScalesTheSelectedPortOnBothSides() => CaptureGain(1);

    [DspPipeWireFact]
    public void StereoCaptureGainScalesBothPortsIndependently() => CaptureGain(2);

    /// <summary>
    /// One NaN from an application reaches a native insert as silence: the
    /// helper keeps running, the chain keeps its nodes, nothing is rebuilt
    /// and the restart budget is untouched. The helper's trace line, which
    /// counts the samples it replaced, reaches the journal.
    /// </summary>
    [DspPipeWireFact]
    public void InvalidCaptureAudioIsSilencedWithoutRestartingItsNativeChain()
    {
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var pw = new PipeWireAdapter(() => { }, notes.Enqueue);
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw, out _, width: 2);
        mixer.SetLevel("capture", "monitor", 1);
        mixer.SetChannelMuted("capture", "monitor", false);
        mixer.SetMixLatencyCompensation(true);
        var insert = new InsertDefinition { Id = "delay", Kind = "lv2", Plugin = "urn:openxlr:test:latency",
            Params = new() { ["delay"] = 0 } };
        mixer.SetInserts("capture", [insert]);
        mixer.SetInserts("mix:monitor", [insert]);
        Assert.True(Assert.Single(mixer.Snapshot().Inserts["capture"]).NativeHostRunning);
        Assert.True(Assert.Single(mixer.Snapshot().Inserts["mix:monitor"]).NativeHostRunning);
        int? unaffected = null;
        CaptureChainFixture.Wait(() =>
            (unaffected = pw.FindNodeId("OpenXLR_ins_monitor_in_stage_0")) is not null);
        AssertCaptureDc(2, 1);
        string[] before = CaptureChainFixture.WaitForChain(pw, "capture");
        ProcessResult result = ProcessRunner.Run("python3", ["-c", """
            import struct, subprocess
            args = ['pw-cat', '--playback', '--properties={ node.link-group = openxlr-test-playback }',
                    '--format=f32', '--rate=48000', '--channels=2', '--target', 'capture_test_signal', '-']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.insert(2, '--raw')
            signal = struct.pack('<ff', .1, .2) * 24000
            signal += struct.pack('<ff', float('nan'), .2)
            signal += struct.pack('<ff', .1, .2) * 24000
            subprocess.run(args, input=signal, check=True, timeout=10)
            """], TimeSpan.FromSeconds(15));
        Assert.True(result.Ok, result.Stderr);
        CaptureChainFixture.Wait(() => notes.Any(line =>
            line.Contains("non-finite input samples replaced with silence", StringComparison.Ordinal)),
            () => string.Join("\n", notes));
        Assert.True(Assert.Single(mixer.Snapshot().Inserts["capture"]).NativeHostRunning);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        Assert.Equal(before, CaptureChainFixture.Nodes(pw, "capture"));
        Assert.Equal(unaffected, pw.FindNodeId("OpenXLR_ins_monitor_in_stage_0"));
        AssertCaptureDc(2, 1);
    }

    private static void CaptureGain(int width)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw, out _, width);
        mixer.SetLevel("capture", "monitor", 1);
        mixer.SetChannelMuted("capture", "monitor", false);
        mixer.EnsureCellLevels();
        AssertCaptureDc(width, 1);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(width)]);
        CaptureChainFixture.WaitForChain(pw, "capture");
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        AssertCaptureDc(width, .25);
        mixer.SetInsertParam("capture", "gain", "gain", .5);
        AssertCaptureDc(width, .5);
        mixer.SetInsertBypass("capture", "gain", true);
        AssertCaptureDc(width, 1);
    }

    /// <summary>
    /// A capture chain that cannot be built leaves the channel on its direct
    /// feed with the reason on the insert. The builder refuses to host this
    /// LV2 plugin natively, since it has no editor, before any node exists.
    /// (A missing LV2 binary is no such failure: PipeWire builds the
    /// filter-chain's nodes and only fails when the graph starts.)
    /// </summary>
    [DspPipeWireFact]
    public void ACaptureChainThatCannotBeBuiltLeavesTheDirectFeedPlaying()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.SetLevel("capture", "monitor", 1);
        mixer.SetChannelMuted("capture", "monitor", false);
        mixer.EnsureCellLevels();
        AssertCaptureDc(1, 1);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1) with { NativeHost = true }]);
        Assert.NotNull(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        Assert.Empty(CaptureChainFixture.Nodes(pw, "capture"));
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected);
        AssertCaptureDc(1, 1);
    }

    private static void AssertCaptureDc(int width, double gain)
    {
        ProcessResult result = ProcessRunner.Run("python3", ["-c", """
            import struct, subprocess, sys, tempfile
            rate = 48000
            width, gain = int(sys.argv[1]), float(sys.argv[2])
            expected = [.1 * gain, (.1 if width == 1 else .2) * gain]
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with tempfile.TemporaryFile() as capture:
                record = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record stream.capture.sink = true }',
                    '--target', 'OpenXLR_mix_monitor', '-'], stdout=capture, stderr=subprocess.PIPE)
                play = None
                try:
                    play = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }',
                        '--target', 'capture_test_signal', '-'], stdin=subprocess.PIPE, stderr=subprocess.PIPE)
                    _, error = play.communicate(struct.pack('<ff', .1, .2) * (rate * 3), timeout=12)
                    assert play.returncode == 0, error.decode()
                    record.terminate(); record.communicate(timeout=5)
                    capture.seek(0); data = capture.read()
                    values = struct.unpack('<' + 'f' * (len(data) // 4), data)
                    frames = list(zip(values[0::2], values[1::2]))
                    carrying = [i for i, f in enumerate(frames) if max(map(abs, f)) > min(expected) / 2]
                    assert carrying, 'No capture audio'
                    steady = frames[carrying[0] + rate // 5 : carrying[-1] - rate // 5]
                    assert len(steady) >= rate, 'Capture did not settle'
                    for channel in range(2):
                        matching = sum(abs(f[channel] - expected[channel]) < .002 for f in steady) / len(steady)
                        assert matching > .99, (channel, expected, matching)
                finally:
                    for process in (play, record):
                        if process is None: continue
                        if process.poll() is None: process.kill()
                        process.wait()
            """, width.ToString(CultureInfo.InvariantCulture), gain.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(20));
        Assert.True(result.Ok, result.StdoutText + result.Stderr);
    }
}
