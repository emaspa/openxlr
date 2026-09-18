using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class CommandLimitsTests
{
    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "system" or "xlr1";
        public bool HasMix(string id) => id is "monitor" or "monitor2" or "stream";
        public bool IsMonitorFeed(string feed) => feed is "monitor" or "monitor2" or "monitor+monitor2";
        public bool IsMonitorOutput(string device) => device is "alsa_output.headset" or "alsa_output.katana" or "alsa_output.pro#";
        public bool IsInsertKey(string key) => key is "xlr1" or "mix:monitor";
        public InsertDefinition? InsertInChain(string key, string id)
            => key is "xlr1" && id is "comp" ? new InsertDefinition { Id = "comp", Kind = "lv2", Plugin = "urn:test:comp" } : null;
        public int OverrideCount { get; set; }
    }

    private static readonly PluginInfo Comp = new("lv2", "urn:test:comp", "Comp", "Dynamics", 1, 1, "in", "out",
        [new PluginParam("ratio", "Ratio", 1, 20, 4, false, false, false, false, [])], ["http://lv2plug.in/ns/ext/urid#map"], ["in"], ["out"]);
    private static readonly PluginInfo NeedsUi = Comp with
    {
        Plugin = "urn:test:ui", Name = "Fancy",
        UnsupportedFeatures = ["http://lv2plug.in/ns/ext/instance-access"],
    };

    private static PluginInfo? Find(InsertDefinition insert) => insert.Plugin switch { "urn:test:comp" => Comp, "urn:test:ui" => NeedsUi, _ => null };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Command Cmd(string json) => JsonSerializer.Deserialize<Command>(json, Json)!;

    [Theory]
    [InlineData("""{"cmd":"setLevel","channel":"system","mix":"monitor","value":0.5}""", null)]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream","value":0.5}""", null)]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream","value":0}""", null)]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream","value":1.5}""", "between 0 and 1")]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream","value":-0.1}""", "between 0 and 1")]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream","value":1e999}""", "finite")]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"stream"}""", "finite")]
    [InlineData("""{"cmd":"setOutputRoute","device":"alsa_output.headset","mix":"removed","value":0.5}""", "mix must exist")]
    [InlineData("""{"cmd":"setOutputRoute","device":"missing","mix":"stream","value":0.5}""", "selected output")]
    [InlineData("""{"cmd":"setOutputDeviceVolume","device":"alsa_output.headset","value":1.5}""", null)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":0}""", null)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":1.6}""", "between 0 and 1.5")]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":-0.1}""", "between 0 and 1.5")]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":1e999}""", "finite")]
    [InlineData("""{"cmd":"setOutputDeviceVolume","device":"alsa_output.headset"}""", "finite")]
    [InlineData("""{"cmd":"setOutputDeviceVolume","device":"","value":1}""", "invalid output name")]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.headset","mix":"monitor2"}""", null)]
    [InlineData("""{"cmd":"setMonitorFeed","device":"alsa_output.headset","mix":"nope"}""", "not a mix")]
    [InlineData("""{"cmd":"setLevel","channel":"nope","mix":"monitor","value":0.5}""", "unknown channel")]
    [InlineData("""{"cmd":"setLevel","channel":"system","mix":"nope","value":0.5}""", "unknown mix")]
    [InlineData("""{"cmd":"setLevel","channel":"system","mix":"monitor","value":"loud"}""", "finite number")]
    [InlineData("""{"cmd":"setInserts","channel":"game","inserts":[]}""", "no insert chain")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:comp","params":{"ratio":4}}]}""", null)]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:comp","params":{"gain":4}}]}""", "has no control")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:missing"}]}""", "not installed")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:ui"}]}""", "instance-access")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:comp"},{"id":"a","kind":"lv2","plugin":"urn:test:comp"}]}""", "duplicate")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"bad id!","kind":"lv2","plugin":"urn:test:comp"}]}""", "letters, digits")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[null]}""", "every insert")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":"lv2","plugin":"urn:test:comp","params":null}]}""", "parameters")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":null,"kind":"lv2","plugin":"urn:test:comp"}]}""", "every insert")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":[{"id":"a","kind":null,"plugin":"urn:test:comp"}]}""", "every insert")]
    [InlineData("""{"cmd":"setInserts","channel":"xlr1","inserts":null}""", "need 'channel' and 'inserts'")]
    [InlineData("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"ratio","value":4}""", null)]
    [InlineData("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"gone","symbol":"ratio","value":4}""", "not in the chain")]
    [InlineData("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"gain","value":4}""", "has no control")]
    [InlineData("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"ratio\nquit","value":4}""", "has no control")]
    [InlineData("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"ratio","value":"4"}""", "finite number")]
    public void RejectsWhatTheMixerUsedToSwallow(string json, string? expectedFragment)
    {
        string? result = CommandValidation.Check(Cmd(json), new Layout(), Find);
        if (expectedFragment is null) Assert.Null(result);
        else Assert.Contains(expectedFragment, result);
    }

    [Fact]
    public void BoundsListsStringsAndTheAppRegistry()
    {
        var layout = new Layout();
        string many = string.Join(",", Enumerable.Range(0, 17).Select(i => $"\"sink{i}\""));
        Assert.Contains("at most", CommandValidation.Check(Cmd("{\"cmd\":\"setMonitorOutputs\",\"devices\":[" + many + "]}"), layout, Find));
        Assert.Contains("too long", CommandValidation.Check(Cmd("{\"cmd\":\"assignApp\",\"channel\":\"system\",\"identity\":\"" + new string('x', 300) + "\"}"), layout, Find));
        Assert.Contains("invalid output name", CommandValidation.Check(Cmd("{\"cmd\":\"setOutputDeviceVolume\",\"device\":\"" + new string('x', 257) + "\",\"value\":1}"), layout, Find));
        Assert.Null(CommandValidation.Check(Cmd("{\"cmd\":\"setOutputDeviceVolume\",\"device\":\"" + new string('x', 256) + "\",\"value\":1}"), layout, Find));
        layout.OverrideCount = CommandValidation.MaxOverrides;
        Assert.Contains("remembered", CommandValidation.Check(Cmd("""{"cmd":"assignApp","channel":"system","identity":"new-app"}"""), layout, Find));
    }

    [Fact]
    public void FeatureGateFollowsTheChainHost()
    {
        Assert.Empty(Lv2Catalog.UnsupportedFeatures(["http://lv2plug.in/ns/ext/urid#map", "http://lv2plug.in/ns/ext/worker#schedule", "http://lv2plug.in/ns/ext/options#options"]));
        Assert.Equal(["http://lv2plug.in/ns/ext/instance-access"],
            Lv2Catalog.UnsupportedFeatures(["http://lv2plug.in/ns/ext/urid#map", "http://lv2plug.in/ns/ext/instance-access"]));
        Assert.False(NeedsUi.Supported);
        Assert.True(Comp.Supported);
    }

    [Fact]
    public void BudgetAllowsBurstsAndRefills()
    {
        long now = 1_000_000;   // monotonic milliseconds
        var budget = new CommandBudget(capacity: 10, refillPerSecond: 5, clock: () => now);
        for (int i = 0; i < 10; i++) Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        now += 1000;                        // 5 tokens back
        for (int i = 0; i < 5; i++) Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
    }
}
