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
| XLR Dock MK.2 | `0fd9:00c7` | MK.2 backend with bank detection; exposed controls verified on the original 0x0103 unit |
| Wave:3 | `0fd9:0070` | coded from public protocol research; not run on a Wave:3 by anyone on the project, every control waits on an owner |

## USB access

The udev rule ([packaging/70-openxlr.rules](../packaging/70-openxlr.rules))
grants the logged-in user access to the six product ids above. Every
backend opens its device through libusb and claims the vendor-specific
interface 3 before the first control transfer, both when libusb runs in
the daemon and in the USB helper process. No kernel driver owns that
interface on any model in the family, so nothing is detached. A claim
that fails closes the handle, says why on stderr and reports the device
as not opened. A device that opens but fails a state read is dropped and
reopened after 2 s; the wait doubles on each failed read that follows,
up to 32 s, and a read that succeeds resets it.

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
| Gain 0 to 75 dB | verified | analog preamp; confirmed by level measurement. Through the standard ALSA control, or through the gain word of the dock's config block on a unit whose card lacks that control (see below) |
| Mute, headphone volume | verified | standard ALSA controls, with the same config block fallback |
| Low cut 80 / 120 Hz | software | PipeWire high-pass in the mic path; response measured with test tones as second-order |
| ClipGuard | software | post-ADC hard limiter at -3 dB, measured with test tones; needs `swh-plugins` and cannot repair analogue/ADC clipping. If the plugin is missing, the control is disabled and the current mic route remains live |
| Gain lock | software | the daemon rejects all gain changes while set; the dock has no physical dial to bypass it. The gain the dock is given back on connect is not a change and is written even when locked, since the dock forgets it at every power cycle |
| Phantom power | verified | byte 6 of the dock's config block over the original Wave XLR's protocol dialect. Identified by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) on the MK.1 against its 48V LED; confirmed here with a condenser microphone on the dock's XLR. Wave Link does not write it for the dock |
| Low impedance | verified | byte 33 of the same config block, verified by listening on the dock's headphone jack |
| Device info block (0x000A) | read | 51 bytes; carries the unit's USB serial in ASCII from offset 35, so the diagnostics exporter masks it in the hex dump |
| Hardware sidetone | unmapped | no control path found in the byte sweep; this does not establish whether the hardware supports it |

The config block follows the original Wave XLR's layout: gain as a Q8.8 dB
word at offset 0, mute at byte 4, phantom at byte 6, headphone volume as a
signed Q8.8 word at offset 9, low impedance at byte 33. One dock in the
field, the same product and firmware revision 2.10 as the unit verified
here, answers the capture volume's range query with a maximum no higher
than the minimum. The kernel then drops the 'Mic Capture Volume' control
and the card carries only the two switches and the playback volume. The
block reads the live register, and with a microphone attached the capture
level moved by the same 70 dB whether gain was set through ALSA or through
the word, and an ALSA write shows up in the block at once. On such a unit
the daemon drives the missing control through the block and says so in
its journal on connect and in the window's Options, under INTERFACE.
Each control uses one path only, because the
kernel caches mixer values and would not see a block write behind its
back. A control the card lacks while the USB handle is not open (the
udev rule not yet applied) is reported unavailable and left out of the
capabilities; the dock stays connected and the journal names the control.
The diagnostics dump lists the path of each of the three controls.

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

Kernel behaviour: the same full-duplex ordering bug as the dock. In the
pro-audio profile the MK.1's capture and playback nodes share one node
group, and a playback stream opening first leaves the mic recording
silence for the life of the capture stream. OpenXLR ships a second
WirePlumber rule
([packaging/52-openxlr-mk1-capture-hold.conf](../packaging/52-openxlr-mk1-capture-hold.conf))
that keeps the MK.1's capture source always active. The rule matches
the Wave XLR's node name; the Pro's nodes carry a different vendor
string and are not touched. The Debian, RPM and Nix packages install
it; a source install copies it by hand. A
community tester found the bug and verified the rule on their unit.

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
the MK.2's `0x0203`. A later owner report describes the reverse: a
`0fd9:00c7` unit stalled at `0x0103` and returned all three expected
blocks at `0x0203`; using that bank restored control on OpenXLR 0.1.45.
The September 20 Omarchy diagnostics confirm repeated settings-block
stalls while ordinary PipeWire audio remains available. They do not
contain successful alternate-bank reads, so that part remains owner-reported.

The backend probes `0x0103` first, then `0x0203` only if a block stalls
or has an unexpected length. Detection writes nothing and accepts a bank
only after all three blocks return 38, 2 and 6 bytes. Other USB errors
keep their normal failure handling. The selected bank is shown in the
connection note, stays fixed until disconnect and is detected again on
reconnect. Both banks failing closes the handle without writing. Automated
transport tests cover both variants; the automatic probe still needs an
owner run on each physical variant.
There is no commit block (0x0003 stalls) and writes take effect at
once. Gain, mute and headphone volume were cross-checked against the
kernel's ALSA controls for the card, which mirror the feature units:
every write showed up there and read back from the block. Blocks
0x0002 and 0x0006 exist too and are not decoded yet.

The backend asks for the three blocks at those lengths. An answer
shorter than the block but long enough for the offsets in use (11 bytes
of the settings block, both headphone bytes, the first crossfade byte)
is decoded as it is, and the diagnostics dump shows the length the
firmware answered; an answer shorter than that is refused as a failed
read.

| Control | State | Notes |
|---|---|---|
| Gain, mute, low cut, expander, voice tune + strength | verified | Wave XLR MK.2: community tester, reads and writes. Dock: all of them on the author's unit |
| Headphone volume, low impedance, crossfade | verified | Wave XLR MK.2: community tester. Dock: all three on the author's unit (the mic leaves the direct monitor at the PC end of the crossfade) |
| Phantom 48V, ClipGuard, compressor | verified | at the Pro's bit positions; community tester, 0.1.12. Dock: phantom confirmed with a condenser mic going silent when switched off, ClipGuard and compressor by ear |

Every exposed control listed above was confirmed on the dock on
2026-09-05, the DSP ones by ear through the monitor mix and phantom with a condenser microphone.
Hardware EQ is not mapped. Blocks 0x0002 and 0x0006 exist and are not decoded.

## Wave:3 (0fd9:0070), coded and unverified

The USB condenser microphone with a headphone jack, not an XLR
interface. Nobody on the project owns one, so nothing in this section
has been run on a Wave:3 from here. The backend,
`src/OpenXLR.Core/Devices/Wave3Device.cs`, takes every protocol fact
from three public sources and names the source on each line. Every
control below is coded, none verified.

- [rikkichy/openwave](https://github.com/rikkichy/openwave/blob/main/docs/protocol.md):
  an implementation that runs on the hardware and has users. Where it
  and a schema reading differ, it wins.
- [LukasParke/wave3-research](https://github.com/LukasParke/wave3-research):
  live probing of one unit (firmware 0.3.7, API 5.3) from Linux, with
  descriptor and PipeWire dumps.
- [zhgmx/LibreWave](https://github.com/zhgmx/LibreWave/blob/main/docs/protocol-evidence.md):
  a 16-byte `/config` schema recovered from Wave Link for API 5.3 and
  5.4, not run on hardware by that project.

They agree on the transport: the original Wave XLR's class-request
dialect (read 0xA1/0x85, write 0x21/0x05, wIndex 0x3303) with a 16-byte
config block at wValue 0 and a device info block at 0x000A.
wave3-research found that vendor-type requests stall and that a block
scan with them rebooted the unit into its DFU product id 0x0071, so the
backend sends class requests only, and only for those two blocks. On the
layout they agree on the gain (a Q8.8 dB word at 0, 0 to 40), the mute
(byte 4), ClipGuard (byte 5), the headphone level (a signed Q8.8 dB word
at 7, -60 to 0), the headphone mute (byte 9) and the dial's target (byte
12). openwave writes the monitor balance as a Q8.8 percent word at 10 on
the hardware, LibreWave's schema reads it the same way, and
wave3-research reads byte 11 as the dial's mix value, which is that
word's integer byte; the backend writes the word as the crossfade.

Three bytes are read differently by the sources and touched by none of
the backend's setters. Byte 6 is the low cut in LibreWave's schema;
openwave has no code path that reaches it on any device and keeps its
low cut as a filter in the capture chain, and wave3-research wrote it
and saw no effect. The low cut on the Wave:3 is the submixer's, by
design, not as a fallback. Byte 15 is a gain lock in LibreWave, a device
policy that ignores the host's volume requests, and the LED brightness
in wave3-research. Bytes 13 and 14 are the ring's blue and a second,
software monitor mix in wave3-research and unnamed in LibreWave. Every
write reads the block, changes one field and sends all 16 bytes back, so
those bytes, and 2 and 3, are retransmitted with the values read and
never changed by OpenXLR. No value is snapped to a grid: openwave writes
arbitrary raw values on the hardware, so the firmware needs none, and a
grid would swallow the Stream Deck's five-unit crossfade ticks.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 40 dB | coded | Q8.8 word at 0. openwave reads and writes it there; wave3-research logged the same word following the dial in each of its modes and says host writes are ignored. The backend trusts the word while the dial is on gain, where both agree, remembers it, and reports that while the dial is elsewhere; until the dial has been on gain once since the connect, it reports the word as openwave does |
| Mute | coded | byte 4; the capacitive mute pad toggles the same byte in wave3-research's log |
| ClipGuard | coded | byte 5; taken as hardware, so the software limiter is not offered |
| Headphone volume -60 to 0 dB | coded | signed Q8.8 word at 7, written truncated toward zero as openwave does ("matching the firmware setter's int()") |
| Direct monitor balance, as the crossfade | coded | Q8.8 percent at 10, half a percent per crossfade unit, shown as the Mic and PC crossfade (0 to 200). 0 is taken as microphone only and 100 as PC only; none of the sources states the direction |
| Headphone mute | read | byte 9, in the state as `hpMute`. wave3-research saw the firmware assert it when the level reaches its floor; a level written above the floor releases it, so the jack cannot stay silent with nothing in OpenXLR to release it. No control sets it |
| Low cut | software | the submixer's high-pass, by design; byte 6 is carried as read |
| Gain lock | software | the daemon's own lock, kept off the window because of the dial, as on every device with one; byte 15 is carried as read |
| Dial target | read | byte 12: 1 gain, 2 headphones, 3 monitor mix; named in the diagnostics dump, with where the reported gain came from |
| Mute ring colour, LED brightness | unmapped | wave3-research's bytes 10, 11, 13 and 15; carried as read where no setter owns them |
| Device info block (0x000A) | read | 51 bytes as wave3-research read it (openwave asks for 64 and places the serial elsewhere); the diagnostics exporter masks the serial wherever it lands |

The one input strip is the capsule; it carries the XLR 1 name the mixer
gives its first hardware input. The Wave:3 has no XLR jack, no phantom
power, no second input, no output routing and no aux port, and none of
those is offered. None of the sources says whether the microphone keeps
its settings across a power cycle. The backend leaves `retainsSettings`
at true, so the daemon writes nothing on connect that the user did not
ask for, and a replug on real hardware decides it.

With another supported interface attached, the daemon drives that one:
the registry lists attached devices in its own order, verified backends
first and the Wave:3 last, and the daemon takes the first when none is
chosen. The Wave:3 is driven when it is alone on the bus or picked from
the header, or through `OPENXLR_DEVICE=0070`.

The PipeWire node names in wave3-research's dump are
`alsa_input.usb-Elgato_Systems_Elgato_Wave_3_<serial>-00.mono-fallback`
and the matching `alsa_output`: udev turns the colon in the product
string into an underscore, and that is the fragment the daemon looks
for. No capture-hold WirePlumber rule ships for it. openwave applies its
own rule to the Wave:3 alongside the Wave XLR, but the ordering bug the
XLR Dock and the original Wave XLR have has not been reported on a
Wave:3 and its dump shows no shared node group. An owner who records
silence after a playback stream opened first would need a copy of
`52-openxlr-mk1-capture-hold.conf` matching `Elgato_Wave_3_`.

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

A Wave:3 owner moves that section from coded to verified with the
following, in the order that risks least, each result with the
diagnostics archive:

1. Plug the microphone in with the udev rule installed. Alone on the
   bus the daemon drives it at once; next to another supported
   interface it drives that one and lists the Wave:3 in the header
   picker, so pick it. The diagnostics dump shows a 16-byte `config`
   block, a `dial` line and a `gain` line. If the block is another
   length, stop there and report it.
2. Turn the dial through its three modes and take a dump in each: the
   `dial` line follows (gain, headphones, monitor mix), and the first
   two bytes of the block either stay at the gain or follow the dial's
   value in headphone and mix mode. That decides which reading of those
   bytes is right and whether the backend's gain handling can be
   simplified.
3. Mute from the window and from the capacitive pad: the state and the
   LED ring agree both ways.
4. ClipGuard from the window: the setting survives a readback, and a
   loud signal is caught by the hardware. If the byte does nothing, say
   so; the software limiter can take over.
5. Headphone volume from the window: the level in the headphones
   follows, -60 dB is near silence, 0 dB is the loudest. Then turn the
   dial in headphone mode down to its floor and take a dump: `hpMute`
   in the state turns on if the firmware asserts it there. Raise the
   volume from the window: sound is back and `hpMute` is off.
6. Gain from the window with the dial on gain: the microphone's own
   gain display and the recorded level follow, or they do not and the
   value snaps back, which is what wave3-research predicts.
7. The crossfade from the window: with a playback stream running and
   the microphone live, the left end leaves only the microphone in the
   headphones and the right end only the computer. If the ends are the
   other way round, say so.
8. Clear the "On connect" profile picker under the profile list, so
   nothing is written on connect, then unplug and replug: the settings
   from before are still there, or the microphone came back at
   defaults, which decides `retainsSettings`.
9. Play through the microphone's own sink before anything records from
   it, then record: sound, or the silence that would call for a
   capture-hold rule.
