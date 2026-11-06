using System.Reflection;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The EQ on a capture input on a stereo pair, on a private PipeWire server:
/// the source plays a steady 0.1 on the left and 0.2 on the right, and an EQ
/// whose only band is a +6 dB low shelf lifts a steady level by exactly 6 dB.
/// The stage is one stereo node at the head of the input's route, ahead of
/// its inserts, each side kept apart; every edit is live, off bypasses in
/// place, a profile brings it back, a stage whose helper dies is started
/// again, a source that leaves and returns gets it back, and deleting the
/// input takes it away. Noise removal and the dynamics stay off a stereo pair.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    /// <summary>A +6 dB lift, 10^(6/20).</summary>
    private const double StereoBoost = 1.9952623149688795;

    /// <summary>On, band 1 a +6 dB low shelf at 1 kHz and every other band off.</summary>
    private static readonly MicEq StereoBoostEq = MicEq.Default with
    {
        Enabled = true,
        Bands = [
            MicEq.Default.Bands[0] with { Type = MicEq.LowShelf, Frequency = 1000, Gain = 6 },
            MicEq.Default.Bands[1] with { Enabled = false },
            MicEq.Default.Bands[2] with { Enabled = false },
            MicEq.Default.Bands[3] with { Enabled = false },
        ],
    };

    [DspPipeWireFact]
    public void AStereoCaptureInputsEqIsOneStereoStageAheadOfItsInserts()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw, out uint source, width: 2);
        mixer.SetLevel("capture", "monitor", 1);
        mixer.SetChannelMuted("capture", "monitor", false);
        mixer.EnsureCellLevels();
        AssertCaptureDc(2, 1);

        ChannelStatus channel = mixer.Snapshot().Channels.Single(c => c.Id == "capture");
        Assert.False(channel.Microphone);
        Assert.Equal(2, channel.ChainWidth);
        Assert.Null(channel.Noise);
        Assert.Null(channel.Compressor);
        Assert.Null(channel.Eq!.Backend);
        Assert.Equal(MicEqProcessing.Software, channel.Eq.Processing);
        Assert.Empty(StereoEqNodes(pw));
        Assert.Throws<ArgumentException>(() => mixer.SetNoiseRemoval("capture", new(Enabled: true)));

        // On: one stage between the source and the channel, each side lifted on its own.
        mixer.SetEq("capture", StereoBoostEq);
        string stage = WaitForStereoEqStage(pw);
        Assert.Equal(2, RunningEqStage(mixer).Channels);
        Assert.Equal("software", CaptureEqState(mixer).Backend);
        Assert.True(CaptureEqState(mixer).Available);
        string[] links = CaptureChainFixture.Topology(([], [])).Links;
        Assert.Contains($"capture_test_source -> {stage}", links);
        Assert.Contains($"{stage} -> OpenXLR_ch_capture", links);
        Assert.DoesNotContain("capture_test_source -> OpenXLR_ch_capture", links);
        AssertCaptureDc(2, StereoBoost);

        // An insert goes behind it, and the running stage stays. The chain is
        // read from the mixer: its nodes are named by whichever host runs it.
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(2)]);
        var chains = (Dictionary<string, FilterHandle>)typeof(Mixer)
            .GetField("_chains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        CaptureChainFixture.Wait(() => chains.ContainsKey("capture"));
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        string chainIn = chains["capture"].SinkName;
        CaptureChainFixture.Wait(() => CaptureChainFixture.Topology(([], [])).Links.Contains($"{stage} -> {chainIn}"),
            () => string.Join("\n", CaptureChainFixture.Topology(([], [])).Links));
        Assert.Equal([stage], StereoEqNodes(pw));
        Assert.DoesNotContain($"capture_test_source -> {chainIn}", CaptureChainFixture.Topology(([], [])).Links);
        AssertCaptureDc(2, .25 * StereoBoost);

        // Every edit is live: the same stage, off bypasses inside it and it stays through sweeps.
        mixer.SetEqBand("capture", 1, new(Gain: 0));
        AssertCaptureDc(2, .25);
        mixer.SetEqBand("capture", 1, new(Gain: 6));
        AssertCaptureDc(2, .25 * StereoBoost);
        mixer.SetEqEnabled("capture", false);
        AssertCaptureDc(2, .25);
        for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
        Assert.Equal([stage], StereoEqNodes(pw));
        Assert.Equal("software", CaptureEqState(mixer).Backend);
        mixer.SetEqEnabled("capture", true);
        AssertCaptureDc(2, .25 * StereoBoost);
        Assert.Equal([stage], StereoEqNodes(pw));

        // A profile names it and brings it back; the settings carry it.
        MixerScene scene = mixer.ExportScene();
        Assert.Equal(MicEq.Snap(StereoBoostEq), scene.Eq!["capture"]);
        Assert.False(scene.NoiseRemoval!.ContainsKey("capture"));
        mixer.SetEqEnabled("capture", false);
        AssertCaptureDc(2, .25);
        mixer.ApplyScene(scene);
        AssertCaptureDc(2, .25 * StereoBoost);
        Assert.Equal(MicEq.Snap(StereoBoostEq), mixer.ExportSettings().Eq["capture"]);

        // The stage's helper dies: the sweep starts a stereo one again.
        System.Diagnostics.Process helper = RunningEqStage(mixer).Stage.Process;
        helper.Kill();
        helper.WaitForExit(2000);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes();
            return StereoEqNodes(pw) is [string again] && again != stage;
        }, () => string.Join("\n", CaptureChainFixture.Topology(([], [])).Links));
        stage = StereoEqNodes(pw)[0];
        Assert.Equal(2, RunningEqStage(mixer).Channels);
        AssertCaptureDc(2, .25 * StereoBoost);

        // The source leaves and comes back: the stage goes with it and returns.
        pw.UnloadModule(source);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return StereoEqNodes(pw).Length == 0 && !mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected;
        });
        Assert.Equal(MicEq.Snap(StereoBoostEq), mixer.EqFor("capture"));
        pw.CreateVirtualMic("capture_test_source", "capture_test_signal.monitor", "Capture returned");
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected && StereoEqNodes(pw).Length == 1;
        });
        Assert.NotEqual(stage, StereoEqNodes(pw)[0]);
        Assert.Equal("software", CaptureEqState(mixer).Backend);
        AssertCaptureDc(2, .25 * StereoBoost);

        // Deleting the input takes its stage and its EQ with it.
        MixerSettings? saved = null;
        mixer.DeleteApplicationChannel("capture", s => { saved = s; return null; });
        CaptureChainFixture.Wait(() => StereoEqNodes(pw).Length == 0 && pw.FindNodeId("OpenXLR_ch_capture") is null);
        Assert.False(saved!.Eq.ContainsKey("capture"));
        Assert.False(mixer.ExportSettings().Eq.ContainsKey("capture"));
    }

    private static MicEqState CaptureEqState(Mixer mixer) => mixer.Snapshot().Channels.Single(c => c.Id == "capture").Eq!;

    /// <summary>The stage the mixer runs for the capture input now.</summary>
    private static SoftwareEqBackend RunningEqStage(Mixer mixer)
        => ((Dictionary<string, SoftwareEqBackend>)typeof(Mixer)
            .GetField("_eqStages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!)["capture"];

    private static string[] StereoEqNodes(PipeWireAdapter pw)
        => [.. pw.DumpNodes().Select(n => n.Name).Where(n => n.StartsWith("OpenXLR_eq_capture_", StringComparison.Ordinal)).Order()];

    private static string WaitForStereoEqStage(PipeWireAdapter pw)
    {
        CaptureChainFixture.Wait(() => StereoEqNodes(pw).Length == 1, () => string.Join("\n", CaptureChainFixture.Topology(([], [])).Links));
        return StereoEqNodes(pw)[0];
    }
}
