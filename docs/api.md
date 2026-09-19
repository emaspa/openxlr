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
port alone does not hand them the mixer. The window and the OpenDeck
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
parameters are rejected. A client may send bursts of up to 300 commands
and a sustained 100 per second; beyond that it is disconnected with
close code 1008. At most 32 clients can be connected at once. The
`plugins` message is bounded too: a plugin with a URI over 512
characters, more than 4096 ports, or one that would push the message
past 7 MiB is left out of `plugins` altogether, and at most 512
controls, 256 scale points and 64 required features are read per plugin;
a plugin that is listed with `supported: false` is a different case, one
the chain host cannot run. The native host's own protocol is bounded the
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

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `warning` (one sentence the user should see, or null: mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, or a device set aside after three hung USB transfers in one run). In the mixer state, each channel carries `hardware` (true for the fixed input channels), `captureSource` (exact external source name or null), `capturePair` (zero-based pair), and `captureConnected` (its capture route exists). A capture channel is editable but cannot receive application assignments; `renamedSinceStart` says a virtual microphone was renamed since the daemon started (its PipeWire device keeps the old name until a restart), and `layoutWarning` is a sentence for the layout editor when pipewire-pulse nears its open-file limit, or null. Each mix carries `id`, `name`, `volume`, `muted` and `kind` (`monitor`, `virtualMic` or `auxPort`), which gives a client the mix's volume ceiling; `outputVolume` is the first selected output's volume, 0 to 1.5, or null with no output selected. In `devices`, every entry that is a sink carries `volume` (desktop scale, 1.0 = 100%) and `muted`; sources and the Wave XLR Pro pseudo-outputs omit both. A state is pushed whenever a sink's volume or mute changes |
| `diagnostics` | in answer to `getDiagnostics` | `blocks`, mapping vendor block names to hex strings or read errors |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | the installed LV2, CLAP and VST3 plugins with their controls, within the message size limit above and always including the plugins the saved chains use; `supported` is false, with `unsupportedFeatures` listed, for a plugin that needs a host feature the PipeWire chain lacks. `audioIns` and `audioOuts` are the plugin's own port counts, or for VST3 its main buses' default width; a VST3 entry also carries `widths`, the chain widths in channels (1 and 2 are the ones the host carries) its main buses accepted when the helper asked the way the host asks at load, so a plugin that reports 2 and lists 1 in `widths` can be inserted on a mono input. An entry without `widths` (LV2, CLAP, or a description an older helper wrote) fits a mono input with one port each way and a stereo mix with two or more |
| `pluginSetup` | in answer to `getPluginSetup` or `setPluginWineTrace` | where installs go (`lv2Directory`, `clapDirectory`, `vst3Directory`), `hostInstalled`, `wineTrace` (deep tracing enabled in the running daemon), `yabridge` (its version, or null when not installed), `wine`, `windowsDirectories` (the folders yabridge bridges) and `wineFolders` (Wine's own plugin folders that hold a plugin and are not bridged yet, offered as one press since a file dialog hides them), `memoryLockLimitBytes` (the running daemon's soft limit in bytes; -1 means unlimited, null means unknown) and `memoryLockNote` (recovery advice when Windows plugin support is available and the limit is below 256 MiB, otherwise null) |
| `windowsPluginFiles` | in answer to `getWindowsPluginFiles` | `ok`, `message` and `plugins`; each plugin file carries `path`, `name`, `format`, `enabled`, `canDelete`, `winePrefix` and `inUse`. Excluded files remain listed |
| `nativeEditorRules` | in answer to `getNativeEditorRules` or `setNativeEditorRule` | `rules` with `kind`, `plugin`, `name`, `reason`, `defaultBlocked`, `override` and effective `blocked`; `error` describes a refused change or unreadable configuration |
| `nativeEditorRulesChanged` | after a successful rule change | notification to refresh the catalogue and editor availability; no plugin rescan is needed |
| `pluginDiagnostics` | in answer to `getPluginDiagnostics` | `discovery`: daemon host/controller paths, Wine prefix, architecture, effective search paths, bounded `yabridgectl status` output and latest completed CLAP/VST3 scan reports. A scan entry that failed carries `logId`, the name of the file holding that attempt's bounded scanner output, or `logNote` saying why there is none; both are absent from an entry that did not fail and from one an older daemon recorded. `scanLogs` gives the `directory` those files are in and the bounds they are kept under (`stderrCapBytes`, `traceStderrCapBytes`, `traceCaptureBytes`, `stdoutCapBytes`, `maxFiles`, `maxTotalBytes`). Reading the reply or the directory scans nothing and starts no process |
| `pluginInstall` | in answer to `installPlugin`, `addWindowsPluginFolder`, `removeWindowsPluginFolder`, `removeWindowsPluginInserts`, `setWindowsPluginEnabled`, `deleteWindowsPlugin`, `syncWindowsPlugins` and `rescanPlugins` | `ok`, `message` (a sentence or two for the user, ending with the bundles the scan that followed could not read, up to three by name and the rest as a count), `installed` (the bundles or folders put in place), `added` (plugins in the catalogue that were not before) and `total` |
| `error` | when a command without a `requestId` is rejected | `message`; for the mixer commands a `state` follows, so an optimistic edit can be reverted |
| `commandResult` | in answer to a command that carried a `requestId` | `requestId`, `error` (null on success); preceded by the state the result refers to |

Meter readings stay finite when an audio source emits NaN or infinity: an
invalid sample contributes silence to its channel's RMS calculation, without
changing the other channel or later valid frames. This protects metering and
its JSON messages; it does not modify the audio signal sent to outputs.

Commands are single JSON objects with a `cmd` field. The layout commands
(`createChannel` through `setLayoutOrder` below) succeed only after the
new layout is written to `mixer.json`; a failed write restores the previous
layout and answers with an error. Any command may carry a `requestId` of up to 64 characters; the
daemon then answers with a `commandResult {requestId, error}` message after
the state that reflects the outcome (`error` is null on success) instead of
a bare `error` message, so an editor can wait for the acknowledgement.
Failed plugin operations retain their typed `pluginInstall` or
`windowsPluginFiles` reply with `ok:false` and also report the failure in
that final acknowledgement (or an `error` without a request id):

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `createCaptureChannel` | `name`, `source`, optional `capturePair` | add an external PipeWire capture input, muted in every mix; exact source name, zero-based stereo pair 0 to 31 (default 0). See [capture inputs](mixer-layout.md#capture-inputs) |
| `createChannel` | `name` | add an application channel, muted in every mix, without touching existing nodes; its generated stable id is in the next state. Undone with an error when its sends have not appeared within 3 s |
| `renameChannel` | `channel`, `name` | rename an application channel; its playback device is reloaded under the new name and the streams on it are put back (a short gap on that channel only) |
| `deleteChannel` | `channel` | remove an application channel; apps and remembered assignments on it move to the first remaining application channel. The last application channel cannot be removed |
| `createMix` | `name` | add a virtual microphone; every channel gets a muted send into it before the capture device is published. Undone with an error when a channel's send has not appeared within 3 s |
| `renameMix` | `mix`, `name` | rename a virtual microphone in OpenXLR; the PipeWire device keeps its old description until the daemon restarts (reloading it would throw recording apps off), and the mixer state's `renamedSinceStart` says so |
| `deleteMix` | `mix` | remove a virtual microphone with its sends, inserts and capture device |
| `setLayoutOrder` | `channels[]`, `mixes[]` | complete ordered lists of editable-channel and virtual-microphone ids; structural nodes stay fixed |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters; monitor volume range 0 to 1.5, other mixes 0 to 1; values outside the range are clamped |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mixes feed; a newly listed output is fed by the first monitor mix |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setMonitorFeed` | `device`, `mix` | what feeds one selected output: any existing mix id, including `stream`, `chat`, `auxout` and custom virtual microphones, or distinct ids joined with `+` to sum them. The Pro's own jacks follow one feed together. The state's `monitorFeeds` lists exceptions from the first monitor mix in layout order. Unknown or repeated mix ids and unselected outputs are rejected. Deleting the last included mix returns that output to the first monitor mix; deliberately silent matrix outputs stay silent |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputRoute` | `device`, `mix`, `value` | one mix's send into a selected output, 0 to 1. A positive value adds or adjusts the route; zero disconnects it. Values outside that range, non-finite values, unknown mixes and unselected outputs are rejected. Physical jacks sharing a bus change together |
| `setOutputVolume` | `value` | volume of the selected monitor devices, 0 to 1.5; the range the devices themselves take, so a desktop level above unity can be held and written back unchanged. Values outside it are clamped, and the state reports what reached the devices. With no output selected the command succeeds and changes nothing |
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
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2` or `mix:<id>`, each insert is `{id, kind, plugin, label?, bypass?, params?}` where `kind` is `"lv2"` with the plugin URI, `"clap"` with the plugin's id, or `"vst3"` with the class id as 32 hex digits; a CLAP or VST3 insert always runs in the native host, so its `nativeHost` reads true whatever was sent. An insert being added is refused when its plugin cannot run at the chain's width (one channel on an input, two on a mix, by `widths` or the port counts as `plugins` describes them); an insert already in the chain, the same plugin under the same id, is left to the chain builder, so one can always be removed; an id kept while its `kind` or `plugin` changes counts as an addition |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by the catalogue's `symbol` (LV2 port symbol or decimal CLAP/VST3 parameter id); use catalogue ranges and scale points. Refused when the insert is not in the chain or the catalogue does not declare the symbol for its plugin |
| `getNativeEditorRules` | none | read release defaults and explicit user overrides for native editor compatibility |
| `setNativeEditorRule` | `kind`, `plugin`, `name?`, `blocked?` | set `blocked:true` to use OpenXLR controls, `false` to allow the native editor, or null/absent to remove the override and follow release defaults. Saved atomically before success; answered with `nativeEditorRules` |
| `showInsertUi` | `channel`, `insertId` | open an enabled insert's native editor when the optional host is installed and the editor policy allows it; a blocked editor is refused without changing the audio instance |
| `adjustOutputVolume` | optional `device`, `value` | change a PipeWire output by desktop percentage points (`0.05` is 5%). Finite steps from -0.5 to 0.5, final volume clamped to 0 through 1.5. Omit `device` for the current desktop default |
| `setOutputDeviceVolume` | optional `device`, `value` | set a PipeWire output's desktop volume; `value` finite, 0 to 1.5 on the desktop scale (1.0 is 100%), rejected outside that range. A monitor mix sink is set through its mix master; a selected monitor output follows the linked monitor-volume behaviour. Omit `device` for the current desktop default; device names are accepted and rejected as for `adjustOutputVolume` |
| `toggleOutputMute` | optional `device` | toggle an output's mute; omit `device` for the current desktop default. On one of the selected monitor outputs it toggles the mute of the mixes feeding that output (what pressing a Deck dial on the monitor does), so the mixer window, dial rings and keys agree; on a monitor mix sink it toggles that mix; on any other output it toggles the sink's mute at the audio server |
| `setMainOutput` | `device` | select and enforce an available PipeWire output as the system default, or `@monitor` for the first selected monitor output. Retains capture-default policy and mixer feeds |
| `routeFocusedApp` | `channel` | route the focused KDE application to an application channel and remember the assignment; requires the running UI with Desktop keys enabled and `gdbus`. Missing or ambiguous process identity is an error, with no guessed routing |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen); `channel: "ignore"` stops managing it, its streams go back to the system default output and stay wherever the desktop routes them |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app; `ignore` works here too |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold; `sink: "@monitor"` follows the first selected monitor output |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device; loading restores saved gain even while locked and leaves the lock enabled |
| `setRecallOnConnect` | `name` | the profile recalled whenever the active device connects fresh (daemon start, replug, switch to it); empty clears it. With none chosen, a device whose capabilities say `retainsSettings: false` gets the last settings the daemon saw on it instead |
| `resetDevice` | none | write the recorded defaults back to a device using connect-time restoration and forget its last settings (an error until the daemon has seen the device connect after a power cycle once); on the Wave XLR Pro, which keeps its own settings, write OpenXLR's baseline instead: gain 30 dB on both inputs, every processing stage and phantom off, headphones and aux level at half, the crossfade fully on PC, routing untouched, refused while the gain lock is on. The capabilities say `builtInDefaults` when a model has a baseline |
| `getDiagnostics` | none | vendor block dump for bug reports |

When `loadProfile` writes the device settings but the mixer settings fail, the
error says the device settings were applied and gives the mixer error. A
profile file that fails validation, or that cannot be parsed, is refused
before anything is applied; the error starts with `profile '<name>':`. A bad
mixer field is named; a non-finite device level is reported as `Saved device
levels must be finite numbers.` without one.

`mixer.outputRoutes` lists per-route gain exceptions as
`{"device":"alsa_output.headset","mix":"chat","level":0.5}`. A mix included
in `monitorFeeds` but absent from this list uses unity gain. Shared Pro jack
gains use `device#bus`. An explicit empty string in `monitorFeeds` means the
output is silent; it is distinct from an absent entry, which selects the first
monitor mix. Zero in `setOutputRoute` removes that mix from the feed and its
gain exception. Removing an output drops its gains. Levels use the desktop
volume percentage scale, with the same whole-percent precision as `pactl`
writes elsewhere in the mixer. Profile scenes and mixer settings preserve
the route gains. Like other fader edits, saving is debounced and retried.

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
sends and other mixes unchanged. Monitor gain is applied once, at the mix
sink before its inserts. Non-monitor masters still apply to the channel
sends and retain their 0 to 1 range.


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
`yabridge` is the companion package version.
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

The OpenDeck plugin in `plugin/` is a client of this API; the command
handler is `WebSocketHub.cs` and the message shapes are in
`Protocol.cs`, both under `src/OpenXLR.Daemon/`.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `mixer.json`: every mixer decision: the layout (`userChannels`,
  `userMixes`, see [mixer-layout.md](mixer-layout.md)), levels, mutes,
  device choices, the app registry, enforced defaults, the software low
  cut, the insert chains. Written by the daemon.
- `profiles/<vid-pid>/<name>.json`: the named scenes, one file each
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
- `daemon.json`: the daemon's own preferences, read once at start.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). Written
  by the UI's Options window.
- `bridge/yabridgectl/config.toml`: the managed companion's folder registry,
  separate from the system yabridgectl configuration. Private wrappers live
  under `$XDG_DATA_HOME/openxlr/yabridge` (default `~/.local/share/openxlr/yabridge`).
- `native-editors.json`: native editor compatibility overrides. Format
  `{"version":1,"overrides":[{"kind":"vst3","plugin":"<class-id>","name":"Plugin name","blocked":true}]}`.
  Release defaults are not copied into this file. Remove an override through
  `setNativeEditorRule` with null `blocked` to follow release defaults again.
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)

## Plugin discovery diagnostics

`getPluginDiagnostics` reads the daemon's environment, not the UI's shell.
`discovery` includes `controller`, `wineExecutable`, `winePrefix`,
`sourceCommit` for a managed bridge, `hostExecutable`, `hostInstalled`,
`processArchitecture`, `searchPaths` and `scans`. `searchPaths.lv2Override`
is the explicit `LV2_PATH` or null for lilv defaults; `clap` and `vst3`
contain up to 64 effective search directories, including private wrappers.

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
`timedOut`, `truncated`, `output` and `error`, or just `error` when the
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
and sets the level outright. A mute toggle on one of the selected monitor
outputs toggles the mixes feeding that output, so the mixer window, the Deck
dial rings and the keys agree; on a monitor mix sink it goes through the
existing mix setter, so state and graph updates follow the same path as the
mixer mute control; on any other output it uses pipewire-pulse's atomic
toggle. The daemon pushes state whenever a sink's volume or mute changes.

### Plugin latency and optional mix alignment

`setMixLatencyCompensation` takes a boolean `value`. It saves the mixer-wide
preference before acknowledgement; it defaults to false and is not part of a
profile scene. Changing it rebuilds plugin paths and briefly interrupts audio.
The same command is available over the HTTP command transport.

Each insert status includes nullable `latencyMilliseconds`: the plugin's live
algorithmic latency, or null while not reported, stopped or unavailable. A bypass
reports zero. LV2 metadata can declare that a plugin has no latency port; those
running inserts report zero. Native LV2, CLAP and VST3 hosts report samples at
their active sample rate. LV2 latency ports are measured in an isolated host
when compensation is enabled and that host supports the plugin's required
features. The saved native-editor preference is not changed.

Mixer state exposes `compensateMixLatency`, `mixDelayMilliseconds` (mix id to
applied delay) and nullable `mixLatencyError`. With valid reports from every
active mix insert, each mix receives the difference between its total insert
latency and the slowest mix's total. Unknown reports or a total above 2000 ms
disable alignment for all mixes and expose a reason, rather than treating
unknown as zero or truncating the requested delay. Input inserts precede the
fan-out and are reported but are not separately aligned against other inputs.
Device latency, transport/resampler offsets, intentional echo effects and the
hardware direct-monitor path are outside this algorithmic mix alignment.
