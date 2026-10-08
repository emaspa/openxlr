using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// The window's preference files: keys from a newer version survive a save,
/// a null list reads as empty, and a file that cannot be read is never
/// written over.
/// </summary>
[Collection("xdg-config")]
public sealed class PreferenceFileTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "openxlr-prefs-" + Guid.NewGuid());
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public PreferenceFileTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _home);
        Directory.CreateDirectory(UiSettings.ConfigDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_home, true);
    }

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");
    private static string DaemonJson => Path.Combine(UiSettings.ConfigDir, "daemon.json");

    private static OptionsViewModel Options()
    {
        var client = new DaemonClient();
        return new OptionsViewModel(client, new MainViewModel(client));
    }

    [Fact]
    public void UnknownUiKeysSurviveALoadAndSave()
    {
        File.WriteAllText(UiJson, """
            { "skin": "nord", "futureChoice": { "mode": "dark", "sizes": [1, 2] }, "language": "de" }
            """);

        Assert.Null((UiSettings.Load() with { MinimizeToTray = true }).Save());

        JsonObject saved = JsonNode.Parse(File.ReadAllText(UiJson))!.AsObject();
        Assert.Equal("nord", (string?)saved["skin"]);
        Assert.True((bool?)saved["minimizeToTray"]);
        Assert.Equal("de", (string?)saved["language"]);
        Assert.Equal("""{"mode":"dark","sizes":[1,2]}""", saved["futureChoice"]!.ToJsonString());
    }

    [Fact]
    public void UnknownDaemonKeysSurviveBothCopiesOfTheRecord()
    {
        File.WriteAllText(DaemonJson, """{ "submixer": true, "futureSwitch": 3 }""");

        Assert.Null((DaemonPrefs.Load() with { Submixer = false }).Save());
        JsonObject window = JsonNode.Parse(File.ReadAllText(DaemonJson))!.AsObject();
        Assert.False((bool?)window["submixer"]);
        Assert.Equal(3, (int?)window["futureSwitch"]);

        (DaemonSettings.Load() with { Submixer = true }).Save();
        JsonObject daemon = JsonNode.Parse(File.ReadAllText(DaemonJson))!.AsObject();
        Assert.True((bool?)daemon["submixer"]);
        Assert.Equal(3, (int?)daemon["futureSwitch"]);
    }

    [Fact]
    public void NullListLoadsAsEmpty()
    {
        File.WriteAllText(UiJson, """{ "collapsedSections": null, "minimizeToTray": true }""");

        UiSettings loaded = UiSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.NotNull(loaded.CollapsedSections);
        Assert.Empty(loaded.CollapsedSections);
        Assert.True(loaded.MinimizeToTray);
    }

    [Theory]
    [InlineData("{ \"minimizeToTray\": tru")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"skin\": 5 }")]
    [InlineData("{ \"later\": 1, \"later\": 2 }")]
    public async Task UnreadableUiFileIsLeftForTheUserToRepair(string text)
    {
        File.WriteAllText(UiJson, text);

        UiSettings loaded = UiSettings.Load(out string? problem);
        Assert.Equal(new UiSettings().MinimizeToTray, loaded.MinimizeToTray);
        Assert.Contains("ui.json", problem);

        // Each of these loads, changes one field and saves.
        var updates = new UpdatesViewModel(_ => Task.FromResult(
            new UpdateResult(true, "v9.9.9", "Update available", "", null)));
        await updates.CheckAsync(manual: true);
        updates.DismissBanner();
        Assert.NotNull((UiSettings.Load() with { StartMinimized = true }).Save());

        Assert.Equal(text, File.ReadAllText(UiJson));
    }

    [Fact]
    public void UnreadableDaemonFileIsLeftInPlace()
    {
        File.WriteAllText(DaemonJson, "{ \"submixer\": ");

        DaemonPrefs loaded = DaemonPrefs.Load(out string? problem);

        Assert.Null(loaded.Submixer);
        Assert.Contains("daemon.json", problem);
        Assert.NotNull((loaded with { Submixer = false }).Save());
        Assert.Equal("{ \"submixer\": ", File.ReadAllText(DaemonJson));
    }

    [Fact]
    public void OptionsShowsAnUnreadableFileOnceAndClearsItAfterARepair()
    {
        File.WriteAllText(UiJson, "{ broken");
        OptionsViewModel options = Options();
        string? shown = options.PreferenceError;
        Assert.Contains("ui.json", shown);
        Assert.Contains("left as it is", shown);

        var raised = 0;
        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptionsViewModel.PreferenceError)) raised++;
        };

        // Two changes against the same broken file: both apply for the run,
        // neither touches the file, and the line is not raised again.
        options.MinimizeToTray = true;
        options.StartMinimized = true;
        Assert.True(options.MinimizeToTray);
        Assert.True(options.Main.MinimizeToTray);
        Assert.True(options.StartMinimized);
        Assert.Equal(shown, options.PreferenceError);
        Assert.Equal(0, raised);
        Assert.Equal("{ broken", File.ReadAllText(UiJson));

        File.WriteAllText(UiJson, """{ "skin": "nord" }""");
        options.StartMinimized = false;

        Assert.Null(options.PreferenceError);
        Assert.Equal(1, raised);
        UiSettings saved = UiSettings.Load();
        Assert.Equal("nord", saved.Skin);
        Assert.True(saved.MinimizeToTray);
        Assert.False(saved.StartMinimized);
    }

    [Fact]
    public void OptionsShowsAFailedWriteInItsStatusLine()
    {
        // A directory where the file belongs reads as no file, and the
        // atomic rename onto it fails.
        Directory.CreateDirectory(UiJson);
        OptionsViewModel options = Options();
        Assert.Null(options.PreferenceError);

        options.StartMinimized = true;

        Assert.True(options.StartMinimized);
        Assert.StartsWith("Could not save ui.json.", options.PreferenceError);
        Assert.True(Directory.Exists(UiJson));
    }
}
