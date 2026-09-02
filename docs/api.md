# WebSocket API

The daemon serves `ws://127.0.0.1:37890/ws`. The packages reserve that
port from the kernel's ephemeral range; if it is busy at startup the
daemon waits for it instead of touching PipeWire.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | device state, capabilities, mixer state, the device list, the app registry, profile names |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | installed LV2 plugins and their input-control metadata (range, default, type hints, scale points and unit) |
| `error` | when a command is rejected | `message` |

Commands are single JSON objects with a `type` field:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (limiter at -3 dB) |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mix feeds |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `listPlugins` | none | the installed LV2 plugins, answered with a `plugins` message |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2` or `mix:<id>`, each insert is `{id, kind:"lv2", plugin:<uri>, label?, bypass?, params?}` |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control by its LV2 port symbol; the daemon rejects unknown ports and clamps/quantises values to the declared contract |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen) |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device |
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
- `gainlock.json`: which devices have the gain lock set
- `daemon.json`: the daemon's own preferences, read once at start.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). Written
  by the UI's Options window.
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)
