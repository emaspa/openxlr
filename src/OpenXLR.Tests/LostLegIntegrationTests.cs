using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// A combine leg that ends, or that nothing links, as a restarted
    /// WirePlumber that fails one link leaves it, is made again by reloading
    /// its channel's sink: a destroyed leg on an application channel and an
    /// unlinked one on a capture input, whose feed from its source is linked
    /// into the new sink. Once the legs are back nothing is reloaded again.
    /// </summary>
    [MonitorPipeWireFact]
    public void ALostCombineLegComesBackWithItsChannelsSink()
    {
        const string source = "test_lost_leg_source";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { LegMissingSweeps = 2 };
        try
        {
            pw.CreateNullSink("test_lost_leg_parent", "Lost leg parent");
            pw.CreateVirtualMic(source, "test_lost_leg_parent.monitor", "Lost leg source");
            WaitForRecovery(() => pw.ListPorts(source, "capture", true).Count == 2);
            mixer.Build(new MixerConfig
            {
                Channels = [new("music", "Music")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            });
            mixer.CreateCaptureChannel("Capture", source, 0, _ => null);
            string capture = mixer.Snapshot().Channels.Single(c => c.CaptureSource == source).Id;
            foreach (string channel in new[] { "music", capture })
            {
                string sink = "OpenXLR_ch_" + channel;
                WaitForRecovery(() => { mixer.EnsureInputFeeds(); mixer.EnsureCellLevels(); return Legs(sink) is var legs && legs.ContainsKey("OpenXLR_mix_monitor") && legs.ContainsKey("OpenXLR_mix_stream"); });
                if (channel == capture) WaitForRecovery(() => { mixer.EnsureInputFeeds(); return Links(sink, incoming: true) == 2; });
                IReadOnlyDictionary<string, int> before = Legs(sink);
                string legName = NodeWithSerial(before["OpenXLR_mix_stream"]);
                if (channel == "music")
                {
                    // The leg's node goes; pactl refuses to kill a module's stream.
                    int leg = Assert.IsType<int>(pw.FindNodeId(legName));
                    ProcessResult destroyed = ProcessRunner.Run("pw-cli", ["destroy", leg.ToString()]);
                    Assert.True(destroyed.Ok, destroyed.Stderr);
                    WaitForRecovery(() => !Legs(sink).ContainsKey("OpenXLR_mix_stream"));
                }
                else
                {
                    // The leg stays and loses its links, as after a link
                    // WirePlumber failed to make.
                    foreach (string side in new[] { "FL", "FR" })
                        ProcessRunner.Run("pw-link", ["-d", $"{legName}:output_{side}", $"OpenXLR_mix_stream:playback_{side}"]);
                    WaitForRecovery(() => Links(legName, incoming: false) == 0);
                }

                IReadOnlyList<string> reloaded = [];
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels(out _, out reloaded);
                    mixer.EnsureInputFeeds();
                    return reloaded.Count > 0;
                }, TimeSpan.FromSeconds(5)), "the lost leg was not noticed");
                Assert.Equal([channel], reloaded);
                WaitForRecovery(() => { mixer.EnsureCellLevels(); mixer.EnsureInputFeeds(); return Legs(sink) is var legs && legs.ContainsKey("OpenXLR_mix_monitor") && legs.ContainsKey("OpenXLR_mix_stream")
                    && Links(legName, incoming: false) == 2; });
                if (channel == capture) WaitForRecovery(() => { mixer.EnsureInputFeeds(); return Links(sink, incoming: true) == 2; });
                // Settled: nothing is reloaded again.
                for (int i = 0; i < 4; i++)
                {
                    mixer.EnsureCellLevels(out _, out reloaded);
                    Assert.Empty(reloaded);
                }
            }
        }
        finally { pw.TearDown(); }

        IReadOnlyDictionary<string, int> Legs(string sink)
        {
            string? module = ProcessRunner.Run("pactl", ["list", "modules", "short"]).StdoutText.Split('\n')
                .FirstOrDefault(line => line.Contains("module-combine-sink", StringComparison.Ordinal)
                    && line.Contains("sink_name=" + sink + " ", StringComparison.Ordinal))?.Split('\t')[0];
            return module is null ? new Dictionary<string, int>() : pw.FindCombineLegs(uint.Parse(module));
        }
    }

    /// <summary>
    /// A combine leg linked crossed, left into the mix's right and right into
    /// its left, as a restarted WirePlumber can link one, is made again by
    /// reloading its channel's sink, and the new leg is linked side to side.
    /// The legs into the other mixes count as healthy throughout.
    /// </summary>
    [MonitorPipeWireFact]
    public void ACrossedCombineLegComesBackLinkedSideToSide()
    {
        const string leg = "output.OpenXLR_ch_music_OpenXLR_mix_stream";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { LegMissingSweeps = 2 };
        try
        {
            mixer.Build(new MixerConfig
            {
                Channels = [new("music", "Music")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            });
            string[] sideToSide = [$"{leg}:output_FL>OpenXLR_mix_stream:playback_FL", $"{leg}:output_FR>OpenXLR_mix_stream:playback_FR"];
            WaitForRecovery(() => { mixer.EnsureCellLevels(); return PortLinks(leg).SequenceEqual(sideToSide); });
            IReadOnlyList<string> reloaded = [];
            for (int i = 0; i < 3; i++)
            {
                mixer.EnsureCellLevels(out _, out reloaded);
                Assert.Empty(reloaded);
            }

            foreach (string side in new[] { "FL", "FR" })
                ProcessRunner.Run("pw-link", ["-d", $"{leg}:output_{side}", $"OpenXLR_mix_stream:playback_{side}"]);
            foreach ((string from, string to) in new[] { ("FL", "FR"), ("FR", "FL") })
            {
                ProcessResult linked = ProcessRunner.Run("pw-link", [$"{leg}:output_{from}", $"OpenXLR_mix_stream:playback_{to}"]);
                Assert.True(linked.Ok, linked.Stderr);
            }
            WaitForRecovery(() => PortLinks(leg).SequenceEqual([$"{leg}:output_FL>OpenXLR_mix_stream:playback_FR", $"{leg}:output_FR>OpenXLR_mix_stream:playback_FL"]));

            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureCellLevels(out _, out reloaded);
                return reloaded.Count > 0;
            }, TimeSpan.FromSeconds(5)), "the crossed leg was not noticed");
            Assert.Equal(["music"], reloaded);
            WaitForRecovery(() => { mixer.EnsureCellLevels(); return PortLinks(leg).SequenceEqual(sideToSide); });
            for (int i = 0; i < 4; i++)
            {
                mixer.EnsureCellLevels(out _, out reloaded);
                Assert.Empty(reloaded);
            }
        }
        finally { pw.TearDown(); }
    }

    /// <summary>Each link out of the node named <paramref name="node"/>, as "node:port>node:port", sorted.</summary>
    private static string[] PortLinks(string node)
    {
        using JsonDocument graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        JsonElement[] items = [.. graph.RootElement.EnumerateArray()];
        Dictionary<uint, string> nodes = items.Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Node")
            .ToDictionary(item => item.GetProperty("id").GetUInt32(),
                item => item.GetProperty("info").GetProperty("props").TryGetProperty("node.name", out var name) ? name.GetString() ?? "" : "");
        Dictionary<uint, string> ports = items.Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Port")
            .ToDictionary(item => item.GetProperty("id").GetUInt32(),
                item => item.GetProperty("info").GetProperty("props").TryGetProperty("port.name", out var name) ? name.GetString() ?? "" : "");
        return [.. items.Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Link")
            .Select(item => item.GetProperty("info"))
            .Where(info => nodes.GetValueOrDefault(info.GetProperty("output-node-id").GetUInt32()) == node)
            .Select(info => $"{node}:{ports.GetValueOrDefault(info.GetProperty("output-port-id").GetUInt32())}>"
                + $"{nodes.GetValueOrDefault(info.GetProperty("input-node-id").GetUInt32())}:{ports.GetValueOrDefault(info.GetProperty("input-port-id").GetUInt32())}")
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The node.name of the node whose object.serial is <paramref name="serial"/>.</summary>
    private static string NodeWithSerial(int serial)
    {
        using JsonDocument graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        return graph.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Node")
            .Select(item => item.GetProperty("info").GetProperty("props"))
            .Where(props => props.TryGetProperty("object.serial", out JsonElement value) && value.GetInt64() == serial)
            .Select(props => props.GetProperty("node.name").GetString()!).Single();
    }

    /// <summary>The links into or out of the node named <paramref name="node"/>.</summary>
    private static int Links(string node, bool incoming)
    {
        using JsonDocument graph = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        uint? id = graph.RootElement.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Node"
                && item.GetProperty("info").GetProperty("props").TryGetProperty("node.name", out var name) && name.GetString() == node)
            .Select(item => (uint?)item.GetProperty("id").GetUInt32()).FirstOrDefault();
        if (id is null) return 0;
        string side = incoming ? "input-node-id" : "output-node-id";
        return graph.RootElement.EnumerateArray().Count(item => item.GetProperty("type").GetString() == "PipeWire:Interface:Link"
            && item.GetProperty("info").GetProperty(side).GetUInt32() == id);
    }
}
