using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// When the daemon decides a unit's capture has stalled under its own
/// playback: only the kernel holding the capture stopped counts, and the
/// resets must not repeat in a storm.
/// </summary>
public sealed class CaptureWatchTests
{
    private const string Source = "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
    private const string Sink = "alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo";
    private static readonly UnitPlayback Playing = new(Source, Sink);
    private static readonly CaptureRun Running = new(582, Alsa: true, NodeRunning: true, KernelRunning: true);
    private static readonly CaptureRun Stopped = new(582, Alsa: true, NodeRunning: true, KernelRunning: false);

    [Fact]
    public void AKernelStopThatLastsTheWindowRecoversOnceAndThenWaits()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        var verdicts = new List<CaptureVerdict>();
        // Ten minutes of a stopped capture, one look a second.
        for (; now <= 10 * 60_000; now += 1000) verdicts.Add(watch.Observe(Playing, _ => Stopped));
        List<CaptureVerdict> recoveries = [.. verdicts.Where(v => v.Recover)];
        // The first after the three second window, then one per spacing, and
        // no more than the cap within the half hour.
        Assert.Equal(CaptureWatch.MaxRecoveries, recoveries.Count);
        Assert.False(verdicts[2].Recover);
        Assert.True(verdicts[3].Recover);
        Assert.Contains("stopped for 3 seconds", recoveries[0].Reason, StringComparison.Ordinal);
        Assert.Single(verdicts, v => v.Note is not null);
        Assert.Contains("Unplug it", verdicts.Single(v => v.Note is not null).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopThatEndsOrAUnitNoLongerAtRiskStartsTheCountOver()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 2; i++, now += 1000) Assert.False(watch.Observe(Playing, _ => Stopped).Recover);
            // The monitor moved off the unit's headphones for a sweep.
            Assert.False(watch.Observe(null, _ => Stopped).Recover);
            now += 1000;
        }
        // A capture PipeWire does not run is idle, not stalled.
        for (int i = 0; i < 10; i++, now += 1000)
            Assert.False(watch.Observe(Playing, _ => Stopped with { NodeRunning = false }).Recover);
    }

    [Fact]
    public void ACaptureTheKernelRunsIsNeverReset()
    {
        // A running capture recovers by no sign the watch reads, whatever it
        // records: a quiet room, a muted unit's zeros or a buzz.
        long now = 0;
        var watch = new CaptureWatch(() => now);
        for (; now <= 60 * 60_000; now += 1000) Assert.False(watch.Observe(Playing, _ => Running).Recover);
        // Nor does a capture whose kernel status cannot be read.
        for (int i = 0; i < 60; i++, now += 1000)
            Assert.False(watch.Observe(Playing, _ => Running with { KernelRunning = null }).Recover);
    }

    [Fact]
    public void AStationaryHardwareCursorRecoversOnTheSameBudgetAsAStoppedCapture()
    {
        long now = 0;
        var watch = new CaptureWatch(() => now);
        var cursor = new CaptureCursor(2, 0, 0, "19.095039214", 228280655);
        var dock = new UnitCaptureProgress("alsa_input.usb-Elgato_XLR_Dock", Running with { Cursor = cursor });
        // A silent capture still advances. A new PCM run or missing sample
        // starts a new observation, even if it starts at the same frame.
        for (ulong i = 0; i < 10; i++, now += 1000)
            Assert.False(watch.ObserveProgress(dock with { Run = dock.Run with { Cursor = cursor with { Frame = i } } }).Recover);
        Assert.False(watch.ObserveProgress(dock).Recover);
        now += 2000;
        Assert.False(watch.ObserveProgress(null).Recover);
        now += 1000;
        var newRun = dock with { Run = dock.Run with { Cursor = cursor with { Trigger = "30.000000000" } } };
        Assert.False(watch.ObserveProgress(newRun).Recover);
        now += 2000;
        Assert.False(watch.ObserveProgress(newRun).Recover);
        now += 1000;
        Assert.True(watch.ObserveProgress(newRun).Recover);
        var verdicts = new List<CaptureVerdict>();
        for (int i = 0; i < 600; i++, now += 1000) verdicts.Add(watch.ObserveProgress(newRun));
        Assert.Equal(CaptureWatch.MaxRecoveries - 1, verdicts.Count(v => v.Recover));
        Assert.Contains("tried reopening the capture", Assert.Single(verdicts, v => v.Note is not null).Note, StringComparison.Ordinal);
    }
}
