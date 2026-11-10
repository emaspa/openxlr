using System.Text.Json;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// One place that decides whether a mixer command is well formed before the
/// mixer sees it: known channel and mix ids, catalogued and supported
/// plugins, declared parameter symbols, finite numbers, bounded strings and
/// lists. The mixer methods used to swallow an unknown id silently; now the
/// client gets an error and nothing unbounded reaches memory or mixer.json.
/// </summary>
public static class CommandValidation
{
    /// <summary>
    /// A strip's binding: no source and no port returns it to the interface;
    /// a source takes capturePair 0 and a mono port from 0 to 63, default 0.
    /// </summary>
    private static string? CheckBinding(Command cmd) => cmd.Source is null
        ? cmd.CapturePair == 0 && cmd.CaptureMonoChannel is null ? null : $"{cmd.Cmd}: returning the strip to the interface takes no port"
        : CaptureBinding.IsValid(cmd.Source, cmd.CapturePair, cmd.CaptureMonoChannel ?? 0) ? null
            : $"{cmd.Cmd}: need an external source and a mono port from 0 to 63, with capturePair 0";

    /// <summary>The additional-interface commands: an instance id, and a boolean or a finite number.</summary>
    internal static string? CheckWave(Command cmd)
    {
        if (!WaveInterfaces.ValidId(cmd.Device)) return $"{cmd.Cmd}: need a Wave interface instance id from waveInterfaces";
        if (cmd.Cmd == "setWaveInterfaceEnabled")
            return cmd.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ? "setWaveInterfaceEnabled: value must be a boolean"
                : cmd.KeepChannels.ValueKind is JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False ? null
                : "setWaveInterfaceEnabled: keepChannels must be a boolean";
        if (cmd.Control is not { Length: > 0 and <= 64 } || cmd.Control.Any(char.IsControl)) return "setWaveControl: need 'control'";
        return cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            || (cmd.Value.ValueKind == JsonValueKind.Number && cmd.Value.TryGetDouble(out double value) && double.IsFinite(value))
            ? null : "setWaveControl: value must be a boolean or a finite number";
    }

    /// <summary><c>setMixing</c>: a value of <c>software</c> or <c>hardware</c>, and <c>check</c> absent or a boolean.</summary>
    internal static string? CheckMixing(Command cmd)
        => cmd.Value.ValueKind != JsonValueKind.String || !MixingMode.IsValid(cmd.Value.GetString())
            ? "setMixing: value must be 'software' or 'hardware'"
            : cmd.Check.ValueKind is JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False ? null
            : "setMixing: check must be a boolean";

    /// <summary><c>setAudioClock</c>: a value null or absent for Automatic, or an ALSA node name (<see cref="AudioClockRule.IsValidNode"/>).</summary>
    internal static string? CheckAudioClock(Command cmd)
        => cmd.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || cmd.Value.ValueKind == JsonValueKind.String && AudioClockRule.IsValidNode(cmd.Value.GetString())
            ? null : "setAudioClock: value must be null or the node name of an ALSA device, alsa_input. or alsa_output. followed by letters, digits, '_', '-' and '.'";

    public const int MaxText = 256;          // identities, labels, device names, symbols
    public const int MaxUri = 512;
    public const int MaxDevices = 16;
    public const int MaxInsertsPerChannel = 16;
    public const int MaxInsertId = 64;
    public const int MaxParamsPerInsert = InsertDefinition.MaxParams;
    public const int MaxOverrides = Mixer.MaxAppOverrides;

    public static string? Check(Command cmd, ILayoutInfo layout, Func<InsertDefinition, PluginInfo?> findPlugin)
        => Check(cmd, layout, findPlugin, nativeHostInstalled: null);

    internal static string? Check(Command cmd, ILayoutInfo layout, Func<InsertDefinition, PluginInfo?> findPlugin,
        bool? nativeHostInstalled)
    {
        switch (cmd.Cmd)
        {
            case "addPluginSearchPath":
            case "removePluginSearchPath":
                return CheckPluginSearchPath(cmd);
            case "soundCheck":
                if (cmd.Channel is null || !layout.IsSoundCheckChannel(cmd.Channel)
                    || cmd.Action is not ("record" or "loop" or "live" or "stop"))
                    return "soundCheck: need a microphone channel and action 'record', 'loop', 'live' or 'stop'";
                // Stop always goes through, so a session can be ended whatever
                // the helper's state; anything else needs the helper.
                return cmd.Action != "stop" && !(nativeHostInstalled ?? PluginCatalog.HostInstalled)
                    ? Mixer.SoundCheckNeedsHost : null;
            case "setSoundCheckRaw":
            case "setSoundCheckHearMyself":
                if (cmd.Channel is null || !layout.IsSoundCheckChannel(cmd.Channel)
                    || cmd.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return $"{cmd.Cmd}: need a microphone channel and a boolean value";
                return !(nativeHostInstalled ?? PluginCatalog.HostInstalled) ? Mixer.SoundCheckNeedsHost : null;
            case "watchInputMeter":
                if (cmd.Channel is null || !layout.IsSoundCheckChannel(cmd.Channel))
                    return "watchInputMeter: need a microphone channel";
                return !(nativeHostInstalled ?? PluginCatalog.HostInstalled) ? Mixer.SoundCheckNeedsHost : null;
            case "setWaveInterfaceEnabled":
            case "setWaveControl":
                return CheckWave(cmd);
            case "setSoundboard":
            case "importClip":
            case "updateClip":
            case "deleteClip":
            case "playClip":
            case "releaseClip":
            case "stopClip":
            case "stopClips":
            case "auditionClip":
            case "stopAudition":
            case "setClipOrder":
            case "getSoundboard":
            case "renameClipPage":
            case "exportSoundboard":
            case "importSoundboard":
            case "restoreStarterSounds":
                return CheckSoundboard(cmd, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "setDucking":
                return CheckDucking(cmd, layout, out _);
            case "setDuckingProcessing":
                return CheckDuckingProcessing(cmd);
            case "setEq":
            case "setEqEnabled":
            case "setEqBand":
            case "setEqProcessing":
                return CheckEq(cmd, layout, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "loadEqProfile":
                return CheckLoadEqProfile(cmd, layout);
            case "listEqProfiles":
                return null;
            case "saveEqProfile":
            case "renameEqProfile":
            case "deleteEqProfile":
                return CheckEqProfile(cmd);
            case "setNoiseRemoval":
                return CheckNoise(cmd, layout, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "setVoiceChanger":
                return CheckVoice(cmd, layout, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "setCompressor":
                return CheckCompressor(cmd, layout, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "setExpander":
                return CheckExpander(cmd, layout, nativeHostInstalled ?? PluginCatalog.HostInstalled);
            case "setDynamicsProcessing":
                return CheckDynamicsProcessing(cmd, layout);
            case "setMonitorDry":
                return CheckMonitorDry(cmd, layout);
            case "watchDynamics":
                if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
                    return "watchDynamics: need a microphone channel";
                return !(nativeHostInstalled ?? PluginCatalog.HostInstalled) ? Mixer.DynamicsNeedsHost : null;
            case "getNativeEditorRules":
                return null;
            case "setNativeEditorRule":
                return CheckEditorRule(cmd);
            case "setPluginWineTrace":
                return CheckPluginWineTrace(cmd);
            case "setMixLatencyCompensation":
            case "setMonitorMuted":
            case "setRouteAppsByDefault":
            case "setPreserveBrowserOutputs":
            case "setMonitorMuteMicrophones":
                return cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null : $"{cmd.Cmd}: value must be a boolean";
            case "setMixing":
                return CheckMixing(cmd);
            case "setBlockSize":
                return cmd.Value.ValueKind is JsonValueKind.Null
                    || cmd.Value.ValueKind is JsonValueKind.Number && cmd.Value.TryGetInt32(out int frames) && Mixer.BlockSizes.Contains(frames)
                    ? null : $"setBlockSize: value must be null or one of {string.Join(", ", Mixer.BlockSizes)}";
            case "addWindowsPluginFolder":
            case "removeWindowsPluginFolder":
            case "getWindowsPluginFiles":
            case "removeWindowsPluginInserts":
            case "setWindowsPluginEnabled":
            case "deleteWindowsPlugin":
                return CheckPluginPath(cmd);
            case "setInputBinding":
                if (cmd.Channel == "aux") return "setInputBinding: Aux In stays on the interface; bind 'xlr1' or 'xlr2'";
                if (cmd.Channel == "line") return "setInputBinding: Line In stays on the interface; bind 'xlr1' or 'xlr2'";
                if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.HasBindableStrip(cmd.Channel))
                    return "setInputBinding: need channel 'xlr1' or 'xlr2'";
                return CheckBinding(cmd);
            case "setCaptureSource":
            case "createCaptureChannel":
                if (cmd.Cmd == "setCaptureSource")
                {
                    if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.HasCaptureChannel(cmd.Channel))
                        return "setCaptureSource: need a capture input channel";
                }
                else if (BadName(cmd.Name)) return "createCaptureChannel: name must contain 1 to 60 printable characters";
                return CaptureBinding.IsValid(cmd.Source, cmd.CapturePair, cmd.CaptureMonoChannel) ? null
                    : $"{cmd.Cmd}: need an external source, a pair from 0 to 31, and with pair 0 an optional mono port from 0 to 63";
            case "createMix":
                if (cmd.Kind is not (null or "virtualMic" or "monitor"))
                    return "createMix: kind must be virtualMic or monitor";
                if (BadName(cmd.Name)) return "createMix: name must contain 1 to 60 printable characters";
                return BadIcon(cmd.Icon) ? IconError("createMix") : null;
            case "createChannel":
                if (BadName(cmd.Name)) return $"{cmd.Cmd}: name must contain 1 to 60 printable characters";
                return BadIcon(cmd.Icon) ? IconError("createChannel") : null;
            case "renameChannel":
            case "deleteChannel":
                if (cmd.Channel is null) return $"{cmd.Cmd}: need 'channel'";
                if (TooLong(cmd.Channel, 36) || !layout.HasEditableChannel(cmd.Channel))
                    return $"{cmd.Cmd}: '{Short(cmd.Channel)}' is not an editable channel";
                return cmd.Cmd == "renameChannel" && BadName(cmd.Name) ? "renameChannel: name must contain 1 to 60 printable characters" : null;
            case "renameMix":
            case "deleteMix":
                if (cmd.Mix is null) return $"{cmd.Cmd}: need 'mix'";
                if (TooLong(cmd.Mix, 36) || !layout.HasEditableMix(cmd.Mix))
                    return $"{cmd.Cmd}: '{Short(cmd.Mix)}' is not an editable mix";
                return cmd.Cmd == "renameMix" && BadName(cmd.Name) ? "renameMix: name must contain 1 to 60 printable characters" : null;
            case "setLayoutOrder":
                if (cmd.Channels is null || cmd.Mixes is null) return "setLayoutOrder: need 'channels' and 'mixes'";
                if (cmd.Channels.Count > MixerConfig.MaxApplicationChannels || cmd.Mixes.Count > MixerConfig.MaxEditableMixes)
                    return "setLayoutOrder: too many IDs";
                if (cmd.Channels.Concat(cmd.Mixes).Any(id => id is null || id.Length is 0 or > 36))
                    return "setLayoutOrder: invalid ID";
                return null; // The mixer validates exact membership under its state lock.
            case "setExclusiveGroup":
                if (cmd.Group is not null && !ExclusiveGroupsModel.ValidId(cmd.Group)) return "setExclusiveGroup: invalid group id";
                if (ExclusiveGroupsModel.Validate(new(cmd.Group ?? "new", cmd.Name!, cmd.Channels!)) is { } bad)
                    return $"setExclusiveGroup: {bad}";
                return cmd.Channels!.FirstOrDefault(ch => !layout.HasChannel(ch)) is { } unknown
                    ? $"setExclusiveGroup: unknown channel '{unknown}'" : null;
            case "deleteExclusiveGroup":
            case "cycleExclusiveGroup":
                return ExclusiveGroupsModel.ValidId(cmd.Group) ? null : $"{cmd.Cmd}: need a valid 'group'";
            case "setLayoutAppearance":
                if (new[] { cmd.Channel, cmd.Mix, cmd.Output }.Count(t => t is not null) != 1)
                    return "setLayoutAppearance: need exactly one of 'channel', 'mix' and 'output'";
                if (cmd.Output is not null && !LayoutAppearance.Outputs.Contains(cmd.Output))
                    return $"setLayoutAppearance: unknown output '{Short(cmd.Output)}'; use phones1 or phones2";
                if (cmd.Channel is not null && (TooLong(cmd.Channel, 36) || !layout.HasChannel(cmd.Channel)))
                    return $"setLayoutAppearance: unknown channel '{Short(cmd.Channel)}'";
                if (cmd.Mix is not null && (TooLong(cmd.Mix, 36) || !layout.HasMix(cmd.Mix)))
                    return $"setLayoutAppearance: unknown mix '{Short(cmd.Mix)}'";
                if (!LayoutAppearance.IsValid(cmd.Appearance))
                    return "setLayoutAppearance: need 'appearance' with an empty, listed or named icon and a null or #RRGGBB colour";
                return cmd.Output is not null && cmd.Appearance!.Hidden ? "setLayoutAppearance: an output cannot be hidden" : null;
            case "setLevel":
            case "setChannelMuted":
                if (cmd.Channel is not null && !layout.HasChannel(cmd.Channel)) return $"{cmd.Cmd}: unknown channel '{Short(cmd.Channel)}'";
                if (cmd.Mix is not null && !layout.HasMix(cmd.Mix)) return $"{cmd.Cmd}: unknown mix '{Short(cmd.Mix)}'";
                return cmd.Cmd == "setLevel" ? Finite(cmd, "value") : null;
            case "setChannelVolume":
                if (cmd.Channel is null) return "setChannelVolume: need 'channel'";
                if (TooLong(cmd.Channel, 36) || !layout.HasChannel(cmd.Channel))
                    return $"setChannelVolume: unknown channel '{Short(cmd.Channel)}'";
                return Finite(cmd, "value");
            case "setCaptureGain":
                if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.HasCaptureChannel(cmd.Channel))
                    return "setCaptureGain: need a capture input channel";
                return Finite(cmd, "value");
            case "setCaptureOwnClock":
                if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.HasCaptureChannel(cmd.Channel))
                    return "setCaptureOwnClock: need a capture input channel";
                return cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null : "setCaptureOwnClock: value must be a boolean";
            case "setCaptureMuted":
            case "toggleCaptureMuted":
                if (cmd.Channel is null || TooLong(cmd.Channel, 36) || !layout.HasCaptureChannel(cmd.Channel))
                    return $"{cmd.Cmd}: need a capture input channel";
                return cmd.Cmd == "toggleCaptureMuted" || cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null : "setCaptureMuted: value must be a boolean";
            case "setMixVolume":
            case "setMixMuted":
                if (cmd.Mix is not null && !layout.HasMix(cmd.Mix)) return $"{cmd.Cmd}: unknown mix '{Short(cmd.Mix)}'";
                return cmd.Cmd == "setMixVolume" ? Finite(cmd, "value") : null;
            case "setOutputVolume":
                return Finite(cmd, "value");
            case "adjustOutputVolume":
            case "setOutputDeviceVolume":
            case "toggleOutputMute":
            case "setMainOutput":
                if (cmd.Device is not null && (cmd.Device.Length is 0 or > 256 || cmd.Device.Any(char.IsControl)))
                    return $"{cmd.Cmd}: invalid output name";
                if (cmd.Cmd == "setMainOutput" && cmd.Device is null) return "setMainOutput: need 'device'";
                if (cmd.Cmd == "adjustOutputVolume")
                    return Finite(cmd, "value") ?? (cmd.Value.GetDouble() is >= -.5 and <= .5 ? null : "adjustOutputVolume: step must be between -0.5 and 0.5");
                if (cmd.Cmd == "setOutputDeviceVolume")
                    return Finite(cmd, "value") ?? (cmd.Value.GetDouble() is >= 0 and <= 1.5 ? null : "setOutputDeviceVolume: volume must be between 0 and 1.5");
                return null;
            case "routeFocusedApp":
                return cmd.Channel is { Length: > 0 and <= 36 } && layout.HasApplicationChannel(cmd.Channel)
                    ? null : "routeFocusedApp: select an application channel";
            case "assignStream":
                if (cmd.Channel is not null && !IsChannelOrIgnore(layout, cmd.Channel)) return $"assignStream: unknown channel '{Short(cmd.Channel)}'";
                return null;
            case "setAppVolume":
                if (!Mixer.ValidAppVolumeIdentity(cmd.Identity)) return "setAppVolume: supply a valid application identity";
                return PlaybackVolume(cmd);
            case "setBrowserTabVolume":
                if (!BrowserTabRouting.ValidSession(cmd.Session) || cmd.TabId is not >= 0) return "setBrowserTabVolume: supply a browser session and tabId";
                if (BrowserTabRouting.CanonicalSite(cmd.Site) is null) return "setBrowserTabVolume: supply the tab's HTTP or HTTPS origin";
                if (cmd.Remember.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "setBrowserTabVolume: remember must be a boolean";
                return PlaybackVolume(cmd);
            case "assignBrowserTab":
                if (!BrowserTabRouting.ValidSession(cmd.Session) || cmd.TabId is not >= 0) return "assignBrowserTab: supply a browser session and tabId";
                if (BrowserTabRouting.CanonicalSite(cmd.Site) is null) return "assignBrowserTab: supply the tab's HTTP or HTTPS origin";
                if (cmd.Channel is not null && !layout.HasApplicationChannel(cmd.Channel)) return "assignBrowserTab: select an application channel or browser default";
                return cmd.Remember.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "assignBrowserTab: remember must be a boolean";
            case "activateBrowserTab":
                if (!BrowserTabRouting.ValidSession(cmd.Session) || cmd.TabId is not >= 0) return "activateBrowserTab: supply a browser session and tabId";
                return BrowserTabRouting.CanonicalSite(cmd.Site) is null ? "activateBrowserTab: supply the tab's HTTP or HTTPS origin" : null;
            case "getBrowserSession":
                return null;
            case "syncBrowserTabs":
                return !BrowserTabRouting.ValidSession(cmd.Session) ? "syncBrowserTabs: supply the connection's browser session"
                    : BrowserTabRouting.CheckTabs(cmd.Tabs);
            case "assignApp":
                if (cmd.Channel is not null && !IsChannelOrIgnore(layout, cmd.Channel)) return $"assignApp: unknown channel '{Short(cmd.Channel)}'";
                if (TooLong(cmd.Identity, MaxText)) return "assignApp: identity too long";
                if (TooLong(cmd.Label, MaxText)) return "assignApp: label too long";
                if (layout.OverrideCount >= MaxOverrides && (cmd.Identity is null || !layout.HasOverride(cmd.Identity)))
                    return $"assignApp: {MaxOverrides} remembered applications already; forget some first";
                return null;
            case "forgetApp":
                return TooLong(cmd.Identity, MaxText) ? "forgetApp: identity too long" : null;
            case "setMonitorOutput":
                return TooLong(cmd.Device, MaxText) ? "setMonitorOutput: device name too long" : null;
            case "setMonitorOutputs":
                if (cmd.Devices is { Count: > MaxDevices }) return $"setMonitorOutputs: at most {MaxDevices} devices";
                if (cmd.Devices?.Any(d => TooLong(d, MaxText)) == true) return "setMonitorOutputs: device name too long";
                return null;
            case "setRecallOnConnect":
                return TooLong(cmd.Name, MaxText) ? "setRecallOnConnect: name too long" : null;
            case "toggleMonitorMuted":
                return null;   // no arguments; toggle the current state under the mixer lock
            case "resetDevice":
                return null;   // no arguments; the device manager checks the device
            case "recoverCapture":
                return null;   // no arguments; the driven unit's capabilities decide
            case "restartDaemon":
                return null;   // no arguments
            case "setAudioClock":
                return CheckAudioClock(cmd);
            case "restartAudioServices":
                return null;   // no arguments
            case "setMonitorFeed":
                if (TooLong(cmd.Device, MaxText)) return "setMonitorFeed: device name too long";
                if (cmd.Mix is null || !layout.IsMonitorFeed(cmd.Mix)) return $"setMonitorFeed: '{Short(cmd.Mix ?? "")}' is not a mix or a sum of distinct mixes";
                if (cmd.Device is null || !layout.IsMonitorOutput(cmd.Device)) return $"setMonitorFeed: '{Short(cmd.Device ?? "")}' is not a selected monitor output";
                return null;
            case "setEnforcedDefaults":
                if (TooLong(cmd.Sink, MaxText) || TooLong(cmd.Source, MaxText)) return "setEnforcedDefaults: device name too long";
                return null;
            case "renameInsert":
                if (cmd.Channel is null || cmd.InsertId is null || !layout.IsInsertKey(cmd.Channel)
                    || layout.InsertInChain(cmd.Channel, cmd.InsertId) is null) return "renameInsert: select an existing insert";
                return string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Length > MaxText || cmd.Name.Any(char.IsControl)
                    ? $"renameInsert: name must be 1 to {MaxText} characters without control characters" : null;
            case "setInserts":
                if (cmd.Channel is null || cmd.Inserts is null) return "setInserts: need 'channel' and 'inserts'";
                if (!layout.IsInsertKey(cmd.Channel)) return $"setInserts: '{Short(cmd.Channel)}' has no insert chain";
                if (cmd.Inserts is { Count: > MaxInsertsPerChannel }) return $"setInserts: at most {MaxInsertsPerChannel} inserts per chain";
                if (cmd.Inserts is not null)
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (InsertDefinition i in cmd.Inserts)
                    {
                        if (i is null || string.IsNullOrWhiteSpace(i.Id) || i.Kind is not ("lv2" or "clap" or "vst3") || string.IsNullOrWhiteSpace(i.Plugin))
                            return "setInserts: every insert needs an id, a kind of 'lv2', 'clap' or 'vst3', and a plugin identifier";
                        if (i.Params is null) return "setInserts: parameters must be an object";
                        if (i.Id.Length > MaxInsertId || !i.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                            return "setInserts: insert ids are up to 64 letters, digits, '-' or '_'";
                        if (!ids.Add(i.Id)) return $"setInserts: duplicate insert id '{i.Id}'";
                        if (TooLong(i.Label, MaxText)) return "setInserts: label too long";
                        if (i.Plugin.Length > MaxUri) return "setInserts: plugin URI too long";
                        PluginInfo? plugin = findPlugin(i);
                        if (plugin is null) return $"setInserts: plugin '{Short(i.Plugin)}' is not installed";
                        if (i.NativeHost && !plugin.NativeEditorSupported)
                            return $"setInserts: '{plugin.Name}' has no editor the native host can open";
                        if (i.NeedsNativeHost && !(nativeHostInstalled ?? PluginCatalog.HostInstalled))
                            return "setInserts: the native plugin host is not installed on this machine";
                        if (!plugin.Supported)
                            return $"setInserts: '{plugin.Name}' needs {string.Join(", ", plugin.UnsupportedFeatures.Select(Tail))}, which the PipeWire chain does not provide";
                        // The chain's width is the plugin's to fit: one channel on a
                        // mono input, two on a stereo input or a mix. An insert already
                        // in the chain, the same plugin under the same id, is left to
                        // the chain builder, so removing it stays possible; an id kept
                        // while its plugin changes is an addition and is held to the rule.
                        bool alreadyThere = cmd.Channel is not null
                            && layout.InsertInChain(cmd.Channel, i.Id) is { } existing
                            && findPlugin(existing) is { } current
                            && current.Kind == plugin.Kind && current.Plugin == plugin.Plugin;
                        if (cmd.Channel is not null && !alreadyThere)
                        {
                            int width = layout.InsertChannels(cmd.Channel);
                            if (!plugin.Fits(width))
                                return width == 1
                                    ? $"setInserts: '{plugin.Name}' has no mono layout for an input chain"
                                    : cmd.Channel.StartsWith("mix:", StringComparison.Ordinal)
                                    ? $"setInserts: '{plugin.Name}' has no stereo layout for a mix chain"
                                    : $"setInserts: '{plugin.Name}' has no stereo layout for an input chain";
                        }
                        if (i.Params.Count > MaxParamsPerInsert) return "setInserts: too many parameters";
                        foreach ((string symbol, double value) in i.Params)
                        {
                            if (!plugin.Params.Any(p => p.Symbol == symbol)) return $"setInserts: '{plugin.Name}' has no control '{Short(symbol)}'";
                            if (!double.IsFinite(value)) return $"setInserts: '{symbol}' must be a finite number";
                        }
                    }
                }
                return null;
            case "setInsertOwnBlock":
                if (cmd.Channel is null || cmd.InsertId is null || !layout.IsInsertKey(cmd.Channel)
                    || layout.InsertInChain(cmd.Channel, cmd.InsertId) is null) return "setInsertOwnBlock: select an existing insert";
                if (cmd.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "setInsertOwnBlock: value must be a boolean";
                // Only the native host runs a plugin on its own block, so
                // turning it on needs the host; turning it off never does.
                return cmd.Value.GetBoolean() && !(nativeHostInstalled ?? PluginCatalog.HostInstalled)
                    ? "setInsertOwnBlock: the native plugin host is not installed on this machine" : null;
            case "setInsertBypass":
            case "showInsertUi":
                if (cmd.Channel is not null && !layout.IsInsertKey(cmd.Channel)) return $"{cmd.Cmd}: '{Short(cmd.Channel)}' has no insert chain";
                return TooLong(cmd.InsertId, MaxInsertId) ? $"{cmd.Cmd}: insert id too long" : null;
            case "setInsertParam":
                if (cmd.Channel is not null && !layout.IsInsertKey(cmd.Channel)) return $"setInsertParam: '{Short(cmd.Channel)}' has no insert chain";
                if (TooLong(cmd.InsertId, MaxInsertId) || TooLong(cmd.Symbol, MaxText)) return "setInsertParam: id or symbol too long";
                // The symbol names a line in the native host's protocol and
                // a key in the saved chain, so only a control the plugin
                // declares is accepted, as setInserts requires of a whole chain.
                if (cmd.Channel is not null && cmd.InsertId is not null && cmd.Symbol is not null)
                {
                    if (layout.InsertInChain(cmd.Channel, cmd.InsertId) is not { } insert)
                        return $"setInsertParam: '{Short(cmd.InsertId)}' is not in the chain";
                    if (cmd.Symbol.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                        || findPlugin(insert)?.Params.Any(p => p.Symbol == cmd.Symbol) != true)
                        return $"setInsertParam: '{Short(insert.Label ?? insert.Plugin)}' has no control '{Short(cmd.Symbol)}'";
                }
                return Finite(cmd, "value");
            default:
                return null;   // the service knows the rest, or reports the command as unknown
        }
    }

    /// <summary>
    /// The EQ commands: either a microphone channel (XLR 1, XLR 2 or a
    /// capture input on one mono port) as <c>channel</c>, or any mix as
    /// <c>mix</c>, never both, and values inside the EQ's ranges. A value
    /// outside a range is refused, not clamped; the mixer snaps what it takes
    /// onto the EQ's steps. Switching the EQ on is refused at once when the
    /// native helper is not installed and no unit runs the channel's EQ, since
    /// nothing could run it; values can still be set. Where the EQ runs is
    /// <c>device</c> or <c>software</c>, takes a microphone only, and is
    /// refused on a capture input, whose EQ always runs on this computer.
    /// </summary>
    internal static string? CheckEq(Command cmd, ILayoutInfo layout, bool hostInstalled)
    {
        if (EqTarget(cmd, layout, mixAllowed: cmd.Cmd != "setEqProcessing") is { } bad) return bad;
        if (cmd.Mix is not null)
        {
            switch (cmd.Cmd)
            {
                case "setEqEnabled":
                    if (cmd.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "setEqEnabled: value must be a boolean";
                    return cmd.Value.GetBoolean() && !hostInstalled ? Mixer.MixEqNeedsHost : null;
                case "setEq":
                    if (MicEq.Read(cmd.Eq, out string? mixError) is not { } mixEq) return $"setEq: {mixError}";
                    return mixEq.Enabled && !hostInstalled ? Mixer.MixEqNeedsHost : null;
                default:
                    return CheckEqBand(cmd);
            }
        }
        switch (cmd.Cmd)
        {
            case "setEqEnabled":
                if (cmd.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "setEqEnabled: value must be a boolean";
                return cmd.Value.GetBoolean() && !hostInstalled && !layout.EqRunsOnDevice(cmd.Channel!) ? Mixer.EqNeedsHost : null;
            case "setEq":
                if (MicEq.Read(cmd.Eq, out string? error) is not { } eq) return $"setEq: {error}";
                return eq.Enabled && !hostInstalled && !layout.CanRunEqOnDevice(cmd.Channel!, eq.Bands.Count) ? Mixer.EqNeedsHost : null;
            case "setEqProcessing":
                if (layout.EqRunsInSoftwareOnly(cmd.Channel!)) return Mixer.EqSoftwareOnly(Short(cmd.Channel!));
                return cmd.Value.ValueKind == JsonValueKind.String && MicEqProcessing.IsValid(cmd.Value.GetString())
                    ? null : "setEqProcessing: value must be 'device' or 'software'";
            default:
                return CheckEqBand(cmd);
        }
    }

    /// <summary>
    /// What an EQ command acts on: a microphone or capture input as <c>channel</c>, or, where
    /// <paramref name="mixAllowed"/>, any mix of the layout as <c>mix</c>.
    /// Exactly one of the two. Null when the target is right.
    /// </summary>
    private static string? PlaybackVolume(Command cmd)
        => cmd.Value.ValueKind == JsonValueKind.Null ? null
            : Finite(cmd, "value") ?? (Mixer.ValidPlaybackVolume(cmd.Value.GetDouble()) ? null : $"{cmd.Cmd}: volume must be between 0 and 1");

    private static string? EqTarget(Command cmd, ILayoutInfo layout, bool mixAllowed)
    {
        if (cmd.Channel is not null && cmd.Mix is not null) return $"{cmd.Cmd}: give 'channel' or 'mix', not both";
        if (cmd.Mix is { } mix)
        {
            if (!mixAllowed) return $"{cmd.Cmd}: takes a microphone 'channel'; a mix's EQ always runs on this computer";
            return TooLong(mix, 36) || !layout.HasMix(mix) ? $"{cmd.Cmd}: '{Short(mix)}' is not a mix" : null;
        }
        if (cmd.Channel is null) return mixAllowed ? $"{cmd.Cmd}: need 'channel' or 'mix'" : $"{cmd.Cmd}: need 'channel'";
        return TooLong(cmd.Channel, 36) || !layout.CarriesEqChannel(cmd.Channel)
            ? $"{cmd.Cmd}: {Mixer.NoEq(Short(cmd.Channel))}" : null;
    }

    /// <summary><c>setEqBand</c>'s band and fields, the same for a microphone and a mix.</summary>
    private static string? CheckEqBand(Command cmd)
    {
        if (cmd.Band is not int band || band is < 1 or > MicEq.MaximumBands) return $"setEqBand: need 'band' from 1 to {MicEq.MaximumBands}";
        var change = new EqBandChange(cmd.Enabled, cmd.Type, cmd.Frequency, cmd.Gain, cmd.Q);
        if (change.IsEmpty) return "setEqBand: need at least one of 'enabled', 'type', 'frequency', 'gain' and 'q'";
        if (change.Type is not null && MicEq.TypeIndex(change.Type) < 0)
            return $"setEqBand: type must be one of {string.Join(", ", MicEq.Types)}";
        // Each present field is checked on a band that is valid
        // otherwise, so the message names the field at fault.
        EqBand probe = change.ApplyTo(MicEq.Default.Bands[0] with { Type = change.Type ?? MicEq.Peak });
        return MicEq.Check(probe) is string problem ? $"setEqBand: {problem}" : null;
    }

    /// <summary>
    /// <c>loadEqProfile</c>: a microphone channel or a mix, as <c>setEq</c>
    /// takes them, and a profile id. Whether the profile exists, and whether
    /// the EQ can be switched on, is decided when it loads, as the
    /// <c>setEq</c> it becomes.
    /// </summary>
    internal static string? CheckLoadEqProfile(Command cmd, ILayoutInfo layout)
    {
        if (EqTarget(cmd, layout, mixAllowed: true) is { } bad) return bad;
        return EqProfileStore.CheckId(cmd.Profile) is { } error ? $"loadEqProfile: {error}" : null;
    }

    /// <summary>
    /// <c>saveEqProfile</c>, <c>renameEqProfile</c> and <c>deleteEqProfile</c>:
    /// a saved profile's name, a new one to rename it to, and for a save one
    /// to sixteen bands inside the EQ's ranges. A value outside a range is
    /// refused, not clamped.
    /// </summary>
    internal static string? CheckEqProfile(Command cmd)
    {
        if (cmd.Name is null || cmd.Name.Length > 256 || !EqProfileStore.ValidName(cmd.Name.Trim()))
            return $"{cmd.Cmd}: need 'name', {EqProfileStore.NameRule}";
        switch (cmd.Cmd)
        {
            case "renameEqProfile":
                return cmd.NewName is not null && cmd.NewName.Length <= 256 && EqProfileStore.ValidName(cmd.NewName.Trim())
                    ? null : $"renameEqProfile: need 'newName', {EqProfileStore.NameRule}";
            case "saveEqProfile":
                return EqProfileBands(cmd, out string? error) is null ? $"saveEqProfile: {error}" : null;
            default:
                return null;
        }
    }

    /// <summary>The bands of a <c>saveEqProfile</c>, read as <c>setEq</c> reads an EQ's; null when they cannot be taken.</summary>
    internal static IReadOnlyList<EqBand>? EqProfileBands(Command cmd) => EqProfileBands(cmd, out _);

    private static IReadOnlyList<EqBand>? EqProfileBands(Command cmd, out string? error)
    {
        error = "need 'bands'";
        if (cmd.Bands.ValueKind != JsonValueKind.Array) return null;
        MicEq? eq = MicEq.Read(JsonSerializer.SerializeToElement(new { enabled = true, bands = cmd.Bands }), out error);
        return eq?.Bands;
    }

    /// <summary>
    /// <c>setNoiseRemoval</c>: a microphone channel (one that carries the EQ)
    /// and at least one of <c>enabled</c>, <c>engine</c> and <c>strength</c>,
    /// each inside its range; a value outside it is refused, not clamped.
    /// Switching it on is refused at once when the native helper is not
    /// installed. Whether the helper carries the engine is the mixer's to say.
    /// </summary>
    internal static string? CheckNoise(Command cmd, ILayoutInfo layout, bool hostInstalled)
    {
        if (cmd.Channel is null) return "setNoiseRemoval: need 'channel'";
        if (TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
            return $"setNoiseRemoval: '{Short(cmd.Channel)}' is not a microphone input";
        var change = new MicNoiseChange(cmd.Enabled, cmd.Engine, cmd.Strength);
        if (change.IsEmpty) return "setNoiseRemoval: need at least one of 'enabled', 'engine' and 'strength'";
        if (MicNoise.Check(change.ApplyTo(MicNoise.Default)) is string problem) return $"setNoiseRemoval: {problem}";
        return cmd.Enabled == true && !hostInstalled ? Mixer.NoiseNeedsHost : null;
    }

    /// <summary>
    /// <c>setVoiceChanger</c>: a microphone channel (one that carries the EQ)
    /// and at least one of <c>enabled</c>, <c>voice</c>, <c>pitch</c> and
    /// <c>formant</c>, each inside its range; a value outside it is refused,
    /// not clamped. Switching it on is refused at once when the native helper
    /// is not installed.
    /// </summary>
    internal static string? CheckVoice(Command cmd, ILayoutInfo layout, bool hostInstalled)
    {
        if (cmd.Channel is null) return "setVoiceChanger: need 'channel'";
        if (TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
            return $"setVoiceChanger: '{Short(cmd.Channel)}' is not a microphone input";
        var change = new MicVoiceChange(cmd.Enabled, cmd.Voice, cmd.Pitch, cmd.Formant);
        if (change.IsEmpty) return "setVoiceChanger: need at least one of 'enabled', 'voice', 'pitch' and 'formant'";
        if (cmd.Voice is not null && (TooLong(cmd.Voice, 32) || MicVoice.VoiceIndex(cmd.Voice) < 0))
            return $"setVoiceChanger: voice must be one of {string.Join(", ", MicVoice.Voices)}";
        if (MicVoice.Check(change.ApplyTo(MicVoice.Default)) is string problem) return $"setVoiceChanger: {problem}";
        return cmd.Enabled == true && !hostInstalled ? Mixer.VoiceNeedsHost : null;
    }

    /// <summary>
    /// <c>setCompressor</c>: a microphone channel and at least one of
    /// <c>enabled</c>, <c>threshold</c>, <c>ratio</c>, <c>attack</c>,
    /// <c>release</c>, <c>knee</c> and <c>makeup</c>, each inside its range;
    /// a value outside it is refused, not clamped. Switching it on is refused
    /// at once when the native helper is not installed and no unit runs it.
    /// </summary>
    internal static string? CheckCompressor(Command cmd, ILayoutInfo layout, bool hostInstalled)
    {
        if (cmd.Channel is null) return "setCompressor: need 'channel'";
        if (TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
            return $"setCompressor: '{Short(cmd.Channel)}' is not a microphone input";
        var change = new MicCompressorChange(cmd.Enabled, cmd.Threshold, cmd.Ratio, cmd.Attack, cmd.Release, cmd.Knee, cmd.Makeup);
        if (change.IsEmpty) return "setCompressor: need at least one of 'enabled', 'threshold', 'ratio', 'attack', 'release', 'knee' and 'makeup'";
        if (MicCompressor.Check(change.ApplyTo(MicCompressor.Default)) is string problem) return $"setCompressor: {problem}";
        return cmd.Enabled == true && !hostInstalled && !layout.DynamicsRunOnDevice(cmd.Channel) ? Mixer.DynamicsNeedsHost : null;
    }

    /// <summary>
    /// <c>setExpander</c>: a microphone channel and at least one of
    /// <c>enabled</c>, <c>threshold</c>, <c>ratio</c>, <c>range</c>,
    /// <c>attack</c>, <c>release</c> and <c>hold</c>, each inside its range.
    /// Switching it on is refused at once when the native helper is not
    /// installed and no unit runs it.
    /// </summary>
    internal static string? CheckExpander(Command cmd, ILayoutInfo layout, bool hostInstalled)
    {
        if (cmd.Channel is null) return "setExpander: need 'channel'";
        if (TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
            return $"setExpander: '{Short(cmd.Channel)}' is not a microphone input";
        var change = new MicExpanderChange(cmd.Enabled, cmd.Threshold, cmd.Ratio, cmd.Range, cmd.Attack, cmd.Release, cmd.Hold);
        if (change.IsEmpty) return "setExpander: need at least one of 'enabled', 'threshold', 'ratio', 'range', 'attack', 'release' and 'hold'";
        if (MicExpander.Check(change.ApplyTo(MicExpander.Default)) is string problem) return $"setExpander: {problem}";
        return cmd.Enabled == true && !hostInstalled && !layout.DynamicsRunOnDevice(cmd.Channel) ? Mixer.DynamicsNeedsHost : null;
    }

    /// <summary>
    /// <c>setDynamicsProcessing</c>: XLR 1 or XLR 2 and a <c>value</c> of
    /// <c>device</c> or <c>software</c>, as <c>setEqProcessing</c> takes it. A
    /// capture input's compressor and expander always run in software, so it
    /// is refused there. A unit that cannot run them is no refusal: the choice
    /// is kept and takes effect once it can.
    /// </summary>
    internal static string? CheckDynamicsProcessing(Command cmd, ILayoutInfo layout)
    {
        if (cmd.Channel is null) return "setDynamicsProcessing: need 'channel'";
        if (TooLong(cmd.Channel, 36) || !layout.IsMicrophoneChannel(cmd.Channel))
            return $"setDynamicsProcessing: '{Short(cmd.Channel)}' is not a microphone input";
        if (layout.EqRunsInSoftwareOnly(cmd.Channel)) return Mixer.DynamicsSoftwareOnly(Short(cmd.Channel));
        return cmd.Value.ValueKind == JsonValueKind.String && UnitDynamics.IsValid(cmd.Value.GetString())
            ? null : "setDynamicsProcessing: value must be 'device' or 'software'";
    }

    /// <summary>The longest channel or mix list a <c>setDucking</c> may carry.</summary>
    public const int MaxDuckingIds = 64;

    /// <summary>
    /// <c>setDucking</c>: <c>value</c> is an object with at least one of
    /// <c>enabled</c> (boolean), <c>key</c> (a microphone channel id),
    /// <c>channels</c> (distinct channel ids, possibly none), <c>mixes</c>
    /// (distinct mix ids, possibly none), <c>sensitivity</c> (<c>low</c>,
    /// <c>medium</c>, <c>high</c> or whole dBFS), <c>amount</c> (<c>low</c>,
    /// <c>medium</c>, <c>high</c> or whole dB) and <c>speed</c> (<c>slow</c>,
    /// <c>medium</c>, <c>fast</c> or an object of whole-ms <c>attack</c> and
    /// <c>release</c>), the numbers within <see cref="ProDucking"/>'s bounds. Any other
    /// field is refused, so a misspelt one is not silently ignored. Ids are
    /// checked against the layout when one is given; the key's absence from
    /// <c>channels</c> is checked here when both come in one command, and by
    /// the mixer against the merged setup otherwise.
    /// </summary>
    internal static string? CheckDucking(Command cmd, ILayoutInfo? layout, out DuckingChange? change)
    {
        change = null;
        if (cmd.Value.ValueKind != JsonValueKind.Object) return "setDucking: value must be an object";
        bool? enabled = null;
        string? key = null;
        DuckingLevel? sensitivity = null, amount = null;
        DuckingSpeed? speed = null;
        List<string>? channels = null, mixes = null;
        int fields = 0;
        foreach (JsonProperty field in cmd.Value.EnumerateObject())
        {
            fields++;
            switch (field.Name)
            {
                case "enabled":
                    if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "setDucking: enabled must be a boolean";
                    enabled = field.Value.GetBoolean();
                    break;
                case "key":
                    if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { Length: > 0 and <= 36 } k
                        || layout is not null && !layout.IsSoundCheckChannel(k))
                        return "setDucking: key must be a microphone channel";
                    key = k;
                    break;
                case "channels":
                    if (Ids(field.Value, id => layout?.HasChannel(id) ?? true) is not { } c)
                        return "setDucking: channels must be distinct channel ids other than the key";
                    channels = c;
                    break;
                case "mixes":
                    if (Ids(field.Value, id => layout?.HasMix(id) ?? true) is not { } m) return "setDucking: mixes must be distinct mix ids";
                    mixes = m;
                    break;
                case "sensitivity":
                    if (Level(field.Value) is not { } sv || ProDucking.ThresholdOf(sv) is null) return $"setDucking: {ProDucking.SensitivityProblem}";
                    sensitivity = sv;
                    break;
                case "amount":
                    if (Level(field.Value) is not { } a || ProDucking.DepthOf(a) is null) return $"setDucking: {ProDucking.AmountProblem}";
                    amount = a;
                    break;
                case "speed":
                    DuckingSpeed? sp = field.Value.ValueKind == JsonValueKind.String ? DuckingSpeed.Preset(field.Value.GetString()!)
                        : DuckingJson.Speed(field.Value);
                    if (sp is null || ProDucking.TimesOf(sp) is null) return $"setDucking: {ProDucking.SpeedProblem}";
                    speed = sp;
                    break;
                default:
                    return $"setDucking: unknown field '{Short(field.Name)}'";
            }
        }
        if (fields == 0) return "setDucking: value needs at least one of enabled, key, channels, mixes, sensitivity, amount and speed";
        if (key is not null && channels?.Contains(key) == true) return "setDucking: channels must be distinct channel ids other than the key";
        change = new DuckingChange(enabled, key, channels, mixes, sensitivity, amount, speed);
        return null;

        // A word, or a whole number; its range is checked by the caller.
        static DuckingLevel? Level(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => DuckingLevel.Preset(value.GetString()!),
            JsonValueKind.Number when DuckingJson.Whole(value.GetDouble()) is { } number => DuckingLevel.Number(number),
            _ => null,
        };

        static List<string>? Ids(JsonElement value, Func<string, bool> known)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxDuckingIds) return null;
            var ids = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 36 } id
                    || ids.Contains(id) || !known(id))
                    return null;
                ids.Add(id);
            }
            return ids;
        }
    }

    /// <summary>
    /// <c>setDuckingProcessing</c>: where ducking should run, <c>device</c>
    /// or <c>software</c>, as <c>setEqProcessing</c> takes it. A unit that
    /// cannot run the setup leaves it in software whatever the choice.
    /// </summary>
    internal static string? CheckDuckingProcessing(Command cmd)
        => cmd.Value.ValueKind == JsonValueKind.String && MicEqProcessing.IsValid(cmd.Value.GetString())
            ? null : "setDuckingProcessing: value must be 'device' or 'software'";

    internal static string? CheckEditorRule(Command cmd)
        => cmd.Kind is not ("lv2" or "clap" or "vst3") || string.IsNullOrWhiteSpace(cmd.Plugin)
           || cmd.Plugin.Length > MaxUri || cmd.Plugin.Any(char.IsControl)
           || cmd.Name is { Length: > 200 } || cmd.Name?.Any(char.IsControl) == true
            ? "setNativeEditorRule: need a valid kind, plugin id and optional name"
            : null;

    /// <summary>
    /// <c>setMonitorDry</c>: a monitor mix and a boolean <c>value</c>. It
    /// needs no native helper: each dry route is a combine and a port link.
    /// </summary>
    internal static string? CheckMonitorDry(Command cmd, ILayoutInfo layout)
    {
        if (cmd.Mix is null) return "setMonitorDry: need 'mix'";
        if (TooLong(cmd.Mix, 36) || !layout.IsMonitorMix(cmd.Mix))
            return $"setMonitorDry: '{Short(cmd.Mix)}' is not a monitor mix";
        return cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "setMonitorDry: value must be a boolean";
    }

    internal static string? CheckPluginWineTrace(Command cmd)
        => cmd.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? null : "setPluginWineTrace: value must be a boolean";

    internal static string? CheckPluginSearchPath(Command cmd)
        => PluginSearchPaths.Valid(cmd.Kind, cmd.Path) ? null
            : $"{cmd.Cmd}: kind must be lv2, clap or vst3 and path an absolute folder of at most 4096 characters, without a colon or control character";

    internal static string? CheckPluginPath(Command cmd)
        => string.IsNullOrWhiteSpace(cmd.Path) || cmd.Path.Length > 4096
           || cmd.Path.Any(char.IsControl) || !Path.IsPathFullyQualified(cmd.Path)
            ? $"{cmd.Cmd}: path must be an absolute path of at most 4096 characters"
            : null;

    private static string? Finite(Command cmd, string field)
        => cmd.Value.ValueKind == JsonValueKind.Number && cmd.Value.TryGetDouble(out double d) && double.IsFinite(d)
            ? null : $"{cmd.Cmd}: '{field}' must be a finite number";

    /// <summary>
    /// The soundboard commands' shape: a clip id where one is named, a mode
    /// from the four, a gain from 0 to 1, a name, icon and colour as the
    /// library keeps them, an absolute path to import. Whether the clip
    /// exists is the mixer's to say. Turning the board on and importing need
    /// the helper; everything else answers without it.
    /// </summary>
    internal static string? CheckSoundboard(Command cmd, bool nativeHostInstalled)
    {
        string? NeedClip() => SoundboardLibrary.IsValidId(cmd.Clip) ? null : $"{cmd.Cmd}: need 'clip', a clip id";
        switch (cmd.Cmd)
        {
            case "setSoundboard":
                if (cmd.Enabled is not bool on) return "setSoundboard: need 'enabled', a boolean";
                return on && !nativeHostInstalled ? Mixer.SoundboardNeedsHost : null;
            case "importClip":
                if (cmd.Path is not { Length: > 0 and <= 4096 } path || path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path))
                    return "importClip: need 'path', an absolute path to the file";
                if (cmd.Name is not null && BadName(cmd.Name)) return "importClip: name must contain 1 to 60 printable characters";
                if (BadPage(cmd.Page)) return PageError("importClip");
                return nativeHostInstalled ? null : Mixer.SoundboardNeedsHost;
            case "exportSoundboard":
                if (BadArchivePath(cmd.Path)) return "exportSoundboard: need 'path', an absolute path to the archive";
                return cmd.Path!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? null : "exportSoundboard: path must end in .zip";
            case "importSoundboard":
                if (BadArchivePath(cmd.Path)) return "importSoundboard: need 'path', an absolute path to the archive";
                if (cmd.Merge is null) return "importSoundboard: need 'merge', a boolean: true adds the archive's clips, false replaces the library";
                return nativeHostInstalled ? null : Mixer.SoundboardNeedsHost;
            case "restoreStarterSounds":
                return nativeHostInstalled ? null : Mixer.SoundboardNeedsHost;
            case "renameClipPage":
                if (cmd.Page is null || BadPage(cmd.Page)) return "renameClipPage: need 'page', the page to rename, empty for the default page";
                if (cmd.Name is null || BadPage(cmd.Name)) return PageError("renameClipPage", "name");
                return null;
            case "updateClip":
                if (NeedClip() is { } missing) return missing;
                if (cmd.Name is null && cmd.Gain is null && cmd.Mode is null && cmd.Loop is null && cmd.Icon is null && cmd.Colour is null && cmd.Page is null)
                    return "updateClip: give at least one of name, gain, mode, loop, icon, colour and page";
                if (BadPage(cmd.Page)) return PageError("updateClip");
                if (cmd.Name is not null && BadName(cmd.Name)) return "updateClip: name must contain 1 to 60 printable characters";
                if (cmd.Gain is double gain && (!double.IsFinite(gain) || gain is < 0 or > 1)) return "updateClip: gain must be from 0 to 1";
                if (cmd.Mode is not null && !SoundboardLibrary.Modes.Contains(cmd.Mode)) return "updateClip: mode must be overlap, restart, toggle or hold";
                if (BadIcon(cmd.Icon)) return IconError("updateClip");
                if (cmd.Colour is { Length: > 0 } colour && !LayoutAppearance.IsValid(new LayoutAppearance("", colour)))
                    return "updateClip: colour must be #RRGGBB, or empty to clear it";
                return null;
            case "playClip":
                if (NeedClip() is { } none) return none;
                return cmd.Mode is null || SoundboardLibrary.Modes.Contains(cmd.Mode) ? null
                    : "playClip: mode must be overlap, restart, toggle or hold";
            case "deleteClip":
            case "releaseClip":
            case "stopClip":
            case "auditionClip":
                return NeedClip();
            case "setClipOrder":
                return cmd.Clips is { Count: <= SoundboardLibrary.MaxClips } clips && clips.All(SoundboardLibrary.IsValidId)
                    ? null : $"setClipOrder: need 'clips', at most {SoundboardLibrary.MaxClips} clip ids";
            default:
                return null;
        }
    }

    private static bool TooLong(string? s, int max) => s is not null && s.Length > max;
    private static bool BadArchivePath(string? path)
        => path is not { Length: > 0 and <= 4096 } || path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path);
    /// <summary>An absent page is kept; a present one, trimmed, is empty or a page name.</summary>
    private static bool BadPage(string? page) => page is not null && !SoundboardLibrary.IsValidPage(page.Trim());
    private static string PageError(string command, string field = "page")
        => $"{command}: {field} must be empty, for the default page, or at most {SoundboardLibrary.MaxPageLength} printable characters other than {SoundboardLibrary.DefaultPageWord} alone";
    private static bool BadName(string? name)
        => name is null || name.Length > 60 || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl);
    /// <summary>An absent icon takes the default; a present one has the shape setLayoutAppearance accepts.</summary>
    private static bool BadIcon(string? icon) => icon is not null && !LayoutIcons.IsValidChoice(icon);
    private static string IconError(string command)
        => $"{command}: icon must be empty, \"none\", a listed glyph or an icon name of lower-case letters, digits and hyphens";
    internal static string Short(string s) => s.Length <= 40 ? s : s[..40] + "...";
    private static string Tail(string uri) => uri[(uri.LastIndexOfAny(['#', '/']) + 1)..];

    /// <summary>A real channel, or the "ignore" pseudo-channel that leaves an app to the desktop.</summary>
    private static bool IsChannelOrIgnore(ILayoutInfo layout, string id)
        => id == OpenXLR.Core.Mixing.StreamMatcher.Ignore || layout.HasApplicationChannel(id);
}
