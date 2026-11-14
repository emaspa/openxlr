using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// A channel volume scales every send of its own channel, in the stream
    /// that carries each send, and nothing else: not the other channel's
    /// sends, not the channel's sink, not a mix master. It is measured twice,
    /// on the leg volumes PipeWire reports and on recorded audio, and it holds
    /// through a mute, a profile recall, a layout edit and a rebuild.
    /// </summary>
    [MonitorPipeWireFact]
    public void AChannelVolumeScalesEverySendOfItsChannelAndNothingElse()
    {
        const string Output = "test_chanvol_output";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        // The output belongs to an adapter of its own, so a rebuild of the
        // mixer, which unloads every module the mixer's adapter loaded, leaves
        // it in place; it is removed in the finally block.
        var outputs = new PipeWireAdapter();
        using var mixer = new Mixer(pw);
        var config = new MixerConfig
        {
            Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor),
                new("chat", "Chat", MixKind.VirtualMic)],
            Channels =
            [
                new("test", "Test") { Levels = new Dictionary<string, double> { ["monitor"] = 0.8, ["monitor2"] = 0.6, ["chat"] = 1.0 } },
                new("other", "Other") { Levels = new Dictionary<string, double> { ["monitor"] = 0.5, ["monitor2"] = 0.9, ["chat"] = 0.4 } },
            ],
        };
        var level = new Dictionary<string, double>
        {
            ["test|monitor"] = 0.8, ["test|monitor2"] = 0.6, ["test|chat"] = 1.0,
            ["other|monitor"] = 0.5, ["other|monitor2"] = 0.9, ["other|chat"] = 0.4,
        };
        const double ChatMaster = 0.8;
        try
        {
            outputs.CreateNullSink(Output, "Channel volume output");
            mixer.Build(config);
            mixer.SetMonitorOutputs([Output]);
            mixer.SetMixVolume("chat", ChatMaster);
            WaitForLegs();
            var volumes = new Dictionary<string, double> { ["test"] = 1, ["other"] = 1 };
            AssertLegs();

            // Turning one channel down moves each of its sends and no other.
            mixer.SetChannelVolume("test", 0.7);
            volumes["test"] = 0.7;
            AssertLegs();
            Assert.Equal(1, pw.GetSinkVolume("OpenXLR_ch_test"));
            Assert.Equal(1, pw.GetSinkVolume("OpenXLR_ch_other"));
            Assert.Equal(1, pw.GetSinkVolume("OpenXLR_mix_monitor"));
            Assert.Equal(1, pw.GetSinkVolume("OpenXLR_mix_monitor2"));
            MixerState state = mixer.Snapshot();
            Assert.Equal(0.7, state.Channels.Single(c => c.Id == "test").Volume);
            Assert.Equal(1, state.Channels.Single(c => c.Id == "other").Volume);
            Assert.Equal(0.8, state.Channels.Single(c => c.Id == "test").Levels["monitor"]);
            Assert.Equal(ChatMaster, state.Mixes.Single(m => m.Id == "chat").Volume);

            // The audio agrees: every mix hears the channel at its send level
            // times its volume, and the other channel exactly as before.
            foreach (string mix in new[] { "monitor", "monitor2", "chat" })
            {
                Assert.Null(mixer.SetMonitorFeed(Output, mix));
                CaptureLevel(0.1 * Math.Pow(Gain("test", mix), 3), "OpenXLR_ch_test", Output);
            }
            Assert.Null(mixer.SetMonitorFeed(Output, "monitor+monitor2+chat"));
            CaptureLevel(0.1 * new[] { "monitor", "monitor2", "chat" }.Sum(mix => Math.Pow(Gain("other", mix), 3)),
                "OpenXLR_ch_other", Output);

            // Mute stays per send: a muted send stays muted through a volume
            // change and opens at the new volume.
            mixer.SetChannelMuted("test", "monitor", true);
            mixer.SetChannelVolume("test", 0.4);
            volumes["test"] = 0.4;
            Assert.True(SpinWait.SpinUntil(() => Legs()["test|monitor"].Muted, TimeSpan.FromSeconds(3)));
            AssertLegs();
            mixer.SetChannelMuted("test", "monitor", false);
            Assert.True(SpinWait.SpinUntil(() => !Legs()["test|monitor"].Muted, TimeSpan.FromSeconds(3)));
            AssertLegs();

            // A profile brings the volume back, and one saved before channel
            // volumes plays every channel it describes at full volume.
            MixerScene scene = mixer.ExportScene();
            Assert.Equal(0.4, scene.ChannelVolumes!["test"]);
            mixer.SetChannelVolume("test", 0.9);
            mixer.SetChannelVolume("other", 0.5);
            mixer.ApplyScene(scene);
            volumes["other"] = 1;
            AssertLegs();
            mixer.SetChannelVolume("other", 0.5);
            mixer.ApplyScene(scene with { ChannelVolumes = null });
            volumes["test"] = 1;
            volumes["other"] = 1;
            AssertLegs();
            mixer.SetChannelVolume("test", 0.4);
            volumes["test"] = 0.4;

            // A send added by a layout edit carries the volume, and so does
            // every send of a channel whose sink a rename reloads.
            mixer.CreateMix("Phones", _ => null, MixKind.Monitor);
            mixer.SetLevel("test", "phones", 1.0);
            mixer.SetChannelMuted("test", "phones", false);
            level["test|phones"] = 1.0;
            Assert.True(SpinWait.SpinUntil(() => Near(Legs().GetValueOrDefault("test|phones").Volume, Math.Pow(0.4, 3)),
                TimeSpan.FromSeconds(3)), "the new send carries the channel volume");
            mixer.DeleteMix("phones", _ => null);
            level.Remove("test|phones");
            mixer.RenameApplicationChannel("test", "Renamed", _ => null);
            WaitForLegs();
            AssertLegs();
            // A channel made later starts at full volume.
            mixer.CreateApplicationChannel("Fresh", _ => null);
            Assert.Equal(1, mixer.Snapshot().Channels.Single(c => c.Id == "fresh").Volume);
            mixer.DeleteApplicationChannel("fresh", _ => null);
            Assert.DoesNotContain("fresh", mixer.ExportSettings().ChannelVolumes.Keys);

            // The settings file brings it back after a rebuild.
            MixerSettings settings = mixer.ExportSettings();
            Assert.Equal(0.4, settings.ChannelVolumes["test"]);
            mixer.Build(config);
            mixer.ApplySettings(settings);
            WaitForLegs();
            AssertLegs();
            Assert.Null(mixer.SetMonitorFeed(Output, "monitor"));
            CaptureLevel(0.1 * Math.Pow(Gain("test", "monitor"), 3), "OpenXLR_ch_test", Output);

            // What a send's leg carries, and what the mix hears: the Chat
            // master is the Chat sink's own volume, after the leg.
            double Send(string channel, string mix) => level[$"{channel}|{mix}"] * volumes[channel];

            double Gain(string channel, string mix) => Send(channel, mix) * (mix == "chat" ? ChatMaster : 1);

            void AssertLegs()
            {
                Dictionary<string, (double Volume, bool Muted)> legs = [];
                bool settled = SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels();   // the daemon's sweep
                    legs = Legs();
                    return level.Keys.All(cell => legs.ContainsKey(cell) && Near(legs[cell].Volume, Math.Pow(GainOf(cell), 3)));
                }, TimeSpan.FromSeconds(3));
                Assert.True(settled, "leg volumes: " + string.Join(", ", level.Keys.Select(cell =>
                    $"{cell}={(legs.TryGetValue(cell, out var leg) ? leg.Volume : double.NaN):0.######} want {Math.Pow(GainOf(cell), 3):0.######}")));
            }

            double GainOf(string cell) => Send(cell[..cell.IndexOf('|')], cell[(cell.IndexOf('|') + 1)..]);

            void WaitForLegs() => Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureCellLevels();
                var legs = Legs();
                return level.Keys.All(legs.ContainsKey);
            }, TimeSpan.FromSeconds(5)), "every send's leg came up");
        }
        finally { outputs.TearDown(); }

        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-4;
    }

    /// <summary>
    /// Every channel send's internal stream by "channel|mix", with the gain
    /// PipeWire applies to it and its mute, read from pw-dump.
    /// </summary>
    private static Dictionary<string, (double Volume, bool Muted)> Legs()
    {
        var legs = new Dictionary<string, (double, bool)>();
        var result = ProcessRunner.Run("pw-dump", []);
        if (!result.Ok) return legs;
        using JsonDocument graph = PipeWireSnapshot.Parse(result.Stdout);
        foreach (JsonElement item in graph.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) continue;
            if (!info.TryGetProperty("props", out JsonElement props)
                || !props.TryGetProperty("node.name", out JsonElement name)
                || name.GetString() is not { } node
                || !node.StartsWith("output.OpenXLR_ch_", StringComparison.Ordinal)) continue;
            // output.OpenXLR_ch_<channel>_OpenXLR_mix_<mix>
            int split = node.IndexOf("_OpenXLR_mix_", StringComparison.Ordinal);
            if (split < 0) continue;
            string cell = node["output.OpenXLR_ch_".Length..split] + "|" + node[(split + "_OpenXLR_mix_".Length)..];
            if (!info.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object
                || !parameters.TryGetProperty("Props", out JsonElement list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement entry in list.EnumerateArray())
                if (entry.TryGetProperty("channelVolumes", out JsonElement channels) && channels.GetArrayLength() > 0)
                {
                    legs[cell] = (channels[0].GetDouble(), entry.TryGetProperty("mute", out JsonElement mute) && mute.GetBoolean());
                    break;
                }
        }
        return legs;
    }
}
