using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ChannelInsertIntegrationTests
{
    private const string Plugin = "urn:openxlr:test:gain";
    private static InsertDefinition Gain(double value) => new()
    {
        Id = "gain", Kind = "lv2", Plugin = Plugin,
        Params = new() { ["gain"] = value },
    };

    [MonitorPipeWireFact]
    public async Task SoftwareAndCaptureChainsProcessBothSidesWithoutReplacingThePublicSink()
    {
        Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == Plugin);
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("effects_output", "Test output");
        pw.CreateNullSink("effects_capture_signal", "Capture signal");
        pw.CreateVirtualMic("effects_capture", "effects_capture_signal.monitor", "External microphone");
        mixer.Build(new MixerConfig
        {
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels = [new("software", "Software") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 } }],
        });
        mixer.SetMonitorOutputs(["effects_output"]);
        int? identity = pw.FindNodeId("OpenXLR_ch_software");
        Assert.NotNull(identity);
        using var stop = new CancellationTokenSource();
        var playing = Play("OpenXLR_ch_software", stop.Token);
        try
        {
            AssertSound(1);
            mixer.SetInserts("software", [Gain(.5)]);
            string? error = Assert.Single(mixer.Snapshot().Inserts["software"]).Error;
            Assert.True(error is null, error);
            AssertSound(.5);
            Assert.Equal(identity, pw.FindNodeId("OpenXLR_ch_software"));
            Assert.DoesNotContain(pw.ListDevices(), d => d.Name.StartsWith("OpenXLR_bus_", StringComparison.Ordinal));
            var failed = pw.DumpNodes().First(n => n.Name.StartsWith("OpenXLR_ins_channel_software_", StringComparison.Ordinal));
            pw.Run("pw-cli", "destroy", failed.Id.ToString());
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureFilterRoutes();
                return pw.FindNodeId(failed.Name) is int node && node != failed.Id;
            }, TimeSpan.FromSeconds(5)));
            AssertSound(.5);
            mixer.SetInsertParam("software", "gain", "gain", .25);
            AssertSound(.25);
            mixer.SetInsertBypass("software", "gain", true);
            AssertSound(1);
            mixer.SetInsertBypass("software", "gain", false);
            AssertSound(.25);
            // Losing the Wave interface must not remove unrelated software chains.
            mixer.SetInputDeviceHint("absent-interface");
            AssertSound(.25);
            mixer.ApplyScene(mixer.ExportScene());
            AssertSound(.25);
            Assert.Equal(identity, pw.FindNodeId("OpenXLR_ch_software"));
            mixer.SetInserts("mix:monitor", [Gain(.5)]);
            mixer.SetInserts("mix:chat", [Gain(.5)]);
            AssertSound(.125);
            var removed = mixer.RemovePluginInserts(new HashSet<(string, string)> { ("lv2", Plugin) }, _ => null);
            Assert.Null(removed.Error);
            Assert.Equal(3, removed.Chains);
            AssertSound(1);
            Assert.True(SpinWait.SpinUntil(() => !pw.DumpNodes().Any(n => n.Name.StartsWith("OpenXLR_ins_", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)));
            mixer.RenameApplicationChannel("software", "Renamed", _ => null);
            AssertSound(1);
        }
        finally { stop.Cancel(); await playing; }

        Assert.Throws<IOException>(() => mixer.CreateApplicationChannel("Failed creation", _ => "disk full"));
        Assert.DoesNotContain(mixer.Config.Channels, c => c.Name == "Failed creation");
        Assert.True(SpinWait.SpinUntil(() => !pw.DumpNodes().Any(n => n.Name.Contains("failed-creation", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)));
        mixer.CreateCaptureChannel("External", "effects_capture", 0, _ => null);
        string capture = mixer.Config.Channels.Single(c => c.CaptureSource is not null).Id;
        mixer.SetChannelMuted(capture, "monitor", false);
        mixer.SetInserts(capture, [Gain(.5)]);
        using var captureStop = new CancellationTokenSource();
        playing = Play("effects_capture_signal", captureStop.Token);
        try
        {
            AssertSound(.5);
            Assert.Throws<IOException>(() => mixer.DeleteApplicationChannel(capture, _ => "disk full"));
            Assert.Single(mixer.ExportSettings().Inserts[capture]);
            AssertSound(.5);
            MixerSettings? saved = null;
            mixer.DeleteApplicationChannel(capture, settings => { saved = settings; return null; });
            Assert.False(saved!.Inserts.ContainsKey(capture));
            AssertSound(0);
            Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId($"OpenXLR_ch_{capture}") is null
                && pw.FindNodeId($"OpenXLR_bus_{capture}") is null, TimeSpan.FromSeconds(3)));
        }
        finally { captureStop.Cancel(); await playing; }

        void AssertSound(double gain)
        {
            mixer.EnsureCellLevels();
            var result = ProcessRunner.Run("python3", ["-c", """
                import struct, subprocess, sys, tempfile, time
                args = ['--format=f32', '--rate=48000', '--channels=2']
                if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                with tempfile.TemporaryFile() as f:
                    p = subprocess.Popen(['pw-cat', '--record', *args, '--target', 'effects_output', '--properties={ stream.capture.sink = true }', '-'], stdout=f, stderr=subprocess.PIPE)
                    try:
                        time.sleep(.6); p.terminate(); _, err = p.communicate(timeout=5)
                        f.seek(0); raw = f.read()
                        values = struct.unpack('<' + 'f' * (len(raw)//4), raw)
                        assert len(values) > 16000, (len(values), err.decode())
                        tail = values[-8000:]
                        gain = float(sys.argv[1])
                        for channel, level in enumerate([.2, .4]):
                            samples = tail[channel::2]
                            matching = sum(abs(v - level * gain) < .01 for v in samples) / len(samples)
                            assert matching > .99, (channel, gain, min(samples), max(samples), matching)
                    finally:
                        if p.poll() is None: p.kill()
                        p.wait()
                """, gain.ToString(System.Globalization.CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(10));
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        }
    }

    private static Task<ProcessResult> Play(string target, CancellationToken cancel)
        => ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess, struct, sys
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--target', sys.argv[1], '-'], stdin=subprocess.PIPE)
            try: p.communicate(struct.pack('<ff', .2, .4) * (48000 * 60), timeout=65)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, target], TimeSpan.FromSeconds(70), cancel: cancel);
}
