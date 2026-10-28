using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void AnUnchainedCaptureKeepsTheExplicitMainGraph()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        // Everything in the graph before the mixer starts is left out, so any
        // node or link the mixer adds, whatever its name, has to be listed.
        (HashSet<int> Nodes, HashSet<int> Links)? baseline = null;
        using var mixer = CaptureChainFixture.Build(pw, out _, 1, () =>
        {
            // The test source links itself to its signal after it appears.
            CaptureChainFixture.Wait(() => CaptureChainFixture.Topology(([], [])).Links
                .Count(l => l == "capture_test_signal -> input.capture_test_source") == 2);
            baseline = CaptureChainFixture.Ids();
        });
        string[] nodes =
        [
            "OpenXLR_ch_capture", "OpenXLR_ch_system", "OpenXLR_mix_monitor",
            "output.OpenXLR_ch_capture_OpenXLR_mix_monitor", "output.OpenXLR_ch_system_OpenXLR_mix_monitor",
            "meter:OpenXLR_ch_capture", "meter:OpenXLR_ch_system", "meter:OpenXLR_mix_monitor",
        ];
        string[] links = [
            "capture_test_source -> OpenXLR_ch_capture",
            "output.OpenXLR_ch_capture_OpenXLR_mix_monitor -> OpenXLR_mix_monitor",
            "output.OpenXLR_ch_system_OpenXLR_mix_monitor -> OpenXLR_mix_monitor",
            "OpenXLR_ch_capture -> meter:OpenXLR_ch_capture",
            "OpenXLR_ch_system -> meter:OpenXLR_ch_system",
            "OpenXLR_mix_monitor -> meter:OpenXLR_mix_monitor",
        ];
        // The main graph has one hidden combine, its existing legs and its
        // existing meter. No insert bus, holder or extra meter is allowed.
        // Every link is a stereo pair: the mono port feeds both sides.
        string[] expectedNodes = [.. nodes.Order(StringComparer.Ordinal)];
        string[] expectedLinks = [.. links.SelectMany(l => new[] { l, l }).Order(StringComparer.Ordinal)];
        (string[] Nodes, string[] Links) Added() => CaptureChainFixture.Topology(baseline!.Value);
        CaptureChainFixture.Wait(() =>
        {
            var graph = Added();
            return expectedNodes.SequenceEqual(graph.Nodes) && expectedLinks.SequenceEqual(graph.Links);
        }, () => string.Join("\n", Added().Nodes) + "\n--\n" + string.Join("\n", Added().Links));
        mixer.SetInserts("capture", []);
        for (int i = 0; i < 30; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
        Assert.Equal(expectedNodes, Added().Nodes);
        Assert.Equal(expectedLinks, Added().Links);
    }

    [MonitorPipeWireFact]
    public void CaptureChainChangesKeepCombineIndexesAndHeldSendLevels()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.CreateMix("Other", _ => null, MixKind.Monitor);
        mixer.SetChannelVolume("capture", .8);
        mixer.SetLevel("capture", "monitor", .5);
        mixer.SetChannelMuted("capture", "monitor", false);
        mixer.SetLevel("capture", "other", .7);
        CaptureChainFixture.Wait(() => { mixer.EnsureCellLevels(); return pw.CombineLegLevels().Count == 4; });
        CaptureChainFixture.Wait(() => pw.CombineLegLevels().Values.OrderBy(v => v.Percent)
            .SequenceEqual(new[] { (0, false), (40, false), (56, true), (100, true) }));
        var legs = pw.CombineLegLevels().OrderBy(p => p.Key).ToArray();
        int? combine = pw.FindNodeId("OpenXLR_ch_capture");
        foreach (bool active in new[] { true, false, true, false })
        {
            mixer.SetInserts("capture", active ? [CaptureChainFixture.Gain(1)] : []);
            if (active) Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
            if (active) CaptureChainFixture.WaitForChain(pw, "capture");
            else CaptureChainFixture.Wait(() => CaptureChainFixture.Nodes(pw, "capture").Length == 0);
            Assert.Equal(combine, pw.FindNodeId("OpenXLR_ch_capture"));
            mixer.EnsureCellLevels(out IReadOnlyList<string> held);
            Assert.Empty(held);
            Assert.Equal(legs, pw.CombineLegLevels().OrderBy(p => p.Key));
        }
    }

    [MonitorPipeWireFact]
    public void ThirtyHealthyCaptureSweepsStartNoLinkHelper()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        CaptureChainFixture.WaitForChain(pw, "capture");
        CaptureChainFixture.Wait(() => !mixer.EnsureInputFeeds() && !mixer.EnsureFilterRoutes());
        long starts = pw.LinkHelperStarts;
        for (int i = 0; i < 30; i++)
        {
            Assert.False(mixer.EnsureInputFeeds());
            Assert.False(mixer.EnsureFilterRoutes());
        }
        Assert.Equal(starts, pw.LinkHelperStarts);
    }

    [MonitorPipeWireFact]
    public void CaptureHotplugAndDeletionStopOnlyThatChannelsChain()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw, out uint source);
        mixer.CreateCaptureChannel("Second", "capture_test_source", 0, _ => null, 0);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]);
        mixer.SetInserts("second", [CaptureChainFixture.Gain(1)]);
        string[] first = CaptureChainFixture.WaitForChain(pw, "capture");
        string[] second = CaptureChainFixture.WaitForChain(pw, "second");
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1), CaptureChainFixture.Gain(1) with { Id = "second_gain" }]);
        Assert.Equal(second, CaptureChainFixture.Nodes(pw, "second"));
        pw.UnloadModule(source);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return CaptureChainFixture.Nodes(pw, "capture").Length == 0
                && !mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected;
        });
        Assert.NotNull(mixer.Snapshot().Channels.Single(c => c.Id == "capture").BindingWarning);
        pw.CreateVirtualMic("capture_test_source", "capture_test_signal.monitor", "Capture returned");
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected;
        });
        Assert.Empty(first.Intersect(CaptureChainFixture.WaitForChain(pw, "capture")));
        second = CaptureChainFixture.WaitForChain(pw, "second");
        var scene = mixer.ExportScene();
        mixer.ApplyScene(scene);
        mixer.ApplyScene(scene);
        Assert.Equal(second, CaptureChainFixture.Nodes(pw, "second"));
        Assert.Throws<IOException>(() => mixer.DeleteApplicationChannel("capture", _ => "disk full"));
        Assert.Equal(2, mixer.ExportSettings().Inserts["capture"].Count);
        Assert.NotEmpty(CaptureChainFixture.Nodes(pw, "capture"));
        MixerSettings? saved = null;
        mixer.DeleteApplicationChannel("capture", s => { saved = s; return null; });
        CaptureChainFixture.Wait(() => CaptureChainFixture.Nodes(pw, "capture").Length == 0
            && pw.FindNodeId("OpenXLR_ch_capture") is null);
        Assert.False(saved!.Inserts.ContainsKey("capture"));
        Assert.False(mixer.Snapshot().Inserts.ContainsKey("capture"));
        Assert.Equal(second, CaptureChainFixture.Nodes(pw, "second"));
    }

    [MonitorPipeWireFact]
    public void ARejectedCaptureChainLinkLeavesItsDirectFeedAndOtherChains()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.CreateCaptureChannel("Second", "capture_test_source", 0, _ => null, 0);
        mixer.SetInserts("second", [CaptureChainFixture.Gain(1)]);
        string[] second = CaptureChainFixture.WaitForChain(pw, "second");
        RejectNextRouteLink("OpenXLR_lc_capture_", "OpenXLR_ch_capture:playback_FR",
            () => mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]));
        Assert.Equal(second, CaptureChainFixture.Nodes(pw, "second"));
        Assert.NotNull(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        CaptureChainFixture.Wait(() => CaptureChainFixture.Nodes(pw, "capture").Length == 0
            && IncomingRouteLinks("OpenXLR_ch_capture").Length == 2);
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected);
    }

    [MonitorPipeWireFact]
    public void CaptureHostDeathsHealAloneAndStopAfterThreeFailures()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.CreateCaptureChannel("Second", "capture_test_source", 0, _ => null, 0);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]);
        mixer.SetInserts("second", [CaptureChainFixture.Gain(1)]);
        string[] second = CaptureChainFixture.WaitForChain(pw, "second");
        for (int death = 1; death <= 3; death++)
        {
            string[] old = CaptureChainFixture.WaitForChain(pw, "capture");
            var chains = (Dictionary<string, FilterHandle>)typeof(Mixer).GetField("_chains",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
            var holder = chains["capture"].Process;
            string stat = File.ReadAllText($"/proc/{holder.Id}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal(Environment.ProcessId, int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
            holder.Kill();
            Assert.True(holder.WaitForExit(2000));
            CaptureChainFixture.Wait(() =>
            {
                mixer.EnsureInputFeeds();
                mixer.EnsureFilterRoutes();
                string[] now = CaptureChainFixture.Nodes(pw, "capture");
                return death == 3 ? now.Length == 0 : now.Length > 0 && !old.Intersect(now).Any();
            });
            Assert.Equal(second, CaptureChainFixture.Nodes(pw, "second"));
        }
        Assert.Contains(RestartPolicy.GivenUp, Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected);
        for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
        Assert.Empty(CaptureChainFixture.Nodes(pw, "capture"));

        // Once the window passes the sweep builds it again on its own, also
        // when no other chain runs to keep the sweep going.
        mixer.SetInserts("second", []);
        CaptureChainFixture.Wait(() => CaptureChainFixture.Nodes(pw, "second").Length == 0);
        for (int i = 0; i < 3; i++) { mixer.EnsureInputFeeds(); mixer.EnsureFilterRoutes(); }
        var restarts = (RestartPolicy)typeof(Mixer).GetField("_restarts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mixer)!;
        restarts.Clock = () => Environment.TickCount64 + 300_001;
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureFilterRoutes();
            return CaptureChainFixture.Nodes(pw, "capture").Length > 0;
        });
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["capture"]).Error);
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected);
    }

    [MonitorPipeWireFact]
    public void MonoMigrationKeepsLiveLinksAndSavesOnlyEmptyBindings()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        pw.CreateNullSink("migration_signal", "Migration signal");
        ProcessResult result = ProcessRunner.Run("pactl", ["load-module", "module-remap-source",
            "master=migration_signal.monitor", "source_name=migration_source", "channels=1",
            "master_channel_map=front-left", "channel_map=mono", "remix=no"]);
        Assert.True(result.Ok, result.Stderr);
        uint source = uint.Parse(result.StdoutText.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        using var mixer = new Mixer(pw);
        try
        {
            CaptureChainFixture.Wait(() => pw.ListDevices().Any(d => d.Name == "migration_source" && d.Channels == 1));
            var settings = new MixerSettings
            {
                UserChannels = [new("empty", "Empty", "migration_source"), new("kept", "Kept", "migration_source"), new("system", "System")],
                Inserts = new() { ["kept"] = [CaptureChainFixture.Gain(2) with { Bypass = true }] },
            };
            mixer.Build(MixerConfig.FromSettings(settings));
            mixer.ApplySettings(settings);
            CaptureChainFixture.Wait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Count(c => c.CaptureConnected) == 2; });
            uint[] links = IncomingRouteLinks("OpenXLR_ch_empty");
            Assert.Equal(2, links.Length);
            Assert.True(mixer.MigrateCaptureInputs());
            Assert.False(mixer.MigrateCaptureInputs());
            Assert.Equal(links, IncomingRouteLinks("OpenXLR_ch_empty"));
            MixerSettings saved = mixer.ExportSettings();
            Assert.Equal(0, saved.UserChannels!.Single(c => c.Id == "empty").CaptureMonoChannel);
            Assert.Null(saved.UserChannels!.Single(c => c.Id == "kept").CaptureMonoChannel);
            Assert.Equal(1, mixer.Snapshot().Channels.Single(c => c.Id == "empty").ChainWidth);
            Assert.Equal(2, mixer.Snapshot().Channels.Single(c => c.Id == "kept").ChainWidth);
            string directory = Directory.CreateTempSubdirectory("openxlr-capture-migration-").FullName;
            try
            {
                string path = Path.Combine(directory, "mixer.json");
                Assert.Null(saved.Save(path));
                MixerSettings restored = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
                Assert.Null(warning);
                Assert.Equal(0, restored.UserChannels!.Single(c => c.Id == "empty").CaptureMonoChannel);
                Assert.Null(restored.UserChannels!.Single(c => c.Id == "kept").CaptureMonoChannel);
                Assert.Single(restored.Inserts["kept"]);
            }
            finally { Directory.Delete(directory, true); }
        }
        finally { UnloadModule(source); }
    }

    [MonitorPipeWireFact]
    public void RemovingTheLastCaptureInsertKeepsTheChainUntilTheDirectFeedConnects()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = CaptureChainFixture.Build(pw);
        mixer.SetInserts("capture", [CaptureChainFixture.Gain(1)]);
        string[] chain = CaptureChainFixture.WaitForChain(pw, "capture");
        RejectNextRouteLink("capture_test_source:", "OpenXLR_ch_capture:playback_",
            () => mixer.SetInserts("capture", []), everyLinkInto: true);
        Assert.Equal(chain, CaptureChainFixture.Nodes(pw, "capture"));
        Assert.NotNull(mixer.Snapshot().Channels.Single(c => c.Id == "capture").BindingWarning);
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return CaptureChainFixture.Nodes(pw, "capture").Length == 0 && IncomingRouteLinks("OpenXLR_ch_capture").Length == 2;
        });
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "capture").BindingWarning);
    }
}

internal static class CaptureChainFixture
{
    internal static InsertDefinition Gain(int width, double gain = .25)
    {
        string uri = width == 1 ? "urn:openxlr:test:gain:mono" : "urn:openxlr:test:gain";
        Assert.NotNull(PluginCatalog.Find("lv2", uri));
        return new() { Id = "gain", Kind = "lv2", Plugin = uri, Params = new() { ["gain"] = gain } };
    }

    internal static Mixer Build(PipeWireAdapter pw) => Build(pw, out _);

    internal static Mixer Build(PipeWireAdapter pw, out uint source, int width = 1, Action? beforeMixer = null)
    {
        pw.CreateNullSink("capture_test_signal", "Capture signal");
        source = pw.CreateVirtualMic("capture_test_source", "capture_test_signal.monitor", "Capture source");
        Wait(() => pw.ListDevices().Any(d => d.Name == "capture_test_source"));
        beforeMixer?.Invoke();
        var mixer = new Mixer(pw);
        try
        {
            mixer.Build(new MixerConfig
            {
                Channels = [new("system", "System"), new("capture", "Capture")
                    { CaptureSource = "capture_test_source", CaptureMonoChannel = width == 1 ? 0 : null }],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
            });
            Wait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Single(c => c.Id == "capture").CaptureConnected; });
            return mixer;
        }
        catch { mixer.Dispose(); throw; }
    }

    internal static void Wait(Func<bool> condition, Func<string>? describe = null)
        => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8)),
            "The private capture graph did not settle." + (describe is null ? "" : "\n" + describe()));

    internal static string[] Nodes(PipeWireAdapter pw, string key)
        => [.. pw.DumpNodes().Select(n => n.Name).Where(n => n.StartsWith($"OpenXLR_lc_{key}_", StringComparison.Ordinal)).Order()];

    internal static string[] WaitForChain(PipeWireAdapter pw, string key)
    {
        Wait(() => Nodes(pw, key).Length > 0);
        return Nodes(pw, key);
    }

    /// <summary>The ids of every node and link in the graph now.</summary>
    internal static (HashSet<int> Nodes, HashSet<int> Links) Ids()
    {
        using JsonDocument dump = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        var nodes = new HashSet<int>();
        var links = new HashSet<int>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            string? type = item.GetProperty("type").GetString();
            if (type == "PipeWire:Interface:Node") nodes.Add(item.GetProperty("id").GetInt32());
            else if (type == "PipeWire:Interface:Link") links.Add(item.GetProperty("id").GetInt32());
        }
        return (nodes, links);
    }

    /// <summary>
    /// The nodes and links that are not in <paramref name="baseline"/>, by
    /// name. A meter is named after the node it reads; a node without a name
    /// still counts.
    /// </summary>
    internal static (string[] Nodes, string[] Links) Topology((HashSet<int> Nodes, HashSet<int> Links) baseline)
    {
        using JsonDocument dump = PipeWireSnapshot.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        var names = new Dictionary<int, string>();
        var added = new List<string>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Node") continue;
            int id = item.GetProperty("id").GetInt32();
            string label = $"unnamed node {id}";
            if (item.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("props", out JsonElement props))
            {
                if (props.TryGetProperty("node.name", out JsonElement name) && name.GetString() is { } value) label = value;
                if (props.TryGetProperty("node.link-group", out JsonElement group)
                    && group.GetString() is { } meter && meter.StartsWith("openxlr-meter-", StringComparison.Ordinal))
                    label = "meter:" + meter["openxlr-meter-".Length..];
            }
            names[id] = label;
            if (!baseline.Nodes.Contains(id)) added.Add(label);
        }
        var links = new List<string>();
        foreach (JsonElement item in dump.RootElement.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "PipeWire:Interface:Link"
                || baseline.Links.Contains(item.GetProperty("id").GetInt32())
                || !item.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object) continue;
            int from = info.GetProperty("output-node-id").GetInt32(), to = info.GetProperty("input-node-id").GetInt32();
            links.Add($"{names.GetValueOrDefault(from, $"node {from}")} -> {names.GetValueOrDefault(to, $"node {to}")}");
        }
        return ([.. added.Order(StringComparer.Ordinal)], [.. links.Order(StringComparer.Ordinal)]);
    }
}
