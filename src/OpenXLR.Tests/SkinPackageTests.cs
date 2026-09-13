using Avalonia;
using Avalonia.Media;
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
    public void BuiltInSkinsReadWithoutComplaint()
    {
        SkinEntry[] built = [.. SkinCatalog.BuiltIn()];
        SkinEntry openDeck = Assert.Single(built, e => e.Id == "opendeck");
        Assert.Empty(openDeck.Errors);
        Assert.Equal("Deck", openDeck.Name);
        // The skin is a whole appearance, not a recolour of a few labels.
        Assert.True(openDeck.Package.Tokens.Count > 40, $"only {openDeck.Package.Tokens.Count} tokens");

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
    public void ABuiltInSkinCannotUseAnImageBecauseItCarriesNoFiles()
    {
        Assert.Contains("carries no files", Assert.Single(Read(Doc("""
            "Ox.Card.Background": {"type":"image","source":"x.png"}
            """)).Errors));
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
        foreach (SkinControlSlot slot in SkinControls.Slots)
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

    // --- accessibility ---

    /// <summary>
    /// Text has to stay readable on the surface it lands on, in every
    /// appearance the application ships. The contrast is measured against the
    /// merged result a skin actually produces: the skin's own surface where it
    /// sets one, the default where it does not, and the worst stop of a
    /// gradient rather than its average.
    /// </summary>
    [Fact]
    public void BothShippedAppearancesKeepTheirTextReadable()
    {
        foreach (SkinEntry entry in new[] { new SkinEntry(SkinPackage.Default, []) }
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
                    Assert.True(ratio >= least,
                        $"{entry.Id}: {ink} on {surface} is {ratio:F2}:1, under {least}:1");
                }

            // The indicators have to be told apart from the surface they sit on.
            foreach (string led in new[] { "Ox.Led.On", "Ox.Led.Alert", "Ox.Meter.Fill" })
                Assert.True(Worst(Colours(entry, led), Colours(entry, "Ox.Tile.Background")) >= 3.0,
                    $"{entry.Id}: {led} does not stand out on a strip");
        }
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
                         ("Ox.Danger.Foreground", "Ox.Danger.Background", 4.5),
                         ("Ox.Check.Foreground", "Ox.Card.Background", 4.5),
                         // A switched-off toggle is lettered, not greyed out, so
                         // it is held to the text bar like the rest.
                         ("Ox.Toggle.Foreground", "Ox.Control.Background", 4.5),
                     })
            {
                if (!entry.Package.Tokens.ContainsKey(ink) || !entry.Package.Tokens.ContainsKey(face)) continue;
                double ratio = Worst(Colours(entry, ink), Colours(entry, face));
                Assert.True(ratio >= least, $"{entry.Id}: {ink} on {face} is {ratio:F2}:1, under {least}:1");
            }

            // A switched-off toggle and an unavailable action are both red, so
            // the difference between them cannot be the colour. It is the fade,
            // which the control themes apply and the rendered comparison in the
            // desktop tests checks; here it only has to be asked for.
            if (entry.Package.Tokens.ContainsKey("Ox.Toggle.ForegroundDisabled"))
                Assert.True(((SkinNumber)entry.Package.Tokens["Ox.Control.DisabledOpacity"]).Value < 1,
                    $"{entry.Id}: a disabled key does not fade, so it reads as merely switched off");
        }
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
}
