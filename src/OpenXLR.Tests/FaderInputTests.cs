using Avalonia.Controls;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class FaderInputTests
{
    [Theory]
    [InlineData(.8, 20, 200, 0, 1, .01, .81)]
    [InlineData(.8, -20, 200, 0, 1, .01, .79)]
    [InlineData(1.49, 200, 200, 0, 1.5, .01, 1.5)]
    [InlineData(.01, -200, 200, 0, 1, .01, 0)]
    [InlineData(-30, 20, 120, -60, 0, 1, -29)]
    [InlineData(.8, 20, 0, 0, 1, .01, .8)]
    [InlineData(.8, 20, 200, 1, 1, .01, .8)]
    [InlineData(.8, 20, 200, 0, 1, 0, .8)]
    public void FineDragStepsAndBounds(double value, double distance, double span,
        double min, double max, double step, double expected)
        => Assert.Equal(expected, FaderInput.FineValue(value, distance, span, min, max, step), 10);

    internal static void StepsDoNotQuantizeIncomingValuesOrPluginControls()
    {
        var slider = new Slider { Maximum = 1.5, Value = .755 };
        FaderInput.SetStep(slider, .01);
        Assert.Equal(.755, slider.Value);
        Assert.Equal(.01, slider.SmallChange);
        Assert.Equal(.1, slider.LargeChange);
        slider.Value = 1.234;
        Assert.Equal(1.234, slider.Value);
        var plugin = new Slider { Minimum = 20, Maximum = 20000, SmallChange = 3 };
        Assert.Equal(0, FaderInput.GetStep(plugin));
        Assert.Equal(3, plugin.SmallChange);
    }
}
