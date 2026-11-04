using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class GraphFallbackTests
{
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
