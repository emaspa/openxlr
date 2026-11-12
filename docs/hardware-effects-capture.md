# Capturing unmapped hardware effects

OpenXLR already controls the hardware effects listed in
[hardware support](hardware-support.md). This guide collects the evidence
needed for the remaining onboard controls. It does not add those controls
or change a device. Software LV2, CLAP and VST3 inserts are a separate path.

## Pick the device and effect

| Device | USB product id | Unmapped effects to capture |
|---|---|---|
| Wave XLR Pro | `00b4` | The channel booster. For ducking only USB Aux or Line In as a lowered channel, which no capture shows yet; the keys and the mix maximizer are mapped. For the EQ only a change of band type, band enable or Q in Wave Link; its layout, switch and band gain and frequency are mapped on firmware 1.0.14, see [hardware support](hardware-support.md#wave-xlr-pro-0fd900b4) |
| Wave XLR MK.2 | `00b6` | For the EQ only a change of band type or band enable in Wave Link; the rest of its layout and its master enable are mapped on firmware 1.0.15, see the [findings](mk2-eq-findings.md) |
| Wave:3 MK.2 | `00ca` | A capture of the expander and ClipGuard switches in Wave Link, to confirm the XLR MK.2 offsets they are driven at; the low cut, compressor, voice tune and EQ are measured, see [hardware support](hardware-support.md#wave3-mk2-0fd900ca) |
| XLR Dock MK.2 | `00c7` | As for the Wave XLR MK.2: on firmware 1.0.15 the dock takes the same EQ layout. On firmware 1.0.0 Wave Link runs the EQ on the computer. See the [findings](mk2-eq-findings.md) |

The original Wave XLR, first XLR Dock, Wave:3 and Wave:1 do not gain these controls
by sharing the USB vendor id. Use their existing controls and host-side
inserts. Do not copy a Pro command to another model: even the MK.2 models
can select different property banks on different units.

The [Pro feature guide](https://www.elgato.com/us/en/explorer/products/wave/wave-xlr-pro-give-your-setup-superpowers/)
describes the device's four-band EQ, per-mix ducking and maximizer, and
per-channel booster. Capture the device's effect in Wave Link, not a VST
with a similar name. Record which input, channel or mix you are editing.

## Record one control at a time

Use Windows, Wave Link and Wireshark with USBPcap as described in the
[USB capture guide](usb-capture.md#what-you-need). Keep listening levels
low when changing EQ gain or a booster. Do not change phantom
power, microphone gain or output routing during an effect capture.

1. Note the exact device model, USB product id, firmware and Wave Link
   version. Start the USB capture, reconnect the device, wait until Wave
   Link recognizes it, then leave it idle for ten seconds. The handshake
   and idle traffic help separate configuration from counters.
2. Record the initial values of the effect and the selected input or mix.
   Keep all other controls unchanged. Save a screenshot or a text table of
   these values.
3. Toggle only this effect on, wait five seconds, then off and wait again.
   Repeat the pair three times. Note the times and the final state.
4. With the effect enabled, change just one parameter between at least
   three explicit values within the UI's allowed range. Record the actual
   displayed numbers and units, wait five seconds between changes, and
   return to the original value. Repeat this sequence once. An endpoint
   pair alone cannot identify a value's scale or byte order.
5. Stop and save this capture before changing a different parameter.
   Restore the original settings in Wave Link afterwards.

For EQ, capture each band's enable or filter type, frequency, gain and
width or Q separately, using only controls the device actually exposes.
Keep the other bands unchanged. Include negative, zero and positive gain
values. Give the band index and the frequency and width units.

For the Pro's ducking with USB Aux or Line In lowered, change only that
channel's ducking and name the key and the mix; repeat it on a second mix
with the first left unchanged. For the booster, use a second input channel
to establish its scope. Do not assume that an effect's master enable and
its per-mix or per-channel enable are the same field.

## Share only the intended device

USBPcap can record other devices on the controller, including a keyboard.
Do not type passwords during a capture. A display filter does not remove
those packets from a saved file.

Find the device's enumeration packet by vendor `0fd9` and its product id.
Note both the USB bus and device address, then filter by both. Check the
address again after every reconnect. Export the displayed packets through
**File, Export Specified Packets** to a new file. Reopen that exported file
and check that it contains the intended device before sharing it.

Open a GitHub issue with the model and effect in its title. Attach the
device-only `.pcapng` in a zip, the versions, initial values, action times,
selected input or mix, and the exact parameter values in order. A capture
from a second unit or firmware revision is especially useful.

## Before a control can ship

The capture must establish the request type, block, bank, offset, encoding,
range and commit sequence. A changed byte alone could be a counter or a
checksum. Unknown bytes must survive a read-modify-write unchanged, and a
command must be gated on the device whose protocol was verified.

An owner then verifies readback, the audible result, isolation from the
other input or mix, restoration on reconnect and profile recall on the
actual device. Record that verification in hardware support and the model's
protocol notes. A fake USB transport can test bounds and byte preservation;
it cannot establish that an effect works on hardware.
