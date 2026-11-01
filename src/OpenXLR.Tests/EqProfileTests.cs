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
/// or deleted; a profile saved from one microphone loads on another as one
/// loadEqProfile, shown at once with the EQ on; names are unique whatever
/// their case, and a refusal says why in the UI's language before anything
/// is sent; the daemon's word on a file it cannot read whole shows as the
/// row's line and keeps every change from being sent; a profile with more
/// bands than the microphone's EQ takes is refused; every open editor lists
/// the same profiles and keeps a renamed profile chosen.
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

    private static MicEqViewModel Eq(Daemon daemon, string channel, bool variable = false)
    {
        var eq = new MicEqViewModel(daemon, channel, channel, TimeSpan.Zero, new ManualTime()) { ProfileLibrary = daemon.Library };
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
    public async Task AProfileSavedFromOneMicrophoneLoadsOnAnotherAsOneLoadEqProfileWithTheEqOn()
    {
        var daemon = new Daemon();
        using MicEqViewModel xlr1 = Eq(daemon, "xlr1"), xlr2 = Eq(daemon, "xlr2");
        xlr1.Bands[1].DragTo(1000, 6);
        xlr1.Bands[3].Enabled = true;
        daemon.Sent.Clear();
        using var saving = new EqProfilesViewModel(xlr1, daemon.Library);
        using var loading = new EqProfilesViewModel(xlr2, daemon.Library);
        Assert.Empty(Saved(saving));
        Assert.False(saving.HasSelection);

        Assert.True(await saving.Save("  Podcast  ", replace: false));
        Assert.Equal("Podcast", Assert.Single(Saved(saving)).Name);
        Assert.Equal("Podcast", saving.Selected?.Name);
        Assert.Equal("saved:Podcast", saving.Selected?.Id);
        Assert.True(saving.HasSavedSelection);
        Assert.Null(saving.Error);
        Assert.True(daemon.Sent.TryDequeue(out var save));
        Assert.Equal("saveEqProfile", save.Command);
        // The other editor lists it too, without being asked.
        EqProfile listed = Assert.Single(Saved(loading));
        Assert.Equal(xlr1.Current.Bands, listed.Bands);
        Assert.Equal("Podcast", listed.DisplayName);

        // Loading off nothing chosen does nothing.
        Assert.False(await loading.Load());
        Assert.Empty(daemon.Sent);
        loading.Selected = listed;
        Assert.True(loading.HasSelection);
        Assert.True(await loading.Load());
        Assert.True(daemon.Sent.TryDequeue(out var load));
        Assert.Equal(("loadEqProfile", "xlr2", (object)"saved:Podcast"), load);
        Assert.Empty(daemon.Sent);
        // Shown at once, with the EQ on.
        Assert.True(xlr2.Enabled);
        Assert.Equal(1000, xlr2.Bands[1].Frequency);
        Assert.Equal(6, xlr2.Bands[1].Gain);
    }

    [Fact]
    public async Task TheBuiltInProfilesComeFirstAndLoadButKeepTheirNames()
    {
        var daemon = new Daemon();
        using MicEqViewModel eq = Eq(daemon, "guest", variable: true);
        using var profiles = new EqProfilesViewModel(eq, daemon.Library);
        Assert.True(await profiles.Save("Mine", false));
        Assert.Equal(["builtin:clear", "builtin:warm", "builtin:broadcast", "saved:Mine"], profiles.Profiles.Select(p => p.Id));
        Assert.Equal([Localizer.Format("EqProfileBuiltInName", Localizer.Text("MicSetupPresetClear")),
            Localizer.Format("EqProfileBuiltInName", Localizer.Text("MicSetupPresetWarm")),
            Localizer.Format("EqProfileBuiltInName", Localizer.Text("MicSetupPresetBroadcast")), "Mine"],
            profiles.Profiles.Select(p => p.ToString()));
        // Mic setup's presets, the same table.
        Assert.Equal(MicSetupPresets.All.Select(p => p.Eq.Bands), profiles.Profiles.Take(3).Select(p => p.Bands), new BandsComparer());

        EqProfile warm = profiles.Profiles[1];
        profiles.Selected = warm;
        Assert.True(profiles.HasSelection);
        Assert.False(profiles.HasSavedSelection);
        daemon.Sent.Clear();
        Assert.False(await profiles.Rename(warm, "Hot"));
        Assert.False(await profiles.Delete(warm));
        Assert.Empty(daemon.Sent);
        Assert.True(await profiles.Load());
        Assert.Equal(("loadEqProfile", "guest", (object)"builtin:warm"), Assert.Single(daemon.Sent));
        Assert.True(MicSetupPresets.Warm.Holds(eq.Current, eq.Limits));

        // A saved profile may share a built-in one's name; both stay listed.
        Assert.True(await profiles.Save("Warm", false));
        Assert.Equal(2, profiles.Profiles.Count(p => p.Name == "Warm"));
        Assert.Equal("saved:Warm", profiles.Selected?.Id);
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
        // Another editor that had it chosen keeps it chosen under the new name.
        var other = new EqProfilesViewModel(eq, daemon.Library);
        other.Selected = other.Find("Music");
        Assert.True(await profiles.Rename(profiles.Find("Music")!, "Singing"));
        Assert.Equal("Singing", other.Selected?.Name);
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
        Assert.True(await profiles.Load());
        Assert.True(daemon.Sent.TryDequeue(out _));
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
        Assert.Equal(3, profiles.Profiles.Count);
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
        loading.Selected = loading.Find("Six");
        daemon.Sent.Clear();
        Assert.False(await loading.Load());
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Empty(daemon.Sent);
        // The editor coming forward keeps the line; a change to the list clears it.
        loading.Reread();
        Assert.Equal(Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4), loading.Error);
        Assert.Equal("Six", loading.Selected?.Name);
        Assert.True(await saving.Save("Other", false));
        Assert.Null(loading.Error);
        Assert.Equal(4, fixedEq.Bands.Count);
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
