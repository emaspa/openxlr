using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// The session manager restores a remembered volume and mute onto every
    /// new stream, and every leg of one channel shares the key it remembers
    /// them by. WirePlumber 0.4 writes full level onto a new leg when it
    /// remembers nothing, and on a busy machine that write lands after the
    /// mixer has set the leg. The sweep puts every leg back on its fader: the
    /// level, and the mute of a send the user had closed.
    /// </summary>
    [MonitorPipeWireFact]
    public void TheSweepPutsASendTheSessionManagerMovedBackOnItsFader()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        MixerConfig config = MonitorConfig();
        mixer.Build(config with { Channels = [config.Channels[0] with { MutedIn = new HashSet<string> { "chat" } }] });
        SettleSends(mixer);
        Assert.True(Legs()["test|chat"].Muted);

        // What a late restore does: full level on one send, and another
        // send's level with its mute lifted on a send the user had closed.
        RestoreLeg("monitor2", 1.0, muted: false);
        RestoreLeg("chat", Math.Pow(0.8, 3), muted: false);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var legs = Legs();
            return legs["test|monitor2"].Volume == 1.0 && !legs["test|chat"].Muted;
        }, TimeSpan.FromSeconds(3)), "the restore reached the legs");

        IReadOnlyList<string> held = [];
        Assert.True(SpinWait.SpinUntil(() => mixer.EnsureCellLevels(out held), TimeSpan.FromSeconds(3)),
            "the sweep saw the moved sends");
        Assert.Contains("test|monitor2", held);
        Assert.Contains("test|chat", held);
        SettleSends(mixer);
        var settled = Legs();
        Assert.True(settled["test|chat"].Muted);
        Assert.Equal(0.6, Math.Cbrt(settled["test|monitor2"].Volume), 2);
        Assert.Equal(0.8, Math.Cbrt(settled["test|monitor"].Volume), 2);

        // Nothing moved, nothing to do.
        Assert.False(mixer.EnsureCellLevels(out held));
        Assert.Empty(held);

        // The way WirePlumber writes it: Props on the node, not a pulse volume.
        static void RestoreLeg(string mix, double linear, bool muted)
        {
            int node = LegNode($"output.OpenXLR_ch_test_OpenXLR_mix_{mix}");
            string value = linear.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ProcessResult set = ProcessRunner.Run("pw-cli", ["set-param", node.ToString(), "Props",
                $"{{ mute: {(muted ? "true" : "false")}, channelVolumes: [ {value}, {value} ] }}"]);
            Assert.True(set.Ok, set.Stderr);
        }
    }

    /// <summary>The registry id of the node named <paramref name="name"/>.</summary>
    private static int LegNode(string name)
    {
        ProcessResult dump = ProcessRunner.Run("pw-dump", []);
        Assert.True(dump.Ok, dump.Stderr);
        using JsonDocument graph = PipeWireSnapshot.Parse(dump.Stdout);
        return graph.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("type").GetString() == "PipeWire:Interface:Node"
            && item.GetProperty("info").GetProperty("props").TryGetProperty("node.name", out JsonElement node)
            && node.GetString() == name).GetProperty("id").GetInt32();
    }

    /// <summary>
    /// Run the daemon's send sweep until every send's leg carries what the
    /// mixer reports for it, read back from pw-dump: the send level with the
    /// channel volume, and the send's own mute. The mix master and mute are
    /// on the mix sink, not on any send. A test that records audio straight after a build or a layout
    /// edit waits here, because the daemon only promises the fader from its
    /// sweep on: a leg can grow late, and the session manager can move a new
    /// one after the mixer set it.
    /// </summary>
    private static void SettleSends(Mixer mixer)
    {
        string wrong = "";
        bool settled = SpinWait.SpinUntil(() =>
        {
            mixer.EnsureCellLevels();
            MixerState state = mixer.Snapshot();
            var legs = Legs();
            var off = new List<string>();
            foreach (ChannelStatus channel in state.Channels)
                foreach (MixStatus mix in state.Mixes)
                {
                    double gain = channel.Levels[mix.Id] * (channel.Volume ?? 1);
                    bool muted = channel.MutedIn.Contains(mix.Id);
                    string cell = $"{channel.Id}|{mix.Id}";
                    if (!legs.TryGetValue(cell, out var leg))
                        off.Add(cell + " has no leg");
                    else if (Math.Abs(Math.Cbrt(leg.Volume) - Math.Round(gain * 100) / 100) > 0.005 || leg.Muted != muted)
                        off.Add($"{cell}={Math.Cbrt(leg.Volume):0.###}{(leg.Muted ? " muted" : "")} want {gain:0.###}{(muted ? " muted" : "")}");
                }
            wrong = string.Join(", ", off);
            return off.Count == 0;
        }, TimeSpan.FromSeconds(5));
        Assert.True(settled, "sends off their faders: " + wrong);
    }
}
