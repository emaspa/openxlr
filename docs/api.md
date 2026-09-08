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
TOKEN=$(cat "${XDG_RUNTIME_DIR:-$HOME/.config}/openxlr/token")
printf '{"cmd":"auth","token":"%s"}\n{"cmd":"getState"}\n' "$TOKEN" | websocat ws://127.0.0.1:37890/ws
```

Limits. Commands are validated before the mixer sees them: unknown
channel or mix ids, plugins that are not installed or need a host
feature the PipeWire chain lacks, undeclared parameter symbols,
non-finite numbers, and over-long strings or lists all come back as an
`error` message instead of being silently ignored. A client may send
bursts of up to 300 commands and a sustained 100 per second; beyond
that it is disconnected with close code 1008. At most 32 clients can be
connected at once. The plugin catalog is bounded too: a plugin with a
URI over 512 characters, more than 4096 ports, or one that would push
the catalog past 7 MiB is left out of `plugins` altogether, and at most
512 controls, 256 scale points and 64 required features are read per
plugin; a plugin that is listed with `supported: false` is a different
case, one the chain host cannot run.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `warning` (one sentence the user should see, or null: mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, or a device set aside after three hung USB transfers in one run). In the mixer state, each channel carries `hardware` (true for the fixed input channels), `renamedSinceStart` says a virtual microphone was renamed since the daemon started (its PipeWire device keeps the old name until a restart), and `layoutWarning` is a sentence for the layout editor when pipewire-pulse nears its open-file limit, or null |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | the installed LV2 plugins with their controls; `supported` is false, with `unsupportedFeatures` listed, for a plugin that needs a host feature the PipeWire chain lacks |
| `pluginSetup` | in answer to `getPluginSetup` | where installs go (`lv2Directory`, `clapDirectory`, `vst3Directory`), `hostInstalled`, `yabridge` (its version, or null when not installed), `wine`, `windowsDirectories` (the folders yabridge bridges) and `wineFolders` (Wine's own plugin folders that hold a plugin and are not bridged yet, offered as one press since a file dialog hides them) |
| `pluginInstall` | in answer to `installPlugin`, `syncWindowsPlugins` and `rescanPlugins` | `ok`, `message` (a sentence or two for the user), `installed` (the bundles or folders put in place), `added` (plugins in the catalogue that were not before) and `total` |
| `error` | when a command without a `requestId` is rejected | `message` |
| `commandResult` | in answer to a command that carried a `requestId` | `requestId`, `error` (null on success); preceded by the state the result refers to |

Commands are single JSON objects with a `cmd` field. The layout commands
(`createChannel` through `setLayoutOrder` below) succeed only after the
new layout is written to `mixer.json`; a failed write restores the previous
layout and answers with an error. Any command may carry a `requestId` of up to 64 characters; the
daemon then answers with a `commandResult {requestId, error}` message after
the state that reflects the outcome (`error` is null on success) instead of
a bare `error` message, so an editor can wait for the acknowledgement:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `createChannel` | `name` | add an application channel, muted in every mix, without touching existing nodes; its generated stable id is in the next state. Undone with an error when its sends have not appeared within 3 s |
| `renameChannel` | `channel`, `name` | rename an application channel; its playback device is reloaded under the new name and the streams on it are put back (a short gap on that channel only) |
| `deleteChannel` | `channel` | remove an application channel; apps and remembered assignments on it move to the first remaining application channel. The last application channel cannot be removed |
| `createMix` | `name` | add a virtual microphone; every channel gets a muted send into it before the capture device is published. Undone with an error when a channel's send has not appeared within 3 s |
| `renameMix` | `mix`, `name` | rename a virtual microphone in OpenXLR; the PipeWire device keeps its old description until the daemon restarts (reloading it would throw recording apps off), and the mixer state's `renamedSinceStart` says so |
| `deleteMix` | `mix` | remove a virtual microphone with its sends, inserts and capture device |
| `setLayoutOrder` | `channels[]`, `mixes[]` | complete ordered lists of application-channel and virtual-microphone ids; structural nodes stay fixed |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mixes feed; a newly listed output is fed by the first monitor mix |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setMonitorFeed` | `device`, `mix` | what feeds one selected output: `monitor` (Monitor A), `monitor2` (Monitor B), or both summed as `monitor+monitor2` (Monitor A+B); the Pro's own jacks follow one feed together. The state's `monitorFeeds` lists the exceptions from the first mix in the same form. An error when the feed names anything but distinct monitor mixes, or the output is not selected |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `listPlugins` | none | the installed LV2 plugins, answered with a `plugins` message |
| `getPluginSetup` | none | where plugins are installed and what bridges Windows ones, answered with a `pluginSetup` message |
| `installPlugin` | `path` | install what is at an absolute path the user picked: a `.clap` or single-file `.vst3` is copied into `~/.clap` or `~/.vst3`, a `.vst3` or `.lv2` directory into `~/.vst3` or `~/.lv2`, a plain directory installs every plugin inside it, and a Windows VST3 or CLAP plugin has its directory added to yabridge and synced. Archives, installers and VST2 files are refused with a message that says what to do. The catalogues are read again before the `pluginInstall` answer |
| `syncWindowsPlugins` | none | run yabridge's sync over the folders it knows, then read the catalogues again; answered with `pluginInstall` |
| `rescanPlugins` | none | read the plugin directories again, for plugins installed by other means; answered with `pluginInstall` |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2` or `mix:<id>`, each insert is `{id, kind, plugin, label?, bypass?, params?}` where `kind` is `"lv2"` with the plugin URI, `"clap"` with the plugin's id, or `"vst3"` with the class id as 32 hex digits; a CLAP or VST3 insert always runs in the native host, so its `nativeHost` reads true whatever was sent |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by its LV2 port symbol |
| `showInsertUi` | `channel`, `insertId` | open an enabled insert's native editor when the optional host is installed |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen); `channel: "ignore"` stops managing it, its streams go back to the system default output and stay wherever the desktop routes them |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app; `ignore` works here too |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device |
| `setRecallOnConnect` | `name` | the profile recalled whenever the active device connects fresh (daemon start, replug, switch to it); empty clears it. With none chosen, a device whose capabilities say `retainsSettings: false` gets the last settings the daemon saw on it instead |
| `resetDevice` | none | write the firmware defaults back to a device without settings memory and forget its last settings (an error until the daemon has seen the device connect after a power cycle once); on the Wave XLR Pro, which keeps its own settings, write OpenXLR's baseline instead: gain 30 dB on both inputs, every processing stage and phantom off, headphones and aux level at half, the crossfade fully on PC, routing untouched, refused while the gain lock is on. The capabilities say `builtInDefaults` when a model has a baseline |
| `getDiagnostics` | none | vendor block dump for bug reports |

Application identities use playback-node metadata, falling back to the
owning PipeWire client's application name and process binary when absent.
Windows executable names normalize to the same key as their Wine/Proton
client, for example `Balatro.exe` becomes `balatro`. `assignApp` and
`forgetApp` also accept those legacy executable-name identities. When
loading conflicting old and normalized overrides, the normalized key wins.

Insert definitions optionally carry `nativeHost: true` to select the native
LV2 helper for that insert. Missing or false keeps PipeWire filter-chain, even
when the helper is installed. Unsupported native selections are rejected.
Changing this choice via `setInserts` rebuilds the chain and can interrupt audio.

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
- `devices/<vid-pid>/last-state.json`: for a device without settings
  memory, the hardware settings the daemon last saw, written a second
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
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)
