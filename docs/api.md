# WebSocket API

The [versioned local HTTP API](http-api.md) serves the same commands and
uses the same Unix connection.

The daemon serves HTTP and the WebSocket paths `/ws` and `/api/v1/events`
on `$XDG_RUNTIME_DIR/openxlr/control.sock`. Without a runtime directory it
uses `$XDG_CONFIG_HOME/openxlr/control.sock`, defaulting to
`~/.config/openxlr/control.sock`. There is no loopback listener or token
file. The directory is private (0700) and the socket is 0600. Linux
`SO_PEERCRED` must identify the daemon's effective user before Kestrel reads
HTTP; bundled clients also verify the server's user before sending data.
This trusts processes belonging to that user, not other local accounts.
Foreign browser Origins are still refused.

The first server message is state. There is no `auth` command or HTTP
Authorization header. Upgrade the UI, terminal mixer, Deck plugin and
Omarchy plugin together with the daemon; older clients that dial the TCP
port cannot connect.

A client that cannot reach the socket can tell why. A daemon from before
the socket keeps running after an upgrade and serves only
`127.0.0.1:37890`. It still holds `daemon.lock` in the same directory, an
exclusive `flock`. The bundled clients test the lock with a shared,
non-blocking `flock` that they release at once, and the daemon retries its
own lock for 200 ms so a probe never turns a starting daemon away. A held
lock with no socket is checked again after 250 ms, time enough for a
starting daemon to bind its socket. If the lock is free they make a bare TCP connect to the old port and send nothing.
Either sign means a daemon from before the update that must be restarted;
neither means no daemon is running.

A toolkit without Unix WebSocket support can run
`openxlr-control`. This launcher is only a client: it starts no
services and touches no USB or audio configuration. It writes
`{"type":"controlReady"}` once connected, then forwards one JSON message
per line in each direction. Commands are capped at 64 KiB and received
messages at 8 MiB. EOF closes the sending half and drains replies for up
to five seconds. A daemon disconnect exits immediately even while stdin
remains open. Keep stdin open for long-running commands and subscriptions.
When it cannot connect it writes one line in place of `controlReady` and
exits with status 1:

```json
{"type":"controlError","reason":"olderDaemon","message":"An OpenXLR daemon from before the update is still running and cannot accept this client. Restart it: systemctl --user restart openxlr-daemon"}
```

`reason` is `olderDaemon`, `notRunning`, `otherUser` (the socket answered
for another user) or `unavailable` (no reason found). `message` is one
English sentence; under Flatpak it names `openxlr-flatpak` instead. The same
sentence goes to stderr. The Deck plugin shows "restart" on its keys and
dials for `olderDaemon`, and the Omarchy bar shows the sentence while it is
offline.

An installation from before the control socket has no `openxlr-control`.
When the Deck plugin cannot start one, natively or through Flatpak, it makes a
bare connection to the old loopback port 37890 and sends nothing. If a daemon
answers there, OpenXLR is older than the plugin: keys and dials show "update"
and the plugin logs that OpenXLR must be updated to the plugin's release.

The Deck plugin and Omarchy use this adapter and reconnect after it exits.
Deck also stops an adapter that does not signal readiness within 15 seconds,
so a stalled process cannot prevent reconnection.

The dedicated launcher is absent from older releases, so a new client
cannot accidentally start an older daemon that ignores `--control-stdio`.
Deck's Flatpak fallback also selects this launcher. Source installations
must [put it on PATH](install-from-source.md#6-make-it-permanent).

```sh
printf '{"cmd":"getState"}\n' | openxlr-control
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
cannot silently retain stale state. Reconnect to
receive a fresh snapshot; reload any cached plugin catalogue and editor rules.
The UI, terminal mixer and OpenDeck plugin reconnect automatically.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `warning` (one sentence the user should see, or null: the mixer waiting for the audio server, which did not answer when the daemon built the graph (see `mixerWaiting`), mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, a device set aside after three hung USB transfers in one run, a device that does not answer USB reads (named, with the error, until a read succeeds or the unit leaves the bus), hardware input strips left silent because the capture node's name matches two units or because several interfaces are attached and none is driven, a hardware strip bound to a source that is missing, named twice, lacks the selected port or is not linked yet, one sentence per strip with the strip's name in front, or additional interface choices that could not be read or saved). In the mixer state, each channel carries `id`, `name`, `levels` (mix id to send level, 0 to 1), `mutedIn` (the mix ids its send is muted in), `hardware` (true for the fixed input channels), `captureSource` (exact external source name or null), `capturePair` (zero-based pair), `captureMonoChannel` (the one zero-based source port fed to both sides, or null for a pair), `captureConnected` (its direct or chained capture route exists), `microphone` (true for `xlr1`, `xlr2` and a capture input with `captureMonoChannel` set), `chainWidth` (1 for those microphone inputs, 2 for a capture pair, null for Aux In, Line In and application channels; the binding decides this while unplugged too), `binding`, `bindingConnected`, `bindingWarning` and `unitInput` (see [input bindings](#input-bindings)), `present` (whether a client should draw the strip, see [input bindings](#input-bindings); `xlr2`, `aux` and `line` are absent on every model but the Wave XLR Pro, and `line` also needs the Pro's capture source in its 18-channel layout), `exclusiveGroup` (the id of the exclusive group the channel is in, or null), `volume` (the channel's own volume, 0 to 1, which `setChannelVolume` sets; every channel has one, `xlr1`, `xlr2`, `aux` and `line` included; an older daemon reports null for a hardware input), `captureGain` (on a capture input whose source OpenXLR does not drive, `{gainDb, minDb, maxDb, hardware}`, which `setCaptureGain` sets; absent on every other channel, on a capture input that takes a Wave unit's input, while the source is missing, and from an older daemon; see [capture input gain](#capture-input-gain)), and on a microphone channel, `xlr1`, `xlr2` and a capture input whose `microphone` is true, `eq` (see [microphone EQ](#microphone-eq)), `noise` (see [noise removal](#noise-removal)) and `voice` (see [voice changer](#voice-changer)), all three absent on every other channel, a capture input on a stereo pair included, and from an older daemon. An absent channel stays in the list and keeps its levels, so a client leaves it out of its strips rather than forgetting it. A capture channel is editable but cannot receive application assignments; `renamedSinceStart` says a user mix was renamed since the daemon started (its PipeWire device keeps the old name until a restart), and `layoutWarning` is a sentence for the layout editor when pipewire-pulse nears its open-file limit, or null. Every channel and mix also carries `appearance {icon, colour, hidden, defaultIcon}`: the first three as `setLayoutAppearance` set them (`icon` empty, `colour` null and `hidden` false by default), and `defaultIcon`, read-only, the built-in icon name the daemon chose for the item's kind, never empty (see [mixer-layout.md](mixer-layout.md#appearance) for the rule). A client draws `icon` when it is a text glyph or a name it can draw, nothing when it is `none`, and `defaultIcon` otherwise, which covers an empty `icon` and a name only a skin defines. The built-in names, their text characters and their path data are in `plugin/com.emaspa.openxlr.sdPlugin/icons.json`, generated from `src/OpenXLR.Core/Mixing/LayoutIcons.cs`. A client draws a hidden channel's sends and applications as usual and only leaves out its strip; for a hidden mix it leaves out the mix's master and the sends into it from its full mixer, and keeps the mix in output and monitor feed choices. The mixer state's `outputAppearance` maps the headphone outputs `phones1` and `phones2` to `{icon, colour, hidden, defaultIcon}` in the same form, `hidden` always false and `defaultIcon` `headset`; a model with one headphone output still carries both entries. Each mix carries `id`, `name`, `volume`, `muted`, `editable` (true for every monitor mix and virtual microphone, including Monitor A/B; false for Aux), `deletable` (editable and not the last remaining monitor) and `kind` (`monitor`, `virtualMic` or `auxPort`; a monitor mix the user added is `monitor` and editable), which gives a client the mix's volume ceiling. `defaultMonitorMix` names the implicit feed for outputs without an entry in `monitorFeeds`, independent of display order; `outputVolume` is the first selected output's volume, 0 to 1.5, or null with no output selected. In `devices`, every entry that is a sink carries `volume` (desktop scale, 1.0 = 100%) and `muted`; sources and the Wave XLR Pro pseudo-outputs omit both. A source carries `channels` and `positions` when PipeWire reports them, and any entry can carry `drivenUnit` (see below). A state is pushed whenever a sink's volume or mute changes |
| `diagnostics` | in answer to `getDiagnostics` | `blocks`, mapping vendor block names to hex strings or read errors. An XLR Dock adds `paths`, the card number and the path each of its gain, mute and headphone controls takes (`Alsa` through the card's mixer, `Block` through the dock's config block when the card lacks the control, `None` when the USB handle is closed too, so the control is unavailable), and `alsa`, the values read through the card. `usbFault` carries the last USB fault of this run and `error` a dump that failed |
| `meters` | 15 Hz while the mixer is built | `levels`, an object mapping each key below to its values: live stereo levels per channel (`ch:<id>`) and mix (`mix:<id>`), and `duck:<id>` for each mix with a running software [ducking](#ducking) stage: one value, the reduction in dB now (0 while the key is quiet). While a client keeps a [meter before the chain](#meter-before-the-chain) running, `in:<id>` carries that microphone's `[rms, peak, clip]`, and while a [Sound Check](#sound-check) take loops, `take:<id>` carries one value, where the loop is across the take from 0 to 1. While a [soundboard](#soundboard) clip plays, `clip:<id>` carries `[position, voices]`: how far its newest voice is, 0 to 1, and how many voices play it; the key is absent while the clip is silent |
| `soundCheckTake` | once, when the Sound Check helper finishes a take, and in answer to `getSoundCheckTake` | `channel`, `take` (the take's number in the session, from 1), `seconds` (its length) and `peaks` (200 values from 0 to 1, the peak of each stretch of the take in order); without a take `channel` is null, `take` 0 and `peaks` empty. See [Sound Check](#sound-check) |
| `soundboard` | in answer to `getSoundboard` | `soundboard`, the same object as the state's `mixer.soundboard`. See [Soundboard](#soundboard) |
| `clipImport` | in answer to `importClip` | `ok`, `message` (a sentence for the user) and `clip`, the new clip's id, or null when the file was refused. See [Soundboard](#soundboard) |
| `soundboardExport` | in answer to `exportSoundboard` | `ok`, `message` (a sentence for the user), `clips` (how many clips the archive holds) and `skipped` (each clip left out, as `name: reason`). See [Soundboard](#soundboard) |
| `starterSounds` | in answer to `restoreStarterSounds` | `ok` (true when it went through, also when no starter clip was missing), `message`, `clips` (the ids added, in library order) and `skipped` (each clip left out, as `name: reason`). See [Starter sounds](#starter-sounds) |
| `soundboardImport` | in answer to `importSoundboard` | `ok` (true when at least one clip was imported), `message`, `clips` (the ids added, in library order), `renamed` (the archive's id to the new one, for each clip whose id was in use here), `skipped` (each clip left out, as `name: reason`, and on a refusal each clip that could not be taken) and `replaced` (true when the library was replaced). See [Soundboard](#soundboard) |
| `plugins` | in answer to `listPlugins` | the installed LV2, CLAP and VST3 plugins with their controls, within the message size limit above and always including the plugins the saved chains use; `supported` is false, with `unsupportedFeatures` listed, for a plugin that needs a host feature the PipeWire chain lacks. `audioIns` and `audioOuts` are the plugin's own port counts, or for VST3 its main buses' default width; a VST3 entry also carries `widths`, the chain widths in channels (1 and 2 are the ones the host carries) its main buses accepted when the helper asked the way the host asks at load, so a plugin that reports 2 and lists 1 in `widths` can be inserted on a mono input. An entry without `widths` (LV2, CLAP, or a description an older helper wrote) fits a mono input with one port each way and a stereo input or mix with two or more |
| `pluginSetup` | in answer to `getPluginSetup` or `setPluginWineTrace` | where installs go (`lv2Directory`, `clapDirectory`, `vst3Directory`), `hostInstalled`, `wineTrace` (deep tracing enabled in the running daemon), `yabridge` (its version, or null when not installed), `wine`, `windowsDirectories` (the folders yabridge bridges) and `wineFolders` (Wine's own plugin folders that hold a plugin and are not bridged yet, offered as one press since a file dialog hides them), `memoryLockLimitBytes` (the running daemon's soft limit in bytes; -1 means unlimited, null means unknown) and `memoryLockNote` (recovery advice when Windows plugin support is available and the limit is below 256 MiB, otherwise null), `searchDirectories` (every folder searched, see [Plugin search paths](#plugin-search-paths)) and `searchPathWarning` |
| `windowsPluginFiles` | in answer to `getWindowsPluginFiles` | `ok`, `message` and `plugins`; each plugin file carries `path`, `name`, `format`, `enabled`, `canDelete`, `winePrefix` and `inUse`. Excluded files remain listed |
| `nativeEditorRules` | in answer to `getNativeEditorRules` or `setNativeEditorRule` | `rules` with `kind`, `plugin`, `name`, `reason`, `defaultBlocked`, `override` and effective `blocked`; `error` describes a refused change or unreadable configuration |
| `nativeEditorRulesChanged` | after a successful rule change | notification to refresh the catalogue and editor availability; no plugin rescan is needed |
| `browserSession` | in answer to `getBrowserSession` over WebSocket | `session`, a server-generated 32-character hexadecimal id owned by that connection; see [browser tabs](#browser-tabs) |
| `browserTabActivation` | to the browser companion connection that registered the tab, after an accepted `activateBrowserTab` | `tabId` and `site`, the tab's canonical origin; no other client receives it. See [browser tabs](#browser-tabs) |
| `pluginDiagnostics` | in answer to `getPluginDiagnostics` | `discovery`: daemon host/controller paths, Wine prefix, architecture, effective search paths, bounded `yabridgectl status` output and latest completed CLAP/VST3 scan reports. A scan entry that failed carries `logId`, the name of the file holding that attempt's bounded scanner output, or `logNote` saying why there is none; both are absent from an entry that did not fail and from one an older daemon recorded. `scanLogs` gives the `directory` those files are in and the bounds they are kept under (`stderrCapBytes`, `traceStderrCapBytes`, `traceCaptureBytes`, `stdoutCapBytes`, `maxFiles`, `maxTotalBytes`). Reading the reply or the directory scans nothing and starts no process |
| `pluginInstall` | in answer to `installPlugin`, `addPluginSearchPath`, `removePluginSearchPath`, `addWindowsPluginFolder`, `removeWindowsPluginFolder`, `removeWindowsPluginInserts`, `setWindowsPluginEnabled`, `deleteWindowsPlugin`, `syncWindowsPlugins` and `rescanPlugins` | `ok`, `message` (a sentence or two for the user, ending with the bundles the scan that followed could not read, up to three by name and the rest as a count), `installed` (the bundles or folders put in place), `added` (plugins in the catalogue that were not before) and `total` |
| `error` | when a command without a `requestId` is rejected | `message`; for the mixer commands a `state` follows, so an optimistic edit can be reverted |
| `commandResult` | in answer to a command that carried a `requestId` | `requestId`, `error` (null on success); preceded by the state the result refers to |

`daemonBuildId` is the running assembly's module identity (a GUID). Doctor
compares it with the installed assembly to detect a daemon left running
across an upgrade, including two builds with the same version. Older
daemons omit it; clients that do not use it can ignore it.

`daemonBuildVersion` is the running daemon's full assembly informational
version. It includes the source commit after `+` when the build stamped one;
without that metadata it is only the version. `daemonVersion` remains the
release number used for compatibility checks. A source rebuild can keep
the same release number while changing either build field. Older daemons
omit `daemonBuildVersion`; its absence is not evidence of a particular commit.

The daemon builds the device half of a state message from its last good
read of the unit. Its poll loop reads the unit every 100 ms and a command
reads it once after writing. The state message itself never reads USB, so it
does not wait for a transfer in progress. When the daemon reopens a unit after
failed reads and the unit has not answered yet, `state` holds the last values
read from it and `warning` says the unit does not answer.

The state message in full. At the top level: `daemonVersion`, `daemonBuildId`, `daemonBuildVersion`, `warning`,
`connected`, `device` (`vendor`, `model`, `usbId`; when the unit is driven differently from the usual, `note`: today an XLR Dock taking a control through its config block, an XLR Dock MK.2 that answered on its other USB control bank or on neither, or a Wave XLR, Wave:3 or Wave:1 whose ALSA card was not found, so the desktop's volume controls are not kept in step; and `firmware`, the version the unit reports, on a model that reports one, today the Wave:3, the Wave:1, the MK.2 family, the Wave:3 MK.2 (in its own numbering, 0.2.x) and the Wave XLR Pro), `capabilities` (`gain`,
`mute`, `lowCut`, `expander`, `voiceTune`, `hpVolume`, `lowImpedance`,
`crossfade`, `phantom`, `clipGuard`, `compressor`, `outputRouting`,
`auxInput`, `lineInput` (the analog Line In with a gain of its own, separate from the USB Aux input: true on the Wave XLR Pro only), `lineInGainMaxDb` (the highest Line In gain in dB that `lineInGain` takes, from 0: 40 on the Wave XLR Pro, whose unit takes 80 but hisses above about 20; 0 on a model without `lineInput`), `xlrInputs`, `xlrJacks` (false on the Wave:3, Wave:3 MK.2 and Wave:1, whose one input is the microphone's own capsule rather than an XLR jack), `hpOutputs`, `physicalControls`, `retainsSettings`, `hardwareEq` (the unit's DSP runs the [microphone EQ](#microphone-eq) and the driver can write it: true on the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15, on the Wave:3 MK.2 from its firmware 0.2.3 and on the Wave XLR Pro from firmware 1.0.14, false on every other model and on older firmware), `ducking` (the unit can run [ducking](#ducking) on its own processor: true on the Wave XLR Pro only),
`builtInDefaults`, `captureRecovery` (the daemon brings the unit's microphone back by a USB reset when its capture stalls under its own playback, and takes `recoverCapture`, which first links the capture again: true on the Wave:3 MK.2 only), and `gainMaxDb`, the highest gain in dB that `gain` and
`gain2` take, from 0: 80 on the Wave XLR Pro and the MK.2 family, 75 on the
Wave XLR and the first XLR Dock, 40 on the Wave:3, Wave:3 MK.2 and Wave:1; a higher value is held
to it), `state` (the hardware settings: `gainDb`, `mute`,
`lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `hpVolumeDb`,
`hp2VolumeDb`, `hpMute` (read-only: the headphone mute the Wave:3's and Wave:1's
firmware keeps and asserts at the headphone floor, released by a level
written above it; false on every other model), `lowImpedance`,
`crossfade`, `phantom`, `clipGuard`,
`compressor`, their `2` variants for XLR 2, `outHp1`, `outHp2`, `outUsbAux`,
`outLineOut`, `hpMixMonitorReturn` (the Wave XLR Pro's Monitor return in the mix its headphone jacks and Line Out play, true only at 0 dB with the Line In jack's own path and the Aux mix's stream out of that mix, which is how the daemon sets it while a jack is a monitor output), `hpMixMicDirect` (XLR 1 in that mix: the Pro's zero-latency microphone path), `hpMixMicLevelDb` (that path's level in dB, -60 to +12 in half-dB steps; 0 on every other model), `auxReturnEnabled`,
`auxLevelDb`, `auxLevelLock`, `lineInGainDb` (the Line In's gain in dB as the unit reads it back, 0 to `lineInGainMaxDb` once OpenXLR has written it and up to 80 on a unit set higher elsewhere, on a model with `lineInput`; absent on every other model), `lineInLevelLock` (the Line In's level lock, on a model with `lineInput`: while true the unit ignores Line In gain writes through its ALSA control, so the desktop mixer and other applications cannot move the gain, while `lineInGain` still applies; absent on every other model), plus the daemon-stamped `gainLocked`,
`phantomSettling`, `phantomSettling2`, `phantomSettleSeconds` and
`phantomSettleSeconds2`), `ducking` (present whenever the daemon runs, see [ducking](#ducking)),
`mixer`, `mixerWaiting`, `devices`, `profiles`, `activeProfile`, `recallOnConnect` and
`detected` (`usbId`, `name`, `active` for every attached interface; `usbId`
is the unit's instance id, see [additional interface state](#additional-interface-state))
and `waveInterfaces`. The
mixer state carries `mixes`, `channels`, `monitorOutput` (the first selected
output), `monitorOutputs`, `monitorFeeds`, `monitorOutputLabels` (selected
output name to the description it last had in `devices`, so a client can
name an output that is away; an output not seen since it was selected has
no entry, and an entry goes when its output is deselected; an older daemon
does not send it), `outputVolume`,
`monitorMuted` (the selected outputs are paused, independently of mix and
channel mutes; saved in `mixer.json`, never recalled by a profile),
`preserveBrowserOutputs` (leave Chromium-family playback at the output its
browser selects; false by default), `browserTabs` (connected companion tabs,
see [browser tabs](#browser-tabs)),
`monitorMuteMicrophones` (the opt-in microphone pause) and
`microphonePausePending` (a requested microphone pause or resume is not yet confirmed),
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
`soundCheck` (see [Sound Check](#sound-check)), `soundboard` (see
[Soundboard](#soundboard)),
`exclusiveGroups` (`id`, `name`, `channels`; see
[exclusive groups](mixer-layout.md#exclusive-groups)), `enforcedDefaultSink`, `enforcedDefaultSource`, `streams` (`id`, `serial`,
`label`, `identity`, `channelId`, `active`, `running`, and `icon`; see
[application names](#application-names) and
[application icons](#application-icons); a browser held by browser output
selection also carries `browserHeld` and `outputChannelId`, see
[browser tabs](#browser-tabs)), `renamedSinceStart`
and `layoutWarning`. A `devices` entry carries `name`, `description` (a
hardware output's or source's description leaves out the ALSA profile
PipeWire appends, such as "Analog Stereo", unless that would give two
outputs, or two sources, one name), `kind`
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
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `lineInGain`, `lineInLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock`. `lineInLevelLock` is a boolean, and a saved state from before it existed leaves the lock as it is. `lineInGain` is the Line In's gain, an integer in dB held to 0 to `lineInGainMaxDb`, as is a saved state's gain on recall; the gain lock does not cover it, and the daemon logs each write with the value the unit reads back. A control the active device's capabilities do not advertise is refused with an error, as is an XLR Dock control the card lacks while the USB handle is closed |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setMixLatencyCompensation` | `value` | boolean; delay the other mixes to line up with the mix whose inserts take longest (see [plugin latency](#plugin-latency)). Off by default. Either way every insert chain is rebuilt, which briefly interrupts audio |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelVolume` | `channel`, `value` | a channel's own volume, 0 to 1 (values outside are clamped), which scales every send of the channel at once; see [channel volume](#channel-volume). Any channel takes one, `xlr1`, `xlr2`, `aux` and `line` included, where it comes after the interface's gain and leaves the gain alone; an unknown channel is refused |
| `setCaptureGain` | `channel`, `value` | a capture input's gain in dB, the volume of its source; see [capture input gain](#capture-input-gain). Clamped to the channel's `captureGain.minDb` to `maxDb`. Refused on any channel that is not a capture input, on a capture input that takes a Wave unit's input or remembers a unit (its gain is the unit's, set with `set` or `setWaveControl`), on a source of an Elgato Wave interface, XLR Dock or OpenXLR itself, and while the source is missing. Nothing is saved: the audio server keeps the source's volume |
| `createCaptureChannel` | `name`, `source`, optional `capturePair`, `captureMonoChannel` | add an external PipeWire capture input, muted in every mix; exact source name of up to 256 characters, zero-based stereo pair 0 to 31 (default 0). `captureMonoChannel`, 0 to 63 and only with pair 0, takes that one port of the source and feeds it to both sides. A source named `OpenXLR...` or ending in `.monitor` is refused, and so is a source that is not present. Capture inputs count against the 32 application channels. See [capture inputs](mixer-layout.md#capture-inputs) |
| `setCaptureSource` | `channel`, `source`, optional `capturePair`, `captureMonoChannel` | change an existing capture input's source in place. The source and port rules are those of `createCaptureChannel`, but an offline source is accepted. The channel keeps its id, name, send levels, volume, inserts, group and appearance. Its monitor sends are muted and its Sound Check session ends; other sends keep their mutes. A mono/stereo width change is refused while any insert remains, bypassed inserts included. Saves the binding and mutes before acknowledging; a failed save restores them and leaves Sound Check running. The selected source's supported unit is learned, or the old `captureUnit` is cleared when selecting another source; see [capture inputs](mixer-layout.md#capture-inputs). No new state fields. Older daemons reject this command |
| `createChannel` | `name`, optional `icon` | add an application channel, muted in every mix, with `icon` saved in the same write (the values `setLayoutAppearance` takes; absent or empty is the default), without touching existing nodes; its generated stable id is in the next state. Undone with an error when its sends have not appeared within 3 s |
| `renameChannel` | `channel`, `name` | rename an application or capture channel; an application channel's playback device is reloaded under the new name and the streams on it are put back (a short gap on that channel only), a capture channel's label changes without touching its route |
| `deleteChannel` | `channel` | remove an application or capture channel; apps and remembered assignments on it move to the fallback application channel, which is `system`, or when `system` is gone the application channel whose id sorts first. The last application channel cannot be removed |
| `createMix` | `name`, `kind?`, `icon?` | add a user mix, with `icon` as `createChannel` takes it: a virtual microphone (`kind` absent or `"virtualMic"`) or a monitor mix (`"monitor"`), which has no capture device and a desktop-controlled master up to 1.5 like Monitor A and B. Every channel gets a muted send into it, before a virtual microphone's capture device is published. A monitor created during a monitoring pause also starts with its master muted. Both kinds share a limit of 18 editable mixes, Monitor A and B included. Undone with an error when a channel's send has not appeared within 3 s |
| `renameMix` | `mix`, `name` | rename any monitor mix or virtual microphone in OpenXLR; its PipeWire devices keep their old description until the daemon restarts (reloading them would throw the apps using them off), and the mixer state's `renamedSinceStart` says so |
| `deleteMix` | `mix` | remove a monitor mix or virtual microphone with its sends and inserts, and a virtual microphone's capture device; the last monitor cannot be removed. During a monitoring pause only the removed id leaves the listening selection; deleting its last previously open monitor clears `monitorMuted` without opening other muted mixes. With `monitorMuteMicrophones` on, that also resumes XLR 1, XLR 2 and mono capture microphones in every mix once the layout is saved; a failed save keeps the pause and the microphones paused. Outputs fall back to `defaultMonitorMix` if their feed becomes empty; an enforced default sink or source that was the mix is cleared. The Pro's jack selectors and microphone path follow the remaining feed before acknowledgement |
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
| `setMonitorMuted` | `value` | boolean only; pause or resume OpenXLR's feeds to all selected monitor outputs, including the Pro's direct microphone monitoring. Selected physical sinks mirror the pause, preserving their previous native mutes for resume. Raw mix and channel mutes, levels and feeds stay unchanged. Recording stays live unless `monitorMuteMicrophones` is enabled. The state reports `monitorMuted`; saved in `mixer.json`, not in profiles, and profile recall keeps the current pause |
| `toggleMonitorMuted` | none | atomically toggle the same monitor pause. Use this for keys and repeated presses instead of reading `monitorMuted` and sending its inverse; each accepted command toggles once |
| `getBrowserSession` | none | WebSocket only; register this connection as a browser companion and return its `browserSession`. Repeated calls on the same connection return the same id |
| `syncBrowserTabs` | `session`, `tabs` | WebSocket only; replace this companion's complete tab list. Only the connection that obtained the session may publish it; see [browser tabs](#browser-tabs) |
| `assignBrowserTab` | `session`, `tabId`, `site`, `channel`, `remember` | select an application channel or `null` for browser default for a connected tab. Required `site` is the origin the caller saw; an assignment is refused if that tab has navigated to another origin. `remember` must be boolean; true saves the website origin preference, and `channel:null, remember:true` forgets it. False changes only this tab |
| `activateBrowserTab` | `session`, `tabId`, `site` | ask the companion that registered a connected tab to bring it forward and start routing it, as its own Activate does. Refused unless `preserveBrowserOutputs` is on, `site` still matches the tab's origin, the tab has a channel, and it is neither capturing nor reporting an `activation`. Success means the request reached the companion, not that capture started; see [browser tabs](#browser-tabs) |
| `setRouteAppsByDefault` | `value` | boolean only; automatic name rules for apps without an explicit assignment. True by default. False leaves them not managed by OpenXLR; explicit assignments still win. Saved in `mixer.json`, never recalled by a profile |
| `setBlockSize` | `value` | `null`, or 256, 512 or 1024 frames, the sizes `mixer.blockSizes` lists; force the quantum of the graph OpenXLR runs in, or leave it to PipeWire with `null`. Saved in `mixer.json` before it is acknowledged, never recalled by a profile; see [block size](#block-size) |
| `setPreserveBrowserOutputs` | `value` | boolean only; explicitly enable or disable preservation of Chromium-family playback outputs. Saved in `mixer.json`, never recalled by a profile. Registration alone does not enable it |
| `setMonitorMuteMicrophones` | `value` | boolean only, false by default; include XLR 1, XLR 2 and mono capture channels in the monitoring pause, across every OpenXLR mix. Stereo captures, Aux In, Line In and application channels keep playing. Saved only in `mixer.json`; takes effect immediately without restarting the daemon. See [monitoring and microphones](#monitoring-and-microphones) |
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
| `soundCheck` | `channel`, `action` | Sound Check on any channel whose `microphone` is true: `xlr1`, `xlr2` or a mono capture input. Stereo capture inputs, Aux In, Line In, application channels and mixes are refused. `record` starts a session if none runs and records up to ten seconds, replacing an earlier sample; `loop` plays the sample into the channel in place of the microphone, its last 50 ms crossfaded into its start where it starts over, and needs at least a tenth of a second recorded; `live` goes back to the microphone and keeps the sample, and starts a session with no sample when none runs; `stop` ends the session and discards the sample. One session at a time across all microphones: `record`, `loop` and `live` for another channel are refused with the active channel's name, and its `stop` does nothing. Every action but `stop` is refused at once when the native plugin host is not installed. Never saved. See [Sound Check](#sound-check) |
| `setSoundCheckRaw` | `channel`, `value` | `true` plays the session's signal, the sample or the live microphone, straight into the channel past its EQ, software processing and inserts; `false` plays it through them again. The helper crossfades between its two outputs in 20 ms and no chain is built again. Starts a session on the live microphone when none runs. The channel and the host rules are those of `soundCheck`. Never saved; the session's end turns it off |
| `setSoundCheckHearMyself` | `channel`, `value` | `true` opens the channel's send into every monitor mix that plays on a selected monitor output, whatever its mute, for the rest of the session; `false` lets the sends' own mutes decide again. Starts a session on the live microphone when none runs. The channel and the host rules are those of `soundCheck`. Never saved; the session's end turns it off |
| `watchInputMeter` | `channel` | run the [meter before the chain](#meter-before-the-chain) of a channel whose `microphone` is true for the next 15 seconds, starting it when none runs. Send it again to keep the meter running. Refused at once when the native plugin host is not installed. Nothing in the channel's route changes, and nothing is saved |
| `getSoundCheckTake` | none | answered with `soundCheckTake`: the waveform of the take the running session holds |
| `setSoundboard` | `enabled` | `true` starts the soundboard's player and links it into SFX, the application channel with the id `sfx`, creating that channel first, muted in every mix, when the layout has none; `false` stops the player. Saved in `soundboard.json`. `true` is refused at once when the native plugin host is not installed. See [Soundboard](#soundboard) |
| `importClip` | `path`, `name?`, `page?` | copy the file the user picked, an absolute path, into the clip directory, check it in a helper process of its own and add it to the library under an id made from `name`, or from the file's name, on `page`, the default page when it is absent or empty. Answered with `clipImport`. Refused, with a sentence, past 64 clips, for a path that is not a regular file, a file over 64 MiB, a clip over 30 s, a file this build cannot decode, and without the native plugin host. On a WebSocket the import runs beside the connection's later commands. The daemon takes a press or a `releaseClip` sent during the copy and check at once, so its answer can come before the `clipImport`. The `clipImport`, with its state and `commandResult`, follows when the import ends. A connection runs one import at a time, so a second `importClip` waits for the first, and the commands sent after it wait too. Over HTTP the reply waits for the import |
| `updateClip` | `clip`, at least one of `name`, `gain`, `mode`, `loop`, `icon`, `colour`, `page` | change a clip's library entry; an absent field keeps its value. `gain` is 0 to 1, `mode` one of `overlap`, `restart`, `toggle` and `hold`, `icon` as `setLayoutAppearance` takes it, `colour` `#RRGGBB` or empty to clear it, `page` a page name or empty for the default page. `gain` and `loop` reach a playing clip at once. A clip moved to another page goes to the end of the library |
| `renameClipPage` | `page`, `name` | give every clip on `page` (empty for the default page) the page `name`. An empty `name` moves them to the default page, which is how a page is removed; the name of another page puts the clips together on it. The clips keep their places in the library. Refused when no clip is on `page` |
| `exportSoundboard` | `path` | write the library and every clip file into a zip archive at `path`, an absolute path ending in `.zip`. Answered with `soundboardExport`. The archive is built whole before the path is touched, and an archive or a link already there is replaced whole; a folder, a pipe or a device at the path is refused. In a folder where only the chosen file can be written, as a desktop portal grants one, the file is written in place instead, which is not atomic and refuses a link. A clip whose file cannot be read is left out and named. Refused for an empty library and past 512 MiB of clip files. Needs no native plugin host. Runs beside the connection's later commands, as `importClip` does |
| `importSoundboard` | `path`, `merge` | read a soundboard archive at `path`, an absolute path. `merge: true` adds its clips after the ones in the library, with new ids where theirs are in use; `merge: false` replaces the library with them, keeping their ids. Answered with `soundboardImport`. Each clip file is checked by the helper as `importClip` checks a file. A merge leaves out a clip that fails and imports the rest; a replace changes nothing unless every clip can be imported, and its refusal lists the clips in `skipped`. Refused, with nothing changed, when no clip can be imported, for an archive that breaks the rules under [Soundboard](#soundboard), and without the native plugin host. Runs beside the connection's later commands, as `importClip` does |
| `deleteClip` | `clip` | stop the clip, remove it from the library and delete its copy |
| `setClipOrder` | `clips` | every clip id once, in the order clients draw them |
| `playClip` | `clip`, `mode?` | press the clip's key: the clip's own `mode` unless `mode` gives another for this press. A press in `hold` mode belongs to the WebSocket it came on: the clip plays until `releaseClip`, or until that connection closes, unless another open connection holds it too. A hold lasts as long as its voice. Once the clip ends by itself, or a stop, a toggle or a ninth voice ends it, the connection holds nothing, even with its key still down. Over HTTP, which keeps no connection, a `hold` press is refused. Refused while the board cannot play, with the board's `error`, while the clip is not `ready`, and for the moment the player's link into SFX is being made again, with "The soundboard is reconnecting to SFX. Press the clip again in a moment." Never saved; no state of its own beyond the start of the clip |
| `releaseClip` | `clip` | the key came up: this connection's hold on the clip ends, and the clip's `hold` voices end unless another open connection still holds it with a voice that plays. A hold whose voice has ended keeps no other connection's release from ending that connection's voice |
| `stopClip` | `clip` | fade the clip's voices out, its audition included |
| `stopClips` | none | fade every voice out, the audition included; no error while the board is off |
| `auditionClip` | `clip` | play the clip once from its start in the monitor mixes the monitor outputs play, and in no other mix. It never plays in SFX, Stream, Chat or Aux, so the headphones hear it and the stream and a call do not. It replaces the audition playing, of any clip. SFX's fader, mutes and sends do not apply; the clip's own `gain` does, and its `mode` and `loop` do not. It is not one of the eight voices, and no press ends it. Refused like `playClip` while the board cannot play or the clip is not `ready`; with "No monitor output is selected, so an audition would not be heard. Select a monitor output first." while none is; with "Listening is paused, so an audition would not be heard. Resume listening first." while the monitor is paused; with "The monitor outputs play no monitor mix, so an audition would not be heard. Have an output play a monitor mix first." while every selected output plays only virtual microphone mixes; with "The soundboard is reconnecting to the monitor mixes. Audition the clip again in a moment." while its links are made again; and, as a press is, while the player's link into SFX is made again, since the player stands still then. Never saved. See [auditions](#auditions) |
| `stopAudition` | none | fade the audition out; the clips playing in SFX play on. No error while the board is off or nothing is auditioned |
| `restoreStarterSounds` | none | add back each [starter sound](#starter-sounds) the library no longer has, after the clips there, which are kept. Answered with `starterSounds`, whose `message` says when none is missing. A starter clip counts as present by its `starter` id, whatever its name and page are now. Each clip is checked by the helper as `importClip` checks a file, and one that fails is left out and named. Refused, with nothing changed, when the starter sounds are not installed, when no missing clip can be added, while a library export or import runs, and without the native plugin host. Runs beside the connection's later commands, as `importClip` does |
| `getSoundboard` | none | answered with `soundboard`, for a client that wants the board without the whole state |
| `setEq` | `channel`, `eq` | replace a microphone's whole EQ: `eq` is `{enabled, bands}` with 1 to 16 bands, each `{enabled, type, frequency, gain, q}` with every field. `channel` is `xlr1`, `xlr2` or a capture input whose `microphone` is true; any other channel is refused. See [microphone EQ](#microphone-eq) |
| `setEqEnabled` | `channel`, `value` | the EQ's own switch, a boolean. Off bypasses inside the running stage, so on and off compare at once |
| `setEqBand` | `channel`, `band`, at least one of `enabled`, `type`, `frequency`, `gain`, `q` | change part of one existing band, numbered from 1 to 16; an absent field keeps its value. Values outside the ranges are refused, not clamped, and what is taken is snapped onto the EQ's steps. Switching the EQ on, here or through `setEq`, is refused at once when the native plugin host is not installed and no unit runs the channel's EQ |
| `setEqProcessing` | `channel`, `value` | where a microphone's EQ runs when the unit it carries can run it: `device` (the default) on the unit's own DSP, or `software` on this computer. Any other value is refused. Saved in `mixer.json` under `eqProcessing`, never in a profile. Moving between the two switches the leaving one off first. Refused on a capture input, whose EQ always runs on this computer. See [where the EQ runs](#where-the-eq-runs) |
| `setNoiseRemoval` | `channel`, at least one of `enabled`, `engine`, `strength` | a microphone's noise removal: `enabled` a boolean, `engine` `rnnoise` or `deepfilternet`, `strength` the most the noise is turned down, 0 to 100 dB. The channel rules are those of `setEq`. Values outside the range are refused, not clamped. Switching it on is refused at once when the native plugin host is not installed, and an engine the host was built without is refused. See [noise removal](#noise-removal) |
| `setVoiceChanger` | `channel`, at least one of `enabled`, `voice`, `pitch`, `formant` | a microphone's voice changer: `enabled` a boolean, `voice` one of `custom`, `deep`, `high`, `robot`, `radio`, `alien`, `dracula`, `frankenstein`, `mummy`, `werewolf` and `witch`, `pitch` and `formant` the custom voice's shifts, -12 to 12 semitones. The channel rules are those of `setEq`. Values outside the range are refused, not clamped. Switching it on is refused at once when the native plugin host is not installed. See [voice changer](#voice-changer) |
| `adjustOutputVolume` | optional `device`, `value` | change a PipeWire output by desktop percentage points (`0.05` is 5%). Finite steps from -0.5 to 0.5, final volume clamped to 0 through 1.5. Omit `device` for the current desktop default |
| `setOutputDeviceVolume` | optional `device`, `value` | set a PipeWire output's desktop volume; `value` finite, 0 to 1.5 on the desktop scale (1.0 is 100%), rejected outside that range. A monitor mix sink is set through its mix master; a selected monitor output follows the linked monitor-volume behaviour. Omit `device` for the current desktop default; device names are accepted and rejected as for `adjustOutputVolume` |
| `toggleOutputMute` | optional `device` | toggle an output's mute; omit `device` for the current desktop default. On a selected physical monitor output it toggles `monitorMuted` for the whole monitor selection, as pressing a Deck monitor dial does; on a monitor mix sink it toggles only that mix; on any other output it toggles the sink's mute at the audio server |
| `setMainOutput` | `device` | select and enforce an available PipeWire output as the system default, or `@monitor` for the first selected monitor output. Retains capture-default policy and mixer feeds |
| `routeFocusedApp` | `channel` | route the focused application on KDE Plasma, Sway, Hyprland, Niri, X11 or GNOME Shell on Wayland with its optional OpenXLR Focus extension to an application channel and remember the assignment; X11 supports native clients only, since Flatpak window PIDs cannot be mapped to the host from X11 alone; requires the running UI with Desktop keys enabled and `gdbus`, and `xprop` on X11; the Flatpak UI cannot route the focused application. Missing or ambiguous process identity is an error, with no guessed routing. One request runs at a time; another sent while it runs is refused. For a browser held by `preserveBrowserOutputs` it only saves the assignment, as `assignApp` does |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen); `channel: "ignore"` stops managing it, its streams go back to the system default output and stay wherever the desktop routes them. For a browser held by `preserveBrowserOutputs` the command succeeds and saves the assignment without moving any stream; it applies when the option is turned off |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app; `ignore` works here too. A held browser stream is not moved, as with `assignApp` |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setInputBinding` | `channel`, `source?`, `captureMonoChannel?` | bind a hardware strip to one port of a PipeWire capture source by exact node name. `channel` is `xlr1` or `xlr2`; Aux In and Line In stay on the interface. With `source` null or absent the strip returns to the interface's capture pair and takes no port. With a source, `capturePair` must be 0 and `captureMonoChannel` is the zero-based port, 0 to 63, default 0. OpenXLR nodes, monitor taps, control characters and names over 256 characters are refused. A node of an attached supported interface (a `devices` entry with `drivenUnit`) is accepted, and the binding records that unit, see [a strip on another interface](#a-strip-on-another-interface). The source may be offline. Saved before the acknowledgement, rolled back on a failed save. The strip's monitor sends start muted after a change; no other strip changes. Channel ids, software processing and inserts stay as they are. See [input bindings](#input-bindings) |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold; `sink: "@monitor"` follows the first selected monitor output |
| `setActiveDevice` | `device` | switch to another attached interface: one unit by its instance id from `detected`, or the first unit of a model by `vvvv:pppp`. The one command that moves the primary; it wins over `OPENXLR_DEVICE`. The daemon keeps the chosen unit's id in `~/.config/openxlr/active-device.json` and drives that unit again after a restart, waiting up to 10 seconds for it to enumerate before another unit stands in. A stand-in, the model `OPENXLR_DEVICE` names or else the first attached unit, hands the role back once the chosen unit is attached |
| `setWaveInterfaceEnabled` | `device`, `value`, optional `keepChannels` | drive an attached unit, by its instance id from `waveInterfaces`, as an additional interface (`true`) or stop driving it (`false`); at most four. Refused for a unit that is not attached, and for a Wave XLR Pro whose capture node name another attached unit shares. Enabling creates no mixer channel; disabling deletes the unit's capture channels and their chains as `deleteChannel` does, unless `keepChannels` is true. Absent or false keeps deletion; any other type is refused. Channels are found by their saved `captureUnit`, even while unplugged, or by `captureHint` and `captureModelHint` for older entries without a unit. Shared model fragments alone never select another unit's channels. Kept channels retain their binding, sends and chains, and stay silent while the source is missing. Neither value changes the primary, and a unit enabled here is never driven as the primary in place of an absent one; only `setActiveDevice` makes it the primary. See [additional interface state](#additional-interface-state) |
| `setWaveControl` | `device`, `control`, `value` | the `set` command for one unit by instance id: an additional interface, or the primary when `device` is the primary's id. Same control names, capabilities, gain lock and phantom settling as `set`; an error for a unit that is not driven |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the connected interface, or to `generic` while none is connected (see below); loading restores saved gain even while locked and leaves the lock enabled |
| `setRecallOnConnect` | `name` | the profile recalled whenever the active device connects fresh (daemon start, replug, switch to it); empty clears it. With none chosen, a device whose capabilities say `retainsSettings: false` gets the last settings the daemon saw on it instead. Refused while no interface is connected |
| `setDucking` | `value` | change the [ducking](#ducking) setup: an object with any of `enabled`, `key`, `channels`, `mixes`, `sensitivity`, `amount`, `speed`; the fields it leaves out keep their values and an unknown one is refused. Saved in `mixer.json` before it applies. Needs the software mixer, on any device or none |
| `setDuckingProcessing` | `value` | where [ducking](#ducking) should run: `device` (the default) on the unit when the setup fits it, or `software` on this computer. On the Wave XLR Pro `device` runs ducking on the unit when the setup fits it, and in software otherwise, with the reason in `ducking.device.reason` (see [where ducking runs](#ducking)). Saved in `mixer.json`, never in a profile |
| `resetDevice` | none | write the recorded defaults back to a device using connect-time restoration and forget its last settings (an error until the daemon has seen the device connect after a power cycle once); on the Wave XLR Pro, which keeps its own settings, write OpenXLR's baseline instead: gain 30 dB on both inputs, every processing stage and phantom off, headphones and aux level at half, routing, the microphone's level in the jacks' mix and the Line In gain and level lock untouched, refused while the gain lock is on. The capabilities say `builtInDefaults` when a model has a baseline |
| `recoverCapture` | none | bring back the driven unit's microphone. First every link OpenXLR made out of the unit's capture is removed and made again, which leaves the mixes on the unit's own output playing; after 2 seconds the daemon asks the kernel whether the capture runs, and stops there when it does. Only a capture still stopped or missing goes on to the reset: every mix comes off the unit's own output, the unit is reset on USB, and the mixes go back once the reconnected unit's capture runs, or after 30 seconds without it. Only on a unit whose capabilities say `captureRecovery`; an error otherwise, while a recovery is under way, or when the reset fails, in which case the mixes go straight back. The daemon resets the unit by itself, without the relink, when the kernel keeps the capture stopped for 3 seconds while PipeWire runs it and a mix plays into the unit, at most once every 2 minutes and 3 times in 30 minutes |
| `restartDaemon` | none | stop cleanly with exit code 75, so a service manager with `Restart=on-failure` or `Restart=always` starts the daemon again; a daemon run by hand just stops. The Flatpak UI sends it to a daemon running as a user service ([Flatpak](flatpak.md)) |
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

`saveProfile` writes a `mixer.monitorFeeds` entry for every selected output,
including one that follows `defaultMonitorMix`, because the default can change
before the recall: deleting it, or adding a monitor whose id sorts first once
`monitor` is gone. A recall replaces the feed table with the profile's. An
output whose saved mixes are all gone, or that an older profile left without
an entry, follows the default at the time of the recall.

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
of the monitor feed. Recording stays live unless microphone pausing is
explicitly enabled as described below; raw channel and mix mutes stay unchanged.
The previous flags are saved in `mixer.json`. A daemon stop leaves the native
mutes in place and the next start takes them over, including releases still
pending for outputs no longer selected. A stop with the software mixer turned
off in `daemon.json` restores them first, since no later start would.

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

### Monitoring and microphones

`monitorMuteMicrophones` is an explicit opt-in. While it and `monitorMuted`
are true, the channel sinks of XLR 1, XLR 2 and mono capture inputs are
muted, including strips bound to other hardware sources. No send or mix mute
is overwritten. Resuming reveals their current raw choices, including edits
or profile recalls made while paused. Explicitly unmuting a monitor clears
the monitoring pause and resumes those microphones too. So does deleting the
last monitor mix that was open when monitoring was paused (`deleteMix`): the
microphones resume in every mix, Stream and Chat included, while the
remaining monitor mixes keep their individual mutes. Turning the option
off during a pause resumes microphone audio while monitoring stays paused.
After a rebuild, channels follow OpenXLR's current pause state rather than
desktop mute or gain remembered for another channel. On WirePlumber 0.4,
this requires the owned-node compatibility rule shipped by packages built
for that version. Load it by restarting WirePlumber after installation or
logging in again; [source installations](install-from-source.md#4-wireplumber-rules)
copy the rule explicitly. WirePlumber 0.5 honors the node declaration directly.

The channel mute covers all sends, including Stream, Chat, Aux and custom
virtual microphones, without rewriting each send. Application audio, stereo
capture inputs, Aux In and Line In are outside this microphone option. Sound Check
is stopped and its sample discarded on a combined pause; recording, looping
and live actions are refused until it ends. Stop remains available.
A paused microphone that is the [ducking](#ducking) key stops ducking in
software until it resumes. Its channel's `meters` entry keeps showing the
microphone input during the pause, because the meter reads the channel
before the mute.
If restoring the live input after stopping Sound Check fails, the requested
pause and microphone option are still saved. The command reports the cleanup
error; clients read the resulting state rather than assuming the pause reverted.

`microphonePausePending` is true while a requested microphone pause or resume
has not been confirmed by the audio server. Helpers that refuse a write or
report a different state leave the operation pending for the regular sweep;
clients must not present that state as confirmed silence. New microphone
feeds wait for their channel mute to be confirmed on a paused start or
reconnect. EQ edits are kept while a software EQ start waits for that
confirmation. This is an OpenXLR routing control, not a hardware privacy switch:
applications recording the physical microphone directly bypass it, and audio
already buffered or held by an effect can finish playing.

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
`xlr1`, `xlr2`, `aux` or `line`, so they are recalled at 100%.

On XLR 1, XLR 2, Aux In and Line In the volume comes after the interface's
gain, the Aux In level or the Line In gain, and after the input's plugin
chain; `set` and the gain
lock are unchanged. It belongs to the strip, not to its source: an input
binding change keeps it, and a strip bound to another source, which has no
gain of its own, keeps its volume as its level control. The Wave XLR Pro's
direct microphone path (`hpMixMicDirect`, see
[the interface while a strip is bound](#the-interface-while-a-strip-is-bound))
carries the microphone to the jacks inside the interface. The daemon sets
the path's level (`hpMixMicLevelDb`) to what the jacks' feed would play the
microphone at through the software send: the send level, the channel volume
and the feeding mix's volume, on the same cubic scale. The channel volume
reaches the jacks as it reaches every other mix. The channel's software
effects do not, because the path carries the raw microphone.


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
the hardware channels `xlr1`, `xlr2`, `aux` and `line` are refused with
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

Non-finite samples at a native helper's audio input stop that helper under
the existing chain restart budget. Non-finite output silences its block. It
counts only while the input carries sound: half a second of such blocks in
a row stops the helper under the same budget, and blocks on an input of
exact zeros are silenced without counting. The last message names the
input or output in the journal and, if retries are exhausted, in the
insert's `error`. Finite audio above unity is not rejected.
A VST3 or CLAP processing error, including a refused CLAP processing start,
silences that block, and the next block is tried. Half a second of them in
a row stops the helper under the same budget, with the last message
`the plugin could not process audio; restart the chain`.
After a latched audio failure the helper stops reporting audio health even
if its editor has blocked the main loop, so the existing supervisor can
replace it. An editor stall alone does not change audio health.

The OpenDeck plugin in `plugin/`, the terminal mixer
(`src/OpenXLR.Tui/DaemonLink.cs`) and the Omarchy plugin
(`packaging/omarchy/openxlr.mixer/DaemonLink.qml`) are clients of this
API; the command handler is `WebSocketHub.cs` and the message shapes are
in `Protocol.cs`, both under `src/OpenXLR.Daemon/`.

### Capture input gain

A capture input on a source OpenXLR does not drive reports `captureGain`:
the source's volume as pipewire-pulse holds it, in dB. `hardware` is true
when the source sets its volume on the device's own capture control
(pipewire-pulse flags it `HW_VOLUME_CTRL` with a base volume below 0 dB).
The gain then reads as that control does in `amixer`: the source volume in
dB minus the base volume in dB, so the top of the range, `maxDb`, is the
base volume negated. `minDb` is the lowest value of the ALSA capture volume
control whose top matches; without one it is 60 dB under the top. On a
Logitech BRIO the base volume is -54 dB and 'Mic Capture Volume' runs from
+18 to +54 dB: a source volume of 0 dB reads +54 dB and -18 dB reads
+36 dB. Under the control's floor PipeWire attenuates the rest in software,
and the gain reads that too. Without a hardware control `hardware` is
false and the gain is the source's software level, 0 dB being unity,
from `minDb` -60 to `maxDb` +10.

The daemon reads the gain again whenever the registry shows the source's
channel volumes moved, so a change made by another program (a voice chat's
automatic gain control, a browser, a desktop volume panel) is in the next
state within about a second. It never writes such a change back; only
`setCaptureGain` writes the source's volume, on every channel at once. Two
capture inputs on one source report and set the same gain.

`captureGain` is absent on a capture input that carries a Wave unit's input
(`unitInput`) or remembers a unit (`binding.unit`); that channel's gain is
the unit's. It is also absent on a source of an Elgato Wave interface, an
XLR Dock or OpenXLR itself, which OpenXLR never writes, the Wave XLR Pro's
raw source among them.

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
it does not. Aux In, Line In and application channels still report "has no insert
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

### Block size

`mixer.blockSizes` lists the block sizes in frames that `setBlockSize`
takes, smallest first; a daemon without the setting leaves it out.
`mixer.blockSize` is the forced size, absent while PipeWire chooses. The
meters carry the force as `node.force-quantum` on their `parec` streams,
which share the driver of the sinks they read, so it reaches every node in
OpenXLR's graph and holds against other clients' `node.latency` requests.
PipeWire reopens the driver at the new size, a short gap in the audio. A
global `clock.force-quantum` in the settings metadata still wins.

A device driven in IRQ mode, such as a Wave XLR Pro in the pro-audio
profile, keeps the period it was opened with when nothing forces one, and
ignores `node.latency`. When a forced size is cleared, the meters force the
settings' `clock.quantum` (1024 without one) for 2 s and then stop forcing,
so such a device returns to the default instead of staying at the last
forced size.

While nothing is forced, each meter asks for the default quantum
(`clock.quantum` at `clock.rate`, rounded up to whole milliseconds), which
PipeWire rounds back down to the same quantum, so the meters never change
the block size themselves.

### Application names

`mixer.routeAppsByDefault` reports whether automatic name rules apply to
applications without a saved override. The default is true, preserving
existing installations. `setRouteAppsByDefault` saves the new boolean before
acknowledging it; a failed save leaves the old policy and placements intact.
Running unassigned streams follow the new policy on the next sweep. Explicit
channel assignments and explicit `ignore` overrides remain unchanged.
With automatic routing off, `forgetApp` returns an app to `ignore` on its
next registration. Turning automatic routing back on reclassifies silent
apps when they next open a playback stream. The policy does not change
browser tab assignments or disable `preserveBrowserOutputs`.

`ignore` releases playback on an OpenXLR application sink to the desktop's
current default output and otherwise leaves playback where the desktop put
it. A refused or not yet confirmed move is retried on the next sweep.
It does not mute the app or change that default. An OpenXLR default
output can therefore still carry unmanaged playback.

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

Each hardware strip has an input binding. By default XLR 1, XLR 2, Aux In
and Line In take the driven interface's capture pairs 0, 1, 2 and 3. On the
Wave XLR Pro's 18-channel capture source those are AUX0 and AUX1, AUX2 and
AUX3, AUX4 and AUX5 (the USB Aux port) and AUX6 and AUX7 (the analog Line
In); AUX8 to AUX17 carry the unit's own mixes back and no channel takes
them. `setInputBinding` binds XLR 1 or XLR 2 instead to one port of any
PipeWire capture source, by exact node name, and that port feeds both sides
of the strip ahead of the strip's software DSP and insert chain. Aux In and
Line In always stay on the interface.
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
attached, so a desk with no Elgato unit gets one strip. Line In needs the
unit's `lineInput` and also the interface's capture source in the layout
Line In was measured on, 18 channels. On a source with fewer, such as a
six-channel or a stereo profile, it is absent and silent. The daemon takes the
count from the source it last wired in this run.

A microphone strip's `name` follows what feeds it. XLR 1 and XLR 2 keep
those names only while a real XLR jack feeds them: the driven unit's input
on a Wave XLR Pro, a Wave XLR of either model or an XLR Dock, or such a
unit's input that the strip is bound to, which keeps the strip's own name
(a strip bound to an XLR Dock is `XLR 1` or `XLR 2`). A strip bound to any
other source is `Mic 1` or `Mic 2`, and so is the strip on a Wave:3's,
Wave:3 MK.2's or Wave:1's built-in capsule, whose `capabilities.xlrJacks` is false, the
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
Aux In, Line In and a strip still on the interface keep their audio.

The unit's input-stage controls belong to the channel whose `unitInput`
names them (see above). The Wave XLR Pro's direct microphone path
(`hpMixMicDirect`, the zero-latency path from its first input to its jacks)
follows the same channel: XLR 1 while it takes the interface, or the capture
channel on the Pro's own node at its first port while XLR 1 is bound
elsewhere. The daemon turns the path on while every monitor output is a Pro
jack and at least one mix feeding them is unmuted and carries that channel
unmuted. Muting the feeding mix turns the direct path off. For a summed feed,
it stays on while any feeding mix and that channel's send to it are both
unmuted. The path plays at the level that send would have (see
[channel volume](#channel-volume)). The daemon writes that level before it
opens the path and again whenever the send, the channel volume or the mix
volume moves. A level change needs no restart of the playback stream. The daemon mutes that
channel's monitor send in the graph meanwhile so the microphone is not
heard twice; it never uses the path while monitoring is paused or that channel
is in an exclusive group or under Sound Check, nor while software changes the
channel's audio: an insert that runs, the soft low cut or ClipGuard, or its EQ
in software, noise removal or voice changer. The commands that change those
move the path at once. With no channel carrying the Pro's first input
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

## Browser tabs

The Chromium companion captures tabs the user explicitly starts and sends
their audio to the selected OpenXLR application channel inside the browser.
The daemon exchanges metadata and output choices; audio does not pass
through this protocol. Browser process ids and application identities
cannot distinguish tabs that share a browser audio process.

The user enables `preserveBrowserOutputs` before using tab routing. While
enabled, OpenXLR does not move or unmute recognized Chrome, Chromium, Brave,
Edge, Vivaldi and Opera playback streams, including streams already playing
when the mode changes. A browser is recognized by the process binary
PipeWire reports, never by `application.name`: Electron apps such as Discord
call themselves `Chromium` there and keep their usual routing. Browser-wide
`assignApp` and `assignStream` choices remain saved but are suspended until
the mode is disabled. Those commands, and `routeFocusedApp` on a browser
window, still succeed: they save the assignment and move nothing, and the
command result carries no note saying so. Browser audio without a tab choice
plays where the browser or desktop puts it. That is often the Linux
default, but it can be an OpenXLR channel: a stream already on one when the
mode is enabled stays there, and the session manager can restore a new
browser stream to the channel the browser was last routed to. Other
applications and the existing `ignore` behavior keep their usual routing.

While the mode holds a browser, its `streams` entry keeps the saved
assignment in `channelId`, so `knownApps` in `mixer.json` does not change,
and adds `browserHeld:true`. A playing entry also carries `outputChannelId`,
the application channel its stream plays into now, which is absent when it
plays on another output. Turning the mode off drops both fields and moves
the browser's streams to `channelId`.
Each stream sweep refreshes `outputChannelId` from the current target
without moving held streams. Preservation is decided per stream from its
browser binary; `assignApp` still moves non-browser streams that happen to
share its identity, including Electron applications.
When a stream is replaced, its browser flags follow the replacement even
while routing is pending; the previous stream's `outputChannelId` is cleared.

A companion authenticates a WebSocket, calls `getBrowserSession`, then
publishes `syncBrowserTabs` with that session and its full `tabs` array.
Each entry has `id` (a distinct non-negative integer), `site` (an HTTP or
HTTPS origin, at most 512 characters), `title` (at most 160 characters),
`capturing` (boolean), optional `error` (at most 200 characters) and
optional `activation` (`starting`, `popup`, `browser` or null; see below).
Origins cannot contain credentials, a browsing path, query or fragment.
Titles and errors cannot contain control characters. The daemon normalizes
origins to the form a Chromium browser serializes them in, for example
`https://EXAMPLE.com:443/` becomes `https://example.com` and
`http://[::ffff:1.2.3.4]` becomes `http://[::ffff:102:304]`. A host the
daemon cannot parse, such as one holding `*`, `!` or `&`, has no canonical
form, and a list that contains one is refused. The browser's native host
leaves such tabs out of the list it sends, so the other tabs still
register. The normal 64 KiB incoming-frame limit also applies.
At most eight companions and 128 tabs per companion can be registered.

The mixer state's `browserTabs` entries carry `session`, `id`, `site`,
`title`, `channelId`, `remember`, `capturing`, optional `error` and optional
`activation`.
`channelId:null` explicitly means browser default. Title and capture updates
on the same origin preserve the tab's choice. Navigation to another origin
clears its temporary choice and uses the new origin's saved default, if any.
The shared `title` is empty unless the tab is capturing or has a non-null
`channelId`. Clients can label other tabs by `site`; the origin remains
available for selection and navigation checks. The daemon coalesces
title-only changes to the latest value on a one-second timer. An identical
list, including one sent in a different order, causes no state broadcast.
Changes to the tab list, origins, capture, errors or activation publish
immediately.
Deleting a channel moves remembered website defaults to the fallback
application channel; tab-only choices for it return to browser default.
`capturing` and `error` describe the browser's last report,
not an audio-server confirmation. Authenticated controllers can select a
connected tab with `assignBrowserTab`, including the `site` from the tab
state they selected. The daemon compares canonical origins under the same
lock as the assignment: a queued choice for an origin the tab has left is
refused before any runtime or saved preference changes. Equivalent forms
such as `https://EXAMPLE.com:443/` and `https://example.com` match. Only the
owning socket can replace the tab metadata. Closing it removes its runtime
tabs and frees the companion slot. Reconnect, authenticate, get a new session and resync;
there is no heartbeat or persisted session id.

A controller can ask for capture with `activateBrowserTab`. The daemon
checks the tab as it does for `assignBrowserTab` and sends a
`browserTabActivation` message with `tabId` and `site` to the companion's
connection alone, since the session is that connection. The browser lets
the companion capture a tab only after the user invoked the extension on
that tab, by its toolbar button or keyboard shortcut, and that grant ends
when the tab navigates. The companion brings the tab's
window and the tab forward and starts it as its popup's Activate would.
While it works on the request the tab reports `activation:"starting"`. If
the browser still holds the grant, capture starts and `activation` is
cleared. Otherwise the companion opens its popup on the tab where the
browser allows that (`popup`); when it cannot, or a press in that popup
is refused too, the tab reports `browser`, meaning the user has to invoke
the extension on the tab and press Activate there. Any other failure clears
`activation` and sets the tab's `error`. The companion drops a request after
two minutes, on navigation to another origin, when the tab closes or when
`preserveBrowserOutputs` is turned off. A companion that predates this
command ignores the message and never reports `activation`.

With `remember:true`, a non-null channel saves a default for the origin and
applies to other tabs from it that have no tab-only choice. At most 128
origins can be remembered. A `remember:false` choice is a tab-only override,
including a null choice, and keeps the saved website preference intact.
To forget a website, send `channel:null, remember:true`: its saved default
is removed, this tab returns to browser default, and other explicit tab
overrides remain. A successful remembered choice or mode change is saved
before acknowledgement; a failed write keeps the previous choice.

Only origin-to-channel defaults and the preservation flag survive daemon
restarts in `mixer.json`. Titles, errors, capture status, tab ids and session
ids are runtime state. These settings are machine wiring, absent from
profiles. Deleting an application channel removes its website defaults and
returns affected tabs to browser default.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `installation.json`: exact startup ownership and replaced-file metadata
- `installation-backups/<sha256>`: private originals retained for startup restoration
- `installation-history/<sha256>.json`: retained package inventories across upgrades
- `installation.lock`: serializes installation inventory writes
- `mixer.json`: every mixer decision: the layout (`userChannels`,
  `mixes`, with `userMixes` kept for 0.2.0, `appearance`, see [mixer-layout.md](mixer-layout.md)), levels, mutes,
  device choices, the app registry, enforced defaults, `monitorMuted` (the
  monitoring pause, never part of a profile), `monitorMuteMicrophones` (the
  opt-in microphone pause, also never part of a profile), `routeAppsByDefault`,
  `preserveBrowserOutputs`, `blockSize` and
  `browserSiteChannels` (origin-to-channel defaults, all machine settings
  absent from profiles), the software low
  cut, the insert chains, the microphone EQs (`eq`), noise removal
  (`noiseRemoval`), the voice changer (`voiceChanger`) and where the EQs run
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
- `active-device.json`: `{device}`, the instance id of the unit `setActiveDevice` chose last (a model id `vvvv:pppp` for a unit without a USB location), driven again as the primary after a restart. Written by the daemon.
- `gainlock.json`: which devices have the gain lock set
- `soundboard.json`: the [soundboard](#soundboard) library, `{enabled,
  clips}`, each clip `{id, name, file, gain, mode, loop, icon, colour,
  seconds, page, starter}`, `retired`, the ids of deleted clips, which a new clip
  never gets, so a key still bound to one never plays another clip (the
  newest 1024 are kept), and `starterAdded`, true once the
  [starter sounds](#starter-sounds) were added. `file` is a bare name in
  `$XDG_DATA_HOME/openxlr/soundboard/` (default
  `~/.local/share/openxlr/soundboard/`), the clip directory. An entry that
  breaks a rule is dropped with a log line; a file that cannot be read is
  copied to `soundboard.json.corrupt` before the next save. Written by the
  daemon, never part of a profile.
- `$XDG_RUNTIME_DIR/openxlr/control.sock` (or `control.sock` here without a
  runtime directory): the private control socket, 0600, see the
  top of this page
- `$XDG_RUNTIME_DIR/openxlr/daemon.lock`: held by the running daemon for
  the life of the process; a second daemon for the same user finds it
  held and exits at once with code 75, or 73 under Flatpak
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
  hidden draws hidden channels and mixes faded; `mixerScale`, the size of
  the submixer in its own window: `fit`, or a whole percentage from `100`
  to `300` as a string, the picker's `125`, `150`, `175` and `200` or a
  custom size such as `137`; absent, out of range or anything else reads as
  100)
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

`mixer.soundCheck` is `{channel, mode, seconds, error, raw, hearMyself,
heard, monitorLatencyMs, take}`. `channel` is the
microphone channel of the session, including a mono capture input id, null
when there is none. `mode` is what
the helper last did: `recording`, `looping` or `live`, and `idle` without a
session. `seconds` is the length recorded, 0 to 10, and the daemon pushes
state as it grows. `error` says why a session ended by itself. If its native
helper failed with a message, that last message is included in `error` and
written to the daemon's log before the session is discarded. The other
fields describe the running session and are false, null or 0 without one:

- `raw` is true while the session plays the signal past the channel's
  processing (`setSoundCheckRaw`).
- `hearMyself` is true while the session holds the channel's monitor sends
  open (`setSoundCheckHearMyself`).
- `heard` is true while the channel reaches a selected monitor output
  through an open send, with or without Hear myself. It is false while
  monitoring is paused or no monitor output is selected.
- `monitorLatencyMs` is an estimate of what hearing the channel through
  this computer adds over the Wave XLR Pro's direct path: two quanta of
  the audio server at the session's start, the forced quantum when one is
  set and the default one otherwise, plus the latency the channel's inserts
  report, an insert without a figure counted as zero. A client that asks
  for a smaller quantum makes the real figure smaller. Null when the
  settings name no quantum.
- `take` counts the takes the helper has finished in this session, from 1;
  0 before the first. The waveform of the last one comes once in a
  `soundCheckTake` message when the helper finishes it, about 70 ms after
  the recording stops, and again on `getSoundCheckTake`. A client that
  sees a `take` it has no waveform for asks for it.
  Its peaks and duration describe the completed take even if another
  recording starts before the message is published. While the next take's
  meter batch arrives, requests still return the previous completed take;
  the new waveform, duration and take number become visible together.
  Within a session, clients keep the newest take received or requested and
  ignore older replies and duplicates. An idle session or `take: 0` clears
  that tracking for the next session.

The helper has two outputs. The main one feeds the channel's route as
before, through the EQ, the software processing and the inserts. The tap
goes straight into the channel's sink, past all of them. Processed plays
the signal on the main output and silence on the tap; raw does the
opposite, and a switch moves the signal across in a 20 ms linear
crossfade, so the chain keeps running and nothing is built again. While
raw plays, the chain is fed silence, so a reverb or a delay rings out by
itself, and a compressor or gate starts from silence when processed comes
back. The raw path is not delayed to match the inserts' own latency, so
the two paths are that far apart.

Hear myself opens the send at its own level, or at unity when its level is
0, scaled by the channel's volume. Only the monitor mixes that play on a
selected monitor output are opened, or the default monitor mix while no
output is selected; an exclusive group still holds a send that is waiting
for it. On the Wave XLR Pro the hardware path stays off for the channel
carrying its first input while the session runs, as for any session, so
the jacks hear the channel through the software send, with its processing.

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
channel and action 'record', 'loop', 'live' or 'stop'", and an invalid
channel or value for the two switches with "setSoundCheckRaw: need a
microphone channel and a boolean value" or its `setSoundCheckHearMyself`
form. A missing capture
source refuses an action that starts a session with "The microphone is not connected.", the channel's
name and its `bindingWarning`. A source missing the selected port refuses
with "The microphone's capture port is unavailable." `loop` with no session
is refused with "Record a sample first." An older UI has
only its XLR Sound Check views and does not show a capture session.

### Soundboard

The soundboard plays short clips into the SFX channel, the application
channel with the id `sfx`, so the clips take SFX's fader, mutes and sends
into every mix, together with whatever applications play into SFX. A
helper process, the native plugin host's player, holds the clips decoded
in memory and mixes up to eight voices; the daemon links its output into
SFX's sink with a port link of its own. A press is one line to that
helper, and nothing in the graph changes when a clip plays.

`mixer.soundboard` is `{channel, enabled, available, error, formats,
clips, playing, auditioning, pages}`:

- `channel` is `sfx` while that channel exists, and null after it was
  deleted. A client finds the strip whose fader and sends carry the clips
  by this id and draws the channel's own name.
- `enabled` is whether the board is on, as `soundboard.json` says.
- `available` is false when the native plugin host is not installed.
- `error` is null exactly when the board can play, and otherwise says why
  in one sentence: the native plugin host is not installed, which it says
  even while the board is off, SFX was deleted, the player kept failing or did not
  start, the board is off ("The soundboard is off. Turn it on first."), or
  the player is starting. A refused press carries the same sentence.
- `formats` lists what this build decodes, from `wav`, `flac`, `ogg`,
  `opus` and `mp3`. MP3 needs libsndfile 1.1 or newer.
- `clips` holds each clip in library order: `{id, name, seconds, gain,
  mode, loop, icon, colour, loaded, error, playing, status, page}`. `status` is
  one word a client can draw without working anything out: `ready` when a
  press plays it, `loading` while the player loads it, `error` when it
  cannot be loaded, the reason in the clip's `error`, and `off` while the
  board cannot play at all, the reason in the board's `error`. `loaded` is
  true exactly when `status` is `ready`, and `playing` is true while a
  voice plays the clip.
- `playing` lists the ids of the clips playing now. An audition is not
  counted here, nor in a clip's `playing`.
- `auditioning` is the id of the clip auditioned now, or null.
- `pages` lists every page a clip is on, in the order clients draw them as
  tabs: the default page, `""`, first while it holds a clip, then each
  named page where its first clip stands in the library. A clip's `page`
  is `""` for the default page.

A page is a name a clip carries, up to 40 characters with no control
character, other than `-` alone, which the terminal mixer's prompts take
for the default page; spaces around it are dropped. A page exists while a clip is on
it, so there is no command to create or delete one: `importClip` and
`updateClip` take a `page`, and `renameClipPage` renames one or, with an
empty name, moves its clips to the default page. A page named in another
case of a page there already, such as `intro` beside `Intro`, takes that
page's spelling, and so do such pages in the library file and in an
archive, under the spelling of the first clip on them. A clip moved to a page goes to the end of the library,
so it comes last on that page, and a page it starts comes after the
others. `setClipOrder` still names every clip, whatever its page.

A clip starting or ending, by a press or by itself, pushes a state at the
next meter frame, within about 70 ms, and so does an audition. Where a clip is lies in `meters` as
`clip:<id>`, never in the state.

The press modes are `overlap`, the default, which starts another voice;
`restart`, which fades out the clip's voices and starts one; `toggle`, which
fades them out when one plays and otherwise starts one; and `hold`, which
plays until `releaseClip` or until the connection that pressed it closes. A
hold ends with its voice: a key still down after its clip ended by itself,
or after a stop, a toggle or a ninth voice ended it, holds nothing, so when
another connection holds the clip later, that connection's `releaseClip`
ends its voice at once. The daemon numbers each press, and the player
reports the newest press whose hold voice has ended. A press the player has
not taken yet still holds, such as one sent just as the last voice ends. A ninth voice fades out the oldest, of any clip.
Every start and stop fades over 5 ms. A clip with `loop` starts over at its
end until it is stopped; turning `loop` off lets the current pass finish.

The daemon copies clips into `$XDG_DATA_HOME/openxlr/soundboard/` (default
`~/.local/share/openxlr/soundboard/`) as `<id>.<format>`, private to the
user, and the daemon never opens a clip anywhere else. `importClip` follows
a link to a regular file, refuses a pipe, a device or a directory, copies at
most 64 MiB, and hands the copy to `openxlr-lv2-host probe` in a process of
its own before the player sees it, so a file that crashes the decoder never
stops the running player. The refusal is the helper's own sentence, such as
"The clip is 31.0 s long; the limit is 30 s." The limits are 64 clips, 30
seconds a clip, 64 MiB a file, 8 voices and 96 MiB of decoded audio in the
player; a clip past that last limit stays in the library with `loaded`
false and the reason in `error`.

When the player starts, it loads the clips one at a time in library order.
It decodes on a thread of its own, so presses and stops are taken at once
while a clip loads. If it stops on its own, the daemon starts it again and
loads them again. A clip is left out, with a sentence in its `error`, only
when the player crashed while decoding it, or when its load did not answer
within ten seconds; the daemon then starts another player without it. A
player that stops for any other reason three times in five minutes turns
the board off with the reason in `error`. The player stops and starts again
with the audio graph, and when the graph's sample rate changes, since each
clip is converted to the player's rate when it loads; the clips load again
at the new rate. A restart for the rate, or for a load that did not
answer, is not counted as a failure.

While the player is not linked into SFX it does not run, and a press sent
then would wait. The daemon refuses presses while it makes the link again,
after a rename of SFX or a broken link, and ends every voice before the new
link; the player drops a press that waited more than a quarter of a second.

SFX is an ordinary application channel. Renaming it keeps the board's
binding, since the id does not change; hiding it keeps its audio, so the
clips still play. Deleting it stops the player and unlinks it, turns the
board off and leaves `channel` null, with `error` "The soundboard is off
because the SFX channel was deleted. Turn it on again to create SFX, muted
in every mix." The library and the clip files stay. `setSoundboard` with
`enabled: true` then creates an application channel named SFX, whose id is
`sfx`, muted in every mix like every created channel, so its sends have to
be opened before the clips are heard. An application channel that already
has the id `sfx` is used as it is.

The library is machine data, like the input bindings: profiles carry SFX's
sends, mutes and volume as they carry any channel's, and nothing of the
board.

#### Auditions

`auditionClip` lets the user hear a clip before playing it to the stream.
The player has a second stereo output, its audition pair, which the daemon
links into the monitor mixes that the selected monitor outputs play, the
mixes Sound Check's Hear myself also uses. While no monitor output is
selected, or listening is paused, nothing would hear an audition, so the
pair is linked nowhere and `auditionClip` is refused. It is never linked into SFX's sink, a virtual
microphone mix such as Stream or Chat, or the Aux mix, so SFX's sends cannot
carry an audition anywhere. An output whose feed sums a monitor mix with a
virtual microphone mix hears the audition through the monitor mix. An
output that plays only a virtual microphone mix does not hear it. When an
output's feed, the selection of outputs or the pause changes, the links
follow at once. A link the audio server refuses is made again by the next
sweep, and until then `auditionClip` is refused as reconnecting. When a monitor mix is
created or deleted, they follow at the next sweep. The mix's own volume and
mute apply, as for anything the mix plays.

One clip is auditioned at a time. A second `auditionClip` fades the first
out under it. The audition plays the clip once from its start at the clip's
`gain`, whatever its `mode` and `loop`, and ends by itself, with
`stopAudition`, with `stopClip` for that clip, with `stopClips`, or when the
clip is deleted or the player stops. It does not take one of the eight
voices. A press never steals it, and `restart`, `toggle` and `releaseClip`
leave it alone. `auditioning` names the clip while it plays, and `meters`
carries no `clip:<id>` for it.

#### Library export and import

`exportSoundboard` and `importSoundboard` carry the library between
machines as one zip archive: `soundboard.json`, which holds `format` (1)
and `clips`, each entry as the library keeps it, and each clip's file
under `clips/`. The daemon builds the archive as a private file in the
clip directory, then renames it into place, through a private copy beside
the path when the two are on different file systems. A folder where the
daemon may create no file of its own, as a desktop portal grants a sandbox,
takes the archive by a write into the chosen file in place: that write is
not atomic, so a failure part way leaves the file part written, and it
never follows a link or writes into anything but a regular file. Copies
left in the clip directory by a daemon that stopped mid-export are removed
at the next start. A replace keeps the archive's ids, so Deck keys and
desktop keys bound to them play the same clips on the new machine; the
clips that were in the library are deleted with their files, and their ids
retire, as a deleted clip's do. A clip's `starter` id travels with it, so a
starter sound imported this way counts as present. A replace changes nothing unless every
clip in the archive can be imported here, so a build that cannot decode
one of its formats never trades a library for part of another. A merge gives a clip whose id is in use
here, or once belonged to a deleted clip here, the next free id, as an
import does, and names each change in `renamed`. Neither touches
`enabled`.

Before it unpacks a byte, the daemon refuses an archive whole when it is
larger than 512 MiB, when its directory says it holds more than 66 entries
(the library file, a `clips/` folder entry and one file per clip), when
it is split or in the zip64 form, when its entries declare more than
512 MiB in all, when an entry is a link or a special file, when an entry's
name is anything other than `soundboard.json`, `clips/` or `clips/` and a
name the library's `file` rule takes (no separator, no `..`, no control
character, not hidden, a known extension), and when `soundboard.json` is
missing, larger than 4 MiB, not JSON, lists no clips or names a newer
format. In a merge, a clip is left out, and the rest imported, when its entry in
`soundboard.json` breaks the library's rules, when its file is missing
from the archive, empty, over 64 MiB or longer than 30 seconds, when the
helper cannot decode it, when its file is damaged in the archive, and once
the library holds 64 clips; in a replace any of these refuses the import.
The daemon unpacks each file into a private copy in the clip directory,
never more bytes than its entry declares, and the helper checks the copy
before the library names it. An entry whose data is not the size and the
CRC-32 it declares refuses the whole archive: the reader stops at the
declared size, so an entry that holds more shows as a checksum that does
not match. One export or import runs at
a time across the daemon; another is refused with a sentence until it
ends.

#### Starter sounds

Every package installs a set of eighteen short clips beside the daemon,
in `starter-sounds/` next to its executable (`/usr/lib/openxlr/daemon/starter-sounds`
in the distribution packages): applause, a cheer, a laugh, a boo, a
rimshot, a drumroll, a fanfare, a sad trombone, an air horn, a ding, a
wrong-answer buzzer, countdown beeps, crickets, a whoosh, a record
scratch, a cash register, and "You win" and "Game over" lines. The
folder is laid out as an archive is: `soundboard.json` lists the clips,
each with a `starter` id, `clips/` holds their Ogg Vorbis files, and
`CREDITS` names each clip's source, author, licence (all CC0 1.0) and the
edits made by `tools/starter-sounds.py`, which builds them.

The daemon adds the set by itself when it builds the graph and the
library's `starterAdded` is false, as on a new board or a board from
before the starter sounds. It adds each starter clip the library does
not already hold, on the page `Starter`, after the clips there, which are
kept, with ids made unique as a merge makes them, and sets `starterAdded`
in the same save. From then on it never adds them by itself again, so a
starter clip the user deletes stays deleted. When the set is not
installed, or the native plugin host is missing, nothing changes, the
daemon logs why, and `starterAdded` stays false, so a later start tries
again. `restoreStarterSounds` adds back the starter clips the library
lacks, by `starter` id, at any time.

### Meter before the chain

The UI's Mic setup reads a microphone's level before any processing, which
the channel meter cannot show: `ch:<id>` reads the channel's sink, after
its EQ, software processing and inserts. `watchInputMeter` starts a small
native helper linked from the channel's own capture port, the one its
route and Sound Check take, beside the route, which it leaves untouched.
The helper measures and passes nothing on. Its level comes with every
`meters` frame as `in:<id>` and `[rms, peak, clip]`: the RMS over about
300 ms and the sample peak, falling back 20 dB a second, both on the
channel meters' scale (0 at -60 dBFS and below, 1 at 0 dBFS), and `clip`,
1 for a second after a sample reached -0.1 dBFS, else 0. The level is after
the interface's gain and any processing the unit runs itself.

The meter runs for 15 seconds after the last `watchInputMeter` for its
channel, so a client renews it while it shows it and a client that goes
away leaves nothing running. The sweep follows the channel's source, so a
binding or a replug moves the meter with it, and retires a meter whose
helper died; the next request starts a new one. Each microphone has at most
one meter, shared by every client. A rebuilt graph ends every meter.

### Microphone EQ

XLR 1, XLR 2 and every capture input on one mono port each have an EQ with
1 to 16 bands, with the controls, ranges, steps and curves of Wave Link's. A capture
input's always runs in software (see
[EQ on capture inputs](#eq-on-capture-inputs)); the rest of this section
describes XLR 1 and XLR 2. On the Wave XLR MK.2 and the XLR Dock MK.2 from
firmware 1.0.15, on the Wave:3 MK.2 from its own firmware 0.2.3, and on
each of the Wave XLR Pro's two inputs from firmware 1.0.14, the unit's own
DSP runs it (see
[where the EQ runs](#where-the-eq-runs)). Everywhere else it runs in the
native plugin host at the head of the input's route, before the software
low cut, ClipGuard and the inserts, so the channel volume, the sends, the
meters and Sound Check's loop all see the equalised signal; in software the
Wave XLR Pro's zero-latency hardware path and the other units' hardware
monitor mix carry the raw microphone. It adds no latency of its own; noise
removal, which runs first in the same stage, does (see
[noise removal](#noise-removal)). It is off until
switched on.

A channel's `eq` in the mixer state is
`{enabled, bands, backend, available, error, processing, device}`. `bands`
holds 1 to 16 `{enabled, type, frequency, gain, q}` objects:

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
for clients to draw from: `{bands, minBands, maxBands, types, frequency {min, max, step}, gain
{min, max, step}, q {min, max, log10Step}}`.

`bands` remains 4 as the default for older clients; `minBands` and `maxBands`
are 1 and 16. Each channel's `eq.bands` is its current table. A client that
receives no minimum or maximum keeps the older fixed band count. `setEq`
replaces the table, so use it to add or remove a band; following bands are
renumbered after removal. A partial write past the current table is refused.
Profiles and `mixer.json` keep the entire table and its count. The original
four defaults and profiles remain unchanged.

Up to four bands can run on supported hardware: unused hardware slots are
disabled. More than four run wholly in software, leaving the preferred
`processing` choice unchanged; shrinking back restores hardware when it is
available. An enabled extended EQ is refused without the native helper.
Changing the software table's length replaces only the EQ stage. A value
change keeps it and the plugin chain behind it running.

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
   read from block `0x0000` bytes 5 to 7, is 1.0.15 or later, a Wave:3 MK.2
   on its firmware 0.2.3 or later, or a Wave XLR
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
hardware EQ (the MK.2 family, the Wave:3 MK.2 and the Wave XLR Pro). A one-input unit has no
input 2, so on one
XLR 2's `device` is null and its EQ runs in software with no `reason`.
Otherwise `device` is `{unit, input, name, firmware, ready, reason, note,
minimumFirmware}`. `unit` is the instance id `waveInterfaces` gives, `input`
1 or 2, `name` the model, `firmware` the version the unit reports or null,
`minimumFirmware` the first version the model runs it on: `1.0.15` on the
MK.2 family, `0.2.3` on the Wave:3 MK.2, `1.0.14` on the Pro. `ready` is true while the unit runs the EQ
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

### Noise removal

Every channel that carries the [microphone EQ](#microphone-eq) (XLR 1,
XLR 2 and every capture input on one mono port) has noise removal, with a
choice of two engines:

- `rnnoise`: RNNoise 0.2 from Xiph. Light on the processor, best on steady
  noise such as hiss, hum and fans. It adds 30 ms.
- `deepfilternet`: DeepFilterNet 3. Cleaner on keyboards, changing noise and
  some room echo, at a higher processor cost. It adds its own 30 ms, plus
  10 ms, plus one audio quantum or 10 ms, whichever is longer, because it
  runs on a thread of its own and never on the audio thread. That is 51 ms
  at a quantum of 256, 61 ms at 1024 and 83 ms at 2048. The figure rises at
  once when the quantum grows and falls 2 s after it shrinks.

Both run at 48 kHz on mono audio. On a graph running at another rate the
microphone passes through untouched and `error` says why. Noise removal
runs in the native plugin host, first in the same stage as the software EQ,
so the EQ, the software low cut, ClipGuard, the inserts, the channel
volume, the sends, the meters and Sound Check's loop all hear the cleaned
signal. Where the unit's own DSP runs the EQ, the stage runs noise removal
alone. It is off until switched on.

A channel's `noise` in the mixer state is
`{enabled, engine, strength, engines, active, latencyMs, available, error}`:

- `enabled`, `engine` and `strength`: the setting. `strength` is the most
  the noise is turned down, 0 to 100 dB in whole decibels; at 100 the engine
  works unlimited and at 0 the microphone passes through, late by the
  engine's delay. DeepFilterNet takes it as its attenuation limit; RNNoise
  mixes the microphone back in under its output at that level, so the same
  figure means the same thing for both. The defaults are off, `rnnoise` and
  100.
- `engines`: the engines this installation's native plugin host carries,
  from its `engines` command. Every package carries both; a build from
  source without Rust carries only `rnnoise`. Empty without the host.
- `active`: the engine heard now, or null. It lags `engine` while a change
  fades over, and while DeepFilterNet loads its model, which takes a moment
  the first time; the engine that played keeps playing meanwhile.
- `latencyMs`: the delay the running engine adds, or null.
- `available`: false when the native plugin host is not installed, or after
  the stage failed three times in five minutes.
- `error`: why the setting does not run as asked, or null: no host, an
  engine this host was built without, a graph not at 48 kHz, an engine that
  could not start, or DeepFilterNet stopping or not keeping up, which turns
  noise removal off until it is switched off and on again or the other
  engine is chosen.

`setNoiseRemoval` takes the channel and at least one of `enabled`, `engine`
and `strength`; an absent field keeps its value. Values outside the range
are refused, not clamped, and the strength taken is snapped to whole
decibels. Switching it on is refused at once when the native plugin host is
not installed, and choosing an engine the host lacks is refused with
`setNoiseRemoval: this installation's plugin host was built without
DeepFilterNet`.

Switching noise removal on with the EQ off starts the stage and links it in
at the head of the route, as switching the EQ on does, and the microphone is
never silent. Every other change goes to the running stage: the switch, the
engine and the strength. Turning noise removal on or off, or changing the
engine, fades the microphone out over 5 ms and back in over 5 ms, since the
engines' outputs are late by different amounts and cannot be crossfaded;
the strength changes at once. Switched off, the stage stays, with the EQ in
it, until the route is next built.

Noise removal is saved in `mixer.json` under `noiseRemoval`, by channel id,
as `{enabled, engine, strength}`. A microphone absent there has it off. A
profile saved now carries `mixer.noiseRemoval` for every microphone; a
recall replaces the setting of each channel it names, and a profile
without it, as every older one is, leaves noise removal alone. Deleting a
capture input drops its setting with the channel.

### Voice changer

Every channel that carries the [microphone EQ](#microphone-eq) (XLR 1,
XLR 2 and every capture input on one mono port) has a voice changer. It
runs in the native plugin host, last in the same stage as noise removal and
the software EQ, so the EQ shapes the voice before the character goes on.
The software low cut, ClipGuard, the inserts, the channel volume, the sends,
the meters and Sound Check's loop all hear the changed voice. It is off
until switched on.

The voices, by the names the API uses:

| `voice` | What it does |
|---|---|
| `custom` | shifts the pitch and the formant by `pitch` and `formant` |
| `deep` | lower pitch and formant |
| `high` | higher pitch and formant |
| `robot` | ring modulator and a short metallic comb |
| `radio` | a band from 300 Hz to 3.4 kHz with light saturation |
| `alien` | higher pitch, ring modulator and a slow flanger |
| `dracula` | lower pitch and formant in a castle hall |
| `frankenstein` | much lower pitch with a saturated growl |
| `mummy` | slightly lower, raspy, in a long tomb reverb |
| `werewolf` | lower pitch with a rough growl |
| `witch` | higher pitch with a vibrato quiver and reverb |

A channel's `voice` in the mixer state is
`{enabled, voice, pitch, formant, active, available, error}`:

- `enabled`, `voice`, `pitch` and `formant`: the setting. `pitch` and
  `formant` are the custom voice's shifts, -12 to 12 semitones in half
  semitones; the other voices carry their own and ignore them. The defaults
  are off, `deep`, 0 and 0.
- `active`: the voice heard now, or null. It lags `voice` while a change
  crossfades.
- `available`: false when the native plugin host is not installed, or after
  the stage failed three times in five minutes.
- `error`: why the setting does not run as asked, or null: no host, a host
  from before the voice changer, a voice changer that could not start, or
  one that could not keep up.

`setVoiceChanger` takes the channel and at least one of `enabled`, `voice`,
`pitch` and `formant`; an absent field keeps its value, so
`{"cmd":"setVoiceChanger","channel":"xlr1","enabled":true,"voice":"robot"}`
switches XLR 1 to the robot whatever it held, and
`{"cmd":"setVoiceChanger","channel":"xlr1","enabled":false}` switches it off
without reading the state first. Values outside the range are refused, not
clamped, and the shifts taken are snapped to half semitones. Switching it on
is refused at once when the native plugin host is not installed.

Switching the voice changer on with the EQ and noise removal off starts the
stage and links it in at the head of the route, as switching the EQ on does,
and the microphone is never silent. Every other change goes to the running
stage, where changing the voice or switching it on or off crossfades
rather than clicks. Switched off, the stage passes the microphone through
untouched, and stays, with the EQ in it, until the route is next built.

The Wave XLR Pro and the MK.2 units, like every interface with a direct
monitor, send the dry microphone to their own headphone jacks in hardware.
The changed voice is heard there only through a monitor mix that carries the
microphone.

The voice changer is saved in `mixer.json` under `voiceChanger`, by channel
id, as `{enabled, voice, pitch, formant}`. A microphone absent there has it
off. A profile saved now carries `mixer.voiceChanger` for every microphone;
a recall replaces the setting of each channel it names, and a profile
without it, as every older one is, leaves the voice changer alone. Deleting
a capture input drops its setting with the channel.

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
application channel, the default monitor mix (`defaultMonitorMix`, which is
`monitor` while Monitor A exists), medium presets and processing `device`. A
`ducking` object without `mixes`, here or in a profile, takes the default
monitor mix of the layout it is applied to. On load, ids the layout lacks are
dropped, a key that is not a microphone turns ducking off and resets the key
to `xlr1`, and a word the daemon does not know takes its default; each drop is
noted in the journal.
Layout edits prune the setup in the same save: a deleted channel leaves
`channels`, a deleted mix leaves `mixes`, and a deleted key turns ducking
off and resets the key to `xlr1`. A new channel is not added. A profile
carries `mixer.ducking` without `processing`. Recalling it replaces the
setup, drops ids the layout lacks and turns ducking off when the key is
missing; a profile without `ducking` leaves the setup alone. A profile
whose `ducking` holds a preset word the daemon does not know is refused
whole, as any other invalid mixer field is.

A key ducks only while it is heard: a key muted in every send it has does
not duck, and in software neither does a microphone key while
`monitorMuteMicrophones` and `monitorMuted` pause it. Sound Check's loop passes through the key channel, so a looped
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
| the unit's ducking is heard on the jacks: no target mix is heard on them while the last read shows the jacks' feed outside the mix they play | `monitorReturn` | The jacks' feed is not in the Wave XLR Pro's headphone mix, where it ducks, so this computer ducks. |
| the unit's ducking is known to reach the USB Aux port: no target mix is heard there until it is | `auxNotChecked` | Ducking on the Wave XLR Pro's USB Aux port has not been checked by ear yet, so this computer ducks. |
| every target mix that is heard is heard only on the unit: a monitor mix in the jacks' feed, or the Aux mix on the USB Aux port, and on no other output, virtual microphone or feed | `mixElsewhere` | Monitor A also plays on Speakers, which the Wave XLR Pro cannot duck. |
| when one mix of the jacks' feed is ducked, all of them are | `sharedOutput` | The jacks play Monitor A and Chat together, and only Monitor A is ducked. |
| every channel heard in a target mix (send above 0, not muted) is in `channels`, the key excepted | `partialMix` | Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output. |
| the key is not heard in a target mix through its software send | `keyInMix` | XLR 1 is heard in Monitor A through its software send, so the unit would lower the voice too. |
| the unit kept the ducking written to it (two misses fail it) | `failed` | The Wave XLR Pro did not keep the ducking written to it. Switch ducking off and on to try again. |
| the unit answers a read of its ducking block | `unsupportedBlock` | The Wave XLR Pro did not answer a read of its ducking block. |

On the Wave XLR Pro the jacks pass once a poll reads their feed in the
mix they play, which OpenXLR sets whenever a jack is a monitor output.
`monitorReturn` shows for the sweep before that read, or while another
program holds the feed out of the mix. The USB Aux port fails
`auxNotChecked` until it is checked by ear.
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
