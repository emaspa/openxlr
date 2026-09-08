# Wave XLR Pro vendor control protocol

How the Wave XLR Pro's vendor protocol was captured and decoded, in the order it happened;
later sections correct earlier ones where noted. This is a chronological
notebook: provisional labels are retained as evidence, not API promises.
For supported controls, start with [hardware-support.md](hardware-support.md).

Source: `wavexlrpro.pcapng` (7.4 GB, 4.6M packets, 649 s, Wave Link on Windows, 2026-08-25).
Decoded on Linux with tshark. No companion action-log was captured, so **block/offset → named
control mapping is partially inferred** (see §4); the transport, framing, and value encodings
below are directly observed and solid.

## Implementation

The current implementation of this protocol is
`src/OpenXLR.Core/Devices/WaveXlrProDevice.cs` (libusb control transfers,
read-modify-write of the blocks below, commit block after selector
writes): both XLR structures, both headphone volumes, the USB Aux input
stage, the physical output selectors and the commit sequence. The mic map
as finally verified (section 6): flags bit 1 = phantom, bit 4 = low cut,
bit 5 = expander, bit 6 = voice tune, bit 7 = compressor; ClipGuard is the
inverted `0x04` value at structure offset 2. There is no polarity control.
Sections 3 to 5 keep the first capture's provisional labels as the record
of how the map was found; where they disagree with section 6, section 6
is right. The daemon control names are listed in [api.md](api.md). An
earlier prototype written into a fork of openwave was used to validate the
transport and is not maintained.

## 0. HARDWARE-CONFIRMED (2026-08-25, live on the device)

Everything below was validated live against the plugged-in Pro with a ctypes-libusb probe
(`tools/proprobe.py`) after installing a udev rule (MODE 0660 + uaccess for 0fd9:00b4):

- **Transport works exactly as decoded**: reads `0xC1/1/wValue/0x0103` and writes `0x41/1/…`
  succeed on all blocks. Block 0x0003 STALLs on read = write-only (matches capture).
- **The Pro is essentially a Wave XLR MK.2 variant.** The fork's existing `WaveXLRMk2` backend
  (device.py) uses the SAME `bRequest=1`, block numbers, and offsets, only `wIndex` differs
  (Pro **0x0103** vs MK.2 0x0203; the XLR Dock MK.2 has the MK.2 layout but answers at the
  Pro's 0x0103). MK.2's constants therefore label most Pro fields directly.
- **Mic gain = block 0x0004 offset 0, value = dB.** Proven bidirectionally: ALSA `numid=3` and
  vendor off0 track 1:1 (40↔0x28, 65↔0x41, 52↔0x34), reading AND writing either side.
- **Mic mute = block 0x0004 offset 1, bit 0.** Proven: vendor bit0=1 ⇒ ALSA capture switch off
  (muted); bit0=0 ⇒ on. Read-modify-write of the block is confirmed non-destructive to audio.
- Current device state read live: gain 0x34 (52 dB, matches the Windows config), flags off1=0xd8
  (bits 3,4,6,7 set), HP block `00 00 00 00 50 00 00 00`.

Confidence upgraded below from this: gain and mute are now **CONFIRMED**; low-cut/expander/
voice-tune/HP/low-Z/crossfade inherit **HIGH** from the MK.2 field identity; only the Pro-only
extra bits (phantom / ClipGuard / polarity) remain to be labeled by ear, see §4.

## 1. Transport (the Pro dialect of the MK.2-style block/property bank)

All device control rides on **vendor control transfers to interface 3** (the vendor-specific
interface, class 0xFF, that has no kernel driver, so no audio-driver detach needed):

| field          | write            | read             |
|----------------|------------------|------------------|
| bmRequestType  | **0x41** (host→dev, vendor, interface) | **0xc1** (dev→host, vendor, interface) |
| bRequest       | **1**            | **1**            |
| wValue         | **block number** (0x0001…0x0008) | same |
| wIndex         | **0x0103** (=259; low byte 0x03 = interface 3, high byte 0x01 = unit/entity 1) | same |
| wLength        | fixed per block  | fixed per block  |

So it is NOT the MK.1 `wIndex=0x3303` trick and NOT the MK.2 `wIndex=0x0203` standard-class
address. The MK.2 also uses vendor requests; "standard-class" above was
an early classification error. It is a **paged property bank**: `wValue` selects a fixed-size block; the whole block is
read or written as one unit; individual controls are byte fields at fixed offsets inside a block.

## 2. The blocks (wValue) observed

| block | size | dir seen | reads (17 Hz poll) | writes | role |
|-------|------|----------|--------|--------|------|
| 0x0001 | 108 B | R/W | 9832 | 146 | main config/state (toggles + a large tail array) |
| 0x0002 | 150 B | R only | 9872 | 0 | **telemetry / meters** (polled, never written) |
| 0x0003 | 12 B  | W     | 0    | 57  | write-only commit block (constant `04 00…` in this capture; role established in section 6) |
| 0x0004 | 80 B  | R/W   | 9828 | 168 | config: a 0–100 fader + a packed-flags byte + an enum |
| 0x0005 | 8 B   | R/W   | 9790 | 131 | **two independent one-byte level faders** |
| 0x0006 | 29 B  | R only| 9837 | 0   | status/telemetry |
| 0x0008 | 96 B  | R only| 9805 | 0   | status/telemetry |

Wave Link **polls every read block at ~17 Hz continuously** (≈9800 reads each over 577 s). A
Linux backend does not need that rate to *set* controls, but the firmware may expect periodic
reads to stay live (matches upstream OpenWave's keepalive concern), to be confirmed on hardware.

## 3. Decoded value encodings (directly observed)

### Block 0x0005 (8 B) two level faders. Framing `[L0] 00 [L2] 00  50 00 00 00`
- **offset 0**: one-byte level, swept full range **0x00–0xf0** (0–240), t=361–379 s.
- **offset 2**: one-byte level, swept full range **0x03–0xf0**, t=388–405 s.
- offset 4 constant **0x50** (80), a third level parked at default (candidate: a fixed/again level).
- These two are the analog level controls dragged end-to-end during capture, i.e. **headphone
  volume and monitor/direct-monitor blend** (which is which not yet distinguished; see §4).

### Block 0x0004 (80 B) mixed config
- **offset 10**: one-byte fader swept **0x00–0x64 (0–100, a percentage)**, t=506–517 s. A distinct
  level control from the 0x0005 pair, on a 0–100 scale (candidate: mic-output or a mix %).
- **offset 0**: **MIC GAIN in dB** (0x00–0x50 = 0–80 dB); gradual, dial-driven. See §4.
- **offset 1**: a **packed-flags byte**, individual bits flip independently across t=197–533 s
  (values ba/bb/b8/a9/f8/d8/58/d0). Several booleans live here (phantom / low-cut / ClipGuard /
  mute / impedance / polarity, bit-level decode in §4).
- **offset 2**: a small **enum**, values 0x00 / 0x01 / 0x05 (3 states), candidate low-cut *type*
  or a mode selector.

### Block 0x0001 (108 B) main config plus a tail array
- Low-cardinality byte fields (candidate toggles/small enums) at offsets **12, 13, 24, 30, 39,
  48, 90, 91**. Flip events clustered at t≈76 s (initial multi-field apply), 386 s, 445 s, and
  606–640 s.
- offset 98… onward: a long monotonic-ish array (a curve/table or a meter mirror), not a control.

## 4. Control map (aligned to the captured action order, which followed the plan ~99%)

The user ran the capture following `wave-xlr-pro-capture-plan.md` Session 2 in order (plus some
unremembered extra mic tweaks). Aligning the event timeline to that step order gives the mapping
below. Confidence is marked; **HIGH** = encoding + order both unambiguous, **MED** = order fits
but the exact field/bit split needs one confirming toggle, **LOW** = region known, label inferred.

### Mic gain in block 0x0004 offset 0, value = gain in dB (HIGH)
Gain moves gradually only (both the Wave Link UI and the Stream Deck dials drive it as a dial, per
the user), which is exactly the signature of block 0x0004 **offset 0**: it climbs 0x01→0x3c during
2:41–3:08, later falls to 0x00, ends 0x33. `0x3c = 60` maps 1:1 to the plan's "gain → 60 dB", so
**the byte is gain in dB** (range 0x00–0x50 = 0–80, matching the ALSA control's 0–80 dB span).
Proof it is a control and not a write-counter: when off1/off2/off10 changed, off0 stayed stable in
65 of 66 writes (a counter would increment every write); off0 moved alone in 100 writes.
Convenience: the SAME gain is also the standard **UAC2 ALSA capture-volume control** (`amixer -c
Pro`, currently 52 dB), on Linux, set gain the easy way through ALSA/wpctl; the vendor block is
an alternative, not required, for gain.

### Block 0x0004 offset 1, packed byte of 8 booleans (bit-level decoded)
Baseline `0xba = 1011_1010`. Flips observed, in order:
- **bit 0**, toggled on/off/on/off at 3:17–3:24 → **MIC MUTE** (the "do it twice" step). HIGH.
- **bit 1**, single flip at 3:50 → a mic toggle, **phantom power or low-cut** (first single mic
  toggle after mute). MED.
- **bit 4**, flips at 4:01 & 4:11 (paired with bit 0) → **ClipGuard or the other of phantom/
  low-cut**. MED.
- **bit 6**, flip at 8:24 (start of the output/monitor cluster) → **headphone impedance**. MED.
- **bit 5**, flip at 8:42 → **polarity or mic-output mute**. LOW.
- **bit 7**, on/off at 8:48–8:49; **bit 3**, on/off at 8:50–8:52 → the remaining output toggles
  (mic-output mute / polarity). LOW.

Early bits (0,1,4) are the **mic** toggles (plan steps 5–13); late bits (3,5,6,7) are the
**output/monitor** toggles (plan steps 18/21/22). The mic-vs-output split is solid; which bit is
exactly which within each group is the only soft part.

### Block 0x0004 offset 2, 3-state enum, values 00 / 01 / 05 → **LOW-CUT TYPE / mode** (MED).
Cycled 01→05→01 at 4:11–4:18 (during the mic-toggle window), consistent with low-cut type.

### Block 0x0005, the two swept faders (framing `[o0] 00 [o2] 00  50 00 00 00`)
- **offset 0**: swept full-range first, 6:00–6:19 → **HEADPHONE VOLUME** (plan steps 15–17, done
  before monitor blend). MED-HIGH.
- **offset 2**: swept full-range second, 6:27–6:45 → **MONITOR / DIRECT-MONITOR BLEND** (plan step
  19). MED-HIGH.
- offset 4 constant **0x50**, a third level parked at default (candidate: a second monitor/mic
  level). Range for both faders 0x00–0xf0.

### Block 0x0004 offset 10, one-byte fader, range **0x00–0x64 (0–100 %)**, swept 8:25–8:36 →
**MIC OUTPUT VOLUME** (plan step 20, in the output cluster). MED.

### Block 0x0001 (108 B), mic channel config + the unremembered extras
Multi-field writes at 1:16 (session start) and 10:06–10:40 (session end), touching offsets
12/13/24/30/39/48 and single-sets at 90 and 91 (6:26, 7:25). This block holds the remaining mic
DSP state and is where the "extra mic stuff" landed. Individual labels here are **LOW** confidence
without a targeted pass, but nothing in daily use is blocked: the day-to-day controls (mute,
phantom, low-cut, ClipGuard, headphone, monitor, mic-output) are covered above.

### Post-live-probe status (2026-08-25)
- **CONFIRMED on hardware**: gain (0x0004 off0), mute (0x0004 off1 bit0).
- **HIGH (via MK.2 field identity)**: low-cut = off1 **bit4** (also corroborated, device reads
  bit4=1 and the Windows config has LowCut ON); expander = bit5; voice-tune = bit6; voice-tune
  strength = off10; HP volume = 0x0005 off0 (dB = −byte0/4); low-impedance = 0x0005 off1 bit1;
  crossfade = 0x0001 off0. (Note: the MK.2 map reassigns what §3 tentatively called "monitor
  blend"/"mic-output", 0x0005 off2 and 0x0004 off10, so treat those two as MK.2 says: HP is
  0x0005 off0, and off10 is voice-tune strength. The 0x0005 off2 second level is Pro-specific,
  still unlabeled.)
- **NEEDS EARS (Pro-only extras)**: phantom power, ClipGuard, polarity live in off1 bits **1, 3,
  7** (currently 0,1,1). Cross-ref: Windows config has Phantom OFF, ClipGuard ON, Polarity OFF, so a currently-set bit (3 or 7) is ClipGuard, and bit1 (=0) is likely phantom. Not
  auto-verifiable (no ALSA control, no LED on the Pro). Finalize by toggling each and listening,
  or by recording the mic and measuring the spectral change (low-cut/expander are audible).

The backend can be built now regardless: read-modify-write on these offsets is proven, and the
three ear-dependent bits can be named the moment you toggle them and listen.

## 5. Linux implementation implications

- The `0x00b4` backend is a **paged-register driver**: read a fixed-size
  block (`0xc1/1/wValue/0x0103`), modify one field, write the block back
  (`0x41/1/wValue/0x0103`), and commit where required. Its field layout is
  MK.2-style, but the Pro has its own index, lengths, second-XLR structure,
  tail and output matrix, so it remains a dedicated backend.
- Controls use read-modify-write so unrelated bytes survive every change.
  The current backend reads fresh state for each setter instead of trusting
  a cache that another client could invalidate.
- Mic gain is also visible through standard ALSA controls, but OpenXLR uses
  the same vendor block as the other per-XLR fields to keep both input
  structures and client state consistent.
- Telemetry blocks (0x0002/0x0006/0x0008) are readable for meters or clip
  indicators; they are not required for control.

## Hardware correction (2026-08-25, user-supplied): TWO XLR inputs, TWO headphone outputs

The Pro has XLR 1 and XLR 2 inputs and two headphone outputs, which resolves the block
structures cleanly (all verified bidirectionally against ALSA on hardware):

- **Block 0x0004 (80 B) = two 38-byte MK.2-style settings structures + 4-byte tail.**
  XLR 1 at offset 0, XLR 2 at offset 38, identical field layout (gain byte, flags byte,
  voice-tune strength at +10). Verified: off38 tracks the ALSA rear channel pair exactly
  (vendor write 40 -> ALSA rear reads 40). The tail (off76 = 0x14 = 20) matches the ALSA
  front-center/woofer pair, a third input stage, not yet exposed.
- **ALSA channel map of the 6-channel capture control: front pair = XLR 1 gain, rear
  pair = XLR 2 gain, center/woofer pair = third stage.** `amixer cset numid=3 <v>` sets
  ALL of them at once, which is why they can appear synced.
- **Block 0x0005: off0 = Headphones 1 volume, off2 = Headphones 2 volume** (both dB =
  -byte/4). This retires the earlier provisional "monitor blend" label for off2; the
  monitor/crossfade blend remains block 0x0001 off0 per the MK.2 field identity. off4
  (0x50) is still unlabeled.

Implemented in OpenXLR end to end (device layer, daemon controls gain2/mute2/lowCut2/
expander2/voiceTune2/voiceTuneStrength2/phantom2/clipGuard2/compressor2/hp2VolumeDb, UI
strips for XLR 1 / XLR 2 and Phones 1 / Phones 2), with gain2 and hp2 verified live.

## 6. Capture 2 (xlrpro2.pcapng, 2026-08-25, WITH ordered action log): outputs, mic DSP, USB Aux

A second targeted capture (6.4 GB, with an ordered log of 26 actions and a Wave Link Audio
Flow screenshot showing the Pro's full topology:
inputs XLR Mic 1, XLR Mic 2, Line In, USB Aux; monitor destinations Headphone 1, Headphone 2,
USB Aux out, Line out, or any system device). Everything below is capture-decoded AND
hardware-verified on Linux the same day; the headphone findings are confirmed by ear on BOTH
jacks.

### Physical output routing (block 0x0001 off90..93 + commit)

One selector byte per physical output: off90 = Headphone 1, off91 = Headphone 2, off92 =
USB Aux out, off93 = Line out. Value 0x1e = output carries the hardware monitor bus, 0x23 =
off. (0x20 also observed on the USB Aux out: a third, unlabeled source state.) A block 0x0003
commit write (payload `04 00 x11`) MUST follow, or nothing changes; this explains block 3,
which Wave Link writes after every config change. The Linux jacks were silent because both
selectors sat at 0x23 (the state left by the last Windows session, monitor on the
Katana). Live-verified: toggling off91 mid-tone gates jack 2, off90 gates jack 1.

### The monitor bus and which USB playback channels feed it

With a jack enabled, a 17-channel pair sweep found the monitor bus is fed by USB playback
channel pairs 2/3, 10/11, 12/13, and 14/15 (0/1, 4/5, 6/7, 8/9, 16 are silent). OpenXLR routes
the monitor mix into channels 2/3 (`#phones` pseudo-device in the Monitor picker; direct port
links to playback_AUX2/AUX3). End-to-end chain (app -> System channel -> monitor mix -> monitor
bus -> Headphones 1) confirmed by ear.

### Mic DSP corrections (block 0x0004, per-XLR struct; all now CONFIRMED, no more provisional)

- phantom = flags(off1) bit1  (provisional guess was right)
- compressor = flags bit7  (previously mislabeled polarity; that's why it read wrong)
- ClipGuard is NOT in the flags byte: struct offset 2, value 0x04 = DISABLED (inverted).
  Confirmed identically on XLR2 (off38+2). Off2 bit0 (0x01/0x05 values in capture 1) remains
  unlabeled.
- low cut bit4, expander bit5, voice tune bit6, voicetune strength off10 (0..0x64 = %):
  reconfirmed by the ordered log.
- flags bits 2 and 3 remain unlabeled (bit3 reads set on this unit).

### USB Aux input stage (block 0x0004 tail)

- off79 = input level, quarter-dB attenuation: dB = -byte/4, range 0..240 = 0..-60 dB (same
  encoding as the phones volumes).
- off77 = level lock, 0x04 = locked.
- off76 = a further gain-stage byte (memory: third input stage), still unexposed.

### Implemented in OpenXLR (same day, all live-verified)

`WaveXlrProDevice`: output selectors (SetOutHp1/2/UsbAux/LineOut) with `WriteCommitted` (config
write + block 3 commit), corrected ClipGuard/compressor, aux level + lock. Daemon controls:
outHp1/outHp2/outUsbAux/outLineOut/auxLevelDb/auxLevelLock/compressor (polarity removed). UI:
MONITOR ROUTING toggles in the HEADPHONES card, USB Aux level+lock row, Compressor button,
asterisks gone. Mixer: `<proSink>#phones` pseudo-sink "Wave XLR Pro Headphones" in the Monitor
picker, routed to channels 2/3.

## 7. Captures 3-4 (2026-08-26): the hardware mix matrix and the aux-out solution

Sources: `xlrpro3.pcapng` + ordered log + a screenshot of Wave Link's "Mixes (XLR Pro
Hardware Mixer)" view, `xlrpro4.pcapng` + ordered log with music confirmed flowing to a
second computer on the aux port. Everything below is verified live on Linux by ear and meter.

### Block 0x0001 is the hardware mix matrix

- The device runs HARDWARE mixes; Wave Link's Personal Mix and user-created mixes (here
  "MacBook Mix") are device-side. Chat Mix has no output assignment and is software-only.
- Output selector bytes off90..93 hold a MIX ID per physical output: 0x1e = Personal mix,
  0x20 = the aux/user mix, 0x23 = unassigned. (Not "modes" as earlier assumed.)
- The matrix is stored in aa5555-delimited per-mix sections full of quarter-dB attenuation
  bytes (0x00 = 0 dB, 0xc4 = -49 dB, 0xf4 = -61 dB) plus membership bit-bytes:
  - Personal section: line-in level off21, membership off30 (bit3 = line-in). CORRECTION
    (2026-09-04, issue #8, by ear on hardware): none of off16..33 nor the head cells off3..10
    change what the headphone jacks hear. The jacks' mix membership is in the HEAD: off12
    bit5 (0x20) = USB return pair 2/3 (the Monitor stream) summed into the jacks, off13 bit1
    (0x02) = the mic's direct zero-latency path. A unit Wave Link left with off12 bit5 clear
    (reporter's: off12 = 0x90 vs 0x24 here) hears no Monitor mix on Headphones 1 at all; the
    daemon now asserts bit5 whenever a jack is the monitor output and drives bit1 from the
    XLR 1 -> Monitor send's mute. Both need the commit and a playback-stream restart to latch.
  - Aux-mix section: music-return level off45, line-in membership off48 bit3 (bit0 = mic,
    set on this unit), music membership off49 bit1.
- USB playback pairs are Wave Link channel returns; the Music channel = pair 10/11 (proven
  from capture 4's iso OUT energy while the Mac was receiving). Pairs 2/3, 12/13, 14/15 are
  other channel returns summed into the Personal mix (jack listening test).

### The aux-out recipe (implemented in OpenXLR, verified end to end)

1. off92 = 0x20 (aux out -> aux mix) + commit.
2. Open the music cell: off45 = 0x00, off49 |= 0x02 + commit.
3. Stream the monitor mix on playback pair 10/11 (`#usbaux` pseudo-device routes there;
   analog outputs keep pair 2/3 via the Personal mix).
4. THE LATCH: the device samples the aux-return routing when the HOST PLAYBACK STREAM
   STARTS. Changing the cell under a running stream has no effect until the stream restarts;
   `pactl suspend-sink 1/0` (PipeWireAdapter.BounceSink) is sufficient and the daemon does it
   automatically when aux out is newly enabled.
5. Confirmed stable for 30s+ with the full daemon (capture stream, 10 Hz poll, and mixer all
   running); simultaneous Headphones 1 + USB Aux Out both carry the monitor mix.

### Testing note

Untagged test streams played at the Pro sink get moved to a channel sink by OpenXLR's own
stream matcher within ~1 s, silently invalidating hardware tests ("worked for a few seconds").
Every apparent "the daemon breaks it" observation was this. Test streams must set
`PULSE_PROP="node.name=OpenXLR_test"` (the matcher excludes OpenXLR-named streams) or play
via a channel sink. The earlier "capture stream conflicts with aux forwarding" conclusion was
an artifact of this and is WITHDRAWN.

## 8. Phantom anti-thump hold (2026-08-30, measured live)

The firmware mutes an XLR input around EVERY 48V transition, on and off
alike, and unmutes it by itself. Measured on XLR 2: mute engages with the
phantom write and releases after ~13 s; host unmute writes during the
window are ignored. This is thump protection while the 48V rail ramps or
discharges, and it is per input (the same flags byte, bit 0, set by the
firmware itself).

The firmware sometimes SKIPS the mute entirely (observed on a phantom
toggle shortly after a previous cycle, i.e. a rail still charged has no
thump to protect against), so the hold must not be a fixed timer.

Implication for clients: a mute that appears with a phantom toggle is not
stuck and must not be fought. The daemon stamps `phantomSettling` /
`phantomSettling2` plus a `phantomSettleSeconds` countdown on the state
after each phantom write; the hold ends the moment the firmware's own
unmute shows up in the readback (a 15 s window is only the cap, with a
2 s grace before the mute is first observed). The UI disables the
matching mute button and counts the hold down on it.
