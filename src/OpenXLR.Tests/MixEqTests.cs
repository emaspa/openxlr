using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The output EQ on every mix: its commands with <c>mix</c> in place of
/// <c>channel</c>, how the mixer keeps and reports it, how it is saved and
/// recalled, the listening curves among the built-in profiles, and what
/// hardware mixing on the Wave XLR Pro does to it. The audio is checked on a
/// private PipeWire server by <see cref="DspAudioIntegrationTests"/>.
/// </summary>
public sealed class MixEqTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest";
        public bool HasMix(string id) => id is "monitor" or "monitor2" or "stream" or "auxout" or "discord";
        public bool IsMonitorMix(string id) => id is "monitor" or "monitor2";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json, bool host = true)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, host);

    private const string Bands = """[{"enabled":true,"type":"highShelf","frequency":4500,"gain":-4,"q":0.707}]""";

    [Theory]
    [InlineData("""{"cmd":"setEqEnabled","mix":"stream","value":true}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","mix":"discord","value":false}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","mix":"auxout","value":true}""", null)]
    [InlineData("""{"cmd":"setEqBand","mix":"monitor","band":1,"gain":-3}""", null)]
    [InlineData("""{"cmd":"setEq","mix":"monitor2","eq":{"enabled":true,"bands":""" + Bands + "}}", null)]
    [InlineData("""{"cmd":"loadEqProfile","mix":"stream","profile":"builtin:footsteps"}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","mix":"monitor","profile":"builtin:clear"}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"builtin:bass-boost"}""", null)]
    [InlineData("""{"cmd":"setEqEnabled","mix":"nope","value":true}""", "setEqEnabled: 'nope' is not a mix")]
    [InlineData("""{"cmd":"setEqEnabled","mix":"xlr1","value":true}""", "setEqEnabled: 'xlr1' is not a mix")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"stream","value":true}""", "setEqEnabled: 'stream' is not a microphone or capture input")]
    [InlineData("""{"cmd":"setEqEnabled","channel":"xlr1","mix":"stream","value":true}""", "setEqEnabled: give 'channel' or 'mix', not both")]
    [InlineData("""{"cmd":"setEqEnabled","mix":"stream","value":1}""", "setEqEnabled: value must be a boolean")]
    [InlineData("""{"cmd":"setEqBand","mix":"stream","band":0,"gain":3}""", "setEqBand: need 'band' from 1 to 16")]
    [InlineData("""{"cmd":"setEqBand","mix":"stream","band":1,"gain":13}""", "setEqBand: gain must be -12 to +12 dB")]
    [InlineData("""{"cmd":"setEq","mix":"stream","eq":{"enabled":true,"bands":[]}}""", "setEq: need 1 to 16 bands")]
    [InlineData("""{"cmd":"setEqProcessing","mix":"stream","value":"software"}""", "setEqProcessing: takes a microphone 'channel'; a mix's EQ always runs on this computer")]
    [InlineData("""{"cmd":"loadEqProfile","mix":"nope","profile":"builtin:clear"}""", "loadEqProfile: 'nope' is not a mix")]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1","mix":"stream","profile":"builtin:clear"}""", "loadEqProfile: give 'channel' or 'mix', not both")]
    public void TheCommandsTakeAMixInPlaceOfAChannel(string json, string? error) => Assert.Equal(error, Check(json));

    [Fact]
    public void SwitchingAMixEqOnNeedsTheNativeHelperAndSettingValuesDoesNot()
    {
        Assert.Equal(Mixer.MixEqNeedsHost, Check("""{"cmd":"setEqEnabled","mix":"stream","value":true}""", host: false));
        Assert.Equal(Mixer.MixEqNeedsHost, Check("""{"cmd":"setEq","mix":"stream","eq":{"enabled":true,"bands":""" + Bands + "}}", host: false));
        Assert.Null(Check("""{"cmd":"setEqEnabled","mix":"stream","value":false}""", host: false));
        Assert.Null(Check("""{"cmd":"setEqBand","mix":"stream","band":2,"gain":3}""", host: false));
        Assert.Null(Check("""{"cmd":"setEq","mix":"stream","eq":{"enabled":false,"bands":""" + Bands + "}}", host: false));
    }

    [Fact]
    public void EveryMixReportsItsEqOffByDefaultAndTheMixerKeepsWhatIsSet()
    {
        using var mixer = new Mixer();
        MixerState state = mixer.Snapshot();
        Assert.All(state.Mixes, m =>
        {
            Assert.NotNull(m.Eq);
            Assert.False(m.Eq!.Enabled);
            Assert.Equal(MicEq.Default.Bands, m.Eq.Bands);
            Assert.Equal(MicEqProcessing.Software, m.Eq.Processing);
            Assert.Null(m.Eq.Device);
            Assert.Null(m.Eq.Backend);
        });
        Assert.Empty(mixer.ExportSettings().MixEq);

        mixer.SetMixEq("stream", EqPresets.Footsteps.Eq);
        mixer.SetMixEqBand("stream", 1, new(Gain: -6.1));
        mixer.SetMixEqEnabled("monitor", true);
        Assert.Equal(-6, mixer.MixEqFor("stream").Bands[0].Gain);
        Assert.Equal(2, mixer.MixEqFor("stream").Bands.Count);
        Assert.True(mixer.MixEqFor("monitor").Enabled);
        state = mixer.Snapshot();
        Assert.True(state.Mixes.Single(m => m.Id == "stream").Eq!.Enabled);
        Assert.Equal(mixer.MixEqFor("stream").Bands, state.Mixes.Single(m => m.Id == "stream").Eq!.Bands);
        // The microphones' EQs are a table of their own.
        Assert.False(mixer.EqFor("xlr1").Enabled);
        Assert.Empty(mixer.ExportSettings().Eq);

        Assert.Throws<ArgumentException>(() => mixer.SetMixEq("xlr1", MicEq.Default));
        Assert.Throws<ArgumentException>(() => mixer.SetMixEqEnabled("nope", true));
        Assert.Throws<ArgumentException>(() => mixer.SetMixEqBand("stream", 3, new(Gain: 1)));
        Assert.Throws<ArgumentException>(() => mixer.SetMixEqBand("stream", 1, new(Gain: 30)));

        Assert.Equal(["monitor", "stream"], mixer.ExportSettings().MixEq.Keys.Order());
        // A profile names every mix, so a recall sets each of them.
        Dictionary<string, MicEq> scene = mixer.ExportScene().MixEq!;
        Assert.Equal(MixerConfig.Default().Mixes.Select(m => m.Id).Order(), scene.Keys.Order());
        Assert.Equal(MicEq.Default, scene["chat"]);
    }

    [Fact]
    public void RecalledEqsTakeOnlyTheMixesOfThisLayoutAndAnOlderProfileChangesNothing()
    {
        using var mixer = new Mixer();
        mixer.SetMixEq("chat", EqPresets.SoftTreble.Eq);
        var recall = typeof(Mixer).GetMethod("RecallMixEqLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // A profile saved before the output EQ carries none: nothing changes.
        recall.Invoke(mixer, [null]);
        Assert.Equal(EqPresets.SoftTreble.Eq, mixer.MixEqFor("chat"));
        recall.Invoke(mixer, [new Dictionary<string, MicEq> { ["stream"] = EqPresets.BassBoost.Eq, ["xlr1"] = EqPresets.Warm.Eq, ["gone"] = EqPresets.Warm.Eq }]);
        Assert.Equal(EqPresets.BassBoost.Eq, mixer.MixEqFor("stream"));
        Assert.Equal(EqPresets.SoftTreble.Eq, mixer.MixEqFor("chat"));
        Assert.Equal(["chat", "stream"], mixer.ExportSettings().MixEq.Keys.Order());
    }

    [Fact]
    public void TheSettingsFileRoundTripsTheEqsAndAnOlderFileStillLoads()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-mix-eq-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """{ "mixEq": { "stream": { "enabled": true, "bands": """ + Bands + " } } }");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.True(settings.MixEq["stream"].Enabled);
            Assert.Equal(EqPresets.SoftTreble.Eq.Bands, settings.MixEq["stream"].Bands);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.MixEq, MixerSettings.Load(path, out _)!.MixEq);
            Assert.Contains("\"mixEq\"", File.ReadAllText(path));
            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out _)!.MixEq);
            // An entry that cannot be read is dropped and named; the rest load.
            File.WriteAllText(path, """{ "mixEq": { "stream": { "enabled": true, "bands": [] }, "chat": { "enabled": false, "bands": """ + Bands + " } } }");
            MixerSettings edited = MixerSettings.Load(path, out string? note)!;
            Assert.Equal(["chat"], edited.MixEq.Keys);
            Assert.Contains("mixEq.stream", note);
            File.WriteAllText(path, """{ "mixEq": null }""");
            Assert.Empty(MixerSettings.Load(path, out note)!.MixEq);
            Assert.Contains("mixEq", note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsTheEqsAndAnOlderOneLeavesThemOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var scene = new MixerScene { MixEq = new() { ["stream"] = EqPresets.VoicesForward.Eq, ["monitor"] = MicEq.Default } };
        string text = JsonSerializer.Serialize(scene, json);
        Assert.Contains("\"mixEq\"", text);
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(text, json)!;
        Assert.Equal(scene.MixEq, back.MixEq);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.MixEq);
        var broken = new MixerScene { MixEq = new() { ["stream"] = new MicEq { Bands = [] } } };
        JsonException refused = Assert.Throws<JsonException>(() => SavedMixerValidation.Validate(broken));
        Assert.Contains("mixEq.stream", refused.Message);
    }

    [Fact]
    public void ALoadOnAMixBecomesOneSetEqOnThatMix()
    {
        var load = new Command { Cmd = "loadEqProfile", Mix = "stream", Profile = "builtin:footsteps" };
        Command setEq = MixerService.ProfileEq(load, EqLimits.Instance, out string? error)!;
        Assert.Null(error);
        Assert.Equal("setEq", setEq.Cmd);
        Assert.Equal("stream", setEq.Mix);
        Assert.Null(setEq.Channel);
        Assert.Equal(EqPresets.Footsteps.Eq, MicEq.Read(setEq.Eq, out _));
        Assert.Null(Check(JsonSerializer.Serialize(new { cmd = setEq.Cmd, mix = setEq.Mix, eq = setEq.Eq })));
        // A voice curve loads on a mix too, and a listening curve on a microphone.
        Assert.Equal(EqPresets.Clear.Eq, MicEq.Read(MixerService.ProfileEq(load with { Profile = "builtin:clear" }, EqLimits.Instance, out _)!.Eq, out _));
        Command onMic = MixerService.ProfileEq(load with { Mix = null, Channel = "xlr1", Profile = "builtin:bass-boost" }, EqLimits.Instance, out _)!;
        Assert.Equal("xlr1", onMic.Channel);
        Assert.Null(onMic.Mix);
        Assert.Null(MixerService.ProfileEq(load, new EqLimits(), out error));
        Assert.Equal("loadEqProfile: Footsteps has 2 bands, and this mix's EQ takes 4 to 4", error);
    }

    [Fact]
    public void TheListeningCurvesAreBuiltInProfilesBesideTheVoiceCurves()
    {
        Assert.Equal(["voices-forward", "footsteps", "bass-boost", "soft-treble"], EqPresets.Listening.Select(p => p.Key));
        Assert.Equal(["Voices forward", "Footsteps", "Bass boost", "Soft treble"], EqPresets.Listening.Select(p => p.Name));
        // One list for every EQ: the voice curves first, then the listening curves.
        Assert.Equal([.. EqPresets.Voices, .. EqPresets.Listening], EqPresets.All);
        Assert.Equal(EqPresets.All.Select(p => p.Id), EqPresets.All.Select(p => p.Id).Distinct());
        Assert.Equal(EqPresets.All.Select(p => p.Name.ToLowerInvariant()), EqPresets.All.Select(p => p.Name.ToLowerInvariant()).Distinct());
        // A Voice Boost preset was dropped on purpose; no curve takes its name back.
        Assert.DoesNotContain(EqPresets.All, p => p.Name.Contains("boost", StringComparison.OrdinalIgnoreCase) && p.Key != "bass-boost");
        Assert.All(EqPresets.Listening, p => Assert.Equal(p, EqPresets.FindId("builtin:" + p.Key)));

        static EqBand Shelf(string type, double hz, double db) => new() { Enabled = true, Type = type, Frequency = hz, Gain = db, Q = MicEq.DefaultQ };
        Assert.Equal([Shelf(MicEq.LowShelf, 380, -4), Shelf(MicEq.HighShelf, 6200, -2)], EqPresets.VoicesForward.Eq.Bands);
        Assert.Equal([Shelf(MicEq.LowShelf, 240, -6), Shelf(MicEq.HighShelf, 7200, -2)], EqPresets.Footsteps.Eq.Bands);
        Assert.Equal([Shelf(MicEq.HighShelf, 170, -5)], EqPresets.BassBoost.Eq.Bands);
        Assert.Equal([Shelf(MicEq.HighShelf, 4500, -4)], EqPresets.SoftTreble.Eq.Bands);
        Assert.All(EqPresets.Listening, p =>
        {
            Assert.True(p.Eq.Enabled);
            Assert.Null(MicEq.Check(p.Eq));
            Assert.Equal(p.Eq, MicEq.Snap(p.Eq));
            // Every listening curve cuts; none lifts the mix.
            Assert.All(p.Eq.Bands, b => Assert.True(b.Gain < 0));
        });
        // Mic setup offers the voice curves only.
        Assert.Equal(EqPresets.Voices.Select(p => p.Key), OpenXLR.UI.MicSetupPresets.All.Select(p => p.Id));
    }

    /// <summary>The manual's table of listening curves is the presets as the code holds them.</summary>
    [Fact]
    public void TheManualListsTheListeningCurvesAsTheyAre()
    {
        string manual = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "docs", "manual.md"));
        foreach (EqPreset preset in EqPresets.Listening)
        {
            string bands = string.Join(" | ", preset.Eq.Bands.Select(b =>
            {
                var c = System.Globalization.CultureInfo.InvariantCulture;
                string hz = b.Frequency >= 1000 ? (b.Frequency / 1000).ToString("0.#", c) + " kHz" : b.Frequency.ToString("0", c) + " Hz";
                string kind = b.Type == MicEq.LowShelf ? "low shelf" : "high shelf";
                return $"{kind} {hz} {b.Gain.ToString("+0.##;-0.##;0", c)} dB";
            }));
            Assert.Contains($"| {preset.Name} | {bands} |", manual);
        }
    }

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    /// <summary>The default layout on a ready Pro, Headphones 1 on Monitor A, the USB Aux port on or off.</summary>
    private static HardwareMixingSetup ProSetup(bool auxPort) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = MixerConfig.Default().Channels,
        Mixes = MixerConfig.Default().Mixes,
        LevelsDb = new Dictionary<string, double> { ["xlr1|monitor"] = -3, ["game|monitor"] = -10 },
        MixVolumesDb = new Dictionary<string, double> { ["monitor"] = 0, ["monitor2"] = 0, ["auxout"] = 0 },
        Jacks = [("hp1", "monitor")],
        AuxPort = auxPort,
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
    };

    [Fact]
    public void HardwareMixingTurnsOffTheEqOfTheMixesTheUnitBuildsAndNoOther()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(ProSetup(auxPort: true));
        Assert.True(plan.Active);
        // The unit builds the jacks' feeds and, with the USB Aux port on, the Aux mix.
        Assert.Equal(["eq:mix:auxout", "eq:mix:monitor"], plan.Disabled.Keys.Where(k => k.StartsWith("eq:", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal("The Wave XLR Pro builds Monitor A for its jacks, so its EQ is off while hardware mixing runs. It comes back with software mixing.",
            plan.Disabled[Mixer.MixEqDisabledKey("monitor")]);
        Assert.Contains("its USB Aux port", plan.Disabled["eq:mix:auxout"]);
        Assert.All(plan.Disabled.Values, sentence => Assert.DoesNotContain("—", sentence));
        HardwareMixingPlan noAux = HardwareMixingPlanner.Plan(ProSetup(auxPort: false));
        Assert.Equal(["eq:mix:monitor"], noAux.Disabled.Keys.Where(k => k.StartsWith("eq:", StringComparison.Ordinal)));
        // A summed jack feed holds every part off; a mix that only streams keeps its EQ.
        HardwareMixingPlan summed = HardwareMixingPlanner.Plan(ProSetup(auxPort: false) with { Jacks = [("hp1", "monitor+monitor2")] });
        Assert.Contains("eq:mix:monitor2", summed.Disabled.Keys);
        Assert.DoesNotContain("eq:mix:stream", summed.Disabled.Keys);
    }

    [Fact]
    public void WhileTheUnitBuildsAMixItsEqIsHeldOffAndRefusedAndComesBackAsSaved()
    {
        using var mixer = new Mixer();
        mixer.SetMixEq("monitor", EqPresets.SoftTreble.Eq);
        mixer.SetMixEq("stream", EqPresets.Footsteps.Eq);
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(ProSetup(auxPort: false));
        mixer.NoteHardwarePlan(plan);
        // A plan alone, with the unit not in the mode, holds nothing.
        Assert.Null(mixer.MixEqHeldByUnit("monitor"));
        mixer.BeginHardwareMixing(new HardwareEntry());
        string sentence = plan.Disabled["eq:mix:monitor"];
        Assert.Equal(sentence, mixer.MixEqHeldByUnit("monitor"));
        Assert.Null(mixer.MixEqHeldByUnit("stream"));
        Assert.Equal($"setEqEnabled: {sentence}", Assert.Throws<InvalidOperationException>(() => mixer.SetMixEqEnabled("monitor", false)).Message);
        Assert.Equal($"setEqBand: {sentence}", Assert.Throws<InvalidOperationException>(() => mixer.SetMixEqBand("monitor", 1, new(Gain: -2))).Message);
        Assert.Equal($"setEq: {sentence}", Assert.Throws<InvalidOperationException>(() => mixer.SetMixEq("monitor", MicEq.Default)).Message);
        // The stream mix is built here as before and takes changes.
        mixer.SetMixEqBand("stream", 1, new(Gain: -3));
        Assert.Equal(-3, mixer.MixEqFor("stream").Bands[0].Gain);
        // The saved EQ is kept, in the state and the settings, for software mixing.
        Assert.Equal(EqPresets.SoftTreble.Eq, mixer.MixEqFor("monitor"));
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Eq!.Enabled);
        Assert.Equal(EqPresets.SoftTreble.Eq, mixer.ExportSettings().MixEq["monitor"]);

        mixer.LeaveHardwareMixing();
        Assert.Null(mixer.MixEqHeldByUnit("monitor"));
        mixer.SetMixEqBand("monitor", 1, new(Gain: -2));
        Assert.Equal(-2, mixer.MixEqFor("monitor").Bands[0].Gain);
    }
}
