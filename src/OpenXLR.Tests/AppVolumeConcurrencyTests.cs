using System.Diagnostics;
using OpenXLR.Core.Mixing;
using static OpenXLR.Tests.BrowserTabRoutingTests;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class AppVolumeConcurrencyTests
{
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
