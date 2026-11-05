using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// XLR 1 with an insert that takes it to a quarter: the monitor hears it
    /// through the insert until the switch is on, then at the source's own
    /// level, while the recorded mix keeps the processed voice throughout.
    /// The dry send follows the channel volume, survives the source going
    /// away and coming back, and a profile without the field leaves it on.
    /// </summary>
    [MonitorPipeWireFact]
    public void ADryMicrophoneReachesTheMonitorBeforeItsEffectsAndTheRecordedMixKeepsThem()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("dry_signal", "Microphone signal");
        uint source = pw.CreateVirtualMic("Wave_XLR_DRY_source", "dry_signal.monitor", "Synthetic Wave XLR");
        mixer.SetInputDeviceHint(["Wave_XLR_DRY_source"], hardwareOutputRouting: false, modelFallback: false);
        var stream = new MixDefinition("stream", "Stream", MixKind.VirtualMic);
        mixer.Build(new MixerConfig
        {
            Channels = [new("xlr1", "XLR 1") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1, ["stream"] = 1 } }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), stream],
        });
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single().BindingConnected;
        });
        pw.CreateNullSink("test_dry_output", "Output");
        CaptureChainFixture.Wait(() => pw.FindNodeId("test_dry_output") is not null);
        mixer.SetMonitorOutputs(["test_dry_output"]);
        mixer.SetInserts("xlr1", [CaptureChainFixture.Gain(1, .25)]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Error);

        Monitor(.025);
        Recorded(.025);

        mixer.SetMonitorDry("xlr1", true);
        Assert.Contains("xlr1", mixer.DryCarrying);
        Assert.True(mixer.Snapshot().Channels.Single().MonitorDry);
        // Only the monitor mix has a leg from the dry route.
        IReadOnlyDictionary<string, int> legs = CombineLegs(pw, "OpenXLR_ch_xlr1.dry");
        Assert.Equal(["OpenXLR_mix_monitor"], legs.Keys);
        Monitor(.1);
        Recorded(.025);

        // The channel volume reaches the dry send as it reaches every send.
        mixer.SetChannelVolume("xlr1", .5);
        Monitor(.1 * Math.Pow(.5, 3));
        Recorded(.025 * Math.Pow(.5, 3));
        mixer.SetChannelVolume("xlr1", 1);

        // A sweep keeps the route as it is.
        mixer.EnsureFilterRoutes();
        Assert.Equal(legs, CombineLegs(pw, "OpenXLR_ch_xlr1.dry"));
        Monitor(.1);

        // The source goes away and comes back, as a replugged unit does: the
        // sweeps follow it and the monitor hears it dry again.
        pw.UnloadModule(source);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            return !mixer.DryCarrying.Contains("xlr1");
        }, () => string.Join(", ", mixer.DryCarrying));
        pw.CreateVirtualMic("Wave_XLR_DRY_source", "dry_signal.monitor", "Synthetic Wave XLR");
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            return mixer.DryCarrying.Contains("xlr1");
        });
        Monitor(.1);
        Recorded(.025);

        // A profile from before the switch leaves it on; one that names the
        // microphone sets it.
        mixer.ApplyScene(new MixerScene());
        Assert.Contains("xlr1", mixer.DryCarrying);
        mixer.ApplyScene(new MixerScene { MonitorDry = new() { ["xlr1"] = false } });
        Assert.DoesNotContain("xlr1", mixer.DryCarrying);
        Assert.Null(pw.FindNodeId("OpenXLR_ch_xlr1.dry"));
        Monitor(.025);
        Recorded(.025);

        void Monitor(double expected)
        {
            mixer.EnsureCellLevels();
            CaptureLevel(expected, "dry_signal", "test_dry_output");
        }

        void Recorded(double expected) => CaptureLevel(expected, "dry_signal", stream.VirtualMicName, virtualMicrophone: true);
    }

    /// <summary>The streams of the combine named <paramref name="sink"/>, by the sink each feeds.</summary>
    private static IReadOnlyDictionary<string, int> CombineLegs(PipeWireAdapter pw, string sink)
    {
        string modules = ProcessRunner.Run("pactl", ["list", "modules", "short"], TimeSpan.FromSeconds(5)).StdoutText;
        uint module = modules.Split('\n').Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 3 && columns[2].Contains($"sink_name={sink} ", StringComparison.Ordinal))
            .Select(columns => uint.Parse(columns[0], System.Globalization.CultureInfo.InvariantCulture)).Single();
        return pw.FindCombineLegs(module);
    }
}
