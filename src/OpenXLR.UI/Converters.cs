using Avalonia.Data.Converters;

namespace OpenXLR.UI;

public static class Converters
{
    /// <summary>Tile-header chevron: up when the tile is expanded.</summary>
    public static readonly IValueConverter Chevron =
        new FuncValueConverter<bool, string>(expanded => expanded ? "⌃" : "⌄");
}
