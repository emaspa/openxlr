using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using DaemonBand = OpenXLR.Core.Mixing.EqBand;
using DaemonEq = OpenXLR.Core.Mixing.MicEq;
using DaemonResponse = OpenXLR.Core.Mixing.EqResponse;

namespace OpenXLR.Tests;

/// <summary>
/// The EQ editor's model, with no window: the curve it draws is the daemon's
/// own <c>EqResponse</c> at every point, a position on the graph maps to a
/// frequency on the log axis and a gain, both snapped on the daemon's steps
/// and held to its ranges; a wheel notch is one step of the Q grid; a type
/// without a gain keeps its gain and takes the gain field away; values the
/// daemon reports are what the editor shows, unless they are an older echo of
/// an edit; band changes go out at most once a send interval per band,
/// merged and never losing the last; the comparison switches the EQ off and
/// on while the switch stays on; copy and paste and reset send one setEq;
/// and the window's model makes an EQ only for a channel the daemon reports
/// one for.
/// </summary>
[Collection("xdg-config")]
public sealed class EqEditorModelTests
{
    /// <summary>The four commands as the editor sends them.</summary>
    private sealed class Recorder : IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();

        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }

        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }

        public Task SetEqProcessingAsync(string channel, string value) { Sent.Enqueue(("setEqProcessing", channel, value)); return Task.CompletedTask; }

        public Task SetEqBandAsync(string channel, int band, EqBandChange change)
        {
            Sent.Enqueue(("setEqBand", channel, (band, change)));
            return Task.CompletedTask;
        }

        public (int Band, EqBandChange Change) Band()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Equal("setEqBand", sent.Command);
            Assert.Empty(Sent);
            return ((int, EqBandChange))sent.Value;
        }
    }

    private static (MicEqViewModel Eq, Recorder Sent) Model(string channel = "xlr1", ManualTime? time = null)
    {
        var sent = new Recorder();
        return (new MicEqViewModel(sent, channel, channel == "xlr1" ? "XLR 1" : "XLR 2", TimeSpan.Zero, time ?? new ManualTime()), sent);
    }

    private static JsonNode State(bool enabled, string bands, string backend = "\"software\"") => JsonNode.Parse(
        $$"""{"enabled":{{(enabled ? "true" : "false")}},"bands":[{{bands}}],"backend":{{backend}},"available":true,"error":null}""")!;

    private const string Defaults = """
        {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
        {"enabled":true,"type":"peak","frequency":310,"gain":0,"q":0.7046930689671475},
        {"enabled":true,"type":"peak","frequency":2172,"gain":0,"q":0.7046930689671475},
        {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
        """;

    private static readonly Rect Plot = new(30, 6, 640, 300);

    [Fact]
    public void TheCurveIsTheDaemonsResponseAtEveryPointOnALogAxis()
    {
        // The texts below are written as an English desktop formats numbers.
        using var culture = new CultureScope("en-US");
        EqBand[] bands =
        [
            new() { Enabled = true, Type = MicEq.LowCut, Frequency = 120, Q = MicEq.SnapQ(4) },
            new() { Enabled = true, Type = MicEq.Peak, Frequency = 1000, Gain = 9.25, Q = MicEq.SnapQ(2) },
            new() { Enabled = true, Type = MicEq.HighShelf, Frequency = 5000, Gain = -6, Q = MicEq.SnapQ(3) },
            new() { Enabled = false, Type = MicEq.Notch, Frequency = 3000, Q = MicEq.SnapQ(10) },
        ];
        DaemonBand[] daemon = [.. bands.Select(b => new DaemonBand { Enabled = b.Enabled, Type = b.Type, Frequency = b.Frequency, Gain = b.Gain, Q = b.Q })];
        Point[] sum = EqCurve.Sum(bands, Plot, 256);
        Assert.Equal(256, sum.Length);
        for (int i = 0; i < sum.Length; i++)
        {
            double hz = EqCurve.FrequencyAt(sum[i].X, Plot);
            Assert.Equal(EqCurve.FrequencyOf(i, 256), hz, 1e-6 * hz);
            // Evenly spaced on the axis: equal steps in log frequency.
            Assert.Equal(Plot.Left + Plot.Width * i / 255, sum[i].X, 1e-9);
            double expected = DaemonResponse.CascadeDb(daemon, 48000, hz);
            Assert.Equal(EqCurve.Y(expected, Plot), sum[i].Y, 1e-6);
            Assert.Equal(expected, EqCurve.GainAt(sum[i].Y, Plot), 1e-6);
        }
        // Each band's own curve too, the switch of the EQ aside.
        for (int b = 0; b < bands.Length; b++)
        {
            Point[] curve = EqCurve.Band(bands[b], Plot, 64);
            var c = DaemonResponse.Coefficients(daemon[b], 48000);
            for (int i = 0; i < curve.Length; i++)
                Assert.Equal(EqCurve.Y(DaemonResponse.MagnitudeDb(c, 48000, EqCurve.FrequencyOf(i, 64)), Plot), curve[i].Y, 1e-6);
        }
        // The axis: 20 Hz at the left edge, 20 kHz at the right, 1 kHz two thirds in; +12 dB at the top.
        Assert.Equal(Plot.Left, EqCurve.X(20, Plot), 1e-9);
        Assert.Equal(Plot.Right, EqCurve.X(20000, Plot), 1e-9);
        Assert.Equal(Plot.Left + Plot.Width * 2 / 3, EqCurve.X(2000, Plot), 1e-9);
        Assert.Equal(Plot.Top, EqCurve.Y(12, Plot), 1e-9);
        Assert.Equal(Plot.Center.Y, EqCurve.Y(0, Plot), 1e-9);
        Assert.Equal(["20", "80", "300", "1.4k", "5k", "12k", "20k"], EqCurve.Edges.Select(EqCurve.Label));
        // A gainless band's handle sits on the 0 dB line, whatever gain it keeps.
        Assert.Equal(EqCurve.Y(0, Plot), EqCurve.Handle(bands[0] with { Gain = 9 }, Plot).Y);
        Assert.Equal(EqCurve.Y(9.25, Plot), EqCurve.Handle(bands[1], Plot).Y);
    }

    [Fact]
    public void ADragMapsToFrequencyAndGainOnTheDaemonsSteps()
    {
        var (eq, sent) = Model();
        EqBandViewModel band = eq.Bands[1];
        // Two thirds across is 2 kHz; a quarter down is +6 dB.
        band.BeginDrag();
        Assert.Same(band, eq.SelectedBand);
        Point at = new(Plot.Left + Plot.Width * 2 / 3 + 0.3, Plot.Top + Plot.Height / 4 + 0.4);
        band.DragTo(EqCurve.FrequencyAt(at.X, Plot), EqCurve.GainAt(at.Y, Plot));
        var (number, change) = sent.Band();
        Assert.Equal(2, number);
        double hz = Math.Round(EqCurve.FrequencyAt(at.X, Plot));
        Assert.InRange(hz, 2001, 2012);
        Assert.Equal(hz, change.Frequency);
        Assert.Equal(6, change.Gain);
        Assert.Null(change.Q);
        Assert.Equal(hz, band.Frequency);
        Assert.Equal(6, band.Gain);
        // Past the graph: held to the ranges, never refused by the daemon.
        band.DragTo(EqCurve.FrequencyAt(Plot.Right + 50, Plot), EqCurve.GainAt(Plot.Bottom + 80, Plot));
        (_, change) = sent.Band();
        Assert.Equal(20000, change.Frequency);
        Assert.Equal(-12, change.Gain);
        // Only what moved goes out.
        band.DragTo(20000.2, -11.6);
        (_, change) = sent.Band();
        Assert.Null(change.Frequency);
        Assert.Equal(-11.5, change.Gain);
        band.DragTo(20000, -11.55);
        Assert.Empty(sent.Sent);
        band.EndDrag();

        // A type without a gain drags Q up and down instead, on the grid.
        EqBandViewModel low = eq.Bands[0];
        low.BeginDrag();
        low.DragToWithQ(80.4, 2);
        (number, change) = sent.Band();
        Assert.Equal(1, number);
        Assert.Equal(80, change.Frequency);
        Assert.Equal(DaemonEq.SnapQ(2), change.Q);
        Assert.Null(change.Gain);
        low.EndDrag();
    }

    [Fact]
    public void ReportedValuesAreShownAndAnOlderEchoWaitsForTheEdit()
    {
        // The texts below are written as an English desktop formats numbers.
        using var culture = new CultureScope("en-US");
        var time = new ManualTime();
        var (eq, sent) = Model(time: time);
        eq.Apply(State(true, """
            {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
            {"enabled":true,"type":"peak","frequency":310,"gain":9.25,"q":1.4125375446227544},
            {"enabled":true,"type":"peak","frequency":2172,"gain":-3.5,"q":0.7046930689671475},
            {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
            """), null);
        EqBandViewModel band = eq.Bands[1];
        Assert.Equal(9.25, band.Gain);
        Assert.Equal(1.4125375446227544, band.Q);
        Assert.Equal("1.41", band.QText);
        Assert.Equal(9.25m, band.GainValue);
        Assert.Equal(1.41m, band.QValue);
        Assert.Equal("310 Hz", band.FrequencyText);
        Assert.Equal("+9.25 dB", band.GainText);
        Assert.Equal("Band 2, Bell, 310 Hz, +9.25 dB, Q 1.41", band.Description);
        Assert.Equal("Band 4, off, Low pass, 15 kHz, Q 0.70", eq.Bands[3].Description);
        Assert.True(eq.Enabled);
        Assert.True(eq.IsLit);
        Assert.Empty(sent.Sent);

        // An edit, then a report from before it: the edit stays through the settling interval.
        string Report(double gain) => """
            {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
            {"enabled":true,"type":"peak","frequency":310,"gain":GAIN,"q":1.4125375446227544},
            {"enabled":true,"type":"peak","frequency":2172,"gain":-3.5,"q":0.7046930689671475},
            {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
            """.Replace("GAIN", gain.ToString(System.Globalization.CultureInfo.InvariantCulture));
        band.Gain = 3;
        Assert.Equal(3, sent.Band().Change.Gain);
        eq.Apply(State(true, Report(9.25)), null);
        Assert.Equal(3, band.Gain);
        eq.Apply(State(true, Report(3)), null);
        Assert.Equal(3, band.Gain);
        // A matching echo need not be the last outstanding report. External changes win after settling.
        time.Advance(MicEqViewModel.EchoWait);
        eq.Apply(State(true, Report(-2)), null);
        Assert.Equal(-2, band.Gain);
        // During a drag the hand wins; once it ends, a report after its last edit is shown.
        band.BeginDrag();
        band.DragTo(310, 4);
        sent.Band();
        eq.Apply(State(true, Report(-2)), null);
        Assert.Equal(4, band.Gain);
        band.EndDrag();
        eq.Apply(State(true, Report(4)), null);
        time.Advance(MicEqViewModel.EchoWait);
        eq.Apply(State(true, Report(5)), null);
        Assert.Equal(5, band.Gain);
        Assert.Equal(2, band.Number);

        // An echo that never comes: the edit waits a second for it, then the daemon's word wins.
        band.Gain = 6;
        sent.Band();
        time.Advance(MicEqViewModel.EchoWait - TimeSpan.FromTicks(1));
        eq.Apply(State(true, Report(5)), null);
        Assert.Equal(6, band.Gain);
        time.Advance(TimeSpan.FromTicks(1));
        eq.Apply(State(true, Report(5)), null);
        Assert.Equal(5, band.Gain);
    }


    [Fact]
    public void NumbersFollowTheDesktopsDecimalSeparator()
    {
        // The editor writes values as the desktop's own locale does; only
        // what goes to the daemon is locale-free.
        using var culture = new CultureScope("it-IT");
        Assert.Equal("1,4k", EqCurve.Label(1400));
        Assert.Equal("1,41", EqText.Q(1.4125375446227544));
        Assert.Equal("+9,25", EqText.Gain(9.25).Split(' ')[0]);
        Assert.Equal("2,3", EqText.Frequency(2300).Split(' ')[0]);
    }
    [Fact]
    public void TheWheelTheKeysAndATypeChangeEachSendTheirOneChange()
    {
        // The texts below are written as an English desktop formats numbers.
        using var culture = new CultureScope("en-US");
        var (eq, sent) = Model();
        EqBandViewModel band = eq.Bands[2];
        double q = band.Q;
        // One notch, one step of the grid: log10(Q) moves 0.016.
        band.StepQ(1);
        double up = DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + 0.016));
        Assert.Equal(up, sent.Band().Change.Q);
        Assert.Equal(up, band.Q);
        Assert.Equal(0.016, Math.Log10(band.Q) - Math.Log10(q), 9);
        band.StepQ(-1);
        Assert.Equal(q, sent.Band().Change.Q);
        // Held to the range at either end.
        band.StepQ(-500);
        Assert.Equal(0.1, sent.Band().Change.Q!.Value, 9);
        band.StepQ(-1);
        Assert.Empty(sent.Sent);
        band.StepQ(500);
        Assert.Equal(10, sent.Band().Change.Q!.Value, 9);

        // The arrows: a twelfth of an octave, or a quarter of that, and never less than a hertz.
        band.StepFrequency(1.0 / 12);
        Assert.Equal(DaemonEq.SnapFrequency(2172 * Math.Pow(2, 1.0 / 12)), sent.Band().Change.Frequency);
        eq.Bands[0].SetFrequency(20);
        sent.Band();
        eq.Bands[0].StepFrequency(1.0 / 48);
        Assert.Equal(21, sent.Band().Change.Frequency);
        band.StepGain(1);
        Assert.Equal(1, sent.Band().Change.Gain);
        band.StepGain(0.25);
        Assert.Equal(1.25, sent.Band().Change.Gain);

        // A type without a gain keeps the gain and takes the field away.
        Assert.True(band.UsesGain);
        band.TypeChoice = EqTypeChoice.Of(MicEq.Notch);
        var (number, change) = sent.Band();
        Assert.Equal(3, number);
        Assert.Equal(new EqBandChange(Type: "notch"), change);
        Assert.False(band.UsesGain);
        Assert.Equal(1.25, band.Gain);
        Assert.Equal("Band 3, Notch, 2.3 kHz, Q 10.00", band.Description);
        band.Type = MicEq.LowShelf;
        Assert.Equal("lowShelf", sent.Band().Change.Type);
        Assert.True(band.UsesGain);
        Assert.Equal(1.25, band.Gain);
        // The switch, as a double click or Space sends it.
        band.Enabled = false;
        Assert.Equal(new EqBandChange(Enabled: false), sent.Band().Change);

        // The Q field's spinner moves a whole grid step even where 0.01 is finer.
        eq.Bands[1].SetQ(1);
        Assert.Equal(DaemonEq.SnapQ(1), sent.Band().Change.Q);
        decimal shown = eq.Bands[1].QValue!.Value;
        Assert.Equal(1.02m, shown);
        eq.Bands[1].QValue = shown + 0.01m;
        Assert.Equal(DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(1) + 0.016)), sent.Band().Change.Q);
        eq.Bands[1].FrequencyValue = 999.6m;
        Assert.Equal(1000, sent.Band().Change.Frequency);
        eq.Bands[1].GainValue = -40m;
        Assert.Equal(-12, sent.Band().Change.Gain);
    }

    /// <summary>A sender on a clock the test moves, recording each command with the clock's time.</summary>
    private static (EqBandSender Sender, ManualTime Time, List<(int Band, TimeSpan At, EqBandChange Change)> Sent) Sender()
    {
        var time = new ManualTime();
        var sent = new List<(int Band, TimeSpan At, EqBandChange Change)>();
        return (new EqBandSender(TimeSpan.FromMilliseconds(20), (band, change) => sent.Add((band, time.Elapsed, change)), time), time, sent);
    }

    /// <summary>The last value a field took over the commands: what the daemon holds once they are in.</summary>
    private static double? Last(IEnumerable<(int Band, TimeSpan At, EqBandChange Change)> sent, Func<EqBandChange, double?> field) =>
        sent.Select(s => field(s.Change)).LastOrDefault(v => v is not null);

    private static void AssertSpaced(IReadOnlyList<(int Band, TimeSpan At, EqBandChange Change)> sent)
    {
        for (int i = 1; i < sent.Count; i++)
            Assert.True(sent[i].At - sent[i - 1].At >= TimeSpan.FromMilliseconds(20),
                $"two changes of band {sent[i].Band} {(sent[i].At - sent[i - 1].At).TotalMilliseconds} ms apart");
    }

    [Fact]
    public void BandChangesGoOutAtMostOnceAnIntervalMergedAndEndingOnTheLast()
    {
        var (sender, time, sent) = Sender();
        using (sender)
        {
            // Band 2 dragged for 300 ms with a move every 2 ms, band 3's Q once on the way.
            for (int i = 1; i <= 150; i++)
            {
                sender.Queue(2, new EqBandChange(Frequency: 300 + i));
                if (i % 2 == 0) sender.Queue(2, new EqBandChange(Gain: i * 0.25 % 12));
                if (i == 75) sender.Queue(3, new EqBandChange(Q: 2));
                time.Advance(TimeSpan.FromMilliseconds(2));
            }
            time.Advance(TimeSpan.FromMilliseconds(80));
            // Nothing waits any more: disposing sends nothing.
            int before = sent.Count;
            sender.Dispose();
            Assert.Equal(before, sent.Count);
        }
        var two = sent.Where(s => s.Band == 2).ToList();
        AssertSpaced(two);
        // The first move at once, then one command at the end of each interval: 0, 20, ... 300 ms.
        TimeSpan[] interval = [.. Enumerable.Range(0, 16).Select(n => TimeSpan.FromMilliseconds(20 * n))];
        Assert.Equal(interval, two.Select(s => s.At));
        // Every move inside an interval merged into one command: the last frequency and gain of it, together.
        Assert.All(two.Skip(1), s =>
        {
            Assert.NotNull(s.Change.Frequency);
            Assert.NotNull(s.Change.Gain);
        });
        // The last change of the drag arrives, with every field that moved.
        Assert.Equal(new EqBandChange(Frequency: 450, Gain: 150 * 0.25 % 12), two[^1].Change);
        // Another band is not held back by the first.
        Assert.Equal((3, TimeSpan.FromMilliseconds(148), new EqBandChange(Q: 2)), Assert.Single(sent, s => s.Band == 3));
    }

    [Fact]
    public void AChangeAfterAPauseGoesAtOnceAndWhatFollowsItGoesAtTheEndOfTheInterval()
    {
        var (sender, time, sent) = Sender();
        using (sender)
        {
            // A drag that stalls longer than the interval, as a busy machine makes it:
            // the frequency finds the interval over and goes at once, and the gain
            // that comes right after it has to wait for the next interval to end.
            sender.Queue(2, new EqBandChange(Frequency: 449, Gain: 1));
            time.Advance(TimeSpan.FromMilliseconds(5));
            sender.Queue(2, new EqBandChange(Gain: 1.25));
            time.Advance(TimeSpan.FromMilliseconds(40));
            sender.Queue(2, new EqBandChange(Frequency: 450));
            sender.Queue(2, new EqBandChange(Gain: 1.5));
            time.Advance(TimeSpan.FromMilliseconds(19));
            Assert.Equal(3, sent.Count);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }
        AssertSpaced(sent);
        (int, TimeSpan, EqBandChange)[] expected =
        [
            (2, TimeSpan.Zero, new EqBandChange(Frequency: 449, Gain: 1)),
            (2, TimeSpan.FromMilliseconds(20), new EqBandChange(Gain: 1.25)),
            (2, TimeSpan.FromMilliseconds(45), new EqBandChange(Frequency: 450)),
            (2, TimeSpan.FromMilliseconds(65), new EqBandChange(Gain: 1.5)),
        ];
        Assert.Equal(expected, sent.Select(s => (s.Band, s.At, s.Change)));
        // The last command carries only the gain, and the daemon still ends on the last of each field.
        Assert.Equal(450, Last(sent, c => c.Frequency));
        Assert.Equal(1.5, Last(sent, c => c.Gain));
    }

    [Fact]
    public void WhatWaitsGoesOutWhenTheEditorCloses()
    {
        var (sender, time, sent) = Sender();
        sender.Queue(1, new EqBandChange(Frequency: 100));
        sender.Queue(1, new EqBandChange(Frequency: 120));
        sender.Queue(4, new EqBandChange(Enabled: true));
        time.Advance(TimeSpan.FromMilliseconds(10));
        sender.Queue(4, new EqBandChange(Type: MicEq.Notch));
        Assert.Equal([1, 4], sent.Select(s => s.Band));
        // Closed before the interval ends: both bands' last changes go at once, and nothing after.
        sender.Dispose();
        (int, TimeSpan, EqBandChange)[] expected =
        [
            (1, TimeSpan.Zero, new EqBandChange(Frequency: 100)),
            (4, TimeSpan.Zero, new EqBandChange(Enabled: true)),
            (1, TimeSpan.FromMilliseconds(10), new EqBandChange(Frequency: 120)),
            (4, TimeSpan.FromMilliseconds(10), new EqBandChange(Type: MicEq.Notch)),
        ];
        Assert.Equal(expected, sent.Select(s => (s.Band, s.At, s.Change)));
        sender.Queue(1, new EqBandChange(Frequency: 130));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(4, sent.Count);
    }

    [Fact]
    public void TheComparisonBypassesWhileTheSwitchStaysOn()
    {
        var (eq, sent) = Model();
        eq.Apply(State(false, Defaults, "null"), null);
        Assert.False(eq.Enabled);
        Assert.False(eq.IsLit);
        Assert.Equal(Localizer.Text("EqStatusOff"), eq.Status);
        // Only an EQ that is on is compared.
        eq.Comparing = true;
        Assert.False(eq.Comparing);
        Assert.Empty(sent.Sent);

        eq.Enabled = true;
        Assert.True(sent.Sent.TryDequeue(out var on));
        Assert.Equal(("setEqEnabled", "xlr1", (object)true), on);
        // A report from before the switch does not take it back.
        eq.Apply(State(false, Defaults, "null"), null);
        Assert.True(eq.Enabled);
        eq.Apply(State(true, Defaults), null);
        Assert.True(eq.IsLit);
        Assert.Equal(Localizer.Format("EqButtonNameOn", "XLR 1"), eq.ButtonName);

        eq.Comparing = true;
        Assert.True(sent.Sent.TryDequeue(out var bypass));
        Assert.Equal(false, bypass.Value);
        eq.Apply(State(false, Defaults, "\"software\""), null);
        Assert.True(eq.Enabled);
        Assert.True(eq.Comparing);
        Assert.False(eq.IsLit);
        Assert.Equal(Localizer.Text("EqStatusComparing"), eq.Status);
        // Closing the editor ends it.
        eq.EndComparison();
        Assert.True(sent.Sent.TryDequeue(out var back));
        Assert.Equal(true, back.Value);
        Assert.False(eq.Comparing);

        // Unavailable: the reason, and the button's alert.
        eq.Apply(JsonNode.Parse($$"""{"enabled":true,"bands":[{{Defaults}}],"backend":null,"available":false,"error":"The native plugin host is not installed."}""")!, null);
        Assert.True(eq.IsFaulty);
        Assert.False(eq.IsLit);
        Assert.Equal("The native plugin host is not installed.", eq.Status);
        Assert.Equal(Localizer.Format("EqButtonNameUnavailable", "XLR 1"), eq.ButtonName);
    }

    [Fact]
    public void CopyPasteAndResetSendOneWholeEq()
    {
        EqClipboard.Value = null;
        var (first, sentFirst) = Model("xlr1");
        var (second, sentSecond) = Model("xlr2");
        Assert.False(second.CanPaste);
        first.Bands[1].DragTo(1000, 6);
        first.Bands[3].Enabled = true;
        first.Enabled = true;
        first.Copy();
        Assert.True(second.CanPaste);
        second.Paste();
        Assert.True(sentSecond.Sent.TryDequeue(out var pasted));
        Assert.Equal("setEq", pasted.Command);
        Assert.Equal("xlr2", pasted.Channel);
        Assert.Equal(first.Current, (MicEq)pasted.Value);
        Assert.Equal(first.Current, second.Current);
        Assert.Equal(1000, second.Bands[1].Frequency);

        second.Reset();
        Assert.True(sentSecond.Sent.TryDequeue(out var reset));
        Assert.Equal(MicEq.Default with { Enabled = true }, (MicEq)reset.Value);
        Assert.Equal([36d, 310, 2172, 15049], second.Bands.Select(b => b.Frequency));
        Assert.True(second.Enabled);
        Assert.Empty(sentSecond.Sent);
        first.Dispose();
        second.Dispose();
        EqClipboard.Value = null;
    }

    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void OnlyAChannelTheDaemonReportsAnEqForHasOne()
    {
        var model = new MainViewModel(new DaemonClient());
        string Mixer(string eq) => """
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1EQ},{"id":"game","name":"Game","volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}],
             "eqLimits":{"bands":4,"frequency":{"min":20,"max":20000,"step":1},"gain":{"min":-10,"max":10,"step":0.25},"q":{"min":0.1,"max":10,"log10Step":0.016}}}
            """.Replace("EQ", eq);
        // An older daemon: no EQ anywhere, so no button.
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(""))]);
        using var matrix = new MixMatrixViewModel(model);
        Assert.All(model.Channels, c => Assert.Null(c.Eq));
        Assert.All(matrix.Rows, r => Assert.False(r.ShowsEq));
        // Tall only for the gain over the volume, as before the EQ.
        Assert.Equal(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked, matrix.Rows.Single(r => r.Channel.Id == "xlr1").Tall);

        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(",\"eq\":" + State(true, Defaults).ToJsonString()))]);
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        MicEqViewModel eq = Assert.IsType<MicEqViewModel>(xlr1.Eq);
        Assert.Null(model.Channels.Single(c => c.Id == "game").Eq);
        Assert.True(eq.Enabled);
        // The daemon's limits, not the model's own.
        Assert.Equal(10, eq.Limits.Gain.Max);
        eq.Bands[1].SetGain(11);
        Assert.Equal(10, eq.Bands[1].Gain);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "xlr1");
        Assert.True(row.ShowsEq);
        Assert.True(row.Tall);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "game").ShowsEq);
        // The Flow window's step, first in the input's card.
        string processing = FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:xlr1").Processing;
        Assert.Equal(Localizer.Format("FlowEq", 3), processing.Split('\n')[0]);

        // The daemon stops reporting it: the model goes with it, and so does the button.
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(""))]);
        Assert.Null(xlr1.Eq);
        Assert.False(row.ShowsEq);
        Assert.DoesNotContain(FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:xlr1").Processing.Split('\n'), l => l == Localizer.Format("FlowEq", 3));
    }

    private static JsonNode OnUnit(bool enabled, string backend, string processing, string firmware, bool ready, string? reason = null)
    {
        JsonNode state = State(enabled, Defaults, $"\"{backend}\"");
        state["processing"] = processing;
        state["device"] = new JsonObject
        {
            ["unit"] = "0fd9:00c7@0123456789abcdef", ["input"] = 1, ["name"] = "Wave XLR Dock MK.2",
            ["firmware"] = firmware.Length == 0 ? null : firmware, ["ready"] = ready, ["reason"] = reason,
            ["note"] = "daemon text", ["minimumFirmware"] = "1.0.15",
        };
        return state;
    }

    [Fact]
    public void TheEditorSaysWhenTheUnitsOwnProcessorRunsTheEqAndWhyNot()
    {
        var (eq, _) = Model();
        // A daemon from before the hardware EQ sends neither field: no badge, no note.
        eq.Apply(State(true, Defaults), null);
        Assert.False(eq.OnDevice);
        Assert.Null(eq.DeviceNote);
        Assert.False(eq.HasDeviceNote);
        Assert.Equal("device", eq.Processing);
        Assert.Equal(Localizer.Text("EqButtonTip"), eq.ButtonTip);

        // On the unit: the badge, the unit named in the footer and the button's tooltip.
        eq.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.OnDevice);
        Assert.True(eq.IsLit);
        Assert.Equal("Wave XLR Dock MK.2", eq.DeviceName);
        Assert.Equal(Localizer.Format("EqStatusDeviceNamed", "Wave XLR Dock MK.2"), eq.Status);
        Assert.Equal(Localizer.Format("EqHardwareBadgeTip", "Wave XLR Dock MK.2"), eq.DeviceBadgeTip);
        Assert.Equal(Localizer.Format("EqButtonTipDevice", "Wave XLR Dock MK.2"), eq.ButtonTip);
        Assert.Null(eq.DeviceNote);
        // Off, the unit still holds it: still the badge, the footer says off.
        eq.Apply(OnUnit(false, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.OnDevice);
        Assert.Equal(Localizer.Text("EqStatusOff"), eq.Status);

        // Older firmware: software runs it, and the note says why and what to do.
        eq.Apply(OnUnit(true, "software", "device", "1.0.0", ready: false, "firmwareOld"), null);
        Assert.False(eq.OnDevice);
        Assert.True(eq.IsLit);
        Assert.Equal(Localizer.Text("EqStatusSoftware"), eq.Status);
        Assert.Equal(Localizer.Format("EqDeviceFirmwareOld", "Wave XLR Dock MK.2", "1.0.0", "1.0.15"), eq.DeviceNote);
        Assert.Contains("1.0.0", eq.DeviceNote);
        Assert.Contains("1.0.15", eq.DeviceNote);
        Assert.True(eq.HasDeviceNote);
        Assert.EndsWith(eq.DeviceNote!, eq.ButtonTip);

        // The other reasons, and the daemon's own sentence for one this client does not know.
        foreach ((string reason, string expected) in new[]
        {
            ("firmwareUnknown", Localizer.Format("EqDeviceFirmwareUnknown", "Wave XLR Dock MK.2", "1.0.15")),
            ("unsupportedBlock", Localizer.Format("EqDeviceUnsupported", "Wave XLR Dock MK.2")),
            ("failed", Localizer.Format("EqDeviceFailed", "Wave XLR Dock MK.2")),
            ("somethingNew", "daemon text"),
        })
        {
            eq.Apply(OnUnit(true, "software", "device", "", ready: false, reason), null);
            Assert.Equal(expected, eq.DeviceNote);
        }

        // Software by choice on a unit that could run it.
        eq.Apply(OnUnit(true, "software", "software", "1.0.15", ready: true), null);
        Assert.Equal("software", eq.Processing);
        Assert.Equal(Localizer.Format("EqDeviceBySoftware", "Wave XLR Dock MK.2"), eq.DeviceNote);
    }

    [Fact]
    public void TheFooterOffersWhereTheEqRunsOnlyWhileAUnitCanRunItAndSendsTheChoice()
    {
        var time = new ManualTime();
        var (eq, sent) = Model(time: time);
        var raised = new List<string>();
        eq.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // An older daemon reports neither the choice nor a unit: no switch, and choosing does nothing.
        eq.Apply(State(true, Defaults), null);
        Assert.False(eq.HasProcessingChoice);
        eq.ChooseProcessing("software");
        Assert.Empty(sent.Sent);
        Assert.Equal("device", eq.Processing);
        // A unit but no choice in the state, which no daemon sends: still no switch.
        JsonNode noChoice = OnUnit(true, "device", "device", "1.0.15", ready: true);
        noChoice.AsObject().Remove("processing");
        eq.Apply(noChoice, null);
        Assert.False(eq.HasProcessingChoice);
        // The choice without a unit, as on a Wave XLR Pro: no switch either.
        JsonNode noUnit = State(true, Defaults);
        noUnit["processing"] = "device";
        noUnit["device"] = null;
        eq.Apply(noUnit, null);
        Assert.False(eq.HasProcessingChoice);

        // On the unit: the switch shows it, with the unit named in its tooltip, and no note.
        eq.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.HasProcessingChoice);
        Assert.True(eq.ProcessOnDevice);
        Assert.False(eq.ProcessInSoftware);
        Assert.Equal(Localizer.Format("EqProcessingDeviceTip", "Wave XLR Dock MK.2"), eq.ProcessOnDeviceTip);
        Assert.Null(eq.EditorNote);

        // The chosen side again sends nothing and stays lit; the other side sends setEqProcessing.
        raised.Clear();
        eq.ChooseProcessing("device");
        Assert.Empty(sent.Sent);
        Assert.True(eq.ProcessOnDevice);
        // Both sides are told again, so a toggle the press switched off lights up again.
        Assert.Contains(nameof(MicEqViewModel.ProcessOnDevice), raised);
        Assert.Contains(nameof(MicEqViewModel.ProcessInSoftware), raised);
        eq.ChooseProcessing("elsewhere");
        Assert.Empty(sent.Sent);
        eq.ChooseProcessing("software");
        Assert.True(sent.Sent.TryDequeue(out var choice));
        Assert.Equal(("setEqProcessing", "xlr1", (object)"software"), choice);
        Assert.Empty(sent.Sent);
        Assert.True(eq.ProcessInSoftware);
        Assert.False(eq.ProcessOnDevice);

        // A report from before the choice does not move the switch back, for a second at most.
        eq.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.ProcessInSoftware);
        time.Advance(MicEqViewModel.EchoWait - TimeSpan.FromMilliseconds(1));
        eq.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.ProcessInSoftware);
        // The echo settles it, and software by choice needs no note under the switch.
        eq.Apply(OnUnit(true, "software", "software", "1.0.15", ready: true), null);
        Assert.Equal("software", eq.Processing);
        Assert.Null(eq.EditorNote);
        Assert.False(eq.HasEditorNote);
        Assert.Equal(Localizer.Text("EqStatusSoftware"), eq.Status);
        // The button's tooltip, which has no switch beside it, still says so.
        Assert.Equal(Localizer.Format("EqDeviceBySoftware", "Wave XLR Dock MK.2"), eq.DeviceNote);

        // A choice the daemon never confirms gives way to its report after the echo wait.
        eq.ChooseProcessing("device");
        Assert.True(sent.Sent.TryDequeue(out choice));
        Assert.Equal(("setEqProcessing", "xlr1", (object)"device"), choice);
        eq.Apply(OnUnit(true, "software", "software", "1.0.15", ready: true), null);
        Assert.True(eq.ProcessOnDevice);
        time.Advance(MicEqViewModel.EchoWait);
        eq.Apply(OnUnit(true, "software", "software", "1.0.15", ready: true), null);
        Assert.True(eq.ProcessInSoftware);

        // A unit that cannot take the EQ: the switch stays, lit on software where the EQ runs, the
        // unit's side cannot be pressed and its tooltip says why, as does the note under the switch.
        foreach ((string reason, string firmware, string expected) in new[]
        {
            ("firmwareOld", "1.0.0", Localizer.Format("EqDeviceFirmwareOld", "Wave XLR Dock MK.2", "1.0.0", "1.0.15")),
            ("firmwareUnknown", "", Localizer.Format("EqDeviceFirmwareUnknown", "Wave XLR Dock MK.2", "1.0.15")),
            ("unsupportedBlock", "1.0.15", Localizer.Format("EqDeviceUnsupported", "Wave XLR Dock MK.2")),
            ("failed", "1.0.15", Localizer.Format("EqDeviceFailed", "Wave XLR Dock MK.2")),
        })
        {
            eq.Apply(OnUnit(true, "software", "device", firmware, ready: false, reason), null);
            Assert.True(eq.HasProcessingChoice, reason);
            Assert.Equal("device", eq.Processing);
            Assert.False(eq.ProcessOnDevice, reason);
            Assert.True(eq.ProcessInSoftware, reason);
            Assert.False(eq.CanProcessOnDevice, reason);
            Assert.Equal(expected, eq.ProcessOnDeviceTip);
            eq.ChooseProcessing("device");
            Assert.Empty(sent.Sent);
            Assert.Equal(expected, eq.EditorNote);
            Assert.True(eq.HasEditorNote);
            Assert.Equal(Localizer.Text("EqStatusSoftware"), eq.Status);
            // Kept in software by choice, the reason the unit cannot run it still shows.
            eq.Apply(OnUnit(true, "software", "software", firmware, ready: false, reason), null);
            Assert.Equal(expected, eq.EditorNote);
        }

        // The unit goes: so does the switch.
        eq.Apply(noUnit, null);
        Assert.False(eq.HasProcessingChoice);
        Assert.Null(eq.EditorNote);
    }

    [Fact]
    public void MoreBandsThanTheUnitHoldsLightTheSoftwareSideAndKeepTheChoice()
    {
        var (eq, sent) = Model();
        static JsonNode Five(string processing)
        {
            JsonNode state = OnUnit(true, "software", processing, "1.0.15", ready: true);
            state["bands"]!.AsArray().Add(JsonNode.Parse("""{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":0.7046930689671475}"""));
            return state;
        }
        string limit = Localizer.Format("EqDeviceBandLimit", "Wave XLR Dock MK.2", MicEq.BandCount);

        // Five bands with the unit chosen: the EQ runs in software, and the switch says so.
        eq.Apply(Five("device"), null);
        Assert.Equal(5, eq.Bands.Count);
        Assert.Equal("device", eq.Processing);
        Assert.False(eq.ProcessOnDevice);
        Assert.True(eq.ProcessInSoftware);
        Assert.False(eq.CanProcessOnDevice);
        Assert.Equal(limit, eq.ProcessOnDeviceTip);
        Assert.Equal(limit, eq.EditorNote);
        // The unit's side cannot be asked for while the bands do not fit.
        eq.ChooseProcessing("device");
        Assert.Empty(sent.Sent);

        // Back to four bands: the kept choice puts the EQ on the unit again.
        eq.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.True(eq.ProcessOnDevice);
        Assert.False(eq.ProcessInSoftware);
        Assert.True(eq.CanProcessOnDevice);
        Assert.Equal(Localizer.Format("EqProcessingDeviceTip", "Wave XLR Dock MK.2"), eq.ProcessOnDeviceTip);

        // Pressing the software side while the bands do not fit makes software the choice.
        eq.Apply(Five("device"), null);
        eq.ChooseProcessing("software");
        Assert.True(sent.Sent.TryDequeue(out var choice));
        Assert.Equal(("setEqProcessing", "xlr1", (object)"software"), choice);
        Assert.Equal("software", eq.Processing);
        Assert.True(eq.ProcessInSoftware);
    }

    [Fact]
    public void TheFlowWindowsEqStepSaysWhereTheEqRuns()
    {
        var model = new MainViewModel(new DaemonClient());
        static string Mixer(string backend, string processing) => """
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1,"eq":EQ}],"mixes":[{"id":"monitor","name":"Monitor A"}]}
            """.Replace("EQ", OnUnit(true, backend, processing, "1.0.15", ready: true).ToJsonString());
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer("device", "device"))]);
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        Assert.True(xlr1.Eq!.OnDevice);
        string Step() => FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:xlr1").Processing.Split('\n')[0];
        Assert.Equal(Localizer.Format("FlowEqDevice", 3, "Wave XLR Dock MK.2"), Step());
        Assert.Contains("Wave XLR Dock MK.2", Step());
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer("software", "software"))]);
        Assert.Equal(Localizer.Format("FlowEq", 3), Step());
    }

    /// <summary>
    /// A capture input's EQ, which the daemon reports like XLR 1's with
    /// processing software: the window makes the same model, the matrix and
    /// the Flow card show it ahead of the input's plugins, and the editor
    /// offers no unit, no choice and no badge even from a state that named
    /// one, and says the EQ runs at the head of the input before the plugins.
    /// A capture input on a stereo pair has the same EQ; one an older daemon
    /// reports no EQ for gets none.
    /// </summary>
    [Fact]
    public void ACaptureInputHasTheSameEqRunningOnThisComputer()
    {
        var model = new MainViewModel(new DaemonClient());
        JsonNode software = State(true, Defaults);
        software["processing"] = "software";
        software["device"] = null;
        string mixer = """
            {"channels":[{"id":"guest","name":"Guest","captureSource":"usb.guest","captureMonoChannel":0,"captureConnected":true,
                          "microphone":true,"chainWidth":1,"volume":1,"eq":EQ},
                         {"id":"camera","name":"Camera","captureSource":"usb.camera","captureConnected":true,
                          "microphone":false,"chainWidth":2,"volume":1,"eq":EQ},
                         {"id":"older","name":"Older","captureSource":"usb.older","captureConnected":true,"chainWidth":2,"volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}],
             "inserts":{"guest":[{"insert":{"id":"comp","plugin":"urn:comp","kind":"lv2","label":"Compressor","params":{}}}]}}
            """.Replace("EQ", software.ToJsonString());
        ApplyMixer.Invoke(model, [JsonNode.Parse(mixer)]);
        using var matrix = new MixMatrixViewModel(model);
        ChannelViewModel guest = model.Channels.Single(c => c.Id == "guest");
        MicEqViewModel eq = Assert.IsType<MicEqViewModel>(guest.Eq);
        MicEqViewModel stereo = Assert.IsType<MicEqViewModel>(model.Channels.Single(c => c.Id == "camera").Eq);
        Assert.True(stereo.IsCaptureInput);
        Assert.False(stereo.HasProcessingChoice);
        Assert.Equal(Localizer.Text("EqStatusSoftwareCapture"), stereo.Status);
        Assert.Null(model.Channels.Single(c => c.Id == "older").Eq);
        Assert.True(matrix.Rows.Single(r => r.Channel.Id == "guest").ShowsEq);
        Assert.True(matrix.Rows.Single(r => r.Channel.Id == "camera").ShowsEq);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "older").ShowsEq);
        Assert.Equal("guest", eq.Channel);
        Assert.True(eq.IsCaptureInput);
        Assert.False(eq.HasProcessingChoice);
        Assert.False(eq.OnDevice);
        Assert.Null(eq.EditorNote);
        Assert.Equal(Localizer.Text("EqStatusSoftwareCapture"), eq.Status);
        Assert.Equal(Localizer.Text("EqButtonTipCapture"), eq.ButtonTip);
        Assert.DoesNotContain("ClipGuard", eq.Status);
        Assert.Equal(Localizer.Format("EqWindowTitle", "Guest"), eq.Title);
        string[] steps = FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:guest").Processing.Split('\n');
        Assert.Equal(Localizer.Format("FlowEq", 3), steps[0]);
        Assert.Contains("Compressor", steps[1]);

        // A state that names a unit and a device backend still offers neither.
        var (direct, sent) = Model("guest");
        direct.Apply(OnUnit(true, "device", "device", "1.0.15", ready: true), null);
        Assert.False(direct.HasProcessingChoice);
        Assert.False(direct.OnDevice);
        Assert.Null(direct.DeviceNote);
        direct.ChooseProcessing("software");
        direct.ChooseProcessing("device");
        Assert.Empty(sent.Sent);
        // Its edits name the capture input.
        direct.Enabled = false;
        Assert.True(sent.Sent.TryDequeue(out var off));
        Assert.Equal(("setEqEnabled", "guest", (object)false), off);
    }
}
