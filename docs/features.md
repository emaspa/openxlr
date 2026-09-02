# Features

What OpenXLR does, area by area, and how. The README has the summary.

## Hardware control

Controls are reached over each device's USB protocol; the per-control
verification state is in [hardware-support.md](hardware-support.md).

Wave XLR Pro:
- Per XLR input: gain (0 to 80 dB), mute, low cut, expander, voice tune
  with strength, phantom power, ClipGuard, compressor
- USB Aux input stage: level (0 to -60 dB) and level lock
- Both headphone outputs: independent volumes, low-impedance mode
- Mic/PC direct-monitor crossfade (inside the device, no host latency)
- Physical output routing: Headphones 1, Headphones 2, Line Out and USB
  Aux are each switched in the device's hardware mixer

Wave XLR MK.2 (from captures, not run on hardware): gain, mute, low cut,
expander, voice tune with strength, headphone volume, low impedance,
crossfade.

Wave XLR: gain, mute, headphone volume, low impedance, phantom power.

XLR Dock: gain, mute and headphone volume through the kernel's standard
ALSA controls, plus phantom power and headphone low impedance over the
original Wave XLR's protocol dialect, which the dock also answers. The
phantom byte was identified by the
[openwave](https://github.com/rikkichy/openwave) project on the Wave XLR
([openwave PR #8](https://github.com/rikkichy/openwave/pull/8)) and
confirmed on the dock with a condenser microphone. Wave Link does not
write it for the dock. The dock has no onboard DSP; Wave Link runs those
effects host-side, and on Linux the submixer provides them (below).

## Software controls

For devices without the hardware version, the PipeWire layer provides:
- Low cut: a high-pass at 80 or 120 Hz (the two values Wave Link
  offers), a filter-chain node inserted between the mic and its channel,
  cycled from a button on the XLR 1 strip. Its response was measured
  with test tones as a second-order high-pass. The node is re-created
  if it disappears from the graph.
- ClipGuard: a hard limiter at -3 dB in the same filter chain, so a
  loud transient cannot clip the recording. Needs the swh-plugins LADSPA
  package.
- Gain lock: the daemon rejects every gain change while the lock is set,
  from any client, and stores the lock per device in `gainlock.json`.
  Shown only for devices without a physical gain dial, which would
  bypass it.

These controls appear only when the active device lacks the hardware
version, so a signal is never filtered twice.

Two behaviours apply on multi-device switching: the mixer's hardware
input channels follow the active device, and after a switch the
hardware channels' monitor sends come up muted, so the newly patched mic
does not reach the speakers until unmuted.

## Submixer

Built from PipeWire nodes, no kernel modules or custom drivers:
- Channels for the hardware inputs (XLR 1, XLR 2, Aux In) and for
  application groups (Game, Music, Browser, System, Voice Chat, SFX)
- Four mixes: Monitor (what you hear), Stream and Chat (published as the
  capture devices `OpenXLR Stream` and `OpenXLR Chat`, selectable in OBS
  or Discord like a microphone), and Aux (what a second computer on the
  USB Aux port receives)
- Per-channel, per-mix send levels and mutes; per-mix masters
- The monitor mix can play on several outputs at once, hardware outputs
  included
- Level meters throughout, dB-scaled, pushed at 15 Hz

Each channel is a combine sink with one internal stream per mix; that
stream's volume is the send fader. The 9 by 4 matrix is 13 sinks and no
loopback processes. Details in [architecture.md](architecture.md).

## Inserts

LV2 plugins in the signal path. Each XLR input carries a mono chain and
each mix (Monitor, Stream, Chat, Aux) a stereo one. An Inserts row
under the channel or mix lists what is loaded: a green or red LED for
active or bypassed, a bypass button, and a gear that opens the plugin's
controls in their own window. The picker shows every installed LV2
plugin that fits the slot (mono for inputs, stereo for mixes), grouped
by category. The controls window is generated from the plugin's port
descriptions: toggled ports become switches, enumerations become named
selectors, integer ports use stepped controls, logarithmic ports get an
appropriate response curve, and the declared unit is shown beside the
value. Controls are grouped by parameter family and include the port's
range, default, and symbol as a tooltip, plus a Defaults button. Chains
are saved with the mixer and recalled by profiles.

Every chain is a PipeWire filter-chain node, the same mechanism as the
software low cut and ClipGuard, so plugins run inside PipeWire's graph
with no extra process, and a chain adds latency only while it holds a
plugin. Plugins are found in the standard LV2 directories
(`/usr/lib/lv2`, `~/.lv2`, or wherever `LV2_PATH` points); the daemon
reads them through lilv. `lsp-plugins-lv2` is the set used during
development. Plugins that ship a custom GUI still load; the generated
controls configure the live PipeWire-hosted instance instead of opening
their toolkit-specific window. A native LV2 window cannot be attached to
that separate PipeWire instance without replacing the current plugin host.
VST and CLAP plugins are not supported; loading them would likewise need
a plugin host.

The submixer can be switched off in Options. The daemon then controls
the hardware only, restarts itself, and leaves the sound card in its
stock PipeWire layout; mixes, virtual microphones and inserts go away
with it. For the Wave XLR Pro there is an experimental ALSA UCM profile
in `packaging/ucm/` that splits the raw 17/18-channel card into named
PipeWire devices (Monitor, Line 1 to 3, XLR 1, XLR 2) for that mode, or
for running without OpenXLR. It is a manual root install with a
matching revert script and is not shipped by any package. While the
submixer runs, the daemon parks the card on its pro-audio profile and
restores the split profile when it stops.

## Application routing

- Audio clients are detected from their PipeWire client registration
  and assigned to a channel by name rules; each assignment is stored in
  the app registry and can be edited while the app is silent
- Electron apps report "Chromium" as their application name; they are
  identified by their process binary instead, so Discord appears as
  Discord
- A Manage dialog shows the full registry, and an installed-application
  picker pre-assigns channels from `.desktop` entries

## Profiles

Named scenes: every hardware setting plus the whole submix (send
levels, mutes, masters, monitor outputs, aux state, insert chains with
their parameters). Saved per device and recalled from the header or over
the API. App routing and the enforced system defaults are global and
not part of a profile, so recalling one does not rewire the desktop.

## OpenDeck plugin

`plugin/com.emaspa.openxlr.sdPlugin` is an
[OpenDeck](https://github.com/nekename/OpenDeck) plugin with two
actions, Dial and Toggle (key). Both are clients of the daemon's
WebSocket API, so they reflect changes made in the UI or on the
hardware.

Dials render a touch panel: a knob with a needle, a level meter, the
value readout, and a mute overlay. Every send, mix master, gain,
headphone volume, and the crossfade is a dial target, and one dial can
hold several targets cycled by tap or press.

![Dial panels](plugin-dials.png)

Keys render a button with an icon and a status LED: red for a mute,
green for an engaged feature or the active monitor output. Every
hardware switch and mute is a key target, plus the software low cut
(its frequency shown on the LED, cycling Off, 80, 120), ClipGuard, gain
lock, and switching the monitor output to a specific device. Each key
can pick its icon, and a typed title replaces the built-in label.

![Keys](plugin-keys.png)

Inserts: the property inspector lists every loaded plugin from live
state. A key toggles one insert's bypass (LED green in the path, red
bypassed) or a whole chain; a dial takes any control of any insert,
stepping along the control's own scale (log, integer, enumeration,
toggle), with its name and value on the panel and the insert's bypass
on the press. A key or dial follows its insert by id, and falls back to
the same plugin in the same chain when a profile recall rebuilds the
chain.

Install: download `com.emaspa.openxlr.sdPlugin.zip` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
use OpenDeck's install-from-file, or copy the plugin folder into
`~/.config/opendeck/plugins/` (a symlink breaks OpenDeck's asset
serving; the packages ship the folder in `/usr/share/openxlr/`). Touch
taps on the Stream Deck + XL need OpenDeck newer than 2.14.0
([nekename/OpenDeck#437](https://github.com/nekename/OpenDeck/pull/437)).

## Other

- Audio Flow window: a graph of the current routing, sources through
  outputs
- Enforced defaults: the daemon re-asserts the chosen system default
  sink and source on its one-second sweep, undoing WirePlumber's
  auto-switch to newly created nodes
- Tray icon, start-minimized option, daemon and window autostart from
  Options
- Diagnostics archive: one action collects app and device state, a
  vendor block dump, the PipeWire graph, daemon logs and configs into a
  tarball for bug reports
