using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The LEDs window's model, with no window: it shows only for a unit whose
/// capabilities report <c>ledRing</c>, takes the state's <c>led</c> without
/// sending anything back, sends one <c>setLed</c> per change, a colour only
/// once its field holds a whole <c>#rrggbb</c>, and reset with no value.
/// </summary>
public sealed class LedRingViewModelTests
{
    private sealed class Recorder : ILedCommands
    {
        public readonly ConcurrentQueue<(string Control, object? Value)> Sent = new();
        public string? Refusal;

        public Task<string?> SetLedAsync(string control, object? value)
        {
            Sent.Enqueue((control, value));
            return Task.FromResult(Refusal);
        }
    }

    private static JsonNode Led(int ring = 100, int meter = 100, string gain = "#ffffff", string headphone = "#ffffff", string mix = "#ffffff")
        => JsonNode.Parse($$"""
            {"ringBrightness":{{ring}},"meterBrightness":{{meter}},"gainColour":"{{gain}}","headphoneColour":"{{headphone}}","mixColour":"{{mix}}"}
            """)!;

    [Fact]
    public void TheStateFillsTheModelAndSendsNothing() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var led = new LedRingViewModel(sent);
        Assert.False(led.Available);

        led.Apply(true, Led(ring: 40, meter: 3, gain: "#0000ff", headphone: "#ff0000", mix: "#00ff00"));
        Assert.True(led.Available);
        Assert.Equal((40, 3, "#0000ff", "#ff0000", "#00ff00"),
            (led.RingBrightness, led.MeterBrightness, led.GainColour, led.HeadphoneColour, led.MixColour));
        Assert.Equal("40%", led.RingBrightnessText);
        SliderSync.FlushPending();
        Assert.Empty(sent.Sent);

        // A unit without the capability, or a state without the field, hides it.
        led.Apply(false, Led());
        Assert.False(led.Available);
        led.Apply(true, null);
        Assert.False(led.Available);
    });

    [Fact]
    public void EachChangeIsOneSetLed() => TestUi.Run(() =>
    {
        var sent = new Recorder();
        var led = new LedRingViewModel(sent);
        led.Apply(true, Led());

        led.RingBrightness = 25;
        led.MeterBrightness = 60;
        SliderSync.FlushPending();
        Assert.Equal([("ringBrightness", (object?)25), ("meterBrightness", (object?)60)], sent.Sent.ToArray());
        sent.Sent.Clear();

        // A colour being typed is sent once it is whole, in lower case.
        led.GainColour = "#00F";
        Assert.Empty(sent.Sent);
        // The state that comes back meanwhile leaves the field being typed in alone.
        led.Apply(true, Led());
        Assert.Equal("#00F", led.GainColour);
        led.GainColour = "#0000FF";
        led.HeadphoneColour = " #ff0000 ";
        led.MixColour = "#00ff00";
        Assert.Equal([("gainColour", (object?)"#0000ff"), ("headphoneColour", (object?)"#ff0000"), ("mixColour", (object?)"#00ff00")],
            sent.Sent.ToArray());
        sent.Sent.Clear();

        sent.Refusal = "setLed: the active device has no LEDs OpenXLR can set";
        led.ResetAsync().GetAwaiter().GetResult();
        Assert.Equal([("reset", (object?)null)], sent.Sent.ToArray());
        Assert.True(led.HasError);
        Assert.Equal(sent.Refusal, led.Error);

        led.Reset();
        Assert.False(led.Available);
        Assert.False(led.HasError);
    });
}
