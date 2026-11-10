using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The ducking commands as the daemon checks them before the mixer sees
/// them: <c>setDucking</c> names only the fields it carries, each checked
/// for its shape and words and, with a layout, its ids; anything else is
/// refused with the field's message. <c>setDuckingProcessing</c> takes
/// <c>device</c> and <c>software</c>.
/// </summary>
public sealed class CommandValidationTests : IDisposable
{
    private readonly Mixer _layout = new(new PipeWireAdapter { DuckHost = "/missing-openxlr-duck-host" });

    public void Dispose() => _layout.Dispose();

    private static Command Ducking(string value, string cmd = "setDucking")
    {
        using JsonDocument doc = JsonDocument.Parse(value);
        return new Command { Cmd = cmd, Value = doc.RootElement.Clone() };
    }

    private DuckingChange Change(string value)
    {
        Assert.Null(CommandValidation.CheckDucking(Ducking(value), _layout, out DuckingChange? change));
        return change!;
    }

    [Fact]
    public void AWellFormedSetDuckingNamesOnlyWhatItCarries()
    {
        Assert.Equal(new DuckingChange(Enabled: true), Change("""{"enabled":true}"""));
        // The single key of a client from before several keys is a list of one.
        Assert.Equal(new DuckingChange(Keys: ["xlr2"]), Change("""{"key":"xlr2"}"""));
        Assert.Equal(new DuckingChange(Keys: ["xlr2", "music", "aux", "line"]), Change("""{"keys":["xlr2","music","aux","line"]}"""));
        DuckingChange lists = Change("""{"channels":["music","game"],"mixes":["monitor","stream"]}""");
        Assert.Equal(["music", "game"], lists.Channels);
        Assert.Equal(["monitor", "stream"], lists.Mixes);
        Assert.Null(lists.Enabled);
        DuckingChange none = Change("""{"channels":[],"mixes":[]}""");
        Assert.Empty(none.Channels!);
        Assert.Empty(none.Mixes!);
        Assert.Equal(new DuckingChange(Sensitivity: "low", Amount: "high", Speed: "fast"),
            Change("""{"sensitivity":"low","amount":"high","speed":"fast"}"""));
        // Numbers, at the bounds and between, stay numbers: one equal to a preset is not turned into its word.
        Assert.Equal(new DuckingChange(Sensitivity: -60, Amount: 1, Speed: DuckingSpeed.Times(1, 10)),
            Change("""{"sensitivity":-60,"amount":1,"speed":{"attack":1,"release":10}}"""));
        Assert.Equal(new DuckingChange(Sensitivity: 0, Amount: 40, Speed: DuckingSpeed.Times(1000, 10000)),
            Change("""{"sensitivity":0,"amount":40.0,"speed":{"release":10000,"attack":1000}}"""));
        DuckingChange preset = Change("""{"sensitivity":-12,"amount":12,"speed":{"attack":80,"release":1200}}""");
        Assert.Equal((null, -12, null, 12, null, 80, 1200), (preset.Sensitivity!.Word, preset.Sensitivity.Db, preset.Amount!.Word,
            preset.Amount.Db, preset.Speed!.Word, preset.Speed.Attack, preset.Speed.Release));
        // The keys may be named with channels that leave them out.
        Assert.Equal(["xlr2"], Change("""{"key":"xlr2","channels":["xlr1","music"]}""").Keys);
        Assert.Equal(["xlr2", "voicechat"], Change("""{"keys":["xlr2","voicechat"],"channels":["xlr1","music"]}""").Keys);
        // The general check knows the command too.
        Assert.Null(CommandValidation.Check(Ducking("""{"enabled":false}"""), _layout, _ => null));
    }

    [Fact]
    public void AChangeKeepsWhatItDoesNotName()
    {
        DuckingSetup saved = DuckingSetup.Default(_layout.Config) with { Enabled = true, Sensitivity = "high", Processing = "software" };
        Assert.Equal(saved with { Enabled = false }, new DuckingChange(Enabled: false).ApplyTo(saved), DuckingSetupComparer.Instance);
        Assert.Equal(saved, new DuckingChange().ApplyTo(saved), DuckingSetupComparer.Instance);
        DuckingSetup moved = new DuckingChange(Channels: ["music"], Mixes: []).ApplyTo(saved);
        Assert.Equal(["music"], moved.Channels);
        Assert.Empty(moved.Mixes);
        Assert.Equal(("high", "software", true), (moved.Sensitivity, moved.Processing, moved.Enabled));
        DuckingSetup keyed = new DuckingChange(Keys: ["xlr2", "voicechat"]).ApplyTo(saved);
        Assert.Equal(["xlr2", "voicechat"], keyed.Keys);
        Assert.Equal(saved.Channels, keyed.Channels);
        DuckingSetup custom = new DuckingChange(Amount: 18, Speed: DuckingSpeed.Times(50, 600)).ApplyTo(saved);
        Assert.Equal((DuckingLevel.Preset("high"), DuckingLevel.Number(18), DuckingSpeed.Times(50, 600)),
            (custom.Sensitivity, custom.Amount, custom.Speed));
    }

    [Theory]
    [InlineData("true", "value must be an object")]
    [InlineData("[]", "value must be an object")]
    [InlineData("{}", "value needs at least one of enabled, keys, key, channels, mixes, sensitivity, amount and speed")]
    [InlineData("""{"enabled":"yes"}""", "enabled must be a boolean")]
    [InlineData("""{"enabled":null}""", "enabled must be a boolean")]
    [InlineData("""{"key":"xlr3"}""", "key must be a microphone, Aux In, Line In or application channel")]
    [InlineData("""{"key":""}""", "key must be a microphone, Aux In, Line In or application channel")]
    [InlineData("""{"key":1}""", "key must be a microphone, Aux In, Line In or application channel")]
    [InlineData("""{"keys":"xlr1"}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"keys":[]}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"keys":["xlr1","xlr1"]}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"keys":["xlr1","xlr3"]}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"keys":[null]}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"keys":["xlr1","xlr2","aux","line","game","music","browser","system","voicechat"]}""", "keys must be one to 8 distinct microphone, Aux In, Line In or application channels")]
    [InlineData("""{"key":"xlr1","keys":["xlr2"]}""", "give key or keys, not both")]
    [InlineData("""{"keys":["xlr2"],"key":"xlr1"}""", "give key or keys, not both")]
    [InlineData("""{"keys":["xlr1","music"],"channels":["music"]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"channels":"music"}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"channels":["music","music"]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"channels":["nothing"]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"channels":[1]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"channels":[""]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"key":"xlr1","channels":["xlr1"]}""", "channels must be distinct channel ids other than the keys")]
    [InlineData("""{"mixes":"monitor"}""", "mixes must be distinct mix ids")]
    [InlineData("""{"mixes":["monitor","monitor"]}""", "mixes must be distinct mix ids")]
    [InlineData("""{"mixes":["music"]}""", "mixes must be distinct mix ids")]
    [InlineData("""{"mixes":[null]}""", "mixes must be distinct mix ids")]
    [InlineData("""{"sensitivity":"loud"}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":12}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":1}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":-61}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":-12.5}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":"-12"}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":null}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"sensitivity":{"db":-12}}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"amount":"slow"}""", "amount must be low, medium, high or a whole number of dB from 1 to 40")]
    [InlineData("""{"amount":0}""", "amount must be low, medium, high or a whole number of dB from 1 to 40")]
    [InlineData("""{"amount":41}""", "amount must be low, medium, high or a whole number of dB from 1 to 40")]
    [InlineData("""{"amount":true}""", "amount must be low, medium, high or a whole number of dB from 1 to 40")]
    [InlineData("""{"speed":"high"}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":80}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":0,"release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":1001,"release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80,"release":9}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80,"release":10001}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80.5,"release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":"80","release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80,"release":1200,"hold":5}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"speed":{"attack":80,"attack":90,"release":1200}}""", "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000")]
    [InlineData("""{"sensitivity":"custom"}""", "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0")]
    [InlineData("""{"processing":"device"}""", "unknown field 'processing'")]
    [InlineData("""{"targets":["monitor"]}""", "unknown field 'targets'")]
    [InlineData("""{"enable":true}""", "unknown field 'enable'")]
    public void AMalformedSetDuckingIsRefusedBeforeTheMixer(string value, string error)
    {
        Command cmd = Ducking(value);
        Assert.Equal("setDucking: " + error, CommandValidation.CheckDucking(cmd, _layout, out DuckingChange? change));
        Assert.Null(change);
        Assert.Equal("setDucking: " + error, CommandValidation.Check(cmd, _layout, _ => null));
    }

    [Fact]
    public void AListLongerThanTheLayoutCanHoldIsRefused()
    {
        string many = "[" + string.Join(",", Enumerable.Range(0, CommandValidation.MaxDuckingIds + 1).Select(i => $"\"c{i}\"")) + "]";
        Assert.Equal("setDucking: channels must be distinct channel ids other than the keys",
            CommandValidation.CheckDucking(Ducking($$"""{"channels":{{many}}}"""), null, out _));
        Assert.Equal("setDucking: mixes must be distinct mix ids", CommandValidation.CheckDucking(Ducking($$"""{"mixes":{{many}}}"""), null, out _));
        // Without a layout only the shape is checked; the mixer checks the ids.
        Assert.Null(CommandValidation.CheckDucking(Ducking("""{"key":"anything","channels":["unknown"]}"""), null, out _));
    }

    [Theory]
    [InlineData("\"device\"", null)]
    [InlineData("\"software\"", null)]
    [InlineData("\"unit\"", "setDuckingProcessing: value must be 'device' or 'software'")]
    [InlineData("true", "setDuckingProcessing: value must be 'device' or 'software'")]
    [InlineData("null", "setDuckingProcessing: value must be 'device' or 'software'")]
    public void WhereDuckingRunsTakesTheDeviceOrSoftware(string value, string? error)
    {
        Command cmd = Ducking(value, "setDuckingProcessing");
        Assert.Equal(error, CommandValidation.CheckDuckingProcessing(cmd));
        Assert.Equal(error, CommandValidation.Check(cmd, _layout, _ => null));
    }

    /// <summary>Setups compared by value, lists included.</summary>
    private sealed class DuckingSetupComparer : IEqualityComparer<DuckingSetup>
    {
        public static readonly DuckingSetupComparer Instance = new();

        public bool Equals(DuckingSetup? x, DuckingSetup? y)
            => x is not null && y is not null && x.Channels.SequenceEqual(y.Channels) && x.Mixes.SequenceEqual(y.Mixes)
                && x.Keys.SequenceEqual(y.Keys)
                && (x.Enabled, x.Sensitivity, x.Amount, x.Speed, x.Processing) == (y.Enabled, y.Sensitivity, y.Amount, y.Speed, y.Processing);

        public int GetHashCode(DuckingSetup obj) => obj.Keys.Count;
    }
}
