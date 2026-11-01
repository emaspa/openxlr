using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

public sealed class MixLatencyTests
{
    private static PluginInfo Lv2(bool? reportsLatency) =>
        new("lv2", "urn:test", "Test", "Plugin", 2, 2, "in_l", "out_l", [], [], ["in_l", "in_r"], ["out_l", "out_r"])
        { ReportsLatency = reportsLatency };

    [Fact]
    public void EachMixIsDelayedByItsDistanceFromTheSlowest()
    {
        var plan = MixLatency.Plan(new Dictionary<string, double> { ["monitor"] = 0, ["chat"] = 4.5, ["stream"] = 20 }, out string? error);
        Assert.Null(error);
        Assert.Equal(20, plan["monitor"]);
        Assert.Equal(15.5, plan["chat"]);
        Assert.Equal(0, plan["stream"]);
        Assert.Empty(MixLatency.Plan(new Dictionary<string, double>(), out error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2000.01)]
    public void AFigureOutsideTheLimitDelaysNoMix(double latency)
    {
        var plan = MixLatency.Plan(new Dictionary<string, double> { ["monitor"] = 25, ["stream"] = latency }, out string? error);
        Assert.NotNull(error);
        Assert.All(plan.Values, delay => Assert.Equal(0, delay));
    }

    [Theory]
    // Bypassed: out of the chain, whatever else is true.
    [InlineData(true, false, false, null, null, 0.0)]
    // A chain that is not running gives no figure.
    [InlineData(false, false, true, 10.0, true, null)]
    // The native host's report, and none before its first.
    [InlineData(false, true, true, 10.0, true, 10.0)]
    [InlineData(false, true, true, null, true, null)]
    // In the native host without a figure, a plugin with no latency port is still zero.
    [InlineData(false, true, true, null, false, 0.0)]
    // In the filter chain: zero only when the plugin has no latency port.
    [InlineData(false, true, false, null, false, 0.0)]
    [InlineData(false, true, false, null, true, null)]
    public void AnInsertGivesItsFigureOrNone(bool bypass, bool running, bool hosted, double? report, bool? reportsLatency, double? expected)
        => Assert.Equal(expected, MixLatency.InsertMilliseconds(bypass, running, hosted, report, Lv2(reportsLatency)));

    [Fact]
    public void AnLv2InsertWithoutTheNativeHostCountsAsZeroAndAlignmentGoesAhead()
    {
        // A plugin that declares a latency port but runs in the filter
        // chain, because the native host is not installed or cannot carry
        // it, has no way to report. Its mix is planned as if it added
        // nothing, rather than holding every mix back for a figure that
        // never comes.
        double? unknown = MixLatency.InsertMilliseconds(bypass: false, running: true, hosted: false, hostReport: null, Lv2(true));
        Assert.Null(unknown);
        double hosted = MixLatency.InsertMilliseconds(bypass: false, running: true, hosted: true, hostReport: 10, Lv2(true))!.Value;
        var plan = MixLatency.Plan(new Dictionary<string, double>
        {
            ["chat"] = MixLatency.Total([unknown, 5]),
            ["stream"] = MixLatency.Total([hosted]),
        }, out string? error);
        Assert.Null(error);
        Assert.Equal(5, plan["chat"]);
        Assert.Equal(0, plan["stream"]);
    }

    [Fact]
    public void CompensationIsOffUntilChosenAndTheChoiceIsSaved()
    {
        using var mixer = new Mixer();
        Assert.False(mixer.ExportSettings().CompensateMixLatency);
        Assert.False(mixer.Snapshot().CompensateMixLatency);
        mixer.SetMixLatencyCompensation(true);
        Assert.True(mixer.ExportSettings().CompensateMixLatency);
        Assert.True(mixer.Snapshot().CompensateMixLatency);
        Assert.Empty(mixer.Snapshot().MixDelayMilliseconds);
        mixer.SetMixLatencyCompensation(false);
        Assert.False(mixer.Snapshot().CompensateMixLatency);
        Assert.False(JsonSerializer.Deserialize<MixerSettings>("{}")!.CompensateMixLatency);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData("\"false\"", false)]
    public void TheCommandTakesABoolean(string json, bool valid)
    {
        using var mixer = new Mixer();
        using var value = JsonDocument.Parse(json);
        Assert.Equal(valid, CommandValidation.Check(new Command { Cmd = "setMixLatencyCompensation", Value = value.RootElement.Clone() }, mixer, _ => null) is null);
    }

    [Fact]
    public void TheControlsWindowSaysWhenAPluginGivesNoFigure()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:stream", 2);
        owner.Apply(JsonNode.Parse("""
            [{"insert":{"id":"a","kind":"lv2","plugin":"urn:a","bypass":false},"latencyMilliseconds":10.5},
             {"insert":{"id":"b","kind":"lv2","plugin":"urn:b","bypass":false},"latencyMilliseconds":null}]
            """));
        Assert.Equal(10.5, owner.Items[0].LatencyMilliseconds);
        Assert.Contains("10", owner.Items[0].LatencyText, StringComparison.Ordinal);
        Assert.Null(owner.Items[1].LatencyMilliseconds);
        Assert.Contains("unknown", owner.Items[1].LatencyText, StringComparison.Ordinal);
        Assert.Contains("zero", owner.Items[1].LatencyText, StringComparison.Ordinal);
    }

    [Theory]
    // Bypassed: out of the chain, so nothing measures it.
    [InlineData(true, true, 48.0, null)]
    // A chain that is not running, or a native host with no figure yet.
    [InlineData(false, false, 48.0, null)]
    [InlineData(false, true, null, null)]
    [InlineData(false, true, 48.0, 48.0)]
    [InlineData(false, true, 0.0, 0.0)]
    public void AnInsertGivesItsMeasuredDelayOnlyWhileItRuns(bool bypass, bool running, double? measured, double? expected)
        => Assert.Equal(expected, MixLatency.MeasuredMilliseconds(bypass, running, measured));

    [Fact]
    public void TheStateCarriesTheMeasuredDelayBesideTheReportedOne()
    {
        var status = new InsertStatus(new InsertDefinition { Id = "focus", Kind = "vst3", Plugin = "0123" }, null, NativeHostRunning: true)
        { LatencyMilliseconds = 0, MeasuredLatencyMilliseconds = 48 };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string json = JsonSerializer.Serialize(status, options);
        Assert.Contains("\"latencyMilliseconds\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"measuredLatencyMilliseconds\":48", json, StringComparison.Ordinal);
        json = JsonSerializer.Serialize(status with { MeasuredLatencyMilliseconds = null }, options);
        Assert.Contains("\"measuredLatencyMilliseconds\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMixerLeavesTheMeasuredDelayOutUntilAHostHasOne()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [new InsertDefinition { Id = "a", Kind = "lv2", Plugin = "urn:a" }]);
        InsertStatus status = Assert.Single(mixer.Snapshot().Inserts["xlr1"]);
        Assert.Null(status.MeasuredLatencyMilliseconds);
    }

    [Theory]
    // In the filter chain nothing measures: the reported figure alone.
    [InlineData(false, 10.5, 48.0, "Latency: 10.5 ms")]
    [InlineData(false, null, null, "Latency: unknown. Latency compensation counts it as zero.")]
    // In the native host, both, or "measuring" until there is a figure.
    [InlineData(true, 0.0, 48.0, "Latency: 0 ms reported, 48 ms measured")]
    [InlineData(true, 0.0, null, "Latency: 0 ms reported, measuring")]
    [InlineData(true, 32.0, 41.958, "Latency: 32 ms reported, 42 ms measured")]
    [InlineData(true, null, 29.96, "Latency: not reported, 30 ms measured. Latency compensation counts it as zero.")]
    [InlineData(true, null, null, "Latency: not reported, measuring. Latency compensation counts it as zero.")]
    public void TheControlsWindowShowsTheReportedAndTheMeasuredDelay(bool native, double? reported, double? measured, string expected)
    {
        // The window's text in English, numbers written the invariant way.
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("en", Localizer.Language);
            var owner = new InsertsViewModel(new DaemonClient(), "xlr1");
            var entry = new JsonObject
            {
                ["insert"] = JsonNode.Parse("""{"id":"a","kind":"vst3","plugin":"0123","bypass":false}"""),
                ["nativeHostRunning"] = native,
                ["latencyMilliseconds"] = reported,
                ["measuredLatencyMilliseconds"] = measured,
            };
            owner.Apply(new JsonArray(entry));
            Assert.Equal(expected, owner.Items[0].LatencyText);
            Assert.Equal(measured, owner.Items[0].MeasuredLatencyMilliseconds);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
