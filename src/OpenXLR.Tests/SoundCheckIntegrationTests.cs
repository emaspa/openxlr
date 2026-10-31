using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed partial class SoundCheckIntegrationTests
{
    [MonitorPipeWireFact]
    public Task TheDrySampleLoopsThroughLiveProcessingAndStopRestoresTheMicrophone()
        => CheckMicrophoneLoop(clipGuard: false);

    internal static async Task CheckMicrophoneLoop(bool clipGuard)
    {
        const string channel = "xlr1";
        double take = clipGuard ? .95 : .25;
        bool limiting = clipGuard;
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("soundcheck_signal", "Synthetic mic signal");
        pw.CreateVirtualMic("Wave_XLR_soundcheck_input", "soundcheck_signal.monitor", "Synthetic microphone");
        pw.CreateNullSink("soundcheck_output", "Sound Check test output");
        mixer.SetInputDeviceHint("Wave_XLR_soundcheck_input");
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            Channels = [new(channel, "Microphone") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1 } },
                new("xlr2", "Second microphone") { InputPair = 1 }],
        });
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        if (clipGuard) mixer.SetSoftClipGuard(true);
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        Task<ProcessResult> first = Play(take, firstStop.Token);
        Task<ProcessResult>? second = null;
        try
        {
            // Observe the generator in the live output before recording. On
            // slower session managers a newly launched stream first carries
            // silence; recording that startup is valid, but not this fixture.
            AssertSound(take);
            mixer.SoundCheck(channel, "record");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes(); mixer.EnsureCellLevels();
                return mixer.Snapshot().SoundCheck.Seconds >= .5;
            }, TimeSpan.FromSeconds(5)), System.Text.Json.JsonSerializer.Serialize(mixer.Snapshot().SoundCheck) + pw.Run("pw-link", "-l") + (first.IsCompleted ? (await first).Stderr : "generator running"));
            AssertSound(take);
            Assert.Equal(channel, mixer.SoundCheckChannel);
            Assert.Equal("Stop Sound Check on Microphone first.", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr2", "record")).Message);
            mixer.SoundCheck(channel, "loop");
            Assert.True(SpinWait.SpinUntil(() => mixer.Snapshot().SoundCheck.Mode == "looping", TimeSpan.FromSeconds(3)));
            // Closing the other microphone's window sends a stop for it; this
            // session carries on.
            mixer.SoundCheck("xlr2", "stop");
            Assert.Equal("looping", mixer.Snapshot().SoundCheck.Mode);
            // The Pro's direct mic path would carry the live microphone to
            // the jacks over the loop, so it stays off for the session.
            Assert.False(mixer.SetHardwareMicMonitor(true));
            firstStop.Cancel(); await first;
            second = Play(.75, secondStop.Token);
            AssertSound(take);
            if (clipGuard)
            {
                mixer.SetSoftClipGuard(false);
                limiting = false;
                AssertSound(take);
                mixer.SetSoftClipGuard(true);
                limiting = true;
                AssertSound(take);
            }
            mixer.SetLowCutHz(120);
            AssertSound(0); // The sample enters before the current software DSP.
            mixer.SetLowCutHz(0);
            AssertSound(take);
            mixer.SetLevel(channel, "monitor", .5); AssertSound(take, .5);
            mixer.SetLevel(channel, "monitor", 1);
            mixer.SoundCheck(channel, "live");
            AssertSound(.75);
            mixer.SoundCheck(channel, "loop");
            AssertSound(take);
            mixer.SoundCheck(channel, "stop");
            AssertSound(.75);
            Assert.Null(mixer.Snapshot().SoundCheck.Channel);
            Assert.Null(mixer.SoundCheckChannel);
            // The Pro's direct path comes back once the session ends, unless
            // the soft ClipGuard is still processing the channel.
            Assert.Equal(!clipGuard, mixer.SetHardwareMicMonitor(true));
            mixer.SetHardwareMicMonitor(false);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_soundcheck_" + channel) is null, TimeSpan.FromSeconds(3)));
            mixer.SoundCheck(channel, "record");
            var node = pw.FindNodeId("OpenXLR_soundcheck_" + channel);
            Assert.NotNull(node);
            pw.Run("pw-cli", "destroy", node.Value.ToString());
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return mixer.Snapshot().SoundCheck.Mode == "idle";
            }, TimeSpan.FromSeconds(5)));
            Assert.NotNull(mixer.Snapshot().SoundCheck.Error);
            Assert.Equal(channel, mixer.Snapshot().SoundCheck.Channel);
            AssertSound(.75);
            mixer.SoundCheck(channel, "stop");
            Assert.Null(mixer.Snapshot().SoundCheck.Error);
            mixer.SoundCheck(channel, "record");
            typeof(Mixer).GetField("_soundCheckStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(mixer, System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 601);
            mixer.EnsureFilterRoutes();
            Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
            Assert.Contains("ten minutes", mixer.Snapshot().SoundCheck.Error);
            AssertSound(.75);
            mixer.SoundCheck(channel, "stop");
            Assert.Null(mixer.Snapshot().SoundCheck.Error);
            mixer.SoundCheck(channel, "record");
            mixer.Build(mixer.Config);
            Assert.Contains("rebuilt", mixer.Snapshot().SoundCheck.Error);
        }
        finally
        {
            firstStop.Cancel(); secondStop.Cancel();
            await first;
            if (second is not null) await second;
        }

        // The player asks for the graph's default quantum. Destroying the
        // helper below cuts the channel off the microphone for a moment, and
        // PipeWire 1.0 left the test's remap-source microphone silent when
        // the two halves rejoined at different quanta; a player at pw-cat's
        // 100 ms default ran its half at 2048 against the meters' 1024.
        static Task<ProcessResult> Play(double value, CancellationToken cancel) => ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess, struct, sys
            args = ['--format=f32', '--rate=48000', '--channels=2', '--latency=1024']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', 'soundcheck_signal', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', *([float(sys.argv[1])] * 2)) * (48000 * 45), timeout=50)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, value.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(55), cancel: cancel);

        void AssertSound(double expected, double level = 1)
        {
            Assert.Equal(limiting, mixer.SoftClipGuard);
            double limited = limiting ? Math.Min(expected, Math.Pow(10, -3.0 / 20)) : expected;
            var result = CaptureSound(limited * Math.Pow(level, 3));
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
    }

    internal static ProcessResult CaptureSound(double expected, TimeSpan? deadline = null,
        IReadOnlyDictionary<string, string>? environment = null, string target = "soundcheck_output")
    {
        TimeSpan duration = deadline ?? TimeSpan.FromSeconds(5);
        return ProcessRunner.Run("python3", ["-c", """
            import math, os, struct, subprocess, sys, tempfile, time
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with tempfile.TemporaryFile() as capture:
                p = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record }', '--target', sys.argv[3], '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE)
                try:
                    expected = float(sys.argv[1])
                    deadline = time.monotonic() + float(sys.argv[2])
                    last = 'no captured samples'
                    while time.monotonic() < deadline:
                        size = os.fstat(capture.fileno()).st_size // 4 * 4
                        if size > 24000 * 4:
                            # pread leaves the writer's shared file offset alone.
                            raw = os.pread(capture.fileno(), 12000 * 4, size - 12000 * 4)
                            tail = struct.unpack('<12000f', raw)
                            matching = sum(abs(v - expected) < .015 for v in tail) / len(tail)
                            rms = math.sqrt(sum(v*v for v in tail) / len(tail))
                            last = (expected, min(tail), max(tail), matching, rms)
                            # Keep the same gain and high-pass attenuation checks.
                            # A high-pass has a short transient at each loop seam.
                            if all(map(math.isfinite, tail)) and (rms < .06 if expected == 0 else matching > .9):
                                break
                        if p.poll() is not None:
                            raise AssertionError(('capture exited before stable audio', p.returncode, p.communicate()[1].decode(), last))
                        time.sleep(.02)
                    else:
                        raise AssertionError(('no stable audio before the deadline', last))
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
            """, expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), target],
            duration + TimeSpan.FromSeconds(5), environment: environment);
    }
}
