using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class Lv2CatalogBudgetTests
{
    private static PluginInfo Plugin(string name, int controls, int points = 0)
        => new("lv2", "urn:test:" + name, name, "Dynamics", 1, 1, "in", "out",
            [.. Enumerable.Range(0, controls).Select(i => new PluginParam($"p{i}", $"Param {i}", 0, 1, 0, false, false, false, false,
                [.. Enumerable.Range(0, points).Select(j => new ScalePoint($"point {j}", j))]))],
            [], ["in"], ["out"]);

    [Fact]
    public void ACatalogUnderBudgetIsUntouchedAndAnOversizedOneLosesItsLargestPluginsFirst()
    {
        var small = new List<PluginInfo> { Plugin("Comp", 8), Plugin("Gate", 4) };
        Assert.Same(small, Lv2Catalog.WithinBudget(small));

        // Twenty monsters near the per-plugin limits, plus two ordinary plugins.
        var all = new List<PluginInfo> { Plugin("Comp", 8), Plugin("Gate", 4) };
        for (int i = 0; i < 20; i++) all.Add(Plugin($"Monster {i}", Lv2Catalog.MaxControls, Lv2Catalog.MaxScalePoints));
        Assert.True(all.Sum(p => (long)Lv2Catalog.Footprint(p)) > Lv2Catalog.CatalogBudgetBytes);
        List<PluginInfo> kept = Lv2Catalog.WithinBudget(all);
        Assert.True(kept.Sum(p => (long)Lv2Catalog.Footprint(p)) <= Lv2Catalog.CatalogBudgetBytes);
        Assert.Contains(kept, p => p.Name == "Comp");
        Assert.Contains(kept, p => p.Name == "Gate");
        Assert.True(kept.Count < all.Count);
        Assert.Equal(all.Where(kept.Contains).Select(p => p.Name), kept.Select(p => p.Name));   // order preserved
    }
}
