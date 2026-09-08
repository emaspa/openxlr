# Roadmap

What OpenXLR is heading towards, in the order the maintainer intends to
take it, and the rules a change has to meet to land. Items move here
from issues and pull requests once they are agreed; a checked item is
implemented on `main`. Device-specific verification is recorded in
[hardware-support.md](hardware-support.md); release notes say when it shipped.

The goal has not changed since the first release: native Linux control
of the Elgato XLR interfaces, a Wave Link style submixer on plain
PipeWire, and Stream Deck control through OpenDeck, with every device
behaviour verified on hardware before it ships. The project is small on
purpose. It prefers one small, idiomatic change over a framework, and a
feature that is measured over one that is described.

## Where it stands (0.1.30)

This is what 0.1.30 ships. A checked item is in the released packages.

- [x] Wave XLR Pro, XLR Dock (MK.1 and MK.2 modules), Wave XLR, Wave XLR
  MK.2: hardware controls, verified by owners of each device.
- [x] Submixer: hardware and application channels, the monitor mixes
  (Monitor A, Monitor B), virtual microphones (Stream and Chat by
  default) and Aux; an editable layout (channels and microphones added,
  renamed, reordered and removed live from the window's layout editor
  or the API, every change saved before it is acknowledged); monitoring
  on several outputs with each output choosing which monitor mix feeds it,
  the USB Aux port as a second computer's feed, live meters, profiles,
  one profile per device recalled on connect, Wave XLR and the first XLR Dock
  restored to their last settings on connect with a
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
  keys, insert keys and dials, monitor feed keys.
- [x] Packages: AUR, Debian/Ubuntu, Fedora, NixOS flake and module.
- [x] Daemon recovery basics: fast shutdown, busy-port wait, self-healing
  input feeds, UCM coexistence on the Pro, a rebuild after a
  pipewire-pulse restart, and a refusal to grow the layout past
  pipewire-pulse's open-file headroom (the packages raise that limit).
- [x] Control API hygiene: commands validated before the mixer, per-client
  command budget, connection cap, foreign browser origins refused.
- [x] Daemon memory: workstation GC under a hard limit, one graph dump
  per sweep; channels and virtual microphones visible in desktop audio
  applets; LV2 plugins gated on the chain host's features.

## Next: mixer layout and customization

The submixer's shape is the user's own since the editable layout landed;
what remains in this block is how the mixer presents itself. The remaining
layout work comes before further plugin expansion: the routing model, the
layout editing and the daemon's service behaviour all changed within a
few releases, and they get to settle in users' hands first.

- [x] Editable application channels and virtual-microphone mixes: add,
  rename, delete, reorder, with stable ids separate from display names so
  PipeWire node names, profiles and Stream Deck keys survive a rename.
  Hardware inputs, Monitor A, Monitor B and Aux stay structural. Every
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
- [ ] Per-mix customization: icon, colour and order per mix and channel,
  hide a channel without deleting its routing, a compact layout that
  keeps one selected channel visible. Icons and colours also reach the
  Stream Deck keys.
- [ ] Listen to any mix: an output can already follow Monitor A or
  Monitor B; letting it follow Stream, Chat or Aux as well is the rest.
- [ ] Many-to-many mix-to-output matrix: two monitor mixes with
  per-output feeds cover the common case (a headset with a game side and
  a chat side). The general form, any mix to any output with a level per
  route, the way Wave Link 3 does it, comes after the layout work.
- [ ] Any PipeWire capture source as an input channel (a second
  microphone, a capture card, a headset), and inputs from more than one
  attached Wave interface at once.

## Next: appearance

The window currently hard-codes its colours in the views. Before any theme
can exist, those become named tokens in one resource dictionary that
every view binds to.

- [ ] Colour tokens: one dictionary for the faceplate, LEDs, meters,
  faders, text and accents; views reference tokens only.
- [ ] System, light and dark appearance, following the desktop by default.
- [ ] Skins: a user-supplied token set loaded from a file, selectable in
  Options, so the mixer can look like the hardware it drives, like Wave
  Link, or like whatever the user wants. The Stream Deck plugin reads the
  same tokens for its key art.
- [ ] Layout density: a compact mode for small screens and a large mode
  for touch.
- [ ] Localization infrastructure and the first translations.

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
- [x] Installing plugins from the window: pick a file or a folder and the
  daemon puts it where it looks, copies Linux bundles into the home
  directories, hands Windows plugins to yabridge and syncs, and reads the
  catalogues again. A card in Options says where plugins go and whether
  yabridge and Wine are there, with a rescan, a sync and the steps behind a
  Manual link.
- [x] Optional OpenXLR yabridge companion: pinned 64-bit bridge with the
  Wine editor input fix, private wrappers and controller settings, system
  bridge fallback, and binary/source package artifacts. The separate CI
  workflow does not publish them to release or distribution repositories.
- [ ] Presets: per-plugin and whole-chain, with export and import; copy a
  chain between channels; A/B comparison.
- [ ] Plugin latency reported per insert and compensated across mixes.
- [ ] Sound Check: record a short microphone sample, loop it through the
  live chain, compare presets while listening.
- [ ] Plugin manager: search paths, rescan, quarantine of plugins that
  crash the scanner.

## Next: daemon and integrations

- [x] Watchdog: systemd notify with a progress gate, restart on failure
  with a start limit, a Restart button in the window, and a graceful
  signal so teardown always runs. Never a restart loop when the audio
  server is down; the daemon degrades to device control instead. The
  packaged unit is a notify service since 0.1.22.
- [x] Update notice: an opt-in, throttled check against the project's
  releases, presented once, never automatic installation.
- [x] A documented, versioned local API for third parties: `/api/v1`
  over HTTP on the session token, with an OpenAPI document, next to the
  WebSocket the window and the OpenDeck plugin use
  ([docs/http-api.md](http-api.md)).
- [ ] Route the focused application to a channel from a key, with a
  portal-based approach that works on Wayland.
- [ ] Generic PipeWire output volume and mute keys, and a main-output
  switch tied to the enforced default sink.
- [ ] Graph discovery without polling: the sweep parses a 2 MB pw-dump
  every second; the daemon should subscribe to registry events (pw-mon,
  or libpipewire directly) and keep an incremental view, which is what
  finally brings its memory and CPU to what a control daemon should use.
- [x] Client authentication for the control API: a per-session token the
  daemon writes at start, presented by every client, on top of the
  origin check. Still open: binding the API to a Unix socket with peer
  credentials instead of a loopback port, so no token file is needed.

## Next: distribution

- [x] Fedora COPR (`emaspa/openxlr`) and Ubuntu PPA (`ppa:sparvoli/openxlr`),
  so `dnf` and `apt` pick up new releases on their own instead of a
  download per release. The build recipes are the spec and the debian
  directory already used by the release workflows; the PPA source package
  carries the NuGet packages (packaging/ppa/make-source.sh) because
  Launchpad builders have no network.
- [ ] Flatpak, after the repositories above, first as a manifest in this
  repo and then on Flathub. The sandbox cannot install the udev rules,
  the WirePlumber rules, the UCM profile or the systemd unit, so the
  work is a Flatpak mode before the manifest: the window starts the
  daemon as a child process and uses the background portal for login
  start, the daemon logs to a file instead of the journal, the
  WirePlumber rules are written to the user's config directory, the
  udev rules ship inside the app with a first-run notice giving the copy
  command, and the UCM profile stays a documented manual step. LV2
  inserts follow the Flathub audio plugin extension instead of host
  plugins. The watchdog does not work in the sandbox, so the packaged
  units stay the recommended install and the Flatpak covers the
  distributions without a package.

## Devices

- [ ] XLR Dock MK.2: blocks 0x0002 and 0x0006, which exist and are not
  decoded.
- [ ] Pro: the remaining hardware mix matrix (which return feeds which
  jack, per-return levels), now that the headphone mix bytes are known
  (see the protocol notes, block 0x0001 bytes 12 and 13). Direct control
  of that matrix from the window is the long-term answer to the
  crossfade and mic-monitoring questions.
- [ ] The four-band equalizer the Wave FX processor runs on the Pro, the
  Wave XLR MK.2 and the XLR Dock MK.2. It is onboard DSP on all three,
  and OpenXLR maps none of it, so a user who shapes their voice in Wave
  Link on Windows loses that shape on Linux. Four bands means the
  registers are wider than anything mapped so far, and the capture has to
  separate the per-band frequency, gain and width bytes.
- [ ] Ducking on the Pro, which lowers the other mixes while you speak
  and is applied per mix. It is the one onboard effect that touches the
  mix matrix rather than the microphone path, so it is worth capturing
  together with the matrix above.
- [ ] The Pro's mix maximizer and channel booster, the other two onboard
  effects with no mapped control. The booster adds up to 12 dB above the
  normal ceiling on any input; the maximizer is per mix.
- [ ] LED controls where captures show the registers; nothing is guessed.
- [ ] UCM profile for the Pro upstreamed to alsa-ucm-conf once a second
  owner confirms the split.

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
