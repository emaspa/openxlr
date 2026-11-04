using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class RegistryPortTests
{
    [Fact]
    public void DuplicateNodeNamesKeepPortsGroupedInNumericNodeOrder()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":90,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"mic"}}},
             {"id":9,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"mic"}}},
             {"id":5,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":90,"port.id":0,"port.name":"second_L"}}},
             {"id":99,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":"9","port.id":"1","port.name":"first_R"}}},
             {"id":101,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":9,"port.id":0,"port.name":"first_L"}}}]
            """);
        Assert.Equal(["mic:first_L", "mic:first_R", "mic:second_L"],
            PipeWireAdapter.ObservedPorts(doc.RootElement.EnumerateArray(), true));
    }

    [Fact]
    public void PortsKeepTheirChannelOrderAcrossRegistryIdReuseAndIgnoreOrphans()
    {
        using var doc = JsonDocument.Parse("""
            [{"id":90,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"mic"}}},
             {"id":5,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":90,"port.id":1,"port.name":"capture_FR"}}},
             {"id":99,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":"90","port.id":"0","port.name":"capture_FL"}}},
             {"id":8,"type":"PipeWire:Interface:Port","info":{"direction":"input","props":{"node.id":90,"port.id":0,"port.name":"playback_FL"}}},
             {"id":7,"type":"PipeWire:Interface:Port","info":{"direction":"output","props":{"node.id":91,"port.id":0,"port.name":"capture_FL"}}}]
            """);
        JsonElement[] objects = [.. doc.RootElement.EnumerateArray()];
        Assert.Equal(["mic:capture_FL", "mic:capture_FR"], PipeWireAdapter.ObservedPorts(objects, true));
        Assert.Equal(["mic:playback_FL"], PipeWireAdapter.ObservedPorts(objects, false));
    }

    [Fact]
    public void AStreamLinkedIntoOneSinkIsAnsweredFromTheRegistry()
    {
        static JsonElement[] Graph(string links) => [.. JsonDocument.Parse($$$$"""
            [{"id":10,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"desk","media.class":"Audio/Sink"}}},
             {"id":11,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"headset","media.class":"Audio/Sink"}}},
             {"id":12,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"filter","media.class":"Stream/Input/Audio"}}},
             {"id":20,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"Chromium","media.class":"Stream/Output/Audio","object.serial":"4711"}}}
             {{{{links}}}}]
            """).RootElement.EnumerateArray()];
        const string ToDesk = """,{"id":30,"type":"PipeWire:Interface:Link","info":{"output-node-id":20,"input-node-id":10}}""";
        Assert.Equal("desk", PipeWireAdapter.LinkedSink(Graph(ToDesk), 4711));
        // Both channels of a stereo stream land on the same sink.
        Assert.Equal("desk", PipeWireAdapter.LinkedSink(Graph(ToDesk + ToDesk.Replace("30", "31", StringComparison.Ordinal)), 4711));
        Assert.Null(PipeWireAdapter.LinkedSink(Graph(ToDesk), 4712));
        // Unlinked, split across sinks or linked into a filter: pactl answers.
        Assert.Null(PipeWireAdapter.LinkedSink(Graph(""), 4711));
        Assert.Null(PipeWireAdapter.LinkedSink(Graph(ToDesk + ToDesk.Replace("30", "31", StringComparison.Ordinal).Replace(":10}", ":11}", StringComparison.Ordinal)), 4711));
        Assert.Null(PipeWireAdapter.LinkedSink(Graph(ToDesk.Replace(":10}", ":12}", StringComparison.Ordinal)), 4711));
    }
}
