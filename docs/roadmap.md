# Roadmap

What OpenXLR is heading towards, in the order the maintainer intends to
take it, and the rules a change has to meet to land. Items move here
from issues and pull requests once they are agreed; a checked item is
released, not merely written.

The goal has not changed since the first release: native Linux control
of the Elgato XLR interfaces, a Wave Link style submixer on plain
PipeWire, and Stream Deck control through OpenDeck, with every device
behaviour verified on hardware before it ships. The project is small on
purpose. It prefers one small, idiomatic change over a framework, and a
feature that is measured over one that is described.

## Where it stands (0.1.20)

- [x] Wave XLR Pro, XLR Dock (MK.1 and MK.2 modules), Wave XLR, Wave XLR
  MK.2: hardware controls, verified by owners of each device.
- [x] Submixer: hardware and application channels, four mixes (Monitor,
  Stream, Chat, Aux), virtual microphones, multi-output monitoring, the
  USB Aux port as a second computer's feed, live meters, profiles,
  one profile per device recalled on connect.
- [x] Software low cut and ClipGuard for devices without the hardware
  versions.
- [x] Plugin inserts: LV2 chains on each XLR input and on every mix, hosted
  by PipeWire's filter-chain, with generated controls; bypass and
  controls on the Stream Deck.
- [x] OpenDeck plugin: dials and keys drawn like the hardware, profile
  keys, insert keys and dials.
- [x] Packages: AUR, Debian/Ubuntu, Fedora, NixOS flake and module.
- [x] Daemon recovery basics: fast shutdown, busy-port wait, self-healing
  input feeds, UCM coexistence on the Pro.
- [x] Control API hygiene: commands validated before the mixer, per-client
  command budget, connection cap, foreign browser origins refused.
- [x] Daemon memory: workstation GC under a hard limit, one graph dump
  per sweep; channels and virtual microphones visible in desktop audio
  applets; LV2 plugins gated on the chain host's features.

## Next: mixer layout and customization

The submixer can now be shaped around the user's applications and outputs.

- [x] Editable application channels and virtual-microphone mixes: add,
  rename and delete with stable ids separate from display names. Hardware
  inputs and the Monitor and Aux buses stay structural. Channel creation is
  incremental and renames update descriptions, so existing application and
  virtual-microphone endpoints are not dropped.
- [ ] Reorder editable channels and mixes without changing their stable ids.
- [ ] Per-mix customization: icon, colour and order per mix and channel,
  hide a channel without deleting its routing, a compact layout that
  keeps one selected channel visible. Icons and colours also reach the
  Stream Deck keys.
- [ ] Listen to any mix: pick which mix the monitor outputs play, not only
  Monitor.
- [ ] Many-to-many mix-to-output matrix: send different mixes to
  different physical outputs at the same time, the way Wave Link 3 does.
  This changes the monitor route model and comes after the layout work.
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

## Next: plugins

Stage 1 (LV2 through filter-chain) is shipped. Stage 2 is the rest of the
plugin world, and it has to keep the audio path inside PipeWire.

- [ ] Native plugin editors: open an LV2 plugin's own window on the
  instance that processes audio. This needs the instance out of
  filter-chain and into a host process that exposes a PipeWire filter
  node; the design has to keep filter-chain for inserts that have no
  editor, and must not make the .NET build depend on a C toolchain.
- [ ] VST3 and CLAP, and Windows VST3 through yabridge, in the same host
  process model, one plugin per process, supervised and fail-open so a
  crashed plugin is bypassed and audio continues.
- [ ] Presets: per-plugin and whole-chain, with export and import; copy a
  chain between channels; A/B comparison.
- [ ] Plugin latency reported per insert and compensated across mixes.
- [ ] Sound Check: record a short microphone sample, loop it through the
  live chain, compare presets while listening.
- [ ] Plugin manager: search paths, rescan, quarantine of plugins that
  crash the scanner.

## Next: daemon and integrations

- [ ] Watchdog: systemd notify with a progress gate, restart on failure
  with a start limit, a Restart button in the window, and a graceful
  signal so teardown always runs. Never a restart loop when the audio
  server is down; the daemon degrades to device control instead.
- [ ] Update notice: an opt-in, throttled check against the project's
  releases, presented once, never automatic installation.
- [ ] A documented, versioned local API for third parties, once client
  authentication exists on top of the origin check; today the WebSocket
  on the loopback is the API and the OpenDeck plugin is its reference
  client.
- [ ] Route the focused application to a channel from a key, with a
  portal-based approach that works on Wayland.
- [ ] Generic PipeWire output volume and mute keys, and a main-output
  switch tied to the enforced default sink.
- [ ] Graph discovery without polling: the sweep parses a 2 MB pw-dump
  every second; the daemon should subscribe to registry events (pw-mon,
  or libpipewire directly) and keep an incremental view, which is what
  finally brings its memory and CPU to what a control daemon should use.
- [ ] Client authentication for the control API (a per-user secret), on
  top of the origin check that exists, before any API is documented as
  a public contract.

## Devices

- [ ] XLR Dock MK.2: blocks 0x0002 and 0x0006, which exist and are not
  decoded.
- [ ] Pro: the remaining hardware mix matrix (which return feeds which
  jack, per-return levels), now that the headphone mix bytes are known
  (see the protocol notes, block 0x0001 bytes 12 and 13). Direct control
  of that matrix from the window is the long-term answer to the
  crossfade and mic-monitoring questions.
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
- The .NET build stays a .NET build. Native helpers, when they come, are
  optional at build time and packaged separately.
- The audio graph is not rebuilt for a change that does not need it;
  every node the daemon creates has a name that survives restarts, and
  existing users' assignments and profiles keep working across upgrades.
- Documentation describes what the code does now. Work logs and test
  counts live in pull requests, not in docs.
- No em dashes, no curly quotes, sentence-case headings, and prose that
  states the mechanism rather than the feeling.
