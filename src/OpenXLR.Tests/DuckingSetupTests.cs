using System.Reflection;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class DuckingSetupTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DefaultsSelectOnlyApplicationsInMonitorAWithMediumPresets()
    {
        MixerConfig layout = MixerConfig.Default();
        layout = layout with { Channels = [.. layout.Channels, new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 }] };
        DuckingSetup setup = DuckingSetup.Default(layout);
        Assert.False(setup.Enabled);
        Assert.Equal("xlr1", setup.Key);
        Assert.Equal(layout.Channels.Where(c => c.IsApplication).Select(c => c.Id), setup.Channels);
        Assert.Equal(["monitor"], setup.Mixes);
        Assert.Equal(("medium", "medium", "medium", "device"), (setup.Sensitivity, setup.Amount, setup.Speed, setup.Processing));
        Assert.Equal((-12.0, 12.0, 80.0, 1200.0), Values(setup.ToPlan()));
    }

    [Theory]
    [InlineData("low", "low", "slow", -3, 6, 100, 2000)]
    [InlineData("medium", "medium", "medium", -12, 12, 80, 1200)]
    [InlineData("high", "high", "fast", -24, 24, 30, 200)]
    public void PlansUseTheHardwarePresetTableAndExcludeTheKey(string sensitivity, string amount, string speed,
        double threshold, double depth, double attack, double release)
    {
        var setup = new DuckingSetup { Key = "mic", Channels = ["mic", "music", "game"], Mixes = ["monitor", "stream"],
            Sensitivity = sensitivity, Amount = amount, Speed = speed, Enabled = true };
        DuckingPlan plan = setup.ToPlan();
        Assert.Equal((threshold, depth, attack, release), Values(plan));
        Assert.True(plan.Enabled);
        Assert.Equal("mic", plan.Key);
        Assert.True(plan.Cells.SetEquals(["music|monitor", "music|stream", "game|monitor", "game|stream"]));
        Assert.Empty((setup with { Channels = [] }).ToPlan().Cells);
        Assert.Empty((setup with { Mixes = [] }).ToPlan().Cells);
        byte[] bytes = ProDucking.SettingBytes(sensitivity, amount, speed);
        Assert.Equal(unchecked((sbyte)bytes[0]), plan.ThresholdDb);
    }

    [Fact]
    public void CustomNumbersGoToTheSoftwareStageAsTheyAre()
    {
        var setup = new DuckingSetup { Key = "xlr1", Channels = ["music"], Mixes = ["monitor"], Enabled = true,
            Sensitivity = -37, Amount = 31, Speed = DuckingSpeed.Times(7, 4321) };
        Assert.Equal((-37.0, 31.0, 7.0, 4321.0), Values(setup.ToPlan()));
        // The bounds themselves are taken.
        Assert.Equal((-60.0, 1.0, 1.0, 10.0), Values((setup with { Sensitivity = -60, Amount = 1, Speed = DuckingSpeed.Times(1, 10) }).ToPlan()));
        Assert.Equal((0.0, 40.0, 1000.0, 10000.0),
            Values((setup with { Sensitivity = 0, Amount = 40, Speed = DuckingSpeed.Times(1000, 10000) }).ToPlan()));
        // A word and the number it stands for plan the same stage.
        Assert.Equal(Values((setup with { Sensitivity = "high", Amount = "low", Speed = "fast" }).ToPlan()),
            Values((setup with { Sensitivity = -24, Amount = 6, Speed = DuckingSpeed.Times(30, 200) }).ToPlan()));
        Assert.Throws<ArgumentException>(() => (setup with { Amount = 41 }).ToPlan());
    }

    [Fact]
    public void ValuesKeepTheirFormInJson()
    {
        var setup = new DuckingSetup { Sensitivity = -18, Amount = "high", Speed = DuckingSpeed.Times(45, 900) };
        string json = JsonSerializer.Serialize(setup, Json);
        Assert.Contains("\"sensitivity\":-18", json);
        Assert.Contains("\"amount\":\"high\"", json);
        Assert.Contains("\"speed\":{\"attack\":45,\"release\":900}", json);
        DuckingSetup back = JsonSerializer.Deserialize<DuckingSetup>(json, Json)!;
        Assert.Equal((setup.Sensitivity, setup.Amount, setup.Speed), (back.Sensitivity, back.Amount, back.Speed));
        // A number equal to a preset stays a number.
        Assert.Contains("\"sensitivity\":-12", JsonSerializer.Serialize(setup with { Sensitivity = -12 }, Json));
        // What is neither a word nor a whole number reads as a value every check refuses.
        foreach (string bad in new[] { "-12.5", "true", "[]", "{}", "{\"attack\":1}", "{\"attack\":1,\"release\":10,\"x\":1}" })
        {
            DuckingScene read = JsonSerializer.Deserialize<DuckingScene>($$"""{"sensitivity":{{bad}},"amount":{{bad}},"speed":{{bad}}}""", Json)!;
            Assert.Null(ProDucking.ThresholdOf(read.Sensitivity));
            Assert.Null(ProDucking.DepthOf(read.Amount));
            Assert.Null(ProDucking.TimesOf(read.Speed));
        }
    }

    private static (double, double, double, double) Values(DuckingPlan plan)
        => (plan.ThresholdDb, plan.DepthDb, plan.AttackMs, plan.ReleaseMs);

    public static TheoryData<DuckingSetup, string> InvalidSetups => new()
    {
        { new() { Key = "music" }, "key must be a microphone channel" },
        { new() { Key = "gone" }, "key must be a microphone channel" },
        { new() { Key = null! }, "key must be a microphone channel" },
        { new() { Channels = ["xlr1"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = ["music", "music"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = ["gone"] }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = null! }, "channels must be distinct channel ids other than the key" },
        { new() { Channels = [null!] }, "channels must be distinct channel ids other than the key" },
        { new() { Mixes = ["monitor", "monitor"] }, "mixes must be distinct mix ids" },
        { new() { Mixes = ["gone"] }, "mixes must be distinct mix ids" },
        { new() { Mixes = null! }, "mixes must be distinct mix ids" },
        { new() { Mixes = [null!] }, "mixes must be distinct mix ids" },
        { new() { Sensitivity = "custom" }, "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0" },
        { new() { Sensitivity = 3 }, "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0" },
        { new() { Sensitivity = -61 }, "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0" },
        { new() { Amount = null! }, "amount must be low, medium, high or a whole number of dB from 1 to 40" },
        { new() { Amount = 0 }, "amount must be low, medium, high or a whole number of dB from 1 to 40" },
        { new() { Amount = 41 }, "amount must be low, medium, high or a whole number of dB from 1 to 40" },
        { new() { Speed = "low" }, "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000" },
        { new() { Speed = DuckingSpeed.Times(0, 200) }, "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000" },
        { new() { Speed = DuckingSpeed.Times(30, 10001) }, "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000" },
        { new() { Processing = "hardware" }, "processing must be device or software" },
    };

    [Theory]
    [MemberData(nameof(InvalidSetups))]
    public void ValidationReportsEveryFieldBeforeSaving(DuckingSetup setup, string problem)
    {
        using var mixer = new Mixer();
        Assert.Equal(problem, DuckingSetup.Validate(setup, mixer));
        bool saved = false;
        ArgumentException error = Assert.Throws<ArgumentException>(() => mixer.SetDuckingSetup(setup, _ => { saved = true; return null; }));
        Assert.Equal("setDucking: " + problem, error.Message);
        Assert.False(saved);
    }

    [Fact]
    public void MonoCaptureKeysAreAcceptedAndStereoCaptureKeysAreRefused()
    {
        using var mixer = new Mixer();
        MixerConfig layout = MixerConfig.Default();
        Set(mixer, "_config", layout with { Channels = [.. layout.Channels,
            new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 },
            new("stereo", "Stereo") { CaptureSource = "source" }] });
        Assert.Null(DuckingSetup.Validate(new() { Key = "mic" }, mixer));
        Assert.Equal("key must be a microphone channel", DuckingSetup.Validate(new() { Key = "stereo" }, mixer));
        Assert.Null(DuckingSetup.Validate(new() { Channels = [], Mixes = [], Processing = "software" }, mixer));
    }

    [Fact]
    public void PruningKeepsRenamesDropsTargetsAndDisablesAMissingKeyWithoutAddingNewChannels()
    {
        MixerConfig layout = MixerConfig.Default();
        var setup = new DuckingSetup { Enabled = true, Key = "mic", Channels = ["music", "game", "xlr1"], Mixes = ["monitor", "stream"] };
        layout = layout with { Channels = [.. layout.Channels, new("mic", "Mic") { CaptureSource = "source", CaptureMonoChannel = 0 }] };
        DuckingSetup kept = DuckingSetup.Prune(setup, layout.WithChannelName("music", "Songs").WithMixName("stream", "Live"));
        Assert.True(kept.Enabled);
        Assert.Equal(setup.Channels, kept.Channels);
        Assert.Equal(setup.Mixes, kept.Mixes);
        kept = DuckingSetup.Prune(setup, layout.WithoutChannel("music").WithoutMix("stream"));
        Assert.Equal(["game", "xlr1"], kept.Channels);
        Assert.Equal(["monitor"], kept.Mixes);
        kept = DuckingSetup.Prune(setup, layout.WithoutChannel("mic"));
        Assert.False(kept.Enabled);
        Assert.Equal("xlr1", kept.Key);
        Assert.Equal(["music", "game"], kept.Channels);
        Assert.Equal(setup.Channels, DuckingSetup.Prune(setup, layout with { Channels = [.. layout.Channels, new("new", "New")] }).Channels);
    }

    [Fact]
    public void SetupSavesBeforeApplyingAndCopiesBothIncomingAndOutgoingLists()
    {
        using var mixer = new Mixer();
        var channels = new List<string> { "music" };
        var setup = new DuckingSetup { Enabled = true, Channels = channels, Processing = "software" };
        mixer.SetDuckingSetup(setup, settings =>
        {
            Assert.False(mixer.DuckingSnapshot().Setup.Enabled);
            Assert.True(settings.Ducking!.Enabled);
            Assert.NotSame(channels, settings.Ducking.Channels);
            Assert.False(settings.Ducking.Channels is IList<string> { IsReadOnly: false });
            return null;
        });
        channels.Add("game");
        Assert.Equal(["music"], mixer.DuckingSnapshot().Setup.Channels);
        Assert.False(mixer.DuckingSnapshot().Setup.Channels is IList<string> { IsReadOnly: false });
        Assert.Equal(["music"], mixer.ExportSettings().Ducking!.Channels);
        Assert.Throws<IOException>(() => mixer.SetDuckingSetup(setup with { Enabled = false }, _ => "read only"));
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
    }

    [Fact]
    public void ProfilesOmitProcessingPruneRecallAndLeaveAnAbsentSetupAlone()
    {
        using var mixer = new Mixer();
        mixer.SetDuckingSetup(new() { Enabled = true, Channels = ["music"], Processing = "software" }, _ => null);
        MixerScene scene = mixer.ExportScene();
        Assert.DoesNotContain("processing", JsonSerializer.Serialize(scene, Json), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["music"], scene.Ducking!.Channels);
        mixer.ApplyScene(new());
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
        mixer.ApplyScene(new() { Ducking = new() { Enabled = true, Key = "gone", Channels = ["game", "missing", "xlr1"], Mixes = ["monitor", "missing"] } });
        DuckingSetup recalled = mixer.DuckingSnapshot().Setup;
        Assert.False(recalled.Enabled);
        Assert.Equal("xlr1", recalled.Key);
        Assert.Equal(["game"], recalled.Channels);
        Assert.Equal(["monitor"], recalled.Mixes);
        Assert.Equal("software", recalled.Processing);
        mixer.ApplyScene(scene);
        Assert.True(mixer.DuckingSnapshot().Setup.Enabled);
        Assert.Equal(["music"], mixer.DuckingSnapshot().Setup.Channels);
    }

    [Fact]
    public void SettingsRoundTripAndBadEntriesAreDroppedWithIndividualNotes()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-duck-settings-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            OpenXlrPaths.WriteAtomic(path, """
                {"channelVolumes":{"music":0.4},"ducking":{"enabled":true,"key":"music",
                "channels":["music","gone","xlr1","game","game",null],"mixes":["monitor","gone","monitor"],
                "sensitivity":"wrong","amount":"high","speed":null,"processing":"wrong"}}
                """);
            MixerSettings saved = MixerSettings.Load(path, out string? warning)!;
            Assert.NotNull(warning);
            foreach (string field in new[] { "key", "channels", "mixes", "sensitivity", "speed", "processing" })
                Assert.Contains("ducking." + field, warning);
            Assert.Equal(.4, saved.ChannelVolumes["music"]);
            DuckingSetup setup = saved.Ducking!;
            Assert.False(setup.Enabled);
            Assert.Equal("xlr1", setup.Key);
            Assert.Equal(["music", "game"], setup.Channels);
            Assert.Equal(["monitor"], setup.Mixes);
            Assert.Equal(("medium", "high", "medium", "device"), (setup.Sensitivity, setup.Amount, setup.Speed, setup.Processing));
            Assert.Null(saved.Save(path));
            MixerSettings restored = MixerSettings.Load(path, out warning)!;
            Assert.Null(warning);
            Assert.Equal(JsonSerializer.Serialize(setup, Json), JsonSerializer.Serialize(restored.Ducking, Json));
            using var mixer = new Mixer();
            mixer.ApplySettings(restored);
            Assert.Equal(JsonSerializer.Serialize(setup, Json), JsonSerializer.Serialize(mixer.ExportSettings().Ducking, Json));
            OpenXlrPaths.WriteAtomic(path, """{"ducking":{"channels":null,"mixes":null,"amount":"wrong"}}""");
            restored = MixerSettings.Load(path, out warning)!;
            Assert.Empty(restored.Ducking!.Channels);
            Assert.Empty(restored.Ducking.Mixes);
            Assert.Contains("ducking.amount", warning);
            Assert.Contains("ducking.channels", warning);
            Assert.Contains("ducking.mixes", warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SavedNumbersLoadAsTheyAreAndOutOfRangeOnesAreResetLikeUnknownWords()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-duck-numbers-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            OpenXlrPaths.WriteAtomic(path, """
                {"ducking":{"enabled":true,"channels":["music"],"mixes":["monitor"],
                "sensitivity":-30,"amount":18,"speed":{"attack":45,"release":900}}}
                """);
            MixerSettings saved = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            DuckingSetup setup = saved.Ducking!;
            Assert.Equal((DuckingLevel.Number(-30), DuckingLevel.Number(18), DuckingSpeed.Times(45, 900)),
                (setup.Sensitivity, setup.Amount, setup.Speed));
            Assert.Null(saved.Save(path));
            string written = File.ReadAllText(path);
            Assert.Contains("\"sensitivity\": -30", written);
            Assert.Contains("\"amount\": 18", written);
            Assert.Contains("\"attack\": 45", written);
            Assert.Null(MixerSettings.Load(path, out warning)!.Ducking!.Speed.Word);
            Assert.Null(warning);

            OpenXlrPaths.WriteAtomic(path, """
                {"ducking":{"sensitivity":5,"amount":12.5,"speed":{"attack":80,"release":20000},"channels":["music"]}}
                """);
            setup = MixerSettings.Load(path, out warning)!.Ducking!;
            foreach (string field in new[] { "sensitivity", "amount", "speed" })
                Assert.Contains($"ducking.{field}: invalid value; reset to medium", warning);
            Assert.Equal(("medium", "medium", "medium"), (setup.Sensitivity, setup.Amount, setup.Speed));
            Assert.Equal(["music"], setup.Channels);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SavedDuckingWithoutMixesLowersTheDefaultMonitorOfTheLayout()
    {
        using var mixer = new Mixer();
        mixer.ApplyScene(JsonSerializer.Deserialize<MixerScene>("""{"ducking":{"channels":["music"]}}""", Json)!);
        Assert.Equal(["monitor"], mixer.DuckingSnapshot().Setup.Mixes);

        MixerConfig layout = MixerConfig.Default().WithoutMix("monitor");
        Set(mixer, "_config", layout);
        mixer.ApplyScene(JsonSerializer.Deserialize<MixerScene>("""{"ducking":{"enabled":true,"channels":["music"]}}""", Json)!);
        Assert.Equal(["monitor2"], mixer.DuckingSnapshot().Setup.Mixes);
        Assert.Equal(DuckingSetup.Default(layout).Mixes, mixer.DuckingSnapshot().Setup.Mixes);
        // A list the file names is kept as written, an empty one included.
        mixer.ApplyScene(JsonSerializer.Deserialize<MixerScene>("""{"ducking":{"mixes":[]}}""", Json)!);
        Assert.Empty(mixer.DuckingSnapshot().Setup.Mixes);
        mixer.ApplyScene(JsonSerializer.Deserialize<MixerScene>("""{"ducking":{"mixes":["monitor","stream"]}}""", Json)!);
        Assert.Equal(["stream"], mixer.DuckingSnapshot().Setup.Mixes);

        string dir = Directory.CreateTempSubdirectory("openxlr-duck-default-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            OpenXlrPaths.WriteAtomic(path, """
                {"mixes":[{"id":"phones","name":"Phones","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"}],
                "ducking":{"enabled":true}}
                """);
            MixerSettings saved = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.Equal(["monitor2"], saved.Ducking!.Mixes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MalformedProfilesAreRefusedBeforeChangingTheSetup()
    {
        using var mixer = new Mixer();
        foreach (DuckingScene invalid in new DuckingScene[]
        {
            new() { Key = null! }, new() { Channels = null! }, new() { Channels = ["xlr1"] },
            new() { Channels = ["music", "music"] }, new() { Mixes = [null!] },
            new() { Mixes = ["monitor", "monitor"] }, new() { Sensitivity = "wrong" },
            new() { Amount = "wrong" }, new() { Speed = "wrong" },
            new() { Sensitivity = 1 }, new() { Amount = 0 }, new() { Speed = DuckingSpeed.Times(80, 5) },
        }) Assert.Throws<JsonException>(() => mixer.ApplyScene(new() { Ducking = invalid }));
        Assert.False(mixer.DuckingSnapshot().Setup.Enabled);
        // A profile file holding numbers in range recalls them as numbers.
        MixerScene scene = JsonSerializer.Deserialize<MixerScene>(
            """{"ducking":{"enabled":true,"channels":["music"],"sensitivity":-45,"amount":3,"speed":{"attack":2,"release":30}}}""", Json)!;
        mixer.ApplyScene(scene);
        DuckingSetup recalled = mixer.DuckingSnapshot().Setup;
        Assert.Equal((DuckingLevel.Number(-45), DuckingLevel.Number(3), DuckingSpeed.Times(2, 30)),
            (recalled.Sensitivity, recalled.Amount, recalled.Speed));
        Assert.Contains("\"speed\":{\"attack\":2,\"release\":30}", JsonSerializer.Serialize(mixer.ExportScene(), Json));
    }

    [Fact]
    public void LayoutPersistencePrunesInTheSameSaveAndAFailedSaveKeepsTheSetup()
    {
        using var mixer = new Mixer();
        mixer.SetDuckingSetup(new() { Enabled = true, Channels = ["music", "game"], Mixes = ["monitor", "stream"] }, _ => null);
        MixerConfig layout = MixerConfig.Default().WithoutChannel("music").WithoutMix("stream");
        Set(mixer, "_config", layout);
        var persist = typeof(Mixer).GetMethod("PersistLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<MixerSettings, string?> fail = settings =>
        {
            Assert.Equal(["game"], settings.Ducking!.Channels);
            Assert.Equal(["monitor"], settings.Ducking.Mixes);
            Assert.DoesNotContain(settings.UserChannels!, c => c.Id == "music");
            Assert.DoesNotContain(settings.Mixes!, m => m.Id == "stream");
            return "read only";
        };
        Assert.IsType<IOException>(Assert.Throws<TargetInvocationException>(() => persist.Invoke(mixer, [fail])).InnerException);
        Assert.Equal(["music", "game"], mixer.DuckingSnapshot().Setup.Channels);
        Func<MixerSettings, string?> save = _ => null;
        persist.Invoke(mixer, [save]);
        Assert.Equal(["game"], mixer.DuckingSnapshot().Setup.Channels);
        Assert.Equal(["monitor"], mixer.DuckingSnapshot().Setup.Mixes);
    }

    internal static void Set(Mixer mixer, string name, object value)
        => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
