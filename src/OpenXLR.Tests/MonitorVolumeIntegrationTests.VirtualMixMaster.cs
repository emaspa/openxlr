using System.Diagnostics;
using System.Globalization;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using Xunit.Abstractions;

namespace OpenXLR.Tests;

/// <summary>
/// A recorded mix's master and mute, measured where its audio is used: the
/// virtual microphone, the mix sink's monitor that its meter reads, an output
/// that follows the mix, and the mix after its inserts. The measurements do
/// not depend on where the mixer applies the master, so they hold for a
/// master folded into every send and for a master on the mix sink alike.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests(ITestOutputHelper output)
{
    private const string VirtualMixOutput = "test_vmix_output";

    private static MixerConfig VirtualMixConfig(params string[] extraMixes) => new()
    {
        Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic),
            new("chat", "Chat", MixKind.VirtualMic), new("auxout", "Aux", MixKind.AuxPort),
            .. extraMixes.Select(id => new MixDefinition(id, id, MixKind.VirtualMic))],
        Channels = [new("test", "Test") { Levels = new Dictionary<string, double>
            { ["monitor"] = 0, ["stream"] = .8, ["chat"] = .8, ["auxout"] = .8 } }],
    };

    private static string MicrophoneOf(string mix) => new MixDefinition(mix, mix, MixKind.VirtualMic).VirtualMicName;

    private static string SinkOf(string mix) => new MixDefinition(mix, mix, MixKind.VirtualMic).SinkName;

    /// <summary>
    /// Build the graph and wait for its virtual microphones: a recorder
    /// started before one is published records the default source instead.
    /// </summary>
    private static void BuildVirtualMixes(Mixer mixer, PipeWireAdapter pw, MixerConfig config)
    {
        mixer.Build(config);
        string[] microphones = [.. config.Mixes.Where(m => m.Kind == MixKind.VirtualMic).Select(m => m.VirtualMicName)];
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var sources = pw.ListDevices().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            return microphones.All(sources.Contains);
        }, TimeSpan.FromSeconds(5)), "The virtual microphones did not appear.");
    }

    /// <summary>The level a send at 0.8 reaches with this master: 0.1 DC through two cubic gains.</summary>
    private static double ThroughSend(double master, bool muted = false) => muted ? 0 : .1 * Math.Pow(.8 * master, 3);

    [MonitorPipeWireFact]
    public void AVirtualMixMasterAndMuteReachItsMicrophoneMeterTapOutputAndInserts()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink(VirtualMixOutput, "Virtual mix output");
        BuildVirtualMixes(mixer, pw, VirtualMixConfig());
        mixer.SetMonitorOutputs([VirtualMixOutput]);
        // Products that are whole percentages: 0.8 at 1, 0.5 and 0.
        foreach (var (master, muted) in new[] { (1.0, false), (.5, false), (0.0, false), (1.0, true), (.5, true) })
        {
            foreach (string mix in new[] { "stream", "chat", "auxout" })
            {
                mixer.SetMixVolume(mix, master);
                mixer.SetMixMuted(mix, muted);
            }
            double expected = ThroughSend(master, muted);
            SettleSends(mixer);
            CaptureLevel(expected, "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);
            CaptureLevel(expected, "OpenXLR_ch_test", SinkOf("stream"));
            CaptureLevel(expected, "OpenXLR_ch_test", MicrophoneOf("chat"), virtualMicrophone: true);
            CaptureLevel(expected, "OpenXLR_ch_test", SinkOf("auxout"));
            foreach (string mix in new[] { "stream", "auxout" })
            {
                Assert.Null(mixer.SetMonitorFeed(VirtualMixOutput, mix));
                SettleSends(mixer);
                CaptureLevel(expected, "OpenXLR_ch_test", VirtualMixOutput);
            }
        }

        // An insert that takes the mix to a quarter sits after the master:
        // the microphone and the output hear both, the meter tap only the master.
        mixer.SetInserts("mix:stream", [CaptureChainFixture.Gain(2, .25)]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["mix:stream"]).Error);
        Assert.Null(mixer.SetMonitorFeed(VirtualMixOutput, "stream"));
        foreach (var (master, muted) in new[] { (.5, false), (1.0, true), (1.0, false) })
        {
            mixer.SetMixVolume("stream", master);
            mixer.SetMixMuted("stream", muted);
            double expected = ThroughSend(master, muted);
            SettleSends(mixer);
            mixer.EnsureFilterRoutes();
            mixer.EnsureMonitorRoutes();
            CaptureLevel(.25 * expected, "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);
            CaptureLevel(.25 * expected, "OpenXLR_ch_test", VirtualMixOutput);
            CaptureLevel(expected, "OpenXLR_ch_test", SinkOf("stream"));
        }
    }

    [MonitorPipeWireFact]
    public void AVirtualMixMasterAndMuteComeBackWithASceneAndWithSettingsAfterABuild()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        BuildVirtualMixes(mixer, pw, VirtualMixConfig());
        mixer.SetMixVolume("stream", .5);
        mixer.SetMixMuted("chat", true);
        MixerScene scene = mixer.ExportScene();
        var settings = mixer.ExportSettings();
        mixer.SetMixVolume("stream", 1);
        mixer.SetMixMuted("chat", false);
        Recorded(1, 1, false);

        mixer.ApplyScene(scene);
        Recorded(.5, 1, true);

        BuildVirtualMixes(mixer, pw, VirtualMixConfig());
        Recorded(1, 1, false);
        mixer.ApplySettings(settings);
        Recorded(.5, 1, true);

        void Recorded(double stream, double chat, bool chatMuted)
        {
            MixerState state = mixer.Snapshot();
            Assert.Equal(stream, state.Mixes.Single(m => m.Id == "stream").Volume);
            Assert.Equal(chatMuted, state.Mixes.Single(m => m.Id == "chat").Muted);
            SettleSends(mixer);
            CaptureLevel(ThroughSend(stream), "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);
            CaptureLevel(ThroughSend(chat, chatMuted), "OpenXLR_ch_test", MicrophoneOf("chat"), virtualMicrophone: true);
        }
    }

    /// <summary>
    /// The mix sink's own volume and mute, written from outside as a desktop
    /// applet would, reach every consumer of a recorded mix: the volume is
    /// applied on the sink's monitor ports, which the virtual microphone, the
    /// meter and an output following the mix all read. The sweep then puts
    /// the sink back where the mixer holds it.
    /// </summary>
    [MonitorPipeWireFact]
    public void AVirtualMixSinkVolumeAndMuteReachItsMicrophoneMeterTapAndOutput()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink(VirtualMixOutput, "Virtual mix output");
        BuildVirtualMixes(mixer, pw, VirtualMixConfig());
        mixer.SetMonitorOutputs([VirtualMixOutput]);
        SettleSends(mixer);
        foreach (string mix in new[] { "chat", "stream", "auxout" })
        {
            Assert.Null(mixer.SetMonitorFeed(VirtualMixOutput, mix));
            foreach (var (percent, muted) in new[] { (50, false), (0, false), (100, true), (100, false) })
            {
                Pactl("set-sink-volume", SinkOf(mix), $"{percent}%");
                Pactl("set-sink-mute", SinkOf(mix), muted ? "1" : "0");
                double expected = ThroughSend(percent / 100.0, muted);
                if (mix != "auxout")
                    CaptureLevel(expected, "OpenXLR_ch_test", MicrophoneOf(mix), virtualMicrophone: true);
                CaptureLevel(expected, "OpenXLR_ch_test", SinkOf(mix));
                CaptureLevel(expected, "OpenXLR_ch_test", VirtualMixOutput);
            }
            Pactl("set-sink-volume", SinkOf(mix), "50%");
            Pactl("set-sink-mute", SinkOf(mix), "1");
            // The sweep reads the registry, which shows a write from another
            // connection a moment later.
            Assert.True(SpinWait.SpinUntil(() => pw.OwnSinkLevels().Single(s => s.Name == SinkOf(mix)) is { Muted: true } level
                && Math.Abs(level.Volume - .125) < .01, TimeSpan.FromSeconds(3)), "The registry did not show the desktop write.");
            Assert.Contains(SinkOf(mix), mixer.EnsureOwnSinkLevels());
            Assert.Equal(1, pw.GetSinkVolume(SinkOf(mix)));
            Assert.False(pw.GetSinkMuted(SinkOf(mix)));
        }
    }

    /// <summary>
    /// WirePlumber keeps saved state for a mix sink and, from 0.5, puts it
    /// back when the sink is created again. The test runner seeds a
    /// quarter-volume muted entry for <c>OpenXLR_mix_restored</c>, and a
    /// second recorded mix learns its state here from a desktop write before
    /// the mixer builds the graph again. A recorded mix's sink opts out with
    /// <c>state.restore-props=false</c>, so both open at full volume and
    /// unmuted, and stay there.
    /// </summary>
    [MonitorPipeWireFact]
    public void ARecreatedRecordedMixSinkKeepsNoSavedDesktopState()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        const string restored = "OpenXLR_mix_restored", learned = "OpenXLR_mix_learned";
        BuildVirtualMixes(mixer, pw, VirtualMixConfig("restored", "learned"));
        try
        {
            Assert.Equal((1.0, false), Level(restored));
            // A level the desktop wrote is saved, then the mixer builds the
            // graph again and the sink is created anew under the same name.
            Pactl("set-sink-volume", learned, "30%");
            Pactl("set-sink-mute", learned, "1");
            Thread.Sleep(1500);
            BuildVirtualMixes(mixer, pw, VirtualMixConfig("restored", "learned"));
            foreach (string sink in new[] { restored, learned, SinkOf("stream"), SinkOf("chat"), SinkOf("auxout") })
                Assert.Equal((1.0, false), Level(sink));
            Thread.Sleep(2000);
            foreach (string sink in new[] { restored, learned, SinkOf("stream"), SinkOf("chat"), SinkOf("auxout") })
                Assert.Equal((1.0, false), Level(sink));
        }
        finally
        {
            // Leave nothing saved that a later test could get back.
            Pactl("set-sink-volume", learned, "100%");
            Pactl("set-sink-mute", learned, "0");
        }

        (double Volume, bool Muted) Level(string sink) => (pw.GetSinkVolume(sink) ?? -1, pw.GetSinkMuted(sink));
    }

    /// <summary>
    /// The cost of one master move with eleven channels, as on the desk: one
    /// helper process on the mix sink, for a recorded mix as for a monitor
    /// mix, whatever the number of channels. The times are reported.
    /// </summary>
    [MonitorPipeWireFact]
    public void AVirtualMixMasterMoveIsOneWriteToItsSink()
    {
        int runs = 0;
        var pw = new PipeWireAdapter(() => Interlocked.Increment(ref runs));
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        var levels = new Dictionary<string, double> { ["monitor"] = .8, ["stream"] = .8, ["chat"] = .8, ["auxout"] = .8 };
        mixer.Build(VirtualMixConfig() with
        {
            Channels = [.. Enumerable.Range(1, 11).Select(i => new ChannelDefinition($"app{i}", $"App {i}") { Levels = levels })],
        });
        SettleSends(mixer);
        foreach (string mix in new[] { "stream", "chat", "auxout", "monitor" })
        {
            var times = new List<double>();
            var counts = new List<int>();
            foreach (double volume in new[] { .5, .6, .7, .8, 1 })
            {
                int before = Volatile.Read(ref runs);
                var clock = Stopwatch.StartNew();
                mixer.SetMixVolume(mix, volume);
                times.Add(clock.Elapsed.TotalMilliseconds);
                counts.Add(Volatile.Read(ref runs) - before);
            }
            output.WriteLine($"setMixVolume {mix}: helpers {string.Join('/', counts)}, "
                + $"ms {string.Join('/', times.Select(t => t.ToString("0", CultureInfo.InvariantCulture)))}");
            Assert.All(counts, count => Assert.Equal(1, count));
        }
    }

    /// <summary>
    /// A desktop applet moving or muting a recorded mix's sink does not
    /// change the master: the sweep puts the sink back at the stored master
    /// and mute, and the virtual microphone hears the master again.
    /// </summary>
    [MonitorPipeWireFact]
    public void TheSweepPutsARecordedMixSinkBackAtItsMasterAndMute()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        BuildVirtualMixes(mixer, pw, VirtualMixConfig());
        mixer.SetMixVolume("stream", .5);
        SettleSends(mixer);
        CaptureLevel(ThroughSend(.5), "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);

        Desktop("100%", "0", amplitude: 1, muted: false);
        Assert.Contains(SinkOf("stream"), mixer.EnsureOwnSinkLevels());
        Assert.Equal(.5, pw.GetSinkVolume(SinkOf("stream")));
        Assert.False(pw.GetSinkMuted(SinkOf("stream")));
        Assert.Equal(.5, mixer.Snapshot().Mixes.Single(m => m.Id == "stream").Volume);
        CaptureLevel(ThroughSend(.5), "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);

        mixer.SetMixMuted("stream", true);
        Desktop("50%", "0", amplitude: .125, muted: false);
        Assert.Contains(SinkOf("stream"), mixer.EnsureOwnSinkLevels());
        Assert.True(pw.GetSinkMuted(SinkOf("stream")));
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "stream").Muted);
        CaptureLevel(0, "OpenXLR_ch_test", MicrophoneOf("stream"), virtualMicrophone: true);
        Assert.DoesNotContain(SinkOf("stream"), mixer.EnsureOwnSinkLevels());

        void Desktop(string volume, string mute, double amplitude, bool muted)
        {
            Pactl("set-sink-volume", SinkOf("stream"), volume);
            Pactl("set-sink-mute", SinkOf("stream"), mute);
            // The sweep reads the registry, which shows the write a moment later.
            Assert.True(SpinWait.SpinUntil(() => pw.OwnSinkLevels().Single(s => s.Name == SinkOf("stream")) is { } level
                && level.Muted == muted && Math.Abs(level.Volume - amplitude) < .01, TimeSpan.FromSeconds(3)),
                "The registry did not show the desktop write.");
        }
    }

    private static void Pactl(params string[] arguments)
    {
        ProcessResult result = ProcessRunner.Run("pactl", arguments);
        Assert.True(result.Ok, result.Stderr);
    }
}
