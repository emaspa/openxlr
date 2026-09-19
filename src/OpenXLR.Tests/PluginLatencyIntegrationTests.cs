using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PluginLatencyIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task LiveLv2LatencyChangesBypassAndProcessFailureRecalculateTheOtherMixes()
    {
        string? previous = Environment.GetEnvironmentVariable("LV2_PATH");
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "native")))
            root = Directory.GetParent(root)?.FullName ?? throw new IOException("Repository not found");
        Environment.SetEnvironmentVariable("LV2_PATH", Path.Combine(root, "native", "tests"));
        using var stop = new CancellationTokenSource();
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        Task<ProcessResult>? playback = null;
        try
        {
            Assert.True(File.Exists(Path.Combine(root, "native", "tests", "latency.lv2", "latency.so")));
            Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == "urn:openxlr:test:latency" && p.ReportsLatency == true);
            pw.CreateNullSink("latency_test_output", "Latency test output");
            mixer.Build(new MixerConfig
            {
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
                Channels = [new("test", "Test") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 } }],
            });
            mixer.SetMonitorOutputs(["latency_test_output"]);
            mixer.SetMixLatencyCompensation(true);
            mixer.SetInserts("mix:chat", [new() { Id = "delay", Kind = "lv2", Plugin = "urn:openxlr:test:latency", Params = new() { ["delay"] = 480 } }]);
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
            WaitForDelay(10);
            var liveHost = pw.DumpNodes().Single(n => n.Name.StartsWith("OpenXLR_ins_chat_", StringComparison.Ordinal) && n.Name.EndsWith("_stage_0", StringComparison.Ordinal));
            mixer.SetInsertParam("mix:chat", "delay", "delay", 960);
            string commands = Directory.CreateTempSubdirectory("openxlr-delay-control-").FullName;
            string? originalPath = Environment.GetEnvironmentVariable("PATH");
            string? originalCli = Environment.GetEnvironmentVariable("OPENXLR_TEST_REAL_CLI");
            string realCli = originalPath!.Split(Path.PathSeparator).Select(dir => Path.Combine(dir, "pw-cli")).First(File.Exists);
            try
            {
                // Fail only delay control writes. The plugin host, graph and
                // other PipeWire commands stay alive throughout the fault.
                ExecutableScript.Write(Path.Combine(commands, "pw-cli"), """
                    case "$*" in *'left:Delay (s)'*) echo 'transient delay write failure' >&2; exit 1;; esac
                    exec "$OPENXLR_TEST_REAL_CLI" "$@"
                    """);
                Environment.SetEnvironmentVariable("OPENXLR_TEST_REAL_CLI", realCli);
                Environment.SetEnvironmentVariable("PATH", commands + Path.PathSeparator + originalPath);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureFilterRoutes();
                    return mixer.Snapshot().MixLatencyError?.Contains("transient delay write failure", StringComparison.Ordinal) == true;
                }, TimeSpan.FromSeconds(5)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Environment.SetEnvironmentVariable("OPENXLR_TEST_REAL_CLI", originalCli);
                Directory.Delete(commands, true);
            }
            WaitForDelay(20); // A temporary write error must heal without toggling the option.
            Assert.Equal(liveHost.Id, pw.FindNodeId(liveHost.Name));

            mixer.SetInsertBypass("mix:chat", "delay", true);
            WaitForDelay(0);
            mixer.SetInsertBypass("mix:chat", "delay", false);
            WaitForDelay(20);
            // Destroy a compensation stage while the inserts keep processing.
            int? hostBeforeFailure = pw.FindNodeId(liveHost.Name);
            var failed = pw.DumpNodes().Single(n => n.Name == "OpenXLR_delay_monitor_in");
            pw.Run("pw-cli", "destroy", failed.Id.ToString());
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return pw.FindNodeId("OpenXLR_delay_monitor_in") is int id && id != failed.Id;
            }, TimeSpan.FromSeconds(8)));
            WaitForDelay(20);
            Assert.Null(mixer.Snapshot().MixLatencyError);
            Assert.Equal(hostBeforeFailure, pw.FindNodeId(liveHost.Name));
            Assert.False(mixer.EnsureFilterRoutes()); // An unchanged report does not keep broadcasting.
        }
        finally
        {
            stop.Cancel();
            if (playback is not null) await playback;
            Environment.SetEnvironmentVariable("LV2_PATH", previous);
            PluginCatalog.Refresh();
        }

        void WaitForDelay(double value)
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                mixer.EnsureCellLevels();
                var state = mixer.Snapshot();
                return state.MixLatencyError is null && state.MixDelayMilliseconds.GetValueOrDefault("monitor", -1) == value;
            }, TimeSpan.FromSeconds(8)), System.Text.Json.JsonSerializer.Serialize(mixer.Snapshot()));
            var status = mixer.Snapshot().Inserts["mix:chat"].Single();
            Assert.Null(status.Error);
            Assert.Equal(value, status.LatencyMilliseconds);
            Assert.Equal(0, mixer.Snapshot().MixDelayMilliseconds["chat"]);
        }
    }

    [MonitorPipeWireFact]
    public void MixDelaysStayInternalAndAreRemovedWhenDisabledOrDeleted()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels = [],
        });
        mixer.SetMixLatencyCompensation(true);
        mixer.EnsureFilterRoutes();
        Assert.Null(mixer.Snapshot().MixLatencyError);
        Assert.Equal(2, mixer.Snapshot().MixDelayMilliseconds.Count);
        Assert.All(mixer.Snapshot().MixDelayMilliseconds.Values, v => Assert.Equal(0, v));
        Assert.DoesNotContain(pw.ListDevices(), n => n.Name.StartsWith("OpenXLR_delay_", StringComparison.Ordinal));
        mixer.DeleteVirtualMix("chat", _ => null);
        Assert.Single(mixer.Snapshot().MixDelayMilliseconds);
        Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_delay_chat_in") is null, TimeSpan.FromSeconds(3)));
        mixer.SetMixLatencyCompensation(false);
        Assert.Empty(mixer.Snapshot().MixDelayMilliseconds);
        Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId("OpenXLR_delay_monitor_in") is null, TimeSpan.FromSeconds(3)));
    }

    [MonitorPipeWireFact]
    public void TheStereoDelayMovesRealAudioByTheRequestedNumberOfSamples()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        FilterHandle delay = pw.CreateMixDelay("test");
        try
        {
            foreach (int milliseconds in new[] { 0, 10, 125 })
            {
                pw.SetMixDelay(delay, milliseconds);
                // Keep one channel as a same-clock reference. The other retains
                // the value set by the actual stereo compensation command.
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
                            # Ignore startup and shutdown. Every interior reference
                            # impulse has its delayed counterpart in the same capture.
                            right = set(peaks[1])
                            assert all(any(p + expected + tolerance in right for tolerance in [-1, 0, 1]) for p in peaks[0][2:-2]), (expected, peaks)
                            print('Measured delay:', expected, 'samples')
                        finally:
                            for p in (play, record):
                                if p is not None:
                                    if p.poll() is None: p.kill()
                                    p.wait()
                    """, delay.SinkName, delay.SourceName, milliseconds.ToString()], TimeSpan.FromSeconds(20));
                Assert.True(result.Ok, result.StdoutText + result.Stderr);
            }
        }
        finally { pw.StopFilter(delay); pw.TearDown(); }
    }
}
