using OpenXLR.Core.Mixing;
using Xunit;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's microphone path to the jacks follows the output volume
/// down, as the Monitor mix on the jacks does, and is never raised by it.
/// The unit's own loss on a USB playback pair is not taken off.
/// </summary>
public sealed class JackOutputVolumeTests
{
    [Theory]
    [InlineData(null, 0.0)]         // not read yet: 100%
    [InlineData(1.0, 0.0)]
    [InlineData(0.7, -9.294)]       // the desk's 70% on the cubic scale
    [InlineData(0.5, -18.062)]
    [InlineData(1.3, 0.0)]          // lifts the pair only; the voice keeps the send's level
    [InlineData(1.5, 0.0)]
    public void FollowsTheOutputVolumeDown(double? volume, double expected)
        => Assert.Equal(expected, Mixer.JackOutputVolumeDb(volume), 3);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void ASilentOutputSilencesThePath(double volume)
        => Assert.Equal(double.NegativeInfinity, Mixer.JackOutputVolumeDb(volume));

    [Fact]
    public void NeverRaisesTheVoice()
    {
        for (double v = 0.01; v <= 1.5; v += 0.01)
            Assert.True(Mixer.JackOutputVolumeDb(v) <= 0);
    }
}
