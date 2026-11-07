using System.Collections.Generic;
using System.Linq;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The outlines a skin's <c>"window": {"shape": ...}</c> can give the main
/// window. Like a control appearance it is a choice among drawings OpenXLR
/// owns, not a shape the skin supplies, and the tokens
/// <c>Ox.Window.CornerRadius</c>, <c>Ox.Window.BorderBrush</c> and
/// <c>Ox.Window.BorderThickness</c> size and line it.
/// </summary>
public static class SkinWindowShapes
{
    /// <summary>The platform's own window with its own title bar: what the UI has always been.</summary>
    public const string Rectangle = "rectangle";

    /// <summary>
    /// Corners rounded by <c>Ox.Window.CornerRadius</c>, transparent outside
    /// them, with OpenXLR's own caption row and resize edges in place of the
    /// platform's title bar. Worn only where the platform draws a transparent
    /// window; elsewhere the window stays a rectangle.
    /// </summary>
    public const string Rounded = "rounded";

    /// <summary>The shape of a skin that names none.</summary>
    public const string Default = Rectangle;

    /// <summary>Every shape, the default first.</summary>
    public static IReadOnlyList<string> All { get; } = [Rectangle, Rounded];

    /// <summary>The shapes quoted for a message: "rectangle" or "rounded".</summary>
    public static string List => string.Join(" or ", All.Select(s => $"\"{s}\""));
}
