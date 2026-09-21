using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;
using Presentation = OpenXLR.Core.WindowPresentation;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ProfilePresentationTests
{
    [Theory]
    [InlineData("{\"collapsedSections\":null}")]
    [InlineData("{\"sectionOrder\":[null]}")]
    [InlineData("{\"sectionOrder\":[\"MonitorTile\",\"MonitorTile\"]}")]
    [InlineData("{\"skin\":\"bad\\nname\"}")]
    [InlineData("{\"compactChannel\":\"\"}")]
    public void InvalidPresentationRejectsTheWholeProfile(string json)
    {
        InConfig(() =>
        {
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(Path.Combine(UiSettings.ConfigDir, "profiles", "test", "bad.json"),
                "{\"device\":{\"gainDb\":75},\"presentation\":" + json + "}");
            Assert.Throws<JsonException>(() => ProfileStore.Load("test", "bad"));
        });
    }

    [Fact]
    public void PresentationIsBoundedAndRoundTripsIncludingUnknownSections()
    {
        var presentation = new Presentation { CompactMixer = true, CompactChannel = "music", Skin = "deck",
            SectionOrder = ["SubmixerTile", "FutureTile", "MonitorTile"], CollapsedSections = ["InputsTile"] };
        presentation.Validate();
        Assert.Throws<JsonException>(() => (presentation with { SectionOrder = Enumerable.Range(0, 17).Select(i => "Tile" + i).ToArray() }).Validate());
        Assert.Throws<JsonException>(() => (presentation with { Skin = new string('x', 65) }).Validate());
        Assert.Throws<JsonException>(() => (presentation with { CompactChannel = new string('x', 37) }).Validate());
        InConfig(() =>
        {
            ProfileStore.Save("test", "scene", new() { Presentation = presentation });
            Assert.Equal(JsonSerializer.Serialize(presentation), JsonSerializer.Serialize(ProfileStore.Load("test", "scene")!.Presentation));
        });
    }

    [Theory]
    [InlineData("{\"mix:monitor\":{\"hidden\":true}}")]
    [InlineData("{\"channel:game\":null}")]
    [InlineData("{\"channel:game\":{\"colour\":\"red\"}}")]
    [InlineData("{\"channel:game\":{\"order\":-1}}")]
    [InlineData("{\"other:game\":{}}")]
    public void InvalidAppearanceCannotPartiallyChangeAScene(string json)
    {
        using var mixer = new Mixer();
        var before = JsonSerializer.Serialize(mixer.ExportSettings());
        var scene = JsonSerializer.Deserialize<MixerScene>("{\"mixVolumes\":{\"monitor\":0.1},\"appearance\":" + json + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<JsonException>(() => mixer.ApplyScene(scene));
        Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
    }

    [Fact]
    public void EmptyAppearanceClearsAndDeletedTargetsAreNotResurrected()
    {
        using var mixer = new Mixer();
        typeof(Mixer).GetField("_built", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mixer, true);
        try
        {
            mixer.ApplyScene(new() { Appearance = new() { ["channel:gone"] = new("♫"), ["channel:music"] = new("◆") } });
            Assert.Equal("channel:music", Assert.Single(mixer.ExportSettings().Appearance).Key);
            mixer.ApplyScene(new() { Appearance = [] });
            Assert.Empty(mixer.ExportSettings().Appearance);
        }
        finally { typeof(Mixer).GetField("_built", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(mixer, false); }
    }

    [Fact]
    public void RecallPersistsOnlyPresentationAndDoesNotReplayAfterManualEditsOrRestart()
    {
        InConfig(() =>
        {
            new UiSettings { StartDaemonAtLogin = true, MinimizeToTray = true, CheckForUpdates = true }.Save();
            var client = new DaemonClient();
            var vm = new MainViewModel(client);
            int recalled = 0;
            vm.PresentationRecalled += () => recalled++;
            var state = Recall("deck");
            Apply(vm, state);
            Assert.True(vm.CompactMixer);
            Assert.Equal(1, recalled);
            var saved = UiSettings.Load();
            Assert.True(saved.StartDaemonAtLogin);
            Assert.True(saved.MinimizeToTray);
            Assert.True(saved.CheckForUpdates);
            Assert.Equal("music", saved.CompactChannel);
            Assert.Equal(new[] { "SubmixerTile", "MonitorTile" }, saved.SectionOrder);
            Assert.Equal(new[] { "InputsTile" }, saved.CollapsedSections);
            Assert.Equal("deck", saved.Skin);
            (saved with { Skin = "classic", CompactMixer = false }).Save();
            Apply(vm, state);
            Apply(new MainViewModel(client), state);
            Assert.Equal(1, recalled);
            Assert.Equal("classic", UiSettings.Load().Skin);
            Assert.False(UiSettings.Load().CompactMixer);
            state["revision"] = Guid.NewGuid().ToString("N");
            Apply(vm, state);
            Assert.Equal(2, recalled);
            Assert.Equal("deck", UiSettings.Load().Skin);
            Assert.True(vm.CompactMixer);
            Apply(vm, null); // legacy profile leaves presentation alone
            Assert.Equal("deck", UiSettings.Load().Skin);
        });
    }

    [Fact]
    public void FailedPresentationWritesDoNotChangeTheVisibleChoiceOrReportSuccess()
    {
        InConfig(() =>
        {
            Directory.CreateDirectory(Path.Combine(UiSettings.ConfigDir, "ui.json"));
            var client = new DaemonClient();
            var vm = new MainViewModel(client);
            var options = new OptionsViewModel(client, vm);
            var original = options.SelectedSkin;
            string originalSkin = OpenXLR.UI.Skinning.SkinService.Current.Id;
            options.SelectedSkin = options.SkinChoices.First(c => c.Id != originalSkin);
            Assert.Same(original, options.SelectedSkin);
            Assert.Equal(originalSkin, OpenXLR.UI.Skinning.SkinService.Current.Id);
            Assert.Contains("could not be saved", options.SkinError);
            int recalled = 0;
            vm.PresentationRecalled += () => recalled++;
            vm.CompactMixer = true;
            Assert.False(vm.CompactMixer);
            Assert.Contains("could not be saved", vm.Status);
            Apply(vm, Recall("deck"));
            Assert.False(vm.CompactMixer);
            Assert.Equal(0, recalled);
            Assert.Null(UiSettings.Load().AppliedPresentation);
            Assert.Contains("could not be restored", vm.Status);
        });
    }

    private static JsonNode Recall(string skin) => JsonSerializer.SerializeToNode(new
    {
        revision = Guid.NewGuid().ToString("N"), settings = new { compactMixer = true, compactChannel = "music", skin,
            sectionOrder = new[] { "SubmixerTile", "MonitorTile" }, collapsedSections = new[] { "InputsTile" } },
    })!;

    private static void Apply(MainViewModel vm, JsonNode? state) => typeof(MainViewModel)
        .GetMethod("ApplyProfilePresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [state]);

    private static void InConfig(Action run)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-presentation-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir); run(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(dir, true); }
    }
}
