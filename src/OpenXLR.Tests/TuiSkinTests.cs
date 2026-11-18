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

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void UnsupportedSchemasDoNotApplyNamesControlsOrColours(string schema)
    {
        string document = "{\"schema\":" + schema + ",\"name\":\"Future skin\",\"controls\":{\"fader\":\"console\"},\"tokens\":{\"Ox.Card.Background\":\"#123456\"}}";
        Theme theme = Theme.FromJson(document, "future", "Future");
        Assert.Equal("Future", theme.Name);
        Assert.Equal(Theme.Material.Card, theme.Card);
        Assert.False(theme.ConsoleFaders);
    }

    [Fact]
    public void ADocumentWithoutASchemaDoesNotApplyItsTokens()
    {
        Theme theme = Theme.FromJson("""{"tokens":{"Ox.Card.Background":"#123456"}}""", "missing", "Missing");
        Assert.Equal(Theme.Material.Card, theme.Card);
    }

    /// <summary>
    /// The desktop layout block is the window's business. The terminal reads
    /// the same palette and controls with it present, whether the window
    /// would take the layout, refuse it, or not understand its version.
    /// </summary>
    [Theory]
    [InlineData("""{"version":1,"main":{"kind":"stack","id":"row","orientation":"horizontal","spacing":12,"children":[{"kind":"region","region":"submixer"},{"kind":"region","region":"inputs"},{"kind":"region","region":"monitor"},{"kind":"region","region":"headphones"},{"kind":"region","region":"applications"}]}}""")]
    [InlineData("""{"version":1,"main":{"kind":"grid","id":"desktop","rows":["auto","auto"],"columns":[{"size":600},{"star":1}],"children":[{"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},{"kind":"region","region":"inputs","row":1,"column":0},{"kind":"dock","id":"right","row":1,"column":1,"children":[{"kind":"region","region":"monitor","dock":"top"},{"kind":"region","region":"headphones","dock":"left"},{"kind":"region","region":"applications","dock":"bottom"},{"kind":"region","region":"submixer","dock":"fill"}]}]},"regions":{"applications":{"visibility":"hidden"},"header":{"overflow":["about"]}}}""")]
    [InlineData("""{"version":2,"main":{"kind":"grid","rows":["auto"]}}""")]
    [InlineData("""{"version":1,"main":{"kind":"stack","orientation":"diagonal"}}""")]
    [InlineData("""[]""")]
    public void ALayoutBlockLeavesTheTerminalPaletteAlone(string layout)
    {
        string document = "{\"schema\":1,\"name\":\"Laid out\",\"controls\":{\"fader\":\"console\"},"
            + "\"tokens\":{\"Ox.Card.Background\":\"#123456\"},\"layout\":" + layout + "}";
        Theme theme = Theme.FromJson(document, "laid-out", "Fallback");
        Assert.Equal("Laid out", theme.Name);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
        Assert.True(theme.ConsoleFaders);
        Assert.Equal(Theme.Material.Window, theme.Window);

        // Installed the way a user installs one, it is listed and loads the same.
        Skin("home", "laid-out", document);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        Assert.Single(SkinCatalog.Scan(), skin => skin.Id == "laid-out");
        Theme installed = SkinCatalog.Load("laid-out");
        Assert.Equal("Laid out", installed.Name);
        Assert.Equal(Rgb.Parse("#123456"), installed.Card);
        Assert.True(installed.ConsoleFaders);
    }

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

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AWrongNameTypeDoesNotStopHealthyTokensFromLoading(string name)
    {
        Theme theme = Theme.FromJson("{\"schema\":1,\"name\":" + name + ",\"tokens\":{\"Ox.Card.Background\":\"#123456\"}}", "mine", "Mine");
        Assert.Equal("Mine", theme.Name);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
    }

    [Theory]
    [InlineData("{\"color\":42}")]
    [InlineData("{\"color\":{}}")]
    [InlineData("{\"stops\":[42]}")]
    [InlineData("{\"stops\":[\"#ffffff\"]}")]
    [InlineData("{\"stops\":[{\"color\":true}]}")]
    [InlineData("{\"stops\":[{\"color\":[]}]}")]
    public void AMalformedColourKeepsItsFallbackAndOtherTokens(string colour)
    {
        Theme theme = Theme.FromJson(Doc("\"Ox.Window.Background\":" + colour + ",\"Ox.Card.Background\":\"#123456\""), "mine", "Mine");
        Assert.Equal(Theme.Material.Window, theme.Window);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
    }

    [Fact]
    public void ASelectedMalformedSkinDoesNotCrashTheCatalogueLoader()
    {
        Skin("home", "mistyped", """
            {"schema":1,"name":42,"tokens":{"Ox.Window.Background":{"stops":[false]},"Ox.Card.Background":"#123456"}}
            """);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        Theme theme = SkinCatalog.Load("mistyped");
        Assert.Equal("mistyped", theme.Name);
        Assert.Equal(Theme.Material.Window, theme.Window);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
    }

    [Fact]
    public void DuplicateFieldsUseTheLastValueWithoutBreakingTheCatalogue()
    {
        Skin("home", "duplicate", """
            {"schema":1,"name":"Old","name":"New","tokens":{"Ox.Card.Background":"#ffffff","Ox.Card.Background":"#123456"}}
            """);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        Theme theme = SkinCatalog.Load("duplicate");
        Assert.Equal("New", theme.Name);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
    }

    [Fact]
    public void LoadedNamesKeepTheCatalogueLengthAndControlBounds()
    {
        string name = "\u001b" + new string('x', 500) + "\n";
        string json = System.Text.Json.JsonSerializer.Serialize(new { schema = 1, name });
        Theme theme = Theme.FromJson(json, "mine", "Mine");
        Assert.Equal(new string('x', 400), theme.Name);
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
        Assert.Equal(Rgb.Parse("#00ff00"), theme.MeterColour(0.35));
        Assert.Equal(Rgb.Parse("#ffff00"), theme.MeterColour(0.70));
        Assert.Equal(Rgb.Parse("#ff0000"), theme.MeterColour(0.90));
        Assert.Equal(Rgb.Parse("#ff0000"), theme.MeterColour(0.95));
        // Between the anchors the colour runs from one to the next.
        Rgb lime = theme.MeterColour(0.525);
        Assert.Equal((255, 0), (lime.G, lime.B));
        Assert.InRange(lime.R, 120, 136);
        Rgb orange = theme.MeterColour(0.80);
        Assert.Equal((255, 0), (orange.R, orange.B));
        Assert.InRange(orange.G, 120, 136);
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

    /// <summary>
    /// A skin.json that is a pipe, or a link to a device that never ends, is
    /// skipped as an oversized one is, without waiting on the open or reading
    /// without end, and the good skins beside it are still listed.
    /// </summary>
    [Fact]
    public async Task ASkinDocumentThatIsNotARegularFileIsSkipped()
    {
        if (!OperatingSystem.IsLinux()) return;
        Skin("home", "mine", Doc("", "Mine"));
        string pipe = Path.Combine(Skin("home", "piped", "{}"), "skin.json");
        string device = Path.Combine(Skin("home", "endless", "{}"), "skin.json");
        SpecialFile.Make(pipe, "pipe");
        SpecialFile.Make(device, "device");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));

        IReadOnlyList<Tui.SkinEntry> skins = await SpecialFile.Promptly(pipe, "pipe", SkinCatalog.Scan);

        Assert.Contains(skins, skin => skin.Id == "mine" && skin.Name == "Mine");
        Assert.DoesNotContain(skins, skin => skin.Id == "piped");
        Assert.DoesNotContain(skins, skin => skin.Id == "endless");
    }

    /// <summary>
    /// The catalogue keeps a reader, so a document replaced by a pipe or a
    /// device after the scan reads as nothing, promptly and within the limit.
    /// </summary>
    [Theory]
    [InlineData("pipe")]
    [InlineData("device")]
    public async Task ACataloguedDocumentReplacedByASpecialFileReadsAsNothing(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Skin("home", "changing", Doc("", "Changing"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        Tui.SkinEntry entry = Assert.Single(SkinCatalog.Scan(), skin => skin.Id == "changing");
        string file = Path.Combine(folder, "skin.json");
        SpecialFile.Make(file, kind);

        (string? text, long allocated) = await SpecialFile.Promptly(file, kind, () =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            string? read = entry.Read();
            return (read, GC.GetAllocatedBytesForCurrentThread() - before);
        });

        Assert.Null(text);
        Assert.True(allocated < 1024 * 1024, $"{allocated} bytes allocated");
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
    public void ACataloguedDocumentCannotGrowPastTheReadLimitAndCanBeRepaired()
    {
        string folder = Skin("home", "changing", Doc("", "Changing"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        Tui.SkinEntry entry = Assert.Single(SkinCatalog.Scan(), skin => skin.Id == "changing");
        string file = Path.Combine(folder, "skin.json");
        string valid = Doc("\"Ox.Card.Background\":\"#123456\"", "Repaired");
        File.WriteAllText(file, valid + new string(' ', 256 * 1024));
        Assert.Null(entry.Read());

        File.WriteAllText(file, valid);
        Assert.Equal(valid, entry.Read());
        Assert.Equal(Rgb.Parse("#123456"), SkinCatalog.Load("changing").Card);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADocumentAtTheByteLimitKeepsItsEncodingAndPalette(bool unicode)
    {
        string folder = Skin("home", "boundary", "");
        var encoding = unicode ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
        string valid = Doc("\"Ox.Card.Background\":\"#123456\"", "Grün");
        int padding = (256 * 1024 - encoding.GetPreamble().Length - encoding.GetByteCount(valid))
            / encoding.GetByteCount(" ");
        File.WriteAllText(Path.Combine(folder, "skin.json"), valid + new string(' ', padding), encoding);
        Assert.Equal(256 * 1024, new FileInfo(Path.Combine(folder, "skin.json")).Length);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));

        Theme theme = SkinCatalog.Load("boundary");
        Assert.Equal("Grün", theme.Name);
        Assert.Equal(Rgb.Parse("#123456"), theme.Card);
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

    private string PreferencePath()
    {
        string config = Path.Combine(_root, "preferences-config");
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        return Path.Combine(config, "openxlr", "ui.json");
    }

    /// <summary>
    /// No saved choice is Broadcast here as in the window, whether ui.json is
    /// missing or holds no skin, and the terminal writes nothing for it. A
    /// saved skin, Material included, is kept, and --skin default is Material.
    /// </summary>
    [Fact]
    public void NoSavedChoiceStartsOnBroadcastLikeTheWindow()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));
        string path = PreferencePath();

        Assert.Equal("broadcast", SkinCatalog.LoadSaved(UiSettingsFile.ReadSkin()).Id);
        Assert.Equal("Broadcast", SkinCatalog.LoadSaved(null).Name);
        Assert.True(SkinCatalog.LoadSaved(null).ConsoleFaders);
        Assert.False(File.Exists(path));

        foreach (string text in new[] { """{ "minimizeToTray": true }""", """{ "skin": "" }""", """{ "skin": null }""" })
        {
            File.WriteAllText(path, text);
            Assert.Equal("broadcast", SkinCatalog.LoadSaved(UiSettingsFile.ReadSkin()).Id);
            Assert.Equal(text, File.ReadAllText(path));
        }

        foreach (string id in new[] { "default", "nord", "broadcast" })
        {
            Assert.True(UiSettingsFile.WriteSkin(id));
            Assert.Equal(id, SkinCatalog.LoadSaved(UiSettingsFile.ReadSkin()).Id);
        }
        Assert.Same(Theme.Material, SkinCatalog.LoadSaved("default"));
        Assert.Same(Theme.Material, SkinCatalog.Load("default"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schema":2,"name":"Broadcast","tokens":{"Ox.Card.Background":"#123456"}}""")]
    public void AnUnreadableBroadcastLeavesMaterialOn(string document)
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));
        Skin("home", "broadcast", document);
        Assert.Same(Theme.Material, SkinCatalog.LoadSaved(null));
    }

    [Fact]
    public void OversizedPreferencesCannotBeAppliedOrOverwritten()
    {
        string path = PreferencePath();
        string text = "{\"skin\":\"nord\"}" + new string(' ', OpenXlrPaths.MaxPreferenceBytes);
        File.WriteAllText(path, text);

        Assert.Null(UiSettingsFile.ReadSkin());
        Assert.False(UiSettingsFile.WriteSkin("opendeck"));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void APreferenceWriteCannotGrowBeyondTheReadLimit()
    {
        string path = PreferencePath();
        const string prefix = "{\"skin\":\"nord\",\"padding\":\"";
        const string suffix = "\"}";
        string text = prefix + new string('x', OpenXlrPaths.MaxPreferenceBytes - prefix.Length - suffix.Length) + suffix;
        File.WriteAllText(path, text);
        Assert.Equal("nord", UiSettingsFile.ReadSkin());

        Assert.False(UiSettingsFile.WriteSkin("opendeck"));

        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void PreferencesAtTheReadLimitAndWithLegacyEncodingsStillLoad()
    {
        string path = PreferencePath();
        const string text = "{\"skin\":\"nord\",\"appearanceMode\":\"dark\"}";
        File.WriteAllText(path, text.PadRight(OpenXlrPaths.MaxPreferenceBytes));
        Assert.Equal("nord", UiSettingsFile.ReadSkin());
        foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
        {
            File.WriteAllText(path, text, encoding);
            Assert.Equal("nord", UiSettingsFile.ReadSkin());
            Assert.Equal("dark", UiSettingsFile.ReadAppearanceMode());
        }
    }

    [Fact]
    public async Task APreferencePipeCannotBlockTheTerminal()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = PreferencePath();
        var created = ProcessRunner.Run("mkfifo", [path]);
        Assert.True(created.Ok, created.Stderr);
        using var keeper = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var read = Task.Run(UiSettingsFile.ReadSkin);
        try { Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { keeper.Dispose(); await read.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(File.Exists(path));
    }

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

    [Theory]
    [InlineData("{\"skin\":\"nord\",\"skin\":\"gruvbox\"}")]
    [InlineData("{\"skin\":\"nord\",\"startDaemonAtLogin\":true,\"startDaemonAtLogin\":false}")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("42")]
    [InlineData("\"nord\"")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public void MalformedSavedDocumentsCannotCrashStartupOrBeOverwritten(string document)
    {
        string config = Path.Combine(_root, "duplicates-config");
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        string file = Path.Combine(config, "openxlr", "ui.json");
        File.WriteAllText(file, document);

        Assert.Null(UiSettingsFile.ReadSkin());
        Assert.False(UiSettingsFile.WriteSkin("opendeck"));
        Assert.Equal(document, File.ReadAllText(file));

        File.WriteAllText(file, """{"skin":"nord","startDaemonAtLogin":true}""");
        Assert.Equal("nord", UiSettingsFile.ReadSkin());
        Assert.True(UiSettingsFile.WriteSkin("opendeck"));
        Assert.Equal("opendeck", UiSettingsFile.ReadSkin());
        Assert.Contains("startDaemonAtLogin", File.ReadAllText(file), StringComparison.Ordinal);
    }

    // --- the skins this repository ships ---

    [Fact]
    public void AnUnreadableSavedChoiceFallsBackAndCanBeReadAfterRepair()
    {
        // Root reads a file with no permission bits anyway.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        string config = Path.Combine(_root, "unreadable-config");
        Directory.CreateDirectory(Path.Combine(config, "openxlr"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        string file = Path.Combine(config, "openxlr", "ui.json");
        File.WriteAllText(file, """{"skin":"nord","startDaemonAtLogin":true}""");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));
            Assert.Null(UiSettingsFile.ReadSkin());
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal("nord", UiSettingsFile.ReadSkin());
        Assert.Contains("startDaemonAtLogin", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedSkinsReadIntoAPaletteThatIsNotTheDefaultOne()
    {
        string shipped = Path.Combine(Root(), "src", "OpenXLR.UI", "Assets", "Skins");
        string[] folders = [.. Directory.GetDirectories(shipped),
            Path.Combine(Root(), "docs", "examples", "skins", "example"),
            Path.Combine(Root(), "docs", "examples", "skins", "broadcast")];
        Assert.True(folders.Length > 2, "only one appearance ships");

        foreach (string folder in folders)
        {
            string id = Path.GetFileName(folder);
            Theme theme = Theme.FromJson(File.ReadAllText(Path.Combine(folder, "skin.json")), id, id);
            Assert.NotEqual(Theme.Material.Window, theme.Window);
            Assert.NotEqual(Theme.Material.TextPrimary, theme.TextPrimary);
            // A meter that never changes colour wastes the scale, and every
            // shipped appearance sets its three zones.
            if (id != "example")
                Assert.NotEqual(theme.MeterFill, theme.MeterHot);
        }
    }


    /// <summary>
    /// Broadcast is the shipped skin with a layout. The terminal takes every
    /// colour, threshold and control appearance from it exactly as it would
    /// from the same file with the layout block taken out.
    /// </summary>
    [Fact]
    public void TheBroadcastSkinGivesTheTerminalThePaletteItWouldHaveWithoutItsLayout()
    {
        string json = File.ReadAllText(Path.Combine(Root(), "src", "OpenXLR.UI", "Assets", "Skins", "broadcast", "skin.json"));
        System.Text.Json.Nodes.JsonObject stripped = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(stripped.Remove("layout"));
        Theme theme = Theme.FromJson(json, "broadcast", "broadcast");
        Theme flat = Theme.FromJson(stripped.ToJsonString(), "broadcast", "broadcast");
        Assert.Equal("Broadcast", theme.Name);
        Assert.NotEqual(Theme.Material.Window, theme.Window);
        // Console faders for the round blue cap; flat lamps and keys, as on the dark neutral desk it copies.
        Assert.True(theme.ConsoleFaders);
        Assert.False(theme.LampLeds);
        Assert.False(theme.CapKeys);
        Assert.False(theme.Light);
        foreach (System.Reflection.PropertyInfo property in typeof(Theme).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            Assert.Equal(property.GetValue(flat), property.GetValue(theme));
    }

    [Fact]
    public void ControlAppearancesAreReadEvenWithoutAnyColourOverrides()
    {
        Theme theme = Theme.FromJson("""
            {"schema":1,"controls":{"meter":"segmented","fader":"console","button":"cap","mute":"cap","led":"lamp"}}
            """, "console", "Console");
        Assert.True(theme.ConsoleFaders);
        Assert.True(theme.CapKeys);
        Assert.True(theme.CapMutes);
        Assert.True(theme.LampLeds);
        Assert.False(Theme.Material.ConsoleFaders);
        Theme unknown = Theme.FromJson("""{"schema":1,"controls":{"meter":17,"fader":"future"},"tokens":{}}""", "x", "X");
        Assert.False(unknown.ConsoleFaders);
        Assert.False(unknown.CapKeys);
    }

    [Theory]
    [InlineData("rose-pine")]
    [InlineData("catppuccin-latte")]
    public void LightSkinsKeepTheDeskSelectionAndFaderCapsLegible(string id)
    {
        Theme theme = Theme.FromJson(
            File.ReadAllText(Path.Combine(Root(), "src", "OpenXLR.UI", "Assets", "Skins", id, "skin.json")), id, id);
        Assert.True(theme.Light);
        Assert.True(Contrast(theme.TextPrimary, theme.Selection) >= 4.5);
        Assert.True(Contrast(theme.On(theme.FocusedCap), theme.FocusedCap) >= 4.5);
        Assert.True(Contrast(theme.On(theme.FaderThumb), theme.FaderThumb) >= 4.5);
        DaemonLink link = new();
        link.Receive("""
            {"type":"state","connected":true,"mixer":{
              "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor","volume":1}],
              "channels":[{"id":"mic","name":"Microphone","levels":{"monitor":0.5}}]}}
            """);
        link.Receive("""{"type":"meters","levels":{"ch:mic":[0.5,0.8],"mix:monitor":[0.3,0.6]}}""");
        App app = new(link, theme);
        Screen screen = new(150, 42);
        app.Draw(screen);
        Assert.Equal(theme.Selection, screen.At(20, 40).Back);
        Assert.Equal(theme.TextPrimary, screen.At(20, 40).Fore);
        Assert.Equal(theme.Card, screen.At(30, 3).Back);
        Assert.Equal(theme.Accent, screen.At(1, 5).Fore);
        // A vertical bar colours each cell by its top edge over the bar's
        // height, so the warning zone's colours are those of every such edge
        // between the warning level and the hot level, whatever the height.
        HashSet<Rgb> warning = [];
        for (int height = 1; height <= 60; height++)
            for (int row = 1; row <= height; row++)
            {
                double position = (double)row / height;
                if (position >= 0.7 && position <= 0.9) warning.Add(theme.MeterColour(position));
            }
        bool cap = false, meter = false;
        for (int y = 0; y < screen.Height; y++)
            for (int x = 0; x < screen.Width; x++)
            {
                Cell cell = screen.At(x, y);
                if (cell.Ch == '╞' && cell.Back == theme.FocusedCap)
                {
                    Assert.Equal(theme.On(theme.FocusedCap), cell.Fore);
                    cap = true;
                }
                if (cell.Ch == '█' && warning.Contains(cell.Fore)) meter = true;
            }
        Assert.True(cap, "the focused fader cap was not drawn");
        Assert.True(meter, "the light skin's warning zone was not drawn");
    }

    private static double Contrast(Rgb a, Rgb b) =>
        (Math.Max(a.Luminance(), b.Luminance()) + 0.05) / (Math.Min(a.Luminance(), b.Luminance()) + 0.05);

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
