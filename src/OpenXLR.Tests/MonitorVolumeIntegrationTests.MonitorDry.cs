using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// XLR 1 with an insert that takes it to a quarter, Monitor A on one
    /// output and Monitor B on another: Monitor A hears it through the insert
    /// until its switch is on, then at the source's own level, while Monitor
    /// B, which is not dry, and the recorded mix keep the processed voice.
    /// Monitor B's switch moves only Monitor B. The dry send follows the
    /// channel volume, survives the source going away and coming back, and a
    /// profile without the field leaves it on.
    /// </summary>
    [MonitorPipeWireFact]
    public void ADryMonitorHearsTheMicrophoneBeforeItsEffectsAndEveryOtherMixKeepsThem()
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
            Channels = [new("xlr1", "XLR 1") { InputPair = 0, Levels = new Dictionary<string, double> { ["monitor"] = 1, ["monitor2"] = 1, ["stream"] = 1 } }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor), stream],
        });
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single().BindingConnected;
        });
        // The outputs can arrive after selection. Like the daemon, retry the
        // routes until the playback links exist, rather than waiting for the
        // nodes alone, which PipeWire can announce before their ports.
        const string output = "test_dry_output", output2 = "test_dry_output2";
        PortLink Route(string mix, string sink) => new([
            ($"OpenXLR_mix_{mix}:monitor_FL", sink + ":playback_FL"),
            ($"OpenXLR_mix_{mix}:monitor_FR", sink + ":playback_FR"),
        ]);
        mixer.SetMonitorOutputs([output, output2]);
        Assert.Null(mixer.SetMonitorFeed(output, "monitor"));
        Assert.Null(mixer.SetMonitorFeed(output2, "monitor2"));
        pw.CreateNullSink(output, "Output");
        pw.CreateNullSink(output2, "Output B");
        mixer.SetInserts("xlr1", [CaptureChainFixture.Gain(1, .25)]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Error);

        Monitor(.025);
        MonitorB(.025);
        Recorded(.025);

        mixer.SetMonitorDry("monitor", true);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureFilterRoutes();
            return mixer.DryCarrying.Contains("xlr1");
        });
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").MonitorDry);
        // Only the monitor mixes have legs from the dry route.
        IReadOnlyDictionary<string, int> legs = CombineLegs(pw, "OpenXLR_ch_xlr1.dry");
        Assert.Equal(["OpenXLR_mix_monitor", "OpenXLR_mix_monitor2"], legs.Keys.Order());
        Monitor(.1);
        MonitorB(.025);
        Recorded(.025);

        // Monitor B's switch moves Monitor B only, on the same route.
        mixer.SetMonitorDry("monitor2", true);
        MonitorB(.1);
        Monitor(.1);
        mixer.SetMonitorDry("monitor", false);
        Monitor(.025);
        MonitorB(.1);
        Assert.Equal(legs, CombineLegs(pw, "OpenXLR_ch_xlr1.dry"));
        mixer.SetMonitorDry("monitor", true);
        mixer.SetMonitorDry("monitor2", false);
        Monitor(.1);
        MonitorB(.025);

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
        MonitorB(.025);
        Recorded(.025);

        // A profile from before the switch leaves it on; one that names the
        // monitor mix sets it.
        mixer.ApplyScene(new MixerScene());
        Assert.Contains("xlr1", mixer.DryCarrying);
        mixer.ApplyScene(new MixerScene { MonitorDry = new() { ["monitor"] = false } });
        Assert.DoesNotContain("xlr1", mixer.DryCarrying);
        Assert.Null(pw.FindNodeId("OpenXLR_ch_xlr1.dry"));
        Monitor(.025);
        Recorded(.025);

        void Monitor(double expected) => Heard(expected, Route("monitor", output), output);

        void MonitorB(double expected) => Heard(expected, Route("monitor2", output2), output2);

        void Heard(double expected, PortLink route, string sink)
        {
            CaptureChainFixture.Wait(() =>
            {
                mixer.EnsureMonitorRoutes();
                return pw.LinksPresent(route);
            });
            mixer.EnsureCellLevels();
            CaptureLevel(expected, "dry_signal", sink);
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
