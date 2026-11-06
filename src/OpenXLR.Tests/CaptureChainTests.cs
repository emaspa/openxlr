using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class CaptureChainTests
{
    private static readonly ChannelDefinition Mono = new("mono", "Mono") { CaptureSource = "test_mic", CaptureMonoChannel = 2 };
    private static readonly ChannelDefinition Stereo = new("stereo", "Stereo") { CaptureSource = "test_pair", CapturePair = 1 };
    private static readonly InsertDefinition Insert = new() { Id = "gain", Kind = "lv2", Plugin = "urn:test:gain" };
    private static readonly PluginInfo Plugin = new("lv2", Insert.Plugin, "Gain", "", 1, 1, "in", "out", [], [], ["in"], ["out"]);

    private static Mixer Layout()
    {
        var mixer = new Mixer();
        typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer,
            MixerConfig.Default() with { Channels = [.. MixerConfig.Default().Channels, Mono, Stereo] });
        return mixer;
    }

    [Fact]
    public void WidthAndMicrophoneFollowTheBindingWhileTheSourceIsAbsent()
    {
        using var mixer = Layout();
        MixerState state = mixer.Snapshot();
        foreach (ChannelStatus channel in state.Channels)
        {
            bool microphone = channel.Id is "xlr1" or "xlr2" or "mono";
            int? width = microphone ? 1 : channel.Id == "stereo" ? 2 : null;
            Assert.Equal(microphone, channel.Microphone);
            Assert.Equal(width, channel.ChainWidth);
            Assert.Equal(width is not null, mixer.IsInsertKey(channel.Id));
            Assert.Equal(width ?? 0, mixer.InsertChannels(channel.Id));
        }
        Assert.Equal(2, mixer.InsertChannels("mix:monitor"));
        Assert.False(mixer.IsInsertKey("missing"));
        Assert.False(mixer.IsInsertKey("mix:missing"));
        using JsonDocument json = JsonSerializer.SerializeToDocument(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        JsonElement mono = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "mono");
        Assert.True(mono.GetProperty("microphone").GetBoolean());
        Assert.Equal(1, mono.GetProperty("chainWidth").GetInt32());
        JsonElement app = json.RootElement.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "system");
        Assert.Equal(JsonValueKind.Null, app.GetProperty("chainWidth").ValueKind);
    }

    [Theory]
    [InlineData(1, 0, null, false, true)]
    [InlineData(2, 0, null, false, false)]
    [InlineData(null, 0, null, false, false)]
    [InlineData(1, 1, null, false, false)]
    [InlineData(1, 0, 1, false, false)]
    [InlineData(1, 0, null, true, false)]
    public void MonoMigrationRequiresOneChannelAndAnEmptyChain(int? ports, int pair, int? mono, bool inserts, bool migrates)
    {
        ChannelDefinition channel = Stereo with { CaptureSource = "test_mic", CapturePair = pair, CaptureMonoChannel = mono };
        AudioNode source = new("test_mic", "Mic", AudioNodeKind.Source, false) { Channels = ports };
        ChannelDefinition migrated = CaptureBinding.MigrateMono(channel, [source], inserts ? [Insert with { Bypass = true }] : []);
        Assert.Equal(migrates, !ReferenceEquals(channel, migrated));
        if (migrates)
        {
            Assert.Equal(0, migrated.CaptureMonoChannel);
            Assert.Equal(1, migrated.ChainWidth);
        }
        Assert.Same(migrated, CaptureBinding.MigrateMono(migrated, [source], inserts ? [Insert] : []));
        Assert.Same(channel, CaptureBinding.MigrateMono(channel, [], []));
        Assert.Same(channel, CaptureBinding.MigrateMono(channel, [source, source], []));
        Assert.Same(channel, CaptureBinding.MigrateMono(channel, [source with { Kind = AudioNodeKind.Sink }], []));
    }

    [Fact]
    public void SavedInsertKeysFollowTheRestoredLayoutWithoutDroppingMisfits()
    {
        MixerSettings settings = new()
        {
            UserChannels = [new("mono", "Mono", "test_mic", 0, 0), new("stereo", "Stereo", "test_pair"), new("apps", "Apps")],
            UserMixes = [new("custom", "Custom")],
            Inserts = new() { ["mono"] = [Insert], ["stereo"] = [Insert], ["xlr1"] = [], ["xlr2"] = [],
                ["mix:monitor"] = [], ["mix:custom"] = [], ["mix:missing"] = [], ["apps"] = [], ["aux"] = [], ["missing"] = [] },
        };
        MixerSettings kept = SavedMixerValidation.Sanitize(settings, out IReadOnlyList<string> dropped);
        Assert.Equal(new[] { "mix:custom", "mix:monitor", "mono", "stereo", "xlr1", "xlr2" }, kept.Inserts.Keys.Order());
        Assert.Equal(Insert.Plugin, Assert.Single(kept.Inserts["stereo"]).Plugin);
        Assert.Equal(4, dropped.Count);
        Assert.All(dropped, text => Assert.Contains("has no insert chain", text));
        SavedMixerValidation.Validate(new OpenXLR.Core.MixerScene { Inserts = new() { ["deleted"] = [Insert] } });
    }

    [Theory]
    [InlineData("test_mic", 0, "microphone")]
    [InlineData("test_pair", null, "camera")]
    [InlineData("bluez_input.headset", 0, "headset")]
    [InlineData("bluez_input.headset", null, "headset")]
    public void CaptureDefaultsUseTheSourceAndMonoPort(string source, int? mono, string expected)
        => Assert.Equal(expected, LayoutDefaultIcons.ForChannel("capture", false, source, captureMonoChannel: mono));

    [Theory]
    [InlineData("mono", 1, null)]
    [InlineData("mono", 2, "no mono layout for an input chain")]
    [InlineData("stereo", 1, "no stereo layout for an input chain")]
    [InlineData("stereo", 2, null)]
    [InlineData("aux", 2, "has no insert chain")]
    [InlineData("system", 2, "has no insert chain")]
    public void InsertValidationUsesEachCaptureWidth(string channel, int width, string? error)
    {
        using var mixer = Layout();
        PluginInfo plugin = Plugin with { AudioIns = width, AudioOuts = width };
        string? result = CommandValidation.Check(new() { Cmd = "setInserts", Channel = channel, Inserts = [Insert] }, mixer, _ => plugin);
        if (error is null) Assert.Null(result);
        else Assert.Contains(error, result);
    }

    [Fact]
    public void ExistingInsertCommandsAcceptCaptureKeysAndTheEqTakesBoth()
    {
        using var mixer = Layout();
        foreach (string key in new[] { "mono", "stereo" })
        {
            mixer.SetInserts(key, [Insert]);
            foreach (string cmd in new[] { "setInsertBypass", "showInsertUi", "renameInsert" })
                Assert.Null(CommandValidation.Check(new() { Cmd = cmd, Channel = key, InsertId = "gain", Name = "Renamed" }, mixer, _ => Plugin));
            PluginInfo controls = Plugin with { Params = [new("gain", "Gain", 0, 1, 1, false, false, false, false, [])] };
            Assert.Null(CommandValidation.Check(new() { Cmd = "setInsertParam", Channel = key, InsertId = "gain", Symbol = "gain",
                Value = JsonSerializer.SerializeToElement(.5) }, mixer, _ => controls));
            // A capture microphone takes the EQ on a mono stage, a stereo pair on a stereo one.
            Assert.Null(CommandValidation.Check(new() { Cmd = "setEqEnabled", Channel = key, Value = JsonSerializer.SerializeToElement(true) }, mixer, _ => Plugin,
                nativeHostInstalled: true));
            // An existing misfit can still be removed or reordered.
            Assert.Null(CommandValidation.Check(new() { Cmd = "setInserts", Channel = key, Inserts = [Insert] }, mixer, _ => Plugin));
        }
    }

    [Fact]
    public void CaptureValidationKeepsInsertAndNativeHostLimits()
    {
        using var mixer = Layout();
        foreach (string key in new[] { "mono", "stereo" })
        {
            Assert.Contains("at most 16", CommandValidation.Check(new() { Cmd = "setInserts", Channel = key,
                Inserts = [.. Enumerable.Range(0, 17).Select(i => Insert with { Id = "slot" + i })] }, mixer, _ => Plugin));
            PluginInfo clap = Plugin with { Kind = "clap", Widths = [1, 2] };
            Assert.Contains("native plugin host is not installed", CommandValidation.Check(new() { Cmd = "setInserts",
                Channel = key, Inserts = [Insert with { Kind = "clap" }] }, mixer, _ => clap, nativeHostInstalled: false));
        }
    }

    [Fact]
    public void ProfilesNameEveryCaptureChainAndOldProfilesKeepAbsentInputs()
    {
        using var mixer = Layout();
        var scene = mixer.ExportScene();
        Assert.Empty(scene.Inserts!["mono"]);
        Assert.Empty(scene.Inserts["stereo"]);
        mixer.SetInserts("mono", [Insert]);
        var recalled = Mixer.RecallChains(mixer.ExportScene().Inserts!, new Dictionary<string, List<InsertDefinition>>
            { ["xlr1"] = [] }, key => key.StartsWith("mix:", StringComparison.Ordinal));
        Assert.Single(recalled["mono"]);
        Assert.Empty(recalled["stereo"]);
    }
}
