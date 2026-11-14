using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>What a command does to the undo history.</summary>
internal enum HistoryClass
{
    /// <summary>Leaves the history alone: a read, a live switch, a lock, machine wiring, or a category not covered yet.</summary>
    Ignored,
    /// <summary>Becomes a step, or joins the newest one.</summary>
    Recorded,
    /// <summary>Clears the history on success: it changes so much, or what the values mean, that older steps would only be refused.</summary>
    Barrier,
}

/// <summary>
/// What a recorded command changes: the step's kind and the ids it names,
/// the scene fields to compare and which of their keys. Comparing only these
/// keeps a desktop volume change that lands between the two reads out of a
/// fader's step.
/// </summary>
internal sealed record HistoryTarget(string Kind, string? Channel, string? Mix, string? Group,
    IReadOnlyCollection<SceneField> Fields, Func<SceneKey, bool> Keep)
{
    /// <summary>One string per thing a step changes, so a burst can say how many it joined.</summary>
    public string Identity => $"{Kind}|{Channel}|{Mix}|{Group}";
}

/// <summary>
/// The undo policy: every command the daemon takes, with its class. A test
/// holds this table to the command table in docs/api.md and to the hub's
/// dispatch, so a new command cannot be added without deciding where it
/// stands. The first version records send levels, volumes and mutes; later
/// categories move from <see cref="HistoryClass.Ignored"/> to
/// <see cref="HistoryClass.Recorded"/> with a target in
/// <see cref="TargetOf"/> and a field in <see cref="SceneField"/>, and the
/// channel and mix deletes and profile loads stop being barriers once they
/// can be undone.
/// </summary>
internal static class HistoryPolicy
{
    private static readonly string[] Recorded =
    [
        "setLevel", "setChannelVolume", "setMixVolume", "setChannelMuted", "setMixMuted", "cycleExclusiveGroup",
    ];

    private static readonly string[] Barriers =
    [
        "loadProfile", "deleteChannel", "deleteMix", "setMixing", "setActiveDevice", "resetDevice",
    ];

    // Everything else. Phantom power, the locks and the live switches
    // (hardware mute, capture mute, the monitoring pause) stay here for good;
    // the processing, hardware, insert and layout commands until their
    // milestones. setWaveInterfaceEnabled is a barrier only through the
    // deleteChannel it sends for a unit's capture channels.
    private static readonly string[] Ignored =
    [
        "undo", "redo",
        "getState", "getBrowserSession", "syncBrowserTabs", "activateBrowserTab", "getDiagnostics", "getRuntimeDiagnostics",
        "getSoundCheckTake", "getSoundboard", "importClip", "exportSoundboard", "importSoundboard", "restoreStarterSounds",
        "listPlugins", "getNativeEditorRules", "setNativeEditorRule", "getPluginDiagnostics", "getPluginSetup",
        "setPluginWineTrace", "installPlugin", "addWindowsPluginFolder", "removeWindowsPluginFolder", "getWindowsPluginFiles",
        "removeWindowsPluginInserts", "setWindowsPluginEnabled", "deleteWindowsPlugin", "syncWindowsPlugins",
        "addPluginSearchPath", "removePluginSearchPath", "rescanPlugins",
        "setInputBinding", "setCaptureSource", "setInserts", "set",
        "createCaptureChannel", "createChannel", "renameChannel", "createMix", "renameMix", "setLayoutOrder",
        "setExclusiveGroup", "deleteExclusiveGroup", "setLayoutAppearance",
        "setCaptureGain", "setCaptureMuted", "setCaptureOwnClock", "toggleCaptureMuted",
        "adjustOutputVolume", "setOutputDeviceVolume", "toggleOutputMute", "setMainOutput", "routeFocusedApp",
        "assignStream", "assignApp", "assignBrowserTab", "setAppVolume", "setBrowserTabVolume", "setRouteAppsByDefault",
        "setBlockSize", "setPreserveBrowserOutputs", "forgetApp",
        "setMonitorOutput", "setMonitorOutputs", "setMonitorMuted", "setMonitorMuteMicrophones", "toggleMonitorMuted",
        "setMonitorFeed", "setOutputVolume", "setOutputVolumeLock", "setEnforcedDefaults", "setAuxPortEnabled",
        "setLowCutHz", "setSoftClipGuard", "setMixLatencyCompensation",
        "soundCheck", "setSoundCheckRaw", "setSoundCheckHearMyself", "watchInputMeter",
        "setInsertBypass", "setInsertOwnBlock", "setInsertParam", "renameInsert", "showInsertUi",
        "setEq", "setEqEnabled", "setEqBand", "setEqProcessing", "loadEqProfile",
        "setNoiseRemoval", "setVoiceChanger", "setCompressor", "setExpander", "setMonitorDry", "setMixMaximizer",
        "setDynamicsProcessing", "watchDynamics", "setDucking", "setDuckingProcessing",
        "setSoundboard", "updateClip", "renameClipPage", "deleteClip", "setClipOrder", "playClip", "releaseClip",
        "stopClip", "stopClips", "auditionClip", "stopAudition",
        "setWaveInterfaceEnabled", "setWaveControl",
        "listEqProfiles", "saveEqProfile", "renameEqProfile", "deleteEqProfile",
        "saveProfile", "deleteProfile", "setRecallOnConnect", "setLed", "recoverCapture",
        "setAudioClock", "restartAudioServices", "restartDaemon",
    ];

    /// <summary>Every command name with its class.</summary>
    internal static readonly IReadOnlyDictionary<string, HistoryClass> Classes =
        Recorded.Select(c => (c, HistoryClass.Recorded))
            .Concat(Barriers.Select(c => (c, HistoryClass.Barrier)))
            .Concat(Ignored.Select(c => (c, HistoryClass.Ignored)))
            .ToDictionary(p => p.c, p => p.Item2, StringComparer.Ordinal);

    /// <summary>The class of a command; an unknown one, which the hub refuses, leaves the history alone.</summary>
    internal static HistoryClass ClassOf(string command) => Classes.GetValueOrDefault(command, HistoryClass.Ignored);

    /// <summary>The kinds a step can have, one per recorded category, as the state names them.</summary>
    internal static readonly IReadOnlyList<string> Kinds =
        ["sendLevel", "channelVolume", "mixVolume", "sendMute", "mixMute", "exclusiveGroup"];

    /// <summary>
    /// What a recorded command changes, or null when it does not name its
    /// target well enough to be recorded (the command is then refused anyway).
    /// <paramref name="peersOf"/> gives a channel's exclusive group with the
    /// channel in it, since unmuting a send closes the other members;
    /// <paramref name="membersOf"/> a group's members by id.
    /// </summary>
    internal static HistoryTarget? TargetOf(Command cmd, Func<string, IReadOnlyList<string>> peersOf,
        Func<string, IReadOnlyList<string>> membersOf)
    {
        switch (cmd.Cmd)
        {
            case "setLevel" when cmd.Channel is { } channel && cmd.Mix is { } mix:
            {
                string cell = Cell(channel, mix);
                return new("sendLevel", channel, mix, null, [SceneField.Level], key => key.Key == cell);
            }
            case "setChannelVolume" when cmd.Channel is { } channel:
                return new("channelVolume", channel, null, null, [SceneField.ChannelVolume], key => key.Key == channel);
            case "setMixVolume" when cmd.Mix is { } mix:
                return new("mixVolume", null, mix, null, [SceneField.MixVolume], key => key.Key == mix);
            case "setChannelMuted" when cmd.Channel is { } channel && cmd.Mix is { } mix:
            {
                var members = new HashSet<string>(peersOf(channel), StringComparer.Ordinal) { channel };
                return new("sendMute", channel, mix, null, [SceneField.ChannelMuted], key => members.Contains(ChannelOf(key.Key)));
            }
            case "setMixMuted" when cmd.Mix is { } mix:
                // Opening a monitor mix during a monitoring pause mutes the other monitor mixes.
                return new("mixMute", null, mix, null, [SceneField.MixMuted], _ => true);
            case "cycleExclusiveGroup" when cmd.Group is { } group:
            {
                var members = new HashSet<string>(membersOf(group), StringComparer.Ordinal);
                return new("exclusiveGroup", null, null, group, [SceneField.ChannelMuted], key => members.Contains(ChannelOf(key.Key)));
            }
            default:
                return null;
        }
    }

    /// <summary>One English phrase for a step, with the names the layout gives its ids now.</summary>
    internal static string LabelOf(HistoryTarget target, MixerConfig layout)
    {
        string Channel() => layout.Channels.FirstOrDefault(c => c.Id == target.Channel)?.Name ?? target.Channel ?? "";
        string Mix() => layout.Mixes.FirstOrDefault(m => m.Id == target.Mix)?.Name ?? target.Mix ?? "";
        string Group() => layout.ExclusiveGroups.FirstOrDefault(g => g.Id == target.Group)?.Name ?? target.Group ?? "";
        return target.Kind switch
        {
            "sendLevel" => $"{Channel()} level in {Mix()}",
            "channelVolume" => $"{Channel()} volume",
            "mixVolume" => $"{Mix()} volume",
            "sendMute" => $"{Channel()} mute in {Mix()}",
            "mixMute" => $"{Mix()} mute",
            "exclusiveGroup" => $"{Group()} switch",
            _ => target.Kind,
        };
    }

    private static string Cell(string channel, string mix) => $"{channel}|{mix}";

    private static string ChannelOf(string cell)
    {
        int bar = cell.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? cell : cell[..bar];
    }
}
