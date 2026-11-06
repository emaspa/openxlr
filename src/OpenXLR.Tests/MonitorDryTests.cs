using System.Text.Json;
using System.Text.RegularExpressions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A monitor mix that hears the microphones dry: the switch, its command, how
/// it is saved and recalled, the pattern its routes follow, and what it does
/// to the Wave XLR Pro's direct path and to hardware mixing. The audio is
/// checked on a private PipeWire server by <see cref="MonitorVolumeIntegrationTests"/>.
/// </summary>
public sealed class MonitorDryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "system" or "guest";
        public bool HasMix(string id) => id is "monitor" or "monitor2" or "stream";
        public bool IsMonitorMix(string id) => id is "monitor" or "monitor2";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, false);

    [Theory]
    [InlineData("""{"cmd":"setMonitorDry","mix":"monitor","value":true}""", null)]
    [InlineData("""{"cmd":"setMonitorDry","mix":"monitor2","value":false}""", null)]
    [InlineData("""{"cmd":"setMonitorDry","value":true}""", "setMonitorDry: need 'mix'")]
    [InlineData("""{"cmd":"setMonitorDry","channel":"xlr1","value":true}""", "setMonitorDry: need 'mix'")]
    [InlineData("""{"cmd":"setMonitorDry","mix":"stream","value":true}""", "setMonitorDry: 'stream' is not a monitor mix")]
    [InlineData("""{"cmd":"setMonitorDry","mix":"nope","value":true}""", "setMonitorDry: 'nope' is not a monitor mix")]
    [InlineData("""{"cmd":"setMonitorDry","mix":"monitor"}""", "setMonitorDry: value must be a boolean")]
    [InlineData("""{"cmd":"setMonitorDry","mix":"monitor","value":1}""", "setMonitorDry: value must be a boolean")]
    public void TheCommandIsCheckedBeforeTheMixerAndNeedsNoNativeHelper(string json, string? error)
        => Assert.Equal(error, Check(json));

    [Fact]
    public void TheSwitchIsOffByDefaultOnEveryMonitorMixAndAbsentElsewhere()
    {
        using var mixer = new Mixer();
        MixerState state = mixer.Snapshot();
        Assert.False(state.Mixes.Single(m => m.Id == "monitor").MonitorDry);
        Assert.False(state.Mixes.Single(m => m.Id == "monitor2").MonitorDry);
        Assert.All(state.Mixes.Where(m => m.Kind != "monitor"), m => Assert.Null(m.MonitorDry));
        Assert.Empty(mixer.ExportSettings().MonitorDry);
        Assert.True(mixer.IsMonitorMix("monitor"));
        Assert.False(mixer.IsMonitorMix("stream"));
    }

    [Fact]
    public void TheMixerStoresTheSwitchAndRefusesAMixThatIsNoMonitor()
    {
        using var mixer = new Mixer();
        mixer.SetMonitorDry("monitor", true);
        mixer.SetMonitorDry("monitor", true);
        Assert.True(mixer.MonitorDry("monitor"));
        Assert.False(mixer.MonitorDry("monitor2"));
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").MonitorDry);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").MonitorDry);
        Assert.Throws<ArgumentException>(() => mixer.SetMonitorDry("stream", true));
        Assert.Throws<ArgumentException>(() => mixer.SetMonitorDry("xlr1", true));

        Assert.Equal(new Dictionary<string, bool> { ["monitor"] = true }, mixer.ExportSettings().MonitorDry);
        // A profile names every monitor mix, so a recall sets each of them.
        Assert.Equal(new Dictionary<string, bool> { ["monitor"] = true, ["monitor2"] = false }, mixer.ExportScene().MonitorDry);

        mixer.SetMonitorDry("monitor", false);
        Assert.False(mixer.MonitorDry("monitor"));
        Assert.Empty(mixer.ExportSettings().MonitorDry);
    }

    [Fact]
    public void ARecallSetsTheMixesItNamesAndLeavesTheOthers()
    {
        var current = new HashSet<string> { "monitor", "phones" };
        // A profile from before the switch carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallMonitorDry(current, null));
        Assert.Equal(current, Mixer.RecallMonitorDry(current, new Dictionary<string, bool>()));
        HashSet<string> next = Mixer.RecallMonitorDry(current, new Dictionary<string, bool> { ["monitor"] = false, ["monitor2"] = true });
        Assert.Equal(["monitor2", "phones"], next.Order());
        Assert.Equal(["monitor", "phones"], current.Order());
    }

    [Fact]
    public void RecalledSettingsTakeOnlyTheMonitorMixesOfThisLayout()
    {
        using var mixer = new Mixer();
        // An id that names no monitor mix of this layout is ignored rather
        // than refused.
        // ApplySettings and ApplyScene reach the recall only on a built mixer.
        typeof(Mixer).GetMethod("RecallMonitorDryLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(mixer, [new Dictionary<string, bool> { ["xlr1"] = true, ["stream"] = true, ["monitor2"] = true }]);
        Assert.Equal(new Dictionary<string, bool> { ["monitor2"] = true }, mixer.ExportSettings().MonitorDry);
    }

    [Fact]
    public void TheSettingsFileRoundTripsTheSwitchAndAnOlderFileStillLoads()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-dry-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """{ "dryMonitors": { "monitor": true, "monitor2": false } }""");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.True(settings.MonitorDry["monitor"]);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.MonitorDry, MixerSettings.Load(path, out _)!.MonitorDry);
            Assert.Contains("\"dryMonitors\"", File.ReadAllText(path));
            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out _)!.MonitorDry);
            // The per-microphone map an earlier build wrote loads, unread.
            File.WriteAllText(path, """{ "monitorDry": { "xlr1": true }, "outputVolume": 0.45 }""");
            MixerSettings older = MixerSettings.Load(path, out string? olderWarning)!;
            Assert.Null(olderWarning);
            Assert.Empty(older.MonitorDry);
            // A hand edit that leaves null reads as none on, and says so.
            File.WriteAllText(path, """{ "dryMonitors": null }""");
            MixerSettings edited = MixerSettings.Load(path, out string? note)!;
            Assert.Empty(edited.MonitorDry);
            Assert.Contains("dryMonitors", note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsTheSwitchAndAnOlderOneLeavesItOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var scene = new MixerScene { MonitorDry = new() { ["monitor"] = true, ["monitor2"] = false } };
        string text = JsonSerializer.Serialize(scene, json);
        Assert.Contains("\"dryMonitors\"", text);
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(text, json)!;
        Assert.Equal(scene.MonitorDry, back.MonitorDry);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.MonitorDry);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("""{ "monitorDry": { "xlr1": true } }""", json)!.MonitorDry);
    }

    [Fact]
    public void TheRouteFollowsTheMonitorMixesByTheirExactNamesOnly()
    {
        MixerConfig layout = MixerConfig.Default();
        string pattern = Mixer.DryPattern(layout.Mixes);
        Assert.Equal("~^(OpenXLR_mix_monitor|OpenXLR_mix_monitor2)$", pattern);
        // PipeWire reads the pattern as a POSIX extended expression; this one
        // means the same in .NET's syntax.
        var regex = new Regex(pattern[1..]);
        Assert.Matches(regex, "OpenXLR_mix_monitor");
        Assert.Matches(regex, "OpenXLR_mix_monitor2");
        Assert.DoesNotMatch(regex, "OpenXLR_mix_stream");
        Assert.DoesNotMatch(regex, "OpenXLR_mix_chat");
        Assert.DoesNotMatch(regex, "OpenXLR_mix_auxout");
        Assert.DoesNotMatch(regex, "OpenXLR_mix_monitor.duck");
        Assert.DoesNotMatch(regex, "OpenXLR_mix_monitor22");
        Assert.DoesNotMatch(regex, "OpenXLR_post_monitor");
        // A user monitor mix joins; a virtual microphone never does.
        MixDefinition[] mixes = [.. layout.Mixes, new("headphones", "Headphones", MixKind.Monitor), new("discord", "Discord", MixKind.VirtualMic)];
        Assert.Equal("~^(OpenXLR_mix_monitor|OpenXLR_mix_monitor2|OpenXLR_mix_headphones)$", Mixer.DryPattern(mixes));
        // Anything outside letters, digits, '_' and '-' is matched literally.
        Assert.Equal("~^(OpenXLR_mix_a[.]b)$", Mixer.DryPattern([new MixDefinition("a.b", "A", MixKind.Monitor)]));
    }

    public static TheoryData<string> Processing => ["voice", "noise", "eq", "lowcut"];

    [Theory]
    [MemberData(nameof(Processing))]
    public void TheProsDirectPathStaysOnWithEffectsWhenEveryMonitorTheJacksPlayIsDry(string kind)
    {
        using var mixer = new Mixer();
        Set(mixer, kind, on: true);
        Assert.False(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor"));
        mixer.SetMonitorDry("monitor", true);
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor"));
        // Jacks that also play Monitor B, which is not dry, keep the processed send.
        Assert.False(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor+monitor2"));
        mixer.SetMonitorDry("monitor2", true);
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor+monitor2"));
        // With no feed named, every monitor mix has to be dry.
        Assert.True(mixer.SetHardwareMicMonitor(true));
        mixer.SetMonitorDry("monitor", false);
        Assert.False(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void XlrTwosDirectPathFollowsTheSameSwitch()
    {
        using var mixer = new Mixer();
        mixer.SetInputJacks(2, auxInput: true);
        mixer.SetVoiceChanger("xlr2", new(Enabled: true));
        Assert.False(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor", input: 2));
        mixer.SetMonitorDry("monitor", true);
        Assert.True(mixer.SetHardwareMicMonitor(true, whenHeardIn: "monitor", input: 2));
    }

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    /// <summary>The default layout on a ready Pro, Headphones 1 on Monitor A, XLR 1 processed, Monitor A dry.</summary>
    private static HardwareMixingSetup ProSetup(bool auxPort) => new()
    {
        Wanted = true,
        MixerRunning = true,
        Unit = Pro,
        SinkPorts = 17,
        SourcePorts = 18,
        Channels = MixerConfig.Default().Channels,
        Mixes = MixerConfig.Default().Mixes,
        LevelsDb = new Dictionary<string, double> { ["xlr1|monitor"] = -3, ["xlr1|auxout"] = -6, ["game|monitor"] = -10 },
        MixVolumesDb = new Dictionary<string, double> { ["monitor"] = 0, ["monitor2"] = 0, ["auxout"] = 0 },
        Jacks = [("hp1", "monitor")],
        AuxPort = auxPort,
        UnitSources = new Dictionary<int, string> { [0] = "xlr1", [1] = "xlr2", [2] = "aux", [3] = "line" },
        Processed = new HashSet<string> { "xlr1" },
        MonitorDry = new HashSet<string> { "xlr1", "xlr2" },
        DryMixes = new HashSet<string> { "monitor" },
    };

    private static HardwareSectionContent Section(HardwareMixingPlan plan, int section) => plan.Content.Single(c => c.Section == section);

    private static bool Member(HardwareMixingPlan plan, int section, int source) => (Section(plan, section).Members & 1 << source) != 0;

    [Fact]
    public void InHardwareMixingTheJacksTakeADryMicrophoneFromTheUnitsInputAndTheAuxMixFromItsReturnPair()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(ProSetup(auxPort: true));
        Assert.True(plan.Active);
        Assert.Equal(new Dictionary<string, int> { ["xlr1"] = 7 }, plan.ReturnPairs);
        int back = ProMixSections.SourceOfPair(7);
        // The jack's section: XLR 1 raw at its Monitor A level, its return pair out.
        Assert.True(Member(plan, 0, ProMixSections.Xlr1));
        Assert.Equal(ProMixSections.CellOf(-3), Section(plan, 0).Cells[ProMixSections.Xlr1]);
        Assert.False(Member(plan, 0, back));
        Assert.Equal(ProMixSections.CellOff, Section(plan, 0).Cells[back]);
        // The Aux mix keeps the processed voice, and never the raw one beside it.
        Assert.True(Member(plan, HardwareMixingPlanner.AuxSection, back));
        Assert.Equal(ProMixSections.CellOf(-6), Section(plan, HardwareMixingPlanner.AuxSection).Cells[back]);
        Assert.False(Member(plan, HardwareMixingPlanner.AuxSection, ProMixSections.Xlr1));
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
        // XLR 1 now plays raw on the jack, as a unit input heard through a pair
        // before the mode does, so the jack is lowered for it on entry.
        Assert.Equal(-HardwareMixingPlanner.Section0LevelDb + HardwareMixingPlanner.PairLossDb, plan.EntryOffsetsDb["hp1"], 6);
        HardwareMixingPlan processed = HardwareMixingPlanner.Plan(ProSetup(auxPort: true) with { DryMixes = new HashSet<string>() });
        Assert.Equal(-HardwareMixingPlanner.Section0LevelDb, processed.EntryOffsetsDb["hp1"], 6);
        Assert.True(Member(processed, 0, back));
        Assert.False(Member(processed, 0, ProMixSections.Xlr1));
    }

    [Fact]
    public void WithoutTheAuxMixADryMicrophoneClaimsNoReturnPair()
    {
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(ProSetup(auxPort: false));
        Assert.Empty(plan.ReturnPairs);
        // The pair it would have taken is the cue's.
        Assert.Equal(7, plan.CuePair);
        Assert.True(Member(plan, 0, ProMixSections.Xlr1));
        Assert.DoesNotContain("processedMic:xlr1", plan.Disabled.Keys);
        Assert.DoesNotContain("audition", plan.Disabled.Keys);
    }

    [Fact]
    public void OnlyTheJacksWhoseMonitorMixesAreAllDryHearTheRawInput()
    {
        // Headphones 1 plays Monitor A, which is dry; Headphones 2 plays
        // Monitor B, which is not, and keeps the processed voice.
        HardwareMixingSetup setup = ProSetup(auxPort: false) with
        {
            Jacks = [("hp1", "monitor"), ("hp2", "monitor2")],
            LevelsDb = new Dictionary<string, double> { ["xlr1|monitor"] = -3, ["xlr1|monitor2"] = -3 },
        };
        HardwareMixingPlan plan = HardwareMixingPlanner.Plan(setup);
        Assert.True(plan.Active);
        int back = ProMixSections.SourceOfPair(plan.ReturnPairs["xlr1"]);
        int a = plan.Jacks["hp1"], b = plan.Jacks["hp2"];
        Assert.True(Member(plan, a, ProMixSections.Xlr1));
        Assert.False(Member(plan, a, back));
        Assert.True(Member(plan, b, back));
        Assert.False(Member(plan, b, ProMixSections.Xlr1));
        // A jack that sums Monitor A with Monitor B hears it processed, never twice.
        HardwareMixingPlan summed = HardwareMixingPlanner.Plan(setup with { Jacks = [("hp1", "monitor+monitor2")] });
        int both = summed.Jacks["hp1"];
        Assert.False(Member(summed, both, ProMixSections.Xlr1));
        Assert.True(Member(summed, both, ProMixSections.SourceOfPair(summed.ReturnPairs["xlr1"])));
        // Both dry: no jack needs the return, and the Aux mix is off.
        HardwareMixingPlan allDry = HardwareMixingPlanner.Plan(setup with { DryMixes = new HashSet<string> { "monitor", "monitor2" } });
        Assert.Empty(allDry.ReturnPairs);
        Assert.True(Member(allDry, allDry.Jacks["hp2"], ProMixSections.Xlr1));
    }

    private static void Set(Mixer mixer, string kind, bool on)
    {
        switch (kind)
        {
            case "voice": mixer.SetVoiceChanger("xlr1", new(Enabled: on)); break;
            case "noise": mixer.SetNoiseRemoval("xlr1", new(Enabled: on)); break;
            case "eq": mixer.SetEqEnabled("xlr1", on); break;
            default: mixer.SetLowCutHz(on ? 80 : 0); break;
        }
    }
}
