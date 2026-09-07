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

Wave XLR MK.2 and XLR Dock MK.2: gain, mute, phantom power, low cut,
expander, voice tune with strength, ClipGuard, compressor, headphone
volume, low impedance, crossfade.

Wave XLR: gain, mute, headphone volume, low impedance, phantom power.

XLR Dock: gain, mute and headphone volume through the kernel's standard
ALSA controls, plus phantom power and headphone low impedance over the
original Wave XLR's protocol dialect, which the dock also answers. The
phantom byte was identified by the
[openwave](https://github.com/rikkichy/openwave) project on the Wave XLR
([openwave PR #8](https://github.com/rikkichy/openwave/pull/8)) and
confirmed on the dock with a condenser microphone. Wave Link does not
write it for the dock. The dock has no onboard voice-processing DSP;
Wave Link runs those effects host-side, and on Linux the submixer
provides them (below).

## Software controls

For devices without the hardware version, the PipeWire layer provides:
- Low cut: a high-pass at 80 or 120 Hz (the two values Wave Link
  offers), a filter-chain node inserted between the mic and its channel,
  cycled from a button on the XLR 1 strip. Its response was measured
  with test tones as a second-order high-pass. The node is re-created
  if it disappears from the graph.
- ClipGuard: a post-ADC hard limiter at -3 dB in the same filter chain.
  It protects the downstream PipeWire mixes from overload, but cannot
  repair clipping that has already happened in the analogue preamp or
  ADC; microphone gain still needs headroom. It needs the `swh-plugins`
  LADSPA package. If that plugin is unavailable, enabling ClipGuard is
  rejected, the control stays disabled, and the existing microphone
  route remains live.
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
- Mixes: Monitor A (what you hear), Monitor B (a second selection for
  outputs that should hear something else), the virtual microphones
  (Stream and Chat by default, published as capture devices selectable
  in OBS or Discord like a microphone), and Aux (what a second computer
  on the USB Aux port receives)
- Per-channel, per-mix send levels and mutes; per-mix masters
- An editable layout: Edit layout in the SUBMIXER card adds, renames,
  reorders and removes application channels and virtual microphones
  while audio plays, as does the API, with stable ids so profiles and
  Stream Deck keys survive a rename. Every change is saved before it is
  confirmed
- The monitor mixes can play on several outputs at once, hardware
  outputs included; each output picks which monitor mix feeds it, or
  both summed (Monitor A+B), so a headset with a game sink and a chat
  sink hears two selections, and one pair of headphones can hear the
  desktop from A with a separately processed mic from B
- Level meters throughout, dB-scaled, pushed at 15 Hz

Each channel is a combine sink with one internal stream per mix; that
stream's volume is the send fader. The default layout's 9 by 5 matrix
is 14 sinks and no loopback processes, and mixes come and go without
touching the channel nodes. Details in [architecture.md](architecture.md).

On the Wave XLR Pro the headphone jacks are fed by a mix inside the
device. Whenever a Pro jack is a monitor output the daemon makes sure
that mix carries the Monitor stream (a unit set up by Wave Link on
Windows may not), and when a jack is the only monitor output the
microphone's zero-latency hardware path into the jacks follows XLR 1's
send in the Monitor mix: unmuted, you hear yourself with no delay;
muted, you do not. With another device in the monitor set the software
send carries the microphone to everything instead.

Channels appear as playback devices in the desktop's audio applet, and
the virtual microphones (Stream and Chat by default) as recording
devices; the hardware input channels are hidden from it.

## Inserts

LV2 plugins in the signal path. Each XLR input carries a mono chain and
every mix a stereo one, the ones you add included. An Inserts row
under the channel or mix lists what is loaded: a green or red LED for
active or bypassed, a bypass button, and a gear that opens the plugin's
controls in their own window. The picker shows every installed LV2
plugin that fits the slot (mono for inputs, stereo for mixes), grouped
by category. The controls window is generated from the plugin's port
descriptions, grouped by parameter family, with a Defaults button.
Chains are saved with the mixer and recalled by profiles.

Every chain is a PipeWire filter-chain node, the same mechanism as the
software low cut and ClipGuard, so plugins run inside PipeWire's graph
with no extra process, and a chain adds latency only while it holds a
plugin. Plugins are found in the standard LV2 directories
(`/usr/lib/lv2`, `~/.lv2`, or wherever `LV2_PATH` points); the daemon
reads them through lilv. `lsp-plugins-lv2` is the set used during
development. A plugin that requires a host feature the chain does not
provide is left out of the picker and refused by the daemon rather than
failing when the chain is built. VST and CLAP plugins are not supported;
loading them would need a plugin host.

Plugins that ship their own editor still load, and the generated controls
are shown for them. The editor itself can be opened through a small host
process, which every package installs. Turning on "Native host" for one
insert moves that insert out of the shared chain into a process of its
own, which hosts the plugin and its editor together; the other inserts
stay in the chain and nothing moves unless you ask. The editor works on the
audio that is playing and its changes are saved like any other control.
The plugin keeps processing if the editor stops answering or the X
display goes away, and a chain whose host keeps crashing is switched off
once it has failed three times in five minutes, with the reason on the
insert. The manual covers the
setup in 3.12.

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
- An app can be set to "Not managed": the mixer hands its streams back to
  the system default output and never touches them again, so a headset
  with separate game and chat sinks keeps its own routing for that app

## Profiles

Named scenes: every hardware setting plus the whole submix (send
levels, mutes, masters, monitor outputs, aux state, insert chains with
their parameters). Saved per device and recalled from the header, over
the API, or from a Stream Deck key. One profile per device can be
marked to recall on connect: at daemon start, after a replug or power
cycle, or when switching to that device. Interfaces without settings
memory (Wave XLR, the first XLR Dock) get their last settings back
on every fresh connect without a profile, and can be reset to the
firmware defaults recorded after a power cycle. The Wave XLR Pro, which
keeps its own settings, can be reset to OpenXLR's baseline instead:
gain 30 dB, everything off, levels at half, the crossfade on PC. App routing and the
enforced system defaults are global and not part of a profile, so
recalling one does not rewire the desktop.

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
lock, switching the monitor output to a specific device, and cycling
an output's feed through Monitor A, Monitor B and Monitor A+B. Each key
can pick its icon, and a typed title replaces the built-in label.

![Keys](plugin-keys.png)

Profiles: a key can recall one of the active device's saved profiles,
listed live in the property inspector; it lights while that profile is
the last one recalled or saved.

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
  outputs, with the filter chains (built-in low cut and ClipGuard, LV2
  inserts) drawn where they sit in the path and each stage marked active,
  bypassed or broken
- Enforced defaults: the daemon re-asserts the chosen system default
  sink and source on its one-second sweep, undoing WirePlumber's
  auto-switch to newly created nodes
- The control API validates every command before the mixer sees it and
  answers with an error instead of ignoring it; clients are rate-limited
  and browser pages from other origins are refused; see
  [api.md](api.md). The same commands are served over HTTP at `/api/v1`
  with an OpenAPI document ([http-api.md](http-api.md))
- The daemon rebuilds its graph after a pipewire-pulse restart, and
  refuses to grow the layout when pipewire-pulse has no open-file
  headroom left; the packages raise that limit with a systemd drop-in.
  It also holds every sink it created at full volume and unmuted, since
  a desktop applet or the session manager can turn one down and quietly
  cut the mixes it feeds
- One window per user: a second launch brings the running window to the
  front, out of the tray if it is hidden there, and exits
- Tray icon, start-minimized option, daemon and window autostart from
  Options
- Diagnostics archive: one action collects app and device state, a
  vendor block dump, the PipeWire graph, daemon logs and configs into a
  tarball for bug reports
