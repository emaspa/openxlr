using System;
using System.Collections.Generic;
using System.Linq;

// Shared with the window, which offers these presets in Mic setup and lists
// them in the EQ editor, so both read the one table the daemon loads from.
#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// A built-in EQ profile: a stable key, its English name, and the EQ with
/// four bands and the switch on. Clients name it in their own language from
/// the key; the English name is what the daemon reports.
/// </summary>
public sealed record EqPreset(string Key, string Name, MicEq Eq)
{
    /// <summary>The id the daemon's profile list and <c>loadEqProfile</c> use.</summary>
    public string Id => EqProfileIds.Builtin(Key);
}

/// <summary>
/// How a profile is named on the wire: <c>builtin:</c> and a preset's key,
/// or <c>saved:</c> and a saved profile's name. The two never meet, so a
/// saved profile may share a built-in one's name.
/// </summary>
public static class EqProfileIds
{
    public const string BuiltinPrefix = "builtin:", SavedPrefix = "saved:";

    public static string Builtin(string key) => BuiltinPrefix + key;

    public static string Saved(string name) => SavedPrefix + name;
}

/// <summary>
/// OpenXLR's own starting points for a speaking voice, written down here and
/// nowhere else in code: Mic setup offers them, the EQ editor and the deck
/// list them as built-in profiles, and the manual's table is checked against
/// them. Each one keeps the low end out with the first band and shapes the
/// voice with the other three, gently enough to be a starting point rather
/// than a sound.
/// </summary>
public static class EqPresets
{
    private static EqBand Band(string type, double hz, double db = 0, double? q = null)
        => new() { Enabled = true, Type = type, Frequency = hz, Gain = db, Q = q ?? MicEq.DefaultQ };

    private static MicEq Eq(params EqBand[] bands) => MicEq.Snap(new MicEq { Enabled = true, Bands = bands });

    /// <summary>Rumble out below 80 Hz, a little less boxiness, a small lift for presence and air.</summary>
    public static EqPreset Clear { get; } = new("clear", "Clear", Eq(
        Band(MicEq.LowCut, 80),
        Band(MicEq.Peak, 300, -2, 1.0),
        Band(MicEq.Peak, 3500, 2, 1.0),
        Band(MicEq.HighShelf, 10000, 1)));

    /// <summary>A lower low cut, more body under 200 Hz and a softer top.</summary>
    public static EqPreset Warm { get; } = new("warm", "Warm", Eq(
        Band(MicEq.LowCut, 60),
        Band(MicEq.LowShelf, 200, 2),
        Band(MicEq.Peak, 3000, 1, 1.0),
        Band(MicEq.HighShelf, 8000, -2)));

    /// <summary>A tight low end, less mud around 400 Hz and a forward presence lift.</summary>
    public static EqPreset Broadcast { get; } = new("broadcast", "Broadcast", Eq(
        Band(MicEq.LowCut, 100),
        Band(MicEq.Peak, 400, -3, 1.4),
        Band(MicEq.Peak, 4000, 3, 0.8),
        Band(MicEq.HighShelf, 12000, 2)));

    /// <summary>Bring consonants forward and reduce the low mids without raising the whole microphone.</summary>
    public static EqPreset VoiceBoost { get; } = new("voice-boost", "Voice Boost", Eq(
        Band(MicEq.LowCut, 90),
        Band(MicEq.Peak, 250, -2.5, 1.0),
        Band(MicEq.Peak, 2800, 3, 0.9),
        Band(MicEq.HighShelf, 8000, 0)));

    /// <summary>The presets in the order every client shows them.</summary>
    public static IReadOnlyList<EqPreset> All { get; } = [Clear, Warm, Broadcast, VoiceBoost];

    /// <summary>The preset with this id (<c>builtin:</c> and its key), or null.</summary>
    public static EqPreset? FindId(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
