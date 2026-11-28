# Roadmap

The maintainer's plan for OpenXLR: where the project stands, what comes
next in the order he intends to take it, and the rules a change has to
meet to land. Items move here from issues and pull requests once he has
agreed to them. Device verification is recorded in
[hardware-support.md](hardware-support.md); release notes say when a
change shipped.

The goal is a Wave Link style mixer on plain PipeWire for any
microphone, with native Linux control of Elgato's interfaces and USB
microphones on top and Stream Deck control through OpenDeck. A device
behaviour is marked verified only after a run on the hardware; until then
the docs say it is coded or in beta. The project is small on purpose. It
prefers one small, idiomatic change over a framework, and a feature that
is measured over one that is described.

## Where it stands (0.9.5)

- [x] Devices. The Wave XLR Pro, the XLR Dock (MK.1 and MK.2 modules),
  the Wave XLR and the Wave XLR MK.2 have their core hardware controls
  verified by owners of each device;
  [hardware-support.md](hardware-support.md) has the state of every
  control, the ones still coded or in beta included. The microphone EQ
  runs on the unit on the Wave XLR MK.2 and the XLR Dock MK.2 from
  firmware 1.0.15, on the Wave:3 MK.2 from its 0.2.3 and on the Pro from
  1.0.14, verified on hardware; other models and older firmware run it
  in software. The microphone compressor and expander run on the unit
  too, wherever the EQ does: the byte layout is verified on the Wave:3
  MK.2, and the Pro, the XLR Dock MK.2 and the Wave XLR MK.2 are driven
  at the same fields but not yet checked. The Pro runs ducking itself on
  its headphone jacks and its USB Aux port for a setup keyed by its own
  inputs, accepted by ear keyed by XLR 1 with the presets; custom ducking
  values are not yet heard on it. In an experimental hardware mixing mode
  the Pro also builds the mixes its jacks and USB Aux port play, one USB
  pair per channel, heard on one unit with Headphones 1 and the USB Aux
  port. There it ducks per channel, with any of its inputs or a channel
  on a USB pair as a key
  ([protocol notes section 20](wave-xlr-pro-protocol.md#20-ducking-keys-and-the-input-taps-2026-11-10)).
  A mix the unit builds alone has the unit's maximizer and reads the
  unit's own meter. Raw capture sends an input to the computer before
  the unit's processing, on the Pro from firmware 1.0.14 and on the Wave
  XLR MK.2 from 1.0.15. From that firmware OpenXLR also drives the Wave
  XLR MK.2's LED ring and its own gain lock, and reads its compressor and
  expander meters. On the maintainer's Wave:3 every control writes and
  reads back, and the gain, mute, monitor balance and desktop volume
  controls are verified; a Wave:1 owner confirmed gain, mute and
  headphone volume. On the maintainer's Wave:3 MK.2, on the Wave XLR
  MK.2's backend, gain, mute, headphone volume, the crossfade, the EQ on
  the unit, the low cut, the compressor and voice tune are verified; the
  expander's switch and settings read back at the documented bytes, and
  ClipGuard is driven at the Wave XLR MK.2's older offset, not yet
  measured. The Wave XLR and the first XLR Dock get their last settings
  back on connect. The Wave XLR also drives ClipGuard and the mic/PC
  crossfade at another project's offsets, not yet checked with OpenXLR.
  Up to four additional interfaces run beside the primary one, and the
  one picked last is driven again after a restart.
- [x] Mixer. Hardware, application and capture channels, monitor mixes,
  virtual microphones and Aux, in a layout edited live from the UI or the
  API, with every layout change saved before it is acknowledged. An
  output follows any mix or a sum of mixes. XLR 1 and XLR 2 can be bound
  to any capture source while the interface stays driven, and a capture
  input is a full microphone strip: its own insert chain, the microphone
  EQ, noise removal and Mic setup when mono, and the controls of the unit
  behind it. Each microphone also has noise removal with RNNoise or
  DeepFilterNet, a compressor, an expander and a voice changer with ten
  preset voices. Built-in and saved EQ profiles load from the UI, a Deck
  key or the API, and every output mix has an EQ of its own. Exclusive
  channel groups, ducking on every device with one to eight keys and
  presets or custom values, a playback volume for each application, a
  lock on an output's volume, profiles with one recalled on connect, and
  browser tabs routed to channels through the Chromium companion, each
  with a volume of its own. A soundboard plays clips into SFX, with
  pages, audition, export and import, starter sounds, and keys on the
  Deck and the desktop. Undo and redo, kept by the daemon and shared by
  every client, cover sends, volumes, mutes, microphone and mix
  processing, insert chains and the unit's own controls. Stream, Chat and
  Aux carry their master and mute on the mix's own sink, one write per
  move. An option per Bluetooth output unlinks it while its feed is
  silent, so multipoint headphones can switch to another device.
- [x] Plugins. LV2, CLAP and VST3 inserts on every microphone, capture
  input and mix, with generated controls and the plugin's own editor;
  Windows plugins on OpenXLR's own Windows plugin host, with yabridge and
  the optional companion as the fallback;
  installation from the UI; presets with A/B comparison; latency
  compensation across mixes; the delay a plugin adds, measured on its
  audio beside the figure it reports; a plugin that cannot keep up
  running on its own 1024-sample block; a plugin's own bypass parameter
  left out of its controls; a scanner that passes over a bundle that
  hangs. OpenXLR's host and the companion's report the system disk's
  identity, so a Windows plugin that ties its licence to that disk can
  activate.
- [x] Clients. The UI, with twenty-five built-in skins, skins that
  rearrange it, Broadcast and Wave among them with the mixer drawn as a
  matrix, system, light and dark modes, normal and Touch control sizes,
  five text sizes, Mic setup, the Flow window and eight languages. The
  terminal mixer `openxlr-tui`, drawing the same skins, with the EQ's
  band editor. The OpenDeck plugin, with keys and dials drawn like the
  hardware. Desktop keys through the portal, microphone mute and push to
  talk among them, and compositor-specific focused application routing.
  The Omarchy bar plugin. A versioned HTTP API beside the WebSocket.
- [x] Daemon. A systemd notify service with a watchdog; the control API
  on a private Unix socket that checks peer credentials, with no port and
  no token file; recovery when PipeWire, pipewire-pulse, a device or a
  plugin host misbehaves; an incremental registry view and ALSA control
  events instead of polling, so an idle daemon starts no helper program
  each second; commands validated and budgeted per client; a hard memory
  limit; an opt-in update notice; an installation receipt read back by
  `openxlr doctor`; a diagnostics archive from Options with the whole
  boot's journals, the kernel's USB and sound messages and the state of
  each open ALSA device. A glitch marker adds what the daemon saw in the
  30 seconds before it to that archive.
- [x] Packages. AUR (Arch Linux, CachyOS and Omarchy), Debian and Ubuntu
  with a PPA, Fedora with COPR, a NixOS flake and module, and an
  experimental Flatpak bundle on each release. The packaged units stay
  the recommended install.

## Next, in order

The sections below are in the order the maintainer takes them, and so
are the items inside each. Stability and correctness on real hardware
come before new features. Since the 0.2 releases the list of features
waiting for a run on real units has grown faster than it was worked off:
input bindings, capture inputs, ducking, the EQ and the dynamics on the
unit, hardware mixing, raw capture, the soundboard, the voice changer and
undo all shipped in a few weeks. The first two sections below are
finished before a new feature area is started. Fixes and owner reports
land at any time.

### Stability and hardware acceptance

- [x] Settings and the graph survive the daemon's own failures. Each of
  these was found by reading the code, none by a user report, and each
  gets a test that fails before the fix:
  - a plugin rescan or install no longer holds up the mixer;
  - `mixer.json` is written with a sync, carries a version, keeps keys a
    newer release wrote, keeps the previous file as a backup, and is
    never written from a mixer that is not built. A single wrong value
    drops that entry, as [mixer-layout.md](mixer-layout.md) says, and not
    the whole file;
  - a restore of the last settings that fails partway is tried again,
    and what the unit holds meanwhile is not saved over them. A daemon
    start no longer unmutes a microphone or raises a headphone level
    that was changed on the unit while the daemon was stopped;
  - when pipewire-pulse restarts, the daemon builds its graph again in
    place;
  - a command that fails inside the daemon answers with an error;
  - the healing sweep notices a missing mix, post or virtual microphone
    node, a channel sink whose module could not be unloaded, and a meter
    whose reader exited, and one strip that cannot be wired no longer
    stops the rest of the sweep;
  - the native helper replaces a non-finite input sample with silence in
    every stage, and a CLAP plugin that asks for a restart gets one in
    place.
- [x] A client says what went wrong. The UI shows the daemon's answer
  when it refuses a change, and greys the hardware panel and the mixer
  while it is not connected. The terminal mixer's header says when the
  daemon is gone. A Deck dial the daemon refused goes back to the value
  the daemon holds, and the inspector keeps a key's target while its
  option is not listed.
- [ ] Hardware acceptance. Each of these is released and none is marked
  verified until it runs on real units
  ([hardware-support.md](hardware-support.md#input-bindings)). Most need
  only the maintainer's desk, one session per unit:
  - Wave XLR Pro: input bindings with the Pro as the interface, its
    controls following the channel that carries its microphone, the
    zero-latency monitor path, replugs and daemon restarts; hardware
    mixing with two jacks on two mixes, a profile recalled in the mode
    after a replug, and a channel on a USB pair as a ducking key; the
    compressor and expander on the unit by ear; custom ducking values;
    Mic setup's Hear myself, Suggest gain and room noise measurement;
  - XLR Dock and XLR Dock MK.2: input bindings with a dock as the
    interface; on the MK.2 at firmware 1.0.15, ClipGuard, the gain lock
    and the compressor and expander on the unit;
  - Wave:3 and Wave:1: the dial modes, LED ring, ClipGuard and
    headphones by ear, and a replug; ClipGuard and the mic/PC mix on a
    Wave:1, from an owner;
  - Wave:3 MK.2: ClipGuard's bit, which the Wave XLR MK.2 moved in its
    firmware 1.0.15, checked by a gain write through the kernel's control
    or a Wave Link capture; the expander; the headphone level by ear; a
    replug; whether the float playback rule ends its microphone stall;
  - Wave XLR MK.2: the compressor and expander on the unit by ear, and
    whether the unit's gain lock holds the dial;
  - every microphone: each voice of the voice changer tuned by ear;
  - capture inputs: their insert chain and Mic setup in the UI and the
    terminal mixer, the controls of an additional interface behind one,
    and a source changed in place;
  - from owners: ClipGuard and the mic/PC crossfade on an original Wave
    XLR, and two units of one model at once;
  - the Omarchy bar's live theme switch and its popout's hardware
    controls; the Flatpak on Bazzite and in SteamOS Game Mode.
- [ ] Defaults follow verification. Until its row in
  [hardware-support.md](hardware-support.md) says verified, a unit's
  compressor and expander run in software unless the user picks the
  unit, and a control whose bit is unconfirmed on a model is not offered
  there.
- [ ] The support table says how each row was verified: a short legend
  of states, and a date and firmware on every row that claims a check.
  The README and the manual are brought in line with it where they say
  more than it does.

### Releases and checks

- [ ] The release `.deb` is built for each Ubuntu series the README
  names, and its native helper is run on the oldest of them before the
  release is public.
- [ ] A release is cut from a commit whose CI run finished green. The
  packages already build before the release is published. A pushed tag
  starts the release workflows, and they attach their files to a draft
  that is published once complete ([releases.md](releases.md)). Left: a check
  that the tagged commit's CI run finished green, and GitHub's immutable
  releases setting, so no asset can change after publication.
- [ ] The tests that fail now and then on main are listed by name from
  the run history and fixed, and a failed run keeps its test results and
  a screenshot. Cancelling a superseded run on main stays.
- [ ] Every test has a job that runs it: the native dynamics suite and the
  history tooltip test have none today, and
  a change to `docs/api.md` skips the unit tests that read it.
- [ ] Release assets come from workflows with a checksum and a
  provenance attestation. The packages, the plugin zip and the browser
  companion do; the Flatpak bundle and the `openxlr-yabridge` files are
  still uploaded by hand without one. The release procedure for every
  channel is written down in this repository. The GitHub release's part
  is in [releases.md](releases.md); the other channels are still to do.
- [ ] Packages declare every tool the daemon runs as a hard dependency,
  and each package's install check looks for them.
- [ ] One job builds the native helper with the address and undefined
  behaviour sanitizers and runs its tests.

### The mixer and its clients

- [x] The mix EQ, Dry and the Pro's Max on the classic layout, which the
  default appearance and thirteen of the built-in skins use, as on the
  matrix and bank layouts, the terminal mixer and the API.
- [ ] Undo and redo, the rest: a profile load as one step in place of a
  barrier that clears the history; then monitor outputs and feeds, the
  output volume, renames, order and appearance; then a deleted channel or
  mix brought back with its sends, processing and assignments.
- [ ] The terminal mixer: names in Chinese, Japanese and Korean drawn at
  their width; EQ profiles, exclusive groups, the output volume lock and
  the audio clock, which it lacks.
- [ ] The OpenDeck plugin: keys for the monitor mute, the software
  compressor and expander and Dry, a dial for an application's volume
  and for the ducking amount, a key that saves a profile, and a notice in
  the inspectors while the daemon is offline.
- [ ] Languages: the terminal mixer and the OpenDeck plugin read the UI's
  catalogue, and a test bounds how many keys a translation may lack.
- [ ] First run: packages enable the user service where the distribution
  allows a preset, and the Omarchy bar shows the daemon's own notice
  while the mixer waits for the audio server.

### Devices

- [ ] The Wave Neo, which has no backend yet. The maintainer has a unit
  on the way, so the work starts on his desk: its USB identity and
  descriptors, which protocol it speaks, its controls mapped from Wave
  Link captures on Windows, then a backend. Each control is marked
  verified in [hardware-support.md](hardware-support.md) once it passes
  on the unit.
- [ ] Last settings, the gain lock, LEDs and raw capture kept per unit,
  not per model, so a second unit of one model does not take the first
  one's values when it is picked.
- [ ] Hardware mixing, the next step once the mode has run for a while:
  section 72 as a fourth feed.
- [ ] The Pro's channel booster, the onboard effect with no mapped
  control. It adds up to 12 dB above the normal ceiling on any input and
  needs Wave Link captures ([capture guide](hardware-effects-capture.md)).
- [ ] XLR Dock MK.2: blocks 0x0002, 0x0006 and 0x0016, and block 0x0000
  beyond its firmware version bytes. The Wave XLR MK.2 reads its meter
  block 0x0002 and status block 0x0006 from firmware 1.0.15
  ([layout](hardware-support.md#mk2-meter-status)). The dock and the
  Wave:3 MK.2 answer blocks of the same lengths; OpenXLR reads neither
  block on them until a desk check shows the same layout there.
- [ ] LED controls on the other models where captures or desk
  measurements show the registers; nothing is guessed. Block 0x0016 byte
  1 and the rest of that block, and the mute button's lights, are still
  open on the Wave XLR MK.2.
- [ ] The original Wave XLR's low cut and its save to the unit, from an
  owner's capture ([USB capture guide](usb-capture.md)).
- [ ] A firmware page: which firmware each feature needs on each model,
  and how to update it, which today takes Wave Link on Windows or macOS.
- [ ] The Pro's UCM profile upstreamed to alsa-ucm-conf once a second
  owner confirms the split.

### Daemon and integrations

- [ ] The mixer's code split along its seams (input wiring, routes,
  settings and scenes, monitor outputs, application streams), with slow
  work such as a plugin scan or a helper start kept off the lock that
  every fader takes. No behaviour changes with it.
- [ ] Level, volume and mute writes on one connection to the audio
  server. Today each is a helper program started under the mixer's lock.
  Measured first on a fader drag and a profile recall, and built only if
  the numbers say so.
- [ ] One table of commands that holds each command's validation, its
  handler and its place in the undo history, in place of four lists kept
  in step by a test.
- [ ] One state model shared by the UI and the terminal mixer, so a
  field the daemon changes is read the same way in both and a daemon of
  another version cannot stop a client at start.
- [ ] Routing the focused application on compositors other than KWin,
  each with a focus source of its own. Hyprland has one, run on a live
  session; Sway, Niri, X11 and the optional GNOME Shell extension have
  sources still awaiting owner desktop acceptance. Other compositors
  remain separate work.

### Distribution

- [ ] openSUSE Tumbleweed and Leap. The RPM spec builds there, through
  COPR's openSUSE targets or the Open Build Service, with the package
  names those distributions use, and a packaged install runs on a
  virtual machine with a unit attached before the package is offered.
  Until then openSUSE users build from source.
- [ ] The browser companion on the Chrome Web Store, so the packages can
  offer it instead of an unpacked load. Before it is listed, the
  companion and its native host exchange a version, so an extension the
  Store updated keeps working with an older package.

## Later: plugins

LV2, CLAP and VST3 hosting and plugin installation are complete. Fixes to
the existing hosts are maintenance and land at any time; new plugin
features wait until the sections above are done.

The first one, when that time comes: an EQ and an insert chain on
application channels. Today a chain sits on a microphone, a capture input
or a whole mix, so a compressor on Voice Chat alone is not possible.

One piece of plugin work has started ahead of that order, by the
maintainer's decision: a Windows plugin host of our own, built into OpenXLR,
to replace yabridge and the optional companion. A small Windows program
runs the plugin under Wine and talks to the native plugin host directly.
What exists is in the tree behind the build flag `WINHOST=1`. It carries
CLAP and VST3 audio, parameters and latency, it shows a plugin's own editor
in the window every other plugin editor opens in, and it describes a
Windows plugin for the catalogue, with the same CLAP and VST3 host code that
runs and scans a native plugin. It reports the system disk's identity to a
plugin as the companion's host does. It has its own tests, described in
[native/README.md](../native/README.md#windows-plugin-host). A VST3 plugin
keeps the class id and the parameter ids it has through yabridge, so a saved
insert will still name it and keep its values. Every package except the
Flatpak builds it in, with mingw, and only suggests Wine. In a
build that has it, a client pins one insert of a Windows plugin to either
host with `setInsertWindowsHost` ([docs/api.md](api.md#windows-host)) and
moves every insert that follows the default with `setWindowsHostDefault`;
the UI offers both, in a bridged plugin's controls window and in Options.
Such a build also scans the Windows plugin folders
of the Wine prefix, and folders the user adds, with no yabridge, and runs
what it finds there on its own host; a plugin with a yabridge wrapper is
still listed by the wrapper. The host is now the default for every bridged
plugin, and a plugin that follows the default and cannot start there starts
again through yabridge. The companion is frozen and receives no new work. It is retired once the
maintainer decides the host has proven itself; the folder commands of the
yabridge setup are deprecated then and removed the release after.

## Parked

- A Windows port. Device control, the UI, the terminal mixer, the HTTP
  API and the Stream Deck plugin look feasible without an audio graph;
  the mixer itself would need its own audio engine and a signed driver.
  Not started.
- NVIDIA's GPU noise removal (the Audio Effects SDK, once Maxine) as a
  third engine. A nice to have if its licence opens up. On Linux the SDK
  runs only on data-centre GPUs, has no aarch64 build, and its licence
  forbids redistribution and use under an open source licence, so no
  package could carry it. It would fit technically: 48 kHz, 10 ms frames,
  on a worker like DeepFilterNet's.
- AMD Noise Suppression as an engine for the microphones. Nothing to build
  on today. It exists only inside AMD's Adrenalin driver software, which
  runs on Windows 10 and 11, and applications reach it through a virtual
  audio device. AMD's Linux drivers do not include it and AMD offers no
  library for other programs on any system, so OpenXLR has nothing to call
  or ship. Back on the list if AMD publishes one that runs on Linux.
- Per-tab browser routing in Firefox. Its extensions have no way to
  capture a tab's audio the way Chromium's do; Firefox as a whole routes
  like any application.

## Not planned

- Per-route levels on a sum. An output that follows several mixes takes
  each at unity. A level per route shipped in 0.1.40 and was taken out
  again. It was a second mixing layer over the sends and cost a hidden
  node per route, and a blend at other levels is what a monitor mix is
  for.
- Fixed Mic 3 and Mic 4 strips. More microphones are capture inputs,
  which scale past four and carry a microphone strip's controls.
- Flathub. Its rules do not allow AI-assisted development, so the
  Flatpak stays a bundle to install yourself.
- A third noise removal engine, or one that runs on the GPU. Checked in
  November 2026 against DeepFilterNet as shipped. The small models that
  suit a microphone stage run slower on a GPU than on one CPU core, and
  under load they missed the 10 ms deadline. The CUDA libraries they need
  weigh 2.5 GB and are not free software, and ONNX Runtime no longer has
  a ROCm backend. Two CPU models went through the same recorded voice and
  were compared by ear. DeepFilterNet's low-latency model cuts the model's
  delay from 30 ms to 10 ms, but it leaves more noise between words, uses
  four times the CPU and is a 35 MB model. FastEnhancer has 11 ms of delay
  and uses less CPU than DeepFilterNet, but it changes the voice more, and
  the export terms of its training data are unclear. DeepFilterNet as
  shipped is the better compromise.

## How a change lands

- A pull request names the issue or roadmap item it serves. Work that is
  on neither starts as an issue, so the maintainer can place it before it
  is built.
- One change per pull request. A pull request that mixes a feature, a
  packaging change and a CI change is asked to split, however good the
  parts are.
- Device facts come from USB captures and measurements on the hardware.
  No code, DSP, preset tables or assets are taken from Elgato's software.
- Hardware behaviour is verified on the device, by the maintainer or by
  a named tester on the issue, before the docs call it verified, and the
  verification is written down in docs/hardware-support.md or the
  protocol notes with its date and firmware. A change that cannot be
  checked yet lands marked coded or beta, with its check added to the
  acceptance list above, and a unit is not driven by default at a layout
  nobody has checked on it.
- Versions, tags, releases, distribution changelogs and package identity
  are set by the maintainer at release time. A pull request does not bump
  them. A release is cut from a commit whose CI run finished green.
- Contributors are credited in the release notes once their work is
  merged. A pull request does not add its own credit paragraph.
- The .NET build stays usable without a native compiler. The plugin host
  is optional at source-build time and included in distribution packages.
  OpenXLR's Windows plugin host is in every package except the Flatpak,
  and the yabridge companion is separate and optional.
- The audio graph is not rebuilt for a change that does not need it;
  every node the daemon creates has a name that survives restarts, and
  existing users' assignments and profiles keep working across upgrades.
- Documentation describes what the code does now. Work logs and test
  counts live in pull requests, not in docs. The protocol notes are the
  one exception: they are evidence, kept in the order it was gathered.
- No em dashes, no curly quotes, sentence-case headings, and prose that
  states the mechanism rather than the feeling.
