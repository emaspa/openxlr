using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The device volume range the mixer reads and writes, and what it remembers
/// about outputs that refused a write.
/// </summary>
public sealed class OutputVolumeTests
{
    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.4, "40%")]
    [InlineData(1.0, "100%")]
    [InlineData(1.2, "120%")]
    [InlineData(1.5, "150%")]
    [InlineData(2.0, "150%")]
    [InlineData(-1.0, "0%")]
    public void ADeviceVolumeIsWrittenOverTheWholeRangeItIsReadIn(double volume, string expected)
        => Assert.Equal(expected, PipeWireAdapter.VolumePercent(volume));

    [Fact]
    public void ABoostedDesktopVolumeSurvivesTheReadAndWriteRoundTrip()
    {
        // pactl reports a boost past unity, and what comes back has to be
        // writable as it stands; anything else copies 120% as 100% to the
        // other selected outputs and then believes both are at 120%.
        double? read = PipeWireAdapter.ParseVolumePercent(
            "Volume: front-left: 78642 / 120% / 1.58 dB,   front-right: 78642 / 120% / 1.58 dB");
        Assert.Equal(1.2, read);
        Assert.Equal("120%", PipeWireAdapter.VolumePercent(read!.Value));
        // A lock at 150% must still see when the desktop writes a higher level.
        Assert.Equal(2, PipeWireAdapter.ParseVolumePercent("Volume: front-left: 98304 / 200% / 6.02 dB"));
        Assert.Null(PipeWireAdapter.ParseVolumePercent("Volume: 999999999999%"));
        Assert.Null(PipeWireAdapter.ParseVolumePercent("Failure: No such entity"));
        Assert.Null(PipeWireAdapter.ParseVolumePercent(null));
    }

    [Fact]
    public void AnOutputThatTookItsVolumeIsNeverWrittenAgain()
    {
        var due = new OutputVolumeSync();
        due.Failed("speakers", 0.2);
        Assert.Equal([("speakers", 0.2)], due.Due());
        due.Delivered("speakers");
        Assert.Empty(due.Due());
    }

    [Fact]
    public void AFailedWriteIsRetriedForAFewSweepsAndThenWaitsForTheDeviceToReturn()
    {
        var due = new OutputVolumeSync();
        due.Failed("speakers", 0.2);
        for (int sweep = 0; sweep < OutputVolumeSync.Attempts; sweep++)
        {
            Assert.Equal([("speakers", 0.2)], due.Due());
            due.Failed("speakers", 0.2);
        }
        Assert.Empty(due.Due());

        // The monitor route to it was rebuilt: the device is back.
        due.Rearm("speakers");
        Assert.Equal([("speakers", 0.2)], due.Due());
        due.Delivered("speakers");
        due.Rearm("speakers");
        Assert.Empty(due.Due());
    }

    [Fact]
    public void ANewerVolumeReplacesWhatAnOutputWasOwed()
    {
        var due = new OutputVolumeSync();
        due.Failed("speakers", 0.2);
        due.Failed("speakers", 0.2);
        due.Failed("speakers", 0.6);
        Assert.Equal([("speakers", 0.6)], due.Due());
        for (int sweep = 0; sweep < OutputVolumeSync.Attempts; sweep++) due.Failed("speakers", 0.6);
        Assert.Empty(due.Due());
    }

    [Fact]
    public void OutputsThatLeaveTheSelectionOrMeetAFreshBaselineAreForgotten()
    {
        var due = new OutputVolumeSync();
        due.Failed("speakers", 0.2);
        due.Failed("headset", 0.2);
        Assert.Equal([("headset", 0.2), ("speakers", 0.2)], due.Due());
        due.Keep(["speakers"]);
        Assert.Equal([("speakers", 0.2)], due.Due());
        due.Clear();
        Assert.Empty(due.Due());
    }
}
