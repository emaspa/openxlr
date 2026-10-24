# MK.2 equalizer findings

Recorded on 2026-10-20 on one XLR Dock MK.2 and one Wave XLR MK.2. The
question was whether these devices run the four-band equalizer Wave Link
shows for their microphone, so that OpenXLR could drive it.

On firmware 1.0.15 they do. Both devices keep four EQ bands in their
settings block, block `0x0004`, and apply them once a master enable bit,
byte 2 bit 0, is set. Wave Link 3.5 sets that bit and writes the bands to
the device on every EQ change. On the dock's earlier firmware 1.0.0 Wave
Link ran the dock's equalizer on the computer and sent nothing to the
device.

The first version of this page, written the same morning, concluded that
neither device applies its stored bands. Every Linux run behind that
conclusion had byte 2 at `00`, so the master switch was off. Those runs
show that the bands do nothing while the switch is off, and they stay
below as that. Afternoon captures with the Wave XLR MK.2, and with the
dock after Wave Link updated it to 1.0.15, found the switch.

The other onboard processing is real too. Low cut, compressor, expander,
voice tune and ClipGuard act on the audio when their bits are written, as
[hardware support](hardware-support.md) already records.

A measurement from Linux the same evening, on the dock after its update
and with the switch on, confirmed it objectively (see
[measured from Linux, switch on](#measured-from-linux-switch-on)). All six
filter types follow the RBJ cookbook curves OpenXLR's software EQ uses, to
within 1 dB with the same frequency, gain and Q.

Not verified yet:

- whether the equalizer also acts on the zero-latency headphone path (the
  Linux run points that way but does not settle it);
- the Wave XLR MK.2 measured from Linux with the switch on (it was
  verified by ear on Windows);
- whether the bands and the switch survive a cold power cycle;
- bits 4 and 5 of each band's flags byte (bit 5 changed nothing
  measurable on one band).

## The units and the tools

| Item | XLR Dock MK.2 | Wave XLR MK.2 |
|---|---|---|
| Device | `0fd9:00c7`, `bcdDevice 2.10` | `0fd9:00b6`, `bcdDevice 4.00` |
| Firmware | 1.0.0 (build `2025-08-15-06:31-AG-Release`) until 15:56, then 1.0.15, installed by Wave Link; `bcdDevice` did not change | 1.0.15 (build `2026-02-16-09:46-ZP-Release`) |
| Control bank | `0x0103` on 1.0.0, `0x0203` on 1.0.15 | `0x0203`; every block stalls at `0x0103` |
| `c1 00 0000 0003` answers | `01 0d` on 1.0.0 | `02 05` |

| Item | Value |
|---|---|
| Windows side | Windows 11 in a virtual machine with the device passed through over USB, Wave Link 3.5.0.4892, Wireshark 4.6.8 and USBPcap 1.5.4.0 |
| Linux side | the device back on the host, PipeWire, control transfers through libusb, pink noise and recording with `pw-play` and `pw-record`, spectra with NumPy |
| Microphone | Elgato Wave DX (dynamic) on both devices |

## Blocks and their lengths

Block lengths as Wave Link requests them:

| Block | Bytes | What is known |
|---|---|---|
| `0x0000` | 117 | identity. Bytes 5, 6 and 7 are the firmware version as major, minor and patch: `01 00 00` on the dock before its update, `01 00 0f` on the Wave XLR MK.2, both as Wave Link reports them. A hash in ASCII at 12 and 27, the serial at 35 and the build string at 53 |
| `0x0001` | 6 | crossfade at byte 0 |
| `0x0002` | 21 | not decoded; changes between reads when Wave Link is closed, perhaps meters |
| `0x0004` | 38 | input settings, including the equalizer below |
| `0x0005` | 2 | headphones |
| `0x0006` | 17 | not decoded. Byte 15 changes between reads with nothing touched. Bytes 10 to 13 read `80 bb 00 00` (48000, perhaps the sample rate). On the updated dock byte 14 read `05`, `04` and `00` while the firmware held the input muted after phantom power was switched off |
| `0x0016` | 51 on the dock's firmware 1.0.0, 61 on 1.0.15 (both devices) | not decoded |

The firmware answers whatever length is asked for, so these are the
lengths Wave Link uses, not proven block sizes.

## The equalizer in block 0x0004

Byte 2 bit 0 switches the whole equalizer on and off. On the same byte,
bit 2 set means ClipGuard off, and OpenXLR's ClipGuard write changes only
that bit.

Bytes 18 to 37 hold four bands of five bytes each, at 18, 23, 28 and 33.
The layout is the same on both devices:

| Byte in the band | Field | Encoding |
|---|---|---|
| 0 | flags | bit 0 enables the band; bits 1 to 3 are the filter type; bits 4 and 5 are unknown |
| 1 | gain | signed 8-bit, quarter dB, two's complement (`30` is +12 dB, `ff` is -0.25 dB) |
| 2 and 3 | frequency | unsigned 16-bit little endian, Hz |
| 4 | Q | signed 8-bit, round(log10(Q) × 64), so `f6` (-10) is Q 0.70 |

The filter type uses Wave Link's own numbers: 0 high pass, 1 low shelf,
2 bell (Wave Link calls it "Band Pass"), 3 notch, 4 high shelf, 5 low
pass. Wave Link's ranges give gain bytes from -48 to +48 (-12 to +12 dB),
frequencies from 20 to 20000 Hz and Q bytes from -64 to +64 (Q 0.1 to 10).

Gain and frequency fit every write Wave Link made while a band was being
dragged, 23 on the Wave XLR MK.2 and 33 on the updated dock, each against
the value Wave Link logged just before it. The Q encoding fits the four
logged Q factors on each device and Wave Link's launch write of band 2's
Q. The flags byte is read from saved state only. Bit 0 and bits 1 to 3
fit all eight bands of the two devices against Wave Link's saved
settings, but no capture yet shows Wave Link changing a band's type or
enable. Bits 4 and 5 read `0x30` and `0x20` on the dock before its update
and `0x20` in band 4 of the Wave XLR MK.2.

As an example, band 3 of the Wave XLR MK.2 after capture 11 read
`05 30 bf 02 f2`: enabled, bell, +12 dB, 703 Hz, Q 0.60.

## The hardware test on the Wave XLR MK.2

Captures 11 to 13. Everything was heard through Windows' "Listen to this
device" on the Wave XLR MK.2's microphone input, so the listener heard
the device's USB recording stream.

1. With the EQ on, band 3 was set to +12 dB at 703 Hz in Wave Link, and
   the boost was audible.
2. Wave Link quit (capture 11). It wrote nothing at quit, and the boost
   stayed with Wave Link closed.
3. A single read from the virtual machine (capture 12) returned the block
   as Wave Link had left it: byte 2 `01`, band 3 `05 30 bf 02 f2`.
4. Wave Link was started again and its EQ switched off (capture 13). The
   one write changed byte 2 from `01` to `00` and nothing else. Wave Link
   quit, and with it closed the boost was gone.

Nine `Eq_Enabled` lines in Wave Link's log, across captures 10 and 13,
each map to a write of byte 2 bit 0 with nothing else changed.

## How Wave Link drives the equalizer on firmware 1.0.15

The same on the Wave XLR MK.2 and on the updated dock:

- Every EQ change is a write of the whole 38-byte block,
  `41 01 0004 0203`, sent 50 to 100 ms after Wave Link logs the matching
  `Set DeviceParameter` line (`Eq_Enabled`, `BandN_GainDb`,
  `BandN_FrequencyHz`). A drag sends about ten a second. There is no
  commit: the next read returns the written bytes.
- Wave Link reads every block about ten times a second for as long as it
  runs.
- At launch it writes the block once, setting byte 1 bit 3 and byte 27,
  band 2's Q, to the logged `Band2_QFactor`. What byte 1 bit 3 does is not
  mapped; it follows the `IsVSTEnabled` line in the log.
- It does not clear the equalizer when it quits. A unit last driven by
  Wave Link 3.5 can reach Linux with its hardware EQ on.

Wave Link's plugin graph still lists WaveEQ in its "Wave XLR MKII Host"
chain on 1.0.15, marked bypassed in a capture taken with the EQ off.

## The dock's update to firmware 1.0.15

Wave Link had offered 1.0.15 for the dock since the morning. The attempt
at 15:53 failed. The dock left its application mode and came back as a
bootloader with product id `0x00c8`, which the virtual machine did not
pass through. The retry with that device passed through finished at
15:56.

After the update:

- the dock answers at bank `0x0203`. Capture 14 has no request to
  `0x0103`; read from Linux afterwards, every block at `0x0103` stalls;
- block `0x0016` is 61 bytes, as on the Wave XLR MK.2;
- Wave Link polls it and writes its EQ exactly as for the Wave XLR MK.2;
  all 33 writes in capture 14 fit the layout above;
- block `0x0004` was reset. Bytes 2 to 37 equal the Wave XLR MK.2's block
  before any of our changes, bands at 60, 227, 860 and 10237 Hz, and the
  dock's earlier bands were gone. Byte 0 (gain) went from `50` to `1e`;
- byte 1 bit 1 was set, so phantom power came on. Wave Link showed it on.
  Neither Wave Link nor our tools had written it.

Switching phantom power off in Wave Link (capture 15) wrote byte 1 bit 1
clear and nothing else. About 100 ms later the dock reported byte 1
bit 0, mute, set by its own firmware. It cleared that bit itself 12.5 s
later. Block `0x0006` byte 14 read `05`, `04` and `00` through that time.

The morning's difference between the two devices came from the firmware
rather than the device type.

## Measured from Linux, switch on

The same evening, on the dock at firmware 1.0.15, with the method of
[writing the bands from Linux](#writing-the-bands-from-linux-switch-off):
pink noise at -12 dBFS from the dock's headphone output into an earbud in
front of a Wave DX on its XLR input, recorded from the dock's USB input.
Processing bits off, crossfade at 200, headphone byte `20`. Band 4 was the
only band enabled and byte 2 was `01` for every step except the
references. Each result is the change against a reference with the
switch off, in 1/6 octave bands, compared with the RBJ cookbook curve for
the same values averaged the same way. The room limited the comparison
to 2.2 to 13.7 kHz; two references taken minutes apart agreed within
0.4 dB up to 11 kHz.

| Band 4 (type, frequency, gain, Q byte) | Largest difference from the cookbook curve |
|---|---|
| switch on, every band disabled | none beyond the references' spread |
| bell, 5 kHz, +12 dB (`30`), `f6` (Q 0.70) | 0.5 dB |
| bell, 5 kHz, -12 dB (`d0`), `f6` | 1.4 dB |
| bell, 5 kHz, +12 dB, `13` (Q 1.98) | 0.6 dB |
| bell, 5 kHz, +12 dB, `df` (Q 0.30) | 1.0 dB |
| high shelf, 5 kHz, +12 dB | 0.5 dB |
| high shelf, 5 kHz, -12 dB | 1.7 dB |
| low shelf, 5 kHz, +12 dB | 1.0 dB |
| low shelf, 5 kHz, -12 dB | 0.6 dB |
| low pass, 5 kHz, `f6` | 2.4 dB, at 13.7 kHz where the cut nears the room's level |
| notch, 6 kHz, `f6` | 0.9 dB, -25 dB measured at the 6.1 kHz band against -24 dB |
| high pass, 8 kHz, `f6` | 1.1 dB |
| bell +12 dB with the band's bit 5 clear, and with byte 1 bit 3 clear | within 0.3 dB of the same bell with them set |
| bell +12 dB with byte 2 set back to `00` | within 0.2 dB of the reference |

So the type numbers in the flags byte are as Wave Link's model numbers
them, gain is quarter dB, Q is `10^(byte / 64)`, and the filters are the
cookbook's. Byte 1 bit 3, which Wave Link sets at launch, is not needed.

The first run used 55 dB of gain. With crossfade 200 a part of the
microphone still reached the earbud. Every boost raised that loop's gain,
and the broad ones (the Q 0.30 bell and the +12 dB low shelf) made it
ring near 3 kHz and clip, while the cuts measured as in the table. The
boosts in the table come from a second run at 45 dB of gain, which stayed
stable. That a boost changed the acoustic loop suggests the equalizer
also acts on the dock's zero-latency path, but the crossfade's behaviour
on 1.0.15 was not mapped and this is not settled.

## How Wave Link drove the dock on firmware 1.0.0

This section and the next three were captured with the dock on firmware
1.0.0, before its update, one action per capture, with Wave Link's own
log on the same clock.

- At connect Wave Link reads blocks `0x0000`, `0x0001`, `0x0002`,
  `0x0004`, `0x0005`, `0x0006` and `0x0016` every 100 ms for about 2.7 s,
  plus two reads addressed to interface 3 itself (`c1 00 0000 0003`, two
  bytes, and `c1 02 0000 0003`, four bytes, once). After that it sends no
  control transfer at all until it quits.
- About 3 s after its first read it selects alternate setting 1 of
  interface 3, the vendor interface. That setting has an interrupt IN
  endpoint `0x84` of 204 bytes and an interrupt OUT endpoint `0x02` of
  404 bytes. While Wave Link runs, `0x84` carries about 500 transfers a
  second of 816 bytes and `0x02` about 480 a second of 1615 bytes, nearly
  all different. The payloads look like sample data in both directions.
  It selects setting 0 again when it quits.
- Wave Link's log and plugin graph put the dock's input in a mode called
  "Wave XLR MKII Host". The input gets Wave Link's own WaveEQ,
  WaveCompressor, WaveExpander and VoiceFocus plugins as nodes in its
  plugin host, and the log reports software mixing
  (`Hardware mixing state changed to: False`). In its binaries, hardware
  mixing and settings sync to the device are named for the Wave XLR Pro
  only.

## What Wave Link records about its own mode

With the dock on firmware 1.0.0, Wave Link 3.5.0.4892 (Store package
`Elgato.WaveLink_g54w8ztgkx496`) recorded in its own files that it
processed the dock's EQ on the computer. Paths below are under
`%LOCALAPPDATA%\Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState`
unless they name the install folder.

### The log

`Logs\ElgatoWaveLink_<date-time>.log`, one file per launch.

- `ProSoftwareHardwareMixerTransitionHelper` writes the hardware mixing
  state on every start:

  ```
  CheckForXLRProInitialization: XLRProConnectionState=NoneConnected, XLRProInitState=NotStarted, IsHardwareMixingEnabled=False.
  Hardware mixing state changed to: False.
  ```

  The flag is `IsHardwareMixingEnabled`, and the state around it is named
  for the Wave XLR Pro only (`XLRProConnectionState`, `XLRProInitState`).
  Nothing comparable is logged for the MK.2 models.
- `EWLWLegacyThesyconBackend::open` opens Wave Link's virtual device
  (USB product id `0x96`) with `Use device with PID: 0x96 and handle: ...
  for software mixing`.
- `PluginHost` builds a plugin rack for the dock's input and names it:

  ```
  LEWITT plugin rack initialized <Wave XLR MKII Host>
  Added default plugin: <PCM_IN_01_C_00_SD1 - WaveEQ>, <Wave XLR MKII Host>
  ```

  The input `PCM_IN_01_C_00_SD1` gets ElgatoSampleRecorder, VoiceFocus,
  WaveCompressor, WaveEQ and WaveExpander as plugins on the computer.
- `DeviceSupportService` logs every parameter it hands to the device
  layer as `Set DeviceParameter: <name> to: <value>`. Through the dock's
  1.0.0 session it named only `Band1_QFactor` to `Band4_QFactor`,
  `Comp_ThresholdDb`, `Exp_Ratio`, `HWOutputsSourceSelect`,
  `USBOutputsSourceSelect`, `IsSampleRecorderOn` and `IsVSTEnabled`, all
  at connect. None of them produced a vendor write on the wire, and no
  band gain, frequency, type or enable was ever handed down. On firmware
  1.0.15 the same service logs `Eq_Enabled` and the band values, and each
  becomes a write.

### The plugin graph

`AudioPluginCache\graph_visualization.md` (the same data in
`graph_visualization.json`) is the host's own description of its signal
flow:

```
# EWLWPluginHost - Audio Signal Flow
**Host Mode:** Wave XLR MKII Host
Audio Input -> ElgatoSampleRecorder -> ElgatoAiCoustics -> WaveCompressor -> WaveEQ -> WaveExpander -> Audio Output
```

Wave Link uses the same host mode for the Wave XLR MK.2 on 1.0.15, where
it also writes the EQ to the device, so the mode name alone does not say
where the EQ runs.

### The program files

In the install folder
(`C:\Program Files\WindowsApps\Elgato.WaveLink_3.5.0.4892_x64__g54w8ztgkx496`),
read with `strings` and `objdump` from a copy; nothing was run.

- `EWLWPluginManager.dll` is the JUCE plugin host with WaveEQ,
  WaveCompressor and WaveExpander built in. WaveEQ has a global
  `enabled` and four bands of `Band N Filter Enabled`, `Type`,
  `Frequency` (20 Hz to 20 kHz, 1 Hz steps), `Gain` (-12 to +12 dB,
  0.25 dB steps) and `Quality` (log10 of Q, -1 to +1, steps of 0.016).
  The six types are 0 high pass, 1 low shelf, 2 bell (named "Band Pass"),
  3 notch, 4 high shelf and 5 low pass.
- `waveapi.dll`, the device API, describes each model's parameter tree.
  EQ trees appear at the root, under `/mic/`, and once per XLR input of
  the Wave XLR Pro under `/xlr1/` and `/xlr2/`. Each has
  `equalizer_enabled` and, for bands 0 to 3, `type`, `enabled`, `gain_dB`,
  `frequency_Hz` and `quality_log10`; the band type enum is
  `ven_signal_dsp_fx_band_type_e`. The same file lists signal taps per
  input (`XLR1_PREFX`, `XLR1_POSTDSP`, `XLR1_POSTVST`) and an output source
  select "0 - XLR pre 1 - XLR post 2 - XLR VST 3 - Monitor Mix", which is
  what `HWOutputsSourceSelect` and `USBOutputsSourceSelect` set. Which
  models own the root and `/mic/` trees is not established. The MK.2
  models' block `0x0004` EQ, with a master enable and the same five
  fields per band, is a likely match.
- `Elgato.WaveLink.AppLogic.dll` names the same fields `Eq_Enabled`,
  `BandN_Type`, `BandN_IsEnabled`, `BandN_FrequencyHz`, `BandN_GainDb` and
  `BandN_QFactor` ("Band index must be between 1 and 4"). Hardware mixing
  and settings sync to the device are named for the Wave XLR Pro only.

### The local API

Wave Link 3's local JSON-RPC API, which Stream Deck style plugins use,
separates software `effects` (such as `"eq"`) from device `dspEffects`
(such as `"clipguard"`) and reports each only as
`{id, name, isEnabled, isSupported}`
([WaveLink.Client protocol notes](https://github.com/Agash/WaveLink.Client/blob/main/PROTOCOL.md)).
The EQ is in the software list.

## What Wave Link sent when the EQ changed on firmware 1.0.0

| Capture | Action in Wave Link | Vendor writes | Block `0x0004` afterwards |
|---|---|---|---|
| 01 | moved band 1 of the dock input's EQ for about 50 s | none | unchanged |
| 02 | toggled the dock input's EQ off and on, twice | none | unchanged |
| 03 | quit and relaunched Wave Link with the EQ on (41, 310, 2172 and 15049 Hz) | none | byte for byte as in the connect capture |
| 04 | quit and relaunched with the EQ at 130, 792, 3193 and 6238 Hz, ±9.25 dB, then enabled the EQ by hand | none | byte for byte as in the connect capture |

No `Set DeviceParameter` line appeared in Wave Link's log for any of these
actions, and nothing in the `0x02` stream behaved like a parameter: 815
of its byte offsets were constant zero padding and the other 800 changed
on nearly every transfer.

## The EQ values the dock held on firmware 1.0.0

Byte 2 read `00`, so the master switch was off. The four bands, read with
the layout above:

| Bytes | Band | Stored value | Reading |
|---|---|---|---|
| 18 to 22 | 1 | `32 00 64 00 f6` | off, low shelf, 0 dB, 100 Hz, Q 0.70 |
| 23 to 27 | 2 | `34 00 f4 01 f6` | off, bell, 0 dB, 500 Hz, Q 0.70 |
| 28 to 32 | 3 | `34 00 cc 07 f6` | off, bell, 0 dB, 1996 Hz, Q 0.70 |
| 33 to 37 | 4 | `28 00 e9 13 f6` | off, high shelf, 0 dB, 5097 Hz, Q 0.70 |

The frequencies and types match an EQ state Wave Link held earlier the
same morning, so something wrote Wave Link's EQ into the dock at some
point. None of the captures shows Wave Link 3.5 doing it on 1.0.0, and
the values stayed the same through every capture until the firmware
update erased them.

## Writing the bands from Linux, switch off

Every run in this section and the next two left byte 2 at `00`, so the
equalizer's master switch was off throughout. They show that the stored
bands do nothing while the switch is off. They say nothing about the
bands with the switch on.

The dock was on firmware 1.0.0 for these runs. Whether 1.0.0 applies the
bands with the switch on was never tested, and this unit can no longer
test it since its update.

Wave Link on 1.0.0 never wrote these bytes, so the test was to write them
ourselves and measure. Every write was a read-modify-write of the
38-byte block at bank `0x0103`, read back after writing, and the original
bytes went back at the end of each run.

The signal path: pink noise from the dock's headphone output into an
earbud taped about 1 cm in front of the microphone, recorded from the
dock's input. Both streams were created with `node.autoconnect = false`
and linked by hand, because OpenXLR's application routing otherwise moves
an untargeted stream into its own channels. For the runs the dock's
compressor, expander, voice tune, low cut and ClipGuard were off, its
gain was 55 dB and phantom power was off. Each value was compared with a
flat run taken just before it, so slow drift in the room cancels.

Above 3 kHz the earbud was about 15 dB above the room and two identical
runs agreed within about 0.1 dB. Below 3 kHz the room was as loud as the
earbud, so results there are not evidence either way.

| Write (block `0x0004`) | Measured effect, 3.8 to 9.7 kHz |
|---|---|
| band 4 gain (byte 34) `0x18`, processing bits off | within ±0.1 dB |
| band 4 gain `0x18`, compressor, expander, voice tune and low cut bits on | within ±0.2 dB |
| band 4 flags (byte 33) with bit `0x10` set, gain `0x18` | within ±0.2 dB |
| band 4 flags `0xff` | within ±0.3 dB |
| band 3 moved to 5000 Hz (bytes 30 and 31) with gain (byte 29) `0x18` | within ±0.4 dB |
| band 4 gain `0x18` with Q (byte 37) `0x40` | up to 1 dB, not consistent across frequency |

Most of these runs also left the band's own enable bit, flags bit 0,
clear. The run with flags `0xff` set it, with the master switch still
off.

A screen inverted each of bytes 3 to 17 in turn with band 4 boosted.
Inverting byte 11 (`ff` to `00`) showed one rise of 6.8 dB around 4.8 kHz,
but a retest of byte 11 values (`00`, `f7`, `ef`, `fe`, `7f`, `f0`, `0f`)
with band 4 boosted did not reproduce it. The room had become louder by
then, so the retest's repeatability was only 2 to 5.6 dB. The retest did
not set the flags byte at the same time as the screen did; that
combination was covered by the listening test below. Bytes 4, 8, 9, 10,
12, 13, 14, 15 and 17 changed the overall level by 5 to 19 dB, so they
are live parameters, most likely for the compressor or expander. They are
not mapped.

Earlier, from the Windows side with Wave Link closed, writing the low cut
bit made rumble audible on the dock's USB capture and setting it again
removed it, which confirms that writes to this block reach the DSP. A
band 3 gain of `0x0c` (+3 dB) was heard as brighter in that session. Byte
2 was `00` and band 3's enable bit was clear (`34`), so by the layout
above that write should have changed nothing. Band 3 sat at 1996 Hz,
below the 3 kHz where the Linux measurement could see anything. The
report stays unexplained: firmware 1.0.0 may ignore the switches, or the
ear test misled.

## Listening on the zero-latency path, switch off

The equalizer might act only on the dock's own headphone monitor, which
the USB recording cannot show. With the crossfade moved fully to the
microphone and the headphone level at its usual -20 dB, a timed sequence
switched every 5 s while the listener made a steady "ssss" into the
microphone: flat, band 4 moderate (byte 11 `00`, byte 33 `0x38`, byte 34
`0x0c`), flat, the same again, flat, band 4 large (byte 34 `0x18`), flat,
band 3 large (byte 11 `00`, byte 29 `0x18`). The listener heard no
difference at any step. Byte 2 was `00` and band 4's enable bit was clear
throughout.

## The Wave XLR MK.2 from Linux, switch off

Measured from Linux on the same day, before the afternoon captures, on
firmware 1.0.15. Its blocks `0x0000`, `0x0001`, `0x0002`, `0x0004`,
`0x0005`, `0x0006` and `0x0016` answer at the dock's lengths, except
`0x0016`, which Wave Link reads as 61 bytes.

This unit held an EQ that is not flat, with every band enabled and byte 2
at `00`:

| Band | Flags | Gain byte | Frequency | Q byte | Reading |
|---|---|---|---|---|---|
| 1 | `01` | `00` | 60 Hz | `f6` | on, high pass, Q 0.70 |
| 2 | `03` | `0d` | 227 Hz | `00` | on, low shelf, +3.25 dB, Q 1.0 |
| 3 | `05` | `f4` | 860 Hz | `f2` | on, bell, -3 dB, Q 0.60 |
| 4 | `25` | `07` | 10237 Hz | `e8` | on, bell, +1.75 dB, Q 0.42 |

Byte 11 reads `f4` here and `ff` on the dock before its update.

The measurement used the same method as for the dock: pink noise from the
MK.2's headphone output into an earbud taped in front of a Wave DX on
its XLR input, processing off, gain 55 dB, the crossfade on the PC side
and the headphone level raised for the run. From 3.4 to 10 kHz the
earbud was 30 to 35 dB above the room and three repeats of each
comparison agreed within 0.1 dB. Below 1.7 kHz the room was within
10 dB of the earbud and the comparisons there are noise.

| Comparison (block `0x0004`) | Measured effect |
|---|---|
| stored EQ against all four gains (bytes 19, 24, 29, 34) at zero | -0.02 dB at 10237 Hz, where band 4 stores `07` |
| band 4 moved to 5000 Hz with gain `0x18`, against the same at gain 0 | 0.00 dB at 5000 Hz |
| the same with byte 11 at `00` | -0.01 dB at 5000 Hz |
| the same with byte 11 at `ff` | +0.01 dB at 5000 Hz |

On the zero-latency path the listener set gain, crossfade and headphone
level on the device until they heard themselves clearly (gain 47 dB,
crossfade 100, headphone byte `2d`). A timed sequence then changed only
the EQ bytes every 5 s: stored EQ, gains zeroed, stored, zeroed, band 4
at 5 kHz with gain `0x18` and byte 11 `00`, zeroed, the same boost,
stored. The listener heard no difference. With the switch off, the
stored +3.25 dB shelf at 227 Hz and -3 dB bell at 860 Hz did nothing.

## What this means for OpenXLR

- On firmware 1.0.15 the MK.2 family has a hardware equalizer, and
  OpenXLR drives it: the layout is known, a write applies at once, and the
  dock's filters match OpenXLR's software EQ for the same values. The
  daemon's own writes were tested on hardware and work. A microphone
  that carries such a unit's input runs its EQ there, and the unit's
  `hardwareEq` capability is true; see
  [where the EQ runs](api.md#where-the-eq-runs).
- OpenXLR's four-band [microphone EQ](api.md#microphone-eq) runs in
  software for every device and firmware without a hardware one, which
  includes the dock on firmware 1.0.0 unless the switch turns out to work
  there.
- Wave Link 3.5 leaves the hardware EQ on when it quits. When such a unit
  connects, OpenXLR writes the microphone's own EQ over it, or switches it
  off on an input whose EQ runs in software. Byte 2 of the settings block
  in a diagnostics dump shows the switch.
- The dock's control bank depends on its firmware: `0x0103` on 1.0.0 and
  `0x0203` on 1.0.15. OpenXLR's dock backend tries `0x0103` first and
  `0x0203` when a block stalls or answers short. On the updated dock
  `0x0103` stalls and `0x0203` answers every block, as that fallback
  expects, and the daemon drives the updated dock.
- The Wave XLR Pro runs its equalizer on the device too, in this layout
  once per input: Wave Link 3.5 was captured writing it on firmware 1.0.14
  on 2026-10-21, XLR 1's struct at block offset 0 and XLR 2's at 38, with
  no commit (see the
  [protocol notes, section 10](wave-xlr-pro-protocol.md#10-captures-16-to-20-2026-10-21-the-microphone-eq-in-block-0x0004)).
  OpenXLR drives it there. One bell was measured on the desk: +12 dB at
  5 kHz read +12.3 to +12.7 dB on the raw USB capture, and software mode
  left it flat; the other filter types are not measured yet.
- Still open, beyond the list at the top: bytes 3 to 17 of block
  `0x0004`, byte 1 bit 3, and the dock's 1.0.0 report of band 3 sounding
  brighter with both switches off.
