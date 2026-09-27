using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// Portable window choices in a profile. Startup, update checks and security
/// preferences remain local. Linked into the window, which does not reference Core.
/// Unknown section IDs are retained for newer windows and ignored when displayed.
/// </summary>
public sealed record WindowPresentation
{
    public bool CompactMixer { get; init; }
    public string? CompactChannel { get; init; }
    public string? Skin { get; init; }
    public IReadOnlyList<string> CollapsedSections { get; init; } = [];
    public IReadOnlyList<string> SectionOrder { get; init; } = [];

    public void Validate()
    {
        if (!Text(CompactChannel, 36) || !Text(Skin, 64) ||
            !Sections(CollapsedSections) || !Sections(SectionOrder))
            throw new JsonException("Invalid profile presentation: use bounded identifiers and distinct section lists.");
    }

    private static bool Text(string? value, int limit) =>
        value is null || value.Length is > 0 && value.Length <= limit && !value.Any(char.IsControl);

    private static bool Sections(IReadOnlyList<string>? values) => values is not null &&
        values.Count <= 16 && values.All(v => v is not null && Text(v, 64)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;
}
