using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginCatalogTests
{
    private static PluginInfo Plugin(string kind, string name, int controls, int width = 1)
        => new(kind, kind + ":" + name, name, "", width, width, "in", "out",
            [.. Enumerable.Range(0, controls).Select(i => new PluginParam($"p{i}", $"Param {i}", 0, 1, 0, false, false, false, false, []))],
            [], ["in"], ["out"]);

    /// <summary>
    /// Enough ordinary LV2 plugins to fill the message a client is sent, in
    /// a size that leaves less room over than one large plugin needs.
    /// </summary>
    private static List<PluginInfo> FullOfLv2()
        => [.. Enumerable.Range(0, 1200).Select(i => Plugin("lv2", $"Plugin {i}", 40))];

    [Fact]
    public void EverySourceIsKeptWholeAndOneThatFailsCostsOnlyItsOwnPlugins()
    {
        List<PluginInfo> lv2 = FullOfLv2();
        PluginInfo vst3 = Plugin("vst3", "Nova", 76);
        IReadOnlyList<PluginInfo> all = PluginCatalog.Combine(
            () => lv2,
            () => throw new InvalidOperationException("the scanner died"),
            () => [vst3]);

        // The catalogue an insert is resolved against is not bounded: the
        // whole LV2 scan is there, and so is the plugin there was no room for.
        Assert.Equal(lv2.Count + 1, all.Count);
        Assert.True(all.Sum(p => (long)Lv2Catalog.Footprint(p)) > Lv2Catalog.CatalogBudgetBytes);
        Assert.NotNull(PluginCatalog.Find(all, "vst3", vst3.Plugin));
        Assert.NotNull(PluginCatalog.Find(all, "lv2", lv2[^1].Plugin));
    }

    [Fact]
    public void APluginASavedChainUsesIsSentToClientsThoughTheBudgetHasNoRoomForIt()
    {
        List<PluginInfo> lv2 = FullOfLv2();
        PluginInfo used = Plugin("vst3", "Nova", Lv2Catalog.MaxControls), unused = Plugin("vst3", "Supermassive", Lv2Catalog.MaxControls);
        IReadOnlyList<PluginInfo> all = PluginCatalog.Combine(() => lv2, () => [used, unused]);

        // As it was: the LV2 set fills the message and the other format gets
        // what is left, which is nothing.
        IReadOnlyList<PluginInfo> idle = ClientCatalog.ForClient(all, []);
        Assert.DoesNotContain(idle, p => p.Kind == "vst3");

        // With a chain on it, the plugin is listed, and paid for by the LV2
        // entries that no longer fit.
        IReadOnlyList<PluginInfo> sent = ClientCatalog.ForClient(all, [("vst3", used.Plugin)]);
        Assert.Contains(sent, p => p.Plugin == used.Plugin);
        Assert.DoesNotContain(sent, p => p.Plugin == unused.Plugin);
        Assert.True(sent.Count(p => p.Kind == "lv2") < idle.Count(p => p.Kind == "lv2"));
        Assert.True(sent.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);

        // An insert is resolved against the whole catalogue either way, so
        // the chain loads whether or not the picker could show the plugin.
        Assert.NotNull(PluginCatalog.Find(all, "vst3", unused.Plugin));
    }

    [Fact]
    public void PinnedPluginsAreListedSmallestFirstAndStillStopAtTheBudget()
    {
        // More chain plugins than a message can hold: the list ends where a
        // client would stop reading, and the small ones are the ones kept.
        List<PluginInfo> monsters = [.. Enumerable.Range(0, 200).Select(i => Plugin("clap", $"Monster {i}", Lv2Catalog.MaxControls))];
        PluginInfo small = Plugin("clap", "Small", 4);
        List<PluginInfo> clap = [.. monsters, small];
        List<PluginInfo> sent = ClientCatalog.Merge([.. clap.Select(p => (p.Kind, p.Plugin))], [], clap);

        Assert.True(sent.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);
        Assert.True(sent.Count < clap.Count);
        Assert.Contains(sent, p => p.Plugin == small.Plugin);
        Assert.Equal(sent.Count, sent.Distinct().Count());
    }

    [Fact]
    public void TheSavedFileNamesTheChainsPluginsBeforeTheMixerHasRestoredThem()
    {
        // What a client asking during startup is answered from.
        var saved = new MixerSettings
        {
            Inserts = new Dictionary<string, List<InsertDefinition>>
            {
                ["xlr1"] = [new() { Id = "a", Kind = "vst3", Plugin = "ABCD", Label = "Nova" },
                            new() { Id = "b", Kind = "lv2", Plugin = "urn:test:eq", Bypass = true }],
                ["stream"] = [new() { Id = "c", Kind = "clap", Plugin = "com.vendor.verb" }],
            },
        };
        Assert.Equal([("clap", "com.vendor.verb"), ("lv2", "urn:test:eq"), ("vst3", "ABCD")],
            saved.InsertPlugins().Order());
    }

    [Fact]
    public void TheLv2ListStaysWholeWhenTheChainsPluginsLeaveRoomForIt()
    {
        // The ordinary case: a chain plugin changes nothing about the order
        // the rest of the catalogue is offered in.
        List<PluginInfo> lv2 = [.. Enumerable.Range(0, 20).Select(i => Plugin("lv2", $"Plugin {i}", 30))];
        List<PluginInfo> vst3 = [Plugin("vst3", "Nova", 76)];
        List<PluginInfo> sent = ClientCatalog.Merge([("vst3", vst3[0].Plugin)], lv2, vst3);

        Assert.Equal(lv2.Select(p => p.Plugin), sent.Where(p => p.Kind == "lv2").Select(p => p.Plugin));
        Assert.Equal(lv2.Count + 1, sent.Count);
    }
}
