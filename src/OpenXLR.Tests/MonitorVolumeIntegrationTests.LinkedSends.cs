using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// A linked channel's sends on a real graph: linking writes the first
    /// mix's level into every send's stream, a level set on any mix reaches
    /// every stream of the channel and no other, the mutes stay per send,
    /// and a mix added while linked starts the channel's send at the linked
    /// level while an unlinked channel's starts at unity.
    /// </summary>
    [MonitorPipeWireFact]
    public void ALinkedChannelPlaysOneLevelInEveryMix()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        var config = new MixerConfig
        {
            Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
            Channels =
            [
                new("test", "Test") { Levels = new Dictionary<string, double> { ["monitor"] = 0.8, ["chat"] = 0.3 } },
                new("other", "Other") { Levels = new Dictionary<string, double> { ["monitor"] = 0.5, ["chat"] = 0.4 } },
            ],
        };
        var level = new Dictionary<string, double>
        {
            ["test|monitor"] = 0.8, ["test|chat"] = 0.3, ["other|monitor"] = 0.5, ["other|chat"] = 0.4,
        };
        mixer.Build(config);
        WaitForLegs();
        AssertLegs();

        mixer.SetLinkedSends("test", true);
        level["test|chat"] = 0.8;
        AssertLegs();

        mixer.SetLevel("test", "chat", 0.5);
        level["test|monitor"] = level["test|chat"] = 0.5;
        AssertLegs();

        // A mute moves one send only, and the level stays linked under it.
        mixer.SetChannelMuted("test", "chat", true);
        Assert.True(SpinWait.SpinUntil(() => Legs()["test|chat"].Muted, TimeSpan.FromSeconds(3)));
        Assert.False(Legs()["test|monitor"].Muted);
        mixer.SetLevel("test", "monitor", 0.6);
        level["test|monitor"] = level["test|chat"] = 0.6;
        AssertLegs();
        mixer.SetChannelMuted("test", "chat", false);

        mixer.CreateMix("Phones", _ => null, MixKind.Monitor);
        MixerState state = mixer.Snapshot();
        Assert.Equal(0.6, state.Channels.Single(c => c.Id == "test").Levels["phones"]);
        Assert.Equal(1.0, state.Channels.Single(c => c.Id == "other").Levels["phones"]);
        Assert.Contains("phones", state.Channels.Single(c => c.Id == "test").MutedIn);
        level["test|phones"] = 0.6;
        level["other|phones"] = 1.0;
        WaitForLegs();
        AssertLegs();

        // Unlinking moves nothing, and the sends go their own way again.
        mixer.SetLinkedSends("test", false);
        mixer.SetLevel("test", "phones", 0.2);
        level["test|phones"] = 0.2;
        AssertLegs();

        void AssertLegs()
        {
            Dictionary<string, (double Volume, bool Muted)> legs = [];
            bool settled = SpinWait.SpinUntil(() =>
            {
                mixer.EnsureCellLevels();
                legs = Legs();
                return level.Keys.All(cell => legs.ContainsKey(cell) && Math.Abs(legs[cell].Volume - Math.Pow(level[cell], 3)) < 1e-4);
            }, TimeSpan.FromSeconds(3));
            Assert.True(settled, "leg volumes: " + string.Join(", ", level.Keys.Select(cell =>
                $"{cell}={(legs.TryGetValue(cell, out var leg) ? leg.Volume : double.NaN):0.######} want {Math.Pow(level[cell], 3):0.######}")));
        }

        void WaitForLegs() => Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureCellLevels();
            var legs = Legs();
            return level.Keys.All(legs.ContainsKey);
        }, TimeSpan.FromSeconds(5)), "every send's leg came up");
    }
}
