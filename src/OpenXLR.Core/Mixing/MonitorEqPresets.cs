using System;
using System.Collections.Generic;
using System.Linq;

#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>Broad stereo listening curves. Cuts give the featured range room without adding output gain.</summary>
internal sealed record MonitorEqPreset(string Id, IReadOnlyList<EqBand> Bands);

internal static class MonitorEqPresets
{
    internal static IReadOnlyList<MonitorEqPreset> All { get; } =
    [
        new("voice-boost", [Shelf(MicEq.LowShelf, 380, -4), Shelf(MicEq.HighShelf, 6200, -2)]),
        new("fps", [Shelf(MicEq.LowShelf, 240, -6), Shelf(MicEq.HighShelf, 7200, -2)]),
        new("bass-boost", [Shelf(MicEq.HighShelf, 170, -5)]),
        new("soft-treble", [Shelf(MicEq.HighShelf, 4500, -4)]),
    ];

    internal static MonitorEqPreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private static EqBand Shelf(string type, double hz, double db)
        => new() { Type = type, Frequency = hz, Gain = db, Q = MicEq.DefaultQ, Enabled = true };
}
