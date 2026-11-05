using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// The clock on a private PipeWire server: two capture devices that each
    /// have a clock of their own (null sources standing in for a USB
    /// microphone and a capture card, with the priorities WirePlumber gives
    /// such devices), a microphone input on one and a capture input on the
    /// other. The card's higher priority.driver drives the mixer's graph and
    /// the microphone is reported resampled; with the microphone's priority
    /// above the card's, the roles turn round.
    /// </summary>
    [MonitorPipeWireFact]
    public void TheMixerReportsWhichDeviceDrivesAndWhichCaptureIsResampled()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        // No settling time and no gap, so each sweep below reads the graph.
        using var mixer = new Mixer(pw) { ClockSettle = TimeSpan.Zero, ClockMinGap = TimeSpan.Zero };
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        var modules = new List<string>();
        string Source(string name, int priority, int device, int channels)
        {
            string module = ProcessRunner.Run("pactl", ["load-module", "module-null-sink", "media.class=Audio/Source/Virtual",
                $"sink_name={name}", $"channels={channels}", channels == 1 ? "channel_map=mono" : "channel_map=front-left,front-right",
                $"sink_properties=device.api=test device.id={device} priority.driver={priority} node.description={name}"]).StdoutText.Trim();
            Assert.False(string.IsNullOrEmpty(module), "pactl could not load the test source " + name);
            modules.Add(module);
            Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == name), TimeSpan.FromSeconds(5)));
            return module;
        }
        try
        {
            string mic = Source("test_clock_mic", 2100, 9002, 1);
            Source("test_clock_card", 2109, 9001, 2);
            mixer.CreateCaptureChannel("Mic", "test_clock_mic", 0, _ => null, monoChannel: 0);
            mixer.CreateCaptureChannel("Console", "test_clock_card", 0, _ => null);

            MixerClock? Settled(Func<MixerClock, bool> done)
            {
                MixerClock? last = null;
                SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureInputFeeds();
                    mixer.SyncClock();
                    last = mixer.Snapshot().Clock;
                    return last is not null && done(last);
                }, TimeSpan.FromSeconds(10));
                return last;
            }

            MixerClock clock = Assert.IsType<MixerClock>(Settled(c => c.Driver?.Name == "test_clock_card" && c.Followers.Count > 0));
            Assert.Equal(2109, clock.Driver!.PriorityDriver);
            Assert.Equal("capture", clock.Driver.Role);
            ClockFollower follower = Assert.Single(clock.Followers);
            Assert.Equal("test_clock_mic", follower.Name);
            Assert.True(follower.Resampled);
            // A microphone on a capture input is shown resampled but does not
            // warn: only XLR 1 and XLR 2 do, whatever source they are bound to.
            Assert.False(follower.Microphone);
            Assert.False(clock.MicResampled);

            // The same microphone at 2200, as the packaged rules give a
            // unit's input: it drives, and the card is the one resampled.
            ProcessRunner.Run("pactl", ["unload-module", mic]);
            modules.Remove(mic);
            Source("test_clock_mic", 2200, 9002, 1);
            clock = Assert.IsType<MixerClock>(Settled(c => c.Driver?.Name == "test_clock_mic" && c.Followers.Count > 0));
            Assert.Equal("test_clock_card", Assert.Single(clock.Followers).Name);
            Assert.True(clock.Followers[0].Resampled);
            Assert.False(clock.MicResampled);
        }
        finally
        {
            foreach (string module in modules) ProcessRunner.Run("pactl", ["unload-module", module]);
        }
    }
}
