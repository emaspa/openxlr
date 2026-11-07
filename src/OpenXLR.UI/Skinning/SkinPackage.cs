using OpenXLR.UI.Localization;
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
public sealed record SkinImage(string RelativePath, Stretch Stretch, double Opacity) : SkinValue
{
    /// <summary>
    /// The flat colour that stands for the image where it cannot be drawn: in
    /// the terminal mixer, the Deck and bar palettes, and in the UI when the
    /// image is refused. Null when the skin gives none.
    /// </summary>
    public Color? Fallback { get; init; }
}

/// <summary>A bounded number.</summary>
public sealed record SkinNumber(double Value) : SkinValue;

public sealed record SkinCornerRadius(CornerRadius Value) : SkinValue;

public sealed record SkinThickness(Thickness Value) : SkinValue;

public sealed record SkinFontFamily(string Value) : SkinValue;

public sealed record SkinFontWeight(FontWeight Value) : SkinValue;

/// <summary>Where a skin came from, which decides what it is allowed to carry.</summary>
public enum SkinOrigin
{
    /// <summary>Compiled into the application; its images, if any, are compiled in beside it.</summary>
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
/// <param name="Layout">
/// The desktop arrangement the skin declares, or null when it declares none
/// or its layout block was refused. Null keeps the classic arrangement and
/// the user's own saved section order.
/// </param>
public sealed record SkinPackage(
    string Id,
    string Name,
    string? Description,
    string? Author,
    int Schema,
    SkinOrigin Origin,
    string? Directory,
    IReadOnlyDictionary<string, SkinValue> Tokens,
    IReadOnlyDictionary<string, string> Controls,
    SkinLayout? Layout = null)
{
    /// <summary>
    /// The id of Material, the token defaults with no overlay, which the
    /// application falls back to whenever a skin is missing or unreadable.
    /// </summary>
    public const string DefaultId = "default";

    /// <summary>
    /// The skin worn while ui.json holds no choice: a new install, or one
    /// whose user never picked a skin. Nothing is written for it, so a choice
    /// made later, Material included, is the one that stays.
    /// </summary>
    public const string StartingId = "broadcast";

    /// <summary>
    /// The skin's own icons, name to SVG path data, each checked by
    /// <see cref="LayoutIcons.IsValidPath"/>. A name of the built-in set
    /// replaces that icon's drawing while the skin is worn.
    /// </summary>
    public IReadOnlyDictionary<string, string> Icons { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Where the skin's images are read from: its folder, or the resources
    /// compiled in beside a built-in document. Null for a skin with no files,
    /// which the reader allowed no image.
    /// </summary>
    public SkinAssets? Assets { get; init; }

    /// <summary>
    /// The main window's outline, one of <see cref="SkinWindowShapes.All"/>.
    /// The rectangle unless the skin's <c>window</c> section names another.
    /// </summary>
    public string WindowShape { get; init; } = SkinWindowShapes.Default;

    /// <summary>
    /// The size the UI takes when the skin is put on, from the skin's
    /// <c>window</c> section; null for a skin that leaves the size alone.
    /// </summary>
    public SkinWindowSize? WindowSize { get; init; }

    /// <summary>The built-in appearance: no overlay at all, so every token keeps its default.</summary>
    public static SkinPackage Default { get; } = new(
        // The id stays "default" forever: it is what ui.json holds and what
        // OPENXLR_SKIN=default asks for. Only the name a user reads changes.
        DefaultId, "Material", Localizer.Text("MaterialDescription"), "OpenXLR",
        SkinFormat.Schema, SkinOrigin.BuiltIn, null,
        new Dictionary<string, SkinValue>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>A one-line origin for the picker.</summary>
    public string OriginLabel => Origin switch
    {
        SkinOrigin.BuiltIn => Localizer.Text("SkinBuiltIn"),
        SkinOrigin.User => Localizer.Text("SkinInstalledForYou"),
        _ => Localizer.Text("SkinInstalledOnSystem"),
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

    /// <summary>How many icons one skin may define.</summary>
    public const int MaxIcons = 64;

    /// <summary>The longest path data one skin icon may carry.</summary>
    public const int MaxIconPathLength = 4096;

    /// <summary>The longest id, name, description or author a package may carry.</summary>
    public const int MaxTextLength = 400;

    /// <summary>
    /// The version of the optional <c>layout</c> block this version reads. It
    /// moves on its own, so a newer layout leaves the top-level schema, the
    /// tokens and the controls readable by older builds.
    /// </summary>
    public const int LayoutVersion = 1;

    /// <summary>How many children one layout container may hold.</summary>
    public const int MaxLayoutChildren = 16;

    /// <summary>
    /// How many nodes, containers and regions together, one layout tree may
    /// hold. Six regions need far fewer; the bound is there so a tree of empty
    /// containers cannot grow without end.
    /// </summary>
    public const int MaxLayoutNodes = 64;

    /// <summary>How deep containers may nest; <c>main</c> is the first level.</summary>
    public const int MaxLayoutDepth = 4;

    /// <summary>How many rows, and separately how many columns, one grid may define.</summary>
    public const int MaxLayoutTracks = 8;

    /// <summary>The longest container id. Ids use lowercase letters, digits and hyphens.</summary>
    public const int MaxLayoutIdLength = 32;

    /// <summary>The smallest gap a layout container may put between its children.</summary>
    public const double MinLayoutSpacing = 0;

    /// <summary>The largest gap a layout container may put between its children.</summary>
    public const double MaxLayoutSpacing = 64;

    /// <summary>The smallest share a grid's <c>star</c> track may take.</summary>
    public const double MinLayoutStar = 0.1;

    /// <summary>The largest share a grid's <c>star</c> track may take.</summary>
    public const double MaxLayoutStar = 10;

    /// <summary>The smallest fixed <c>size</c> of a grid track, in device-independent pixels.</summary>
    public const double MinLayoutTrackSize = 40;

    /// <summary>The largest fixed <c>size</c> of a grid track, in device-independent pixels.</summary>
    public const double MaxLayoutTrackSize = 2000;

    /// <summary>The smallest preferred <c>width</c> or <c>height</c> a section may ask for.</summary>
    public const double MinRegionSize = 120;

    /// <summary>The largest preferred <c>width</c> or <c>height</c> a section may ask for.</summary>
    public const double MaxRegionSize = 4000;

    /// <summary>The fewest mix banks a submixer of banks shows.</summary>
    public const int MinVisibleBanks = 1;

    /// <summary>The most mix banks a submixer of banks shows. Every mix stays reachable through each bank's picker.</summary>
    public const int MaxVisibleBanks = 4;

    /// <summary>The fewest applications one column of <c>"application-columns"</c> may hold.</summary>
    public const int MinApplicationsPerColumn = 1;

    /// <summary>The most applications one column of <c>"application-columns"</c> may hold.</summary>
    public const int MaxApplicationsPerColumn = 24;

    /// <summary>How many applications one column holds when <c>perColumn</c> is missing.</summary>
    public const int DefaultApplicationsPerColumn = 6;

    /// <summary>How many mix ids <c>selectionDefaults</c> may list.</summary>
    public const int MaxSelectionDefaults = 16;

    /// <summary>The longest mix id <c>selectionDefaults</c> may list. Ids use lowercase letters, digits, hyphens and underscores.</summary>
    public const int MaxMixIdLength = 64;
}
