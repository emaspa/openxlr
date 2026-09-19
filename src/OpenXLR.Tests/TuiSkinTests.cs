using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// What the terminal mixer makes of a skin: the colours it reads out of the
/// same files the window reads, where it looks for them, and the one setting
/// it writes back. None of this needs a terminal.
/// </summary>
[Collection("xdg-config")]
public sealed class TuiSkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-tui-" + Guid.NewGuid());
    private readonly string? _oldDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _oldDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
    private readonly string? _oldConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldDataHome);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", _oldDataDirs);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _oldConfigHome);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Skin(string where, string id, string json)
    {
        string folder = Path.Combine(_root, where, "openxlr", "skins", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    private static string Doc(string tokens, string name = "Test") =>
        "{\"schema\":1,\"name\":\"" + name + "\",\"tokens\":{" + tokens + "}}";

    // --- colours ---

    [Theory]
    [InlineData("#abc", 0xaa, 0xbb, 0xcc)]
    [InlineData("#1d2027", 0x1d, 0x20, 0x27)]
    [InlineData("#cc1d2027", 0x1d, 0x20, 0x27)]   // the alpha is dropped: a cell has no transparency
    [InlineData("black", 0, 0, 0)]
    public void ColourIsReadFromEveryFormTheSkinFormatAllows(string text, int r, int g, int b)
    {
        Rgb? colour = Rgb.TryParse(text);
        Assert.NotNull(colour);
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), colour.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("rebeccapurple")]
    public void AColourThatIsNotOneIsRefusedRatherThanGuessed(string text) =>
        Assert.Null(Rgb.TryParse(text));

    // --- the palette ---

    [Fact]
    public void ASkinPaintsTheSurfacesTheTextAndTheIndicators()
    {
        Theme theme = Theme.FromJson(Doc("""
            "Ox.Window.Background": "#101010",
            "Ox.Card.Background": "#202020",
            "Ox.Text.Primary": "#fefefe",
            "Ox.Led.On": "#00ff00",
            "Ox.Meter.Hot": "#ff0000",
            "Ox.Accent": "#0000ff"
            """), "mine", "Mine");

        Assert.Equal("mine", theme.Id);
        Assert.Equal("Test", theme.Name);
        Assert.Equal(Rgb.Parse("#101010"), theme.Window);
        Assert.Equal(Rgb.Parse("#202020"), theme.Card);
        Assert.Equal(Rgb.Parse("#fefefe"), theme.TextPrimary);
        Assert.Equal(Rgb.Parse("#00ff00"), theme.LedOn);
        Assert.Equal(Rgb.Parse("#ff0000"), theme.MeterHot);
        Assert.Equal(Rgb.Parse("#0000ff"), theme.Accent);
    }

    [Fact]
    public void AGradientIsTakenAtItsFirstStopBecauseACellHoldsOneColour()
    {
        Theme theme = Theme.FromJson(Doc("""
            "Ox.Window.Background": {
              "type": "linear", "from": [0,0], "to": [0,1],
              "stops": [{"offset":0,"color":"#262626"},{"offset":1,"color":"#151515"}]
            },
            "Ox.Card.Background": {"type":"solid","color":"#333333"}
            """), "mine", "Mine");

        Assert.Equal(Rgb.Parse("#262626"), theme.Window);
        Assert.Equal(Rgb.Parse("#333333"), theme.Card);
    }

    [Fact]
    public void ATokenThisVersionCannotUseLeavesTheShippedValueAlone()
    {
        Theme theme = Theme.FromJson(Doc("""
            "Ox.Card.Background": "not a colour",
            "Ox.Card.CornerRadius": 10,
            "Ox.Tile.Background": {"type":"image","source":"faceplate.png"},
            "Ox.Nothing.AtAll": "#ffffff"
            """), "mine", "Mine");

        Assert.Equal(Theme.Material.Card, theme.Card);
        Assert.Equal(Theme.Material.Tile, theme.Tile);
    }

    [Fact]
    public void AFileThatIsNotJsonKeepsTheShippedAppearance()
    {
        Theme theme = Theme.FromJson("{ this is not json", "mine", "Mine");
        Assert.Equal(Theme.Material.Window, theme.Window);
        Assert.Equal("Mine", theme.Name);
    }

    [Fact]
    public void TheMeterIsColouredByThePlaceOnTheScaleNotByTheReading()
    {
        Theme theme = Theme.FromJson(Doc("""
            "Ox.Meter.Fill": "#00ff00",
            "Ox.Meter.Warning": "#ffff00",
            "Ox.Meter.Hot": "#ff0000",
            "Ox.Meter.WarningLevel": 0.7,
            "Ox.Meter.HotLevel": 0.9
            """), "mine", "Mine");

        Assert.Equal(Rgb.Parse("#00ff00"), theme.MeterColour(0.10));
        Assert.Equal(Rgb.Parse("#00ff00"), theme.MeterColour(0.69));
        Assert.Equal(Rgb.Parse("#ffff00"), theme.MeterColour(0.70));
        Assert.Equal(Rgb.Parse("#ff0000"), theme.MeterColour(0.95));
    }

    [Fact]
    public void TheMeterZonesCannotCross()
    {
        Theme theme = Theme.FromJson(Doc("""
            "Ox.Meter.WarningLevel": 0.8,
            "Ox.Meter.HotLevel": 0.2
            """), "mine", "Mine");

        Assert.Equal(theme.MeterWarningLevel, theme.MeterHotLevel);
    }

    // --- where skins come from ---

    [Theory]
    [InlineData("tokyo-night", true)]
    [InlineData("a1.b_c-d", true)]
    [InlineData("Gruvbox", false)]      // upper case is not an id
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    public void AnIdIsLowerCaseAndStartsWithALetterOrADigit(string id, bool valid) =>
        Assert.Equal(valid, SkinCatalog.ValidId(id));

    [Fact]
    public void SkinsAreFoundWhereTheWindowLooksAndMaterialIsAlwaysFirst()
    {
        Skin("home", "mine", Doc("""  "Ox.Window.Background": "#010101" """, "Mine"));
        Skin("system", "theirs", Doc("""  "Ox.Window.Background": "#020202" """, "Theirs"));
        Skin("home", "NotAnId", Doc("", "Ignored"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));

        IReadOnlyList<Tui.SkinEntry> skins = SkinCatalog.Scan();

        Assert.Equal("default", skins[0].Id);
        Assert.Contains(skins, skin => skin.Id == "mine" && skin.Name == "Mine");
        Assert.Contains(skins, skin => skin.Id == "theirs");
        Assert.DoesNotContain(skins, skin => skin.Id == "NotAnId");
        // The appearances the application ships with are there without a file.
        Assert.Contains(skins, skin => skin.Id == "opendeck" && skin.Name == "Deck");
    }

    [Fact]
    public void YourOwnCopyShadowsTheSystemOne()
    {
        Skin("home", "shared", Doc("""  "Ox.Window.Background": "#010101" """, "Yours"));
        Skin("system", "shared", Doc("""  "Ox.Window.Background": "#020202" """, "Theirs"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));

        Theme theme = SkinCatalog.Load("shared");

        Assert.Equal("Yours", theme.Name);
        Assert.Equal(Rgb.Parse("#010101"), theme.Window);
    }

    [Fact]
    public void ASkinThatIsNotThereLeavesTheShippedAppearanceOn()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));

        Assert.Equal("default", SkinCatalog.Load("nothing-like-it").Id);
        Assert.Equal("default", SkinCatalog.Load(null).Id);
    }

    // --- the saved choice ---

    [Fact]
    public void ChoosingASkinKeepsEverythingElseTheWindowSaved()
    {
        string config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        File.WriteAllText(Path.Combine(config, "openxlr", "ui.json"),
            """{"startDaemonAtLogin":true,"collapsedSections":["HeadphonesTile"],"skin":"opendeck"}""");

        Assert.Equal("opendeck", UiSettingsFile.ReadSkin());
        Assert.True(UiSettingsFile.WriteSkin("gruvbox"));

        string written = File.ReadAllText(Path.Combine(config, "openxlr", "ui.json"));
        Assert.Equal("gruvbox", UiSettingsFile.ReadSkin());
        Assert.Contains("startDaemonAtLogin", written, StringComparison.Ordinal);
        Assert.Contains("HeadphonesTile", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWithNoSavedChoiceIsNotAnError()
    {
        string config = Path.Combine(_root, "empty-config");
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);

        Assert.Null(UiSettingsFile.ReadSkin());
        Assert.True(UiSettingsFile.WriteSkin("nord"));
        Assert.Equal("nord", UiSettingsFile.ReadSkin());
    }

    // --- the skins this repository ships as examples ---

    [Fact]
    public void TheExampleSkinsReadIntoAPaletteThatIsNotTheDefaultOne()
    {
        string examples = Path.Combine(Root(), "docs", "examples", "skins");
        string[] folders = Directory.GetDirectories(examples);
        Assert.True(folders.Length > 1, "the examples folder holds only one skin");

        foreach (string folder in folders)
        {
            string id = Path.GetFileName(folder);
            Theme theme = Theme.FromJson(File.ReadAllText(Path.Combine(folder, "skin.json")), id, id);
            Assert.NotEqual(Theme.Material.Window, theme.Window);
            Assert.NotEqual(Theme.Material.TextPrimary, theme.TextPrimary);
            // A meter that never changes colour wastes the scale, and every
            // example here sets its three zones.
            if (id != "example")
                Assert.NotEqual(theme.MeterFill, theme.MeterHot);
        }
    }

    /// <summary>The repository root, found from the test assembly.</summary>
    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
