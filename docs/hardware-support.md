# Hardware support

The controls OpenXLR exposes, with recorded hardware verification kept
separate from implementation status. An unavailable control may exist in
the device but have no mapped OpenXLR command. See the final section for
the checks that still need an owner. A microphone or interface of another
brand works in the mixer without hardware controls; see
[Other microphones and interfaces](#other-microphones).

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | `0fd9:00b4` | exposed controls verified on hardware, the microphone EQ on the unit included; ducking on the unit accepted by ear on the headphone jacks, keyed by XLR 1 |
| XLR Dock | `0fd9:00a6` | exposed controls verified on hardware |
| Wave XLR | `0fd9:007d` | core controls verified on hardware by community testers on two units (0.1.13) |
| Wave XLR MK.2 | `0fd9:00b6` | exposed controls verified on hardware by a community tester; the microphone EQ on the unit verified on hardware |
| XLR Dock MK.2 | `0fd9:00c7` | MK.2 backend with bank detection; exposed controls verified on the original 0x0103 unit, and the microphone EQ on the unit verified on firmware 1.0.15 |
| Wave:3 | `0fd9:0070` | coded from public protocol research; on the maintainer's unit every control writes and reads back, and the gain, the monitor balance and the desktop volume controls are verified |
| Wave:1 | `0fd9:006e` | the Wave:3's backend under the Wave:1's id; an owner's two archives showed the Wave:3's block, firmware line and desktop volume mirror, and the gain, mute and headphone volume writes taking effect; ClipGuard and the mix are not yet confirmed |

## USB access

The udev rule ([packaging/70-openxlr.rules](../packaging/70-openxlr.rules))
grants the logged-in user access to the seven product ids above. Every
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
| Ducking on the unit, keyed by XLR 1 | verified on the jacks | blocks 0x0008 and 0x0001 as Wave Link 3.5 writes them (captures of 2026-10-21). A desk probe (2026-10-23) found that the unit ducks a pair only when both bits of its field are set; with both set the jacks dipped by the configured depth, on the feed OpenXLR uses, with a test tone as the key. The daemon now writes both bits, and on the desk (2026-10-23) a voice on XLR 1 ducked the music on Headphones 1 at each amount and speed, with no dropout when switching ducking or its side. XLR 2 does not trigger it, and a muted XLR 1 still does. The USB Aux port's mix ducked on its USB stream and was not listened to, so it stays in software |
| Reset to OpenXLR's baseline | verified | the device keeps its settings, so the reset writes a known set (gain 30 dB, everything off, levels at half, crossfade on PC) instead of recorded firmware defaults |
| Microphone EQ on the unit, firmware 1.0.14 or later | verified | both inputs, captured from Wave Link 3.5 on firmware 1.0.14; the encoder reproduces the captured writes byte for byte. Used on the maintainer's unit and by other owners. On the desk a +12 dB bell at 5 kHz written from OpenXLR read +12.3 to +12.7 dB on the raw USB capture, and software mode left the capture flat. On older firmware, or when the version or the EQ fields cannot be read, both inputs' EQ runs in software |

The [microphone EQ](api.md#where-the-eq-runs) runs on the Pro from
firmware 1.0.14, the version Wave Link 3.5 ran on in captures 16 to 20 of
2026-10-21. A newer version is allowed; an older or unreadable one leaves
the EQ to software. Block 0x0004 holds each input's EQ in the MK.2
family's layout, XLR 1's in its struct at offset 0 and XLR 2's at 38. The
switch is bit 0 of byte 2, or byte 40 for XLR 2, beside ClipGuard's
inverted bit 2. Four five-byte bands follow at 18 to 37, or 56 to 75, each
holding flags, gain in quarter decibels, frequency in hertz and the Q
byte. The idle block decoded to Wave Link's saved EQ on both inputs. Wave
Link switched each input's EQ on by writing that one bit and wrote band
2's gain and frequency live during a drag. Every write read back on the
next poll with no 0x0003 commit.

Wave Link sends the commit on its own timing, which looks like a save for
standalone use, so OpenXLR sends none with an EQ write. Nobody has checked
yet whether an EQ written without it survives a power cycle; OpenXLR
writes its own EQ again at every connect anyway. Wave Link left this Pro
with both inputs' EQ on and band 2 at +12 dB. On connect OpenXLR writes
each microphone's own EQ over that, or switches it off where the EQ runs
in software or the strip is bound to another source. The diagnostics
archive carries block 0x0000 and both inputs' EQ decoded. The maintainer
and other owners use the EQ on the unit, and on the desk +12 dB at 5 kHz
read +12.3 to +12.7 dB on the raw USB capture, with software mode leaving
it flat.

The Pro's mix maximizer and channel booster have no mapped OpenXLR
controls. Elgato describes them, and ducking, as running on the device: a
maximizer per mix, a booster worth up to 12 dB above the normal ceiling on
any input, and ducking that lowers the other mixes while you speak and is
applied per mix.

Ducking comes from captures of Wave Link 3.5 on Windows, firmware 1.0.14. Block
0x0008 holds twelve entries, one per hardware input and USB playback pair,
with the key threshold, depth, attack, release and a ducked flag, and each
of the five mix sections of block 0x0001 holds a 2-bit field per USB pair
whose high bit Wave Link sets on a ducked pair. With XLR 1 loud the unit
lowered the ducked pairs by the configured depth with no host write. The
daemon can write the same bytes, without the commit block, on the two pairs
OpenXLR streams to: 2/3 for the jacks and Line Out, 10/11 for the USB Aux
port. A first desk test saw the unit's meters lower pair 2/3 while XLR 1
was loud and the headphones stay level: the daemon set the high bit of
each field alone, and the unit ducks a pair only when both bits are set.
With both bits set the jacks dipped by the configured depth on the path
OpenXLR feeds them through, so the daemon writes both and the unit runs
ducking on the jacks when the setup fits. The USB Aux port stays in
software until it is checked by ear. The captures never show how the key is encoded, so the unit
would run only a setup keyed by XLR 1. Details are in the
[protocol notes](wave-xlr-pro-protocol.md#11-ducking-captures-of-2026-10-21),
and the probe in
[section 12](wave-xlr-pro-protocol.md#12-the-mix-sections-and-the-jacks-desk-probe-of-2026-10-23). The MK.2
models have no ducking. They run a four-band equalizer on the device from
firmware 1.0.15, switched on by a master enable bit, and OpenXLR's
microphone EQ runs there on those units, as Wave Link 3.5 does
([findings](mk2-eq-findings.md), and the MK.2 section below). The full hardware mix matrix is also
unfinished.
Elgato's [Pro feature guide](https://www.elgato.com/us/en/explorer/products/wave/wave-xlr-pro-give-your-setup-superpowers/)
distinguishes those onboard effects from VST processing on the computer.
OpenXLR exposes Voice Tune and the other DSP controls listed above; its
plugin inserts run in the Linux audio graph.
The [hardware effects capture guide](hardware-effects-capture.md) lists
the per-model captures needed to map the remaining onboard controls.

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
its journal on connect and in the UI's Options, under INTERFACE.
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

The MK.1's ALSA card carries 'Mic Capture Volume', 'Mic Capture Switch'
and 'PCM Playback Volume', the controls a desktop's sound settings and
WirePlumber's restore at login act on. The kernel caches their values and
never reads them back from the interface. An owner's archive showed the
card's capture volume, which spans 0 to 75 dB, cached at about 74 dB while
the config block read 48 dB. The backend keeps the card in step with
the block as it does on the Wave:3: a change in the block (the dial, the
mute pad, a write from OpenXLR) goes to the card on the next poll, and
every half second a mute or headphone level changed on the card goes into
the block. The gain only goes from the block to the card. Each control's
range is read from the card at connect, and a control the card lacks or
reports with an empty range is left alone. Whether the MK.1's firmware
applies a gain written through the card, as the XLR Dock's does, has not
been checked on an MK.1. Without its card, or without `amixer`, the
interface is still driven, and the diagnostics dump gives the reason on
its `alsa` line.

| Control | State | Notes |
|---|---|---|
| Gain, mute | verified | community tester; scale is 256 raw units per dB ([openwave PR #8](https://github.com/rikkichy/openwave/pull/8) measured it on the shared protocol) |
| Headphone volume, low impedance | verified | community tester |
| Phantom 48V | coded | config byte 6, found by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) against the MK.1's own 48V LED; the same byte is verified on the XLR Dock. Added after the tester's run, so an LED check on a MK.1 is still open |
| Desktop volume controls | coded | the card's three controls kept in step with the config block, as on the Wave:3; see above. Not yet checked on an MK.1 |
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
tune and compressor). Elgato also lists an EQ. From firmware 1.0.15 both
MK.2 models run it on the device: Wave Link 3.5 writes it there, the Wave
XLR MK.2 was heard applying it on its own, and OpenXLR's microphone EQ
runs there too. On the dock's firmware 1.0.0 Wave Link ran it on the
computer ([findings](mk2-eq-findings.md)). Its `lsusb -v` dump
([issue #1](https://github.com/emaspa/openxlr/issues/1)) shows the same
five interfaces as the Wave XLR MK.2, including the vendor-specific
interface 3 that carries the control protocol. Interface 3 has two
alternate settings. Setting 0 has no endpoints, every control transfer
below goes to it, and OpenXLR never leaves it. Setting 1 adds an
interrupt IN endpoint `0x84` and an interrupt OUT endpoint `0x02`; the
author's unit reports them as 204 and 404 bytes. Wave Link 3.5 selects
setting 1 and streams the microphone to the computer and the processed
result back over those two endpoints, then runs its effects in its own
plugin host. Run against one on
2026-09-05: the blocks have the MK.2 layout (0x0004 input settings,
38 bytes; 0x0005 headphones, 2 bytes; 0x0001 crossfade, 6 bytes) but
the firmware serves them at `wIndex 0x0103`, the Pro's bank, and stalls
the MK.2's `0x0203`. A later owner report describes the reverse: a
`0fd9:00c7` unit stalled at `0x0103` and returned all three expected
blocks at `0x0203`; using that bank restored control on OpenXLR 0.1.45.
The September 20 Omarchy diagnostics confirm repeated settings-block
stalls while ordinary PipeWire audio remains available. They do not
contain successful alternate-bank reads, so that part remains owner-reported.

Nothing in the descriptors tells the two revisions apart. Both report
`bcdDevice 2.10`, the rest of the descriptor matches, and neither the
interface numbers nor the strings map to the high byte of `wIndex`, so
the backend has to ask the device. It probes `0x0103` first, then
`0x0203` only if a block stalls or answers short. Detection writes
nothing and accepts a bank only after all three blocks fill a 38, 2 and
6 byte read. That is evidence, not proof. A longer block truncated to
the requested length answers the same way.

The bank follows the firmware. On 2026-10-20 the author's dock answered
at `0x0103` on firmware 1.0.0, and at `0x0203` after Wave Link updated it
to 1.0.15, the bank the Wave XLR MK.2 uses on the same firmware
([findings](mk2-eq-findings.md#the-docks-update-to-firmware-1015)). That
fits the owner report above, whose firmware version is not known. The
probe should therefore select `0x0203` on an updated dock and name it in
the connection note. Read from Linux after the update, every block at
`0x0103` stalls and every block at `0x0203` answers, which is the case
the probe's fallback handles, and the daemon drives the updated dock,
its EQ included. The update re-enumerates the dock as a bootloader with product id
`0x00c8` while it flashes.

Other USB errors keep their normal failure handling. The selected bank
stays fixed until disconnect, and the backend detects it again on
reconnect. The connection note names the alternate bank, and names both
banks when neither answers; the ordinary bank adds no note. A dock that
answers on neither bank stays open on `0x0103`, so the reads report what
the firmware does and diagnostics can still dump the blocks. Automated
transport tests cover both variants; the automatic probe still needs an
owner run on each physical variant.
There is no commit block (0x0003 stalls) and writes take effect at
once. Gain, mute and headphone volume were cross-checked against the
kernel's ALSA controls for the card, which mirror the feature units:
every write showed up there and read back from the block. Blocks
0x0000 (117 bytes, with the serial and a firmware build string), 0x0002
(21), 0x0006 (17) and 0x0016 (51 on firmware 1.0.0, 61 on 1.0.15) exist
too; those are the lengths Wave Link asks for. Block 0x0000 bytes 5, 6
and 7 are the firmware version as major, minor and patch (`01 00 0f` is
1.0.15) on both MK.2 models. The rest of those blocks is not decoded yet.

Switching phantom power off makes the dock's firmware set the mute bit
(settings byte 1 bit 0) by itself and clear it 12.5 s later, seen on
firmware 1.0.15 with Wave Link; the Pro does the same for about 13 s. The
daemon's 15 s phantom settling window covers that hold, which has not
been checked from Linux on the dock yet. The update to 1.0.15 also
switched phantom power on for the author's dock, so check it after an
update.

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
| Microphone EQ on the unit, firmware 1.0.15 or later | verified | four bands and a switch in the settings block, written whole per change as Wave Link 3.5 writes it; the encoder reproduces 67 of Wave Link's captured writes byte for byte. The dock's filters were measured from Linux against the software EQ's curves, within about 1 dB for every type, and OpenXLR runs the EQ on the unit. On older firmware, or when the version or the EQ fields cannot be read, the EQ runs in software |

Every exposed control listed above was confirmed on the dock on
2026-09-05, the DSP ones by ear through the monitor mix and phantom with a condenser microphone.
The [microphone EQ](api.md#where-the-eq-runs) runs on the unit from
firmware 1.0.15, read from block 0x0000 bytes 5 to 7 at connect and shown
in the state's `device.firmware`; its layout in the settings block is in
the [findings](mk2-eq-findings.md#the-equalizer-in-block-0x0004). Wave
Link 3.5 leaves that EQ on when it quits, so a unit it has driven can
reach OpenXLR with it running. On connect OpenXLR writes the microphone's
own EQ over it, or switches it off on an input whose EQ runs in software.
The diagnostics archive carries block 0x0000 and the EQ decoded from the
settings block. Blocks 0x0002 and 0x0006 exist and are not decoded.

## Wave:3 (0fd9:0070)

The USB condenser microphone with a headphone jack, not an XLR
interface. The backend, `src/OpenXLR.Core/Devices/Wave3Device.cs`,
takes every protocol fact from three public sources and names the
source on each line. One user ran a quick test on their own unit before
0.1.49: OpenXLR found the microphone, and gain, mute, headphone volume
and the monitor balance answered from the UI.

The maintainer has had a unit since 2026-10-19 (firmware 1.2.4, API
5.4) and ran the checks that need nobody at the microphone, with the
daemon driving it as an additional interface beside a Wave XLR Pro.
Each write was read back twice, in the daemon's state and in the raw
config block read over USB with the daemon released. "Written and read
back" in the table below means the byte changed, held after the daemon
let go, and the kernel's card followed. Nobody has yet watched its LED
ring, turned its dial or listened on its headphones, and a replug is
still owed; the checklist at the end of this page lists those steps.

- [rikkichy/openwave](https://github.com/rikkichy/openwave/blob/main/docs/protocol.md):
  an implementation that runs on the hardware and has users. Where it
  and a schema reading differ, it wins.
- [LukasParke/wave3-research](https://github.com/LukasParke/wave3-research):
  live probing of one unit (API 5.3) from Linux, with descriptor and
  PipeWire dumps. Its "firmware 0.3.7" is bytes 6 to 8 of the device
  info block, which read `00 03 07` on two units with different firmware.
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
| Gain 0 to 40 dB | verified on hardware | Q8.8 word at 0. On the maintainer's unit a gain written with the dial on headphones read back in the block (30 dB as `1E 00`), and going from 40 to 20 dB lowered the recorded noise floor by 20 to 22 dB. openwave reads and writes it there; wave3-research logged the same word following the dial in each of its modes and says host writes are ignored, which neither firmware 1.2.4 nor a Wave:1 bears out. The backend trusts the word while the dial is on gain, where both agree, remembers it, and reports that while the dial is elsewhere; until the dial has been on gain once since the connect, it reports the word as openwave does. A gain it wrote with the dial elsewhere counts once the block reads the word back, which a Wave:1 owner's archive showed the firmware doing; a firmware that drops the write reads back the dial's word and changes nothing |
| Mute | written and read back | byte 4, with the card's 'Mic Capture Switch' following; the capacitive mute pad toggles the same byte in wave3-research's log. The pad and the ring are not yet checked |
| ClipGuard | written and read back | byte 5; taken as hardware, so the software limiter is not offered. Whether it audibly catches a loud signal is not yet checked |
| Headphone volume -60 to 0 dB | written and read back | signed Q8.8 word at 7 (-30 dB as `00 E2`, -12.5 dB as `80 F3`), with the card's 'PCM Playback Volume' following, written truncated toward zero as openwave does ("matching the firmware setter's int()"). openwave lets the word go down to -128 dB; the kernel's control and wave3-research's class range stop at -60, and so does the backend |
| Direct monitor balance, as the crossfade | verified on hardware | Q8.8 percent at 10, half a percent per crossfade unit, shown as the Mic and PC crossfade (0 to 200). The word runs the other way from the crossfade: 0 percent is the computer alone and 100 the microphone alone. None of the sources states the direction; an owner settled it by ear on 2026-10-18, when the straight mapping put all computer audio at the slider's microphone end, and the backend mirrors the scales since |
| Headphone mute | read | byte 9, in the state as `hpMute`. wave3-research saw the firmware assert it when the level reaches its floor; a level written above the floor releases it, so the jack cannot stay silent with nothing in OpenXLR to release it. No control sets it |
| Low cut | software | the submixer's high-pass, by design; byte 6 is carried as read |
| Gain lock | software | the daemon's own lock, kept off the UI because of the dial, as on every device with one; byte 15 is carried as read |
| Dial target | read | byte 12: 1 gain, 2 headphones, 3 monitor mix; named in the diagnostics dump, with where the reported gain came from |
| Mute ring colour, LED brightness | unmapped | wave3-research's bytes 10, 11, 13 and 15; carried as read where no setter owns them |
| Desktop volume controls | verified on hardware | the kernel's 'Mic Capture Volume', 'Mic Capture Switch' and 'PCM Playback Volume' kept in step with the block, as openwave does on the hardware; a change to the sink volume or the source mute in the desktop reached the block within a second. See below |
| Firmware version | read | bytes 21 to 23 of the device info block, openwave's offsets; shown in Options under INTERFACE and in the state's `device.firmware`. Two units read 1.2.2 and 1.2.4 there while bytes 6 to 8 read `00 03 07` on both, and bytes 0 and 1 read 5.3 and 5.4, LibreWave's two API versions. Not yet compared with the version Wave Link shows |
| Device info block (0x000A) | read | 64 bytes on two units, the length openwave requires before it drives a unit (wave3-research read 51); the diagnostics exporter masks the serial wherever it lands |

The microphone's ALSA card carries the three controls that a desktop's
sound settings and WirePlumber's restore at login act on. The kernel
caches those values and never reads them back from the device, so the
backend treats the config block as the one source of truth and keeps the
card in step with it, as openwave does. When the block changes (the dial,
the mute pad, a write from OpenXLR), the next poll writes the new value
to the card. Every half second the backend also reads the card's mute
and headphone level, and writes a change something else made there into
the block. The gain only goes from the block to the card.
wave3-research found its unit ignoring a gain written through the class
control. Firmware 1.2.4 does not: with OpenXLR stopped, a change to any
of the three controls, the gain included, shows in the block and in the
sound, so a desktop's gain change reaches the backend through the block
either way. A card gain that differs from the block is a stale cache.
The half-second readback is what keeps the block in step on firmware
that does not carry the controls over by itself.
Without its card the microphone is still driven, Options says the
desktop's controls are not kept in step, and the diagnostics dump gives
the reason on its `alsa` line.

The one input strip is the capsule, not an XLR jack. The capabilities
say so with `xlrJacks` false, so the mixer names that strip Mic 1 rather
than XLR 1 and gives it the microphone icon, and the UI calls its
input the built-in microphone, never a jack. The Wave:3 has no XLR jack,
no phantom power, no second input, no output routing and no aux port, and
none of those is offered. None of the sources says whether the microphone keeps
its settings across a power cycle. The backend leaves `retainsSettings`
at true, so the daemon writes nothing on connect that the user did not
ask for, and a replug on real hardware decides it.

With another supported interface attached, the daemon drives that one:
the registry lists attached devices in its own order, verified backends
first and the two microphones last, and the daemon takes the first when
none is chosen. The Wave:3 is driven when it is alone on the bus or
picked from the header, or through `OPENXLR_DEVICE=0070`.

The PipeWire node names in wave3-research's dump are
`alsa_input.usb-Elgato_Systems_Elgato_Wave_3_<serial>-00.mono-fallback`
and the matching `alsa_output`: udev turns the colon in the product
string into an underscore, and that is the fragment the daemon looks
for.

Kernel behaviour: the same full-duplex ordering bug as the XLR Dock and
the original Wave XLR, on some ports. An owner's unit (bcdDevice 1.22,
at full speed on a root port of an AMD xHCI controller) recorded
bit-exact zeros after some boots while every stream looked normal.
Opening capture before any playback made it work until the idle suspend
closed the capture. Restarting the daemon did not help; a replug did.
OpenXLR ships a WirePlumber rule
([packaging/53-openxlr-wave3-capture-hold.conf](../packaging/53-openxlr-wave3-capture-hold.conf))
that keeps the Wave:3's capture source always active, as openwave does.
The owner ran with the same settings over several reboots and the
silence did not come back. The rule matches `Elgato_Wave_3_` only, so
the Wave:1 and the Wave XLR models are not touched. The Debian, RPM and
Nix packages install it; a source install copies it by hand.

The maintainer's unit has not shown the bug. It runs at full speed
behind a hub and its kernel logs the XLR Dock's `bad transfer trb
length` warning, yet with playback running on its sink before capture
opened it recorded every time, in three trials. As on the dock, the port
and the controller seem to decide whether the bug shows.

The daemon opens capture first on every interface it drives. It links
the interface's capture into XLR 1 before it routes any mix into the interface's own sink: at startup,
including when `monitorOutput` in `daemon.json` or
`OPENXLR_MONITOR_OUTPUT` names the interface, when you pick the
interface as an output, and when the sweep rebuilds routes after a
replug or a profile switch. A unit that XLR 1 or XLR 2 is bound to gets
the same order: a route into its sink waits for the strip's capture. When
the interface's sink is there but its capture does not come within five
seconds, the route is made anyway and the journal says so. The capture-hold rules are still needed, since
applications that play straight to the interface open playback without
asking the daemon.

## Wave:1 (0fd9:006e)

The Wave:3's smaller sibling, a USB condenser microphone with a
headphone jack. Nobody on the project owns one and none of the three
sources above has probed one, so `Wave1Device` is the Wave:3 backend
under the Wave:1's product id and name. A published review of the
microphone (TechPowerUp's Wave:1 review) describes the settings, not
the wire: one dial that sets the headphone volume by default or the
gain when Wave Link is told so, the dial pressed to mute, and in Wave
Link the gain, headphone volume, mic/PC mix, ClipGuard, low cut and
gain lock. Those are the Wave:3's controls, and the two microphones
shipped on one software generation. The backend refuses a config block
of another length before it writes anything. Its input is the capsule,
so its strip is Mic 1, as on the Wave:3.

An owner's diagnostics archive, taken on 0.1.60 before any control was
touched, settled the reading side. The unit answered both blocks:
firmware 1.2.2 and a 16-byte config block holding gain 11 dB at 0,
mute off at 4, ClipGuard on at 5, headphone level 0 dB at 7, headphone
mute off at 9, mix 0 at 10 and the dial on headphones at 12, the
Wave:1's default according to the review. The kernel's card held Mic
Capture Volume 22 for those 11 dB, capture on and PCM Playback Volume
120, so the daemon had written the desktop volume mirror and it agreed
with the block. Bytes 2 and 3 read 0x00EC, as they do on both Wave:3
units checked since. The backend retransmits them as read.

The same owner's second archive, taken after every control in the UI
had been tried, covered the writes. The gain word held 5 dB where the
first archive had 11, with the dial still on headphones, so only
OpenXLR can have written it, and it was still there after the daemon
had restarted in between: the firmware takes a host write to the gain,
as firmware 1.2.4 on a Wave:3 does, where wave3-research says a Wave:3
ignores one. The owner saw the
microphone's LED turn red on Mute and the white LED follow the
headphone slider. ClipGuard and the mix were back at their defaults
when the archive was collected, so it shows nothing about them.

That archive also showed the desktop volume mirror lagging a gain
written from the UI: the card still held the first archive's 22 after
the block had moved to 5 dB. Since then the backend takes a gain word
it wrote and reads back with the dial elsewhere as the gain, reports
it and gives it to the card; a firmware that drops the write reads
back the dial's own word and nothing changes.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 40 dB | verified on hardware | the Wave:3's word; an owner's block read 11 dB where the kernel's capture control read 22, two steps per dB as on the Wave:3, and a 5 dB set from the UI with the dial on headphones was in the block afterwards and survived a daemon restart |
| Mute | verified on hardware | byte 4; set from the UI, the owner saw the microphone's LED turn red. On the Wave:1 the dial is pressed to mute, and the press showing in the block is not confirmed yet |
| ClipGuard | read on hardware | byte 5, read as on; the setter awaits an archive collected with it changed |
| Headphone volume -60 to 0 dB | verified on hardware | the Wave:3's word, read as 0 dB with the kernel's playback control at 120; moved from the UI, the owner saw the white LED follow the slider |
| Direct monitor balance, as the crossfade | read on hardware | the Wave:3's word, read as 0, which on a Wave:3 is the computer alone; the review calls it the mic/PC mix; the setter and the direction on a Wave:1 await an archive collected with it changed |
| Headphone mute | read on hardware | byte 9, as on the Wave:3, read as unmuted on an owner's unit |
| Dial target | read on hardware | byte 12 read 2 (headphones) on an owner's unit, the review's default; the review gives the dial two targets, so 3 (monitor mix) should never appear. The target is set in Wave Link, not on the microphone, and OpenXLR does not write byte 12, so the gain word has not been read with the dial on gain on a Wave:1 |
| Low cut, gain lock | software | the submixer's and the daemon's, as on the Wave:3 |
| Desktop volume controls, firmware version | verified on hardware | the Wave:3's mirror and device info offsets; an owner's dump showed firmware 1.2.2 and the card's three controls in step with the block |

The daemon prefers the Wave:1 after everything else, the Wave:3 included:
it is driven when alone on the bus, picked from the header, or through
`OPENXLR_DEVICE=006e`. The node-name fragment is `Wave_1`.

What is left for an owner, with the steps under "Help confirm a
control" below: change ClipGuard and the mic/PC mix from the UI, leave
them changed, collect diagnostics, and say whether ClipGuard caught a
loud input and the mix changed what the headphones carry. The dump's
`config` line shows which bytes moved and `alsa` whether the card
followed. The gain word read with the dial on gain needs Wave Link to
switch the dial first, so it stays open until an owner who has done
that reports.

<a name="other-microphones"></a>
## Other microphones and interfaces

OpenXLR does not need an Elgato device. Any source PipeWire lists for
capture works: a USB microphone, a headset, a webcam, another brand's
interface. It enters the mixer in one of two ways.

Bound to XLR 1 or XLR 2 ([manual](manual.md#input-bindings)), one mono
port of the source feeds both sides of the strip, which is then named
Mic 1 or Mic 2. The strip keeps its sends into every mix, its volume and
meter, the microphone EQ, its LV2, CLAP and VST3 insert chain and Sound
Check. XLR 1 also gets the software low cut and ClipGuard; ClipGuard needs
`swh-plugins`. The EQ and Sound Check run in the native plugin host, which
the packages include. Aux In cannot be bound.

As a capture input (Edit layout, **Add capture input**), the source gets a
channel of its own with sends, a volume, meters and its place in profiles,
fed from a stereo pair, or from one mono port through the API. A capture
input hosts an insert chain of its own, shown in the UI's INPUTS card,
the racks and the matrix, and a mono capture input has Sound Check. It has
no EQ of its own, and the inserts on the mixes it feeds process it too.

Everything after the inputs works the same with or without Elgato
hardware: application channels and routing, virtual microphones, monitor
mixes on any PipeWire output, mix inserts, the Flow window, desktop keys,
the terminal mixer, and the OpenDeck plugin's mixer, insert and output
actions.

OpenXLR sends nothing to another brand's device. Its preamp gain, phantom
power, mute button, headphone level and onboard effects stay on the device
or with its own software. These need an Elgato device: every control in
the device sections above, the gain lock, recall on connect, last settings
restored on connect, the Wave XLR Pro's output routing, USB Aux mix,
zero-latency monitor path and Aux In, and the OpenDeck plugin's hardware
actions.

With no Elgato device attached, the daemon runs and builds the mixer as
usual. XLR 1 is shown and stays silent until it is bound: the header reads
"No interface." with a link to choose a microphone for it. XLR 2 and Aux In
are hidden while no supported device has been attached since the daemon
started; XLR 2 can still be bound from Options, AUDIO, Input bindings, and
shows once it is. Profiles are saved under `generic` and hold the mixer
alone.

The routing, hotplug, restart and Sound Check cases for a bound source, and
a desk with no Elgato device, are covered by tests against a private
PipeWire server with simulated sources. The table below records which of
them have been checked with real hardware.

## Input bindings

XLR 1 and XLR 2 can take another capture source while the interface stays
driven ([manual](manual.md#input-bindings)). The private PipeWire suite
covers the routing, hotplug, restart and Sound Check cases with simulated
nodes. On real units, so far:

| Check | State | Notes |
|---|---|---|
| A Wave:3's controls while it is driven as an additional interface beside a Wave XLR Pro | verified on hardware | the controls a strip bound to that Wave:3 shows. Gain, mute, ClipGuard, headphone level and the crossfade, written through `setWaveControl` and read back in the block; writes to the Pro leave the Wave:3 alone and the other way round. Not yet checked on a bound strip |
| A Wave:3 recording with its sink already playing | verified on hardware | on the maintainer's port, playback first and then capture still recorded, so a strip bound to this unit does not depend on the capture-hold rule there. An owner's unit on another port needed the rule |
| XLR 1 bound to another microphone with a Wave XLR Pro primary: audio on XLR 1, XLR 2 and Aux In, the keep-the-microphone capture channel | not yet checked | |
| The Pro's gain, mute, phantom, low cut, ClipGuard, voice tune and dial on the capture channel that carries its input, headphone and output controls unchanged | not yet checked | |
| The Pro's zero-latency monitor path following the carrying channel, off with a second monitor output | not yet checked | |
| Software low cut, ClipGuard, inserts and Sound Check on a bound XLR 1 | not yet checked | |
| The crossfade's line naming where the interface's own microphone plays | not yet checked | |
| XLR 2 on another source while XLR 1 is bound; both strips on one port | not yet checked | |
| Unplugging and replugging the bound source, and the primary interface while a strip is bound | not yet checked | |
| Bindings and the capture channel after a daemon restart; no interface attached, binding from the header and `generic` profiles | not yet checked | |
| A Wave:3 primary with XLR 1 on another microphone: its controls in the UI, the crossfade line | not yet checked | |
| An XLR Dock primary with XLR 1 bound elsewhere: dock controls, live capture on return to the jack | not yet checked | |
| A Bluetooth headset bound to XLR 1: the headset profile switch and back | not yet checked | |
| Stream Deck keys and the Omarchy bar naming the channel that carries the interface's microphone | not yet checked | |
| A capture input's own insert chain and Sound Check on a mono capture input, in the UI and the terminal mixer | not yet checked | |
| The unit controls following a capture input bound to an additional interface's input | not yet checked | |
| Changing a capture input's source in place: the channel keeps its id, sends and inserts, the old source is released | not yet checked | |

## Every device gets

- Capability-driven UI: controls, channels, and mixes the device does
  not have are not shown
- Per-device profiles: named scenes of hardware state plus the whole
  submix, recalled from the UI, the API or a Stream Deck key, and one
  of them on connect if chosen
- Last settings restored on connect for Wave XLR and the first XLR Dock,
  plus a reset to the defaults recorded after a power cycle. The Pro and the MK.2 family, the XLR
  Dock MK.2 included, keep their settings on board (verified by
  replugging the dock); the Wave:3 and Wave:1 are assumed to until a
  replug with nothing written on connect decides it (checklist step 8)
- Multi-device switching: a header picker chooses which interface
  OpenXLR drives; the mixer's input channels follow it
- More than one unit at once, two of one model included: up to four
  additional interfaces next to the primary. Run on hardware with a
  Wave XLR Pro primary beside an XLR Dock and beside a Wave:3; two of one
  model only with simulated units so far;
  [wave-interfaces.md](wave-interfaces.md#what-still-needs-hardware)
  lists what an owner of two units checks
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

A Wave:3 owner confirms the controls still open with the following, in
the order that risks least, each result with the diagnostics archive:

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
3. Mute from the UI and from the capacitive pad: the state and the
   LED ring agree both ways.
4. ClipGuard from the UI: the setting survives a readback, and a
   loud signal is caught by the hardware. If the byte does nothing, say
   so; the software limiter can take over.
5. Headphone volume from the UI: the level in the headphones
   follows, -60 dB is near silence, 0 dB is the loudest. Then turn the
   dial in headphone mode down to its floor and take a dump: `hpMute`
   in the state turns on if the firmware asserts it there. Raise the
   volume from the UI: sound is back and `hpMute` is off.
6. Gain from the UI with the dial on gain: the microphone's own
   gain display and the recorded level follow. Firmware 1.2.4 took the
   write on the maintainer's unit; say if yours snaps back, which is what
   wave3-research saw on an older firmware.
7. The crossfade from the UI, settled by ear on the maintainer's
   unit: with a playback stream running and the microphone live, the left
   end leaves only the microphone in the headphones and the right end
   only the computer. Say if yours differs.
8. Clear the "On connect" profile picker under the profile list, so
   nothing is written on connect, then unplug and replug: the settings
   from before are still there, or the microphone came back at
   defaults, which decides `retainsSettings`.
9. Reboot with the capture-hold rule installed, play through the
   microphone's own sink before anything records from it, then record:
   sound every time. One owner confirmed this over several reboots; say
   whether your unit records silence without the rule, and which USB port
   and controller it is on.
10. Change the microphone's output volume and mute its input in the
    desktop's sound settings: within a second the headphones and the
    mute ring follow, and so does the UI. Then turn the dial in
    headphone mode: the desktop's slider follows.
11. Options, under INTERFACE, shows a firmware version. Say whether it
    matches the one Wave Link shows for the microphone.

An owner of two supported units confirms driving them together with the
following, each result with the diagnostics archive:

1. Plug both in. The header picker lists both, two of one model with
   their USB ports. Note each unit's `id` in the state's
   `waveInterfaces`, unplug one, plug it into another port: a unit with
   a serial keeps its id.
2. Pick each in turn as the primary: the first input strip (XLR 1, or
   Mic 1 on a Wave:3 or Wave:1) carries that unit's
   microphone, never the other's, and the daemon log names no fallback
   to the model name. If it does, include the `pactl list short sources`
   output; it shows how udev spelled the serial.
3. Enable the other unit under Options, INTERFACE, Additional
   interfaces. Its gain, mute and phantom power move that unit only, and
   the primary's controls move the primary only.
4. Add its microphone with Add input channel in the Additional
   interfaces window: the new channel carries that unit's microphone. On a Pro, microphone 2 carries the second
   input.
5. With two XLR Docks, collect diagnostics with each dock as the
   primary in turn: the `paths` line names a different ALSA card each
   time, and gain, mute and headphone volume on one dock leave the
   other's alone.
6. With a Wave XLR Pro among them, its card is in the pro-audio profile
   whether it is primary or additional, the other card's profile is
   unchanged, and Headphones 1, Headphones 2 and Line Out switch that
   Pro's outputs.
