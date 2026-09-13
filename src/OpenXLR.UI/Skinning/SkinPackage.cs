using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace OpenXLR.UI.Skinning;

/// <summary>A token value as the file states it, before any Avalonia object exists for it.</summary>
public abstract record SkinValue;

/// <summary>A flat colour.</summary>
public sealed record SkinSolid(Color Color) : SkinValue;

/// <summary>One stop of a gradient.</summary>
/// <param name="Offset">0 at the start of the gradient, 1 at its end.</param>
public readonly record struct SkinStop(double Offset, Color Color);

/// <summary>
/// A linear or radial gradient in relative coordinates, which is how the
/// OpenDeck plugin draws its side-lit faceplates and its machined knob caps.
/// </summary>
public sealed record SkinGradient(
    bool Radial,
    Point Start,
    Point End,
    double Radius,
    IReadOnlyList<SkinStop> Stops) : SkinValue;

/// <summary>An image file inside the skin's own folder.</summary>
public sealed record SkinImage(string RelativePath, Stretch Stretch, double Opacity) : SkinValue;

/// <summary>A bounded number.</summary>
public sealed record SkinNumber(double Value) : SkinValue;

public sealed record SkinCornerRadius(CornerRadius Value) : SkinValue;

public sealed record SkinThickness(Thickness Value) : SkinValue;

public sealed record SkinFontFamily(string Value) : SkinValue;

public sealed record SkinFontWeight(FontWeight Value) : SkinValue;

/// <summary>Where a skin came from, which decides what it is allowed to carry.</summary>
public enum SkinOrigin
{
    /// <summary>Compiled into the application; no files, no images.</summary>
    BuiltIn,
    /// <summary>A folder under the user's data directory.</summary>
    User,
    /// <summary>A folder under a system data directory.</summary>
    System,
}

/// <summary>
/// A validated skin. Every value here has passed its token's type and bounds
/// check; nothing in it can run, fetch or reach outside <see cref="Directory"/>.
/// </summary>
public sealed record SkinPackage(
    string Id,
    string Name,
    string? Description,
    string? Author,
    int Schema,
    SkinOrigin Origin,
    string? Directory,
    IReadOnlyDictionary<string, SkinValue> Tokens,
    IReadOnlyDictionary<string, string> Controls)
{
    /// <summary>The id of the appearance the application ships and falls back to.</summary>
    public const string DefaultId = "default";

    /// <summary>The built-in appearance: no overlay at all, so every token keeps its default.</summary>
    public static SkinPackage Default { get; } = new(
        // The id stays "default" forever: it is what ui.json holds and what
        // OPENXLR_SKIN=default asks for. Only the name a user reads changes.
        DefaultId, "Material", "The appearance the application ships with.", "OpenXLR",
        SkinFormat.Schema, SkinOrigin.BuiltIn, null,
        new Dictionary<string, SkinValue>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>A one-line origin for the picker.</summary>
    public string OriginLabel => Origin switch
    {
        SkinOrigin.BuiltIn => "built in",
        SkinOrigin.User => "installed for you",
        _ => "installed on this system",
    };
}

/// <summary>The file format's version and the limits a package is read under.</summary>
public static class SkinFormat
{
    /// <summary>The schema this version writes and reads.</summary>
    public const int Schema = 1;

    /// <summary>A skin.json larger than this is refused before it is parsed.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>How many image assets one skin may declare.</summary>
    public const int MaxImages = 32;

    /// <summary>The largest image file a skin may carry.</summary>
    public const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>The largest side of a decoded image.</summary>
    public const int MaxImagePixels = 4096;

    /// <summary>
    /// The total decoded size of every image in a skin. Four bytes a pixel at
    /// this cap is 64 MiB, which a mixer window can afford once and cannot
    /// afford many times over.
    /// </summary>
    public const long MaxDecodedPixels = 16L * 1024 * 1024;

    /// <summary>How many gradient stops one token may declare.</summary>
    public const int MaxGradientStops = 16;

    /// <summary>The longest id, name, description or author a package may carry.</summary>
    public const int MaxTextLength = 400;
}
