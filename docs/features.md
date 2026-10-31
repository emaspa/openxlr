# Features

What OpenXLR does, area by area, and how. The README has the summary.

The mixer, the microphone processing, the UI, the terminal mixer and
the OpenDeck plugin work with any capture source PipeWire offers; [hardware control](#hardware-control) is
for Elgato devices. [Hardware support](hardware-support.md#other-microphones)
says what a microphone of another brand gets and what it does not.

The experimental [Flatpak mode](flatpak.md) runs the daemon with the UI
and requests login startup through the desktop portal.

## Hardware control

Elgato devices are controlled over each device's USB protocol; the
per-control verification state is in [hardware-support.md](hardware-support.md).
Another brand's microphone or interface gets no hardware controls from
OpenXLR.

Wave XLR Pro:
- Per XLR input: gain (0 to 80 dB), mute, low cut, expander, voice tune
  with strength, phantom power, ClipGuard, compressor
- USB Aux input stage: level (0 to -60 dB) and level lock
- Analog Line In: its own channel and its own gain (0 to 40 dB), separate
  from the USB Aux input, and kept out of the headphone jacks' hardware mix
  so the jacks hear it only through Monitor A (both in beta, see
  [hardware-support.md](hardware-support.md))
- Line In level lock, which keeps the desktop mixer and other
  applications from changing the Line In gain
- Both headphone outputs: independent volumes, low-impedance mode
- Mic/PC direct-monitor crossfade (inside the device, no host latency)
- Physical output routing: Headphones 1, Headphones 2, Line Out and USB
  Aux are each switched in the device's hardware mixer
- Ducking on the device, mapped from Wave Link on Windows: when the
  [ducking](#submixer) setup fits what the unit can do (XLR 1 as the key, a
  whole mix on the jacks and Line Out), the Pro runs it on its own
  processor; otherwise this computer does, and the state says why
  ([api.md](api.md#where-ducking-runs)). The USB Aux port stays in software
  until it is checked by ear. The unit is written with ducking off while
  this computer runs it, which clears ducking Wave Link left

Wave XLR MK.2 and XLR Dock MK.2: gain, mute, phantom power, low cut,
expander, voice tune with strength, ClipGuard, compressor, headphone
volume, low impedance, crossfade.

Wave XLR: gain, mute, headphone volume, low impedance, phantom power.
The desktop's volume controls follow the interface's gain, mute and
headphone level and can set the mute and headphone level; this is not yet
checked on an MK.1.

Wave:3: gain (0 to 40 dB), mute, ClipGuard, headphone volume and the
direct monitor balance, shown as the crossfade; the low cut is the
submixer's, by design. The desktop's volume controls follow the
microphone's mute and headphone level and can set them, and Options
shows the firmware version. Written from public protocol research;
on the maintainer's unit every control writes and reads back, and the
gain, the monitor balance and the desktop volume controls are verified;
the mute pad, the LED ring and a replug are not yet checked. With an XLR
interface attached too, the daemon drives the interface and the Wave:3
only when picked from the header. [hardware-support.md](hardware-support.md)
names the sources, the bytes they dispute and what an owner checks.

Wave:3 MK.2: gain (0 to 40 dB), mute, headphone volume, the direct
monitor crossfade, the microphone EQ, low cut, expander, voice tune with
its strength, compressor and ClipGuard on the unit, from the Wave XLR
MK.2's backend. On the maintainer's unit each control wrote and read
back. The gain and headphone level matched the kernel's own controls, and
the EQ, low cut, compressor and voice tune were measured on the capsule's
noise. The low cut sits where the XLR MK.2 keeps phantom power. A replug
is not yet checked. Like the Wave:3, the daemon
drives it only when no XLR interface is attached or when picked from the
header. Its microphone can stall while its own headphones play. Options,
INTERFACE, has **Recover microphone**, which links the microphone into
its strips again and, if the unit's capture is still stopped, resets the
unit on USB and puts its mixes back once it records. OpenXLR does the
reset by itself when the kernel keeps the capture stopped for three
seconds while a mix plays into the unit's headphones, at most once every
two minutes and three times in half an hour.

Wave:1: the same controls as the Wave:3, from the same backend under the
Wave:1's product id. An owner's diagnostics showed the
microphone answering the Wave:3's block, firmware line and desktop
volume mirror, and a second run showed the gain, mute and headphone
volume set from the UI taking effect on the microphone. ClipGuard and
the mic/PC mix still await an archive collected with them changed. A
block of another length is still refused before writing anything.
The daemon drives it after every other supported device.

The XLR Dock MK.2 and the Wave XLR MK.2 run OpenXLR's
[microphone EQ](#software-controls) on their own DSP from firmware 1.0.15,
the Wave:3 MK.2 from its own firmware 0.2.3
([measured](hardware-support.md#wave3-mk2-0fd900ca)), and the Wave XLR Pro on each of its two inputs from firmware 1.0.14, the
way Wave Link 3.5 does ([findings](mk2-eq-findings.md),
[Pro](hardware-support.md#wave-xlr-pro-0fd900b4)); every other model and
older firmware run it in software. The Pro's EQ follows Wave Link
captures and is verified on hardware by the maintainer and other owners.
The Pro's mix
maximizer, channel booster and remaining hardware mix matrix are
unmapped, as is any ducking key but XLR 1. Wave Link configures those onboard effects on supported systems;
OpenXLR's LV2, CLAP and VST3 inserts process audio on the Linux host.

XLR Dock: gain, mute and headphone volume through the kernel's standard
ALSA controls, plus phantom power and headphone low impedance over the
original Wave XLR's protocol dialect, which the dock also answers. A dock
whose firmware answers the kernel's range query badly comes up without
the capture volume control; OpenXLR then drives gain, or any of the three
the card lacks, through that same config block, and the connection note
in the UI says so. Each control takes exactly one path, since the
kernel caches feature-unit values and a block write behind ALSA's back
would leave the two disagreeing. The
phantom byte was identified by the
[openwave](https://github.com/rikkichy/openwave) project on the Wave XLR
([openwave PR #8](https://github.com/rikkichy/openwave/pull/8)) and
confirmed on the dock with a condenser microphone. Wave Link does not
write it for the dock. The dock has no onboard voice-processing DSP;
Wave Link runs those effects host-side, and on Linux the submixer
provides them (below).

### More than one interface

Every attached unit has an instance id from its USB serial, or its USB port
when it has no serial, and is opened at its exact USB address, so two units
of one model are two devices. The header picker chooses the primary, whose
microphone feeds the hardware strips; the pick is kept in
`~/.config/openxlr/active-device.json`, and the daemon drives the same
unit again after a restart, waiting up to ten seconds for it at start
even when another unit appears first. Up to four more units can be driven as
additional interfaces, each with its own device manager, controls and
remembered settings, and each feeding a capture channel from one port of its
source. The daemon finds the capture node, the Pro's card profile and its
outputs by the unit's serial, then by its model while no other attached unit
shares the name. With one interface attached, any Wave XLR source still feeds
the strips, driven or not. A name that matches two cards is used for neither.
Each XLR Dock drives the ALSA card at its own USB address. A Wave XLR Pro with
an XLR Dock beside it, and with a Wave:3 beside it, has run this way; two
units of one model have not. See [Multiple Wave interfaces](wave-interfaces.md).

## Software controls

These run on the computer. The microphone EQ is on XLR 1 and XLR 2
whatever feeds them, and on every capture input on one mono port. The low cut and ClipGuard are on XLR 1 when it is
bound to another source, or when the interface's backend has no mapped
hardware control for them. Gain lock is for Elgato devices without a
physical gain dial.

- Low cut: a high-pass at 80 or 120 Hz (the two values Wave Link
  offers), a filter-chain node inserted between the mic and its channel,
  cycled from a button on the XLR 1 strip. Its response was measured
  with test tones as a second-order high-pass. The node is re-created
  if it disappears from the graph.
- ClipGuard: a post-ADC hard limiter at -3 dB in the same filter chain.
  It protects the downstream PipeWire mixes from overload, but cannot
  repair clipping that has already happened in the analogue preamp or
  ADC; microphone gain still needs headroom. It needs the `swh-plugins`
  LADSPA package. If that plugin is unavailable, enabling ClipGuard is
  rejected, the control stays disabled, and the existing microphone
  route remains live.
- Microphone EQ: one to sixteen bands on XLR 1, XLR 2 and mono capture inputs with Wave Link's band
  types (high pass, low shelf, bell, notch, high shelf, low pass), ranges
  and steps (20 Hz to 20 kHz in hertz, -12 to +12 dB in quarter decibels,
  Q 0.1 to 10 on Wave Link's log grid) and defaults, off until switched
  on. The curves are the Audio EQ Cookbook's, which Wave Link's are; a test
  holds the daemon's maths and the running filters to the same table. It
  runs in the native plugin host at the head of the input, before the low
  cut, ClipGuard and the inserts, with no added latency. Switching it on
  splices it in without restarting the chain behind it. After that every
  change reaches the running filters without a new node: a frequency, gain
  or Q change keeps the filter state, and a switch or a type change
  crossfades over 5 ms. It is saved with the mixer and in profiles. The
  UI opens the editor from an **EQ** button in INPUTS, an input rack and
  the matrix row heads. Its response graph uses the daemon's curve maths
  on a log frequency axis. Each band has a handle to drag for frequency
  and gain, or for Q on a type without a gain; the wheel steps Q, a
  double click switches the band and a right click picks its type. Under
  the graph is a row of fields per band, and above it the EQ switch, a
  bypass to compare, copy and paste between microphones and a reset. The
  handles take the keyboard and carry names for screen readers. A drag
  sends each band's changes at most every 20 ms, and the editor shows the
  values the daemon keeps. The [control API](api.md#microphone-eq) drives
  the same EQ. On a Wave XLR MK.2 or XLR Dock MK.2 from firmware 1.0.15,
  a Wave:3 MK.2 from its firmware 0.2.3, and on a Wave XLR Pro from 1.0.14
  for each of its inputs, the unit's own
  DSP runs it instead, with the same values and the same
  curves: each change is one write of the unit's settings block, at most
  ten a second, read back by the next poll. Moving between the unit and
  software switches the leaving one off first, so a voice is never
  equalised twice. The editor shows a Hardware processing badge while the
  unit runs it, and a note when the unit's firmware is older than the
  version its model needs.
  A switch in its footer, **On the unit** or **In software**, sets where
  each microphone's EQ runs. The terminal mixer's Inputs tab offers the
  same choice and says what runs each EQ, and `setEqProcessing` sets it
  from a script ([api.md](api.md#where-the-eq-runs)). The Flow window says
  where the EQ runs. A capture input's EQ always runs in software, at the
  head of the input before its inserts, with the same editor without the
  switch and the badge; its Mic setup take is the raw microphone, looped
  through the EQ ([api.md](api.md#eq-on-capture-inputs)).
- Noise removal: on the same microphones, first in the same native stage,
  ahead of the EQ, the low cut, ClipGuard and the inserts, with a choice of
  two engines per microphone. RNNoise 0.2 (Xiph) is light, best on steady
  hiss and hum, and adds 30 ms; DeepFilterNet 3 is cleaner on typing,
  changing noise and some room echo, costs more processor and adds 51 ms
  at a quantum of 256 or 61 ms at 1024, since it runs on a thread of its
  own and never on the audio thread. The block size option below sets
  that quantum. A strength from 0 to 100 dB limits
  how far the noise is turned down, the same for both. Switching it on or
  off or changing the engine dips the voice for about 10 ms; the strength
  changes live. It runs at a 48 kHz graph and says so at any other rate.
  Every package carries both engines; a source build without Rust carries
  RNNoise and says so. Mic setup's Shape step, the terminal mixer, a Deck key and
  [`setNoiseRemoval`](api.md#noise-removal) control it, and it is saved
  with the mixer and in profiles.
- Voice changer: on the same microphones, last in the same native stage,
  after noise removal and the EQ, a voice per microphone. Custom shifts
  the pitch and the formant by -12 to 12 semitones each; Deep, High,
  Robot, Radio, Alien, Dracula, Frankenstein, Mummy, Werewolf and Witch
  are presets. Changing the voice or switching it crossfades. Interfaces
  with a direct monitor, the MK.2 units among them, send the dry voice to
  their jacks, so the changed one is heard through a monitor mix. The Wave
  XLR Pro turns its zero-latency path off while a voice is on. The Voice
  changer window, opened from the SUBMIXER header after Soundboard, the
  terminal mixer, a Deck key with toggle and hold modes and
  [`setVoiceChanger`](api.md#voice-changer) control it, and it is saved
  with the mixer and in profiles.
- Gain lock: the daemon rejects every gain change while the lock is set,
  from any client, and stores the lock per device in `gainlock.json`.
  Shown only for devices without a physical gain dial, which would
  bypass it. Giving a device its own settings back when it connects is
  not a change and goes through: a dock that forgets its settings would
  otherwise come up at whatever gain its firmware chooses, which is the
  one thing the lock is there to prevent.

The software low cut and limiter appear when XLR 1 is bound to another
source or the backend does not expose the corresponding hardware control.
On the original Wave XLR, hardware
processing configured in Wave Link can still be active; OpenXLR cannot
read or disable those unmapped settings.

Two behaviours apply on multi-device switching: the mixer's hardware
input channels follow the active device, and after a switch the
hardware channels' monitor sends come up muted, so the newly patched mic
does not reach the speakers until unmuted. Aux In, Line In and XLR 2 keep
their monitor sends when either device lacks that input, as a one-input unit does,
since no new microphone reaches them.

## Submixer

The submixer shows the channel strips under its own header and the mix
masters below them under OUTPUT MIXES, so channel sends and mix master
controls can be distinguished at a glance. Every strip has a Volume row
above its sends, the hardware inputs included, so the send rows line up
across the strips.

The **Flow** window shows inputs, channels, mixes and outputs in four columns.
Click a card to trace its signal path, with colours for each routing stage and
unrelated routes dimmed. Processing stays inside the channel and mix cards;
the tooltip lists each insert in signal order. See [Audio flow](manual.md#audio-flow).

Built from PipeWire nodes, no kernel modules or custom drivers:
- Channels for the hardware inputs (XLR 1, XLR 2, Aux In, Line In), for
  application groups (Game, Music, Browser, System, Voice Chat, SFX) and
  for other PipeWire capture sources (below). XLR 2, Aux In and Line In
  are shown only where the device has them, which is the Wave XLR Pro, or
  while XLR 2 is bound to another source; Line In also needs the Pro's
  capture source in its 18-channel layout. Every client reads that from
  the state rather than deciding for itself
- Aux In and Line In are separate channels: Aux In takes the USB Aux
  port's pair (AUX4 and AUX5 of the Pro's capture source), Line In the
  analog Line In's (AUX6 and AUX7), and the two are never mixed into one
  channel. Each strip's readout shows its level or gain, and the
  channel's volume while that volume's fader moves
- Mixes: Monitor A (what you hear), Monitor B (a second selection for
  outputs that should hear something else), the virtual microphones
  (Stream and Chat by default, published as capture devices selectable
  in OBS or Discord like a microphone), monitor mixes you add for another
  output (a blend for headphones or speakers that publishes no
  microphone), and Aux (what a second computer on the USB Aux port
  receives)
- Per-channel, per-mix send levels and mutes; per-mix masters
- A volume for every channel that scales every send of the channel at
  once, like the channel level in Wave Link 3. It is folded into the send
  streams the mixer already has, so it adds no node and no delay. XLR 1,
  XLR 2, Aux In and Line In have one too, after the interface's gain, so a strip
  bound to a webcam or USB microphone without a gain control still has a
  level, and a strip with plugins has one after them. The UI draws it
  in the channel tiles, in the input rack and in the mix matrix's row
  heads, under the gain on a hardware row; the terminal mixer and the
  OpenDeck dials change it too. The Wave XLR Pro's zero-latency headphone
  path takes its level from it, and the UI marks the channel it carries
- Ducking on every device: a key microphone (XLR 1, XLR 2 or a mono
  capture input) lowers chosen channels in chosen mixes while it is loud,
  by 6, 12 or 24 dB with the Wave XLR Pro's sensitivity and speed presets.
  The key is never lowered. A native stage per ducked mix runs it on this
  computer with no added latency. On a Wave XLR Pro whose setup fits what
  the unit can do, the unit runs it instead; otherwise the state says why
  it does not. Saved with the mixer, carried by profiles, and reported as a
  `duck:<mix>` reduction meter while software runs. Set up in the
  Ducking window, opened from the SUBMIXER header, the layout editor and
  Options, with
  the key, a channel and a mix checklist, the presets, where it runs and
  a reduction bar ([manual](manual.md#ducking))
- Bidirectional Linux volume and mute controls for every monitor mix,
  with an explicit 150% boost button for monitor masters and output volume
- A MONITOR mute that pauses every selected output while recording and
  streaming continue by default. An option also pauses microphone sends to
  OpenXLR's recording mixes, leaving raw physical capture to other programs
  alone. Resuming keeps mix and channel mutes, levels and feeds
  as they were. The UI, terminal mixer and Deck share the same pause,
  saved with mixer settings and left unchanged by profile recall. Physical
  output mutes mirror it; explicitly opening one monitor during a pause starts
  a new listening selection and opens its assigned Linux outputs, without
  reviving the old selection on the next key. A shared output stays open while
  any assigned mix is open. The original native flags survive a daemon restart
  and come back when the software mixer is turned off
- An editable layout: Edit layout in the SUBMIXER card adds, renames,
  reorders and removes application channels, virtual microphones and
  monitor mixes, including Monitor A/B, while audio plays, as does the API.
  At least one monitor remains. Stable ids mean
  profiles and Stream Deck keys survive a rename. Every change is saved before it is
  confirmed. The order is only where things are drawn: an app whose
  channel is deleted goes to System, or to the first application channel
  by id when System is gone
- An icon and a colour for any channel or mix, and a hidden flag that
  leaves a channel's strip or a mix's master and column out of the mixer
  while its audio, sends, outputs and apps carry on. Every mix can be
  hidden, Monitor A, Monitor B, Stream, Chat and Aux included. Show hidden
  in the SUBMIXER header draws them faded in their places, as a UI
  preference. Icons come from a built-in vector set of 24, every item
  starts with one chosen by its kind, and the icon can be picked when the
  channel or mix is added. A skin may redraw an icon or add its own as
  path data. The colour comes from a row of twelve swatches or is typed
  as `#RRGGBB`. The UI, the terminal mixer and the Stream Deck keys
  show the same icon and colour. See
  [Edit the mixer layout](manual.md#layout)
- Arrange mode in the UI: drag handles, or the arrow keys, move the
  five sections and the channel and mix tiles. Section order is a UI
  preference; tile order is the layout's one order. See
  [Arrange](manual.md#arrange)
- Skins can place the UI's header and five sections in a tree of
  stacks, grids and docks that scrolls sideways when it is wider than the
  UI, start a section folded or hidden, give a section a preferred
  size, and order the header's actions with some in a ⋯ menu; Options
  always stays first. Under such a skin Arrange trades sections' places,
  across containers too, by pointer, arrow keys or a Swap with menu, and
  shows hidden sections again. The moves are kept for that skin, and Reset
  returns to its own places. See [Layout](skins.md#layout)
- Skins can draw INPUTS as an input rack, one strip per channel with the
  INPUTS panel's own controls, and the submixer as up to four mix banks
  side by side or stacked. A skin picks upright strips or send lists,
  orders their parts or leaves some out, stands faders and meters up,
  reverses faders, labels channels with icons, and draws each master as a
  card or a strip. Every bank has a picker listing
  every mix that is not hidden; your pick is kept per skin, and a bank whose mix is deleted
  moves to the next one. Channel and mix order stay the layout's. See
  [Templates](skins.md#templates) and [Mix banks](manual.md#mix-banks)
- Skins can draw MONITOR as a one-row toolbar that keeps every output and
  its feed in its picker, APPLICATIONS as a list for a narrow column or as
  columns of tiles of one width with a skin's number per column, a long
  name scrolling to its end under the pointer, and
  open the submixer in its own window once when the skin is put on, unless
  you have already chosen where it goes. A skin switch ends a fader drag,
  keeps focus on the same channel, mix and control, keeps the page's
  scroll, the detached mixer and its plugin windows, and sends nothing to
  the daemon. Options says what the skin arranges and resets that skin's
  arrangement, bank picks and docking choice. See
  [Switching skins](skins.md#switching-skins)
- A mix matrix for the submixer: a row per channel, a column per mix, and
  each channel's send into each mix where they cross, its fader drawn over
  the channel's meter and its cell tinted red or greyed out while muted, as
  the skin chooses. Row heads carry
  the input gain, mute, microphone switches and plugin chain under the
  INPUTS panel's rules, the switches behind a **Mic** button that shows
  whether one is on and whether phantom power is, the gain written under
  it (the volume while its fader moves), and
  column heads the mix's master, mute, chain and how many outputs hear it.
  A skin orders and hides the parts of the heads and cells. See
  [The mix matrix](skins.md#the-mix-matrix)
- The Broadcast skin, shipped with the application: the submixer as a mix
  matrix across the UI, HEADPHONES and MONITOR's toolbar side by side
  above it, INPUTS hidden until Arrange mode shows it, and APPLICATIONS
  in columns of six under the matrix, on a dark neutral palette. It is the complete example
  of a layout skin, and a copy to start from is in `docs/examples/skins`.
  See [Broadcast](manual.md#broadcast)
- Compact Desk: a dark matrix skin with a bounded headphone column, a
  monitor toolbar that grows with the window, wider names and faders, and
  application tiles wrapping across the page with readable names. Restart, version and About
  stay in the header's overflow menu. See [Skins](skins.md)
- Nyan Pink: dark plum surfaces, pink faders and cyan details, keeping the
  classic mixer arrangement and the saved section order.
- The Wave skin: Broadcast's arrangement on a dark charcoal palette with
  grey faders and green meters. HEADPHONES and MONITOR's toolbar side by
  side above the mix matrix, INPUTS hidden until Arrange mode shows it, and
  APPLICATIONS in columns of six under the matrix. Its palette also
  reaches the terminal, Deck and bar through the shared skin palettes. See
  [Skins](skins.md)
- A compact view with one chosen channel strip, and a mini view with that
  channel's send into one chosen mix, its master and the monitor volume,
  for a narrow window. Chosen in Options and kept in `ui.json`; hidden
  channels can be picked there. See [Mixer view](manual.md#mixer-view)
- Any mix feeds any selected output, hardware outputs included: the
  checked picker beside each output selects one or more of Monitor A/B,
  Stream, Chat, Aux or mixes you added. A mix can feed several devices,
  and one device can hear several mixes summed at unity. The API uses
  the same feeds, each a direct port link so a sum costs no node or
  additional mixer. At least one mix stays selected; untick the output
  to disconnect it. Outputs that share a hardware bus follow one feed, and mix
  inserts sit upstream so every output hears the processed mix. Profiles recall the feeds. A
  headset with a game sink and a chat sink hears two selections, and
  one pair of headphones can hear the desktop from A with a separately
  processed mic from B; a blend at other levels is a mix of its own
- Level meters throughout, dB-scaled, pushed at 15 Hz

Each channel is a combine sink with one internal stream per mix; that
stream's volume is the send fader. The default layout's 10 by 5 matrix
is 15 channel and mix sinks, plus a post sink for each virtual
microphone, and no loopback processes, and mixes come and go without
touching the channel nodes. Details in [architecture.md](architecture.md).

On the Wave XLR Pro the headphone jacks are fed by a mix inside the
device. Whenever a Pro jack is a monitor output the daemon makes sure
that mix carries the Monitor stream (a unit set up by Wave Link on
Windows may not), and when a jack is the only monitor output the
microphones' zero-latency hardware paths into the jacks follow XLR 1's
and XLR 2's sends in the Monitor mix, each its own: unmuted, you hear that
microphone with no delay; muted, you do not. With another device in the
monitor set, with the microphone in an exclusive group, or while it runs a
software effect (EQ in software, noise removal, the voice changer, the soft
low cut, ClipGuard or a plugin), the software send carries it instead,
effects included.

Channels appear as playback devices in the desktop's audio applet, and
the virtual microphones (Stream and Chat by default) as recording
devices; the hardware input channels are hidden from it.

### Precise faders

Mixer faders use one-percentage-point steps, arrow keys and ten-point page
steps. Shift-drag reduces pointer sensitivity tenfold for short faders.
Device controls step in their displayed units; plugin parameter steps are
unchanged.

### Another microphone on XLR 1 or XLR 2

XLR 1 and XLR 2 can each be bound to one mono port of any external PipeWire
capture source. The UI binds either strip from a chip on the strip, from
Options and, with no interface attached, from a link in the header; the API
binds either too. The chip names the source and lights while the strip is
linked, and a line under it says why a strip is silent. The existing sends,
volume, microphone EQ, software DSP, inserts, Mic setup and meters
process that microphone. A
missing source stays selected and silent until it returns. The dialog names
each port by its channel position and warns about a Bluetooth headset's
headset profile. It also lists other Elgato interfaces' sources: choosing
one offers to drive that interface as an additional one, and while it is
driven its gain and switches are on the strip, the same controls the
additional interfaces window shows. A strip bound to an Elgato interface
follows it when its source is renamed by a card profile switch. Source and port changes keep
channel nodes and application routing, and start that strip's monitoring
muted. The binding is machine wiring, independent of profile recall.

A strip on another source is named Mic 1 or Mic 2 and wears the
microphone icon, or the headset for a Bluetooth headset; XLR 1 and XLR 2
keep their names only on a real XLR jack, the primary interface's or
another one's such as an XLR Dock's. A Wave:3, Wave:3 MK.2 or Wave:1 has a capsule
rather than a jack, so its strip is Mic 1 too. The ids never change.

The Elgato interface stays driven whatever the strips take: headphones,
the dial, output selectors, USB Aux, additional interfaces, recall on
connect and the gain lock keep working, and the other strips keep its
audio. Its microphone can stay in the mixer as a capture channel, which the
binding dialog offers to add. That channel then carries the interface's
input: the UI draws the interface's gain, mute and switches on it, and
on a Wave XLR Pro the zero-latency path to the jacks follows it. With no
channel carrying it, the INPUTS panel keeps the controls folded under a
heading that says so, and the crossfade says where the interface's own
microphone plays. With no interface connected, profiles live under
`generic` and hold the mixer scene only. The terminal mixer binds both
strips from its Inputs tab, offers to drive another Elgato interface it
binds to, and heads the interface's controls with the channel that carries
them, whose key is then the interface's mute; a strip on a driven
additional interface gets that unit's controls. Stream
Deck keys and dials for those controls name that channel, and a gain dial
meters it. The Omarchy bar labels its readouts with the strips' names and
puts its mic button on that channel, or on a strip bound to a driven
additional interface for that unit's mute.

### External capture channels

Additional microphones, headsets, capture cards and other attached Wave
interfaces feed their own channels from their PipeWire sources. Pick the
source and a named mono port or stereo pair in the layout editor; the API
and additional interfaces window can add inputs too. The channel has the
same sends, mutes, meters and profile entries as any other. The binding
names the source node, never a registry id, so it survives a replug. While
the source or the pair is absent the channel stays silent and no other
microphone takes its place; the sweep links it again when the ports
return. A mono source is linked to both sides of the channel. See
[Additional capture inputs](manual.md#capture-inputs).

The source chip in classic INPUTS, the input rack and the matrix opens the
binding dialog. Changing a capture input's source or ports keeps its id,
sends, volume, insert chain and group, mutes its monitor sends and ends its
Mic setup session. Switching between mono and stereo needs an empty chain.
The API exposes the same edit as `setCaptureSource`. A different source
replaces the remembered Elgato unit too, so the old unit cannot take over
when the new source is missing.

A capture input on a source OpenXLR does not drive has a gain, which is the
source's own volume in dB. On a device with a hardware capture control,
such as a webcam's microphone, it reads as that control does in alsamixer
and covers the device's range. On other sources it is a software level
from -60 to +10 dB. The input rack, the matrix and classic INPUTS draw it as a fader
with a readout that shows the volume while the volume fader moves. The
terminal mixer and the OpenDeck gain dial change it too, and the API sets
it with `setCaptureGain`. A change made by another program, such as a voice
chat's automatic gain control, shows up within a second and is never
written back. A capture input on a Wave unit has the unit's gain instead,
and OpenXLR never writes the volume of a Wave unit's source.

Capture inputs host insert chains, set in the UI or through the API:
a mono chain on an input that takes one port, a stereo chain on a pair. An
empty input on the first pair of a one-channel source becomes mono, with
its links left as they were. Each chain sits before the channel's sends and meter and is built
and healed on its own; a capture input without active inserts adds no node.
Profiles carry capture chains, and an older profile that does not name one
leaves it alone. When the source goes away its chain stops, and it starts
again from the saved parameters on replug. The classic INPUTS panel lists
each capture input after Aux In and Line In with its source, volume and inserts.
An additional interface's gain and switches appear in that capture block,
labelled with the unit's name; the primary keeps its "plays on" heading.
Capture inputs remember their Elgato unit and follow a renamed source on
its card, keeping their chains. An ambiguous card stays silent with a
warning. Disabling an additional unit asks whether to keep its capture
inputs and chains, defaulting to deletion.

Input racks and the Broadcast and Wave matrices show the source and the chain
button through their `binding` and `effects` parts. The chain window keeps
presets, A/B, copy and paste, native editors and latency readouts; its picker
filters by the input's width. Flow cards include the chain, and microphone
capture inputs draw a one-bar meter. Older daemons without a chain width show
no capture Inserts button. The terminal mixer edits capture chains too, at
their published mono or stereo width, and shows mono capture microphones
with one meter bar. Mic setup and the microphone EQ, in software, also
run on mono capture inputs.

### Separate mixer window

The submixer can be moved into its own resizable window for a second or small
display. It keeps the live controls, compact and mini views, skins and touch
sizing. Its mixer size picker draws the panel at 125% to 200%, at a custom
size typed from 100% to 300%, or fits it to the window: the largest size,
never under 100%, at which the whole panel shows without scrolling. Closing it returns the panel to the UI at its usual
size. See [Separate mixer window](manual.md#mixer-size)

### Exclusive channel groups

A group marks several channels of which only one is heard, such as a
broadcast microphone and a headset. Unmuting a member's send in any mix
mutes the other members in every mix, so switching is one click; levels
stay as set. Groups are part of the saved layout, edited from the layout
editor or the API, and a Stream Deck key hands a group to its next
member. On a Wave XLR Pro, XLR 1 in a group gives up the zero-latency
hardware path to the jacks, because the group mutes through the mixer.
See [Exclusive groups](manual.md#exclusive-groups).

### Soundboard

Short clips play into the SFX channel, so SFX's fader, mutes and sends
into Stream, Chat and the monitor mixes decide where they are heard. The
native helper holds up to 64 clips decoded in memory and plays up to eight
at once, each press a single line to it and nothing rebuilt in the graph.
A clip has its own gain, a loop switch and a press mode: overlap, restart,
toggle or hold. Imported WAV, FLAC, Ogg Vorbis, Opus and MP3 files, up to
30 seconds and 64 MiB each, are copied into OpenXLR's own data folder and
checked in a separate process before the player opens them. Renaming or
hiding SFX keeps the clips playing; deleting it turns the board off until it
is turned on again, which creates SFX anew. An OpenDeck key plays a clip
or stops them all. A clip can also be auditioned. The player's second
stereo output then plays it once in the monitor mixes the outputs play and
in no other mix, so the headphones hear it and the stream never does.

In the UI, the Soundboard window opens from a Soundboard button in the
SUBMIXER header after Ducking, lit while a clip plays, from the tray menu
and from Options. It shows a pad per clip with its icon, name, length and
mode, filled with the clip's colour when it has one, with lettering that
stays readable on any colour, a ring while it plays and a bar for how far
along it is;
a pad plays on the pointer or on Space and Enter, and a hold pad stops when
it comes up. Its header has the board's switch, SFX's own volume as the
master, Stop all, and adding files or a whole folder through the desktop's
file picker or by dropping them on the window from a file manager. Clips
can be grouped on pages, drawn as tabs, and a folder comes in as a page of
its own. Each pad's menu edits, auditions, stops, moves or deletes its clip. The whole
library, clips, settings and pages, exports to one zip file and imports on
another computer with the same ids, so Deck keys and desktop keys made for
it keep working; an import can add to the clips there or replace them,
and every file in it is checked as an added file is. Eighteen CC0 starter
sounds, such as applause and an air horn, come on a page of their own: the
daemon adds them once, after any clips there, and Restore starter sounds
adds back the ones deleted since. See
[Play sound clips](manual.md#play-sound-clips). The terminal mixer has the
board in its Sounds tab ([manual](manual.md#terminal-sounds)); the
[API](api.md#soundboard) drives it from scripts.

## Inserts

LV2, CLAP and VST3 effects can form a mono chain on each XLR input, a mono
or stereo chain on each capture input, and a stereo chain
on every mix, including virtual microphones you add. The
picker filters by format, name, category and compatible channel width; a
VST3 effect that reports stereo buses but accepts a mono layout when asked
is offered for the inputs, since the helper asks each plugin as it scans.
Unsupported host requirements are reported instead of loading a plugin
that the chosen backend cannot run. VST2 is not supported.

| Format | Processing host | Discovery |
|---|---|---|
| LV2 | PipeWire filter-chain by default; optional native host per insert, which also takes over an insert the filter chain refuses | lilv, standard LV2 directories or `LV2_PATH`, plus added folders |
| CLAP | native host, one process per insert | standard CLAP directories or `CLAP_PATH`, plus added folders |
| VST3 | native host, one process per insert | standard VST3 directories or `VST3_PATH`, plus added folders |
| Windows VST3 / CLAP | yabridge and Wine behind the native host | bridge-generated Linux wrappers |

The plugin manager in Options lists every folder the three formats search
and takes folders of your own, whose plugins are used where they are,
without a copy. The list survives a restart. An added LV2 folder is on the
search path of the filter-chain and the native host as well as the
catalogue. The same window rescans every format and manages the Windows
plugin folders. See [Plugin folders](manual.md#plugin-search-folders).

The Inserts row provides bypass, ordering, removal and generated parameter
controls, and the button that opens a chain is green while a plugin in it
is processing and red when none is; an empty chain keeps the plain button. Native plugin editors open on the instance processing the audio.
Every chain is rebuilt on its own: an edit on XLR 2 leaves XLR 1's plugins
running, and a profile recall rebuilds only the chains it changes and
moves the controls of the others live.
For LV2, enabling "Native host" moves only that insert out of filter-chain
and briefly interrupts its chain. CLAP and VST3 always use that host.
Packages include the helper; source builds need
`-p:EnableNativeLv2Host=true`. When PipeWire's filter chain refuses an LV2
insert, for example on a PipeWire built without its LV2 loader, the helper
runs that insert instead, one insert at a time, and its saved choice stays
as it was. Without the helper the insert shows the refusal. The helper
scans CLAP/VST3 bundles in separate processes and caches their descriptions
until the bundle or the helper itself changes, so an updated helper reads
every installed bundle once.

Each insert's controls show the latency its plugin reports, or that it gave
none. Optional latency compensation, off by default, delays the faster mixes
so they line up with the mix whose inserts take longest, up to two seconds;
a plugin with no figure counts as zero. Turning it on or off rebuilds every
chain. See [plugin latency](manual.md#plugin-latency).

A block size option forces PipeWire's quantum for the graph OpenXLR runs
in to 256, 512 or 1024 samples, or leaves it to PipeWire. It holds against
other applications' requests, and it works on a Wave XLR Pro in the
pro-audio profile too, where PipeWire ignores ordinary requests. See
[block size](manual.md#block-size).

Chains and exposed parameter values are saved with the mixer and profiles.
Opaque plugin state, loaded sample files and plugin preset data are not
persisted by OpenXLR. A plugin may save its own preferences separately.

The chain window copies effects from one chain to another, input or mix,
and renames an effect without restarting it. Two states of a chain can be
stored as A and B and switched to compare them by ear. Whole chains and
single effects save as named presets of their control values. A preset
loads on any chain whose width its plugins support, and exports to a file
for another machine.
See [effect presets](effect-presets.md) and the
[manual](manual.md#effect-presets).

Mic setup, on XLR 1, XLR 2 and mono capture inputs, is a window in four
steps, free to take in any order. It opens from the SUBMIXER header on the
primary microphone or from a chain window on that input, and a
**Microphone** picker moves it between every microphone the mixer can set
up, an additional interface's included, stopping a session on the one it
leaves. The window sizes itself to the step it shows, up to the screen's
height. **Position** gives placement tips for
the kind of unit the microphone is on (a Wave:3's, Wave:3 MK.2's or Wave:1's capsule, an
XLR jack, or a device OpenXLR does not drive) and the unit's phantom power
switch where it has one. **Level** shows the microphone before any processing on a large
meter, with a band for speech, a clip light that holds until cleared and
the interface's gain beside it where the input has one. **Suggest gain**
listens to five seconds of speech and proposes a gain for the interface
that brings the voice into the band without clipping, and writes it only
when you accept; for a microphone whose gain OpenXLR does not set, it says
how much to change it on the device. **Listen** records
up to ten seconds and loops that dry take through the channel's software
processing and insert chain in place of the live input, so the effects can
be tuned while the same words repeat, with the take's waveform and a
playhead; the end of the take crossfades into its start, so the loop does
not click where it starts over. **Processed** and **Raw** flip between the microphone with and
without its processing on the same take, in the loop helper with a 20 ms
crossfade and no chain rebuilt, and **Hear myself** puts the processed
microphone in the monitor mix for the session only, and the window says
how much latency that adds. **Shape** holds the microphone EQ's graph,
edited in place, the unit's and XLR 1's software low cut and ClipGuard, the
insert chain with bypass, editor and add effect, and three starting
presets of OpenXLR's own (Clear, Warm and Broadcast), each applied as one
EQ change with a one-step undo and lit while the EQ holds it, all heard
live while the take loops. On
the Wave XLR Pro the session turns the zero-latency
path off, since it cannot carry effects. The take stays in the native
helper's memory and is discarded on stop; a session ends by itself on a
source change, a lost audio path, capture channel deletion, a mixer rebuild
or after ten minutes. It works without any insert, and one session runs at
a time across all microphones. Stereo capture inputs have no Mic setup. It
needs the native plugin host. See [Mic setup](manual.md#mic-setup) and the
[API](api.md#sound-check), where the session is called Sound Check.

"Install file" and "Install folder" accept extracted Linux plugins or
Windows plugin folders. Linux bundles are copied into `~/.lv2`, `~/.clap`
or `~/.vst3`. Windows installers must run in Wine first; OpenXLR bridges
the installed plugins and rescans automatically. Options offers a rescan,
a Windows sync, detected Wine folders and the selected bridge's status.

The `openxlr-yabridge` companion is packaged for every channel OpenXLR
is, the Flatpak bundle apart,
and includes the Wine 9.22+ editor input fix. It creates wrappers under
`~/.local/share/openxlr/yabridge` and keeps its controller registry under
`~/.config/openxlr/bridge`, honoring XDG overrides. Other DAWs' wrappers and controller settings remain separate.
Private wrappers take priority over duplicate global copies. A system/user
bridge remains supported; Options warns about known incompatible versions.
Options also checks the running daemon's memory-lock allowance for Windows
plugins and links directly to the relevant memory-lock or editor-input fix.
See [Windows plugins](manual.md#windows-plugins) for availability and setup.

The catalogue sent to clients is bounded. While it fits, all formats are
offered. Beyond the limit, the plugins the saved chains use are kept
first, LV2 entries retain priority over the formats that follow, and
other formats fill the remaining space, with duplicate names last. The
daemon itself keeps every scan whole, so an insert loads and reports on a
plugin the list had no room for.

Editor borders follow each plugin's size limits. TDR Nova uses its own UI
scale menu instead of border resizing. LSP editors default to software
rendering to avoid freezes during large drags. Display loss and stalled
editor controls are handled without rebuilding a healthy audio instance;
a crash of the plugin process interrupts its chain. Repeated chain failures
stop automatic retries after three failures in five minutes. See
[plugin editors](manual.md#plugin-editors) for recovery and renderer settings.

The submixer can be switched off in Options. The daemon then controls
the hardware only, restarts itself, and leaves the sound card in its
stock PipeWire layout; mixes, virtual microphones and inserts go away
with it. For the Wave XLR Pro there is an experimental ALSA UCM profile
in `packaging/ucm/` that splits the raw 17/18-channel card into named
PipeWire devices (Monitor, Line 1 to 3, XLR 1, XLR 2) for that mode, or
for running without OpenXLR. It is a manual root install with a
matching revert script and is not shipped by any package. While the
submixer runs, the daemon parks the card on its pro-audio profile and
restores the split profile when it stops.

## Application routing

- Automatic app routing can be disabled in Manage or the terminal mixer's
  Apps tab. Unassigned apps then stay not managed until assigned by hand;
  saved assignments keep working. The choice survives restarts and profile
  recalls. Not managed playback can still use an OpenXLR system default

- The optional [Chromium companion](browser-routing.md) lists individual
  tabs in a Browser tabs window, opened from the button beside Manage in
  APPLICATIONS, which also holds the option that turns tab routing on.
  User-activated tab audio can follow
  separate application channels, with saved website defaults. Activate
  in a tab's row brings the tab forward in the browser and starts it, or
  says what the browser still needs. Browser
  output selection suspends generic Chromium routing while enabled,
  keeping each browser's saved assignment and showing where it plays
  meanwhile; restarting the browser requires activating tabs again

- The companion follows the browser's language. `tools/browser-extension.py`
  packages it for the Chrome Web Store, and as the tar.gz each release
  offers for loading unpacked. Its
  [privacy policy](browser-privacy.md) lists what it reads and keeps.

- Audio clients are detected from their PipeWire client registration
  and playback metadata, falling back to the owning client when fields
  are missing. Windows `.exe` identities normalize to their Wine/Proton
  key; an existing normalized assignment wins over an old alias. Apps are
  assigned to a channel by name rules; each assignment is stored in
  the app registry and can be edited while the app is silent
- Electron apps report "Chromium" as their application name; they are
  identified by their process binary instead, so Discord appears as
  Discord
- Every app in APPLICATIONS shows its icon. The daemon takes the icon
  name the app publishes to PipeWire, or finds the app's desktop entry by
  its Flatpak or application id, its process binary or its name, in the
  XDG data directories, the Flatpak exports and Snap's. The UI looks
  the name up in the desktop's icon theme, then hicolor and the pixmaps
  directory, and draws a PNG or SVG at its drawn size. An app with no icon
  shows its initial on a badge the skin sizes and colours
- A Manage dialog shows the full registry, and an installed-application
  picker pre-assigns channels from `.desktop` entries
- An app can be set to "Not managed": the mixer hands its streams back to
  the system default output and never touches them again, so a headset
  with separate game and chat sinks keeps its own routing for that app
- The application that has the focus can be sent to a channel from a
  desktop shortcut or a Stream Deck key. The UI reads the focused
  process id from KWin on KDE Plasma, Sway's tree, Hyprland's or Niri's
  active window, X11's local EWMH properties, or the optional OpenXLR Focus
  GNOME Shell extension. The daemon sends live
  audio process keys over D-Bus; the UI matches host processes, including
  Flatpak instances on sources that report host PIDs, and the daemon rechecks
  their identity before routing. The X11 source supports native clients only.
  Sway, Niri, X11 and GNOME still need owner desktop acceptance. An
  application that cannot be identified without guessing is refused

## Profiles

Named scenes: every hardware setting plus the whole submix (send
levels, channel volumes, mutes, masters, monitor outputs, aux state, insert chains with
their parameters). Saved per device and recalled from the header, over
the API, or from a Stream Deck key. One profile per device can be
marked to recall on connect: at daemon start, after a replug or power
cycle, or when switching to that device. Interfaces using OpenXLR's connect-time
restoration (Wave XLR, the first XLR Dock) get their last settings back
on every fresh connect without a profile, and can be reset to the
firmware defaults recorded after a power cycle. The Wave XLR Pro, which
keeps its own settings, can be reset to OpenXLR's baseline instead:
gain 30 dB, everything off, levels at half, the crossfade on PC. With no
Elgato device connected, profiles are kept under `generic` and hold the
mixer scene alone, and recall on connect does not apply. App routing and the
enforced system defaults are global and not part of a profile, so
recalling one does not rewire the desktop.

## OpenDeck plugin

OpenDeck keys and dials follow the UI's saved skin: surfaces, lettering,
status lights, icons, needles and meters share its colours. Changes arrive
through file events without an audio command or a polling timer. Built-in
palettes also work when the UI is closed. See [skins.md](skins.md) for
custom-skin limits and the realised palette file.

`plugin/com.emaspa.openxlr.sdPlugin` is an
[OpenDeck](https://github.com/nekename/OpenDeck) plugin with three
actions, Dial, Toggle (key) and Clip (key). All are clients of the daemon's
WebSocket API, so they reflect changes made in the UI or on the
hardware.

Dials render a touch panel: a knob with a needle, a level meter, the
value readout, and a mute overlay. The meter's colours sit along the bar,
green into yellow at the skin's warning level and into red at its hot
level, and only the bar's length moves with the sound, so the strip never
flashes from one colour to another. Every send, channel volume, mix master, gain,
headphone volume, the crossfade and each desktop output's volume (up to
150%, pressing toggles its mute) is a dial target, and one dial can
hold several targets cycled by tap or press. A turn leads locally: the
dial steps from its own value, sends a burst as one command per 80 ms,
and the daemon's echo cannot pull it back while the turn is warm.

![Dial panels](plugin-dials.png)

Keys render a button with an icon and a status LED: red for a mute,
green for an engaged feature or the active monitor output. Every
hardware switch and mute is a key target, plus the software low cut
(its frequency shown on the LED, cycling Off, 80, 120), ClipGuard, gain
lock, switching the monitor output to a specific device, and cycling
an output's feed through Monitor A, Monitor B, Monitor A+B and then every other mix, an
output's volume in 5% steps and its mute (red while muted), the enforced
system output, routing the focused application to a channel, the
next member of an exclusive group, and ducking on or off on any device. Each key
can pick its icon, and a typed title replaces the built-in label. A key or
dial on a channel or mix shows the icon and colour that channel or mix was
given in the layout editor, unless the key has its own icon; a group key
shows its heard member's.

The property inspector's Microphone controls group offers channel gain,
mute, phantom power, low cut and ClipGuard targets for primary and additional
unit inputs. A target resolves the channel's current unit at each press or
turn, keeps its id through a rename, and becomes unavailable when the unit
or control is unavailable. Legacy interface targets remain unchanged.

Microphone EQ groups offer EQ and band on/off keys, filter-type keys and
dials for a band's frequency, gain, Q and type. The dial press switches
that band. They follow the daemon's reported bands, types, ranges and
steps, retain stable channel ids through renames, and use the channel's
icon and colour. Quick turns lead locally and coalesce through the
existing dial sender; removal, rebinding and reconnection discard stale
unsent EQ edits. Changes use the same EQ and saved state as the UI.

![Keys](plugin-keys.png)

Soundboard: a Clip key plays one clip into SFX, or stops every clip. The
inspector lists clips by name and the key keeps the clip's id, so a rename
does not break it. A press uses the clip's own mode or one set on the key,
and in hold mode the clip ends when the key comes up. The key wears the
clip's icon and colour, or SFX's where the clip has none, lights while the
clip plays and draws its progress along the bottom from the meters. A
deleted clip, a deleted SFX, the board turned off and a missing native host
each read as one grey word on the key, and as the daemon's sentence in the
key's settings.

Voice changer: a Toggle key per microphone and voice switches that voice
on, or off when it is the one playing; in hold mode the voice plays while
the key is down. The key wears the microphone's icon and colour and lights
while the voice plays ([manual](manual.md#voice-keys)).

Profiles: a key can recall one of the active device's saved profiles,
listed live in the property inspector; it lights while that profile is
the last one recalled or saved.

Inserts: the property inspector lists every loaded plugin from live
state. A key toggles one insert's bypass (LED green in the path, red
bypassed) or a whole chain; a dial takes any control of any insert,
stepping along the control's own scale (log, integer, enumeration,
toggle), with its name and value on the panel and the insert's bypass
on the press. A key or dial follows its insert by id, and falls back to
the same plugin in the same chain when a profile recall rebuilds the
chain. Capture insert keys and dials keep their channel id through a rename.
Mic setup stays in the UI.

Install: download `com.emaspa.openxlr.sdPlugin.zip` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
use OpenDeck's install-from-file, or copy the plugin folder into
`~/.config/opendeck/plugins/` (a symlink breaks OpenDeck's asset
serving; the packages ship the folder in `/usr/share/openxlr/`). Touch
taps on the Stream Deck + XL need OpenDeck newer than 2.14.0
([nekename/OpenDeck#437](https://github.com/nekename/OpenDeck/pull/437)).

## Terminal mixer

- `openxlr-tui`: the whole mixer in a terminal, for a machine with no
  desktop session, an ssh connection or a tiling setup. It speaks the same
  WebSocket the UI does, so both can be open at once and each shows
  what the other changed
- A desk of vertical channel sends and separate mix masters, with stereo
  meters, hold markers and fifteen seconds of level history for the chosen
  mix. The scale is RMS dBFS; the hold lasts one second, then falls by
  18 dB per second. Meter colours belong to positions on the scale
- Nine keyboard sections: the desk, the matrix of every send into every
  mix, hardware inputs, outputs and defaults,
  application routing, inserts, profiles, options and the
  soundboard. Wide terminals have
  a section rail and grouped hardware cards with gain arcs. At 80x24 the
  rail becomes a top row and the strips shorten. Both banks scroll to keep
  the selection visible. F1 or `?` lists all keys. A plugin is added from
  a picker over the catalogue, narrowed by typing, of the plugins that fit
  the chain's width
- Ducking in Inputs on any device: the switch, a key that cycles through
  the microphones, a row of keys for the channels to lower and one for the
  mixes, the three presets, and on a Wave XLR Pro the choice between the
  unit and this computer with the reason the unit does not run it. A short
  bar shows the reduction while software runs it
  ([manual](manual.md#terminal-ducking))
- Capture microphones in Inputs show their source, connection and carried
  unit controls. Inserts includes mono and stereo capture chains, using the
  width the daemon reports; an older daemon keeps XLR and mix chains only
- Microphone EQ in Inputs: open a microphone's bands to edit each band's
  switch, type, frequency, gain and Q, plus the EQ's own
  switch. Enter on a number asks for an exact value. The shared EQ model
  supplies the ranges, steps and defaults; reset keeps the EQ's on/off
  state and changes only that microphone
- The soundboard in Sounds: the board's switch, SFX's volume as its
  master, and a row per clip with its length, mode, loop, gain, status or
  reason, and a bar while it plays. Keys play, audition, stop, stop all,
  step the gain, mode and loop, reorder, rename and delete, `i` imports a file
  by its typed path, and `R` restores the starter sounds. Pages stand as tabs over the rows; keys switch page,
  move a clip to a page and rename a page. A Hold clip plays on one press and stops on the next,
  since a terminal sends no key-up ([manual](manual.md#terminal-sounds))
- The voice changer in Inputs: a choice of Off or a voice per microphone,
  and the custom voice's pitch and formant
- Insert editing opens the plugin's native editor. A blocked or refused
  editor falls back to terminal controls with the reason shown. Controls
  follow the catalogue's order, defaults, switches, named scale points and
  integer ranges, with ratio steps for positive logarithmic ranges. Escape
  returns to the chain and `r` resets the controls to their defaults
- The same skins as the UI, read from the same folders and following
  the same saved choice, so an appearance chosen in one is the appearance
  in the other. Console faders, cap keys and lamps follow the skin's
  control choices, and every meter is a solid bar that blends the skin's
  fill, warning and hot colours along its scale. Light skins use their own
  colours for the selection and cap lettering. `--skin <id>` tries one for a single run.
  A terminal without true colour gets xterm-256 colours
- It draws its own cells rather than taking a widget toolkit, so it adds no
  dependency to any package. A frame writes only the cells that changed

## Omarchy shell plugin

- `openxlr.mixer`: up to four microphone meters in layout order, including
  mono capture inputs, with hardware mute state in the Omarchy 4 bar, beside
  the selected monitor output's feed and meter. Inputs appear
  after signal and stay for 10 seconds after it falls quiet. Muted inputs
  dim, labels keep the bar's regular body font, and levels do not move
  neighbouring widgets while an input is shown
- A popout of channel sends and mix masters, with volume and mute
  controls, hardware mute on each primary or additional unit carrier, and
  the terminal mixer's block meters and strip layout in the bar's colours
  and font
- Popout meters take the OpenXLR skin matching the active Omarchy theme,
  with foreground shades for unmatched themes. Theme changes repaint the
  meters; the bar stays monochrome
- Whole-strip pages with visible range controls, friendly output names,
  and the same screen bounds as Omarchy's first-party audio panel
- Authenticated daemon events, quiet disconnects and bounded reconnects.
  The Arch package supplies the files on Omarchy; a user command links
  and enables them. See [omarchy.md](omarchy.md) for the recipe and limits

## Other

- Options stays open beside the mixer without blocking its controls. The
  header's gear returns to the same window, including when it is minimized.
- Enforced defaults: the daemon re-asserts the chosen system default
  sink and source on its one-second sweep, undoing WirePlumber's
  auto-switch to newly created nodes. The defaults to restore are read
  at the top of the daemon process, before the device connects, since
  connecting switches the card's profile and WirePlumber then moves the
  defaults to the card's new nodes
- Capture hold: WirePlumber rules keep the capture node of the XLR Dock,
  the original Wave XLR, the Wave:3 and the Wave:3 MK.2 running. On those
  devices a playback stream opened before the capture stream silences the
  microphone for the life of that capture stream, so capture is never
  allowed to suspend. A further rule opens the Wave:3 MK.2's headphones
  in the unit's float format, since the two-channel format PipeWire picks
  stalls its microphone
- Desktop monitor volume: the default output can follow the first selected
  MONITOR device. Keyboard volume controls and the desktop applet then use
  the same output volume as the mixer in the UI, including synchronization to
  the other selected monitor devices, without changing Stream or Chat levels.
  A `150%` button next to MONITOR and next to each monitor mix opens a boost
  range above unity; a boosted value arriving from the desktop opens it on its
  own, so the UI and the OpenDeck dials show the boosted level instead of
  stopping at 100%. On KDE Plasma 6 these buttons share one range with the
  desktop's Raise maximum volume setting and follow it in both directions.
  Other desktops keep a range per control.

- The control API validates every command before the mixer sees it and
  answers with an error instead of ignoring it. The Unix socket checks Linux peer credentials
  before any HTTP or WebSocket data is read; clients are rate-limited
  and browser pages from other origins are refused; see
  [api.md](api.md). The same commands are served over HTTP at `/api/v1`
  with an OpenAPI document ([http-api.md](http-api.md)), next to read
  resources for devices, profiles, the mixer, single channels, mixes and
  insert chains, the soundboard, plugin setup and diagnostics
- The daemon follows the PipeWire graph through one `pw-dump --monitor`
  subscription and keeps an incremental snapshot keyed by object id, so
  the one-second sweep reads memory instead of launching a dump. A
  disconnect discards the whole snapshot before ids can be reused and
  reconnects with a delay growing from 250 ms to 5 s; until it is back, a
  one-shot `pw-dump` serves the sweep, so routing, default enforcement
  and route repair keep working. Route repair checks each link against
  that snapshot and runs `pw-link` only for a link that is missing; a
  monitor, microphone or USB Aux route that came up with a side missing
  is linked again rather than kept as healthy
- The daemon rebuilds its graph after a pipewire-pulse restart, retries
  a graph build the audio server did not answer at 2, 4, 8 and 16 s and
  then every 30 s while the state warns that the mixer is waiting, and
  refuses to grow the layout when pipewire-pulse has no open-file
  headroom left; the packages raise that limit with a systemd drop-in.
  It also holds every sink it created at full volume and unmuted, since
  a desktop applet or the session manager can turn one down and quietly
  cut the mixes it feeds
- Skins: every surface, label, indicator, meter, fader and control in the
  UI reads a named appearance value. A skin is a folder with a
  versioned `skin.json` that replaces some or all of those values and picks,
  per control, one of the appearances OpenXLR itself draws: a flat or a
  console fader, a continuous or a segmented meter, a flat dot or a lamp in
  a bezel, a flat or a bevelled mute cap. A meter is coloured by position on
  its scale, so a skin can give it a hi-fi green, amber and red ladder
  without the bar ever repainting itself end to end. What a skin leaves out
  keeps the shipped default. Seventeen appearances ship, all compiled into
  the application: Material, the classic look, Deck, built from the
  OpenDeck plugin's key and dial art, one for each of the eleven
  Omarchy palettes, two of them light, Nyan Pink, Broadcast, Compact Desk
  and Wave. The last three also arrange the UI. Broadcast is the default: a new
  install, or one that never picked a skin, starts on it, and a skin the
  user picked, Material included, is kept. The picker is in Options, the
  choice lives in `ui.json` alone, and switching
  repaints open windows without touching audio or the layout. A skin is
  data. It carries no markup and no code, reaches no file outside its own
  folder, makes no network request, and its images are bounded and measured
  before they are decoded. It cannot change what a control does either: the
  console fader is the framework's slider with OpenXLR's drawing over it,
  and a plugin's own editor window is drawn by the plugin and is not
  skinned. [skins.md](skins.md) is the contract
- Material in System, Light or Dark mode: System follows the desktop's light
  or dark preference, in the UI and in the terminal mixer
- Touch sizing in the UI: larger buttons, faders and list rows,
  wider channel strips and mix tiles, chosen in Options and kept in
  `ui.json` beside the skin, over whichever skin is worn
- A translatable UI: every string it shows comes from one catalogue,
  and it ships in English, German, Spanish, Italian, Tagalog, Cebuano (Bisaya) and Serbian (Cyrillic). Languages come from
  contributors ([localization.md](localization.md)); a language chosen in
  Options applies the next time the UI starts
- One UI per user: a second launch brings the running UI to the
  front, out of the tray if it is hidden there, and exits
- Tray icon, start-minimized option, daemon and UI autostart from
  Options
- Diagnostics archive: one action collects app and device state, a
  vendor block dump, the PipeWire graph, daemon logs and configs into a
  tarball for bug reports. It also includes plugin catalogue and bridge
  setup, effective search paths and bounded native scan outcomes, with
  personal paths, the login name and host names redacted, and no forced
  sync or insert changes. For audio glitches it adds the PipeWire,
  pipewire-pulse and WirePlumber journals, node statistics, and playback and
  recording stream metadata. Window and daemon build identities, state
  receive time and connection status distinguish source rebuilds and
  retained state from an earlier connection
- Deep Wine trace checkbox in Options, SUPPORT for reproducing a bridged
  plugin scan failure. Enable it, Rescan, collect diagnostics, then disable
  it. Scans get much slower and produce large logs. The switch takes effect
  without a daemon restart and is not saved across restarts

### Installation checks

`openxlr doctor` checks recorded package files and compares the
running daemon with the installed build. It reports missing, changed and
stale owned paths with repair commands, without changing audio or opening
the UI. Options records exact startup files and original backups,
migrating recognized files from older versions before removal. See
[installation.md](installation.md).

### Desktop keys

The UI registers global shortcuts through the desktop's GlobalShortcuts
portal, which works on Wayland, and keeps them active while it sits in the
tray. The daemon owns no shortcut session. A key routes the application
that has the focus to a chosen channel, steps an output's volume by five
percentage points within 0 to 150%, toggles its mute, or switches the
enforced system output. A volume or mute key follows the current default
output or stays bound to a named output or monitor mix. On a selected
physical monitor output the mute key pauses the whole monitor selection,
the same as MONITOR's Mute button or a Deck monitor dial press. Mix and
channel mutes keep their values. Microphone sends pause too when that option
is enabled in AUDIO. A separate monitoring pause key addresses this state
directly, without a desktop-wide mute of internal routing sinks.
On a monitor mix sink the key toggles only that mix; on any other output
it toggles the sink's native mute. A key can also play a soundboard clip
chosen in the list, or stop every clip. A clip's key carries its name and
follows its renames and its deletion. A hold clip plays while its key is
down on KDE Plasma, GNOME and Hyprland, whose portals report the key coming
up; on Plasma any key coming up or another shortcut ends it. Elsewhere it
plays as overlap until the desktop first reports a key coming up. A clip
keeps its key unless the daemon is seen deleting it, so an unreadable
library drops no binding. Focus
identity comes from KWin on KDE Plasma, `swaymsg` on Sway or `hyprctl`
on Hyprland, `niri` on Niri or local EWMH properties through `xprop` on X11.
GNOME Shell uses the optional [OpenXLR Focus extension](gnome-focus.md),
whose endpoint answers only the running OpenXLR desktop service.
Flatpak audio clients are matched by their portal instance and process namespace
when the focus source reports a host PID; X11 cannot resolve their sandbox PIDs.
Other compositors need a focus source of their own. An
unavailable GlobalShortcuts portal leaves the Deck focus service available;
PC shortcuts still need that portal. See
[Desktop keys](manual.md#desktop-keys),
[Soundboard keys](manual.md#soundboard-keys) and
[Output keys](manual.md#output-keys).
