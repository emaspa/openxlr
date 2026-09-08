# Capturing USB traffic for testers

This guide is for owners of the original Wave XLR (`0fd9:007d`) who want
to help map the rest of its protocol. No programming needed, just
Wireshark and about 15 minutes.

The remaining targets are low cut, ClipGuard, mic/PC crossfade and the
hardware-save action exposed by Wave Link. Their OpenXLR commands are not
yet mapped; the [support table](hardware-support.md#wave-xlr-0fd9007d)
tracks current status. Capture these device controls separately from
software plugin effects.
Phantom power is already coded (the
[openwave](https://github.com/rikkichy/openwave) project found its
byte); the phantom toggles in your capture serve as confirmation, and
the front 48V LED makes them the easiest events to line up.

## What you need

- A Windows PC with [Wave Link](https://www.elgato.com/downloads)
  installed and the Wave XLR working normally. This guide covers
  Windows only.
- [Wireshark](https://www.wireshark.org/download.html). During
  installation, tick the **USBPcap** component when the installer asks.
  It is off by default. Reboot after installing, the capture driver
  needs it.

## Before you start

- Plug the Wave XLR directly into the PC, not through a hub, ideally on
  a port away from your keyboard and mouse.
- Privacy: USBPcap records every device on the same USB controller,
  which can include keystrokes from a keyboard. Keep the capture short
  and do not type passwords while it runs. Filtering the file to the
  Wave XLR's device address (last section) removes the rest.

## The capture

1. Open Wireshark. You will see interfaces named `USBPcap1`,
   `USBPcap2`, and so on. Click the small gear next to each one: a
   window lists the devices attached to that controller. Pick the one
   showing the Elgato Wave XLR. If you cannot tell, capturing on all of
   them works too.
2. Double click the interface to start capturing. Packets will scroll
   by, that is normal.
3. Unplug the Wave XLR, wait 3 seconds, plug it back in. This puts the
   device's introduction handshake into the capture and lets us find it
   among the other traffic.
4. Wait for Wave Link to see the device again, then do nothing for 10
   seconds.
5. In Wave Link, turn phantom power **on**. Check the 48V LED lights.
   Wait 5 seconds.
6. Turn phantom power **off**. LED goes dark. Wait 5 seconds.
7. Repeat the on/off pair two more times, three rounds total. The
   repetition is what lets us tell the phantom packet apart from
   background noise.
8. In Wireshark press the red stop button, then File, Save As, and save
   as a `.pcapng` file.

## Optional round two, everything else

For the remaining controls, start a second capture (replug the
device again, step 3 above) and work through this list, one action at a
time with 5 second pauses between them, in this order:

1. Gain: turn the dial to minimum, pause, then to maximum, pause, then
   back to a middle value you note down (the device shows it).
2. Mute on, pause, mute off.
3. Low cut on, pause, off.
4. ClipGuard on, pause, off.
5. Headphone volume: minimum, pause, maximum, pause, middle.
6. Mic/PC balance: sweep it fully one way, pause, fully back.
7. Save settings to hardware, if your Wave Link version offers it. Note
   the gain before saving, then replug and record what was retained.

Jot down the order you actually did things in and roughly when. The
notes matter as much as the capture.

## Sending it in

Open an [issue](https://github.com/emaspa/openxlr/issues) titled "Wave
XLR MK.1 USB capture" and attach:

- the `.pcapng` file or files, zipped (GitHub does not accept raw
  pcapng)
- your Wave Link version and the firmware version it shows for the
  device
- your notes on what you toggled and when

## Checking your own capture (optional)

In Wireshark, type
`usb.idProduct == 0x007d` into the filter bar and press enter. You
should see a packet from the replug; its "Device address" field, say 5,
identifies your Wave XLR on that USB bus. Filter by both `usb.bus_id`
and `usb.device_address` if the file contains several buses. Recheck the
address after a replug; it may change. Use File, Export Specified Packets
and select displayed packets to create a device-only file to share. A
display filter alone does not remove other devices from the saved capture.
Toggling phantom should produce a small burst at each recorded action.
