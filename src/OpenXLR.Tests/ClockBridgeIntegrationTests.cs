using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// A capture input on its own clock, on a private PipeWire server: two
    /// capture devices with clocks of their own (null sources with the
    /// priorities WirePlumber gives a USB microphone and a capture card), the
    /// microphone the higher, so it drives the mixer and the card follows it
    /// resampled. On its own clock the card leaves the mixer's graph: the
    /// bridge's input runs in the card's graph, its output in the mixer's,
    /// the readout lists the card as bridged rather than as a follower, and
    /// the bridge fills to its target and carries the card's frames without
    /// an underrun. Off again, the bridge is gone and the card follows the
    /// microphone as before.
    /// </summary>
    [MonitorPipeWireFact]
    public void ACaptureInputOnItsOwnClockLeavesTheMixersGraph()
    {
        Assert.True(NativePluginHost.HostInstalled, "Build with -p:EnableNativeLv2Host=true so the bridge helper sits beside the tests.");
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { ClockSettle = TimeSpan.Zero, ClockMinGap = TimeSpan.Zero };
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        var modules = new List<string>();
        void Source(string name, int priority, int device, int channels)
        {
            string module = ProcessRunner.Run("pactl", ["load-module", "module-null-sink", "media.class=Audio/Source/Virtual",
                $"sink_name={name}", $"channels={channels}", channels == 1 ? "channel_map=mono" : "channel_map=front-left,front-right",
                $"sink_properties=device.api=test device.id={device} priority.driver={priority} node.description={name}"]).StdoutText.Trim();
            Assert.False(string.IsNullOrEmpty(module), "pactl could not load the test source " + name);
            modules.Add(module);
            Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == name), TimeSpan.FromSeconds(5)));
        }
        MixerSettings? saved = null;
        try
        {
            Source("test_bridge_mic", 2200, 9012, 1);
            Source("test_bridge_card", 2109, 9011, 2);
            mixer.CreateCaptureChannel("Mic", "test_bridge_mic", 0, _ => null, monoChannel: 0);
            mixer.CreateCaptureChannel("Console", "test_bridge_card", 0, _ => null);
            string console = mixer.Snapshot().Channels.Single(c => c.CaptureSource == "test_bridge_card").Id;

            MixerClock? Settled(Func<MixerClock, bool> done)
            {
                MixerClock? last = null;
                SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureInputFeeds();
                    mixer.SyncClockBridges();
                    mixer.SyncClock();
                    last = mixer.Snapshot().Clock;
                    return last is not null && done(last);
                }, TimeSpan.FromSeconds(15));
                return last;
            }

            // On the mixer's clock the card follows the microphone, resampled.
            MixerClock clock = Assert.IsType<MixerClock>(Settled(c => c.Driver?.Name == "test_bridge_mic" && c.Followers.Count > 0));
            Assert.Equal("test_bridge_card", Assert.Single(clock.Followers).Name);
            Assert.Empty(clock.Bridged);

            mixer.SetCaptureOwnClock(console, true, s => { saved = s; return null; });
            Assert.True(saved!.UserChannels!.Single(c => c.Id == console).CaptureOwnClock);
            clock = Assert.IsType<MixerClock>(Settled(c => c.Driver?.Name == "test_bridge_mic" && c.Followers.Count == 0
                && c.Bridged.Any(b => b.Name == "test_bridge_card" && b.Separate)));
            Assert.Equal([console], Assert.Single(clock.Bridged).Channels);

            // In the registry: the bridge's input in the card's graph, its
            // output in the microphone's, and nothing links the card to the mixer.
            JsonElement[] graph = pw.ReadGraphOnce();
            uint card = NodeId(graph, "test_bridge_card"), mic = NodeId(graph, "test_bridge_mic");
            string bridge = NodeNames(graph).Single(n => n.StartsWith("OpenXLR_bridge_", StringComparison.Ordinal) && !n.EndsWith("_in", StringComparison.Ordinal));
            Assert.Equal(card, DriverOf(graph, bridge + "_in"));
            Assert.Equal(mic, DriverOf(graph, bridge));
            Assert.NotEqual(mic, DriverOf(graph, "test_bridge_card"));

            // The bridge fills to its target and holds: running, no underrun.
            CaptureClockStatus? own = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureInputFeeds();
                own = mixer.Snapshot().Channels.Single(c => c.Id == console).OwnClock;
                return own is { Running: true, DelayMs: > 0, TargetMs: > 0 };
            }, TimeSpan.FromSeconds(15)), $"The bridge did not start carrying audio: {own}");
            Assert.True(own!.On);
            Assert.Equal(0, own.Underruns);
            Assert.Null(own.Error);
            Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == console).CaptureConnected);

            // Off: the bridge stops and the card follows the microphone again.
            mixer.SetCaptureOwnClock(console, false, _ => null);
            clock = Assert.IsType<MixerClock>(Settled(c => c.Driver?.Name == "test_bridge_mic" && c.Followers.Count > 0 && c.Bridged.Count == 0));
            Assert.Equal("test_bridge_card", Assert.Single(clock.Followers).Name);
            Assert.True(SpinWait.SpinUntil(() => !NodeNames(pw.ReadGraphOnce()).Any(n => n.StartsWith("OpenXLR_bridge_", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5)), "The bridge kept running after its input left its own clock.");
            Assert.False(mixer.Snapshot().Channels.Single(c => c.Id == console).OwnClock!.On);
        }
        finally
        {
            foreach (string module in modules) ProcessRunner.Run("pactl", ["unload-module", module]);
        }

        static IEnumerable<string> NodeNames(JsonElement[] graph)
            => graph.Where(IsNode).Select(o => o.GetProperty("info").GetProperty("props").GetProperty("node.name").GetString()!);

        static bool IsNode(JsonElement o) => o.TryGetProperty("type", out JsonElement type) && type.GetString() == "PipeWire:Interface:Node"
            && o.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("props", out JsonElement props) && props.TryGetProperty("node.name", out _);

        static JsonElement Find(JsonElement[] graph, string name)
            => graph.Where(IsNode).Single(o => o.GetProperty("info").GetProperty("props").GetProperty("node.name").GetString() == name);

        static uint NodeId(JsonElement[] graph, string name) => Find(graph, name).GetProperty("id").GetUInt32();

        static uint? DriverOf(JsonElement[] graph, string name)
            => Find(graph, name).GetProperty("info").GetProperty("props").TryGetProperty("node.driver-id", out JsonElement id)
                && id.TryGetUInt32(out uint driver) ? driver : null;
    }
}
