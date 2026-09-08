# Wave XLR Pro USB protocol capture plan

Status: completed historical procedure. The ordered follow-up capture
resolved the input and output-selector controls described here; use
[wave-xlr-pro-protocol.md, section 6](wave-xlr-pro-protocol.md#6-capture-2-xlrpro2pcapng-2026-08-25-with-ordered-action-log-outputs-mic-dsp-usb-aux)
for current offsets. The steps below are retained as the methodology for
capturing another device, not as the current protocol specification.
Hardware EQ and the remaining mix matrix still need separate captures;
see [hardware support](hardware-support.md) and the [roadmap](roadmap.md#devices).

Goal: capture every USB control transfer Wave Link sends to the Wave XLR Pro so its vendor
protocol can be reimplemented in a Linux backend. The device has
**no physical or LED controls**, everything is software-driven, so this is a write-mostly
protocol: gain, mute, phantom, low-cut, ClipGuard, headphone volume/impedance, monitor blend.

The single rule that makes decoding possible: **one action at a time, distinct memorable
values, a timestamped note for every action, one capture file per session.** A capture without
the companion log is nearly worthless, the log is what maps bytes to meaning.

---

## 0. Before you start

- Boot Windows with Wave Link installed. Confirm the Pro works there first.
- Have the Pro plugged into a port you can physically reach to unplug/replug.
- **Reduce USB noise**: during capture, do not touch any other USB audio or HID device, and
  close other audio applications (camera software, Stream Deck software, OBS, Discord,
  browsers). One exception is called out in Session 2 step 23. The quieter the host, the
  easier the decoding.
- Save the files somewhere readable from Linux afterwards (a data partition, not the Windows
  system partition).

### Tools

**USBPcap + Wireshark (primary).** Install Wireshark for Windows and tick "Install USBPcap"
in the installer (or install USBPcap separately from its GitHub releases). Reboot if the
installer asks.

**Frida (fallback only).** Skip unless USBPcap output turns out to be opaque. If needed:
`pip install frida-tools` on the Windows Python, then attach to Wave Link and hook
`WinUsb_ControlTransfer` / `DeviceIoControl`. The symbol names to look for come from the
LukasParke/wave3-research teardown: `LWT::WaveXLRProDevice`, and the three vendor backend
strategies (`LegacyUAC1VendorUSBBackendStrategy`, `LegacyUAC2VendorUSBBackendStrategy`,
`MK2VendorUSBBackendStrategy`), knowing which one constructs for the Pro tells us the request
scheme. Do the USBPcap pass first regardless.

---

## 1. Identify the right capture interface

1. Open Wireshark. In the capture-interface list you'll see `USBPcapN` entries, one per USB
   root hub. You need the one the Pro is under.
2. To find it: open **Device Manager → View → Devices by connection**, expand the USB host
   controllers, and locate "Elgato Wave XLR Pro" (or its audio/vendor child nodes). Note which
   host controller / root hub it hangs off. Match that to the USBPcap interface, in Wireshark's
   capture options, hovering a `USBPcapN` interface lists the devices under it; pick the one
   listing the Elgato Wave XLR Pro. If unsure, start a quick capture on a candidate, replug the
   Pro, and see if enumeration traffic appears; if not, try the next.
3. Once identified, note it (e.g. "Pro is on USBPcap2"), use the same interface for all sessions.

### Cutting the trace down to just the Pro

USBPcap captures the whole root hub, so filter in Wireshark. Two ways:

- **After capture (recommended, lossless):** capture everything on that root hub, then apply a
  display filter. First find the device address: after enumeration, `usb.idVendor == 0x0fd9 &&
  usb.idProduct == 0x00b4` on the GET DESCRIPTOR response reveals the `usb.device_address`
  (e.g. 5). Then filter the whole session with `usb.device_address == 5`. Re-check the address
  after any replug, it can change.
- Useful display filters while reviewing:
  - Control transfers only: `usb.transfer_type == 0x02`
  - Vendor/class SETUP packets (the interesting writes): `usb.bmRequestType.type == 2` (vendor)
    or `== 1` (class). The MK.1 uses class requests; the MK.2/Pro family uses vendor requests.
    Watch both when identifying an unknown device.
  - Interrupt IN traffic (the 6-byte endpoint): `usb.transfer_type == 0x01`
  - Just the setup stage with data: `usb.setup.wLength > 0`

Keep the FULL capture (don't filter at capture time) so nothing is lost; filter only for viewing.

---

## 2. Logging discipline (do this for every session)

Keep a text file per session, e.g. `session2-controls.txt`. For each action write a line:

```
HH:MM:SS  <what you changed>  <from> -> <to>
14:32:05  START idle baseline
14:32:35  mic gain  20dB -> 40dB
14:32:41  mic gain  40dB -> 60dB
14:33:00  phantom   off -> on
```

Your PC clock and the capture timestamps share the same wall clock, so these lines pin each
transfer to an action. Pause **~5 seconds** between actions so bursts are visually separable in
the trace. Move controls in **discrete steps landing on exact values** (type the number if Wave
Link lets you), distinct values like 10/20/40/60 dB make the encoding obvious; a smooth drag
produces an indecipherable smear.

Record the exact Wave Link version (Help/About) in the log once.

---

## 3. Session 1 enumeration and init (most important single file)

This captures the handshake a Linux backend must replay before any control works, plus the idle
cadence. Save as `session1-init.pcapng`.

1. **Quit Wave Link completely** (check the tray, fully exit, don't just close the window).
2. Start the Wireshark capture on the Pro's USBPcap interface.
3. Log `START`. **Unplug the Pro, wait 3 s, replug it.** → clean enumeration (descriptors).
4. Wait **30 s** doing nothing. Log `idle, WaveLink closed`. → shows if anything polls the
   device without Wave Link (probably not, but we want to know the true baseline).
5. **Launch Wave Link.** Wait until the Pro appears in its UI and settles. Log `WaveLink
   launched`. → this window contains the **init / handshake sequence**, the crown jewel.
6. Touch nothing for **60 s**. Log `idle, WaveLink running`. → reveals keepalive / poll cadence
   (or confirms it's silent). This matters because upstream OpenWave holds the capture stream
   open to dodge a firmware silence race; we need to see if the Pro needs periodic pokes.
7. **Quit Wave Link cleanly.** Log `WaveLink quit`. → any teardown/release commands.
8. Stop the capture. Save. Note the device address you saw at enumeration.

---

## 4. Session 2 control writes (the bulk of the protocol)

Wave Link running. Save as `session2-controls.pcapng`. Do each in order, exact values, ~5 s
apart, one log line each. If a control isn't present in Wave Link for the Pro, note "N/A" and
move on.

1. Mic gain → **10 dB** (log the starting value first).
2. Mic gain → **20 dB**.
3. Mic gain → **40 dB**.
4. Mic gain → **60 dB**. (Four distinct values reveal whether encoding is dB-linear like the
   ALSA capture-volume control, or a raw index.)
5. Mic **mute** on. 6. Mic mute off. (Do it twice: on, off, on, off, confirms the toggle is
   stateless-per-write vs a toggle command.)
7. **Phantom power** on. 8. Phantom off. (If you run a condenser, expect an audible pop, fine.)
9. **Low-cut** on. 10. Low-cut off. 11. Low-cut **type** change if the Pro exposes more than one.
12. **ClipGuard** on. 13. ClipGuard off, then **back on** and leave it on for the next step.
14. **Talk into the mic loudly enough to trigger ClipGuard / clip the preamp for ~10 s**, then
    speak normally ~20 s. Log `speaking loud (clipguard)` / `speaking normal`. → THIS is the test
    for what the 6-byte interrupt IN endpoint carries: if it streams metering / clip /
    gain-reduction notifications, this is when they appear. Watch `usb.transfer_type == 0x01`.
    (This is the one moment sustained input is deliberate, not noise.)
15. **Headphone volume** → 25%. 16. → 50%. 17. → 100%.
18. **Headphone impedance / low-impedance mode** toggle (high → low → high).
19. **Monitor blend / direct-monitor volume**: move it in ~4 discrete steps from one end to the
    other (e.g. 0% → 33% → 66% → 100%), ~5 s each. Log each step. → resolves the open question of
    whether the zero-latency monitor blend is exposed as a vendor command or only in DSP.
20. **Mic output volume** (the "Microphone Output Volume" in your config) → a couple of distinct
    values. 21. **Mic output mute** on/off.
22. **Polarity / polarization** toggle if present.
23. (Optional confirmation) Change mic gain once **from the Stream Deck dial** instead of the
    Wave Link UI. Log it. → the bytes should be identical to step 1–4, confirming the Stream Deck
    just drives Wave Link and there's no separate path. This is the one time re-enabling the
    Stream Deck app during capture is worth the noise.
24. Stop capture, save.

---

## 5. Session 3, reconnect + hardware submix + edge cases

Wave Link running. Save as `session3-edge.pcapng`.

1. With Wave Link open, **unplug the Pro, wait 5 s, replug**. Log it. → the reconnect/re-init
   path (differs from cold start in step 5 of Session 1; a Linux backend needs both).
2. If Wave Link exposes **hardware submix routing** for the Pro (the teardown showed
   `EWLWHardwareMixerHelperWaveXLRPro` / `SoftwareMixerHelper`, the Pro may do on-device
   mixing), poke any per-mix mic level/route controls it offers, one at a time. Log each.
3. Change the Wave Link **sample rate / buffer** if that's device-facing, once, to see if it
   reconfigures the device. (Optional; log it.)
4. Stop capture, save.

---

## 6. Optional Session 4, Frida (only if USBPcap decoding stalls)

If some control writes an opaque blob USBPcap can't disambiguate, run Frida to get the same
transfers with call stacks and the pre-serialization arguments:

- Attach to the Wave Link process, hook `WinUsb_ControlTransfer` (and `DeviceIoControl` as a
  fallback), log `SetupPacket` (bmRequestType/bRequest/wValue/wIndex/wLength) + the data buffer +
  a short backtrace.
- Repeat the specific action that was ambiguous. The backtrace through `LWT::WaveXLRProDevice`
  and whichever `*VendorUSBBackendStrategy` fires tells us the request family.

Skip this unless needed, it's slower and USBPcap is usually sufficient for a controls-only
protocol.

---

## 7. What to hand back to Linux

Copy to a location readable from Linux:

- `session1-init.pcapng` + `session1-init.txt`
- `session2-controls.pcapng` + `session2-controls.txt`
- `session3-edge.pcapng` + `session3-edge.txt`
- (if run) Frida logs
- The exact Wave Link version string.

Analysis steps on Linux:
1. Extract every vendor/class SETUP packet + data payload, aligned to your log timestamps.
2. Decode the request scheme (bmRequestType / bRequest / wValue / wIndex layout, is it the
   MK.1 0x3303 trick, the MK.2 0x0203 standard-class scheme, or new) and the value encodings.
3. Identify the init sequence and any keepalive.
4. Determine what the interrupt endpoint carries (from the speaking/clip test).
5. Write the `0x00b4` backend against that.

---

## 8. Quick reference, device facts (verified on Linux)

- Wave XLR Pro: `0fd9:00b4`, UAC2, `bcdDevice 04.10`.
- Interfaces: If0 AudioControl (+ interrupt IN endpoint 0x81, 6 bytes, 1 ms, NOT user input,
  the Pro has no buttons/knob; watch it during the speaking test), If1/If2 audio streaming,
  **If3 vendor-specific class 0xFF, no endpoints, no kernel driver**, the control target.
- Mic gain is ALSO a standard ALSA control on Linux (0–80 dB), already works without this
  protocol; the capture is for phantom / low-cut / ClipGuard / monitor blend / mic-output.
- No LEDs, no physical controls; LED/knob/button settings in a Wave Link config belong to
  the XLR Dock, not the Pro.
