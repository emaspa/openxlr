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
`127.0.0.1:37890`. The bundled clients make a bare TCP connect to that port
and send nothing. An answer means a daemon from before the update that must
be restarted. No answer means no daemon is running, or one of this release
is starting or stopping: it holds `daemon.lock` for its whole life but
accepts on the socket only once its mixer and devices are up, which takes
seconds, and stops accepting before it shuts them down. Clients from 0.3.0
to 0.7.1 took the held lock as the sign of an older daemon and named one
during every restart; the daemon still retries its own lock for 200 ms so
their shared probe never turns a starting daemon away.

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
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `history` (the two ends of the [undo history](#undo-and-redo), absent from an older daemon), `cardProfile` (the driven unit's audio card, see [card profile](#card-profile)), `warning` (one sentence the user should see, or null: the mixer waiting for the audio server, which did not answer when the daemon built the graph (see `mixerWaiting`), mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, a device set aside after three hung USB transfers in one run, a device that does not answer USB reads (named, with the error, until a read succeeds or the unit leaves the bus), the driven unit's audio card on a profile without a microphone input that the daemon could not or may not fix (the sentence in `cardProfile.problem`, see [card profile](#card-profile)), a microphone's interface that follows another device's clock (see [audio clock](#audio-clock)), hardware input strips left silent because the capture node's name matches two units or because several interfaces are attached and none is driven, a hardware strip bound to a source that is missing, named twice, lacks the selected port or is not linked yet, one sentence per strip with the strip's name in front, or additional interface choices that could not be read or saved). In the mixer state, each channel carries `id`, `name`, `levels` (mix id to send level, 0 to 1), `mutedIn` (the mix ids its send is muted in), `hardware` (true for the fixed input channels), `captureSource` (exact external source name or null), `capturePair` (zero-based pair), `captureMonoChannel` (the one zero-based source port fed to both sides, or null for a pair), `captureConnected` (its direct or chained capture route exists), `microphone` (true for `xlr1`, `xlr2` and a capture input with `captureMonoChannel` set), `chainWidth` (1 for those microphone inputs, 2 for a capture pair, null for Aux In, Line In and application channels; the binding decides this while unplugged too), `binding`, `bindingConnected`, `bindingWarning` and `unitInput` (see [input bindings](#input-bindings)), `present` (whether a client should draw the strip, see [input bindings](#input-bindings); `xlr2`, `aux` and `line` are absent on every model but the Wave XLR Pro, and `line` also needs the Pro's capture source in its 18-channel layout), `exclusiveGroup` (the id of the exclusive group the channel is in, or null), `volume` (the channel's own volume, 0 to 1, which `setChannelVolume` sets; every channel has one, `xlr1`, `xlr2`, `aux` and `line` included; an older daemon reports null for a hardware input), `captureGain` (on a capture input whose source OpenXLR does not drive, `{gainDb, minDb, maxDb, hardware, muted}`, which `setCaptureGain` and `setCaptureMuted` set; `muted` is the source's own mute, absent from an older daemon; absent on every other channel, on a capture input that takes a Wave unit's input, while the source is missing, and from an older daemon; see [capture input gain](#capture-input-gain)), `ownClock` (on every capture input, whether its device runs on its own clock and what its bridge measures; absent on every other channel and from an older daemon; see [own clock](#own-clock)), on `xlr1`, `xlr2` and every capture input, `eq` (see [microphone EQ](#microphone-eq)), and on a microphone channel, `xlr1`, `xlr2` and a capture input whose `microphone` is true, `noise` (see [noise removal](#noise-removal)), `voice` (see [voice changer](#voice-changer)), `compressor` and `expander` (see [compressor and expander](#compressor-and-expander)). A capture input on a stereo pair carries `eq` and none of the other four. All five are absent on every other channel and from an older daemon, which also leaves `eq` off a stereo pair. An absent channel stays in the list and keeps its levels, so a client leaves it out of its strips rather than forgetting it. A capture channel is editable but cannot receive application assignments; `renamedSinceStart` says a user mix was renamed since the daemon started (its PipeWire device keeps the old name until a restart), and `layoutWarning` is a sentence for the layout editor when pipewire-pulse nears its open-file limit, or null. Every channel and mix also carries `appearance {icon, colour, hidden, defaultIcon}`: the first three as `setLayoutAppearance` set them (`icon` empty, `colour` null and `hidden` false by default), and `defaultIcon`, read-only, the built-in icon name the daemon chose for the item's kind, never empty (see [mixer-layout.md](mixer-layout.md#appearance) for the rule). A client draws `icon` when it is a text glyph or a name it can draw, nothing when it is `none`, and `defaultIcon` otherwise, which covers an empty `icon` and a name only a skin defines. The built-in names, their text characters and their path data are in `plugin/com.emaspa.openxlr.sdPlugin/icons.json`, generated from `src/OpenXLR.Core/Mixing/LayoutIcons.cs`. A client draws a hidden channel's sends and applications as usual and only leaves out its strip; for a hidden mix it leaves out the mix's master and the sends into it from its full mixer, and keeps the mix in output and monitor feed choices. The mixer state's `outputAppearance` maps the headphone outputs `phones1` and `phones2` to `{icon, colour, hidden, defaultIcon}` in the same form, `hidden` always false and `defaultIcon` `headset`; a model with one headphone output still carries both entries. Each mix carries `id`, `name`, `volume`, `muted`, `editable` (true for every monitor mix and virtual microphone, including Monitor A/B; false for Aux), `deletable` (editable and not the last remaining monitor) and `kind` (`monitor`, `virtualMic` or `auxPort`; a monitor mix the user added is `monitor` and editable), which gives a client the mix's volume ceiling. A monitor mix also carries `monitorDry`, whether it hears the microphones [dry](#dry-monitor), absent on every other mix and from an older daemon. Every mix carries `eq`, its [output EQ](#output-eq), absent from an older daemon, and `maximizer`, the Wave XLR Pro's [maximizer](#hardware-mixing) amount on it, 0 (off) to 100, absent from an older daemon. `defaultMonitorMix` names the implicit feed for outputs without an entry in `monitorFeeds`, independent of display order; `outputVolume` is the first selected output's volume, 0 to 1.5, or null with no output selected. `clock` describes the [audio clock](#audio-clock). `hardwareMixing` describes [hardware mixing](#hardware-mixing); while it runs, each channel on a USB pair carries `hardwarePair` and each mix a jack plays carries `hardwareSection`. In `devices`, every entry that is a sink carries `volume` (desktop scale, 1.0 = 100%) and `muted`; sources and the Wave XLR Pro pseudo-outputs omit both. A source carries `channels` and `positions` when PipeWire reports them, and any entry can carry `drivenUnit` (see below). A state is pushed whenever a sink's volume or mute changes |
| `mixingPlan` | in answer to `setMixing` with `check` | what [hardware mixing](#hardware-mixing) would do with the layout as it stands: `wanted`, `active`, `reason`, `note`, `pairs`, `sections`, `jacks`, `cells` (section start to bytes 0 to 13 as hex, the membership's bit 12 the maximizer's bypass), `clamped`, `disabled` and `entryOffsetsDb`. Nothing changes |
| `diagnostics` | in answer to `getDiagnostics` | `blocks`, mapping vendor block names to hex strings or read errors. An XLR Dock adds `paths`, the card number and the path each of its gain, mute and headphone controls takes (`Alsa` through the card's mixer, `Block` through the dock's config block when the card lacks the control, `None` when the USB handle is closed too, so the control is unavailable), and `alsa`, the values read through the card. A Wave XLR Pro adds its five hardware mixes decoded from block 0x0001: `section0`, `section18`, `section36`, `section54` and `section72` (each member with its level, the pairs the section ducks, the raw ducking fields, the maximizer's amount and whether it is bypassed, and for sections 18 to 72 the level and mute bytes; section 0 shows its mute byte, 99, raw), `outputs` (the section each jack, the USB Aux port and Line Out plays), `section0Level` (byte 98), `xlr1Capture` and `xlr2Capture` (bytes 94 and 95, whether each microphone's capture is taken before or after the unit's processing), `usbAuxCapture` (byte 96, which showed no effect on USB Aux's capture) and `lineInCapture` (byte 97: as it arrives, after the insert return, after the ducker, or silent); a block of another layout gives `sections` saying why it was not decoded instead. It adds its meters decoded from block 0x0002, each line naming only what moves, levels in dBFS and `-` for silence: `meterDucking` (each ducker that lowers its source or hears its key: the source, the reduction, the key level and its output left and right), `meterLevels` (each of the 35 level points with a level: each input before the unit's processing, after it, after the insert return and after the ducker, each USB pair as it arrives and after its ducker, and the five mix sections), `meterOutputs` (Headphones 1, Headphones 2, Line Out and the USB Aux port), `meterMaximizers` and `meterDynamics` (the expanders, compressors and Voice Tune, read only for diagnostics); a block of another length gives `meters` saying why. A Wave XLR MK.2 on firmware 1.0.15 or later adds `meterBlock` and `statusBlock`, its blocks 0x0002 and 0x0006 as hex, with `meterLevels` (the input, headphones, USB, processed, after-insert and monitor mix levels, left/right, `-` for silence), `meterDynamics` (the expander's and compressor's reduction, key level and output) and `status` (whether a microphone and headphones are plugged in, the microphone detection value, whether the unit is storing its settings, and the sample rate); a block of another length gives `meters` or `status` saying why (see [hardware support](hardware-support.md#mk2-meter-status)). adds `usbCapture`, block 0x0001 byte 4 decoded: whether its capture is taken before or after the unit's processing. `usbFault` carries the last USB fault of this run and `error` a dump that failed |
| `runtimeDiagnostics` | in answer to `getRuntimeDiagnostics` | the daemon build version and ID, and the [runtime snapshot](#runtime-snapshot) |
| `timeline` | in answer to `getTimeline` | the daemon's [event timeline](#event-timeline): `now`, `since`, `capacity`, `spanSeconds`, `windowSeconds`, `overflowed`, `events` and `markers` |
| `meters` | 15 Hz while the mixer is built | `levels`, an object mapping each key below to its values: live stereo levels per channel (`ch:<id>`) and mix (`mix:<id>`), `[left, right]` from 0 at -60 dBFS and below to 1 at 0 dBFS (a mix the Wave XLR Pro builds in [hardware mixing](#hardware-mixing) reads the unit's own meter), and `duck:<id>` for each mix with a running software [ducking](#ducking) stage, or ducked by a Wave XLR Pro that holds the setup: one value, the reduction in dB now (0 while the key is quiet). `max:<id>` carries the reduction in dB of the Wave XLR Pro's maximizer on a mix the unit builds alone in [hardware mixing](#hardware-mixing), while it runs (an amount above 0) and the unit's meters are read. While a client keeps a [meter before the chain](#meter-before-the-chain) running, `in:<id>` carries that microphone's `[rms, peak, clip]`, and while a [Sound Check](#sound-check) take loops, `take:<id>` carries one value, where the loop is across the take from 0 to 1. While a [soundboard](#soundboard) clip plays, `clip:<id>` carries `[position, voices]`: how far its newest voice is, 0 to 1, and how many voices play it; the key is absent while the clip is silent. While a client keeps a microphone's [compressor and expander](#compressor-and-expander) levels coming with `watchDynamics` and its stage runs, or a unit that reports them runs them, `exp:<id>` and `comp:<id>` each carry `[input, reduction, output]` in dB |
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
`auxInput`, `lineInput` (the analog Line In with a gain of its own, separate from the USB Aux input: true on the Wave XLR Pro only), `lineInGainMaxDb` (the highest Line In gain in dB that `lineInGain` takes, from 0: 40 on the Wave XLR Pro, whose unit takes 80 but hisses above about 20; 0 on a model without `lineInput`), `xlrInputs`, `xlrJacks` (false on the Wave:3, Wave:3 MK.2 and Wave:1, whose one input is the microphone's own capsule rather than an XLR jack), `hpOutputs`, `physicalControls`, `retainsSettings`, `hardwareEq` (the unit's DSP runs the [microphone EQ](#microphone-eq) and the driver can write it: true on the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15, on the Wave:3 MK.2 from its firmware 0.2.3 and on the Wave XLR Pro from firmware 1.0.14, false on every other model and on older firmware), `hardwareDynamics` (the unit's DSP runs the microphone's [compressor and expander](#where-the-compressor-and-expander-run) and the driver can write their settings: true where `hardwareEq` is true and the unit's compressor and expander fields hold values it takes), `dynamicsMeters` (the unit reports the levels of the compressor and expander it runs, which then reach `exp:<id>` and `comp:<id>`: true on the Wave XLR MK.2 from firmware 1.0.15 where `hardwareDynamics` is true, false on every other model; the XLR Dock MK.2 and the Wave:3 MK.2 have the same meter block, not yet checked), `hardwareGainLock` (the unit has a gain lock of its own, which the daemon holds with the gain lock: true on the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15, false on every other model, on older firmware and on a unit whose version was not read), `ducking` (the unit can run [ducking](#ducking) on its own processor: true on the Wave XLR Pro only), `rawCapture` (the unit can send the computer its inputs before its own processing, see [raw capture](#raw-capture): true on the Wave XLR Pro from firmware 1.0.14 and the Wave XLR MK.2 from firmware 1.0.15, false on every other model, the XLR Dock MK.2 and the Wave:3 MK.2 included, on older firmware and on a unit whose version was not read), `hardwareMixing` (the unit can build the mixes its outputs play, see [hardware mixing](#hardware-mixing): true on the Wave XLR Pro only),
`builtInDefaults`, `ledRing` (the unit's LED ring can be set with [`setLed`](#led-ring): true on the Wave XLR MK.2 from firmware 1.0.15 once its LED block has answered in full at connect, false on every other model, the XLR Dock MK.2 and the Wave:3 MK.2 among them, on older firmware and on a unit whose version was not read), `captureRecovery` (the daemon brings the unit's microphone back by a USB reset when its capture stalls under its own playback, and takes `recoverCapture`, which first links the capture again: true on the Wave:3 MK.2 only), and `gainMaxDb`, the highest gain in dB that `gain` and
`gain2` take, from 0: 80 on the Wave XLR Pro and the MK.2 family, 75 on the
Wave XLR and the first XLR Dock, 40 on the Wave:3, Wave:3 MK.2 and Wave:1; a higher value is held
to it), `state` (the hardware settings: `gainDb`, `mute`,
`lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `hpVolumeDb`,
`hp2VolumeDb`, `hpMute` (read-only: the headphone mute the Wave:3's and Wave:1's
firmware keeps and asserts at the headphone floor, released by a level
written above it; false on every other model), `lowImpedance`,
`crossfade`, `phantom`, `clipGuard`,
`compressor`, their `2` variants for XLR 2, `outHp1`, `outHp2`, `outUsbAux`,
`outLineOut`, `hpMixMonitorReturn` (the Wave XLR Pro's Monitor return in the mix its headphone jacks and Line Out play, true only at 0 dB with the Line In jack's own path and every other USB playback pair, the Aux mix's stream among them, out of that mix, which is how the daemon sets it while a jack is a monitor output), `hpMixMicDirect` (XLR 1 in that mix: the Pro's zero-latency microphone path), `hpMixMicLevelDb` (that path's level in dB, -60 to +12 in half-dB steps; 0 on every other model), `hpMixMic2Direct` and `hpMixMic2LevelDb` (the same for XLR 2), `auxReturnEnabled`,
`auxLevelDb`, `auxLevelLock`, `lineInGainDb` (the Line In's gain in dB as the unit reads it back, 0 to `lineInGainMaxDb` once OpenXLR has written it and up to 80 on a unit set higher elsewhere, on a model with `lineInput`; absent on every other model), `lineInLevelLock` (the Line In's level lock, on a model with `lineInput`: while true the unit ignores Line In gain writes through its ALSA control, so the desktop mixer and other applications cannot move the gain, while `lineInGain` still applies; absent on every other model), `clipGuardAndMixRead` (true on the original Wave XLR, whose `clipGuard` and `crossfade` are read from its config block; absent on every other model and in a saved state or profile from before OpenXLR read them, whose recall leaves the unit's ClipGuard and crossfade as they are), `unitGainLock` (the unit's own gain lock as the unit reads it back, block 0x0004 byte 2 bit 0x04, on a model with `hardwareGainLock`: while true the unit ignores gain written through its ALSA control, so the desktop mixer and WirePlumber's restore cannot move the gain, while `gain` still applies; the daemon sets it while `gainLocked` is true and clears it while it is false, at every connect too; absent on every other model and left out of saved settings and profiles), `micConnected` and `headphonesConnected` (read only: whether a microphone is plugged into the unit's XLR jack and headphones into its headphone jack, block 0x0006 byte 0 bits 0 and 1, read with each poll on the Wave XLR MK.2 from firmware 1.0.15; `rawCapture`, `rawCapture2` and `lineInRawCapture` (XLR 1, XLR 2 and Line In sent to the computer before the unit's processing, as the unit reads it back, on a model with `rawCapture` and the input, see [raw capture](#raw-capture); absent on every other model, on older firmware and after a read of the block failed, model and input and left out of saved settings and profiles), plus the daemon-stamped `gainLocked`,
`phantomSettling`, `phantomSettling2`, `phantomSettleSeconds` and
`phantomSettleSeconds2`, and `led`, the unit's LEDs as it reads them back on a model with `ledRing`, see [LED ring](#led-ring); absent on every other model and left out of saved settings and profiles), `ducking` (present whenever the daemon runs, see [ducking](#ducking)),
`mixer`, `mixerWaiting`, `devices`, `profiles`, `activeProfile`, `recallOnConnect`,
`eqProfiles` and `eqProfileProblem` (see [EQ profiles](#eq-profiles)) and
`detected` (`usbId`, `name`, `active` for every attached interface; `usbId`
is the unit's instance id, see [additional interface state](#additional-interface-state))
and `waveInterfaces`. The
mixer state carries `mixes`, `channels`, `monitorOutput` (the first selected
output), `monitorOutputs`, `monitorFeeds`, `monitorOutputLabels` (selected
output name to the description it last had in `devices`, so a client can
name an output that is away; an output not seen since it was selected has
no entry, and an entry goes when its output is deselected; an older daemon
does not send it), `outputVolume`, `outputVolumeLock` (null when off, otherwise
`{device, volume}`, with the exact PipeWire sink name and a desktop level
from 0 to 1.5, plus `channelVolumes` when the levels differ by channel),
`outputVolumeLockSuspended` (hardware mixing holds the locked sink's jacks,
so the lock waits), `monitorVolumeLocked` (the first selected output is
locked, so `setOutputVolume` is refused and a client disables its shared
volume control; an older daemon sends none of the three), `monitorMuted` (the selected outputs are paused, independently of mix and
channel mutes; saved in `mixer.json`, never recalled by a profile),
`preserveBrowserOutputs` (leave Chromium-family playback at the output its
browser selects; false by default), `browserTabs` (connected companion tabs,
see [browser tabs](#browser-tabs)),
`monitorMuteMicrophones` (the opt-in microphone pause) and
`microphonePausePending` (a requested microphone pause or resume is not yet confirmed),
`softLowCutApplicable` and `softClipGuardApplicable` (true when XLR 1's
signal has no hardware low cut or ClipGuard in front of it: XLR 1 is bound
to a source, or the driven unit lacks the control, so `lowCutHz` and
`softClipGuard` act on it; `softLowCutApplicable` is also true while the
unit sends XLR 1 to the computer before its low cut, with
[raw capture](#raw-capture)), `auxPortEnabled`, `lowCutHz`, `softClipGuard`, `softClipGuardAvailable`,
`softClipGuardError`, `compensateMixLatency`, `mixDelayMilliseconds`,
`mixLatencyError` (see [plugin latency](#plugin-latency)), `eqLimits` (see
[microphone EQ](#microphone-eq)), `inserts`
(chains by insert key, `xlr1`, `xlr2`, a capture channel id or `mix:<id>`; each entry carries
`insert`, `error`, `meters`, `nativeHostRunning`, `nativeUiBlocked`,
`nativeUiBlockReason`, `filterChainError`, `latencyMilliseconds` and
`measuredLatencyMilliseconds`),
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
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `lineInGain`, `lineInLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`), the software `gainLock`, and `rawCapture`, `rawCapture2` and `lineInRawCapture`, booleans the daemon holds per unit (see [raw capture](#raw-capture)). On a model with `hardwareGainLock`, `gainLock` also sets or clears the unit's own lock first, and an error writing it leaves both locks as they were. `lineInLevelLock` is a boolean, and a saved state from before it existed leaves the lock as it is. `lineInGain` is the Line In's gain, an integer in dB held to 0 to `lineInGainMaxDb`, as is a saved state's gain on recall; the gain lock does not cover it, and the daemon logs each write with the value the unit reads back. A control the active device's capabilities do not advertise is refused with an error, as is an XLR Dock control the card lacks while the USB handle is closed. While the mixer runs a unit's [compressor and expander](#where-the-compressor-and-expander-run), `compressor`, `expander` and their `2` variants are left out of its capabilities and refused: they are the microphone's, set through `setCompressor` and `setExpander`. A recalled profile's values for them are skipped then |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setMixLatencyCompensation` | `value` | boolean; delay the other mixes to line up with the mix whose inserts take longest (see [plugin latency](#plugin-latency)). Off by default. Either way every insert chain is rebuilt, which briefly interrupts audio |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelVolume` | `channel`, `value` | a channel's own volume, 0 to 1 (values outside are clamped), which scales every send of the channel at once; see [channel volume](#channel-volume). Any channel takes one, `xlr1`, `xlr2`, `aux` and `line` included, where it comes after the interface's gain and leaves the gain alone; an unknown channel is refused |
| `setCaptureGain` | `channel`, `value` | a capture input's gain in dB, the volume of its source; see [capture input gain](#capture-input-gain). Clamped to the channel's `captureGain.minDb` to `maxDb`. Refused on any channel that is not a capture input, on a capture input that takes a Wave unit's input or remembers a unit (its gain is the unit's, set with `set` or `setWaveControl`), on a source of an Elgato Wave interface, XLR Dock or OpenXLR itself, and while the source is missing. Not in `mixer.json`: the audio server keeps the source's volume. A profile keeps it ([capture input gain](#capture-input-gain)) |
| `setCaptureMuted` | `channel`, `value` | boolean only; mute or unmute a capture input's source, for every program that records it, as a desktop sound panel does; see [capture input gain](#capture-input-gain). The state reports it as `captureGain.muted`. Refused where `setCaptureGain` is. Not in `mixer.json`: the audio server keeps the source's mute. A profile keeps it |
| `toggleCaptureMuted` | `channel` | atomically flip the same source mute from what the audio server reports at that moment. Use this for keys and repeated presses instead of reading `captureGain.muted` and sending its inverse; each accepted command flips once. Refused where `setCaptureGain` is |
| `setCaptureOwnClock` | `channel`, `value` | boolean only; put a capture input's device on its own clock, bridged into the mixer, or with `false` back on the mixer's clock; see [own clock](#own-clock). The state reports it as the channel's `ownClock`. Saved in `mixer.json` with the binding before the graph changes; a refused save changes nothing. A profile keeps it too, as `mixer.captureOwnClock`. Ends a Sound Check session on the channel. Refused on any channel that is not a capture input. Older daemons reject this command |
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
| `undo` | optional `step` | put back the values the newest step of the [undo history](#undo-and-redo) changed. With `step`, the id from `history.undo.step`, the command runs only while the newest step has that id |
| `redo` | optional `step` | write again the values the newest undone step put back, under the same rule, with the id from `history.redo.step` |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mixes feed, at most 16; a newly listed output is fed by `defaultMonitorMix`. A missing sink stays selected and saved, and is linked when it appears. A client that lists outputs for the user keeps the missing ones in the list it sends |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setMonitorFeed` | `device`, `mix` | what feeds one selected output, independently of other outputs: any existing mix id, including `stream`, `chat`, `auxout` and user mixes of either kind, or distinct ids joined with `+` to sum them at unity. One mix can appear in several outputs' feeds. An empty feed is rejected; remove an output from `setMonitorOutputs` to disconnect it. Each of the Wave XLR Pro's jacks (`<sink>#hp1`, `#hp2`, `#lineout`) takes its own feed, and a `device` that ends at `#` sets every selected jack of that unit. Each distinct feed the jacks play has a mix of its own on the unit, in the order Headphones 1, Headphones 2, Line Out: the first streams on USB playback pair 1 into section 0 of block 0x0001, as all jacks did before, the second on pair 2 into section 18 and the third on pair 3 into section 54. Jacks that play the same feed share it. Sections 18 and 54 are kept in `mixer.json` as they were before their first use and put back once no jack plays them (see [mixer-layout.md](mixer-layout.md)). The state's `monitorFeeds` lists exceptions from `defaultMonitorMix` (`monitor` when present, otherwise the lowest monitor id), independent of layout order. Unknown or repeated mix ids and unselected outputs are rejected. Deleting the last included mix returns that output to `defaultMonitorMix` |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setMixing` | `value`, optional `check` | experimental: where the mixes the Wave XLR Pro's outputs play are built, `software` (the default) or `hardware`, switched live; see [hardware mixing](#hardware-mixing). With `check` true nothing changes and the answer is a `mixingPlan` message. `hardware` is refused while the layout has more channels than USB pairs |
| `setOutputVolume` | `value` | volume of the selected monitor devices, 0 to 1.5; the range the devices themselves take, so a desktop level above unity can be held and written back unchanged. Values outside it are clamped, and the state reports what reached the devices. While the first selected output is locked (`monitorVolumeLocked`) the command is refused with the error `output volume is locked in Options, Audio`, the same one `adjustOutputVolume` and `setOutputDeviceVolume` give for a locked device, and nothing moves. A locked output further down the selection is skipped and the others take the value. A profile recall is never refused for the lock: it leaves a locked output at its level. With no output selected the command succeeds and changes nothing |
| `setOutputVolumeLock` | `value` | boolean only. True holds the current level of one output: the enforced default output (`enforcedDefaultSink`, with `@monitor` meaning the first selected monitor output), or the desktop default when none is enforced. When that is an OpenXLR sink, the first selected monitor output is held instead. False releases the lock. The lock stays with its exact device name, survives restart in `mixer.json`, and is independent of profiles and mute. A change from the desktop is put back on a mixer sweep. `adjustOutputVolume` and `setOutputDeviceVolume` report an error for the locked device, and `setOutputVolume` does while it is the first selected output. The Wave XLR Pro's sink cannot be locked while hardware mixing holds its jacks. A lock already on it waits from the moment the mode is taken, a restart in the mode included, until the mode is left; the held level is then written before the computer's mixes reach the jacks again |
| `setMonitorMuted` | `value` | boolean only; pause or resume OpenXLR's feeds to all selected monitor outputs, including the Pro's direct microphone monitoring. Selected physical sinks mirror the pause, preserving their previous native mutes for resume. Raw mix and channel mutes, levels and feeds stay unchanged. Recording stays live unless `monitorMuteMicrophones` is enabled. The state reports `monitorMuted`; saved in `mixer.json`, not in profiles, and profile recall keeps the current pause |
| `toggleMonitorMuted` | none | atomically toggle the same monitor pause. Use this for keys and repeated presses instead of reading `monitorMuted` and sending its inverse; each accepted command toggles once |
| `getBrowserSession` | none | WebSocket only; register this connection as a browser companion and return its `browserSession`. Repeated calls on the same connection return the same id |
| `syncBrowserTabs` | `session`, `tabs` | WebSocket only; replace this companion's complete tab list. Only the connection that obtained the session may publish it; see [browser tabs](#browser-tabs) |
| `setAppVolume` | `identity`, `value` | set a known application's playback volume from 0 to 1 on the desktop percentage scale, or clear it with `null`. Take the identity from `mixer.streams`; the level is saved under the key `assignApp` uses. The level is written to the application's playing streams before the command is acknowledged, up to eight of them and for a quarter of a second at most; the daemon writes the rest, and repeats a write the sound server refused, within the next sweeps, about a second each. Each stream the application starts later gets the level when it appears. `null` puts the playing streams back to 100% the same way. Saved in `mixer.json` with the next write, independent of routing and profiles. It is the stream volume the desktop shows, so a change made there or by the application replaces the saved level. The browser application level is shared by its playback streams, including captured tabs; tab levels apply separately |
| `setBrowserTabVolume` | `session`, `tabId`, `site`, `value`, `remember` | volume from 0 to 1 for a tab whose companion reports `volumeSupported`. The origin must still match the tab. `remember:true` stores a site default independently of routing; `false` overrides only this tab until navigation or closure. `value:null` clears the specified override |
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
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2`, a capture channel id or `mix:<id>`, each insert is `{id, kind, plugin, label?, bypass?, ownBlock?, params?}` where `kind` is `"lv2"` with the plugin URI, `"clap"` with the plugin's id, or `"vst3"` with the class id as 32 hex digits; a CLAP or VST3 insert always runs in the native host, so its `nativeHost` reads true whatever was sent. An insert being added is refused when its plugin cannot run at the chain's width (`chainWidth` on an input, two on a mix, by `widths` or the port counts as `plugins` describes them); an insert already in the chain, the same plugin under the same id, is left to the chain builder, so one can always be removed; an id kept while its `kind` or `plugin` changes counts as an addition. Only this chain is rebuilt; every other chain keeps its running plugins |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert; rebuilds this chain only |
| `setInsertOwnBlock` | `channel`, `insertId`, `value` | `true` runs one insert's plugin [on its own block](#own-block), `false` back in PipeWire's cycle; saved with the insert as `ownBlock`. Rebuilds this chain only, which starts that insert's host again in the new mode. Refused when the insert is not in the chain, when `value` is not a boolean, and, for `true`, when the native plugin host is not installed |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by the catalogue's `symbol` (LV2 port symbol or decimal CLAP/VST3 parameter id); use catalogue ranges and scale points. Refused when the insert is not in the chain or the catalogue does not declare the symbol for its plugin |
| `renameInsert` | `channel`, `insertId`, `name` | change one insert's label; `name` is 1 to 256 characters with no control characters. The running plugin, its parameters and the audio path stay as they are. Refused when the insert is not in the chain |
| `getNativeEditorRules` | none | read release defaults and explicit user overrides for native editor compatibility |
| `setNativeEditorRule` | `kind`, `plugin`, `name?`, `blocked?` | set `blocked:true` to use OpenXLR controls, `false` to allow the native editor, or null/absent to remove the override and follow release defaults. Saved atomically before success; answered with `nativeEditorRules` |
| `showInsertUi` | `channel`, `insertId` | open an enabled insert's native editor when the optional host is installed and the editor policy allows it; a blocked editor is refused without changing the audio instance |
| `soundCheck` | `channel`, `action` | Sound Check on any channel whose `microphone` is true: `xlr1`, `xlr2` or a mono capture input. Stereo capture inputs, Aux In, Line In, application channels and mixes are refused. `record` starts a session if none runs and records up to ten seconds, replacing an earlier sample; `loop` plays the sample into the channel in place of the microphone, its last 50 ms crossfaded into its start where it starts over, and needs at least a tenth of a second recorded; `live` goes back to the microphone and keeps the sample, and starts a session with no sample when none runs; `stop` ends the session and discards the sample. One session at a time across all microphones: `record`, `loop` and `live` for another channel are refused with the active channel's name, and its `stop` does nothing. Every action but `stop` is refused at once when the native plugin host is not installed. In [hardware mixing](#hardware-mixing) a session on XLR 1 or XLR 2 plays the take back on the microphone's return pair, and is refused with its `soundCheck:<id>` sentence while no pair is left for it. Never saved. See [Sound Check](#sound-check) |
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
| `auditionClip` | `clip` | play the clip once from its start in the monitor mixes the monitor outputs play, and in no other mix. It never plays in SFX, Stream, Chat or Aux, so the headphones hear it and the stream and a call do not. It replaces the audition playing, of any clip. SFX's fader, mutes and sends do not apply; the clip's own `gain` does, and its `mode` and `loop` do not. It is not one of the eight voices, and no press ends it. Refused like `playClip` while the board cannot play or the clip is not `ready`; with "No monitor output is selected, so an audition would not be heard. Select a monitor output first." while none is; with "Listening is paused, so an audition would not be heard. Resume listening first." while the monitor is paused; with "The monitor outputs play no monitor mix, so an audition would not be heard. Have an output play a monitor mix first." while every selected output plays only virtual microphone mixes; with "The soundboard is reconnecting to the monitor mixes. Audition the clip again in a moment." while its links are made again; and, as a press is, while the player's link into SFX is made again, since the player stands still then. In [hardware mixing](#hardware-mixing) it plays on the cue pair into the jacks' sections, and is refused with the `audition` sentence while no pair is left for the cue. Never saved. See [auditions](#auditions) |
| `stopAudition` | none | fade the audition out; the clips playing in SFX play on. No error while the board is off or nothing is auditioned |
| `restoreStarterSounds` | none | add back each [starter sound](#starter-sounds) the library no longer has, after the clips there, which are kept. Answered with `starterSounds`, whose `message` says when none is missing. A starter clip counts as present by its `starter` id, whatever its name and page are now. Each clip is checked by the helper as `importClip` checks a file, and one that fails is left out and named. Refused, with nothing changed, when the starter sounds are not installed, when no missing clip can be added, while a library export or import runs, and without the native plugin host. Runs beside the connection's later commands, as `importClip` does |
| `getSoundboard` | none | answered with `soundboard`, for a client that wants the board without the whole state |
| `setEq` | `channel` or `mix`, `eq` | replace a microphone's or a mix's whole EQ: `eq` is `{enabled, bands}` with 1 to 16 bands, each `{enabled, type, frequency, gain, q}` with every field. `channel` is `xlr1`, `xlr2` or any capture input, on one mono port or a stereo pair; any other channel is refused with `'<id>' is not a microphone or capture input`. `mix` is any mix's id, for its [output EQ](#output-eq), and takes the place of `channel`: a command with both, or with neither, is refused. See [microphone EQ](#microphone-eq) |
| `setEqEnabled` | `channel` or `mix`, `value` | the EQ's own switch, a boolean. Off bypasses inside the running stage, so on and off compare at once |
| `setEqBand` | `channel` or `mix`, `band`, at least one of `enabled`, `type`, `frequency`, `gain`, `q` | change part of one existing band, numbered from 1 to 16; an absent field keeps its value. Values outside the ranges are refused, not clamped, and what is taken is snapped onto the EQ's steps. Switching the EQ on, here or through `setEq`, is refused at once when the native plugin host is not installed and no unit runs the channel's EQ (a mix's EQ always needs the host). While [hardware mixing](#hardware-mixing) runs, the three are refused on a mix the unit builds |
| `setEqProcessing` | `channel`, `value` | where a microphone's EQ runs when the unit it carries can run it: `device` (the default) on the unit's own DSP, or `software` on this computer. Any other value is refused. Saved in `mixer.json` under `eqProcessing`, never in a profile. Moving between the two switches the leaving one off first. Refused on a capture input, whose EQ always runs on this computer, and with `mix`, since a mix's EQ always runs on this computer too. See [where the EQ runs](#where-the-eq-runs) |
| `loadEqProfile` | `channel` or `mix`, `profile` | put an [EQ profile](#eq-profiles)'s bands on a microphone or a mix and switch its EQ on, as one `setEq` with the profile's bands and `enabled` true, checked and refused as that `setEq` is. `profile` is an id from `eqProfiles`: `builtin:` and a key, or `saved:` and a saved profile's name, matched whatever its case. The channel and mix rules are those of `setEq`. Refused for a profile the daemon does not list, and for one with a band count the EQ does not take |
| `saveEqProfile` | `name`, `bands`, optional `replace` | save bands under a name as a saved EQ profile: `bands` is 1 to 16 bands as `setEq` takes them, refused, not clamped, outside the ranges and snapped onto the EQ's steps. A name already saved, compared without case, is refused unless `replace` is true, which puts the new bands in its place. Names have 1 to 80 characters and no control characters; spaces around one are dropped. At most 64 saved profiles. Refused, with the file unchanged, while the file holds an entry that does not read |
| `renameEqProfile` | `name`, `newName` | give a saved EQ profile another name; a change of case alone is a rename too. Refused for a name that is not saved, onto another saved profile's name, and while the file holds an entry that does not read. A built-in profile has no name to rename |
| `deleteEqProfile` | `name` | delete a saved EQ profile. Refused for a name that is not saved and while the file holds an entry that does not read |
| `listEqProfiles` | none | read `eq-profiles.json` again, for an edit by hand, and send the state: to every client when the list changed, else to the one that asked |
| `setNoiseRemoval` | `channel`, at least one of `enabled`, `engine`, `strength` | a microphone's noise removal: `enabled` a boolean, `engine` `rnnoise` or `deepfilternet`, `strength` the most the noise is turned down, 0 to 100 dB. The channel rules are those of `setEq`. Values outside the range are refused, not clamped. Switching it on is refused at once when the native plugin host is not installed, and an engine the host was built without is refused. See [noise removal](#noise-removal) |
| `setVoiceChanger` | `channel`, at least one of `enabled`, `voice`, `pitch`, `formant` | a microphone's voice changer: `enabled` a boolean, `voice` one of `custom`, `deep`, `high`, `robot`, `radio`, `alien`, `dracula`, `frankenstein`, `mummy`, `werewolf` and `witch`, `pitch` and `formant` the custom voice's shifts, -12 to 12 semitones. The channel rules are those of `setEq`. Values outside the range are refused, not clamped. Switching it on is refused at once when the native plugin host is not installed. See [voice changer](#voice-changer) |
| `setCompressor` | `channel`, at least one of `enabled`, `threshold`, `ratio`, `attack`, `release`, `knee`, `makeup` | a microphone's compressor, with the ranges in [compressor and expander](#compressor-and-expander). The channel rules are those of `setEq`. Values outside a range are refused, not clamped. On a unit that runs it, the unit takes it rounded and held to its own limits (see [where they run](#where-the-compressor-and-expander-run)). Switching it on is refused at once when the native plugin host is not installed and no unit runs the channel's compressor |
| `setExpander` | `channel`, at least one of `enabled`, `threshold`, `ratio`, `range`, `attack`, `release`, `hold` | a microphone's expander, as `setCompressor` takes the compressor |
| `setDynamicsProcessing` | `channel`, `value` | where a microphone's compressor and expander run when the unit it carries can run them: `device` (the default) on the unit's own DSP, or `software` on this computer. One choice covers both. Any other value is refused. Saved in `mixer.json` under `dynamicsProcessing`, never in a profile. Moving between the two switches the leaving one off first. Refused on a capture input, whose compressor and expander always run on this computer. See [where they run](#where-the-compressor-and-expander-run) |
| `setMonitorDry` | `mix`, `value` | `value` true has a monitor mix hear every microphone before its software effects, earlier, while every other mix, the other monitor mixes included, keeps the processed voice; false hears them through their effects again. `mix` must be a monitor mix and `value` a boolean. It needs no native plugin host. Saved in `mixer.json` under `dryMonitors` and in profiles. See [dry monitor](#dry-monitor) |
| `setMixMaximizer` | `mix`, `value` | the Wave XLR Pro's maximizer on a mix, `value` a whole number from 0 (off) to 100. Taken on any mix of the layout in either mixing mode; it runs only while [hardware mixing](#hardware-mixing) builds the mix alone in a section, and reads back as the mix's `maximizer` in the state. Saved in `mixer.json` under `mixMaximizer`, never in a profile. See [hardware mixing](#hardware-mixing) |
| `watchDynamics` | `channel` | send the [compressor and expander](#compressor-and-expander) levels of a channel whose `microphone` is true for the next 15 seconds, as `exp:<id>` and `comp:<id>` in `meters`. Send it again to keep them coming. They come only while the microphone's stage runs, or while a unit that reports them (`capabilities.dynamicsMeters`) runs them; asking starts nothing and changes nothing in the route. Refused at once when the native plugin host is not installed, unless the unit runs the channel's compressor and expander |
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
| `setDucking` | `value` | change the [ducking](#ducking) setup: an object with any of `enabled`, `keys` (or `key`, one key, as older clients send it), `channels`, `mixes`, `sensitivity`, `amount`, `speed`; the fields it leaves out keep their values and an unknown one is refused. Saved in `mixer.json` before it applies. Needs the software mixer, on any device or none |
| `setDuckingProcessing` | `value` | where [ducking](#ducking) should run: `device` (the default) on the unit when the setup fits it, or `software` on this computer. On the Wave XLR Pro `device` runs ducking on the unit when the setup fits it, and in software otherwise, with the reason in `ducking.device.reason` (see [where ducking runs](#ducking)). Saved in `mixer.json`, never in a profile |
| `setLed` | `control`, `value` | set the unit's LED ring on a unit with `ledRing`: `ringBrightness` or `meterBrightness` with a whole percentage from 0 to 100, `gainColour`, `headphoneColour` or `mixColour` with a colour as `#rrggbb`, or `reset` with no value for the factory LEDs. The daemon keeps what was set per unit and writes it back at every connect; see [LED ring](#led-ring) |
| `resetDevice` | none | write the recorded defaults back to a device using connect-time restoration and forget its last settings (an error until the daemon has seen the device connect after a power cycle once); on the Wave XLR Pro, which keeps its own settings, write OpenXLR's baseline instead: gain 30 dB on both inputs, every processing stage and phantom off, headphones and aux level at half, routing, the microphone's level in the jacks' mix and the Line In gain and level lock untouched, refused while the gain lock is on. The capabilities say `builtInDefaults` when a model has a baseline |
| `recoverCapture` | none | bring back the driven unit's microphone. First every link OpenXLR made out of the unit's capture is removed and made again, which leaves the mixes on the unit's own output playing; after 2 seconds the daemon asks the kernel whether the capture runs, and stops there when it does. Only a capture still stopped or missing goes on to the reset: every mix comes off the unit's own output, the unit is reset on USB, and the mixes go back once the reconnected unit's capture runs, or after 30 seconds without it. Only on a unit whose capabilities say `captureRecovery`; an error otherwise, while a recovery is under way, or when the reset fails, in which case the mixes go straight back. The daemon resets the unit by itself, without the relink, when the kernel keeps the capture stopped for 3 seconds while PipeWire runs it and a mix plays into the unit, at most once every 2 minutes and 3 times in 30 minutes |
| `setAudioClock` | `value` | make one ALSA device the audio clock by its node name, or with `null` or no `value` go back to Automatic. The node must be one of `mixer.clockChoice.options` or the one already chosen; a name other than `alsa_input.` or `alsa_output.` followed by letters, digits, `_`, `-` and `.` is refused. Writes `~/.config/wireplumber/wireplumber.conf.d/57-openxlr-clock.conf`, or for Automatic deletes it, and restarts nothing; the rule applies when WirePlumber next starts. A file of that name OpenXLR did not write is neither replaced nor deleted. Refused in the Flatpak. See [audio clock](#audio-clock) |
| `restartAudioServices` | none | restart WirePlumber (`systemctl --user restart wireplumber.service`) so the audio clock rule applies. PipeWire and pipewire-pulse keep running. Every device stops for a moment while WirePlumber makes its nodes again, and the daemon links its graph to them without restarting itself. One at a time; a failed `systemctl` is the error. Refused in the Flatpak |
| `restartDaemon` | none | stop cleanly with exit code 75, so a service manager with `Restart=on-failure` or `Restart=always` starts the daemon again; a daemon run by hand just stops. The Flatpak UI sends it to a daemon running as a user service ([Flatpak](flatpak.md)) |
| `getDiagnostics` | none | vendor block dump for bug reports |
| `getRuntimeDiagnostics` | none | reply `runtimeDiagnostics` with `daemonBuildVersion`, `daemonBuildId` and a numerical `snapshot` from the daemon that answers. See [runtime snapshot](#runtime-snapshot) |
| `markGlitch` | optional `label` | mark a glitch the user heard: put a marker on the [event timeline](#event-timeline) with the time now and the entries of the 30 seconds before it, and write one line to the daemon's journal, `the user marked a glitch`, with the label in brackets when one was given. `label` says where it was marked, 1 to 32 characters without control characters: the UI sends `UI`, or `Desktop key` from its [glitch key](manual.md#glitch-key), the OpenDeck plugin `Deck` and the terminal mixer `terminal mixer`. Works with the mixer off and with no interface connected |
| `getTimeline` | none | reply `timeline` with the [event timeline](#event-timeline) and the glitch markers. A read; nothing changes |

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
A paused microphone that is a [ducking](#ducking) key stops ducking in
software until it resumes; another key still ducks. Its channel's `meters` entry keeps showing the
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
gain of its own, keeps its volume as its level control. Each of the Wave XLR
Pro's direct microphone paths (`hpMixMicDirect` and `hpMixMic2Direct`, see
[the interface while a strip is bound](#the-interface-while-a-strip-is-bound))
carries its microphone to the jacks inside the interface. The daemon sets
the path's level (`hpMixMicLevelDb`, `hpMixMic2LevelDb`) to what the jacks'
feed would play the microphone at through the software send: the send level,
the channel volume and the feeding mix's volume, on the same cubic scale. The
channel volume reaches the jacks as it reaches every other mix. The path
cannot carry the channel's software effects, so the daemon keeps it off while
one runs and the send carries the processed microphone instead.


Application identities use playback-node metadata, falling back to the
owning PipeWire client's application name and process binary when absent.
Windows executable names normalize to the same key as their Wine/Proton
client, for example `Balatro.exe` becomes `balatro`. A binary or
application name with a space in it normalizes the same way, so
`Music Player` becomes `musicplayer` in a playing stream as in a saved
assignment. A WebKitGTK or WPE
application plays from the shared `WebKitWebProcess` or `WPEWebProcess`
binary, so its identity comes from its application name; an override or
remembered app saved under the shared binary moves to that name on load. A stream's
`label` takes the spelling of an installed desktop entry whose name matches
it but for letter case. `assignApp` and
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

<a name="own-block"></a>
Any insert definition optionally carries `ownBlock: true`, set with
`setInsertOwnBlock` or in a `setInserts` chain. The native host then calls
the plugin on a worker thread with 1024 frames at a time, apart from
PipeWire's cycle, for a plugin that cannot finish a short cycle in time;
the node and its ports stay as they were. Missing or false keeps the plugin
in the cycle. Only the native host can do this, so an LV2 insert with it
runs there whatever its `nativeHost` says, and `nativeHost` is not changed.
The audio comes out late by the block plus the larger of 512 frames and the
graph's quantum: 1536 frames, 32 ms at 48 kHz, at a quantum of 256, and
2048 frames at 1024. That delay is part of the insert's
`latencyMilliseconds`, on top of the plugin's own figure, and counts as
zero for a plugin that gives none, so such an insert reports the delay
alone. A block the plugin answers late plays as silence and the delay
stays the same. A bypassed insert is out of the chain, delay included. The
helper's side is described in
[native/README.md](../native/README.md#own-block).

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
channel volumes or mute moved, so a change made by another program (a voice
chat's automatic gain control, a browser, a desktop volume panel) is in the
next state within about a second. It never writes such a change back; only
`setCaptureGain` writes the source's volume, on every channel at once. Two
capture inputs on one source report and set the same gain.

`muted` in `captureGain` is the source's own mute. `setCaptureMuted` sets
it and `toggleCaptureMuted` flips it. Like a desktop panel's mute, it
silences the source for every program that records it, OpenXLR's mixes
included, and leaves the gain and every send mute alone. Two capture inputs
on one source report and set the same mute, and a mute set by another
program shows in the next state the same way the gain does. An older daemon
sends `captureGain` without `muted`, and a client then draws no mute.

`captureGain` is absent on a capture input that carries a Wave unit's input
(`unitInput`) or remembers a unit (`binding.unit`); that channel's gain and
mute are the unit's. It is also absent on a source of an Elgato Wave interface, an
XLR Dock or OpenXLR itself, which OpenXLR never writes, the Wave XLR Pro's
raw source among them.

A profile saved now carries `mixer.captureGains`: channel id to
`{gainDb, muted}` for every capture input with a gain of its own whose
source the daemon has read. A recall writes each one the next time the
daemon finds that channel's source connected, so a profile recalled with
the camera unplugged takes effect when it is plugged in. A profile
without the field, as every older one is, leaves the gain and the mute
alone. A recall clamps the gain to the source's range, as `setCaptureGain`
does.

A profile saved now also carries `mixer.captureOwnClock`: channel id to
`true` or `false` for every capture input. A recall sets each capture input
it names, rewires the ones that change as `setCaptureOwnClock` does, and
the daemon saves the result in `mixer.json`. A name that is not a capture
input of the layout is skipped. A profile without the field leaves every
input's clock alone.

### Capture input chains

Each channel in the state carries `microphone` and `chainWidth`. An older
daemon sends neither, and a client then keeps to mono chains on `xlr1` and
`xlr2`. `chainWidth` is written as null on a channel that cannot host a
chain. Sound Check accepts channels whose `microphone` is true. The
microphone EQ commands accept every capture input, mono or stereo, and its
EQ is XLR 1's, run on this computer (see
[EQ on capture inputs](#eq-on-capture-inputs)).

The `mixer.inserts` map also accepts capture channel ids. `setInserts`,
`setInsertBypass`, `setInsertOwnBlock`, `setInsertParam`, `renameInsert`
and `showInsertUi` use those ids as `channel`. `removeWindowsPluginInserts {path}` removes matching
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
for a Windows game under Wine or Proton. A binary or application name
with a space in it becomes a key of the same kind, lowercased letters and
digits only. It is a key, not a name.
`label` is the name a client shows. The daemon takes it from the stream or
client's `application.name`, except when that names a runtime such as
Chromium, Electron, WEBRTC VoiceEngine or Wine, or is an identifier: one
word joined by underscores, with no space. Discord's
`discord_hardware_mute`, the client it opens to watch a headset's mute
switch, is one. In both cases the label is the process binary with its
first letter capitalised (`Discord`), when the binary is the application's
own and not a Wine or Proton runtime. A
Windows executable's name loses its `.exe`. A label remembered in
`mixer.json` from such an identifier is read back the same way.

A program that plays or records through ALSA reaches PipeWire through an
ALSA plugin. pipewire-alsa sets its `application.name` to
`PipeWire ALSA [kded6]`, the PulseAudio plugin to `ALSA plug-in [kded6]`.
The daemon takes the name inside the brackets, `kded6`, as the program's
own `application.name`. The label and the routing rules use it, and so
does the identity when the client has no process binary. KDE's service
daemon, `kded5` or `kded6`, which plays the desktop's notification sounds
this way, is labelled `KDE`; its identity stays the binary. A name with
nothing between the brackets stays as it is. An identity or label saved
under the wrapper name in `mixer.json` is read back as the program's. Clients
show `label` and keep `identity` for commands.

An entry's `volume` is the application's saved playback level, null or
absent when none is saved. `liveVolume` is the level its newest playback
stream has now, whoever set it, and is null or absent while the application
is silent. `mixer.appVolumeSupported` advertises `setAppVolume`.

### Application icons

A `streams` entry carries `icon` when the daemon found one for the app: a
freedesktop icon name, or an absolute image path when the app's desktop
entry names one or the icon came out of a Windows program. `icon` is
absent when nothing was found. The daemon tries,
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
5. for a program under Wine or Proton, the largest icon inside its `.exe`.
   The daemon reads the program's Windows path from the process's command
   line and finds the file in the process's `WINEPREFIX`. Its service
   cannot read another process's environment, so it then looks in
   `~/.wine` and in the prefixes under `XDG_DATA_HOME`, `~/Games` and
   Steam's `compatdata`, and a `Z:` path is the file system root. It
   writes the icon once as a PNG under `$XDG_CACHE_HOME/openxlr/icons`,
   and `icon` is that file's path;
6. the process binary itself, lowercased, since many packages install their
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
the plugin reports, plus the fixed delay of an insert
[on its own block](#own-block): zero for a bypassed insert, and absent (null over HTTP)
when the plugin gives no figure. A plugin in the native host reports what
the helper last read from it; there is no figure before the helper's first
report, which comes once audio has run. An LV2 plugin in the PipeWire filter
chain cannot report, so it reads zero when its metadata declares no latency
port and has no figure otherwise.

Each insert's status also carries `measuredLatencyMilliseconds`, the delay
the native host measured between the audio entering the insert's node and
leaving it. The host lines the two up by cross-correlation, so the figure
holds delay the plugin does not report and the delay of an insert
[on its own block](#own-block). It is absent (null over HTTP) until the
host has a figure it is sure of, which takes several seconds of signal
through the insert, and always for an insert that is bypassed, whose chain
is not running, or that runs in PipeWire's filter chain. A plugin that
changes the signal beyond recognition may never give one. Once given, a
figure stays until the host is as sure of a different one. Latency
compensation does not use it. The helper's side is described in
[native/README.md](../native/README.md#measured-delay).

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
names them (see above). The Wave XLR Pro's direct microphone paths
(`hpMixMicDirect`, the zero-latency path from its first input to its jacks,
and `hpMixMic2Direct`, the same from its second) follow the same channels:
XLR 1 (or XLR 2) while it takes the interface, or the capture channel on the
Pro's own node at that input's port while the strip is bound elsewhere. Each
path follows its own channel by the rule below, and input 2's never takes the
channel that carries input 1. The daemon turns the path on while every monitor output is a Pro
jack and at least one mix feeding them is unmuted and carries that channel
unmuted. Muting the feeding mix turns the direct path off. For a summed feed,
it stays on while any feeding mix and that channel's send to it are both
unmuted. The path plays at the level that send would have (see
[channel volume](#channel-volume)), less `outputVolume`'s attenuation
while it is below 100% (60 times its log10, the cubic scale), which acts
on the Monitor mix's USB stream and not on the path. Above 100% nothing
comes off, so the path is never louder than the send. The daemon writes that level before it
opens the path and again whenever the send, the channel volume, the mix
volume or the output volume moves. A level change needs no restart of the playback stream. The daemon mutes that
channel's monitor send in the graph meanwhile so the microphone is not
heard twice; it never uses the path while monitoring is paused or that channel
is in an exclusive group or under Sound Check, nor while software changes the
channel's audio: an insert that runs, the soft low cut or ClipGuard, or its EQ
in software, noise removal, voice changer, compressor or expander. The commands that change those
move the path at once. With no channel carrying an input that input's
path is off, and a bound XLR 1 or XLR 2 reaches the jacks through the
software mix. The daemon holds the path to this rule whether or not a jack is in use,
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
`state`, `warning` and `cardProfile` (the unit's card while it is driven,
as in [card profile](#card-profile); absent otherwise). `id` is the instance id, `vvvv:pppp@` and sixteen
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

### LED ring

On a unit whose capabilities report `ledRing` (the Wave XLR MK.2 from
firmware 1.0.15), the state carries `led`:

```json
"led": {
  "ringBrightness": 100,
  "meterBrightness": 100,
  "gainColour": "#ffffff",
  "headphoneColour": "#ffffff",
  "mixColour": "#ffffff"
}
```

`ringBrightness` is the brightness of the ring's steady display and
`meterBrightness` that of the moving microphone level meter on the ring,
each 0 to 100 %. `gainColour`, `headphoneColour` and `mixColour` are the
ring's colour while the dial sets the gain, the headphone level and the
mix, as `#rrggbb` in lower case. The values are what the unit reads back. The
daemon reads the LED block once at connect and again with each write.

`setLed` sets one of them:

```json
{"cmd":"setLed","control":"ringBrightness","value":40}
{"cmd":"setLed","control":"mixColour","value":"#00ff00"}
{"cmd":"setLed","control":"reset"}
```

| `control` | `value` |
|---|---|
| `ringBrightness`, `meterBrightness` | a whole number from 0 to 100. The unit takes a byte from 0x08 to 0xff: 100 is 0xff, and 3 and below write 0x08, the dimmest level checked on the unit, so they read back as 3 |
| `gainColour`, `headphoneColour`, `mixColour` | a colour as `#rrggbb`, either case. `#ffffff` lights the unit's white LED alone, as the unit ships; any other colour lights its red, green and blue LEDs at the colour's own values with the white LED off. A slot set elsewhere with the white LED and a colour together reads back as their sum |
| `reset` | none. Writes the factory LEDs, 100 % for both brightnesses and `#ffffff` in every mode, and forgets what OpenXLR kept for the unit |

Each write is one read of the unit's LED block and one write of the whole
block, with every byte other than the field set written back as read; a
value the unit already holds is not written. On a unit without `ledRing`
the daemon answers `setLed: the active device has no LEDs OpenXLR can set`.
The command acts on the primary interface.

The daemon keeps the fields set through `setLed` in
`~/.config/openxlr/leds.json`, per unit by its USB id, and writes them back
each time the unit connects, so a unit changed elsewhere meanwhile comes
back as OpenXLR left it. A field never set from OpenXLR is left as the unit
holds it, and after `reset` nothing is written back. Profiles and saved
settings do not carry the LEDs.

### Card profile

`cardProfile` describes the PipeWire card of the driven unit, at the top of
the state for the primary interface and in each `waveInterfaces` entry for
a unit driven as additional. It is absent until the daemon has found the
card, while no unit is driven, and while the software mixer is off, since
hardware-control mode leaves the card alone.

| field | meaning |
|---|---|
| `card` | the card's `device.name`, such as `alsa_card.usb-Elgato_Systems_Elgato_Wave_XLR_<serial>-00` |
| `profile` | the active profile's name |
| `capture` | whether that profile gives the card a capture node, from the profile's own classes in pw-dump, or from a source node of the card where the classes are missing |
| `switchedFrom` | the profile the daemon switched the card away from in this connection, absent when it switched nothing |
| `problem` | a sentence for the user while the card has no capture node and the daemon does not fix it, absent otherwise; the same sentence is part of `warning` for the primary and of the entry's `warning` for an additional unit |

The daemon checks a driven card when the unit connects, every 2 seconds
for up to 2 minutes while the session manager brings the card up, then
every 30 seconds while the unit is driven. A card is taken for the unit's
own only when its name carries the unit's `captureHint`, or its
`captureModelHint` where that is not empty, and the USB vendor and product
ids it states are the unit's.

On a card whose active profile plays but has no capture node, such as the
original Wave XLR on `output:analog-stereo`, the daemon switches once per
connection to the profile that plays through the same card devices and
adds a capture node, the available one with the highest priority
(`output:analog-stereo+input:mono-fallback` on that model), and logs a
warning with both names. A reconnect after a USB error is the same
connection; a replug or a power cycle starts a new one. The daemon does
not switch again in that connection. If the session manager or the user
puts a profile without capture back, `problem` says so and the card stays
where it was put. `problem` is also set when the card offers no such profile,
when the switch fails, and, once the first 2 minutes are over, while the
card is off. It clears when the card is on a profile with capture again.
The switch is not undone when the daemon stops.

The Wave XLR Pro keeps its own rule. The daemon parks a Pro card in its
UCM `HiFi` or `Direct` profile on `pro-audio`, restores it when the daemon
stops, and never switches it for capture. Its `problem` is set only when, after the
first 2 minutes, its card has no capture node.

### Own clock

A capture input can put its device on a clock of its own with
`setCaptureOwnClock`. It is for a device that misbehaves while it follows
another device's clock, which PipeWire does whenever a device feeds the
mixer. An Elgato 4K X on some USB4 controllers garbles and then drops off
the bus as a follower of a Wave XLR (issue #474). On its own clock the
device is linked only to a clock bridge, a mode of the native helper
(`openxlr-lv2-host bridge`). The bridge has two nodes:
`OpenXLR_bridge_<id>_in` takes the device's selected port or pair and runs in
the device's graph, which the device drives, and `OpenXLR_bridge_<id>` plays
the audio into the channel from the mixer's graph. They meet in a ring
buffer inside the helper. The mixer's clock is untouched and the device is
never resampled by PipeWire.

The two clocks drift apart, so the bridge corrects the drift itself. It
measures how much the ring holds, counting the device's frames as if they
came in one at a time, and a delay-locked loop (PipeWire's `spa_dll`) turns
the difference from the target into a tiny change of ratio for a
resampler on the mixer's side. The loop is wide for the first seconds after
a start, then narrows to PipeWire's slowest setting, where the ratio moves
by far less than a cent of pitch. A late or bunched device cycle is taken
by the ring's headroom and does not move the ratio. The target is the
device's cycle, two of the mixer's cycles, 32 frames for the resampler and a
10 ms margin: 1280 frames, 26.7 ms, at 48 kHz with both graphs at 256
frames, and 3584 frames, 74.7 ms, at 1024. That is the delay the input gains. When the
ring runs dry the bridge plays silence and fills to its target again; when
it holds twice its target, or the mixer stops reading, it drops back to the
target. Each event counts once. A mixer that stops reading counts one
overrun while the ring is full and one more when the bridge drops back.

The channel's `ownClock` is `{on, running, delayMs, targetMs, underruns,
overruns, driftPpm, error}`: whether the input is set to its own clock,
whether the bridge carries audio, the delay it holds and the one it steers
to in milliseconds (null until it knows), its underruns and overruns since
it started, how far the device's clock runs from the mixer's in parts per
million, and why the bridge does not run, or null. The state is pushed when
the bridge starts or stops carrying audio, counts an underrun or an
overrun, or its delay moves by a whole millisecond. Without the native
helper the channel stays silent with a reason in `bindingWarning` and
`ownClock.error`; it is never linked to the mixer directly, since that would
put the device back on the mixer's clock. A helper that dies is started
again by the next sweep, at most once every 10 seconds after a failure.

Every capture input on the device needs its own clock: a second input on
the same device without it links the device into the mixer's graph, and the
readout then shows the device with `separate` false. The setting is saved
in the channel's `userChannels` entry as `captureOwnClock` and belongs to
the layout, not a profile.

### Audio clock

`mixer.clock` says which node drives the PipeWire graph the mixer runs in
and which of the mixer's hardware devices follow it. It is absent from an
older daemon and null until the registry shows a driver, for example while
nothing runs.

| field | meaning |
|---|---|
| `driver` | `{name, description, priorityDriver, role, drivenUnit}`: the driving node's `node.name`, a name to show (the unit's name for a supported unit's node, the node's description otherwise), its `priority.driver` or null, which of the mixer's devices it is (the `role` values below, or null when it is none of them), and the attached unit whose card publishes it, as in `devices` |
| `followers` | the mixer's hardware devices that run on the driver's clock, the driver left out, each `{name, description, role, channels, microphone, priorityDriver, resampled, drivenUnit}` |
| `micResampled` | true when a follower with `microphone` true is `resampled` |
| `bridged` | the devices of capture inputs on their own clock, each `{name, description, channels, driver, separate}`: the device's `node.name`, a name to show, the capture inputs that take it, the node that drives the device's graph when that is another node (null when the device drives it or is idle), and `separate`, false when the device runs in the mixer's graph after all. Empty from an older daemon. See [own clock](#own-clock) |

A follower's `role` is `unitInput` (the driven unit's capture node),
`capture` (the source of a capture input or of a strip bound to another
source), `unitOutput` (a selected output, or the Aux port's sink, on the
driven unit's card) or `output` (any other selected output). `channels`
lists the strips the device feeds, empty for an output. `microphone` is
true when one of them is a microphone strip, `xlr1` or `xlr2`, whatever
source it is bound to. A capture input on another device is listed and can
be `resampled` without setting `microphone`, since an extra microphone
follows the unit's clock in a normal setup. Followers come unit input
first, then capture inputs, the unit's outputs and other outputs.

The daemon reads this from one `pw-dump` after the graph changes. It
waits for 1.5 quiet seconds, or reads after 5 seconds of continuous
changes. The registry changes it watches are new or removed nodes and
links, node state, and changes to `node.name`, `node.description`,
`node.driver-id`, `priority.driver`, `clock.name`, `device.id` or
`device.api`. Reads are at least 5 seconds apart. A failed or empty read
is retried after the minimum gap, even if the graph keeps changing. If
the mixer's device selection changes during the read, the next sweep
schedules a read for the new selection. After a successful read, a graph
that stays unchanged starts no further helper. PipeWire keeps a node's
`node.driver-id` current but does not send that change to subscribers,
which is why the dump is needed. Every node that runs in a
graph carries `node.driver-id`, the registry id of its driver, and a
running driver carries its own id or none. The mixer's graph is the one
its mix sinks (`OpenXLR_mix_*`) follow, or, before they run, the one its
devices follow. A follower is `resampled` when it has a clock of its own:
a hardware node (`device.api` set) of another device than the driver,
another `device.id`, or another card in the node name where there is no
device id, and no shared `clock.name`. Nodes of one card, such as a unit's
capture and playback, share its clock. This comes from the properties,
not from the resampler. A device on another clock at the driver's rate is
still rate matched, and a device that stopped (suspended or unlinked) runs
in no graph and is not listed.

A device a capture input takes through a clock bridge is not a follower,
whichever graph it runs in: it is listed in `bridged`, and it is never
taken for the mixer's driver.

While `micResampled` is true, `warning` carries one sentence naming the
microphone's interface and the driver, and the journal has a warning; each
change of driver or of a resampled follower gets one journal line.

`mixer.clockChoice` is the audio clock the user picked with
`setAudioClock`. It is absent from an older daemon and from the Flatpak's,
which cannot change the host's WirePlumber.

| field | meaning |
|---|---|
| `node` | the node the rule makes the clock, or null for Automatic. Read back from `57-openxlr-clock.conf` on every sweep, so a deleted or edited file shows here |
| `pending` | the choice waits for WirePlumber to restart: the chosen node runs in the mixer's graph, as the driver or a follower, without `priority.driver` 3000. For Automatic, a node in the graph still has 3000 after this daemon deleted its rule. A chosen node that is not in the graph is not pending |
| `active` | the chosen node drives the mixer's graph |
| `options` | the devices `setAudioClock` takes, each `{node, description, kind}`, `kind` `input` or `output`: the driven unit's input, then the driver and followers of `mixer.clock` that are ALSA nodes, inputs before outputs, and the chosen node when the graph does not show it |
| `problem` | why the file cannot be used, when it cannot: not written by OpenXLR, a symbolic link, unreadable, or not naming one node |

The rule is a WirePlumber 0.5 `monitor.alsa.rules` entry that matches the
node's `node.name` exactly and sets `priority.driver = 3000`, above the 2200
of the packaged rules and the 2600 of a pro-audio input. WirePlumber sets
the property when its ALSA monitor creates the node and PipeWire does not
take a new `priority.driver` on a node that exists, so the rule applies
after `restartAudioServices` or at the next login.

### Undo and redo

The daemon keeps one undo history, shared by every client and held in
memory, so it starts empty at each daemon start. `undo` puts back the
values the newest step changed and `redo` writes them again. A change made
in the UI, on a Deck key or over HTTP is a step in the same history, in the
order the daemon received it, and the next `undo` takes it back whoever
sent it.

This version records two groups of commands. The first is send levels,
channel volumes, mix volumes, send mutes and mix mutes: `setLevel`,
`setChannelVolume`, `setMixVolume`, `setChannelMuted`, `setMixMuted` and
`cycleExclusiveGroup`. The second is the processing of microphones and
mixes: `setEq`, `setEqEnabled`, `setEqBand` and `loadEqProfile` on a
microphone, a capture input or a mix, `setNoiseRemoval`, `setVoiceChanger`,
`setCompressor`, `setExpander`, `setLowCutHz`, `setSoftClipGuard`,
`setMonitorDry` and `setDucking`. A step holds each value the command
changed, before and after, including the sends an exclusive group closed
when a member opened, and the other monitor mixes a `setMixMuted` muted
when it opened a monitor during a monitoring pause. A processing value is
the whole setting: one channel's EQ with its switch and every band, one
channel's noise removal, voice changer, compressor or expander, one mix's
EQ or dry monitor switch, the low cut, ClipGuard, or the ducking setup
without where it runs. The history does not record where processing runs
(`setEqProcessing`, `setDynamicsProcessing`, `setDuckingProcessing`) or the
maximizer (`setMixMaximizer`). Every other command leaves the history alone, with these exceptions, which
empty it once they succeed: `loadProfile`, a profile recalled on connect,
`deleteChannel`, `deleteMix` (and `setWaveInterfaceEnabled` when it removes
a unit's capture channels), `setMixing`, `setActiveDevice` and
`resetDevice`. The history never holds phantom power, a lock (the gain
lock, the level locks and `setOutputVolumeLock`), the hardware mute, a
capture input's mute or the monitoring pause, so no undo can switch 48 V,
unlock a control, unmute a microphone on the unit or end a pause. It does
hold send mutes, so an undo can open a microphone's send in a mix again;
the step is named in `history.undo` before it runs.

Commands join steps by time. A recorded command joins the newest step from
the same connection when it changes a value that step already holds and
comes within one second of the step's last command, so a fader drag, a dial
turn, a held key or a drag of EQ bands is one step. It also joins that step, whatever it
changes, when it comes within 50 ms, so a Deck key that sends one command
per mix is one step. HTTP requests count as one connection. A joined step
keeps the first value before and the last value after, and a step whose
values end where they began is removed. `undo` and `redo` close the newest
step, so the next command starts a new one. A new step empties the redo
side. The history keeps 50 steps and at most 2 MiB of values, the oldest
dropped first.

A step is written only where it still holds. For each value the daemon
compares what the mixer has now with what the step left: the value after
for `undo`, before for `redo`. A value that matches is written. A value
already at the target counts as done and is not written. Any other value
was moved since, by the desktop, the unit or a profile recall, and is left
alone. A value whose channel or mix is gone is left out. A processing value
goes back through the same path as its command: an EQ that runs on the unit
is posted to the unit, a compressor or expander that runs on the unit is
written there, and a running stage takes the value live. Undo also leaves
out a value the mixer refuses, such as ClipGuard without its plugin or a
mix's EQ while hardware mixing builds that mix. When something was
skipped and the rest went through, the command succeeds, the step moves to
the other side holding only what was done, and `history.note` says what was
skipped in one sentence. When nothing went through, the step is dropped and
the command answers `undo: <label> was skipped: <reason>`; the next `undo`
takes the step below. Only a mixer that is not built, or that waits for the
audio server, keeps the step and answers with its sentence. Other answers
are `undo: nothing to undo` and, when `step` names another step than the
newest, `undo: the history changed` (`redo:` for `redo`). Undo and redo send
the state after a refusal, as the mixer commands do.

The state's `history`:

```json
"history": {
  "undo": {"step": 41, "kind": "sendLevel", "channel": "xlr1", "mix": "monitor", "count": 1, "label": "XLR 1 level in Monitor A"},
  "redo": null,
  "undoSteps": 7,
  "redoSteps": 0,
  "note": null
}
```

| field | meaning |
|---|---|
| `undo` | the step `undo` would take, or null |
| `redo` | the step `redo` would take, or null |
| `undoSteps`, `redoSteps` | how many steps each side holds |
| `note` | one sentence about the last `undo` or `redo` when it skipped something, null otherwise; the next change to the history clears it |

A step carries `step`, its id for the `step` guard, `kind`, the ids it
names, `count` and `label`. `count` is the number of things a burst joined
(the label then ends in "and 4 more"). `label` is one English phrase, built
from the names at the time of the step. The kinds:

| kind | ids | from |
|---|---|---|
| `sendLevel` | `channel`, `mix` | `setLevel` |
| `channelVolume` | `channel` | `setChannelVolume` |
| `mixVolume` | `mix` | `setMixVolume` |
| `sendMute` | `channel`, `mix` | `setChannelMuted` |
| `mixMute` | `mix` | `setMixMuted` |
| `exclusiveGroup` | `group` | `cycleExclusiveGroup` |
| `eq` | `channel` | `setEq`, `setEqEnabled`, `setEqBand` and `loadEqProfile` on a microphone or capture input |
| `mixEq` | `mix` | `setEq`, `setEqEnabled`, `setEqBand` and `loadEqProfile` with `mix` |
| `noiseRemoval` | `channel` | `setNoiseRemoval` |
| `voiceChanger` | `channel` | `setVoiceChanger` |
| `compressor` | `channel` | `setCompressor` |
| `expander` | `channel` | `setExpander` |
| `lowCut` | `channel` (`xlr1`) | `setLowCutHz` |
| `clipGuard` | `channel` (`xlr1`) | `setSoftClipGuard` |
| `monitorDry` | `mix` | `setMonitorDry` |
| `ducking` | none | `setDucking` |

A client that shows a step sends its id as `step`, so a change from another
client that got there first is refused rather than undone unseen. A script
may leave `step` out. A later version records more kinds; a client shows
`label` for a kind it does not know.

## Browser tabs

The Chromium companion captures tabs the user explicitly starts and sends
their audio to the selected OpenXLR application channel inside the browser.
The daemon exchanges metadata and output choices; audio does not pass
through this protocol. Browser process ids and application identities
cannot distinguish tabs that share a browser audio process. The companion
applies `browserTabs[].volume` inside each tab's capture graph. It advertises
`volumeSupported` in its registration, and `rememberVolume` in the state says
the level comes from the saved website default. An absent or null volume
plays at unity.

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
  `preserveBrowserOutputs`, `blockSize`, `browserSiteChannels`
  (origin-to-channel defaults), `appVolumes` (each application's saved
  playback volume) and `browserSiteVolumes` (the tab volume remembered for
  an origin), all machine settings absent from profiles, the software low
  cut, the insert chains, the microphone EQs (`eq`), noise removal
  (`noiseRemoval`), the voice changer (`voiceChanger`), the compressor
  (`compressor`), the expander (`expander`), where the EQs run
  (`eqProcessing`) and where the compressors and expanders run
  (`dynamicsProcessing`), the Wave XLR Pro sections kept for jack feeds
  (`hardwareRestore`, machine wiring, never in a profile),
  [hardware mixing](#hardware-mixing) (`mixing`, `hardwarePairs`,
  `hardwareSections` and `hardwareEntry`, machine wiring, never in a
  profile), each mix's maximizer amount on the Wave XLR Pro
  (`mixMaximizer`, by mix id, 1 to 100, never in a profile), the
  [ducking](#ducking) setup (`ducking`), the
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
- `leds.json`: the LEDs set with `setLed`, per unit, written back at every connect ([LED ring](#led-ring))
- `rawcapture.json`: the inputs each device sends to the computer before its
  processing, each with the tap to put back when it is off (see
  [raw capture](#raw-capture)). Written by the daemon.
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
- `eq-profiles.json`: the saved EQ profiles, 0600. Written whole by the
  daemon, through `saveEqProfile`, `renameEqProfile` and `deleteEqProfile`.
  Format `{"version":1,"profiles":[{"name":"Podcast","bands":[...]}]}`,
  each band as `setEq` takes it; the file the UI wrote before the daemon
  kept it has the same format. See [EQ profiles](#eq-profiles).

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

## Runtime snapshot

The snapshot has the UTC collection time, the .NET runtime description, the
working set in bytes, the .NET thread-pool size and pending work items, and
the `managed` heap counters. `liveBytes` is an estimate and
`totalAllocatedBytes` is cumulative. The fields prefixed with `last` describe
the last garbage collection identified by `collectionIndex`. An index of
zero means that no collection has been reported yet. The command never
forces one.

`memoryKiB` holds available process counters from Linux, including
resident, peak, locked and swapped memory and the proportional share of
mapped memory. An unavailable kernel counter is absent. The snapshot
does not contain addresses, memory contents, arguments or environment
variables. It works even with the mixer off or no interface attached.

## Event timeline

The daemon keeps a record of what changed around the audio in the last ten
minutes, in memory only, and a glitch marked with `markGlitch` is read
against it.
The diagnostics archive writes it to `timeline.txt`. It cannot say where an
xrun happened: PipeWire reports those only through its profiler, which drops
a driver's report above 16 KiB, and the mixer's graph is larger than that.

Each entry of `events` is `{at, kind, text}`: the time in UTC, one of the
kinds below, and one sentence. The ring holds the last `spanSeconds` (600)
of entries, at most `capacity` (500); `overflowed` counts the entries
dropped because the ring was full, so a reader knows the ten minutes are not
complete. `since` is when the daemon started and `now` when it answered.

| Kind | Entries |
|---|---|
| `daemon` | the daemon started |
| `device` | a supported interface came onto the USB bus or left it, was connected, is no longer driven, was switched to by `setActiveDevice`, closed because its USB helper exited, or the device loop failed (the first time for each message) |
| `usb` | a USB transfer that never returned, with its count against the set-aside limit, a USB reset, a unit set aside that is back, a unit that stops or starts answering reads |
| `card` | each card profile note the daemon logs for the driven unit |
| `capture` | the capture rescue's stalls, relinks, USB resets and reopened sources, and the hardware inputs' notes about their capture source |
| `mixer` | the mixer's graph built, not built, lost, torn down or answering again after a wait, its sweep failing or running again, sinks, sends and stage streams put back after something outside OpenXLR changed them, hardware mixing entering and leaving, and every note the mixer writes to the journal, such as a plugin's last message or a sample rate change |
| `plugin` | a native plugin host started for an insert, and any helper that stopped on its own (an insert chain, an EQ, ducking or delay stage, the soundboard player), with its last message and whether it is started again or left off |
| `clock` | the device that drives the mixer's graph and any microphone that follows it resampled, the audio clock rule written or removed, WirePlumber restarted for it, `setBlockSize`, and each underrun or overrun a capture input's own clock bridge counts |
| `stream` | an application stream started or stopped playing on a mixer channel, by the application's name and the channel; a stream's own title is never recorded |
| `marker` | a glitch the user marked |

Entries come from events the daemon already handles; nothing polls for
them, and an idle daemon adds none. Devices are named by model, never by
serial or instance id, and the clock by the devices' descriptions rather
than their node names.

Each of `markers` is `{at, label, before}`: when it was marked, the label
or null, and the entries of the 30 seconds (`windowSeconds`) before it,
oldest first, copied at that moment. The last 20 markers are kept for the
daemon's run, whatever the ring has dropped since. A daemon restart empties
both.

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

XLR 1, XLR 2 and every capture input, on one mono port or a stereo pair,
each have an EQ with 1 to 16 bands, with the controls, ranges, steps and
curves of Wave Link's. A capture input's always runs in software (see
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
- `gain`: -20 to +20 dB, in quarter decibels. It is kept for every type and
  ignored by `lowCut`, `notch` and `highCut`, so a band changed to one of
  those and back keeps its gain. Settings and profiles saved by a version
  that took -12 to +12 dB load unchanged. A unit may take a narrower range;
  see `maxGain` under [where the EQ runs](#where-the-eq-runs).
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

Every capture input, on one mono port or a stereo pair of any source,
takes the same `setEq`, `setEqEnabled`, `setEqBand` and `loadEqProfile`
with its channel id, and its `eq` in the state has the same fields,
defaults, ranges and steps.
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

A stereo pair's stage is one stereo node, the native helper's `eq` backend
on two channels, as an output mix's is. It runs the EQ alone, the same curve on both sides with each side's signal
kept apart, and has no noise removal, voice changer, compressor or
expander: the state reports `eq` and none of `noise`, `voice`,
`compressor` and `expander`, and `setNoiseRemoval`, `setVoiceChanger`,
`setCompressor` and `setExpander` refuse it. The stage is there only for
the EQ: a stereo pair whose EQ is off when its route is built gets none.

A profile saved now names the EQ of every microphone and capture input.
Deleting a capture input stops its stage and drops its EQ from
`mixer.json` with the channel. A source that disappears takes the stage
with it, and the EQ comes back on the same values when the source returns.
`setCaptureSource` keeps the EQ, and moving between a mono port and a
stereo pair starts a stage of the new width with the same values. Noise
removal, the voice changer and the dynamics stay saved through a move to a
stereo pair and run again once the input is back on one mono port.

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
   inputs'). On every model a band gain from -20 to +20 dB decodes. A band
   another program stored past what OpenXLR writes to that model does not
   stop the unit from running the EQ. Its `capabilities.hardwareEq`, and
   the one in its `waveInterfaces` entry, is then true. XLR 1's EQ goes to the Pro's
   input 1 and XLR 2's to its input 2, each with its own switch.
3. The unit can hold the channel's EQ: four bands or fewer, and every
   band's `gain` within the unit's `maxGain` either way, whatever the
   band's type, since the unit stores every band's gain.
4. The channel's `processing` is `device`, the default. `setEqProcessing`
   sets it.
5. No Sound Check session runs on the channel.
6. The unit does not send that input to the computer before its processing
   ([raw capture](#raw-capture)): the unit's EQ would not reach the
   computer.
7. The unit has not failed to keep the EQ written to it in this run.

`processing` is the channel's choice, `device` or `software`. `device` is
non-null while the channel carries an input of a unit whose model has a
hardware EQ (the MK.2 family, the Wave:3 MK.2 and the Wave XLR Pro). A one-input unit has no
input 2, so on one
XLR 2's `device` is null and its EQ runs in software with no `reason`.
Otherwise `device` is `{unit, input, name, firmware, ready, reason, note,
minimumFirmware, maxGain}`. `unit` is the instance id `waveInterfaces` gives, `input`
1 or 2, `name` the model, `firmware` the version the unit reports or null,
`minimumFirmware` the first version the model runs it on: `1.0.15` on the
MK.2 family, `0.2.3` on the Wave:3 MK.2, `1.0.14` on the Pro. `maxGain` is
the widest band gain in dB, either way, the unit is written: 20 on the Wave
XLR MK.2, measured on the desk on firmware 1.0.15 (2026-11-11), and 12 on
the Pro, the XLR Dock MK.2 and the Wave:3 MK.2, which have been measured to
12 dB only. While a band of the channel's EQ is past it, condition 3 fails
and the EQ runs in software with `ready` still true. It returns to the unit
when every band fits again. An older daemon sends no `maxGain`; a client
then takes 12. `ready` is true while the unit runs the EQ
now. When it is false, `reason` says why in a word a client can explain and
`note` in a sentence: `firmwareOld` (older than `minimumFirmware`; Elgato's updater is
in Wave Link on Windows or macOS), `firmwareUnknown` (the version could not
be read), `unsupportedBlock` (the EQ fields hold values the EQ does not
take; diagnostics show the block), `rawCapture` (the unit sends this input
to the computer before its processing, so the EQ runs in software until raw
capture is off; the unit holds its own EQ switch off meanwhile) or `failed`
(the unit did not keep an EQ written to it twice; switching the EQ or the
choice off and on tries it again). OpenXLR writes no EQ field to a unit that
is not ready.

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
When the microphone leaves a unit that no microphone carries any more, such
as an additional interface after `setActiveDevice` moved the primary off
it, OpenXLR switches that unit's EQ off. Once the unit reads it back off,
the mixer lets go of the input, and an EQ another program sets there later
is not written back.
Another channel taking the same unit's audio, a capture channel on its node,
hears the unit's EQ, as it hears the unit's low cut.

#### EQ profiles

An EQ profile is a set of bands to load on any microphone or mix. It holds
no channel, no mix, no EQ switch and no choice of where the EQ runs. The
state lists the profiles in `eqProfiles`, the same list for every client
and every EQ, built-in profiles first:

```json
"eqProfiles": [
  {"id": "builtin:clear", "name": "Clear", "builtin": true, "bands": [{"enabled": true, "type": "lowCut", "frequency": 80, "gain": 0, "q": 0.7047}, ...]},
  {"id": "saved:Podcast", "name": "Podcast", "builtin": false, "bands": [...]}
]
```

Each band is `{enabled, type, frequency, gain, q}` as `setEq` takes it.

- The built-in profiles come in two groups, in this order. The voice
  curves are Mic setup's starting presets: `builtin:clear`,
  `builtin:warm`, `builtin:broadcast`, `builtin:podcast`,
  `builtin:higher-voice`, `builtin:deep-voice` and `builtin:soft-sibilants`,
  four bands each. The listening curves are for what an output plays:
  `builtin:voices-forward` and `builtin:footsteps`, two shelves each, then
  `builtin:bass-boost` and `builtin:soft-treble`, one shelf each; every one
  cuts and none adds gain
  (the [manual](manual.md#output-eq) lists their bands). Every built-in
  profile loads on every EQ, a microphone's or a mix's; Mic setup offers
  only the voice curves.
  Their ids never change, and their `name` is English. A client names them
  in its own language from the key after `builtin:`, and shows `name` for
  a key it does not know. They cannot be renamed or deleted.
- The saved profiles follow, in the order of `eq-profiles.json`, each with
  the id `saved:` and its name. A saved profile may share a built-in one's
  name; the ids keep them apart.
- `loadEqProfile` puts a profile on a microphone or a mix; `saveEqProfile`,
  `renameEqProfile` and `deleteEqProfile` change the saved ones and send
  the new list to every client.

The daemon reads the file again when its size or time changes, so an edit
by hand shows with the next state, and at once after `listEqProfiles`.
Values outside the EQ's ranges are moved to the nearest edge and snapped
onto its steps as the file is read, so what loads is what the EQ takes. An
entry that does not read, such as one with an unknown band type, a missing
field or a name another entry has whatever its case, is left out of the
list. A file over 1 MiB, one that is not a profile list, or more than 64
entries is not read at all. `eqProfileProblem` then says why, and is
absent while the whole file reads:

```json
"eqProfileProblem": {"reason": "unreadable", "count": 1, "path": "/home/me/.config/openxlr/eq-profiles.json", "message": "Some profiles in ... could not be read (1) and are not listed. ..."}
```

`reason` is `unreadable` (`count` entries were left out), `damaged` (not a
profile list), `tooLarge` or `failed` (the file could not be read, the
message says why). The message is English; a client may say it in its own
language from the reason. While the file holds anything the daemon cannot
read, it writes nothing to it, so a save never loses an edit by hand. An
older daemon sends neither field.

### Output EQ

Every mix has an EQ of its own: Monitor A and B, the monitor mixes a user
added, Stream, Chat, the other virtual microphones and Aux. It is the
microphone EQ's model with the same 1 to 16 bands, types, ranges, steps,
curves and profiles, and the EQ commands take it with `mix` and the mix's
id in place of `channel`:
`{"cmd":"setEqBand","mix":"stream","band":2,"gain":-3}`. It always runs on
this computer, in the native plugin host, so `setEqProcessing` takes no
`mix`.

A mix's `eq` in the mixer state has a microphone's fields: `enabled`,
`bands`, `backend` (`software` while a stage runs for the mix, else null),
`available`, `error` and `processing`, always `software`; it never has a
`device`. The defaults are a microphone's, with the EQ off.

The stage is one stereo node, `OpenXLR_mixeq_<id>_<n>`, the native helper's
EQ on two channels with nothing else in it: no noise removal, dynamics or
voice changer. It sits at the head of the mix, as the microphone EQ sits
before a microphone's inserts: it always reads the mix's own monitor, and
the mix's insert chain reads it, so a limiter among the inserts catches
what the EQ boosts. Everything that reads the mix comes after both: the
outputs that play it, the post sink behind a virtual microphone, the USB
Aux port, and the latency delay when one runs. A mix without inserts has
those read the stage itself.

```
OpenXLR_mix_<id> -> OpenXLR_mixeq_<id>_<n> -> [inserts] -> [latency delay] -> outputs, post sink, USB Aux
```

Switching a mix's EQ on starts its stage and points the mix's insert chain
at it, or the mix's readers when no chain runs. The new link is made before
the old one goes and nothing else is touched, so the insert chain keeps
running and no plugin restarts. Every other change is written to the
running stage, live: a band's values, a band on or off, the EQ's own
switch. Switching the EQ off bypasses inside the stage, which stays until
the mix is next built or the daemon restarts. A mix whose EQ is off when it
is built gets no stage. Only a change in the number of bands starts a new
stage in place of the old, and the running chain is then linked from the
new one. When the insert chain is built again for its own reasons, the
stage keeps running and the new chain is linked from it; a chain the mixer
gave up on stays off, and the mix's readers take the stage itself. A stage
that dies is started again by the sweep, on the restart policy the
microphone EQ has (three failures in five minutes, keyed per mix); while it
cannot run, the mix plays without its EQ, never silent. Nothing here moves
a microphone's route, so the Wave XLR Pro's zero-latency path stays as it
was; the microphone it carries reaches the jacks before any mix, and so
without the output EQ.

The EQ is saved in `mixer.json` under `mixEq`, by mix id. A mix absent there
has the defaults with the EQ off, and an id that is no mix of the layout is
dropped as the file is read. Deleting a mix drops its EQ. A profile saved
now carries `mixer.mixEq` for every mix; a recall replaces the EQ of each
mix of the layout it names, and a profile saved before the output EQ leaves
every mix's EQ alone. A recall that changes only values writes them to the
running stages and starts nothing.

While [hardware mixing](#hardware-mixing) runs, the Wave XLR Pro builds the
mixes its jacks play, and the Aux mix while the USB Aux port is on; each of
those has its EQ held off, bypassed in its stage, with the
`eq:mix:<id>` sentence in `hardwareMixing.disabled`. `setEq`,
`setEqEnabled`, `setEqBand` and `loadEqProfile` on such a mix are refused
with that sentence. The state keeps reporting the saved EQ, and it applies
again when software mixing returns. Every other mix keeps its EQ.

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
- `latencyMs`: the delay the running engine adds, or null. It is noise
  removal's own: a voice on the same microphone reports its delay in the
  channel's `voice.latencyMs`, and the microphone is late by the two added
  together. A plugin host older than the daemon reports only the stage's
  total, which then stands here.
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
`{enabled, voice, pitch, formant, active, latencyMs, available, error}`:

- `enabled`, `voice`, `pitch` and `formant`: the setting. `pitch` and
  `formant` are the custom voice's shifts, -12 to 12 semitones in half
  semitones; the other voices carry their own and ignore them. The defaults
  are off, `deep`, 0 and 0.
- `active`: the voice heard now, or null. It lags `voice` while a change
  crossfades.
- `latencyMs`: the delay the running voice adds, 33.3 ms at 48 kHz for
  every voice, Custom at 0 and 0 semitones included, or null while the dry
  voice plays. Noise removal's delay is reported apart, in `noise.latencyMs`.
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

<a name="dry-monitor"></a>
### Dry monitor

Each monitor mix (Monitor A, Monitor B and every monitor mix the user
added) can hear the microphones dry. With `setMonitorDry`, the monitor
mix's id as `mix` and `value` true, that mix takes every channel that
carries the [microphone EQ](#microphone-eq) (XLR 1, XLR 2 and every
capture input on one mono port) as it comes in, before the software EQ,
noise removal, the voice changer, the compressor and expander on this
computer, the soft low cut, ClipGuard and the inserts. The owner hears
their own voice earlier there, by the delay those effects add. The other
monitor mixes, Stream, Chat, every other virtual microphone and the Aux
mix keep the processed voice. It is off on every monitor mix until
switched on.

The send level, the send's mute, the channel volume, an exclusive group and
the monitor mix's master act on the dry send as they act on the processed
send, which is held closed meanwhile. A
microphone with no software effect on sounds the same either way. While
Sound Check runs on the microphone the monitor hears the take through the
effects, and the dry send comes back when the session ends. While the
microphones are part of the monitoring pause, the dry send is paused with
them.

In the graph the dry send is a hidden combine of its own,
`OpenXLR_ch_<id>.dry`, fed by a link from the same capture port the
channel's own route starts at, with one stream into each monitor mix and
none into any other. A microphone has it while any monitor mix is dry, and
its streams into the monitor mixes that are not dry stay closed. Switching
a monitor mix moves only the microphones' sends into that mix: no stage,
chain or plugin host restarts, and no other mix changes. The processed send is closed before the dry one opens, and the
dry feed is unlinked before the processed send opens again, so the voice
is never heard twice. Until the dry combine is up and fed, and while its
source is away, the monitor keeps hearing the processed send. The sweep
follows the source across a replug or a change of interface, and loads the
combine again when a monitor mix is added or deleted.

On a Wave XLR Pro the [zero-latency path](manual.md#volume-direct-monitor)
carries the microphone to the jacks inside the unit. When every monitor
mix the jacks play is dry, the path stays on while software effects run,
since the dry voice is what was asked for, and the monitor send is held
back as it always is while the path runs. The path's other conditions are unchanged: every monitor output
is a jack, the channel is not in an exclusive group or under Sound Check,
and its send is open in the mix the jacks play. In [hardware
mixing](#hardware-mixing) a jack whose feed is made only of dry monitor
mixes takes the microphones from the unit's own input rather than their
return pairs; every other jack and the Aux mix's section play the return
pairs. A jack that sums a dry monitor mix with one that is not hears the
processed voice, so the voice is never doubled. A microphone claims no
return pair while every jack hears it dry and the unit builds no Aux mix.
On the MK.2 units, the Wave:3, the Wave:3 MK.2 and the Wave:1 the switch
changes the software monitor send only; their Mic and PC crossfade is as
before.

A monitor mix's `monitorDry` in the mixer state's `mixes` is the switch,
true or false; every other mix has none. It is saved in `mixer.json` under
`dryMonitors`, mix id to true; a monitor mix absent there has it off, and
the per-microphone `monitorDry` map of earlier development builds is not
read. A profile saved now carries `mixer.dryMonitors` for every monitor
mix; a recall sets each monitor mix it names, ignores an id that is not a
monitor mix of the layout, and a profile without it, as every older one
is, leaves the switches alone. Deleting a monitor mix drops its switch with
the mix.

### Compressor and expander

Every channel that carries the [microphone EQ](#microphone-eq) has a
compressor and an expander. On XLR 1 and XLR 2 they run on the unit's own
DSP when the unit can run them (see
[where they run](#where-the-compressor-and-expander-run)). Everywhere else
they run on this computer, in the native plugin
host, in the same stage as noise removal, the software EQ and the voice
changer. The order in the stage is noise removal, expander, EQ, compressor,
voice changer. The expander closes on the noise that noise removal leaves,
and the compressor evens out the shaped voice. Both are off until switched
on, and off they pass the microphone through untouched, to the bit. Neither
adds delay.

The compressor turns the voice down above its threshold by its ratio, with
a soft knee. The expander turns the microphone down below its threshold by
its ratio, at most by its range, and waits for its hold before it closes,
so it does not close between the peaks of a word. Both measure each
sample's peak in dB and smooth the gain they apply, attack as the input
gets louder and release as it gets quieter.

| Setting | Compressor | Expander |
|---|---|---|
| `threshold` | -60 to 0 dB, whole dB, default -18 | -80 to 0 dB, whole dB, default -50 |
| `ratio` | 1 to 20, steps of 0.1, default 3: 3 dB over the threshold come out as 1 dB | 1 to 20, steps of 0.1, default 3: 1 dB under the threshold comes out 3 dB under it |
| `attack` | 0.1 to 200 ms, steps of 0.1, default 5: the time to reach 63 percent of a new reduction | 0.1 to 200 ms, steps of 0.1, default 1: the time to open 63 percent of the way |
| `release` | 10 to 2000 ms, whole ms, default 150: the time to give back 63 percent | 10 to 2000 ms, whole ms, default 150: the time to close 63 percent of the way once the hold has passed |
| `knee` | 0 to 24 dB, whole dB, default 6: the width of the soft knee around the threshold, 0 for a hard one | |
| `makeup` | 0 to 24 dB, steps of 0.5, default 0: gain added after the compressor | |
| `range` | | 0 to 80 dB, whole dB, default 24: the most it turns the microphone down |
| `hold` | | 0 to 500 ms, whole ms, default 50 |

A channel's `compressor` in the mixer state is
`{enabled, threshold, ratio, attack, release, knee, makeup, available, error, backend, processing, device}`
and its `expander`
`{enabled, threshold, ratio, range, attack, release, hold, available, error, backend, processing, device}`:
the setting, `available` (always true while the unit runs them, which needs
no helper; in software false when the native plugin host is not
installed, or after the stage failed three times in five minutes),
`error` (why the setting does not run as asked, or null: no host, a host
from before the compressor and expander, or the stage's own failure),
`backend` (`device` while the unit runs them, on or off; `software` while
the stage runs with either on; null otherwise), `processing` (the
channel's choice, below; always `software` on a capture input) and
`device` (the unit the channel carries, below). While the unit runs them,
the numbers are the ones the unit holds, and `knee` and `hold`, which the
unit does not have, are the setting's.

`setCompressor` and `setExpander` take the channel and at least one of
their fields; an absent field keeps its value, so
`{"cmd":"setCompressor","channel":"xlr1","enabled":true,"threshold":-24}`
switches XLR 1's compressor on at -24 dB and
`{"cmd":"setExpander","channel":"xlr1","enabled":false}` switches its
expander off. Values outside a range are refused, not clamped, and what is
taken is snapped onto the steps above. Switching either on with nothing
else in the stage on starts the stage and links it in, as the voice
changer does; every other change goes to the running stage, and switching
on or off is a 5 ms crossfade.

Their levels reach a client only while it asks for them. `watchDynamics`
with the channel sends them for the next 15 seconds, and a client that
shows them sends it again every few seconds. While the microphone's stage
runs, the `meters` frame then carries `exp:<id>` and `comp:<id>`, each
`[input, reduction, output]`: the highest input and output peak in dBFS
since the stage's last report, -120 for silence, and the most gain
reduction in dB, 0 or more, not counting the make-up gain. The stage
reports about 30 times a second and the frame goes out 15 times a second.
The expander's input is the microphone after noise removal, and the
compressor's is the microphone after noise removal, the expander and the
EQ. Asking starts no stage, so with nothing in the stage on, no `exp:` or
`comp:` levels come. Once nobody asks, the stage stops reporting them. The
UI fills that gap from the [meter before the chain](#meter-before-the-chain):
while no stage levels come it also sends `watchInputMeter` and shows the
peak of `in:<id>` as the input, which is the raw microphone before noise
removal, with no reduction and no output.

While a unit runs them, what comes depends on the unit. A Wave XLR MK.2
on firmware 1.0.15 or later reports them in its meter block, and its
`device` carries `levels: true`. While a client watches, the device loop
reads that block about 20 times a second and `exp:<id>` and `comp:<id>`
carry the unit's own readings: the input is the level the processor's
detector hears (the expander's is the microphone, the compressor's is
the expander's output and moves more slowly than a peak), the reduction
in quarter-dB steps and the output, -120 at the unit's floor. With
nobody watching, the device loop does not read the block. A frame after a failed read has no
`exp:` or `comp:` for the channel. Every other unit (`levels: false`)
reports neither its input nor its gain reduction, so no `exp:` or
`comp:` levels come for it; the UI then shows the peak of `in:<id>`,
which is the microphone after the unit's processing, as the output
alone, and does the same on the Wave XLR MK.2 while its levels have not
come.

Both are saved in `mixer.json` under `compressor` and `expander`, by
channel id. A microphone absent there has them off. A profile saved now
carries `mixer.compressor` and `mixer.expander` for every microphone; a
recall replaces the setting of each channel it names, and a profile
without them, as every older one is, leaves them alone. Deleting a capture
input drops its settings with the channel.

#### Where the compressor and expander run

The compressor and the expander of XLR 1 and XLR 2 run on the unit's own
DSP when all of these hold, and on this computer otherwise. It is the rule
of [where the EQ runs](#where-the-eq-runs):

1. The channel carries a driven unit's input, by the rule `unitInput`
   follows.
2. The unit runs them: it runs the EQ (the same models and firmware), and
   the compressor and expander fields of its settings block hold values
   the unit takes, on the Pro on both inputs. Its
   `capabilities.hardwareDynamics` is then true.
3. The channel's `processing` is `device`, the default.
   `setDynamicsProcessing` sets it, for the compressor and the expander
   together.
4. No Sound Check session runs on the channel.
5. The unit does not send that input to the computer before its processing
   ([raw capture](#raw-capture)).
6. The unit has not failed to keep what was written to it in this run.

`device` in each of the two objects is non-null while the channel carries
an input of a unit whose model has them, with the fields of the EQ's
`device`: `{unit, input, name, firmware, ready, reason, note,
minimumFirmware}`, without the EQ's `maxGain`, and `levels`: whether the unit reports their levels
while it runs them (true on the Wave XLR MK.2 from firmware 1.0.15, see
above). `reason` is `firmwareOld`, `firmwareUnknown`,
`unsupportedBlock` (the compressor or expander fields, or the EQ's, hold
values the unit does not take), `rawCapture` (the unit sends this input to
the computer before its processing, so they run in software until raw
capture is off) or `failed` (the unit did not keep a write twice; switching
either processor or the choice off and on tries again).

On the way to the unit the daemon rounds each value to the unit's step
and clamps it to the unit's limits:

| Setting | Compressor on the unit | Expander on the unit |
|---|---|---|
| `threshold` | -60 to -1 dB, whole dB | -60 to -1 dB, whole dB |
| `ratio` | 1 to 20, whole numbers | 1 to 3, steps of 0.1, held in sixteenths |
| `attack` | 1 to 300 ms, whole ms (the setting stops at 200) | 1 to 200 ms, whole ms |
| `release` | 50 to 1000 ms, whole ms | 10 to 1000 ms, whole ms |
| `makeup` | 0 to 20 dB, whole dB | |
| `range` | | 0 to 60 dB, whole dB |
| `knee`, `hold` | none: kept for software | none: kept for software |

A value the commands take that the unit cannot hold is clamped and
rounded, never refused, so the same setting runs on either side; the
setting itself is kept as sent and comes back when software runs it. While
the unit runs them the state reports the values it holds, and the UI
limits its sliders to these ranges.

The unit's two switches are the same bits the unit's own `compressor` and
`expander` controls set. While the mixer drives a unit's compressor and
expander, those controls are left out of the unit's `capabilities` and
`set` refuses them, so the microphone's setting is the only one. When
software runs them, or they are off, the unit's switches are held off, so
they never stack. When the microphone leaves a unit that no microphone
carries any more, such as an additional interface after `setActiveDevice`
moved the primary off it, the daemon writes both switches off. Once the
unit reads them back off, the mixer lets go of it. Its `compressor` and
`expander` controls are back in its `capabilities`, which `waveInterfaces[]`
reports for an additional interface, `set` takes them again, and nothing is
written back over them. A microphone with no compressor and no expander setting
of its own when its unit first runs them takes the unit's switches and
settings (each held to the software's ranges, the knee and the hold at
their defaults), so a compressor switched on with the unit's own switch
before stays on; the daemon waits for the unit's first read for that and
writes nothing before it. The journal says so once.

On the unit, each change is a read of the settings block and a write of
the whole block with only these fields changed, without a commit, written
and read back as the EQ is: the first change at once, then at most ten a
second, the last always, nothing while phantom power settles, a write the
unit does not keep tried once more, and a change another program makes
written back at most every 5 s. Moving between the two switches the
leaving side off first, and software takes over once the unit confirms
it, after 1 s at most.

The settings layout is the one in
[the Pro protocol notes](wave-xlr-pro-protocol.md#compressor-and-expander-on-the-unit).
On the Wave:3 MK.2 every field was seen written to both ends of its limits
and read back by the unit. The Wave XLR MK.2, the XLR Dock MK.2 and both
inputs of the Wave XLR Pro hold the same fields at the same offsets, with
the same factory values, and are driven the same way. How any of them
sounds with OpenXLR's writes has not been checked by ear yet.

### Raw capture

A unit with `capabilities.rawCapture` can send the computer an input before
its own processing, so the computer's chain (the [microphone EQ](#microphone-eq),
the [compressor and expander](#compressor-and-expander),
[noise removal](#noise-removal), the inserts) works on the microphone as the
preamp gives it. The unit's own paths keep its processing: the Wave XLR
Pro's mixes take their inputs at their own taps, which raw capture never
changes, and so the zero-latency path to the jacks, the mixes the unit
builds in [hardware mixing](#hardware-mixing) and the USB Aux port still
hear the processed microphone.

| Control | Input | Unit | Register |
|---|---|---|---|
| `rawCapture` | XLR 1 | Wave XLR Pro from firmware 1.0.14, Wave XLR MK.2 from 1.0.15 | Pro: block 0x0001 byte 94; MK.2: block 0x0001 byte 4 |
| `rawCapture2` | XLR 2 | Wave XLR Pro from 1.0.14 | block 0x0001 byte 95 |
| `lineInRawCapture` | Line In | Wave XLR Pro from 1.0.14 | block 0x0001 byte 97 |

Each is a `set` control with a boolean. The state's fields of the same
names read back what the unit holds: true while the byte's tap, bits 0 and
1, reads `00`. On a microphone input `00` is the microphone before the
unit's low cut, compressor, expander and Voice Tune; on Line In it is Line In
as it arrives, before the unit's insert return and ducker. Every other bit
of the byte is written back as read, and no commit follows: the taps apply
live (desk measurements of 2026-11-11). The XLR Dock MK.2 and the Wave:3
MK.2 share the MK.2's layout but were not measured, so they do not offer it.
The Pro's byte 96, a tap for USB Aux by its place, showed no effect on the
desk and is never written.

The setting belongs to the daemon, per unit and per input, saved in
`rawcapture.json` with the tap the unit held when it was turned on. The
daemon writes it again at every connect, so a unit another program set back
is put right, and turning it off puts back the tap it found, or `02`, the
value the units hold as Wave Link leaves them. An input the setting does not
name is left as the unit holds it. The daemon leaves the unit as it is when
it stops, as it does every other unit setting. The setting is machine
wiring: it is not in saved settings or profiles. An additional interface
takes the same controls through `setWaveControl`.

What changes on the computer while an input is raw:

- The microphone's EQ, compressor and expander run in software, whatever
  `setEqProcessing` and `setDynamicsProcessing` chose: their `device` reports
  `ready` false with `reason` `rawCapture`, and the unit holds its own EQ
  switch and its compressor and expander switches off, as for any
  microphone whose EQ runs in software. The choice comes back into force
  when raw capture is off.
- The unit's `lowCut`, `voiceTune` and `voiceTuneStrength`, and
  `compressor` and `expander` where the capabilities offer them, still
  switch the unit's processing, which the unit's own paths hear and the
  computer does not.
- For XLR 1, `softLowCutApplicable` turns true, so the software low cut
  (`lowCutHz`) acts on it.
- The gain is the preamp's and applies either way. Whether the unit's mute,
  ClipGuard and its capture limiter act before the tap has not been
  measured.

### Ducking

Ducking lowers chosen channels in chosen mixes while a key is loud. A
setup has one to eight keys, and the loudest of them at each moment is the
one that counts, as on the Wave XLR Pro's own processor; two quiet keys
never add up to a loud one. A key is never lowered. It is one setup on every device, or
with no device at all, and it needs the software mixer. It runs in
software, as a native stage per ducked mix, or on the Wave XLR Pro's own
processor when the setup fits what the unit can do. The choice of where it
runs is shaped like the [microphone EQ's](#where-the-eq-runs):
`processing` is the user's choice and `backend` is what runs it now.

The state message always carries `ducking`, `{enabled, keys, key,
channels, mixes, sensitivity, amount, speed, processing, backend, available,
error, device}`:

```json
"ducking": {
  "enabled": true,
  "keys": ["xlr1"],
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
    "readback": {"sensitivity": "medium", "amount": "high", "speed": "fast", "pairs": [1], "entries": [5], "inputs": [], "keys": [0]}
  }
}
```

- `enabled`: the switch.
- `keys`: the channels whose level ducks, one to eight, in the order they
  were chosen. A key is a microphone (`xlr1`, `xlr2` or a capture input
  on one mono port, a channel with `microphone` true), Aux In (`aux`) or
  Line In (`line`) on a unit that has them, or an application channel. A
  capture input on a stereo pair cannot be a key. In software each key is
  heard on its channel's first (left) channel.
- `key`: the first of `keys`, for clients from before several keys.
- `channels`: the channel ids to lower, never a key. Any channel can be
  lowered, the unit's own inputs (`xlr1`, `xlr2`, `aux`, `line`)
  included. `mixes`: the mix
  ids they are lowered in. The ducked sends are every channel in
  `channels` times every mix in `mixes`; an empty list in either ducks
  nothing.
- `sensitivity` (`low` -3 dBFS, `medium` -12 dBFS, `high` -24 dBFS; a
  higher one reacts to a softer voice), `amount` (`low` 6 dB, `medium`
  12 dB, `high` 24 dB) and `speed` (`slow` 100 ms down and 2000 ms back,
  `medium` 80 and 1200, `fast` 30 and 200). The words are the Wave XLR
  Pro's presets, and software uses the same numbers. Each field also takes
  a custom value in whole numbers: `sensitivity` the key threshold in
  dBFS, -60 to 0; `amount` the depth in dB, 1 to 40; `speed` an object
  `{"attack": ms, "release": ms}` with both fields, attack 1 to 1000 and
  release 10 to 10000. A number written as `12.0` counts as whole; `12.5`
  is refused. The state reports each field in the form it was set: a
  word stays a word, and a number stays a number even when it equals a
  preset, so `-12` is reported as `-12`, not `medium`. The UI's sliders
  always send numbers.
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
  this setup, any of `monitor` (the jacks and Line Out, USB playback pairs
  1 to 3, one per jack feed) and `aux` (the USB Aux port, pair 5, or
  section 36 in hardware mixing), given
  even when it is not ready. `held` is true when the unit was last read holding what OpenXLR
  wrote to it. `readback` is that read decoded: `sensitivity`, `amount`
  and `speed` as the unit holds them, in the setup's shapes: the preset
  word when all twelve entries hold the value a preset writes, the number
  (for the speed, the attack and release object) when they agree on any
  other value, and `custom` when the entries disagree. The unit cannot
  tell a word from its number, so a setup of `-12` reads back `medium`.
  `pairs`, the USB playback pairs
  ducked in any of its mix sections (0 to 7, pair n is channels 2n and
  2n+1); `inputs`, the unit's inputs ducked in any section (0 XLR 1, 1 XLR
  2, 2 USB Aux, 3 Line In); `entries`, the block 0x0008 entries any source
  keys, an entry's own bit aside (0 to 3 the inputs, 4 to 11 the pairs);
  and `keys`, the sources set in those entries' key masks, numbered the
  same way (4 + n for pair n). Wave Link leaves keys on entries 4, 7 and 8
  with no ducking at all, so `entries` reads `[4, 7, 8]` on such a unit
  until OpenXLR writes to it; OpenXLR clears every key it does not set.
  `held` compares every byte, so a unit whose entries hold a key beside the
  ones OpenXLR wrote is not held.

Live reduction is a meter, not state: while software runs, the `meters`
message carries `duck:<mix>` for each ducked mix, the reduction in dB. While
the unit runs ducking and `held` is true, `duck:<mix>` carries the unit's
own reduction from its meters (block 0x0002) for each ducked mix: the
largest across the unit's ducked inputs and pairs, in quarter dB, the same
value under every such mix, since the unit ducks a pair or an input, not a
mix. In hardware mixing a mix software ducks too keeps its stage's reading
under its key. The device loop reads the unit's meters once per poll and
once more halfway to the next, about 20 times a second, and only while
ducking is on the unit and the `meters` message goes to a client. It stops
a second after the last frame. One read serves the ducking reduction and
the [hardware mixing](#hardware-mixing) mix meters together. The first
frame after ducking moves to the unit, or after a client connects, carries
no `duck:` key for the unit. The unit's levels are peaks: they rise within
about 40 ms, hold nothing and fall at about 34 dB a second.

`setDucking` takes `value`, an object with any of `enabled`, `keys`,
`key`, `channels`, `mixes`, `sensitivity`, `amount` and `speed`; a field
left out keeps its value, an unknown one is refused. `keys` replaces the
keys; `key`, one channel id, is what a client from before several keys
sends, and sets that one key. A command carries one or the other. For example
`{"cmd":"setDucking","value":{"enabled":true,"channels":["music","game"],"mixes":["monitor","stream"],"amount":"high"}}`
or `{"cmd":"setDucking","value":{"keys":["xlr1","voicechat"],"channels":["music","game"]}}`.
The errors name the field: `setDucking: value must be an object`,
`setDucking: value needs at least one of enabled, keys, key, channels,
mixes, sensitivity, amount and speed`, `setDucking: keys must be one to 8
distinct microphone, Aux In, Line In or application channels`,
`setDucking: key must be a microphone, Aux In, Line In or application
channel`, `setDucking: give key or keys, not both`,
`setDucking: channels must be distinct channel ids other than the keys`,
`setDucking: mixes must be distinct mix ids`, `setDucking: sensitivity
must be low, medium, high or a whole number of dBFS from -60 to 0`,
`setDucking: amount must be low, medium, high or a whole number of dB from
1 to 40`, `setDucking: speed must be slow, medium, fast or {attack,
release} in whole ms, attack 1 to 1000 and release 10 to 10000`,
`setDucking: enabled must be a boolean`, `setDucking: unknown field
'<name>'`. A speed object with a field missing, an extra field or a value
that is not a whole number in range takes the speed error. For example
`{"cmd":"setDucking","value":{"sensitivity":-30,"amount":18,"speed":{"attack":45,"release":900}}}`.
`setDuckingProcessing` takes `value` `device` or `software`, as
`setEqProcessing` does. The daemon saves the setup in `mixer.json` first
and applies it only once the save succeeded; a failed save leaves the
setup and both backends as they were. Both commands need the software
mixer; in hardware-control mode they are refused with `mixer not built
(start the daemon with --mixer)`, and the state shows the saved setup with
nothing running.

`mixer.json` holds the setup as `ducking`, `{enabled, keys, channels, mixes,
sensitivity, amount, speed, processing}`, each of the three a word or a
custom value in the command's form. A file or a profile from before several
keys holds `key`, one channel id, instead of `keys`; it loads as that one
key, and the next save writes `keys`. When a file holds both, `keys` wins.
A file without `ducking` takes the
defaults, and the next save writes them: off, keys `["xlr1"]`, every
application channel, the default monitor mix (`defaultMonitorMix`, which is
`monitor` while Monitor A exists), medium presets and processing `device`. A
`ducking` object without `mixes`, here or in a profile, takes the default
monitor mix of the layout it is applied to. On load, ids the layout lacks are
dropped, a key that is gone or cannot be a key is dropped, a key among
`channels` leaves `channels`, and with no key left ducking turns off and the
keys reset to `["xlr1"]`. A word the daemon does not know takes its default, `medium`, as
does a number out of range, a fraction or a speed object it cannot read;
each drop is noted in the journal.
Layout edits prune the setup in the same save: a deleted channel leaves
`channels`, a deleted mix leaves `mixes`, and a deleted key leaves `keys`;
deleting the last key turns ducking off and resets the keys to `["xlr1"]`.
A new channel is not added. A profile
carries `mixer.ducking` without `processing`. Recalling it replaces the
setup, drops ids the layout lacks and turns ducking off when no key is
left; a profile without `ducking` leaves the setup alone. A profile
whose `ducking` holds a preset word the daemon does not know, or a custom
value it does not take, is refused whole, as any other invalid mixer field
is.

A key ducks only while it is heard: a key muted in every send it has does
not duck, and in software neither does a microphone key while
`monitorMuteMicrophones` and `monitorMuted` pause it; the other keys still
duck. In software each ducked mix has one native stage with a key input
for each key, `key_0` to `key_7` in the order of `keys`, and follows the
loudest open key at each sample. Sound Check's loop passes through a key's channel, so a looped
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
| every key is one of the unit's inputs: XLR 1 or XLR 2 on whatever strip carries it, Aux In or Line In | `keyNotSupported` | Voice Chat is not one of the Wave XLR Pro's inputs, so the unit cannot use it as a key. |
| the unit's ducking is heard on the jacks: no target mix is heard on them while the last read shows the jacks' feed outside the mix they play | `monitorReturn` | The jacks' feed is not in the Wave XLR Pro's headphone mix, where it ducks, so this computer ducks. |
| every target mix that is heard is heard only on the unit: a monitor mix in the jacks' feed, or the Aux mix on the USB Aux port, and on no other output, virtual microphone or feed | `mixElsewhere` | Monitor A also plays on Speakers, which the Wave XLR Pro cannot duck. |
| when one mix of a jack's feed is ducked, all of that feed's mixes are; jacks on other feeds are not counted | `sharedOutput` | The jacks play Monitor A and Chat together, and only Monitor A is ducked. |
| every channel heard in a target mix (send above 0, not muted) is in `channels`, the keys excepted | `partialMix` | Voice Chat plays in Monitor A and is not ducked; the Wave XLR Pro lowers a whole output. |
| no key is heard in a target mix through its software send | `keyInMix` | XLR 1 is heard in Monitor A through its software send, so the unit would lower the key too. |
| the unit kept the ducking written to it (two misses fail it) | `failed` | The Wave XLR Pro did not keep the ducking written to it. Switch ducking off and on to try again. |
| the unit answers a read of its ducking block | `unsupportedBlock` | The Wave XLR Pro did not answer a read of its ducking block. |

On the Wave XLR Pro the jacks pass once a poll reads their feed in the
mix they play, which OpenXLR sets whenever a jack is a monitor output.
`monitorReturn` shows for the sweep before that read, or while another
program holds the feed out of the mix. The USB Aux port dips with its
mix, so it needs no rule of its own.
A target mix no output, virtual microphone or feed hears is left out of
these rules; a setup whose target mixes nobody hears runs on the unit with
no pairs ducked. The unit ducks the pairs `outputs` names: for `monitor`,
the pair of each jack feed that plays a target mix, so a jack on another
feed keeps its level. Each of the unit's inputs in `channels` is ducked in
the section of each pair it ducks (section 0 for the jacks' first feed, 18
and 54 for the others, 36 for the Aux mix), where the unit plays it
through its own path, such as a microphone's zero-latency path to the
jacks. The unit writes the union of the keys on the entry of every pair
and input it ducks, less the entry's own source, and no key anywhere else.

In [hardware mixing](#hardware-mixing) a target mix a jack plays, and the
Aux mix while the USB Aux port is on, is built by the unit from each
channel's own pair, and the unit ducks it per
channel instead, whatever `processing` says, since nothing on this
computer can lower a mix the unit builds. Each channel in `channels` is
ducked in the section of each such target mix (a summed feed's section
when either of its mixes is a target), and nowhere else: a channel that
streams on a pair at its pair, and the unit's own inputs (XLR 1, XLR 2,
Aux In, Line In) at their input's tap, as they come into that section. A
unit input software processes is ducked on the pair its processed audio
plays back on too. Every key must be one the unit hears: one of its
inputs, or a channel on a pair (source 4 + n for pair n), so an
application channel can key the unit here. The rules above do not apply
to these mixes: a channel heard there and not in `channels` keeps its
level, and so does every key. Only these rules do, giving `ready` false
and leaving the unit's mixes unducked: `keyNotSupported` (a key the unit
does not hear, such as a capture microphone: "Mic 3 is neither one of the
Wave XLR Pro's inputs nor on one of its USB pairs, so the unit cannot use
it as a key and the mixes it builds for its outputs are not ducked."),
`failed` and `unsupportedBlock`. Every other target mix (Stream,
Chat, a monitor mix no jack plays) and the computer's copy of
a unit-built mix another output also plays are ducked in software at the
same time, from the channels' sends. The two never meet, since a pair
carries a channel before its sends, so they need no hand-over. `backend`
reads `device` while the unit ducks, `outputs` names `monitor` for a jack's
section and `aux` for section 36, and
`readback.pairs` lists every pair ducked in any section. The amount and
speed are one per unit, so every pair dips by the same depth in every
section. A setup whose target mixes the unit does not build follows the
rules above, as outside the mode.

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

### Hardware mixing

Experimental. On the Wave XLR Pro the mixes its outputs play (Headphones 1,
Headphones 2 and Line Out, and the Aux mix on the USB Aux port) can be
built by the unit instead of this computer. Every channel then streams to the unit once, on a USB playback
pair of its own, and the unit mixes the pairs with its own inputs at zero
latency. `mixing` in `mixer.json` holds the choice, `software` (the
default) or `hardware`, and `setMixing` changes it live. The UI offers
the choice in Options (AUDIO, "Mixes on the Pro's outputs") and the
terminal mixer on its Options tab; both send the dry run first and switch
only after it. The Deck greys a key on a plugin of a mix the unit builds,
from `disabled`. Nothing of it has been heard on a unit yet.

**Pairs.** A channel the unit cannot take raw streams on a pair: System on
pair 0, Game on 1, Music on 2, Browser on 3, Voice Chat on 4 and SFX on 6.
Any other channel takes the first pair of 7, 5, 0, 1, 2, 3, 4 and 6 that
no channel and no return pair (below) holds.
Pair 5 carries the Aux mix's stream to the USB Aux port outside the mode;
in it the unit builds the Aux mix itself, so pair 5 is a channel pair like
the others. XLR 1, XLR 2, Aux
In and Line In are the unit's own inputs and need no pair while they carry
the unit's input. A strip bound to another source takes a pair, and the
capture input kept for the unit's microphone takes the unit's input
instead. A channel keeps its pair in `hardwarePairs` across restarts and
layout edits, and deleting the channel drops it. Eight channels fit, hidden ones included. A layout with
more is refused, and `note` says how many need a pair: "Hardware mixing on
the Wave XLR Pro streams at most 8 channels; this layout has 9 that need a
USB pair."

**Processed inputs and the cue.** The pairs no channel takes are claimed on
demand, in this order. First each of the unit's inputs that software
processes (noise removal, the voice changer, a software EQ, an insert, the
soft low cut or ClipGuard, or Mic setup running on it), in the order XLR 1,
XLR 2, Aux In, Line In: its channel's audio plays back to the unit on that
pair, a return pair, and the unit's mixes take the input from there,
processed, at its sends, instead of the raw input. Then the cue pair, which
plays soundboard auditions in the jacks' sections only, at the volume of
the jack's mixes, and never in the Aux mix. The order is: each channel's
kept pair and each return pair the running mode holds, the built-in
channels' own pairs, the other return pairs, the other channels in layout
order, then the cue. A pair is never taken from a
channel or an input that holds it, so a return pair is never handed to a
channel. A channel added while every pair is in use, as with seven
channels and a processed XLR 1, gets no pair. It carries no
`hardwarePair`, the jacks' mixes and the Aux mix leave it out, and
`disabled` carries its `pair:<id>` note. The computer's mixes keep it. It takes a
pair once one is free. An input whose processing starts while every pair
is in use reaches the unit's mixes raw, with its `processedMic:<id>`
note. A channel added while a pair is free takes it and the cue gives
way. A switch
between the raw input and its return pair is one block write that clears
the one and opens the other, with the pair linked before, so the voice is
never heard twice. Neither claim is kept in `hardwarePairs`. On a layout
of seven channels, one pair is left: a processed XLR 1 takes it and
auditions are refused.

**Sections.** Each distinct feed a selected jack plays takes one of the
unit's sections 0, 18 and 54, kept per feed in `hardwareSections`. A feed
that sums two mixes is a section of its own. While the USB Aux port is on
(`setAuxPortEnabled`), the unit builds the Aux mix in section 36, the one
the port plays, from the same pairs and inputs at the Aux mix's levels.
Aux In is never in it, since the port would send the second computer its
own audio back. The Aux mix then shows `hardwareSection` 36. With the port
off, section 36 is left as the unit holds it.

**Levels.** A source's level cell is the channel's level in the feed: the
send, the channel volume and the mix volume, in half-dB steps. A summed
feed adds its mixes' levels as amplitudes. A muted send, a muted mix, an
exclusive group holding the channel and a level of 0 take the source out
of the section; the monitoring pause takes every source out of the jacks'
sections and leaves the Aux mix playing. A mix the unit builds alone in
section 18, 36 or 54 (a jack's second or third feed of one mix, or the Aux
mix while the port plays it) takes its volume in the section's own level
byte and its mute in the section's mute bit instead (block 0x0001 bytes 100
to 105, [protocol notes, section 22](wave-xlr-pro-protocol.md)): its cells
hold the sends and the channel volumes alone. A mix volume or mute then
moves one byte and leaves the cells and memberships as they are, a mix
volume never takes a quiet send under the cells' -59.5 dB floor, and a
muted mix keeps its memberships behind the bit. The level byte attenuates
only, in quarter-dB steps down to -63.75 dB: a monitor mix above 100% adds
what is above 0 dB to the cells as before, and a mix at 0 or under -63.75
dB is muted. Section 0 keeps its mix volume in the cells, since byte 98 is
the level the jacks' entry offset and leaving rest on; a summed feed keeps
every mix's volume in its cells, with its section at 0 dB. The unit
plays a USB pair 7.94 dB below its cell, and a cell above 0 dB does not
make that up ([protocol notes, section 16](wave-xlr-pro-protocol.md)). So
the mode keeps the balance Wave Link has: nothing adds the 7.94 dB back, a
pair's cell stops at 0 dB and a unit input's at +12 dB. A level held at
its ceiling is listed in `clamped` as `{channel, feed, wantedDb, heldDb}`.

**Jack levels.** In the mode, section 0's level (block 0x0001 byte 98,
-21 dB) goes to 0 dB. In the same write each jack's own level, its
headphone volume or Line Out's level, drops by its `entryOffsetsDb`: 21 dB,
and 7.94 dB more while the jack's feed holds a unit input it heard through
the Monitor mix's USB pair before (Line In, Aux In, or a microphone the
direct path did not carry and that has no return pair). No source gets louder on any jack. In the mode
the output volume of a jack lowers its headphone volume and never raises
it, and the Pro's playback sink stays at 100%. Leaving gives back each
jack's whole entry offset, so a jack returns to the level it had before
the mode, moved by any change made in it, less what the sink above 100%
then adds to the pairs.

**Entering and leaving.** On the first sweep that finds the mode wanted
and nothing in its way, the daemon keeps what the unit holds (sections 0
and 36, byte 98, each jack's level less its entry offset, and the output
volume, and how far each jack was lowered) as `hardwareEntry` in `mixer.json`, and saves it before anything is
written. It then writes the unit's sections with every membership clear,
the jacks' levels lowered and byte 98 at 0 dB. Only then does the graph
take every mix off the jacks and the Aux mix off pair 5, hold the Pro's
playback sink at 100% and link each channel's sink to its pair. Last, the
memberships open and the jacks' selectors move to their sections. Leaving
runs the other way: memberships cleared (section 36's too while the port
plays it), the pairs unlinked, the sink back at its volume, the mixes
routed to the jacks and the Aux mix to pair 5 again, and then sections 0
and 36 and byte 98 put back, the levels and mutes of sections 18, 36 and
54 (bytes 100 to 105) at 0 dB and unmuted, and
each jack raised, after block 0x0001. `hardwareEntry` goes once a read
shows the unit put back. A daemon stopped in the mode writes nothing, and
the next start resumes the mode, or finishes leaving it. While the unit is
away, or something else is in the way for a moment, the mode holds as it
is; only `setMixing` with `software`, or `failed`, leaves it. A section 18
or 54 the mode takes is kept first in `hardwareRestore`, as a second jack
feed keeps it.

**While it runs.** A fader, mute, channel volume or mix volume moves the
unit's cells in the next write, or a section's level or mute byte for a
mix built alone in section 18, 36 or 54, at most one block write per
sweep, the Aux mix's in section 36 as well. The UI offers each mix's
maximizer on the mix's master while the unit builds it, and the terminal
mixer on its Outputs tab. The second computer hears the Aux mix with
the unit's inputs at their faders and the channels' pairs 7.94 dB under
theirs, the balance the jacks have; it no longer follows the output
volume, which only moves the jacks. The output volume of the jacks
(`setOutputVolume`, `adjustOutputVolume`, a desktop key on the Pro's sink)
moves their levels, and the state's `outputVolume` reads it. A headphone
volume moved on the unit, by its knob or by `set` with `hpVolumeDb`, is
taken as the jack's new base. Microphones play as the unit's inputs, or
from their return pairs while processed, so the direct path is not used.
Mic setup's Hear myself opens the microphone's sends in the unit's mixes
as it does in the graph. If the unit does not keep five writes in a row,
the mode leaves with `failed` until `setMixing` is sent again.

**Maximizer.** Each section has a maximizer (byte 14 of the section, the
amount 0 to 100, and the membership's bit 12, which bypasses it; desk
measurements of 2026-11-11): make-up gain of about 0.2 dB a step, 20 dB at
100, against a ceiling near -1 dBFS. `setMixMaximizer` sets a mix's amount,
and the mix's `maximizer` reports it. It runs on a mix the unit builds
alone in a section: a jack's feed of one mix, or the Aux mix while the
port plays it. Every other section the mode builds has it off, written as
the bypass set and the amount at 0. The desk measured the bypass dry at any
amount, so a mix is never limited unless its amount asks for it. A mix the unit builds only summed with another has a
`maximizer:<id>` sentence in `disabled`; a mix the computer builds keeps
its amount, which does nothing until the unit builds it alone. While it
runs and the `meters` message goes to a client, `max:<id>` carries the
maximizer's reduction in dB, from the same reads of block 0x0002 as the
mix meters. Leaving the mode puts each section's byte 14 and bit 12 back
as they were, with the rest of the section.

**Mix meters.** A mix the unit builds alone in a section, a jack's feed of
one mix or the Aux mix while the port plays it, reads that section's level
from the unit's meters (block 0x0002) as its `mix:<id>` meter. The section
level comes after the faders, channel volumes and mix volume and before
the jack's headphone volume or Line Out level; for a mix with its volume
and mute in its section's own bytes, it comes after those (desk,
2026-11-11). The output volume moves neither this meter nor the
computer's. The unit measures peaks, which
read higher than the computer's average on the same audio, 3 dB higher on
a sine. Each part of a summed feed, and every mix the computer builds,
reads the computer's copy, which keeps running for any other output that
plays the mix.

The device loop reads the unit's meters only while the mode runs and the
`meters` message goes to a client, and stops a second after the last
frame. It reads them with each poll and once more halfway to the next,
about 20 times a second. A frame with no fresh reading carries the
computer's copy: the first one after a client connects, and any after a
failed read. The input channels (`ch:xlr1`, `ch:xlr2`, `ch:aux`,
`ch:line`) keep their meters on this computer. They read each input as
the unit sends it over USB, before the channel volume, as in software
mixing.

**Refused while it runs.** `soundCheck` (other than `stop`),
`setSoundCheckRaw` and `setSoundCheckHearMyself` with true are refused for
a microphone with no pair for its take, with the `soundCheck:<id>` or
`soundCheck` sentence, unless a session already runs on it.
`auditionClip` is refused with the `audition` sentence while there is no
cue pair, `setInserts` with a chain for a mix a jack plays with its
`inserts:mix:<id>` sentence, and `setEq`, `setEqEnabled`, `setEqBand` and
`loadEqProfile` on a mix the unit builds with its `eq:mix:<id>` sentence. `createChannel`, `createCaptureChannel` and a `setInputBinding`
that would put more than eight channels on pairs are refused with the
budget sentence; one that fits while every pair is in use is made, and
the channel gets no pair (see **Processed inputs and the cue**). `setMixing` with `hardware` is refused with that sentence while
the layout has more channels than pairs. Ducking on a mix the unit builds,
the Aux mix included while the port is on, runs on the unit per channel
(see [where ducking runs](#where-ducking-runs)); on mixes the computer
builds it runs as before.

**Eligibility.** `reason` names the first thing in the way, whether or not
the mode is wanted: `mixerOff`, `notSupported` (the driven unit is not a
Wave XLR Pro), `unitAway`, `firmware` (older than 1.0.14), `unsupportedBlock`
(block 0x0001 does not decode), `captureLayout` (the unit's playback sink
is not 17 channels or its capture source not 18), `budget` and `failed`.
`note` says it in one sentence.

**What the mode turns off.** `disabled` maps fixed keys to the sentence a
client shows: `inserts:mix:<id>` and `eq:mix:<id>` for each mix the unit
builds (a jack's feed, and the Aux mix while the port is on), since the
unit's copy runs no insert and no EQ, and the computer's copy holds its EQ
off; `maximizer:<id>` for a mix the unit builds only summed with another,
which runs no maximizer; while the unit builds a mix, `ducking:key` (the unit keys the
ducking of its mixes only from its own inputs and the channels on its
pairs, so a capture microphone cannot key it); `latencyCompensation`;
`processedMic:<id>` for a unit input with software processing and no
return pair, which the unit's mixes take as it comes in; `pair:<id>` for
a channel that needs a USB pair and found none, which the unit's mixes
leave out;
`soundCheck:<id>` for XLR 1 or XLR 2 when no pair is left for its take,
and `soundCheck` when that holds for both; `audition` while there is no
cue pair. Mixes the computer builds (Stream, Chat, and any mix only other
outputs play) keep everything.

**Profiles.** A profile's `hpVolumeDb` and `hp2VolumeDb` are the jacks as
software mixing plays them. `loadProfile` and recall on connect write them
lowered by each jack's entry offset and the output volume while the mode
runs, and `saveProfile` stores the levels leaving would give, so a profile
recalls the same loudness in either mode. A jack the mode plays louder than
software mixing can saves above 0 dB, in quarter-dB steps, and recalls at
the same level in the mode. Outside the mode, `loadProfile` and recall on
connect write such a level as 0 dB. The state's `hpVolumeDb` and `hp2VolumeDb` stay what the
unit holds.

**State.** Once the daemon has planned the mode, the mixer state carries
`hardwareMixing {wanted, active, reason, note}`. While the mode runs it
also carries `pairs`, `returnPairs` (each processed unit input to its
return pair), `cuePair` (or null), `sections`, `jacks`, `clamped` and
`disabled`, each channel on a pair or a return pair carries
`hardwarePair` (a channel left without a pair carries none), and each mix a jack plays carries `hardwareSection`. `capabilities.hardwareMixing` is true on the
Wave XLR Pro.

**Dry run.** `{"cmd":"setMixing","value":"hardware","check":true}` is
answered with a `mixingPlan` message: the fields above for the layout as
it stands, plus `cells`, each section's twelve level cells and membership
mask as hex (bytes 0 to 13), and `entryOffsetsDb` by jack. It changes
nothing and also answers while the mixer is off.

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
