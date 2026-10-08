using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void PartialMonitorRoutesRetryRejectedLinksAndAcceptReplacementMonoOutputs()
    {
        const string output = "test_monitor_partial";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig { Channels = [], Mixes = [
            new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)] });
        uint stereo = pw.CreateNullSink(output, "Partial monitor output");
        uint? mono = null;
        try
        {
            WaitForRecovery(() => ProcessRunner.Run("pw-link", ["-i"]).StdoutText.Contains(output + ":playback_FR", StringComparison.Ordinal));
            foreach ((string feed, int links) in new[] { ("monitor", 2), ("monitor+chat", 4) })
            {
                RejectNextRouteLink("OpenXLR_mix_", output + ":playback_FR", () =>
                {
                    if (feed == "monitor") mixer.SetMonitorOutputs([output]);
                    else Assert.Null(mixer.SetMonitorFeed(output, feed));
                });
                WaitForRecovery(() => IncomingRouteLinks(output).Length == links - 1);
                WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return IncomingRouteLinks(output).Length == links; });
                Stable(() => mixer.EnsureMonitorRoutes(), output, links);
            }

            pw.UnloadModule(stereo);
            WaitForRecovery(() => pw.FindNodeId(output) is null);
            mixer.EnsureMonitorRoutes();
            ProcessResult loaded = ProcessRunner.Run("pactl", ["load-module", "module-null-sink",
                "sink_name=" + output, "channels=1", "channel_map=mono"]);
            Assert.True(loaded.Ok, loaded.Stderr);
            mono = uint.Parse(loaded.StdoutText.Trim());
            WaitForRecovery(() => ProcessRunner.Run("pw-link", ["-i"]).StdoutText.Contains(output + ":playback_MONO", StringComparison.Ordinal));
            WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return IncomingRouteLinks(output).Length == 2; });
            Stable(() => mixer.EnsureMonitorRoutes(), output, 2); // One link per mix is complete for a genuine mono output.
        }
        finally
        {
            if (mono is uint module) ProcessRunner.Run("pactl", ["unload-module", module.ToString()]);
        }
    }

    [MonitorPipeWireFact]
    public void HealthyLinksAreCheckedAgainstTheRegistryWithoutRunningPwLink()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-link-probe-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        try
        {
            pw.CreateNullSink("link_source", "Source");
            pw.CreateNullSink("link_target", "Target");
            WaitForRecovery(() => pw.FindNodeId("link_source") is not null && pw.FindNodeId("link_target") is not null);
            PortLink link = pw.LinkNodes("link_source", "monitor", "link_target", "playback");
            Assert.Equal(2, link.Pairs.Count);
            Assert.True(SpinWait.SpinUntil(() => pw.EnsureLinks(link) == LinkHealth.Healthy, TimeSpan.FromSeconds(3)));
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                printf called >> "${0%/*}/calls"
                exit 1
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            for (int i = 0; i < 100; i++) Assert.Equal(LinkHealth.Healthy, pw.EnsureLinks(link));
            Assert.False(File.Exists(Path.Combine(directory, "calls")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            pw.TearDown();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LinkIndexNamesPortsByNodeAndIgnoresDanglingEndpoints()
    {
        using var document = JsonDocument.Parse("""
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"source"}}},
             {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"target"}}},
             {"id":3,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":1,"port.name":"monitor_FL"}}},
             {"id":4,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"2","port.name":"playback_FL"}}},
             {"id":5,"type":"PipeWire:Interface:Link","info":{"output-port-id":3,"input-port-id":4}},
             {"id":6,"type":"PipeWire:Interface:Link","info":{"output-port-id":3,"input-port-id":99}},
             {"id":7,"type":"PipeWire:Interface:Link","info":null},
             {"id":"8","type":"PipeWire:Interface:Node","info":{"props":{"node.name":"bad id"}}}]
            """);
        Assert.Equal(("source:monitor_FL", "target:playback_FL"),
            Assert.Single(PipeWireAdapter.ParseGraphLinks(document.RootElement.EnumerateArray())));
    }
}
