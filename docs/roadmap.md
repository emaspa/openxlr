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

## Where it stands (0.3.6)

- [x] Devices. The Wave XLR Pro, the XLR Dock (MK.1 and MK.2 modules),
  the Wave XLR and the Wave XLR MK.2 have their exposed hardware controls
  verified by owners of each device. The microphone EQ runs on the unit
  on the Wave XLR MK.2 and the XLR Dock MK.2 from firmware 1.0.15, on the
  Wave:3 MK.2 from its 0.2.3 and on the Pro from 1.0.14,
  verified on hardware; other models and older firmware run it in
  software. The Pro runs ducking itself for a setup keyed by XLR 1 on its
  headphone jacks, accepted by ear. On the maintainer's Wave:3 every
  control writes and reads back, and the gain, mute, monitor balance and
  desktop volume controls are verified; a Wave:1 owner confirmed gain,
  mute and headphone volume. On the maintainer's Wave:3 MK.2, on the Wave
  XLR MK.2's backend, gain, mute, headphone volume, the crossfade, the EQ
  on the unit, the low cut, the compressor and voice tune are verified;
  the expander and ClipGuard are driven at the Wave XLR MK.2's offsets.
  The Wave XLR and the first XLR Dock get their
  last settings back on connect. Up to four additional interfaces run
  beside the primary one, and the one picked last is driven again after a
  restart.
- [x] Mixer. Hardware, application and capture channels, monitor mixes,
  virtual microphones and Aux, in a layout edited live from the UI or the
  API, with every change saved before it is acknowledged. An output
  follows any mix or a sum of mixes. XLR 1 and XLR 2 can be bound to any
  capture source while the interface stays driven, and a capture input is
  a full microphone strip: its own insert chain, the microphone EQ, noise
  removal and Mic setup when mono, and the controls of the unit behind
  it. Exclusive channel groups, ducking on every device, profiles with one recalled on
  connect, and browser tabs routed to channels through the Chromium
  companion. A soundboard plays clips into SFX, with pages, audition,
  export and import, starter sounds, and keys on the Deck and the desktop.
- [x] Plugins. LV2, CLAP and VST3 inserts on every input and mix, with
  generated controls and the plugin's own editor; Windows plugins through
  yabridge and the optional companion; installation from the UI; presets
  with A/B comparison; latency compensation across mixes; a scanner that
  passes over a bundle that hangs.
- [x] Clients. The UI, with seventeen built-in skins, skins that rearrange
  it, Broadcast and Wave among them with the mixer drawn as a matrix,
  system, light and dark modes, a compact and a large density, the Flow
  window and seven languages. The terminal mixer `openxlr-tui`, drawing the same skins. The
  OpenDeck plugin, with keys and dials drawn like the hardware. Desktop
  keys through the portal and compositor-specific focused application
  routing. The Omarchy bar plugin. A versioned HTTP API beside the WebSocket.
- [x] Daemon. A systemd notify service with a watchdog; recovery when
  PipeWire, pipewire-pulse, a device or a plugin host misbehaves; an
  incremental registry view instead of polling; commands validated and
  budgeted per client; a hard memory limit; an opt-in update notice; an
  installation receipt read back by `openxlr doctor`; a diagnostics
  archive from Options.
- [x] Packages. AUR (Arch Linux, CachyOS and Omarchy), Debian and Ubuntu
  with a PPA, Fedora with COPR, a NixOS flake and module, and an
  experimental Flatpak bundle on each release. The packaged units stay
  the recommended install.

## Next, in order

The sections below are in the order the maintainer takes them, and so
are the items inside each. Stability and correctness on real hardware
come before new features. Input bindings, capture inputs, ducking and the
EQ on the unit all landed in the last few releases, and users should run
them for a while before more is built on top.

### Stability and hardware acceptance

- [ ] User reports first. Issue
  [#474](https://github.com/emaspa/openxlr/issues/474): a Wave XLR's
  microphone garbles after a while on one owner's machine. Every failure
  recorded so far had the native plugin host in the microphone's path,
  and a capture card driving the graph with the Wave XLR resampled as its
  follower. On a fresh CachyOS install it broke as soon as an LV2 RNNoise
  insert was added, with the same setup that ran for days on Bazzite. The
  built-in noise removal and a WirePlumber rule that keeps PipeWire off
  the Wave XLR's gain control are the next tests.
- [ ] Hardware acceptance of the 0.2 work. Each of these is released and
  none is marked verified until it runs on real units
  ([hardware-support.md](hardware-support.md#input-bindings)):
  - input bindings: XLR 1 or XLR 2 on another source with a Pro, an XLR
    Dock or a Wave:3 as the interface, the unit's controls following the
    channel that carries its microphone, the Pro's zero-latency monitor
    path, replugs and daemon restarts;
  - capture inputs: their insert chain and Mic setup in the UI and the
    terminal mixer, the controls of an additional interface behind one,
    and a source changed in place;
  - the Pro's ducking on the USB Aux port, by ear;
  - the Wave:3's dial modes, LED ring, ClipGuard and headphones by ear,
    and a replug; ClipGuard and the mic/PC mix on a Wave:1, from an owner;
  - the Wave:3 MK.2's headphone level by ear, a replug, whether the float
    playback rule ends its microphone stall, and a Wave Link capture of
    its expander and ClipGuard switches;
  - two units of one model at once, from an owner of two;
  - the Omarchy bar's live theme switch and its popout's hardware
    controls;
  - the Flatpak on Bazzite and in SteamOS Game Mode.

### The mixer and its clients

- [x] The microphone EQ in every client: a band editor in the terminal
  mixer, followed by EQ keys and dials in the OpenDeck plugin. The
  graphical UI already has the full editor.
- [x] Mic setup, a guided microphone test in the UI in place of the Sound
  Check window: Position, Level with the meter before the chain and a
  suggested gain, Listen with an instant Raw and Processed switch, and
  Shape with the EQ, the inserts and starting presets.
- [x] Noise removal on every microphone that has the EQ, first in its
  native stage: RNNoise or DeepFilterNet per microphone, with a strength,
  in Mic setup, the terminal mixer, a Deck key and the API, and both
  engines in every package.
- [x] A soundboard: a clip library played into the SFX channel by the
  native helper, so the clips take SFX's fader, mute and sends; triggered
  from Deck keys, desktop keys, the UI and the terminal mixer, with per
  clip gain, press modes, loop and stop all. Clips are decoded with
  libsndfile, which every package now builds against.
- [ ] A voice changer on every microphone that has the EQ, last in its
  native stage: custom pitch and formant and ten preset voices, from its
  own window in the UI, the terminal mixer, Deck keys and the API. Coded and
  tested offline and on a private PipeWire server; each voice is tuned by
  ear on the desk before it is called done.

### Devices

- [ ] A hardware mixing mode on the Wave XLR Pro. The layout of block
  0x0001 is known
  ([protocol notes section 12](wave-xlr-pro-protocol.md#12-the-mix-sections-and-the-jacks-desk-probe-of-2026-10-23)):
  five mix sections, each with a level per source and a membership mask,
  each selectable for a jack and streamed back on its own capture pair. A
  mode that builds the mixes in the unit, one USB pair per channel, would
  give Wave Link's per-channel ducking and mixing without the computer in
  the path, at the cost of eight channels, five mixes and no plugin
  inserts on a mix. Probes on a unit come first: whether the unit takes level
  and membership changes live, the gain of each section, and the latency
  of the returned mixes. The rest of the Pro's ducking waits on this
  mode: ducking per channel, and keys other than XLR 1, whose encoding no
  capture has shown.
- [ ] The Pro's mix maximizer and channel booster, the onboard effects
  with no mapped control. The booster adds up to 12 dB above the normal
  ceiling on any input; the maximizer is per mix. Both need Wave Link
  captures ([capture guide](hardware-effects-capture.md)).
- [ ] XLR Dock MK.2: blocks 0x0002, 0x0006 and 0x0016, which exist and
  are not decoded, block 0x0000 beyond its firmware version bytes, and
  bytes 3 to 17 of the settings block, several of which change the level
  (likely compressor and expander parameters).
- [ ] LED controls where captures show the registers; nothing is
  guessed.
- [ ] The Pro's UCM profile upstreamed to alsa-ucm-conf once a second
  owner confirms the split.

### Daemon and integrations

- [x] The control API on a Unix socket with peer credentials instead of
  a loopback port, so no token file is needed.
- [ ] Routing the focused application on compositors other than KWin,
  each with a focus source of its own. Hyprland has one, run on a
  live session; Sway, Niri, X11 and the optional GNOME Shell extension have
  sources still awaiting owner desktop acceptance. Other compositors
  remain separate work.

### Distribution

- [ ] The browser companion on the Chrome Web Store, so the packages can
  offer it instead of an unpacked load.

## Later: plugins

LV2, CLAP and VST3 hosting and plugin installation are complete. Fixes to
the existing hosts are maintenance and land at any time; new plugin
features wait until the sections above are done.

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

## How a change lands

- A pull request names the issue or roadmap item it serves. Work that is
  on neither starts as an issue, so the maintainer can place it before it
  is built.
- One change per pull request. A pull request that mixes a feature, a
  packaging change and a CI change is asked to split, however good the
  parts are.
- Device facts come from USB captures and measurements on the hardware.
  No code, DSP, preset tables or assets are taken from Elgato's software.
- Hardware behaviour is verified on the device before merge, by the
  maintainer or by a named tester on the issue, and the verification is
  written down in docs/hardware-support.md or the protocol notes.
- Versions, tags, releases, distribution changelogs and package identity
  are set by the maintainer at release time. A pull request does not bump
  them.
- Contributors are credited in the release notes once their work is
  merged. A pull request does not add its own credit paragraph.
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
