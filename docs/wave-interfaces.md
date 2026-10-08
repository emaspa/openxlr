# Multiple Wave interfaces

OpenXLR drives one primary interface: the one in the header picker, whose
microphone feeds XLR 1 and the other hardware strips and whose controls fill
the main window. Up to four more attached units can be driven next to it as
additional interfaces. Each gets its own hardware controls and feeds its own
capture channel. None of this has been run with two physical units yet; see
[what still needs hardware](#what-still-needs-hardware).

## Telling units apart

Every attached unit has an instance id, `vvvv:pppp@` followed by sixteen hex
digits. The digits are a hash of the unit's USB serial, so a unit keeps its id
when it is replugged or moved to another port. A unit without a serial, or two
units that report the same serial, are told apart by their USB port instead,
and moving such a unit to another port gives it a new id.

A unit is opened at its exact USB bus and address. When the transport cannot
open by address it refuses; it never falls back to the first unit with the
same product id. The header picker lists every attached unit, with its USB
port after the name when two of one model are attached.

The primary is chosen by instance id. While the chosen unit is unplugged and
other units are attached, none of them is taken over in its place, since one
may be driven as an additional interface. Pick one in the header. A lone
attached unit is driven whatever its id, so a unit without a serial still
connects after a port change.

A unit that hangs on three USB transfers is set aside on its own, without
affecting another unit of the same model. It is driven again once it has been
off the bus, which a changed USB address also shows when the unplug fell
between two scans.

## Which microphone feeds the hardware strips

The hardware strips take their audio from a PipeWire capture node found by
name. The name udev gives a node carries the model and the serial, for
example `alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_SERIAL-00.analog-stereo`.
OpenXLR looks for, in order:

1. The primary's model and serial, with the separator ALSA writes after the
   serial, so `Wave_XLR_SERIAL-` cannot match a unit whose serial is
   `SERIAL2`.
2. The primary's model alone, for a serial that udev spells differently from
   the USB descriptor. OpenXLR skips this step while another attached unit
   could answer to the same name: a second unit of the model, or a Wave XLR
   Pro next to a Wave XLR, since both names contain `Wave_XLR`. When this step
   finds the node, the daemon log says so.
3. While at most one supported interface is attached, any Wave XLR node. This
   keeps the microphone working when that unit is not driven: before the udev
   rule applies on a fresh install, after a failed USB claim, or while the unit
   is set aside after hangs.

A name that matches the nodes of two cards is two units that cannot be told
apart, and the strips stay silent rather than take either microphone. With
more than one interface attached and none driven, the strips also stay
silent. In both cases the warning under the window's header says why.

The daemon switches a Wave XLR Pro's card to its pro-audio profile and lists
its Headphones 1, Headphones 2 and Line Out outputs. It finds that card and
those outputs by the first two names above. If the first name that matches
anything matches two cards, it changes neither card's profile and lists no
outputs for either.

## Additional interfaces

Options, INTERFACE, **Additional interfaces** lists every attached unit, and
every unit enabled before that is not attached now. The button shows once
more than one unit is attached or one is enabled. Tick **Drive as an
additional interface** to enable a unit. The choice is kept in
`~/.config/openxlr/wave-interfaces.json`. If that file cannot be written, the
choice holds until the daemon restarts and the warning under the window's
header says so. If it cannot be read, no additional unit is driven until the
next change writes it again.

An additional unit gets its own device manager, with the primary's USB
isolation, reconnect backoff, gain lock, phantom power settling and last
settings, stored under its instance id in `~/.config/openxlr/devices/`. The
window shows gain, mute, low cut, ClipGuard and phantom power for each input
the model has; the API reaches every control the model exposes. The software
low cut and ClipGuard stay on the primary's XLR 1 and the XLR inserts on its
XLR channels; an additional unit's channel can use the inserts on the mixes it
feeds.

Two Wave XLR Pros that the node name cannot tell apart cannot be made
additional, because switching one card's profile could switch the other's.
Pick one of them as the primary instead.

Choosing a unit as the primary stops its additional manager before the
primary opens it, so a unit is never driven twice. A command for that unit
then reaches the primary. Disabling an additional unit releases its USB
handle and deletes the capture channels made from its own nodes, the ones
found by the names the source list uses, with their sends and routing, as
deleting the channel in the layout editor would. A channel from any other
source stays. A unit that is unplugged and still enabled keeps its channel,
silent until the unit returns; one disabled while unplugged leaves its
channel too, since its node names are not known without it.

## Adding its microphone

Under each driven unit, choose its capture source, its microphone, 1 or 2 on
a Pro, and a channel name, then press **Add input channel**. The source list
holds the unit's own nodes, found as above. A single source is selected for
you; with several, you choose. The channel is a capture channel
([Additional capture inputs](manual.md#capture-inputs)) that takes one port of
the source and feeds it to both sides: the first port for microphone 1, the
third for microphone 2. It starts muted in every mix. While its source or port
is missing it stays silent and no other microphone takes its place.

## Profiles

A profile saved while additional units are driven also holds their hardware
settings by instance id. Recalling it writes them to those units if they are
enabled and connected. It never enables a unit, changes the primary or opens
a unit that is not attached. Capture channels, application routing and the
choice of additional units are not part of a profile.

## What still needs hardware

A Wave XLR Pro as the primary with an XLR Dock as an additional interface has
run on real hardware: the dock was listed by its instance id, driven with its
settings restored and its source preselected, and a capture channel was added
from it and removed again when it was disabled. The rest rests on the code and
on tests with simulated units and a private PipeWire server:

- Two Wave XLRs, two MK.2s or two XLR Docks: each opens at its own address,
  the instance ids stay put across replugs, and the daemon finds the
  primary's capture node by serial.
- Two XLR Docks: each dock drives the ALSA card at its own USB bus and
  device number for gain, mute and headphone volume.
- A Wave XLR Pro with a second Pro or a Wave XLR: its card's profile switch and its
  physical outputs stay on its own card, as primary and as additional.
- The serial in a node name as udev spells it, for every model, and the
  fallback to the model name when it differs.
