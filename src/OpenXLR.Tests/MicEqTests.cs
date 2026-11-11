using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ's model: Wave Link's defaults, snapping onto its steps,
/// validation, and how the EQ is saved and recalled.
/// </summary>
public sealed class MicEqTests
{
    [Fact]
    public void TheDefaultsAreWaveLinksBandTableWithTheEqOff()
    {
        MicEq eq = MicEq.Default;
        Assert.False(eq.Enabled);
        Assert.Equal(4, eq.Bands.Count);
        Assert.Equal(["lowCut", "peak", "peak", "highCut"], eq.Bands.Select(b => b.Type));
        Assert.Equal([36.0, 310, 2172, 15049], eq.Bands.Select(b => b.Frequency));
        Assert.Equal([true, true, true, false], eq.Bands.Select(b => b.Enabled));
        Assert.All(eq.Bands, b => Assert.Equal(0, b.Gain));
        // Q 0.707 stored as log10(Q) on Wave Link's grid: -1 + 53 x 0.016.
        Assert.All(eq.Bands, b => Assert.Equal(Math.Pow(10, -0.152), b.Q, 12));
        Assert.Equal(-0.152, MicEq.QLog10(MicEq.DefaultQ), 12);
        Assert.Equal(["lowCut", "lowShelf", "peak", "notch", "highShelf", "highCut"], MicEq.Types);
    }

    [Fact]
    public void ValuesSnapOntoWaveLinksStepsAndStayThere()
    {
        Assert.Equal(9.25, MicEq.SnapGain(9.3));
        Assert.Equal(-9.25, MicEq.SnapGain(-9.3));
        Assert.Equal(36, MicEq.SnapFrequency(36.4));
        Assert.Equal(37, MicEq.SnapFrequency(36.5));
        double q = MicEq.SnapQ(0.705);
        Assert.Equal(0.7047, q, 4);
        Assert.Equal(q, MicEq.SnapQ(q));
        // Every point of the grid, 0.1 to 10, comes back as itself.
        for (int k = 0; k <= 125; k++)
        {
            double grid = Math.Pow(10, -1 + 0.016 * k);
            double snapped = MicEq.SnapQ(grid);
            Assert.Equal(snapped, MicEq.SnapQ(snapped));
            Assert.Equal(grid, snapped, 12);
        }
        Assert.Equal(0.1, MicEq.SnapQ(0.1), 12);
        Assert.Equal(10, MicEq.SnapQ(10), 12);
        EqBand band = MicEq.Snap(new EqBand { Enabled = true, Type = "peak", Frequency = 1000.4, Gain = 3.1, Q = 2 });
        Assert.Equal(1000, band.Frequency);
        Assert.Equal(3, band.Gain);
        Assert.Equal(band, MicEq.Snap(band));
    }


    [Fact]
    public void ARangeErrorWritesItsLimitsTheSameWayInEveryLocale()
    {
        // The reason goes back to clients as protocol text; a daemon started
        // under an Italian, Spanish or German locale must not say "0,1".
        using var culture = new CultureScope("it-IT");
        Assert.Equal("q must be 0.1 to 10", MicEq.Check(new EqBand { Type = "peak", Frequency = 1000, Gain = 0, Q = 0.09 }));
        Assert.Equal("gain must be -20 to +20 dB", MicEq.Check(new EqBand { Type = "peak", Frequency = 1000, Gain = 20.5, Q = 1 }));
        Assert.Equal("frequency must be 20 to 20000 Hz", MicEq.Check(new EqBand { Type = "peak", Frequency = 10, Gain = 0, Q = 1 }));
    }
    [Theory]
    [InlineData("bell", 1000, 0, 1, "type must be one of")]
    [InlineData("peak", 19.9, 0, 1, "frequency must be 20 to 20000 Hz")]
    [InlineData("peak", 20001, 0, 1, "frequency")]
    [InlineData("peak", double.NaN, 0, 1, "frequency")]
    [InlineData("peak", 1000, 20.1, 1, "gain must be -20 to +20 dB")]
    [InlineData("peak", 1000, -20.5, 1, "gain")]
    [InlineData("peak", 1000, double.PositiveInfinity, 1, "gain")]
    [InlineData("peak", 1000, 0, 0.09, "q must be 0.1 to 10")]
    [InlineData("peak", 1000, 0, 10.5, "q")]
    [InlineData("peak", 1000, 0, 0, "q")]
    [InlineData("highCut", 20000, 12, 10, null)]
    [InlineData("lowCut", 20, -12, 0.1, null)]
    [InlineData("peak", 703, 20, 0.6, null)]
    [InlineData("lowShelf", 80, -20, 1, null)]
    [InlineData("highShelf", 8000, 15.25, 1, null)]
    public void ABandOutsideTheRangesIsRefusedNotClamped(string type, double frequency, double gain, double q, string? error)
    {
        string? problem = MicEq.Check(new EqBand { Type = type, Frequency = frequency, Gain = gain, Q = q });
        if (error is null) Assert.Null(problem);
        else Assert.Contains(error, problem);
    }

    [Fact]
    public void AnEqNeedsOneToSixteenBands()
    {
        Assert.Contains("need 'enabled' and 'bands'", MicEq.Check((MicEq?)null));
        Assert.Contains("1 to 16", MicEq.Check(MicEq.Default with { Bands = [] }));
        Assert.Contains("band 2: a band is missing", MicEq.Check(MicEq.Default with { Bands = [MicEq.Default.Bands[0], null!, MicEq.Default.Bands[2], MicEq.Default.Bands[3]] }));
        Assert.Null(MicEq.Check(MicEq.Default));
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Bands(string band2 = """{"enabled":true,"type":"peak","frequency":310.4,"gain":9.3,"q":0.705}""")
        => "[" + string.Join(",",
            """{"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.705}""",
            band2,
            """{"enabled":true,"type":"peak","frequency":2172,"gain":0,"q":0.705}""",
            """{"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.705}""") + "]";

    [Fact]
    public void ACommandsEqIsReadStrictlyAndSnapped()
    {
        MicEq? eq = MicEq.Read(JsonDocument.Parse($$"""{"enabled":true,"bands":{{Bands()}},"backend":"software","available":true}""").RootElement, out string? error);
        Assert.Null(error);
        Assert.True(eq!.Enabled);
        Assert.Equal(310, eq.Bands[1].Frequency);
        Assert.Equal(9.25, eq.Bands[1].Gain);
        Assert.Equal(MicEq.DefaultQ, eq.Bands[1].Q);

        foreach ((string json, string expected) in new[]
        {
            ("""{"bands":[]}""", "need 'eq' with 'enabled' and 'bands'"),
            ("""{"enabled":"yes","bands":[]}""", "need 'eq'"),
            ("""{"enabled":true,"bands":[]}""", "1 to 16 bands"),
            ($$"""{"enabled":true,"bands":{{Bands("""{"enabled":true,"type":"peak","frequency":310,"q":1}""")}}}""", "band 2: need 'enabled', 'type', 'frequency', 'gain' and 'q'"),
            ($$"""{"enabled":true,"bands":{{Bands("""{"enabled":true,"type":"peak","frequency":"310","gain":0,"q":1}""")}}}""", "band 2: need"),
            ($$"""{"enabled":true,"bands":{{Bands("""{"enabled":true,"type":"bandPass","frequency":310,"gain":0,"q":1}""")}}}""", "band 2: type must be one of"),
            ($$"""{"enabled":true,"bands":{{Bands("""{"enabled":true,"type":"peak","frequency":310,"gain":21,"q":1}""")}}}""", "band 2: gain must be"),
            ("null", "need 'eq'"),
        })
        {
            Assert.Null(MicEq.Read(JsonDocument.Parse(json).RootElement, out string? why));
            Assert.Contains(expected, why);
        }
    }

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2";
        public int OverrideCount => 0;
    }

    private static string? Check(string json, bool host = true)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, host);

    [Theory]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr1","value":true}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr2","value":false}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","channel":"aux","value":true}""", "setEqEnabled: 'aux' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"system","value":true}""", "is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr3","value":true}""", "is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqEnabled","value":true}""", "setEqEnabled: need 'channel' or 'mix'")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr1","value":1}""", "value must be a boolean")]
    [InlineData("""{"cmd":"setEqBand","channel":"aux","band":1,"gain":3}""", "setEqBand: 'aux' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":2,"gain":3}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":4,"enabled":true,"type":"notch","frequency":3000,"gain":0,"q":10}""", null)]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":0,"gain":3}""", "need 'band' from 1 to 16")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":17,"gain":3}""", "need 'band' from 1 to 16")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","gain":3}""", "need 'band'")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1}""", "need at least one of")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"type":"bandPass"}""", "type must be one of lowCut, lowShelf, peak, notch, highShelf, highCut")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"frequency":19}""", "frequency must be 20 to 20000 Hz")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"frequency":20000.5}""", "frequency")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"gain":-20.25}""", "gain must be -20 to +20 dB")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"q":0.05}""", "q must be 0.1 to 10")]
    [InlineData("""{"cmd":"setEqBand","channel":"xlr1","band":1,"q":11}""", "q must be")]
    [InlineData("""{"cmd":"setEq","channel":"xlr1"}""", "setEq: need 'eq' with 'enabled' and 'bands'")]
    [InlineData("""{"cmd":"setEq","channel":"aux","eq":{"enabled":false,"bands":[]}}""", "setEq: 'aux' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEq","channel":"xlr1","eq":{"enabled":false,"bands":[]}}""", "setEq: need 1 to 16 bands")]
    public void EqCommandsAreCheckedBeforeTheMixer(string json, string? error)
    {
        string? problem = Check(json);
        if (error is null) Assert.Null(problem);
        else Assert.Contains(error, problem);
    }

    [Fact]
    public void SwitchingTheEqOnNeedsTheNativeHelper()
    {
        Assert.Equal(Mixer.EqNeedsHost, Check("""{"cmd":"setEqEnabled","channel":"xlr1","value":true}""", host: false));
        Assert.Equal(Mixer.EqNeedsHost, Check($$$"""{"cmd":"setEq","channel":"xlr1","eq":{"enabled":true,"bands":{{{Bands()}}}}}""", host: false));
        // Values and the off switch still go through: they are only stored.
        Assert.Null(Check("""{"cmd":"setEqEnabled","channel":"xlr1","value":false}""", host: false));
        Assert.Null(Check($$$"""{"cmd":"setEq","channel":"xlr1","eq":{"enabled":false,"bands":{{{Bands()}}}}}""", host: false));
        Assert.Null(Check("""{"cmd":"setEqBand","channel":"xlr1","band":2,"gain":3}""", host: false));
    }

    [Fact]
    public void TheMixerStoresSnappedValuesAndRefusesOutsideTheRanges()
    {
        using var mixer = new Mixer();
        Assert.Equal(MicEq.Default, mixer.EqFor("xlr1"));
        mixer.SetEqBand("xlr1", 2, new(Gain: 9.3, Frequency: 310.4));
        mixer.SetEqBand("xlr1", 4, new(Enabled: true, Type: "notch"));
        MicEq eq = mixer.EqFor("xlr1");
        Assert.Equal(9.25, eq.Bands[1].Gain);
        Assert.Equal(310, eq.Bands[1].Frequency);
        Assert.Equal("notch", eq.Bands[3].Type);
        Assert.Equal(0, eq.Bands[3].Gain);
        Assert.Equal(MicEq.Default, mixer.EqFor("xlr2"));
        // A gainless type keeps its gain, so changing back restores it.
        mixer.SetEqBand("xlr1", 2, new(Type: "notch"));
        mixer.SetEqBand("xlr1", 2, new(Type: "peak"));
        Assert.Equal(9.25, mixer.EqFor("xlr1").Bands[1].Gain);
        Assert.Throws<ArgumentException>(() => mixer.SetEqBand("xlr1", 2, new(Gain: 20.25)));
        Assert.Throws<ArgumentException>(() => mixer.SetEqBand("xlr1", 5, new(Gain: 1)));
        Assert.Throws<ArgumentException>(() => mixer.SetEqBand("xlr1", 1, new()));
        Assert.Throws<ArgumentException>(() => mixer.SetEqEnabled("aux", true));
        Assert.Throws<ArgumentException>(() => mixer.SetEq("system", MicEq.Default));
        Assert.Equal(9.25, mixer.EqFor("xlr1").Bands[1].Gain);
        mixer.SetEqEnabled("xlr1", true);
        Assert.True(mixer.EqFor("xlr1").Enabled);
        Assert.True(mixer.ExportSettings().Eq["xlr1"].Enabled);
        Assert.False(mixer.ExportSettings().Eq.ContainsKey("xlr2"), "An EQ never set is not written: absent means the defaults.");
        // A profile names every microphone, so recalling it sets each one.
        MixerScene scene = mixer.ExportScene();
        Assert.Equal(["xlr1", "xlr2"], scene.Eq!.Keys.Order());
        Assert.Equal(MicEq.Default, scene.Eq["xlr2"]);
    }

    [Fact]
    public void ARecallChangesOnlyTheEqsItNamesAndKeepsOtherKeys()
    {
        MicEq on = MicEq.Default with { Enabled = true };
        MicEq capture = MicEq.Default.WithBand(2, MicEq.Default.Bands[1] with { Gain = 6 });
        var current = new Dictionary<string, MicEq> { ["xlr1"] = on, ["cap1"] = capture };
        // A profile from before the EQ carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallEq(current, null));
        Assert.Equal(current, Mixer.RecallEq(current, new Dictionary<string, MicEq>()));
        var next = Mixer.RecallEq(current, new Dictionary<string, MicEq> { ["xlr2"] = on with { Bands = [.. on.Bands.Select(b => b with { Gain = 0.3 })] } });
        Assert.Same(on, next["xlr1"]);
        Assert.Same(capture, next["cap1"]);
        Assert.Equal(0.25, next["xlr2"].Bands[0].Gain);
        // An unreadable entry never replaces a good one.
        next = Mixer.RecallEq(current, new Dictionary<string, MicEq> { ["xlr1"] = on with { Bands = [] } });
        Assert.Same(on, next["xlr1"]);
    }

    [Fact]
    public void TheSettingsFileKeepsEveryReadableEqAndDropsTheRest()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-eq-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, $$"""
                {
                  "eq": {
                    "xlr1": {"enabled": true, "bands": {{Bands()}}},
                    "cap1": {"enabled": false, "bands": {{Bands()}}},
                    "xlr2": {"enabled": true, "bands": []},
                    "bad": null
                  }
                }
                """);
            MixerSettings? settings = MixerSettings.Load(path, out string? warning);
            Assert.NotNull(settings);
            Assert.Contains("eq.xlr2: need 1 to 16 bands", warning);
            Assert.Contains("eq.bad", warning);
            Assert.Equal(["cap1", "xlr1"], settings!.Eq.Keys.Order());
            // Snapped on load.
            Assert.Equal(9.25, settings.Eq["xlr1"].Bands[1].Gain);
            Assert.Equal(310, settings.Eq["xlr1"].Bands[1].Frequency);
            // A key for a channel that is not a microphone survives a load and a save.
            Assert.Null(settings.Save(path));
            MixerSettings again = MixerSettings.Load(path, out string? clean)!;
            Assert.Null(clean);
            Assert.Equal(settings.Eq, again.Eq);
            Assert.True(again.Eq["xlr1"].Enabled);
            Assert.Contains("\"q\": 0.70469", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsItsEqAndOneWithoutLeavesItOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        MicEq eq = MicEq.Snap(MicEq.Default with { Enabled = true }).WithBand(3, new EqBand { Enabled = true, Type = "highShelf", Frequency = 6238, Gain = -9.25, Q = MicEq.SnapQ(3) });
        var scene = new MixerScene { Eq = new() { ["xlr1"] = eq } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene, json), json)!;
        Assert.Equal(eq, back.Eq!["xlr1"]);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.Eq);
        // A profile is applied whole, so one bad EQ refuses the file.
        MixerScene bad = back with { Eq = new() { ["xlr1"] = eq with { Bands = [] } } };
        Assert.Contains("eq.xlr1", Assert.Throws<JsonException>(() => SavedMixerValidation.Validate(bad)).Message);
    }

    [Fact]
    public void TheNativeControlsCarryEveryValueByItsSymbol()
    {
        MicEq eq = MicEq.Default with { Enabled = true };
        Dictionary<string, double> controls = SoftwareEqBackend.Controls(eq);
        Assert.Equal(21, controls.Count);
        Assert.Equal(1, controls["enabled"]);
        Assert.Equal(0, controls["band1_type"]);
        Assert.Equal(2, controls["band2_type"]);
        Assert.Equal(5, controls["band4_type"]);
        Assert.Equal(0, controls["band4_enabled"]);
        Assert.Equal(15049, controls["band4_frequency"]);
        Assert.Equal(-0.152, controls["band1_qlog10"], 12);
        // The C side declares the same symbols.
        string source = File.ReadAllText(Path.Combine(RepoRoot, "native", "eq.c"));
        foreach (string field in new[] { "enabled", "type", "frequency", "gain", "qlog10" })
            Assert.Contains($"\"{field}\"", source);
        Assert.Contains("\"band%d_%s\"", source);
        Assert.Equal(["eq"], NativePluginHost.Arguments(new() { Id = "x", Kind = "eq", Plugin = "eq" }, null));
    }

    internal static string RepoRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "eq.c")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        }
    }
}
