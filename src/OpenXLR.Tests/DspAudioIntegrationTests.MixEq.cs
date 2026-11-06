using System.Globalization;
using System.Reflection;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The output EQ on a private PipeWire server: XLR 1 and XLR 2 on the fake
/// unit's 1 kHz tone, both sent to the Stream mix, which has the latency
/// fixture as an insert. The EQ is one stereo stage after the insert, in
/// front of what records the mix; every value reaches it live, with no node,
/// link or helper process, the insert chain is never built again for it, a
/// chain the mixer gave up on stays off, and the microphone EQ works beside it.
/// </summary>
public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public Task AMixEqIsOneLiveStageAfterTheInsertsAndTheMicrophoneEqStillWorks() => OnTheUnitWithTone(compensate: false, (mixer, pw, runs) =>
    {
        // The insert runs in PipeWire's filter chain, whose nodes would come
        // back with new serials if anything built it again; the native
        // helpers counted are the EQ stages alone.
        mixer.SetInserts("mix:stream", [Fixture("mix", 100)]);
        ChainGraph chain = SettledChain("stream", "OpenXLR_ins_");
        // The chain as the mixer holds it. PipeWire's filter chain, the
        // native host, or a mix of the two when this PipeWire refuses the
        // fixture or plugin latency is measured: each names its ends its own
        // way, so the test reads them rather than guess.
        var chains = (Dictionary<string, FilterHandle>)typeof(Mixer)
            .GetField("_chains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        FilterHandle? StreamChain() => chains.TryGetValue("mix:stream", out FilterHandle? held) ? held : null;
        string Links(string into) => $"{into} <- [{string.Join(", ", LinksInto(into))}]";
        int hosts = NativeHosts();
        string recorded = new MixDefinition("stream", "Stream", MixKind.VirtualMic).PostSinkName;
        double flat = NodeLevel(recorded);
        double mic = Level("xlr1");

        // Values set while the EQ is off are only stored: no stage runs.
        mixer.SetMixEqBand("stream", 2, new(Frequency: 1000, Gain: 12, Q: 1));
        Assert.Null(mixer.MixEqNode("stream"));
        Assert.Null(MixState(mixer, "stream").Backend);
        Assert.Equal(hosts, NativeHosts());

        // On: one stereo stage, fed by the insert chain, read by the post sink.
        mixer.SetMixEqEnabled("stream", true);
        string node = mixer.MixEqNode("stream")!;
        Assert.StartsWith("OpenXLR_mixeq_stream_", node);
        ChainGraph eq = SettledChain("stream", "OpenXLR_mixeq_");
        Assert.Single(eq.Nodes);
        Assert.Contains(StreamChain()!.SourceName, LinksInto(node));
        Assert.Equal([node], LinksInto(recorded).Distinct());
        Assert.Equal(chain.Nodes, Chain("stream", "OpenXLR_ins_").Nodes);
        Assert.True(SpinWait.SpinUntil(() => NativeHosts() == hosts + 1, TimeSpan.FromSeconds(5)), "The stage is not one helper.");
        Assert.Equal("software", MixState(mixer, "stream").Backend);
        AssertNodeLevel(recorded, flat + 12);
        // The microphones and the other mixes are left as they were.
        AssertLevel("xlr1", mic);
        Assert.Null(mixer.MixEqNode("chat"));

        // Every value is live: no node, no link, no helper process.
        void Live(Action change, double expected)
        {
            int before = Volatile.Read(ref runs.Count);
            change();
            Assert.Equal(before, Volatile.Read(ref runs.Count));
            AssertNodeLevel(recorded, expected);
            Assert.Equal(eq, SettledChain("stream", "OpenXLR_mixeq_"));
            Assert.Equal(chain.Nodes, Chain("stream", "OpenXLR_ins_").Nodes);
            Assert.Equal(hosts + 1, NativeHosts());
        }
        Live(() => mixer.SetMixEqBand("stream", 2, new(Enabled: false)), flat);
        Live(() => mixer.SetMixEqBand("stream", 2, new(Enabled: true)), flat + 12);
        Live(() => mixer.SetMixEqEnabled("stream", false), flat);
        Assert.Equal("software", MixState(mixer, "stream").Backend);   // bypassed, still running
        Live(() => mixer.SetMixEqEnabled("stream", true), flat + 12);
        Live(() => mixer.SetMixEqBand("stream", 2, new(Gain: 6.1)), flat + 6);
        Assert.Equal(6, mixer.MixEqFor("stream").Bands[1].Gain);
        // A shelf is at half its gain at its frequency.
        Live(() => mixer.SetMixEqBand("stream", 2, new(Type: "lowShelf")), flat + 3);
        Live(() => mixer.SetMixEqBand("stream", 2, new(Type: "peak")), flat + 6);

        // A recall that changes only values builds nothing, though the rest
        // of the scene runs its own helpers; one from before the output EQ
        // leaves it alone.
        MixerScene scene = mixer.ExportScene();
        MicEq louder = scene.MixEq!["stream"].WithBand(2, scene.MixEq["stream"].Bands[1] with { Gain = 9 });
        void Recalled(MixerScene recalled, double expected)
        {
            mixer.ApplyScene(recalled);
            AssertNodeLevel(recorded, expected);
            Assert.Equal(eq, SettledChain("stream", "OpenXLR_mixeq_"));
            Assert.Equal(chain.Nodes, Chain("stream", "OpenXLR_ins_").Nodes);
            Assert.Equal(hosts + 1, NativeHosts());
        }
        Recalled(scene with { MixEq = new() { ["stream"] = louder } }, flat + 9);
        Recalled(scene with { MixEq = null }, flat + 9);

        // The microphone EQ beside it still runs in its own mono stage.
        mixer.SetEqBand("xlr1", 2, new(Frequency: 1000, Gain: -6, Q: 1));
        mixer.SetEqEnabled("xlr1", true);
        Assert.Single(SettledChain("xlr1", "OpenXLR_eq_").Nodes);
        AssertLevel("xlr1", mic - 6);
        Assert.Equal(eq, SettledChain("stream", "OpenXLR_mixeq_"));
        mixer.SetEqEnabled("xlr1", false);
        AssertLevel("xlr1", mic);
        AssertNodeLevel(recorded, flat + 9);

        // The insert chain built again for its own reasons: the stage keeps
        // running and takes its input from the new chain. A rebuilt mix
        // chain keeps its node names, and a link made while the old nodes are
        // still leaving goes with them, so the sweep links it, as it does
        // without the EQ.
        mixer.SetInserts("mix:stream", [Fixture("mix", 100), Fixture("more", 50)]);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return StreamChain() is { } rebuilt && LinksInto(node).Contains(rebuilt.SourceName)
                && LinksInto(rebuilt.SinkName).Contains("OpenXLR_mix_stream") && LinksInto(recorded).Contains(node);
        }, TimeSpan.FromSeconds(8)), "The rebuilt chain was not linked between the mix and its EQ: "
            + (StreamChain() is { } now ? $"{Links(now.SinkName)}; {now.SourceName} -> EQ? {Links(node)}" : "no chain"));
        Assert.NotEqual(chain.Nodes, SettledChain("stream", "OpenXLR_ins_").Nodes);
        Assert.Equal(eq.Nodes, Chain("stream", "OpenXLR_mixeq_").Nodes);
        AssertNodeLevel(recorded, flat + 9);
        chain = SettledChain("stream", "OpenXLR_ins_");
        hosts = NativeHosts();

        // Another number of bands is the one change that starts a fresh stage.
        mixer.SetMixEq("stream", louder with { Bands = louder.Bands.Take(3).ToArray() });
        ChainGraph three = SettledChain("stream", "OpenXLR_mixeq_");
        Assert.NotEqual(eq.Nodes, three.Nodes);
        Assert.Single(three.Nodes);
        Assert.Equal(chain.Nodes, Chain("stream", "OpenXLR_ins_").Nodes);
        Assert.True(SpinWait.SpinUntil(() => NativeHosts() == hosts, TimeSpan.FromSeconds(5)), "The old stage's helper is still running.");
        AssertNodeLevel(recorded, flat + 9);

        // The stage's helper dies: the sweep starts a new one, the chain keeps running.
        KillChainHolder(three);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            ChainGraph now = Chain("stream", "OpenXLR_mixeq_");
            return now.Nodes.Count == 1 && !now.Nodes.Keys.Intersect(three.Nodes.Keys).Any();
        }, TimeSpan.FromSeconds(8)), "The mix EQ was not started again.");
        AssertNodeLevel(recorded, flat + 9);
        Assert.Equal(chain.Nodes, Chain("stream", "OpenXLR_ins_").Nodes);

        // A chain the mixer gave up on stays off whatever the EQ does.
        var restarts = typeof(Mixer).GetField("_restarts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        MethodInfo failed = restarts.GetType().GetMethod("Failed")!;
        for (int i = 0; i < 3; i++) failed.Invoke(restarts, ["mix:stream", "test"]);
        // The process the mixer watches for the chain; a native host chain
        // runs one per insert.
        System.Diagnostics.Process holder = StreamChain()!.Process;
        holder.Kill();
        holder.WaitForExit(2000);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            return Chain("stream", "OpenXLR_ins_").Nodes.Count == 0;
        }, TimeSpan.FromSeconds(8)), "The given-up chain was built again.");
        string current = mixer.MixEqNode("stream")!;
        Assert.True(SpinWait.SpinUntil(() => LinksInto(current).Contains("OpenXLR_mix_stream"), TimeSpan.FromSeconds(5)),
            "The EQ does not take the mix itself without its chain: " + string.Join(", ", LinksInto(current)));
        mixer.SetMixEqEnabled("stream", false);
        mixer.SetMixEqEnabled("stream", true);
        mixer.SetMixEq("stream", louder);
        mixer.EnsureFilterRoutes();
        Assert.Empty(Chain("stream", "OpenXLR_ins_").Nodes);
        Assert.All(mixer.Snapshot().Inserts["mix:stream"], insert => Assert.NotNull(insert.Error));

        // Deleting the mix takes its EQ with it.
        string last = mixer.MixEqNode("stream")!;
        mixer.DeleteMix("stream", _ => null);
        Assert.Null(mixer.MixEqNode("stream"));
        Assert.True(SpinWait.SpinUntil(() => pw.FindNodeId(last) is null, TimeSpan.FromSeconds(5)), "The deleted mix's EQ is still in the graph.");
        Assert.DoesNotContain("stream", mixer.ExportSettings().MixEq.Keys);
    });

    private static MicEqState MixState(Mixer mixer, string mix) => mixer.Snapshot().Mixes.Single(m => m.Id == mix).Eq!;

    /// <summary>The native helpers this test process runs now: the plugin hosts and the EQ stages.</summary>
    private static int NativeHosts()
    {
        int count = 0;
        foreach (string dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            try
            {
                string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields[0] == "Z" || int.Parse(fields[1], CultureInfo.InvariantCulture) != Environment.ProcessId) continue;
                if (File.ReadAllText(Path.Combine(dir, "comm")).Trim().StartsWith("openxlr-lv2-hos", StringComparison.Ordinal)) count++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }

    /// <summary>Wait until a sink's level is within 0.2 dB of <paramref name="expected"/>.</summary>
    private static void AssertNodeLevel(string node, double expected, double tolerance = 0.2)
    {
        double last = double.NaN;
        Assert.True(SpinWait.SpinUntil(() => Math.Abs((last = NodeLevel(node)) - expected) <= tolerance, TimeSpan.FromSeconds(8)),
            $"{node} plays at {last:0.00} dBFS, expected {expected:0.00}");
    }
}
