using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace OpenXLR.UI.Skinning;

/// <summary>One token value that could not be used, with the reason a user can act on.</summary>
internal sealed class SkinValueException(string message) : Exception(message);

/// <summary>The outcome of reading one skin.json: a package, or the reasons there is none.</summary>
/// <param name="Package">Null when the document could not be used at all.</param>
/// <param name="Errors">Everything that stopped a value or the whole package.</param>
public sealed record SkinReadResult(SkinPackage? Package, IReadOnlyList<string> Errors)
{
    public bool Ok => Package is not null && Errors.Count == 0;
}

/// <summary>
/// Reads and validates a skin document. Nothing here touches the rendering
/// platform, so the rules below are covered by ordinary unit tests:
///
/// - the document declares a schema this version understands;
/// - every token name is one this version has, and every value has that
///   token's type and stays inside its bounds;
/// - an image is a plain relative path inside the skin's own folder, with no
///   parent segment, no root, no symbolic link leaving the folder and no URL,
///   whether that folder is on disk or compiled in (<see cref="SkinAssets"/>);
/// - an optional layout block passes <see cref="SkinLayoutReader"/> whole, or
///   is dropped whole.
///
/// A value that fails is reported and dropped; the token keeps its default,
/// which is what makes a partial skin an overlay on the shipped appearance
/// rather than a half-painted window.
/// </summary>
public static class SkinReader
{
    /// <summary>Ids are folder names, so they are kept to a boring, portable shape.</summary>
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Read a document. <paramref name="directory"/> is the skin's own folder
    /// and the only place its images may come from; null for a document with
    /// no files at all, which therefore may not declare images.
    /// </summary>
    public static SkinReadResult Read(string id, string json, SkinOrigin origin, string? directory) =>
        ReadFrom(id, json, origin, directory is null ? null : new SkinFolderAssets(directory));

    /// <summary>
    /// Read a document whose images come from <paramref name="assets"/>: an
    /// installed skin's folder, or the resources compiled in beside a built-in
    /// skin. Null for a document with no files, which may not declare images.
    /// </summary>
    public static SkinReadResult ReadFrom(string id, string json, SkinOrigin origin, SkinAssets? assets)
    {
        string? directory = (assets as SkinFolderAssets)?.Directory;
        var errors = new List<string>();
        if (!IsValidId(id))
            return new SkinReadResult(null, [$"\"{id}\" is not a usable skin name: use lower-case letters, digits, dot, dash or underscore."]);
        if (json.Length > SkinFormat.MaxDocumentBytes)
            return new SkinReadResult(null, [$"skin.json is larger than the {SkinFormat.MaxDocumentBytes / 1024} KB limit."]);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        }
        catch (JsonException ex)
        {
            return new SkinReadResult(null, [$"skin.json is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new SkinReadResult(null, ["skin.json must hold a JSON object."]);

            if (!root.TryGetProperty("schema", out JsonElement schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out int schema))
                return new SkinReadResult(null, ["skin.json has no \"schema\" number. Add \"schema\": 1."]);
            if (schema < 1)
                return new SkinReadResult(null, [$"skin schema {schema} is not a version this format ever had."]);
            if (schema > SkinFormat.Schema)
                return new SkinReadResult(null,
                    [$"this skin needs skin schema {schema}; this OpenXLR reads up to {SkinFormat.Schema}. Update OpenXLR to use it."]);

            if (Text(root, "id") is { Length: > 0 } declared && !string.Equals(declared, id, StringComparison.Ordinal))
                errors.Add($"skin.json says its id is \"{declared}\" but it is installed as \"{id}\"; the folder name wins.");

            string name = Text(root, "name") ?? id;
            string? description = Text(root, "description");
            string? author = Text(root, "author");

            var tokens = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
            if (root.TryGetProperty("tokens", out JsonElement tokenElement))
            {
                if (tokenElement.ValueKind != JsonValueKind.Object)
                    errors.Add("\"tokens\" must be an object of token name to value.");
                else
                    ReadTokens(tokenElement, assets, tokens, errors);
            }
            else errors.Add("skin.json has no \"tokens\" object, so it changes nothing.");

            var controls = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("controls", out JsonElement controlElement))
            {
                if (controlElement.ValueKind != JsonValueKind.Object)
                    errors.Add("\"controls\" must be an object of control name to variant name.");
                else ReadControls(controlElement, controls, errors);
            }

            // The layout is optional and taken whole: a block with any fault
            // is reported and dropped, and the colours and controls above it
            // still apply.
            SkinLayout? layout = null;
            if (root.EnumerateObject().Count(p => p.Name == "layout") > 1)
                errors.Add($"layout: is given twice. {SkinLayoutReader.Consequence}");
            else if (root.TryGetProperty("layout", out JsonElement layoutElement))
                layout = SkinLayoutReader.Read(layoutElement, errors);

            var icons = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("icons", out JsonElement iconElement))
            {
                if (iconElement.ValueKind != JsonValueKind.Object)
                    errors.Add("\"icons\" must be an object of icon name to {\"path\": \"...\"}.");
                else ReadIcons(iconElement, icons, errors);
            }

            string shape = SkinWindowShapes.Default;
            SkinWindowSize? size = null;
            if (root.TryGetProperty("window", out JsonElement windowElement))
            {
                if (windowElement.ValueKind != JsonValueKind.Object)
                    errors.Add("\"window\" must be an object such as {\"shape\": \"rounded\"}.");
                else (shape, size) = ReadWindow(windowElement, errors);
            }

            var package = new SkinPackage(id, name, description, author, schema, origin, directory, tokens, controls, layout)
            {
                Icons = icons,
                Assets = assets,
                WindowShape = shape,
                WindowSize = size,
            };
            return new SkinReadResult(package, errors);
        }
    }

    private static void ReadTokens(JsonElement tokenElement, SkinAssets? assets,
        Dictionary<string, SkinValue> tokens, List<string> errors)
    {
        int images = 0;
        foreach (JsonProperty property in tokenElement.EnumerateObject())
        {
            if (SkinTokens.Find(property.Name) is not { } token)
            {
                errors.Add($"\"{property.Name}\" is not a token this OpenXLR has; it is ignored.");
                continue;
            }
            try
            {
                SkinValue value = ReadValue(token, property.Value, assets, ref images);
                tokens[token.Name] = value;
            }
            catch (SkinValueException ex)
            {
                errors.Add($"{token.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The "controls" section: a control name to the name of one of the
    /// appearances the application owns for it. A skin never supplies markup,
    /// so an unknown control or an unknown variant is reported and that
    /// control keeps the appearance the window has always had.
    /// </summary>
    private static void ReadControls(JsonElement element,
        Dictionary<string, string> controls, List<string> errors)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (SkinControls.Find(property.Name) is not { } slot)
            {
                errors.Add($"\"{property.Name}\" is not a control this OpenXLR can restyle; it is ignored.");
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"controls.{slot.Name}: expected one of {slot.VariantList}.");
                continue;
            }
            string variant = property.Value.GetString()!;
            if (!slot.Variants.ContainsKey(variant))
            {
                errors.Add($"controls.{slot.Name}: \"{variant}\" is not an appearance this OpenXLR has; "
                    + $"expected one of {slot.VariantList}.");
                continue;
            }
            controls[slot.Name] = variant;
        }
    }

    /// <summary>
    /// The "window" section: the outline of the main window, one of the
    /// shapes OpenXLR draws, and the size the UI takes when the skin is put
    /// on. A shape this version does not have is reported and the window
    /// keeps the rectangle; a size out of bounds is reported and the UI keeps
    /// its size; a field it does not read is reported and ignored, since that
    /// is almost always a typo.
    /// </summary>
    private static (string Shape, SkinWindowSize? Size) ReadWindow(JsonElement element, List<string> errors)
    {
        string shape = SkinWindowShapes.Default;
        SkinWindowSize? size = null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name == "size")
            {
                size = ReadWindowSize(property.Value);
                if (size is null)
                    errors.Add($"window.size: expected {SkinWindowSize.Bounds}; the UI keeps its size.");
                continue;
            }
            if (property.Name != "shape")
            {
                string name = property.Name.Length > 40 ? property.Name[..40] + "..." : property.Name;
                errors.Add($"window: \"{name}\" is not a window setting this OpenXLR reads; it is ignored.");
                continue;
            }
            if (property.Value.ValueKind == JsonValueKind.String
                && SkinWindowShapes.All.Contains(property.Value.GetString()!, StringComparer.Ordinal))
                shape = property.Value.GetString()!;
            else
                errors.Add($"window.shape: expected one of {SkinWindowShapes.List}; the window keeps the rectangle.");
        }
        return (shape, size);
    }

    /// <summary>A width and a height, both whole numbers in bounds and nothing else, or null.</summary>
    private static SkinWindowSize? ReadWindowSize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        int? width = null, height = null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value)) return null;
            switch (property.Name)
            {
                case "width" when width is null && value is >= SkinWindowSize.MinWidth and <= SkinWindowSize.MaxWidth: width = value; break;
                case "height" when height is null && value is >= SkinWindowSize.MinHeight and <= SkinWindowSize.MaxHeight: height = value; break;
                default: return null;
            }
        }
        return width is { } w && height is { } h ? new SkinWindowSize(w, h) : null;
    }

    /// <summary>
    /// The "icons" section: a name to an object holding SVG path data. Each
    /// entry is checked on its own; a bad one is reported and dropped, and the
    /// item that names it wears the built-in icon of that name or its default.
    /// Path data is text with numbers and drawing commands only, so it cannot
    /// carry markup or reach a file.
    /// </summary>
    private static void ReadIcons(JsonElement element, Dictionary<string, string> icons, List<string> errors)
    {
        int seen = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name.Length > LayoutIcons.MaxNameLength + 8
                ? property.Name[..(LayoutIcons.MaxNameLength + 8)] + "..." : property.Name;
            if (++seen > SkinFormat.MaxIcons)
            {
                errors.Add($"icons: a skin may define at most {SkinFormat.MaxIcons} icons; \"{name}\" and the rest are ignored.");
                return;
            }
            if (!LayoutIcons.IsName(property.Name) || property.Name == LayoutIcons.None)
            {
                errors.Add($"icons: \"{name}\" is not a usable icon name: start with a lower-case letter and use lower-case letters, digits or hyphens, at most {LayoutIcons.MaxNameLength} characters, never \"none\".");
                continue;
            }
            if (icons.ContainsKey(property.Name))
            {
                icons.Remove(property.Name);
                errors.Add($"icons.{name}: is given twice; neither is used.");
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String)
            {
                errors.Add($"icons.{name}: expected an object with a \"path\" string of SVG path data.");
                continue;
            }
            foreach (JsonProperty extra in property.Value.EnumerateObject().Where(p => p.Name != "path"))
                errors.Add($"icons.{name}: \"{(extra.Name.Length > 40 ? extra.Name[..40] + "..." : extra.Name)}\" is not an icon field this OpenXLR reads; it is ignored.");
            string data = path.GetString()!;
            if (data.Length > SkinFormat.MaxIconPathLength)
            {
                errors.Add($"icons.{name}: the path is longer than {SkinFormat.MaxIconPathLength} characters.");
                continue;
            }
            if (!LayoutIcons.IsValidPath(data, SkinFormat.MaxIconPathLength))
            {
                errors.Add($"icons.{name}: the path is not SVG path data of M, L, H, V, C, S, Q, T, A and Z commands with plain numbers, starting with M.");
                continue;
            }
            icons[property.Name] = data;
        }
    }

    private static SkinValue ReadValue(SkinToken token, JsonElement value, SkinAssets? assets, ref int images) =>
        token.Kind switch
        {
            SkinTokenKind.Brush => ReadBrush(token, value, assets, ref images),
            SkinTokenKind.Number => new SkinNumber(Bounded(token, Double(value))),
            SkinTokenKind.CornerRadius => new SkinCornerRadius(ReadCornerRadius(token, value)),
            SkinTokenKind.Thickness => new SkinThickness(ReadThickness(token, value)),
            SkinTokenKind.FontFamily => new SkinFontFamily(ReadFontFamily(value)),
            _ => new SkinFontWeight(ReadFontWeight(value)),
        };

    private static SkinValue ReadBrush(SkinToken token, JsonElement value, SkinAssets? assets, ref int images)
    {
        if (value.ValueKind == JsonValueKind.String)
            return new SkinSolid(ReadColor(value.GetString()!));
        if (value.ValueKind != JsonValueKind.Object)
            throw new SkinValueException("expected a colour such as \"#3ecf7a\", or a gradient or image object.");

        string type = Text(value, "type") ?? throw new SkinValueException("the object needs a \"type\" of \"linear\", \"radial\" or \"image\".");
        if (token.SolidOnly && type != "solid")
            throw new SkinValueException("this token drives an indicator drawn in code and must be a flat colour.");

        switch (type)
        {
            case "solid":
                return new SkinSolid(ReadColor(Text(value, "color")
                    ?? throw new SkinValueException("a solid needs a \"color\".")));
            case "linear":
            case "radial":
                return ReadGradient(type == "radial", value);
            case "image":
                if (assets is null)
                    throw new SkinValueException("this skin carries no files, so it cannot use an image.");
                if (++images > SkinFormat.MaxImages)
                    throw new SkinValueException($"a skin may use at most {SkinFormat.MaxImages} images.");
                return ReadImage(value, assets);
            default:
                throw new SkinValueException($"\"{type}\" is not a brush type; use \"solid\", \"linear\", \"radial\" or \"image\".");
        }
    }

    private static SkinValue ReadGradient(bool radial, JsonElement value)
    {
        if (!value.TryGetProperty("stops", out JsonElement stopsElement) || stopsElement.ValueKind != JsonValueKind.Array)
            throw new SkinValueException("a gradient needs a \"stops\" array.");
        var stops = new List<SkinStop>();
        foreach (JsonElement stop in stopsElement.EnumerateArray())
        {
            if (stops.Count == SkinFormat.MaxGradientStops)
                throw new SkinValueException($"a gradient may have at most {SkinFormat.MaxGradientStops} stops.");
            if (stop.ValueKind != JsonValueKind.Object)
                throw new SkinValueException("each stop is an object with \"offset\" and \"color\".");
            double offset = stop.TryGetProperty("offset", out JsonElement o) ? Double(o) : stops.Count;
            if (double.IsNaN(offset) || offset is < 0 or > 1)
                throw new SkinValueException("a stop offset is between 0 and 1.");
            stops.Add(new SkinStop(offset, ReadColor(Text(stop, "color")
                ?? throw new SkinValueException("each stop needs a \"color\"."))));
        }
        if (stops.Count < 2) throw new SkinValueException("a gradient needs at least two stops.");

        // "to" is the far end of a linear gradient and the centre of a radial
        // one; "from" is the near end, or the radial's highlight, which sits on
        // the centre unless the skin moves it.
        Point end = ReadPoint(value, "to", radial ? new Point(0.5, 0.5) : new Point(0, 1));
        Point start = ReadPoint(value, "from", radial ? end : new Point(0, 0));
        double radius = value.TryGetProperty("radius", out JsonElement r) ? Double(r) : 0.5;
        if (double.IsNaN(radius) || radius is <= 0 or > 4)
            throw new SkinValueException("a radial \"radius\" is between 0 and 4.");
        return new SkinGradient(radial, start, end, radius, stops);
    }

    private static Point ReadPoint(JsonElement value, string name, Point fallback)
    {
        if (!value.TryGetProperty(name, out JsonElement element)) return fallback;
        if (element.ValueKind != JsonValueKind.Array)
            throw new SkinValueException($"\"{name}\" is a two-number array such as [0, 1].");
        double[] numbers = [.. element.EnumerateArray().Select(Double)];
        if (numbers.Length != 2 || numbers.Any(n => double.IsNaN(n) || n is < -2 or > 3))
            throw new SkinValueException($"\"{name}\" is two numbers between -2 and 3.");
        return new Point(numbers[0], numbers[1]);
    }

    private static SkinValue ReadImage(JsonElement value, SkinAssets assets)
    {
        string source = Text(value, "source") ?? throw new SkinValueException("an image needs a \"source\" file name.");
        string relative = assets.Check(source);
        Stretch stretch = Text(value, "stretch") switch
        {
            null or "uniformToFill" => Stretch.UniformToFill,
            "fill" => Stretch.Fill,
            "uniform" => Stretch.Uniform,
            "none" => Stretch.None,
            var other => throw new SkinValueException(
                $"\"{other}\" is not a stretch; use \"fill\", \"uniform\", \"uniformToFill\" or \"none\"."),
        };
        double opacity = value.TryGetProperty("opacity", out JsonElement o) ? Double(o) : 1;
        if (double.IsNaN(opacity) || opacity is < 0 or > 1)
            throw new SkinValueException("an image \"opacity\" is between 0 and 1.");
        Color? fallback = Text(value, "color") is { } colour ? ReadColor(colour) : null;
        return new SkinImage(relative, stretch, opacity) { Fallback = fallback };
    }

    /// <summary>
    /// The file a reference resolves to, or null when it is missing, is not a
    /// regular file, or is a link whose target leaves the skin's folder. Run
    /// when the skin is realized, since the folder can change under us.
    /// </summary>
    internal static FileInfo? ResolveAsset(string directory, string relative)
    {
        try
        {
            string root = Path.GetFullPath(directory);
            var file = new FileInfo(Path.Combine(root, relative));
            if (!file.Exists) return null;
            // ResolveLinkTarget walks the whole chain; a plain file returns null.
            FileSystemInfo resolved = file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
            string target = Path.GetFullPath(resolved.FullName);
            return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? new FileInfo(target) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static Color ReadColor(string text)
    {
        if (!Color.TryParse(text, out Color color))
            throw new SkinValueException($"\"{text}\" is not a colour; use \"#rrggbb\", \"#aarrggbb\" or a colour name.");
        return color;
    }

    private static double Double(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
            throw new SkinValueException("expected a number.");
        return double.IsFinite(number) ? number : throw new SkinValueException("expected a finite number.");
    }

    private static double Bounded(SkinToken token, double number) =>
        number >= token.Minimum && number <= token.Maximum ? number
            : throw new SkinValueException(
                $"{number.ToString(CultureInfo.InvariantCulture)} is outside the allowed range "
                + $"{token.Minimum.ToString(CultureInfo.InvariantCulture)} to {token.Maximum.ToString(CultureInfo.InvariantCulture)}.");

    private static double[] Sides(SkinToken token, JsonElement value, int[] allowed)
    {
        if (value.ValueKind == JsonValueKind.Number) return [Bounded(token, Double(value))];
        if (value.ValueKind != JsonValueKind.Array)
            throw new SkinValueException("expected a number or an array of numbers.");
        double[] numbers = [.. value.EnumerateArray().Select(e => Bounded(token, Double(e)))];
        return allowed.Contains(numbers.Length) ? numbers
            : throw new SkinValueException($"expected {string.Join(" or ", allowed)} numbers.");
    }

    private static CornerRadius ReadCornerRadius(SkinToken token, JsonElement value)
    {
        double[] n = Sides(token, value, [1, 4]);
        return n.Length == 1 ? new CornerRadius(n[0]) : new CornerRadius(n[0], n[1], n[2], n[3]);
    }

    private static Thickness ReadThickness(SkinToken token, JsonElement value)
    {
        double[] n = Sides(token, value, [1, 2, 4]);
        return n.Length switch
        {
            1 => new Thickness(n[0]),
            2 => new Thickness(n[0], n[1]),
            _ => new Thickness(n[0], n[1], n[2], n[3]),
        };
    }

    private static string ReadFontFamily(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new SkinValueException("expected a font family name.");
        string family = value.GetString()!;
        if (family.Length is 0 or > 120) throw new SkinValueException("a font family name is between 1 and 120 characters.");
        // "avares://…#Family" and "file://…" are font URIs. A skin names
        // families the system already has; it does not load font files.
        if (family.Contains("://", StringComparison.Ordinal) || family.Contains('#'))
            throw new SkinValueException("name an installed font family; a skin does not load font files.");
        if (family.Any(char.IsControl)) throw new SkinValueException("a font family name has no control characters.");
        return family;
    }

    private static FontWeight ReadFontWeight(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = Double(value);
            if (number is < 1 or > 1000) throw new SkinValueException("a numeric font weight is between 1 and 1000.");
            return (FontWeight)(int)number;
        }
        if (value.ValueKind != JsonValueKind.String) throw new SkinValueException("expected a font weight.");
        return Enum.TryParse(value.GetString(), ignoreCase: true, out FontWeight weight)
            ? weight
            : throw new SkinValueException($"\"{value.GetString()}\" is not a font weight; try \"Normal\", \"SemiBold\" or \"Bold\".");
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string text = value.GetString()!;
        if (text.Length > SkinFormat.MaxTextLength) text = text[..SkinFormat.MaxTextLength];
        return text.Any(char.IsControl) ? new string([.. text.Where(c => !char.IsControl(c))]) : text;
    }
}
