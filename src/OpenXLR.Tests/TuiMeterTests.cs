using OpenXLR.Tui;

namespace OpenXLR.Tests;

public sealed class TuiMeterTests
{
    private static Theme Zones() => Theme.FromJson("""
        {"tokens":{"Ox.Meter.Fill":"#00ff00","Ox.Meter.Warning":"#ffff00","Ox.Meter.Hot":"#ff0000"}}
        """, "test", "Test");

    [Fact]
    public void AVerticalMeterFillsFromTheBottomInEighths()
    {
        Screen screen = new(4, 10);
        Theme theme = Zones();
        screen.Clear(theme.Card);
        Widgets.VerticalMeter(screen, 1, 0, 10, 0.35, 0, theme, theme.Card);
        Assert.Equal('█', screen.At(1, 9).Ch);
        Assert.Equal('█', screen.At(1, 7).Ch);
        Assert.Equal('▄', screen.At(1, 6).Ch);
        Assert.Equal('│', screen.At(1, 5).Ch);
        Assert.Equal(theme.Groove, screen.At(1, 0).Fore);
    }

    [Fact]
    public void AVerticalMetersZonesStayAtTheSameHeightAsTheLevelChanges()
    {
        Screen screen = new(2, 10);
        Theme theme = Zones();
        Widgets.VerticalMeter(screen, 0, 0, 10, 1, 0, theme, theme.Card);
        Widgets.VerticalMeter(screen, 1, 0, 10, 0.8, 0, theme, theme.Card);
        Assert.Equal(theme.MeterFill, screen.At(0, 9).Fore);
        // Row 2 sits at 0.8 of the scale, between the warning and the hot level.
        Assert.Equal(theme.MeterColour(0.8), screen.At(0, 2).Fore);
        Assert.NotEqual(theme.MeterWarning, screen.At(0, 2).Fore);
        Assert.NotEqual(theme.MeterHot, screen.At(0, 2).Fore);
        Assert.Equal(screen.At(0, 2).Fore, screen.At(1, 2).Fore);
        Assert.Equal(theme.MeterHot, screen.At(0, 0).Fore);
    }

    [Fact]
    public void TheBarIsSolidAndTheHoldMarkWearsTheColourOfItsPlace()
    {
        Screen screen = new(2, 10);
        Theme theme = Zones();
        Widgets.VerticalMeter(screen, 0, 0, 10, 0.3, 0.8, theme, theme.Card, width: 2);
        Assert.Equal('█', screen.At(0, 9).Ch);
        Assert.Equal('█', screen.At(1, 9).Ch);
        Assert.Equal('━', screen.At(0, 2).Ch);
        Assert.Equal(theme.MeterColour(0.8), screen.At(0, 2).Fore);
        Assert.Equal('│', screen.At(0, 3).Ch);
    }

    [Fact]
    public void AVerticalFaderShowsBoostAndStopsAtItsCeiling()
    {
        Screen screen = new(10, 10);
        Theme theme = Theme.Material;
        Widgets.VerticalFader(screen, 1, 0, 10, 1, 1.5, theme, theme.Card, false);
        Assert.Equal('━', screen.At(1, 3).Ch);
        Assert.Equal(theme.FaderThumb, screen.At(1, 3).Back);
        Widgets.VerticalFader(screen, 6, 0, 10, 2, 1.5, theme, theme.Card, true);
        Assert.Equal('━', screen.At(6, 0).Ch);
        Assert.Equal(theme.Accent, screen.At(6, 0).Back);
    }

    [Fact]
    public void StereoHoldsWaitOneSecondThenFallByEighteenDecibelsPerSecond()
    {
        MeterTrace trace = new();
        trace.Push(0.8, 0.4, 0);
        trace.Push(0.1, 0.2, 0.5);
        MeterReading held = trace.Read(0.9);
        Assert.Equal(0.1, held.Left);
        Assert.Equal(0.2, held.Right);
        Assert.Equal(0.8, held.HoldLeft);
        Assert.Equal(0.4, held.HoldRight);
        Assert.Equal(0.65, trace.Read(1.5).HoldLeft, 6);
        Assert.Equal(0.25, trace.Read(1.5).HoldRight, 6);
        Assert.Equal(0, trace.Read(5).Hold);
    }

    [Fact]
    public void ARisingLevelImmediatelyRestartsTheHold()
    {
        MeterTrace trace = new();
        trace.Push(0.5, 0.2, 0);
        trace.Push(0.9, 0.6, 1.2);
        Assert.Equal(0.9, trace.Read(2.1).HoldLeft);
        Assert.Equal(0.6, trace.Read(2.1).HoldRight);
        Assert.Equal(0.75, trace.Read(2.7).HoldLeft, 6);
    }

    [Fact]
    public void AHoldCannotFallBelowTheCurrentReadingBetweenPackets()
    {
        MeterTrace trace = new();
        trace.Push(0.8, 0.8, 0);
        trace.Push(0.55, 0.55, 1.8);
        MeterReading between = trace.Read(1.85);
        Assert.Equal(between.Level, between.Hold);
        Assert.Equal(0.55, between.Hold);
    }

    [Fact]
    public void StaleReadingsGoToZeroWithoutFreezingTheHold()
    {
        MeterTrace trace = new();
        trace.Push(1, 0.5, 0);
        Assert.Equal(1, trace.Read(0.99).Left);
        Assert.Equal(0, trace.Read(1).Level);
        Assert.Equal(0.7, trace.Read(2).Hold, 6);
        Assert.Equal(0, trace.Read(10).Hold);
    }

    [Fact]
    public void HistoryUsesFixedTimeBucketsAndKeepsTheLoudestSampleInEach()
    {
        MeterTrace trace = new();
        trace.Push(0.8, 0.1, 0.2);
        trace.Push(0.1, 0.7, 0.25);
        double[] history = trace.History(0.35);
        Assert.Equal(MeterTrace.Samples, history.Length);
        Assert.Equal(0.8, history[^2]);
        Assert.Equal(0, history[^1]);
        Assert.All(trace.History(16), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void MoreThanFifteenSecondsOfSamplesWrapWithoutGrowingTheHistory()
    {
        MeterTrace trace = new();
        for (int i = 0; i < 1000; i++) trace.Push(0.4, 0.6, i * 0.1 + 0.01);
        double[] history = trace.History(99.92);
        Assert.Equal(150, history.Length);
        Assert.All(history, sample => Assert.Equal(0.6, sample));
    }

    [Fact]
    public void CompressingHistoryKeepsShortBurstsAndColoursByHeight()
    {
        Screen screen = new(2, 10);
        Theme theme = Zones();
        Widgets.History(screen, new Rect(0, 0, 2, 10), [0, 0.8, 0.1, 0.2], theme, theme.Card);
        Assert.Equal('█', screen.At(0, 2).Ch);
        Assert.Equal(theme.MeterColour(0.8), screen.At(0, 2).Fore);
        Assert.Equal(theme.MeterFill, screen.At(0, 9).Fore);
        Assert.Equal(' ', screen.At(1, 7).Ch);
        Assert.Equal('█', screen.At(1, 8).Ch);
    }

    [Fact]
    public void InvalidSamplesNeverReachTheDrawings()
    {
        MeterTrace trace = new();
        trace.Push(double.NaN, double.PositiveInfinity, 0);
        Assert.Equal(default, trace.Read(0));
        trace.Push(-5, 5, 0.1);
        Assert.Equal(0, trace.Read(0.1).Left);
        Assert.Equal(1, trace.Read(0.1).Right);
    }

    [Fact]
    public void TheConnectionPreservesStereoAndDropsRemovedMeters()
    {
        DaemonLink link = new();
        link.Receive("""{"type":"meters","levels":{"mix:stream":[0.2,0.7],"ch:mic":[0.4]}}""");
        Assert.Equal(0.2, link.StereoMeter("mix", "stream").Left);
        Assert.Equal(0.7, link.StereoMeter("mix", "stream").Right);
        Assert.Equal(0.4, link.StereoMeter("ch", "mic").Right);
        Assert.Contains(0.7, link.MeterHistory("mix", "stream"));
        link.Receive("""{"type":"meters","levels":{}}""");
        Assert.Equal(default, link.StereoMeter("mix", "stream"));
        Assert.All(link.MeterHistory("mix", "stream"), sample => Assert.Equal(0, sample));
    }
}
