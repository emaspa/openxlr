using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public Task ApplicationVolumeBalancesStreamsOnOneChannelAndSurvivesAPlaybackRestart()
        => CheckApplicationVolume(native: false);

    [MonitorPipeWireFact]
    public Task ApplicationVolumeBalancesNativePipeWireStreams()
        => CheckApplicationVolume(native: true);

    private static async Task CheckApplicationVolume(bool native)
    {
        const string app = "openxlr-app-volume-teams";
        const string other = "openxlr-app-volume-discord";
        var pw = new PipeWireAdapter();
        using var watch = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        var system = new ChannelDefinition("system", "System");
        mixer.Build(new MixerConfig { Channels = [system], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        using var all = new CancellationTokenSource();
        using var first = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
        var players = new List<Task<ProcessResult>>();
        Task<ProcessResult> Play(string identity, string media, CancellationToken cancel)
        {
            // Keep the test level out of WirePlumber's saved state. Other
            // tests on this server use pw-cat as a unity-gain signal source.
            var task = native
                ? ProcessRunner.RunAsync("pw-cat", ["--raw", "--playback", "--target", system.SinkName,
                    "--properties", $"{{ application.id = {identity} application.name = {identity} application.process.binary = {identity} media.name = {media} state.restore-props = false }}",
                    "/dev/zero"], TimeSpan.FromSeconds(30), cancel: cancel)
                : ProcessRunner.RunAsync("pacat", ["--raw", "--playback", $"--device={system.SinkName}",
                    $"--client-name={identity}", $"--property=application.process.binary={identity}",
                    $"--stream-name={media}", "/dev/zero"], TimeSpan.FromSeconds(30), cancel: cancel);
            players.Add(task);
            return task;
        }

        Dictionary<string, (int Index, string Volume, uint Sink)> Wait(
            Func<Dictionary<string, (int Index, string Volume, uint Sink)>, bool> condition)
        {
            Dictionary<string, (int Index, string Volume, uint Sink)> items = [];
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                items = StreamVolumes();
                if (condition(items)) return true;
                Thread.Sleep(20);
                return false;
            }, TimeSpan.FromSeconds(5)), string.Join(", ", items));
            return items;
        }

        bool RegistrySees(Dictionary<string, (int Index, string Volume, uint Sink)> items, double? appLevel = null)
        {
            IReadOnlyList<AudioStream> streams = pw.ListStreams();
            return items.All(item => streams.Any(stream => stream.Serial == item.Value.Index
                && stream.Identity == (item.Key == "discord" ? other : app)
                && (item.Key == "discord" || appLevel is null || stream.Volume == appLevel)));
        }

        try
        {
            Task<ProcessResult> firstPlayer = Play(app, "teams-first", first.Token);
            _ = Play(app, "teams-second", all.Token);
            _ = Play(other, "discord", all.Token);
            // Pulse and the registry can see a new stream in either order.
            // Wait for both app streams and the peer to enter the mixer's snapshot.
            var before = Wait(items => items.Count == 3 && RegistrySees(items));
            mixer.SyncStreams();
            Assert.Equal(3, before.Count);
            Assert.Single(before.Values.Select(stream => stream.Sink).Distinct());
            int arrivals = 0;
            pw.PlaybackStreamArrived += () => Interlocked.Increment(ref arrivals);
            mixer.SetAppVolume(app.ToUpperInvariant(), .35);
            // A busy server can defer a write or restore an old value.
            // Both registry echoes must settle before the desktop edit.
            long? echoedAt = null;
            var after = Wait(items =>
            {
                if (items.Count != 3 || items.GetValueOrDefault("teams-first").Volume != "35%"
                    || items.GetValueOrDefault("teams-second").Volume != "35%" || !RegistrySees(items, .35))
                {
                    echoedAt = null;
                    return false;
                }
                echoedAt ??= Environment.TickCount64;
                return Environment.TickCount64 - echoedAt.Value >= 400;
            });
            Assert.Equal(before["discord"], after["discord"]);
            Assert.Equal(before["teams-first"].Sink, after["teams-first"].Sink);
            Assert.Equal(before["teams-second"].Sink, after["teams-second"].Sink);
            Assert.Equal(.35, mixer.Snapshot().Streams.Single(a => a.Identity == app).LiveVolume);
            Assert.False(mixer.TakeAppVolumesChanged());

            // A desktop adjustment of one stream becomes the application's
            // level: the saved one follows it and the other stream gets it.
            pw.SetSinkInputVolume(after["teams-second"].Index, .62);
            Wait(items => items.GetValueOrDefault("teams-first").Volume == "62%"
                && items.GetValueOrDefault("teams-second").Volume == "62%");
            Assert.Equal(.62, mixer.ExportSettings().AppVolumes[app]);
            Assert.True(mixer.TakeAppVolumesChanged());
            Assert.Equal(before["discord"], StreamVolumes()["discord"]);

            // A stream that starts later gets the level from the pass the
            // registry's announcement starts, without a sweep.
            first.Cancel();
            await firstPlayer;
            Wait(items => !items.ContainsKey("teams-first"));
            int seen = Volatile.Read(ref arrivals);
            _ = Play(app, "teams-restarted", all.Token);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref arrivals) > seen, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncAppVolumes();
                var items = StreamVolumes();
                return items.TryGetValue("teams-restarted", out var stream) && stream.Volume == "62%"
                    && RegistrySees(items);
            }, TimeSpan.FromSeconds(5)), string.Join(", ", StreamVolumes()));
            mixer.SyncAppVolumes();

            // Reset hands the streams back at full volume.
            mixer.SetAppVolume(app, null);
            Assert.Empty(mixer.ExportSettings().AppVolumes);
            var reset = Wait(items => items.GetValueOrDefault("teams-second").Volume == "100%"
                && items.GetValueOrDefault("teams-restarted").Volume == "100%");
            Assert.Equal(before["discord"], reset["discord"]);
            Assert.Equal(before["teams-second"].Sink, reset["teams-second"].Sink);
            Assert.Equal(before["teams-first"].Sink, reset["teams-restarted"].Sink);
        }
        finally
        {
            all.Cancel();
            await Task.WhenAll(players);
        }
    }

    private static Dictionary<string, (int Index, string Volume, uint Sink)> StreamVolumes()
    {
        ProcessResult result = ProcessRunner.Run("pactl", ["--format=json", "list", "sink-inputs"]);
        Assert.True(result.Ok, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        return json.RootElement.EnumerateArray()
            // Pulse can list a stream before attaching it. Its unsigned
            // PA_INVALID_INDEX sink id is not an output we can compare.
            .Where(stream => stream.GetProperty("properties").TryGetProperty("media.name", out JsonElement name)
                && name.GetString() is "teams-first" or "teams-second" or "teams-restarted" or "discord"
                && stream.GetProperty("sink").TryGetUInt32(out uint sink) && sink != uint.MaxValue)
            .ToDictionary(stream => stream.GetProperty("properties").GetProperty("media.name").GetString()!,
                stream => (stream.GetProperty("index").GetInt32(),
                    stream.GetProperty("volume").EnumerateObject().First().Value.GetProperty("value_percent").GetString()!,
                    stream.GetProperty("sink").GetUInt32()));
    }
}
