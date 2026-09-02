using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using Xunit;

namespace OpenXLR.Tests;

public sealed class Lv2ControlSurfaceTests
{
    [Fact]
    public void ParameterContractClampsAndQuantisesExternalValues()
    {
        var integer = new PluginParam("bands", "Bands", 1, 8, 4, false, true, false, false, []);
        var toggle = new PluginParam("enabled", "Enabled", 0, 1, 0, true, false, false, false, []);
        var choice = new PluginParam("mode", "Mode", 0, 4, 0, false, true, false, true,
            [new ScalePoint("Off", 0), new ScalePoint("Fast", 2), new ScalePoint("Slow", 4)]);

        Assert.Equal(8, integer.Normalize(99));
        Assert.Equal(3, integer.Normalize(2.6));
        Assert.Equal(1, toggle.Normalize(0.7));
        Assert.Equal(2, choice.Normalize(2.8));
        Assert.Throws<ArgumentOutOfRangeException>(() => integer.Normalize(double.NaN));
    }

    [Fact]
    public void LogarithmicSliderRoundTripsPositiveAndZeroBasedRanges()
    {
        double positive = InsertParamViewModel.ToSliderPosition(1000, 10, 100_000, logarithmic: true);
        Assert.Equal(1000, InsertParamViewModel.FromSliderPosition(positive, 10, 100_000, true), 8);

        double zeroBased = InsertParamViewModel.ToSliderPosition(1.25, 0, 10, logarithmic: true);
        Assert.Equal(1.25, InsertParamViewModel.FromSliderPosition(zeroBased, 0, 10, true), 8);
    }

    [Fact]
    public void CatalogBecomesReadyOnlyAfterEveryPluginWasAdded()
    {
        using var client = new DaemonClientForTest();
        var chain = new InsertsViewModel(client.Client, "xlr1");
        int choicesAtReady = -1;
        chain.CatalogLoaded += (_, _) => choicesAtReady = chain.PluginChoices.Count;

        chain.ApplyCatalog(JsonNode.Parse("""
            [
              { "plugin":"urn:first", "name":"First", "category":"Utility", "audioIns":1, "audioOuts":1, "params":[] },
              { "plugin":"urn:target", "name":"Target", "category":"Dynamics", "audioIns":1, "audioOuts":1,
                "params":[
                  { "symbol":"mode", "name":"Mode", "min":0, "max":2, "default":0,
                    "toggled":false, "integer":true, "logarithmic":false, "enumeration":true,
                    "unitSymbol":"dB", "scalePoints":[{"label":"Off","value":0},{"label":"On","value":2}] }
                ] }
            ]
            """));

        Assert.True(chain.CatalogReady);
        Assert.Equal(2, choicesAtReady);
        Assert.Equal(2, chain.PluginChoices.Count);

        var insert = new InsertViewModel(chain, "slot", "urn:target", "Target");
        insert.EnsureParams();
        InsertParamViewModel control = Assert.Single(insert.Params);
        Assert.True(control.IsEnumeration);
        Assert.Equal("dB", control.UnitSymbol);
        Assert.Equal(["Off", "On"], control.Options.Select(o => o.Label));
    }

    [Fact]
    public void InstalledLspCatalogExposesRichControlMetadataWhenAvailable()
    {
        List<PluginInfo> lsp = [.. Lv2Catalog.Plugins.Where(p =>
            p.Plugin.Contains("lsp-plug.in", StringComparison.OrdinalIgnoreCase))];
        if (lsp.Count == 0) return; // Optional distro package is not required in CI.

        PluginParam[] controls = [.. lsp.SelectMany(p => p.Params)];
        Assert.Contains(controls, p => !string.IsNullOrWhiteSpace(p.UnitSymbol));
        Assert.Contains(controls, p => p.Enumeration && p.ScalePoints.Count > 0);
        Assert.Contains(controls, p => p.Logarithmic);
    }

    /// <summary>Owns the async-disposable client without opening a connection.</summary>
    private sealed class DaemonClientForTest : IDisposable
    {
        public DaemonClient Client { get; } = new();
        public void Dispose() => Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
