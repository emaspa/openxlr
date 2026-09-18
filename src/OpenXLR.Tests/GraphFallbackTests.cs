using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GraphFallbackTests
{
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
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, recursive: true);
        }
    }
}
