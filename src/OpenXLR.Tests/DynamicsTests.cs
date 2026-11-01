using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone compressor and expander on this computer: their models,
/// validation, how they are saved and recalled, the stage they ask for, the
/// controls they write and the levels clients read. The audio itself is
/// checked by <c>make -C native test-dynamics</c>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DynamicsTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-dynamics-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json, bool host = true)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, host);

    [Fact]
    public void BothAreOffByDefaultWithTheDocumentedSettings()
    {
        MicCompressor c = MicCompressor.Default;
        Assert.False(c.Enabled);
        Assert.Equal((-18, 3, 5, 150, 6, 0), (c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Makeup));
        MicExpander e = MicExpander.Default;
        Assert.False(e.Enabled);
        Assert.Equal((-50, 3, 24, 1, 150, 50), (e.Threshold, e.Ratio, e.Range, e.Attack, e.Release, e.Hold));
        Assert.Null(MicCompressor.Check(c));
        Assert.Null(MicExpander.Check(e));
        // Each default sits inside its own range and on its own step.
        foreach (DynamicsRange range in new[]
        {
            MicCompressor.ThresholdRange, MicCompressor.RatioRange, MicCompressor.AttackRange, MicCompressor.ReleaseRange,
            MicCompressor.KneeRange, MicCompressor.MakeupRange, MicExpander.ThresholdRange, MicExpander.RatioRange,
            MicExpander.RangeRange, MicExpander.AttackRange, MicExpander.ReleaseRange, MicExpander.HoldRange,
        })
        {
            Assert.True(range.Contains(range.Default), range.Name);
            Assert.Equal(range.Default, range.Snap(range.Default));
        }
    }

    [Theory]
    [InlineData(-60, 1, 0.1, 10, 0, 0, null)]
    [InlineData(0, 20, 200, 2000, 24, 24, null)]
    [InlineData(-61, 3, 5, 150, 6, 0, "threshold must be -60 to 0 dB")]
    [InlineData(1, 3, 5, 150, 6, 0, "threshold must be -60 to 0 dB")]
    [InlineData(-18, 0.9, 5, 150, 6, 0, "ratio must be 1 to 20 to 1")]
    [InlineData(-18, double.NaN, 5, 150, 6, 0, "ratio must be 1 to 20 to 1")]
    [InlineData(-18, 3, 0.05, 150, 6, 0, "attack must be 0.1 to 200 ms")]
    [InlineData(-18, 3, 5, 2001, 6, 0, "release must be 10 to 2000 ms")]
    [InlineData(-18, 3, 5, 150, 25, 0, "knee must be 0 to 24 dB")]
    [InlineData(-18, 3, 5, 150, 6, double.PositiveInfinity, "makeup must be 0 to 24 dB")]
    public void ACompressorOutsideItsRangesIsRefusedNotClamped(double threshold, double ratio, double attack, double release,
        double knee, double makeup, string? error)
    {
        using var culture = new CultureScope("it-IT");
        Assert.Equal(error, MicCompressor.Check(new MicCompressor
        {
            Enabled = true, Threshold = threshold, Ratio = ratio, Attack = attack, Release = release, Knee = knee, Makeup = makeup,
        }));
    }

    [Theory]
    [InlineData(-80, 1, 0, 0.1, 10, 0, null)]
    [InlineData(0, 20, 80, 200, 2000, 500, null)]
    [InlineData(-81, 3, 24, 1, 150, 50, "threshold must be -80 to 0 dB")]
    [InlineData(-50, 21, 24, 1, 150, 50, "ratio must be 1 to 20 to 1")]
    [InlineData(-50, 3, 81, 1, 150, 50, "range must be 0 to 80 dB")]
    [InlineData(-50, 3, -1, 1, 150, 50, "range must be 0 to 80 dB")]
    [InlineData(-50, 3, 24, 0, 150, 50, "attack must be 0.1 to 200 ms")]
    [InlineData(-50, 3, 24, 1, 9, 50, "release must be 10 to 2000 ms")]
    [InlineData(-50, 3, 24, 1, 150, 501, "hold must be 0 to 500 ms")]
    public void AnExpanderOutsideItsRangesIsRefusedNotClamped(double threshold, double ratio, double range, double attack,
        double release, double hold, string? error)
    {
        using var culture = new CultureScope("it-IT");
        Assert.Equal(error, MicExpander.Check(new MicExpander
        {
            Enabled = true, Threshold = threshold, Ratio = ratio, Range = range, Attack = attack, Release = release, Hold = hold,
        }));
    }

    [Fact]
    public void ValuesSnapOntoTheirSteps()
    {
        MicCompressor c = MicCompressor.Snap(new MicCompressor
        {
            Threshold = -17.6, Ratio = 3.14, Attack = 4.26, Release = 149.6, Knee = 5.5, Makeup = 6.3,
        });
        Assert.Equal((-18, 3.1, 4.3, 150, 6, 6.5), (c.Threshold, c.Ratio, c.Attack, c.Release, c.Knee, c.Makeup));
        MicExpander e = MicExpander.Snap(new MicExpander { Threshold = -44.4, Ratio = 2.06, Range = 23.5, Hold = 49.5 });
        Assert.Equal((-44, 2.1, 24, 50), (e.Threshold, e.Ratio, e.Range, e.Hold));

        MicCompressor? read = MicCompressor.Read(JsonDocument.Parse(
            """{"enabled":true,"threshold":-12.2,"ratio":4,"attack":10,"release":100,"knee":0,"makeup":6,"available":true}""").RootElement,
            out string? error);
        Assert.Null(error);
        Assert.Equal(new MicCompressor { Enabled = true, Threshold = -12, Ratio = 4, Attack = 10, Release = 100, Knee = 0, Makeup = 6 }, read);
        Assert.Null(MicCompressor.Read(JsonDocument.Parse("""{"enabled":true,"threshold":-12}""").RootElement, out error));
        Assert.Equal("need 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'", error);
        Assert.Null(MicExpander.Read(JsonDocument.Parse(
            """{"enabled":true,"threshold":-90,"ratio":3,"range":24,"attack":1,"release":150,"hold":50}""").RootElement, out error));
        Assert.Equal("threshold must be -80 to 0 dB", error);
    }

    [Theory]
    [InlineData("""{"cmd":"setCompressor","channel":"xlr1","enabled":true,"threshold":-24}""", null)]
    [InlineData("""{"cmd":"setCompressor","channel":"guest","makeup":12}""", null)]
    [InlineData("""{"cmd":"setCompressor","channel":"xlr1"}""", "setCompressor: need at least one of 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'")]
    [InlineData("""{"cmd":"setCompressor","enabled":true}""", "setCompressor: need 'channel'")]
    [InlineData("""{"cmd":"setCompressor","channel":"aux","enabled":true}""", "setCompressor: 'aux' is not a microphone input")]
    [InlineData("""{"cmd":"setCompressor","channel":"xlr1","ratio":30}""", "setCompressor: ratio must be 1 to 20 to 1")]
    [InlineData("""{"cmd":"setCompressor","channel":"xlr1","range":10}""", "setCompressor: need at least one of 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'")]
    [InlineData("""{"cmd":"setExpander","channel":"xlr2","enabled":true,"range":40,"hold":100}""", null)]
    [InlineData("""{"cmd":"setExpander","channel":"xlr1","threshold":-80}""", null)]
    [InlineData("""{"cmd":"setExpander","channel":"xlr1","threshold":-81}""", "setExpander: threshold must be -80 to 0 dB")]
    [InlineData("""{"cmd":"setExpander","channel":"xlr1","knee":3}""", "setExpander: need at least one of 'enabled', 'threshold', 'ratio', 'range', 'attack', 'release' and 'hold'")]
    [InlineData("""{"cmd":"setExpander","channel":"system","enabled":false}""", "setExpander: 'system' is not a microphone input")]
    [InlineData("""{"cmd":"watchDynamics","channel":"guest"}""", null)]
    [InlineData("""{"cmd":"watchDynamics","channel":"aux"}""", "watchDynamics: need a microphone channel")]
    [InlineData("""{"cmd":"watchDynamics"}""", "watchDynamics: need a microphone channel")]
    public void TheCommandsAreCheckedBeforeTheMixer(string json, string? error) => Assert.Equal(error, Check(json));

    [Fact]
    public void SwitchingOnOrWatchingNeedsTheNativeHelper()
    {
        Assert.Equal(Mixer.DynamicsNeedsHost, Check("""{"cmd":"setCompressor","channel":"xlr1","enabled":true}""", host: false));
        Assert.Equal(Mixer.DynamicsNeedsHost, Check("""{"cmd":"setExpander","channel":"xlr1","enabled":true}""", host: false));
        Assert.Equal(Mixer.DynamicsNeedsHost, Check("""{"cmd":"watchDynamics","channel":"xlr1"}""", host: false));
        // Off, the settings are only stored.
        Assert.Null(Check("""{"cmd":"setCompressor","channel":"xlr1","enabled":false,"threshold":-30}""", host: false));
        Assert.Null(Check("""{"cmd":"setExpander","channel":"xlr1","ratio":2}""", host: false));
    }

    [Fact]
    public void TheMixerStoresSnappedValuesAndRefusesTheRest()
    {
        using var mixer = new Mixer();
        mixer.SetCompressor("xlr1", new(Enabled: true, Threshold: -24.4, Makeup: 5.8));
        Assert.Equal(MicCompressor.Default with { Enabled = true, Threshold = -24, Makeup = 6 }, mixer.CompressorFor("xlr1"));
        // One field at a time keeps the others.
        mixer.SetCompressor("xlr1", new(Enabled: false));
        Assert.Equal(-24, mixer.CompressorFor("xlr1").Threshold);
        Assert.Throws<ArgumentException>(() => mixer.SetCompressor("xlr1", new(Ratio: 0.5)));
        Assert.Throws<ArgumentException>(() => mixer.SetCompressor("xlr1", new()));
        Assert.Throws<ArgumentException>(() => mixer.SetCompressor("aux", new(Enabled: true)));
        mixer.SetExpander("xlr2", new(Enabled: true, Range: 40, Hold: 80));
        Assert.Equal(MicExpander.Default with { Enabled = true, Range = 40, Hold = 80 }, mixer.ExpanderFor("xlr2"));
        Assert.Throws<ArgumentException>(() => mixer.SetExpander("xlr2", new(Hold: 600)));
        Assert.Equal(MicExpander.Default, mixer.ExpanderFor("xlr1"));

        MixerSettings saved = mixer.ExportSettings();
        Assert.Equal(["xlr1"], saved.Compressor.Keys);
        Assert.Equal(["xlr2"], saved.Expander.Keys);
        MixerScene scene = mixer.ExportScene();
        Assert.Equal(["xlr1", "xlr2"], scene.Compressor!.Keys.Order());
        Assert.Equal(["xlr1", "xlr2"], scene.Expander!.Keys.Order());
        Assert.Equal(MicCompressor.Default, scene.Compressor["xlr2"]);
    }

    [Fact]
    public void EitherAloneAsksForTheMicrophoneStageAndWatchingDoesNot()
    {
        using var mixer = new Mixer();
        mixer.SetCompressor("xlr1", new(Threshold: -30));
        Assert.False(mixer.WantsMicrophoneStage("xlr1"), "A setting made while off asks for nothing.");
        mixer.SetCompressor("xlr1", new(Enabled: true));
        Assert.True(mixer.WantsMicrophoneStage("xlr1"));
        Assert.False(mixer.EqWantsSoftwareStage("xlr1"), "The EQ itself stays off.");
        mixer.SetCompressor("xlr1", new(Enabled: false));
        mixer.SetExpander("xlr1", new(Enabled: true));
        Assert.True(mixer.WantsMicrophoneStage("xlr1"));
        mixer.SetExpander("xlr1", new(Enabled: false));
        Assert.False(mixer.WantsMicrophoneStage("xlr1"));
        // Levels are asked for only of a mixer that runs.
        Assert.Throws<InvalidOperationException>(() => mixer.WatchDynamics("xlr1"));
        Assert.Throws<ArgumentException>(() => mixer.WatchDynamics("aux"));
        Assert.Empty(mixer.DynamicsMeterChannels);
    }

    [Fact]
    public void ProcessingOnXlr1TakesTheProsDirectPathOff()
    {
        using var mixer = new Mixer();
        Assert.True(mixer.SetHardwareMicMonitor(true));
        mixer.SetCompressor("xlr1", new(Enabled: true));
        Assert.False(mixer.SetHardwareMicMonitor(true));
        mixer.SetCompressor("xlr1", new(Enabled: false));
        mixer.SetExpander("xlr1", new(Enabled: true));
        Assert.False(mixer.SetHardwareMicMonitor(true));
        mixer.SetExpander("xlr1", new(Enabled: false));
        Assert.True(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void TheStateCarriesTheSettingAndWhyItCannotRun()
    {
        using var mixer = new Mixer();
        ((PipeWireAdapter)typeof(Mixer).GetField("_pw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!).EqHost
            = "/nonexistent/openxlr-lv2-host";
        mixer.SetCompressor("xlr1", new(Enabled: true, Ratio: 4));
        ChannelStatus xlr1 = mixer.Snapshot().Channels.Single(c => c.Id == "xlr1");
        Assert.Equal(new MicCompressorState(true, -18, 4, 5, 150, 6, 0) { Available = false, Error = Mixer.DynamicsNeedsHost }, xlr1.Compressor);
        Assert.Equal(new MicExpanderState(false, -50, 3, 24, 1, 150, 50) { Available = false }, xlr1.Expander);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "aux").Compressor);
        Assert.Null(mixer.Snapshot().Channels.Single(c => c.Id == "system").Expander);

        using JsonDocument json = JsonSerializer.SerializeToDocument(mixer.Snapshot(), Web);
        JsonElement channel = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "xlr1");
        JsonElement compressor = channel.GetProperty("compressor");
        Assert.True(compressor.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, compressor.GetProperty("ratio").GetDouble());
        Assert.Equal(0, compressor.GetProperty("makeup").GetDouble());
        Assert.False(compressor.GetProperty("available").GetBoolean());
        Assert.Equal(24, channel.GetProperty("expander").GetProperty("range").GetDouble());
        Assert.Equal(50, channel.GetProperty("expander").GetProperty("hold").GetDouble());
    }

    [Fact]
    public void ARecallChangesOnlyTheSettingsItNames()
    {
        MicCompressor on = MicCompressor.Default with { Enabled = true };
        var current = new Dictionary<string, MicCompressor> { ["xlr1"] = on };
        // A profile from before the compressor carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallCompressor(current, null));
        var next = Mixer.RecallCompressor(current, new Dictionary<string, MicCompressor> { ["xlr2"] = on with { Threshold = -20.4 } });
        Assert.Same(on, next["xlr1"]);
        Assert.Equal(-20, next["xlr2"].Threshold);
        // An unreadable entry never replaces a good one.
        next = Mixer.RecallCompressor(current, new Dictionary<string, MicCompressor> { ["xlr1"] = on with { Ratio = 0 } });
        Assert.Same(on, next["xlr1"]);

        MicExpander open = MicExpander.Default with { Enabled = true };
        var expanders = new Dictionary<string, MicExpander> { ["guest"] = open };
        Assert.Equal(expanders, Mixer.RecallExpander(expanders, null));
        Assert.Same(open, Mixer.RecallExpander(expanders, new Dictionary<string, MicExpander> { ["guest"] = open with { Hold = -1 } })["guest"]);
    }

    [Fact]
    public void TheSettingsFileKeepsEveryReadableSettingAndDropsTheRest()
    {
        string path = Path.Combine(_directory, "mixer.json");
        File.WriteAllText(path, """
            {
              "compressor": {
                "xlr1": {"enabled": true, "threshold": -20.3, "ratio": 4, "attack": 10, "release": 200, "knee": 3, "makeup": 4},
                "xlr2": {"enabled": true, "threshold": -70, "ratio": 4, "attack": 10, "release": 200, "knee": 3, "makeup": 4}
              },
              "expander": {
                "xlr1": {"enabled": true, "threshold": -45, "ratio": 2, "range": 30, "attack": 2, "release": 300, "hold": 80},
                "guest": {"enabled": true, "threshold": -45, "ratio": 2, "range": 99, "attack": 2, "release": 300, "hold": 80}
              }
            }
            """);
        MixerSettings? settings = MixerSettings.Load(path, out string? warning);
        Assert.NotNull(settings);
        Assert.Contains("compressor.xlr2: threshold must be -60 to 0 dB", warning);
        Assert.Contains("expander.guest: range must be 0 to 80 dB", warning);
        Assert.Equal(["xlr1"], settings!.Compressor.Keys);
        Assert.Equal(-20, settings.Compressor["xlr1"].Threshold);
        Assert.Equal(["xlr1"], settings.Expander.Keys);
        Assert.Null(settings.Save(path));
        MixerSettings again = MixerSettings.Load(path, out string? clean)!;
        Assert.Null(clean);
        Assert.Equal(settings.Compressor, again.Compressor);
        Assert.Equal(settings.Expander, again.Expander);
        // A file from before them has both off everywhere.
        File.WriteAllText(path, "{}");
        MixerSettings old = MixerSettings.Load(path, out _)!;
        Assert.Empty(old.Compressor);
        Assert.Empty(old.Expander);
    }

    [Fact]
    public void AProfileRoundTripsItsSettingsAndOneWithoutLeavesThemOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        MicCompressor compressor = MicCompressor.Default with { Enabled = true, Makeup = 6 };
        MicExpander expander = MicExpander.Default with { Enabled = true, Threshold = -44 };
        var scene = new MixerScene
        {
            Compressor = new() { ["xlr1"] = compressor },
            Expander = new() { ["xlr1"] = expander },
        };
        string text = JsonSerializer.Serialize(scene, json);
        Assert.Contains("\"makeup\":6", text);
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(text, json)!;
        Assert.Equal(compressor, back.Compressor!["xlr1"]);
        Assert.Equal(expander, back.Expander!["xlr1"]);
        SavedMixerValidation.Validate(back);
        MixerScene none = JsonSerializer.Deserialize<MixerScene>("{}", json)!;
        Assert.Null(none.Compressor);
        Assert.Null(none.Expander);
        MixerScene bad = back with { Expander = new() { ["xlr1"] = expander with { Range = 81 } } };
        Assert.Contains("expander.xlr1", Assert.Throws<JsonException>(() => SavedMixerValidation.Validate(bad)).Message);
    }

    [Fact]
    public void TheNativeControlsCarryEveryValueByItsSymbol()
    {
        Dictionary<string, double> compressor = SoftwareEqBackend.Controls(MicCompressor.Default with { Enabled = true, Makeup = 3 });
        // The switch last, so switching on starts on the setting asked for.
        Assert.Equal(["comp_threshold", "comp_ratio", "comp_attack", "comp_release", "comp_knee", "comp_makeup", "comp_enabled"], compressor.Keys);
        Assert.Equal(3, compressor["comp_makeup"]);
        Assert.Equal(1, compressor["comp_enabled"]);
        Dictionary<string, double> expander = SoftwareEqBackend.Controls(MicExpander.Default);
        Assert.Equal(["exp_threshold", "exp_ratio", "exp_range", "exp_attack", "exp_release", "exp_hold", "exp_enabled"], expander.Keys);
        Assert.Equal(0, expander["exp_enabled"]);
        // The stage starts without them: a helper from before them refuses a
        // control it does not declare.
        Assert.DoesNotContain(SoftwareEqBackend.Controls(MicEq.Default, MicNoise.Default).Keys,
            k => k.StartsWith("comp_", StringComparison.Ordinal) || k.StartsWith("exp_", StringComparison.Ordinal));
        string[] meters = ["comp_input", "comp_reduction", "comp_output", "exp_input", "exp_reduction", "exp_output"];
        Assert.All(meters, m => Assert.Contains(m, SoftwareEqBackend.Meters));
        // The C side declares the same symbols, with the same defaults the
        // daemon assumes the stage starts on.
        string native = Path.Combine(MicEqTests.RepoRoot, "native", "dynamics.c");
        if (!File.Exists(native)) return;
        string source = File.ReadAllText(native);
        foreach (string symbol in compressor.Keys.Concat(expander.Keys).Concat(meters).Append("comp_meters").Append("exp_meters"))
            Assert.Contains($"\"{symbol}\"", source);
        foreach ((string symbol, double value) in SoftwareEqBackend.Controls(MicCompressor.Default).Concat(SoftwareEqBackend.Controls(MicExpander.Default)))
            Assert.Matches($"\"{symbol}\", [^,]+, [^,]+, {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, false", source);
    }

    // A helper that says it is ready and writes each line it is given to a file.
    private const string FakeStage = """
        echo ready
        while IFS= read -r line; do
          printf '%s\n' "$line" >> "$0.lines"
        done
        """;

    private static string[] Lines(string file) => File.Exists(file) ? File.ReadAllLines(file) : [];

    [Fact]
    public void TheStageIsWrittenOnlyWhatChangedAndNothingWhileTheyStayOff()
    {
        string executable = ExecutableScript.Write(Path.Combine(_directory, "stage"), FakeStage);
        string lines = executable + ".body.lines";
        using var host = new NativePluginHost(new InsertDefinition { Id = "xlr1_1", Kind = "eq", Plugin = "4" },
            "OpenXLR_eq_test", 1, 48000, executable, [], meterSymbols: SoftwareEqBackend.Meters);
        var stage = new SoftwareEqBackend(new PipeWireAdapter(), new FilterHandle("eq", "eq", "eq", host.Process) { NativeHost = host },
            MicEq.Default, MicNoise.Default);

        // Never switched on: a helper without them is sent nothing.
        stage.ApplyCompressor(MicCompressor.Default with { Threshold = -30 });
        stage.ApplyExpander(MicExpander.Default);
        stage.ApplyCompressor(MicCompressor.Default with { Enabled = true, Threshold = -30, Makeup = 4.5 });
        stage.ApplyCompressor(MicCompressor.Default with { Enabled = true, Threshold = -30, Makeup = 4.5 });
        stage.ApplyExpander(MicExpander.Default with { Enabled = true, Hold = 80 });
        stage.ApplyCompressor(MicCompressor.Default with { Enabled = false, Threshold = -30, Makeup = 4.5 });
        stage.SetDynamicsMeters(true);
        stage.SetDynamicsMeters(true);
        stage.SetDynamicsMeters(false);
        string[] expected =
        [
            "set comp_threshold -30", "set comp_makeup 4.5", "set comp_enabled 1",
            "set exp_hold 80", "set exp_enabled 1",
            "set comp_enabled 0",
            "set comp_meters 1", "set exp_meters 1",
            "set comp_meters 0", "set exp_meters 0",
        ];
        Assert.True(SpinWait.SpinUntil(() => Lines(lines).Length >= expected.Length, TimeSpan.FromSeconds(10)), "every line arrived");
        Assert.Equal(expected, Lines(lines));
    }

    [Fact]
    public void TheLevelsGoOutInDecibelsWithinBounds()
    {
        var values = new Dictionary<string, double> { ["comp_input"] = -23.46, ["comp_reduction"] = 4.04, ["comp_output"] = 30 };
        Assert.Equal([-23.5, 4.0, 24], Mixer.DynamicsLevels(values, "comp_input", "comp_reduction", "comp_output"));
        // Before the stage reported anything: silence and no reduction.
        Assert.Equal([-120, 0, -120], Mixer.DynamicsLevels(new Dictionary<string, double>(), "exp_input", "exp_reduction", "exp_output"));
        values = new() { ["exp_input"] = double.NegativeInfinity, ["exp_reduction"] = -3, ["exp_output"] = -200 };
        Assert.Equal([-120, 0, -120], Mixer.DynamicsLevels(values, "exp_input", "exp_reduction", "exp_output"));
    }
}
