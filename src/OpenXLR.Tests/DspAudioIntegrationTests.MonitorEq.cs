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
        CaptureChainFixture.Wait(() => !MixDelayIntegrationTests.OwnNodes(pw).Contains("OpenXLR_meq_monitor_out"));
        Assert.Contains("OpenXLR_meq_monitor2_out", MixDelayIntegrationTests.OwnNodes(pw));
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
    public void RestoringAndChangingMonitorEqKeepsTheHostedInsertAndItsRestartLimit()
    {
        const string mix = "monitor", key = "mix:monitor";
        const string inserts = "OpenXLR_ins_", eq = "OpenXLR_meq_";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.SetMixLatencyCompensation(true);
        mixer.SetInserts(key, [Fixture("delay", 0)]);
        mixer.Build(new MixerConfig { Mixes = [new(mix, "A", MixKind.Monitor)], Channels = [] });

        InsertStatus Insert() => Assert.Single(mixer.Snapshot().Inserts[key]);
        MonitorEqStatus Eq() => Assert.Single(mixer.Snapshot().Mixes).MonitorEq!;
        Assert.True(Insert().NativeHostRunning, Insert().Error);
        var originalHost = SettledChain(mix, inserts).Nodes.OrderBy(p => p.Key).ToArray();
        mixer.ApplySettings(mixer.ExportSettings() with { MonitorEqPresets = new() { [mix] = "fps" } });
        Assert.Equal(originalHost, SettledChain(mix, inserts).Nodes.OrderBy(p => p.Key));
        Assert.Equal(new MonitorEqStatus("fps", null), Eq());

        mixer.SetMonitorEqPreset(mix, "bass-boost", _ => null);
        Assert.Equal(originalHost, SettledChain(mix, inserts).Nodes.OrderBy(p => p.Key));
        var originalEq = SettledChain(mix, eq).Nodes.OrderBy(p => p.Key).ToArray();

        // Kill the insert process three times, as the normal sweep would
        // observe a crashing plugin. Each filter remains owned by this test.
        for (int death = 0; death < 3; death++)
        {
            KillChainHolder(SettledChain(mix, inserts));
            Assert.True(mixer.EnsureFilterRoutes());
        }
        Assert.Contains("kept failing", Insert().Error);
        Assert.False(Insert().NativeHostRunning);
        Assert.Equal(new MonitorEqStatus("bass-boost", null), Eq());
        Assert.Equal(originalEq, SettledChain(mix, eq).Nodes.OrderBy(p => p.Key));

        mixer.SetMonitorEqPreset(mix, "soft-treble", _ => null);
        Assert.Contains("kept failing", Insert().Error);
        Assert.False(Insert().NativeHostRunning);
        Assert.Empty(Chain(mix, inserts).Nodes);
        Assert.Equal(new MonitorEqStatus("soft-treble", null), Eq());
        var workingEq = SettledChain(mix, eq).Nodes.OrderBy(p => p.Key).ToArray();

        // This fixture has no native editor, so forcing its native mode
        // fails at chain creation. The listening filter is still usable.
        mixer.SetInserts(key, [CaptureChainFixture.Gain(2) with { NativeHost = true }]);
        Assert.Contains("no editor", Insert().Error);
        Assert.Equal(new MonitorEqStatus("soft-treble", null), Eq());
        Assert.Equal(workingEq, SettledChain(mix, eq).Nodes.OrderBy(p => p.Key));
    }

    [DspPipeWireFact]
    public void MonitorEqFailureLeavesTheInsertAndAProJackKeepsItsDirectMic()
    {
        const string mix = "monitor", key = "mix:monitor";
        const string inserts = "OpenXLR_ins_", eq = "OpenXLR_meq_";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.SetMixLatencyCompensation(true);
        mixer.Build(MixerConfig.Default());
        mixer.SetChannelMuted("xlr1", mix, false);
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: mix));
        mixer.SetInserts(key, [Fixture("delay", 0)]);
        mixer.SetMonitorEqPreset(mix, "fps", _ => null);
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: mix));

        var originalHost = SettledChain(mix, inserts).Nodes.OrderBy(p => p.Key).ToArray();
        KillChainHolder(SettledChain(mix, eq));
        Assert.True(mixer.EnsureFilterRoutes());
        var state = mixer.Snapshot();
        Assert.Null(Assert.Single(state.Inserts[key]).Error);
        Assert.Contains("monitor EQ stopped", state.Mixes.Single(m => m.Id == mix).MonitorEq!.Error);
        Assert.Equal(originalHost, Chain(mix, inserts).Nodes.OrderBy(p => p.Key));
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: mix));
    }

    [DspPipeWireFact]
    public void ChangingMonitorEqPreservesLinksToAnOutputOnAnotherMix()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        const string first = "monitor_eq_test_a", second = "monitor_eq_test_b";
        pw.CreateNullSink(first, "Test output A");
        pw.CreateNullSink(second, "Test output B");
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "A", MixKind.Monitor), new("monitor2", "B", MixKind.Monitor)],
            Channels = [],
        });
        mixer.SetMonitorOutputs([first, second]);
        Assert.Null(mixer.SetMonitorFeed(second, "monitor2"));
        int firstId = pw.FindNodeId(first)!.Value, secondId = pw.FindNodeId(second)!.Value;

        string[] LinksTo(int id)
        {
            using var dump = Dump();
            return [.. dump.RootElement.EnumerateArray()
                .Where(entry => entry.GetProperty("type").GetString() == "PipeWire:Interface:Link"
                    && entry.GetProperty("info").GetProperty("input-node-id").GetInt32() == id)
                .Select(entry => entry.GetProperty("info").GetProperty("props").GetProperty("object.serial").ToString())
                .Order(StringComparer.Ordinal)];
        }

        string[] previousA = LinksTo(firstId), previousB = LinksTo(secondId);
        Assert.Equal(2, previousA.Length);
        Assert.Equal(2, previousB.Length);
        mixer.SetMonitorEqPreset("monitor", "fps", _ => null);
        Assert.Equal(new MonitorEqStatus("fps", null), mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").MonitorEq);
        string[] nextA = LinksTo(firstId);
        Assert.Equal(2, nextA.Length);
        Assert.Empty(previousA.Intersect(nextA));
        Assert.Equal(previousB, LinksTo(secondId));
    }

    [DspPipeWireFact]
    public void BassPresetShapesBothSidesOfARealPipeWireFilter()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        FilterHandle eq = pw.CreateMonitorEq("eq-test", "Test monitor EQ", MonitorEqPresets.Find("bass-boost")!);
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
