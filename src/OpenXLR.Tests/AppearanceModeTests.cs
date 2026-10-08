using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;
using Modes = OpenXLR.UI.AppearanceModes;

namespace OpenXLR.Tests;

/// <summary>
/// Material's saved mode outside a running window: how ui.json carries it,
/// what Options does when it cannot be saved, and the light palette's colours.
/// </summary>
[Collection("xdg-config")]
public sealed class AppearanceModeTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("openxlr-appearance-").FullName;
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public AppearanceModeTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);

    public void Dispose()
    {
        SkinService.ApplyMode(Modes.System, new SkinEntry(SkinPackage.Default, []));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfig);
        Directory.Delete(_config, true);
    }

    private static string UiJson => Path.Combine(UiSettings.ConfigDir, "ui.json");

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("unknown", "system")]
    [InlineData("LIGHT", "system")]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("system", "system")]
    public void BothClientsReadTheSameModesAndFollowTheDesktopOtherwise(string? input, string expected)
    {
        Assert.Equal(expected, Modes.Normalize(input));
        Assert.Equal(expected, OpenXLR.Tui.AppearanceModes.Normalize(input));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"nested\":[true]}")]
    [InlineData("\"unknown\"")]
    public void ADamagedModeReadsAsSystemAndKeepsTheOtherPreferences(string mode)
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        File.WriteAllText(UiJson,
            "{\"startMinimized\":true,\"appearanceMode\":" + mode + ",\"skin\":\"nord\",\"minimizeToTray\":true}");

        UiSettings settings = UiSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.Equal(Modes.System, settings.AppearanceMode);
        Assert.True(settings.StartMinimized);
        Assert.True(settings.MinimizeToTray);
        Assert.Equal("nord", settings.Skin);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void TheModeRoundTripsThroughUiJsonBesideTheSkin(string mode)
    {
        Assert.Null(new UiSettings { Skin = "nord", AppearanceMode = mode }.Save());

        Assert.Equal(mode, (string?)JsonNode.Parse(File.ReadAllText(UiJson))!["appearanceMode"]);
        UiSettings loaded = UiSettings.Load();
        Assert.Equal(mode, loaded.AppearanceMode);
        Assert.Equal("nord", loaded.Skin);
    }

    [Fact]
    public void ChoosingAModeKeepsKeysThisVersionDoesNotKnow()
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        File.WriteAllText(UiJson, """{ "skin": "nord", "futureChoice": { "density": "touch" } }""");

        SkinService.ChooseMode(Modes.Light, out string? saveError);

        Assert.Null(saveError);
        JsonObject saved = JsonNode.Parse(File.ReadAllText(UiJson))!.AsObject();
        Assert.Equal("light", (string?)saved["appearanceMode"]);
        Assert.Equal("nord", (string?)saved["skin"]);
        Assert.Equal("""{"density":"touch"}""", saved["futureChoice"]!.ToJsonString());
        Assert.Throws<ArgumentException>(() => SkinService.ChooseMode("sepia", out _));
    }

    [Fact]
    public void AModeThatCannotBeSavedIsStillWornAndOptionsSaysWhy()
    {
        Directory.CreateDirectory(UiSettings.ConfigDir);
        File.WriteAllText(UiJson, "{ broken");
        var client = new DaemonClient();
        var options = new OptionsViewModel(client, new MainViewModel(client));
        Assert.Equal(Modes.System, options.SelectedAppearanceMode!.Id);

        options.SelectedAppearanceMode = options.AppearanceModeChoices.Single(c => c.Id == Modes.Dark);

        Assert.Equal(Modes.Dark, SkinService.Mode);
        Assert.Equal(Modes.Dark, options.SelectedAppearanceMode.Id);
        Assert.Contains("ui.json", options.PreferenceError);
        Assert.Equal("{ broken", File.ReadAllText(UiJson));

        // A repaired file takes the next choice and clears the line.
        File.WriteAllText(UiJson, "{}");
        options.SelectedAppearanceMode = options.AppearanceModeChoices.Single(c => c.Id == Modes.Light);
        Assert.Null(options.PreferenceError);
        Assert.Equal(Modes.Light, UiSettings.Load().AppearanceMode);
    }

    [Fact]
    public void TheLightPaletteIsSkinDataWithoutAPickerEntry()
    {
        SkinEntry light = SkinService.MaterialLight;
        Assert.Empty(light.Errors);
        Assert.NotEmpty(light.Package.Tokens);
        Assert.All(light.Package.Tokens.Keys, token => Assert.NotNull(SkinTokens.Find(token)));
        Assert.All(light.Package.Tokens.Values, value => Assert.IsType<SkinSolid>(value));
        Assert.Empty(light.Package.Controls);
        Assert.DoesNotContain(SkinCatalog.BuiltIn(), entry => entry.Id == "material-light");
        Assert.DoesNotContain(SkinCatalog.Discover(), entry => entry.Id == "material-light");
    }
}
