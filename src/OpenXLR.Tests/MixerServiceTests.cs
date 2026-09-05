using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class MixerServiceTests
{
    [Theory]
    [InlineData("--mixer", true)]
    [InlineData("--MIXER", true)]
    [InlineData("--mixer=false", false)]
    [InlineData("--monitorOutput", false)]
    public void BareMixerSwitchMatchesOnlyDocumentedFlag(string argument, bool expected)
        => Assert.Equal(expected, MixerService.HasBareMixerSwitch(["OpenXLR.Daemon", argument]));
}
