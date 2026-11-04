using OpenXLR.Core.Mixing;
using Xunit.Abstractions;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GraphFallbackTests(ITestOutputHelper output)
{
    [Fact]
    public void FailedPairedDefaultsAreBackedOffWhileDirectReadsStayFresh()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-default-backoff-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                echo '[{"id":0,"type":"PipeWire:Interface:Core"}]'
                exec /bin/sleep 60
                """);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                dir=${0%/*}
                case "$1" in
                    --format=json) echo attempt >> "$dir/info-calls"; echo '{}';;
                    get-default-sink) /bin/cat "$dir/sink";;
                    get-default-source) echo mic;;
                    *) exit 1;;
                esac
                """);
            string sink = Path.Combine(directory, "sink");
            File.WriteAllText(sink, "desk");
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            using var watch = adapter.WatchGraph();
            Assert.Equal("desk", adapter.GetDefaultSink());
            Assert.Equal("mic", adapter.GetDefaultSource());
            File.WriteAllText(sink, "headphones");
            Assert.Equal("headphones", adapter.GetDefaultSink());
            Assert.Single(File.ReadAllLines(Path.Combine(directory, "info-calls")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRegistryReadsDoNotAllocateForUnrelatedObjects(bool volumeUpdates)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-port-cost-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var objects = new List<object> { new { id = 0, type = "PipeWire:Interface:Core" } };
            for (int i = 1; i <= 100; i++)
            {
                objects.Add(new { id = i, type = "PipeWire:Interface:Node",
                    info = new
                    {
                        props = new Dictionary<string, object> { ["node.name"] = $"node{i}", ["media.class"] = "Audio/Sink" },
                        @params = new { Props = new[] { new { channelVolumes = new[] { 1.0 } } } }
                    } });
                for (int port = 0; port < 10; port++)
                    objects.Add(new { id = 100 + i * 10 + port, type = "PipeWire:Interface:Port",
                        info = new { direction = "output", props = new Dictionary<string, object>
                        { ["node.id"] = i, ["port.id"] = port, ["port.name"] = $"capture_{port}" } } });
            }
            File.WriteAllText(Path.Combine(directory, "graph.json"), System.Text.Json.JsonSerializer.Serialize(objects));
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph.json"
                for i in $(/bin/seq 1 14); do
                    while [ ! -f "$dir/update-$i.json" ]; do /bin/sleep 0.01; done
                    /bin/cat "$dir/update-$i.json"
                done
                exec /bin/sleep 60
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
                    if (volumeUpdates)
                    {
                        long updateAllocations = 0;
                        for (int i = 1; i <= 12; i++)
                        {
                            int percent = 50 + i;
                            Publish(i, $$$$"""
                                [{"id":1,"type":"PipeWire:Interface:Node","info":{
                                "props":{"node.name":"node1","media.class":"Audio/Sink"},
                                "params":{"Props":[{"channelVolumes":[{{{{Math.Pow(percent / 100d, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}]}]}}}]
                                """);
                            Assert.True(SpinWait.SpinUntil(() => adapter.GetSinkVolume("node1") == percent / 100d, TimeSpan.FromSeconds(2)));
                            long updateBefore = GC.GetAllocatedBytesForCurrentThread();
                            List<string> ports = adapter.ListPorts("node1", "capture", output: true);
                            updateAllocations += GC.GetAllocatedBytesForCurrentThread() - updateBefore;
                            Assert.Equal(10, ports.Count);
                        }
                        output.WriteLine($"12 port lookups after volume updates allocated {updateAllocations:N0} bytes.");
                        Assert.True(updateAllocations < 100_000, $"Port lookups after volume updates allocated {updateAllocations:N0} bytes.");
                        Publish(13, """
                            [{"id":1,"type":"PipeWire:Interface:Node","info":{
                              "props":{"node.name":"node1","media.class":"Audio/Sink"},
                              "params":{"Props":[{"channelVolumes":[1]}]}}},
                             {"id":110,"info":null}]
                            """);
                        Assert.True(SpinWait.SpinUntil(() => adapter.GetSinkVolume("node1") == 1, TimeSpan.FromSeconds(2)));
                        Assert.Equal(9, adapter.ListPorts("node1", "capture", output: true).Count);
                        Publish(14, """
                            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"renamed","media.class":"Audio/Sink"}}}]
                            """);
                        Assert.True(SpinWait.SpinUntil(() => adapter.SinkExists("renamed") == true, TimeSpan.FromSeconds(2)));
                        Assert.Empty(adapter.ListPorts("node1", "capture", output: true));
                        Assert.Equal(9, adapter.ListPorts("renamed", "capture", output: true).Count);
                        return;
                    }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100; i++)
                    {
                        adapter.ListPorts("node1", "capture", output: true);
                        adapter.SinkExists("node100");
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    output.WriteLine($"100 paired port and sink lookups allocated {allocated:N0} bytes.");
                    Assert.True(allocated < 500_000, $"100 paired port and sink lookups allocated {allocated:N0} bytes.");
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            probe.Start();
            Assert.True(probe.Join(TimeSpan.FromSeconds(10)), "Port allocation probe hung.");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(adapter.SinkExists("node100"));
            if (!volumeUpdates)
            {
                Assert.Equal(10, adapter.ListPorts("node1", "capture", output: true).Count);
                Assert.Empty(adapter.ListPorts("node1", "capture", output: false));
            }

            void Publish(int index, string batch)
            {
                string temp = Path.Combine(directory, "pending.json");
                File.WriteAllText(temp, batch);
                File.Move(temp, Path.Combine(directory, $"update-{index}.json"));
            }
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
