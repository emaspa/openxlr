# UCM profile for the Wave XLR Pro (experimental)

An ALSA UCM2 profile that splits the Pro's 17-in/18-out multichannel
card into named devices when the OpenXLR submixer is off, or when
OpenXLR is not running: a "Monitor (headphones / line out)" sink on playback pair 2/3,
the three other hardware-monitor-bus feeds as Line1..3 (pairs 10/11,
12/13, 14/15), and mono "XLR 1" / "XLR 2" sources (capture pairs 0 and
1). Suggested by the goxlr-utility / PipeWeaver author; the GoXLR's own
in-tree UCM profile is the precedent.

From this directory, install: `./install.sh` as root (purely additive under
`/usr/share/alsa/ucm2/USB-Audio/`, edits nothing shipped), then restart
pipewire + wireplumber. Revert: `./revert.sh` as root, then the same restart. Restarting the audio
services interrupts playback and recording.

## Verified 2026-08-30 on real hardware

- WirePlumber picks the profile up; the card gains four profiles:
  HiFi (the split, default), Direct (raw 17/18ch verb), pro-audio, off.
- Tone to the split Monitor sink reached the headphone jack, by ear.
- The split XLR 1 source records real signal.
- Gotcha for daemon-less users: audio reaches the ears only if a jack
  selector in the device's vendor config carries the monitor bus
  (block 0x0001 off90..93, value 0x1e); the device keeps whatever the
  last software set. Fresh-from-Windows units typically have the
  headphone jack enabled.

## Coexistence with the daemon

Under the HiFi split the raw multichannel nodes the daemon links
against do not exist. The daemon therefore switches the card to the
pro-audio profile while its submixer runs and restores the previous
profile on graceful shutdown. In hardware-control-only mode the split
can remain active; see [the manual](../../docs/manual.md#hardware-only).

## Status

In the repo, manual install only: no package ships these files, and
removing OpenXLR leaves the profile in place until revert.sh is run
(the split is useful on its own). Upstreaming to alsa-ucm-conf is the
goal once a second Pro owner has confirmed the split.
