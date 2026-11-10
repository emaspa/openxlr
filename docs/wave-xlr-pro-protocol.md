# Wave XLR Pro vendor control protocol

How the Wave XLR Pro's vendor protocol was captured and decoded, in the order it happened;
later sections correct earlier ones where noted. This is a chronological
notebook: provisional labels are retained as evidence, not API promises.
For supported controls, start with [hardware-support.md](hardware-support.md).

Source: `wavexlrpro.pcapng` (7.4 GB, 4.6M packets, 649 s, Wave Link on Windows, 2026-08-25).
Decoded on Linux with tshark. No companion action-log was captured, so **block/offset to named
control mapping is partially inferred** (see §4); the transport, framing, and value encodings
below are directly observed and solid.

## Implementation

The current implementation of this protocol is
`src/OpenXLR.Core/Devices/WaveXlrProDevice.cs` (libusb control transfers,
read-modify-write of the blocks below, commit block after selector
writes): both XLR structures, both headphone volumes, the USB Aux input
stage, the Line In gain (section 13), the physical output selectors and the
commit sequence, and each input's microphone EQ (section 10). The mic map
as finally verified (section 6): flags bit 1 = phantom, bit 4 = low cut,
bit 5 = expander, bit 6 = voice tune, bit 7 = compressor; ClipGuard is the
inverted `0x04` value at structure offset 2. There is no polarity control.
Sections 3 to 5 keep the first capture's provisional labels as the record
of how the map was found; where they disagree with section 6, section 6
is right. The daemon control names are listed in [api.md](api.md). An
earlier prototype written into a fork of openwave was used to validate the
transport and is not maintained.

The mix sections of block 0x0001 (sections 12 and 15 to 19) have their own
codec, `src/OpenXLR.Core/Devices/ProMixSections.cs`: the five sections'
levels, membership and ducking fields, the output selectors, byte 94 and
byte 98, the cell law with its floor and ceiling, and the small low-level
changes the unit drops. Diagnostics show the block decoded with it.

Block 0x0002, the unit's meters (section 21), has its own codec,
`src/OpenXLR.Core/Devices/ProMeters.cs`. Diagnostics show it decoded, and
the device loop reads it for the ducking reduction only while the unit
runs ducking and a client takes meters.

OpenXLR writes block 0x0001 in one place,
`src/OpenXLR.Core/Devices/ProMixPlan.cs`: the output selectors, the USB Aux
port's cells in section 36, the Monitor return and both microphones' paths
and levels in section 0, and the ducking fields are one plan, made as edits
on a fresh read of the block. The device manager posts the mixer's plan
each sweep together with any ducking target waiting to be written, so the
block is written once for all of them, block 0x0008 first when ducking
changed, and the commit follows only when a selector or a membership bit
changed. At steady state the plan matches the last poll and nothing is
read or written.

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
  vendor off0 track 1:1 (40 = 0x28, 65 = 0x41, 52 = 0x34), reading AND writing either side.
- **Mic mute = block 0x0004 offset 1, bit 0.** Proven: vendor bit0=1 means ALSA capture switch off
  (muted); bit0=0 means on. Read-modify-write of the block is confirmed non-destructive to audio.
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
| bmRequestType  | **0x41** (host to dev, vendor, interface) | **0xc1** (dev to host, vendor, interface) |
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
| 0x0002 | 150 B | R only | 9872 | 0 | **telemetry / meters** (polled, never written; layout in section 21) |
| 0x0003 | 12 B  | W     | 0    | 57  | write-only commit block (constant `04 00…` in this capture; role established in section 6) |
| 0x0004 | 80 B  | R/W   | 9828 | 168 | config: a 0–100 fader + a packed-flags byte + an enum |
| 0x0005 | 8 B   | R/W   | 9790 | 131 | **two independent one-byte level faders** |
| 0x0006 | 29 B  | R only| 9837 | 0   | status/telemetry |
| 0x0008 | 96 B  | R only| 9805 | 0   | ducking settings, written by Wave Link 3.5 (section 11) |

Wave Link **polls every read block at ~17 Hz continuously** (≈9800 reads each over 577 s). A
Linux backend does not need that rate to *set* controls, but the firmware may expect periodic
reads to stay live (matches upstream OpenWave's keepalive concern), to be confirmed on hardware.

## 3. Decoded value encodings (directly observed)

### Block 0x0005 (8 B) two level faders. Framing `[L0] 00 [L2] 00  50 00 00 00`
- **offset 0**: one-byte level, swept full range **0x00–0xf0** (0–240), t=361–379 s.
- **offset 2**: one-byte level, swept full range **0x03–0xf0**, t=388–405 s.
- offset 4 constant **0x50** (80), a third level parked at default. Section 19 measured it: Line Out's level, dB = -byte/4.
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
the user), which is exactly the signature of block 0x0004 **offset 0**: it climbs 0x01 to 0x3c during
2:41–3:08, later falls to 0x00, ends 0x33. `0x3c = 60` maps 1:1 to the plan's "gain to 60 dB", so
**the byte is gain in dB** (range 0x00–0x50 = 0–80, matching the ALSA control's 0–80 dB span).
Proof it is a control and not a write-counter: when off1/off2/off10 changed, off0 stayed stable in
65 of 66 writes (a counter would increment every write); off0 moved alone in 100 writes.
Convenience: the SAME gain is also the standard **UAC2 ALSA capture-volume control** (`amixer -c
Pro`, currently 52 dB), on Linux, set gain the easy way through ALSA/wpctl; the vendor block is
an alternative, not required, for gain.

### Block 0x0004 offset 1, packed byte of 8 booleans (bit-level decoded)
Baseline `0xba = 1011_1010`. Flips observed, in order:
- **bit 0**, toggled on/off/on/off at 3:17–3:24: **MIC MUTE** (the "do it twice" step). HIGH.
- **bit 1**, single flip at 3:50: a mic toggle, **phantom power or low-cut** (first single mic
  toggle after mute). MED.
- **bit 4**, flips at 4:01 & 4:11 (paired with bit 0): **ClipGuard or the other of phantom/
  low-cut**. MED.
- **bit 6**, flip at 8:24 (start of the output/monitor cluster): **headphone impedance**. MED.
- **bit 5**, flip at 8:42: **polarity or mic-output mute**. LOW.
- **bit 7**, on/off at 8:48–8:49; **bit 3**, on/off at 8:50–8:52: the remaining output toggles
  (mic-output mute / polarity). LOW.

Early bits (0,1,4) are the **mic** toggles (plan steps 5–13); late bits (3,5,6,7) are the
**output/monitor** toggles (plan steps 18/21/22). The mic-vs-output split is solid; which bit is
exactly which within each group is the only soft part.

### Block 0x0004 offset 2, 3-state enum, values 00 / 01 / 05: **LOW-CUT TYPE / mode** (MED).
Cycled 01, 05, 01 at 4:11–4:18 (during the mic-toggle window), consistent with low-cut type.

### Block 0x0005, the two swept faders (framing `[o0] 00 [o2] 00  50 00 00 00`)
- **offset 0**: swept full-range first, 6:00–6:19: **HEADPHONE VOLUME** (plan steps 15–17, done
  before monitor blend). MED-HIGH.
- **offset 2**: swept full-range second, 6:27–6:45: **MONITOR / DIRECT-MONITOR BLEND** (plan step
  19). MED-HIGH.
- offset 4 constant **0x50**, a third level parked at default; Line Out's level (section 19).
  Range for both faders 0x00–0xf0.

### Block 0x0004 offset 10, one-byte fader, range **0x00–0x64 (0–100 %)**, swept 8:25–8:36:
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
  crossfade = 0x0001 off0 (on the Pro, XLR 1's level in the jacks' mix; see section 12). (Note: the MK.2 map reassigns what §3 tentatively called "monitor
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
- Telemetry blocks (0x0002/0x0006) are readable for meters or clip
  indicators; they are not required for control.

## Hardware correction (2026-08-25, user-supplied): TWO XLR inputs, TWO headphone outputs

The Pro has XLR 1 and XLR 2 inputs and two headphone outputs, which resolves the block
structures cleanly (all verified bidirectionally against ALSA on hardware):

- **Block 0x0004 (80 B) = two 38-byte MK.2-style settings structures + 4-byte tail.**
  XLR 1 at offset 0, XLR 2 at offset 38, identical field layout (gain byte, flags byte,
  voice-tune strength at +10). Verified: off38 tracks the ALSA rear channel pair exactly
  (vendor write 40 -> ALSA rear reads 40). The tail (off76 = 0x14 = 20) matches the ALSA
  front-center/woofer pair, a third input stage, not yet exposed. (Section 13: off76 is the
  analog Line In's gain.)
- **ALSA channel map of the 6-channel capture control: front pair = XLR 1 gain, rear
  pair = XLR 2 gain, center/woofer pair = third stage.** `amixer cset numid=3 <v>` sets
  ALL of them at once, which is why they can appear synced.
- **Block 0x0005: off0 = Headphones 1 volume, off2 = Headphones 2 volume** (both dB =
  -byte/4). This retires the earlier provisional "monitor blend" label for off2; the
  monitor/crossfade blend remains block 0x0001 off0 per the MK.2 field identity. off4
  (0x50) is Line Out's level, dB = -byte/4 (section 19).

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
the monitor mix into channels 2/3 (at the time a `#phones` pseudo-device in the Monitor picker,
today the Headphones 1, Headphones 2 and Line Out entries on one return; direct port
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
- off77 = level lock, 0x04 = locked. Bit 3 (0x08) of the same byte is Line In's own level
  lock (section 13).
- off76 = a further gain-stage byte (memory: third input stage), still unexposed. Section 13
  identifies it as the Line In gain; the USB Aux level and lock above are unchanged.

### Implemented in OpenXLR (same day, all live-verified)

`WaveXlrProDevice`: output selectors with `WriteCommitted` (config write + block 3 commit;
since written through `ProMixPlan`), corrected ClipGuard/compressor, aux level + lock. Daemon controls:
outHp1/outHp2/outUsbAux/outLineOut/auxLevelDb/auxLevelLock/compressor (polarity removed). UI:
MONITOR ROUTING toggles in the HEADPHONES card, USB Aux level+lock row, Compressor button,
asterisks gone. Mixer: `<proSink>#phones` pseudo-sink "Wave XLR Pro Headphones" in the Monitor
picker, routed to channels 2/3 (historical; the picker now lists Headphones 1, Headphones 2
and Line Out).

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
    (0x02) = the mic's direct zero-latency path (CORRECTED 2026-10-30, see "Section 0's input
    side" in section 12: off13 bit1 is the Aux mix's USB pair 5; the mic's path is off12 bit0 at
    the level in off0). A unit Wave Link left with off12 bit5 clear
    (reporter's: off12 = 0x90 vs 0x24 here) hears no Monitor mix on Headphones 1 at all; the
    daemon now asserts bit5 whenever a jack is the monitor output and drives bit1 from the
    XLR 1 -> Monitor send's mute. Both need the commit and a playback-stream restart to latch.
    LATER (see Layout below): byte 5 is pair 1's level in that mix after all, int8 in half
    dB. Wave Link keeps its own fader level there, so a unit that dual-boots can play the
    Monitor mix 27.5 dB down (`c9`) with bit5 set. The daemon writes byte 5 to `00` with
    bit5, and reads the return as on only when both hold.
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

## 9. The class-request dialect of the original Wave XLR and the XLR Dock

The original Wave XLR (`0fd9:007d`) and the XLR Dock (`0fd9:00a6`) do not
speak the block bank above. Their backends use the class-request dialect
the openwave project documented, with wIndex naming interface 3:

| field         | write  | read   |
|---------------|--------|--------|
| bmRequestType | 0x21   | 0xA1   |
| bRequest      | 0x05   | 0x85   |
| wValue        | block  | block  |
| wIndex        | 0x3303 | 0x3303 |

wIndex 0x3303 rather than 0x3300 bypasses the kernel's ownership check
for interface 0, which snd-usb-audio holds; the firmware validates only
the 0x33 prefix. Block 0x0000 is the config block, read and written whole
(34 bytes on the Wave XLR, 64 on the dock). Block 0x000A is a 51-byte
device info record carrying the unit's USB serial in ASCII from offset
35. The config block layout the code uses on both devices:

| offset | field | encoding |
|---|---|---|
| 0 | gain | u16 little-endian, Q8.8 dB (256 raw units per dB), written 0 to 75 dB |
| 4 | mute | byte, nonzero = muted |
| 6 | phantom 48V | byte, 0x01 = on |
| 9 | headphone volume | int16 little-endian, Q8.8 dB, -60 to 0 dB |
| 33 | low impedance | byte, 0x01 = on |

On the XLR Dock the kernel also exposes gain (`Mic Capture Volume`, 0 to
150 for 0 to 75 dB), mute (`Mic Capture Switch`) and headphone volume
(`PCM Playback Volume`, 0 to 120 for -60 to 0 dB) as ALSA controls backed
by the same registers. The dock backend lists the card's controls at
connect and drives each of the three through its ALSA control when the
card has it, else through the block field above, one path per control:
the kernel caches feature unit values and does not re-read them, so a
block write behind ALSA's back would leave the two disagreeing. An ALSA
write shows up in the block at once. Phantom power and low impedance
have no ALSA control and always go through the block.

## 10. Captures 16 to 20 (2026-10-21): the microphone EQ in block 0x0004

Wave Link 3.5.0.4892 on Windows, the Pro on firmware 1.0.14 (block 0x0000,
117 bytes, bytes 5 to 7 `01 00 0e`), bank 0x0103, hardware mixing on. Wave
Link polls blocks 0x0008, 0x0001, 0x0004, 0x0005, 0x0006 and 0x0002 at
about 30 Hz and writes `41 01 <block> 0103`, the whole block.

Each input's 38-byte struct in block 0x0004 carries the four-band EQ in
the layout the Wave XLR MK.2 and XLR Dock MK.2 use from firmware 1.0.15
([findings](mk2-eq-findings.md)), XLR 1's at offset 0 and XLR 2's at 38:

| Offset (XLR 1 / XLR 2) | Field | Encoding |
|---|---|---|
| 2 / 40, bit 0 | EQ on | 1 = on; bit 2 of the same byte is ClipGuard off |
| 18, 23, 28, 33 / 56, 61, 66, 71 | band flags | bit 0 band on, bits 1 to 3 type (0 high pass, 1 low shelf, 2 bell, 3 notch, 4 high shelf, 5 low pass), bits 4 and 5 unknown and kept |
| next byte | gain | int8, quarter dB |
| next two | frequency | uint16 little endian, Hz |
| next byte | Q | int8, round(64 log10 Q) |

Bytes 76 to 79 are the USB Aux input tail (section 6).

- **16, idle.** the block was constant over 801 reads and decoded on both
  inputs to Wave Link's saved EQ (60 Hz high pass, a +3.25 dB low shelf at
  227 Hz, a -3 dB bell at 860 Hz, a +1.75 dB bell at 10237 Hz), with the
  switch clear on both, as Wave Link had both EQs off.
- **17 and 19, the switch.** XLR Mic 1's EQ on wrote byte 2 `00` to `01`,
  XLR Mic 2's wrote byte 40 `04` to `05`, nothing else, each read back on
  the next poll.
- **18 and 20, band 2 dragged to +12 dB.** 18 and 16 whole-block writes,
  changing only band 2's gain byte (24 or 62) and frequency (25 and 26 or
  63 and 64), 13 of 13 gains and every frequency matching what Wave Link showed,
  the carry at 256 Hz included.
- **No commit for EQ writes.** Wave Link sends the 0x0003 commit `04 00`
  followed by zeros only on its own timing,
  2.5 s to minutes after a change, which looks like a save for standalone
  use; EQ changes read back without it. Whether an EQ written without the
  commit survives a power cycle was not captured.

OpenXLR drives this from firmware 1.0.14 through the same codec as the MK.2
family, at the struct's base offset, through the block's one
read-modify-write path ([api.md](api.md#where-the-eq-runs)). One bell was
measured on the desk: +12 dB at 5 kHz written from OpenXLR read +12.3 to
+12.7 dB on the raw USB capture, and software mode left it flat. The other
filter types' curves and the zero-latency headphone path are still open.

## 11. Ducking (captures of 2026-10-21)

Wave Link 3.5 on Windows, firmware 1.0.14, captures 24 to 31 of the
session on the maintainer's unit. This corrects section 2: block 0x0008 is
not telemetry, Wave Link writes it.

Block 0x0008, 96 bytes, twelve entries of eight bytes. Entries 0 to 3 are
the hardware inputs (XLR Mic 1, XLR Mic 2, USB Aux in, Line In), entries 4
to 11 the USB playback pairs 0/1 to 14/15, which Wave Link uses as its
channel returns (Browser 2/3, Music 10/11, Game 12/13, System 14/15).

| Byte | Field | Values Wave Link writes |
|---|---|---|
| 0 | key threshold, int8 dB | sensitivity Low `fd` (-3), Medium `f4` (-12), High `e8` (-24) |
| 1 | depth, dB | amount Low `06`, Medium `0c`, High `18` |
| 2, 3 | attack, LE16 ms | speed Slow 100, Medium 80, Fast 30 |
| 4, 5 | release, LE16 ms | speed Slow 2000, Medium 1200, Fast 200 |
| 6, 7 | key mask, LE16 | bit M set when source M keys this entry (section 20) |

What the daemon writes to bytes 0 to 5:

| Byte | A preset word | A custom value |
|---|---|---|
| 0 | the preset's threshold above | the number, -60 to 0 dBFS, as int8 |
| 1 | the preset's depth above | the number, 1 to 40 dB |
| 2, 3 | the preset's attack above | the attack, 1 to 1000 ms, LE16 |
| 4, 5 | the preset's release above | the release, 10 to 10000 ms, LE16 |

Only the three preset values of each field have ever been seen on the
unit. A custom value goes into the same bytes, but nobody has yet checked
how the unit behaves with one. If the unit does not keep a value, the
read back after the write shows it and the `failed` rule moves ducking to
software. The readback reports a value every entry holds as the preset's
word when a preset writes it and as the number otherwise, and `custom`
only when the entries disagree.

Each preset change wrote one field in all twelve entries and nothing else
(three values each). With no key at all Wave Link left byte 6 at `00 02 00
00 03 00 00 03 03 00 00 00`: leftover key masks, XLR 2 (`02`) and XLR 1
with XLR 2 (`03`), which section 20 explains.

Making XLR Mic 1 the key for Browser, Music, Game and System wrote 0x0008
(byte 6 bit 0 on entries 5, 9, 10, 11) and block 0x0001 bytes 16 to 17, 34
to 35 and 52 to 53 `55 55 -> 5d fd`, 8 ms apart, with no 0x0003 commit and
nothing rolled back. Those two bytes after byte 15, 33 and 51 are
eight 2-bit fields, little endian, field k for pair k: `01` normally, `11`
while the pair is ducked in that mix. The sections at 69 and 87 did not
change. With XLR 1 loud and no host write the unit lowered the ducked
pairs by the configured depth (block 0x0002 meters, capture 28). Section
20 shows that byte 6 bit 0 is XLR 1's bit in a key mask.

The daemon writes the same fields
(`src/OpenXLR.Core/Devices/ProDucking.cs`): the setup's sensitivity,
amount and speed in bytes 0 to 5 of all twelve entries while ducking is
on, the key mask of the setup's keys (`01 00` for XLR 1 alone, section
20) in bytes 6 and 7 of the target pairs' and inputs' entries and `00 00`
in every other entry, so the keys Wave Link leaves behind are cleared,
and the high bit of each pair field in the three mix sections. Every
write of block 0x0001 that carries ducking also sets the target inputs'
tap fields to `11` and puts every other input tap field at `11` back to
`10` (section 20). Every other
byte of both blocks is copied from a fresh read.
It writes 0x0008, then 0x0001, only the blocks that change, without the
commit. Its targets are the pairs OpenXLR streams to: 2/3 (the jacks'
monitor feed) and 10/11 (the Aux mix, through the aux mix's Music cell).
On the desk the unit's ducking did not reach the jacks, so OpenXLR ducks in
software on the Pro ([hardware support](hardware-support.md)). Section 12
explains it: the unit ducks a pair only when its field reads `11`, and on
this unit, outside Wave Link, the fields read `00`, so setting the high bit
alone writes `10`, which the unit ignores.

## 12. The mix sections and the jacks (desk probe of 2026-10-23)

Firmware 1.0.14, the daemon stopped, `tools/proprobe.py` for every write.
Four sine tones (1, 2.7, 4 and 7.9 kHz, a sweep from 0.6 to 11 kHz for the
colour check) were played on one USB pair with `paplay --channel-map`.
Headphones 1 was at 0 dB (block 0x0005 byte 0 `00`) with an ear cup pressed
on the XLR 1 microphone, and each tone was read narrowband from capture
AUX0 and from the capture channels the unit streams back. Repeated runs
agreed within 0.5 dB. Every path or selector change was written with the
0x0003 commit and followed by a sink bounce. Blocks 0x0001, 0x0005 and
0x0008 were restored byte for byte afterwards.

This corrects section 11 and the desk note of 2026-10-21 in three places:
each pair of field bytes belongs to the section before its byte 15, the
monitor return is not a path around a mix, and a ducked field must read
`11`, not only have its high bit set.

### Layout

Block 0x0001 starts with five mix sections of 18 bytes, at 0, 18, 36, 54
and 72. For a section starting at `s`:

| Bytes | Field |
|---|---|
| `s` to `s+11` | one level per source, int8 in half dB, `00` unity, held at +12 dB (`18`) and above |
| `s+12`, `s+13` | membership, little endian, one bit per source |
| `s+14` | unknown |
| `s+15` | the four inputs' 2-bit tap fields (section 20); `aa` as Wave Link leaves them |
| `s+16`, `s+17` | the 2-bit ducking fields, field k for USB pair k |

The sources, as bit and level index: 0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line
In, then 4 + k for USB playback pair k (pair 1, the jacks' monitor feed on
AUX2/AUX3, is bit 5; pair 5, the Aux mix on AUX10/AUX11, is bit 9). This
fits the cells known before: Line In's level at 21 and membership at byte
30 bit 3, the aux mix's Music level at 45 and membership at byte 49 bit 1.

Each section is played by one output selector value and streamed back on
one capture pair:

| Section | Selector | Capture | Notes |
|---|---|---|---|
| 0 | `1e` | AUX8, AUX9 | the jacks' mix today; byte 12 bit 5 is pair 1's membership, the "monitor return", and bit 3 Line In's (section 13) |
| 18 | `1f` | AUX10, AUX11 | on this unit XLR 1 and XLR 2 are members (byte 30 `13`) |
| 36 | `20` | AUX12, AUX13 | the aux mix |
| 54 | `21` | AUX14, AUX15 | |
| 72 | `22` | AUX16, AUX17 | |

The other selector values from `18` to `2f` played nothing.

### What the probe showed

- With byte 12 bit 5 clear, the only pairs the jacks played on `1e` were 6
  and 7, at the return's level: byte 13 is `0c`, bits 10 and 11. The
  "monitor return" is pair 1's membership in the section the jacks play,
  not a path around a mix.
- Membership comes from the mask alone. Pair 1's field at `01`, `10` or
  `11` added nothing; on `1f`, byte 30 bit 5 with the field at `00` put
  pair 1 on the jacks.
- Level cells: byte 5 (pair 1 in section 0) measured +6.2 dB at `0c` and
  -6.1 dB at `f4`. The daemon holds it at `00` while a jack is a monitor
  output; a unit Wave Link had used held `c9` (-27.5 dB) there. Byte 23
  (pair 1 in section 18) measured -30 dB at `c4`, -10 dB at `ec`, -6 dB
  at `f4`, -3 dB at `fa`, +3 dB at `06` and +12 dB at both `28` and `50`.
- At the same level cell, sections 18 to 72 play 21 dB louder than section
  0, on the jacks and on their capture streams alike. With byte 23 at `dc`
  (-18 dB) the difference was a flat 3.0 dB from 1.5 to 11 kHz, each
  reading within 0.3 dB of it: gain, no colour. Headphones 1's volume acts
  on all of them.
- Byte 0 at 0, 100 and 200 did not change pair 1.
- Ducking: block 0x0008 with every threshold at -60 dB and depth 6 dB, pair
  1's entry flagged, the key held by the tone itself through the cup (block
  0x0002 bytes 26, 30, 38 and 42 at `18`, the full depth). On `1e` with
  byte 12 bit 5 set, OpenXLR's path today, pair 1's field in byte 16 at
  `11` lowered the jacks and capture AUX8/AUX9 by 6.0 dB, twice. At `10`,
  `01` or `00` nothing changed. Pair 5's field in byte 53 at `11` (entry 9
  flagged) lowered the aux mix's stream on AUX12/AUX13 by 6.0 dB. The USB
  Aux port itself dips too (section 17, the probe of 2026-11-02).
- Block 0x0002 bytes 48 and 49 stayed `f0` with noise on pair 1, so they
  are not a playback meter for it. The bytes that moved all followed XLR 1.

Wave Link leaves every field at `01` and sets the high bit on a ducked
pair, which makes `11`. The daemon sets the high bit and keeps the low bit
as read; with the fields at `00`, as this unit holds them under OpenXLR,
that writes `10`, which is why the desk test of 2026-10-21 saw the engine
reduce and the headphones stay level.

### Open

- What the low bit means alone, and whether setting it on every pair, as
  Wave Link does, changes anything else. Section 17 found it does nothing
  to the level by itself.
- Byte 14 of each section. Section 0's 21 dB lower level is byte 98
  (section 17).

### Section 0's input side (desk measurement of 2026-10-30)

Firmware 1.0.14, the desk unit, daemon stopped. A probe played tones on
pair 1 (AUX2/AUX3, 440 Hz) and pair 5 (AUX10/AUX11, 1 kHz) and recorded
section 0's capture (AUX8/AUX9) beside XLR 1's (AUX0). It made one change
at a time and restarted the sink after each. The layout above holds for
the input side too:

| Change | 1 kHz from pair 5 in section 0 | XLR 1 into section 0 |
|---|---|---|
| byte 13 bit 1 clear | -112 dBFS, absent | |
| bit 1 set, byte 9 `00` | -72.5 dBFS | |
| bit 1 set, byte 9 `f4` | -75.3 dBFS | |
| bit 1 set, byte 9 `88` | -123 dBFS, absent | |
| byte 12 bit 0 clear | | silent |
| bit 0 set, byte 0 `00` | | -21.0 dB |
| bit 0 set, byte 0 `f4` | | -27.0 dB |
| bit 0 set, byte 0 `64` | | -9.0 dB (held at +12) |
| bit 0 set, byte 0 `c8` | | -49.6 dB |

The -21 dB at unity is section 0's own offset (see above), the same for
pair 1. Byte 13 bit 1 with XLR 1 out of the mix carried no microphone.
So byte 13 bit 1 is pair 5's membership with its level at byte 9, and
the microphone's direct path is byte 12 bit 0 with its level at byte 0,
the byte OpenXLR drove as a Mic/PC crossfade from 0 to 200. A second run
with the daemon running wrote byte 0 alone, without a commit or a stream
restart: the level moved at once (`00` -21.0 dB, `f4` -27.0 dB).

The daemon drove byte 13 bit 1 as the microphone's path until then: on a
unit whose Aux port was in use that played the whole Aux mix on the jacks,
and on an owner's unit (byte 9 `88`, left by Wave Link; byte 12 `30`, so
XLR 1 out of the mix) it played nothing while the software send was held
back, and the microphone was missing from the jacks. It now drives byte
12 bit 0 at the level the Monitor send would play, and clears byte 13
bit 1 with the Monitor return.

### The other USB pairs in section 0 (desk measurement of 2026-10-31)

Firmware 1.0.14, the desk unit, daemon stopped. A probe played a 1 kHz tone
at -40 dBFS on one USB playback pair at a time and read it on section 0's
capture (AUX8) with that pair's membership bit clear and then set, the sink
suspended and resumed after each write. The block held byte 12 `24`, byte
13 `0c` and every level cell from byte 0 to 11 at `00`, and was written
back identical afterwards.

| Pair | Channels | Bit | Clear | Set |
|---|---|---|---|---|
| 0 | AUX0, AUX1 | byte 12 bit 4 | absent | -70.8 dBFS |
| 2 | AUX4, AUX5 | byte 12 bit 6 | absent | -70.4 dBFS |
| 3 | AUX6, AUX7 | byte 12 bit 7 | absent | -70.6 dBFS |
| 4 | AUX8, AUX9 | byte 13 bit 0 | absent | -70.5 dBFS |
| 6 | AUX12, AUX13 | byte 13 bit 2 | absent | -72.5 dBFS |
| 7 | AUX14, AUX15 | byte 13 bit 3 | absent | -70.4 dBFS |

So pair k is source 4 + k, as section 12 has it, with its level at byte
4 + k. With the daemon running the desk unit played pairs 6 and 7 on the
jacks' mix, an owner's unit pair 0 (byte 12 `30`), and the unit Wave Link
left in section 13's table pairs 2, 3, 4, 6 and 7 (`e8 0d`). Nothing of
OpenXLR's plays on those pairs, but a program that picks the Pro's sink
directly lands on pair 0, and it reached the jacks with no control in
OpenXLR. When the daemon turns the Monitor return on it now clears every
pair but pair 1 in the same write, and the return reads as on only while
they are clear.

Bit 1 is XLR 2, at the level in byte 1. With a microphone on XLR 2 and the
daemon stopped, bits 0 and 1 clear left the mix silent; bit 1 set put XLR
2's capture (AUX2) on AUX8 at a flat -21 dB with coherence 1.000, and byte
1 at `f4` lowered it to -27 dB at once, without a stream restart, as XLR 1's
level does. It is XLR 2's zero-latency path, and the daemon drives it as it
drives XLR 1's: open at the level the Monitor send would play while that send
is unmuted, the jacks are the only monitor output and no software effect runs
on XLR 2, and the send held back in the graph meanwhile.

Bit 2 is the USB Aux input. With a Mac playing into the desk unit's USB
Aux port, bit 2 set and byte 2 at `00`, section 0's capture carried the
Aux In capture (AUX4, AUX5) at a flat -21 dB, section 0's offset, with
coherence 1.0, left to left and right to right, while the monitor played
elsewhere and pair 1 was silent. So the jacks hear a device on the USB Aux
port in hardware, whatever the Aux In channel's sends are. The daemon
leaves this bit as read: unlike Line In, an owner can rely on it to hear a
second computer or a console on the jacks with the Aux In channel muted in
the Monitor mix.

## 13. Line In and the capture layout (desk measurement of 2026-10-25)

Firmware 1.0.14, the desk unit. This corrects the mixer's earlier model, in
which the USB Aux port and the Line In jack shared one capture pair (pair
2) and one channel served both. Elgato documents USB Aux and the analog
Line In as separate inputs
([Wave XLR Pro: USB Host and USB Aux](https://www.elgato.com/ch/en/explorer/products/wave/wave-xlr-pro-whats-the-difference-between-usb-host-and-usb-aux/)),
and the capture source carries them apart.

### The raw capture source

`alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_*.pro-input-0` has 18 channels:

| Channels | Carries |
|---|---|
| AUX0, AUX1 | XLR 1 |
| AUX2, AUX3 | XLR 2 |
| AUX4, AUX5 | USB Aux |
| AUX6, AUX7 | Line In, stereo, confirmed with a source playing into the jack |
| AUX8 to AUX17 | the five hardware mixes streamed back (section 12) |

OpenXLR's Aux In channel takes AUX4 and AUX5 only, and its Line In channel
takes AUX6 and AUX7 (capture pair 3). No channel takes AUX8 to AUX17. The
Line In channel is shown only while the capture source has this 18-channel
layout; on a source with fewer channels it stays silent.

### The Line In gain

Block 0x0004 byte 76 holds the Line In gain in dB, 0 to 80 (`14`, 20 dB,
on the desk unit). OpenXLR writes no more than 40: an owner reports hiss
from the Line In above about 20 dB, and that Wave Link limits the gain to
20 dB. A unit set higher elsewhere reads back its own value. The same value is the third pair (center and woofer) of
the six-value ALSA control `Elgato Wave XLR Pro Capture Volume` (numid=3),
whose front pair is XLR 1's gain and rear pair XLR 2's. OpenXLR writes it
as it writes the XLR gains: one read of block 0x0004 and one write of the
whole block, with no 0x0003 commit, every other byte as read. Bytes 77 to
79 are not written by this control. There is no hardware Line In mute;
the channel's mute in OpenXLR is the software one.

The firmware holds a Line In gain written through that ALSA control to 20
dB (desk measurement of 2026-10-26, below): ALSA writes of 22, 25 and 35
each left byte 76 at 20, while 10 and 12 were taken as written. A write
through block 0x0004 takes values above 20; the desk unit sits at 40.

### Line In's level lock (desk measurement of 2026-10-26)

Firmware 1.0.14, the desk unit, the daemon stopped, `tools/proprobe.py`
for every block read and write, the block restored afterwards. Block
0x0004 byte 77 bit 3 (0x08) is Line In's level lock, beside the USB Aux
lock at bit 2 (0x04, section 6):

| Byte 77 | Write | Byte 76 after |
|---|---|---|
| bit 3 set | ALSA, 5 | 12, unchanged |
| bit 3 set | ALSA, 17 | 12, unchanged |
| bit 3 set | block 0x0004, 8 | 8 |
| bit 3 clear | ALSA, 5 | 5 |
| bit 2 set, bit 3 clear | ALSA, 14 | 14 |

The ALSA writes went to the centre pair of `Elgato Wave XLR Pro Capture
Volume` (numid=3). So the lock stops the desktop mixer and other
applications from changing the Line In gain through ALSA, and leaves
writes of the block, which is how OpenXLR writes the gain, in effect. The
USB Aux lock does not hold Line In. An owner's unit on which Line In's
Level Lock had been turned on read byte 77 `0c`, both locks set (the
table in the next section).

OpenXLR writes the lock as it writes the gain: one read of block 0x0004
and one write of the whole block, with no 0x0003 commit, every other byte
as read, the USB Aux lock included. The daemon's control is
`lineInLevelLock`.

### Line In in the jacks' mix

By the layout of section 12, block 0x0001 byte 12 bit 3 is Line In's
membership in section 0, the mix the jacks play on selector `1e`, and byte
3 its level there. The desk unit holds the bit clear. An owner's
diagnostics archive (0.2.4) read:

| Bytes | Owner's unit | Desk unit |
|---|---|---|
| block 0x0001, 0 to 13 | `c8 00 00 c2 00 00 c9 c9 c9 00 a6 c9 e8 0d` | `c8 00 00 00 00 00 00 00 00 00 00 00 25 0c` |
| block 0x0001, 90 to 93 | `1e 23 20 23` | `23 23 20 23` |
| block 0x0004, 76 to 79 | `14 0c 04 00` | `28 00 00 00` |

Byte 12 `e8` has bit 3 set, and Headphones 1 plays section 0. That owner,
with a console on the Line In, reported that raising Line In's Monitor A
send doubled it and that the console stayed audible on the headphones
with the send muted: the jack reached the jacks in hardware beside the
Line In channel in the Monitor mix.

When the daemon turns the Monitor return on (byte 12 bit 5, while a
headphone jack or Line Out is a monitor output), it clears bit 3 in the
same write, with the 0x0003 commit, and the return reads as on only while
bit 3 is clear, so a unit that connects with the return set and Line In
in the mix is written once. The change latches when the host playback
stream restarts, as the return does, and the daemon bounces it. Bit 2
(USB Aux, set on the desk unit as `25`, measured in section 12) stays as
read, and bit 0 is XLR 1's direct path. The other USB pairs are cleared
with bit 3 since 2026-10-31 (section 12). Turning the return off leaves
all of them as read.

### Open

- Writing byte 76 from OpenXLR and hearing the gain change on the Line In
  channel.
- Hearing the Line In path on the jacks come and go with byte 12 bit 3.
- Byte 78, and byte 77 beyond its two level locks. Line In's lock at bit
  3, open until 2026-10-26, is now measured above.

<a name="compressor-and-expander-on-the-unit"></a>
## 14. Compressor and expander on the unit (2026-11-01)

The 38-byte input struct of block 0x0004 carries the settings of the
unit's own compressor and expander between the flag byte and the EQ bands.
The layout is the one the Wave:3 MK.2 (firmware 1.0.1, bank 0x0103) holds,
where every field was seen written to both ends of its limits and read
back at once, with no commit, as part of a write of the whole block. The
Pro holds the same fields at the same offsets in both structs, XLR 1's at
0 and XLR 2's at 38, with the same factory values (a unit on firmware
1.0.14 read `d4 20 0a 00 c8 00 18 .. f4 04 0a 00 64 00 06` from byte 3 of
each struct). The Pro's layout is inferred from those matching fields:
nobody has yet heard its DSP act on values written there.

| Offset (XLR 1 / XLR 2) | Field | Encoding | Limits |
|---|---|---|---|
| 1 / 39, bit 7 (0x80) | compressor on | the switch the `compressor` control sets | |
| 1 / 39, bit 5 (0x20) | expander on | the switch the `expander` control sets | |
| 3 / 41 | expander threshold | int8, dB | -60 to -1 |
| 4 / 42 | expander ratio | uint8, sixteenths (2.0:1 is `20`) | 16 to 48 (1.0 to 3.0) |
| 5, 6 / 43, 44 | expander attack | uint16 little endian, ms | 1 to 200 |
| 7, 8 / 45, 46 | expander release | uint16 little endian, ms | 10 to 1000 |
| 9 / 47 | expander range | uint8, dB, the most it turns down | 0 to 60 |
| 10 / 48 | voice tune strength | (section 6) | |
| 11 / 49 | compressor threshold | int8, dB | -60 to -1 |
| 12 / 50 | compressor ratio | uint8, a whole number | 1 to 20 |
| 13, 14 / 51, 52 | compressor attack | uint16 little endian, ms | 1 to 300 |
| 15, 16 / 53, 54 | compressor release | uint16 little endian, ms | 50 to 1000 |
| 17 / 55 | compressor make-up gain | uint8, dB | 0 to 20 |

The factory values are: compressor -12 dB, 4:1, 10 ms, 100 ms, +6 dB;
expander -44 dB, 2:1, 10 ms, 200 ms, 24 dB range. The other bits of byte 1
(mute, phantom power, the low cut, voice tune, and bits 2 and 3, which
nobody has named; bit 3 reads set on several units) are kept as read.

OpenXLR writes these fields only on a unit whose EQ it drives (section 10)
and whose fields hold values inside the limits above, through the same
read-modify-write path and timing as the EQ ([api.md](api.md#where-the-compressor-and-expander-run)).

## 15. The mix sections as a mixer (desk probe of 2026-11-02)

Firmware 1.0.14, the desk unit, the daemon running. These are the probes
the roadmap's hardware mixing mode waits on: whether the unit takes level
and membership changes live, the gain of each section, and the delay of
the returned mixes. Writes went to section 54 and section 72, which no
output selector played (bytes 90 to 93 read `23 23 20 23`), so nothing
reached the jacks or the USB Aux port. Each write was a read-modify-write
of block 0x0001, and the block was written back identical afterwards. The
unit's capture source was recorded across every change, all 18 channels,
and each section's stream (AUX14 for section 54, AUX16 for section 72) was
compared with XLR 1's own capture on AUX0.

### Live changes

| Time | Write | Section 54's stream |
|---|---|---|
| 3 s | byte 66 bit 0 set (XLR 1 in), byte 54 `00`, no commit | XLR 1 at 0.0 dB, coherence 1.000 |
| 7 s | byte 54 `f4`, no commit | -6.0 dB |
| 11 s | byte 54 `00`, then the 0x0003 commit | 0.0 dB |
| 15 s | byte 66 bit 0 clear, no commit | silent |
| 19 s | byte 66 bit 0 set, then the commit | 0.0 dB |

Every write took effect within the 250 ms window it was written in,
membership as well as level, with or without the commit and with no
stream restart. The unit ramps each change itself: the gain moved from
the old value to the new over 12 to 16 ms, measured in 1 ms steps, with
no step, so a mixer driving these bytes needs no fade of its own.

### Gain and delay

- A microphone in a section at level `00` comes back at the gain of its
  own capture: XLR 1 in section 18 (AUX10), section 54 and section 72
  matched AUX0 at 0.0 dB with coherence 1.0000. Only section 0 has the
  -21 dB offset of section 12. Byte 14 reads `00` in all five sections,
  so it does not account for that offset.
- The unit's other inputs behave the same (later runs the same day, a
  microphone on XLR 2, a phone playing into Line In and audio playing into
  the USB Aux port). XLR 2 (byte 66 bit 1, level byte 55), USB Aux (bit 2,
  byte 56) and Line In (bit 3, byte 57) came back in section 54 as exact
  copies of their own capture, AUX2, AUX4/AUX5 and AUX6/AUX7: 0.00 dB at
  `00`, 6.00 dB lower at `f4`, residual below -144 dB, 0 samples of delay.
  XLR 2 and Line In read the same in section 72. The stereo inputs stay
  stereo: left reaches only the section's left (AUX14, AUX16) and right
  only the right.
- A USB playback pair in a section at level `00` comes back 7.9 dB below
  what the computer sent: a 1 kHz tone at -43.0 dBFS RMS on pair 7, played
  with the Pro's sink at 100% and linked port by port, read -51.0 dBFS on
  AUX14. With the sink at the 70% WirePlumber had restored on the desk
  (-9.3 dB of software volume) it read -60.2 dBFS.
- A section's stream carries no delay over the direct capture: the cross
  correlation of AUX10 and of AUX14 against AUX0 peaks at 0 samples. A
  microphone mixed in the unit reaches the computer as early as its own
  capture does.

### The bottom of the level scale

USB Aux in section 54, every cell from `80` to `a0` written after `c4`,
then single steps from nine levels:

- Every cell from `89` (-59.5 dB) up gives its value exactly, in 0.5 dB
  steps. `88` (-60 dB) and below are off: the stream is digital zero, not
  -60 dB. Section 72 stores `88` for USB Aux on the desk, so that cell is
  off rather than quiet.
- A small change near the bottom is not applied. A 0.5 dB step down
  applied from every level down to -36 dB and was dropped from -42 and
  -48 dB; `90` to `8a`, `8a` to `90` and `8a` to `89` were dropped too,
  while a 3.5 dB step applied from -48 dB, and `a0`, `c4` or `00` to `8a`
  applied. The dropped and applied steps divide at a change in linear gain
  of about 0.0005: 0.00046 was dropped and 0.00089 applied. A dropped
  write still reads back from block 0x0001 as written, while the audio
  stays at the old gain, so a fader moved slowly below about -40 dB can
  leave the unit at a level the block does not show.
- XLR 1 (byte 54) and USB pair 7 (byte 65) behave the same, step for step
  (a later run the same day, both cells written together in section 54):
  0.5 dB steps applied down to -36 dB and were dropped from -42, -48 and
  -52 dB, `90` to `8a` and `8a` to `89` were dropped, 3.5 dB steps and
  steps from `00` applied, `89` was exact and `88` off. The divide is the
  same for a microphone, a playback pair and USB Aux, although their
  signals were tens of dB apart, so it belongs to the cell's gain, not to
  the signal.

### Across a power cycle

With the daemon stopped, section 54 got XLR 1 at `f4` (bytes 54 and 66)
followed by the 0x0003 commit, then section 72 got XLR 1 at `e8` (bytes 72
and 84) with no commit. The unit was unplugged for about 4 s within a
minute of the writes and plugged back in. Five seconds later block 0x0001
read exactly as written, both sections included, and blocks 0x0004,
0x0005 and 0x0008 had not changed. The unit keeps block 0x0001 across a
power cycle whether or not the commit followed the write, so a mix
written to it is still there on the next connect. Whether a write made a
second before the power goes would survive was not tested.

### Write rate

With the daemon stopped and a steady 1 kHz tone on pair 7, alone in
section 54 at `00`, block 0x0001 was written 20 times a second for 10 s
unchanged, then 10 s with section 72's empty cell changing on every
write, then 10 s sweeping pair 7's cell from `00` to `ec` and back in
0.5 dB steps, one step a write; after that the 0x0003 commit went 10
times a second for 5 s.

| Phase | Writes | Time per write | Section 54's stream |
|---|---|---|---|
| unchanged block | 201 | 3.4 ms median, 3.6 ms most | the tone untouched, residual -151 dB |
| another section changing | 201 | 3.5 ms median, 4.0 ms most | untouched |
| pair 7's cell sweeping | 201 | 3.4 ms median, 4.0 ms most | every step at its written level (mean error 0.003 dB), ramped |
| commits | 49 | 2.8 ms median, 3.0 ms most | untouched |

No write failed, the unit stayed on the bus with nothing in the kernel
log, and the stream had no sample-level discontinuity. A write to block
0x0001 costs about 3.5 ms of the USB control pipe, so 20 a second takes
about 7% of it, and neither a rewrite of the same block nor a commit
disturbs audio that the write does not change.

### Notes for a probe

- pipewire-pulse and pw-play remix a two-channel stream onto the first
  two ports of the Pro's 17-channel sink, whatever channel map they are
  given, and a 17-channel pw-play stream was placed on the default sink.
  A tone reaches one pair only from a stream created with
  `node.autoconnect = false` and linked by hand with `pw-link`.
- The Pro's sink keeps the volume WirePlumber stored for it. On the desk
  that was 70%, so everything OpenXLR plays to the unit, the Aux mix on
  pair 5 included, leaves 9.3 dB quieter than the graph sends it.

### Open

- Where the playback pairs' 7.9 dB goes (section 16 shows it is fixed and
  uniform, with a limiter above it).
- Section 0's -21 dB is byte 98 (section 17).
- Per-channel ducking keys other than XLR 1 (section 11); section 20
  answers it.

## 16. USB playback pairs in the mix sections (desk probe of 2026-11-02)

Firmware 1.0.14, the desk unit, the daemon running, writes to section 54
only, the Pro's sink at 100% for each run and back at its stored 70%
afterwards. Each tone came from a `pw-play` stream created with
`node.autoconnect = false` and linked to one pair by hand. Section 54's
stream (AUX14) was read narrowband, block 0x0001 was polled every 250 ms,
and blocks 0x0001, 0x0004, 0x0005 and 0x0008 were written back identical
after each run.

### Live writes

A -30 dBFS tone played on pair 7 for 32 s while its cell (byte 65) and its
membership (byte 67 bit 3) changed every 3 s:

| Write | AUX14 |
|---|---|
| bit set, cell `00` | -7.94 dB against what was sent |
| cell `f4`, then `e8` | 6.0 dB and 12.0 dB lower |
| bit clear, then set | silent, then back |
| the same, each followed by the 0x0003 commit | the same |
| cell `10` | 8.0 dB higher, so the pair came back at what was sent |

Every change applied within the 250 ms window it was written in, while
the stream ran, with or without the commit, and no poll read anything but
the last write. A pair's membership does not latch at stream start.

### Round trip

A JACK client under `pw-jack` (block size 256 at 48 kHz, the Pro's ALSA
nodes at three periods of 256) wrote a single-sample impulse to pair 7
every 0.5 s, with pair 7 alone in section 54 at `00`, and read section
54's stream (AUX14). All 23 impulses came back 1012 frames (21.08 ms)
after the cycle they were written in, as one sample at -17.23 dB: the
pair's 7.94 dB and the sink's 70% volume (-9.29 dB), with no smearing.
PipeWire reports one quantum of latency on each of the two ports, 512
frames together, so 500 frames (10.4 ms) of the round trip are not in
the reported latencies. The section adds no delay of its own (section
15), so all of it is the USB and driver path.

### Gain and headroom

- The loss is the same on every pair: a -30 dBFS tone on pairs 0, 1, 2, 3,
  4, 6 and 7 in turn, each alone in section 54 at `00`, came back at
  -7.94 dB. Pair 0 in section 18 read -7.86 dB; pair 1 in section 0 read
  -28.94 dB, the same loss plus section 0's 21 dB.
- The pairs share a ceiling. A -3 dBFS tone on pair 7 came back at
  -7.94 dB at `00` (a peak of 0.284) and at a peak of 0.382 (-8.4 dBFS) at
  both `10` and `18`, with its harmonics more than 120 dB down: gain
  reduction, not clipping. Two tones at -6 dBFS on pairs 6 and 7 (1 kHz
  and 1.5 kHz) peaked at 0.382 at `00` and did not rise when both cells
  went to `10`; their intermodulation stayed more than 110 dB down.
- The microphone does not meet that ceiling: XLR 1 peaks of 0.40 came
  back on AUX10 at the level of its own capture.

So the unit takes the computer's audio at 0.40 of full scale and holds
the playback sum near that peak with a clean limiter. A cell above `00`
lifts quiet material only, and full-scale playback never comes back
louder than about -8 dBFS. Whether the limiter acts on the jacks as it
does on the returned streams has not been measured.

On the jacks the microphone's path in section 0 plays at the Monitor
send's level less the output volume's attenuation while it is below 100%
(the Pro sink's volume, which acts on the pairs and not on the path), so
the output volume moves the voice with the Monitor mix
(`Mixer.JackOutputVolumeDb`). The pair's 7.94 dB is left in, as Wave Link
leaves it with the microphone's cell at `00`: the voice sits that much
above where the faders put it against the Monitor mix. Taking it off as
well was tried on the desk on 2026-11-02 (PR #661) and put the voice out of
hearing under music at equal faders: speech on XLR 1 at -35 dBFS RMS against
music near -12 dBFS RMS.

## 17. Section 0's offset, a capture tap and per-pair ducking (desk probe of 2026-11-02)

Firmware 1.0.14, the desk unit, the daemon stopped for both runs (it
writes blocks 0x0005 and 0x0008 itself). No output selector played
sections 0, 54 or 72 (bytes 90 to 93 read `23 23 20 23`). Every write was
followed by the 0x0003 commit in the first run and not in the second, and
blocks 0x0001, 0x0004, 0x0005 and 0x0008 read back identical after each
run.

### Section 0's 21 dB

With XLR 1 the only member of section 0 at `00`, AUX8 against AUX0:

| Write | AUX8 |
|---|---|
| none | -21.00 dB |
| block 0x0005 byte 4 `50` to `4c`, `60`, back | -21.00 dB throughout |
| block 0x0001 byte 98 `54` to `50` | -20.00 dB |
| byte 98 `60` | -24.00 dB |
| byte 98 back to `54` | -21.00 dB |
| block 0x0004 byte 78 `02` to `00`, `03`, back | -21.00 dB throughout |

Byte 98 is section 0's level in quarter-dB attenuation, dB = -byte/4, and
`54` (84) is the 21 dB. Section 18 (AUX10) was unchanged by it. Block
0x0005 byte 4 and block 0x0004 byte 78 did not move section 0.

### Byte 94

Byte 94 at `00` left every mix as it was and changed XLR 1's own capture:
AUX0 dropped 10.7 dB from 500 Hz to 3 kHz and 2.6 dB at 60 Hz, still
coherent with XLR 1 in the mixes from 150 Hz up, with 0 samples of delay.
At `03` AUX0 was the same as at `02`.

A second run the same day settled what `00` is. With the daemon stopped,
XLR 1 alone in section 54 at `00` as the processed reference (AUX14), the
unit's low cut, compressor and Voice Tune (block 0x0004 byte 1 bits 4, 7
and 6, each with the commit) were switched off and on, first with byte 94
at `00`, then at `02`:

| Switch | AUX0 at `00` | AUX14 against AUX0 at `00` | at `02` |
|---|---|---|---|
| none | -68.7 dBFS | +10.7 dB at 1 kHz, -10.4 dB at 40 Hz | identical |
| low cut off, on | unchanged | +3.2 dB at 40 Hz, then back | identical |
| compressor off, on | unchanged | 6.0 dB lower, then back | identical |
| Voice Tune off, on | unchanged | 4.6 dB lower, then back | identical |

So byte 94 at `00` gives XLR 1's capture before the unit's low cut,
compressor and Voice Tune, while every mix keeps the processed
microphone; `01`, `02` and `03` give the processed microphone. Coherence
stayed 1.000 throughout, with room noise as the signal, so the compressor
acted as its makeup gain only; the clip guard and the gain stage were not
tested. Bytes 95 to 97 at `00` changed nothing that could be seen: XLR 2
and USB Aux carried digital silence during the run and Line In's noise
floor stayed the same, so whether they hold the same choice for the other
inputs is still open.

### Ducking on the USB Aux port

A later run the same day, the daemon stopped, a MacBook recording the USB
Aux port. Section 36 holds pair 5 alone and the port plays it on `20`. A
1 kHz tone at -20 dBFS on pair 5, pair 5's field in byte 53 at `11`
(bits 2 and 3; the byte read `01` and was written `0d`), entry 9 flagged
with depth 12 dB, the key switched with every threshold at `c4` or `00`
as below. The section's stream (AUX12) and the MacBook's recording,
aligned on three beeps at the start:

| Key | AUX12 | the USB Aux port, as the MacBook heard it |
|---|---|---|
| off | -27.9 dBFS | -25.5 dBFS |
| on, twice | -39.9 dBFS | -37.5 dBFS |

The port dips by the entry's 12.0 dB at the same moments as the section's
stream and comes back with it, so the unit's ducking reaches the USB Aux
port. The 2.4 dB between the two columns is the MacBook's input level.

### Ducking in sections 54 and 72

A -20 dBFS tone on pair 6 (1 kHz) and on pair 7 (1.5 kHz), both members
of sections 54 and 72 at `00`. Block 0x0008: entry 10 (pair 6) flagged
with depth 12 dB, entry 11 (pair 7) flagged with depth 6 dB, attack and
release as the unit held them (30 ms and 200 ms). The key was XLR 1 with
room noise on it, held with every threshold at -60 dB (`c4`) and released
with every threshold at 0 dB (`00`).

| Fields | Key held | Section 54 | Section 72 |
|---|---|---|---|
| pair 7 `11` in 54, pair 6 `11` in 72 | yes | pair 7 -6.00 dB, pair 6 level | pair 6 -12.00 dB, pair 7 level |
| the same | no | level | level |
| both at `01` | yes | level | level |
| both at `10` | yes | level | level |
| every field in 54 and 72 at `11` | yes | pair 6 -12.00 dB, pair 7 -6.00 dB | pair 6 -12.00 dB, pair 7 -6.00 dB |

- The fields duck in sections 54 and 72 as they do in sections 0 to 36,
  for pairs 6 and 7 as for pairs 1 and 5.
- The depth belongs to the pair's entry in block 0x0008, not to the
  section: a pair dips by the same amount in every section that marks it.
- A field ducks only at `11`. The low bit alone and the high bit alone do
  nothing.
- The dip settled within 0.5 s of the threshold write and came back
  within 1 s of the release.
- A unit input is not ducked this way. In a second run, with a
  microphone live on XLR 2, XLR 2 was a member of sections 54 and 72 at
  `00` and entry 1 was flagged with depth 12 dB. While the key was held
  XLR 2 stayed at 0.00 dB in both sections, flagged or not, and pair 7 in
  the same section dipped its 6.00 dB. The inputs' own fields are in
  byte `s+15` (section 20), which this run did not write.

## 18. The headphone jacks on each section (desk probe of 2026-11-02)

Firmware 1.0.14, the desk unit, the daemon stopped, headphones on
Headphones 1 only with an ear cup held against a microphone on XLR 2.
Each section held one USB pair at `00` with its own tone at -12 dBFS:
section 0 pair 4 at 800 Hz, section 18 pair 2 at 1.2 kHz, section 54 pair
6 at 1 kHz, section 72 pair 7 at 1.5 kHz. The microphones were taken out
of section 18 for the run so the jack could not feed XLR 2 back to
itself. Headphones 1's volume was -18 dB. Blocks 0x0001, 0x0004, 0x0005
and 0x0008 read back identical afterwards.

| Byte 90 | Heard through XLR 2 |
|---|---|
| `23` | none of the tones |
| `21` | section 54's tone only |
| `22` | section 72's tone only |
| `1f` | section 18's tone only |
| `1e` | section 0's tone only |

- Each selector write played its section within the first second, with
  or without the 0x0003 commit and with no stream bounce. Clicks were not
  judged; the measurement was acoustic.
- Byte 98 (section 17) acts on the jack as on the capture stream. With
  byte 98 at `00` section 0's stream rose 21.0 dB to the level of the
  other sections, and its tone on the jack rose at least 13.5 dB, as far
  above the noise as the cup could show.
- The two jacks are independent. In a second run the headphones moved to
  Headphones 2, with byte 98 at `00`. With byte 91 at `22`, Headphones 2
  played section 72's tone alone and at the same level while byte 90 went
  through `21`, `1e`, `1f` and `22`. With byte 90 held at `22`, byte 91 at
  `21`, `1f`, `1e` and `23` played section 54, section 18, section 0 and
  nothing. So each jack plays its own section at the same time as the
  other, with no commit.
- Line Out on each section is in section 19. The USB Aux port on sections
  other than its own was not tested.

## 19. Line Out on each section, and its level (desk probe of 2026-11-02)

Firmware 1.0.14, the desk unit, the daemon stopped, a cable from Line Out
into Line In. Line In was a member of no section, so nothing could loop.
Each section held one USB pair at `00` carrying its own white noise at
-40 dBFS (section 0 pair 4, section 18 pair 2, section 54 pair 6, section
72 pair 7), with byte 98 at `00`. Line In's gain was 20 dB. Line In's
capture (AUX6) was matched against each section's stream with a 96-tap
filter fit. Blocks 0x0001, 0x0004, 0x0005 and 0x0008 read back identical
afterwards.

| Write | AUX6 |
|---|---|
| byte 93 `23` | Line In's own noise, -90.6 dBFS |
| byte 93 `1e`, `1f`, `21`, `22` in turn | section 0, 18, 54 and 72 respectively, only that one |
| block 0x0005 byte 4 `50` to `4c` | +1.00 dB |
| block 0x0005 byte 4 `60` | -4.00 dB |
| block 0x0005 byte 4 back to `50` | as before |
| byte 93 `20` | silence (section 36 carried nothing) |

- Line Out plays the section its selector names within half a second of
  the write, with no commit.
- Block 0x0005 byte 4 is Line Out's level, dB = -byte/4, the encoding of
  the headphone volumes at bytes 0 and 2. It did nothing to section 0's
  capture stream (section 17), so it acts on the jack only. At `50`
  (-20 dB) the loop through Line In's 20 dB gain measured -0.4 dB at
  1 kHz.
- The path from a section to Line Out and back through Line In is flat
  within 0.6 dB from 50 Hz to 20 kHz and takes 97 samples (2.02 ms) more
  than the section's own capture stream. That is the output and input
  converters together; how it splits between them was not measured.

## 20. Ducking keys and the input taps (2026-11-10)

USB captures of Wave Link on Windows choosing each key, and the unit's own
meters in block 0x0002 while speaking into one input at a time.

### The key mask in block 0x0008

Bytes 6 and 7 of each entry are one little-endian 16-bit key mask. Bit M
set means source M keys the entry, with the sources numbered as the
entries are: 0 XLR 1, 1 XLR 2, 2 USB Aux in, 3 Line In, 4 + k USB
playback pair k. Making XLR 2 the key set bit 1, Music (pair 5) bit 9 and
XLR 1 bit 0, in each ducked channel's entry.

- The mask is a union: every set bit is a live key, and the unit's key
  level is the loudest of the masked sources. With masks `03 02` (XLR 1,
  XLR 2 and pair 5) on the pairs' entries, speaking into XLR 2 alone
  ducked those pairs.
- XLR 1's key level is taken after its insert.
- A source's own bit in its own entry is ignored: entry 1 with bit 1 set
  was never keyed.
- Wave Link sets a bit when a source becomes a key and never clears one,
  so units carry leftover masks, commonly `02` and `03` (the idle state of
  section 11). On such a unit XLR 2 ducks whatever those entries' fields
  mark.

OpenXLR writes the union of the setup's keys on each entry it ducks, less
that entry's own bit, and `00 00` on every other entry, which clears the
leftovers. Keyed by XLR 1 alone that is `01 00`; keyed in hardware mixing by XLR 1, XLR 2
and Voice Chat on pair 4 it is `03 01` on a pair's entry and `01 01` on
XLR 2's own. A key is one of the four inputs, or in hardware mixing a
channel's pair (bit 4 + k). The readback's `entries` lists the entries
whose mask has a bit set beside their own, and `keys` the bits set across
them; whether the unit holds OpenXLR's setting is decided on every byte,
so an entry that also holds a key OpenXLR did not write does not read as
held and is written again.

### The tap fields in block 0x0001

Bytes `s+15`, `s+16` and `s+17` of each of the five mix sections hold
twelve 2-bit fields, little endian, field M for source M in the same
numbering: `s+15` the four inputs, `s+16` and `s+17` the eight pairs (the
ducking fields of sections 11 and 12). A field is where the section takes
that source:

| Field | Tap |
|---|---|
| `00` | before the unit's processing |
| `01` | after it |
| `10` | after the insert return |
| `11` | after the ducker |

Wave Link's usual `aa` at `s+15` is all four inputs at `10`. It wrote `ae`
(XLR 2 at `11`) and `af` (XLR 1 and XLR 2 at `11`) when it made those
inputs ducked channels. The pairs' fields follow the same table, which
is why a ducked pair reads `11`.

An input's field at `11` in a section ducks that input in that section's
mix, by the depth and keys of the input's own entry in block 0x0008.
That is confirmed for XLR 1 and XLR 2. USB Aux and Line In use the same
layout (fields 2 and 3 at `s+15`, entries 2 and 3), but no capture shows
them ducked, so OpenXLR's writes for them are not confirmed on a unit.

OpenXLR ducks an input when the setup lowers it: the input's field goes
to `11` in each section the setup ducks (the section of each ducked jack
feed and of the Aux mix, or each section of a ducked mix the unit builds
in hardware mixing), and its entry takes the keys. Its decode takes any
value at `s+15`. Every write that carries ducking sets every other input
field read at `11` to `10`, so an input that leaves the setup is put back
at once. A hardware mixing write and every section it puts back do the
same, except for the inputs the ducking posted to the unit puts after the
ducker, which they leave as read; they never set a field to `11`. The
other input fields stay as read.

## 21. The meters in block 0x0002

Block 0x0002 is 150 bytes, read only. It is the unit's meters: reductions
and levels, never a setting.

| Bytes | Field |
|---|---|
| 0 to 9 | five maximizers, two bytes each: reduction, input level |
| 10 to 57 | twelve duckers, four bytes each, ducker N at `10 + 4N`: reduction, key level, output left, output right |
| 58 to 63 | two expanders, three bytes each: reduction, key level, output level |
| 64 to 69 | two compressors, laid out as the expanders |
| 70, 71 | Voice Tune's reduction on XLR 1 and XLR 2 |
| 72 to 141 | 35 stereo levels, channel K at `72 + 2K`, left then right |
| 142 to 149 | the level at Headphones 1, Headphones 2, Line Out and the USB Aux port, left and right each |

Ducker N is block 0x0008's entry N, in the same numbering: 0 XLR 1, 1 XLR
2, 2 USB Aux, 3 Line In, 4 + k USB playback pair k. Pair k's reduction is
byte `26 + 4k`. This is what section 12 saw: bytes 26, 30, 38 and 42 at
`18` (6 dB) are the duckers of pairs 0, 1, 3 and 4 at the configured
depth, and bytes 48 and 49, which stayed at the floor with noise on pair 1,
are the output of pair 5's ducker.

The 35 level channels:

| K | Level |
|---|---|
| 0 to 3 | XLR 1 before the unit's processing, after it, after the insert return, after the ducker |
| 4 to 7 | XLR 2, the same four |
| 8 to 10 | USB Aux as it arrives, after the insert return, after the ducker |
| 11 to 13 | Line In, the same three |
| `14 + 2k`, `15 + 2k` | USB playback pair k as it arrives and after its ducker (k 0 to 7) |
| 30 to 34 | the five mix sections of block 0x0001, in order (0, 18, 36, 54, 72) |

A level byte is -4 times dBFS: `00` is 0 dBFS, `50` is -20 dBFS, and `f0`
is the floor, silence. A reduction byte is 4 times dB: `18` is 6 dB.

Checked on the desk (2026-11-10, firmware 1.0.14):

- A 1 kHz sine at -20 dBFS on a pair reads `50` on that pair's two
  channels, and only the mixes it plays in move with it. The level is the
  sine's peak, not its average (-23 dBFS).
- Levels rise within one read (about 40 ms), hold nothing, and fall in a
  straight line at about 34 dB a second down to the floor.
- With the unit ducking at a depth of 24 dB, each ducked entry reads `60`
  (24 dB), and the pair's level after its ducker sits 24 dB under its level
  as it arrives. The reduction rises within about 60 ms and falls back
  with the configured release.

### What OpenXLR does with it

While the unit runs ducking and holds the setup, and a client takes the
meters, the device loop reads the block once per poll and reports the
largest reduction across the duckers of the inputs and pairs it ducks as
the ducking reduction meter ([api.md](api.md#ducking)). Diagnostics decode
the whole block. The maximizers, expanders, compressors and Voice Tune
bytes are read for diagnostics only.

### Open

- Which input each maximizer, expander and compressor belongs to.
