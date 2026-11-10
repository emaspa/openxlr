using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task ApplicationVolumeBalancesStreamsOnOneChannelAndSurvivesAPlaybackRestart()
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
            var task = ProcessRunner.RunAsync("pacat", ["--raw", "--playback", $"--device={system.SinkName}",
                $"--client-name={identity}", $"--property=application.process.binary={identity}",
                $"--stream-name={media}", "/dev/zero"], TimeSpan.FromSeconds(30), cancel: cancel);
            players.Add(task);
            return task;
        }

        void Wait(Func<Dictionary<string, (int Index, string Volume, uint Sink)>, bool> condition)
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                return condition(StreamVolumes());
            }, TimeSpan.FromSeconds(5)), string.Join(", ", StreamVolumes()));
        }

        try
        {
            Task<ProcessResult> firstPlayer = Play(app, "teams-first", first.Token);
            _ = Play(app, "teams-second", all.Token);
            _ = Play(other, "discord", all.Token);
            Wait(items => items.Count == 3 && mixer.Snapshot().Streams.Any(a => a.Identity == app && a.Active));
            var before = StreamVolumes();
            Assert.Equal(3, before.Count);
            Assert.Single(before.Values.Select(stream => stream.Sink).Distinct());
            Assert.Throws<IOException>(() => mixer.SetAppVolume(app, .35, _ => "disk full"));
            Assert.Empty(mixer.ExportSettings().AppVolumes);
            mixer.SetAppVolume(app.ToUpperInvariant(), .35, _ => null);
            Wait(items => items.TryGetValue("teams-first", out var a) && items.TryGetValue("teams-second", out var b)
                && a.Volume == "35%" && b.Volume == "35%");
            var after = StreamVolumes();
            Assert.Equal(before["discord"], after["discord"]);
            Assert.Equal(before["teams-first"].Sink, after["teams-first"].Sink);

            // A setting is applied once per stream, so a desktop adjustment
            // during this playback is not fought by the next sweep.
            pw.SetSinkInputVolume(after["teams-second"].Index, .62);
            mixer.SyncStreams();
            Assert.Equal("62%", StreamVolumes()["teams-second"].Volume);
            first.Cancel();
            await firstPlayer;
            Wait(items => !items.ContainsKey("teams-first"));
            _ = Play(app, "teams-restarted", all.Token);
            Wait(items => items.TryGetValue("teams-restarted", out var stream) && stream.Volume == "35%");
            Assert.Equal("62%", StreamVolumes()["teams-second"].Volume);
            Assert.Equal(.35, mixer.ExportSettings().AppVolumes[app]);
            mixer.SetAppVolume(app, null, _ => null);
            Assert.Empty(mixer.ExportSettings().AppVolumes);
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
