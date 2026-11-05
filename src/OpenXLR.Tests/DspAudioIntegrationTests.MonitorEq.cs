using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public void MonitorPresetsSurviveRenameAndRestoreAndStayOnTheirOwnMix()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "A", MixKind.Monitor), new("monitor2", "B", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels = [],
        });
        MonitorEqStatus Status(string id) => mixer.Snapshot().Mixes.Single(m => m.Id == id).MonitorEq!;
        Assert.Throws<InvalidOperationException>(() => mixer.SetMonitorEqPreset("chat", "fps", _ => null));
        Assert.Throws<InvalidOperationException>(() => mixer.SetMonitorEqPreset("monitor", "bad", _ => null));
        Assert.Throws<IOException>(() => mixer.SetMonitorEqPreset("monitor", "bass-boost", _ => "cannot save"));
        Assert.Null(Status("monitor").Preset);
        mixer.SetMonitorEqPreset("monitor", "bass-boost", _ => null);
        mixer.SetMonitorEqPreset("monitor2", "fps", _ => null);
        Assert.Equal(new MonitorEqStatus("bass-boost", null), Status("monitor"));
        Assert.Equal(new MonitorEqStatus("fps", null), Status("monitor2"));
        mixer.RenameMix("monitor2", "Earbuds", _ => null);
        Assert.Equal("fps", Status("monitor2").Preset);
        MixerSettings saved = mixer.ExportSettings();
        mixer.SetMonitorEqPreset("monitor", null, _ => null);
        Assert.DoesNotContain(MixDelayIntegrationTests.OwnNodes(pw), n => n == "OpenXLR_ins_monitor_out");
        mixer.ApplySettings(saved);
        Assert.Equal(new MonitorEqStatus("bass-boost", null), Status("monitor"));
        Assert.Equal(new MonitorEqStatus("fps", null), Status("monitor2"));
        Assert.Throws<IOException>(() => mixer.DeleteMix("monitor2", _ => "cannot save"));
        Assert.Equal("fps", Status("monitor2").Preset);
        mixer.DeleteMix("monitor2", settings =>
        {
            Assert.DoesNotContain("monitor2", settings.MonitorEqPresets!.Keys);
            return null;
        });
        Assert.Equal("bass-boost", Status("monitor").Preset);
    }

    [DspPipeWireFact]
    public void BassPresetShapesBothSidesOfARealPipeWireFilter()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        FilterHandle eq = pw.CreateMonitorEqChain("eq-test", "Test monitor EQ", [], MonitorEqPresets.Find("bass-boost")!);
        try
        {
            var result = ProcessRunner.Run("python3", ["-c", """
                import math, struct, subprocess, sys, tempfile
                rate, frames = 48000, 96000
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                signal = bytearray()
                for n in range(frames):
                    sample = sum(.15 * math.sin(2 * math.pi * hz * n / rate) for hz in (80, 2000))
                    signal.extend(struct.pack('<ff', sample, sample))
                with tempfile.TemporaryFile() as output:
                    rec = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record }',
                                            '--target', sys.argv[2], '-'], stdout=output, stderr=subprocess.PIPE)
                    try:
                        subprocess.run(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }',
                                        '--target', sys.argv[1], '-'], input=signal, check=True, timeout=10)
                    finally:
                        rec.terminate()
                        rec.communicate(timeout=5)
                    output.seek(0)
                    raw = output.read()
                assert len(raw) >= 8 * 24000, len(raw)
                samples = struct.unpack('<' + 'f' * (len(raw) // 4), raw)
                start = len(samples) // 4 - 4800
                def amplitude(channel, hz):
                    return abs(sum(samples[2 * (start + n) + channel] * complex(
                        math.cos(2 * math.pi * hz * n / rate), math.sin(2 * math.pi * hz * n / rate))
                        for n in range(4800))) / 2400
                for channel in (0, 1):
                    bass, mid = amplitude(channel, 80), amplitude(channel, 2000)
                    assert .13 < bass < .16 and 1.5 < bass / mid < 2.0, (bass, mid)
                assert abs(amplitude(0, 80) - amplitude(1, 80)) < .001
                """, eq.SinkName, eq.SourceName], TimeSpan.FromSeconds(20));
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
        finally { pw.StopFilter(eq); pw.TearDown(); }
    }
}
