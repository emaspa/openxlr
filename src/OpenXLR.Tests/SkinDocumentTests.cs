using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
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
}
