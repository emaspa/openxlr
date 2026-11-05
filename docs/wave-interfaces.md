# Multiple Wave interfaces

OpenXLR drives one primary interface: the one in the header picker, whose
microphone feeds XLR 1 and the other hardware strips and whose controls fill
the UI. Up to four more attached units can be driven next to it as
additional interfaces. Each gets its own hardware controls and feeds its own
capture channel. Two pairings have run on hardware, a Wave XLR Pro beside an
XLR Dock and beside a Wave:3; the rest rests on simulated units, see
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

Only the header picker (`setActiveDevice`) moves the primary, and the
journal says when it does. The daemon saves the pick in
`~/.config/openxlr/active-device.json` and drives the same unit after a
restart, ahead of `OPENXLR_DEVICE`. At start it waits up to 10 seconds for
that unit, so one that enumerates after the others is still the first one
driven. If it stays away, another unit stands in as below until it comes
back. Until a unit is picked, the primary is a unit of
the model `OPENXLR_DEVICE` names, or the first attached unit. Enabling or
disabling an additional interface never changes it, and a unit enabled as
additional is never driven as the primary in place of an absent one. Another
unit may stand in while the chosen unit, or the model `OPENXLR_DEVICE` names,
is away for a moment, after a USB reset or while a hung unit is set aside.
It gives the primary role back as soon as the chosen unit is attached and
usable again.

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
silent. In both cases the warning under the UI's header says why.

The daemon switches a Wave XLR Pro's card to its pro-audio profile and lists
its Headphones 1, Headphones 2 and Line Out outputs. It finds that card and
those outputs by the first two names above. If the first name that matches
anything matches two cards, it changes neither card's profile and lists no
outputs for either.

XLR 1 and XLR 2 can each be bound to another capture source instead
([input bindings](api.md#input-bindings)). A bound strip takes one port of
that source, named exactly, and no step above applies to it; a strip bound
to another unit's source follows that unit's card instead
([a strip on an additional unit](#a-strip-on-an-additional-unit)). The rules above
still find the node for Aux In and for a strip left on the interface, and
the units stay driven whatever the strips take. Their outputs, controls,
recall and additional roles do not change. The primary's microphone input 1
then belongs to the first capture channel on its own node at its first port,
the same channel **Add input channel** below makes for microphone 1, and
that channel takes the unit's controls and, on a Wave XLR Pro, the direct
microphone path to the jacks. Until such a channel exists, the input is not
in the mixer.

## Additional interfaces

Options → Audio → **Additional interfaces** lists every attached unit, and
every unit enabled before that is not attached now. The button shows once
more than one unit is attached or one is enabled. Tick **Drive as an
additional interface** to enable a unit. The choice is kept in
`~/.config/openxlr/wave-interfaces.json`. If that file cannot be written, the
choice holds until the daemon restarts and the warning under the UI's
header says so. If it cannot be read, no additional unit is driven until the
next change writes it again.

An additional unit gets its own device manager, with the primary's USB
isolation, reconnect backoff, gain lock, phantom power settling and last
settings, stored under its instance id in `~/.config/openxlr/devices/`. The
UI shows gain, mute, low cut, ClipGuard and phantom power for each input
the model has; the API reaches every control the model exposes. The software
low cut and ClipGuard stay on the primary's XLR 1 and the XLR inserts on its
XLR channels; an additional unit's capture channel has an insert chain of its
own and, when mono, Mic setup, and the inserts on the mixes it feeds process
it too.

Two Wave XLR Pros that the node name cannot tell apart cannot be made
additional, because switching one card's profile could switch the other's.
Pick one of them as the primary instead.

Choosing a unit as the primary in the picker stops its additional manager
before the primary opens it, so a unit is never driven twice. A command for that unit
then reaches the primary. Disabling an additional unit releases its USB
handle. With capture inputs, the UI asks whether to keep them and their
insert chains, defaulting to deletion. Kept channels retain their sends,
binding and chains and play while their source is available; their hardware
controls disappear. Deletion uses the saved `captureUnit`, even while the
unit is unplugged, with unambiguous source names as a fallback for older
entries. Other units' channels stay. An unplugged unit that remains enabled
keeps its channels, silent until it returns.

## Adding its microphone

Under each driven unit, choose its capture source, its microphone, 1 or 2 on
a Pro, and a channel name, then press **Add input channel**. The source list
holds the unit's own nodes, found as above. A single source is selected for
you; with several, you choose. The channel is a capture channel
([Additional capture inputs](manual.md#capture-inputs)) that takes one port of
the source and feeds it to both sides: the first port for microphone 1, the
third for microphone 2. It starts muted in every mix. While its source or port
is missing it stays silent and no other microphone takes its place.

## A strip on an additional unit

XLR 1 or XLR 2 can take an additional unit's microphone instead of a capture
channel. Bind the strip to the unit's source from the strip's chip
([input bindings](api.md#input-bindings)). The binding dialog lists every
attached unit's sources except the primary's own and, for a unit not driven
yet, offers **Also drive this interface**, which enables it as above. A
strip on an additional unit has the strip's inserts and Mic setup, and,
while the unit is driven, the unit's gain and switches, the same controls as
in this window drawn by the same view, on the strip in the input rack, the
mix matrix and the classic INPUTS panel. In the state the strip's
`unitInput` names the unit with `primary` false. The first port is the
unit's microphone 1 and the third its microphone 2, as for **Add input
channel**; a strip bound to that input wins over a capture channel on it.

The binding records the unit, because the unit's source can change its name
while the strip is bound. Enabling the unit can switch its card to
pro-audio, a card profile switch in the desktop's sound settings renames
every node under the card, and so can a replug. The strip then takes the
one source the unit's card publishes under its new name, at the same port,
and returns to the saved name when it comes back. If the card publishes
several sources at that moment, as a Wave XLR Pro in a split profile does,
OpenXLR picks none, and the strip stays silent and says so until it is
bound again.

Disabling the unit takes its controls off the strip and leaves the binding.
The strip keeps its audio, and is silent while the unit is unplugged. The
strip is named by what feeds it: XLR 1 or XLR 2 on an XLR Dock's or a Wave
XLR's jack, Mic 1 or Mic 2 on a Wave:3's, Wave:3 MK.2's or Wave:1's capsule.

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
from it and removed again when it was disabled. A Wave:3 beside the same Pro
has run too: its controls were written and read back while the Pro's were
left alone ([hardware-support.md](hardware-support.md#input-bindings)). The
rest rests on the code and on tests with simulated units and a private
PipeWire server:

- Two Wave XLRs, two MK.2s or two XLR Docks: each opens at its own address,
  the instance ids stay put across replugs, and the daemon finds the
  primary's capture node by serial.
- Two XLR Docks: each dock drives the ALSA card at its own USB bus and
  device number for gain, mute and headphone volume.
- A Wave XLR Pro with a second Pro or a Wave XLR: its card's profile switch and its
  physical outputs stay on its own card, as primary and as additional.
- The serial in a node name as udev spells it, for every model, and the
  fallback to the model name when it differs.
- A strip bound to an additional unit, such as a Wave:3 beside a Wave XLR
  Pro: the unit's controls on the strip, the strip following the unit's
  source through a card profile switch and a replug, and capture before
  playback when a mix is routed into that unit.
