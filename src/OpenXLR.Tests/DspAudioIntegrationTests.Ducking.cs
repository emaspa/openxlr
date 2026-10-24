using System.Globalization;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    private static DuckingPlan DuckPlan => DuckingPlanTests.Plan("key", "music|monitor", "music|stream");
    private static double DuckedMusic => .1 * Math.Pow(10, -12.0 / 20);

    [DspPipeWireFact]
    public void DuckingLowersOnlyTargetSendsAndReportsReduction()
    {
        using var f = new DuckAudio();
        f.Level("monitor", .15);
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        f.Level("chat", .05);
        f.Level("key", .5);
        f.Legs(DuckPlan);
        DuckWait(() => f.Mixer.ReadMeters().TryGetValue("duck:monitor", out double[]? meter)
            && meter.Length == 1 && Math.Abs(meter[0] - 12) < .05, "The reduction meter did not reach 12 dB.");
        Assert.Equal(["monitor", "stream"], f.Mixer.DuckingStatus.RunningMixes);
        Assert.True(f.Mixer.DuckingStatus.Available);
        Assert.Null(f.Mixer.DuckingStatus.Error);

        // The virtual microphone carries the key at its original level.
        f.Signal(.5, 0, 0);
        f.Level("stream", .5);
        f.Level("key", .5);
        // Chat alone passes through the mix unchanged while the key is loud.
        f.Signal(.5, 0, .05);
        f.Level("monitor", .05);
        f.Level("chat", .05);
        f.Signal(0, 0, .05);
        f.Level("monitor", .05);
        f.Level("chat", .05);
    }

    [DspPipeWireFact]
    public void DuckingRecoversWithinTheFastReleaseWhenTheKeyStops()
    {
        using var f = new DuckAudio();
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        f.Signal(0, .1, .05);
        // Includes the 50 ms detector decay, 200 ms release and the small
        // playback and recorder buffers. No healing runs during this wait.
        f.Level("monitor", .15, timeout: TimeSpan.FromSeconds(.45));
        f.Level("chat", .05);
        f.Legs(DuckPlan);
    }

    [DspPipeWireFact]
    public void AMicrophoneKeyStopsDuckingWhileMicrophonesArePausedWithMonitoring()
    {
        // The pause mutes the microphone's channel sink. That sink's monitor
        // feeds the key and still carries the speech, so only key_open stops it.
        using var f = new DuckAudio();
        ProcessResult capture = ProcessRunner.Run("pactl", ["load-module", "module-remap-source", "source_name=test_duck_mic",
            "master=test_duck_source.monitor", "channels=1", "channel_map=mono", "master_channel_map=aux0"]);
        Assert.True(capture.Ok, capture.Stderr);
        try
        {
            DuckWait(() => f.Pw.ListDevices().Any(d => d.Name == "test_duck_mic"), "The microphone fixture did not appear.");
            f.Mixer.CreateCaptureChannel("Talk", "test_duck_mic", 0, _ => null, monoChannel: 0);
            Assert.True(f.Mixer.Config.Channels.Single(c => c.Id == "talk").Microphone);
            // A new channel starts muted in every mix; its monitor send makes it
            // an audible key. The stream then carries only the ducked music.
            f.Mixer.SetChannelMuted("talk", "monitor", false);
            f.Mixer.SetChannelMuted("key", "stream", true);
            f.Signal(.5, .1, 0);
            f.Mixer.SetDuckingPlan(DuckPlan with { Key = "talk" });
            f.Level("stream", DuckedMusic);
            f.Mixer.SetMonitorMuteMicrophones(true);
            f.Level("stream", DuckedMusic);   // monitoring still runs
            f.Mixer.SetMonitorMuted(true);
            Assert.True(f.Pw.GetSinkMuted("OpenXLR_ch_talk"));
            f.Level("stream", .1);
            f.Mixer.SetMonitorMuted(false);
            Assert.False(f.Pw.GetSinkMuted("OpenXLR_ch_talk"));
            f.Level("stream", DuckedMusic);
            f.Mixer.DeleteApplicationChannel("talk", _ => null);
        }
        finally { ProcessRunner.Run("pactl", ["unload-module", capture.StdoutText.Trim()]); }
    }

    [DspPipeWireFact]
    public void RemovingDuckingRestoresDirectLevelsAndRemovesEveryBusAndStage()
    {
        using var f = new DuckAudio();
        var channels = DuckNodes("OpenXLR_ch_");
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        f.Mixer.SetDuckingPlan(DuckPlan with { Cells = new HashSet<string>() });
        f.Level("monitor", .15);
        f.Legs(null);
        DuckWait(() => DuckNodes("OpenXLR_duck_").Count == 0
            && !f.Pw.OwnSinkLevels().Any(s => s.Name.EndsWith(".duck", StringComparison.Ordinal)), "Duck nodes survived removal.");
        Assert.Empty(f.Mixer.DuckingStatus.RunningMixes);
        Assert.DoesNotContain(f.Mixer.ReadMeters().Keys, k => k.StartsWith("duck:", StringComparison.Ordinal));
        Assert.Equal(channels.OrderBy(n => n.Key), DuckNodes("OpenXLR_ch_").OrderBy(n => n.Key));
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        f.Mixer.SetDuckingPlan(null);
        f.Level("monitor", .15);
        Assert.Empty(DuckNodes("OpenXLR_duck_"));
    }

    [DspPipeWireFact]
    public void ADeadDuckStageBypassesOnOneSweepAndRestartsWithoutTouchingOtherMixes()
    {
        using var f = new DuckAudio();
        f.Signal(.5, .1, 0);
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic);
        string stream = DuckNodes("OpenXLR_duck_stream").Single().Value;
        for (int failure = 0; failure < 3; failure++)
        {
            KillDuckStage("monitor");
            Assert.True(f.Mixer.EnsureFilterRoutes());
            f.Level("monitor", .1);
            Assert.DoesNotContain("monitor", f.Mixer.DuckingStatus.RunningMixes);
            Assert.DoesNotContain("duck:monitor", f.Mixer.ReadMeters().Keys);
            if (failure < 2)
            {
                Assert.True(f.Mixer.EnsureFilterRoutes());
                f.Level("monitor", DuckedMusic);
            }
        }
        Assert.False(f.Mixer.DuckingStatus.Available);
        Assert.Contains("kept failing", f.Mixer.DuckingStatus.Error);
        f.Mixer.EnsureFilterRoutes();
        f.Level("monitor", .1);
        Assert.Equal(stream, DuckNodes("OpenXLR_duck_stream").Single().Value);
        f.Mixer.SetDuckingPlan(DuckPlan with { Enabled = false });
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic);
        Assert.True(f.Mixer.DuckingStatus.Available);
    }

    [DspPipeWireFact]
    public void ActivatingDuckingNeverDoublesTheTargetSignal()
    {
        using var f = new DuckAudio();
        f.Level("monitor", .15);
        long start = f.Frames("monitor");
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        float[] audio = f.Audio("monitor", start);
        Assert.NotEmpty(audio);
        Assert.True(audio.Max(Math.Abs) <= .15 * 1.05, $"Activation peak: {audio.Max(Math.Abs)}");
    }

    [DspPipeWireFact]
    public void DuckedAndDirectStepsArriveWithinTheSameGraphQuantum()
    {
        using var f = new DuckAudio();
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        // Both steps originate in the same six-channel stream and frame.
        // Music is left only, chat right only, so the two paths can be timed
        // separately on one recording. The key remains at a fixed level.
        f.Signal(.5, 0, 0);
        f.Level("monitor", 0);
        long start = f.Frames("monitor");
        f.Signal(.5, .1, .05, split: true);
        f.Level("monitor", DuckedMusic, right: .05);
        float[] audio = f.Audio("monitor", start);
        int left = Array.FindIndex(audio.Where((_, i) => i % 2 == 0).ToArray(), v => v > DuckedMusic / 2);
        int right = Array.FindIndex(audio.Where((_, i) => i % 2 == 1).ToArray(), v => v > .025);
        Assert.True(left >= 0 && right >= 0, "Both steps must be captured.");
        ProcessResult settings = ProcessRunner.Run("pw-metadata", ["-n", "settings"]);
        Assert.True(settings.Ok, settings.Stderr);
        var quantum = System.Text.RegularExpressions.Regex.Match(settings.StdoutText, @"key:'clock.quantum' value:'(\d+)'");
        Assert.True(quantum.Success, settings.StdoutText);
        int frames = int.Parse(quantum.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(left - right), 0, frames - 1);
    }

    [DspPipeWireFact]
    public void DuckControlsKeyAndTargetsChangeWithoutReloadingCombinesOrOtherStages()
    {
        using var f = new DuckAudio();
        var channels = DuckNodes("OpenXLR_ch_");
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        var stages = DuckNodes("OpenXLR_duck_");
        int helpers = f.HelperRuns;
        f.Mixer.SetDuckingPlan(DuckPlan with { DepthDb = 6, AttackMs = 80, ReleaseMs = 300 });
        Assert.Equal(helpers, f.HelperRuns);
        f.Level("monitor", .1 * Math.Pow(10, -6.0 / 20) + .05);
        f.Mixer.SetDuckingPlan(DuckPlan with { Enabled = false });
        Assert.Equal(helpers, f.HelperRuns);
        f.Level("monitor", .15);
        f.Mixer.SetDuckingPlan(DuckPlan);
        Assert.Equal(helpers, f.HelperRuns);
        f.Level("monitor", DuckedMusic + .05);
        f.Mixer.SetChannelMuted("key", "stream", true);
        f.Level("monitor", .15);
        f.Mixer.SetChannelMuted("key", "stream", false);
        f.Level("monitor", DuckedMusic + .05);
        Assert.Equal(stages.OrderBy(n => n.Key), DuckNodes("OpenXLR_duck_").OrderBy(n => n.Key));
        f.Mixer.SetDuckingPlan(DuckPlan with { Key = "chat" });
        f.Level("monitor", .15);   // the new key is below the threshold
        Assert.All(stages.Keys, stage => Assert.Contains("OpenXLR_ch_chat", LinksInto(stage)));
        f.Mixer.SetDuckingPlan(DuckPlan with { Cells = new HashSet<string> { "chat|monitor", "music|stream" } });
        f.Level("monitor", .1 + .05 * Math.Pow(10, -12.0 / 20));
        f.Mixer.SetDuckingPlan(DuckPlan with { Cells = new HashSet<string> { "music|stream" } });
        f.Level("monitor", .15);
        Assert.Equal(stages["OpenXLR_duck_stream"], DuckNodes("OpenXLR_duck_stream").Single().Value);
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        Assert.Equal(stages["OpenXLR_duck_stream"], DuckNodes("OpenXLR_duck_stream").Single().Value);
        Assert.NotEqual(stages["OpenXLR_duck_monitor"], DuckNodes("OpenXLR_duck_monitor").Single().Value);
        Assert.Equal(channels.OrderBy(n => n.Key), DuckNodes("OpenXLR_ch_").OrderBy(n => n.Key));
    }

    [DspPipeWireFact]
    public void DuckBusesSurviveLayoutEditsAndTheStoredPlanReturnsAfterBuild()
    {
        using var f = new DuckAudio(probes: false);
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        f.Mixer.CreateApplicationChannel("Fresh", _ => null);
        f.Legs(DuckPlan);
        ProcessResult capture = ProcessRunner.Run("pactl", ["load-module", "module-remap-source", "source_name=test_duck_capture",
            "master=test_duck_source.monitor", "channels=1", "channel_map=mono", "master_channel_map=aux0"]);
        Assert.True(capture.Ok, capture.Stderr);
        try
        {
            DuckWait(() => f.Pw.ListDevices().Any(d => d.Name == "test_duck_capture"), "The capture fixture did not appear.");
            f.Mixer.CreateCaptureChannel("Captured", "test_duck_capture", 0, _ => null, monoChannel: 0);
            f.Legs(DuckPlan);
            f.Level("monitor", DuckedMusic + .05);
            f.Mixer.DeleteApplicationChannel("captured", _ => null);
        }
        finally { ProcessRunner.Run("pactl", ["unload-module", capture.StdoutText.Trim()]); }
        f.Mixer.RenameApplicationChannel("music", "Songs", _ => null);
        f.LinkSignals();
        f.Legs(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        Assert.Throws<IOException>(() => f.Mixer.DeleteApplicationChannel("music", _ => "disk full"));
        Assert.Throws<IOException>(() => f.Mixer.DeleteMix("stream", _ => "disk full"));
        f.Legs(DuckPlan);
        f.Mixer.DeleteApplicationChannel("fresh", _ => null);
        f.Mixer.DeleteMix("stream", _ => null);
        f.Level("monitor", .15);   // the key's only open send was removed
        Assert.Equal(["monitor"], f.Mixer.DuckingStatus.RunningMixes);
        Assert.DoesNotContain(f.Pw.OwnSinkLevels(), s => s.Name == "OpenXLR_mix_stream.duck");
        f.Mixer.SetChannelMuted("key", "monitor", false);
        f.Level("monitor", .5 + DuckedMusic + .05);
        f.Mixer.Build(DuckAudio.Config);
        f.Mixer.SetMonitorOutputs([DuckAudio.Output]);
        f.LinkSignals();
        // The stream mix is back, but the stored plan lost its cell when the
        // mix was deleted. The sweep settles the new legs first: the session
        // manager's restore can land on a new leg after the build set it.
        f.Legs(DuckPlan with { Cells = new HashSet<string> { "music|monitor" } });
        f.Level("monitor", DuckedMusic + .05);
        Assert.Equal(["monitor"], f.Mixer.DuckingStatus.RunningMixes);
        f.Mixer.DeleteApplicationChannel("key", _ => null);
        f.Level("monitor", .15);
        Assert.Empty(DuckNodes("OpenXLR_duck_"));
        Assert.DoesNotContain(f.Pw.OwnSinkLevels(), s => s.Name.EndsWith(".duck", StringComparison.Ordinal));
    }

    [DspPipeWireFact]
    public void TheDuckSweepRepairsLinksAndHoldsBothLegPaths()
    {
        using var f = new DuckAudio();
        f.Mixer.SetDuckingPlan(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        var stages = DuckNodes("OpenXLR_duck_");
        foreach ((string from, string to) in new[]
        {
            ("OpenXLR_ch_key:monitor_FL", "OpenXLR_duck_monitor:key_0"),
            ("OpenXLR_mix_monitor.duck:monitor_FL", "OpenXLR_duck_monitor:playback_0"),
            ("OpenXLR_duck_monitor:capture_1", "OpenXLR_mix_monitor:playback_FR"),
        })
        {
            ProcessResult cut = ProcessRunner.Run("pw-link", ["-d", from, to]);
            Assert.True(cut.Ok, cut.Stderr);
            DuckWait(() => f.Mixer.EnsureFilterRoutes(), "The missing duck link was not repaired.");
            f.Level("monitor", DuckedMusic + .05);
        }
        foreach (string name in new[] { "OpenXLR_mix_monitor", "OpenXLR_mix_monitor.duck" })
        {
            int index = f.ChannelLegs("music")[name];
            f.Pw.SetSinkInputVolume(index, .37);
            f.Pw.SetSinkInputMuted(index, true);
        }
        f.Legs(DuckPlan);
        f.Level("monitor", DuckedMusic + .05);
        Assert.Equal(stages.OrderBy(n => n.Key), DuckNodes("OpenXLR_duck_").OrderBy(n => n.Key));
    }

    private static Dictionary<string, string> DuckNodes(string prefix)
    {
        using JsonDocument dump = Dump();
        return dump.RootElement.EnumerateArray().Where(o => o.GetProperty("type").GetString() == "PipeWire:Interface:Node"
            && o.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object)
            .Select(o => o.GetProperty("info").GetProperty("props"))
            .Where(p => p.TryGetProperty("node.name", out JsonElement name) && name.GetString()!.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.GetProperty("node.name").GetString()!, p => p.GetProperty("object.serial").ToString());
    }

    private static void KillDuckStage(string mix)
    {
        using JsonDocument dump = Dump();
        JsonElement node = dump.RootElement.EnumerateArray().Single(o => o.GetProperty("type").GetString() == "PipeWire:Interface:Node"
            && o.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object
            && info.GetProperty("props").TryGetProperty("node.name", out JsonElement name) && name.GetString() == "OpenXLR_duck_" + mix);
        int client = node.GetProperty("info").GetProperty("props").GetProperty("client.id").GetInt32();
        KillChainHolder(new ChainGraph(new Dictionary<string, string>(), [], [client]));
    }

    private static void DuckWait(Func<bool> test, string message)
        => Assert.True(SpinWait.SpinUntil(test, TimeSpan.FromSeconds(5)), message);

    /// <summary>
    /// Constant signals on one six-channel stream, split into key, music
    /// and chat. Continuous recordings measure changes while C# edits the
    /// graph, including peaks that a new recorder would miss.
    /// </summary>
    private sealed class DuckAudio : IDisposable
    {
        public const string Output = "test_duck_output";
        private const string Source = "test_duck_source";
        public static MixerConfig Config => new()
        {
            Channels = [new("key", "Key") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["stream"] = 1 },
                    MutedIn = new HashSet<string> { "monitor" } },
                new("music", "Music") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["stream"] = 1 } },
                new("chat", "Chat") { Levels = new Dictionary<string, double> { ["monitor"] = 1, ["stream"] = 1 } }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
        };
        public PipeWireAdapter Pw { get; }
        private int _helpers;
        public int HelperRuns => Volatile.Read(ref _helpers);
        public Mixer Mixer { get; }
        private readonly PipeWireAdapter _outputs = new();
        private readonly IDisposable? _registry;
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-duck-audio-").FullName;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<ProcessResult>? _signal;
        private readonly uint? _sourceModule;

        public DuckAudio(bool probes = true)
        {
            Pw = new(() => Interlocked.Increment(ref _helpers));
            Mixer = new(Pw);
            try
            {
                Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true.");
                _registry = Pw.WatchGraph();
                _outputs.CreateNullSink(Output, "Duck test output");
                ProcessResult source = ProcessRunner.Run("pactl", ["load-module", "module-null-sink", "sink_name=" + Source,
                    "channels=6", "channel_map=aux0,aux1,aux2,aux3,aux4,aux5",
                    "sink_properties=\"node.description='Duck test source' node.suspend-on-idle=false\""]);
                Assert.True(source.Ok, source.Stderr);
                _sourceModule = uint.Parse(source.StdoutText.Trim(), CultureInfo.InvariantCulture);
                Mixer.SetInputDeviceHint([], hardwareOutputRouting: false, modelFallback: false);
                Mixer.Build(Config);
                Assert.Empty(DuckNodes("OpenXLR_duck_"));
                Assert.DoesNotContain(Pw.OwnSinkLevels(), s => s.Name.EndsWith(".duck", StringComparison.Ordinal));
                Mixer.SetMonitorOutputs([Output]);
                LinkSignals();
                Signal(.5, .1, .05);
                _signal = ProcessRunner.RunAsync("python3", ["-c", """
                    import os, pathlib, struct, subprocess, sys
                    root = pathlib.Path(sys.argv[1])
                    args = ['--format=f32', '--rate=48000', '--latency=256']
                    if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
                    processes, files = [], []
                    try:
                        recordings = [('monitor', 'test_duck_output', True)]
                        if sys.argv[2] == '1':
                            recordings += [('stream', 'OpenXLR_stream', False), ('chat', 'OpenXLR_ch_chat', True), ('key', 'OpenXLR_ch_key', True)]
                        for name, target, sink in recordings:
                            capture = open(root / (name + '.raw'), 'wb', buffering=0)
                            files.append(capture)
                            extra = 'stream.capture.sink = true ' if sink else ''
                            processes.append(subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record ' + extra + '}',
                                '--channels=2', '--target', target, '-'], stdout=capture))
                        play = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }',
                            '--channels=6', '--channel-map=AUX0,AUX1,AUX2,AUX3,AUX4,AUX5', '--target', 'test_duck_source', '-'],
                            stdin=subprocess.PIPE, bufsize=0)
                        processes.append(play)
                        while True:
                            values = [float(v) for v in (root / 'signal').read_text().split()]
                            data = struct.pack('<6f', *values) * 256
                            view = memoryview(data)
                            while view:
                                view = view[os.write(play.stdin.fileno(), view):]
                            assert all(p.poll() is None for p in processes), 'an audio helper exited'
                    finally:
                        for process in reversed(processes):
                            if process.poll() is None: process.kill()
                            process.wait()
                        for capture in files: capture.close()
                    """, _dir, probes ? "1" : "0"], TimeSpan.FromSeconds(180), cancel: _stop.Token);
                Legs(null);   // every send on its fader before the first reading, as the daemon's sweep does
                Level("monitor", .15);
            }
            catch { Dispose(); throw; }
        }

        public void Signal(double key, double music, double chat, bool split = false)
            => OpenXlrPaths.WriteAtomic(Path.Combine(_dir, "signal"), string.Join(' ',
                new[] { key, key, music, split ? 0 : music, split ? 0 : chat, chat }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));

        public void LinkSignals()
        {
            foreach ((string channel, int pair) in new[] { ("key", 0), ("music", 1), ("chat", 2) })
                DuckWait(() => Pw.LinkNodes(Source, "monitor", "OpenXLR_ch_" + channel, "playback", fromPairOffset: pair)
                    is { Complete: true, Pairs.Count: 2 }, "Could not link " + channel);
            DuckWait(() => { Mixer.EnsureCellLevels(); return Pw.CombineLegLevels().Count >= 6; }, "Combine legs did not appear.");
        }

        public long Frames(string name)
        {
            string path = Path.Combine(_dir, name + ".raw");
            return File.Exists(path) ? new FileInfo(path).Length / 8 : 0;
        }

        public float[] Audio(string name, long start)
        {
            string path = Path.Combine(_dir, name + ".raw");
            if (!File.Exists(path)) return [];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int bytes = checked((int)Math.Max(0, file.Length / 8 * 8 - start * 8));
            var raw = new byte[bytes];
            file.Position = start * 8;
            file.ReadExactly(raw);
            var samples = new float[bytes / 4];
            Buffer.BlockCopy(raw, 0, samples, 0, bytes);
            return samples;
        }

        public void Level(string name, double left, double? right = null, TimeSpan? timeout = null)
        {
            double last = double.NaN;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Assert.False(_signal?.IsCompleted == true, _signal?.IsCompleted == true ? _signal.Result.Stderr : "");
                long frames = Frames(name);
                if (frames < 512) return false;
                float[] audio = Audio(name, frames - 512);
                last = audio.Where((_, i) => i % 2 == 0).Average(v => (double)v);
                return audio.Select((v, i) => Math.Abs(v - (i % 2 == 0 ? left : right ?? left)))
                    .All(error => error <= Math.Max(.0001, Math.Max(Math.Abs(left), Math.Abs(right ?? left)) * .02));
            }, timeout ?? TimeSpan.FromSeconds(5)), $"{name}: expected {left}, last left mean {last}");
        }

        public IReadOnlyDictionary<string, int> ChannelLegs(string channel)
        {
            ProcessResult modules = ProcessRunner.Run("pactl", ["list", "modules", "short"]);
            Assert.True(modules.Ok, modules.Stderr);
            string row = modules.StdoutText.Split('\n').Single(s => s.Contains("sink_name=OpenXLR_ch_" + channel + " ", StringComparison.Ordinal));
            return Pw.FindCombineLegs(uint.Parse(row.Split('\t')[0], CultureInfo.InvariantCulture));
        }

        public void Legs(DuckingPlan? plan)
        {
            DuckWait(() =>
            {
                Mixer.EnsureCellLevels();
                var levels = Pw.CombineLegLevels();
                foreach (ChannelStatus channel in Mixer.Snapshot().Channels)
                {
                    var legs = ChannelLegs(channel.Id);
                    foreach (MixDefinition mix in Mixer.Config.Mixes)
                    {
                        bool ducked = plan?.Cells.Contains(channel.Id + "|" + mix.Id) == true;
                        bool muted = channel.MutedIn.Contains(mix.Id);
                        if (!legs.TryGetValue(mix.SinkName, out int direct) || !levels.TryGetValue(direct, out var directLevel)
                            || directLevel != (ducked ? 0 : 100, ducked || muted)) return false;
                        if (legs.TryGetValue(mix.DuckBusName, out int bus)
                            && (!levels.TryGetValue(bus, out var busLevel) || busLevel != (ducked ? 100 : 0, !ducked || muted))) return false;
                    }
                }
                return true;
            }, "Direct or bus leg levels did not match the plan.");
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _signal?.GetAwaiter().GetResult(); }
            finally
            {
                Mixer.Dispose();
                _registry?.Dispose();
                _outputs.TearDown();
                if (_sourceModule is uint source) ProcessRunner.Run("pactl", ["unload-module", source.ToString(CultureInfo.InvariantCulture)]);
                _stop.Dispose();
                Directory.Delete(_dir, true);
            }
        }
    }
}
