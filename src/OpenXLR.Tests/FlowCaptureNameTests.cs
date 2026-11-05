using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>Flow names a capture input's source the way its source chip does.</summary>
public sealed class FlowCaptureNameTests
{
    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void ACaptureInputShowsItsDeviceNameAndFallsBackToTheNodeName()
    {
        var model = new MainViewModel(new DaemonClient());
        model.Inputs.Add(new AudioDeviceItem("alsa_input.usb-046d_Logi_4K_Stream_Edition-03.analog-stereo", "BRIO 4K Stream Edition", false, true, 2));
        string mixer = """
            {"channels":[{"id":"brio","name":"Brio","captureSource":"alsa_input.usb-046d_Logi_4K_Stream_Edition-03.analog-stereo","captureConnected":true,"volume":1},
                         {"id":"gone","name":"Gone","captureSource":"alsa_input.usb-unplugged-00.mono-fallback","captureConnected":false,"volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}]}
            """;
        ApplyMixer.Invoke(model, [JsonNode.Parse(mixer)]);

        FlowGraph graph = FlowGraph.From(model);
        Assert.Equal("BRIO 4K Stream Edition", graph.Nodes.Single(n => n.Key == "capture:brio").Label);
        Assert.Equal("alsa_input.usb-unplugged-00.mono-fallback", graph.Nodes.Single(n => n.Key == "capture:gone").Label);
    }
}
