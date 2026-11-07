using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// docs/skins.md and docs/skin.schema.json are what a skin author writes
/// against, so they are held to the code rather than to a reviewer's memory:
/// every token with its kind, its default and its bounds, every control with
/// the appearances it can take, and the limits a package is read under. A
/// token added without its row, or a default changed in one place only, fails
/// here rather than misleading someone six months later.
/// </summary>
public sealed class SkinDocumentTests
{
    /// <summary>
    /// The application's resources hold the skin tokens. Window text is read
    /// from its catalogue straight into the control, so a catalogue entry can
    /// never take the place of a token, whatever its key.
    /// </summary>
    [Fact]
    public void WindowTextStaysOutOfTheResourcesTheSkinTokensOwn()
    {
        var english = Localizer.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!
            .Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToArray();
        Assert.NotEmpty(english);
        foreach (string key in english)
        {
            Assert.False(SkinTokens.Exists(key), key);
            Assert.False(key.StartsWith("Ox.", StringComparison.Ordinal), key);
            Assert.Equal(Localizer.Text(key), new TextExtension { Key = key }.ProvideValue(null!));
        }
    }

    [Fact]
    public void GeneratedDeckDefaultsMatchTheWindowAndEveryShippedSkinIsIncluded()
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,
            "plugin", "com.emaspa.openxlr.sdPlugin", "skin-palettes.json")));
        JsonElement defaults = file.RootElement.GetProperty("default");
        Assert.Equal(DeckPalette.Names.Order(), defaults.EnumerateObject().Select(p => p.Name).Order());
        foreach (JsonProperty value in defaults.EnumerateObject())
        {
            object? expected = SkinTokens.Find(value.Name)!.Default;
            if (expected is ISolidColorBrush brush)
                Assert.Equal(brush.Color, Color.Parse(value.Value.GetString()!));
            else Assert.Equal((double)expected!, value.Value.GetDouble());
        }
        foreach (SkinEntry skin in SkinCatalog.BuiltIn())
            Assert.True(file.RootElement.TryGetProperty(skin.Id, out _), skin.Id);
    }

    /// <summary>One row of a token table: token, kind, default, range, what it paints.</summary>
    private sealed record Row(string Token, string Kind, string Default, string Range, string Text);

    private static string Root
    {
        get
        {
            // src/<project>/bin/... in a plain build, dist/<name>/bin/<project>/...
            // with an artifacts path: walk to the repository either way.
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static string Document => File.ReadAllText(Path.Combine(Root, "docs", "skins.md"));

    private static string Section(string document, string heading, string next)
    {
        int start = document.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
        Assert.True(start > 0, $"docs/skins.md has no \"## {heading}\" section.");
        int end = document.IndexOf($"\n## {next}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"docs/skins.md has no \"## {next}\" after \"## {heading}\".");
        return document[start..end];
    }

    /// <summary>The token tables, which are the one place a token is defined.</summary>
    private static List<Row> TokenRows()
    {
        var rows = new List<Row>();
        foreach (string line in Section(Document, "The tokens", "Images").Split('\n'))
        {
            if (!line.StartsWith("| `Ox.", StringComparison.Ordinal)) continue;
            string[] cells = line.Split('|');
            Assert.True(cells.Length == 7,
                $"a token row needs five columns (token, kind, default, range, what it paints): {line}");
            rows.Add(new Row(cells[1].Trim().Trim('`'), cells[2].Trim(), cells[3].Trim(),
                cells[4].Trim(), cells[5].Trim()));
        }
        return rows;
    }

    private static string Kind(SkinToken token) => token.Kind switch
    {
        SkinTokenKind.Brush => token.SolidOnly ? "colour" : "brush",
        SkinTokenKind.Number => "number",
        SkinTokenKind.CornerRadius => "radius",
        SkinTokenKind.Thickness => "thickness",
        SkinTokenKind.FontFamily => "family",
        _ => "weight",
    };

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The bounds column, empty for a kind that has none.</summary>
    private static string Range(SkinToken token) =>
        token.Kind is SkinTokenKind.Number or SkinTokenKind.CornerRadius or SkinTokenKind.Thickness
            ? $"{Number(token.Minimum)} to {Number(token.Maximum)}"
            : "";

    /// <summary>
    /// True when the default column says what the token's default is. A weight
    /// is compared as a weight, since "SemiBold" is the name an author types
    /// and "DemiBold" is the name the enum prints for the same 600.
    /// </summary>
    private static bool DefaultSaid(SkinToken token, string text) => token.Default switch
    {
        null => text == "framework",
        ISolidColorBrush brush => text == Hex(brush.Color),
        double number => text == Number(number),
        CornerRadius radius => radius.IsUniform && text == Number(radius.TopLeft),
        Thickness thickness => thickness.IsUniform && text == Number(thickness.Left),
        FontWeight weight => Enum.TryParse(text, ignoreCase: true, out FontWeight said) && said == weight,
        FontFamily family => text == family.Name,
        _ => false,
    };

    private static string Expected(SkinToken token) => token.Default switch
    {
        null => "framework",
        ISolidColorBrush brush => Hex(brush.Color),
        double number => Number(number),
        CornerRadius radius => Number(radius.TopLeft),
        Thickness thickness => Number(thickness.Left),
        FontWeight weight => weight.ToString(),
        FontFamily family => family.Name,
        var other => other.ToString() ?? "",
    };

    private static string Hex(Color colour) => colour.A == 255
        ? $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}"
        : $"#{colour.A:x2}{colour.R:x2}{colour.G:x2}{colour.B:x2}";

    [Fact]
    public void EveryTokenHasOneRowWithItsKindDefaultAndBounds()
    {
        List<Row> rows = TokenRows();
        var problems = new List<string>();

        foreach (IGrouping<string, Row> duplicate in rows.GroupBy(r => r.Token, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            problems.Add($"{duplicate.Key}: {duplicate.Count()} rows; a token is defined once.");

        foreach (SkinToken token in SkinTokens.All)
        {
            Row? row = rows.FirstOrDefault(r => r.Token == token.Name);
            if (row is null)
            {
                problems.Add($"{token.Name}: no row in docs/skins.md. Expected "
                    + $"| `{token.Name}` | {Kind(token)} | {Expected(token)} | {Range(token)} | what it paints |");
                continue;
            }
            if (row.Kind != Kind(token)) problems.Add($"{token.Name}: documented as {row.Kind}, is {Kind(token)}.");
            if (!DefaultSaid(token, row.Default))
                problems.Add($"{token.Name}: documented default {row.Default}, is {Expected(token)}.");
            if (row.Range != Range(token))
                problems.Add($"{token.Name}: documented range \"{row.Range}\", is \"{Range(token)}\".");
            if (row.Text.Length == 0) problems.Add($"{token.Name}: the row says nothing about what it paints.");
        }

        foreach (Row row in rows.Where(r => !SkinTokens.Exists(r.Token)))
            problems.Add($"{row.Token}: documented, but this OpenXLR has no such token.");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// A token named in the prose as well: a name that never existed, or one
    /// renamed in the code, reads as a working value and is not.
    /// </summary>
    [Fact]
    public void TheDocumentNamesNoTokenThisVersionDoesNotHave()
    {
        var missing = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(Document, @"`(Ox\.[A-Za-z0-9.]+)`"))
            if (!SkinTokens.Exists(match.Groups[1].Value)) missing.Add(match.Groups[1].Value);
        Assert.True(missing.Count == 0, string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void EveryControlAndEveryAppearanceItCanTakeIsDocumented()
    {
        var documented = new Dictionary<string, (List<string> Variants, string Default)>(StringComparer.Ordinal);
        foreach (string line in Section(Document, "Controls", "Values").Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal)) continue;
            string[] cells = line.Split('|');
            var variants = System.Text.RegularExpressions.Regex.Matches(cells[2], @"`([a-z]+)`")
                .Select(m => m.Groups[1].Value).ToList();
            string fallback = System.Text.RegularExpressions.Regex.Match(cells[2], @"`([a-z]+)` \(default\)")
                .Groups[1].Value;
            documented[cells[1].Trim().Trim('`')] = (variants, fallback);
        }

        var problems = new List<string>();
        foreach (SkinControlSlot slot in SkinControls.Slots)
        {
            if (!documented.TryGetValue(slot.Name, out (List<string> Variants, string Default) row))
            {
                problems.Add($"{slot.Name}: no row under Controls in docs/skins.md.");
                continue;
            }
            if (!row.Variants.Order(StringComparer.Ordinal)
                    .SequenceEqual(slot.Variants.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add($"{slot.Name}: documented as {string.Join(", ", row.Variants)}, "
                    + $"has {string.Join(", ", slot.Variants.Keys)}.");
            if (row.Default != slot.Default)
                problems.Add($"{slot.Name}: documented default \"{row.Default}\", is \"{slot.Default}\".");
        }
        foreach (string name in documented.Keys.Where(n => SkinControls.Find(n) is null))
            problems.Add($"{name}: documented, but this OpenXLR cannot restyle it.");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// The window section as the document and the schema state it: a row per
    /// shape the reader takes, the default marked, the schema's enum and
    /// default the same, and the example a skin the reader takes whole.
    /// </summary>
    [Fact]
    public void TheWindowShapesAreDocumentedAndInTheSchema()
    {
        string section = Section(Document, "Window shape", "Window size");
        string[] rows = [.. section[section.IndexOf("| Shape | What it is |", StringComparison.Ordinal)..].Split('\n').Skip(2)
            .TakeWhile(line => line.StartsWith('|'))
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^\| `([a-z]+)`").Groups[1].Value)];
        Assert.Equal(SkinWindowShapes.All, rows);
        Assert.Contains($"| `{SkinWindowShapes.Default}` (default) |", section, StringComparison.Ordinal);
        Assert.Contains("| `window` | no |", Section(Document, "The file", "Controls"), StringComparison.Ordinal);

        int start = section.IndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        string example = section[start..section.IndexOf("```", start, StringComparison.Ordinal)];
        SkinReadResult read = SkinReader.Read("rounded", example, SkinOrigin.User, null);
        Assert.True(read.Ok, string.Join("\n", read.Errors));
        Assert.Equal(SkinWindowShapes.Rounded, read.Package!.WindowShape);

        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs", "skin.schema.json")));
        JsonElement window = schema.RootElement.GetProperty("properties").GetProperty("window");
        Assert.False(window.GetProperty("additionalProperties").GetBoolean());
        JsonElement shape = window.GetProperty("properties").GetProperty("shape");
        Assert.Equal(SkinWindowShapes.All, shape.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!));
        Assert.Equal(SkinWindowShapes.Default, shape.GetProperty("default").GetString());
        Assert.Equal(["shape", "size"], window.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// The window size as the document and the schema state it: the bounds
    /// the reader holds a size to, in the prose and the schema alike, and the
    /// example a skin the reader takes whole.
    /// </summary>
    [Fact]
    public void TheWindowSizeIsDocumentedAndInTheSchema()
    {
        string section = Section(Document, "Window size", "Layout");
        Assert.Contains($"The width is {SkinWindowSize.MinWidth}\nto {SkinWindowSize.MaxWidth} and the height {SkinWindowSize.MinHeight} to {SkinWindowSize.MaxHeight}",
            section, StringComparison.Ordinal);
        Assert.Contains($"its usual {SkinWindowSize.Default.Width} by {SkinWindowSize.Default.Height}", section, StringComparison.Ordinal);

        int start = section.IndexOf("```json\n", StringComparison.Ordinal) + "```json\n".Length;
        string example = section[start..section.IndexOf("```", start, StringComparison.Ordinal)];
        SkinReadResult read = SkinReader.Read("wide", example, SkinOrigin.User, null);
        Assert.True(read.Ok, string.Join("\n", read.Errors));
        Assert.Equal(new SkinWindowSize(1668, 1174), read.Package!.WindowSize);

        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs", "skin.schema.json")));
        JsonElement size = schema.RootElement.GetProperty("properties").GetProperty("window").GetProperty("properties").GetProperty("size");
        Assert.False(size.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["width", "height"], size.GetProperty("required").EnumerateArray().Select(v => v.GetString()!));
        JsonElement width = size.GetProperty("properties").GetProperty("width"), height = size.GetProperty("properties").GetProperty("height");
        Assert.Equal((SkinWindowSize.MinWidth, SkinWindowSize.MaxWidth), (width.GetProperty("minimum").GetInt32(), width.GetProperty("maximum").GetInt32()));
        Assert.Equal((SkinWindowSize.MinHeight, SkinWindowSize.MaxHeight), (height.GetProperty("minimum").GetInt32(), height.GetProperty("maximum").GetInt32()));
    }

    /// <summary>
    /// The font weight names the document offers are names the reader takes.
    /// They are the toolkit's, and "SemiBold" and "DemiBold" are the same 600,
    /// so the list is worth checking rather than remembering.
    /// </summary>
    [Fact]
    public void TheWeightNamesTheDocumentOffersAreAccepted()
    {
        string values = Section(Document, "Values", "The tokens");
        var names = System.Text.RegularExpressions.Regex.Matches(values, "`\"([A-Za-z]+)\"`")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        foreach (string name in names)
        {
            SkinReadResult result = SkinReader.Read("test",
                "{\"schema\":1,\"tokens\":{\"Ox.Label.FontWeight\":\"" + name + "\"}}",
                SkinOrigin.BuiltIn, null);
            Assert.True(result.Ok, $"{name}: {string.Join("; ", result.Errors)}");
        }
    }

    /// <summary>The numbers in the prose are the limits a package is actually read under.</summary>
    [Fact]
    public void TheDocumentedLimitsAreTheOnesTheReaderEnforces()
    {
        string document = Document;
        var missing = new List<string>();
        void Says(string text)
        {
            if (!document.Contains(text, StringComparison.Ordinal)) missing.Add(text);
        }

        Says($"{SkinFormat.MaxDocumentBytes / 1024} KB");
        Says($"at most {SkinFormat.MaxImages} images");
        Says($"at most {SkinFormat.MaxImageBytes / (1024 * 1024)} MB");
        Says($"at most {SkinFormat.MaxImagePixels} pixels a side");
        Says($"at most {SkinFormat.MaxDecodedPixels / (1024 * 1024)} megapixels");
        Says($"between 2 and {SkinFormat.MaxGradientStops} `stops`");
        Says($"cut at {SkinFormat.MaxTextLength} characters");
        Says($"\"schema\": {SkinFormat.Schema}");
        Says($"OPENXLR_SKIN=default");
        Assert.True(missing.Count == 0, "docs/skins.md no longer states: " + string.Join("; ", missing));
    }

    /// <summary>
    /// The corner radius and thickness bounds are one pair each, which is what
    /// lets the JSON schema describe them once.
    /// </summary>
    [Fact]
    public void RadiiAndThicknessesShareOneSetOfBounds()
    {
        Assert.All(SkinTokens.All.Where(t => t.Kind == SkinTokenKind.CornerRadius),
            t => Assert.Equal((0, SkinTokens.MaxCornerRadius), (t.Minimum, t.Maximum)));
        Assert.All(SkinTokens.All.Where(t => t.Kind == SkinTokenKind.Thickness),
            t => Assert.Equal((0, SkinTokens.MaxThickness), (t.Minimum, t.Maximum)));
    }

    [Fact]
    public void TheJsonSchemaDescribesTheFormatThisVersionReads()
    {
        using JsonDocument schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "docs", "skin.schema.json")));
        JsonElement root = schema.RootElement;
        JsonElement properties = root.GetProperty("properties");
        var problems = new List<string>();

        Assert.Equal(SkinFormat.Schema, properties.GetProperty("schema").GetProperty("const").GetInt32());
        Assert.Equal(SkinFormat.MaxTextLength, properties.GetProperty("name").GetProperty("maxLength").GetInt32());
        Assert.Equal(SkinFormat.MaxGradientStops, root.GetProperty("$defs").GetProperty("gradient")
            .GetProperty("properties").GetProperty("stops").GetProperty("maxItems").GetInt32());
        Assert.Equal(SkinTokens.MaxCornerRadius, root.GetProperty("$defs").GetProperty("radius")
            .GetProperty("oneOf")[0].GetProperty("maximum").GetDouble());
        Assert.Equal(SkinTokens.MaxThickness, root.GetProperty("$defs").GetProperty("thickness")
            .GetProperty("oneOf")[0].GetProperty("maximum").GetDouble());

        JsonElement tokens = properties.GetProperty("tokens").GetProperty("properties");
        Assert.False(properties.GetProperty("tokens").GetProperty("additionalProperties").GetBoolean(),
            "the schema would accept a token name OpenXLR does not have.");

        List<Row> rows = TokenRows();
        foreach (SkinToken token in SkinTokens.All)
        {
            if (!tokens.TryGetProperty(token.Name, out JsonElement entry))
            {
                problems.Add($"{token.Name}: not in docs/skin.schema.json.");
                continue;
            }
            string? reference = entry.TryGetProperty("$ref", out JsonElement r) ? r.GetString() : null;
            string? expected = token.Kind switch
            {
                SkinTokenKind.Brush => token.SolidOnly ? "#/$defs/flatColour" : "#/$defs/brush",
                SkinTokenKind.CornerRadius => "#/$defs/radius",
                SkinTokenKind.Thickness => "#/$defs/thickness",
                SkinTokenKind.FontFamily => "#/$defs/fontFamily",
                SkinTokenKind.FontWeight => "#/$defs/fontWeight",
                _ => null,
            };
            if (expected is not null)
            {
                if (reference != expected) problems.Add($"{token.Name}: schema says {reference}, expected {expected}.");
            }
            else if (entry.GetProperty("minimum").GetDouble() != token.Minimum
                     || entry.GetProperty("maximum").GetDouble() != token.Maximum)
                problems.Add($"{token.Name}: schema bounds are not {Range(token)}.");

            // One description, written once in the document and carried here
            // for the editor's tooltip.
            string? text = entry.TryGetProperty("description", out JsonElement d) ? d.GetString() : null;
            string? documented = rows.FirstOrDefault(row => row.Token == token.Name)?.Text;
            if (text != documented)
                problems.Add($"{token.Name}: the schema's description is not the one in docs/skins.md.");
        }
        foreach (JsonProperty entry in tokens.EnumerateObject().Where(p => !SkinTokens.Exists(p.Name)))
            problems.Add($"{entry.Name}: in docs/skin.schema.json, but this OpenXLR has no such token.");

        JsonElement controls = properties.GetProperty("controls").GetProperty("properties");
        foreach (SkinControlSlot slot in SkinControls.Slots)
        {
            if (!controls.TryGetProperty(slot.Name, out JsonElement entry))
            {
                problems.Add($"{slot.Name}: not in docs/skin.schema.json.");
                continue;
            }
            string[] variants = [.. entry.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!)];
            if (!variants.Order(StringComparer.Ordinal)
                    .SequenceEqual(slot.Variants.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add($"{slot.Name}: the schema lists {string.Join(", ", variants)}.");
            if (entry.GetProperty("default").GetString() != slot.Default)
                problems.Add($"{slot.Name}: the schema's default is not \"{slot.Default}\".");
        }
        foreach (JsonProperty entry in controls.EnumerateObject().Where(p => SkinControls.Find(p.Name) is null))
            problems.Add($"{entry.Name}: in docs/skin.schema.json, but this OpenXLR cannot restyle it.");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// The layout block as the document states it: the version, the
    /// containers and their bounds, the region ids with a row each, the
    /// region options and header actions, the limits, the saved arrangement,
    /// and complete grid and dock examples that the reader takes without
    /// complaint.
    /// </summary>
    [Fact]
    public void TheLayoutSectionStatesWhatTheLayoutReaderTakes()
    {
        string layout = Section(Document, "Layout", "What a skin cannot do");
        var missing = new List<string>();
        void Says(string text)
        {
            if (!layout.Contains(text, StringComparison.Ordinal)) missing.Add(text);
        }
        Says($"\"version\": {SkinFormat.LayoutVersion}");
        Says($"from {Number(SkinFormat.MinLayoutSpacing)} to {Number(SkinFormat.MaxLayoutSpacing)}");
        Says($"{Number(SkinLayoutDefaults.Spacing)}, the classic gap");
        Says($"no more than {SkinFormat.MaxLayoutChildren} children");
        Says($"more than {SkinFormat.MaxLayoutChildren} children in a container");
        Says($"no more than {SkinFormat.MaxLayoutNodes} nodes");
        Says($"nest at most {SkinFormat.MaxLayoutDepth} deep");
        Says($"at most {SkinFormat.MaxLayoutTracks}\nrows and {SkinFormat.MaxLayoutTracks} columns");
        Says($"{1} to {SkinFormat.MaxLayoutTracks} tracks");
        Says($"1 to {SkinFormat.MaxLayoutIdLength} lowercase letters, digits and hyphens");
        Says($"from {Number(SkinFormat.MinLayoutStar)} to {Number(SkinFormat.MaxLayoutStar)}");
        Says($"from {Number(SkinFormat.MinLayoutTrackSize)} to {Number(SkinFormat.MaxLayoutTrackSize)}");
        Says($"from {Number(SkinFormat.MinRegionSize)} to {Number(SkinFormat.MaxRegionSize)}");
        Says($"at least\n{Number(SkinLayoutDefaults.RowSectionMinWidth)} pixels wide");
        Says($"from {SkinFormat.MinVisibleBanks} to {SkinFormat.MaxVisibleBanks}; {SkinSubmixerOptions.DefaultVisibleBanks} when it is missing");
        Says($"from {SkinFormat.MinApplicationsPerColumn} to {SkinFormat.MaxApplicationsPerColumn}; {SkinFormat.DefaultApplicationsPerColumn} when it is missing");
        Says($"at most {SkinFormat.MaxSelectionDefaults} mix ids, each 1 to {SkinFormat.MaxMixIdLength} lowercase letters, digits, hyphens and underscores");
        // Said in words: three strip widths for an input row or a row of a send list.
        Assert.Equal(3, SkinLayoutDefaults.RowStrips);
        Says("an input rack is three times `Ox.Mixer.StripWidth` wide");
        Says("a send list needs three strip widths");
        Says($"{Number(SkinLayoutDefaults.RowStrips * (double)SkinTokens.Find("Ox.Mixer.StripWidth")!.Default! + SkinLayoutDefaults.CardPadding)} pixels with the default tokens");
        Says($"raises\n`Ox.Mixer.StripWidth` to {Number(SkinTokens.TouchMinimums["Ox.Mixer.StripWidth"])}");
        // The matrix: its narrowest width with the default tokens, its values and its rules.
        double cellWidth = (double)SkinTokens.Find("Ox.Matrix.CellWidth")!.Default!;
        double nameWidth = (double)SkinTokens.Find("Ox.Matrix.NameWidth")!.Default!;
        Assert.Equal(1, SkinLayoutDefaults.MatrixMinimumColumns);
        Says($"padding: {Number(nameWidth + cellWidth + cellWidth + SkinLayoutDefaults.MatrixSpacing + SkinLayoutDefaults.CardPadding)} pixels with the default tokens");
        Says("`\"tint\"`, the default, or `\"dim\"`");
        Says("`\"under-fader\"`, the default");
        Says("`fader` hidden in a cell");
        Says("`identity` hidden in a head");
        Says("`skinBankSelections`");
        Says("`mixerDocked`");
        Says("`skinPopOutsApplied`");
        Says($"Below {Number(SkinLayoutDefaults.MonitorStackWidth)} pixels of room the toolbar stands in two rows");
        Says("is the channel's own meter, before its send");
        Says($"The {OpenXLR.UI.UiSettings.MaxSkinArrangements}\n  most recent skins are kept");
        foreach (string word in new[] { "`\"vertical\"`", "`\"horizontal\"`", "`\"stack\"`", "`\"grid\"`", "`\"dock\"`", "`\"region\"`",
                     "`\"auto\"`", "`{ \"star\": n }`", "`{ \"size\": n }`", "`row`", "`column`", "`rowSpan`", "`columnSpan`",
                     "`\"top\"`", "`\"bottom\"`", "`\"left\"`", "`\"right\"`", "`\"fill\"`", "`id`", "`regions`",
                     "`\"visible\"`", "`\"collapsed\"`", "`\"hidden\"`", "`visibility`", "`width`", "`height`", "`actions`", "`overflow`",
                     "`skinArrangements`", "`skinLayoutsSeen`", "`sectionOrder`", "`window:2`", "`desktop:1,0`" })
            Says(word);
        Assert.True(missing.Count == 0, "docs/skins.md, Layout, no longer states: " + string.Join("; ", missing));

        // Each table lists exactly the ids the reader knows, in order.
        string[] Table(string header) => [.. layout[layout.IndexOf(header, StringComparison.Ordinal)..].Split('\n').Skip(2)
            .TakeWhile(line => line.StartsWith('|'))
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^\| `([A-Za-z.-]+)` \|").Groups[1].Value)];
        Assert.Equal(SkinLayoutRegions.All, Table("| Region | What it is |"));
        Assert.Equal(SkinHeaderActions.All, Table("| Action | What it is |"));
        Assert.Equal(["visibility", "width", "height", "actions", "overflow", "version"], Table("| Option | Region | What it is |"));
        Assert.Equal(["template", "strip", "orientation", "visibleBanks", "selectionDefaults", "master", "picker", "perColumn", "matrix", "popOut"], Table("| Option | Template | What it is |"));
        Assert.Equal(["rowHead", "columnHead", "cell", "meter"], Table("| Matrix option | What it is |"));
        Assert.Equal(SkinMatrixParts.RowHead.Concat(SkinMatrixParts.ColumnHead).Concat(SkinMatrixParts.Cell).Distinct(),
            Table("| Matrix part | Where | What it is |"));
        Assert.Equal(["template", "partsOrder", "hidden", "label", "meter", "fader"], Table("| Strip option | What it is |"));
        Assert.Equal(SkinStripParts.InputRow.Concat(SkinStripParts.Send).Distinct(), Table("| Part | What it is |"));
        Assert.Equal(["label.style", "meter.orientation", "meter.placement", "fader.orientation", "fader.direction"], Table("| Value | What it is |"));
        foreach (string name in SkinLayoutTemplates.Inputs.Concat(SkinLayoutTemplates.Submixer).Concat(SkinLayoutTemplates.InputStrips)
                     .Concat(SkinLayoutTemplates.Monitor).Concat(SkinLayoutTemplates.Applications).Concat(SkinPopOutDefaults.All)
                     .Concat(SkinLayoutTemplates.BankStrips).Concat(SkinLayoutTemplates.Masters).Concat(["tint", "dim", "under-fader", "none"])
                     .Concat(["text", "icon-and-text", "icon", "below-label", "beside-label", "beside-fader", "normal", "reversed"]))
            Says($"`\"{name}\"`");
        Assert.True(missing.Count == 0, "docs/skins.md, Layout, no longer states: " + string.Join("; ", missing));

        // The examples are whole skins, read as an installed one is: one grid, one dock.
        var read = new List<SkinLayout>();
        for (int start = layout.IndexOf("```json\n", StringComparison.Ordinal); start >= 0;
             start = layout.IndexOf("```json\n", start, StringComparison.Ordinal))
        {
            start += "```json\n".Length;
            string example = layout[start..layout.IndexOf("```", start, StringComparison.Ordinal)];
            using JsonDocument parsed = JsonDocument.Parse(example);
            SkinReadResult result = SkinReader.Read(parsed.RootElement.GetProperty("id").GetString()!, example, SkinOrigin.BuiltIn, null);
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            read.Add(Assert.IsType<SkinLayout>(result.Package!.Layout));
        }
        Assert.Equal(5, read.Count);
        SkinLayoutGrid grid = Assert.IsType<SkinLayoutGrid>(read[0].Main);
        Assert.True(read[0].PlacesHeader);
        Assert.Contains(grid.Containers(), c => c is SkinLayoutStack);
        Assert.Equal(SkinRegionVisibility.Collapsed, read[0].OptionsFor(SkinLayoutRegions.Applications).Visibility);
        Assert.NotEmpty(read[0].Header.Overflow);
        Assert.IsType<SkinLayoutDock>(read[1].Main);
        Assert.Equal(SkinRegionVisibility.Hidden, read[1].OptionsFor(SkinLayoutRegions.Applications).Visibility);
        Assert.NotNull(read[1].OptionsFor(SkinLayoutRegions.Inputs).Width);
        // The third: an input rack beside two banks.
        Assert.True(read[2].Inputs.IsRack);
        Assert.True(read[2].Submixer.IsBanks);
        Assert.Equal(SkinSubmixerOptions.DefaultVisibleBanks, read[2].Submixer.VisibleBanks);
        Assert.Equal(SkinLayoutTemplates.MasterStrip, read[2].Submixer.Master.Template);
        // The fourth: a matrix with its own heads and cells.
        Assert.True(read[3].Submixer.IsMatrix);
        Assert.Equal(["identity", "mute", "gain", "binding", "meter", "processing", "effects"], read[3].Submixer.Matrix.RowHead.Shown);
        Assert.DoesNotContain("effects", read[3].Submixer.Matrix.ColumnHead.Shown);
        Assert.Equal(SkinMatrixMuted.Tint, read[3].Submixer.Matrix.Muted);
        // The fifth: the toolbar and the list in a side column, the submixer in its own window.
        Assert.Equal(SkinLayoutTemplates.OutputToolbar, read[4].Monitor);
        Assert.Equal(SkinLayoutTemplates.ApplicationList, read[4].Applications);
        Assert.Equal(SkinMixerPlacement.Detached, read[4].PopOut);
    }

    [Fact]
    public void TheJsonSchemaDescribesTheLayoutThisVersionReads()
    {
        using JsonDocument schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "docs", "skin.schema.json")));
        JsonElement root = schema.RootElement;
        JsonElement definitions = root.GetProperty("$defs");
        Assert.Equal("#/$defs/desktopLayout",
            root.GetProperty("properties").GetProperty("layout").GetProperty("$ref").GetString());
        // The layout's version moves on its own; the file's stays.
        Assert.Equal(SkinFormat.Schema, root.GetProperty("properties").GetProperty("schema").GetProperty("const").GetInt32());
        JsonElement Definition(string name) => definitions.GetProperty(name);
        JsonElement Properties(string name) => Definition(name).GetProperty("properties");
        string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(v => v.GetString()!)];
        string[] Names(JsonElement properties) => [.. properties.EnumerateObject().Select(p => p.Name)];

        // Closed objects: a property the reader refuses is one the schema refuses.
        foreach (string name in new[] { "desktopLayout", "layoutRegions", "layoutSectionOptions", "layoutHeaderOptions",
                     "layoutInputsOptions", "layoutSubmixerOptions", "layoutMonitorOptions", "layoutApplicationsOptions", "layoutPopOut", "layoutInputStrip", "layoutBankStrip", "layoutMaster",
                     "layoutMatrix", "layoutMatrixRowHead", "layoutMatrixColumnHead", "layoutMatrixCell",
                     "layoutLabel", "layoutMeter", "layoutFader" })
            Assert.False(Definition(name).GetProperty("additionalProperties").GetBoolean(),
                $"{name} would accept a property the layout reader refuses.");
        // A node is closed where it is used, so a grid's child may carry its cell and a dock's child its edge.
        JsonElement mainUse = Properties("desktopLayout").GetProperty("main");
        Assert.Equal("#/$defs/layoutContainer", mainUse.GetProperty("$ref").GetString());
        Assert.False(mainUse.GetProperty("unevaluatedProperties").GetBoolean());
        foreach (string container in new[] { "layoutStack", "layoutGrid", "layoutDock" })
        {
            JsonElement items = Properties(container).GetProperty("children").GetProperty("items");
            Assert.Equal("#/$defs/layoutNode", items.GetProperty("$ref").GetString());
            Assert.False(items.GetProperty("unevaluatedProperties").GetBoolean(), $"{container} children are open.");
            Assert.Equal(SkinFormat.MaxLayoutChildren, Properties(container).GetProperty("children").GetProperty("maxItems").GetInt32());
            Assert.Equal("#/$defs/layoutId", Properties(container).GetProperty("id").GetProperty("$ref").GetString());
            Assert.Equal("#/$defs/layoutSpacing", Properties(container).GetProperty("spacing").GetProperty("$ref").GetString());
            Assert.Contains("id", Strings(Definition(container).GetProperty("required")));
        }

        JsonElement layout = Properties("desktopLayout");
        Assert.Equal(SkinFormat.LayoutVersion, layout.GetProperty("version").GetProperty("const").GetInt32());
        Assert.Equal(["main", "regions", "version"], Names(layout).Order(StringComparer.Ordinal));
        Assert.Equal(["version"], Strings(Definition("desktopLayout").GetProperty("required")));

        // The node union, chosen by kind.
        string Kind(JsonElement branch) =>
            Properties(branch.GetProperty("$ref").GetString()!["#/$defs/".Length..]).GetProperty("kind").GetProperty("const").GetString()!;
        Assert.Equal(["stack", "grid", "dock", "region"], Definition("layoutNode").GetProperty("oneOf").EnumerateArray().Select(Kind));
        Assert.Equal(["stack", "grid", "dock"], Definition("layoutContainer").GetProperty("oneOf").EnumerateArray().Select(Kind));

        JsonElement id = Definition("layoutId");
        Assert.Equal($"^[a-z0-9-]{{1,{SkinFormat.MaxLayoutIdLength}}}$", id.GetProperty("pattern").GetString());
        JsonElement spacing = Definition("layoutSpacing");
        Assert.Equal(SkinFormat.MinLayoutSpacing, spacing.GetProperty("minimum").GetDouble());
        Assert.Equal(SkinFormat.MaxLayoutSpacing, spacing.GetProperty("maximum").GetDouble());
        Assert.Equal(SkinLayoutDefaults.Spacing, spacing.GetProperty("default").GetDouble());

        Assert.Equal(["vertical", "horizontal"], Strings(Properties("layoutStack").GetProperty("orientation").GetProperty("enum")));

        foreach (string axis in new[] { "rows", "columns" })
        {
            JsonElement tracks = Properties("layoutGrid").GetProperty(axis);
            Assert.Equal(1, tracks.GetProperty("minItems").GetInt32());
            Assert.Equal(SkinFormat.MaxLayoutTracks, tracks.GetProperty("maxItems").GetInt32());
            Assert.Equal("#/$defs/layoutTrack", tracks.GetProperty("items").GetProperty("$ref").GetString());
        }
        JsonElement[] track = [.. Definition("layoutTrack").GetProperty("oneOf").EnumerateArray()];
        Assert.Equal("auto", track[0].GetProperty("const").GetString());
        JsonElement star = track[1].GetProperty("properties").GetProperty("star");
        Assert.Equal((SkinFormat.MinLayoutStar, SkinFormat.MaxLayoutStar), (star.GetProperty("minimum").GetDouble(), star.GetProperty("maximum").GetDouble()));
        JsonElement size = track[2].GetProperty("properties").GetProperty("size");
        Assert.Equal((SkinFormat.MinLayoutTrackSize, SkinFormat.MaxLayoutTrackSize), (size.GetProperty("minimum").GetDouble(), size.GetProperty("maximum").GetDouble()));
        Assert.All(track.Skip(1), t => Assert.False(t.GetProperty("additionalProperties").GetBoolean()));

        JsonElement cell = Properties("layoutGrid").GetProperty("children").GetProperty("items");
        Assert.Equal(["column", "columnSpan", "row", "rowSpan"], Names(cell.GetProperty("properties")).Order(StringComparer.Ordinal));
        Assert.Equal(["row", "column"], Strings(cell.GetProperty("required")));
        Assert.Equal(SkinFormat.MaxLayoutTracks - 1, cell.GetProperty("properties").GetProperty("row").GetProperty("maximum").GetInt32());
        Assert.Equal(SkinFormat.MaxLayoutTracks, cell.GetProperty("properties").GetProperty("columnSpan").GetProperty("maximum").GetInt32());

        JsonElement edge = Properties("layoutDock").GetProperty("children").GetProperty("items");
        Assert.Equal(["top", "bottom", "left", "right", "fill"], Strings(edge.GetProperty("properties").GetProperty("dock").GetProperty("enum")));
        Assert.Equal(Enum.GetNames<SkinLayoutEdge>().Select(n => n.ToLowerInvariant()), Strings(edge.GetProperty("properties").GetProperty("dock").GetProperty("enum")));
        Assert.Equal(["dock"], Strings(edge.GetProperty("required")));

        Assert.Equal(SkinLayoutRegions.All, Strings(Properties("layoutRegion").GetProperty("region").GetProperty("enum")));
        Assert.Equal(SkinLayoutRegions.All, Names(Properties("layoutRegions")));
        Assert.Equal("#/$defs/layoutHeaderOptions", Properties("layoutRegions").GetProperty("header").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/layoutSectionOptions", Properties("layoutRegions").GetProperty("headphones").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/layoutMonitorOptions", Properties("layoutRegions").GetProperty("monitor").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/layoutApplicationsOptions", Properties("layoutRegions").GetProperty("applications").GetProperty("$ref").GetString());
        // MONITOR takes every section option and a template, nothing else; APPLICATIONS also a column's length.
        foreach ((string def, IReadOnlyList<string> templates, string[] names) in new[]
                 {
                     ("layoutMonitorOptions", SkinLayoutTemplates.Monitor, new[] { "visibility", "width", "height", "template" }),
                     ("layoutApplicationsOptions", SkinLayoutTemplates.Applications, new[] { "visibility", "width", "height", "template", "perColumn" }),
                 })
        {
            Assert.Equal(names, Names(Properties(def)));
            Assert.Equal(templates, Strings(Properties(def).GetProperty("template").GetProperty("enum")));
            Assert.Equal(templates[0], Properties(def).GetProperty("template").GetProperty("default").GetString());
        }
        JsonElement perColumn = Properties("layoutApplicationsOptions").GetProperty("perColumn");
        Assert.Equal((SkinFormat.MinApplicationsPerColumn, SkinFormat.MaxApplicationsPerColumn, SkinFormat.DefaultApplicationsPerColumn),
            (perColumn.GetProperty("minimum").GetInt32(), perColumn.GetProperty("maximum").GetInt32(), perColumn.GetProperty("default").GetInt32()));
        // perColumn is refused without application-columns, as the reader refuses it.
        Assert.Equal(SkinLayoutTemplates.ApplicationColumns,
            Definition("layoutApplicationsOptions").GetProperty("if").GetProperty("properties").GetProperty("template").GetProperty("const").GetString());
        Assert.Equal(["perColumn"], Strings(Definition("layoutApplicationsOptions").GetProperty("else").GetProperty("not").GetProperty("required")));
        JsonElement popOut = Definition("layoutPopOut");
        Assert.Equal(["default"], Strings(popOut.GetProperty("required")));
        Assert.Equal(["default"], Names(popOut.GetProperty("properties")));
        Assert.Equal(SkinPopOutDefaults.All, Strings(popOut.GetProperty("properties").GetProperty("default").GetProperty("enum")));
        Assert.Equal(Enum.GetNames<SkinMixerPlacement>().Select(n => n.ToLowerInvariant()), SkinPopOutDefaults.All);
        Assert.Equal("#/$defs/layoutInputsOptions", Properties("layoutRegions").GetProperty("inputs").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/layoutSubmixerOptions", Properties("layoutRegions").GetProperty("submixer").GetProperty("$ref").GetString());

        // INPUTS and the submixer take every section option and their templates' own.
        JsonElement inputs = Properties("layoutInputsOptions");
        Assert.Equal(["visibility", "width", "height", "template", "strip"], Names(inputs));
        Assert.Equal(SkinLayoutTemplates.Inputs, Strings(inputs.GetProperty("template").GetProperty("enum")));
        Assert.Equal("#/$defs/layoutInputStrip", inputs.GetProperty("strip").GetProperty("$ref").GetString());
        JsonElement submixer = Properties("layoutSubmixerOptions");
        Assert.Equal(["visibility", "width", "height", "template", "orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker", "matrix", "popOut"],
            Names(submixer));
        Assert.Equal(SkinLayoutTemplates.BankPickers, Strings(submixer.GetProperty("picker").GetProperty("enum")));
        Assert.Equal(SkinLayoutTemplates.PickerDropdown, submixer.GetProperty("picker").GetProperty("default").GetString());
        Assert.Equal("#/$defs/layoutMatrix", submixer.GetProperty("matrix").GetProperty("$ref").GetString());
        // matrix is refused without mix-matrix, as the reader refuses it.
        JsonElement matrixRule = Assert.Single(Definition("layoutSubmixerOptions").GetProperty("allOf").EnumerateArray());
        Assert.Equal("mix-matrix", matrixRule.GetProperty("if").GetProperty("properties").GetProperty("template").GetProperty("const").GetString());
        Assert.Equal(["matrix"], Strings(matrixRule.GetProperty("else").GetProperty("not").GetProperty("required")));
        Assert.Equal(["rowHead", "columnHead", "cell", "meter"], Names(Properties("layoutMatrix")));
        Assert.Equal(["under-fader", "none"], Strings(Properties("layoutMatrix").GetProperty("meter").GetProperty("enum")));
        Assert.Equal(Enum.GetValues<SkinMatrixMeter>().Length, Properties("layoutMatrix").GetProperty("meter").GetProperty("enum").GetArrayLength());
        foreach ((string def, IReadOnlyList<string> parts, string kept, string[] rest) in new[]
                 {
                     ("layoutMatrixRowHead", SkinMatrixParts.RowHead, SkinMatrixParts.HeadKept, new[] { "label" }),
                     ("layoutMatrixColumnHead", SkinMatrixParts.ColumnHead, SkinMatrixParts.HeadKept, ["label"]),
                     ("layoutMatrixCell", SkinMatrixParts.Cell, SkinMatrixParts.CellKept, ["muted", "valuePlacement"]),
                 })
        {
            JsonElement fields = Properties(def);
            Assert.Equal(["parts", "hidden", .. rest], Names(fields));
            Assert.Equal(parts, Strings(fields.GetProperty("parts").GetProperty("items").GetProperty("enum")));
            Assert.Equal(parts.Count, fields.GetProperty("parts").GetProperty("maxItems").GetInt32());
            Assert.Equal(parts.Where(p => p != kept), Strings(fields.GetProperty("hidden").GetProperty("items").GetProperty("enum")));
            Assert.Equal(parts.Count - 1, fields.GetProperty("hidden").GetProperty("maxItems").GetInt32());
        }
        Assert.Equal(["tint", "dim"], Strings(Properties("layoutMatrixCell").GetProperty("muted").GetProperty("enum")));
        Assert.Equal(Enum.GetNames<SkinMatrixMuted>().Select(n => n.ToLowerInvariant()),
            Strings(Properties("layoutMatrixCell").GetProperty("muted").GetProperty("enum")));
        Assert.Equal(["beside", "over-fader"], Strings(Properties("layoutMatrixCell").GetProperty("valuePlacement").GetProperty("enum")));
        Assert.Equal(Enum.GetValues<SkinMatrixValue>().Length, Properties("layoutMatrixCell").GetProperty("valuePlacement").GetProperty("enum").GetArrayLength());
        Assert.Equal("#/$defs/layoutPopOut", submixer.GetProperty("popOut").GetProperty("$ref").GetString());
        Assert.Equal(SkinLayoutTemplates.Submixer, Strings(submixer.GetProperty("template").GetProperty("enum")));
        Assert.Equal(["horizontal", "vertical"], Strings(submixer.GetProperty("orientation").GetProperty("enum")));
        Assert.Equal((SkinFormat.MinVisibleBanks, SkinFormat.MaxVisibleBanks, SkinSubmixerOptions.DefaultVisibleBanks),
            (submixer.GetProperty("visibleBanks").GetProperty("minimum").GetInt32(), submixer.GetProperty("visibleBanks").GetProperty("maximum").GetInt32(),
             submixer.GetProperty("visibleBanks").GetProperty("default").GetInt32()));
        JsonElement defaults = submixer.GetProperty("selectionDefaults");
        Assert.Equal(SkinFormat.MaxSelectionDefaults, defaults.GetProperty("maxItems").GetInt32());
        Assert.True(defaults.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal($"^[a-z0-9_-]{{1,{SkinFormat.MaxMixIdLength}}}$", defaults.GetProperty("items").GetProperty("pattern").GetString());
        // An option of another template is refused there as the reader refuses it.
        Assert.Equal("input-rack", Definition("layoutInputsOptions").GetProperty("if").GetProperty("properties").GetProperty("template").GetProperty("const").GetString());
        Assert.Equal(["strip"], Strings(Definition("layoutInputsOptions").GetProperty("else").GetProperty("not").GetProperty("required")));
        Assert.Equal("mix-banks", Definition("layoutSubmixerOptions").GetProperty("if").GetProperty("properties").GetProperty("template").GetProperty("const").GetString());
        Assert.Equal(["orientation", "visibleBanks", "selectionDefaults", "strip", "master", "picker"],
            Definition("layoutSubmixerOptions").GetProperty("else").GetProperty("not").GetProperty("anyOf").EnumerateArray()
                .Select(b => Strings(b.GetProperty("required")).Single()));

        foreach ((string strip, IReadOnlyList<string> templates, IReadOnlyList<string> parts, string[] stripOptions) in new[]
                 {
                     ("layoutInputStrip", SkinLayoutTemplates.InputStrips, SkinStripParts.InputRow, new[] { "template", "partsOrder", "hidden", "label", "meter" }),
                     ("layoutBankStrip", SkinLayoutTemplates.BankStrips, SkinStripParts.Send, new[] { "template", "partsOrder", "hidden", "label", "meter", "fader" }),
                 })
        {
            JsonElement fields = Properties(strip);
            Assert.Equal(stripOptions, Names(fields));
            Assert.Equal(templates, Strings(fields.GetProperty("template").GetProperty("enum")));
            Assert.Equal(parts, Strings(fields.GetProperty("partsOrder").GetProperty("items").GetProperty("enum")));
            Assert.Equal(parts.Count, fields.GetProperty("partsOrder").GetProperty("maxItems").GetInt32());
            // identity cannot be hidden.
            Assert.Equal(parts.Skip(1), Strings(fields.GetProperty("hidden").GetProperty("items").GetProperty("enum")));
            Assert.Equal(parts.Count - 1, fields.GetProperty("hidden").GetProperty("maxItems").GetInt32());
        }
        Assert.Equal(["template", "label", "meter", "fader"], Names(Properties("layoutMaster")));
        Assert.Equal(SkinLayoutTemplates.Masters, Strings(Properties("layoutMaster").GetProperty("template").GetProperty("enum")));
        Assert.Equal(["text", "icon-and-text", "icon"], Strings(Properties("layoutLabel").GetProperty("style").GetProperty("enum")));
        Assert.Equal(Enum.GetValues<SkinLabelStyle>().Length, Properties("layoutLabel").GetProperty("style").GetProperty("enum").GetArrayLength());
        Assert.Equal(["below-label", "beside-label", "beside-fader"], Strings(Properties("layoutMeter").GetProperty("placement").GetProperty("enum")));
        Assert.Equal(Enum.GetValues<SkinMeterPlacement>().Length, Properties("layoutMeter").GetProperty("placement").GetProperty("enum").GetArrayLength());
        Assert.Equal(["normal", "reversed"], Strings(Properties("layoutFader").GetProperty("direction").GetProperty("enum")));
        Assert.Equal(Enum.GetNames<SkinFaderDirection>().Select(n => n.ToLowerInvariant()),
            Strings(Properties("layoutFader").GetProperty("direction").GetProperty("enum")));
        foreach (string def in new[] { "layoutMeter", "layoutFader" })
            Assert.Equal(["horizontal", "vertical"], Strings(Properties(def).GetProperty("orientation").GetProperty("enum")));

        JsonElement options = Properties("layoutSectionOptions");
        Assert.Equal(["visibility", "width", "height"], Names(options));
        Assert.Equal(Enum.GetNames<SkinRegionVisibility>().Select(n => n.ToLowerInvariant()),
            Strings(options.GetProperty("visibility").GetProperty("enum")));
        foreach (string dimension in new[] { "width", "height" })
        {
            Assert.Equal(SkinFormat.MinRegionSize, options.GetProperty(dimension).GetProperty("minimum").GetDouble());
            Assert.Equal(SkinFormat.MaxRegionSize, options.GetProperty(dimension).GetProperty("maximum").GetDouble());
        }
        Assert.Equal(["actions", "overflow", "version"], Names(Properties("layoutHeaderOptions")));
        Assert.Equal(["row", "title"], Strings(Properties("layoutHeaderOptions").GetProperty("version").GetProperty("enum")));
        Assert.Equal(SkinHeaderActions.All, Strings(Definition("layoutHeaderAction").GetProperty("enum")));
        Assert.All(new[] { "actions", "overflow" }, list =>
            Assert.Equal(SkinHeaderActions.All.Count, Properties("layoutHeaderOptions").GetProperty(list).GetProperty("maxItems").GetInt32()));
    }

    /// <summary>
    /// The example an author is told to copy. It is read exactly as an
    /// installed skin is, so a token renamed in the code leaves the example
    /// broken here rather than in someone's window.
    /// </summary>
    [Fact]
    public void TheExampleSkinReadsWithoutComplaint()
    {
        string folder = Path.Combine(Root, "docs", "examples", "skins", "example");
        SkinEntry entry = Assert.IsType<SkinEntry>(SkinCatalog.Read(folder, "example", SkinOrigin.User));
        Assert.True(entry.Errors.Count == 0, string.Join("\n", entry.Errors));
        Assert.Equal("example", entry.Id);
        Assert.Equal("Example", entry.Name);
        Assert.NotNull(entry.Package.Description);
        // It is worth copying only if it shows what the format can do.
        Assert.True(entry.Package.Tokens.Count > 20, $"only {entry.Package.Tokens.Count} tokens");
        Assert.NotEmpty(entry.Package.Controls);
        Assert.Contains(entry.Package.Tokens.Values, v => v is SkinGradient);
        Assert.Contains("examples/skins/example/skin.json", Document, StringComparison.Ordinal);
    }

    /// <summary>
    /// The complete layout example is the Broadcast skin the application
    /// ships, byte for byte, so the copy an author starts from is the one the
    /// window wears. It reads as an installed skin does, with its layout
    /// whole, and the documents point at it.
    /// </summary>
    [Fact]
    public void TheBroadcastExampleIsTheShippedSkinAndReadsWithItsLayout()
    {
        string folder = Path.Combine(Root, "docs", "examples", "skins", "broadcast");
        Assert.Equal(File.ReadAllText(Path.Combine(Root, "src", "OpenXLR.UI", "Assets", "Skins", "broadcast", "skin.json")),
            File.ReadAllText(Path.Combine(folder, "skin.json")));
        SkinEntry entry = Assert.IsType<SkinEntry>(SkinCatalog.Read(folder, "broadcast", SkinOrigin.User));
        Assert.True(entry.Errors.Count == 0, string.Join("\n", entry.Errors));
        Assert.Equal("Broadcast", entry.Name);
        SkinLayout layout = Assert.IsType<SkinLayout>(entry.Package.Layout);
        // The matrix under the header, HEADPHONES and the toolbar above it, INPUTS hidden and recoverable.
        Assert.True(layout.Submixer.IsMatrix);
        Assert.False(layout.Inputs.IsRack);
        Assert.Equal(SkinRegionVisibility.Hidden, layout.OptionsFor(SkinLayoutRegions.Inputs).Visibility);
        Assert.Equal(SkinLayoutTemplates.OutputToolbar, layout.Monitor);
        Assert.Equal(SkinRegionVisibility.Visible, layout.OptionsFor(SkinLayoutRegions.Headphones).Visibility);
        Assert.Equal(SkinRegionVisibility.Visible, layout.OptionsFor(SkinLayoutRegions.Applications).Visibility);
        Assert.Empty(layout.Header.Overflow);
        Assert.False(layout.PlacesHeader);
        Assert.Equal(layout.Description(), SkinCatalog.BuiltIn().Single(e => e.Id == "broadcast").Package.Layout!.Description());
        Assert.Contains("examples/skins/broadcast", Section(Document, "Layout", "What a skin cannot do"), StringComparison.Ordinal);
        Assert.Contains("**Broadcast**", Document, StringComparison.Ordinal);
        string readme = File.ReadAllText(Path.Combine(Root, "docs", "examples", "skins", "README.md"));
        Assert.Contains("~/.local/share/openxlr/skins/", readme, StringComparison.Ordinal);
        Assert.Contains("examples/skins/broadcast/skin.json", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// The icons section as the reader takes it: the limits, the name rule,
    /// every built-in name, an example that reads without complaint, and a
    /// schema with the same bounds.
    /// </summary>
    [Fact]
    public void TheIconsSectionAndSchemaStateWhatTheReaderTakes()
    {
        string document = Document;
        int start = document.IndexOf("\n## Icons\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/skins.md has no Icons section.");
        string section = document[start..document.IndexOf("\n## ", start + 4, StringComparison.Ordinal)];
        var missing = new List<string>();
        void Says(string text)
        {
            if (!section.Contains(text, StringComparison.Ordinal)) missing.Add(text);
        }
        Says($"at most {SkinFormat.MaxIcons} icons");
        Says($"at most {SkinFormat.MaxIconPathLength} characters");
        Says($"up to {OpenXLR.UI.LayoutIcons.MaxNameLength - 1} lower-case letters");
        Says("nonzero");
        foreach (var icon in OpenXLR.UI.LayoutIcons.BuiltIn) Says($"`{icon.Name}`");
        Assert.True(missing.Count == 0, "the Icons section no longer states: " + string.Join("; ", missing));

        int open = section.IndexOf("```json\n", StringComparison.Ordinal) + 8;
        string example = section[open..section.IndexOf("```", open, StringComparison.Ordinal)];
        SkinReadResult read = SkinReader.Read("icons", "{\"schema\":1,\"tokens\":{}," + example + "}", SkinOrigin.BuiltIn, null);
        Assert.Empty(read.Errors);
        Assert.Equal(2, read.Package!.Icons.Count);

        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "docs", "skin.schema.json")));
        JsonElement icons = schema.RootElement.GetProperty("properties").GetProperty("icons");
        Assert.Equal(SkinFormat.MaxIcons, icons.GetProperty("maxProperties").GetInt32());
        Assert.Equal($"^[a-z][a-z0-9-]{{0,{OpenXLR.UI.LayoutIcons.MaxNameLength - 1}}}$",
            icons.GetProperty("propertyNames").GetProperty("pattern").GetString());
        Assert.Equal("none", icons.GetProperty("propertyNames").GetProperty("not").GetProperty("const").GetString());
        JsonElement path = icons.GetProperty("additionalProperties").GetProperty("properties").GetProperty("path");
        Assert.Equal(SkinFormat.MaxIconPathLength, path.GetProperty("maxLength").GetInt32());
        Assert.Contains("icon", document.Split("\n## What a skin cannot do")[1], StringComparison.Ordinal);
    }
}
