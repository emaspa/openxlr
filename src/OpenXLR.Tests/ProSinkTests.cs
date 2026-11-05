using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The Pro's raw sink, behind its jack outputs, is taken from the listing's
/// pseudo-outputs and never from another Wave XLR's sink.
/// </summary>
public sealed class ProSinkTests
{
    private const string Pro = "alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_AAY3H5481241GS-00.pro-output-0";
    private const string Mk2 = "alsa_output.usb-Elgato_Elgato_Wave_XLR_MK.2_AAY4I61912AK30-00.analog-stereo";

    private static AudioNode Sink(string name) => new(name, name, AudioNodeKind.Sink, IsOwn: false, IsPhysical: true);

    [Fact]
    public void AnotherWaveXlrListedFirstIsNotTakenForThePro()
    {
        AudioNode[] devices = [Sink(Mk2), Sink(Pro + "#hp1"), Sink(Pro + "#hp2"), Sink(Pro + "#lineout")];
        Assert.Equal(Pro, Mixer.ProSinkOf(devices));
    }

    [Fact]
    public void WithoutJackOutputsThereIsNoProSink()
    {
        AudioNode[] devices = [Sink(Mk2), new(Pro + "#hp1", "Headphones 1", AudioNodeKind.Source, IsOwn: false)];
        Assert.Null(Mixer.ProSinkOf(devices));
    }
}
