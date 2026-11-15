using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// mixer.json survives what happens to it on disk: a hand edit with one wrong
/// value, a newer release's keys, a save cut short by a power loss.
/// </summary>
[Collection("xdg-config")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MixerSettingsFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-mixer-file-").FullName;
    private string SettingsPath => Path.Combine(_directory, "mixer.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AWrongTypedEntryIsDroppedAndTheRestOfTheFileApplies()
    {
        File.WriteAllText(SettingsPath, """
            {
              "levels": {"music|stream": "0.5", "music|chat": 0.25},
              "mixVolumes": {"stream": 0.75},
              "mixMuted": ["chat", 3],
              "monitorMuted": "yes",
              "appOverrides": {"firefox": "music"},
              "userChannels": [{"id": "music", "name": "Music"}]
            }
            """);
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out string? warning));
        Assert.Equal(0.25, Assert.Single(loaded.Levels, p => p.Key == "music|chat").Value);
        Assert.False(loaded.Levels.ContainsKey("music|stream"));
        Assert.Equal(0.75, loaded.MixVolumes["stream"]);
        Assert.Equal(["chat"], loaded.MixMuted);
        Assert.False(loaded.MonitorMuted);
        Assert.Equal("music", loaded.AppOverrides["firefox"]);
        Assert.Equal("music", Assert.Single(loaded.UserChannels!).Id);
        Assert.Contains("levels.music|stream", warning);
        Assert.Contains("mixMuted[1]", warning);
        Assert.Contains("monitorMuted", warning);
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void AHandEditWithATrailingCommaOrACommentStillLoads()
    {
        File.WriteAllText(SettingsPath, """
            {
              // turned down by hand
              "mixVolumes": {"stream": 0.5,},
            }
            """);
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out string? warning));
        Assert.Null(warning);
        Assert.Equal(0.5, loaded.MixVolumes["stream"]);
    }

    [Fact]
    public void AKeyFromANewerReleaseSurvivesALoadAndASave()
    {
        File.WriteAllText(SettingsPath, """
            {"version": 99, "futureSetting": {"depth": [1, 2]}, "mixVolumes": {"stream": 0.5}}
            """);
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out _));
        Assert.Null((loaded with { MixVolumes = new() { ["stream"] = 0.25 } }).Save(SettingsPath));
        AssertKeepsFutureSetting();

        // Through the live mixer too, which rebuilds the settings from its own state on every save.
        using var mixer = new Mixer();
        mixer.ApplySettings(Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out _)));
        Assert.Null(mixer.ExportSettings().Save(SettingsPath));
        AssertKeepsFutureSetting();
    }

    private void AssertKeepsFutureSetting()
    {
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        JsonElement future = saved.RootElement.GetProperty("futureSetting");
        Assert.Equal([1, 2], future.GetProperty("depth").EnumerateArray().Select(e => e.GetInt32()));
        // The version names the format this release wrote, not the one it read.
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"mixVolumes\":{\"stream\":0.")]
    public void AnEmptyOrTruncatedFileFallsBackToTheBackup(string broken)
    {
        File.WriteAllText(SettingsPath + ".bak", "{\"mixVolumes\":{\"stream\":0.4}}");
        File.WriteAllText(SettingsPath, broken);
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out string? warning));
        Assert.Equal(0.4, loaded.MixVolumes["stream"]);
        Assert.Contains(SettingsPath + ".bak", warning);
        // The broken file is still set aside, as before.
        Assert.Equal(broken, File.ReadAllText(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void AMissingFileFallsBackToTheBackup()
    {
        File.WriteAllText(SettingsPath + ".bak", "{\"mixVolumes\":{\"stream\":0.4}}");
        MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(SettingsPath, out string? warning));
        Assert.Equal(0.4, loaded.MixVolumes["stream"]);
        Assert.Contains(SettingsPath + ".bak", warning);
    }

    [Fact]
    public void NoFileAndNoBackupIsAFirstStart()
    {
        Assert.Null(MixerSettings.Load(SettingsPath, out string? warning));
        Assert.Null(warning);
    }

    [Fact]
    public void ASaveKeepsThePreviousFileAsTheBackup()
    {
        Assert.Null(new MixerSettings { MixVolumes = new() { ["stream"] = 0.1 } }.Save(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".bak"));
        byte[] first = File.ReadAllBytes(SettingsPath);
        Assert.Null(new MixerSettings { MixVolumes = new() { ["stream"] = 0.2 } }.Save(SettingsPath));
        Assert.Equal(first, File.ReadAllBytes(SettingsPath + ".bak"));
        Assert.Equal(0.2, MixerSettings.Load(SettingsPath, out _)!.MixVolumes["stream"]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath + ".bak"));
    }

    [Fact]
    public void ABrokenFileNeverReplacesTheBackup()
    {
        File.WriteAllText(SettingsPath + ".bak", "{\"mixVolumes\":{\"stream\":0.4}}");
        File.WriteAllText(SettingsPath, "{broken");
        Assert.Null(new MixerSettings { MixVolumes = new() { ["stream"] = 0.2 } }.Save(SettingsPath));
        Assert.Equal("{\"mixVolumes\":{\"stream\":0.4}}", File.ReadAllText(SettingsPath + ".bak"));
    }
}
