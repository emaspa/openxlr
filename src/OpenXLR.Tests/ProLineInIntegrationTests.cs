using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's Line In on a private PipeWire server, against a capture
/// node laid out as the desk unit's raw source: 18 ports, XLR 1 on AUX0 and
/// AUX1, XLR 2 on AUX2 and AUX3, USB Aux on AUX4 and AUX5, Line In on AUX6
/// and AUX7, the unit's own mixes on AUX8 to AUX17. Line In takes its pair
/// and Aux In keeps its own, no channel ever takes a mix return, a Line In
/// route that failed is made again without touching the routes beside it,
/// and the strip is there only while the source has that layout: a six or
/// two port source leaves it silent and absent.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    private const string LinePro = "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_LINEPRO-00.pro-input-0";
    private const string LineParent = "line_pro_parent";

    private static string AuxMap(int ports) => string.Join(',', Enumerable.Range(0, ports).Select(i => "aux" + i));

    /// <summary>The Pro's capture node with this many ports, under its parent sink of 18; returns the node's module.</summary>
    private static uint LineProSource(PipeWireAdapter pw, int ports)
    {
        uint node = LoadModule("module-remap-source", $"source_name={LinePro}", $"master={LineParent}.monitor",
            $"channels={ports}", $"channel_map={AuxMap(ports)}", $"master_channel_map={AuxMap(ports)}",
            "source_properties=device.description=Line-pro");
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == LinePro && d.Channels == ports));
        WaitForRecovery(() => pw.ListPorts(LinePro, "", true).Count == ports);
        return node;
    }

    private static uint LineProParent()
    {
        uint parent = LoadModule("module-null-sink", $"sink_name={LineParent}", "channels=18", $"channel_map={AuxMap(18)}",
            "sink_properties=device.description=Line-pro-parent");
        WaitForRecovery(() => ProcessRunner.Run("pactl", ["list", "sources", "short"]).StdoutText.Contains(LineParent + ".monitor", StringComparison.Ordinal));
        return parent;
    }

    /// <summary>The source ports linked into a strip's sink, by port name, in order.</summary>
    private static string[] PortsInto(string strip)
        => [.. GraphLinksInto($"OpenXLR_ch_{strip}").Select(link => link.From[(LinePro.Length + 1)..]).Order()];

    private static Mixer LineProMixer(PipeWireAdapter pw)
    {
        var mixer = new Mixer(pw);
        mixer.SetAttached(true);
        mixer.SetInputJacks(2, true, true);
        mixer.SetInputDeviceHint(["Wave_XLR_Pro_LINEPRO-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(MixerConfig.Default());
        return mixer;
    }

    private static bool AllFed(Mixer mixer, params string[] strips)
    {
        mixer.EnsureInputFeeds();
        return strips.All(strip => IncomingRouteLinks($"OpenXLR_ch_{strip}").Length == 2);
    }

    [MonitorPipeWireFact]
    public void LineInTakesItsOwnPairAndNoChannelTakesAMixReturn()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        List<uint> modules = [LineProParent()];
        Mixer? mixer = null;
        try
        {
            modules.Add(LineProSource(pw, 18));
            mixer = LineProMixer(pw);
            WaitForRecovery(() => AllFed(mixer, "xlr1", "xlr2", "aux", "line"));

            Assert.Equal(["capture_AUX0", "capture_AUX1"], PortsInto("xlr1"));
            Assert.Equal(["capture_AUX2", "capture_AUX3"], PortsInto("xlr2"));
            Assert.Equal(["capture_AUX4", "capture_AUX5"], PortsInto("aux"));
            Assert.Equal(["capture_AUX6", "capture_AUX7"], PortsInto("line"));
            // Each side of the pair lands on its own side of the strip.
            Assert.Equal([($"{LinePro}:capture_AUX6", "OpenXLR_ch_line:playback_FL"), ($"{LinePro}:capture_AUX7", "OpenXLR_ch_line:playback_FR")],
                GraphLinksInto("OpenXLR_ch_line").Order());
            // The unit's own mixes streamed back reach no channel.
            using (var graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout))
            {
                string[] taken = [.. PipeWireAdapter.ParseGraphLinks(graph.RootElement.EnumerateArray())
                    .Where(link => link.From.StartsWith(LinePro + ":", StringComparison.Ordinal)).Select(link => link.From[(LinePro.Length + 1)..])];
                Assert.Equal(Enumerable.Range(0, 8).Select(i => $"capture_AUX{i}").Order(), taken.Distinct().Order());
            }

            ChannelStatus line = mixer.Snapshot().Channels.Single(c => c.Id == "line");
            Assert.True(line.Present);
            Assert.True(line.BindingConnected);
            Assert.Equal(new ChannelBinding("unit", Pair: 3), line.Binding);
            // A healthy graph is left exactly as it is, sweep after sweep.
            Stable(() => mixer.EnsureInputFeeds(), "OpenXLR_ch_line", 2);
            Stable(() => mixer.EnsureInputFeeds(), "OpenXLR_ch_aux", 2);
        }
        finally
        {
            mixer?.TearDown();
            mixer?.Dispose();
            foreach (uint module in Enumerable.Reverse(modules)) UnloadModule(module);
        }
    }

    [MonitorPipeWireFact]
    public void ALineInRouteThatFailedIsMadeAgainBesideTheRoutesThatStand()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        List<uint> modules = [LineProParent()];
        Mixer? mixer = null;
        try
        {
            modules.Add(LineProSource(pw, 18));
            // Every link into Line In fails while the mixer is built: the other inputs come up alone.
            RejectNextRouteLink(LinePro + ":", "OpenXLR_ch_line:", () => mixer = LineProMixer(pw), everyLinkInto: true);
            WaitForRecovery(() => IncomingRouteLinks("OpenXLR_ch_xlr1").Length == 2 && IncomingRouteLinks("OpenXLR_ch_aux").Length == 2);
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_line"));
            uint[] xlr1 = IncomingRouteLinks("OpenXLR_ch_xlr1"), xlr2 = IncomingRouteLinks("OpenXLR_ch_xlr2"), aux = IncomingRouteLinks("OpenXLR_ch_aux");

            // The sweep makes Line In's route and leaves the others' links alone.
            WaitForRecovery(() => AllFed(mixer!, "line"));
            Assert.Equal(["capture_AUX6", "capture_AUX7"], PortsInto("line"));
            Assert.Equal(xlr1, IncomingRouteLinks("OpenXLR_ch_xlr1"));
            Assert.Equal(xlr2, IncomingRouteLinks("OpenXLR_ch_xlr2"));
            Assert.Equal(aux, IncomingRouteLinks("OpenXLR_ch_aux"));

            // One side lost later is linked again, and the side that stayed is kept.
            uint[] before = IncomingRouteLinks("OpenXLR_ch_line");
            Assert.True(ProcessRunner.Run("pw-link", ["-d", $"{LinePro}:capture_AUX7", "OpenXLR_ch_line:playback_FR"]).Ok);
            WaitForRecovery(() => IncomingRouteLinks("OpenXLR_ch_line").Length == 1);
            uint kept = Assert.Single(IncomingRouteLinks("OpenXLR_ch_line"));
            Assert.Contains(kept, before);
            WaitForRecovery(() => AllFed(mixer!, "line"));
            Assert.Contains(kept, IncomingRouteLinks("OpenXLR_ch_line"));
            Assert.Equal(["capture_AUX6", "capture_AUX7"], PortsInto("line"));
            Assert.Equal(aux, IncomingRouteLinks("OpenXLR_ch_aux"));
            Stable(() => mixer!.EnsureInputFeeds(), "OpenXLR_ch_line", 2);
        }
        finally
        {
            mixer?.TearDown();
            mixer?.Dispose();
            foreach (uint module in Enumerable.Reverse(modules)) UnloadModule(module);
        }
    }

    [MonitorPipeWireFact]
    public void TheSourceLayoutDecidesWhetherLineInIsThere()
    {
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        uint parent = LineProParent();
        uint? node = null;
        Mixer? mixer = null;
        bool LinePresent() => mixer!.Snapshot().Channels.Single(c => c.Id == "line").Present;
        try
        {
            // A six-port layout: Aux In on its pair, Line In silent and absent.
            node = LineProSource(pw, 6);
            mixer = LineProMixer(pw);
            WaitForRecovery(() => AllFed(mixer, "xlr1", "xlr2", "aux"));
            Assert.Equal(["capture_AUX4", "capture_AUX5"], PortsInto("aux"));
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_line"));
            Assert.False(LinePresent());
            Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "aux").Present);
            Stable(() => mixer.EnsureInputFeeds(), "OpenXLR_ch_aux", 2);

            // The card comes back with all 18 ports: Line In arrives on its own pair.
            UnloadModule(node.Value);
            node = null;
            WaitForRecovery(() => pw.FindNodeId(LinePro) is null);
            node = LineProSource(pw, 18);
            WaitForRecovery(() => AllFed(mixer, "xlr1", "xlr2", "aux", "line"));
            Assert.Equal(["capture_AUX6", "capture_AUX7"], PortsInto("line"));
            Assert.Equal(["capture_AUX4", "capture_AUX5"], PortsInto("aux"));
            Assert.True(LinePresent());

            // Two ports: XLR 1 alone carries anything, and Line In goes again.
            UnloadModule(node.Value);
            node = null;
            WaitForRecovery(() => pw.FindNodeId(LinePro) is null);
            node = LineProSource(pw, 2);
            WaitForRecovery(() => AllFed(mixer, "xlr1"));
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_line"));
            Assert.Empty(IncomingRouteLinks("OpenXLR_ch_aux"));
            Assert.False(LinePresent());
            // Its levels and mutes stay for the layout's return.
            Assert.Contains("monitor", mixer.Snapshot().Channels.Single(c => c.Id == "line").MutedIn);
        }
        finally
        {
            mixer?.TearDown();
            mixer?.Dispose();
            if (node is uint id) UnloadModule(id);
            UnloadModule(parent);
        }
    }
}
