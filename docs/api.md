# WebSocket API

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
that the daemon rewrites at every start. Nothing is sent before it;
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
connected at once.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes), `recallOnConnect` (the profile recalled when the device connects, or null), `warning` (one sentence the user should see, or null: mixer settings that cannot be written to disk, which the daemon keeps retrying with backoff, or a device set aside after three hung USB transfers in one run) |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | the installed LV2 plugins with their controls; `supported` is false, with `unsupportedFeatures` listed, for a plugin that needs a host feature the PipeWire chain lacks |
| `error` | when a command is rejected | `message` |

Commands are single JSON objects with a `cmd` field:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mixes feed; a newly listed output is fed by the first monitor mix |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setMonitorFeed` | `device`, `mix` | which monitor mix (`monitor` for Monitor A, `monitor2` for Monitor B) feeds one selected output; the Pro's own jacks follow one feed together. The state's `monitorFeeds` lists the exceptions from the first mix. An error when the mix is not a monitor mix or the output is not selected |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `listPlugins` | none | the installed LV2 plugins, answered with a `plugins` message |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2` or `mix:<id>`, each insert is `{id, kind:"lv2", plugin:<uri>, label?, bypass?, params?}` |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by its LV2 port symbol |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen); `channel: "ignore"` stops managing it, its streams go back to the system default output and stay wherever the desktop routes them |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app; `ignore` works here too |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device |
| `setRecallOnConnect` | `name` | the profile recalled whenever the active device connects fresh (daemon start, replug, switch to it); empty clears it. With none chosen, a device whose capabilities say `retainsSettings: false` gets the last settings the daemon saw on it instead |
| `resetDevice` | none | write the firmware defaults back to a device without settings memory and forget its last settings; an error until the daemon has seen the device connect after a power cycle once |
| `getDiagnostics` | none | vendor block dump for bug reports |

The OpenDeck plugin in `plugin/` is a client of this API; the command
handler is `WebSocketHub.cs` and the message shapes are in
`Protocol.cs`, both under `src/OpenXLR.Daemon/`.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `mixer.json`: every mixer decision: levels, mutes, device choices, the
  app registry, enforced defaults, the software low cut, the insert
  chains. Written by the daemon.
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
- `daemon.json`: the daemon's own preferences, read once at start.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). Written
  by the UI's Options window.
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)
