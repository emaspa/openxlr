using System.Text.Json;
using System.Text.RegularExpressions;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// A microphone heard dry in the monitor mixes: the switch, its command, how
/// it is saved and recalled, the pattern its route follows, and what it does
/// to the Wave XLR Pro's direct path and to hardware mixing. The audio is
/// checked on a private PipeWire server by <see cref="MonitorVolumeIntegrationTests"/>.
/// </summary>
public sealed class MonitorDryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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

    private static string? Check(string json)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, false);

    [Theory]
    [InlineData("""{"cmd":"setMonitorDry","channel":"xlr1","value":true}""", null)]
    [InlineData("""{"cmd":"setMonitorDry","channel":"guest","value":false}""", null)]
    [InlineData("""{"cmd":"setMonitorDry","value":true}""", "setMonitorDry: need 'channel'")]
    [InlineData("""{"cmd":"setMonitorDry","channel":"aux","value":true}""", "setMonitorDry: 'aux' is not a microphone input")]
    [InlineData("""{"cmd":"setMonitorDry","channel":"system","value":true}""", "setMonitorDry: 'system' is not a microphone input")]
    [InlineData("""{"cmd":"setMonitorDry","channel":"xlr1"}""", "setMonitorDry: value must be a boolean")]
    [InlineData("""{"cmd":"setMonitorDry","channel":"xlr1","value":1}""", "setMonitorDry: value must be a boolean")]
    public void TheCommandIsCheckedBeforeTheMixerAndNeedsNoNativeHelper(string json, string? error)
        => Assert.Equal(error, Check(json));

    [Fact]
    public void TheSwitchIsOffByDefaultOnEveryMicrophoneAndAbsentElsewhere()
    {
        using var mixer = new Mixer();
        MixerState state = mixer.Snapshot();
        Assert.False(state.Channels.Single(c => c.Id == "xlr1").MonitorDry);
        Assert.False(state.Channels.Single(c => c.Id == "xlr2").MonitorDry);
        Assert.Null(state.Channels.Single(c => c.Id == "aux").MonitorDry);
        Assert.Null(state.Channels.Single(c => c.Id == "system").MonitorDry);
        Assert.Empty(mixer.ExportSettings().MonitorDry);
    }

    [Fact]
    public void TheMixerStoresTheSwitchAndRefusesAChannelThatIsNoMicrophone()
    {
        using var mixer = new Mixer();
        mixer.SetMonitorDry("xlr1", true);
        mixer.SetMonitorDry("xlr1", true);
        Assert.True(mixer.MonitorDry("xlr1"));
        Assert.False(mixer.MonitorDry("xlr2"));
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").MonitorDry);
        Assert.Throws<ArgumentException>(() => mixer.SetMonitorDry("aux", true));
        Assert.Throws<ArgumentException>(() => mixer.SetMonitorDry("game", true));

        Assert.Equal(new Dictionary<string, bool> { ["xlr1"] = true }, mixer.ExportSettings().MonitorDry);
        // A profile names every microphone, so a recall sets each of them.
        Assert.Equal(new Dictionary<string, bool> { ["xlr1"] = true, ["xlr2"] = false }, mixer.ExportScene().MonitorDry);

        mixer.SetMonitorDry("xlr1", false);
        Assert.False(mixer.MonitorDry("xlr1"));
        Assert.Empty(mixer.ExportSettings().MonitorDry);
    }

    [Fact]
    public void ARecallSetsTheChannelsItNamesAndLeavesTheOthers()
    {
        var current = new HashSet<string> { "xlr1", "guest" };
        // A profile from before the switch carries none: nothing changes.
        Assert.Equal(current, Mixer.RecallMonitorDry(current, null));
        Assert.Equal(current, Mixer.RecallMonitorDry(current, new Dictionary<string, bool>()));
        HashSet<string> next = Mixer.RecallMonitorDry(current, new Dictionary<string, bool> { ["xlr1"] = false, ["xlr2"] = true });
        Assert.Equal(["guest", "xlr2"], next.Order());
        Assert.Equal(["guest", "xlr1"], current.Order());
    }

    [Fact]
    public void TheSettingsFileRoundTripsTheSwitchAndAFileWithoutItHasItOff()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-dry-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """{ "monitorDry": { "xlr1": true, "cap1": true, "xlr2": false } }""");
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.True(settings.MonitorDry["xlr1"]);
            Assert.Null(settings.Save(path));
            Assert.Equal(settings.MonitorDry, MixerSettings.Load(path, out _)!.MonitorDry);
            Assert.Contains("\"monitorDry\"", File.ReadAllText(path));
            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out _)!.MonitorDry);
            // A hand edit that leaves null reads as none on, and says so.
            File.WriteAllText(path, """{ "monitorDry": null }""");
            MixerSettings edited = MixerSettings.Load(path, out string? note)!;
            Assert.Empty(edited.MonitorDry);
            Assert.Contains("monitorDry", note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AProfileRoundTripsTheSwitchAndOneWithoutLeavesItOut()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var scene = new MixerScene { MonitorDry = new() { ["xlr1"] = true, ["xlr2"] = false } };
        MixerScene back = JsonSerializer.Deserialize<MixerScene>(JsonSerializer.Serialize(scene, json), json)!;
        Assert.Equal(scene.MonitorDry, back.MonitorDry);
        SavedMixerValidation.Validate(back);
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("{}", json)!.MonitorDry);
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
    public void ADryMicrophoneKeepsTheProsDirectPathWithItsEffectsOn(string kind)
    {
        using var mixer = new Mixer();
        Set(mixer, kind, on: true);
        Assert.False(mixer.SetHardwareMicMonitor(true));
        mixer.SetMonitorDry("xlr1", true);
        Assert.True(mixer.SetHardwareMicMonitor(true));
        // XLR 2's switch is its own.
        mixer.SetMonitorDry("xlr2", true);
        mixer.SetMonitorDry("xlr1", false);
        Assert.False(mixer.SetHardwareMicMonitor(true));
    }

    [Fact]
    public void ADryMicrophoneOnXlr2KeepsOnlyItsOwnPath()
    {
        using var mixer = new Mixer();
        mixer.SetInputJacks(2, auxInput: true);
        mixer.SetVoiceChanger("xlr1", new(Enabled: true));
        mixer.SetVoiceChanger("xlr2", new(Enabled: true));
        mixer.SetMonitorDry("xlr2", true);
        Assert.False(mixer.SetHardwareMicMonitor(true));
        Assert.True(mixer.SetHardwareMicMonitor(true, input: 2));
    }

    private static readonly HardwareMixingUnit Pro = new() { Pro = true, Connected = true, Firmware = "1.0.14", SectionsDecode = true };

    /// <summary>The default layout on a ready Pro, Headphones 1 on Monitor A, XLR 1 processed and heard dry.</summary>
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
        MonitorDry = new HashSet<string> { "xlr1" },
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
        HardwareMixingPlan processed = HardwareMixingPlanner.Plan(ProSetup(auxPort: true) with { MonitorDry = new HashSet<string>() });
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
