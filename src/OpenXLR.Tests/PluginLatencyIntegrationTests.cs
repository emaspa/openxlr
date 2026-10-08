using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>The delay stage on a private PipeWire server, with no plugin involved.</summary>
[Collection("xdg-config")]
public sealed class MixDelayIntegrationTests
{
    [MonitorPipeWireFact]
    public void CompensationWithoutPluginLatencyAddsNoStage()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels = [],
        });
        string[] before = OwnNodes(pw);
        mixer.SetMixLatencyCompensation(true);
        mixer.EnsureFilterRoutes();
        Assert.Equal(before, OwnNodes(pw));
        Assert.Null(mixer.Snapshot().MixLatencyError);
        Assert.Empty(mixer.Snapshot().MixDelayMilliseconds);
        mixer.SetMixLatencyCompensation(false);
        Assert.Equal(before, OwnNodes(pw));
    }

    [MonitorPipeWireFact]
    public void TheStereoDelayMovesAudioByTheTimeItIsSetTo()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        FilterHandle delay = pw.CreateMixDelay("test");
        try
        {
            foreach (int milliseconds in new[] { 0, 10, 125 })
            {
                pw.SetMixDelay(delay, milliseconds);
                // The left side goes back to zero as a reference on the same
                // clock; the right keeps the value the stereo setter gave it.
                pw.SetFilterControl(delay, "left:Delay (s)", 0);
                var result = ProcessRunner.Run("python3", ["-c", """
                    import struct, subprocess, sys, tempfile
                    rate, period = 48000, 12000
                    args = ['--format=f32', '--rate=48000', '--channels=2']
                    if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                    signal = b''.join(struct.pack('<ff', *([0.5 if i % period == 0 else 0.0] * 2)) for i in range(rate * 3))
                    with tempfile.TemporaryFile() as capture:
                        record = subprocess.Popen(['pw-cat', '--record', *args, '--target', sys.argv[2], '-'], stdout=capture, stderr=subprocess.PIPE)
                        play = None
                        try:
                            play = subprocess.Popen(['pw-cat', '--playback', *args, '--target', sys.argv[1], '-'], stdin=subprocess.PIPE, stderr=subprocess.PIPE)
                            _, err = play.communicate(signal, timeout=10)
                            assert play.returncode == 0, err.decode()
                            record.terminate(); record.communicate(timeout=5)
                            capture.seek(0); raw = capture.read()
                            samples = struct.unpack('<' + 'f' * (len(raw) // 4), raw)
                            peaks = [[i // 2 for i in range(c, len(samples), 2) if abs(samples[i]) > .4] for c in range(2)]
                            assert min(map(len, peaks)) >= 6, f'Not enough impulses: {peaks}'
                            expected = int(sys.argv[3]) * rate // 1000
                            # Leave out the start and the end; every impulse in
                            # between has its delayed twin in the same capture.
                            right = set(peaks[1])
                            assert all(any(p + expected + slack in right for slack in [-1, 0, 1]) for p in peaks[0][2:-2]), (expected, peaks)
                        finally:
                            for p in (play, record):
                                if p is not None:
                                    if p.poll() is None: p.kill()
                                    p.wait()
                    """, delay.SinkName, delay.SourceName, milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(20));
                Assert.True(result.Ok, result.StdoutText + result.Stderr);
            }
        }
        finally { pw.StopFilter(delay); pw.TearDown(); }
    }

    internal static string[] OwnNodes(PipeWireAdapter pw)
        => [.. pw.DumpNodes().Select(n => n.Name).Where(n => n.StartsWith("OpenXLR", StringComparison.Ordinal)).Order()];
}

/// <summary>
/// Compensation with a plugin that reports latency: the fixture in
/// native/tests/latency.lv2, run by the native host. The server and this
/// process both need it on LV2_PATH.
/// </summary>
[Collection("xdg-config")]
public sealed class PluginLatencyIntegrationTests
{
    private const string Fixture = "urn:openxlr:test:latency";

    private static void RequireFixture()
    {
        Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true so the native host sits beside the tests.");
        Assert.True(PluginCatalog.Find("lv2", Fixture) is { ReportsLatency: true },
            "Build native/tests/latency.lv2 (make -C native test-lv2) and add native/tests to LV2_PATH.");
    }

    private static InsertDefinition Delay(double samples = 480) =>
        new() { Id = "delay", Kind = "lv2", Plugin = Fixture, Params = new() { ["delay"] = samples } };

    [DspPipeWireFact]
    public void TheSwitchAndTheSavedSettingRebuildTheSameChains()
    {
        RequireFixture();
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels = [],
        });
        mixer.SetInserts("mix:chat", [Delay()]);
        Assert.False(Hosted(), "Without compensation the plugin stays in the filter chain.");
        mixer.SetMixLatencyCompensation(true);
        Assert.True(Hosted(), "The switch moves a plugin with a latency port into the native host.");
        mixer.SetMixLatencyCompensation(false);
        Assert.False(Hosted());
        mixer.ApplySettings(mixer.ExportSettings() with { CompensateMixLatency = true });
        Assert.True(Hosted(), "A saved setting rebuilds the chains the way the switch does.");
        mixer.ApplySettings(mixer.ExportSettings() with { CompensateMixLatency = false });
        Assert.False(Hosted());
        Assert.False(mixer.Snapshot().CompensateMixLatency);
        Assert.Empty(mixer.Snapshot().MixDelayMilliseconds);

        // In the native host by choice of the mixer, not because PipeWire's
        // filter chain refused the plugin and the host took it over.
        bool Hosted()
        {
            InsertStatus insert = Assert.Single(mixer.Snapshot().Inserts["mix:chat"]);
            return insert.NativeHostRunning && insert.FilterChainError is null;
        }
    }

    [DspPipeWireFact]
    public async Task TheOtherMixFollowsTheReportedLatencyBypassAndAFailedStage()
    {
        RequireFixture();
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult>? playback = null;
        try
        {
            pw.CreateNullSink("latency_test_output", "Latency test output");
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
                Channels = [new("test", "Test") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 } }],
            });
            mixer.SetMonitorOutputs(["latency_test_output"]);
            mixer.SetMixLatencyCompensation(true);
            mixer.SetInserts("mix:chat", [Delay(480)]);
            // The host reports once audio has run through the plugin.
            playback = ProcessRunner.RunAsync("python3", ["-c", """
                import subprocess, struct
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                p = subprocess.Popen(['pw-cat', '--playback', *args, '--target', 'OpenXLR_ch_test', '-'], stdin=subprocess.PIPE)
                try: p.communicate(struct.pack('<ff', .1, .1) * (48000 * 40), timeout=50)
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
                """], TimeSpan.FromSeconds(55), cancel: stop.Token);

            double first = WaitForAlignment(ms => ms > 0);
            mixer.SetInsertParam("mix:chat", "delay", "delay", 960);
            double second = WaitForAlignment(ms => ms > first);
            Assert.Equal(2 * first, second, 3);

            mixer.SetInsertBypass("mix:chat", "delay", true);
            WaitForAlignment(ms => ms == 0);
            Assert.Null(pw.FindNodeId("OpenXLR_delay_monitor_in"));
            mixer.SetInsertBypass("mix:chat", "delay", false);
            WaitForAlignment(ms => ms == second);

            // A stage that goes away is rebuilt; the plugin keeps running.
            string host = pw.DumpNodes().Single(n => n.Name.StartsWith("OpenXLR_ins_chat_", StringComparison.Ordinal)
                && n.Name.EndsWith("_stage_0", StringComparison.Ordinal)).Name;
            int? hostId = pw.FindNodeId(host);
            int stage = pw.FindNodeId("OpenXLR_delay_monitor_in") ?? throw new InvalidOperationException("no delay stage");
            pw.Run("pw-cli", "destroy", stage.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return pw.FindNodeId("OpenXLR_delay_monitor_in") is int id && id != stage;
            }, TimeSpan.FromSeconds(8)));
            WaitForAlignment(ms => ms == second);
            Assert.Equal(hostId, pw.FindNodeId(host));
            Assert.False(mixer.EnsureFilterRoutes(), "An unchanged report does not push state again.");

            mixer.SetMixLatencyCompensation(false);
            Assert.Empty(mixer.Snapshot().MixDelayMilliseconds);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_delay_monitor_in") is null, TimeSpan.FromSeconds(3)));
        }
        finally
        {
            stop.Cancel();
            if (playback is not null) await playback;
            pw.TearDown();
        }

        // Wait until the chat insert reports a figure the predicate accepts
        // and the monitor mix is delayed by exactly that much.
        double WaitForAlignment(Func<double, bool> accept)
        {
            double reported = -1;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                mixer.EnsureCellLevels();
                MixerState state = mixer.Snapshot();
                InsertStatus insert = state.Inserts["mix:chat"].Single();
                if (insert.LatencyMilliseconds is not double ms || !accept(ms) || state.MixLatencyError is not null) return false;
                reported = ms;
                return state.MixDelayMilliseconds.GetValueOrDefault("monitor", 0) == ms
                    && !state.MixDelayMilliseconds.ContainsKey("chat");
            }, TimeSpan.FromSeconds(8)), JsonSerializer.Serialize(mixer.Snapshot()));
            return reported;
        }
    }
}
