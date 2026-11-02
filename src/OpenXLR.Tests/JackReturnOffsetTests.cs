using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using Xunit;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's microphone path to the jacks takes off what the Monitor
/// mix loses on its way there: the unit's loss on a USB playback pair and the
/// output volume below 100%, so the voice sits against the computer audio
/// where the faders put it, and is never raised.
/// </summary>
public sealed class JackReturnOffsetTests
{
    [Theory]
    [InlineData(null, -7.94)]       // not read yet: 100%
    [InlineData(1.0, -7.94)]
    [InlineData(0.7, -17.234)]      // the desk's 9.29 dB on the cubic scale
    [InlineData(0.5, -26.002)]
    [InlineData(1.3, -1.103)]
    [InlineData(1.5, 0.0)]          // lifts the pair 10.6 dB; the voice keeps the send's level
    public void TakesOffThePairLossAndTheOutputVolume(double? volume, double expected)
        => Assert.Equal(expected, Mixer.JackReturnOffsetDb(volume), 3);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void ASilentOutputSilencesThePath(double volume)
        => Assert.Equal(double.NegativeInfinity, Mixer.JackReturnOffsetDb(volume));

    [Fact]
    public void TheLossIsTheMeasuredOne() => Assert.Equal(7.94, ProReturnPairs.LossDb);

    [Fact]
    public void NeverRaisesTheVoice()
    {
        for (double v = 0.01; v <= 1.5; v += 0.01)
            Assert.True(Mixer.JackReturnOffsetDb(v) <= 0);
    }
}
