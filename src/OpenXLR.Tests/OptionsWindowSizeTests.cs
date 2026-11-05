using Avalonia;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The one size Options keeps on every tab, and what it gives up on a screen
/// too small for it. The window tests check the size it really opens at.
/// </summary>
public sealed class OptionsWindowSizeTests
{
    [Fact]
    public void ARoomyScreenGetsTheWindowsOwnSize() =>
        Assert.Equal(new Size(OptionsWindow.DesignWidth, OptionsWindow.Comfortable), OptionsWindow.FitTo(new Size(1920, 1040)));

    [Fact]
    public void A720LineScreenAtDoubleScaleKeepsTheWidthAndShortensTheHeight() =>
        // 2560 by 1440 at 200% is 1280 by 720 units, less a 30 unit panel.
        Assert.Equal(new Size(OptionsWindow.DesignWidth, 630), OptionsWindow.FitTo(new Size(1280, 690)));

    [Fact]
    public void ANarrowScreenNarrowsTheWindow() =>
        Assert.Equal(new Size(568, 440), OptionsWindow.FitTo(new Size(600, 500)));

    [Fact]
    public void AScreenThatReportsNothingIsNotBelieved() =>
        Assert.Equal(new Size(OptionsWindow.DesignWidth, OptionsWindow.Comfortable), OptionsWindow.FitTo(default));
}
