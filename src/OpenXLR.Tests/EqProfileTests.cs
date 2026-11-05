using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using Store = OpenXLR.Core.Mixing.EqProfileStore;
using StoreBand = OpenXLR.Core.Mixing.EqBand;

namespace OpenXLR.Tests;

/// <summary>
/// The EQ editor's profile row, with no window, against a daemon that keeps
/// the profiles in its own store: the built-in presets come first, named in
/// the UI's language and marked as built in, and load but are never renamed
/// or deleted; a profile saved from one microphone loads on another when
/// picked in the list, as one loadEqProfile, shown at once with the EQ on;
/// the list shows the profile the EQ holds, from any client, and the choices
/// it makes itself send nothing; a pick after an edit loads again, and Save
/// then offers the picked profile's name; names are unique whatever their
/// case, and a refusal says why in the UI's language before anything is
/// sent; the daemon's word on a file it cannot read whole shows as the row's
/// line and keeps every change from being sent; a profile with more bands
/// than the microphone's EQ takes is refused; every open editor lists the
/// same profiles and keeps a renamed profile chosen.
/// </summary>
[Collection("xdg-config")]
public sealed class EqProfileTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-eq-profiles-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public EqProfileTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }

    /// <summary>The daemon's side: its store, the state it pushes after each command, and the commands it heard.</summary>
    private sealed class Daemon : IEqProfileCommands, IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();
        public readonly EqProfileLibrary Library;
        public string? Refuse;

        public Daemon()
        {
            Library = new EqProfileLibrary(this);
            Push();
        }

        public void Push(bool reread = false)
        {
            var state = Store.State(reread);
            Library.Apply(JsonNode.Parse(JsonSerializer.Serialize(new { eqProfiles = state.Profiles, eqProfileProblem = state.Problem }, Web)));
        }

        private Task<string?> Run(string command, Action change)
        {
            Sent.Enqueue((command, "", ""));
            try
            {
                if (Refuse is { } refusal) return Task.FromResult<string?>(refusal);
                change();
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex) when (Store.IsFailure(ex)) { return Task.FromResult<string?>($"{command}: {ex.Message}"); }
            finally { Push(); }
        }

        public Task<string?> SaveEqProfileAsync(string name, IReadOnlyList<EqBand> bands, bool replace) => Run("saveEqProfile", () =>
            Store.Save(name, [.. bands.Select(b => new StoreBand { Enabled = b.Enabled, Type = b.Type, Frequency = b.Frequency, Gain = b.Gain, Q = b.Q })], replace));

        public Task<string?> RenameEqProfileAsync(string name, string newName) => Run("renameEqProfile", () => Store.Rename(name, newName));

        public Task<string?> DeleteEqProfileAsync(string name) => Run("deleteEqProfile", () => Store.Delete(name));

        public Task<string?> LoadEqProfileAsync(string channel, string profile)
        {
            Sent.Enqueue(("loadEqProfile", channel, profile));
            return Task.FromResult(Refuse);
        }

        public Task ListEqProfilesAsync()
        {
            Push(reread: true);
            return Task.CompletedTask;
        }

        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }
        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }
        public Task SetEqProcessingAsync(string channel, string value) { Sent.Enqueue(("setEqProcessing", channel, value)); return Task.CompletedTask; }
        public Task SetEqBandAsync(string channel, int band, EqBandChange change) { Sent.Enqueue(("setEqBand", channel, (band, change))); return Task.CompletedTask; }
    }

    private static MicEqViewModel Eq(Daemon daemon, string channel, bool variable = false, ManualTime? time = null)
    {
        var eq = new MicEqViewModel(daemon, channel, channel, TimeSpan.Zero, time ?? new ManualTime()) { ProfileLibrary = daemon.Library };
        if (variable)
        {
            eq.Apply(JsonNode.Parse("""{"enabled":false,"bands":[],"backend":null,"available":true,"error":null}""")!,
                JsonNode.Parse("""{"minBands":1,"maxBands":16}"""));
        }
        return eq;
    }

    private static void WriteFile(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!);
        File.WriteAllText(Store.FilePath, text);
    }

    private const string Band = """{"enabled":true,"type":"peak","frequency":1000,"gain":3,"q":0.7046930689671475}""";

    private static IEnumerable<EqProfile> Saved(EqProfilesViewModel profiles) => profiles.Profiles.Where(p => !p.Builtin);

    [Fact]
    public async Task AProfileSavedFromOneMicrophoneLoadsOnAnotherWhenPickedAsOneLoadEqProfileWithTheEqOn()
    {
        var daemon = new Daemon();
        using MicEqViewModel xlr1 = Eq(daemon, "xlr1"), xlr2 = Eq(daemon, "xlr2");
        xlr1.Enabled = true;
        xlr1.Bands[1].DragTo(1000, 6);
        xlr1.Bands[3].Enabled = true;
        daemon.Sent.Clear();
        using var saving = new EqProfilesViewModel(xlr1, daemon.Library);
        using var loading = new EqProfilesViewModel(xlr2, daemon.Library);
        Assert.Empty(Saved(saving));
        Assert.Null(saving.Selected);
        Assert.Empty(daemon.Sent);

        // The EQ holds what was just saved, so the list shows it.
        Assert.True(await saving.Save("  Podcast  ", replace: false));
        Assert.Equal("Podcast", Assert.Single(Saved(saving)).Name);
        Assert.Equal("Podcast", saving.Selected?.Name);
        Assert.Equal("saved:Podcast", saving.Selected?.Id);
        Assert.True(saving.HasSavedSelection);
        Assert.Null(saving.Error);
        Assert.Equal("saveEqProfile", Assert.Single(daemon.Sent).Command);
        daemon.Sent.Clear();
        // The other editor lists it too, without being asked, and its EQ holds none of the list.
        EqProfile listed = Assert.Single(Saved(loading));
        Assert.Equal(xlr1.Current.Bands, listed.Bands);
        Assert.Equal("Podcast", listed.DisplayName);
        Assert.Null(loading.Selected);
        Assert.Empty(daemon.Sent);

        // Picking it is the load: one loadEqProfile, shown at once with the EQ on.
        loading.Selected = listed;
        Assert.Equal(("loadEqProfile", "xlr2", (object)"saved:Podcast"), Assert.Single(daemon.Sent));
        Assert.Same(listed, loading.Selected);
        Assert.Null(loading.Error);
        Assert.True(xlr2.Enabled);
        Assert.Equal(1000, xlr2.Bands[1].Frequency);
        Assert.Equal(6, xlr2.Bands[1].Gain);
        // The same profile again is no pick: a list does not report its own choice.
        daemon.Sent.Clear();
        loading.Selected = listed;
        Assert.Empty(daemon.Sent);
        // Something not listed is not taken.
        loading.Selected = listed with { Name = "Other" };
        Assert.Same(listed, loading.Selected);
        Assert.Empty(daemon.Sent);
    }

    [Fact]
    public async Task TheBuiltInProfilesComeFirstAndLoadButKeepTheirNames()
    {
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "guest", variable: true);
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        Assert.True(await profiles.Save("Mine", false));
        Assert.Equal(MicSetupPresets.All.Select(p => EqProfileIds.Builtin(p.Id)).Append("saved:Mine"),
            profiles.Profiles.Select(p => p.Id));
        Assert.Equal(MicSetupPresets.All.Select(p => Localizer.Format("EqProfileBuiltInName", p.Name)).Append("Mine"),
            profiles.Profiles.Select(p => p.ToString()));
        // Mic setup's presets, the same table.
        Assert.Equal(MicSetupPresets.All.Select(p => p.Eq.Bands), profiles.Profiles.Take(MicSetupPresets.All.Count).Select(p => p.Bands), new BandsComparer());
        // Saved with the EQ off: an EQ that is off holds no profile, so the list stays on its placeholder.
        Assert.Null(profiles.Selected);

        EqProfile warm = profiles.Profiles[1];
        daemon.Sent.Clear();
        profiles.Selected = warm;
        Assert.Equal(("loadEqProfile", "guest", (object)"builtin:warm"), Assert.Single(daemon.Sent));
        Assert.True(MicSetupPresets.Warm.Holds(eq.Current, eq.Limits));
        Assert.Same(warm, profiles.Selected);
        Assert.False(profiles.HasSavedSelection);
        Assert.Equal("", profiles.SaveName);
        daemon.Sent.Clear();
        Assert.False(await profiles.Rename(warm, "Hot"));
        Assert.False(await profiles.Delete(warm));
        Assert.Empty(daemon.Sent);

        // A saved profile may share a built-in one's name; both stay listed, and the one just saved is chosen.
        Assert.True(await profiles.Save("Warm", false));
        Assert.Equal(2, profiles.Profiles.Count(p => p.Name == "Warm"));
        Assert.Equal("saved:Warm", profiles.Selected?.Id);
        Assert.Equal("Warm", profiles.SaveName);
    }

    /// <summary>A channel's <c>eq</c> as the daemon reports it, on and with these bands.</summary>
    private static JsonNode Report(IReadOnlyList<EqBand> bands, bool enabled = true)
        => JsonNode.Parse(JsonSerializer.Serialize(new { enabled, bands, backend = "software", available = true, error = (string?)null }, Web))!;

    [Fact]
    public async Task TheListShowsTheProfileTheEqHoldsAndItsOwnChoicesSendNothing()
    {
        var daemon = new Daemon();
        var time = new ManualTime();
        using MicEqViewModel eq = Eq(daemon, "xlr1", time: time);
        IReadOnlyList<EqBand> warm = EqPresets.Warm.Eq.Bands, broadcast = EqPresets.Broadcast.Eq.Bands;
        // Opened on an EQ that holds a profile: that profile is chosen.
        eq.Apply(Report(broadcast), null);
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        Assert.Equal("builtin:broadcast", profiles.Selected?.Id);
        Assert.Empty(daemon.Sent);

        // A load from elsewhere (the deck, another client): the list follows, and sends nothing.
        eq.Apply(Report(warm), null);
        Assert.Equal("builtin:warm", profiles.Selected?.Id);
        Assert.Empty(daemon.Sent);

        // An edit here leaves the profile: the list goes back to its placeholder and only the edit is sent.
        eq.Bands[1].SetGain(5);
        Assert.Null(profiles.Selected);
        Assert.False(profiles.HasSavedSelection);
        Assert.All(daemon.Sent, s => Assert.Equal("setEqBand", s.Command));
        daemon.Sent.Clear();
        time.Advance(MicEqViewModel.EchoWait);

        // Another client puts Warm back, then edits a band itself.
        eq.Apply(Report(warm), null);
        Assert.Equal("builtin:warm", profiles.Selected?.Id);
        eq.Apply(Report([.. warm.Select((b, i) => i == 2 ? b with { Frequency = 3100 } : b)]), null);
        Assert.Null(profiles.Selected);
        // A value that came back a little off the grid still counts.
        eq.Apply(Report([.. warm.Select((b, i) => i == 0 ? b with { Frequency = b.Frequency + 0.25 } : b)]), null);
        Assert.Equal("builtin:warm", profiles.Selected?.Id);

        // A comparison keeps the choice; the switch off clears it, and on again the profile is back.
        eq.Comparing = true;
        Assert.Equal("builtin:warm", profiles.Selected?.Id);
        eq.Comparing = false;
        eq.Enabled = false;
        Assert.Null(profiles.Selected);
        eq.Enabled = true;
        Assert.Equal("builtin:warm", profiles.Selected?.Id);
        // A whole EQ put on here, as Paste does without the shared clipboard, and Reset: the list follows.
        eq.Replace(new MicEq { Enabled = true, Bands = broadcast });
        Assert.Equal("builtin:broadcast", profiles.Selected?.Id);
        eq.Reset();
        Assert.Null(profiles.Selected);
        Assert.DoesNotContain(daemon.Sent, s => s.Command == "loadEqProfile");

        // A refresh of the list, for a change any client made, keeps what the EQ holds and loads nothing.
        eq.Replace(new MicEq { Enabled = true, Bands = broadcast });
        daemon.Sent.Clear();
        Store.Save("Elsewhere", [new StoreBand { Enabled = true, Frequency = 500 }]);
        daemon.Push();
        await daemon.ListEqProfilesAsync();
        profiles.Reread();
        Assert.Equal("builtin:broadcast", profiles.Selected?.Id);
        Assert.Equal(MicSetupPresets.All.Count + 1, profiles.Profiles.Count);
        Assert.Empty(daemon.Sent);
    }

    [Fact]
    public async Task APickAfterAnEditLoadsAgainAndSaveOffersThePickedName()
    {
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "xlr1");
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        eq.Enabled = true;
        Assert.True(await profiles.Save("Voice", false));
        eq.Bands[0].SetFrequency(80);
        Assert.Null(profiles.Selected);
        Assert.True(await profiles.Save("Music", false));
        Assert.Equal("Music", profiles.Selected?.Name);
        daemon.Sent.Clear();

        // Pick Voice: it loads once and stays chosen.
        EqProfile voice = profiles.Find("Voice")!;
        profiles.Selected = voice;
        Assert.Equal(("loadEqProfile", "xlr1", (object)"saved:Voice"), Assert.Single(daemon.Sent));
        Assert.Same(voice, profiles.Selected);
        Assert.Equal(36, eq.Bands[0].Frequency);
        Assert.Equal("Voice", profiles.SaveName);
        daemon.Sent.Clear();

        // A tweak clears the choice, and Save offers to replace the profile it started from.
        eq.Bands[1].SetGain(4);
        Assert.Null(profiles.Selected);
        Assert.Equal("Voice", profiles.SaveName);
        // Picking Voice again loads it again.
        daemon.Sent.Clear();
        profiles.Selected = voice;
        Assert.Equal(("loadEqProfile", "xlr1", (object)"saved:Voice"), Assert.Single(daemon.Sent));
        Assert.Same(voice, profiles.Selected);
        Assert.Equal(0, eq.Bands[1].Gain);

        // A built-in pick is not offered for replacing.
        daemon.Sent.Clear();
        profiles.Selected = profiles.Profiles[0];
        Assert.Equal("builtin:clear", Assert.Single(daemon.Sent).Value);
        eq.Bands[1].SetGain(4);
        Assert.Equal("", profiles.SaveName);

        // What the daemon refuses shows as the line.
        daemon.Refuse = "loadEqProfile: the microphone is gone";
        profiles.Selected = voice;
        Assert.Equal("loadEqProfile: the microphone is gone", profiles.Error);
    }

    private sealed class BandsComparer : IEqualityComparer<IReadOnlyList<EqBand>>
    {
        public bool Equals(IReadOnlyList<EqBand>? a, IReadOnlyList<EqBand>? b) => a!.SequenceEqual(b!);
        public int GetHashCode(IReadOnlyList<EqBand> bands) => bands.Count;
    }

    [Fact]
    public async Task NamesAreUniqueAndARefusalSaysWhyBeforeAnythingIsSent()
    {
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "xlr1");
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        Assert.True(await profiles.Save("Voice", false));
        Assert.True(await profiles.Save("Music", false));
        Assert.True(await profiles.Save("Bright", false));
        daemon.Sent.Clear();

        // A name in use is refused unless replacing, whatever its case.
        eq.Bands[0].SetFrequency(80);
        Assert.False(await profiles.Save("voice", false));
        Assert.Equal(Localizer.Format("EqProfileNameExists", "Voice"), profiles.Error);
        Assert.Equal(36, profiles.Find("Voice")!.Bands[0].Frequency);
        foreach (string bad in new[] { "", "   ", new string('x', EqProfileLibrary.MaximumNameLength + 1), "a\nb" })
        {
            Assert.False(await profiles.Save(bad, false));
            Assert.Equal(Localizer.Format("EqProfileNameRule", EqProfileLibrary.MaximumNameLength), profiles.Error);
        }
        Assert.DoesNotContain(daemon.Sent, s => s.Command != "setEqBand");
        Assert.True(await profiles.Save("voice", true));
        Assert.Null(profiles.Error);
        Assert.Equal(["voice", "Music", "Bright"], Saved(profiles).Select(p => p.Name));
        Assert.Equal(80, profiles.Find("VOICE")!.Bands[0].Frequency);

        // Rename: in place, a change of case alone, never onto another name.
        // Another editor that had it chosen keeps it chosen under the new name, and loads nothing for it.
        using MicEqViewModel eq2 = Eq(daemon, "xlr2");
        var other = new EqProfilesViewModel(eq2, daemon.Library);
        other.Selected = other.Find("Music");
        Assert.Equal("Music", other.Selected?.Name);
        daemon.Sent.Clear();
        Assert.True(await profiles.Rename(profiles.Find("Music")!, "Singing"));
        Assert.Equal("Singing", other.Selected?.Name);
        Assert.Equal("Singing", other.SaveName);
        Assert.Equal("renameEqProfile", Assert.Single(daemon.Sent).Command);
        other.Dispose();
        Assert.Equal(["voice", "Singing", "Bright"], Saved(profiles).Select(p => p.Name));
        Assert.True(await profiles.Rename(profiles.Find("voice")!, "Voice"));
        Assert.Equal(["Voice", "Singing", "Bright"], Saved(profiles).Select(p => p.Name));
        Assert.False(await profiles.Rename(profiles.Find("Voice")!, "BRIGHT"));
        Assert.Equal(Localizer.Format("EqProfileNameExists", "BRIGHT"), profiles.Error);

        // Delete: only that one, and a profile already gone says so.
        EqProfile singing = profiles.Find("Singing")!;
        profiles.Selected = profiles.Find("Voice");
        Assert.True(await profiles.Delete(singing));
        Assert.Equal(["Voice", "Bright"], Saved(profiles).Select(p => p.Name));
        Assert.Equal("Voice", profiles.Selected?.Name);
        Assert.False(await profiles.Delete(singing));
        Assert.Equal(Localizer.Format("EqProfileMissing", "Singing"), profiles.Error);
        Assert.False(await profiles.Rename(singing, "Again"));
        Assert.Equal(Localizer.Format("EqProfileMissing", "Singing"), profiles.Error);
        Assert.Equal(["Voice", "Bright"], Store.Read().Profiles.Select(p => p.Name));

        // What the daemon refuses shows as the line.
        daemon.Refuse = "deleteEqProfile: the disk is full";
        Assert.False(await profiles.Delete(profiles.Find("Bright")!));
        Assert.Equal("deleteEqProfile: the disk is full", profiles.Error);
    }

    [Fact]
    public async Task SixtyFourSavedProfilesAreTheMost()
    {
        for (int i = 0; i < EqProfileLibrary.MaximumProfiles; i++) Store.Save($"P{i}", OpenXLR.Core.Mixing.MicEq.Default.Bands);
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "xlr1");
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        Assert.False(await profiles.Save("One more", false));
        Assert.Equal(Localizer.Format("EqProfileLimit", EqProfileLibrary.MaximumProfiles), profiles.Error);
        Assert.Empty(daemon.Sent);
        // Replacing one is not another.
        Assert.True(await profiles.Save("p0", true));
        Assert.Equal(EqProfileLibrary.MaximumProfiles, Saved(profiles).Count());
    }

    [Fact]
    public async Task AFileTheDaemonCannotReadWholeIsSaidAndNothingIsSent()
    {
        string text = "{\"version\":1,\"profiles\":[{\"name\":\"Good\",\"bands\":[" + Band + "]},{\"name\":\"Broken\"}]}";
        WriteFile(text);
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "xlr1", variable: true);
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        string note = Localizer.Format("EqProfileFileUnreadable", 1, Store.FilePath);
        Assert.Equal(note, profiles.Error);
        // The good one still loads.
        profiles.Selected = profiles.Find("Good");
        Assert.True(daemon.Sent.TryDequeue(out var load));
        Assert.Equal("loadEqProfile", load.Command);
        Assert.Equal("Good", profiles.Selected?.Name);
        Assert.Equal(note, profiles.Error);
        // Nothing is asked of the daemon while the file holds the entry.
        Assert.False(await profiles.Save("New", false));
        Assert.Equal(note, profiles.Error);
        Assert.False(await profiles.Rename(profiles.Find("Good")!, "Renamed"));
        Assert.False(await profiles.Delete(profiles.Find("Good")!));
        Assert.Empty(daemon.Sent);
        Assert.Equal(text, File.ReadAllText(Store.FilePath));

        // A file that is not a profile list: the built-in ones stay, the line says why in one line.
        WriteFile("not json");
        await daemon.ListEqProfilesAsync();
        Assert.Equal(Localizer.Format("EqProfileFileDamaged", Store.FilePath), profiles.Error);
        Assert.DoesNotContain("\n", profiles.Error!, StringComparison.Ordinal);
        Assert.Equal(MicSetupPresets.All.Count, profiles.Profiles.Count);
        Assert.Null(profiles.Selected);
        Assert.False(await profiles.Save("New", false));
        Assert.Equal("not json", File.ReadAllText(Store.FilePath));

        // Fixed by hand: the editor coming forward asks the daemon to read it again.
        File.Delete(Store.FilePath);
        profiles.Reread();
        Assert.Null(profiles.Error);
    }

    [Fact]
    public async Task AProfileWithMoreBandsThanTheEqTakesIsRefused()
    {
        var daemon = new Daemon();
        using MicEqViewModel variable = Eq(daemon, "cap-1", variable: true);
        while (variable.CanAddBand && variable.Bands.Count < 6) variable.AddBand();
        Assert.Equal(6, variable.Bands.Count);
        using var saving = new EqProfilesViewModel(variable, daemon.Library);
        Assert.True(await saving.Save("Six", false));

        // An older daemon's EQ takes four bands and no other count.
        using MicEqViewModel fixedEq = Eq(daemon, "xlr1");
        using var loading = new EqProfilesViewModel(fixedEq, daemon.Library);
        daemon.Sent.Clear();
        loading.Selected = loading.Find("Six");
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Empty(daemon.Sent);
        // The editor coming forward keeps the line and the pick; a change to the list clears both,
        // since the EQ does not hold that profile.
        loading.Reread();
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Equal("Six", loading.Selected?.Name);
        Assert.True(await saving.Save("Other", false));
        Assert.Null(loading.Error);
        Assert.Null(loading.Selected);
        Assert.Equal(4, fixedEq.Bands.Count);
        Assert.DoesNotContain(daemon.Sent, s => s.Command == "loadEqProfile");
    }

    [Fact]
    public async Task AClosedEditorStopsFollowingTheList()
    {
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "xlr1");
        using var open = new EqProfilesViewModel(eq, daemon.Library);
        var closed = new EqProfilesViewModel(eq, daemon.Library);
        closed.Dispose();
        Assert.True(await open.Save("After", false));
        Assert.Single(Saved(open));
        Assert.Empty(Saved(closed));
        Assert.False(await closed.Save("Closed", false));
        Assert.Single(Store.Read().Profiles);
    }

    [Fact]
    public void AnOlderDaemonListsNothing()
    {
        var library = new EqProfileLibrary(new Daemon());
        library.Apply(JsonNode.Parse("""{"type":"state"}"""));
        Assert.Empty(library.Profiles);
        Assert.Null(library.Problem);
    }
}
