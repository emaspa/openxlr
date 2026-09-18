using OpenXLR.Core.Mixing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.Tests;

public sealed class Lv2CatalogBudgetTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
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

    [Theory]
    [InlineData("ä漢😀\"\\\n<>&+")]
    [InlineData("ordinary ASCII metadata")]
    public void TheEstimateIncludesEscapingAndAllMetadata(string text)
    {
        string large = string.Concat(Enumerable.Repeat(text, 40));
        PluginInfo plugin = Plugin("Effect", 1, 1) with
        {
            Kind = "vst3", Name = large, Category = large, Path = large,
            InputSymbol = large, OutputSymbol = large, InputSymbols = [large], OutputSymbols = [large],
            RequiredFeatures = [large], UnsupportedFeatures = [large], NativeUiRequiredFeatures = [large],
            AudioIns = int.MaxValue, AudioOuts = int.MaxValue, Widths = [int.MaxValue],
            HasNativeUi = true, NativeUiBlocked = true,
            Params = [new PluginParam(large, large, -double.MaxValue, double.MaxValue, double.Epsilon,
                false, false, false, false, [new ScalePoint(large, double.MaxValue)])],
        };
        // Measured, not estimated: the wire bytes plus the list separator.
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(plugin, Json).Length + 1, Lv2Catalog.Footprint(plugin));
    }

    [Fact]
    public void AUnicodeCatalogueFitsTheActualClientMessageBudget()
    {
        string name = new('漢', 200);
        List<PluginInfo> plugins = [.. Enumerable.Range(0, 400).Select(i => Plugin($"Effect {i}", 0) with
        {
            Params = [.. Enumerable.Range(0, 20).Select(p => new PluginParam($"p{p}", name, 0, 1, 0,
                false, false, false, false, []))],
        })];
        var sent = ClientCatalog.ForClient(plugins, []);
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new { type = "plugins", plugins = sent }, Json);
        Assert.True(message.Length <= Lv2Catalog.CatalogBudgetBytes + 64, $"Catalogue response was {message.Length} bytes");
        Assert.NotEmpty(sent);
    }
}
