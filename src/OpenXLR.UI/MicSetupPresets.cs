using System;
using System.Collections.Generic;
using System.Linq;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// A starting point for a speaking voice in Mic setup's Shape step: the
/// microphone EQ's four bands, with the EQ switched on. Applying one is a
/// single <c>setEq</c>, so it is heard at once and undone in one step.
/// </summary>
public sealed record MicSetupPreset(string Id, MicEq Eq)
{
    /// <summary>The preset's name in the UI's language.</summary>
    public string Name => NameOf(Id) ?? Localizer.Text("MicSetupPresetClear");

    /// <summary>A built-in preset's name in the UI's language, from its key; null for a key this UI does not know.</summary>
    public static string? NameOf(string key) => key switch
    {
        "clear" => Localizer.Text("MicSetupPresetClear"),
        "warm" => Localizer.Text("MicSetupPresetWarm"),
        "broadcast" => Localizer.Text("MicSetupPresetBroadcast"),
        "podcast" => Localizer.Text("MicSetupPresetPodcast"),
        "higher-voice" => Localizer.Text("MicSetupPresetHigherVoice"),
        "deep-voice" => Localizer.Text("MicSetupPresetDeepVoice"),
        "soft-sibilants" => Localizer.Text("MicSetupPresetSoftSibilants"),
        "voices-forward" => Localizer.Text("EqPresetVoicesForward"),
        "footsteps" => Localizer.Text("EqPresetFootsteps"),
        "bass-boost" => Localizer.Text("EqPresetBassBoost"),
        "soft-treble" => Localizer.Text("EqPresetSoftTreble"),
        _ => null,
    };

    /// <summary>What the preset does to a voice, for its tooltip.</summary>
    public string Hint => Id switch
    {
        "warm" => Localizer.Text("MicSetupPresetWarmTip"),
        "broadcast" => Localizer.Text("MicSetupPresetBroadcastTip"),
        "podcast" => Localizer.Text("MicSetupPresetPodcastTip"),
        "higher-voice" => Localizer.Text("MicSetupPresetHigherVoiceTip"),
        "deep-voice" => Localizer.Text("MicSetupPresetDeepVoiceTip"),
        "soft-sibilants" => Localizer.Text("MicSetupPresetSoftSibilantsTip"),
        _ => Localizer.Text("MicSetupPresetClearTip"),
    };

    /// <summary>The preset's EQ held to the ranges the daemon reports, which the shipped values are inside.</summary>
    public MicEq For(EqLimits limits) => MicEq.Snap(Eq with
    {
        Bands = [.. Eq.Bands.Select(b => b with
        {
            Frequency = Math.Clamp(b.Frequency, limits.Frequency.Min, limits.Frequency.Max),
            Gain = Math.Clamp(b.Gain, limits.Gain.Min, limits.Gain.Max),
            Q = Math.Clamp(b.Q, limits.Q.Min, limits.Q.Max),
        })],
    });

    /// <summary>The EQ holds this preset as the ranges the daemon reports hold it, by <see cref="MicEq.Holds"/>.</summary>
    public bool Holds(MicEq eq, EqLimits limits) => eq.Holds(For(limits));
}

/// <summary>
/// The presets Mic setup offers: the voice curves of <see cref="EqPresets"/>,
/// the one table the daemon and the EQ editor use too, in the same order.
/// The listening curves are for outputs and are not offered here.
/// </summary>
public static class MicSetupPresets
{
    /// <summary>The presets in the order the Shape step shows them.</summary>
    public static IReadOnlyList<MicSetupPreset> All { get; } = [.. EqPresets.Voices.Select(p => new MicSetupPreset(p.Key, p.Eq))];

    public static MicSetupPreset Clear => All[0];

    public static MicSetupPreset Warm => All[1];

    public static MicSetupPreset Broadcast => All[2];
}
