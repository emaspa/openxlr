using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.InputBindingModelTests;

namespace OpenXLR.Tests;

/// <summary>
/// The hardware strips' own volume in the window's model, without a window:
/// XLR 1, XLR 2 and Aux In take one as any channel does, a bound strip keeps
/// it, and the channel that the Wave XLR Pro's direct path carries to the
/// jacks says its volume reaches the mixes only.
/// </summary>
public sealed class InputChannelVolumeModelTests
{
    private static void Apply(MainViewModel model, string state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse(state)!]);

    /// <summary>The Pro's state with its direct path on or off.</summary>
    private static string WithDirectPath(string state, bool on)
        => state.Replace("\"connected\":true,", "\"connected\":true,\"state\":{\"hpMixMicDirect\":" + (on ? "true" : "false") + "},",
            StringComparison.Ordinal);

    private static string Volume(string channel, double volume)
        => channel[..^1] + ",\"volume\":" + volume.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

    [Fact]
    public async Task TheDirectPathMarksTheChannelThatCarriesTheProsFirstInput()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new MainViewModel(client);
        using var rack = new InputRackViewModel(model);
        using var matrix = new MixMatrixViewModel(model);
        string onTheInterface = ProState($"{Volume(Xlr("xlr1", 0, Input(1)), 0.9)},{Volume(Xlr("xlr2", 1, Input(2)), 1)}");

        // Both strips on the interface, the path off: each has its volume and
        // the plain tooltip.
        Apply(model, WithDirectPath(onTheInterface, false));
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1"), xlr2 = model.Channels.Single(c => c.Id == "xlr2");
        Assert.True(xlr1.HasVolume);
        Assert.Equal(0.9, xlr1.Volume);
        Assert.True(xlr2.HasVolume);
        Assert.False(model.HpMixMicDirect);
        Assert.False(xlr1.DirectMonitored);
        Assert.Equal(Localizer.Text("ChannelVolumeTip"), xlr1.VolumeTip);
        // The rack draws the gain and the volume on XLR 1, the matrix stacks them.
        Assert.True(rack.Strips.Single(s => s.Channel.Id == "xlr1").ShowsVolume);
        Assert.Contains(SkinStripParts.Gain, rack.Strips.Single(s => s.Channel.Id == "xlr1").Spec.Parts);
        Assert.True(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked);

        // The path on: XLR 1 carries input 1 to the jacks, XLR 2 does not.
        Apply(model, WithDirectPath(onTheInterface, true));
        Assert.True(model.HpMixMicDirect);
        Assert.True(xlr1.DirectMonitored);
        Assert.False(xlr2.DirectMonitored);
        Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"), xlr1.VolumeTip);
        Assert.Equal(Localizer.Text("ChannelVolumeTip"), xlr2.VolumeTip);

        // XLR 1 bound to a webcam and the Pro's microphone kept as a capture
        // channel: the path follows the capture channel, and XLR 1 keeps its
        // volume, now its head's only fader.
        Apply(model, WithDirectPath(ProState(
            $"{Volume(BoundXlr1(true), 0.9)},{Volume(Xlr("xlr2", 1, Input(2)), 1)},{ProMic(Input(1))}"), true));
        ChannelViewModel proMic = model.Channels.Single(c => c.Id == "promic");
        Assert.False(xlr1.DirectMonitored);
        Assert.True(proMic.DirectMonitored);
        Assert.Equal(0.9, xlr1.Volume);
        Assert.Contains(SkinStripParts.Gain, rack.Strips.Single(s => s.Channel.Id == "xlr1").Spec.Parts);
        Assert.False(rack.Strips.Single(s => s.Channel.Id == "xlr1").CarriesUnit);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked);
        Assert.True(matrix.Rows.Single(r => r.Channel.Id == "promic").Stacked);

        // A state without the unit takes the mark away with the path.
        Apply(model, ProState($"{Volume(Xlr("xlr1", 0, null), 0.9)},{Volume(Xlr("xlr2", 1, null), 1)}", connected: false));
        Assert.False(model.HpMixMicDirect);
        Assert.All(model.Channels, c => Assert.False(c.DirectMonitored));
    }

    [Fact]
    public void EveryLanguageNamesTheNote()
    {
        foreach (string key in new[] { "ChannelVolumeDirectTip", "ChannelVolumeMixesOnly", "ChannelVolumeName", "ChannelVolumeTip" })
            foreach (string language in new[] { "de", "es", "fr", "it" })
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
                string? translated = Localizer.Resources.GetString(key, culture);
                Assert.False(string.IsNullOrEmpty(translated), $"{key} in {language}");
                Assert.NotEqual(Localizer.Resources.GetString(key, System.Globalization.CultureInfo.InvariantCulture), translated);
            }
    }
}
