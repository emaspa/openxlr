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
}
