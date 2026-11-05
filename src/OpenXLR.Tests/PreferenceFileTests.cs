using System.Text;
using System.Text.Json;
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
    private const int PreferenceLimit = OpenXLR.Core.OpenXlrPaths.MaxPreferenceBytes;
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

    [Fact]
    public void WelcomeGuideStaysPendingAcrossPreferenceSavesUntilDismissed()
    {
        Assert.False(File.Exists(UiJson));
        Assert.True(UiSettings.PrepareWelcomeGuide());
        Assert.False(UiSettings.Load().WelcomeGuideCompleted);
        Assert.Null((UiSettings.Load() with { StartMinimized = true }).Save());
        Assert.True(UiSettings.PrepareWelcomeGuide());

        Assert.Null((UiSettings.Load() with { WelcomeGuideCompleted = true }).Save());
        Assert.False(UiSettings.PrepareWelcomeGuide());
        Assert.True(UiSettings.Load().StartMinimized);
    }

    [Theory]
    [InlineData("""{"startMinimized":true,"futureSetting":13}""")]
    [InlineData("""{"welcomeGuideCompleted":true,"dismissedUpdate":"0.1.0","futureSetting":13}""")]
    [InlineData("""{"startMinimized":""")]
    public void WelcomeGuideDoesNotTreatExistingPreferencesAsAFirstLaunch(string text)
    {
        File.WriteAllText(UiJson, text);
        Assert.False(UiSettings.PrepareWelcomeGuide());
        Assert.Equal(text, File.ReadAllText(UiJson));
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(105)]
    [InlineData(int.MaxValue)]
    public void InvalidTextSizesKeepTheNormalSizeAndOtherPreferences(int percent)
    {
        Assert.Null(new UiSettings { TextSizePercent = percent, StartMinimized = true }.Save());
        UiSettings saved = UiSettings.Load();
        Assert.Equal(100, saved.TextSizePercent);
        Assert.True(saved.StartMinimized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DismissingAnUpdateWhileCheckingKeepsOnlyThatReleaseHidden(bool newerRelease)
    {
        var pending = new TaskCompletionSource<UpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        const string dismissed = "v9.9.9";
        var updates = new UpdatesViewModel(_ => ++checks == 1
            ? Task.FromResult(new UpdateResult(true, dismissed, "Update available", "", null))
            : pending.Task);
        await updates.CheckAsync(manual: true);
        Assert.True(updates.BannerVisible);

        Task checking = updates.CheckAsync(manual: true);
        Assert.True(updates.Busy);
        updates.DismissBanner();
        Assert.False(updates.BannerVisible);
        Assert.Null((UiSettings.Load() with { StartMinimized = true }).Save());
        string reported = newerRelease ? "v9.9.10" : dismissed;
        pending.SetResult(new UpdateResult(true, reported, "Update available", "", null));
        await checking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(updates.Busy);
        Assert.Equal(newerRelease, updates.BannerVisible);
        UiSettings saved = UiSettings.Load();
        Assert.Equal(dismissed, saved.DismissedUpdate);
        Assert.True(saved.StartMinimized);
    }

    private static OptionsViewModel Options()
    {
        var client = new DaemonClient();
        return new OptionsViewModel(client, new MainViewModel(client));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedPreferenceFilesAreLeftForTheUserToRepair(bool daemon)
    {
        string path = daemon ? DaemonJson : UiJson;
        string text = "{\"startMinimized\":true,\"submixer\":true}" + new string(' ', PreferenceLimit);
        File.WriteAllText(path, text);

        string? problem;
        if (daemon)
        {
            DaemonPrefs settings = DaemonPrefs.Load(out problem);
            Assert.Null(settings.Submixer);
            Assert.NotNull((settings with { Submixer = false }).Save());
        }
        else
        {
            UiSettings settings = UiSettings.Load(out problem);
            Assert.False(settings.StartMinimized);
            Assert.NotNull((settings with { StartMinimized = true }).Save());
        }
        Assert.Contains(Path.GetFileName(path), problem);
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedPreferenceWritesKeepTheExistingFile(bool daemon)
    {
        string path = daemon ? DaemonJson : UiJson;
        const string original = "{\"future\":true}";
        File.WriteAllText(path, original);
        var unknown = new Dictionary<string, JsonElement>
        {
            ["future"] = JsonSerializer.SerializeToElement(new string('x', PreferenceLimit)),
        };

        string? problem = daemon
            ? new DaemonPrefs { UnknownKeys = unknown }.Save()
            : new UiSettings { UnknownKeys = unknown }.Save();

        Assert.Contains(Path.GetFileName(path), problem);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void TheDaemonAlsoRejectsOversizedPreferencesWithoutChangingTheFile()
    {
        string text = "{\"submixer\":true}" + new string(' ', PreferenceLimit);
        File.WriteAllText(DaemonJson, text);

        Assert.Null(DaemonSettings.Read(out string? problem));
        Assert.NotNull(problem);
        Assert.Equal(text, File.ReadAllText(DaemonJson));
    }

    [Fact]
    public void OversizedDaemonWritesKeepTheExistingFile()
    {
        const string original = "{\"future\":true}";
        File.WriteAllText(DaemonJson, original);
        var settings = new DaemonSettings
        {
            UnknownKeys = new() { ["future"] = JsonSerializer.SerializeToElement(new string('x', PreferenceLimit)) },
        };

        Assert.Throws<IOException>(settings.Save);
        Assert.Equal(original, File.ReadAllText(DaemonJson));
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("window-daemon")]
    [InlineData("daemon")]
    public void TheReadLimitIsInclusiveAndMissingFilesAndLegacyEncodingsStillLoad(string kind)
    {
        Assert.Equal(kind == "ui" ? false : (bool?)null, ReadChoice(kind, out string? problem));
        Assert.Null(problem);
        string path = kind == "ui" ? UiJson : DaemonJson;
        const string json = "{\"submixer\":true,\"startMinimized\":true}";
        File.WriteAllText(path, json.PadRight(PreferenceLimit));
        Assert.True(ReadChoice(kind, out problem));
        Assert.Null(problem);
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
        {
            File.WriteAllText(path, json, encoding);
            Assert.True(ReadChoice(kind, out problem));
            Assert.Null(problem);
        }
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("window-daemon")]
    [InlineData("daemon")]
    public void PreferencesAtTheWriteLimitRemainReadable(string kind)
    {
        string path = kind == "ui" ? UiJson : DaemonJson;
        Action<Dictionary<string, JsonElement>> save = kind switch
        {
            "ui" => keys => Assert.Null(new UiSettings { UnknownKeys = keys }.Save()),
            "window-daemon" => keys => Assert.Null(new DaemonPrefs { UnknownKeys = keys }.Save()),
            _ => keys => new DaemonSettings { UnknownKeys = keys }.Save(),
        };
        var unknown = new Dictionary<string, JsonElement> { ["padding"] = JsonSerializer.SerializeToElement("") };
        save(unknown);
        int overhead = checked((int)new FileInfo(path).Length);
        unknown["padding"] = JsonSerializer.SerializeToElement(new string('x', PreferenceLimit - overhead));

        save(unknown);

        Assert.Equal(PreferenceLimit, new FileInfo(path).Length);
        ReadChoice(kind, out string? problem);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("window-daemon")]
    [InlineData("daemon")]
    public async Task PipesCannotBlockPreferenceReads(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = kind == "ui" ? UiJson : DaemonJson;
        var created = OpenXLR.Core.ProcessRunner.Run("mkfifo", [path]);
        Assert.True(created.Ok, created.Stderr);
        using var keeper = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var read = Task.Run(() => { ReadChoice(kind, out string? problem); return problem; });
        try { Assert.NotNull(await read.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { keeper.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(File.Exists(path));
    }

    private static bool? ReadChoice(string kind, out string? problem) => kind switch
    {
        "ui" => UiSettings.Load(out problem).StartMinimized,
        "window-daemon" => DaemonPrefs.Load(out problem).Submixer,
        _ => DaemonSettings.Read(out problem)?.Submixer,
    };

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
