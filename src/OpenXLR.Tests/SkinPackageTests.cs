using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Media.Immutable;
using System.Text.Json;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The skin format: what a package may say, what it may reach, and what the
/// application does with a package that is wrong. None of this needs a
/// rendering platform, so it runs in the ordinary test pass.
/// </summary>
[Collection("xdg-config")]
public sealed class SkinPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openxlr-skins-" + Guid.NewGuid());
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

    private static SkinReadResult Read(string json, string id = "test", string? directory = null) =>
        SkinReader.Read(id, json, directory is null ? SkinOrigin.BuiltIn : SkinOrigin.User, directory);

    private static string Doc(string tokens) => """{"schema":1,"name":"Test","tokens":{""" + tokens + "}}";

    /// <summary>One token line, quoted, so the test reads as the file does.</summary>
    private static string Tok(string name, string json) => $"\"{name}\": {json}";

    // --- the shipped packages ---

    [Fact]
    public void DeckPalettePublishesRealisedValuesPrivatelyAndReportsWriteFailure()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
        var resources = new Avalonia.Controls.ResourceDictionary
        {
            ["Ox.Card.Background"] = new ImmutableSolidColorBrush(Color.Parse("#fffaff")),
            ["Ox.Text.Primary"] = new ImmutableSolidColorBrush(Color.Parse("#010203")),
            ["Ox.Meter.HotLevel"] = 0.8,
            ["Ox.Meter.Track"] = new LinearGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#020304"), 0),
                    new GradientStop(Color.Parse("#050607"), 1),
                },
            },
        };
        var errors = new List<string>();
        DeckPalette.Publish("default", resources, errors);
        Assert.Empty(errors);
        string file = Path.Combine(_root, "openxlr", "deck-palette.json");
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(file)))
        {
            Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal("default", json.RootElement.GetProperty("skin").GetString());
            Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("pid").GetInt32());
            JsonElement tokens = json.RootElement.GetProperty("tokens");
            Assert.Equal(Color.Parse("#fffaff"), Color.Parse(tokens.GetProperty("Ox.Card.Background").GetString()!));
            Assert.Equal(0.8, tokens.GetProperty("Ox.Meter.HotLevel").GetDouble());
            Assert.Equal(Color.Parse("#020304"), Color.Parse(tokens.GetProperty("Ox.Meter.Track").GetString()!));
            Assert.Equal(DeckPalette.Names.Length, tokens.EnumerateObject().Count());
        }
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.False(File.Exists(Path.Combine(_root, "openxlr", "ui.json")));

        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "keep");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", blocked);
        DeckPalette.Publish("default", resources, errors);
        Assert.Single(errors);
        Assert.StartsWith("Stream Deck palette:", errors[0], StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(blocked));
    }

    [Fact]
    public void BuiltInSkinsReadWithoutComplaint()
    {
        SkinEntry[] built = [.. SkinCatalog.BuiltIn()];
        // Every appearance the application carries is compiled in, so a
        // package has them all without installing a single file.
        Assert.Equal(
            ["broadcast", "catppuccin", "catppuccin-latte", "compact-desk", "everforest", "gruvbox", "kanagawa", "matte-black",
                "nord", "nyan-pink", "opendeck", "osaka-jade", "ristretto", "rose-pine", "tokyo-night", "wave"],
            built.Select(e => e.Id).Order(StringComparer.Ordinal));
        foreach (SkinEntry entry in built)
        {
            Assert.Empty(entry.Errors);
            Assert.NotEqual("", entry.Name);
            // A whole appearance, not a recolour of a few labels.
            Assert.True(entry.Package.Tokens.Count > 40,
                $"{entry.Id} sets only {entry.Package.Tokens.Count} tokens");
        }

        SkinEntry openDeck = Assert.Single(built, e => e.Id == "opendeck");
        Assert.Empty(openDeck.Errors);
        Assert.Equal("Deck", openDeck.Name);

        // It speaks the plugin's language: the faceplate is side lit, the fader
        // cap is machined, and every key wears the face the mute key wears.
        Assert.IsType<SkinGradient>(openDeck.Package.Tokens["Ox.Card.Background"]);
        Assert.True(((SkinGradient)openDeck.Package.Tokens["Ox.Fader.Thumb.Background"]).Radial);
        foreach (string face in new[] { "Ox.Control.Background", "Ox.Control.BackgroundPointerOver",
                     "Ox.Control.BackgroundChecked", "Ox.Mute.Background", "Ox.Mute.BackgroundChecked" })
            Assert.False(((SkinGradient)openDeck.Package.Tokens[face]).Radial,
                $"{face} is not the flat-lit face the keys share");
        // Square keys: the corner radius a skin sets reaches every one of them.
        Assert.Equal(new CornerRadius(0),
            ((SkinCornerRadius)openDeck.Package.Tokens["Ox.Control.CornerRadius"]).Value);
        // The meter runs green to amber to red across the scale.
        Assert.NotEqual(((SkinSolid)openDeck.Package.Tokens["Ox.Meter.Fill"]).Color,
            ((SkinSolid)openDeck.Package.Tokens["Ox.Meter.Warning"]).Color);
        Assert.NotEqual(((SkinSolid)openDeck.Package.Tokens["Ox.Meter.Warning"]).Color,
            ((SkinSolid)openDeck.Package.Tokens["Ox.Meter.Hot"]).Color);
        Assert.Equal(Color.Parse("#3ecf7a"), ((SkinSolid)openDeck.Package.Tokens["Ox.Led.On"]).Color);
        // The keys are black and speak with their lettering, so no face is a
        // bright fill.
        foreach (string face in new[] { "Ox.Control.Background", "Ox.Control.BackgroundChecked",
                     "Ox.Mute.Background", "Ox.Mute.BackgroundChecked" })
            Assert.All(Colours(openDeck, face), c => Assert.True(Luminance(c) < 0.05,
                $"{face} has a stop at luminance {Luminance(c):F3}, which is a fill and not a face"));
    }

    /// <summary>
    /// A shipped skin describes itself in its own words: no built-in
    /// description names Wave Link or Elgato.
    /// </summary>
    [Fact]
    public void NoBuiltInSkinDescriptionNamesWaveLinkOrElgato()
    {
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            string description = entry.Package.Description ?? "";
            Assert.DoesNotContain("Wave Link", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Elgato", description, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Broadcast is the reference layout skin an author copies, and Wave
    /// wears the same arrangement in its own palette: the whole layout is
    /// taken, with INPUTS hidden, the mix matrix, MONITOR's toolbar,
    /// HEADPHONES beside it, the applications in columns, every header
    /// action in the row and nothing folded. The meter colours are flat, so
    /// both palette exporters can carry them.
    /// </summary>
    [Theory]
    [InlineData("broadcast", "Broadcast")]
    [InlineData("wave", "Wave")]
    public void TheMatrixSkinsCarryTheWholeLayout(string id, string name)
    {
        SkinEntry skin = Assert.Single(SkinCatalog.BuiltIn(), e => e.Id == id);
        Assert.Empty(skin.Errors);
        Assert.Equal(name, skin.Name);
        Assert.Equal("OpenXLR", skin.Package.Author);
        Assert.False(string.IsNullOrWhiteSpace(skin.Package.Description));
        Assert.DoesNotContain("Wave Link", skin.Package.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Elgato", skin.Package.Description!, StringComparison.OrdinalIgnoreCase);

        SkinLayout layout = Assert.IsType<SkinLayout>(skin.Package.Layout);
        // The header stays above the tree, across the window.
        Assert.False(layout.PlacesHeader);
        // A column: a grid with HEADPHONES in its top left cell, MONITOR in its top right cell and
        // the matrix across both columns under them, then INPUTS and APPLICATIONS across the window.
        SkinLayoutStack desk = Assert.IsType<SkinLayoutStack>(layout.Main);
        Assert.Equal(SkinLayoutOrientation.Vertical, desk.Orientation);
        SkinLayoutGrid mixes = Assert.IsType<SkinLayoutGrid>(desk.Children[0].Node);
        Assert.Equal([new SkinLayoutTrack(SkinTrackUnit.Star, 1), new SkinLayoutTrack(SkinTrackUnit.Pixel, 360)], mixes.Columns);
        SkinLayoutChild headphones = mixes.Children.Single(c => c.Node == new SkinLayoutRegion(SkinLayoutRegions.Headphones));
        Assert.Equal((0, 0), (headphones.Row, headphones.Column));
        SkinLayoutChild monitor = mixes.Children.Single(c => c.Node == new SkinLayoutRegion(SkinLayoutRegions.Monitor));
        Assert.Equal((0, 1), (monitor.Row, monitor.Column));
        SkinLayoutChild submixer = mixes.Children.Single(c => c.Node == new SkinLayoutRegion(SkinLayoutRegions.Submixer));
        Assert.Equal((1, 0, 2), (submixer.Row, submixer.Column, submixer.ColumnSpan));
        Assert.Equal([SkinLayoutRegions.Inputs, SkinLayoutRegions.Applications],
            desk.Children.Skip(1).Select(c => ((SkinLayoutRegion)c.Node).Id));
        Assert.Equal(SkinLayoutTemplates.HardwarePanel, layout.Inputs.Template);
        Assert.Equal(SkinRegionVisibility.Hidden, layout.OptionsFor(SkinLayoutRegions.Inputs).Visibility);
        Assert.True(layout.Submixer.IsMatrix);
        SkinMatrixOptions matrix = layout.Submixer.Matrix;
        // XLR 1's and XLR 2's binding, drawn under the name wherever the order puts it.
        Assert.Equal(["identity", "binding", "mute", "gain", "meter", "processing", "effects"], matrix.RowHead.Shown);
        Assert.Equal(["identity", "outputs", "mute", "master", "effects"], matrix.ColumnHead.Shown);
        Assert.Equal(["mute", "fader", "value"], matrix.Cell.Shown);
        Assert.Equal(SkinLabelStyle.IconAndText, matrix.RowHead.Label);
        // A muted send greys out, and the send's level shows over its fader while it moves.
        Assert.Equal(SkinMatrixMuted.Dim, matrix.Muted);
        Assert.Equal(SkinMatrixValue.OverFader, matrix.Value);
        Assert.Equal(SkinMatrixMeter.UnderFader, matrix.Meter);
        Assert.Equal(SkinLayoutTemplates.OutputToolbar, layout.Monitor);
        Assert.Equal(SkinLayoutTemplates.ApplicationColumns, layout.Applications);
        Assert.Equal(4, layout.ApplicationsPerColumn);
        Assert.Equal(SkinMixerPlacement.Docked, layout.PopOut);
        Assert.Equal(SkinRegionVisibility.Visible, layout.OptionsFor(SkinLayoutRegions.Headphones).Visibility);
        Assert.Equal(SkinRegionVisibility.Visible, layout.OptionsFor(SkinLayoutRegions.Applications).Visibility);
        Assert.Equal(["profiles", "flow", "arrange", "restart-daemon", "version", "about"], layout.Header.Row);
        Assert.Empty(layout.Header.Overflow);

        Assert.Equal("console", skin.Package.Controls["fader"]);
        foreach (string zone in new[] { "Ox.Meter.Fill", "Ox.Meter.Warning", "Ox.Meter.Hot", "Ox.Meter.Track" })
            Assert.IsType<SkinSolid>(skin.Package.Tokens[zone]);
        Assert.Equal(3, new[] { "Ox.Meter.Fill", "Ox.Meter.Warning", "Ox.Meter.Hot" }
            .Select(z => ((SkinSolid)skin.Package.Tokens[z]).Color).Distinct().Count());
        Assert.True(((SkinNumber)skin.Package.Tokens["Ox.Meter.WarningLevel"]).Value
            < ((SkinNumber)skin.Package.Tokens["Ox.Meter.HotLevel"]).Value);
    }

    /// <summary>
    /// The names a user reads are not the ids anything is stored under. A
    /// ui.json that already says "opendeck" has to keep working, and so does
    /// OPENXLR_SKIN=default, whatever the skins are called in the picker.
    /// </summary>
    [Fact]
    public void RenamingASkinDoesNotMoveWhatIsStoredUnderIt()
    {
        Assert.Equal("default", SkinPackage.DefaultId);
        Assert.Equal("Material", SkinPackage.Default.Name);

        SkinEntry saved = Assert.Single(SkinCatalog.BuiltIn(), e => e.Id == "opendeck");
        Assert.Equal("Deck", saved.Name);
        Assert.Equal("opendeck", saved.Package.Id);

        // The id a settings file holds still finds the skin.
        Assert.Equal("Deck", SkinCatalog.Find("opendeck")!.Name);
        Assert.Equal("Material", SkinCatalog.Find("default")!.Name);
        Assert.Equal("Material", SkinCatalog.Find(null)!.Name);
    }

    [Fact]
    public void TheDefaultSkinOverlaysNothing()
    {
        Assert.Empty(SkinPackage.Default.Tokens);
        Assert.Equal("default", SkinPackage.DefaultId);
        // Every token the views read has a value without any skin at all.
        Assert.All(SkinTokens.All.Where(t => t.Name.StartsWith("Ox.Text.", StringComparison.Ordinal)),
            t => Assert.NotNull(t.Default));
    }

    /// <summary>
    /// The default table is data. It is built by a class initializer on
    /// whichever thread reads it first, and an AvaloniaObject in it can only be
    /// read from the UI thread: a mutable brush there turned every read of the
    /// defaults from another thread into a type initialization failure, which
    /// is a test harness away from being a window that will not open.
    /// </summary>
    [Fact]
    public void NoDefaultNeedsTheUiThreadToBeRead()
    {
        Assert.All(SkinTokens.All.Where(t => t.Default is not null),
            t => Assert.False(t.Default is AvaloniaObject,
                $"{t.Name}: the default is a {t.Default!.GetType().Name}, which only the UI thread may read."));
    }

    [Fact]
    public void EveryTokenNameIsUniqueAndFindable()
    {
        Assert.Equal(SkinTokens.All.Count, SkinTokens.All.Select(t => t.Name).Distinct().Count());
        Assert.All(SkinTokens.All, t => Assert.Same(t, SkinTokens.Find(t.Name)));
        Assert.Null(SkinTokens.Find("Ox.Nope"));
    }

    /// <summary>
    /// Every Ox.* name a view asks for is a token the application has. A typo
    /// in markup resolves to nothing at runtime and shows as a hole, which no
    /// other test would catch.
    /// </summary>
    [Fact]
    public void ViewsOnlyNameTokensThatExist()
    {
        if (SourceDirectory() is not { } source) return;   // running from a copied build tree
        var missing = new List<string>();
        foreach (string file in Directory.GetFiles(source, "*.axaml").Concat(Directory.GetFiles(source, "*.cs")))
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"""(Ox\.[A-Za-z0-9.]+)"""))
                if (!SkinTokens.Exists(match.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
        Assert.Empty(missing);
    }

    [Fact]
    public void ViewsOnlyNameTextMetricsThatExist()
    {
        if (SourceDirectory() is not { } source) return;
        var keys = SkinService.TextSizeBases.Select(size => $"UiText.FontSize{size}").ToHashSet();
        keys.Add(SkinService.CrossfadeReadoutWidth);
        keys.UnionWith(SkinService.LabelWidthBases.Select(width => $"UiText.Width{width}"));
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".axaml" or ".cs")
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal) && !file.Contains("/bin/", StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"UiText\.[A-Za-z0-9]+\b(?!\{)"))
                if (!keys.Contains(match.Value)) missing.Add($"{Path.GetFileName(file)}: {match.Value}");
        }
        Assert.Empty(missing);
    }

    private static string? SourceDirectory()
    {
        // The build tree is src/<project>/bin/... in a plain build and
        // dist/<name>/bin/<project>/... under an artifacts path, so walk to the
        // repository and take the project from there rather than counting
        // levels.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "OpenXLR.UI");
            if (File.Exists(Path.Combine(candidate, "MainWindow.axaml"))) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    // --- the format ---

    [Fact]
    public void ASchemaFromTheFutureIsRefusedWithSomethingToDo()
    {
        SkinReadResult result = Read("""{"schema":99,"tokens":{}}""");
        Assert.Null(result.Package);
        Assert.Contains("Update OpenXLR", Assert.Single(result.Errors));
    }

    [Fact]
    public void AMissingOrImpossibleSchemaIsRefused()
    {
        Assert.Null(Read("""{"tokens":{}}""").Package);
        Assert.Null(Read("""{"schema":0,"tokens":{}}""").Package);
        Assert.Null(Read("""{"schema":"1","tokens":{}}""").Package);
        Assert.Null(Read("not json at all").Package);
        Assert.Null(Read("[1,2,3]").Package);
    }

    [Fact]
    public void ADocumentOverTheSizeLimitIsRefusedBeforeParsing()
    {
        string huge = new('x', SkinFormat.MaxDocumentBytes + 1);
        SkinReadResult result = Read(huge);
        Assert.Null(result.Package);
        Assert.Contains("larger than", Assert.Single(result.Errors));
    }

    [Fact]
    public void AnUnknownTokenIsReportedAndTheRestStillApplies()
    {
        SkinReadResult result = Read(Doc("""
            "Ox.Not.AThing": "#ff0000", "Ox.Text.Primary": "#010203"
            """));
        Assert.NotNull(result.Package);
        Assert.Contains("Ox.Not.AThing", Assert.Single(result.Errors));
        Assert.Equal(Color.Parse("#010203"), ((SkinSolid)result.Package!.Tokens["Ox.Text.Primary"]).Color);
    }

    [Fact]
    public void APartialSkinLeavesEveryOtherTokenAlone()
    {
        SkinReadResult result = Read(Doc(Tok("Ox.Text.Primary", "\"#010203\"")));
        Assert.True(result.Ok);
        Assert.Equal(["Ox.Text.Primary"], result.Package!.Tokens.Keys);
    }

    [Fact]
    public void ANumberOutsideItsBoundsIsRefused()
    {
        Assert.Contains("outside the allowed range",
            Assert.Single(Read(Doc(Tok("Ox.Meter.Height", "900"))).Errors));
        Assert.Contains("outside the allowed range",
            Assert.Single(Read(Doc(Tok("Ox.Card.CornerRadius", "5000"))).Errors));
        Assert.Contains("outside the allowed range",
            Assert.Single(Read(Doc(Tok("Ox.Card.BorderThickness", "[0, 99, 0, 0]"))).Errors));
        Assert.Single(Read(Doc(Tok("Ox.Meter.Height", "\"tall\""))).Errors);
    }

    [Fact]
    public void AnIndicatorColourMustStayFlat()
    {
        string gradient = """
            "Ox.Led.On": {"type":"linear","stops":[{"offset":0,"color":"#000"},{"offset":1,"color":"#fff"}]}
            """;
        Assert.Contains("flat colour", Assert.Single(Read(Doc(gradient)).Errors));
        // The same gradient on a surface is fine.
        Assert.True(Read(Doc(gradient.Replace("Ox.Led.On", "Ox.Card.Background"))).Ok);
    }

    [Fact]
    public void AGradientNeedsStopsAndStaysInsideItsBox()
    {
        Assert.Single(Read(Doc(Tok("Ox.Card.Background", "{\"type\":\"linear\",\"stops\":[]}"))).Errors);
        Assert.Single(Read(Doc("""
            "Ox.Card.Background": {"type":"linear","stops":[{"offset":0,"color":"#000"}]}
            """)).Errors);
        Assert.Single(Read(Doc("""
            "Ox.Card.Background": {"type":"linear","from":[0,99],"to":[1,1],
              "stops":[{"offset":0,"color":"#000"},{"offset":1,"color":"#fff"}]}
            """)).Errors);
        Assert.Single(Read(Doc("""
            "Ox.Card.Background": {"type":"radial","radius":900,
              "stops":[{"offset":0,"color":"#000"},{"offset":1,"color":"#fff"}]}
            """)).Errors);

        SkinReadResult ok = Read(Doc("""
            "Ox.Card.Background": {"type":"radial","from":[0.5,0.3],"to":[0.5,0.5],"radius":0.9,
              "stops":[{"offset":0,"color":"#4a4a4a"},{"offset":1,"color":"#333333"}]}
            """));
        Assert.True(ok.Ok);
        var radial = (SkinGradient)ok.Package!.Tokens["Ox.Card.Background"];
        Assert.True(radial.Radial);
        Assert.Equal(new Point(0.5, 0.3), radial.Start);
        Assert.Equal(new Point(0.5, 0.5), radial.End);
        Assert.Equal(0.9, radial.Radius);
    }

    [Fact]
    public void ARadialGradientPutsItsHighlightOnTheCentreUnlessTold()
    {
        var radial = (SkinGradient)Read(Doc("""
            "Ox.Card.Background": {"type":"radial","to":[0.25,0.75],
              "stops":[{"offset":0,"color":"#000"},{"offset":1,"color":"#fff"}]}
            """)).Package!.Tokens["Ox.Card.Background"];
        Assert.Equal(radial.End, radial.Start);
    }

    [Fact]
    public void AFontIsNamedNotLoaded()
    {
        Assert.True(Read(Doc(Tok("Ox.Value.FontFamily", "\"DejaVu Sans Mono\""))).Ok);
        Assert.Contains("does not load font files",
            Assert.Single(Read(Doc(Tok("Ox.Value.FontFamily", "\"avares://Evil/F.ttf#Evil\""))).Errors));
        Assert.Contains("does not load font files",
            Assert.Single(Read(Doc(Tok("Ox.Value.FontFamily", "\"file:///tmp/f.ttf\""))).Errors));
    }

    [Fact]
    public void AFontWeightIsANameOrANumber()
    {
        Assert.Equal(FontWeight.Bold,
            ((SkinFontWeight)Read(Doc(Tok("Ox.Label.FontWeight", "\"Bold\""))).Package!.Tokens["Ox.Label.FontWeight"]).Value);
        Assert.Equal((FontWeight)700,
            ((SkinFontWeight)Read(Doc(Tok("Ox.Label.FontWeight", "700"))).Package!.Tokens["Ox.Label.FontWeight"]).Value);
        Assert.Single(Read(Doc(Tok("Ox.Label.FontWeight", "\"Chunky\""))).Errors);
        Assert.Single(Read(Doc(Tok("Ox.Label.FontWeight", "99999"))).Errors);
    }

    [Fact]
    public void AnIdThatDisagreesWithTheFolderIsReportedAndTheFolderWins()
    {
        SkinReadResult result = SkinReader.Read("mine", """{"schema":1,"id":"theirs","tokens":{}}""",
            SkinOrigin.User, _root);
        Assert.Equal("mine", result.Package!.Id);
        Assert.Contains("the folder name wins", string.Join(" ", result.Errors));
    }

    [Fact]
    public void AnIdWithSomethingOddInItIsRefused()
    {
        Assert.False(SkinReader.IsValidId("../evil"));
        Assert.False(SkinReader.IsValidId("Has Spaces"));
        Assert.False(SkinReader.IsValidId(""));
        Assert.False(SkinReader.IsValidId(new string('a', 65)));
        Assert.True(SkinReader.IsValidId("opendeck"));
        Assert.True(SkinReader.IsValidId("wave-link.2"));
    }

    // --- what a skin may reach ---

    [Fact]
    public void AnImageMustBeAPlainFileInsideTheSkinsOwnFolder()
    {
        Directory.CreateDirectory(_root);
        foreach (string bad in new[]
                 {
                     "../secret.png", "a/../../secret.png", "/etc/passwd", "./x.png", "",
                     "https://example.invalid/x.png", "C:\\x.png", "sub//x.png",
                 })
        {
            SkinReadResult result = Read(Doc($$"""
                "Ox.Card.Background": {"type":"image","source":{{System.Text.Json.JsonSerializer.Serialize(bad)}}}
                """), directory: _root);
            Assert.Single(result.Errors);
        }
        Assert.True(Read(Doc("""
            "Ox.Card.Background": {"type":"image","source":"art/faceplate.png"}
            """), directory: _root).Ok);
    }

    [Fact]
    public void ADocumentWithNoFilesCannotUseAnImage()
    {
        Assert.Contains("carries no files", Assert.Single(Read(Doc("""
            "Ox.Card.Background": {"type":"image","source":"x.png"}
            """)).Errors));
    }

    /// <summary>The fixture skins compiled into this assembly the way the application compiles in its own.</summary>
    private static SkinEntry[] Fixtures => [.. SkinCatalog.BuiltIn(typeof(SkinPackageTests).Assembly)];

    /// <summary>
    /// A built-in skin carries its images as resources beside its document,
    /// read through the same asset source an installed skin's folder is.
    /// </summary>
    [Fact]
    public void ABuiltInSkinReadsItsImagesFromTheResourcesBesideIt()
    {
        SkinEntry art = Assert.Single(Fixtures, e => e.Id == "embedded-art");
        Assert.Empty(art.Errors);
        Assert.Equal(SkinOrigin.BuiltIn, art.Package.Origin);
        Assert.Null(art.Package.Directory);
        Assert.IsType<SkinEmbeddedAssets>(art.Package.Assets);
        Assert.Equal(new SkinImage("art/plate.png", Stretch.Fill, 1), art.Package.Tokens["Ox.Tile.Background"]);

        using (Stream stream = Assert.IsAssignableFrom<Stream>(art.Package.Assets!.Open("art/plate.png")))
            Assert.Equal(new PixelSize(16, 12), SkinImageHeader.Read(stream));
        Assert.Null(art.Package.Assets.Open("art/missing.png"));
        Assert.Null(art.Package.Assets.Open("plate.png"));

        // The application's own skins have a source too, so an image added
        // beside one of them later needs no code.
        Assert.All(SkinCatalog.BuiltIn(), e => Assert.IsType<SkinEmbeddedAssets>(e.Package.Assets));
    }

    /// <summary>
    /// A built-in skin's file names are held to the installed skin's rules,
    /// with the same messages, and one skin cannot reach another's files.
    /// </summary>
    [Fact]
    public void ABuiltInSkinsFilesAreItsOwn()
    {
        SkinEntry limits = Assert.Single(Fixtures, e => e.Id == "embedded-limits");
        Assert.Equal(3, limits.Errors.Count);
        Assert.Contains(limits.Errors, e => e.StartsWith("Ox.Control.Background:", StringComparison.Ordinal)
            && e.Contains("no empty, \".\" or \"..\" segments", StringComparison.Ordinal));
        Assert.Contains(limits.Errors, e => e.StartsWith("Ox.Control.BackgroundPointerOver:", StringComparison.Ordinal)
            && e.Contains("relative to the skin's folder", StringComparison.Ordinal));
        Assert.Contains(limits.Errors, e => e.StartsWith("Ox.Control.BackgroundPressed:", StringComparison.Ordinal)
            && e.Contains("forward slashes", StringComparison.Ordinal));
        Assert.False(limits.Package.Tokens.ContainsKey("Ox.Control.Background"));

        // The name is checked before any resource is looked up, and a
        // resource of another skin is not under this skin's name.
        SkinAssets assets = limits.Package.Assets!;
        Assert.Null(assets.Open("art/plate.png"));
        Assert.NotNull(SkinEmbeddedAssets.For(typeof(SkinPackageTests).Assembly, "embedded-art").Open("art/plate.png"));
        foreach (string bad in new[] { "../embedded-art/art/plate.png", "/art/big.png", "art/./big.png", "https://example.invalid/a.png" })
            Assert.Throws<SkinValueException>(() => assets.Check(bad));
        Assert.Equal("art/big.png", assets.Check("art/big.png"));
    }

    /// <summary>
    /// The fixtures are compiled in under the names the application gives its
    /// own skins, so the tests above read a built-in skin as it really is.
    /// </summary>
    [Fact]
    public void TheFixtureSkinsAreEmbeddedUnderTheApplicationsNames()
    {
        static string[] Names(string project) =>
        [
            .. System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot, "src", project, project + ".csproj"))
                .Descendants("EmbeddedResource")
                .Select(e => (string?)e.Attribute("LogicalName") ?? "")
                .Where(n => n.StartsWith("OpenXLR.UI.Assets.Skin", StringComparison.Ordinal)),
        ];
        string[] application = Names("OpenXLR.UI");
        Assert.Equal(2, application.Length);
        Assert.Equal(application, Names("OpenXLR.Tests"));
        Assert.All(Fixtures, e => Assert.StartsWith("embedded-", e.Id, StringComparison.Ordinal));
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    [Fact]
    public void ALinkOutOfTheSkinsFolderIsNotFollowed()
    {
        string folder = Path.Combine(_root, "skin");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        string secret = Path.Combine(outside, "secret.png");
        File.WriteAllBytes(secret, [0x89, (byte)'P', (byte)'N', (byte)'G']);
        File.CreateSymbolicLink(Path.Combine(folder, "link.png"), secret);
        File.WriteAllBytes(Path.Combine(folder, "own.png"), [0x89, (byte)'P', (byte)'N', (byte)'G']);

        Assert.Null(SkinReader.ResolveAsset(folder, "link.png"));
        Assert.NotNull(SkinReader.ResolveAsset(folder, "own.png"));
        Assert.Null(SkinReader.ResolveAsset(folder, "gone.png"));
    }

    // --- the window's outline ---

    private static SkinReadResult Window(string section) =>
        Read("""{"schema":1,"tokens":{},"window":""" + section + "}");

    /// <summary>
    /// The window section picks one of OpenXLR's outlines. Anything else is
    /// reported and the window keeps the rectangle, and the rest of the skin
    /// still reads.
    /// </summary>
    [Fact]
    public void TheWindowSectionChoosesAnOutlineAndRefusesTheRest()
    {
        SkinReadResult none = Read(Doc(""));
        Assert.True(none.Ok);
        Assert.Equal(SkinWindowShapes.Rectangle, none.Package!.WindowShape);

        SkinReadResult rounded = Window("""{"shape":"rounded"}""");
        Assert.True(rounded.Ok, string.Join("; ", rounded.Errors));
        Assert.Equal(SkinWindowShapes.Rounded, rounded.Package!.WindowShape);
        Assert.True(Window("""{"shape":"rectangle"}""").Ok);

        foreach (string bad in new[] { """{"shape":"oval"}""", """{"shape":3}""", """{"shape":"Rounded"}""" })
        {
            SkinReadResult result = Window(bad);
            Assert.Equal(SkinWindowShapes.Rectangle, result.Package!.WindowShape);
            Assert.Equal("window.shape: expected one of \"rectangle\" or \"rounded\"; the window keeps the rectangle.",
                Assert.Single(result.Errors));
        }

        SkinReadResult notObject = Window("\"rounded\"");
        Assert.Equal(SkinWindowShapes.Rectangle, notObject.Package!.WindowShape);
        Assert.Contains("\"window\" must be an object", Assert.Single(notObject.Errors));

        // A field this version does not read is a typo or a later feature:
        // reported, and the shape beside it still applies.
        SkinReadResult extra = Window("""{"shape":"rounded","mask":"outline.png"}""");
        Assert.Equal(SkinWindowShapes.Rounded, extra.Package!.WindowShape);
        Assert.Equal("window: \"mask\" is not a window setting this OpenXLR reads; it is ignored.", Assert.Single(extra.Errors));

        // Opt in only: every appearance the application ships keeps the rectangle.
        Assert.All(SkinCatalog.BuiltIn(), e => Assert.Equal(SkinWindowShapes.Rectangle, e.Package.WindowShape));
        Assert.Equal(SkinWindowShapes.Rectangle, SkinPackage.Default.WindowShape);
    }

    /// <summary>
    /// The outline the window wears: the skin's shape only where the platform
    /// draws a transparent window, and square corners while it fills the screen.
    /// </summary>
    [Theory]
    [InlineData("rounded", true, WindowState.Normal, "Rounded")]
    [InlineData("rounded", true, WindowState.Minimized, "Rounded")]
    [InlineData("rounded", true, WindowState.Maximized, "Square")]
    [InlineData("rounded", true, WindowState.FullScreen, "Square")]
    [InlineData("rounded", false, WindowState.Normal, "System")]
    [InlineData("rounded", false, WindowState.Maximized, "System")]
    [InlineData("rectangle", true, WindowState.Normal, "System")]
    [InlineData("rectangle", false, WindowState.Normal, "System")]
    public void TheOutlineFollowsTheShapeThePlatformAndTheWindowState(
        string shape, bool transparent, WindowState state, string expected) =>
        Assert.Equal(Enum.Parse<WindowOutline>(expected), WindowFrame.Decide(shape, transparent, state));

    [Fact]
    public void TooManyImagesAreRefused()
    {
        Directory.CreateDirectory(_root);
        string tokens = string.Join(",", SkinTokens.All
            .Where(t => t.Kind == SkinTokenKind.Brush && !t.SolidOnly)
            .Take(SkinFormat.MaxImages + 2)
            .Select(t => $$"""
                "{{t.Name}}": {"type":"image","source":"x.png"}
                """));
        Assert.NotEmpty(Read(Doc(tokens), directory: _root).Errors);
    }

    /// <summary>
    /// An image is measured from its header before it is decoded, so a small
    /// file that describes an enormous picture is refused rather than
    /// allocated. Only PNG and JPEG are measurable, and nothing else is read.
    /// </summary>
    [Fact]
    public void AnImageIsSizedFromItsHeaderAndOnlyPngAndJpegAreRead()
    {
        Directory.CreateDirectory(_root);

        byte[] png =
        [
            0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x04, 0xD2,          // 1234
            0, 0, 0x02, 0x37,          // 567
            8, 6, 0, 0, 0,
        ];
        Assert.Equal(new PixelSize(1234, 567), ReadSize("a.png", png));

        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,                  // a segment to skip
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x02, 0x58, // SOF0: 300 high, 600 wide
            3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        ];
        Assert.Equal(new PixelSize(600, 300), ReadSize("a.jpg", jpeg));

        Assert.Null(ReadSize("a.gif", "GIF89a............"u8.ToArray()));
        Assert.Null(ReadSize("a.txt", "not an image at all"u8.ToArray()));
        Assert.Null(ReadSize("a.tiny", [0x89, (byte)'P']));
    }

    private PixelSize? ReadSize(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return SkinImageHeader.Read(new FileInfo(path));
    }

    // --- the controls a skin may restyle ---

    [Fact]
    public void EveryControlSlotHasItsBuiltInAppearanceAndAtLeastOneAlternative()
    {
        Assert.NotEmpty(SkinControls.Slots);
        foreach (SkinControlSlot slot in SkinControls.Slots)
        {
            Assert.True(slot.Variants.ContainsKey(slot.Default), $"{slot.Name} has no default variant");
            // The default publishes no control theme, which is what leaves the
            // control with the appearance the window has always had.
            Assert.Null(SkinControls.ThemeKeys(slot, slot.Default));
            Assert.True(slot.Variants.Count >= 2, $"{slot.Name} offers nothing to choose");
            Assert.NotEmpty(slot.ResourceKeys);
            foreach ((string variant, IReadOnlyList<string>? themes) in slot.Variants)
            {
                if (variant == slot.Default) { Assert.Null(themes); continue; }
                // One drawing per control the slot covers, so a slot cannot
                // restyle a button and leave the toggle beside it alone.
                Assert.NotNull(themes);
                Assert.Equal(slot.ResourceKeys.Count, themes!.Count);
                Assert.All(themes, t => Assert.NotEmpty(t));
            }
            Assert.Same(slot, SkinControls.Find(slot.Name));
        }
        Assert.Null(SkinControls.Find("nonesuch"));
    }

    [Fact]
    public void TheOpenDeckSkinChoosesAnAppearanceForEveryControl()
    {
        SkinPackage openDeck = SkinCatalog.BuiltIn().Single(e => e.Id == "opendeck").Package;
        // Deck keeps the tick box and its source chips as keys: a check box as
        // a key and a chip as a badge are for skins that draw every switch as
        // a key and every indicator as a display, and Deck's look predates them.
        foreach (SkinControlSlot slot in SkinControls.Slots.Where(s => s.Name is not ("check" or "chip")))
        {
            string variant = Assert.Contains(slot.Name, openDeck.Controls);
            Assert.NotEqual(slot.Default, variant);
            Assert.NotNull(SkinControls.ThemeKeys(slot, variant));
        }
        // The two shipped appearances differ in shape, not only in colour.
        Assert.Equal("segmented", openDeck.Controls["meter"]);
        Assert.Equal("console", openDeck.Controls["fader"]);
        // The raised key face covers the plain buttons and the toggles as well
        // as the mute, so no rectangular control is left flat beside a key.
        Assert.Equal("cap", openDeck.Controls["button"]);
        Assert.Equal("cap", openDeck.Controls["mute"]);
    }

    [Fact]
    public void AnUnknownControlOrAnUnknownAppearanceIsReportedAndIgnored()
    {
        SkinReadResult unknownControl = Read("""
            {"schema":1,"tokens":{},"controls":{"windshield":"console"}}
            """);
        Assert.Contains("windshield", Assert.Single(unknownControl.Errors));
        Assert.Empty(unknownControl.Package!.Controls);

        SkinReadResult unknownVariant = Read("""
            {"schema":1,"tokens":{},"controls":{"meter":"holographic","led":"lamp"}}
            """);
        string error = Assert.Single(unknownVariant.Errors);
        Assert.Contains("holographic", error);
        Assert.Contains("\"continuous\"", error);   // the message says what was expected
        // The control that named a real appearance still gets it.
        Assert.Equal(["led"], unknownVariant.Package!.Controls.Keys);

        Assert.Single(Read("""{"schema":1,"tokens":{},"controls":{"meter":7}}""").Errors);
        Assert.Single(Read("""{"schema":1,"tokens":{},"controls":"segmented"}""").Errors);
    }

    [Fact]
    public void ASkinWithNoControlsSectionKeepsEveryBuiltInAppearance()
    {
        SkinPackage package = Read(Doc(Tok("Ox.Text.Primary", "\"#010203\""))).Package!;
        Assert.Empty(package.Controls);
        foreach (SkinControlSlot slot in SkinControls.Slots)
            Assert.Null(SkinControls.ThemeKeys(slot, package.Controls.GetValueOrDefault(slot.Name)));
    }

    // --- discovery ---

    [Fact]
    public void SkinsComeFromTheUserFirstThenTheSystemThenTheApplication()
    {
        Skin("home", "mine", Doc(Tok("Ox.Text.Primary", "\"#111111\"")));
        Skin("home", "opendeck", Doc(Tok("Ox.Text.Primary", "\"#222222\"")));
        Skin("system", "shared", Doc(Tok("Ox.Text.Primary", "\"#333333\"")));
        Skin("system", "opendeck", Doc(Tok("Ox.Text.Primary", "\"#444444\"")));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "system"));

        IReadOnlyList<SkinEntry> found = SkinCatalog.Discover();
        Assert.Equal("default", found[0].Id);
        Assert.Contains(found, e => e.Id == "mine");
        Assert.Contains(found, e => e.Id == "shared");

        // The user's opendeck wins over the system's, which wins over the built-in.
        SkinEntry openDeck = found.Single(e => e.Id == "opendeck");
        Assert.Equal(SkinOrigin.User, openDeck.Package.Origin);
        Assert.Equal(Color.Parse("#222222"), ((SkinSolid)openDeck.Package.Tokens["Ox.Text.Primary"]).Color);

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "empty"));
        openDeck = SkinCatalog.Discover().Single(e => e.Id == "opendeck");
        Assert.Equal(SkinOrigin.System, openDeck.Package.Origin);

        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "empty"));
        openDeck = SkinCatalog.Discover().Single(e => e.Id == "opendeck");
        Assert.Equal(SkinOrigin.BuiltIn, openDeck.Package.Origin);
    }

    [Fact]
    public void AFolderWithNoSkinJsonIsNotASkinAndABrokenOneSaysWhy()
    {
        Directory.CreateDirectory(Path.Combine(_root, "home", "openxlr", "skins", "empty"));
        Skin("home", "broken", "{ not json");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "none"));

        IReadOnlyList<SkinEntry> found = SkinCatalog.Discover();
        Assert.DoesNotContain(found, e => e.Id == "empty");
        SkinEntry broken = Assert.Single(found, e => e.Id == "broken");
        Assert.NotEmpty(broken.Errors);
        // A broken package still applies as the default appearance rather than
        // leaving the window half painted.
        Assert.Empty(broken.Package.Tokens);
    }

    /// <summary>
    /// An installed skin carries its layout through the catalogue to the
    /// service the window reads. A refused layout reaches the same error list
    /// Options shows for a bad token, with its JSON path, and costs the skin
    /// nothing else.
    /// </summary>
    [Fact]
    public void AnInstalledLayoutReachesTheServiceAndARefusedOneIsReportedThere()
    {
        const string Regions = """
            [{"kind":"region","region":"monitor"},{"kind":"region","region":"inputs"},
             {"kind":"region","region":"headphones"},{"kind":"region","region":"applications"},
             {"kind":"region","region":"submixer"}]
            """;
        Skin("home", "wide", """{"schema":1,"tokens":{"Ox.Text.Primary":"#111111"},"layout":{"version":1,"main":"""
            + """{"kind":"stack","id":"row","orientation":"horizontal","children":""" + Regions + "}}}");
        Skin("home", "misplaced", """{"schema":1,"tokens":{"Ox.Text.Primary":"#222222"},"layout":{"version":1,"main":"""
            + """{"kind":"stack","id":"row","orientation":"horizontal","children":""" + Regions.Replace("\"headphones\"", "\"phones\"") + "}}}");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "none"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "config"));

        SkinEntry wide = SkinCatalog.Find("wide")!;
        Assert.Empty(wide.Errors);
        Assert.Equal(SkinLayoutOrientation.Horizontal, Assert.IsType<SkinLayoutStack>(wide.Package.Layout!.Main).Orientation);
        Assert.Equal("monitor", wide.Package.Layout.Main.RegionOrder.First());

        SkinEntry misplaced = SkinCatalog.Find("misplaced")!;
        Assert.Null(misplaced.Package.Layout);
        Assert.Equal(Color.Parse("#222222"), ((SkinSolid)misplaced.Package.Tokens["Ox.Text.Primary"]).Color);
        Assert.StartsWith("layout.main.children[2].region: ", Assert.Single(misplaced.Errors), StringComparison.Ordinal);

        try
        {
            SkinService.Apply(wide);
            Assert.Same(wide.Package.Layout, SkinService.Layout);
            IReadOnlyList<string> errors = SkinService.Apply(misplaced);
            Assert.Null(SkinService.Layout);
            Assert.Equal(misplaced.Errors, errors);
            Assert.Equal(misplaced.Errors, SkinService.Errors);
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        }
        Assert.Null(SkinService.Layout);
    }

    [Fact]
    public void AnUnknownIdFallsBackToTheDefault()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "none"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "none"));
        Assert.Null(SkinCatalog.Find("no-such-skin"));
        Assert.Equal("default", SkinCatalog.Find(null)!.Id);
        Assert.Equal("default", SkinCatalog.Find("")!.Id);
        Assert.Equal("default", SkinCatalog.Find("default")!.Id);
    }

    // --- where the choice lives ---

    [Fact]
    public void TheChoiceIsKeptInUiJsonAndNowhereElse()
    {
        string config = Path.Combine(_root, "config");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        new UiSettings { MinimizeToTray = true, Skin = "opendeck" }.Save();

        UiSettings reloaded = UiSettings.Load();
        Assert.Equal("opendeck", reloaded.Skin);
        Assert.True(reloaded.MinimizeToTray);

        string[] written = Directory.GetFiles(Path.Combine(config, "openxlr"));
        Assert.Equal(["ui.json"], written.Select(Path.GetFileName));
        Assert.Contains("\"skin\": \"opendeck\"", File.ReadAllText(Path.Combine(config, "openxlr", "ui.json")));
    }

    /// <summary>
    /// The way back from a skin that made something unreadable: one launch
    /// with the variable set ignores the saved choice, and Options can say the
    /// launch decided it. Nothing is written, so the next launch is back to
    /// the saved skin.
    /// </summary>
    [Fact]
    public void TheLaunchOverrideBeatsTheSavedChoiceWithoutChangingIt()
    {
        string config = Path.Combine(_root, "config");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "none"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "none"));
        new UiSettings { Skin = "opendeck" }.Save();
        string? previous = Environment.GetEnvironmentVariable(SkinService.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, null);
            SkinService.Initialize();
            Assert.False(SkinService.Overridden);
            Assert.Equal("opendeck", SkinService.Current.Id);

            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, "default");
            SkinService.Initialize();
            Assert.True(SkinService.Overridden);
            Assert.Equal("default", SkinService.Current.Id);
            Assert.Equal("opendeck", UiSettings.Load().Skin);   // the saved choice is untouched

            // An override naming a skin that is not installed still lands on
            // something usable rather than leaving the window unpainted.
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, "no-such-skin");
            SkinService.Initialize();
            Assert.Equal("default", SkinService.Current.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, previous);
        }
    }

    // --- the starting skin ---

    /// <summary>
    /// A launch with its own preferences and skin folders, no override, and
    /// Material put back afterwards for the tests that follow.
    /// </summary>
    private string StartingFixture()
    {
        string config = Path.Combine(_root, "config");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "home"));
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(_root, "none"));
        Environment.SetEnvironmentVariable(SkinService.OverrideVariable, null);
        return Path.Combine(config, "openxlr", "ui.json");
    }

    private static void Launch(string? overrideId = null)
    {
        string? previous = Environment.GetEnvironmentVariable(SkinService.OverrideVariable);
        Environment.SetEnvironmentVariable(SkinService.OverrideVariable, overrideId);
        try { SkinService.Initialize(); }
        finally { Environment.SetEnvironmentVariable(SkinService.OverrideVariable, previous); }
    }

    private static void PutMaterialBack() => SkinService.Apply(new SkinEntry(SkinPackage.Default, []));

    /// <summary>A new install: no ui.json at all starts on Broadcast's layout, and writes nothing.</summary>
    [Fact]
    public void ANewInstallStartsOnBroadcastWithoutWritingAnything()
    {
        string uiJson = StartingFixture();
        try
        {
            Launch();
            Assert.Equal(SkinPackage.StartingId, SkinService.Current.Id);
            Assert.Equal("broadcast", SkinPackage.StartingId);
            Assert.Equal("Broadcast", SkinService.Current.Name);
            Assert.NotNull(SkinService.Layout);
            Assert.False(SkinService.Overridden);
            Assert.False(SkinService.CanChooseMode);
            Assert.Empty(SkinService.Errors);
            Assert.False(File.Exists(uiJson));
            Assert.False(Directory.Exists(Path.GetDirectoryName(uiJson)));
        }
        finally { PutMaterialBack(); }
    }

    /// <summary>
    /// An install whose user never picked a skin moves to Broadcast too, on
    /// every start, and the file is left byte for byte as it was: the choice
    /// is still the user's to make.
    /// </summary>
    [Theory]
    [InlineData("""{ "minimizeToTray": true, "appearanceMode": "light", "futureChoice": { "density": "touch" } }""")]
    [InlineData("""{ "skin": null, "minimizeToTray": true }""")]
    [InlineData("""{ "skin": "", "minimizeToTray": true }""")]
    public void AnInstallThatNeverChoseASkinStartsOnBroadcastAndKeepsItsFile(string text)
    {
        string uiJson = StartingFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(uiJson)!);
        File.WriteAllText(uiJson, text);
        DateTime written = File.GetLastWriteTimeUtc(uiJson);
        try
        {
            for (int start = 0; start < 2; start++)
            {
                Launch();
                Assert.Equal("broadcast", SkinService.Current.Id);
                Assert.NotNull(SkinService.Layout);
                Assert.Equal(text, File.ReadAllText(uiJson));
                Assert.Equal(written, File.GetLastWriteTimeUtc(uiJson));
            }
            Assert.True(UiSettings.Load().MinimizeToTray);
            Assert.Equal(["ui.json"], Directory.GetFiles(Path.GetDirectoryName(uiJson)!).Select(Path.GetFileName));
        }
        finally { PutMaterialBack(); }
    }

    /// <summary>
    /// A skin the user saved is the one worn, and that includes Material:
    /// "default" in ui.json is a deliberate pick, so Material with its mode
    /// stays where it was.
    /// </summary>
    [Theory]
    [InlineData("default", "Material")]
    [InlineData("opendeck", "Deck")]
    [InlineData("nord", "Nord")]
    [InlineData("broadcast", "Broadcast")]
    public void ASavedSkinIsKept(string id, string name)
    {
        string uiJson = StartingFixture();
        Assert.Null(new UiSettings { Skin = id, AppearanceMode = AppearanceModes.Light }.Save());
        string text = File.ReadAllText(uiJson);
        try
        {
            Launch();
            Assert.Equal(id, SkinService.Current.Id);
            Assert.Equal(name, SkinService.Current.Name);
            Assert.Equal(id == SkinPackage.DefaultId, SkinService.CanChooseMode);
            Assert.Equal(AppearanceModes.Light, SkinService.Mode);
            Assert.Equal(text, File.ReadAllText(uiJson));
        }
        finally { PutMaterialBack(); }
    }

    /// <summary>
    /// Material chosen in Options is saved by its id, so the next start keeps
    /// it rather than reading no choice as Broadcast.
    /// </summary>
    [Fact]
    public void ChoosingMaterialIsSavedAndSurvivesTheNextStart()
    {
        StartingFixture();
        Assert.Null(new UiSettings { MinimizeToTray = true, Skin = "opendeck" }.Save());
        try
        {
            Launch();
            Assert.Equal("opendeck", SkinService.Current.Id);
            SkinService.Choose(SkinPackage.DefaultId, out string? saveError);
            Assert.Null(saveError);
            Assert.Equal("default", UiSettings.Load().Skin);
            Assert.True(UiSettings.Load().MinimizeToTray);
            Launch();
            Assert.Equal("default", SkinService.Current.Id);
            Assert.True(SkinService.CanChooseMode);
        }
        finally { PutMaterialBack(); }
    }

    /// <summary>
    /// OPENXLR_SKIN=default is still the way back to Material, on a new
    /// install as much as over a saved skin, and nothing is written.
    /// </summary>
    [Fact]
    public void TheMaterialOverrideBeatsTheStartingSkin()
    {
        string uiJson = StartingFixture();
        try
        {
            Launch("default");
            Assert.True(SkinService.Overridden);
            Assert.Equal("default", SkinService.Current.Id);
            Assert.Null(SkinService.Layout);
            Assert.False(File.Exists(uiJson));

            Assert.Null(new UiSettings { Skin = "broadcast" }.Save());
            Launch("default");
            Assert.Equal("default", SkinService.Current.Id);
            Assert.Equal("broadcast", UiSettings.Load().Skin);

            // Any other id works as before, and an empty one is no override.
            Launch("nord");
            Assert.Equal("nord", SkinService.Current.Id);
            Launch("");
            Assert.False(SkinService.Overridden);
            Assert.Equal("broadcast", SkinService.Current.Id);
        }
        finally { PutMaterialBack(); }
    }

    /// <summary>
    /// A user's Broadcast folder shadows the built-in one. When its document
    /// cannot be read the window starts on Material instead of wearing an
    /// empty placeholder, and nothing is written; a readable copy is worn.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schema":2,"name":"Broadcast"}""")]
    [InlineData("oversized")]
    public void AnUnreadableBroadcastFallsBackToMaterial(string document)
    {
        string uiJson = StartingFixture();
        if (document == "oversized") document = new string(' ', SkinFormat.MaxDocumentBytes + 1);
        Skin("home", "broadcast", document);
        try
        {
            SkinEntry shadow = SkinCatalog.Find("broadcast")!;
            Assert.False(shadow.Usable);
            Assert.NotEmpty(shadow.Errors);
            Launch();
            Assert.Equal("default", SkinService.Current.Id);
            Assert.Null(SkinService.Layout);
            Assert.False(File.Exists(uiJson));
        }
        finally { PutMaterialBack(); }
    }

    [Fact]
    public void AReadableBroadcastCopyIsWorn()
    {
        StartingFixture();
        Skin("home", "broadcast", Doc(Tok("Ox.Card.Background", "\"#123456\"")));
        try
        {
            Launch();
            Assert.Equal("broadcast", SkinService.Current.Id);
            Assert.Equal(SkinOrigin.User, SkinService.Current.Package.Origin);
        }
        finally { PutMaterialBack(); }
    }

    // --- accessibility ---

    /// <summary>
    /// Text has to stay readable on the surface it lands on, in every
    /// appearance the application ships. The contrast is measured against the
    /// merged result a skin actually produces: the skin's own surface where it
    /// sets one, the default where it does not, and the worst stop of a
    /// gradient rather than its average.
    /// </summary>
    [Fact]
    public void EveryShippedAppearanceKeepsItsTextReadable()
    {
        // Every appearance against every pair: a failure names all of them, so
        // a skin is fixed in one pass rather than one colour per test run.
        var thin = new List<string>();
        foreach (SkinEntry entry in new[] { new SkinEntry(SkinPackage.Default, []), SkinService.MaterialLight }
                     .Concat(SkinCatalog.BuiltIn()))
        {
            foreach (string surface in new[] { "Ox.Window.Background", "Ox.Card.Background", "Ox.Tile.Background" })
                foreach ((string ink, double least) in new[]
                         {
                             ("Ox.Text.Primary", 4.5), ("Ox.Text.Secondary", 4.5), ("Ox.Text.Detail", 4.5),
                             ("Ox.Text.Muted", 3.0), ("Ox.Text.Warning", 3.0), ("Ox.Text.Alert", 3.0),
                             ("Ox.Text.Info", 3.0), ("Ox.Link.Foreground", 3.0),
                         })
                {
                    double ratio = Worst(Colours(entry, ink), Colours(entry, surface));
                    if (ratio < least)
                        thin.Add($"{entry.Id}: {ink} on {surface} is {ratio:F2}:1, under {least}:1");
                }

            // The indicators have to be told apart from the surface they sit on.
            foreach (string led in new[] { "Ox.Led.On", "Ox.Led.Alert", "Ox.Meter.Fill" })
                if (Worst(Colours(entry, led), Colours(entry, "Ox.Tile.Background")) < 3.0)
                    thin.Add($"{entry.Id}: {led} does not stand out on a strip");
        }
        Assert.True(thin.Count == 0, string.Join("\n", thin));
    }

    /// <summary>
    /// The Material light palette and the Wave and Nyan Pink palettes draw their meter zones:
    /// each one stands out on the track behind it and the
    /// three are told apart. An alert does not read as a warning either.
    /// </summary>
    [Fact]
    public void MixerPalettesKeepTheirMeterZonesAndAlertsApart()
    {
        foreach (SkinEntry skin in new[] { SkinService.MaterialLight, SkinCatalog.Find("wave")!, SkinCatalog.Find("nyan-pink")! })
        {
            Color fill = Colours(skin, "Ox.Meter.Fill")[0];
            Color warning = Colours(skin, "Ox.Meter.Warning")[0];
            Color hot = Colours(skin, "Ox.Meter.Hot")[0];
            Assert.Equal(3, new[] { fill, warning, hot }.Distinct().Count());
            foreach (string zone in new[] { "Ox.Meter.Fill", "Ox.Meter.Warning", "Ox.Meter.Hot" })
                Assert.True(Worst(Colours(skin, zone), Colours(skin, "Ox.Meter.Track")) >= 3.0,
                    $"{skin.Id}: {zone} does not stand out on the meter track");
            Assert.NotEqual(Colours(skin, "Ox.Text.Warning")[0], Colours(skin, "Ox.Text.Alert")[0]);
        }
    }

    [Theory]
    [InlineData("wave")]
    [InlineData("nyan-pink")]
    public void WaveAndNyanPinkKeepDarkSurfacesAndLightLettering(string id)
    {
        SkinEntry skin = Assert.Single(SkinCatalog.BuiltIn(), entry => entry.Id == id);
        foreach (string surface in new[] { "Ox.Window.Background", "Ox.Card.Background", "Ox.Tile.Background", "Ox.Dialog.Background" })
            Assert.All(Colours(skin, surface), colour => Assert.True(Luminance(colour) < .08, $"{id}: {surface} is not dark"));
        Assert.All(Colours(skin, "Ox.Text.Primary"), colour => Assert.True(Luminance(colour) > .5, $"{id}: primary text is not light"));
    }

    /// <summary>
    /// The lettering on a control has to be readable on the face behind it, and
    /// the states have to be told apart: lit legends carry text contrast, the
    /// deliberately dim "switched off" legend keeps the lower non-text bar, and
    /// disabled is dimmer still rather than merely different.
    /// </summary>
    [Fact]
    public void ControlLetteringStaysReadableOnItsFace()
    {
        var thin = new List<string>();
        foreach (SkinEntry entry in SkinCatalog.BuiltIn())
        {
            foreach ((string ink, string face, double least) in new[]
                     {
                         ("Ox.Control.Foreground", "Ox.Control.Background", 4.5),
                         ("Ox.Control.ForegroundChecked", "Ox.Control.BackgroundChecked", 4.5),
                         ("Ox.Toggle.ForegroundChecked", "Ox.Control.BackgroundChecked", 4.5),
                         ("Ox.Bypass.Foreground", "Ox.Control.Background", 4.5),
                         ("Ox.Bypass.ForegroundBypassed", "Ox.Control.BackgroundChecked", 4.5),
                         ("Ox.Mute.Foreground", "Ox.Mute.Background", 4.5),
                         ("Ox.Mute.ForegroundChecked", "Ox.Mute.BackgroundChecked", 4.5),
                         // A muted send's mute in a tinted matrix cell is lettered in its own colour.
                         ("Ox.Matrix.MutedForeground", "Ox.Mute.BackgroundChecked", 4.5),
                         ("Ox.Danger.Foreground", "Ox.Danger.Background", 4.5),
                         ("Ox.Check.Foreground", "Ox.Card.Background", 4.5),
                         // A switched-off toggle is lettered, not greyed out, so
                         // it is held to the text bar like the rest.
                         ("Ox.Toggle.Foreground", "Ox.Control.Background", 4.5),
                     })
            {
                if (!entry.Package.Tokens.ContainsKey(ink) || !entry.Package.Tokens.ContainsKey(face)) continue;
                double ratio = Worst(Colours(entry, ink), Colours(entry, face));
                if (ratio < least) thin.Add($"{entry.Id}: {ink} on {face} is {ratio:F2}:1, under {least}:1");
            }

            // A switched-off toggle and an unavailable action are both red, so
            // the difference between them cannot be the colour. It is the fade,
            // which the control themes apply and the rendered comparison in the
            // desktop tests checks; here it only has to be asked for.
            if (entry.Package.Tokens.ContainsKey("Ox.Toggle.ForegroundDisabled")
                && ((SkinNumber)entry.Package.Tokens["Ox.Control.DisabledOpacity"]).Value >= 1)
                thin.Add($"{entry.Id}: a disabled key does not fade, so it reads as merely switched off");
        }
        Assert.True(thin.Count == 0, string.Join("\n", thin));
    }

    /// <summary>
    /// The meter's scale is RMS dBFS with 0 at -60 and 1 at 0 (MeterReader), so
    /// the zone thresholds have to be sane dB values in that mapping rather
    /// than numbers that happened to look right.
    /// </summary>
    [Fact]
    public void TheMeterZoneThresholdsAreSensibleDecibels()
    {
        static double Db(double level) => level * 60 - 60;

        double defaultWarning = (double)SkinTokens.Find("Ox.Meter.WarningLevel")!.Default!;
        double defaultHot = (double)SkinTokens.Find("Ox.Meter.HotLevel")!.Default!;
        Assert.Equal(-18, Db(defaultWarning), 6);   // the usual alignment level
        Assert.Equal(-6, Db(defaultHot), 6);        // RMS this high has no headroom left
        Assert.True(defaultWarning < defaultHot);

        // The shipped appearance draws no zones at all: all three colours match,
        // so a meter looks exactly as it always did.
        Assert.Equal(((ISolidColorBrush)SkinTokens.Find("Ox.Meter.Fill")!.Default!).Color,
            ((ISolidColorBrush)SkinTokens.Find("Ox.Meter.Warning")!.Default!).Color);
        Assert.Equal(((ISolidColorBrush)SkinTokens.Find("Ox.Meter.Fill")!.Default!).Color,
            ((ISolidColorBrush)SkinTokens.Find("Ox.Meter.Hot")!.Default!).Color);

        SkinPackage openDeck = SkinCatalog.BuiltIn().Single(e => e.Id == "opendeck").Package;
        double warning = ((SkinNumber)openDeck.Tokens["Ox.Meter.WarningLevel"]).Value;
        double hot = ((SkinNumber)openDeck.Tokens["Ox.Meter.HotLevel"]).Value;
        Assert.InRange(Db(warning), -24, -12);
        Assert.InRange(Db(hot), -9, -3);
        Assert.True(warning < hot);
    }

    private static IReadOnlyList<Color> Colours(SkinPackage package, string token) =>
        Colours(new SkinEntry(package, []), token);

    private static IReadOnlyList<Color> Colours(SkinEntry entry, string token)
    {
        SkinValue? value = entry.Package.Tokens.GetValueOrDefault(token);
        if (value is null)
            return [((ISolidColorBrush)SkinTokens.Find(token)!.Default!).Color];
        return value switch
        {
            SkinSolid solid => [solid.Color],
            SkinGradient gradient => [.. gradient.Stops.Select(s => s.Color)],
            _ => throw new InvalidOperationException($"{token} is not a colour in {entry.Id}"),
        };
    }

    private static double Worst(IReadOnlyList<Color> ink, IReadOnlyList<Color> ground) =>
        ink.SelectMany(_ => ground, Contrast).Min();

    private static double Contrast(Color a, Color b)
    {
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    [Fact]
    public void IconsAreNamedPathDataAndABadEntryCostsOnlyItself()
    {
        SkinReadResult result = Read("""
            {"schema":1,"tokens":{},"icons":{
              "mic":{"path":"M0 0h24v24h-24z"},
              "on-air":{"path":"M2 6h20v12h-20z","colour":"red"},
              "none":{"path":"M0 0h1v1z"},
              "Bad":{"path":"M0 0h1v1z"},
              "no-path":{"d":"M0 0h1v1z"},
              "markup":{"path":"<path d='M0 0'/>"},
              "twice":{"path":"M0 0h1v1z"},
              "twice":{"path":"M0 0h2v2z"},
              "string":"M0 0h1v1z"
            }}
            """);
        SkinPackage package = Assert.IsType<SkinPackage>(result.Package);
        Assert.Equal(["mic", "on-air"], package.Icons.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("M0 0h24v24h-24z", package.Icons["mic"]);
        foreach (string expected in new[] { "icons.on-air: \"colour\"", "icons: \"none\"", "icons: \"Bad\"", "icons.no-path:",
                     "icons.markup:", "icons.twice: is given twice", "icons.string:" })
            Assert.Contains(result.Errors, e => e.StartsWith(expected, StringComparison.Ordinal));
        Assert.Equal(7, result.Errors.Count);
    }

    [Fact]
    public void IconsAreBoundedInNumberAndLength()
    {
        string many = string.Join(",", Enumerable.Range(0, SkinFormat.MaxIcons + 3).Select(n => $"\"i{n}\":{{\"path\":\"M0 0h1v1z\"}}"));
        SkinReadResult result = Read("{\"schema\":1,\"tokens\":{},\"icons\":{" + many + "}}");
        Assert.Equal(SkinFormat.MaxIcons, result.Package!.Icons.Count);
        Assert.Contains(result.Errors, e => e.StartsWith($"icons: a skin may define at most {SkinFormat.MaxIcons} icons", StringComparison.Ordinal));
        Assert.Single(result.Errors);

        string longPath = "M0 0" + string.Concat(Enumerable.Repeat("h1", SkinFormat.MaxIconPathLength / 2));
        result = Read("{\"schema\":1,\"tokens\":{},\"icons\":{\"long\":{\"path\":\"" + longPath + "\"}}}");
        Assert.Empty(result.Package!.Icons);
        Assert.Contains(result.Errors, e => e.StartsWith("icons.long: the path is longer", StringComparison.Ordinal));

        result = Read("""{"schema":1,"tokens":{},"icons":["mic"]}""");
        Assert.Empty(result.Package!.Icons);
        Assert.Contains(result.Errors, e => e.StartsWith("\"icons\" must be an object", StringComparison.Ordinal));
        Assert.Empty(Read("""{"schema":1,"tokens":{}}""").Package!.Icons);
    }

    [Fact]
    public void WearingASkinPublishesItsIconsToTheWindowAndTakingItOffWithdrawsThem()
    {
        SkinPackage package = Read("""{"schema":1,"tokens":{},"icons":{"on-air":{"path":"M2 6h20v12h-20z"}}}""").Package!;
        try
        {
            SkinService.Apply(new SkinEntry(package, []));
            Assert.True(LayoutIconRegistry.Knows("on-air"));
            Assert.Equal("on-air", OpenXLR.UI.LayoutIcons.Resolve("on-air", "apps", LayoutIconRegistry.Knows));
        }
        finally { SkinService.Apply(new SkinEntry(SkinPackage.Default, [])); }
        Assert.False(LayoutIconRegistry.Knows("on-air"));
        Assert.True(LayoutIconRegistry.Knows("mic"));
    }
}
