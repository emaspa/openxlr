using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class MixLatencyTests
{
    [Fact]
    public void ParallelMixesReceiveOnlyTheDifferenceFromTheSlowestChain()
    {
        var plan = MixLatency.Plan(new Dictionary<string, double?> { ["monitor"] = 0, ["chat"] = 4.5, ["stream"] = 20 }, out var error);
        Assert.Null(error);
        Assert.Equal(20, plan["monitor"]);
        Assert.Equal(15.5, plan["chat"]);
        Assert.Equal(0, plan["stream"]);
        Assert.Empty(MixLatency.Plan(new Dictionary<string, double?>(), out error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2000.01)]
    public void OneUnknownOrExcessivePathDisablesAlignmentForAllMixes(double? latency)
    {
        var plan = MixLatency.Plan(new Dictionary<string, double?> { ["monitor"] = 25, ["stream"] = latency }, out var error);
        Assert.NotNull(error);
        Assert.All(plan.Values, v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData("latency 480 48000", 480)]
    [InlineData("latency 0 48000", 0)]
    [InlineData("latency 4294967294 48000", 4294967294)]
    [InlineData("latency 4294967295 48000", -1)]
    [InlineData("latency 480 44100", -1)]
    [InlineData("latency -1 48000", -1)]
    [InlineData("latency 1.5 48000", -1)]
    [InlineData("latency NaN 48000", -1)]
    [InlineData("latency 12 48000 junk", -1)]
    [InlineData("latency 99999999999999999999999 48000", -1)]
    public void NativeReportsMustBeIntegralBoundedAndUseTheActiveSampleRate(string line, long expected)
        => Assert.Equal(expected, NativePluginHost.ParseLatency(line, 48000));

    [Fact]
    public void CompensationIsOffByDefaultAndAFailedSaveDoesNotEnableIt()
    {
        using var mixer = new Mixer();
        Assert.False(mixer.ExportSettings().CompensateMixLatency);
        Assert.Throws<IOException>(() => mixer.SetMixLatencyCompensation(true, _ => "disk full"));
        Assert.False(mixer.ExportSettings().CompensateMixLatency);
        mixer.SetMixLatencyCompensation(true, saved => { Assert.True(saved.CompensateMixLatency); return null; });
        Assert.True(mixer.Snapshot().CompensateMixLatency);
        mixer.SetMixLatencyCompensation(false);
        Assert.False(mixer.Snapshot().CompensateMixLatency);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData("\"false\"", false)]
    public void TheCommandRequiresABoolean(string json, bool valid)
    {
        using var mixer = new Mixer();
        using var value = JsonDocument.Parse(json);
        Assert.Equal(valid, CommandValidation.Check(new Command { Cmd = "setMixLatencyCompensation", Value = value.RootElement }, mixer, _ => null) is null);
    }
}
