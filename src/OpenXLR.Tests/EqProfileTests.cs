using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Saved EQ profiles, with no window: a profile saved from one microphone
/// loads on another as one setEq with its bands and the EQ switched on;
/// names are unique whatever their case, and save, rename and delete keep
/// the rest of the file; the file is private and written whole; a file that
/// is not a profile list, or one entry in it that does not read, is reported
/// and never overwritten; values outside the daemon's ranges are held to
/// them; a profile with more bands than the microphone's EQ takes is refused;
/// and every open editor lists the same profiles.
/// </summary>
[Collection("xdg-config")]
public sealed class EqProfileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-eq-profiles-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public EqProfileTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    private sealed class Recorder : IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();
        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }
        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }
        public Task SetEqProcessingAsync(string channel, string value) { Sent.Enqueue(("setEqProcessing", channel, value)); return Task.CompletedTask; }
        public Task SetEqBandAsync(string channel, int band, EqBandChange change) { Sent.Enqueue(("setEqBand", channel, (band, change))); return Task.CompletedTask; }
    }

    private static (MicEqViewModel Eq, Recorder Sent) Model(string channel, bool variable = false)
    {
        var sent = new Recorder();
        var eq = new MicEqViewModel(sent, channel, channel, TimeSpan.Zero, new ManualTime());
        if (variable)
        {
            eq.Apply(JsonNode.Parse("""{"enabled":false,"bands":[],"backend":null,"available":true,"error":null}""")!,
                JsonNode.Parse("""{"minBands":1,"maxBands":16}"""));
        }
        return (eq, sent);
    }

    private static void WriteFile(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EqProfiles.FilePath)!);
        File.WriteAllText(EqProfiles.FilePath, text);
    }

    private const string Band = """{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":0.7046930689671475}""";

    [Fact]
    public void AProfileSavedFromOneMicrophoneLoadsOnAnotherAsOneSetEqWithTheEqOn()
    {
        var (xlr1, _) = Model("xlr1");
        var (xlr2, sent) = Model("xlr2");
        xlr1.Bands[1].DragTo(1000, 6);
        xlr1.Bands[3].Enabled = true;
        var saving = new EqProfilesViewModel(xlr1);
        var loading = new EqProfilesViewModel(xlr2);
        Assert.Empty(saving.Profiles);
        Assert.False(saving.HasSelection);

        Assert.True(saving.Save("  Podcast  ", replace: false));
        Assert.Equal("Podcast", Assert.Single(saving.Profiles).Name);
        Assert.Equal("Podcast", saving.Selected?.Name);
        Assert.Null(saving.Error);
        // The other editor lists it too, without being asked.
        EqProfile listed = Assert.Single(loading.Profiles);
        Assert.Equal(xlr1.Current.Bands, listed.Bands);

        // Loading off nothing chosen does nothing.
        Assert.False(loading.Load());
        Assert.Empty(sent.Sent);
        loading.Selected = listed;
        Assert.True(loading.HasSelection);
        Assert.True(loading.Load());
        Assert.True(sent.Sent.TryDequeue(out var load));
        Assert.Equal("setEq", load.Command);
        Assert.Equal("xlr2", load.Channel);
        Assert.Equal(new MicEq { Enabled = true, Bands = xlr1.Current.Bands }, (MicEq)load.Value);
        Assert.Empty(sent.Sent);
        Assert.True(xlr2.Enabled);
        Assert.Equal(1000, xlr2.Bands[1].Frequency);
        Assert.Equal(6, xlr2.Bands[1].Gain);

        // The file is private and holds the bands as setEq carries them.
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(EqProfiles.FilePath));
        JsonNode file = JsonNode.Parse(File.ReadAllText(EqProfiles.FilePath))!;
        Assert.Equal(1, file["version"]!.GetValue<int>());
        JsonNode band = file["profiles"]![0]!["bands"]![1]!;
        Assert.Equal(["enabled", "type", "frequency", "gain", "q"], band.AsObject().Select(p => p.Key));
        Assert.Equal(6, band["gain"]!.GetValue<double>());
        saving.Dispose();
        loading.Dispose();
        xlr1.Dispose();
        xlr2.Dispose();
    }

    [Fact]
    public void NamesAreUniqueAndSaveRenameAndDeleteKeepTheOthers()
    {
        var (eq, _) = Model("xlr1");
        var profiles = new EqProfilesViewModel(eq);
        Assert.True(profiles.Save("Voice", false));
        Assert.True(profiles.Save("Music", false));
        Assert.True(profiles.Save("Bright", false));

        // A name in use is refused unless replacing, whatever its case.
        eq.Bands[0].SetFrequency(80);
        Assert.False(profiles.Save("voice", false));
        Assert.Equal(Localizer.Format("EqProfileNameExists", "Voice"), profiles.Error);
        Assert.Equal(36, profiles.Find("Voice")!.Bands[0].Frequency);
        Assert.True(profiles.Save("voice", true));
        Assert.Null(profiles.Error);
        Assert.Equal(["voice", "Music", "Bright"], profiles.Profiles.Select(p => p.Name));
        Assert.Equal(80, profiles.Find("VOICE")!.Bands[0].Frequency);

        // Names: empty, too long and control characters are refused.
        foreach (string bad in new[] { "", "   ", new string('x', EqProfiles.MaximumNameLength + 1), "a\nb" })
        {
            Assert.False(profiles.Save(bad, false));
            Assert.Equal(Localizer.Format("EqProfileNameRule", EqProfiles.MaximumNameLength), profiles.Error);
        }
        Assert.Equal(3, profiles.Profiles.Count);

        // Rename: in place, a change of case alone, never onto another name.
        // Another editor that had it chosen keeps it chosen under the new name.
        var other = new EqProfilesViewModel(eq);
        other.Selected = other.Find("Music");
        Assert.True(profiles.Rename(profiles.Find("Music")!, "Singing"));
        Assert.Equal("Singing", profiles.Selected?.Name);
        Assert.Equal("Singing", other.Selected?.Name);
        other.Dispose();
        Assert.Equal(["voice", "Singing", "Bright"], profiles.Profiles.Select(p => p.Name));
        Assert.True(profiles.Rename(profiles.Find("voice")!, "Voice"));
        Assert.Equal(["Voice", "Singing", "Bright"], profiles.Profiles.Select(p => p.Name));
        Assert.False(profiles.Rename(profiles.Find("Voice")!, "BRIGHT"));
        Assert.Equal(Localizer.Format("EqProfileNameExists", "BRIGHT"), profiles.Error);
        Assert.Equal(80, profiles.Find("Voice")!.Bands[0].Frequency);

        // Delete: only that one, and a profile already gone says so.
        EqProfile singing = profiles.Find("Singing")!;
        Assert.True(profiles.Delete(singing));
        Assert.Equal(["Voice", "Bright"], profiles.Profiles.Select(p => p.Name));
        Assert.Equal("Voice", profiles.Selected?.Name);
        Assert.False(profiles.Delete(singing));
        Assert.Equal(Localizer.Format("EqProfileMissing", "Singing"), profiles.Error);
        Assert.False(profiles.Rename(singing, "Again"));
        Assert.Equal(Localizer.Format("EqProfileMissing", "Singing"), profiles.Error);
        Assert.Equal(["Voice", "Bright"], EqProfiles.Read().Profiles.Select(p => p.Name));
        profiles.Dispose();
        eq.Dispose();
    }

    [Fact]
    public void TheStoreKeepsAtMostSixtyFourProfiles()
    {
        var bands = MicEq.Default.Bands;
        for (int i = 0; i < EqProfiles.MaximumProfiles; i++) EqProfiles.Save($"P{i}", bands);
        var error = Assert.Throws<InvalidDataException>(() => EqProfiles.Save("One more", bands));
        Assert.Equal(Localizer.Format("EqProfileLimit", EqProfiles.MaximumProfiles), error.Message);
        // Replacing one is not another.
        EqProfiles.Save("p0", bands, replace: true);
        Assert.Equal(EqProfiles.MaximumProfiles, EqProfiles.Read().Profiles.Count);
    }

    [Fact]
    public void HandEditedValuesAreHeldToTheDaemonsRangesAndSnapped()
    {
        WriteFile("""
            {"version":1,"profiles":[{"name":" Edited ","bands":[
              {"enabled":true,"type":"lowCut","frequency":5,"gain":30,"q":50},
              {"enabled":false,"type":"highShelf","frequency":99999,"gain":-40,"q":0.001},
              {"enabled":true,"type":"peak","frequency":1000.4,"gain":1.1,"q":0.71}]}]}
            """);
        EqProfileList list = EqProfiles.Read();
        Assert.Equal(0, list.Unreadable);
        EqProfile profile = Assert.Single(list.Profiles);
        Assert.Equal("Edited", profile.Name);
        Assert.Equal([20d, 20000, 1000], profile.Bands.Select(b => b.Frequency));
        Assert.Equal([12d, -12, 1], profile.Bands.Select(b => b.Gain));
        Assert.Equal([MicEq.MaxQ, MicEq.MinQ, MicEq.SnapQ(0.71)], profile.Bands.Select(b => b.Q));
        Assert.All(profile.Bands, b => Assert.Null(MicEq.Check(b)));

        // What loads is what the daemon takes.
        var (eq, sent) = Model("cap-1", variable: true);
        var profiles = new EqProfilesViewModel(eq);
        profiles.Selected = profiles.Find("edited");
        Assert.True(profiles.Load());
        Assert.True(sent.Sent.TryDequeue(out var load));
        MicEq loaded = (MicEq)load.Value;
        Assert.Null(MicEq.Check(loaded));
        Assert.Equal(profile.Bands, loaded.Bands);
        profiles.Dispose();
        eq.Dispose();
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
    public void AnEntryThatDoesNotReadIsLeftOutAndTheFileIsNeverOverwritten(string entry)
    {
        // "good" repeats the first profile's name in another case.
        string text = "{\"version\":1,\"profiles\":[{\"name\":\"Good\",\"bands\":[" + Band + "]}," + entry + "]}";
        WriteFile(text);
        EqProfileList list = EqProfiles.Read();
        Assert.Equal("Good", Assert.Single(list.Profiles).Name);
        Assert.Equal(1, list.Unreadable);

        var (eq, sent) = Model("xlr1", variable: true);
        var profiles = new EqProfilesViewModel(eq);
        string note = Localizer.Format("EqProfileFileUnreadable", 1, EqProfiles.FilePath);
        Assert.Equal(note, profiles.Error);
        // The good one still loads.
        profiles.Selected = profiles.Profiles[0];
        Assert.True(profiles.Load());
        Assert.True(sent.Sent.TryDequeue(out _));
        Assert.Equal(note, profiles.Error);
        // Nothing writes over the file while it holds the entry.
        Assert.False(profiles.Save("New", false));
        Assert.Equal(note, profiles.Error);
        Assert.False(profiles.Rename(profiles.Profiles[0], "Renamed"));
        Assert.False(profiles.Delete(profiles.Profiles[0]));
        Assert.Equal(text, File.ReadAllText(EqProfiles.FilePath));
        profiles.Dispose();
        eq.Dispose();
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
        var error = Assert.Throws<InvalidDataException>(() => EqProfiles.Read());
        Assert.Equal(Localizer.Format("EqProfileFileDamaged", EqProfiles.FilePath), error.Message);
        var (eq, sent) = Model("xlr1");
        var profiles = new EqProfilesViewModel(eq);
        Assert.Empty(profiles.Profiles);
        Assert.True(profiles.HasError);
        Assert.DoesNotContain("\n", profiles.Error!, StringComparison.Ordinal);
        Assert.False(profiles.Save("New", false));
        Assert.True(profiles.HasError);
        Assert.Equal(text, File.ReadAllText(EqProfiles.FilePath));
        Assert.Empty(sent.Sent);
        profiles.Dispose();
        eq.Dispose();
    }

    [Fact]
    public void AnOversizedFileIsNotRead()
    {
        WriteFile("{\"version\":1,\"profiles\":[]}" + new string(' ', EqProfiles.MaximumFileBytes));
        var error = Assert.Throws<InvalidDataException>(() => EqProfiles.Read());
        Assert.Equal(Localizer.Format("EqProfileFileTooLarge", EqProfiles.FilePath), error.Message);
    }

    [Fact]
    public async Task APipeInPlaceOfTheFileCannotBlockTheEditor()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(EqProfiles.FilePath)!);
        var created = OpenXLR.Core.ProcessRunner.Run("mkfifo", [EqProfiles.FilePath]);
        Assert.True(created.Ok, created.Stderr);
        var (eq, _) = Model("xlr1");
        var made = Task.Run(() => new EqProfilesViewModel(eq));
        try
        {
            EqProfilesViewModel profiles = await made.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(profiles.Profiles);
            Assert.True(profiles.HasError);
            Assert.False(profiles.Save("New", false));
            profiles.Dispose();
        }
        finally
        {
            // Release an ordinary blocking open if the regression is present.
            using var keeper = new FileStream(EqProfiles.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            await Task.WhenAny(made, Task.Delay(TimeSpan.FromSeconds(3)));
        }
        eq.Dispose();
    }

    [Fact]
    public void AProfileWithMoreBandsThanTheEqTakesIsRefused()
    {
        var (variable, _) = Model("cap-1", variable: true);
        while (variable.CanAddBand && variable.Bands.Count < 6) variable.AddBand();
        Assert.Equal(6, variable.Bands.Count);
        var saving = new EqProfilesViewModel(variable);
        Assert.True(saving.Save("Six", false));

        // An older daemon's EQ takes four bands and no other count.
        var (fixedEq, sent) = Model("xlr1");
        var loading = new EqProfilesViewModel(fixedEq);
        loading.Selected = loading.Find("Six");
        Assert.False(loading.Load());
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Empty(sent.Sent);
        // The editor coming forward reads the file again and keeps the line; a change to the file clears it.
        loading.Reread();
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Equal("Six", loading.Selected?.Name);
        Assert.True(saving.Save("Other", false));
        Assert.Null(loading.Error);
        Assert.Equal(4, fixedEq.Bands.Count);
        saving.Dispose();
        loading.Dispose();
        variable.Dispose();
        fixedEq.Dispose();
    }

    [Fact]
    public void AClosedEditorStopsFollowingTheFile()
    {
        var (eq, _) = Model("xlr1");
        var open = new EqProfilesViewModel(eq);
        var closed = new EqProfilesViewModel(eq);
        closed.Dispose();
        Assert.True(open.Save("After", false));
        Assert.Single(open.Profiles);
        Assert.Empty(closed.Profiles);
        Assert.False(closed.Save("Closed", false));
        Assert.Single(EqProfiles.Read().Profiles);
        open.Dispose();
        eq.Dispose();
    }

    [Fact]
    public void BandsTheDaemonWouldRefuseAreNotSaved()
    {
        var error = Assert.Throws<InvalidDataException>(() => EqProfiles.Save("Bad", [new EqBand { Type = "bandPass" }]));
        Assert.Equal(Localizer.Text("EqProfileBandsInvalid"), error.Message);
        Assert.Throws<InvalidDataException>(() => EqProfiles.Save("Bad", []));
        Assert.False(File.Exists(EqProfiles.FilePath));
    }
}
