using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class SoundCheckIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task TheDrySampleLoopsThroughLiveProcessingAndStopRestoresTheMicrophone()
    {
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
            Channels = [new("xlr1", "Microphone") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1 } }],
        });
        mixer.SetMonitorOutputs(["soundcheck_output"]);
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        Task<ProcessResult> first = Play(.25, firstStop.Token);
        Task<ProcessResult>? second = null;
        try
        {
            // Observe the generator in the live output before recording. On
            // slower session managers a newly launched stream first carries
            // silence; recording that startup is valid, but not this fixture.
            AssertSound(.25);
            mixer.SoundCheck("xlr1", "record");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes(); mixer.EnsureCellLevels();
                return mixer.Snapshot().SoundCheck.Seconds >= .5;
            }, TimeSpan.FromSeconds(5)), System.Text.Json.JsonSerializer.Serialize(mixer.Snapshot().SoundCheck) + pw.Run("pw-link", "-l") + (first.IsCompleted ? (await first).Stderr : "generator running"));
            mixer.SoundCheck("xlr1", "loop");
            Assert.True(SpinWait.SpinUntil(() => mixer.Snapshot().SoundCheck.Mode == "looping", TimeSpan.FromSeconds(3)));
            firstStop.Cancel(); await first;
            second = Play(.75, secondStop.Token);
            AssertSound(.25);
            mixer.SetLowCutHz(120);
            AssertSound(0); // The sample enters before the current software DSP.
            mixer.SetLowCutHz(0);
            AssertSound(.25);
            mixer.SoundCheck("xlr1", "live");
            AssertSound(.75);
            mixer.SoundCheck("xlr1", "loop");
            AssertSound(.25);
            mixer.SoundCheck("xlr1", "stop");
            AssertSound(.75);
            Assert.Null(mixer.Snapshot().SoundCheck.Channel);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_soundcheck_xlr1") is null, TimeSpan.FromSeconds(3)));
            mixer.SoundCheck("xlr1", "record");
            var node = pw.FindNodeId("OpenXLR_soundcheck_xlr1");
            Assert.NotNull(node);
            pw.Run("pw-cli", "destroy", node.Value.ToString());
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return mixer.Snapshot().SoundCheck.Channel is null;
            }, TimeSpan.FromSeconds(5)));
            Assert.NotNull(mixer.Snapshot().SoundCheck.Error);
            AssertSound(.75);
        }
        finally
        {
            firstStop.Cancel(); secondStop.Cancel();
            await first;
            if (second is not null) await second;
        }

        static Task<ProcessResult> Play(double value, CancellationToken cancel) => ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess, struct, sys
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--target', 'soundcheck_signal', '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', *([float(sys.argv[1])] * 2)) * (48000 * 45), timeout=50)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, value.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(55), cancel: cancel);

        static void AssertSound(double expected)
        {
            var result = ProcessRunner.Run("python3", ["-c", """
                import math, struct, subprocess, sys, tempfile, time
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                with tempfile.TemporaryFile() as capture:
                    p = subprocess.Popen(['pw-cat', '--record', *args, '--target', 'soundcheck_output', '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE)
                    try:
                        time.sleep(.7); p.terminate(); _, errors = p.communicate(timeout=5)
                        capture.seek(0); raw = capture.read()
                        values = struct.unpack('<' + 'f' * (len(raw) // 4), raw)
                        assert len(values) > 24000, (len(values), errors.decode())
                        tail = values[-12000:]
                        expected = float(sys.argv[1])
                        matching = sum(abs(v - expected) < .015 for v in tail) / len(tail)
                        # A high-pass produces a brief transient at each loop seam.
                        # Measure attenuation, not the false assumption that every
                        # sample of a periodically restarted DC signal becomes zero.
                        if expected == 0:
                            rms = math.sqrt(sum(v*v for v in tail) / len(tail))
                            assert rms < .06, (expected, rms, min(tail), max(tail))
                        else:
                            assert matching > .9, (expected, min(tail), max(tail), matching)
                    finally:
                        if p.poll() is None: p.kill()
                        p.wait()
                """, expected.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(10));
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
    }
}
