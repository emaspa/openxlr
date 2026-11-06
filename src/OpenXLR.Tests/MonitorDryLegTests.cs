using System.Reflection;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The order a dry monitor send is written in, against a fake pactl: the
/// channel's own leg into a dry monitor mix closes before the dry leg opens,
/// the dry leg takes the send's level and mute, the other mixes, a monitor
/// mix that is not dry among them, keep their own legs, and taking the route
/// down hands the monitor send back to the processed path.
/// </summary>
[Collection("xdg-config")]
public sealed class MonitorDryLegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-dry-legs-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private readonly Mixer _mixer;

    // xlr1 into monitor, stream and monitor2 (legs 10, 11 and 12), and its dry
    // legs into monitor (30) and monitor2 (31). Monitor is dry, monitor2 is not.
    public MonitorDryLegTests()
    {
        ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
            dir=${0%/*}
            echo "$*" >> "$dir/writes"
            case "$*" in
                'list sinks short') printf '1\tOpenXLR_mix_monitor\n2\tOpenXLR_mix_stream\n3\tOpenXLR_mix_monitor2\n' ;;
                'list sink-inputs') /bin/cat "$dir/legs" ;;
            esac
            """);
        ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), "echo '[]'");
        ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "echo \"pw-link $*\" >> \"${0%/*}/writes\"");
        File.WriteAllText(Path.Combine(_dir, "legs"),
            "Sink Input #10\nOwner Module: 1\nSink: 1\nSink Input #11\nOwner Module: 1\nSink: 2\nSink Input #12\nOwner Module: 1\nSink: 3\n"
            + "Sink Input #30\nOwner Module: 3\nSink: 1\nSink Input #31\nOwner Module: 3\nSink: 3\n");
        Environment.SetEnvironmentVariable("PATH", _dir);
        _mixer = new Mixer(new PipeWireAdapter());
        Set("_built", true);
        Set("_config", new MixerConfig
        {
            Channels = [new("xlr1", "Mic 1") { InputPair = 0 }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic), new("monitor2", "Monitor B", MixKind.Monitor)],
        });
        Field<Dictionary<string, uint>>("_combineModules")["xlr1"] = 1;
        foreach ((string mix, int leg) in new[] { ("monitor", 10), ("stream", 11), ("monitor2", 12) })
        {
            string cell = "xlr1|" + mix;
            Field<HashSet<string>>("_cells").Add(cell);
            Field<HashSet<string>>("_muted").Add(cell);
            Field<Dictionary<string, double>>("_levels")[cell] = .8;
            Field<Dictionary<string, int>>("_legIndex")[cell] = leg;
        }
        // A ready, fed dry route for XLR 1, as SyncDryRoutesLocked leaves it.
        Type routeType = typeof(Mixer).GetNestedType("DryRoute", BindingFlags.NonPublic)!;
        object route = Activator.CreateInstance(routeType, 3u, Mixer.DryPattern(Field<MixerConfig>("_config").Mixes))!;
        routeType.GetProperty("Ready")!.SetValue(route, true);
        routeType.GetProperty("Feed")!.SetValue(route, new PortLink([("Wave_XLR:capture_AUX0", "OpenXLR_ch_xlr1.dry:playback_FL")]));
        Field<System.Collections.IDictionary>("_dryRoutes")["xlr1"] = route;
        Field<Dictionary<string, int>>("_dryLegIndex")["xlr1|monitor"] = 30;
        Field<Dictionary<string, int>>("_dryLegIndex")["xlr1|monitor2"] = 31;
        Field<HashSet<string>>("_dryMonitors").Add("monitor");
        Field<HashSet<string>>("_dryApplied").Add("monitor");
        File.WriteAllText(Path.Combine(_dir, "writes"), "");
    }

    private T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_mixer)!;

    private void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_mixer, value);

    private string[] Writes()
    {
        string[] lines = File.ReadAllLines(Path.Combine(_dir, "writes"));
        File.WriteAllText(Path.Combine(_dir, "writes"), "");
        return [.. lines.Where(l => l.StartsWith("set-sink-input", StringComparison.Ordinal) || l.StartsWith("pw-link -d", StringComparison.Ordinal))];
    }

    [Fact]
    public void TheMonitorSendOpensOnTheDryLegAfterTheChannelsOwnLegClosed()
    {
        _mixer.SetChannelMuted("xlr1", "monitor", false);
        string[] writes = Writes();
        int closed = Array.IndexOf(writes, "set-sink-input-mute 10 1");
        int opened = Array.IndexOf(writes, "set-sink-input-mute 30 0");
        Assert.True(closed >= 0 && opened > closed, string.Join(" | ", writes));
        Assert.Contains("set-sink-input-volume 10 0%", writes);
        // The dry leg carries the send level the channel's own leg would have.
        Assert.Contains($"set-sink-input-volume 30 {PipeWireAdapter.SinkInputPercent(Mixer.SendGain(.8, 1, 1))}%", writes);
        Assert.DoesNotContain(writes, w => w.Contains(" 11 ", StringComparison.Ordinal));
        Assert.DoesNotContain(writes, w => w.Contains(" 31 0", StringComparison.Ordinal));

        // The channel volume and a mute reach the dry leg as they would the send.
        _mixer.SetChannelVolume("xlr1", .5);
        Assert.Contains($"set-sink-input-volume 30 {PipeWireAdapter.SinkInputPercent(Mixer.SendGain(.8, .5, 1))}%", Writes());
        _mixer.SetChannelMuted("xlr1", "monitor", true);
        Assert.Contains("set-sink-input-mute 30 1", Writes());
    }

    [Fact]
    public void ARecordedMixKeepsTheProcessedSend()
    {
        _mixer.SetChannelMuted("xlr1", "stream", false);
        string[] writes = Writes();
        Assert.Contains("set-sink-input-mute 11 0", writes);
        Assert.DoesNotContain(writes, w => w.Contains(" 30 ", StringComparison.Ordinal));
    }

    [Fact]
    public void AMonitorMixThatIsNotDryKeepsTheProcessedSendAndItsDryLegClosed()
    {
        _mixer.SetChannelMuted("xlr1", "monitor2", false);
        string[] writes = Writes();
        int closed = Array.IndexOf(writes, "set-sink-input-mute 31 1");
        int opened = Array.IndexOf(writes, "set-sink-input-mute 12 0");
        Assert.True(closed >= 0 && opened > closed, string.Join(" | ", writes));
        Assert.DoesNotContain("set-sink-input-mute 31 0", writes);
        Assert.DoesNotContain(writes, w => w.Contains(" 10 ", StringComparison.Ordinal) || w.Contains(" 30 ", StringComparison.Ordinal));

        // Once Monitor B is dry too, its dry leg opens after its own leg closed.
        Field<HashSet<string>>("_dryMonitors").Add("monitor2");
        _mixer.SetLevel("xlr1", "monitor2", .7);
        writes = Writes();
        closed = Array.IndexOf(writes, "set-sink-input-mute 12 1");
        opened = Array.IndexOf(writes, "set-sink-input-mute 31 0");
        Assert.True(closed >= 0 && opened > closed, string.Join(" | ", writes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RediscoveringTheDryLegRecalculatesTheProcessedSend(bool dryLegPresent)
    {
        const string cell = "xlr1|monitor";
        // The processed leg must be rediscovered too, so ApplyCell performs a
        // fresh discovery before writing. The dry leg changes in that round.
        Field<Dictionary<string, int>>("_legIndex").Remove(cell);
        if (dryLegPresent)
            Field<Dictionary<string, int>>("_dryLegIndex").Remove(cell);
        else
        {
            string file = Path.Combine(_dir, "legs");
            string legs = File.ReadAllText(file);
            File.WriteAllText(file, legs[..legs.IndexOf("Sink Input #30", StringComparison.Ordinal)]);
        }

        _mixer.SetChannelMuted("xlr1", "monitor", false);
        string[] writes = Writes();
        if (dryLegPresent)
        {
            int closed = Array.IndexOf(writes, "set-sink-input-mute 10 1");
            int opened = Array.IndexOf(writes, "set-sink-input-mute 30 0");
            Assert.True(closed >= 0 && opened > closed, string.Join(" | ", writes));
        }
        else
        {
            Assert.Contains("set-sink-input-mute 10 0", writes);
            Assert.DoesNotContain("set-sink-input-mute 30 0", writes);
        }
    }

    [Fact]
    public void SwitchingOffUnlinksTheDryFeedBeforeTheProcessedSendOpens()
    {
        _mixer.SetChannelMuted("xlr1", "monitor", false);
        Writes();
        _mixer.SetMonitorDry("monitor", false);
        string[] writes = Writes();
        int unlinked = Array.FindIndex(writes, w => w.StartsWith("pw-link -d", StringComparison.Ordinal));
        int opened = Array.IndexOf(writes, "set-sink-input-mute 10 0");
        Assert.True(unlinked >= 0 && opened > unlinked, string.Join(" | ", writes));
        Assert.DoesNotContain(writes, w => w.Contains(" 30 0", StringComparison.Ordinal));
        Assert.Empty(Field<Dictionary<string, int>>("_dryLegIndex"));
    }

    public void Dispose()
    {
        try { _mixer.Dispose(); }
        finally { Environment.SetEnvironmentVariable("PATH", _path); Directory.Delete(_dir, true); }
    }
}
