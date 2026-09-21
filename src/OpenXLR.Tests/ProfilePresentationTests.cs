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
        => InFakeMixer(mixer =>
        {
            mixer.ApplyScene(new() { Appearance = new() { ["channel:gone"] = new("♫"), ["channel:music"] = new("◆") } });
            Assert.Equal("channel:music", Assert.Single(mixer.ExportSettings().Appearance).Key);
            mixer.ApplyScene(new() { Appearance = [] });
            Assert.Empty(mixer.ExportSettings().Appearance);
        });

    [Fact]
    public void ProfileRecallsAppearanceWithoutChangingRoutingAndLegacyScenesPreserveIt()
        => InFakeMixer(mixer =>
        {
            var config = MixerConfig.Default();
            mixer.SetDisplayOrder(config.Channels.Reverse().Select(c => c.Id).ToArray(),
                config.Mixes.Reverse().Select(m => m.Id).ToArray(), _ => null);
            mixer.SetLayoutAppearance("channel:game", new("♫", "#123456", true), _ => null);
            string expected = JsonSerializer.Serialize(mixer.ExportSettings().Appearance);
            var scene = mixer.ExportScene();
            mixer.SetLayoutAppearance("channel:game", new("◆"), _ => null);
            mixer.ApplyScene(scene);
            Assert.Equal(expected, JsonSerializer.Serialize(mixer.ExportSettings().Appearance));
            mixer.ApplyScene(new());
            Assert.Equal(expected, JsonSerializer.Serialize(mixer.ExportSettings().Appearance));
            Assert.Equal(config.Channels.Where(c => c.InputPair is null).Select(c => c.Id), mixer.ExportSettings().UserChannels!.Select(c => c.Id));
        });

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

    private static void InFakeMixer(Action<Mixer> run)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-presentation-helpers-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                case "$1" in
                  set-sink-volume|set-sink-mute) exit 0 ;;
                  *) exit 99 ;;
                esac
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), "printf '[]\n'\n");
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                case "$1" in
                  -o|-i) exit 0 ;;
                  *) exit 99 ;;
                esac
                """);
            // Never reach the desktop's helpers, whether installed or not.
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer();
            var built = typeof(Mixer).GetField("_built", BindingFlags.NonPublic | BindingFlags.Instance)!;
            built.SetValue(mixer, true);
            try { run(mixer); }
            finally { built.SetValue(mixer, false); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(directory, true);
        }
    }

    private static void InConfig(Action run)
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-presentation-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir); run(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(dir, true); }
    }
}
