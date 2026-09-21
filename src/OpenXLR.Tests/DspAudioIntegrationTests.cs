using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public void SoftwareClipGuardCarriesAudioWithAndWithoutLowCut()
    {
        var pw = new PipeWireAdapter();
        try
        {
            foreach (bool native in new[] { false, true })
            foreach (int hz in new[] { 0, 80, 120 })
            {
                var inserts = native ? new List<InsertDefinition>
                {
                    new() { Id = "test", Kind = "lv2", Plugin = "http://lsp-plug.in/plugins/lv2/gate_mono",
                        NativeHost = true, Params = new() { ["enabled"] = 0 } },
                } : null;
                var filter = pw.CreateMicFilter($"signal_{native}_{hz}", hz, true, inserts);
                try
                {
                    var result = ProcessRunner.Run("python3", ["-c", """
                        import math, struct, subprocess, sys, tempfile
                        rate = 48000
                        samples = b''.join(struct.pack('<f', 0.95 * math.sin(2 * math.pi * 1000 * i / rate)) for i in range(rate * 4))
                        args = ['--format=f32', '--rate=48000', '--channels=1']
                        # Older pw-cat versions use raw audio on stdin/stdout implicitly.
                        if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True):
                            args.append('--raw')
                        capture = tempfile.TemporaryFile()
                        record = subprocess.Popen(['pw-cat', '--record', *args, '--target', sys.argv[2], '-'], stdout=capture, stderr=subprocess.PIPE)
                        play = None
                        try:
                            play = subprocess.Popen(['pw-cat', '--playback', *args, '--target', sys.argv[1], '-'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                            _, playback_errors = play.communicate(samples, timeout=10)
                            assert play.returncode == 0, playback_errors.decode()
                            record.terminate()
                            _, errors = record.communicate(timeout=5)
                            capture.seek(0)
                            audio = capture.read()
                            values = struct.unpack('<' + 'f' * (len(audio) // 4), audio)
                            peak = max(map(abs, values), default=0)
                            assert peak > 0.1, f'Silent filter: peak={peak}, {errors.decode()}'
                            assert 0.68 < peak < 0.72, f'Limiter did not hold its -3 dB ceiling: {peak}'
                            tail = values[-rate:]
                            rms = math.sqrt(sum(v*v for v in tail) / max(1, len(tail)))
                            assert rms > 0.1, f'Audio stalled: RMS={rms}'
                            print(f'Audio passed, peak={peak:.4f}')
                        finally:
                            for process in (play, record):
                                if process is None:
                                    continue
                                if process.poll() is None:
                                    process.kill()
                                process.wait()
                            capture.close()
                        """, filter.SinkName, filter.SourceName], TimeSpan.FromSeconds(20));
                    Assert.True(result.Ok, result.StdoutText + result.Stderr);
                }
                finally { pw.StopFilter(filter); }
            }
        }
        finally { pw.TearDown(); }
    }

}

internal sealed class DspPipeWireFactAttribute : FactAttribute
{
    public DspPipeWireFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME") != "1" ||
            Environment.GetEnvironmentVariable("OPENXLR_TEST_DSP") != "1")
            Skip = "Run OPENXLR_TEST_DSP=1 tools/test-monitor-volume.py with swh-plugins, LSP LV2 plugins and the native host installed.";
    }
}
