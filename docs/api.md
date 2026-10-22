# WebSocket API

The [versioned local HTTP API](http-api.md) provides request/response access to
the same commands and uses the same per-session credential.

The daemon serves `ws://127.0.0.1:37890/ws` on the loopback interface
only. A handshake that carries a browser `Origin` header from anywhere
but localhost is refused with 403, so a web page you happen to visit
cannot drive the hardware; native clients send no Origin. If the port
is busy at startup (it sits inside the kernel's ephemeral range) the
daemon waits for it instead of touching PipeWire.

Token. The first message on every connection must be
`{"cmd":"auth","token":"<token>"}`, where the token is the content of
`$XDG_RUNTIME_DIR/openxlr/token` (or `~/.config/openxlr/token` in a
session without a runtime directory), a file only your user can read
that the daemon replaces the moment it is listening (never earlier, so
a second instance waiting for the port leaves the running daemon's
clients alone). Nothing is sent before it;
anything else as a first message, or nothing within 5 s, closes the
socket with code 1008 and the reason `unauthorized` or `authentication
timeout`. Another local user cannot read the file, so the loopback
port alone does not hand them the mixer. The UI and the OpenDeck
plugin read the file themselves; a script does the same:

```sh
TOKEN=$(cat "${XDG_RUNTIME_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}}/openxlr/token")
printf '{"cmd":"auth","token":"%s"}\n{"cmd":"getState"}\n' "$TOKEN" | websocat ws://127.0.0.1:37890/ws
```

Limits. Commands are validated before the mixer sees them: unknown
channel or mix ids, plugins that are not installed or need a host
feature the PipeWire chain lacks, undeclared parameter symbols,
non-finite numbers, and over-long strings or lists all come back as an
`error` message instead of being silently ignored. Insert entries must
be objects with a nonempty id, a supported kind and a plugin identifier.
Omitted `params` defaults to an empty object; explicit `null` entries or
parameters are rejected. An insert holds at most 256 control values:
`setInserts` refuses a chain past that, `setInsertParam` refuses a new
symbol on an insert already holding 256, and a saved chain read back at
startup is held to the same cap. A client may send bursts of up to 300 commands
and a sustained 100 per second; beyond that it is disconnected with
close code 1008. At most 32 clients can be connected at once. The
`plugins` message is bounded too: a plugin with a URI over 512
characters, more than 4096 ports, or one that would push the message
past 7 MiB is left out of `plugins` altogether, and at most 512
controls, 256 scale points and 64 required features are read per plugin;
a plugin that is listed with `supported: false` is a different case, one
the chain host cannot run. The UI and terminal accept only text messages
up to 8 MiB, including all fragments. They reconnect when a binary or
oversized message arrives. The terminal ignores malformed message envelopes
without stopping its receive loop. The native host's own protocol is bounded the
same way: a line from the helper is at most 4096 characters and a control
or meter symbol at most 255, and a plugin can hold at most 4096 pending
control changes or live meters at once; a meter whose symbol the catalogue
does not declare for that plugin is dropped rather than kept, so a library
that writes to the helper's output cannot fill those slots with names of
its own. The limit is on the message, not on what the
daemon knows: every plugin a saved insert names is listed whatever the
size, and `setInserts`, chain building and insert status all resolve a
plugin against everything installed. A plugin missing from `plugins` can
still be named by an insert that already holds it; it cannot be picked
from the list, which is the only place its absence shows.

Each connection has a bounded outgoing queue. A client that falls behind
may miss transient `meters` frames. If a state update, rule notification or
command reply cannot be queued, the daemon disconnects that client so it
cannot silently retain stale state. Reconnect and authenticate again to
receive a fresh snapshot; reload any cached plugin catalogue and editor rules.
The UI, terminal mixer and OpenDeck plugin reconnect automatically.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `warning` (one sentence the user should see, or null: the mixer waiting for the audio server, which did not answer when the daemon built the graph (see `mixerWaiting`), mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, a device set aside after three hung USB transfers in one run, a device that does not answer USB reads (named, with the error, until a read succeeds or the unit leaves the bus), hardware input strips left silent because the capture node's name matches two units or because several interfaces are attached and none is driven, a hardware strip bound to a source that is missing, named twice, lacks the selected port or is not linked yet, one sentence per strip with the strip's name in front, or additional interface choices that could not be read or saved). In the mixer state, each channel carries `hardware` (true for the fixed input channels), `captureSource` (exact external source name or null), `capturePair` (zero-based pair), `captureMonoChannel` (the one zero-based source port fed to both sides, or null for a pair), `captureConnected` (its direct or chained capture route exists), `microphone` (true for `xlr1`, `xlr2` and a capture input with `captureMonoChannel` set), `chainWidth` (1 for those microphone inputs, 2 for a capture pair, null for Aux In and application channels; the binding decides this while unplugged too), `binding`, `bindingConnected`, `bindingWarning` and `unitInput` (see [input bindings](#input-bindings)), `present` (whether a client should draw the strip, see [input bindings](#input-bindings); `xlr2` and `aux` are absent on every model but the Wave XLR Pro), `exclusiveGroup` (the id of the exclusive group the channel is in, or null), `volume` (the channel's own volume, 0 to 1, which `setChannelVolume` sets; every channel has one, `xlr1`, `xlr2` and `aux` included; an older daemon reports null for a hardware input), and on a microphone channel, `xlr1`, `xlr2` and a capture input whose `microphone` is true, `eq` (see [microphone EQ](#microphone-eq); absent on every other channel, a capture input on a stereo pair included, and from an older daemon). An absent channel stays in the list and keeps its levels, so a client leaves it out of its strips rather than forgetting it. A capture channel is editable but cannot receive application assignments; `renamedSinceStart` says a user mix was renamed since the daemon started (its PipeWire device keeps the old name until a restart), and `layoutWarning` is a sentence for the layout editor when pipewire-pulse nears its open-file limit, or null. Every channel and mix also carries `appearance {icon, colour, hidden, defaultIcon}`: the first three as `setLayoutAppearance` set them (`icon` empty, `colour` null and `hidden` false by default), and `defaultIcon`, read-only, the built-in icon name the daemon chose for the item's kind, never empty (see [mixer-layout.md](mixer-layout.md#appearance) for the rule). A client draws `icon` when it is a text glyph or a name it can draw, nothing when it is `none`, and `defaultIcon` otherwise, which covers an empty `icon` and a name only a skin defines. The built-in names, their text characters and their path data are in `plugin/com.emaspa.openxlr.sdPlugin/icons.json`, generated from `src/OpenXLR.Core/Mixing/LayoutIcons.cs`. A client draws a hidden channel's sends and applications as usual and only leaves out its strip; for a hidden mix it leaves out the mix's master and the sends into it from its full mixer, and keeps the mix in output and monitor feed choices. The mixer state's `outputAppearance` maps the headphone outputs `phones1` and `phones2` to `{icon, colour, hidden, defaultIcon}` in the same form, `hidden` always false and `defaultIcon` `headset`; a model with one headphone output still carries both entries. Each mix carries `id`, `name`, `volume`, `muted`, `editable` (true for every monitor mix and virtual microphone, including Monitor A/B; false for Aux), `deletable` (editable and not the last remaining monitor) and `kind` (`monitor`, `virtualMic` or `auxPort`; a monitor mix the user added is `monitor` and editable), which gives a client the mix's volume ceiling. `defaultMonitorMix` names the implicit feed for outputs without an entry in `monitorFeeds`, independent of display order; `outputVolume` is the first selected output's volume, 0 to 1.5, or null with no output selected. In `devices`, every entry that is a sink carries `volume` (desktop scale, 1.0 = 100%) and `muted`; sources and the Wave XLR Pro pseudo-outputs omit both. A source carries `channels` and `positions` when PipeWire reports them, and any entry can carry `drivenUnit` (see below). A state is pushed whenever a sink's volume or mute changes |
| `diagnostics` | in answer to `getDiagnostics` | `blocks`, mapping vendor block names to hex strings or read errors. An XLR Dock adds `paths`, the card number and the path each of its gain, mute and headphone controls takes (`Alsa` through the card's mixer, `Block` through the dock's config block when the card lacks the control, `None` when the USB handle is closed too, so the control is unavailable), and `alsa`, the values read through the card. `usbFault` carries the last USB fault of this run and `error` a dump that failed |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel (`ch:<id>`) and mix (`mix:<id>`), and `duck:<id>` for each mix with a running software [ducking](#ducking) stage: one value, the reduction in dB now (0 while the key is quiet) |
| `plugins` | in answer to `listPlugins` | the installed LV2, CLAP and VST3 plugins with their controls, within the message size limit above and always including the plugins the saved chains use; `supported` is false, with `unsupportedFeatures` listed, for a plugin that needs a host feature the PipeWire chain lacks. `audioIns` and `audioOuts` are the plugin's own port counts, or for VST3 its main buses' default width; a VST3 entry also carries `widths`, the chain widths in channels (1 and 2 are the ones the host carries) its main buses accepted when the helper asked the way the host asks at load, so a plugin that reports 2 and lists 1 in `widths` can be inserted on a mono input. An entry without `widths` (LV2, CLAP, or a description an older helper wrote) fits a mono input with one port each way and a stereo input or mix with two or more |
| `pluginSetup` | in answer to `getPluginSetup` or `setPluginWineTrace` | where installs go (`lv2Directory`, `clapDirectory`, `vst3Directory`), `hostInstalled`, `wineTrace` (deep tracing enabled in the running daemon), `yabridge` (its version, or null when not installed), `wine`, `windowsDirectories` (the folders yabridge bridges) and `wineFolders` (Wine's own plugin folders that hold a plugin and are not bridged yet, offered as one press since a file dialog hides them), `memoryLockLimitBytes` (the running daemon's soft limit in bytes; -1 means unlimited, null means unknown) and `memoryLockNote` (recovery advice when Windows plugin support is available and the limit is below 256 MiB, otherwise null), `searchDirectories` (every folder searched, see [Plugin search paths](#plugin-search-paths)) and `searchPathWarning` |
| `windowsPluginFiles` | in answer to `getWindowsPluginFiles` | `ok`, `message` and `plugins`; each plugin file carries `path`, `name`, `format`, `enabled`, `canDelete`, `winePrefix` and `inUse`. Excluded files remain listed |
| `nativeEditorRules` | in answer to `getNativeEditorRules` or `setNativeEditorRule` | `rules` with `kind`, `plugin`, `name`, `reason`, `defaultBlocked`, `override` and effective `blocked`; `error` describes a refused change or unreadable configuration |
| `nativeEditorRulesChanged` | after a successful rule change | notification to refresh the catalogue and editor availability; no plugin rescan is needed |
| `pluginDiagnostics` | in answer to `getPluginDiagnostics` | `discovery`: daemon host/controller paths, Wine prefix, architecture, effective search paths, bounded `yabridgectl status` output and latest completed CLAP/VST3 scan reports. A scan entry that failed carries `logId`, the name of the file holding that attempt's bounded scanner output, or `logNote` saying why there is none; both are absent from an entry that did not fail and from one an older daemon recorded. `scanLogs` gives the `directory` those files are in and the bounds they are kept under (`stderrCapBytes`, `traceStderrCapBytes`, `traceCaptureBytes`, `stdoutCapBytes`, `maxFiles`, `maxTotalBytes`). Reading the reply or the directory scans nothing and starts no process |
| `pluginInstall` | in answer to `installPlugin`, `addPluginSearchPath`, `removePluginSearchPath`, `addWindowsPluginFolder`, `removeWindowsPluginFolder`, `removeWindowsPluginInserts`, `setWindowsPluginEnabled`, `deleteWindowsPlugin`, `syncWindowsPlugins` and `rescanPlugins` | `ok`, `message` (a sentence or two for the user, ending with the bundles the scan that followed could not read, up to three by name and the rest as a count), `installed` (the bundles or folders put in place), `added` (plugins in the catalogue that were not before) and `total` |
| `error` | when a command without a `requestId` is rejected | `message`; for the mixer commands a `state` follows, so an optimistic edit can be reverted |
| `commandResult` | in answer to a command that carried a `requestId` | `requestId`, `error` (null on success); preceded by the state the result refers to |

`daemonBuildId` is the running assembly's module identity (a GUID). Doctor
compares it with the installed assembly to detect a daemon left running
across an upgrade, including two builds with the same version. Older
daemons omit it; clients that do not use it can ignore it.

The daemon builds the device half of a state message from its last good
read of the unit. Its poll loop reads the unit every 100 ms and a command
reads it once after writing. The state message itself never reads USB, so it
does not wait for a transfer in progress. When the daemon reopens a unit after
failed reads and the unit has not answered yet, `state` holds the last values
read from it and `warning` says the unit does not answer.

The state message in full. At the top level: `daemonVersion`, `daemonBuildId`, `warning`,
`connected`, `device` (`vendor`, `model`, `usbId`; when the unit is driven differently from the usual, `note`: today an XLR Dock taking a control through its config block, or a Wave:3 or Wave:1 whose ALSA card was not found, so the desktop's volume controls are not kept in step; and `firmware`, the version the unit reports, on a model that reports one, today the Wave:3, the Wave:1, the MK.2 family and the Wave XLR Pro), `capabilities` (`gain`,
`mute`, `lowCut`, `expander`, `voiceTune`, `hpVolume`, `lowImpedance`,
`crossfade`, `phantom`, `clipGuard`, `compressor`, `outputRouting`,
`auxInput`, `xlrInputs`, `xlrJacks` (false on the Wave:3 and Wave:1, whose one input is the microphone's own capsule rather than an XLR jack), `hpOutputs`, `physicalControls`, `retainsSettings`, `hardwareEq` (the unit's DSP runs the [microphone EQ](#microphone-eq) and the driver can write it: true on the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15 and on the Wave XLR Pro from firmware 1.0.14, false on every other model and on older firmware), `ducking` (the unit can run [ducking](#ducking) on its own processor: true on the Wave XLR Pro only),
`builtInDefaults`, and `gainMaxDb`, the highest gain in dB that `gain` and
`gain2` take, from 0: 80 on the Wave XLR Pro and the MK.2 family, 75 on the
Wave XLR and the first XLR Dock, 40 on the Wave:3 and Wave:1; a higher value is held
to it), `state` (the hardware settings: `gainDb`, `mute`,
`lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `hpVolumeDb`,
`hp2VolumeDb`, `hpMute` (read-only: the headphone mute the Wave:3's and Wave:1's
firmware keeps and asserts at the headphone floor, released by a level
written above it; false on every other model), `lowImpedance`,
`crossfade`, `phantom`, `clipGuard`,
`compressor`, their `2` variants for XLR 2, `outHp1`, `outHp2`, `outUsbAux`,
`outLineOut`, `hpMixMonitorReturn`, `hpMixMicDirect`, `auxReturnEnabled`,
`auxLevelDb`, `auxLevelLock`, plus the daemon-stamped `gainLocked`,
`phantomSettling`, `phantomSettling2`, `phantomSettleSeconds` and
`phantomSettleSeconds2`), `ducking` (present whenever the daemon runs, see [ducking](#ducking)),
`mixer`, `mixerWaiting`, `devices`, `profiles`, `activeProfile`, `recallOnConnect` and
`detected` (`usbId`, `name`, `active` for every attached interface; `usbId`
is the unit's instance id, see [additional interface state](#additional-interface-state))
and `waveInterfaces`. The
mixer state carries `mixes`, `channels`, `monitorOutput` (the first selected
output), `monitorOutputs`, `monitorFeeds`, `outputVolume`,
`monitorMuted` (the selected outputs are paused, independently of mix and
channel mutes; saved in `mixer.json`, never recalled by a profile),
`softLowCutApplicable` and `softClipGuardApplicable` (true when XLR 1's
signal has no hardware low cut or ClipGuard in front of it: XLR 1 is bound
to a source, or the driven unit lacks the control, so `lowCutHz` and
`softClipGuard` act on it), `auxPortEnabled`, `lowCutHz`, `softClipGuard`, `softClipGuardAvailable`,
`softClipGuardError`, `compensateMixLatency`, `mixDelayMilliseconds`,
`mixLatencyError` (see [plugin latency](#plugin-latency)), `eqLimits` (see
[microphone EQ](#microphone-eq)), `inserts`
(chains by insert key, `xlr1`, `xlr2`, a capture channel id or `mix:<id>`; each entry carries
`insert`, `error`, `meters`, `nativeHostRunning`, `nativeUiBlocked`,
`nativeUiBlockReason`, `filterChainError` and `latencyMilliseconds`),
`soundCheck` (see [Sound Check](#sound-check)),
`exclusiveGroups` (`id`, `name`, `channels`; see
[exclusive groups](mixer-layout.md#exclusive-groups)), `enforcedDefaultSink`, `enforcedDefaultSource`, `streams` (`id`, `serial`,
`label`, `identity`, `channelId`, `active`, `running`, and `icon`; see
[application names](#application-names) and
[application icons](#application-icons)), `renamedSinceStart`
and `layoutWarning`. A `devices` entry carries `name`, `description` (a
hardware source's description leaves out the ALSA profile PipeWire appends,
such as "Analog Stereo", unless that would give two sources one name), `kind`
(0 for a sink, 1 for a source), `isOwn`, `isPhysical`, for a sink
`volume` and `muted`, and for a source `channels` (its channel count from
PipeWire's `audio.channels`) and `positions` (its channel positions in port
order, such as `["MONO"]`, `["FL","FR"]` or `["AUX0", ...]`); either is
absent when PipeWire does not report it, and `positions` is absent when its
count disagrees with `channels`. Any entry can carry `drivenUnit`: the
instance id of the attached supported interface whose ALSA card publishes
the node, or the model id (`vvvv:pppp`) when two attached units of one model
cannot be told apart by their node names. It names every attached supported
unit, driven or not. Over the
WebSocket a field whose value is null is left
out of the message; the HTTP state reply writes it as `null`.

Meter readings stay finite when an audio source emits NaN or infinity: an
invalid sample contributes silence to its channel's RMS calculation, without
changing the other channel or later valid frames. This protects metering and
its JSON messages; it does not modify the audio signal sent to outputs.

Commands are single JSON objects with a `cmd` field. The layout commands
(`createChannel` through `setLayoutAppearance` below) succeed only after the
new layout is written to `mixer.json`; a failed write restores the previous
layout and answers with an error. Renaming to the current name also saves
pending mixer settings, without changing audio nodes. Any command may carry a `requestId` of up to 64 characters; the
daemon then answers with a `commandResult {requestId, error}` message after
the state that reflects the outcome (`error` is null on success) instead of
a bare `error` message, so an editor can wait for the acknowledgement.
Failed plugin operations retain their typed `pluginInstall` or
`windowsPluginFiles` reply with `ok:false` and also report the failure in
that final acknowledgement (or an `error` without a request id):

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock`. A control the active device's capabilities do not advertise is refused with an error, as is an XLR Dock control the card lacks while the USB handle is closed |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setMixLatencyCompensation` | `value` | boolean; delay the other mixes to line up with the mix whose inserts take longest (see [plugin latency](#plugin-latency)). Off by default. Either way every insert chain is rebuilt, which briefly interrupts audio |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelVolume` | `channel`, `value` | a channel's own volume, 0 to 1 (values outside are clamped), which scales every send of the channel at once; see [channel volume](#channel-volume). Any channel takes one, `xlr1`, `xlr2` and `aux` included, where it comes after the interface's gain and leaves the gain alone; an unknown channel is refused |
| `createCaptureChannel` | `name`, `source`, optional `capturePair`, `captureMonoChannel` | add an external PipeWire capture input, muted in every mix; exact source name of up to 256 characters, zero-based stereo pair 0 to 31 (default 0). `captureMonoChannel`, 0 to 63 and only with pair 0, takes that one port of the source and feeds it to both sides. A source named `OpenXLR...` or ending in `.monitor` is refused, and so is a source that is not present. Capture inputs count against the 32 application channels. See [capture inputs](mixer-layout.md#capture-inputs) |
| `setCaptureSource` | `channel`, `source`, optional `capturePair`, `captureMonoChannel` | change an existing capture input's source in place. The source and port rules are those of `createCaptureChannel`, but an offline source is accepted. The channel keeps its id, name, send levels, volume, inserts, group and appearance. Its monitor sends are muted and its Sound Check session ends; other sends keep their mutes. A mono/stereo width change is refused while any insert remains, bypassed inserts included. Saves the binding and mutes before acknowledging; a failed save restores them and leaves Sound Check running. The selected source's supported unit is learned, or the old `captureUnit` is cleared when selecting another source; see [capture inputs](mixer-layout.md#capture-inputs). No new state fields. Older daemons reject this command |
| `createChannel` | `name`, optional `icon` | add an application channel, muted in every mix, with `icon` saved in the same write (the values `setLayoutAppearance` takes; absent or empty is the default), without touching existing nodes; its generated stable id is in the next state. Undone with an error when its sends have not appeared within 3 s |
| `renameChannel` | `channel`, `name` | rename an application or capture channel; an application channel's playback device is reloaded under the new name and the streams on it are put back (a short gap on that channel only), a capture channel's label changes without touching its route |
| `deleteChannel` | `channel` | remove an application or capture channel; apps and remembered assignments on it move to the fallback application channel, which is `system`, or when `system` is gone the application channel whose id sorts first. The last application channel cannot be removed |
| `createMix` | `name`, `kind?`, `icon?` | add a user mix, with `icon` as `createChannel` takes it: a virtual microphone (`kind` absent or `"virtualMic"`) or a monitor mix (`"monitor"`), which has no capture device and a desktop-controlled master up to 1.5 like Monitor A and B. Every channel gets a muted send into it, before a virtual microphone's capture device is published. A monitor created during a monitoring pause also starts with its master muted. Both kinds share a limit of 18 editable mixes, Monitor A and B included. Undone with an error when a channel's send has not appeared within 3 s |
| `renameMix` | `mix`, `name` | rename any monitor mix or virtual microphone in OpenXLR; its PipeWire devices keep their old description until the daemon restarts (reloading them would throw the apps using them off), and the mixer state's `renamedSinceStart` says so |
| `deleteMix` | `mix` | remove a monitor mix or virtual microphone with its sends and inserts, and a virtual microphone's capture device; the last monitor cannot be removed. During a monitoring pause only the removed id leaves the listening selection; deleting its last previously open monitor clears `monitorMuted` without opening other muted mixes. Outputs fall back to `defaultMonitorMix` if their feed becomes empty; an enforced default sink or source that was the mix is cleared. The Pro's jack selectors and microphone path follow the remaining feed before acknowledgement |
| `setExclusiveGroup` | optional `group`, `name`, `channels[]` | create an exclusive group when `group` is absent (its generated id is in the next state), otherwise replace that group's name and members. 2 to 36 distinct existing channels, none of them in another group. If more than one member is unmuted, every member is muted. See [exclusive groups](mixer-layout.md#exclusive-groups) |
| `deleteExclusiveGroup` | `group` | remove an exclusive group; every send keeps its mute |
| `setLayoutOrder` | `channels[]`, `mixes[]` | complete ordered lists of editable-channel ids and every monitor or virtual microphone mix id; hardware inputs and Aux stay fixed. This one order is what every client draws; it does not decide where applications go |
| `setLayoutAppearance` | exactly one of `channel`, `mix`, `output`; `appearance {icon, colour, hidden}` | replace one channel's, mix's or headphone output's appearance; `output` is `phones1` or `phones2`. `icon` is empty (the default for the item's kind), `none` (no icon), one of the text glyphs ● ♪ ♫ ✦ ◆ ▶ ◉, or an icon name of a lower-case letter and up to 31 lower-case letters, digits or hyphens; the daemon checks a name's shape, not that any client can draw it. `colour` is null or `#RRGGBB`, `hidden` leaves a channel's strip or a mix's master and column out of the full mixer and is refused for an output; an omitted field takes its default. No node, link, level or order changes |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute. Unmuting a send of a channel in an exclusive group first mutes every send of the group's other members, in every mix |
| `cycleExclusiveGroup` | `group` | switch an exclusive group to its next member in member order. The next member is unmuted in every mix the current member was heard in, and the current member is muted everywhere. An error when no member is unmuted |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters; monitor volume range 0 to 1.5, other mixes 0 to 1; values outside the range are clamped |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mixes feed, at most 16; a newly listed output is fed by `defaultMonitorMix`. A missing sink stays selected and saved, and is linked when it appears. A client that lists outputs for the user keeps the missing ones in the list it sends |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setMonitorFeed` | `device`, `mix` | what feeds one selected output, independently of other outputs: any existing mix id, including `stream`, `chat`, `auxout` and user mixes of either kind, or distinct ids joined with `+` to sum them at unity. One mix can appear in several outputs' feeds. An empty feed is rejected; remove an output from `setMonitorOutputs` to disconnect it. The Pro's own jacks follow one feed together. The state's `monitorFeeds` lists exceptions from `defaultMonitorMix` (`monitor` when present, otherwise the lowest monitor id), independent of layout order. Unknown or repeated mix ids and unselected outputs are rejected. Deleting the last included mix returns that output to `defaultMonitorMix` |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices, 0 to 1.5; the range the devices themselves take, so a desktop level above unity can be held and written back unchanged. Values outside it are clamped, and the state reports what reached the devices. With no output selected the command succeeds and changes nothing |
| `setMonitorMuted` | `value` | boolean only; pause or resume OpenXLR's feeds to all selected monitor outputs, including the Pro's direct microphone monitoring. Selected physical sinks mirror the pause, preserving their previous native mutes for resume. Mix and channel mutes, levels, feeds and recording outputs stay unchanged. The state reports `monitorMuted`; saved in `mixer.json`, not in profiles, and profile recall keeps the current pause |
| `toggleMonitorMuted` | none | atomically toggle the same monitor pause. Use this for keys and repeated presses instead of reading `monitorMuted` and sending its inverse; each accepted command toggles once |
| `listPlugins` | none | the installed LV2, CLAP and VST3 plugins, answered with a `plugins` message |
| `getPluginDiagnostics` | none | read bridge status and existing native scan evidence without syncing, rescanning or changing inserts; answered with `pluginDiagnostics` |
| `getPluginSetup` | none | where plugins are installed and what bridges Windows ones, answered with a `pluginSetup` message |
| `setPluginWineTrace` | `value` | boolean only; set or clear deep Wine tracing for future plugin scans in the running daemon, answered with `pluginSetup` carrying the current `wineTrace`. No restart, persistence, rescan or cache deletion |
| `installPlugin` | `path` | install what is at an absolute path the user picked: a `.clap` or single-file `.vst3` is copied into `~/.clap` or `~/.vst3`, a `.vst3` or `.lv2` directory into `~/.vst3` or `~/.lv2`, a plain directory installs every plugin inside it. A single Windows VST3/CLAP file or VST3 bundle is copied into its own folder under `windowsImportDirectory`, and only that folder is registered and synced; a plugin already installed inside a Wine prefix is linked from the managed folder instead, preserving its original location and prefix. An explicit Windows plugin folder is registered in place. Archives, installers and VST2 files are refused with a message that says what to do. The catalogues are read again before the `pluginInstall` answer |
| `addWindowsPluginFolder` | `path` | register an existing folder holding Windows VST3 or CLAP plugins and sync it, without copying source files or installing any native plugins alongside them; answered with `pluginInstall` |
| `removeWindowsPluginFolder` | `path` | unregister a folder from `windowsDirectories`, sync retained folders and remove only its unused generated VST3/CLAP wrappers. Original files are kept. Refused while affected plugins remain in the mixer's insert chains. A missing source folder can still be removed from the list; answered with `pluginInstall` |
| `getWindowsPluginFiles` | `path` | list Windows VST3/CLAP files and bundles reachable from one registered folder, including excluded files; answered with `windowsPluginFiles`. Reads metadata without syncing or deleting |
| `removeWindowsPluginInserts` | `path` | remove every current insert supplied by the selected registered Windows plugin file, including bypassed occurrences across inputs and mixes. Other inserts, files, exclusions and saved profiles are kept. Rewire affected paths and save current settings before answering with `pluginInstall`; requires a running mixer |
| `setWindowsPluginEnabled` | `path`, `value` | enable with `true`, or exclude with `false`, one registered Windows plugin source using yabridge's blacklist. Excluding keeps original files and removes only that source's wrappers, and is refused while the plugin is used by inserts; answered with `pluginInstall` after a catalogue refresh |
| `deleteWindowsPlugin` | `path` | permanently delete one standalone plugin file or bundle and its wrappers, then refresh the catalogue. Refused for unregistered, Wine-installed, symbolic-link or in-use sources; answered with `pluginInstall`. Clients must confirm deletion with the user first |
| `syncWindowsPlugins` | none | run yabridge's sync over the folders it knows, clean missing-source wrappers belonging to those folders unless inserts still use them, then read the catalogues again; answered with `pluginInstall` |
| `rescanPlugins` | none | read the plugin directories again, for plugins installed by other means; answered with `pluginInstall` |
| `addPluginSearchPath` | `kind`, `path` | add an existing folder to the `lv2`, `clap` or `vst3` search path without copying anything, save the list in `daemon.json` and read the catalogues again; answered with `pluginInstall`. See [Plugin search paths](#plugin-search-paths) |
| `removePluginSearchPath` | `kind`, `path` | stop searching an added folder, keeping its files, and read the catalogues again; works for a folder that no longer exists; answered with `pluginInstall` |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2`, a capture channel id or `mix:<id>`, each insert is `{id, kind, plugin, label?, bypass?, params?}` where `kind` is `"lv2"` with the plugin URI, `"clap"` with the plugin's id, or `"vst3"` with the class id as 32 hex digits; a CLAP or VST3 insert always runs in the native host, so its `nativeHost` reads true whatever was sent. An insert being added is refused when its plugin cannot run at the chain's width (`chainWidth` on an input, two on a mix, by `widths` or the port counts as `plugins` describes them); an insert already in the chain, the same plugin under the same id, is left to the chain builder, so one can always be removed; an id kept while its `kind` or `plugin` changes counts as an addition. Only this chain is rebuilt; every other chain keeps its running plugins |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert; rebuilds this chain only |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by the catalogue's `symbol` (LV2 port symbol or decimal CLAP/VST3 parameter id); use catalogue ranges and scale points. Refused when the insert is not in the chain or the catalogue does not declare the symbol for its plugin |
| `renameInsert` | `channel`, `insertId`, `name` | change one insert's label; `name` is 1 to 256 characters with no control characters. The running plugin, its parameters and the audio path stay as they are. Refused when the insert is not in the chain |
| `getNativeEditorRules` | none | read release defaults and explicit user overrides for native editor compatibility |
| `setNativeEditorRule` | `kind`, `plugin`, `name?`, `blocked?` | set `blocked:true` to use OpenXLR controls, `false` to allow the native editor, or null/absent to remove the override and follow release defaults. Saved atomically before success; answered with `nativeEditorRules` |
| `showInsertUi` | `channel`, `insertId` | open an enabled insert's native editor when the optional host is installed and the editor policy allows it; a blocked editor is refused without changing the audio instance |
| `soundCheck` | `channel`, `action` | Sound Check on any channel whose `microphone` is true: `xlr1`, `xlr2` or a mono capture input. Stereo capture inputs, Aux In, application channels and mixes are refused. `record` starts a session if none runs and records up to ten seconds, replacing an earlier sample; `loop` plays the sample into the channel in place of the microphone and needs at least a tenth of a second recorded; `live` goes back to the microphone and keeps the sample; `stop` ends the session and discards the sample. One session at a time across all microphones: `record`, `loop` and `live` for another channel are refused with the active channel's name, and its `stop` does nothing. Every action but `stop` is refused at once when the native plugin host is not installed. Never saved. See [Sound Check](#sound-check) |
| `setEq` | `channel`, `eq` | replace a microphone's whole EQ: `eq` is `{enabled, bands}` with exactly four bands, each `{enabled, type, frequency, gain, q}` with every field. `channel` is `xlr1`, `xlr2` or a capture input whose `microphone` is true; any other channel is refused. See [microphone EQ](#microphone-eq) |
| `setEqEnabled` | `channel`, `value` | the EQ's own switch, a boolean. Off bypasses inside the running stage, so on and off compare at once |
| `setEqBand` | `channel`, `band`, at least one of `enabled`, `type`, `frequency`, `gain`, `q` | change part of one band, 1 to 4; an absent field keeps its value. Values outside the ranges are refused, not clamped, and what is taken is snapped onto the EQ's steps. Switching the EQ on, here or through `setEq`, is refused at once when the native plugin host is not installed and no unit runs the channel's EQ |
| `setEqProcessing` | `channel`, `value` | where a microphone's EQ runs when the unit it carries can run it: `device` (the default) on the unit's own DSP, or `software` on this computer. Any other value is refused. Saved in `mixer.json` under `eqProcessing`, never in a profile. Moving between the two switches the leaving one off first. Refused on a capture input, whose EQ always runs on this computer. See [where the EQ runs](#where-the-eq-runs) |
| `adjustOutputVolume` | optional `device`, `value` | change a PipeWire output by desktop percentage points (`0.05` is 5%). Finite steps from -0.5 to 0.5, final volume clamped to 0 through 1.5. Omit `device` for the current desktop default |
| `setOutputDeviceVolume` | optional `device`, `value` | set a PipeWire output's desktop volume; `value` finite, 0 to 1.5 on the desktop scale (1.0 is 100%), rejected outside that range. A monitor mix sink is set through its mix master; a selected monitor output follows the linked monitor-volume behaviour. Omit `device` for the current desktop default; device names are accepted and rejected as for `adjustOutputVolume` |
| `toggleOutputMute` | optional `device` | toggle an output's mute; omit `device` for the current desktop default. On a selected physical monitor output it toggles `monitorMuted` for the whole monitor selection, as pressing a Deck monitor dial does; on a monitor mix sink it toggles only that mix; on any other output it toggles the sink's mute at the audio server |
| `setMainOutput` | `device` | select and enforce an available PipeWire output as the system default, or `@monitor` for the first selected monitor output. Retains capture-default policy and mixer feeds |
| `routeFocusedApp` | `channel` | route the focused KDE application to an application channel and remember the assignment; requires the running UI with Desktop keys enabled and `gdbus`. Missing or ambiguous process identity is an error, with no guessed routing. One request runs at a time; another sent while it runs is refused |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen); `channel: "ignore"` stops managing it, its streams go back to the system default output and stay wherever the desktop routes them |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app; `ignore` works here too |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setInputBinding` | `channel`, `source?`, `captureMonoChannel?` | bind a hardware strip to one port of a PipeWire capture source by exact node name. `channel` is `xlr1` or `xlr2`; Aux In stays on the interface. With `source` null or absent the strip returns to the interface's capture pair and takes no port. With a source, `capturePair` must be 0 and `captureMonoChannel` is the zero-based port, 0 to 63, default 0. OpenXLR nodes, monitor taps, control characters and names over 256 characters are refused. A node of an attached supported interface (a `devices` entry with `drivenUnit`) is accepted, and the binding records that unit, see [a strip on another interface](#a-strip-on-another-interface). The source may be offline. Saved before the acknowledgement, rolled back on a failed save. The strip's monitor sends start muted after a change; no other strip changes. Channel ids, software processing and inserts stay as they are. See [input bindings](#input-bindings) |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold; `sink: "@monitor"` follows the first selected monitor output |
| `setActiveDevice` | `device` | switch to another attached interface: one unit by its instance id from `detected`, or the first unit of a model by `vvvv:pppp`. The one command that moves the primary; it wins over `OPENXLR_DEVICE`, and the choice stays until the next `setActiveDevice` or a restart |
| `setWaveInterfaceEnabled` | `device`, `value`, optional `keepChannels` | drive an attached unit, by its instance id from `waveInterfaces`, as an additional interface (`true`) or stop driving it (`false`); at most four. Refused for a unit that is not attached, and for a Wave XLR Pro whose capture node name another attached unit shares. Enabling creates no mixer channel; disabling deletes the unit's capture channels and their chains as `deleteChannel` does, unless `keepChannels` is true. Absent or false keeps deletion; any other type is refused. Channels are found by their saved `captureUnit`, even while unplugged, or by `captureHint` and `captureModelHint` for older entries without a unit. Shared model fragments alone never select another unit's channels. Kept channels retain their binding, sends and chains, and stay silent while the source is missing. Neither value changes the primary, and a unit enabled here is never driven as the primary in place of an absent one; only `setActiveDevice` makes it the primary. See [additional interface state](#additional-interface-state) |
| `setWaveControl` | `device`, `control`, `value` | the `set` command for one unit by instance id: an additional interface, or the primary when `device` is the primary's id. Same control names, capabilities, gain lock and phantom settling as `set`; an error for a unit that is not driven |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the connected interface, or to `generic` while none is connected (see below); loading restores saved gain even while locked and leaves the lock enabled |
| `setRecallOnConnect` | `name` | the profile recalled whenever the active device connects fresh (daemon start, replug, switch to it); empty clears it. With none chosen, a device whose capabilities say `retainsSettings: false` gets the last settings the daemon saw on it instead. Refused while no interface is connected |
| `setDucking` | `value` | change the [ducking](#ducking) setup: an object with any of `enabled`, `key`, `channels`, `mixes`, `sensitivity`, `amount`, `speed`; the fields it leaves out keep their values and an unknown one is refused. Saved in `mixer.json` before it applies. Needs the software mixer, on any device or none |
| `setDuckingProcessing` | `value` | where [ducking](#ducking) should run: `device` (the default) on the unit when the setup fits it, or `software` on this computer. The Wave XLR Pro does not run it yet, so `device` runs in software there with the reason in the state. Saved in `mixer.json`, never in a profile |
| `resetDevice` | none | write the recorded defaults back to a device using connect-time restoration and forget its last settings (an error until the daemon has seen the device connect after a power cycle once); on the Wave XLR Pro, which keeps its own settings, write OpenXLR's baseline instead: gain 30 dB on both inputs, every processing stage and phantom off, headphones and aux level at half, the crossfade fully on PC, routing untouched, refused while the gain lock is on. The capabilities say `builtInDefaults` when a model has a baseline |
| `getDiagnostics` | none | vendor block dump for bug reports |

Profiles live in one directory per connected interface model. While no
interface is connected they live in `generic`, whatever the strips are bound
to, and `profiles` lists that directory; the list switches with the
connection as it switches between two units. A `generic` profile holds the
mixer scene only, so `saveProfile` there needs a running mixer and is
refused otherwise, leaving any existing profile untouched. `loadProfile`
there applies the scene. `setRecallOnConnect` is refused, since nothing
arrives to recall it on; the mixer starts with its saved settings instead.
A profile never changes an input binding.

A recall replaces the insert chains the profile's `mixer.inserts` names. An
input key it does not name, `xlr1` or `xlr2`, keeps its chain; a mix key it
does not name is cleared, as it always was. A profile with no `inserts` at
all leaves every chain alone. The daemon rebuilds only a chain whose plugins,
kinds, order, bypass or host choice differ from the running one. A chain that
matches takes the recalled control values live, keeps its native editor
open and stays in the graph under the same nodes. `saveProfile` writes an
entry for XLR 1 and XLR 2 even when their chains are empty, so a new
profile describes both. An older daemon wrote an input's key only once
that chain had been edited. At startup the saved settings rebuild only the
chains that differ too, and leave every chain they do not name, input or
mix, as it is.

Profile files have an 8 MiB save and load limit. The recall marker is at
most 1 KiB and contains a profile name, not a path. Special files are refused
before either is read. A failed explicit load returns an error and applies
nothing; a rejected automatic recall uses the last hardware settings.
If a marker read failure refuses a deletion, the profile and its active
selection are kept.

A profile saved while additional interfaces are driven carries
`additionalDevices`, their hardware states by instance id, at most four.
Recall writes each one to that unit when it is enabled and connected, and
skips it otherwise; it never enables a unit or changes the primary.

When `loadProfile` writes the device settings but the mixer settings fail, the
error says the device settings were applied and gives the mixer error. A
profile file that fails validation, or that cannot be parsed, is refused
before anything is applied; the error starts with `profile '<name>':`. A bad
mixer field is named; a non-finite device level is reported as `Saved device
levels must be finite numbers.` without one.

An unreadable profile directory or recall marker does not prevent a `state`
reply. `warning` describes the read failure; unavailable profile names are
empty and an unavailable recall choice is null. Device and mixer state and
the last active profile remain available. A later successful read clears
the profile warning without restarting the daemon.

An output's feed names one mix or several joined with `+`, every one at
unity; a blend at other levels is a mix of its own. An absent entry in
`monitorFeeds` selects `defaultMonitorMix`. Clients use the first reported
monitor when an older daemon omits `defaultMonitorMix`, or when that field
does not name an existing monitor. They do not infer a different default
from the mix ids. The per-route levels of 0.1.40 and 0.1.41
(`outputRoutes`, `setOutputRoute`) are gone: a saved
list is ignored and the command is unknown.

`setEnforcedDefaults` accepts `sink: "@monitor"` to follow the first selected
monitor output as the system playback device. The state and saved settings
retain `@monitor`; the daemon resolves it to the device's actual sink name
(without a headphone-pair suffix) on each sweep. With no selected output it
does not change the system default. Desktop volume changes on that first
output update the MONITOR volume and the other selected outputs, at the
level the desktop chose, a boost past 100% included. An output that refuses
the change, because it is asleep, re-enumerating or unplugged, is written
again on the following sweeps and once more whenever its monitor route is
rebuilt, so it does not stay behind until the desktop volume happens to
move again. Choosing a different first output starts a fresh baseline and
drops what the previous selection was owed. A fixed sink name and `null`
(no enforcement) keep their existing meanings.


Monitor mix sinks (`OpenXLR_mix_monitor` and `OpenXLR_mix_monitor2` in the
standard layout) expose the same volume and mute as their mix masters.
Desktop changes update `mixer.mixes[].volume` and `muted` on the next sweep
and are persisted with mixer settings. Values are desktop percentages
divided by 100 (1.0 = 100%, 1.5 = 150%), the scale `pactl` shows, not
PipeWire's raw linear amplitude; the daemon takes the cube root when reading
and writes percentages back with `pactl`.
`setMixVolume` and `setMixMuted` update those sinks directly, leaving channel
sends and other mixes unchanged, except for the explicit open during a global pause described below. Monitor gain is applied once, at the mix
sink before its inserts. Non-monitor masters still apply to the channel
sends and retain their 0 to 1 range.

The selected physical sinks mirror monitoring: the global pause mutes them,
and resuming restores their previous native mute flags. A selected sink whose
entire assigned feed is individually muted is natively muted too; any open
assigned mix, including Stream, Chat or Aux, keeps a summed output open. The
Pro's shared native sink stays open while its independent Aux return is active;
its selected headphone and line-output selectors instead follow the audibility
of the monitor feed. No recording mix or channel send is muted by a pause.

An explicit `setMixMuted` with `value:false` on a monitor-kind mix during a
pause replaces the listening selection: the other monitor mixes become
individually muted, the pause clears, and that mix and its assigned physical
sinks are unmuted. The next master toggle preserves this new selection instead
of reopening the previous one. An explicit monitor unmute also clears an older
native mute on its assigned sinks. Virtual-microphone and Aux mix setters do
not replace the monitor selection. Rejected monitor writes restore the previous
mix intent; failed native restorations retry on later sweeps with bounded backoff.

The state retains each mix's individual `muted` choice. Clients show a monitor
master as effectively muted when `mix.muted || mixer.monitorMuted`; sending
`setMixMuted(false)` from that state performs the explicit selection above.
`toggleOutputMute` on a monitor mix uses that same effective state.

A native mute change on a selected physical output, or a native mute on an
internal application sink while it is the system default, toggles
`mixer.monitorMuted`. Application sinks stay open so their audio can still
reach recording and streaming mixes. A mute on another application sink is
repaired without pausing monitoring. Unmuting an output that was already muted
before selection leaves it unmuted without starting a pause. Unmuting a native
mute OpenXLR authored for the global pause resumes monitoring. If the native
mute instead came from individually closing every assigned mix, desktop unmute
releases the hardware flag without guessing which mix to open or starting a
pause; those deliberately muted mixes remain silent. A later explicit OpenXLR
mute re-arms native control. The native flag of an application sink can clear
while monitoring remains paused; `monitorMuted` is the persistent master state.

After a blanket desktop mute, restoring previous mix choices stays pending
until a native read confirms it. A reconnected output keeps its previous native
choice while native and registry identities disagree. Identity reads and their
fallback mute reads back off together when the helper fails. Native changes
observed in one sweep are grouped as one action; the audio server does not
identify whether that change came from a keyboard or an applet, and multiple
toggles completed between observations cannot be reconstructed.

### Waiting for the audio server

The graph build at startup fails when pipewire-pulse does not answer yet.
The daemon then keeps running with `mixer` null and `mixerWaiting` true, and
`warning` says the mixer is waiting for the audio server. It tries the build
again after 2, 4, 8 and 16 seconds, then every 30 seconds until the server
answers or the daemon stops. Nothing has to be restarted. Once the graph is
up, `mixerWaiting` is false and the next state carries the mixer. Mixer commands sent
meanwhile are refused with the same sentence. A profile recalled meanwhile,
on connect or with `loadProfile`, applies its device settings at once and
its mixer settings when the graph is built; the latest recall wins.
`mixerWaiting` is false while the submixer is off.

### Channel volume

Every channel has a volume of its own, a master for everything the
channel sends. Each send is already one internal stream of
the channel's combine sink, with its own volume. The daemon sets that
stream to the send level times the channel volume, times the mix master
when the mix is not a monitor mix. The channel volume therefore adds no
node, no link and no delay. All three values are on the desktop scale the
send levels and masters use, so halving the channel volume takes the same
number of decibels off every send. The channel's sink stays at full desktop
volume, and the channel's meter reads that sink, before the channel volume.
Mutes stay per send.

The state reports the volume in the channel's `volume`. The settings file
and a profile keep it in `channelVolumes`, where a missing entry means 100%.
A profile saved before channel volumes recalls every channel it describes
at 100%, and a channel created later starts at 100%. A settings file or a
profile written before the hardware inputs took a volume has no entry for
`xlr1`, `xlr2` or `aux`, so they are recalled at 100%.

On XLR 1, XLR 2 and Aux In the volume comes after the interface's gain or
the Aux In level and after the input's plugin chain; `set` and the gain
lock are unchanged. It belongs to the strip, not to its source: an input
binding change keeps it, and a strip bound to another source, which has no
gain of its own, keeps its volume as its level control. The Wave XLR Pro's
direct microphone path (`hpMixMicDirect`, see
[the interface while a strip is bound](#the-interface-while-a-strip-is-bound))
carries the microphone to the jacks inside the interface, so the volume of
the channel that carries the Pro's first input does not reach it. While the
device reports `hpMixMicDirect` true, that channel's volume changes every
mix except what the jacks hear of the microphone directly; the daemon does
not try to follow it there.


Application identities use playback-node metadata, falling back to the
owning PipeWire client's application name and process binary when absent.
Windows executable names normalize to the same key as their Wine/Proton
client, for example `Balatro.exe` becomes `balatro`. `assignApp` and
`forgetApp` also accept those legacy executable-name identities. When
loading conflicting old and normalized overrides, the normalized key wins.
Identity comparisons ignore letter case for live routing as well as saved
assignments. `forgetApp` discards the cached placement of matching streams,
so the next sweep reapplies automatic routing even while the app is playing.
`assignApp` and `assignStream` take only application channels (and `ignore`);
the hardware channels `xlr1`, `xlr2` and the aux input are refused with
`unknown channel`.
`assignStream` requires a currently tracked PipeWire node id. An unknown id
returns an error without being treated as a PulseAudio stream serial. A
refused stream move does not store an unapplied assignment, including an
`ignore` choice.
At most 512 application assignments can be added through live commands.
At that limit, `assignApp` and `assignStream` may still update an existing
assignment, including an `ignore` choice. A new remembered identity returns
an error before its live route changes. Older settings with more entries
are preserved and their existing assignments remain editable.

`pluginSetup` also reports `bridgeProvider` (`openxlr` or `system`),
`bridgeDirectory` (the selected companion directory, or null), and
`windowsPluginDirectory` (OpenXLR's private wrapper root, or null), and
`windowsImportDirectory` (the managed single-plugin source folders, normally
`~/.local/share/openxlr/windows-plugins`, honoring `XDG_DATA_HOME`). Imports
use one folder per format and plugin name; selecting the same name again
updates that folder. These source files are separate from generated wrappers.
`wine` is a boolean; `wineVersion` is a string or null. `windowsEditorNote`
is a user-facing compatibility message or null. With provider `openxlr`,
`yabridge` is the companion package version with its packaging revision
after a hyphen, as the companion's receipt records it.
The `openxlr` provider includes the pinned Wine input fix. Its directory
registry and generated wrappers are separate from the system controller.

Folder-management paths must be absolute, at most 4096 characters and contain
no control characters. Removing a folder changes the selected provider's
registry; with `system`, that registry is shared with other applications.
The daemon never runs a global prune. Wrappers still covered by a retained
folder are kept. Folder changes, installs and rescans are serialized, as are
insert-chain replacements and profile loads against those operations. Check
`pluginInstall.ok` and `message` for the operation's outcome: a failed cleanup
can leave the folder unregistered with some wrappers still present. Refresh
`getPluginSetup` after either success or failure. Profiles are not rewritten.

Individual-plugin commands use the same path limits. Before disabling or
uninstalling an in-use plugin, a client can confirm and call
`removeWindowsPluginInserts`. It resolves all classes exported by that file,
not other formats or similarly named files. The reply says how many inserts
and chains changed. A rewire failure preserves the failed chain definitions
and reports partial completion; a save failure restores the previous chain
settings and attempts to restore their audio paths. Clients must check
`pluginInstall.ok` and its message, then refresh usage. The operation is
serialized with profile loads, insert replacements and file-management
operations. Profiles are not edited, so recalling one can add the plugin back.

Exclusions are stored
by canonical source path, so linked imports retain the same exclusion as
the Wine-installed original. A folder-level exclusion must be removed through
yabridge before a file blocked by it can be enabled. Exclusions are not global
class-ID bans: a separately installed copy can still appear in the catalogue.
`deleteWindowsPlugin` cannot run a Windows uninstaller. The UI opens Wine's
installed-apps list in the reported `winePrefix`, waits for it to close, then
syncs and rescans. It never imposes a deadline on an interactive uninstaller.

Native editor compatibility is separate from DSP support. Catalogue entries
carry `nativeUiBlocked`; live insert status also carries `nativeUiBlocked`
and `nativeUiBlockReason`. Blocking leaves `supported`, native-host support,
processing, parameters and bypass state unchanged. Clients should open their
generated controls when blocked and refresh availability on
`nativeEditorRulesChanged`. Existing editor windows are not closed.

Rules use the format and stable plugin id, not the filename or display name.
Only explicit overrides are saved; an untouched plugin follows the defaults
in each release. An explicit allow or block survives future default changes.
The initial release default blocks the Elgato De-Esser VST3 editor. Rule ids
are bounded at 512 characters, names at 200, and the file at 1024 overrides
and 1 MiB.
Malformed stored choices are not silently overwritten; release defaults stay
in force and the rules response reports the error.

LV2 insert definitions optionally carry `nativeHost: true` to select the native
helper for that insert. Missing or false keeps LV2 in PipeWire filter-chain, even
when the helper is installed. Unsupported native selections are rejected.
Changing this choice via `setInserts` rebuilds the chain and can interrupt audio.
Only exposed parameter values are persisted, not opaque plugin state or presets.

When PipeWire's filter chain refuses to load an LV2 insert and the helper is
installed, the daemon runs that insert in the helper instead. Each insert is
decided on its own. The daemon rebuilds a refused chain of several inserts with
one filter-chain stage per insert, and only a stage PipeWire refuses again moves.
Such an insert reports `nativeHostRunning: true` while its `insert.nativeHost`
stays false, and `filterChainError` carries the reason PipeWire gave; the field
is null for every insert running in the host it was saved to. Clients follow
`nativeHostRunning` to offer the plugin's editor. The saved choice is not
changed, so the next build of the chain tries the filter chain again. When the
helper is missing, or cannot run the plugin either, the insert's `error` carries
the filter chain's reason, followed by the helper's when it was tried.

The OpenDeck plugin in `plugin/`, the terminal mixer
(`src/OpenXLR.Tui/DaemonLink.cs`) and the Omarchy plugin
(`packaging/omarchy/openxlr.mixer/DaemonLink.qml`) are clients of this
API; the command handler is `WebSocketHub.cs` and the message shapes are
in `Protocol.cs`, both under `src/OpenXLR.Daemon/`.

### Capture input chains

Each channel in the state carries `microphone` and `chainWidth`. An older
daemon sends neither, and a client then keeps to mono chains on `xlr1` and
`xlr2`. `chainWidth` is written as null on a channel that cannot host a
chain. Sound Check and the microphone EQ commands accept channels whose
`microphone` is true, so a capture input on one mono port has the same EQ
as XLR 1, running on this computer (see
[EQ on capture inputs](#eq-on-capture-inputs)). A capture input on a stereo
pair has no EQ.

The `mixer.inserts` map also accepts capture channel ids. `setInserts`,
`setInsertBypass`, `setInsertParam`, `renameInsert` and `showInsertUi` use
those ids as `channel`. `removeWindowsPluginInserts {path}` removes matching
occurrences from capture chains too; it still takes a plugin path, not a
channel.

Each chain allows at most 16 inserts. A new plugin must fit the binding's
width: a mono capture input reports "has no mono layout for an input chain"
and a stereo capture input "has no stereo layout for an input chain" when
it does not. Aux In and application channels still report "has no insert
chain". A saved insert whose plugin does not fit the width stays in the
saved chain, is left out of the running one and carries its own error, and
it can still be removed. CLAP, VST3 and native LV2 editors still need the
native host.

An active capture chain sits before the channel's combine sink, sends and
meter. Without active inserts the direct graph is unchanged. The daemon
links a replacement before it removes the previous path, and the combine and
its send indexes stay in place. Each channel heals alone, and a failed plugin load
falls back to its direct feed. Unplugging stops the chain; reconnecting
starts it from the saved parameters and closes any previous editor.
Deleting a channel removes its saved chain and stops its hosts and session.

Profiles save every input key, including an empty list. Recall replaces
only the input keys the profile names, so an older profile leaves capture
chains alone. Mix keys keep their existing absent-means-empty rule. The
settings loader drops and logs insert keys outside the restored layout.

### Application names

`mixer.streams` has one entry per application, not per stream: every
playback stream and audio client of one application shares its `identity`,
and the entry stands for all of them. `identity` is the key an assignment
is remembered under and the one `assignApp` and `forgetApp` take. It is
the process binary as PipeWire reports it (`Discord`, `chrome`), `steam`
for Steam's browser process, and a lowercased game key such as `balatro`
for a Windows game under Wine or Proton. It is a key, not a name.
`label` is the name a client shows. The daemon takes it from the stream or
client's `application.name`, except when that names a runtime such as
Chromium, Electron, WEBRTC VoiceEngine or Wine, or is an identifier: one
word joined by underscores, with no space. Discord's
`discord_hardware_mute`, the client it opens to watch a headset's mute
switch, is one. In both cases the label is the process binary with its
first letter capitalised (`Discord`), when the binary is the application's
own and not a Wine or Proton runtime. A
Windows executable's name loses its `.exe`. A label remembered in
`mixer.json` from such an identifier is read back the same way. Clients
show `label` and keep `identity` for commands.

### Application icons

A `streams` entry carries `icon` when the daemon found one for the app: a
freedesktop icon name, or an absolute image path when the app's desktop
entry names one. `icon` is absent when nothing was found. The daemon tries,
in order:

1. the `application.icon_name` the app published to PipeWire, a bare name
   only;
2. the desktop entry whose id is the Flatpak portal's
   `pipewire.access.portal.app_id`, else the app's `application.id`;
3. the desktop entry whose id, `StartupWMClass`, last reverse-DNS segment
   of the id, or Exec program is the process binary;
4. the desktop entry whose `Name`, `StartupWMClass` or id is the app's
   label, or whose `Name` has the same letters and digits, the way game
   identities compare;
5. the process binary itself, lowercased, since many packages install their
   icon under the program's name.

Wine and Proton binaries are skipped, since every game shares them. The
daemon reads desktop entries from `XDG_DATA_HOME`, `XDG_DATA_DIRS`, the user
and system Flatpak exports and `/var/lib/snapd/desktop`; an earlier
directory's entry hides a later one with the same id. It reads them again
only when one of those directories changes. The daemon matches a remembered
app that is not running by its identity and label, and an app keeps the
icon it was last seen with. The name can be one the client's icon theme
lacks, so a client needs its own fallback.

### Plugin latency

Each insert's status carries `latencyMilliseconds`, the processing latency
the plugin reports: zero for a bypassed insert, and absent (null over HTTP)
when the plugin gives no figure. A plugin in the native host reports what
the helper last read from it; there is no figure before the helper's first
report, which comes once audio has run. An LV2 plugin in the PipeWire filter
chain cannot report, so it reads zero when its metadata declares no latency
port and has no figure otherwise.

`setMixLatencyCompensation` turns compensation on or off. It is a mixer
setting, saved in `mixer.json` and not part of a profile. With it on, an LV2
insert whose plugin declares a latency port runs in the native host when the
helper is installed and supports the plugin, so its figure can be read; its
`nativeHost` field is not changed. Turning it on or off rebuilds every insert
chain, on the inputs and on the mixes.

With compensation on, each mix's total is the sum of its inserts' figures,
and every mix is delayed by its distance from the slowest. An insert with no
figure counts as zero, and compensation never waits for one. That covers an LV2
plugin with a latency port when the native host is not installed or cannot
carry it, a CLAP or VST3 plugin that reports nothing, and any plugin until its
helper's first report. Clients show such an insert's latency as unknown.
The delay is a stereo stage after the mix's inserts, which every consumer
of the mix reads; it exists only while the mix needs a delay above zero, and
follows a changed figure without restarting any plugin. Input inserts sit
before the mixes, so they reach every mix alike and are not compensated.

`mixDelayMilliseconds` holds the delay each mix has now, by mix id; a mix
with none is absent. A mix total above 2000 ms delays no mix, and
`mixLatencyError` says so; it also reports a delay stage that could not be
built or set. A stage that keeps failing is left off after three failures
in five minutes, until the mix no longer needs a delay or compensation is
turned off and on again.

### Input bindings

Each hardware strip has an input binding. By default XLR 1, XLR 2 and Aux
In take the driven interface's capture pairs 0, 1 and 2. `setInputBinding`
binds XLR 1 or XLR 2 instead to one port of any PipeWire capture source, by
exact node name, and that port feeds both sides of the strip ahead of the
strip's software DSP and insert chain. Aux In always stays on the interface.
Each strip resolves, heals and reports its own source, so a missing or
ambiguous source silences only its strip. Two strips may take the same
port. A binding is machine wiring. The daemon saves it in `mixer.json` as
`inputBindings` (see [mixer-layout.md](mixer-layout.md)), and saving or
recalling a profile never touches it.

A bound source or port may be offline when selected; no default source and
no interface node replaces it. The sweep links the strip when the exact
name returns and pushes source arrivals and losses to clients, including
when no playback output changed. When two sources have the name, the strip's
route is removed even if it was connected, and it resumes once the name
identifies one source. Changing a strip's binding mutes that strip in the
monitor mixes, and ends Sound Check when the session runs on that strip.
Sound Check records the strip's own source and port; a change to the other
strip's binding leaves a session alone. A strip whose `bindingConnected`
is false refuses `record` with "The microphone is not connected.", followed
by the strip's name and its `bindingWarning` when it has one.

Each channel in the mixer state carries:

- `binding`: what feeds it. `{"kind":"unit","pair":0}` is a hardware strip
  on the interface's capture pair; `{"kind":"source","source":"...","pair":0,"monoChannel":1}`
  is a hardware strip bound to a source, or a capture channel, which
  reports its `captureSource`, `capturePair` and `captureMonoChannel` here
  as well (`monoChannel` is absent when it takes a pair). A strip or capture
  channel bound to a supported interface's node also carries `unit`, that
  unit's id as `waveInterfaces` spells it. For a capture channel this comes
  from `userChannels[].captureUnit`, filled on creation or the first sight
  of its exact source on an attached unit. An older daemon leaves `unit`
  absent on capture channels. `source` stays the saved name while the
  channel plays from the unit's renamed node. The exact name wins; without
  it, only one source on that card with the selected port qualifies. Several
  sources on the card, or indistinguishable units sharing a bare model id,
  leave the channel silent with `bindingWarning`. `{"kind":"apps"}`
  is an application channel. A bound hardware strip keeps `hardware` true
  and `captureSource` null, so a client that tells capture channels apart by
  `captureSource` does not mistake it for one.
- `bindingConnected`: the channel's feed exists. For a strip on the
  interface, the interface's node was found and the pair linked, and a
  pair the unit has that could not be linked is tried again each second,
  whatever the other strips on the unit do; for a bound strip, the exact source is present, unique, has the port and is
  linked to both sides, or, for a binding with a `unit`, the one source
  that unit's card publishes under another name is; for a capture channel, the same as
  `captureConnected`; always true for an application channel.
- `bindingWarning`: why the channel's source is silent, or null: the source
  is not connected, the name matches more than one source, the port is
  missing, or the link is not made yet and is being retried. A strip on the
  interface leaves it null; the interface's own problems stay in the
  top-level `warning`.
- `unitInput`: null, or `{unit, input, primary}` naming a driven unit (its
  instance id, as `waveInterfaces` spells it; `primary` is true for the unit
  in the header, false for an additional interface) and which of its
  microphone inputs, 1 or 2, the channel carries. A strip on the interface
  carries the input on its pair: XLR 1 input 1, XLR 2 input 2 on a unit with
  two XLR inputs. Otherwise the input goes to a strip bound to the unit's
  node at the input's port (the first for input 1, the third for input 2),
  then to the first capture channel, in layout order, on the unit's own
  capture node at that port or on that input's pair. A capture channel also
  matches by its saved `captureUnit`, so its controls survive a rename. With
  no matching channel, no channel carries it. The same rule names an
  additional interface's input, while
  the daemon drives that unit. A unit's input is on one channel at most, and
  a channel carries one input at most, the primary's first. The primary's
  controls stay in `state` and `capabilities` and are written with `set`; an
  additional unit's are in its `waveInterfaces` entry and are written with
  `setWaveControl`. A client can also use `setWaveControl` for the primary
  id: resolve `unitInput.unit`, send `{device, control, value}`, and append
  `2` to the control for input 2.

`present` follows one rule. A channel without a jack, and a strip bound to
a source, is always present. A strip on the interface is present when the
connected unit has its jack. With no unit connected, it keeps the last
answer of this daemon run. Before any unit has connected in this run, XLR 1
is present and XLR 2 and Aux In are present only while a supported unit is
attached, so a desk with no Elgato unit gets one strip.

A microphone strip's `name` follows what feeds it. XLR 1 and XLR 2 keep
those names only while a real XLR jack feeds them: the driven unit's input
on a Wave XLR Pro, a Wave XLR of either model or an XLR Dock, or such a
unit's input that the strip is bound to, which keeps the strip's own name
(a strip bound to an XLR Dock is `XLR 1` or `XLR 2`). A strip bound to any
other source is `Mic 1` or `Mic 2`, and so is the strip on a Wave:3's or
Wave:1's built-in capsule, whose `capabilities.xlrJacks` is false, the
driven unit's or another one's. The daemon learns what a bound unit's
inputs are when the unit is attached, and keeps that by model for the rest
of the run. With
no unit driven, a strip on the interface keeps the name the last driven
unit gave it in this run. The names are fixed strings, `renameChannel`
refuses a hardware strip as before, and the ids stay `xlr1` and `xlr2` for
every command, profile, skin, layout entry and Stream Deck target. The
strip's `appearance.defaultIcon` follows the same rule: `mic` on a real XLR
jack, `microphone` on a capsule or another source, and `headset` on a
`bluez_input.` source. The top-level `warning` and the Sound Check refusal
name the strip the same way.

#### The interface while a strip is bound

A binding never changes whether a unit is driven. The primary interface and
any additional ones stay driven whatever XLR 1 and XLR 2 take: `connected`,
`device`, `capabilities`, `state`, `detected` and `waveInterfaces` describe
them as always, `set`, `setWaveControl`, `setActiveDevice` and
`setWaveInterfaceEnabled` work, recall on connect and the gain lock are
unchanged, and the Wave XLR Pro keeps its pseudo-outputs in `devices`, its
output selectors, the Aux mix on its USB Aux port and its monitor routes.
Aux In and a strip still on the interface keep their audio.

The unit's input-stage controls belong to the channel whose `unitInput`
names them (see above). The Wave XLR Pro's direct microphone path
(`hpMixMicDirect`, the zero-latency path from its first input to its jacks)
follows the same channel: XLR 1 while it takes the interface, or the capture
channel on the Pro's own node at its first port while XLR 1 is bound
elsewhere. The daemon turns the path on while every monitor output is a Pro
jack and at least one mix feeding them is unmuted and carries that channel
unmuted. Muting the feeding mix turns the direct path off. For a summed feed,
it stays on while any feeding mix and that channel's send to it are both
unmuted. The daemon mutes that
channel's monitor send in the graph meanwhile so the microphone is not
heard twice; it never uses the path while monitoring is paused or that channel
is in an exclusive group or under Sound Check. With no channel carrying the Pro's first input
the path is off, and a bound XLR 1 reaches the jacks through the software
mix. The daemon holds the path to this rule whether or not a jack is in use,
so a path another program left on (Wave Link, for one) is switched off as
soon as the Pro is driven.

`softLowCutApplicable` and `softClipGuardApplicable` follow XLR 1's own
binding: the software low cut and ClipGuard act on a bound XLR 1 whatever
unit is driven, and a unit coming or going leaves that strip's chain alone.
A software ClipGuard left on behind a unit's hardware one goes off when
XLR 1 is bound to a source and the plugin it needs is missing, as it does
when the unit changes.

#### A strip on another interface

A strip can take a supported interface's own node, such as a Wave:3's
capsule while a Wave XLR Pro is the primary. The `devices` entries of such
nodes carry `drivenUnit`. The daemon records the unit in the binding (its
`unit`, saved in `mixer.json`), and two things follow from it.

- The strip follows the unit, not only the name. Driving the unit, a card
  profile switch and some replugs rename the unit's node (a Wave:3's
  `.mono-fallback` source becomes `.analog-stereo`; a card with a UCM split
  goes to pro-audio). While the exact name is missing, the strip takes the
  one source the unit's card publishes now, found by the card part of the
  node name, which carries the unit's serial and does not change, and goes
  back to the exact name when it returns. The strip keeps its port, its
  binding as saved and its monitor mutes; the console rule mutes its
  monitor sends when the node it plays from changes, as for any source. If
  the card publishes several sources at that moment, the daemon picks none,
  and the strip stays silent with a `bindingWarning` until it is bound
  again. A binding without a `unit` follows its exact name only.
- While the unit is driven as an additional interface, the strip's
  `unitInput` names it with `primary` false, and a client draws that unit's
  controls on the strip from its `waveInterfaces` entry. Driving the unit
  is `setWaveInterfaceEnabled`, a separate command; the binding works
  whether the unit is driven or not. Disabling the unit takes its
  `unitInput` off the strip and leaves the binding in place.

A route into the sink of a unit that a strip is bound to waits until that
strip's capture is linked, as a route into the driven interface's own sink
waits for the strips on it, and is made anyway after five seconds without
the capture.

Disabling the software mixer keeps the saved bindings for when the mixer is
enabled again.

### Additional interface state

`waveInterfaces` lists every attached supported unit and every unit enabled
as additional that is not attached now, each as `id`, `name`, `active`,
`enabled`, `connected`, `captureHint`, `captureModelHint`, `capabilities`,
`state` and `warning`. `id` is the instance id, `vvvv:pppp@` and sixteen
lowercase hex digits hashed from the unit's USB serial, or from its USB port
when it has no serial or shares it with another unit; it stays the same
across replugs, and for a unit without a serial it changes with the port.
`active` marks the primary, `enabled` the saved additional role, and
`connected` and `state` describe whichever manager drives the unit. A unit
that is primary is never driven as additional too. `active` moves only with
`setActiveDevice`. While the chosen unit is briefly away, another unit that
is not `enabled` may stand in as the primary, and it hands the role back once
the chosen unit returns.

`captureHint` is the PipeWire name fragment of the unit's capture node: the
model as udev spells it, then `_`, the serial and `-` when the unit has a
serial. `captureModelHint` is the model fragment alone, to try when the hint
finds no node, and empty while another attached unit could answer to it.
Clients match both verbatim, without trimming. The selection rules for the
hardware strips are in [multiple Wave interfaces](wave-interfaces.md). An
input binding on XLR 1 or XLR 2 changes none of this: every unit listed here
is driven or not by its own choice, and a capture channel on an additional
unit's node keeps working while a strip is bound. A strip or capture
channel that carries a driven additional unit's input says so in its
`unitInput`; the controls a client draws there are this entry's
`capabilities` and `state`, written with `setWaveControl`.

Unit-to-channel controls are updated from the last published device states
before a changed state is broadcast. These updates are serialized outside
the USB poll loops, so a read stalled on one interface does not block a
command or state notification from another interface.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `installation.json`: exact startup ownership and replaced-file metadata
- `installation-backups/<sha256>`: private originals retained for startup restoration
- `installation-history/<sha256>.json`: retained package inventories across upgrades
- `installation.lock`: serializes installation inventory writes
- `mixer.json`: every mixer decision: the layout (`userChannels`,
  `mixes` (legacy `userMixes`), `appearance`, see [mixer-layout.md](mixer-layout.md)), levels, mutes,
  device choices, the app registry, enforced defaults, `monitorMuted` (the
  monitoring pause, never part of a profile), the software low
  cut, the insert chains, the microphone EQs (`eq`) and where they run
  (`eqProcessing`), the [ducking](#ducking) setup (`ducking`), the
  per-channel volumes (`channelVolumes`), the exclusive groups
  (`exclusiveGroups`), the monitor feeds (`monitorFeeds`),
  `compensateMixLatency`, and `inputBindings`, the hardware strips bound to
  a source. The bindings are machine wiring and never part of a profile. A
  file written before `inputBindings` existed carries XLR 1's source as
  `primaryInput`; the daemon reads it as XLR 1's binding and the next save
  writes `inputBindings` instead. Written by the daemon.
- `profiles/<vid-pid>/<name>.json`: the named scenes, one file each; while
  no interface is connected, `profiles/generic/` holds mixer scenes only
- `profiles/<vid-pid>/recall-on-connect`: the name of the profile
  recalled when that device connects, when one is chosen
- `devices/<vid-pid>/last-state.json`: for a device using connect-time
  restoration (`retainsSettings: false`), the hardware settings the daemon last saw, written a second
  after a change and restored on every fresh connect
- `devices/<vid-pid>/defaults.json`: the settings such a device answered
  with after a power cycle, what `resetDevice` writes back
- `gainlock.json`: which devices have the gain lock set
- `$XDG_RUNTIME_DIR/openxlr/token` (or `token` here without a runtime
  directory): the control API token for this daemon run, 0600, see the
  top of this page
- `$XDG_RUNTIME_DIR/openxlr/daemon.lock`: held by the running daemon for
  the life of the process; a second daemon for the same user finds it
  held and exits with code 75 at once
- `daemon.json`: the daemon's own preferences.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). It is read
  once at start and written by the UI's Options window.
  `pluginFolders` is the list of `{kind, path}` folders added with
  `addPluginSearchPath`, written by the daemon and read at each scan and
  each LV2 host start. Each writer keeps the keys it does not own.
- `bridge/yabridgectl/config.toml`: the managed companion's folder registry,
  separate from the system yabridgectl configuration. Private wrappers live
  under `$XDG_DATA_HOME/openxlr/yabridge` (default `~/.local/share/openxlr/yabridge`).
- `native-editors.json`: native editor compatibility overrides. Format
  `{"version":1,"overrides":[{"kind":"vst3","plugin":"<class-id>","name":"Plugin name","blocked":true}]}`.
  Release defaults are not copied into this file. Remove an override through
  `setNativeEditorRule` with null `blocked` to follow release defaults again.
- `ui.json`: UI preferences (tray, start minimized, autostart
  toggles, the chosen skin, collapsed sections and `sectionOrder`, the
  order of the five sections, and the submixer view: `mixerView`
  (`compact`, `mini`, or absent for the full mixer), `compactChannel`
  and `compactMix`, and `showHiddenItems`, true while the submixer's Show
  hidden draws hidden channels and mixes faded)
- `wave-interfaces.json`: the additional interfaces the user enabled, at
  most four instance ids. Written by the daemon.
- `desktop-keys.json`: the UI's desktop key settings. Written by the
  UI only.
- `effect-chain-presets.json`: the UI's saved effect presets, 0600.
  Written by the UI only; the daemon never reads it. Format in
  [effect-presets.md](effect-presets.md).

## Plugin search paths

`pluginSetup.searchDirectories` lists `{kind, path, custom, exists}` for
every folder searched: `kind` is `lv2`, `clap` or `vst3`, `custom` marks a
folder added with `addPluginSearchPath`, and `exists` is a directory check
made when the reply was built, not a promise that every bundle in it loads.
The LV2 entries are `LV2_PATH`, or the standard directories that exist, then
the added folders. `searchPathWarning` is null, or a sentence saying how many
saved folders were skipped because they are invalid or no longer resolve.
Skipped entries stay saved.

`addPluginSearchPath` and `removePluginSearchPath` take `kind` and an
absolute `path` of at most 4096 characters without a colon or control
character; other values are refused before the command runs. The path is
resolved through symbolic links. Adding refuses the filesystem root, broad
system directories, the home folder itself, a folder that does not exist, one
inside or around a folder the format already searches, and a 33rd folder.
Adding a folder already in the list, or removing one that is not, answers
`ok: true` without a rescan. The list is saved as `pluginFolders` in
`daemon.json` through the same atomic write as the daemon's other settings;
if that write fails, or `daemon.json` exists and cannot be read (it is then
left as it is), the answer is `ok: false` with the reason, and nothing is
rescanned. CLAP and VST3 folders are walked to any depth, up to 16,384
entries per folder; past that the walk stops and the scan reports the folder.

Once an LV2 folder is added, the daemon passes one `LV2_PATH` to the lilv
scan, to every PipeWire filter-chain it loads and to the native host. That
path is the daemon's own `LV2_PATH` or the standard directories, then the
added folders.
With none added, all three keep the daemon's environment and lilv's default.

## Plugin discovery diagnostics

`getPluginDiagnostics` reads the daemon's environment, not the UI's shell.
`discovery` includes `controller`, `wineExecutable`, `winePrefix`,
`sourceCommit` for a managed bridge, `hostExecutable`, `hostInstalled`,
`processArchitecture`, `searchPaths` and `scans`. `searchPaths.lv2Override`
is the `LV2_PATH` LV2 hosts are given (the daemon's own, or the standard
directories plus added plugin folders) or null for lilv defaults; `clap` and
`vst3` contain up to 64 effective search directories, including private
wrappers and added plugin folders.

Both `pluginSetup` and `discovery` include `memoryLockHardLimitBytes`, the
daemon's hard memory-lock limit. The existing `memoryLockLimitBytes` stays
the effective soft allowance. Both use bytes, -1 for unlimited and null
when unreadable. Setup's `memoryLockNote` recommends the unit setting when
the hard limit has enough room, or checking the user manager's ceiling
when the daemon's hard limit is also low. A low daemon hard limit alone
does not prove that the user manager has the same ceiling.

`discovery.hostEnvironment` describes the scanner and live host policy for
both managed and system bridges. `loaderEnvironment` holds `LD_LIBRARY_PATH`,
`LD_PRELOAD` and `LD_AUDIT`, with null for absent or removed values.
`cleanLaunch` is true only for `OPENXLR_PLUGIN_CLEAN_ENV=1`.
`removedLoaderEnvironment` maps the variables actually removed to their
inherited values, and is empty on a normal launch. `wineLoader` records
`WINELOADER`, or null; `wineRunner` resolves that runner, or `wine` when unset,
against the host's effective PATH. An unresolved name or path is retained
so a missing runner can be diagnosed. These strings keep at most 4096
characters each, including the truncation marker. The diagnostics archive
redacts paths in all of them with the same rules as the other plugin data.

`pluginSetup.wineTrace` and `hostEnvironment.wineTrace` are true when the
running daemon has `OPENXLR_PLUGIN_WINE_TRACE=1`. `setPluginWineTrace` sets
that process variable to `1` for `value:true` and clears it for `value:false`.
It replies with `pluginSetup`, followed by `commandResult` when a `requestId`
was supplied. A missing, null or non-boolean value is refused. The switch
is not persisted, since tracing is slow and produces large logs. A value
inherited at daemon startup still applies, including after a later restart.
The command does not change `WINEDEBUG`, start a scan or clear the cache.
Enable it, call `rescanPlugins`, collect diagnostics after the scan finishes,
then disable it. Rescan retries failed bundles; successful unchanged bundles
remain cached and produce no new trace.

`scannerWineDebug` is the effective scanner `WINEDEBUG`, bounded to 4096
characters, or null for Wine's default. The opt-in supplies
`+seh,+unwind,+loaddll` only when `WINEDEBUG` is absent. Explicit values,
including an empty string, win. Live hosts do not receive this added trace.
`scanLogs.traceCaptureBytes` is 8 MiB of raw scanner stderr;
`traceStderrCapBytes` is 1 MiB saved after collapsing consecutive repeats.
Normal stderr stays at 256 KiB, stdout at 64 KiB, retention at 24 files and
4 MiB total. Headers have at most 2048 UTF-8 bytes per string value. Logs
keep three copies of repeated lines and count the rest, ignoring timestamps
and Wine process/thread prefixes while comparing the remaining text.
The archive reads up to 1152 KiB per log within its 4 MiB input budget.

`pluginSetup` and `discovery` also expose `skippedFailedCount` and
`skippedFailedBundles`. Each bundle has `kind`, `path`, `outcome`, a readable
`reason` and `failedAt`, the UTC time the failed attempt began. Older cache
entries have outcome `unknown` and a null time. The list holds at most 128
bundles; the count includes every skipped bundle in the latest completed
scans. Each scan report carries the same fields for its own format. These
omissions do not produce repeated warnings. Options shows the count and
list under PLUGINS, and Rescan retries the failures.

`status` is null without a controller. Otherwise it contains `exitCode`,
`timedOut`, `truncated`, `output` and `error`, or only `error` when the
controller cannot start. The status command has a five-second deadline,
64 KiB stdout and 16 KiB stderr limits. It does not run a sync.

`scans` contains the last completed report for each native format since
startup, with `kind`, `completedAt`, `entries` and `omitted`. An empty list
means no native scan has completed yet. Entries carry `path`, `outcome`,
`cached`, `plugins`, `duplicates`, `exitCode` and `detail`. Outcomes include
`host-missing`, `directory`, `directory-missing`, `directory-error`,
`start-error`, `scan-failed`, `source-missing`, `windows-module-missing`, `timeout`, `output-limit`, `output-incomplete`, `invalid-description`,
`no-plugins`, `ok`, `skipped-failed` and `scan-error`. A cached description is reused only
while the native helper that wrote it is the one asking, so `cached` is
false everywhere in the first scan after the helper changes. Reports retain
at most 128 entries per format, preferring failures over successful entries
when full. Paths keep the first 4096 characters plus a truncation marker.
Deep trace details keep the first 16384 characters including the truncation
marker. Ordinary details use at most 2048 characters including the marker, with a quarter of
the remaining space for the start and three quarters for the end. This
keeps later errors from being hidden by a bridge's startup banner.

After startup discovery and each install, sync or rescan, the daemon logs
retained scan failures with their format, path, outcome and exit code when
known. `pluginInstall.message` appends a summary of these failures without
changing `ok`, `installed`, `added` or `total`. `ok` still describes the
install or sync step, not whether every bundle could be scanned. Missing
optional directories, an absent native helper and bundles reporting no
plugins are not counted as scan failures. The summary uses the retained
entries, so check `omitted` for larger scans. A timed-out scan is remembered
as a failure and is retried on an explicit rescan. Automatic scans skip
unchanged failures without repeating the warning. `source-missing` identifies a missing
plugin path or dangling bundle link. `windows-module-missing` identifies a
yabridge wrapper that cannot find its original Windows module; its detail
includes the broken Windows link target when available. Both are scan
failures and include recovery guidance in the user-facing summary. Cache
stamps follow linked plugin files and where each link points, a bundle that
is itself a link included, so a removed, updated or repointed Windows source
cannot keep an unchanged wrapper's old catalogue entry alive, even when the
new source has the same length and timestamp as the old one. A link with nothing behind it is part of the
stamp rather than a bundle the cache refuses to keep: a bundle also holds
files the host never loads, a wrapper for another architecture among them,
and one of those pointing nowhere does not stop the rest being remembered.
Whether the files the host does need are usable stays the scanner's answer,
and a failed scan is cached as a failure rather than a usable description.
Fresh malformed descriptions are failures too. An unreadable cached
description is invalidated so the next scan reads the bundle again.
Scanner start errors are not cached because a helper or resource problem
can disappear before the next launch.

These reports describe scanner output before the `plugins` message's size
budget and the picker's channel-width/format filters. Compare them with
`listPlugins` to distinguish scanning from filtering. Reading diagnostics
does not invalidate scan caches or retry plugins. The API returns paths and
scanner messages to authenticated clients; the UI redacts personal paths
when writing the diagnostics archive.

The desktop client does not queue catalogue or diagnostic queries while its
daemon connection is down. Such a query returns null at once and retains no
pending reply slot. Queries already sent keep their request identity until
the acknowledgement or disconnect, so a late reply cannot answer a newer
query.

### Sound Check

`mixer.soundCheck` is `{channel, mode, seconds, error}`. `channel` is the
microphone channel of the session, including a mono capture input id, null
when there is none. `mode` is what
the helper last did: `recording`, `looping` or `live`, and `idle` without a
session. `seconds` is the length recorded, 0 to 10, and the daemon pushes
state as it grows. `error` says why a session ended by itself.

The sample is taken from the channel's route source and selected mono port,
after any hardware gain and processing. It enters ahead of the channel's
inserts and, on XLR strips, its software processing, so every change to
those is heard on the next pass. A capture input without inserts uses the
loop helper directly and returns to its direct source feed on stop. What
the channel sends, the loop included, goes wherever the channel
goes: the monitor mixes and the virtual microphones alike. The sample lives
in the helper's memory and is never written to disk, `mixer.json` or a
profile. While a session runs on the channel carrying the Pro's first input,
including a capture input, the Wave XLR Pro's zero-latency hardware
microphone path is off, so jacks fed by a monitor mix hear the loop
through the software send rather than the live microphone.

A session ends by itself when its channel's source changes (the interface's
node, or the bound source and port; another channel's change leaves it
alone), when the helper or its input link goes away, when the audio graph
is rebuilt, when its capture channel is deleted, or ten minutes after it
started. A channel still in the layout is then wired back, and the state
keeps `channel` with `mode` `idle` and the reason in `error`, so a client
shows it on that microphone only, until a `stop` for
that channel clears it or a new `record` replaces it. After deletion only
a new recording can replace the notice, since the old id is no longer valid.

An invalid channel or action is refused with "soundCheck: need a microphone
channel and action 'record', 'loop', 'live' or 'stop'". A missing capture
source refuses `record` with "The microphone is not connected.", the channel's
name and its `bindingWarning`. A source missing the selected port refuses
with "The microphone's capture port is unavailable." An older UI has
only its XLR Sound Check views and does not show a capture session.

### Microphone EQ

XLR 1, XLR 2 and every capture input on one mono port each have a four-band
EQ with the controls, ranges, steps and curves of Wave Link's. A capture
input's always runs in software (see
[EQ on capture inputs](#eq-on-capture-inputs)); the rest of this section
describes XLR 1 and XLR 2. On the Wave XLR MK.2 and the XLR Dock MK.2 from
firmware 1.0.15, and on each of the Wave XLR Pro's two inputs from firmware
1.0.14, the unit's own DSP runs it (see
[where the EQ runs](#where-the-eq-runs)). Everywhere else it runs in the
native plugin host at the head of the input's route, before the software
low cut, ClipGuard and the inserts, so the channel volume, the sends, the
meters and Sound Check's loop all see the equalised signal; in software the
Wave XLR Pro's zero-latency hardware path and the other units' hardware
monitor mix carry the raw microphone. It adds no latency. It is off until
switched on.

A channel's `eq` in the mixer state is
`{enabled, bands, backend, available, error, processing, device}`. `bands`
holds four `{enabled, type, frequency, gain, q}`:

- `type`: `lowCut` (high pass), `lowShelf`, `peak` (a bell, which Wave Link
  calls Band Pass), `notch`, `highShelf` or `highCut` (low pass).
- `frequency`: 20 to 20000 Hz, in whole hertz.
- `gain`: -12 to +12 dB, in quarter decibels. It is kept for every type and
  ignored by `lowCut`, `notch` and `highCut`, so a band changed to one of
  those and back keeps its gain.
- `q`: linear, 0.1 to 10, on Wave Link's grid, where log10(q) moves in steps
  of 0.016 from -1. A value is snapped to the nearest point; a value already
  on the grid comes back unchanged.

The defaults are Wave Link's: a 36 Hz `lowCut`, `peak` bands at 310 Hz and
2172 Hz, and a 15049 Hz `highCut` that is off, every `q` 0.7047 and every
`gain` 0, with the EQ off. `backend` is `device` while the unit's own DSP
runs the EQ, with the EQ on or off, since the unit holds the switch;
`software` while a stage runs for the channel; and null while neither does:
the EQ is off and was off when the route was built, or its stage could not
start. `available` is always true on the unit, which needs no helper; in
software it is false when the native plugin host is not installed, or after
the stage failed three times in five minutes, and `error` then says why;
switching the EQ off and on again tries once more. The mixer state's `eqLimits` gives the same ranges
for clients to draw from: `{bands, types, frequency {min, max, step}, gain
{min, max, step}, q {min, max, log10Step}}`.

The daemon snaps every value it takes, whichever client sent it, so the
files, every client and the curve hold the same numbers. A band's curve is
the one in Robert Bristow-Johnson's Audio EQ Cookbook, and the bands are in
series.

Switching the EQ on starts its stage and links it in at the head of the
route, then removes the old link. The microphone is never silent, and the
chain behind it keeps running, so no plugin restarts and no editor closes.
Every other change is written to the running stage: a band's values, a band
on or off, the EQ's own switch. A frequency, gain or Q change keeps the
filter's state, and a band or the EQ switched, or a band's type changed,
crossfades over 5 ms. Switching the EQ off bypasses inside the stage, which
stays until the route is next built for another reason or the daemon
restarts; a microphone whose EQ is off when its route is built gets no
stage. A stage that dies is started again by the sweep; while it cannot be,
the microphone plays without the EQ.

The EQ is saved in `mixer.json` under `eq`, by channel id, with the debounced
save the faders use. A microphone absent there has the defaults with the EQ
off. A profile saved now carries `mixer.eq` for every microphone; a recall
replaces the EQ of each channel it names and leaves the others, and a
profile without `eq`, as every older one is, leaves every EQ alone. A recall
that changes only values writes them to the running stages and starts
nothing; one that switches an EQ on splices its stage in.

#### EQ on capture inputs

A capture input whose `microphone` is true, one mono port of any source,
takes the same `setEq`, `setEqEnabled` and `setEqBand` with its channel id,
and its `eq` in the state has the same fields, defaults, ranges and steps.
It runs only in the native plugin host on this computer, never on a unit,
even when the source is an additional Wave interface's microphone: its
`processing` is always `software`, its `device` is always null, its
`backend` is `software` or null, and `setEqProcessing` on it is refused with
`setEqProcessing: '<id>' is a capture input, whose EQ always runs on this computer`. The stage sits at the head of the input's route, ahead of its
inserts; a capture input has no software low cut or ClipGuard. A capture
input with no active insert plays on its direct feed, so switching its EQ on
moves it to its own route with the stage at the head, as an insert does,
and the microphone is never silent. Switching the EQ off bypasses inside the
stage, which keeps that route until it is next built. Everything else works
as on XLR 1: the crossfades, the native helper's refusal, the restart
policy, `available` and `error`, and the save in `mixer.json` under `eq` by
channel id.

A profile saved now names every microphone's EQ, capture inputs included.
Deleting a capture input drops its EQ from `mixer.json` with the channel.
`setCaptureSource` keeps the EQ. Moving the input to a stereo pair keeps
the values saved but takes the EQ away: the state reports no `eq` and no
stage runs until the input is on one mono port again. The stage is mono,
which is why a stereo pair has none.

Sound Check on a capture input records the source's selected port directly,
so the take is always the raw microphone, and the loop enters ahead of the
EQ stage, so it is heard through the EQ and the inserts and follows every
edit.

#### Where the EQ runs

A microphone's EQ runs on the unit's own DSP when all of these hold, and in
software otherwise:

1. The channel carries a driven unit's input, by the rule `unitInput` follows:
   the primary unit or an additional interface, the hardware strip on its
   pair, or a strip bound to the unit's own node at that input's port.
2. The unit runs the EQ: a Wave XLR MK.2 or XLR Dock MK.2 whose firmware,
   read from block `0x0000` bytes 5 to 7, is 1.0.15 or later, or a Wave XLR
   Pro on 1.0.14 or later, and whose EQ fields decode (on the Pro, both
   inputs'). Its `capabilities.hardwareEq`, and the one in its
   `waveInterfaces` entry, is then true. XLR 1's EQ goes to the Pro's
   input 1 and XLR 2's to its input 2, each with its own switch.
3. The channel's `processing` is `device`, the default. `setEqProcessing`
   sets it.
4. No Sound Check session runs on the channel.
5. The unit has not failed to keep the EQ written to it in this run.

`processing` is the channel's choice, `device` or `software`. `device` is
non-null while the channel carries an input of a unit whose model has a
hardware EQ (the MK.2 family and the Wave XLR Pro). A one-input unit has no
input 2, so on one
XLR 2's `device` is null and its EQ runs in software with no `reason`.
Otherwise `device` is `{unit, input, name, firmware, ready, reason, note,
minimumFirmware}`. `unit` is the instance id `waveInterfaces` gives, `input`
1 or 2, `name` the model, `firmware` the version the unit reports or null,
`minimumFirmware` the first version the model runs it on: `1.0.15` on the
MK.2 family, `1.0.14` on the Pro. `ready` is true while the unit runs the EQ
now. When it is false, `reason` says why in a word a client can explain and
`note` in a sentence: `firmwareOld` (older than `minimumFirmware`; Elgato's updater is
in Wave Link on Windows or macOS), `firmwareUnknown` (the version could not
be read), `unsupportedBlock` (the EQ fields hold values the EQ does not
take; diagnostics show the block) or `failed` (the unit did not keep an EQ
written to it twice; switching the EQ or the choice off and on tries it
again). OpenXLR writes no EQ field to a unit that is not ready.

The values stay the ones above. The unit stores frequency, gain in quarter
decibels and the type as they are, and Q as round(64 log10 q): each of the
126 points of the Q grid has its own byte and comes back to itself, so the
state reports the same numbers on either backend. The unit's filters follow
the same cookbook curves (measured on the XLR Dock MK.2 within about 1 dB
for every type; the Pro's have not been measured yet), so the editor draws
one curve for both.

On the unit, each change is a read of the settings block and a write of the
whole block with the EQ fields changed, the way Wave Link 3.5 writes it. On
the Pro the block holds both inputs, and a write changes only the one
input's EQ fields; no commit write follows, as Wave Link sends none for an
EQ change.
The first change of a drag goes out at once, then at most ten a second, and
the last value always lands, within 100 ms. Nothing is written while phantom
power settles on either input, since the firmware writes the same block
then; the latest value goes out when it ends. Each write is read back by the next poll; a
unit that does not keep it is written once more, then the channel moves to
software with `reason` `failed`.

Moving between the two switches the leaving one off first. Leaving the
unit, OpenXLR clears only the unit's EQ switch and keeps its bands, and
software starts once the unit confirms it, after 1 s at most. Moving to the
unit, OpenXLR switches the software stage off, then writes the unit's EQ. A voice is never
equalised twice; the price is a moment without the EQ, normally tens of
milliseconds. Sound Check's `record` hands the EQ to software the same way
and waits for the unit's confirmation, so the take is the raw microphone and
the loop hears every edit; the unit takes the EQ back when the session ends.

When a unit connects, at daemon start, after a replug or a firmware update,
OpenXLR writes each carried input's EQ to it, or switches the unit's EQ off
on an input whose EQ runs in software, so an EQ Wave Link left on cannot
double OpenXLR's. A unit that already holds it is not written, so a daemon
restart leaves the voice alone; a unit that held another EQ, Wave Link's
from another computer, gets OpenXLR's and the journal says so once. A change
another program makes while OpenXLR runs is written back, at most every
5 s, and said once in the journal. When an input of the primary unit
carries no microphone because its strip is bound to another source,
OpenXLR switches that input's EQ off once per connection. Moving the
binding off the unit does the same, so an EQ Wave Link left on cannot stay
on the unit's own paths. Nothing is written when the daemon stops, when the unit is unplugged, to an
additional interface's input that no microphone carries, or in
hardware-control mode: the unit keeps the EQ it last held.
Another channel taking the same unit's audio, a capture channel on its node,
hears the unit's EQ, as it hears the unit's low cut.

### Ducking

Ducking lowers chosen channels in chosen mixes while a key microphone is
loud. The key itself is never lowered. It is one setup on every device, or
with no device at all, and it needs the software mixer. It runs in
software, as a native stage per ducked mix, or on the Wave XLR Pro's own
processor when the setup fits what the unit can do. The choice of where it
runs is shaped like the [microphone EQ's](#where-the-eq-runs):
`processing` is the user's choice and `backend` is what runs it now.

The state message always carries `ducking`, `{enabled, key, channels,
mixes, sensitivity, amount, speed, processing, backend, available, error,
device}`:

```json
"ducking": {
  "enabled": true,
  "key": "xlr1",
  "channels": ["game", "music", "browser", "system", "voicechat", "sfx"],
  "mixes": ["monitor"],
  "sensitivity": "medium",
  "amount": "high",
  "speed": "fast",
  "processing": "device",
  "backend": "device",
  "available": true,
  "error": null,
  "device": {
    "unit": "0fd9:00b4@3f2a9c1d",
    "name": "Wave XLR Pro",
    "firmware": "1.0.14",
    "ready": true,
    "reason": null,
    "note": null,
    "outputs": ["monitor"],
    "held": true,
    "readback": {"sensitivity": "medium", "amount": "high", "speed": "fast", "pairs": [1], "entries": [4, 5, 7, 8]}
  }
}
```

- `enabled`: the switch.
- `key`: the microphone channel whose level ducks: `xlr1`, `xlr2` or a
  capture input on one mono port (a channel with `microphone` true).
- `channels`: the channel ids to lower, never the key. `mixes`: the mix
  ids they are lowered in. The ducked sends are every channel in
  `channels` times every mix in `mixes`; an empty list in either ducks
  nothing.
- `sensitivity` (`low` -3 dBFS, `medium` -12 dBFS, `high` -24 dBFS; a
  higher one reacts to a softer voice), `amount` (`low` 6 dB, `medium`
  12 dB, `high` 24 dB) and `speed` (`slow` 100 ms down and 2000 ms back,
  `medium` 80 and 1200, `fast` 30 and 200). These are the Wave XLR Pro's
  presets, and software uses the same numbers.
- `processing`: `device` (the default) or `software`.
- `backend`: `device` while the unit runs the setup, on or off, since the
  unit holds the switch; `software` while the software stages exist; null
  while nothing runs it.
- `available` and `error`: the software side. `available` is false when
  the native plugin host is not installed, or after a stage failed three
  times in five minutes, and `error` then says why. A failed stage never
  silences a channel: the ducked sends play at their normal level until it
  is back.
- `device`: null unless the primary driven unit has hardware ducking
  (`capabilities.ducking`). Otherwise `{unit, name, firmware, ready,
  reason, note, outputs, held, readback}`. `unit` is the instance id
  `waveInterfaces` gives, `name` the model and `firmware` the version the
  unit reports, or null when it could not be read. `ready` is true while the unit can run this setup;
  when it is false, `reason` says why in a word and `note` in a sentence
  (see [where ducking runs](#where-ducking-runs)). A client shows `note`
  for a reason it does not know. `outputs` is what the unit would duck for
  this setup, any of `monitor` (the jacks and Line Out, USB playback pair
  1) and `aux` (the USB Aux port, pair 5), given even when it is not
  ready. `held` is true when the unit was last read holding what OpenXLR
  wrote to it. `readback` is that read decoded: `sensitivity`, `amount`
  and `speed` as the unit holds them (`custom` when its entries disagree
  or hold a value no preset writes), `pairs`, the USB playback pairs
  ducked in any of its mix sections (0 to 7, pair n is channels 2n and
  2n+1), and `entries`, the block 0x0008 entries whose ducked flag is set
  (0 to 3 the hardware inputs, 4 to 11 the pairs). Wave Link leaves flags
  on entries 4, 7 and 8 with no ducking at all; OpenXLR keeps them, so
  `entries` reads `[4, 7, 8]` on such a unit with ducking off.

Live reduction is a meter, not state: while software runs, the `meters`
message carries `duck:<mix>` for each ducked mix, the reduction in dB. On
the unit the meters carry nothing for ducking.

`setDucking` takes `value`, an object with any of `enabled`, `key`,
`channels`, `mixes`, `sensitivity`, `amount` and `speed`; a field left out
keeps its value, an unknown one is refused. For example
`{"cmd":"setDucking","value":{"enabled":true,"channels":["music","game"],"mixes":["monitor","stream"],"amount":"high"}}`.
The errors name the field: `setDucking: value must be an object`,
`setDucking: value needs at least one of enabled, key, channels, mixes,
sensitivity, amount and speed`, `setDucking: key must be a microphone channel`,
`setDucking: channels must be distinct channel ids other than the key`,
`setDucking: mixes must be distinct mix ids`, `setDucking: sensitivity
must be low, medium or high`, `setDucking: amount must be low, medium or
high`, `setDucking: speed must be slow, medium or fast`, `setDucking:
enabled must be a boolean`, `setDucking: unknown field '<name>'`.
`setDuckingProcessing` takes `value` `device` or `software`, as
`setEqProcessing` does. The daemon saves the setup in `mixer.json` first
and applies it only once the save succeeded; a failed save leaves the
setup and both backends as they were. Both commands need the software
mixer; in hardware-control mode they are refused with `mixer not built
(start the daemon with --mixer)`, and the state shows the saved setup with
nothing running.

`mixer.json` holds the setup as `ducking`, `{enabled, key, channels, mixes,
sensitivity, amount, speed, processing}`. A file without it takes the
defaults, and the next save writes them: off, key `xlr1`, every
application channel, the mix `monitor`, medium presets and processing
`device`. On load, ids the layout lacks are dropped, a key that is not a
microphone turns ducking off and resets the key to `xlr1`, and a word the
daemon does not know takes its default; each drop is noted in the journal.
Layout edits prune the setup in the same save: a deleted channel leaves
`channels`, a deleted mix leaves `mixes`, and a deleted key turns ducking
off and resets the key to `xlr1`. A new channel is not added. A profile
carries `mixer.ducking` without `processing`. Recalling it replaces the
setup, drops ids the layout lacks and turns ducking off when the key is
missing; a profile without `ducking` leaves the setup alone. A profile
whose `ducking` holds a preset word the daemon does not know is refused
whole, as any other invalid mixer field is.

A key ducks only while it is heard: a key muted in every send it has does
not duck. Sound Check's loop passes through the key channel, so a looped
take ducks the targets in software. On the unit the loop does not key the
ducking, since the unit hears the live microphone.

#### Where ducking runs

The mixer decides after every setup or layout command and on every sweep.
`backend` is `device` when `processing` is `device` and every rule below
passes; otherwise ducking runs in software. A device without hardware
ducking runs it in software, with `device` null and no reason. The first
rule that fails gives `reason`:

| Rule | `reason` | `note`, for example |
|---|---|---|
| the software mixer runs | `mixerOff` | The software mixer is off, so ducking does not run. |
| the key carries the unit's input 1 | `keyNotSupported` | The Wave XLR Pro ducks only with XLR 1 as the key. |
| the unit's ducking is heard on the jacks: no target mix is heard on them while the unit cannot reach them | `monitorReturn` | The Wave XLR Pro ducks inside its own mix, and OpenXLR feeds the jacks around that mix, so this computer ducks. |
| the unit's ducking is known to reach the USB Aux port: no target mix is heard there until it is | `auxNotChecked` | Ducking on the Wave XLR Pro's USB Aux port has not been checked by ear yet, so this computer ducks. |
| every target mix that is heard is heard only on the unit: a monitor mix in the jacks' feed, or the Aux mix on the USB Aux port, and on no other output, virtual microphone or feed | `mixElsewhere` | Monitor A also plays on Speakers, which the Wave XLR Pro cannot duck. |
| when one mix of the jacks' feed is ducked, all of them are | `sharedOutput` | The jacks play Monitor A and Chat together, and only Monitor A is ducked. |
| every channel heard in a target mix (send above 0, not muted) is in `channels`, the key excepted | `partialMix` | Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output. |
| the key is not heard in a target mix through its software send | `keyInMix` | XLR 1 is heard in Monitor A through its software send, so the unit would lower the voice too. |
| the unit kept the ducking written to it (two misses fail it) | `failed` | The Wave XLR Pro did not keep the ducking written to it. Switch ducking off and on to try again. |
| the unit answers a read of its ducking block | `unsupportedBlock` | The Wave XLR Pro did not answer a read of its ducking block. |

On the Wave XLR Pro both of these fail today, so it reports the unit's
side and runs ducking in software: the unit ducks inside its own mix, and
OpenXLR feeds the jacks through the monitor return, which goes around it.
A target mix no output, virtual microphone or feed hears is left out of
these rules; a setup whose target mixes nobody hears runs on the unit with
no pairs ducked. The unit ducks the pairs `outputs` names.

Moving between the two never doubles a duck. From the unit to software,
the unit is written with ducking off, and the software stages start once
it reads back off, or after 1 s at most. From software to the unit, the
stages go first, then the setting is written. Either way there is a moment
without ducking. Switching ducking off keeps the software stages running
at unity until the next graph build, so a Deck key toggles it without
touching the graph.

The daemon writes the unit only after the mixer's first post, so nothing
is written in hardware-control mode or when the daemon stops: the unit
keeps what it holds. Each connect writes the last setting posted for that
unit, or off for a unit not posted to yet, which clears ducking Wave Link
left; the mixer then posts the current one. When a poll finds the unit
changed by another program, OpenXLR writes its own back, at most every
5 s, and says so once in the journal. Every write is logged with the bytes
it changed. While ducking is off on the unit, the presets stay as the unit
holds them and only the ducked flags are cleared. The bytes are in the
[protocol notes](wave-xlr-pro-protocol.md#11-ducking-captures-of-2026-10-21).
`resetDevice` leaves ducking alone.

### Output key targets

Output keys accept exact external sink names and OpenXLR monitor-mix sinks.
Internal channel, post and non-monitor mix sinks retain unity gain and are
rejected. Numeric ids, Pulse aliases, shared-jack `#` names and unavailable
outputs are rejected. `@monitor` is only valid for `setMainOutput` and requires
a selected, available monitor output. A missing target or audio-server failure
returns a command error without changing the enforced default policy.

Relative volume reads the audio server at key-press time. It uses the same
monitor master and linked output synchronization as desktop volume changes;
it never uses the client's previous slider value. Named keys remain bound to
that output across default changes. A key with no device resolves the desktop
default anew on every press. `setOutputDeviceVolume` takes the same targets
and sets the level outright. A mute toggle on one of the selected physical
monitor outputs toggles `monitorMuted` for the whole monitor selection,
preserving mix and channel mutes and recording feeds. On a monitor mix sink
it goes through the existing mix setter, so state and graph updates follow
the same path as the mixer mute control; on any other output it uses
pipewire-pulse's atomic toggle. The daemon pushes state whenever a sink's
volume or mute changes.
