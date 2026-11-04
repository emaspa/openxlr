using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GraphFallbackTests
{
    [Fact]
    public void RepeatedPortReadsDoNotRebuildTheWholeIdleGraph()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-port-cost-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var objects = new List<object> { new { id = 0, type = "PipeWire:Interface:Core" } };
            for (int i = 1; i <= 100; i++)
            {
                objects.Add(new { id = i, type = "PipeWire:Interface:Node",
                    info = new { props = new Dictionary<string, object> { ["node.name"] = $"node{i}" } } });
                for (int port = 0; port < 10; port++)
                    objects.Add(new { id = 100 + i * 10 + port, type = "PipeWire:Interface:Port",
                        info = new { direction = "output", props = new Dictionary<string, object>
                        { ["node.id"] = i, ["port.id"] = port, ["port.name"] = $"capture_{port}" } } });
            }
            File.WriteAllText(Path.Combine(directory, "graph.json"), System.Text.Json.JsonSerializer.Serialize(objects));
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                /bin/cat "${0%/*}/graph.json"
                while :; do /bin/sleep 1; done
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            using var watch = adapter.WatchGraph();
            Exception? failure = null;
            var probe = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 10; i++) adapter.ListPorts("node1", "capture", output: true);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100; i++) adapter.ListPorts("node1", "capture", output: true);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(allocated < 500_000, $"100 idle port reads allocated {allocated} bytes.");
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            probe.Start();
            Assert.True(probe.Join(TimeSpan.FromSeconds(10)), "Port allocation probe hung.");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(10, adapter.ListPorts("node1", "capture", output: true).Count);
            Assert.Empty(adapter.ListPorts("node1", "capture", output: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PortListsDiscardThePreviousWatchAndKeepTheDirectFallback()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-port-reconnect-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            const string graph = """
                [{"id":0,"type":"PipeWire:Interface:Core"},
                 {"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_filter"}}},
                 {"id":2,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":1,"port.id":0,"port.name":"output_MONO"}}},
                 {"id":3,"type":"PipeWire:Interface:Port","info":{"direction":"input","props":{"node.id":1,"port.id":0,"port.name":"input_MONO"}}}]
                """;
            string file = Path.Combine(directory, "graph.json");
            File.WriteAllText(file, graph);
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                /bin/cat "${0%/*}/graph.json"
                while :; do /bin/sleep 1; done
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                echo 'OpenXLR_filter:capture_fallback'
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            using (adapter.WatchGraph())
            {
                Assert.Equal(["OpenXLR_filter:output_MONO"], adapter.ListPorts("OpenXLR_filter", "capture", true));
                Assert.Equal(["OpenXLR_filter:input_MONO"], adapter.ListPorts("OpenXLR_filter", "playback", false));
            }
            Assert.Equal(["OpenXLR_filter:capture_fallback"], adapter.ListPorts("OpenXLR_filter", "capture", true));
            File.WriteAllText(file, graph.Replace("output_MONO", "output_FL", StringComparison.Ordinal));
            using (adapter.WatchGraph())
                Assert.Equal(["OpenXLR_filter:output_FL"], adapter.ListPorts("OpenXLR_filter", "capture", true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AVisibleSinkWithoutReadableVolumeKeepsTheDirectQueryFallback()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-unreadable-volume-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                printf '%s\n' '[{"id":0,"type":"PipeWire:Interface:Core"},{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"desktop","media.class":"Audio/Sink"}}}]'
                while :; do /bin/sleep 1; done
                """);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                test "$1" = get-sink-volume || exit 1
                printf '%s\n' "$*" >> "${0%/*}/calls"
                echo 'Volume: front-left: 49152 / 75% / -7.50 dB'
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            using var watch = adapter.WatchGraph();
            Assert.True(adapter.SinkExists("desktop"));
            Assert.Equal(.75, adapter.GetSinkVolume("desktop"));
            Assert.Null(adapter.GetSinkVolume("missing"));
            Assert.Equal(["get-sink-volume desktop"], File.ReadAllLines(Path.Combine(directory, "calls")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WhileTheSubscriptionIsNotReadyTheAdapterReadsAOneShotDump()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-fallback-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // The subscription never delivers a registry; the one-shot form
            // answers with a graph. The sweep must see that graph meanwhile.
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                if [ "$1" = "--monitor" ]; then exit 1; fi
                /bin/cat "$dir/graph.json"
                """);
            File.WriteAllText(Path.Combine(directory, "graph.json"), """
                [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"desktop","node.description":"Desk",
                  "media.class":"Audio/Sink","device.api":"alsa"},"params":{"Props":[{"channelVolumes":[0.125,0.125],"mute":true}]}}},
                 {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor",
                  "media.class":"Audio/Sink"},"params":{"Props":[{"channelVolumes":[1.0,1.0],"mute":false}]}}},
                 {"id":3,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"mic","media.class":"Audio/Source","device.api":"alsa"}}}]
                """);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                case "$1" in
                    get-default-sink) echo desktop;;
                    get-default-source) echo mic;;
                    get-sink-volume) echo 'Volume: front-left: 32768 / 50% / -18.06 dB';;
                    list) printf '1\tdesktop\tdriver\n';;
                    *) exit 1;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            using IDisposable watch = adapter.WatchGraph();

            IReadOnlyList<AudioNode> devices = adapter.ListDevices();
            AudioNode desk = Assert.Single(devices, d => d.Name == "desktop");
            Assert.Equal(0.5, desk.Volume);
            Assert.True(desk.Muted);
            AudioNode mic = Assert.Single(devices, d => d.Name == "mic");
            Assert.Null(mic.Volume);
            Assert.Null(mic.Muted);
            OwnSinkLevel own = Assert.Single(adapter.OwnSinkLevels());
            Assert.Equal("OpenXLR_mix_monitor", own.Name);
            Assert.Equal(1.0, own.DesktopVolume);
            Assert.True(adapter.SinkExists("desktop"));
            Assert.False(adapter.SinkExists("missing"));
            Assert.Equal(.5, adapter.GetSinkVolume("desktop"));
            Assert.Equal("desktop", adapter.GetDefaultSink());
            Assert.Equal("mic", adapter.GetDefaultSource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, recursive: true);
        }
    }
}
