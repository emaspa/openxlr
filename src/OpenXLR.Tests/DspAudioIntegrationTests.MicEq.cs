using System.Globalization;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ on a private PipeWire server: XLR 1 and XLR 2 on a fake
/// unit playing a 1 kHz tone, each with the latency fixture as an insert. The
/// EQ stage splices in at the head of XLR 1 without restarting either chain,
/// every value reaches the running stage without a new node or a helper
/// process, and the microphone keeps playing when the stage dies or cannot
/// start.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    private const double ToneAmplitude = 0.05;

    [DspPipeWireFact]
    public Task VariableBandsKeepInsertChainsAndTheSixteenthBandProcessesAudio() => OnTheUnitWithTone(false, (mixer, pw, runs) =>
    {
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        ChainGraph chain = SettledChain("xlr1");
        MicEq flat = new() { Enabled = true, Bands = Enumerable.Range(0, 16).Select(_ => new EqBand()).ToArray() };
        mixer.SetEq("xlr1", flat);
        ChainGraph eq = SettledChain("xlr1", "OpenXLR_eq_");
        double baseline = Level("xlr1");
        mixer.SetEqBand("xlr1", 16, new(Enabled: true, Frequency: 1000, Gain: 6, Q: 1));
        Assert.Equal(6, Level("xlr1") - baseline, 1);
        Assert.Equal(eq, SettledChain("xlr1", "OpenXLR_eq_"));
        mixer.SetEq("xlr1", flat with { Bands = flat.Bands.Take(3).ToArray() });
        ChainGraph smaller = SettledChain("xlr1", "OpenXLR_eq_");
        Assert.NotEqual(eq.Nodes, smaller.Nodes);
        Assert.Equal(baseline, Level("xlr1"), 1);
        Assert.Equal(chain.Nodes, SettledChain("xlr1").Nodes);
        Assert.Equal(3, mixer.EqFor("xlr1").Bands.Count);
    });

    [DspPipeWireFact]
    public Task TheEqSplicesInAtTheHeadAndEveryValueMovesLive() => OnTheUnitWithTone(compensate: false, (mixer, pw, runs) =>
    {
        Assert.True(Volatile.Read(ref runs.Count) > 0, "The helper counter sees the build's helpers.");
        mixer.SetInserts("xlr1", [Fixture("one", 100)]);
        mixer.SetInserts("xlr2", [Fixture("two", 200)]);
        ChainGraph xlr1 = SettledChain("xlr1"), xlr2 = SettledChain("xlr2");
        double flat = Level("xlr1");
        double flat2 = Level("xlr2");
        Assert.Equal(20 * Math.Log10(ToneAmplitude / Math.Sqrt(2)), flat, 1);

        // Values set while the EQ is off are only stored: no stage runs.
        mixer.SetEqBand("xlr1", 2, new(Frequency: 1000, Gain: 12, Q: 1));
        Assert.Empty(Chain("xlr1", "OpenXLR_eq_").Nodes);
        Assert.Null(Strip(mixer, "xlr1").Backend);

        // On: the stage comes in at the head and the chain behind it keeps
        // its nodes and every link but the one it is fed by.
        mixer.SetEqEnabled("xlr1", true);
        ChainGraph eq = SettledChain("xlr1", "OpenXLR_eq_");
        Assert.Single(eq.Nodes);
        AssertSpliced(xlr1, SettledChain("xlr1"), eq);
        xlr1 = SettledChain("xlr1");
        Assert.Equal(xlr2, SettledChain("xlr2"));
        Assert.Equal("software", Strip(mixer, "xlr1").Backend);
        Assert.True(Strip(mixer, "xlr1").Available);
        AssertLevel("xlr1", flat + 12);
        AssertLevel("xlr2", flat2);

        // Band, switch and value changes are live: no node, no link, no helper.
        void Live(Action change, double expected)
        {
            int before = Volatile.Read(ref runs.Count);
            change();
            Assert.Equal(before, Volatile.Read(ref runs.Count));
            AssertLevel("xlr1", expected);
            Assert.Equal(eq, SettledChain("xlr1", "OpenXLR_eq_"));
            Assert.Equal(xlr1, SettledChain("xlr1"));
        }
        Live(() => mixer.SetEqBand("xlr1", 2, new(Enabled: false)), flat);
        Live(() => mixer.SetEqBand("xlr1", 2, new(Enabled: true)), flat + 12);
        Live(() => mixer.SetEqEnabled("xlr1", false), flat);
        Assert.Equal("software", Strip(mixer, "xlr1").Backend);   // bypassed, still running
        Live(() => mixer.SetEqEnabled("xlr1", true), flat + 12);
        Live(() => mixer.SetEqBand("xlr1", 2, new(Gain: 6.1)), flat + 6);
        Assert.Equal(6, mixer.EqFor("xlr1").Bands[1].Gain);
        // A shelf is at half its gain at its frequency.
        Live(() => mixer.SetEqBand("xlr1", 2, new(Type: "lowShelf")), flat + 3);
        Live(() => mixer.SetEqBand("xlr1", 2, new(Type: "peak")), flat + 6);
        Assert.Equal(xlr2, SettledChain("xlr2"));

        // A recall that changes only EQ values starts nothing; one without
        // EQs leaves them alone; the saved settings read back change nothing.
        MixerScene scene = mixer.ExportScene();
        MicEq louder = scene.Eq!["xlr1"].WithBand(2, scene.Eq["xlr1"].Bands[1] with { Gain = 9 });
        mixer.ApplyScene(scene with { Eq = new() { ["xlr1"] = louder } });
        AssertLevel("xlr1", flat + 9);
        mixer.ApplyScene(scene with { Eq = null });
        AssertLevel("xlr1", flat + 9);
        mixer.ApplySettings(mixer.ExportSettings() with { Eq = new() { ["xlr1"] = louder, ["cap1"] = MicEq.Default } });
        Assert.True(mixer.ExportSettings().Eq.ContainsKey("cap1"), "A key for a channel that is not a microphone is kept.");
        Assert.Equal(eq, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));
        Assert.Equal(xlr2, SettledChain("xlr2"));

        // A recall that switches XLR 2's EQ on splices a stage in there alone.
        mixer.ApplyScene(scene with { Eq = new() { ["xlr2"] = louder } });
        ChainGraph eq2 = SettledChain("xlr2", "OpenXLR_eq_");
        AssertSpliced(xlr2, SettledChain("xlr2"), eq2);
        xlr2 = SettledChain("xlr2");
        AssertLevel("xlr2", flat2 + 9);
        Assert.Equal(eq, SettledChain("xlr1", "OpenXLR_eq_"));
        Assert.Equal(xlr1, SettledChain("xlr1"));

        // Sound Check loops the dry take through the EQ, which keeps running.
        mixer.SoundCheck("xlr1", "record");
        Assert.True(SpinWait.SpinUntil(() => LinksInto(eq.Nodes.Keys.Single()).Contains("OpenXLR_soundcheck_xlr1"), TimeSpan.FromSeconds(5)),
            "The loop does not feed the EQ: " + string.Join(", ", LinksInto(eq.Nodes.Keys.Single())));
        Assert.Equal(eq.Nodes, Chain("xlr1", "OpenXLR_eq_").Nodes);
        mixer.SoundCheck("xlr1", "stop");
        Assert.True(SpinWait.SpinUntil(() => LinksInto(eq.Nodes.Keys.Single()).All(n => n.Contains("Wave_XLR_BINDUNIT-", StringComparison.Ordinal))
            && LinksInto(eq.Nodes.Keys.Single()).Count > 0, TimeSpan.FromSeconds(5)));
        AssertLevel("xlr1", flat + 9);
        xlr1 = SettledChain("xlr1");
        eq = SettledChain("xlr1", "OpenXLR_eq_");

        // The helper dies: the sweep starts a new stage at the head, and the
        // chain behind it keeps running.
        KillChainHolder(eq);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            ChainGraph now = Chain("xlr1", "OpenXLR_eq_");
            return now.Nodes.Count == 1 && !now.Nodes.Keys.Intersect(eq.Nodes.Keys).Any();
        }, TimeSpan.FromSeconds(8)), "The EQ stage was not started again.");
        ChainGraph restarted = SettledChain("xlr1", "OpenXLR_eq_");
        Assert.Equal(xlr1.Nodes, SettledChain("xlr1").Nodes);
        AssertLevel("xlr1", flat + 9);

        // It dies where no helper can be started: the microphone carries on
        // without the EQ, never silent, and says why.
        pw.EqHost = "/nonexistent/openxlr-lv2-host";
        KillChainHolder(restarted);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            return Chain("xlr1", "OpenXLR_eq_").Nodes.Count == 0 && Strip(mixer, "xlr1").Backend is null;
        }, TimeSpan.FromSeconds(8)), "The dead stage was not taken out.");
        AssertLevel("xlr1", flat);
        Assert.False(Strip(mixer, "xlr1").Available);
        Assert.Equal(Mixer.EqNeedsHost, Strip(mixer, "xlr1").Error);
        Assert.True(Strip(mixer, "xlr1").Enabled, "The setting stays as the user left it.");
        Assert.Equal(xlr1.Nodes, SettledChain("xlr1").Nodes);

        // The helper is back: the sweep splices the EQ in again.
        pw.EqHost = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            return Chain("xlr1", "OpenXLR_eq_").Nodes.Count == 1;
        }, TimeSpan.FromSeconds(8)), "The EQ did not come back with its helper.");
        AssertLevel("xlr1", flat + 9);
        Assert.Null(Strip(mixer, "xlr1").Error);
        Assert.Equal(xlr1.Nodes, SettledChain("xlr1").Nodes);
    });

    [DspPipeWireFact]
    public Task WithoutTheHelperTheEqIsUnavailableAndTheMicrophoneKeepsItsPath() => OnTheUnitWithTone(compensate: true, (mixer, pw, runs) =>
    {
        // With latency compensation every insert runs in the native host.
        mixer.SetInserts("xlr2", [Fixture("two", 200)]);
        ChainGraph xlr2 = SettledChain("xlr2");
        double flat = Level("xlr2");
        mixer.SetEqBand("xlr2", 2, new(Frequency: 1000, Gain: -6, Q: 1));
        mixer.SetEqEnabled("xlr2", true);
        ChainGraph eq = SettledChain("xlr2", "OpenXLR_eq_");
        AssertSpliced(xlr2, SettledChain("xlr2"), eq);
        AssertLevel("xlr2", flat - 6);

        // A helper that is not there: no stage, the reason, and the
        // microphone on the path it had.
        pw.EqHost = "/nonexistent/openxlr-lv2-host";
        mixer.SetEqEnabled("xlr1", true);
        Assert.Empty(Chain("xlr1", "OpenXLR_eq_").Nodes);
        Assert.False(Strip(mixer, "xlr1").Available);
        Assert.Equal(Mixer.EqNeedsHost, Strip(mixer, "xlr1").Error);
        Assert.Null(Strip(mixer, "xlr1").Backend);
        AssertLevel("xlr1", 20 * Math.Log10(ToneAmplitude / Math.Sqrt(2)));
        // XLR 2's running stage keeps taking values.
        mixer.SetEqBand("xlr2", 2, new(Gain: -3));
        AssertLevel("xlr2", flat - 3);
    });

    private sealed class Counter { public int Count; }

    /// <summary>
    /// XLR 1 and XLR 2 on the fake unit, which plays a 1 kHz tone on every
    /// port, with plain settings or with latency compensation on. The counter
    /// holds how many helper processes the adapter has run.
    /// </summary>
    private static async Task OnTheUnitWithTone(bool compensate, Action<Mixer, PipeWireAdapter, Counter> test)
    {
        Assert.True(PluginCatalog.Find("lv2", ChainFixture) is not null,
            "Build native/tests/latency.lv2 (make -C native test-lv2) and add native/tests to LV2_PATH.");
        Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true so the native host sits beside the tests.");
        var runs = new Counter();
        var pw = new PipeWireAdapter(() => Interlocked.Increment(ref runs.Count));
        using var registry = pw.WatchGraph();
        (uint parent, uint unit) = MonitorVolumeIntegrationTests.LoadBindUnit(pw);
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> signal = Task.Run(() => ProcessRunner.Run("python3", ["-c", """
            import math, struct, subprocess, sys
            args = ['--format=f32', '--rate=48000', '--channels=6']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            amplitude = float(sys.argv[1])
            period = b''.join(struct.pack('<6f', *([amplitude * math.sin(2 * math.pi * i / 48)] * 6)) for i in range(48))
            p = subprocess.Popen(['pw-cat', '--playback', *args, '--properties={ node.link-group = openxlr-test-playback }', '--target', 'bind_unit_parent',
                '--properties={ node.name = "OpenXLR_bind_unit_fixture" }', '-'], stdin=subprocess.PIPE)
            try: p.communicate(period * (1000 * 150), timeout=155)
            finally:
                if p.poll() is None: p.kill()
                p.wait()
            """, ToneAmplitude.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromSeconds(160), cancel: stop.Token));
        try
        {
            using var mixer = new Mixer(pw);
            mixer.SetInputDeviceHint(["Wave_XLR_BINDUNIT-"], hardwareOutputRouting: false, modelFallback: false);
            mixer.Build(MixerConfig.Default());
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureInputFeeds();
                return mixer.Snapshot().Channels.Where(c => c.Id is "xlr1" or "xlr2").All(c => c.BindingConnected);
            }, TimeSpan.FromSeconds(5)), "XLR 1 and XLR 2 did not find the test unit.");
            mixer.SetMixLatencyCompensation(compensate);
            test(mixer, pw, runs);
        }
        finally
        {
            stop.Cancel();
            await signal;
            MonitorVolumeIntegrationTests.UnloadModule(unit);
            MonitorVolumeIntegrationTests.UnloadModule(parent);
        }
    }

    private static MicEqState Strip(Mixer mixer, string channel)
        => mixer.Snapshot().Channels.Single(c => c.Id == channel).Eq!;

    /// <summary>
    /// The chain after a splice: the same nodes, and the same links but the
    /// head's, which now comes from the EQ stage.
    /// </summary>
    private static void AssertSpliced(ChainGraph before, ChainGraph after, ChainGraph eq)
    {
        Assert.Equal(before.Nodes.OrderBy(n => n.Key), after.Nodes.OrderBy(n => n.Key));
        string[] gone = [.. before.Links.Except(after.Links)];
        string[] added = [.. after.Links.Except(before.Links)];
        Assert.NotEmpty(added);
        Assert.Equal(gone.Length, added.Length);
        Assert.All(added, serial => Assert.Contains(serial, eq.Links));
        Assert.All(gone, serial => Assert.DoesNotContain(serial, eq.Links));
    }

    /// <summary>The node names linking into <paramref name="node"/>.</summary>
    private static List<string> LinksInto(string node)
    {
        using JsonDocument dump = Dump();
        var names = new Dictionary<int, string>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
            if (item.GetProperty("type").GetString() == "PipeWire:Interface:Node"
                && item.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object
                && info.GetProperty("props").TryGetProperty("node.name", out JsonElement name))
                names[item.GetProperty("id").GetInt32()] = name.GetString() ?? "";
        int? target = names.Where(n => n.Value == node).Select(n => (int?)n.Key).FirstOrDefault();
        var from = new List<string>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
            if (item.GetProperty("type").GetString() == "PipeWire:Interface:Link"
                && item.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object
                && info.GetProperty("input-node-id").GetInt32() == target)
                from.Add(names.GetValueOrDefault(info.GetProperty("output-node-id").GetInt32(), "?"));
        return from;
    }

    /// <summary>The level of a channel's sink, its left side's RMS in dBFS over a quarter second.</summary>
    private static double Level(string channel) => NodeLevel($"OpenXLR_ch_{channel}");

    /// <summary>The level of a sink's monitor, its left side's RMS in dBFS over a quarter second.</summary>
    private static double NodeLevel(string node)
    {
        ProcessResult result = ProcessRunner.Run("python3", ["-c", """
            import math, os, struct, subprocess, sys, tempfile, time
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True): args.append('--raw')
            with tempfile.TemporaryFile() as capture:
                p = subprocess.Popen(['pw-cat', '--record', *args, '--properties={ node.link-group = openxlr-test-record }', '--target', sys.argv[1],
                    '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE)
                try:
                    deadline = time.monotonic() + 5
                    while time.monotonic() < deadline:
                        size = os.fstat(capture.fileno()).st_size // 8 * 8
                        if size >= 36000 * 8:
                            frames = struct.unpack('<24000f', os.pread(capture.fileno(), 12000 * 8, size - 12000 * 8))
                            left = frames[0::2]
                            rms = math.sqrt(sum(v * v for v in left) / len(left))
                            print(20 * math.log10(max(rms, 1e-9)))
                            break
                        if p.poll() is not None: raise AssertionError(p.communicate()[1].decode())
                        time.sleep(.02)
                    else:
                        raise AssertionError('no audio captured')
                finally:
                    if p.poll() is None: p.kill()
                    p.wait()
            """, node], TimeSpan.FromSeconds(10));
        Assert.True(result.Ok, result.StdoutText + result.Stderr);
        return double.Parse(result.StdoutText.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Wait until a channel's level is within 0.2 dB of <paramref name="expected"/>.</summary>
    private static void AssertLevel(string channel, double expected, double tolerance = 0.2)
    {
        double last = double.NaN;
        Assert.True(SpinWait.SpinUntil(() => Math.Abs((last = Level(channel)) - expected) <= tolerance, TimeSpan.FromSeconds(8)),
            $"{channel} plays at {last:0.00} dBFS, expected {expected:0.00}");
    }
}
