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
            mixer.SetAppVolume(app.ToUpperInvariant(), .35);
            // Written before the command returns, with no sweep in between.
            var after = StreamVolumes();
            Assert.Equal("35%", after["teams-first"].Volume);
            Assert.Equal("35%", after["teams-second"].Volume);
            Assert.Equal(before["discord"], after["discord"]);
            Assert.Equal(before["teams-first"].Sink, after["teams-first"].Sink);
            // The registry shows the level a moment later; the sweep reads
            // it as the application's own and writes nothing.
            await Task.Delay(400, all.Token);
            Wait(_ => mixer.Snapshot().Streams.Single(a => a.Identity == app).LiveVolume == .35);
            Assert.False(mixer.TakeAppVolumesChanged());

            // A desktop adjustment of one stream becomes the application's
            // level: the saved one follows it and the other stream gets it.
            pw.SetSinkInputVolume(after["teams-second"].Index, .62);
            Wait(items => items["teams-first"].Volume == "62%" && items["teams-second"].Volume == "62%");
            Assert.Equal(.62, mixer.ExportSettings().AppVolumes[app]);
            Assert.True(mixer.TakeAppVolumesChanged());
            Assert.Equal(before["discord"], StreamVolumes()["discord"]);

            // A stream that starts later gets the level without a sweep.
            first.Cancel();
            await firstPlayer;
            Wait(items => !items.ContainsKey("teams-first"));
            _ = Play(app, "teams-restarted", all.Token);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncAppVolumes();
                return StreamVolumes().TryGetValue("teams-restarted", out var stream) && stream.Volume == "62%";
            }, TimeSpan.FromSeconds(5)), string.Join(", ", StreamVolumes()));

            // Reset hands the streams back at full volume.
            mixer.SetAppVolume(app, null);
            Assert.Empty(mixer.ExportSettings().AppVolumes);
            var reset = StreamVolumes();
            Assert.Equal("100%", reset["teams-second"].Volume);
            Assert.Equal("100%", reset["teams-restarted"].Volume);
            Assert.Equal(before["discord"], reset["discord"]);
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
