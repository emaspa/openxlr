using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using static OpenXLR.Tests.BrowserTabRoutingTests;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppVolumeConcurrencyTests
{
    [Fact]
    public async Task ArrivalsDuringAnActiveVolumePassWaitForOneFollowUp()
    {
        using var graph = new BrowserGraph();
        var settings = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, settings, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, settings, devices);
        var mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        Action arrive = typeof(MixerService).GetMethod("OnPlaybackStreamArrived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action>(service);
        graph.Build(mixer);
        string Stream(int id, int serial, int percent) => BrowserGraph.Node(id, serial, BrowserGraph.ChromiumProps, percent);
        graph.SetStreams(mixer, Stream(10, 100, 35));
        mixer.SyncStreams();
        mixer.SetAppVolume("chromium", .35);
        graph.ClearCalls();

        using var release = new ManualResetEventSlim();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        service.Changed += () =>
        {
            if (Interlocked.Increment(ref notifications) == 1)
            {
                first.SetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            else next.TrySetResult();
        };
        try
        {
            graph.SetStreams(mixer, Stream(10, 100, 35), Stream(11, 101, 80));
            arrive();
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

            graph.SetStreams(mixer, Stream(10, 100, 35), Stream(11, 101, 35), Stream(12, 102, 20));
            for (int i = 0; i < 20; i++) arrive();
            // Give a mistakenly started second worker time to report while
            // the first is still held in the notification it is delivering.
            Assert.NotSame(next.Task, await Task.WhenAny(next.Task, Task.Delay(500)));
            release.Set();
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["set-sink-input-volume 101 35%", "set-sink-input-volume 102 35%"],
                graph.Calls.Where(call => call.StartsWith("set-sink-input-volume", StringComparison.Ordinal)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AStreamPassReadingBeforeACommandReadsTheRegistryAgain()
    {
        using var graph = new BrowserGraph();
        using var mixer = new Mixer();
        graph.Build(mixer);
        string oldStream = BrowserGraph.Node(10, 100, BrowserGraph.ChromiumProps);
        graph.SetStreams(mixer, oldStream);
        mixer.SyncStreams();
        mixer.SetAppVolume("chromium", .35);
        graph.SetStreams(mixer);
        mixer.SyncStreams();
        graph.ClearCalls();

        // The first read still shows the stream that has gone; any later one
        // shows the registry as it is.
        string started = Path.Combine(graph.Folder, "snapshot-started");
        string release = Path.Combine(graph.Folder, "snapshot-release");
        File.WriteAllText(Path.Combine(graph.Folder, "stale-streams"), $"[{oldStream}]");
        ExecutableScript.Write(Path.Combine(graph.Folder, "pw-dump"), """
            dir=${0%/*}
            if [ -f "$dir/snapshot-started" ]; then /bin/cat "$dir/streams"; exit 0; fi
            /bin/cat "$dir/stale-streams"
            /bin/touch "$dir/snapshot-started"
            while [ ! -f "$dir/snapshot-release" ]; do /bin/sleep 0.01; done
            """);
        graph.SetStreams(mixer);
        Task<bool> arrival = Task.Run(mixer.SyncAppVolumes);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(started), TimeSpan.FromSeconds(5)));
            // The app stopped before the edit, so there is no stream to write.
            mixer.SetAppVolume("chromium", .67);
        }
        finally
        {
            File.WriteAllText(release, "");
            await arrival.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(await arrival);
        Assert.DoesNotContain(graph.Calls, call => call.StartsWith("set-sink-input-volume", StringComparison.Ordinal));
        Assert.Equal(.67, mixer.ExportSettings().AppVolumes["chromium"]);
        Assert.Null(Assert.Single(mixer.Snapshot().Streams).LiveVolume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACommandStopsWritingWhenTheSoundServerStalls(bool reset)
    {
        using var graph = new BrowserGraph();
        using var mixer = new Mixer();
        graph.Build(mixer);
        graph.SetStreams(mixer, [.. Enumerable.Range(10, 12).Select(id =>
            BrowserGraph.Node(id, 90 + id, BrowserGraph.ChromiumProps))]);
        mixer.SyncStreams();
        mixer.SetAppVolume("chromium", .35);
        graph.ClearCalls();

        string pactl = Path.Combine(graph.Folder, "pactl");
        string original = File.ReadAllText(pactl + ".body");
        ExecutableScript.Write(pactl, """
            dir=${0%/*}
            if [ "$1" = set-sink-input-volume ]; then
                echo "$*" >> "$dir/calls"
                /bin/sleep 30
            fi
            """ + "\n" + original);

        // Twelve writes at the usual five seconds each would hold the mixer
        // lock for a minute; one quarter-second write ends the pass.
        var elapsed = Stopwatch.StartNew();
        mixer.SetAppVolume("chromium", reset ? null : .5);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4), $"The command took {elapsed.Elapsed}.");
        Assert.Single(graph.Calls, call => call.StartsWith("set-sink-input-volume", StringComparison.Ordinal));
    }
}
