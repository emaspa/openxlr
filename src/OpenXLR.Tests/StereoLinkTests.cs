using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class StereoLinkTests
{
    [MonitorPipeWireFact]
    public void HealthyStereoLinksUseTheRegistryWithoutLaunchingALinkCommand()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-link-probe-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        try
        {
            pw.CreateNullSink("link_source", "Source");
            pw.CreateNullSink("link_target", "Target");
            var link = pw.LinkStereoNodes("link_source", "monitor", "link_target", "playback");
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
    public void LinkIndexUsesPortIdentityAndIgnoresDanglingEndpoints()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            [{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"source"}}},
             {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"target"}}},
             {"id":3,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":1,"port.name":"monitor_FL"}}},
             {"id":4,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"2","port.name":"playback_FL"}}},
             {"id":5,"type":"PipeWire:Interface:Link","info":{"output-port-id":3,"input-port-id":4}},
             {"id":6,"type":"PipeWire:Interface:Link","info":{"output-port-id":3,"input-port-id":99}}]
            """);
        Assert.Equal(("source:monitor_FL", "target:playback_FL"), Assert.Single(PipeWireAdapter.ParseGraphLinks(document.RootElement.EnumerateArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADelayedRightPortRetriesAndFailedAttemptsRemoveTheLeftLink(bool permanent)
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-stereo-link-").FullName;
        string? previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
                #!/bin/sh
                directory=${0%/*}
                case "$1" in
                  -o) printf 'source:monitor_FL\nsource:monitor_FR\n'; exit 0;;
                  -i) printf 'target:playback_FL\ntarget:playback_FR\n'; exit 0;;
                  -d) printf '0' > "$directory/left"; printf 'cleaned\n' >> "$directory/cleanup"; exit 0;;
                  source:monitor_FL) printf '1' > "$directory/left"; exit 0;;
                  source:monitor_FR)
                    if [ ! -f "$directory/attempted" ] || [ '{{permanent}}' = True ]; then
                      printf '1' > "$directory/attempted"; exit 1
                    fi
                    exit 0;;
                esac
                exit 1
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + previous);
            var adapter = new PipeWireAdapter();
            if (permanent)
            {
                Assert.Throws<InvalidOperationException>(() => adapter.LinkStereoNodes("source", "monitor", "target", "playback", TimeSpan.FromMilliseconds(100)));
                Assert.Equal("0", File.ReadAllText(Path.Combine(directory, "left")));
                Assert.NotEmpty(File.ReadAllLines(Path.Combine(directory, "cleanup")));
            }
            else
            {
                var link = adapter.LinkStereoNodes("source", "monitor", "target", "playback");
                Assert.Equal(2, link.Pairs.Count);
                Assert.Equal("1", File.ReadAllText(Path.Combine(directory, "left")));
                Assert.Single(File.ReadAllLines(Path.Combine(directory, "cleanup")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
            Directory.Delete(directory, true);
        }
    }
}
