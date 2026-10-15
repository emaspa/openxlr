using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FilterStateIntegrationTests
{
    [MonitorPipeWireFact]
    public void InternalMicFiltersIgnoreSavedDesktopMuteAndGain()
    {
        var pw = new PipeWireAdapter();
        try
        {
            // The private runner seeds both halves with a saved mute and 12.5% gain.
            var filter = pw.CreateMicFilter("restore_test", 120, false);
            try
            {
                var result = ProcessRunner.Run("python3", ["-c", """
                    import math, struct, subprocess, sys, tempfile
                    rate = 48000
                    signal = b''.join(struct.pack('<f', .25 * math.sin(2 * math.pi * 1000 * i / rate)) for i in range(rate * 3))
                    args = ['--format=f32', '--rate=48000', '--channels=1']
                    if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                    with tempfile.TemporaryFile() as capture:
                        record = subprocess.Popen(['pw-cat', '--record', *args, '--target', sys.argv[2], '-'], stdout=capture, stderr=subprocess.PIPE)
                        play = None
                        try:
                            play = subprocess.Popen(['pw-cat', '--playback', *args, '--target', sys.argv[1], '-'], stdin=subprocess.PIPE, stderr=subprocess.PIPE)
                            _, errors = play.communicate(signal, timeout=10)
                            assert play.returncode == 0, errors.decode()
                            record.terminate(); record.communicate(timeout=5)
                            capture.seek(0); raw = capture.read()
                            values = struct.unpack('<' + 'f' * (len(raw) // 4), raw)
                            tail = values[-rate:]
                            rms = math.sqrt(sum(v*v for v in tail) / max(1, len(tail)))
                            assert .15 < rms < .20, f'Internal filter restored mute or gain: RMS={rms}'
                        finally:
                            for p in (play, record):
                                if p is not None:
                                    if p.poll() is None: p.kill()
                                    p.wait()
                    """, filter.SinkName, filter.SourceName], TimeSpan.FromSeconds(20));
                Assert.True(result.Ok, result.StdoutText + result.Stderr);
                using var graph = JsonDocument.Parse(ProcessRunner.Run("pw-dump", [], TimeSpan.FromSeconds(5)).StdoutText);
                foreach (string name in new[] { filter.SinkName, filter.SourceName })
                {
                    var node = graph.RootElement.EnumerateArray().Single(n =>
                        n.TryGetProperty("info", out var info) && info.TryGetProperty("props", out var props) &&
                        props.TryGetProperty("node.name", out var value) && value.GetString() == name);
                    var props = node.GetProperty("info").GetProperty("props");
                    Assert.True(props.GetProperty("node.hidden").GetBoolean());
                    Assert.False(props.GetProperty("state.restore-props").GetBoolean());
                }
            }
            finally { pw.StopFilter(filter); }
        }
        finally { pw.TearDown(); }
    }
}
