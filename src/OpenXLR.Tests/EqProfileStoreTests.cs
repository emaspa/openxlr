using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The daemon's EQ profiles: the built-in presets listed first under stable
/// ids, then the saved profiles of eq-profiles.json in the format the UI
/// wrote before; names unique whatever their case; save, rename and delete
/// keeping the rest of the file, which is private and written whole; a file
/// that is not a profile list, or one entry that does not read, reported in
/// the state and never overwritten; values outside the EQ's ranges held to
/// them; at most 64 profiles; a hand edit listed with the next state; the
/// commands checked, dispatched and answered through the hub; and a load
/// that becomes one setEq with the profile's bands and the EQ on.
/// </summary>
[Collection("xdg-config")]
public sealed class EqProfileStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-eq-profile-store-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public EqProfileStoreTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    private const string Band = """{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":0.7046930689671475}""";

    private static void WriteFile(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EqProfileStore.FilePath)!);
        File.WriteAllText(EqProfileStore.FilePath, text);
    }

    private static IReadOnlyList<EqBand> Bands(double gain = 3) =>
        [.. MicEq.Default.Bands.Select((b, i) => i == 1 ? b with { Enabled = true, Gain = gain } : b)];

    private sealed class Layout : ILayoutInfo
    {
        public bool HasChannel(string id) => id is "xlr1" or "xlr2" or "aux" or "guest";
        public bool HasMix(string id) => id is "monitor";
        public bool IsMonitorFeed(string feed) => feed is "monitor";
        public bool IsMonitorOutput(string device) => false;
        public bool IsInsertKey(string key) => key is "xlr1" or "xlr2" or "guest";
        public bool IsMicrophoneChannel(string id) => id is "xlr1" or "xlr2" or "guest";
        public int OverrideCount => 0;
    }

    private static string? Check(string json)
        => CommandValidation.Check(JsonSerializer.Deserialize<Command>(json, Web)!, new Layout(), _ => null, true);

    [Fact]
    public void TheBuiltInPresetsComeFirstUnderStableIdsAndAreMicSetupsOwn()
    {
        EqProfilesState state = EqProfileStore.State();
        Assert.Null(state.Problem);
        Assert.Equal(EqPresets.All.Select(p => p.Id), state.Profiles.Select(p => p.Id));
        Assert.Equal(EqPresets.All.Select(p => p.Name), state.Profiles.Select(p => p.Name));
        Assert.All(state.Profiles, p => Assert.True(p.Builtin));
        // Mic setup offers the voice curves of the same table, not a copy of them.
        Assert.Equal(EqPresets.Voices.Select(p => p.Key), OpenXLR.UI.MicSetupPresets.All.Select(p => p.Id));
        foreach ((EqPreset preset, OpenXLR.UI.MicSetupPreset offered) in EqPresets.Voices.Zip(OpenXLR.UI.MicSetupPresets.All))
        {
            Assert.Equal(preset.Eq.Bands.Count, offered.Eq.Bands.Count);
            Assert.True(preset.Eq.Enabled);
            Assert.Null(MicEq.Check(preset.Eq));
            for (int i = 0; i < preset.Eq.Bands.Count; i++)
            {
                EqBand a = preset.Eq.Bands[i];
                OpenXLR.UI.EqBand b = offered.Eq.Bands[i];
                Assert.Equal((a.Enabled, a.Type, a.Frequency, a.Gain, a.Q), (b.Enabled, b.Type, b.Frequency, b.Gain, b.Q));
            }
            Assert.Equal(preset.Eq.Bands, EqProfileStore.Find(preset.Id)!.Bands);
        }
        Assert.Null(EqProfileStore.Find("builtin:loud"));
        Assert.Null(EqProfileStore.Find("Clear"));
    }

    [Fact]
    public void SaveRenameAndDeleteKeepTheOthersAndNamesAreUniqueWhateverTheirCase()
    {
        EqProfileStore.Save("  Voice  ", Bands());
        EqProfileStore.Save("Music", Bands(6));
        EqProfileStore.Save("Bright", Bands());
        Assert.Equal(["Voice", "Music", "Bright"], EqProfileStore.Read().Profiles.Select(p => p.Name));

        var exists = Assert.Throws<InvalidDataException>(() => EqProfileStore.Save("voice", Bands(-3)));
        Assert.Equal("an EQ profile named Voice already exists", exists.Message);
        EqProfileStore.Save("voice", Bands(-3), replace: true);
        Assert.Equal(["voice", "Music", "Bright"], EqProfileStore.Read().Profiles.Select(p => p.Name));
        Assert.Equal(-3, EqProfileStore.Find("saved:VOICE")!.Bands[1].Gain);

        foreach (string bad in new[] { "", "   ", new string('x', EqProfileStore.MaximumNameLength + 1), "a\nb" })
            Assert.Equal(EqProfileStore.NameRule, Assert.Throws<InvalidDataException>(() => EqProfileStore.Save(bad, Bands())).Message);

        EqProfileStore.Rename("music", "Singing");
        EqProfileStore.Rename("voice", "Voice");
        Assert.Equal(["Voice", "Singing", "Bright"], EqProfileStore.Read().Profiles.Select(p => p.Name));
        Assert.Equal("an EQ profile named BRIGHT already exists",
            Assert.Throws<InvalidDataException>(() => EqProfileStore.Rename("Voice", "BRIGHT")).Message);
        EqProfileStore.Delete("SINGING");
        Assert.Equal(["Voice", "Bright"], EqProfileStore.Read().Profiles.Select(p => p.Name));
        Assert.Equal("there is no EQ profile named Singing", Assert.Throws<InvalidDataException>(() => EqProfileStore.Delete("Singing")).Message);
        Assert.Throws<InvalidDataException>(() => EqProfileStore.Rename("Singing", "Again"));

        // The state lists them after the built-in ones, under saved: ids.
        EqProfilesState state = EqProfileStore.State();
        Assert.Equal(EqPresets.All.Select(p => p.Id).Concat(["saved:Voice", "saved:Bright"]), state.Profiles.Select(p => p.Id));
        Assert.False(state.Profiles[EqPresets.All.Count].Builtin);

        // The file is private and holds the bands as setEq carries them, in the UI's format.
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(EqProfileStore.FilePath));
        JsonNode file = JsonNode.Parse(File.ReadAllText(EqProfileStore.FilePath))!;
        Assert.Equal(1, file["version"]!.GetValue<int>());
        Assert.Equal(["version", "profiles"], file.AsObject().Select(p => p.Key));
        Assert.Equal(["name", "bands"], file["profiles"]![0]!.AsObject().Select(p => p.Key));
        JsonNode band = file["profiles"]![0]!["bands"]![1]!;
        Assert.Equal(["enabled", "type", "frequency", "gain", "q"], band.AsObject().Select(p => p.Key));
        Assert.Equal(-3, band["gain"]!.GetValue<double>());
    }

    [Fact]
    public void AFileTheUiWroteKeepsWorking()
    {
        WriteFile("""
            {
              "version": 1,
              "profiles": [
                {"name":"Podcast","bands":[{"enabled":true,"type":"lowCut","frequency":80,"gain":0,"q":0.7046930689671475},
                  {"enabled":true,"type":"peak","frequency":250,"gain":-3.5,"q":1.4}]}
              ]
            }
            """);
        EqProfileEntry podcast = EqProfileStore.State().Profiles.Single(p => !p.Builtin);
        Assert.Equal("saved:Podcast", podcast.Id);
        Assert.Equal([80d, 250], podcast.Bands.Select(b => b.Frequency));
        Assert.Equal(MicEq.SnapQ(1.4), podcast.Bands[1].Q);
        EqProfileStore.Save("Second", Bands());
        Assert.Equal(["Podcast", "Second"], EqProfileStore.Read().Profiles.Select(p => p.Name));
    }

    [Fact]
    public void TheStoreKeepsAtMostSixtyFourProfiles()
    {
        for (int i = 0; i < EqProfileStore.MaximumProfiles; i++) EqProfileStore.Save($"P{i}", MicEq.Default.Bands);
        var error = Assert.Throws<InvalidDataException>(() => EqProfileStore.Save("One more", MicEq.Default.Bands));
        Assert.Equal("OpenXLR keeps up to 64 EQ profiles; delete one to save another", error.Message);
        EqProfileStore.Save("p0", MicEq.Default.Bands, replace: true);
        Assert.Equal(EqProfileStore.MaximumProfiles, EqProfileStore.Read().Profiles.Count);
    }

    [Fact]
    public void HandEditedValuesAreHeldToTheRangesAndSnapped()
    {
        WriteFile("""
            {"version":1,"profiles":[{"name":" Edited ","bands":[
              {"enabled":true,"type":"lowCut","frequency":5,"gain":30,"q":50},
              {"enabled":false,"type":"highShelf","frequency":99999,"gain":-40,"q":0.001},
              {"enabled":true,"type":"peak","frequency":1000.4,"gain":1.1,"q":0.71}]}]}
            """);
        SavedEqProfile profile = Assert.Single(EqProfileStore.Read().Profiles);
        Assert.Equal("Edited", profile.Name);
        Assert.Equal([20d, 20000, 1000], profile.Bands.Select(b => b.Frequency));
        Assert.Equal([12d, -12, 1], profile.Bands.Select(b => b.Gain));
        Assert.Equal([MicEq.MaxQ, MicEq.MinQ, MicEq.SnapQ(0.71)], profile.Bands.Select(b => b.Q));
        Assert.All(profile.Bands, b => Assert.Null(MicEq.Check(b)));
    }

    public static TheoryData<string> UnreadableEntries => new()
    {
        """{"name":"Bad type","bands":[{"enabled":true,"type":"bandPass","frequency":1000,"gain":0,"q":1}]}""",
        """{"name":"No gain","bands":[{"enabled":true,"type":"peak","frequency":1000,"q":1}]}""",
        """{"name":"Text gain","bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":"3","q":1}]}""",
        """{"name":"Switch","bands":[{"enabled":1,"type":"peak","frequency":1000,"gain":0,"q":1}]}""",
        """{"name":"Huge","bands":[{"enabled":true,"type":"peak","frequency":1e400,"gain":0,"q":1}]}""",
        """{"name":"No bands","bands":[]}""",
        "{\"name\":\"Seventeen\",\"bands\":[" + string.Join(",", Enumerable.Repeat(Band, 17)) + "]}",
        "{\"name\":\"\",\"bands\":[" + Band + "]}",
        "{\"name\":\"a\\u0007b\",\"bands\":[" + Band + "]}",
        "{\"name\":\"" + new string('x', 81) + "\",\"bands\":[" + Band + "]}",
        "{\"name\":\"good\",\"bands\":[" + Band + "]}",
        "{\"bands\":[" + Band + "]}",
        "[]",
        "null",
    };

    [Theory]
    [MemberData(nameof(UnreadableEntries))]
    public void AnEntryThatDoesNotReadIsLeftOutReportedAndTheFileIsNeverOverwritten(string entry)
    {
        // "good" repeats the first profile's name in another case.
        string text = "{\"version\":1,\"profiles\":[{\"name\":\"Good\",\"bands\":[" + Band + "]}," + entry + "]}";
        WriteFile(text);
        SavedEqProfileList list = EqProfileStore.Read();
        Assert.Equal("Good", Assert.Single(list.Profiles).Name);
        Assert.Equal(1, list.Unreadable);
        EqProfilesState state = EqProfileStore.State();
        Assert.Equal("saved:Good", state.Profiles[^1].Id);
        Assert.Equal(new EqProfileProblem("unreadable", 1, EqProfileStore.FilePath,
            $"Some profiles in {EqProfileStore.FilePath} could not be read (1) and are not listed. Until they are fixed or removed, OpenXLR saves no changes to the file."),
            state.Problem);
        // The good one still loads; nothing writes over the file while it holds the entry.
        Assert.NotNull(EqProfileStore.Find("saved:good"));
        Assert.Throws<EqProfileFileException>(() => EqProfileStore.Save("New", Bands()));
        Assert.Throws<EqProfileFileException>(() => EqProfileStore.Rename("Good", "Renamed"));
        Assert.Throws<EqProfileFileException>(() => EqProfileStore.Delete("Good"));
        Assert.Equal(text, File.ReadAllText(EqProfileStore.FilePath));
    }

    public static TheoryData<string> DamagedFiles => new()
    {
        "",
        "not json",
        "null",
        "[]",
        """{"profiles":[]}""",
        """{"version":2,"profiles":[]}""",
        """{"version":"1","profiles":[]}""",
        """{"version":1,"profiles":{}}""",
        """{"version":1,"profiles":[],"profiles":[]}""",
        """{"version":1,"version":1,"profiles":[]}""",
        "{\"version\":1,\"profiles\":[" + string.Join(",", Enumerable.Range(0, 65).Select(i => $"{{\"name\":\"P{i}\",\"bands\":[{Band}]}}")) + "]}",
        "{\"version\":1,\"profiles\":[{\"name\":\"Twice\",\"name\":\"Again\",\"bands\":[" + Band + "]}]}",
        "{\"version\":1,\"profiles\":[" + new string('[', 40) + new string(']', 40) + "]}",
    };

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public void AFileThatIsNotAProfileListIsReportedAndKept(string text)
    {
        WriteFile(text);
        var error = Assert.Throws<EqProfileFileException>(() => EqProfileStore.Read());
        Assert.Equal("damaged", error.Reason);
        EqProfilesState state = EqProfileStore.State();
        Assert.All(state.Profiles, p => Assert.True(p.Builtin));
        Assert.Equal("damaged", state.Problem!.Reason);
        Assert.Equal(error.Message, state.Problem.Message);
        Assert.DoesNotContain("\n", state.Problem.Message, StringComparison.Ordinal);
        Assert.Throws<EqProfileFileException>(() => EqProfileStore.Save("New", Bands()));
        Assert.Equal(text, File.ReadAllText(EqProfileStore.FilePath));
    }

    [Fact]
    public void AnOversizedFileIsNotRead()
    {
        WriteFile("{\"version\":1,\"profiles\":[]}" + new string(' ', EqProfileStore.MaximumFileBytes));
        Assert.Equal("tooLarge", Assert.Throws<EqProfileFileException>(() => EqProfileStore.Read()).Reason);
        Assert.Equal("tooLarge", EqProfileStore.State().Problem!.Reason);
    }

    [Fact]
    public async Task APipeInPlaceOfTheFileCannotBlockTheState()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(EqProfileStore.FilePath)!);
        var created = OpenXLR.Core.ProcessRunner.Run("mkfifo", [EqProfileStore.FilePath]);
        Assert.True(created.Ok, created.Stderr);
        var read = Task.Run(() => EqProfileStore.State(reread: true));
        try
        {
            EqProfilesState state = await read.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("failed", state.Problem!.Reason);
            Assert.Equal(EqPresets.All.Count, state.Profiles.Count);
        }
        finally
        {
            using var keeper = new FileStream(EqProfileStore.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void AHandEditShowsWithTheNextStateAndNotBefore()
    {
        EqProfileStore.Save("First", Bands());
        EqProfilesState first = EqProfileStore.State();
        Assert.Same(first, EqProfileStore.State());
        WriteFile("{\"version\":1,\"profiles\":[{\"name\":\"By hand\",\"bands\":[" + Band + "," + Band + "]}]}");
        EqProfilesState edited = EqProfileStore.State();
        Assert.Equal("saved:By hand", edited.Profiles[^1].Id);
        Assert.Same(edited, EqProfileStore.Listed);
    }

    [Theory]
    [InlineData("""{"cmd":"listEqProfiles"}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"builtin:clear"}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","channel":"guest","profile":"saved:My voice"}""", null)]
    [InlineData("""{"cmd":"loadEqProfile","channel":"aux","profile":"builtin:clear"}""", "loadEqProfile: 'aux' is not a microphone or capture input")]
    [InlineData("""{"cmd":"loadEqProfile","profile":"builtin:clear"}""", "loadEqProfile: need 'channel' or 'mix'")]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1"}""", "loadEqProfile: need 'profile'")]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"Clear"}""", "loadEqProfile: 'profile' must start with 'builtin:' or 'saved:'")]
    [InlineData("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"saved:"}""", "loadEqProfile: 'profile' is not a profile id")]
    [InlineData("""{"cmd":"saveEqProfile","name":"Voice","bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":1}]}""", null)]
    [InlineData("""{"cmd":"saveEqProfile","name":"Voice","replace":true,"bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":1}]}""", null)]
    [InlineData("""{"cmd":"saveEqProfile","name":"Voice"}""", "saveEqProfile: need 'bands'")]
    [InlineData("""{"cmd":"saveEqProfile","name":"Voice","bands":[]}""", "saveEqProfile: need 1 to 16 bands")]
    [InlineData("""{"cmd":"saveEqProfile","name":"Voice","bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":30,"q":1}]}""", "saveEqProfile: band 1: gain must be -12 to +12 dB")]
    [InlineData("""{"cmd":"saveEqProfile","name":" ","bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":1}]}""", "saveEqProfile: need 'name', a profile name has 1 to 80 characters and no control characters")]
    [InlineData("""{"cmd":"renameEqProfile","name":"Voice","newName":"Speech"}""", null)]
    [InlineData("""{"cmd":"renameEqProfile","name":"Voice"}""", "renameEqProfile: need 'newName', a profile name has 1 to 80 characters and no control characters")]
    [InlineData("""{"cmd":"deleteEqProfile","name":"Voice"}""", null)]
    [InlineData("""{"cmd":"deleteEqProfile"}""", "deleteEqProfile: need 'name', a profile name has 1 to 80 characters and no control characters")]
    public void TheCommandsAreCheckedBeforeTheyAct(string json, string? error) => Assert.Equal(error, Check(json));

    [Fact]
    public void ALoadBecomesOneSetEqWithTheProfilesBandsAndTheEqOn()
    {
        EqProfileStore.Save("Six", [.. Enumerable.Repeat(MicEq.Default.Bands[1] with { Gain = 2 }, 6)]);
        var load = new Command { Cmd = "loadEqProfile", Channel = "guest", Profile = "saved:six" };
        Command setEq = MixerService.ProfileEq(load, EqLimits.Instance, out string? error)!;
        Assert.Null(error);
        Assert.Equal("setEq", setEq.Cmd);
        Assert.Equal("guest", setEq.Channel);
        MicEq eq = MicEq.Read(setEq.Eq, out error)!;
        Assert.Null(error);
        Assert.True(eq.Enabled);
        Assert.Equal(EqProfileStore.Find("saved:Six")!.Bands, eq.Bands);
        Assert.Null(Check(JsonSerializer.Serialize(new { cmd = setEq.Cmd, channel = setEq.Channel, eq = setEq.Eq })));

        Command builtin = MixerService.ProfileEq(load with { Profile = "builtin:broadcast" }, EqLimits.Instance, out _)!;
        Assert.Equal(EqPresets.Broadcast.Eq, MicEq.Read(builtin.Eq, out _));

        // A microphone whose EQ takes four bands and no other count refuses six.
        Assert.Null(MixerService.ProfileEq(load, new EqLimits(), out error));
        Assert.Equal("loadEqProfile: Six has 6 bands, and this microphone's EQ takes 4 to 4", error);
        Assert.Null(MixerService.ProfileEq(load with { Profile = "saved:Gone" }, EqLimits.Instance, out error));
        Assert.Equal("loadEqProfile: there is no EQ profile 'saved:Gone'", error);
    }

    [Fact]
    public async Task TheHubSavesRenamesDeletesAndListsAndTheStateCarriesTheList()
    {
        var settings = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, settings, () => []);
        using var mixer = new MixerService(NullLogger<MixerService>.Instance, settings, devices);
        var hub = new WebSocketHub(devices, mixer, NullLogger<WebSocketHub>.Instance, new Lifetime());

        StateMessage state = hub.Snapshot();
        Assert.Equal(EqPresets.All.Count, state.EqProfiles!.Count);
        Assert.Null(state.EqProfileProblem);
        string band = """{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":1}""";
        Assert.True((await hub.ExecuteForApiAsync($$"""{"cmd":"saveEqProfile","name":"Podcast","bands":[{{band}},{{band}}]}""")).Ok);
        ApiCommandResult again = await hub.ExecuteForApiAsync($$"""{"cmd":"saveEqProfile","name":"podcast","bands":[{{band}}]}""");
        Assert.False(again.Ok);
        Assert.Equal("saveEqProfile: an EQ profile named Podcast already exists",
            again.Messages.OfType<ErrorMessage>().Single().Message);
        Assert.True((await hub.ExecuteForApiAsync($$"""{"cmd":"saveEqProfile","name":"podcast","replace":true,"bands":[{{band}}]}""")).Ok);
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"renameEqProfile","name":"PODCAST","newName":"Voice"}""")).Ok);
        state = hub.Snapshot();
        EqProfileEntry voice = state.EqProfiles!.Single(p => !p.Builtin);
        Assert.Equal(("saved:Voice", "Voice", 1), (voice.Id, voice.Name, voice.Bands.Count));
        Assert.Equal(MicEq.SnapQ(1), voice.Bands[0].Q);

        // On the wire: id, name, builtin and bands, each band as setEq carries it.
        JsonNode wire = JsonNode.Parse(JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        JsonNode first = wire["eqProfiles"]![0]!;
        Assert.Equal(["id", "name", "builtin", "bands"], first.AsObject().Select(p => p.Key));
        Assert.Equal(["enabled", "type", "frequency", "gain", "q"], first["bands"]![0]!.AsObject().Select(p => p.Key));

        // A hand edit: listEqProfiles reads the file again.
        WriteFile("{\"version\":1,\"profiles\":[{\"name\":\"Hand\",\"bands\":[" + Band + "]},{\"name\":\"Broken\"}]}");
        Assert.True((await hub.ExecuteForApiAsync("""{"cmd":"listEqProfiles"}""")).Ok);
        state = hub.Snapshot();
        Assert.Equal("saved:Hand", state.EqProfiles![^1].Id);
        Assert.Equal(("unreadable", 1), (state.EqProfileProblem!.Reason, state.EqProfileProblem.Count));
        ApiCommandResult refused = await hub.ExecuteForApiAsync("""{"cmd":"deleteEqProfile","name":"Hand"}""");
        Assert.False(refused.Ok);
        Assert.StartsWith("deleteEqProfile: Some profiles in ", refused.Messages.OfType<ErrorMessage>().Single().Message);

        File.Delete(EqProfileStore.FilePath);
        Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"deleteEqProfile","name":"Hand"}""")).Ok);
        Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"renameEqProfile","name":"Hand","newName":"x"}""")).Ok);
        // A load reaches the mixer, which is not running here.
        ApiCommandResult load = await hub.ExecuteForApiAsync("""{"cmd":"loadEqProfile","channel":"xlr1","profile":"builtin:warm"}""");
        Assert.False(load.Ok);
        Assert.Equal(mixer.NotBuiltError, load.Messages.OfType<ErrorMessage>().Single().Message);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
