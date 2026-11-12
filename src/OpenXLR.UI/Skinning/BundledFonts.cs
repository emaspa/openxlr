using System;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The typefaces compiled into the UI for the skins that name them, so a skin
/// looks the same on a machine that does not have the font installed. They
/// are the files under Assets/Fonts, each under its own licence beside it.
/// </summary>
public static class BundledFonts
{
    /// <summary>The key the collection is registered under.</summary>
    public const string CollectionKey = "fonts:OpenXLR";

    /// <summary>The families the collection holds, as a skin names them.</summary>
    public static readonly string[] Families = ["Fira Sans Condensed", "OpenXLR Segment 7", "OpenXLR Segment 14"];

    private static readonly Uri Key = new(CollectionKey, UriKind.Absolute);

    /// <summary>The collection itself.</summary>
    public static IFontCollection Collection() =>
        new EmbeddedFontCollection(Key, new Uri("avares://OpenXLR.UI/Assets/Fonts", UriKind.Absolute));

    /// <summary>
    /// A skin's family list with each bundled family pointed at the copy in
    /// the application, and every other name left for the system to find.
    /// The UI registers the collection when it is built; a host that did not
    /// (a test application) gets it here the first time a skin names one of
    /// its families, so a family pointed at it never fails to resolve.
    /// </summary>
    public static string Resolve(string families)
    {
        bool bundledNamed = false;
        string resolved = string.Join(", ", families.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0)
            .Select(f =>
            {
                if (Families.FirstOrDefault(b => string.Equals(b, f, StringComparison.OrdinalIgnoreCase)) is not { } bundled) return f;
                bundledNamed = true;
                return $"{CollectionKey}#{bundled}";
            }));
        if (bundledNamed) Register(FontManager.Current);
        return resolved;
    }

    private static FontManager? _registeredWith;

    /// <summary>Adds the collection to a font manager, once: what the UI's builder calls.</summary>
    public static void Register(FontManager fonts)
    {
        if (ReferenceEquals(fonts, _registeredWith)) return;
        fonts.AddFontCollection(Collection());
        _registeredWith = fonts;
    }
}
