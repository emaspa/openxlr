# Linux audio stack research (August 2026)

The survey and decisions that preceded OpenXLR, condensed from the
research log. Repository statistics are from the dates given and have
drifted since. For installation and current behaviour, use the
[manual](manual.md), [architecture](architecture.md) and
[hardware support](hardware-support.md). The protocol findings that came out of this work are in
[wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md).

## 1. The replacement target

Wave Link is not only a hardware control panel. Its core is the virtual
submixer: per-application channels (Game, Chat, Music, Browser, System,
SFX), independent mixes (the monitor mix the user hears, the stream mix
OBS or the audience gets), per-channel volume and mute per mix, mic
routing, and virtual devices other applications can select. On Windows
this needs a virtual audio driver. PipeWire is a routing graph, so the
Linux equivalent is null sinks, links and WirePlumber policy, with no
driver work.

## 2. Ecosystem survey (2026-08-24)

- [rikkichy/openwave](https://github.com/rikkichy/openwave), MIT,
  Python, GTK4, libusb via ctypes. Device controls for the original
  Wave XLR (`0fd9:007d`): gain, mute, headphone volume, low impedance,
  hardware sync by polling, tray icon, udev setup, and a keepalive
  daemon that holds the mic stream open to avoid a firmware race that
  otherwise drops capture to silence. Its protocol work established the
  `wIndex=0x3303` trick: the kernel blocks `wIndex=0x3300` (interface 0,
  owned by the audio driver); the firmware only validates the `0x33`
  prefix, and interface 3 is unclaimed, so transfers pass without
  detaching the audio driver.
- [CryoByte33/openwave](https://github.com/CryoByte33/openwave), a fork
  adding a Wave Link style submixer (Personal, Chat and Record mixes,
  virtual microphones, source groups, meters) built from PipeWire null
  sinks and one `pw-loopback` per mix, plus a Wave XLR MK.2 backend
  (`0fd9:00b6`, standard class requests, `wIndex=0x0203`). Its MK.2
  pull request to upstream was closed unmerged (2026-06-25). Its README
  install instructions pointed at upstream, so following them installed
  the version without the mixer.
- [PipeWeaver](https://github.com/pipeweaver/pipeweaver): PipeWire
  matrix mixing for streaming with a web UI and API, pre-release at the
  time; its companion DeckWeaver is a Stream Deck plugin for it. Not
  used: two processes creating sinks and loopbacks in the same graph
  conflict, and the openwave fork's mix model matched Wave Link more
  directly.
- Stream Deck hosts: [OpenDeck](https://github.com/nekename/OpenDeck)
  (Tauri, runs Elgato SDK plugins, OpenAction plugin API) was chosen.
  Its 2.14.0 release (2026-07-29) supports the Stream Deck + XL
  natively, including encoder events and per-encoder LCD rendering. Touch-tap
  support for the + XL needed a later upstream fix; see the
  [current plugin instructions](features.md#opendeck-plugin). StreamController's + XL support was in beta with open
  dial and touchscreen issues. Neither host imports Elgato profiles;
  profiles are rebuilt by hand.
- Adjacent projects used as references: goxlr-utility (headless daemon
  owning the hardware, stable API, interchangeable frontends), the
  alsa-scarlett-gui and kernel drivers for Focusrite interfaces, and
  the capture workflows documented by OpenRazer, Solaar, HeadsetControl,
  rivalcfg, OpenRGB and ckb-next.

## 3. Hardware findings (2026-08-25)

- The interface on hand was a Wave XLR Pro, `0fd9:00b4`, a third
  revision distinct from the MK.1 (`007d`) and MK.2 (`00b6`). USB Audio
  Class 2, `bcdDevice 04.10`. Interfaces: If0 AudioControl with a
  6-byte interrupt IN endpoint (1 ms), If1 and If2 audio streaming, If3
  vendor-specific (class 0xFF) with no endpoints and no kernel driver.
  The Pro has no physical controls or LEDs; the interrupt endpoint does
  not carry user input.
- Audio works out of the box on Linux (multichannel source and sink in
  PipeWire), and mic gain is a standard UAC2 ALSA capture control
  (0 to 80 dB) with capture mute switches. Only phantom power, the DSP
  toggles, the monitor blend and output routing needed the vendor
  interface.
- No Linux software supported `00b4` at the time: neither openwave
  repository listed it, and a GitHub code search for the id found
  nothing. The
  [LukasParke/wave3-research](https://github.com/LukasParke/wave3-research)
  teardown of Wave Link shows the Pro as `LWT::WaveXLRProDevice` (LWT
  for Lewitt, Elgato's audio OEM) and three vendor backend strategies;
  which one the Pro uses was not visible.
- Companion hardware: Stream Deck + XL, `0fd9:00c6`, 36 keys, 6
  encoders, a touch strip, one HID interface, on a "USB Dock for Stream
  Deck +" (`0fd9:00ac`) that is a USB billboard device with no audio
  function.

## 4. Protocol capture (2026-08-25)

A USBPcap capture of Wave Link driving the Pro on Windows, decoded with
tshark, showed a third protocol family: a paged property bank over
vendor control transfers to interface 3 (`bmRequestType 0x41/0xc1`,
`bRequest 1`, `wIndex 0x0103`, `wValue` = block). Wave Link polls every
readable block at about 17 Hz and writes whole blocks read-modify-write.
The block and offset map, later captures, and hardware verification are
in [wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md); the capture
method is in [wave-xlr-pro-capture-plan.md](wave-xlr-pro-capture-plan.md).

## 5. Decisions

- Standalone product rather than a fork of openwave: with the Pro
  protocol captured independently and verified on hardware, the project
  is not dependent on either openwave repository. openwave (MIT) and its
  MK.2 backend remain a reference for the MK.1 and MK.2 dialects.
- Architecture: a headless daemon owns the device, the PipeWire
  submixer and a control API; the UI and the OpenDeck plugin are
  clients of that API. Control software stays out of the audio path.
- Stack: .NET 10 and Avalonia for daemon and UI, one language across
  both; the OpenDeck plugin is a JavaScript OpenAction plugin.
- Name: OpenXLR. "XLR" is the connector, not a trademark, so interfaces
  from other brands can be added behind the same daemon, UI and plugin.
