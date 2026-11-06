using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The Own clock switch in an input rack and a mix matrix, with no window:
/// the rack draws it in the switches part, the matrix in the row's
/// processing flyout, both only for a capture input the daemon reports an
/// own clock for, and the matrix's processing button is lit while it is on.
///
/// The window's model reads ui.json when it is made, from the configuration
/// directory the tests of that collection redirect for the whole process.
/// </summary>
[Collection("xdg-config")]
public sealed class CaptureOwnClockStripTests
{
    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string State(string? ownClock) => $$"""
        {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1},
                     {"id":"card","name":"4K X","captureSource":"alsa_input.usb-card","captureConnected":true,
                      "microphone":false,"chainWidth":2,"volume":0.8,"unitInput":null{{(ownClock is null ? "" : ",\"ownClock\":" + ownClock)}}},
                     {"id":"game","name":"Game","volume":1}],
         "mixes":[{"id":"monitor","name":"Monitor A"}]}
        """;

    private const string Off = """{"on":false,"running":false,"delayMs":null,"targetMs":null,"underruns":0,"overruns":0,"driftPpm":null,"error":null}""";
    private const string Running = """{"on":true,"running":true,"delayMs":26.7,"targetMs":26.7,"underruns":0,"overruns":0,"driftPpm":-3,"error":null}""";

    [Fact]
    public void TheRackAndTheMatrixDrawTheSwitch()
    {
        var model = new MainViewModel(new DaemonClient());
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(Off))]);
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        InputStripViewModel strip = rack.Strips.Single(s => s.Channel.Id == "card");
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "card");

        // A capture input without a gain of its own still gets the switches part, for the switch alone.
        Assert.False(strip.ShowsCaptureMute);
        Assert.True(strip.ShowsOwnClock);
        Assert.Contains(SkinStripParts.HardwareControls, strip.Spec.Parts);
        Assert.True(row.ShowsOwnClock);
        Assert.True(row.ShowsProcessing);
        Assert.False(row.ProcessingOn);

        ApplyMixer.Invoke(model, [JsonNode.Parse(State(Running))]);
        Assert.True(strip.Channel.OwnClock);
        Assert.True(row.ProcessingOn);
        Assert.NotNull(strip.Channel.OwnClockStatus);

        // An older daemon reports no own clock: no switch, and the parts go with it.
        ApplyMixer.Invoke(model, [JsonNode.Parse(State(null))]);
        Assert.False(strip.ShowsOwnClock);
        Assert.DoesNotContain(SkinStripParts.HardwareControls, strip.Spec.Parts);
        Assert.False(row.ShowsOwnClock);
        Assert.False(row.ShowsProcessing);

        // XLR 1 never has one.
        Assert.False(rack.Strips.Single(s => s.Channel.Id == "xlr1").ShowsOwnClock);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "xlr1").ShowsOwnClock);
    }
}
