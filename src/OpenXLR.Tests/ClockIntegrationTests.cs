using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void ClockReadsDespiteGraphChurnAndRetriesAnEmptySnapshot()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        long now = 0;
        using var mixer = new Mixer(pw) { Clock = () => now };
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-clock-reads-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string? path = Environment.GetEnvironmentVariable("PATH");
        string reads = Path.Combine(directory, "reads");
        string snapshot = Path.Combine(directory, "snapshot");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                #!/bin/sh
                dir=$(dirname "$0")
                printf '%s\n' read >> "$dir/reads"
                cat "$dir/snapshot"
                """);
            File.WriteAllText(snapshot, "[]");
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            Assert.NotNull(pw.ClockRevision);
            Assert.False(mixer.SyncClock());
            for (int step = 1; step <= 5; step++)
            {
                long? before = pw.ClockRevision;
                pw.CreateNullSink($"test_clock_change_{step}", "Clock change");
                Assert.True(SpinWait.SpinUntil(() => pw.ClockRevision is long revision && revision != before, TimeSpan.FromSeconds(5)));
                now = step * 1000;
                Assert.False(mixer.SyncClock());
                if (step < 5) Assert.False(File.Exists(reads));
            }
            Assert.True(File.Exists(reads), "Continuous graph changes must not postpone the clock read indefinitely.");
            Assert.Single(File.ReadAllLines(reads));

            // One last event must not postpone the retry of the empty read.
            File.WriteAllText(snapshot, """
                [
                  {"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"test_driver"}}},
                  {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor","node.driver-id":1}}}
                ]
                """);
            long? lastRevision = pw.ClockRevision;
            pw.CreateNullSink("test_clock_change_before_retry", "Clock change before retry");
            Assert.True(SpinWait.SpinUntil(() => pw.ClockRevision is long revision && revision != lastRevision, TimeSpan.FromSeconds(5)));
            now = 9999;
            Assert.False(mixer.SyncClock());
            Assert.Single(File.ReadAllLines(reads));
            now = 10000;
            Assert.True(mixer.SyncClock());
            Assert.Equal("test_driver", mixer.GraphClock?.Driver?.Name);
            now = 20000;
            Assert.False(mixer.SyncClock());
            Assert.Equal(2, File.ReadAllLines(reads).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [MonitorPipeWireFact]
    public async Task AClockReadDoesNotPublishTheOutputsFromBeforeASelectionChange()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { ClockSettle = TimeSpan.Zero, ClockMinGap = TimeSpan.Zero };
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        const string output = "test_clock_selection";
        pw.CreateNullSink(output, "Clock output");
        Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == output), TimeSpan.FromSeconds(5)));
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-clock-selection-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string? path = Environment.GetEnvironmentVariable("PATH");
        string started = Path.Combine(directory, "started");
        string release = Path.Combine(directory, "release");
        Task<bool>? read = null;
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                #!/bin/sh
                dir=$(dirname "$0")
                touch "$dir/started"
                while [ ! -f "$dir/release" ]; do sleep 0.01; done
                cat <<'EOF'
                [
                  {"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"test_driver"}}},
                  {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor","node.driver-id":1}}},
                  {"id":3,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"test_clock_selection","device.api":"test","node.driver-id":1}}}
                ]
                EOF
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            read = Task.Run(mixer.SyncClock);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(started), TimeSpan.FromSeconds(3)));
            mixer.SetMonitorOutput(output);
            File.WriteAllText(release, "");
            Assert.False(await read);
            Assert.Null(mixer.GraphClock);
            Assert.True(mixer.SyncClock());
            Assert.Equal(output, Assert.Single(mixer.GraphClock!.Followers).Name);
        }
        finally
        {
            if (read is { IsCompleted: false })
            {
                File.WriteAllText(release, "");
                await read;
            }
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

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
