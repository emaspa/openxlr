using System.Text.Json;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// How ui.json carries the mixer's control sizing outside a running window.
/// The window itself is checked in <see cref="TouchControlsWindowTests"/>.
/// </summary>
[Collection("xdg-config")]
public sealed class TouchControlsTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("openxlr-touch-").FullName;
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public TouchControlsTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_config, true);
    }

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"touch\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ADamagedSizingReadsAsStandardAndKeepsTheOtherPreferences(string value)
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        File.WriteAllText(UiJson,
            "{\"touchControls\":" + value + ",\"minimizeToTray\":true,\"skin\":\"deck\",\"appearanceMode\":\"light\"}");

        UiSettings settings = UiSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.False(settings.TouchControls);
        Assert.True(settings.MinimizeToTray);
        Assert.Equal("deck", settings.Skin);
        Assert.Equal(AppearanceModes.Light, settings.AppearanceMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSizingIsSavedBesideKeysThisVersionDoesNotKnow(bool touch)
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        File.WriteAllText(UiJson, """{"futureChoice":{"density":"wide"},"skin":"nord"}""");

        Assert.Null((UiSettings.Load() with { TouchControls = touch }).Save());

        Assert.Equal(touch, UiSettings.Load().TouchControls);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(UiJson));
        Assert.Equal(touch, saved.RootElement.GetProperty("touchControls").GetBoolean());
        Assert.Equal("wide", saved.RootElement.GetProperty("futureChoice").GetProperty("density").GetString());
        Assert.Equal("nord", saved.RootElement.GetProperty("skin").GetString());
    }

    [Fact]
    public void NoSavedSizingIsStandard()
        => Assert.False(UiSettings.Load().TouchControls);
}
