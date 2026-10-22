# Roadmap

What OpenXLR is heading towards, in the order the maintainer intends to
take it, and the rules a change has to meet to land. Items move here
from issues and pull requests once they are agreed; a checked item is
implemented on `main`. Device-specific verification is recorded in
[hardware-support.md](hardware-support.md); release notes say when it shipped.

The goal is a Wave Link style mixer on plain PipeWire for any
microphone, with native Linux control of Elgato's interfaces and USB
microphones on top and Stream Deck control through OpenDeck. A device
behaviour is marked verified only after a run on the hardware; until then
the docs say it is coded or in beta. The project is small on
purpose. It prefers one small, idiomatic change over a framework, and a
feature that is measured over one that is described.

## Where it stands (0.2.0)

This is what 0.2.0 ships. A checked item is in the released packages.

- [x] Wave XLR Pro, XLR Dock (MK.1 and MK.2 modules), Wave XLR, Wave XLR
  MK.2: hardware controls, verified by owners of each device.
- [x] Wave:3, in beta: gain, mute, headphone volume and the monitor
  balance from one user's quick test, the rest coded from public
  research ([hardware-support.md](hardware-support.md)).
- [x] Submixer: hardware and application channels, the monitor mixes
  (Monitor A, Monitor B), virtual microphones (Stream and Chat by
  default) and Aux; an editable layout (channels and microphones added,
  renamed, reordered and removed live from the UI's layout editor
  or the API, every change saved before it is acknowledged); monitoring
  on several outputs with each output choosing which mix or sum of
  mixes feeds it, the enforced default
  output chosen from the UI or a key, any PipeWire capture source as
  an input channel, the USB Aux port as a second computer's feed, live
  meters, profiles, one profile per device recalled on connect, Wave XLR
  and the first XLR Dock restored to their last settings on connect with a
  reset to firmware defaults, and an app can be left to the desktop's
  own routing.
- [x] Software low cut and limiting where the backend does not expose
  the corresponding hardware controls; gain lock respects connect-time
  restoration while preventing requested gain changes.
- [x] Plugin inserts: LV2, CLAP and VST3 chains on each XLR input and
  every mix, generated controls and native editors, bypass and parameter
  controls on the Stream Deck. See the completed plugin work below.
- [x] Application identity fallback from streams to clients, normalized
  Wine/Proton names and migration of stale saved aliases.
- [x] Flow window: four routing columns, selectable signal paths,
  processing inside cards and automatic initial sizing.
- [x] OpenDeck plugin: dials and keys drawn like the hardware, profile
  keys, insert keys and dials, monitor feed keys, output volume and mute
  keys and dials, a main-output switch, and a key that routes the
  focused application to a channel. A dial turn moves the strip at once
  and the commands are coalesced, so a fast turn lands every tick.
- [x] Desktop shortcuts: the same actions bound to global keys through
  the desktop portal, so they work on Wayland; the focused application
  is asked from KWin, so focused routing is KDE Plasma only today, and
  matched to its PipeWire client.
- [x] Terminal mixer: `openxlr-tui`, the whole mixer in a terminal over the
  daemon's socket, with every skin the UI has, one of them for each
  Omarchy palette; inserts open the plugin's own editor or generated
  controls and a picker adds plugins from the catalogue.
- [x] Omarchy 4 bar: `openxlr.mixer`, a shell plugin showing the XLR
  inputs that have signal and the monitor feed, with channel sends and
  mix masters in a popout whose meters follow the active Omarchy theme.
  An Arch package built on Omarchy carries it; [omarchy.md](omarchy.md)
  has the recipe. The live theme switch and the popout's hardware
  controls still need desktop acceptance.
- [x] Packages: AUR (Arch Linux, CachyOS and Omarchy), Debian/Ubuntu,
  Fedora, NixOS flake and module.
- [x] Daemon recovery basics: fast shutdown, busy-port wait, self-healing
  input and capture feeds, UCM coexistence on the Pro, a rebuild after a
  pipewire-pulse restart, a refusal to grow the layout past
  pipewire-pulse's open-file headroom (the packages raise that limit), a
  read backoff for a device that opens but fails every read, and the
  default sink and source snapshot taken before any service touches the
  card.
- [x] Device access: the USB vendor interface is claimed before
  control transfers, the original Wave XLR keeps its capture node running
  so a playback stream cannot silence the microphone, and an XLR Dock
  whose card lacks a mixer control is driven through its config block.
- [x] Control API hygiene: commands validated before the mixer, per-client
  command budget, connection cap, foreign browser origins refused.
- [x] Daemon memory: workstation GC under a hard limit, an incremental
  PipeWire registry kept from one monitoring subscription with a one-shot
  dump while it reconnects; channels and virtual microphones visible in
  desktop audio applets; LV2 plugins gated on the chain host's features.

## Next: mixer layout and customization

The submixer's shape is the user's own since the editable layout landed;
what remains in this block is how the mixer presents itself. The remaining
layout work comes before further plugin expansion: the routing model, the
layout editing and the daemon's service behaviour all changed within a
few releases, and they get to settle in users' hands first.

- [x] Editable application channels and virtual-microphone mixes: add,
  rename, delete, reorder, with stable ids separate from display names so
  PipeWire node names, profiles and Stream Deck keys survive a rename.
  Hardware inputs and Aux stay structural; every monitor mix, including
  Monitor A/B, can be renamed, reordered or removed, with at least one kept. Every
  layout command is acknowledged only after its save succeeded, a failed
  write is an error, and no untouched node is rebuilt: the channel sinks
  feed the mix sinks by name pattern, so a new mix grows its sends on its
  own. Landed in pieces: the Stream Deck choices (#25), the saved layout
  format (#33, [docs/mixer-layout.md](mixer-layout.md)), live channel
  creation (#34), the saved order (#35), then rename, delete, mix
  creation and the desktop editor. One known limit: a renamed virtual
  microphone keeps its old device name in other apps until the daemon
  restarts, since reloading the device would drop the apps recording
  from it.
- [x] Per-mix customization: icon, colour and order per mix and channel,
  hide a channel without deleting its routing, a compact layout that
  keeps one selected channel visible. Icons and colours also reach the
  Stream Deck keys. Icons are a built-in vector set with a default for
  each kind of channel and mix, picked where an item is created, and a
  skin may redraw one or add its own as path data.
- [x] Exclusive channel groups: several input channels marked as one
  group, where unmuting a member mutes the rest. A desk with a broadcast
  microphone and a headset, or a capture card switched between two
  consoles, is one key or one click instead of two. The group is layout
  state, so it saves and recalls with everything else and a Stream Deck
  key can address it.
- [x] Listen to any mix: an output follows any mix in the layout, Stream,
  Chat and Aux included, not only Monitor A and Monitor B.
- [x] Sums: an output can follow several mixes at once, each at unity,
  which covers a headset with a game side and a chat side. A per-route
  level on top, the way Wave Link 3 does it, shipped in 0.1.40 and was
  taken out again: it was a second mixing layer over the sends, cost a
  hidden node per route, and a blend at other levels is what a mix is
  for. Users can add monitor-kind mixes for that blend without exposing
  another microphone.
- [x] Any PipeWire capture source as an input channel: a second
  microphone, a capture card or a headset bound by node name and channel
  pair, healed by the sweep when the device returns. A capture channel
  is edited like an application channel but takes no application
  assignments. It has an insert chain of its own, Sound Check when mono,
  the unit controls of the interface it is bound to, and its source can
  be changed in place without losing its sends and inserts.
- [x] Input bindings: XLR 1 and XLR 2 each take the interface's own input
  or one port of any other PipeWire capture source, by exact node name.
  Aux In stays on the interface. The binding is machine wiring in
  `mixer.json`, never part of a profile, and changes no channel id, node
  name or application routing. The interface stays driven: headphones,
  the dial, output routing, additional interfaces, recall on connect and
  the gain lock keep working. Its gain, mute and hardware effects follow
  the channel that carries its microphone, which can be a capture channel
  the binding dialog offers to add, and so does the Wave XLR Pro's
  zero-latency monitor path. A bound strip is Mic 1 or Mic 2; XLR 1 and
  XLR 2 keep those names only on a real XLR jack, so a Wave:3's capsule is
  Mic 1 too. A strip bound to another supported Elgato unit follows its
  source through a rename, and shows that unit's controls while it is
  driven as an additional interface. The UI, the terminal mixer, the
  Stream Deck plugin and the Omarchy bar show each strip's source and
  connection. Most of the hardware acceptance is still open;
  [hardware-support.md](hardware-support.md#input-bindings) lists what has
  been checked on real units.
- [x] Skins can arrange the desktop UI: an optional `layout` block,
  version 1, places the header and the five sections in a tree of stacks,
  grids and docks with bounded tracks, spans, edges and gaps, starts a
  section folded or hidden, gives it a preferred width or height that
  never goes below what it needs, and orders the header's actions with an
  overflow menu while Options stays first. A tree wider than the window
  scrolls sideways. The user's Arrange moves are swaps over the tree's
  places, by pointer, arrow keys or a menu, kept per skin with the hidden
  sections the user showed; Reset returns to the skin's own places, and a
  skin without a layout keeps the saved order it always had. A skin can
  also pick application-owned templates: INPUTS as an input rack and the
  submixer as mix banks, with fixed strip templates whose parts it orders
  or hides, upright and reversed faders, upright meters, label styles and
  a master card or strip. Each bank's picker lists every mix, the choice
  is kept per skin in `ui.json`, and a bank never changes the mixer's
  order. MONITOR can be a one-row toolbar and APPLICATIONS a list, and a
  skin can start the submixer in its own window, which applies once and
  only while the user has made no docking choice. A structural switch ends
  drags first, changes the submixer in whichever window holds it, restores
  focus by channel, mix and role, keeps scroll offsets, folds, the
  detached mixer and plugin window ownership, and sends no command; Options
  describes the skin's layout and resets its arrangement, banks and
  docking choice. A mix matrix draws a row per channel and a column per
  mix, each send's fader over the channel's meter where they cross, with
  the input controls in the row heads and the mix masters in the column
  heads. Broadcast ships as the complete example: the matrix across the
  UI, MONITOR's toolbar at the top right, INPUTS hidden and the other
  sections folded under it, on a dark neutral palette, with a copy to
  start from in `docs/examples/skins`.
  [skins.md](skins.md#layout)
- [x] Ducking on every device: a key microphone lowers chosen channels in
  chosen mixes while it is loud, with the Wave XLR Pro's presets. A native
  stage per ducked mix runs it on this computer, set from the Ducking
  window, the terminal mixer and a Deck key. One setup in `mixer.json`,
  carried by profiles ([manual](manual.md#ducking)). Checked by ear on the
  desk on 2026-10-21.
- [x] Inputs from more than one attached Wave interface at once: up to
  four additional interfaces driven beside the primary, each with its own
  controls and capture channel, and a strip bound to another unit's input
  ([wave-interfaces.md](wave-interfaces.md)). Run on hardware with a Pro
  beside an XLR Dock and beside a Wave:3; two of one model still only
  with simulated units.

## Next: appearance

The views no longer hold colours. Every surface, label, indicator, meter,
fader and control reads a named token, the defaults live in one place, and
a skin overlays values on top of them.

- [x] Appearance tokens: one set of names for the faceplate, LEDs, meters,
  faders, text and accents; views reference tokens only, and the tokens a
  skin sets also feed the theme keys behind buttons, sliders and dropdowns.
- [x] Skins: a user-supplied value set loaded from a file, selectable in
  Options, so the mixer can look like the hardware it drives. Sixteen
  appearances are compiled into the application, so every package carries
  them: Material, a Deck appearance built from the plugin's own key and
  dial art, one for each of the eleven Omarchy palettes, Nyan Pink,
  Broadcast and Wave.
  Skins are data: no markup, no code, no path outside their own folder, no
  network. [skins.md](skins.md) is the contract.
- [x] System, light and dark appearance, following the desktop by default.
  Material has a light palette beside its dark one, chosen by a mode in
  Options; System follows the desktop's preference through the settings
  portal, in the UI and in the terminal mixer.
- [x] A terminal mixer, `openxlr-tui`, for a desk without a desktop
  session and for a tiling setup where a window is the odd one out. It
  speaks the same WebSocket the UI does and draws the same skins,
  reading the colour tokens out of the same files and following the same
  saved choice, so an appearance picked in one is the appearance in the
  other, the palette skins included. A desk of vertical sends and mix
  masters, with stereo meters and level history, and eight keyboard
  sections for the mixer, hardware, outputs, applications, inserts,
  profiles and appearance. A compact layout fits 80x24. It draws its own
  cells in true colour rather than taking a widget toolkit, so it adds no
  dependency to any package.
- [x] The Stream Deck plugin reading the same tokens for its key art. The
  plugin colours its keys, dials and meters from the saved skin, using the
  palette the UI publishes while it runs.
- [x] Layout density: a compact mode for small screens and a large mode
  for touch.
- [x] Localization infrastructure and the first translations.

## Later: plugins

LV2 filter-chain, native LV2/CLAP/VST3 hosting and plugin installation are
implemented. Remaining plugin work follows the mixer priorities above,
while fixes to existing hosts remain part of normal maintenance.

- [x] Native plugin editors: an LV2 plugin's own window, open on the
  instance that processes its audio. The instance moves out of
  filter-chain into an optional C/C++ helper that carries one plugin and its
  editor behind a PipeWire filter node. It is a per-insert choice, so an
  existing chain never changes host on upgrade, and every insert without
  that choice stays in filter-chain. Display loss and stalled editor
  controls leave healthy processing running; plugin-process crashes can
  interrupt the chain and repeated failures stop automatic retries.
  Resizing follows plugin constraints, fixed-size VST3 editors retain
  plugin-driven scaling, and LSP editors default to software rendering.
  Every package builds and installs the helper; an ordinary .NET build still
  needs no compiler, and a source build opts in with
  `-p:EnableNativeLv2Host=true`.
- [x] CLAP, in the same host: the helper loads a CLAP plugin, carries
  its parameters, timers and file descriptors, and opens its editor, in
  the process model LV2 already had. Bundles are described for the
  catalogue by the helper, one process per bundle, so the daemon never
  loads plugin code. The CLAP headers are vendored, so no package changed.
- [x] VST3, in the same host: a C++ backend against Steinberg's interface
  headers (vendored, MIT), one plugin per process, with the component and
  controller wired, parameters as controls, and the editor on a run loop of
  ours. Windows VST3 plugins arrive through yabridge as ordinary bundles.
  Scans are cached per bundle to avoid repeating expensive discovery.
  Audio remains on PipeWire ports in the same per-insert process model.
- [x] Installing plugins from the UI: pick a file or a folder and the
  daemon puts it where it looks, copies Linux bundles into the home
  directories, hands Windows plugins to yabridge and syncs, and reads the
  catalogues again. A card in Options says where plugins go and whether
  yabridge and Wine are there, with a rescan, a sync and the steps behind a
  Manual link.
- [x] Optional OpenXLR yabridge companion: pinned 64-bit bridge with the
  Wine editor input fix, private wrappers and controller settings, system
  bridge fallback, and binary/source package artifacts. The separate CI
  workflow does not publish them to release or distribution repositories.
- [x] Presets: per-plugin and whole-chain, with export and import; copy a
  chain between channels; A/B comparison.
- [x] Plugin latency reported per insert and compensated across mixes.
- [x] Sound Check: record a short microphone sample, loop it through the
  live chain, compare presets while listening.
- [x] A bundle that hangs or fails the scanner is remembered and passed
  over while the daemon builds its catalogue, asked again when the user
  installs, syncs or rescans or when the bundle, the helper or the bridge
  changes. Skipped bundles and folders the scan could not read are named
  in Options, and a failed scan keeps the phase it reached in its log.
- [x] Plugin manager: search paths and rescan in one place.
- [ ] Microphone EQ, requested by the maintainer ahead of the order
  above: a four-band EQ on every microphone input equal to Wave Link's in
  band types, ranges, steps, defaults and curves, in four steps. The
  daemon side is on `main` for XLR 1 and XLR 2: the model with snapping
  to Wave Link's steps, the curve maths, a built-in backend in the native
  helper that keeps its filter state while values move and crossfades
  switches, the splice at the head of the input that leaves the chain
  running, the `setEq`, `setEqEnabled` and `setEqBand` commands, the
  state, `mixer.json` and profiles ([api.md](api.md#microphone-eq)).
  The UI's editor is on `main` too: a response graph with draggable
  handles, a row of fields per band, the switch, a bypass to compare, copy
  and paste between microphones, opened from INPUTS, an input rack and the
  matrix row heads ([manual](manual.md#microphone-eq)). The daemon also
  runs it on the MK.2 family's own DSP from firmware 1.0.15, with the
  editor's badge, older-firmware note and switch between the unit and
  software, which the terminal mixer offers too
  ([api.md](api.md#where-the-eq-runs)), and on the Wave XLR Pro's two
  inputs from firmware 1.0.14, coded from Wave Link captures, with one
  bell measured on the desk. Capture microphones, any capture input on
  one mono port, have it too, in software only
  ([api.md](api.md#eq-on-capture-inputs)). Then the terminal mixer's editor
  and the OpenDeck plugin.

## Next: daemon and integrations

- [x] Watchdog: systemd notify with a progress gate, restart on failure
  with a start limit, a Restart button in the UI, and a graceful
  signal so teardown always runs. Never a restart loop when the audio
  server is down; the daemon degrades to device control instead. The
  packaged unit is a notify service since 0.1.22.
- [x] Update notice: an opt-in, throttled check against the project's
  releases, presented once, never automatic installation.
- [x] A documented, versioned local API for third parties: `/api/v1`
  over HTTP on the session token, with an OpenAPI document, next to the
  WebSocket the UI and the OpenDeck plugin use
  ([docs/http-api.md](http-api.md)).
- [x] Route the focused application to a channel from a key: the UI
  registers global shortcuts through the desktop portal and asks KWin for
  the focused window, and the daemon resolves that process to one
  PipeWire client, refusing to guess when several match. Other
  compositors need a focus source of their own before the key works
  there.
- [x] Generic PipeWire output volume and mute keys, and a main-output
  switch tied to the enforced default sink, from the UI, the desktop
  keys and the Deck.
- [x] Graph discovery without polling: the daemon subscribes to the
  registry through one monitoring dump and keeps an incremental view, so
  a sweep reads a snapshot instead of parsing a fresh dump. While the
  subscription reconnects a one-shot dump keeps routing and default
  enforcement running.
- [x] Client authentication for the control API: a per-session token the
  daemon writes at start, presented by every client, on top of the
  origin check. Still open: binding the API to a Unix socket with peer
  credentials instead of a loopback port, so no token file is needed.
- [x] An installation receipt and a `doctor` command. The daemon and the
  packages put files in several places a user never sees: a systemd unit,
  a udev rule, four WirePlumber rules, a pipewire-pulse drop-in, a
  desktop entry, the plugin host and the yabridge companion. Nothing
  today can answer what OpenXLR put on this machine, whether the running
  daemon is the build that was last installed, or whether a removal left
  something behind. The receipt records every path an install owns,
  together with the version, the commit and a hash of any user file that
  was replaced. `openxlr doctor` reads it back, reports a path that is
  missing, stale or modified, says whether the running daemon matches the
  installed build, and names the exact command to fix what it found. A
  removal refuses to touch a path the receipt does not own, so it never
  deletes by directory or by filename pattern.

## Next: distribution

- [x] Fedora COPR (`emaspa/openxlr`) and Ubuntu PPA (`ppa:sparvoli/openxlr`),
  so `dnf` and `apt` pick up new releases on their own instead of a
  download per release. The build recipes are the spec and the debian
  directory already used by the release workflows; the PPA source package
  carries the NuGet packages (packaging/ppa/make-source.sh) because
  Launchpad builders have no network.
- [ ] Flatpak on Flathub. The Flatpak mode and the manifest are on main
  as experimental ([flatpak.md](flatpak.md)): the sandbox cannot install
  the udev rules, the WirePlumber rules, the UCM profile or the systemd
  unit, so the UI starts the daemon as a child process and uses the
  background portal for login start, the daemon logs to a file instead
  of the journal, the WirePlumber rules are written to the user's config
  directory, the udev rule is a documented copy, and the UCM profile
  stays a manual step. LV2 inserts follow the Flathub audio plugin
  extension instead of host plugins. The watchdog does not work in the
  sandbox, so the packaged units stay the recommended install and the
  Flatpak covers the distributions without a package. Open: the Flathub
  submission.

## Devices

- [ ] XLR Dock MK.2: blocks 0x0002, 0x0006 and 0x0016, which exist and
  are not decoded, block 0x0000 beyond its firmware version bytes, and
  bytes 3 to 17 of the settings block,
  several of which change the level (likely compressor and expander
  parameters).
- [ ] Pro: the remaining hardware mix matrix (which return feeds which
  jack, per-return levels), now that the headphone mix bytes are known
  (see the protocol notes, block 0x0001 bytes 12 and 13). Direct control
  of that matrix from the UI is the long-term answer to the
  crossfade and mic-monitoring questions.
- [ ] The microphone EQ on hardware. On firmware 1.0.15 the XLR Dock
  MK.2 and the Wave XLR MK.2 run a four-band equalizer on the device
  behind a master enable bit, as Wave Link 3.5 writes it; on the dock's
  firmware 1.0.0 Wave Link ran it on the computer
  ([findings](mk2-eq-findings.md)). Measured on the dock from Linux with
  the enable bit set, its filters match the software EQ's for the same
  values, and the daemon now drives it there, not yet run from OpenXLR on
  either MK.2 unit ([api.md](api.md#where-the-eq-runs)).
  The Wave XLR Pro holds the same layout once per input; Wave Link 3.5
  was captured writing it on firmware 1.0.14, and the daemon drives both
  inputs there too
  ([hardware support](hardware-support.md#wave-xlr-pro-0fd900b4)).
  Every other model and older firmware keep the
  [microphone EQ](#later-plugins) in software. The editor and the terminal
  mixer switch each microphone between the unit and software. On the Pro
  one bell was measured on the desk: +12 dB at 5 kHz read +12.3 to
  +12.7 dB on the raw USB capture, and software mode left it flat. Still
  open: acceptance on both MK.2 units, the Pro's other filter types
  against the software EQ's curves, and whether its EQ reaches the
  zero-latency headphone path.
- [ ] Ducking on the Pro's own processor. Mapped from Wave Link 3.5 on
  Windows, and the daemon can write it, but the unit ducks inside its own
  mix and OpenXLR feeds the jacks through the monitor return, which goes
  around that mix, so the Pro runs ducking in software for now
  ([manual](manual.md#ducking)). A desk test with USB pair 2/3 in the
  Personal mix and the monitor return off gave clean music that the unit
  ducked. Open: feeding the jacks through the unit's mix (levels, latency,
  the direct microphone path, Headphones 2 and Line Out), a by-ear check of
  the USB Aux port, the encoding of keys other than XLR 1, and per-channel
  hardware ducking, which needs each channel on its own pair: the hardware
  mixing track, starting with the remaining mix matrix above.
- [ ] The Pro's mix maximizer and channel booster, the other two onboard
  effects with no mapped control. The booster adds up to 12 dB above the
  normal ceiling on any input; the maximizer is per mix.
- [ ] LED controls where captures show the registers; nothing is guessed.
- [ ] UCM profile for the Pro upstreamed to alsa-ucm-conf once a second
  owner confirms the split.
- [ ] Wave:3 (`0fd9:0070`), the USB condenser microphone rather than an
  XLR interface, in beta. A backend exists, `Wave3Device`,
  one class and one registry line, with gain, mute, ClipGuard, headphone
  volume and the direct monitor balance as the crossfade; the low cut is
  the submixer's by design, as no implementation that runs on the
  hardware touches a low cut byte. The submixer is plain PipeWire, so
  above the device only two things changed: the node-name fragment
  spells a colon the way udev does, and the registry's order is the
  daemon's preference, so with a verified interface attached the Wave:3
  is driven only when picked. It is written from public research
  (openwave, which runs on the hardware, wave3-research and LibreWave)
  with each fact's source named in the code. The bytes the sources
  dispute and no setter owns are retransmitted with the values read,
  never changed. On the maintainer's unit every control writes and reads
  back, and the gain, the monitor balance and the desktop volume controls
  are verified; an owner confirmed the capture-hold rule over several
  reboots. [hardware-support.md](hardware-support.md) records each
  control's state and the checks still open: the mute pad, the LED ring,
  the dial modes, the headphones by ear and a replug. Those take it out
  of beta.
- [ ] Wave:1 (`0fd9:006e`), the Wave:3's smaller sibling, in beta: the
  Wave:3 backend under the Wave:1's product id and name, `Wave1Device`,
  added after a user reported the microphone unrecognized. No public
  source has probed one. An owner's diagnostics on 0.1.60 showed the
  unit answering the Wave:3's block, the firmware line and the desktop
  volume mirror, and a second archive showed the gain, mute and
  headphone volume written from the UI taking effect; ClipGuard and
  the mic/PC mix wait for an archive collected with them changed
  ([hardware-support.md](hardware-support.md)). The backend still
  refuses a block of another length before writing anything, and it
  comes last in the registry's order.

## How a change lands

- One change per pull request. A pull request that mixes a feature, a
  packaging change and a CI change is asked to split, however good the
  parts are.
- Hardware behaviour is verified on the device before merge, by the
  maintainer or by a named tester on the issue, and the verification is
  written down in docs/hardware-support.md or the protocol notes.
- Versions, tags, releases, distribution changelogs and package identity
  are set by the maintainer at release time. A pull request does not bump
  them.
- Contributors are credited in the README once their work is merged, in
  a Credits section the maintainer writes, and in the release notes. A
  pull request does not add its own credit paragraph.
- The .NET build stays usable without a native compiler. The plugin host
  is optional at source-build time and included in distribution packages.
  The Windows bridge is a separate optional companion.
- The audio graph is not rebuilt for a change that does not need it;
  every node the daemon creates has a name that survives restarts, and
  existing users' assignments and profiles keep working across upgrades.
- Documentation describes what the code does now. Work logs and test
  counts live in pull requests, not in docs.
- No em dashes, no curly quotes, sentence-case headings, and prose that
  states the mechanism rather than the feeling.
