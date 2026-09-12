using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class RemovePluginInsertsTests
{
    private static InsertDefinition Insert(string id, string plugin, bool bypass = false, string kind = "vst3")
        => new() { Id = id, Kind = kind, Plugin = plugin, Bypass = bypass, Params = new() { ["gain"] = 0.25 } };

    [Fact]
    public void RemovalCoversEveryChainAndBypassedCopyWithoutChangingOtherInserts()
    {
        var first = Insert("first", "other");
        var last = Insert("last", "other", bypass: true);
        var otherFormat = Insert("other-format", "eq", kind: "clap");
        List<InsertDefinition> untouched = [Insert("chat", "other")];
        var chains = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] = [first, Insert("a", "eq"), Insert("b", "eq", bypass: true), last],
            ["xlr2"] = [Insert("c", "eq")],
            ["mix:monitor"] = [Insert("d", "eq"), otherFormat],
            ["mix:stream"] = [Insert("e", "eq", bypass: true)],
            ["mix:chat"] = untouched,
        };
        var rewired = new List<string[]>();
        int saves = 0;
        var result = Mixer.RemovePluginInserts(chains, new HashSet<(string, string)> { ("vst3", "eq") },
            keys => rewired.Add(keys.ToArray()), () =>
            {
                saves++;
                Assert.DoesNotContain(chains.Values.SelectMany(c => c), i => i.Kind == "vst3" && i.Plugin == "eq");
            });
        Assert.Equal((5, 4, null), result);
        Assert.Equal(1, saves);
        Assert.Equal(3, rewired.Count);
        Assert.Single(rewired, keys => keys.SequenceEqual(new[] { "xlr1", "xlr2" }));
        Assert.DoesNotContain(rewired, keys => keys.Contains("mix:chat"));
        Assert.Equal(new[] { first, last }, chains["xlr1"]);
        Assert.Same(first, chains["xlr1"][0]);
        Assert.Same(last, chains["xlr1"][1]);
        Assert.Equal(0.25, first.Params["gain"]);
        Assert.True(last.Bypass);
        Assert.Same(otherFormat, Assert.Single(chains["mix:monitor"]));
        Assert.Same(untouched, chains["mix:chat"]);
    }

    [Fact]
    public void AFailedSharedInputRewireKeepsBothInputDefinitionsAndReportsPartialSuccess()
    {
        List<InsertDefinition> first = [Insert("a", "eq")], second = [Insert("b", "eq")];
        var chains = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] = first, ["xlr2"] = second, ["mix:stream"] = [Insert("c", "eq")],
        };
        int saves = 0;
        var result = Mixer.RemovePluginInserts(chains, new HashSet<(string, string)> { ("vst3", "eq") },
            keys => { if (keys.Contains("xlr1")) throw new IOException("input rewire failed"); }, () => saves++);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Chains);
        Assert.Contains("xlr1, xlr2", result.Error);
        Assert.Same(first, chains["xlr1"]);
        Assert.Same(second, chains["xlr2"]);
        Assert.Empty(chains["mix:stream"]);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void AFailedSaveRestoresDefinitionsBeforeRewiringBack()
    {
        List<InsertDefinition> before = [Insert("a", "eq")];
        var chains = new Dictionary<string, List<InsertDefinition>> { ["mix:monitor"] = before };
        var counts = new List<int>();
        var error = Assert.Throws<InvalidOperationException>(() => Mixer.RemovePluginInserts(chains,
            new HashSet<(string, string)> { ("vst3", "eq") },
            _ => counts.Add(chains["mix:monitor"].Count), () => throw new IOException("disk full")));
        Assert.Contains("disk full", error.Message);
        Assert.Same(before, chains["mix:monitor"]);
        Assert.Equal(new[] { 0, 1 }, counts);
    }

    [Fact]
    public void MissingMatchesDoNotRewireButStillFlushPendingSettings()
    {
        List<InsertDefinition> before = [Insert("a", "other")];
        var chains = new Dictionary<string, List<InsertDefinition>> { ["mix:monitor"] = before };
        int saves = 0;
        var result = Mixer.RemovePluginInserts(chains, new HashSet<(string, string)> { ("vst3", "eq") },
            _ => Assert.Fail("An unaffected chain was rewired."), () => saves++);
        Assert.Equal((0, 0, null), result);
        Assert.Equal(1, saves);
        Assert.Same(before, chains["mix:monitor"]);
    }

    [Fact]
    public void AllClassesInTheSelectedBundleAreResolvedWithoutMatchingSiblingPaths()
    {
        static PluginInfo Plugin(string id, string? path)
            => new("vst3", id, id, "EQ", 2, 2, "in", "out", [], [], [], []) { Path = path };
        PluginInfo[] catalogue = [
            Plugin("one", "/wrappers/EQ.vst3"),
            Plugin("two", "/wrappers/EQ.vst3/Contents/x86_64-linux/EQ.so"),
            Plugin("other", "/wrappers/EQ.vst3-other"),
            Plugin("unknown", null),
        ];
        var ids = PluginCatalog.IdentitiesUnder(catalogue, ["/wrappers/EQ.vst3"]);
        Assert.Equal(new HashSet<(string, string)> { ("vst3", "one"), ("vst3", "two") }, ids);
    }
}
