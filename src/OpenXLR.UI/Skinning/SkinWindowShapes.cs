using System;
using System.Collections.Generic;
using Avalonia;
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

/// <summary>
/// The size a skin's <c>"window": {"size": ...}</c> gives the UI, in device
/// independent pixels of its content, title bar left out. The UI takes it when
/// the skin is put on, made smaller where the screen is.
/// </summary>
public readonly record struct SkinWindowSize(int Width, int Height)
{
    public const int MinWidth = 640;
    public const int MaxWidth = 7680;
    public const int MinHeight = 480;
    public const int MaxHeight = 4320;

    /// <summary>The size of a skin that names none, after one that did.</summary>
    public static SkinWindowSize Default { get; } = new(1040, 900);

    /// <summary>
    /// The size the UI takes: this one, made smaller where the screen's
    /// working area, less the desktop's frame round the UI, is smaller, but
    /// never under the smallest size a skin can name.
    /// </summary>
    public Size FitIn(Rect? area, Size decorations)
    {
        double width = Width, height = Height;
        if (area is { } screen)
        {
            width = Math.Min(width, Math.Max(MinWidth, screen.Width - decorations.Width));
            height = Math.Min(height, Math.Max(MinHeight, screen.Height - decorations.Height));
        }
        return new Size(width, height);
    }

    /// <summary>The bounds quoted for a message.</summary>
    public static string Bounds => $"{{\"width\": {MinWidth} to {MaxWidth}, \"height\": {MinHeight} to {MaxHeight}}}";
}
