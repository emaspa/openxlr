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
/// its bands and the switch on. Clients name it in their own language from
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
/// OpenXLR's own built-in EQ profiles, written down here and nowhere else in
/// code, and the manual's tables are checked against them. The voice curves
/// are starting points for a speaking voice: Mic setup offers them, and each
/// keeps the low end out with the first band and shapes the voice with the
/// other three, gently enough to be a starting point rather than a sound.
/// The listening curves are for what an output plays: shelves only, every one
/// cutting rather than boosting, so a curve never pushes a mix into clipping.
/// The EQ editor and the deck list every one of them on every EQ, a
/// microphone's or a mix's, and their keys never change.
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

    /// <summary>Balance the extra bass of close speech and add a gentle lift for words.</summary>
    public static EqPreset Podcast { get; } = new("podcast", "Podcast", Eq(
        Band(MicEq.LowCut, 65),
        Band(MicEq.LowShelf, 140, -2),
        Band(MicEq.Peak, 1900, 1.75, 0.8),
        Band(MicEq.HighShelf, 7000, -1.5)));

    /// <summary>Less boom and mud on a deep voice, keeping its low fundamentals.</summary>
    public static EqPreset DeepVoice { get; } = new("deep-voice", "Deep voice", Eq(
        Band(MicEq.LowCut, 70),
        Band(MicEq.Peak, 170, -2.5, 1.2),
        Band(MicEq.Peak, 500, -1.5, 0.9),
        Band(MicEq.HighShelf, 3200, 2)));

    /// <summary>Clear's low end with a static dip around sharp S sounds and a soft top end.</summary>
    public static EqPreset SoftSibilants { get; } = new("soft-sibilants", "Soft sibilants", Eq(
        Band(MicEq.LowCut, 80),
        Band(MicEq.Peak, 300, -2, 1.0),
        Band(MicEq.Peak, 6600, -3, 2.0),
        Band(MicEq.HighShelf, 9500, -1.5)));

    /// <summary>Voices in a game or a call a little ahead of the rest: less low end, a softer top.</summary>
    public static EqPreset VoicesForward { get; } = new("voices-forward", "Voices forward", Eq(
        Band(MicEq.LowShelf, 380, -4),
        Band(MicEq.HighShelf, 6200, -2)));

    /// <summary>Footsteps for shooters: much less low rumble, so the steps in the upper mids stand out.</summary>
    public static EqPreset Footsteps { get; } = new("footsteps", "Footsteps", Eq(
        Band(MicEq.LowShelf, 240, -6),
        Band(MicEq.HighShelf, 7200, -2)));

    /// <summary>More bass by cutting everything above it, so the mix gains no level.</summary>
    public static EqPreset BassBoost { get; } = new("bass-boost", "Bass boost", Eq(
        Band(MicEq.HighShelf, 170, -5)));

    /// <summary>A softer top end for bright or harsh audio.</summary>
    public static EqPreset SoftTreble { get; } = new("soft-treble", "Soft treble", Eq(
        Band(MicEq.HighShelf, 4500, -4)));

    /// <summary>The voice curves, in the order Mic setup offers them.</summary>
    public static IReadOnlyList<EqPreset> Voices { get; } = [Clear, Warm, Broadcast, Podcast, DeepVoice, SoftSibilants];

    /// <summary>The listening curves, for what an output plays.</summary>
    public static IReadOnlyList<EqPreset> Listening { get; } = [VoicesForward, Footsteps, BassBoost, SoftTreble];

    /// <summary>Every built-in profile in the order every client lists them: the voice curves, then the listening curves.</summary>
    public static IReadOnlyList<EqPreset> All { get; } = [.. Voices, .. Listening];

    /// <summary>The preset with this id (<c>builtin:</c> and its key), or null.</summary>
    public static EqPreset? FindId(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
