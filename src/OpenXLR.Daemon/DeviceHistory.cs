using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// One hardware value a step changed: on which unit, by its instance id and
/// the name it showed, under the control name <c>set</c> takes, as it was
/// before and as the step left it. Values are boxed ints, doubles or
/// booleans.
/// </summary>
internal sealed record DeviceChange(string Unit, string UnitName, string Control, object Before, object After);

/// <summary>One entry of a device patch: write <see cref="Target"/> where the unit still reads <see cref="Expected"/>.</summary>
internal sealed record DevicePatchEntry(string Control, object Expected, object Target);

/// <summary>What became of one entry of a device patch.</summary>
internal enum DevicePatchOutcome
{
    /// <summary>The unit read the expected value and the target was written.</summary>
    Applied,
    /// <summary>The unit already read the target; nothing was written.</summary>
    AlreadyThere,
    /// <summary>The unit's dial, another program or a recall moved the value since; it was left alone.</summary>
    Changed,
    /// <summary>The gain lock, the unit's own gain lock or a level lock holds the value.</summary>
    Locked,
    /// <summary>The unit is not driven: unplugged, or another unit drives in its place.</summary>
    Gone,
    /// <summary>The write was refused or the unit did not answer.</summary>
    Failed,
}

/// <summary>
/// What a recorded <c>set</c> or <c>setWaveControl</c> did: the error, or
/// the unit it wrote with the state before the write and the readback after
/// it. <see cref="Additional"/> is true for a unit driven as an additional
/// interface.
/// </summary>
internal sealed record DeviceWrite(string? Error, string? Unit = null, string? UnitName = null, bool Additional = false,
    DeviceState? Before = null, DeviceState? After = null);

/// <summary>One hardware control the history may hold: its kind, which XLR input it belongs to (0 for the whole unit) and where the state holds it.</summary>
internal sealed record DeviceControl(string Kind, int Input, Func<DeviceState, object?> Read);

/// <summary>
/// The hardware side of the undo history. Only the controls in
/// <see cref="Controls"/> are ever recorded or written back: the gain,
/// headphone levels, crossfade, low cut, ClipGuard, expander, compressor,
/// Voice Tune, low impedance, the Aux level and the Line In gain. Phantom
/// power is held out twice: the recorder drops it by name, and the device
/// manager's patch writes nothing outside this list. The hardware mute, the
/// locks, the Pro's output selectors, raw capture and every field the
/// daemon stamps or only reads are not on it either.
/// </summary>
internal static class DeviceHistory
{
    /// <summary>The controls a step can carry, by the name <c>set</c> takes.</summary>
    internal static readonly IReadOnlyDictionary<string, DeviceControl> Controls = new Dictionary<string, DeviceControl>(StringComparer.Ordinal)
    {
        [ControlNames.Gain] = new("unitGain", 1, s => s.GainDb),
        ["gain2"] = new("unitGain", 2, s => s.Gain2Db),
        [ControlNames.LowCut] = new("unitLowCut", 1, s => s.LowCut),
        ["lowCut2"] = new("unitLowCut", 2, s => s.LowCut2),
        [ControlNames.ClipGuard] = new("unitClipGuard", 1, s => s.ClipGuard),
        ["clipGuard2"] = new("unitClipGuard", 2, s => s.ClipGuard2),
        [ControlNames.Expander] = new("unitExpander", 1, s => s.Expander),
        ["expander2"] = new("unitExpander", 2, s => s.Expander2),
        [ControlNames.Compressor] = new("unitCompressor", 1, s => s.Compressor),
        ["compressor2"] = new("unitCompressor", 2, s => s.Compressor2),
        [ControlNames.VoiceTune] = new("unitVoiceTune", 1, s => s.VoiceTune),
        ["voiceTune2"] = new("unitVoiceTune", 2, s => s.VoiceTune2),
        [ControlNames.VoiceTuneStrength] = new("unitVoiceTune", 1, s => s.VoiceTuneStrength),
        ["voiceTuneStrength2"] = new("unitVoiceTune", 2, s => s.VoiceTuneStrength2),
        [ControlNames.HpVolumeDb] = new("unitHeadphones", 0, s => s.HpVolumeDb),
        ["hp2VolumeDb"] = new("unitHeadphones", 0, s => s.Hp2VolumeDb),
        [ControlNames.Crossfade] = new("unitCrossfade", 0, s => s.Crossfade),
        [ControlNames.LowImpedance] = new("unitLowImpedance", 0, s => s.LowImpedance),
        [ControlNames.AuxLevelDb] = new("unitAuxLevel", 0, s => s.AuxLevelDb),
        [ControlNames.LineInGain] = new("unitLineInGain", 0, s => s.LineInGainDb),
    };

    /// <summary>Never recorded and never written by a step, whatever the list above says.</summary>
    internal static readonly IReadOnlySet<string> Never = new HashSet<string>(StringComparer.Ordinal) { ControlNames.Phantom, "phantom2" };

    /// <summary>The hardware kinds, as the state names them.</summary>
    internal static readonly IReadOnlyList<string> Kinds =
        ["unitGain", "unitLowCut", "unitClipGuard", "unitExpander", "unitCompressor", "unitVoiceTune",
            "unitHeadphones", "unitCrossfade", "unitLowImpedance", "unitAuxLevel", "unitLineInGain"];

    /// <summary>Whether a control may be in a step: on the list and not phantom power.</summary>
    internal static bool Allowed(string control) => !Never.Contains(control) && Controls.ContainsKey(control);

    /// <summary>
    /// What a successful write changed, or null when the control is not on
    /// the list or the unit reads back what it read before. Only the
    /// control the command named is compared, so a dial turned on the unit
    /// during the write stays out of the step.
    /// </summary>
    internal static DeviceChange? ChangeOf(DeviceWrite write, string control)
    {
        if (write is not { Error: null, Unit: { } unit, Before: { } before, After: { } after }) return null;
        if (!Allowed(control)) return null;
        DeviceControl read = Controls[control];
        if (read.Read(before) is not { } was || read.Read(after) is not { } now || Equals(was, now)) return null;
        return new(unit, write.UnitName ?? unit, control, was, now);
    }

    /// <summary>
    /// The step a change makes. A control of one XLR input on the primary
    /// interface names that input's channel; one on an additional interface
    /// names the unit instead.
    /// </summary>
    internal static HistoryTarget TargetOf(DeviceChange change, bool additional)
    {
        DeviceControl control = Controls[change.Control];
        string? channel = !additional && control.Input > 0 ? $"xlr{control.Input}" : null;
        return new(control.Kind, channel, null, null, [], _ => false)
        {
            Control = change.Control,
            Unit = additional ? change.Unit : null,
        };
    }

    /// <summary>
    /// One English phrase for a hardware step, or null for another kind.
    /// <paramref name="unitName"/> is the additional interface's name, null
    /// on the primary.
    /// </summary>
    internal static string? LabelOf(HistoryTarget target, MixerConfig layout, string? unitName = null)
    {
        int input = target.Control is { } control && Controls.TryGetValue(control, out DeviceControl? c) ? c.Input
            : target.Channel == "xlr2" ? 2 : 1;
        string Input() => unitName is not null
            ? input == 2 ? $"{unitName} XLR 2" : unitName
            : layout.Channels.FirstOrDefault(ch => ch.Id == target.Channel)?.Name ?? $"XLR {input}";
        string Unit(string phrase) => unitName is not null ? $"{unitName} {phrase}" : char.ToUpperInvariant(phrase[0]) + phrase[1..];
        return target.Kind switch
        {
            "unitGain" => $"{Input()} gain",
            "unitLowCut" => $"{Input()} low cut",
            "unitClipGuard" => $"{Input()} ClipGuard",
            "unitExpander" => $"{Input()} expander",
            "unitCompressor" => $"{Input()} compressor",
            "unitVoiceTune" => $"{Input()} Voice Tune",
            "unitHeadphones" => Unit(target.Control == "hp2VolumeDb" ? "headphone 2 volume" : "headphone volume"),
            "unitCrossfade" => Unit("Mic/PC blend"),
            "unitLowImpedance" => Unit("headphone impedance"),
            "unitAuxLevel" => Unit("USB Aux level"),
            "unitLineInGain" => Unit("Line In gain"),
            _ => null,
        };
    }

    /// <summary>
    /// Whether a lock holds the control: the gain lock or the unit's own gain
    /// lock for a gain, the level locks for the Aux level and the Line In
    /// gain. <c>set</c> checks only the gain lock; a step respects all of
    /// them and never touches a lock.
    /// </summary>
    internal static bool Locked(string control, DeviceState live, bool gainLocked) => control switch
    {
        ControlNames.Gain or "gain2" => gainLocked || live.UnitGainLock == true,
        ControlNames.AuxLevelDb => live.AuxLevelLock,
        ControlNames.LineInGain => live.LineInLevelLock == true,
        _ => false,
    };

    /// <summary>
    /// The same value as the unit reads it. Levels in dB come back in
    /// quarter steps, so a hundredth of a dB apart is the same level.
    /// </summary>
    internal static bool Near(object a, object b) => a is double x && b is double y ? Math.Abs(x - y) <= 0.01 : Equals(a, b);
}
