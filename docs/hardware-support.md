# Hardware support

The controls OpenXLR exposes, with recorded hardware verification kept
separate from implementation status. An unavailable control may exist in
the device but have no mapped OpenXLR command. See the final section for
the checks that still need an owner.

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | `0fd9:00b4` | exposed controls verified on hardware |
| XLR Dock | `0fd9:00a6` | exposed controls verified on hardware |
| Wave XLR | `0fd9:007d` | core controls verified on hardware by community testers on two units (0.1.13) |
| Wave XLR MK.2 | `0fd9:00b6` | exposed controls verified on hardware by a community tester |
| XLR Dock MK.2 | `0fd9:00c7` | MK.2 backend at the Pro's block bank; exposed controls verified on hardware |

## Wave XLR Pro (0fd9:00b4)

Vendor block protocol decoded and documented in
[wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md): config blocks for
both XLR inputs, headphone block, crossfade and output selectors, and
the commit block every selector write needs.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 80 dB, mute (per XLR input) | verified | both inputs, independent structures |
| Low cut, expander, voice tune + strength | verified | per input |
| Phantom 48V, ClipGuard, compressor | verified | ClipGuard is an inverted byte in the protocol. The firmware mutes the input for about 13 s around every 48V change (anti-thump) and unmutes it itself; the UI counts the hold down on the mute button |
| Headphone volumes x2, low impedance | verified | independent jacks |
| Mic and PC crossfade | verified | direct monitor inside the device |
| Physical output routing | verified | HP1, HP2, Line Out, USB Aux; verified by listening on both jacks |
| USB Aux input level + lock, aux return | verified | return routing latches at stream open; the daemon bounces the stream |
| Reset to OpenXLR's baseline | verified | the device keeps its settings, so the reset writes a known set (gain 30 dB, everything off, levels at half, crossfade on PC) instead of recorded firmware defaults |

The Pro's onboard EQ, ducking, mix maximizer and channel booster have no
mapped OpenXLR controls. Elgato describes all four as running on the
device: a four-band equalizer, ducking that lowers the other mixes while
you speak and is applied per mix, a maximizer per mix, and a booster
worth up to 12 dB above the normal ceiling on any input. The same
four-band equalizer runs on the Wave XLR MK.2 and the XLR Dock MK.2,
which have no ducking. The full hardware mix matrix is also unfinished.
Elgato's [Pro feature guide](https://www.elgato.com/us/en/explorer/products/wave/wave-xlr-pro-give-your-setup-superpowers/)
distinguishes those onboard effects from VST processing on the computer.
OpenXLR exposes Voice Tune and the other DSP controls listed above; its
plugin inserts run in the Linux audio graph.

## XLR Dock (0fd9:00a6)

The Stream Deck+ module. It has no onboard voice-processing DSP: Wave
Link is its processing host on Windows. On Linux OpenXLR drives gain,
mute and headphone volume through the kernel's standard ALSA controls
and provides the DSP host-side in the submixer. Phantom power and
headphone low impedance live in firmware registers the kernel does not
expose, reached over the original Wave XLR's protocol dialect.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 75 dB | verified | analog preamp; confirmed by level measurement |
| Mute, headphone volume | verified | standard ALSA controls |
| Low cut 80 / 120 Hz | software | PipeWire high-pass in the mic path; response measured with test tones as second-order |
| ClipGuard | software | post-ADC hard limiter at -3 dB, measured with test tones; needs `swh-plugins` and cannot repair analogue/ADC clipping. If the plugin is missing, the control is disabled and the current mic route remains live |
| Gain lock | software | the daemon rejects all gain changes while set; the dock has no physical dial to bypass it. The gain the dock is given back on connect is not a change and is written even when locked, since the dock forgets it at every power cycle |
| Phantom power | verified | byte 6 of the dock's config block over the original Wave XLR's protocol dialect. Identified by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) on the MK.1 against its 48V LED; confirmed here with a condenser microphone on the dock's XLR. Wave Link does not write it for the dock |
| Low impedance | verified | byte 33 of the same config block, verified by listening on the dock's headphone jack |
| Device info block (0x000A) | read | 51 bytes; carries the unit's USB serial in ASCII from offset 35, so the diagnostics exporter masks it in the hex dump |
| Hardware sidetone | unmapped | no control path found in the byte sweep; this does not establish whether the hardware supports it |

Kernel behaviour: the kernel starves the dock's capture endpoint when
playback to it starts first, and the mic records silence. OpenXLR
ships a WirePlumber rule
([packaging/50-xlr-dock-capture-hold.conf](../packaging/50-xlr-dock-capture-hold.conf))
that keeps the capture source always active, so playback can never
start first.

## Wave XLR (0fd9:007d)

The original MK.1. Its class protocol was documented by the
[openwave](https://github.com/rikkichy/openwave) project, and community
testers have run OpenXLR against two units. A daemon stall reported on
one of them ([issue #6](https://github.com/emaspa/openxlr/issues/6))
turned out not to be the USB write, which completes in milliseconds,
but the daemon's stream sweep starving its own clients; fixed in
0.1.13 by the reporter's own change ([PR #7](https://github.com/emaspa/openxlr/pull/7)).

| Control | State | Notes |
|---|---|---|
| Gain, mute | verified | community tester; scale is 256 raw units per dB ([openwave PR #8](https://github.com/rikkichy/openwave/pull/8) measured it on the shared protocol) |
| Headphone volume, low impedance | verified | community tester |
| Phantom 48V | coded | config byte 6, found by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) against the MK.1's own 48V LED; the same byte is verified on the XLR Dock. Added after the tester's run, so an LED check on a MK.1 is still open |
| Hardware low cut, ClipGuard, mic/PC crossfade | unmapped | Wave Link exposes these controls; their OpenXLR offsets are unknown. Software low cut and limiting are available in the submixer |
| Save settings to hardware, LED colours | unmapped | OpenXLR restores its last observed settings on connect instead of issuing Wave Link's hardware-save operation |

Elgato's [Wave XLR settings guide](https://help.elgato.com/hc/en-us/articles/4404228579853-Elgato-Wave-XLR-Wave-Link-Settings-Overview)
documents a separate hardware-save action. OpenXLR's `retainsSettings: false`
capability describes its restoration policy, not an absence of device memory.
Hardware low cut or ClipGuard configured in Wave Link may remain active;
OpenXLR cannot currently read or disable those settings.

## Wave XLR MK.2 (0fd9:00b6) and XLR Dock MK.2 (0fd9:00c7), verified

Decoded from USB captures of Wave Link, using the Pro's protocol family
at its own address. On 2026-09-02 a community tester
([issue #2](https://github.com/emaspa/openxlr/issues/2)) ran OpenXLR
0.1.10 against a Wave XLR MK.2: the daemon connected, all three blocks
read at the expected lengths (38, 2 and 6 bytes), and every exposed
control changed the device, with the device's own gain mark following
the software and the physical dial reflected back. The settings block's
bytes 1 and 2 follow the Pro's per-input structure (bit 1 phantom, bit 7
compressor, byte 2 = 0x04 for ClipGuard off); those three controls were
exposed at the Pro's positions in 0.1.12 and the same tester confirmed
each of them works.

The XLR Dock MK.2 for the Stream Deck+ is built on the same Wave FX
platform (80 dB gain, phantom, ClipGuard 2.0, onboard expander, voice
tune, compressor, EQ). Its `lsusb -v` dump
([issue #1](https://github.com/emaspa/openxlr/issues/1)) shows the same
five interfaces as the Wave XLR MK.2, including the vendor-specific
interface 3 that carries the control protocol. Run against one on
2026-09-05: the blocks have the MK.2 layout (0x0004 input settings,
38 bytes; 0x0005 headphones, 2 bytes; 0x0001 crossfade, 6 bytes) but
the firmware serves them at `wIndex 0x0103`, the Pro's bank, and stalls
the MK.2's `0x0203`; the backend uses 0x0103 for the dock since 0.1.20.
There is no commit block (0x0003 stalls) and writes take effect at
once. Gain, mute and headphone volume were cross-checked against the
kernel's ALSA controls for the card, which mirror the feature units:
every write showed up there and read back from the block. Blocks
0x0002 and 0x0006 exist too and are not decoded yet.

| Control | State | Notes |
|---|---|---|
| Gain, mute, low cut, expander, voice tune + strength | verified | Wave XLR MK.2: community tester, reads and writes. Dock: all of them on the author's unit |
| Headphone volume, low impedance, crossfade | verified | Wave XLR MK.2: community tester. Dock: all three on the author's unit (the mic leaves the direct monitor at the PC end of the crossfade) |
| Phantom 48V, ClipGuard, compressor | verified | at the Pro's bit positions; community tester, 0.1.12. Dock: phantom confirmed with a condenser mic going silent when switched off, ClipGuard and compressor by ear |

Every exposed control listed above was confirmed on the dock on
2026-09-05, the DSP ones by ear through the monitor mix and phantom with a condenser microphone.
Hardware EQ is not mapped. Blocks 0x0002 and 0x0006 exist and are not decoded.

## Every device gets

- Capability-driven UI: controls, channels, and mixes the device does
  not have are not shown
- Per-device profiles: named scenes of hardware state plus the whole
  submix, recalled from the UI, the API or a Stream Deck key, and one
  of them on connect if chosen
- Last settings restored on connect for Wave XLR and the first XLR Dock,
  plus a reset to the defaults recorded after a power cycle. The Pro and the MK.2 family, the XLR
  Dock MK.2 included, keep their settings on board (verified by
  replugging the dock)
- Multi-device switching: a header picker chooses which interface
  OpenXLR drives; the mixer's input channels follow it
- On switch, the hardware channels' monitor sends come up muted, so the
  newly patched mic does not reach the speakers until unmuted
- OpenDeck plugin: every switch, mute, and level on a Stream Deck, with
  live state

## Help confirm a control

Anything marked "coded" above can be
confirmed in a few minutes:

1. Install OpenXLR per the [README](../README.md), including the udev
   rule.
2. Toggle the control and check the effect on the device itself: an
   LED, the sound in the headphones, a condenser mic on the XLR.
3. In the app: Options, then SUPPORT, then Collect diagnostics.
4. Open an [issue](https://github.com/emaspa/openxlr/issues) with the
   archive and what you observed.

MK.1 owners can help map low cut, ClipGuard, mic/PC crossfade and the
hardware-save action with an ordered Wave Link capture. The
[USB capture guide](usb-capture.md) explains the process in about 15
minutes, no programming needed.
