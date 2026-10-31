# OpenXLR manual

The experimental Flatpak has its own [startup and hardware setup](flatpak.md).

This manual follows `main`. A released package can lag behind it; read the
manual at that release's tag for matching behaviour. The app's Manual links
open this page on GitHub `main`.

How to use OpenXLR day to day: what it changes on your system, the
concepts behind the mixer UI, step-by-step tasks, and what to do
when something does not work. For installing, see the README; for the
full list of controls per device, [hardware-support.md](hardware-support.md);
for scripting, [api.md](api.md). Every section carries a fixed anchor
(the `<a name>` before its heading), so a link like `manual.md#open-files`
keeps working when sections are renumbered.

<a name="first-run"></a>
## 1. First run

OpenXLR is two programs. The daemon (`openxlr-daemon`, a systemd user
service) builds the PipeWire mixer, talks to an Elgato interface over USB
when one is attached, and keeps running whether or not the UI is open.
The UI
(`openxlr`) shows and changes what the daemon holds; closing it changes
nothing.

The gear in the header opens Options in its own window, so you can keep
using the mixer while adjusting settings. Pressing the gear again brings
the same Options window forward, restoring it if minimized. Closing
Options leaves the mixer running; quitting OpenXLR closes both windows.

When the daemon starts with the submixer on (the default), these things
happen on your system:

- New audio devices appear in your desktop's sound settings, all named
  `OpenXLR …`: one output per channel and one input per virtual
  microphone. By default that is `OpenXLR Game`, `OpenXLR Music`,
  `OpenXLR Browser`, `OpenXLR System`, `OpenXLR Voice Chat` and `OpenXLR
  SFX`, plus `OpenXLR Monitor A` and `OpenXLR Monitor B`, as outputs, and
  `OpenXLR Stream` and `OpenXLR Chat` as inputs; the hardware channels are
  not listed as playback devices. A layout you have edited
  ([section 3.12](#layout)) comes back as you left it.
- Unless automatic routing is disabled in Manage, applications that play
  audio are moved onto a channel output by name
  ([section 2](#concepts)). They keep playing; only the device they play into
  changes.
- Your system default output and input are left as they were. The
  daemon reads them before it touches anything and puts them back if
  the session manager switches to one of the new devices in the
  following seconds.
  If you set defaults in Options ([section 3.7](#default-devices)), those are held instead.
- On the Wave XLR Pro the daemon parks the card on its pro-audio
  profile while it runs, so the raw multichannel device is available to
  the mixer, and restores the previous profile when it stops. If the
  card is unplugged in the meantime, the daemon leaves it alone.

The UI's header shows the connected interface with a green dot. With
more than one interface attached, the name there is a picker, and the one
you pick is driven again after a restart, even when the other one appears
first. "No device" means the daemon cannot open the interface: replug it once
after installing so the udev rule applies ([section 5.1](#no-device)).

<a name="without-elgato"></a>
**Without Elgato hardware.** The mixer works with any microphone, headset,
webcam or audio interface PipeWire can record from. With no Elgato device
attached the header reads **No interface.** followed by a link, **Choose a
microphone for XLR 1**. Pick your microphone and its port there, then Save
([Use another microphone on XLR 1 or XLR 2](#input-bindings)). The strip is
then named Mic 1 and carries that microphone into every mix, with its
volume, meter, EQ, inserts, Mic setup, software low cut and ClipGuard
(ClipGuard needs `swh-plugins`). XLR 2 stays hidden until you bind it in
Options, AUDIO, **Input bindings**.
More sources can join as capture channels ([Additional capture
inputs](#capture-inputs)). Set the microphone's gain and phantom power on
the device or its own interface; OpenXLR has no controls for them.
Application channels, virtual microphones, monitor mixes and mix inserts
work as the rest of this manual describes. Profiles are kept under
`generic` and hold the mixer alone.

Gain controls in the UI, the terminal mixer and the Stream Deck dials
cover the connected interface's own range: 0 to 80 dB on the Wave XLR Pro
and the MK.2 family, 0 to 75 dB on the original Wave XLR and the first
XLR Dock, 0 to 40 dB on the Wave:3, Wave:3 MK.2 and Wave:1. The Wave XLR Pro's Line In
gain stops at 40 dB ([Line In](#line-in)). Options shows the firmware
version under INTERFACE on an interface that reports one: the Wave XLR
Pro, the MK.2 family, the Wave:3, the Wave:3 MK.2 and the Wave:1. On the Wave:3 and Wave:1
the desktop's sound settings and OpenXLR also
share the microphone mute and the headphone level, so a change in either
shows in the other within a second. Wave:3 support is written from
public protocol research and works on the maintainer's unit: gain,
mute, headphone volume and the monitor balance. The Wave:1 uses the
Wave:3's backend: an owner's diagnostics showed the microphone
recognised and every control read correctly, and a second run confirmed the gain, mute and headphone
volume set from OpenXLR on the microphone itself. ClipGuard and the
mic/PC mix are not confirmed yet; if you own one, change them, leave
them changed, collect diagnostics and open an issue
([hardware support](hardware-support.md)).

The Wave:3 MK.2 speaks the Wave XLR MK.2's protocol, not the Wave:3's,
and OpenXLR drives it with the MK.2 backend. Gain, mute, headphone
volume, the mic/PC crossfade, the microphone EQ, low cut, expander,
voice tune and its strength, compressor and ClipGuard all run on the unit
and work on the maintainer's unit. The microphone keeps the
desktop's capture and playback volume in step with its gain and headphone
level by itself. It has no phantom power and no low impedance mode. Nobody has checked yet
whether it keeps its settings when unplugged, so OpenXLR writes nothing to
it on connect. Its microphone can stall while its own headphones play,
giving silence or a buzz: OpenXLR resets it by itself, and Options,
INTERFACE, has **Recover microphone** for a stall it misses
([section 5.2](#dock-silent)).

If you only want hardware control and no mixer, turn off "Enable software
mixer" in Options, AUDIO ([section 3.8](#hardware-only)). The daemon restarts in hardware-control mode
and the `OpenXLR …` devices disappear.

Update checks are also controlled in Options. They are off by default. You can
make one explicit check with **Check now**, or opt into a background check at
startup; opted-in checks run at most once per 24 hours. A notice contains the
upstream stable release notes as plain text and a link to GitHub. OpenXLR never
downloads or installs an update. **Dismiss** hides that release's notice,
including when another check is still finishing. A newer release can show
a new notice.

<a name="concepts"></a>
## 2. Concepts

**Channels** are where audio enters the mixer. Four carry the
interface's inputs: XLR 1, XLR 2 where the device has one, Aux In for the
Pro's USB Aux input and Line In for its analog Line In jack. Aux In and
Line In are separate channels and are never mixed into one. XLR 1 and XLR
2 can take any other capture source instead ([input
bindings](#input-bindings)). Only the Wave XLR Pro has the second jack, the
aux stage and the Line In, so on every other model the UI, the terminal
mixer and the Omarchy bar leave those strips out rather than offering a
fader that reaches nothing. Line In also needs the Pro's capture source in
its 18-channel layout; on a source with fewer channels it stays hidden.
The channels keep their levels, so plugging a Pro back in brings them back
as they were. While no interface is connected the strips stay as the last
one left them. Until one has connected since the daemon started, XLR 2,
Aux In and Line In show only while a supported interface is attached, so a
desk without an Elgato interface shows XLR 1 alone. The rest carry
application
groups: Game, Music, Browser, System, Voice Chat and SFX by default, and
whatever you add, rename, hide or remove in the layout editor
([section 3.12](#layout), [Show hidden](#show-hidden)). Each channel is a
PipeWire output device an application can play into.

**Mixes** are where audio leaves. The default layout has five; Monitor A,
Monitor B, Stream, Chat and any mix you add, a virtual microphone or a
monitor mix, can be renamed and removed, as long as one monitor mix
remains, and every mix can be hidden ([section 3.12](#layout)):

| Mix | What it is | Where it goes |
|---|---|---|
| Monitor A | what you hear | the ticked outputs in the MONITOR card, unless their feed is set to another mix |
| Monitor B | a second selection to hear | the ticked outputs whose feed picker ticks Monitor B, summed with A on outputs where both Monitor A and Monitor B are ticked |
| Stream | what your audience hears | the `OpenXLR Stream` virtual microphone, for OBS or any recorder |
| Chat | what your call partners hear | the `OpenXLR Chat` virtual microphone, for Discord, Zoom and the like |
| a virtual microphone you add | whatever you route into it | its own `OpenXLR <name>` virtual microphone |
| a monitor mix you add | another blend to hear, for headphones or speakers | the ticked outputs whose feed is set to it; it publishes no microphone |
| Aux | what a second computer receives | the interface's USB Aux port (Wave XLR Pro only) |

An output is not tied to the monitor mixes: its feed picker can name any
mix or a sum of mixes ([section 3.2](#monitor)). A
blend at other levels is a mix of its own, with the sends set there.

The daemon repairs lost internal links into mix inserts and virtual
microphones during its normal routing sweep. A temporarily refused stereo
side is retried without restarting the running plugin or dropping the other
side. This also applies to virtual microphones without inserts.

Every channel has a **send** into every mix: a level and a mute. The
SUBMIXER card shows them as a grid, channels down, mixes across. Each
mix also has a **master** level and mute. A typical setup keeps Music
loud in Monitor A and lower in Stream, and out of Chat entirely.
The header's actions wrap onto another line when the card is narrow,
including in skins that place the mixer beside other sections.

<a name="channel-volume"></a>
Every channel also has a **volume** of its own. It turns every send of
the channel up or down together, so Game can get quieter everywhere
without changing how its sends sit against each other. At 50% each send
is 50% of what its own fader says. The mutes stay per send. Every channel
starts at 100%, and so does each one in a profile or settings file saved
before it had a volume. The channel's meter shows what plays into the
channel, before its volume.

XLR 1, XLR 2, Aux In and Line In have a volume too. It comes after the
interface's gain, the Aux In level or the Line In gain and after the
input's plugins, and leaves them
as they are: set the gain for the microphone, then use the volume to set
how loud the input is in the mixes. A strip bound to a webcam or a USB
microphone, which has no gain in OpenXLR, keeps its volume, and the volume
stays as it was when the binding changes.

In the SUBMIXER tiles the Volume row sits above the sends. In an input
rack it is under the gain, the Aux In level or the Line In gain. In a mix
matrix row head it is a second fader under the gain or the level, and the
row is as tall as the two faders need; a row without a gain, such as a
bound XLR 1, shows only the volume. Against an older daemon that gives
application and capture channels a volume and the hardware inputs none,
the hardware inputs leave an empty space where the Volume row would be, so
each send row sits at the same height in every tile.

<a name="gain-readout"></a>
Aux In, Line In and a capture input with a [gain](#capture-gain) each have
a readout that shows the input's level or gain in dB: on the USB Aux in
and Line In rows and the capture input's Gain row of the INPUTS card,
beside the level or gain in an input rack, and in the name column of a mix
matrix row. While you move the channel's volume fader, by dragging it or with the
arrow keys, wherever it is drawn, the readout shows the volume as a
percentage instead, and it goes back to the level or gain one second after
the fader stops. A screen reader reads the readout under the name of what
it shows. XLR rows in a mix matrix do the same with the input's gain.

<a name="volume-direct-monitor"></a>
On a Wave XLR Pro, the [zero-latency path](#input-bindings) to the
headphone jacks is inside the interface. It carries the microphone
straight to the jacks, past the mixer. While the
path is on, the channel that carries the Pro's microphone shows a small
ⓘ mark beside its volume. OpenXLR sets the path's level from the
microphone's Monitor send, its channel volume and the Monitor mix's
volume, so these change how loud you hear yourself just as they would
through the computer. The path cannot carry the channel's software
effects, so OpenXLR turns it off while one is on: the EQ in software,
noise removal, the voice changer, the soft low cut, ClipGuard or a plugin
insert that is not bypassed. The headphones then hear the microphone
through its Monitor send with the effects, a few milliseconds later, and
the ⓘ mark goes away. Turn the last effect off and the path comes back.
An EQ the unit runs keeps the path.

### Submixer categories

The SUBMIXER shows the channel strips and their sends first, right under
its header, then **OUTPUT MIXES**, the master volumes and effects of the
resulting mixes. Application audio and external capture sources have channel
strips too. Output mixes include monitor mixes and virtual microphones; they
are not a list of physical output devices. The MONITOR section still chooses
the physical outputs that receive these mixes.

**Application routing**: when an app starts playing, the daemon reads
its name from PipeWire and picks a channel by rules: browsers to
Browser; Spotify, YouTube Music and media players to Music; Discord,
Zoom, Slack and other chat apps to Voice Chat; Steam, Lutris, Heroic
and games to Game; apps named like a soundboard to SFX; anything else to
System. Electron apps report
"Chromium", so the process name is used instead, which is how Discord
lands in Voice Chat. An app you move to another channel is remembered
([section 3.3](#apps)).

**Profiles** are named scenes: the interface's hardware settings plus
the whole submixer (sends, channel volumes, masters, monitor outputs, aux state, insert
chains). They are saved per interface. Application routing and the
system default devices are not part of a profile, so recalling one
does not rewire the desktop.

**Inserts** are LV2, CLAP or VST3 effects placed in the signal path: a mono
chain on each XLR input, a mono or stereo chain on each capture input,
a stereo chain on each mix. On an interface whose
backend exposes no hardware low cut or ClipGuard, OpenXLR offers the
missing one in software on XLR 1: both on the first XLR Dock and the
original Wave XLR, the low cut on the Wave:3 and Wave:1.
Intermediate DSP and mix-delay nodes are hidden from desktop device pickers
and ignore saved desktop volume and mute. Use the channel sends, mix
masters and physical output controls for level and mute changes. Rebuilding a chain starts these
internal stages at unity and unmuted.
If a software low-cut change cannot rebuild its input chain, the previous
frequency and chain remain active. The error is reported and the same change
can be tried again; temporary filters from the refused rebuild are removed.
Unmapped processing previously enabled in Wave Link may still be active
on the original Wave XLR. See [hardware support](hardware-support.md).

<a name="tasks"></a>
## 3. Tasks

The mixer cards follow the UI's width, so a wider UI shows more
submixer strips and mix tiles before they scroll. The UI has no
minimum width of its own: it squeezes down to about 640 pixels. Header
actions and input toggles wrap when needed so their labels stay readable.
Long interface names are shortened with an ellipsis and shown in full in
their tooltip. A UI shorter than its content scrolls vertically. Mix
master cards wrap onto further rows when needed. Long plugin names are
shortened with an ellipsis, with
the full name in the tooltip. Plugin control and chain windows keep
actions below their headings, and actions wrap when space is limited.
A tooltip sits against the control it describes rather than against the
pointer, and flips to whichever side of that control the screen has room
for, so a tooltip near an edge of the screen never ends up over the
button and never takes the click meant for it.

### Precise fader adjustments

Mixer and device faders move in their displayed units: one percentage point
for sends, channel volumes, mix masters and monitor volume, one unit for
hardware controls.
Focus a fader and use the arrow keys for one step, or Page Up and Page Down
for ten. Hold Shift before dragging for one tenth of the normal sensitivity,
so a short fader can still reach an exact value. Release the pointer to end
fine dragging. Locking or hiding a control, moving it between windows, or
changing its range also ends its active drag. A hidden control or a new range
cannot reuse an old pointer position. The 150% range keeps the same
one-percentage-point steps. Additional touch contacts do not take over a drag.

Incoming device or profile values retain their precision until you adjust
them. Plugin parameter controls keep the steps supplied by their plugin.

<a name="mic-to-call"></a>
### 3.1 Send your microphone to a call or a recording

1. In the SUBMIXER card, make sure XLR 1 is unmuted in the Stream and
   Chat columns.
2. In the other application, choose the microphone: `OpenXLR Chat` for
   Discord, Zoom, Teams; `OpenXLR Stream` for OBS or a recorder.
3. Everything you add to those mixes (a game in Stream, music at a low
   level) reaches the same virtual microphone. What you hear yourself
   comes from the Monitor mix, which is separate.

Options has a tip for step 2: enforcing `OpenXLR Chat` as the system
default input ([section 3.7](#default-devices)) makes every voice app pick it up without
configuration.

For an effects-only microphone, add a virtual microphone in the layout,
name it Mic Effects, and send only your microphone channel to it. Select
`OpenXLR Mic Effects` in the recording application. Input inserts run before
the channel reaches the mix; that mix's inserts run before its virtual
microphone. Restarting a recording application does not change this path.
The physical microphone bypasses OpenXLR's software effects. Internal
filter stages, including `OpenXLR Mic Filter`, are graph plumbing rather
than selectable recording devices; a graph editor can still show them.
If an application previously selected one of those stages, select the
finished virtual microphone there instead.

<a name="input-bindings"></a>
#### Use another microphone on XLR 1 or XLR 2

XLR 1 and XLR 2 take the interface's own inputs by default. Either strip
can take one port of another capture source instead, such as a USB
microphone, a webcam or another interface's input. The binding dialog opens
from three places:

- The chip under the strip's name, in INPUTS and in an input rack, or the
  name and the line under it in a mix matrix row.
- Options, AUDIO, **Input bindings**, which has a row with the same chip
  for XLR 1 and XLR 2.
- The header, while no interface is attached and XLR 1 waits for one:
  **No interface.** is followed by a link, **Choose a microphone for XLR 1**.

The dialog lists the interface's own input first, **Interface jack (XLR 1)**,
or **Built-in microphone** on a Wave:3, Wave:3 MK.2 or Wave:1. Every other capture
source follows, other Elgato interfaces included, except OpenXLR's own
nodes and the primary interface's own. An unplugged bound source stays in the list, marked
"(not connected)" and named after its device, such as "Shure MV7".
Pick the source and its port, then Save. Ports are numbered from 1
and named by the channel PipeWire reports, such as "1 (MONO)" for a mono
USB microphone, "2 (FR)" for the right side of a webcam or "3 (AUX2)" on a
multichannel interface. The list stops at the source's channel count, and a
new source starts at port 1, which is usually right for a USB microphone; a
multichannel interface may put the microphone on another port. When
PipeWire does not report the layout, the ports are plain numbers from 1 to
64. OpenXLR takes one mono port and feeds both sides of the strip. The
strip keeps its sends, its insert chain and Mic setup, and XLR 1 keeps
its software low cut and ClipGuard. Set preamp gain and phantom power on the
microphone or its own interface; OpenXLR does not invent USB controls for
other brands. A Bluetooth headset is offered too, with one line of warning:
while it feeds the strip it stays in its headset profile, which lowers its
playback quality until the strip takes another source.

The chip says what feeds the strip: the interface's jack, the built-in
microphone of a Wave:3, Wave:3 MK.2 or Wave:1, or the source's description, with the
port when the source has more than one. Its light is on while the strip is
linked to that feed. When the source is missing or ambiguous, the light is
off and a line under the chip says why.

A strip fed by another source is named Mic 1 or Mic 2 and wears the
microphone icon, or the headset for a Bluetooth headset. Back on the
interface's jack it is XLR 1 or XLR 2 again, with the XLR microphone icon.
A Wave:3, Wave:3 MK.2 or Wave:1 has no XLR jack: its input is the microphone's own
capsule, so its strip is Mic 1 on the unit too. The ids stay `xlr1` and
`xlr2`, so profiles, skins, the layout file and Stream Deck keys keep
working, and an icon you chose in the layout editor stays.

When the strip being replaced has a connected interface behind it, the
dialog offers **Keep the interface's microphone as a capture channel**,
ticked. Save then binds the strip and adds a capture channel on the
interface's own input at that jack's port, named after the interface. If
adding the channel fails, the strip stays bound and the dialog says so.
Untick it and the interface's own microphone drops out of the mixer, as if
its cable were unplugged. Returning the strip to the interface later leaves
that capture channel in place; remove it in the layout editor if you no
longer want it.

The interface stays driven while a strip takes another microphone. Its
headphone outputs, its dial and mute button, the Wave XLR Pro's outputs and
USB Aux, additional interfaces, recall on connect and the gain lock work as
before, and a strip still on the interface, Aux In and Line In keep their audio. The
interface's gain, mute, phantom power and hardware effects belong to its
microphone, so they follow it: in an input rack and a mix matrix they are on
the capture channel that carries it, where they sat on XLR 1 before. In the
classic INPUTS panel they move under XLR 1's own controls with a heading
such as "Wave XLR Pro, input 1: plays on Pro mic". With no channel carrying
the interface's microphone, the heading says it is not in the mixer and the
controls fold away; the arrow beside the heading opens them, so a hot preamp
can still be turned down. On an interface with a Mic/PC blend, the blend
in HEADPHONES keeps mixing the interface's own microphone into its jack,
and a line under it says where that microphone plays. The light in the header means the interface's
connection and nothing else.

On a Wave XLR Pro the zero-latency path to the headphone jacks follows the
channel that carries the Pro's first input: while the jacks are the only
monitor output and that channel is unmuted in their mix, the jacks hear the
microphone directly and the channel's software send is held back. With no
such channel the path is off, and a microphone bound to XLR 1 reaches the
jacks through the software mix, a few milliseconds later. This holds while
the jacks are not in use too, so a path another program such as Wave Link
left on is switched off. The path is
hardware, and OpenXLR sets its level from that channel's
[volume](#volume-direct-monitor) and Monitor send.

A strip can also take another Elgato interface's microphone, such as a
Wave:3's capsule while a Wave XLR Pro is the primary interface. The dialog
lists every attached interface's sources except the primary's own, which
is the first entry. When you choose one that OpenXLR does not drive yet,
the dialog offers **Also drive this interface**, ticked. Save then binds the
strip and enables that unit as an additional interface, as Options,
INTERFACE, **Additional interfaces** does. If enabling it fails, the strip
stays bound and the dialog says so. While the unit is driven, its gain,
mute, low cut, ClipGuard and phantom power, whichever it has, are on the
strip, in the input rack's gain and switch rows, in a mix matrix row's gain
and mute, and in the classic INPUTS panel under the strip's chip. They are
the same controls the Additional interfaces window shows for that unit, and
they act on that unit, not on the primary. Stop driving the unit and the
controls leave the strip; the binding stays, and the strip keeps its audio.

A strip on another interface's XLR jack, such as an XLR Dock's, keeps its
XLR name; a strip on a Wave:3's, Wave:3 MK.2's or Wave:1's capsule is Mic 1 or Mic 2.

Driving an interface, switching its card profile or plugging it in again
can rename its source. A strip bound to an Elgato interface follows that
interface to its new source and back, and keeps its port. Any other source
follows its exact name only.

Changing a strip's source or port mutes that strip in all monitor mixes;
the other strips keep their mutes. Unmute it in the desired monitor mix
deliberately when you want to hear it. If the source is unplugged or its
selected port is missing, the strip stays silent and the chip says why. It
reconnects to the same source when that source returns, without borrowing
another microphone. Changing the binding ends a Mic setup session on that
strip and discards its take, and Mic setup on a silent strip says why it
cannot record. If two sources have the selected name, OpenXLR disconnects the
strip and reports the ambiguity. Rename or remove the duplicate so the name
identifies one microphone before its route resumes. Two strips may take the
same source, even the same port.

The choice lives in `mixer.json` and survives restart, and profiles never
change it. Profiles belong to the connected interface as always. While no
interface is connected, whatever the strips take, they are kept apart under
`generic` and hold only the mixer. Saving one needs the software mixer to be
running, so an empty scene cannot replace a saved profile while it is off,
and recall on connect is unavailable, since startup already restores the
saved mixer state. Profiles saved under `generic` by an earlier build while
an interface was attached show up again whenever none is connected.
Choose **Interface jack (XLR 1)** in the same dialog to return the strip to
the interface; the port list is only used for another source. Disabling the
software mixer leaves the interface as it is and remembers the bindings for
when the mixer is enabled again.

The [terminal mixer](#terminal) binds XLR 1 and XLR 2 from its Inputs tab.
It, the [Stream Deck](#stream-deck) and the [Omarchy bar](#omarchy) follow
the interface's microphone the same way the UI does. The terminal mixer
heads the interface's controls with the channel that carries them, and that
channel's key is the interface's mute. Stream Deck keys and dials for those
controls name that channel. The bar puts its mic button on that channel's
strip. A bound strip keeps its sends and send mutes everywhere.

<a name="monitor"></a>
### 3.2 Choose what you hear and how loud

The level meters show the newest reading available. If the UI is
temporarily busy, it keeps one pending meter update and resumes with the
current levels instead of replaying a backlog of older readings. This
affects only the display; the daemon continues processing audio.

Each meter reads its own channel or mix. Changing the system default
output never moves a meter, also not the meter of the channel or mix
that was the default.

1. In the MONITOR card, tick every device the monitor mixes should play
   on: your speakers, a headset, or several at once. On the Wave XLR
   Pro its own outputs (Headphones 1 (front), Headphones 2 (rear), Line
   Out) appear here too; ticking one switches the hardware's output routing.
   A ticked device that comes up with one side of its stereo feed
   missing, or that goes away and comes back, is linked again on the
   next sweep (about one second). While it is away it stays ticked in
   the list as "(not connected)" and stays selected, even across a
   daemon restart. It keeps the name OpenXLR last saw for it, across
   restarts of the UI and the daemon. An output OpenXLR never saw is named
   after its device, such as "SteelSeries Arctis Nova Elite". OpenXLR
   never moves the monitor to another output in
   its place. Only a recalled profile, loaded by hand or on connect,
   changes the selection, to the outputs that profile saved. Ticking
   another device adds it. A rejected selection returns to the daemon's
   reported outputs and shows the reason. Untick the missing one to drop it. A mono device needs only its one
   channel. The microphone channels recover the same way when the
   interface returns, and a microphone feed missing one side keeps its
   working side while the other is repaired.
2. Next to each ticked device, open the feed picker and tick one or more
   mixes. The same mix can feed several outputs, and each output can hear
   several mixes at once. For example, give the speakers only a Desktop
   mix and give the headset Desktop plus Voice. Each selected mix arrives
   with its own sends, effects, master level and mute. The feeds add at
   unity; put a different blend in a mix of its own rather than adding
   another level between a mix and an output. A shared signal in two
   selected mixes adds twice, so leave headroom when summing them.
   At least one mix stays selected. Untick the device to disconnect it.
   Feed choices use stable mix ids, so renaming a mix updates its name in
   the picker without changing its routes. A missing output keeps its
   selection and its feeds until it returns. Device descriptions update in
   place without changing the selected outputs or pending feed edits.
   The Wave XLR Pro's Headphones 1/2 and Line Out share one hardware
   return bus. Changing the feed of any of these jacks changes all selected
   jacks on that interface; they cannot carry independent blends.
   Stream, Chat, Aux and custom virtual microphones are also available,
   so you can listen to a recorder's mix without moving its recording
   clients. The same assignments are available through the API.
   Deleting a mix removes it from selected feeds; if nothing remains, the
   output returns to the default monitor mix. A failed layout save leaves the old feed intact.
   Listening to a recording or Aux mix on the Pro's jacks uses the software
   microphone path so its recording feed is never silenced by direct monitoring.
   On devices without USB Aux, selecting Aux reveals its mix and send faders
   without showing a switch for a hardware port the device does not have.
3. Linux lists **OpenXLR Monitor A** and **OpenXLR Monitor B** as separate
   playback devices. Their desktop volume and mute controls correspond to
   the respective SUBMIXER master, in both directions. Selecting one as the
   desktop default lets the keyboard volume wheel control that mix without
   changing the other mix or its channel sends. Its **150%** button unlocks
   boost in OpenXLR too. OpenDeck's monitor and output dials also support
   0 to 150%, so a boosted desktop value changes smoothly with each tick.
   Those needles sweep the full range; every other dial keeps its 0 to 100% scale.
   Desktop changes are saved with the mixer settings
   and recalled after a daemon restart; explicitly loading a profile restores
   the values saved in that profile. A physical device named
   "Monitor" (often HDMI audio) is a separate output device, not a third mix.
4. The Volume slider sets the level of the selected devices. To control this
   same level with a keyboard volume wheel, media keys or the Linux audio
   applet, open Options, SYSTEM DEFAULT DEVICES, and choose **Follow MONITOR
   output (system volume controls)** as Default output. Linux then uses the
   first selected monitor device as its default playback device. Changing
   the MONITOR selection updates that default on the next sweep (about one
   second). No selected output means no default is enforced.

   Desktop changes appear in the MONITOR slider on the next sweep and also
   update the other selected monitor outputs, at the level the desktop
   chose: a desktop that boosts past 100% sets the other outputs there too,
   rather than leaving them quieter with nothing to show for it. The
   OpenXLR slider writes the same device volumes, so Linux sees its changes
   too. Both show the same percentage: 100% is unity, not two thirds of
   another control's scale. The **150%** button unlocks boost above 100%;
   switching it off returns a boosted output to 100%. A boosted desktop
   value automatically enables the expanded range so opening the UI
   cannot turn the volume down. Changing the first selected device
   adopts its current volume. Relinking the same device, including after a
   monitor feed or insert change, keeps volume changes synchronized across
   the selected outputs. An output that was asleep or unplugged when the
   volume changed is set as soon as it answers again, without waiting for
   the next desktop change. OpenXLR's application
   routing and the Stream and Chat mix levels stay unchanged. Applications
   left to desktop routing play directly to the default monitor device.
   Existing fixed default-device choices remain available; a virtual channel
   such as OpenXLR System is a routing destination, not a monitor volume
   control.

   On KDE Plasma 6 the **150%** buttons follow **Raise maximum volume** in
   the desktop's audio settings. The MONITOR button and the button on each
   monitor mix share that one setting. Pressing any of them changes it in
   Plasma and on every other monitor button, and changing it in Plasma
   changes all of them in OpenXLR. A boosted level arriving from the desktop
   turns the setting on as well. Turning the range on keeps the current
   percentage, so 67% stays 67% on the longer scale. Turning it off lowers
   the MONITOR output and any monitor mix above 100% to 100%. Levels at or
   below 100% stay where they are, and Stream, Chat and channel sends are
   never touched. Plasma stores the preference, so an OpenXLR profile does
   not save it. Following it needs KDE's `kreadconfig6` and `kwriteconfig6`.
   When Plasma does not take a change, because a helper is missing or an
   administrator locked the setting, a line below the MONITOR slider says
   so and the buttons show the range Plasma holds. Without `kreadconfig6`
   the buttons work inside OpenXLR alone. On other desktops this does
   nothing, and each **150%** button keeps its own range.
5. **Mute** in the MONITOR header pauses every selected monitor output,
   including the direct microphone path on the Pro. Recording and streaming
   continue by default. With **Mute microphone sends with monitoring** enabled
   in Options, AUDIO, the XLR/Mic strips and mono capture microphones pause in
   every OpenXLR mix too. The terminal mixer's Outputs tab has **Mute monitoring**
   too and shows when the microphone audio state is not yet confirmed. Press it
   again to resume: mix mutes, channel sends, levels and
   output feeds keep their settings, so a mix or send that was already muted
   stays muted. The pause survives a daemon restart and profile recall leaves
   it unchanged.
   Adding a monitor during the pause leaves it individually muted. Renaming
   keeps the remembered selection; deleting removes only that monitor. If the
   last previously open monitor is deleted, the pause clears and the remaining
   individually muted monitors stay muted. With **Mute microphone sends with
   monitoring** on, the microphones then resume in every mix, Stream and Chat
   included. These choices survive a restart.
   Selected Linux output devices are muted too; resuming restores their previous
   native mute flags. Muting every mix that feeds an output, one by one, also
   mutes that Linux device, and opening any of them unmutes it again. A native
   mute silences the whole device, so applications that play straight to it
   go quiet as well. With **Follow MONITOR output** as Default output,
   applications left to desktop routing play to the first selected monitor
   device, so pausing, or muting Monitor A when it is that device's only feed,
   silences all desktop sound there.
   The original flags are saved with mixer settings. While the daemon is
   stopped, a device it muted stays muted, so a restart during a pause lets no
   sound out; the next start takes the device over again and resuming unmutes
   it. Turning off **Enable software mixer** puts every original flag back as
   the daemon stops, because no later start would. If you stop OpenXLR for
   good while monitoring is paused, resume first or unmute the device in your
   desktop's sound settings. To start a new listening
   selection instead, unmute one
   monitor mix while paused. Only that monitor opens, the other monitor mixes
   stay individually muted, and its assigned Linux devices are unmuted. The next
   master mute and resume keeps this new choice. A device fed by several mixes
   stays open while any assigned mix is open. On the Pro, an active independent
   Aux return keeps the shared Linux device open; the selected headphone and
   line-output switches still follow the monitor feed.
6. The HEADPHONES card holds the interface's own headphone volume,
   low-impedance mode, and, on every interface with a direct monitor (the
   MK.2 family, the Wave:3, the Wave:3 MK.2 and the Wave:1), the Mic ↔ PC
   crossfade, which is the zero-latency direct monitor inside the device:
   left is only your microphone, right is only computer audio. The Wave XLR
   Pro has no crossfade: its zero-latency path follows the microphone's
   Monitor send (see [the channel volume](#volume-direct-monitor)).

With one headphone jack, its volume fader uses the whole row. Two jacks
share the row equally. In MONITOR, a long output name is shortened to
leave room for the volume fader; hover over the picker for the full name.

On the Wave XLR Pro the headphone jacks are fed by a mix inside the
device. When a jack is the only monitor output, XLR 1's send to the
Monitor mix drives that hardware path: unmuted, you hear yourself with
zero latency; muted, you do not. If another device (speakers, a headset)
is ticked as well, the microphone reaches everything through the
software mix instead, with a few milliseconds of delay.
That device mix also holds a level for the computer audio OpenXLR sends,
and Wave Link writes its own fader setting there. While a jack is a
monitor output, OpenXLR keeps that level at 0 dB. A unit last used with
Wave Link on another system would otherwise play quieter on its jacks.
Muting the mix feeding the jacks also turns off the direct microphone path.
For a summed feed, it stays on while at least one feeding mix and the
microphone's send to that mix are both unmuted.

<a name="ducking"></a>
#### Lower what you hear while you speak

Ducking lowers chosen channels in chosen mixes while a key microphone is
loud, and brings them back when you stop. Talk into XLR 1 and the music
in your headphones dips; your own voice keeps its level. It works on every
device, and with no device at all, as long as the software mixer runs.

The setup has four parts:

- The **key**: the microphone whose level ducks, XLR 1 by default. XLR 2
  or a capture input on one mono port can be the key too.
- The **channels** to lower. By default every application channel: Game,
  Music, Browser, System, Voice Chat and SFX. The key is never lowered.
- The **mixes** to lower them in. By default the default monitor mix,
  Monitor A until it is removed.
  Add Stream to dip the music your viewers hear while you talk.
- Three presets, with the Wave XLR Pro's own numbers. **Sensitivity** is
  how quiet a voice already counts: Low reacts only to a loud voice
  (-3 dBFS), Medium at -12 dBFS, High to a soft one (-24 dBFS). **Amount**
  is how far the channels drop: 6, 12 or 24 dB. **Speed** is how fast they
  drop and come back: Slow 100 ms and 2 s, Medium 80 ms and 1.2 s, Fast
  30 ms and 0.2 s.

Ducking is off until you switch it on. In the UI, open **Ducking**
from the SUBMIXER header beside **Mic setup**, from the bottom row of the
**Mixer layout** window beside **Exclusive groups**, or from **Options**,
card AUDIO, under the software mixer switch. All three open the same
Ducking window, on any device; the header button shows only while the software
mixer runs, and with the mixer off the Ducking window opened from Options shows
the setup without letting you change it:

1. Tick **Lower other audio while the key microphone is loud**.
2. Pick the **Key microphone**. It leaves the channel list, since the key
   is never lowered.
3. Tick the channels under **Lower these channels**. **All application
   channels** ticks the application channels and nothing else; **None**
   clears the list.
4. Tick the mixes under **In these mixes**.
5. Choose **Sensitivity**, **Amount** and **Speed**.

Each change applies at once. With the Wave XLR Pro attached, **Runs on**
offers **This unit** and **This computer** (see below). The line at the
bottom says what runs ducking and where it sits in the signal, or why
nothing runs it. While this computer runs it, **Reduction** shows how far
the chosen channels are lowered now, the deepest of the ducked mixes. If
the daemon refuses a change, the Ducking window shows why and puts its setup back.

On any device the [terminal mixer](#terminal-ducking) also sets the whole
setup on its Inputs tab, a [Stream Deck key](#stream-deck) switches it on
and off, and `setDucking` over the [API](api.md#ducking) sets every part.
OpenXLR saves the setup with the rest of the mixer, and profiles carry it.

**Where it runs.** On most devices this computer runs it: OpenXLR adds a
small stage for each ducked mix that lowers only the chosen channels.
The Wave XLR Pro has ducking on its own processor too, and runs it on its
headphone jacks and Line Out when the setup fits what it can do; its USB
Aux port has not been checked by ear yet, so a ducked Aux mix stays on this
computer. The unit only lowers a whole output, the mix OpenXLR sends to its
jacks and Line Out or to its USB Aux port, and only with XLR 1 as the key.
When the setup does not fit, the computer runs it and the state says why
(see [where ducking runs](api.md#where-ducking-runs)):

- the key is not XLR 1;
- the unit's ducking is not heard where the mix plays: the USB Aux port
  is not checked yet, and for a moment after the unit connects the jacks'
  feed is not read in its mix yet;
- a ducked mix also plays somewhere the unit cannot lower, such as
  speakers on the computer or a virtual microphone like Stream;
- the jacks play two mixes together and only one of them is ducked;
- a channel that plays in a ducked mix is not on the list, so lowering
  the whole output would lower it too;
- XLR 1 is heard in the ducked mix through its software send, so the unit
  would lower your voice as well;
- the unit did not keep the setting written to it. Switch ducking off and
  on to try again.

Moving from one to the other never ducks twice. There can be a moment
without ducking while it moves. To keep it on this computer whatever the
setup, choose **Runs on**, **This computer** in the Ducking window
(`setDuckingProcessing` over the API). **Runs on** lights where ducking
runs. While the unit cannot run the setup, **This computer** is lit and
**This unit** cannot be pressed; its tooltip and the line under **Runs
on** give the unit's reason. Your choice is kept, and ducking goes back
to the unit once the setup fits it again. The **Hardware processing**
badge shows beside the title while the unit runs it.

**Where each side sits in the signal.** The unit and this computer listen
and lower at different points:

- On the unit, the Pro listens to XLR 1 as the unit hears it, before USB.
  That is before anything on this computer: the EQ in software, inserts
  and gates. A noise gate or suppressor insert on XLR 1 does not stop the
  unit from ducking on noise. The unit lowers the finished mix as it plays
  it on its outputs, after everything on this computer.
- On this computer, the key is the key channel after its EQ, its insert
  chain and Mic setup's loop, before the channel's volume and sends. The
  chosen channels dip inside each ducked mix, before that mix's own insert
  chain and latency delay.

If a gate on XLR 1 should stop false triggers, choose **This computer**.

On the Pro, OpenXLR writes the setting every time the unit connects, which
clears any ducking Wave Link left on it. If another program changes the
setting while OpenXLR runs, OpenXLR writes its own back within a few
seconds. In hardware-control mode OpenXLR writes nothing and the unit
keeps what it holds, and the Ducking window shows the setup without
letting you change it. The unit's bytes
come from Wave Link captures on Windows and a desk probe on Linux, and the
result was checked by ear on Headphones 1
([hardware support](hardware-support.md#wave-xlr-pro-0fd900b4)).

On this computer a muted key does not duck: XLR 1 muted in every mix it is
sent to lowers nothing, and a key paused by **Mute microphone sends with
monitoring** lowers nothing either until monitoring resumes. On the Pro the
unit listens to XLR 1 before USB, so it ducks while XLR 1 is muted in every
mix; choose **This computer** if muting the microphone should stop ducking.
The unit reacts to XLR 1 only: talking into XLR 2 does not duck. On this
computer, [Mic setup](#mic-setup)'s loop plays through the key channel, so a looped take ducks the music, which is a quick way to
try a sensitivity. On the Pro the unit hears the live microphone, so the
loop does not duck.

Each mix ducked on this computer adds a hidden bus with one send for every
channel, ten with the default layout, and a small helper process. Ducking
all five mixes of the default layout adds 50 sends, which a stock
pipewire-pulse may refuse for lack of open files; the error says so (see
[open-file limit](#open-files)).

<a name="audio-flow"></a>
### Audio flow

Open **Flow** from the UI to see inputs, channels, mixes and outputs
from left to right. Cyan lines connect inputs to channels, purple lines connect
channels to mixes, and green lines connect mixes to outputs. Muted or inactive
routes are dashed. Sends at zero have no line.

Click a card to trace its upstream and downstream routes. Unrelated cards and
lines dim, and dots travel along the highlighted active routes to show direction.
The dots indicate routing, not measured audio levels. Click the card again,
click empty space, press Escape or use **Clear selection** to show every route.
Cards also work with Tab and Enter or Space. Selection does not change routing.

Input and mix processing appears in an FX row inside its card. Hover over the
card for the full chain in signal order, including bypass and error states.
Outputs follow the selected monitor feeds, including outputs where both
Monitor A and Monitor B are ticked, and the current virtual microphones.
Apps left to desktop routing appear without an OpenXLR connection. The Flow window sizes itself to the graph when it opens, or when
the first routing state arrives, up to the screen's usable area. Larger layouts
scroll horizontally and vertically. Resizing the window manually turns off automatic sizing.

<a name="apps"></a>
### 3.3 Put an application on a different channel

Open Manage in APPLICATIONS and turn off **Automatically route unassigned
applications** to leave new apps not managed until you choose a channel.
Saved assignments still apply, including apps explicitly marked Not managed.
The choice survives a daemon restart and is not changed by loading a profile.
Live unassigned playback follows a changed default on the next routing sweep;
when you turn automatic routing back on, silent apps get their automatic
channel the next time they play. Forgetting an app uses the current policy.
The terminal mixer's Apps tab offers the same switch.

Not managed means the desktop chooses the output, not that the app is silent.
Playback on an OpenXLR channel is released to Linux's current default output.
If that default is itself an OpenXLR channel, unmanaged apps can still be
heard through it. Browser tab routing and saved website choices are separate.

The APPLICATIONS card lists every app that is currently registered with
PipeWire as an audio client; a green light means it is playing. Manage sits
beside the APPLICATIONS heading, like Edit layout beside SUBMIXER. Browser
tabs sits beside Manage when the daemon supports browser tab routing. The
app controls wrap onto further rows when the UI is narrower.

Each app shows its icon before its name, the one your desktop's launcher
shows for it. The UI looks it up in your icon theme, then in hicolor
and `/usr/share/pixmaps`, and Flatpak and Snap apps get theirs too. An app
with no icon gets the first letter of its name on a badge of the same size,
so the names stay lined up. An app keeps its icon in the list after it
quits. SVG icons need GdkPixbuf and its librsvg loader, which GTK desktops
already have. Without them the UI uses a PNG of the same icon if the
theme has one, and the letter if not.

Opening the UI or receiving an updated app list only displays the current
routing. A remembered routing change is sent when you choose a different
channel. When the mixer stops supplying an app list, both the APPLICATIONS
card and the Manage list clear their stale entries.

An app's playback streams and audio client share the same identity even
when PipeWire puts the process name only on the client. Windows executable
names such as `Balatro.exe` use the same normalized key as their Wine or
Proton client, so the app keeps one entry and one channel assignment.
When older settings contain both that key and a stale executable-name
alias, the existing normalized assignment takes precedence.
The entry shows the application's name, not the name of one of its helper
streams: Discord's `discord_hardware_mute` client, which it opens to watch
a headset's mute switch, still reads as Discord.
Identity matching ignores letter case, including live channel changes and
the running and playing indicators. If PipeWire reuses a node id for a new
stream, or a stream later reports its actual app identity, the next sweep
applies that app's routing instead of keeping the previous placement.
The daemon watches PipeWire for graph changes and, while that subscription
is reconnecting, reads the graph with a one-shot `pw-dump` so routing, default
enforcement and route repair continue. The journal reports outages and
recovery.

1. Change the channel in the dropdown next to the app. The move happens
   immediately and is remembered for that app. The channels also appear
   as playback devices in your desktop's audio applet (KDE's, for one),
   so you can see there which OpenXLR channel an app is playing into;
   the hardware inputs (XLR 1, XLR 2, Aux In, Line In) are deliberately not
   listed, nothing should play into a microphone's channel.
2. To pre-assign an app that has not played yet, open Manage, pick it
   from the installed-application list, choose a channel and press Add.
   The identity is guessed from its launcher; if the app reports a
   different name on first play it shows up as a new entry.
3. To keep OpenXLR's hands off an app, choose "Not managed" in its dropdown.
   Its streams go back to the system default output at once and from
   then on stay wherever you or the desktop route them, which is what
   you want for an app that must reach a specific side of a headset
   with its own game and chat sinks. The app stays listed as ignored so
   you can bring it back by picking a channel.
4. Forget, in the same window, drops an app and its remembered channel.
   A running app, including one already playing audio, re-registers on the
   next sweep and follows the automatic-routing choice; use "Not managed" for a
   lasting opt-out.

An app that is missing from the card is not registered with PipeWire
as a client. That happens with some applications until they start
playing.

For separate Teams, YouTube or other browser tabs, the optional
[Chromium companion](browser-routing.md) reports individual tabs. Press
Browser tabs beside Manage to open the Browser tabs window. The option
at its top, Enable individual browser tab routing, turns tab routing on
and off. Below it, each tab has a row with its title, website, capture
light, channel picker, Remember website and Forget website. Until the
companion reports a tab, the window says so under the option. Its Manual
button opens the browser routing guide.
Enable the option, choose a channel and press Activate in the tab's row.
The button shows on a row with a channel that is not routed yet. The
browser lets the companion take a tab's audio only after you clicked its
toolbar icon or pressed its shortcut on that tab. Activate brings the tab
forward and starts it when the browser still allows that. When it does
not, the row tells you what to press in the browser, either Activate this
tab in the popup the companion opens or the OpenXLR icon on the tab. See the
[browser routing guide](browser-routing.md#choose-and-activate-a-tab).
Remember website saves its origin's
default across restarts. Browser permission must be granted again for
each tab after restarting the browser. While browser output selection is
on, OpenXLR does not move the browser itself. Its row keeps the channel
it is assigned to, which applies again when you turn the option off, and
the picker's tooltip names the channel the browser plays into meanwhile,
or says it plays outside OpenXLR's application channels. Choosing another
channel for the whole browser then only saves it, and so does a routing
key on an OpenDeck or the keyboard.
The tooltip follows moves made in desktop audio controls on each stream
sweep. Non-browser streams with the same application identity still follow
their assignments. Tabs that are neither capturing nor assigned to a
channel show their website origin instead of a page title. Shared titles
refresh at most once per second for title-only changes. Deleting a channel
moves its remembered website defaults to the fallback application channel;
tab-only choices return to Browser default.

OpenXLR remembers up to 512 explicit app assignments. Once that limit is
reached, existing assignments can still be changed, including "Not managed".
Forget an unused assignment before adding a new one. A refused assignment
leaves the app's live audio route unchanged; the same limit applies to
commands that assign an individual stream. Older settings with more
assignments remain readable and editable.

<a name="usb-aux"></a>
### 3.4 Feed a second computer over USB Aux (Wave XLR Pro)

1. Connect the second computer to the Pro's USB Aux port. It sees the
   Pro as a plain USB audio device.
2. In the SUBMIXER card, set the sends into the Aux mix: typically your
   microphone and the game, without the second computer's own chat.
3. Tick "To USB Aux port" on the Aux mix. The interface's audio stream
   restarts once, which interrupts playback for a moment; the device
   only picks up the new routing at stream start. If the Pro is
   replugged, or the link to the port comes up incomplete, the next
   sweep links the Aux mix to the port again.

If you change the monitor outputs while the hardware stream restarts,
your latest selection is kept. A restart that finishes after the mixer
has stopped does not reconnect its outputs.

The USB Aux *input* (what the second computer sends back) is the Aux In
channel, with its level and **Level Lock** on the USB Aux in row of the
INPUTS card.

<a name="line-in"></a>
The analog **Line In** jack is a channel of its own, Line In, apart from
Aux In: a console or a player on the jack plays on Line In and nothing of
it reaches Aux In. Its gain, 0 to 40 dB, is on the Line In row of the
INPUTS card, under USB Aux in. The unit takes up to 80 dB and OpenXLR
stops at 40. The unit's own ALSA control stops at 20 dB, and a line-level
source rarely needs more. Above about 20 dB the noise floor rises, so turn
the source's own volume up first and keep the range above 20 dB for quiet
sources such as a phone's headphone output. A unit set higher elsewhere
shows its own gain in the readout with the slider at the top of its range,
and the first move brings it into range.

**Level Lock**, at the end of the Line In row, keeps the desktop's mixer
and other applications from changing the Line In gain. While it is on,
the unit ignores gain changes made through its ALSA control, and the
Line In gain in OpenXLR still works. It is separate from Aux In's Level
Lock, and a profile saves it.

Like Aux In, Line In starts
muted in Monitor A, Monitor B and the Aux mix and open in Stream and Chat;
unmute its Monitor A send to hear it. The jack has no hardware mute, so
muting Line In mutes the channel's sends. The Pro can also play the Line
In jack straight to its headphone jacks, a path a unit set up in Wave Link
may have on. While a headphone jack or Line Out is a monitor output,
OpenXLR turns that path off, so the jacks hear Line In only through the
monitor mix and its mute there silences it. Line In shows only on a Wave
XLR Pro whose capture source has its 18-channel layout, which is the one
it was measured on. A Reset to OpenXLR's baseline leaves the Line In gain
and its Level Lock as they are.

<a name="plugins"></a>
### 3.5 Add a plugin to the signal path

1. Under XLR 1, XLR 2, a capture input or a mix, press "Inserts". The
   chain window opens with that chain's plugins and an "Add plugin"
   button. The picker lists compatible installed LV2, CLAP and VST3
   effects (ones that can run mono for a mono input, stereo for a stereo
   capture input or a mix), searchable by name, category or
   format. A VST3 effect that reports stereo buses but accepts a mono
   layout when asked, as the Elgato effects do, is offered for the inputs
   too: OpenXLR asks each plugin when it scans it. Host
   feature requirements can exclude a plugin; install a compatible set
   such as `lsp-plugins-lv2` if the list is empty.
2. Add. The plugin appears in the chain window and in the Inserts row on
   the strip, with a green light while active. The button that opens the
   chain takes the same colours: green while a plugin in it is processing,
   red when it holds plugins and none is (every one bypassed or failed).
   An empty chain keeps the plain button.
3. Controls opens a window generated from the plugin's parameters,
   grouped, with a Defaults button. Bypass takes it out of the path
   (red light); the arrows reorder the chain; the cross removes it. The
   strip keeps the short form: bypass and the cog that opens the controls.
4. Chains and exposed parameter values are saved with the mixer and
   profiles. OpenXLR does not save opaque plugin state, sample-file
   selections or a plugin's own presets. OpenXLR's presets of control
   values are described below.

The generated controls cover the parameters the daemon read from the
plugin. After recalling a profile, preset or A/B slot, a control without a
saved value shows the plugin's declared default. A plugin's own editor can
be opened as well, with the native host described in
[section 3.13](#plugin-editors). CLAP and VST3 plugins
appear in the same picker and always run in that host. VST2 plugins cannot be loaded.

<a name="effect-presets"></a>
**Copy, compare and save effects.** Each effect in the chain window has
**Copy** and **Rename**. Rename changes the name the effect shows; the
plugin keeps running and its settings are kept. **Presets and A/B** opens
the chain's own window:

- **Copy chain** copies every effect. **Paste effects** adds what was
  copied after the effects already in this chain, and **Replace chain**
  puts it in their place. A copy can be pasted on any chain, an input or a
  mix, as long as each plugin can run at that chain's width; it lasts until
  OpenXLR closes.
- **Store A** and **Store B** keep the chain as it is now. **Hear A** and
  **Hear B** put that state back, so two settings can be compared by ear.
  An edit after hearing a slot is not kept in it until you store the slot
  again. Both slots are forgotten when the UI loses the daemon.
- Type a name and **Save current chain** to keep a preset. **Load preset**
  replaces this chain with the selected preset; a preset saved from one
  chain loads on any other.
  **Import preset** and **Export preset** move one preset in or out as a
  file; importing only adds it to the list. A local export replaces the
  chosen file atomically with user-only read and write access and keeps
  the export folder's permissions.

The controls window of an effect has **Presets** too, for presets of that
one plugin: loading one changes only that effect and leaves the rest of
the chain as it is. Both windows share one list. The saved list must be a
regular file of at most 8 MiB; an unreadable or damaged list shows an error
and is left unchanged.

Hearing a slot or loading a preset rebuilds the chain, so audio through it
stops for a moment. A preset holds the order, names, bypass, host choice
and control values; plugin-private state and sample files are not in it.
A plugin that is not installed, or cannot run at the chain's width, is
refused and the chain stays as it was. The file format and limits are in
[effect-presets.md](effect-presets.md).

Each chain is rebuilt on its own. Adding, removing, reordering or
bypassing a plugin on XLR 2 rebuilds XLR 2's chain and nothing else, so
XLR 1's plugins keep running and their editors stay open. The same holds
for every mix. Moving a control changes the running plugin without a
rebuild.

<a name="plugin-latency"></a>
**Plugin latency.** Some plugins delay the audio they process, a limiter
that looks ahead or a linear-phase equaliser for instance, and report by
how much. The controls window shows the figure under the plugin's name.
"Latency: unknown" means the plugin gave no figure. That happens when it
runs in the PipeWire filter chain, which cannot read one, when the plugin
reports nothing, and until its host first reports, once audio has run
through it.

When a mix's plugins delay it, that mix arrives later than the others. In
Options, AUDIO, **Compensate plugin latency across mixes** delays every
other mix by the difference, so they line up with the slowest, for example
a recording mix and the stream mix that carries the same voice. It is off by
default. It makes the faster mixes later, monitoring included, so leave it
off unless the mixes have to line up. Turning it on or off rebuilds every
plugin chain and briefly interrupts audio.

With it on, an LV2 plugin that declares a latency port runs in the native
host when that is installed, so its figure can be read; its Native host
switch stays as you set it. A plugin with an unknown latency counts as
zero, so the other mixes are aligned without it instead of waiting for a
figure. The delays follow a plugin whose latency changes, and a
bypassed plugin counts as zero, without restarting any plugin. If a mix's
plugins add up to more than two seconds no mix is delayed, and Options says
why. Plugins on XLR 1, XLR 2 and capture inputs reach every mix alike and
are not aligned against other inputs. Compensation does not cover the device's own latency or the
hardware's direct monitoring. The setting is saved with the mixer and stays
as it is when a profile is loaded.

<a name="block-size"></a>
PipeWire moves audio in blocks, and anything that has to wait for a whole
block before it can work adds the block's length to the delay: noise
removal, and plugins that process in blocks. In Options, AUDIO,
**Block size** sets the block for the graph OpenXLR runs in: 256 samples
(5.3 ms at 48 kHz), 512 (10.7 ms) or 1024 (21.3 ms). Automatic, the
default, leaves it to PipeWire, which runs at its configured default,
usually 1024, unless an application asks for less.

A size you choose applies to every application that plays or records
through OpenXLR's mixes, and it wins over what they ask for. Smaller blocks
cost more processor time, and on a busy machine they can crackle; go up a
step if they do. A change reopens the audio device, which you hear as a
short gap. A size forced for the whole system with
`pw-metadata -n settings 0 clock.force-quantum` still wins over this one.
The choice is saved with the mixer, stays as it is when a profile is
loaded, and is on the terminal mixer's Options tab too.

<a name="mic-setup"></a>
<a name="sound-check"></a>
**Mic setup.** Press **Mic setup** in the SUBMIXER header, beside
**Ducking**, to set up XLR 1, or the first microphone in the mixer when
XLR 1 is not one. To start on another microphone, press **Mic setup** in
the chain window of XLR 1, XLR 2 or a capture input on one mono port. The
header button shows while the software mixer runs and has at least one
microphone to set up. Stereo capture inputs have no Mic setup. The window
has four steps, **Position**, **Level**, **Listen** and **Shape**; press
any of them at the top to go to it, in any order. Each microphone opens
on Position the first time and on the step you left it on after that,
until the UI quits.

One Mic setup window is open at a time. When the mixer has more than one
microphone to set up, such as XLR 1 and XLR 2 on a Wave XLR Pro, a
microphone on an additional interface or a mono capture input,
**Microphone** at the top of Position lists them by their channel names.
Choosing another one moves the window to it on the same step. A take, Raw
or Hear myself running on the one shown stops first, as when the window
closes. The meter, the gain, the tips, the EQ and the plugins are then the
new microphone's. Pressing Mic setup in the header or in a
chain window while the window is open moves it the same way. If stopping
fails, the window stays on the current microphone and shows the error so
you can retry. A daemon reconnection during the switch also leaves the
current microphone selected. If the chosen input is removed or replaced
while the switch waits, the window stays on the current microphone too.
Removing the microphone shown closes its Mic setup window, even when the
UI opened it. Late actions from that window cannot affect an input
created in its place.

A gain measurement ends as soon as you close the window. Reopening Mic
setup can start a fresh measurement without the old window cancelling it.

The window takes the height its step needs, up to the screen's height, so
nothing is cut off or needs scrolling until the screen is too short. If
you resize it, it keeps your size and only grows when a step needs more
room.

**Position** says which unit the microphone is on and gives tips for
placing it:

- On a Wave:3's, Wave:3 MK.2's or Wave:1's own capsule: speak into its front, not its
  top, and keep it close, since a condenser capsule hears the room.
- On an XLR jack of a Wave XLR, a Wave XLR Pro or an XLR Dock: check in
  the microphone's manual whether it takes sound from its end or its side.
  A dynamic microphone usually works closer than a condenser.
- On anything else, a USB microphone, a headset or another make of
  interface, OpenXLR controls nothing on the device, so its gain and power
  are set there.

The tips that follow hold for any speech microphone: keep the same
distance while you talk, aim slightly to one side of your mouth so the air
of p and b sounds passes the capsule, and point the back of a cardioid
microphone at noise. They give no distances in centimetres, since none
were measured. For any microphone but a built-in capsule the step also
has **Phantom power**: the unit's own switch where OpenXLR drives the unit,
the same switch as in INPUTS, with a note while the unit reports
phantom power settling. Condenser microphones need it, dynamic ones do
not; check a ribbon microphone's manual first, and turn the headphones
down, since the change can thump. For a microphone on a unit OpenXLR does
not drive, the step says to switch it on that unit.

**Level** shows the microphone before any of OpenXLR's processing: after
the interface's gain and whatever the unit does itself, before the EQ, the
software low cut and ClipGuard and your plugins. The big bar is the peak,
the thin one under it the average level, both on the same -60 to 0 dBFS
scale as the channel meters. Speak as you will on air and set the gain so
your voice peaks inside the band drawn under the bar, -18 to -6 dBFS. The
**Clip** light comes on when a sample reaches -0.1 dBFS and stays on until
you press it. The interface's gain sits beside the meter when the channel
carries an input that has one: the primary interface's input, or that of an
additional interface. For any other microphone, set the gain on the
microphone or its own interface. The meter runs while the window is open
and changes nothing in the channel's route.

**Suggest gain** works out a gain for you. Press it and talk for five seconds
as you will on air. OpenXLR measures two figures from the meter. One is
your speech level, the level the average bar stays under for 95 percent
of the five seconds, so pauses and a single loud word do not decide it.
The other is the highest peak. OpenXLR proposes the gain that brings your
speech level to -20 dBFS, which puts your peaks in the band, unless that
would lift the highest peak over -6 dBFS, the top of the band. Then it
stops where that peak reaches the top. The
suggestion is in whole dB inside the interface's range, and nothing is
written to the interface until you press **Accept**. **Discard** forgets
it.

- A change under 2 dB is not worth making, so the window says to keep the
  gain you have.
- When the interface's highest gain is still too low, or 0 dB still too
  high, the window says how far your voice stays from the target and what
  to try, such as moving closer or further away.
- When your voice clipped during the five seconds, its real level is
  higher than measured. Accept the lower gain and measure again.
- When the level stays near the bottom of the meter, the window does not
  suggest anything. Check the microphone, its mute and phantom power
  first.
- With the gain lock on, the suggestion shows but cannot be accepted until
  you release the lock. The same goes for an additional interface that
  OpenXLR cannot reach at that moment.
- For a microphone whose gain OpenXLR does not set, such as a USB
  microphone or another make of interface, the window says how many dB to
  change on the device.
- If the gain, its range or the microphone's source changes while OpenXLR
  listens or before you accept, the window discards the suggestion and asks
  you to measure again. Returning the gain or source to its old setting
  does not restore the old measurement. Renaming the channel leaves it valid.

**Listen** records a take and plays it back through the chain:

- **Record** and speak; recording stops by itself after ten seconds.
  **Loop take** then plays the take into the channel over and over in
  place of your microphone, through every insert and the channel's
  software processing as they are set, so you can change controls, bypass
  plugins or reorder the chain and hear the result on the same words. The
  last 50 ms of the take fade into its first 50 ms, so the loop does not
  click where it starts over. The loop needs at least a tenth of a second
  recorded; a take shorter than 0.2 seconds fades over a quarter of its
  length. **Live mic** goes back
  to the microphone and keeps the take; **Record** replaces it. The
  interface's own gain and processing are already in the take, so changing
  those while it loops changes nothing.
- The take's waveform appears once it is recorded, with a line that
  follows the loop. While another completed take is being fetched the last
  waveform stays visible; a late reply for an older take cannot replace it.
- **Processed** and **Raw** switch between the microphone through its EQ,
  software processing and plugins and the microphone without any of them,
  on the take or on the live microphone. The plugins keep running and the
  switch fades across in 20 ms, so it is heard at once and the chain is not
  rebuilt. A reverb or a delay rings out by itself after a switch to Raw,
  and a compressor or a gate starts from silence when Processed comes back.
- **Hear myself** plays the microphone, with its effects, in your monitor
  mix for as long as the window is open, even where its send is muted. It
  opens the send into each monitor mix that plays on a selected monitor
  output, at the send's level, or at full level when the send is all the
  way down. The window says how late you hear yourself through this
  computer: two periods of the audio server plus the latency your plugins
  report. Use headphones, since speakers on the same mix feed back. When
  the microphone reaches no monitor output, you hear neither the take nor
  your voice, and the window says so.
- On a Wave XLR Pro, the interface's zero-latency microphone path is
  switched off while the channel carrying its first input has a session,
  including a capture input, so you hear the loop and not your voice, and
  Hear myself plays your voice with its effects. That path never carries
  effects.

**Shape** holds what changes the sound, so you can loop a take on Listen
and adjust it here while it plays. Every change is heard at once, on the
take or on the live microphone:

- **Starting presets** put one of three EQs on the microphone and switch
  the EQ on, as one change. They are OpenXLR's own starting points for a
  speaking voice; adjust them on the curve. The preset the EQ holds is lit
  like any chosen option. Its light goes out when you move a band off the
  preset's values, switch the EQ off or undo it, and comes back if the EQ
  returns to those values. Pressing the lit preset changes nothing.
  **Undo preset** puts
  back the EQ as it was before the last preset, switch included; changes
  made to the EQ since that preset go with it. The UI forgets the undo
  when it loses the daemon.

  | Preset | Band 1 | Band 2 | Band 3 | Band 4 |
  | --- | --- | --- | --- | --- |
  | Clear | high pass 80 Hz | bell 300 Hz -2 dB, Q 1.02 | bell 3.5 kHz +2 dB, Q 1.02 | high shelf 10 kHz +1 dB |
  | Warm | high pass 60 Hz | low shelf 200 Hz +2 dB | bell 3 kHz +1 dB, Q 1.02 | high shelf 8 kHz -2 dB |
  | Broadcast | high pass 100 Hz | bell 400 Hz -3 dB, Q 1.42 | bell 4 kHz +3 dB, Q 0.79 | high shelf 12 kHz +2 dB |

  The high pass and the shelves keep the EQ's default Q of 0.70. Every
  band is on.
- **Noise removal** comes first: **On** switches it, **RNNoise** and
  **DeepFilterNet** choose the engine, and **Strength** sets how far the
  noise may be turned down. The line under it says which engine is heard
  and the delay it adds, or why it does not run. See
  [noise removal](#noise-removal).
- **EQ** is the graph the [microphone EQ](#microphone-eq) editor draws,
  edited the same way: drag a handle, use the wheel for Q, double click to
  switch a band, right click for its type. **EQ on** beside it switches
  the EQ, and **All bands** opens the full editor with a row of fields per
  band.
- **Low cut and ClipGuard** has the interface's own switches where the
  microphone is on a unit that has them. They act before the take, so a
  looped take does not change with them; press **Live mic** on Listen to
  hear them. On XLR 1 the software low cut and ClipGuard come next when
  they apply, and those run after the EQ, so the loop goes through them. A
  microphone with neither, such as a capture input, says so; the EQ's high
  pass band does the same job.
- **Plugins** lists the channel's insert chain: each plugin's state light,
  **Bypass**, and **Editor**, which opens the plugin's own editor when it
  runs in the plugin host and its generated controls otherwise, as the
  chain's cog does, and **✕**, which removes the plugin from the chain.
  **Add effect** adds a plugin at the end of the chain. The chain window
  still moves and renames plugins.

A session starts with **Record**, **Live mic**, **Raw** or **Hear myself**,
and ends with **Stop and discard** or when the window closes:

- Closing the window or **Stop and discard** returns the microphone,
  throws the take away and turns Raw and Hear myself off. Your effect
  settings stay as you left them, and so do the mutes of your sends.
- The loop and Raw go everywhere the channel goes, the virtual microphones
  included, so mute the channel in a call or a recording you are not
  testing on.
- A session runs on one microphone at a time. Stop it on one before
  starting it on another, including between XLR and capture inputs. A
  refusal names the channel whose session is running.
- The take stays in memory and is never written to a file or a profile.
  A session ends by itself when its source changes, when its audio path
  is lost, when its capture channel is deleted, when the mixer is rebuilt
  and after ten minutes. If the channel still exists, the window says why,
  including the helper's last failure message when it supplied one. The same
  failure is written to the daemon's log. **Stop and discard** clears the
  window's message. The daemon's messages call the session Sound Check, the
  name the [API](api.md#sound-check) gives it.
- Capture inputs have no low cut or ClipGuard of their own, and Mic setup
  does not add them. A capture input's EQ already runs on this computer,
  so its take is the raw microphone and the loop goes through the EQ as it
  is set.
- With a capture input's microphone unplugged, starting a session is
  refused with the channel's name and the reason. When the source lacks the
  port the input takes, the refusal says the capture port is unavailable.
- Mic setup needs the native plugin host the packages include. A source
  build without `-p:EnableNativeLv2Host=true` refuses it with that reason.

<a name="noise-removal"></a>
**Noise removal.** XLR 1, XLR 2 and every capture input on one mono port
can have background noise taken out before anything else hears the
microphone: it runs first, ahead of the EQ, the low cut, ClipGuard and your
plugins, so they, your mixes, your calls and Mic setup's loop all get the
cleaned voice. In INPUTS and in an input rack, **Denoise** right after the
microphone's **EQ** button opens its controls. In the Broadcast, Wave and
Compact Desk matrix they are in the row head's **Mic** button, under the
interface's switches. They are also in Mic setup's **Shape** step, in the
terminal mixer's Inputs tab, and on a Deck key. **Denoise** turns green
while noise removal runs and red when it is on and cannot run; **Mic**
turns green while it runs. It is off until you switch it on, and it runs
on this computer even when the interface runs the EQ.

Two engines do the work, and you choose per microphone:

- **RNNoise** is light on the processor and best on steady noise: hiss,
  hum, a fan, an air conditioner. It adds 30 ms of delay. It is the
  default.
- **DeepFilterNet** is cleaner on noise that changes, such as typing,
  clicks and street sound, and takes out some room echo. It uses more of
  the processor and adds more delay: about 50 ms with PipeWire's small
  buffers, 61 ms with its usual 1024-sample ones and 83 ms with 2048. The
  delay follows PipeWire's buffer size, so the figure shown can change
  while it runs. A lower delay takes effect after two continuous seconds
  of smaller buffers; a larger buffer restarts that wait. OpenXLR's level
  meters ask for PipeWire's default size,
  so they never raise it themselves, and [Block size](#block-size) can
  set a smaller one. The first time you choose
  DeepFilterNet, its
  model takes a moment to load, and the microphone keeps its sound until
  it is ready.

On a Ryzen 9 9950X3D, one microphone takes about half a percent of one core
with RNNoise and about two percent with DeepFilterNet; an older or slower
processor needs more.

**Strength** is the most the noise is turned down, from 0 to 100 dB. At
**No limit** the engine takes out all it can. If your voice starts to sound
thin, watery or clipped at the start of words, lower it: 20 to 30 dB keeps
a little of the room and sounds more natural. At 0 the microphone passes
through untouched, only late.

Switching noise removal on or off, or changing the engine, makes a short
dip of about 10 ms in your voice, because the engines are late by
different amounts. Strength changes at once. The delay is the same for
every mix and every call, so the voice stays in step with itself, but you
hear it in your headphones that much later through a monitor mix; the
interface's own direct monitoring, where it has it, is not delayed.

Noise removal runs at a 48 kHz audio graph, which is how PipeWire and every
Wave interface run unless you changed it. At another rate the microphone
passes through untouched and the line under the controls says so. It needs
the native plugin host the packages include; every package carries both
engines. A build from source without Rust offers RNNoise only, and the
Shape step says so.

The setting is saved with the mixer and in profiles. A profile saved
before noise removal leaves it as it is. Removing an input discards its
pending strength edit and stops its old noise controls from changing a
replacement input.

<a name="microphone-eq"></a>
**Microphone EQ.** XLR 1, XLR 2 and every capture input on one mono port
each have an EQ with one to sixteen bands and the same band types, ranges
and steps as Wave Link's, and the same curves. It comes first on the input, before the low
cut, ClipGuard and your plugins, so ClipGuard also catches a boost, and
Mic setup's loop goes through it. A capture input has no low cut or
ClipGuard, so its EQ comes straight before its plugins.

The existing four-band setup stays the default. **Add band** adds a neutral
bell in a gap between the existing frequencies; **Remove** beside a band
removes it and renumbers the following bands. At least one band remains and
at most sixteen are offered. The band list scrolls while the response graph
stays visible. Reset restores the four defaults. Copy, paste, profiles and a
restart retain the full table. The four skin colours repeat for additional
bands, which retain their individual numbers.

A supported interface runs up to four bands itself, with unused slots off.
Above four, the complete EQ moves to software without changing your saved
processing preference. It returns to the unit when its band count fits
again and the unit is available. Meanwhile **Where the EQ runs** lights
**In software** and **On the unit** cannot be pressed. Pressing **In
software** makes software your choice, so the EQ stays there after you
remove bands.
Only the EQ stage is replaced when its software band count changes; the
inserts keep running. An older daemon offers no Add or Remove controls.

Rapid changes keep the latest value you chose while earlier reports arrive.
After the short settling interval the editor follows external changes again.
Removing a microphone closes its EQ editor and discards pending edits, so
they cannot change a replacement channel with the same id.

Open it with **EQ**. In INPUTS it sits on the microphone's input line,
right after the chip that says what feeds it, and stands as tall as the
chip. In an input rack it comes before **Inserts**, and in the Broadcast and
Wave matrix it sits over **FX** in the row head. **EQ** turns green while
the EQ runs and red when it cannot run. Each microphone has one editor;
clicking **EQ** again brings it to the front.

- The graph runs from 20 Hz to 20 kHz across and from -12 to +12 dB up the
  side. The six regions of a voice are shaded in turn and named along the
  top; point at a name to see what lives there. Each band draws its own
  curve thin in its colour, and the sum of the active bands is the thick line.
  While the EQ is off the sum is grey, and the bands still move, so you can
  set them up before switching it on.
- Each band has a numbered handle, hollow while the band is off. Drag a
  handle sideways for its frequency and up or down for its gain. A high
  pass, low pass or notch has no gain: its handle stays on the 0 dB line,
  and dragging it up or down changes its Q instead. Scroll over a handle to
  move its Q one step, double-click it to switch the band on or off, and
  right-click it to pick its type. The selected band's values show in the
  corner of the graph.
- From the keyboard, Tab moves from handle to handle. The left and right
  arrows move the frequency a twelfth of an octave, or a quarter of that
  with Shift. The up and down arrows move the gain 1 dB, or 0.25 dB with
  Shift, and the Q on a band without a gain. Page Up and Page Down step the
  Q, Space switches the band, and Enter opens its type menu.
- Under the graph each band has a row: its switch, its type, its frequency
  in hertz, its gain in decibels and its Q. The gain is greyed out for a
  type without one, which keeps it for when you change back. The graph and
  the rows change the same values. In a narrow editor the fields lose their
  arrow buttons; type a value, or step it with the arrow keys or the wheel.
- **EQ on** switches the whole EQ. **Compare** bypasses it while it is
  on, so you hear the microphone without it; the EQ comes back when you
  turn **Compare** off or close the editor. **Copy** and **Paste** carry one
  microphone's EQ, switch and bands, to another. **Reset** asks first,
  then puts the four bands back where they start and leaves the EQ on or
  off.
- The line at the bottom says what runs the EQ, or why it cannot run. On
  a unit that can run the EQ itself, **Where the EQ runs** sits above it;
  see below.

The types are high pass, low shelf, bell, notch, high shelf and low pass.
The bell raises or lowers the sound around its frequency; some editors call
it band pass. Values snap to the EQ's steps: whole hertz, quarter decibels,
and Q steps of 3.75 percent. The editor shows the values the daemon keeps.

- The EQ is off until you switch it on. Its bands start where Wave Link's
  do: a 36 Hz high pass, bells at 310 Hz and 2172 Hz at 0 dB, and a low
  pass at 15049 Hz that is off.
- The first switch on links the EQ into the input, the way a plugin chain
  is linked in, without restarting your plugins. After that every change
  goes to the running EQ: frequency, gain and Q move without resetting the
  filters, and switching a band, its type or the whole EQ crosses over in
  5 ms. A drag sends each band's changes at most 50 times a second. Off, the
  EQ stays in the path bypassed until the input is next rebuilt, so on, off
  and **Compare** switch at once.
- In software, the EQ does not reach a unit's own zero-latency
  monitoring, the Mic side of the Mic ↔ PC crossfade. On a Wave XLR Pro
  OpenXLR turns that path off while the EQ is on, so the jacks hear it
  (see [the channel volume](#volume-direct-monitor)).
- The Flow window lists the EQ first in the input's card while it is on,
  naming the unit that runs it or saying it runs in software.
- The EQ is saved with the mixer and in profiles. A profile saved before
  the EQ existed leaves it as it is.
- In software it needs the native plugin host the packages include.
  Without it the editor says so, the daemon refuses to switch the EQ on,
  and the microphone keeps its path. On the unit it needs nothing.
- Scripts drive the same EQ through the [control API](api.md#microphone-eq)
  with `setEq`, `setEqEnabled` and `setEqBand`.

**EQ on a capture input.** A capture input on one mono port gets the same
EQ button and editor as XLR 1, in INPUTS after its source chip, in an
input rack and in the matrix. Its EQ always runs on this computer, at the
head of the input before its plugins, even when the input carries another
Wave interface's microphone, so the editor shows no **Where the EQ runs**
switch and no **Hardware processing** badge, and its bottom line says it
runs on this computer. Switching it on moves the input onto its own route,
the way adding a plugin does, without a gap in the sound. It is saved with
the mixer and in profiles like XLR 1's. Changing the input's source keeps
it; deleting the input deletes it. A capture input on a stereo pair has no
EQ, and one moved from a mono port to a stereo pair keeps its EQ settings
for when it goes back to a mono port.

**Where the EQ runs.** A Wave XLR MK.2 or XLR Dock MK.2 on firmware 1.0.15
or later, a Wave:3 MK.2 on firmware 0.2.3 or later, and a Wave XLR Pro on
firmware 1.0.14 or later run the EQ on their own processor, before USB,
the way Wave Link 3.5 does. On the Pro, XLR
1 and XLR 2 each have their own EQ on the unit, with their own switch and
their own choice of where it runs. The editor then shows **Hardware processing** in its header and
names the unit in the line at the bottom, and the **EQ** button's tooltip
says the same. Everything else stays as described above: the same values,
the same curve, **Compare**, Copy and Paste, profiles. Nothing is added to
the input's route, and the plugin host is not needed.

- While the microphone is on such a unit, the editor's footer shows
  **Where the EQ runs** with **On the unit** and **In software**. **On the
  unit** is the default. **In software** runs the EQ on this computer
  instead and switches the unit's own EQ off, which keeps its bands. Use
  it to compare the two, or to keep the unit plain. The lit side is where
  the EQ runs, and pressing it again changes nothing. While the unit
  cannot take the EQ, for the reasons below, **In software** is lit and
  **On the unit** cannot be pressed; its tooltip and the note under the
  switch say why. Your choice is kept, and the EQ goes back to the unit
  when it can take it again. The mixer saves the choice per microphone; profiles do not carry it, and a
  recall never changes it. The terminal mixer's Inputs tab shows the same
  choice.
- On older firmware the EQ runs on this computer until the unit is
  updated. A note under the switch names
  the unit's firmware and the version it needs: 1.0.15 on the Wave XLR
  MK.2 and XLR Dock MK.2, 0.2.3 on the Wave:3 MK.2, 1.0.14 on the Pro. Elgato's updater is in
  Wave Link on Windows or macOS. The same note appears when the firmware
  version or the unit's EQ settings cannot be read, or when the unit did
  not keep an EQ written to it; switch the EQ off and on, or choose **In
  software** and then **On the unit**, to try the unit again.
- Only the microphone the unit carries is equalised there: XLR 1 or XLR 2
  on the unit's own input, an additional interface's input, or a strip
  bound to the unit's own capture node. A microphone bound to any other
  source uses the EQ on this computer, and the unit's EQ on the input that
  strip would otherwise carry is switched off, so XLR 2 bound to a USB
  microphone leaves the Pro's second input plain. On the unit, the EQ is in the
  unit's own signal, so every channel and program that takes that unit's
  audio hears it, a capture channel on its node included, as they hear its
  low cut.
- A Mic setup session moves the EQ to this computer while it runs, so
  the take is the raw microphone, the loop follows every edit and Raw
  leaves the EQ out; the unit takes it back when the session ends.
- When the unit connects, OpenXLR writes its EQ over the one the unit
  holds, Wave Link's from another computer included, and the journal says
  so once. A unit that already holds it is not written, so restarting the
  daemon does not touch your voice. If another program changes the unit's
  EQ while OpenXLR runs, OpenXLR writes its own back.
- Moving the EQ between the unit and this computer switches the old place
  off before the new one comes on, so your voice is never equalised twice;
  you may hear a moment without the EQ. The wait for the unit to confirm
  the switch is bounded to one second, even after a long computer uptime.
  Hardware EQ edits are sent at most ten times per second, with the first
  edit sent at once and the final value kept for the next write.
- When OpenXLR stops, or you unplug the unit, the unit keeps running the
  EQ it last held, as it keeps its gain and low cut. So does an
  additional interface no microphone carries, and every unit while the
  submixer is turned off. Switch the
  EQ off in the editor, or choose **In software**, first if you want the
  unit plain without OpenXLR. Moving a microphone to another source, or
  keeping it in software, switches only the unit's EQ off and keeps its
  bands.
- On a Wave XLR Pro the unit's EQ also acts on its zero-latency path to
  the headphone jacks, so the jacks hear the EQ the unit runs and OpenXLR
  keeps that path on. A measurement on the XLR Dock MK.2 suggests the same
  of its own headphone monitoring, but this is not settled there. Nobody
  has compared the Pro's EQ by ear with the one on this computer yet.
- `setEqProcessing` in the [control API](api.md#where-the-eq-runs) sets the
  same choice from a script.

<a name="native-editor-compatibility"></a>
**Native editor compatibility.** Open Options, PLUGINS, then "Native editors"
to choose plugins that should use OpenXLR's generated controls instead of their
own editor. The release list includes Elgato De-Esser because its
native editor freezes under Wine, and Elgato Noise Removal because closing
or reopening its native editor can crash the plugin host.
An open compatibility list refreshes when the daemon reconnects and
cannot be edited while it is disconnected.

- Find an installed plugin and press "Use OpenXLR controls" to add a block.
- Select a blocked entry and choose "Allow native editor" to try its own
  window again, for example after a Wine or plugin update.
- "Use release default" removes your override. Untouched entries follow the
  list shipped with each OpenXLR release, so a plugin can be unblocked when
  its editor has been verified to work. Your explicit choices always win.

A blocked plugin keeps processing audio in the same host. Its cog opens the
OpenXLR controls, and the Plugin UI button is unavailable. A "Native editors"
button after Bypass opens the compatibility list with that plugin selected.
The rule takes effect on the next open; an existing native window is not forcibly closed.
Changes are saved immediately without an audio restart. Allowed overrides
remain listed so you can return them to release defaults later.

Rules identify plugins by format and stable id, so moving or renaming a file
does not bypass a rule. Only your overrides are stored in
`~/.config/openxlr/native-editors.json`, honoring `XDG_CONFIG_HOME`. If that
file is damaged, Options reports it and keeps the release defaults in force
rather than overwriting it. Repair or remove the file and restart the daemon
to reload it.

<a name="install-plugins"></a>
**Installing plugins.** The quickest set comes from your distribution:
on Arch, `lsp-plugins-lv2` and `x42-plugins` cover the microphone path
well. Choose LV2, CLAP or VST3 packages available for your distribution;
the picker offers only plugins compatible with the selected slot.
For a plugin you downloaded, press "Install file" or "Install folder" in the picker or in Options and pick it; OpenXLR puts it where
it looks and the picker lists it a moment later. A file is a `.clap` or a
single-file `.vst3`; a folder is a `.vst3` or `.lv2` bundle, or a folder
holding several of them, such as an extracted download. Folder selections
look up to three subfolder levels deep. OpenXLR accepts up to 200 plugins
from one pick and reads at most 10,000 directory entries while looking. A
larger pick is refused, with a request to choose a smaller folder, before
any plugin is installed; nothing is installed in part. A registered Windows
folder is not refused for holding more than 200 plugins: its list shows the
first 200 found, and a bigger collection is best split into several folders.
The 10,000 directory entry budget applies to it as well. Linux
plugins are copied into `~/.clap`, `~/.vst3` or `~/.lv2`, so the download
can go afterwards. Installing over a plugin that is already there builds the new
copy beside it and swaps the two only once the copy is complete, so a
download that turns out to be unreadable, or a disk that fills up, costs
the update and not the plugin you had. Existing files beside the plugin,
including backups, are left alone; only a half-made or replaced copy that
an interrupted install left there is removed by the next install, so it is
not scanned as a second plugin. An archive has to be extracted
first. Plugins installed by
other means, or copied into `/usr/lib/clap`, `/usr/lib/vst3` or
`/usr/lib/lv2` by a package, appear after "Rescan" in Options or a daemon
restart (`LV2_PATH`, `CLAP_PATH` and `VST3_PATH` override the places
searched; the plugin manager below adds folders of your own). What a CLAP or VST3 bundle said about itself is remembered
until the bundle changes, so a rescan costs nothing for plugins already
known. Updating OpenXLR reads every one of them again once, because the
new version may see them differently, which makes the first scan after
an update as slow as the first ever.

<a name="plugin-search-folders"></a>
**Plugin folders.** To use plugins where they already are, without copying
them, open Options, PLUGINS, "Plugin manager". Its top list shows every
folder searched for LV2, CLAP and VST3 plugins: the standard ones, those
`LV2_PATH`, `CLAP_PATH` and `VST3_PATH` name, and the ones you added.
A folder that is missing or unreadable says so.

- Choose LV2, CLAP or VST3 and press "Add plugin folder". OpenXLR searches
  that folder from then on and reads the catalogues again, so its plugins
  are in the picker when it finishes. The list is kept in
  `~/.config/openxlr/daemon.json` and survives a restart.
- Select an added folder and press "Remove added folder" to stop searching
  it. Its files stay where they are. An insert that used one of its plugins
  keeps running, and fails on its next load unless the plugin is found
  elsewhere. Standard and environment folders cannot be removed here.
- "Rescan all plugins" reads every format again and retries bundles that
  failed before, like "Rescan" in Options.

A folder must be absolute, without a colon, and cannot be the filesystem
root, `/usr`, `/home`, your home folder itself or a system tree such as
`/proc`. One inside or around a folder already searched is refused, and
symbolic-link aliases count as the same folder. Up to 32 folders can be
added across the three formats. A CLAP or VST3 folder is searched to any
depth, up to 16,384 entries; a bigger one is reported and the search
stops there, so choose a narrower folder.

For LV2, OpenXLR hands the same search path to the catalogue, the PipeWire
filter-chain and the native host, so a plugin the picker lists from an added
folder also loads. Once an LV2 folder is added, that path is `LV2_PATH`
when the daemon has one, otherwise `~/.lv2`, `/usr/local/lib/lv2`,
`/usr/lib/lv2`, their `lib64` and Debian multiarch counterparts and the Nix
profile folders, whichever exist, followed by the added folders. With no
LV2 folder added, every LV2 host uses lilv's own default.

A saved folder that can no longer be resolved, such as a symbolic link that
now points at itself, is skipped with a warning in the manager; the other
folders keep working and the entry stays saved until you fix or remove it.
If the daemon cannot save the list, or `daemon.json` cannot be read, the
manager says so and nothing changes; an unreadable file is left for you to
repair.

Installing refuses a plugin that is or contains a named pipe, socket or
device file, or a link to one; empty files and ordinary symbolic links are
fine. A registered Windows folder lists its valid plugins and skips
unreadable entries and entries like these. Installing checks files but does
not sandbox the plugin, so install only bundles you trust and leave the
source alone until it has finished.

CLAP and VST3 discovery follows linked folders, but visits each resolved
directory only once per search root. Links back to a parent or another alias
of the same folder do not repeatedly scan its plugins. An unreadable child
folder does not discard plugins found in other folders. VST3 bundle contents
are not searched for further bundles.

The scan cache is disposable. Invalid index entries are ignored independently,
so undamaged descriptions and records of failed scans remain usable. Cached
descriptions must use the filename assigned to their bundle and must not be
symbolic links; cache cleanup does not follow index paths to other files. A
description larger than the live scanner's 64 MiB output limit is ignored and
scanned again. Pointing a linked folder at a different build of a plugin
also scans it again. A bundle with a folder link that loops back on itself,
or with a very large number of files, still loads, but OpenXLR scans it every
time instead of caching it.

<a name="windows-plugins"></a>
**Windows VST3 and CLAP plugins.** They run through
[yabridge](https://github.com/robbert-vdh/yabridge), which wraps them as
Linux bundles.

The **openxlr-yabridge** package supplies a tested bridge for 64-bit
Windows VST3 and CLAP plugins, built from a pinned source with the Wine
editor input fix. Install it from the same place you installed OpenXLR.
Wine comes with it as a dependency.

```sh
# Arch Linux, CachyOS and Omarchy, from the AUR
yay -S openxlr-yabridge

# Ubuntu, from the PPA you already added for OpenXLR
sudo apt install openxlr-yabridge

# Fedora 44, from the COPR repository you already enabled
sudo dnf install openxlr-yabridge
```

On NixOS, set `services.openxlr.yabridgePackage` to the flake's
`openxlr-yabridge` package. On any other distribution, take the `.deb`,
the `.rpm` or the `.pkg.tar.zst` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest), check
it against `SHA256SUMS-yabridge.txt`, and install it by hand. The
[package guide](../packaging/yabridge/README.md) covers building it
yourself, with the build dependencies for each distribution and where to
install the result so OpenXLR finds it.

Restart the daemon with `systemctl --user restart openxlr-daemon` so it
finds the companion; this briefly interrupts audio. Options then
identifies the selected bridge as "OpenXLR bridge". Use "Bridge Wine's
plugins" or pick a Windows plugin folder to create private wrappers.

Without the companion, a distribution yabridge works only with Wine older
than 9.22. From 9.22 an embedded plugin window never learns where it is,
so every click lands as far from the pointer as the window is from the
corner of the screen, and the plugin ignores the mouse entirely. Options
says so when it sees that pair of versions. If it cannot read the Wine
version, the rest of the setup still shows, without this warning.

The companion keeps those wrappers in
`~/.local/share/openxlr/yabridge/{vst3,clap}` and its directory registry in
`~/.config/openxlr/bridge/yabridgectl`, honoring XDG overrides. It leaves
other DAWs' existing wrappers and yabridgectl configuration alone. OpenXLR
can also read existing wrappers using the companion's matching libraries
and host. Private copies take priority when the same plugin is found twice.

On NixOS, set `services.openxlr.yabridgePackage` to the flake's
`packages.x86_64-linux.openxlr-yabridge` package. Installation, source-package
builds and rollback are covered in the
[companion package guide](../packaging/yabridge/README.md).

An existing system or user yabridge installation remains supported. It is
used when the companion is absent, or when the daemon environment sets
`OPENXLR_YABRIDGE=system`. An absolute path in that variable selects a
companion installed elsewhere. Restart the daemon after changing it.

**Using a separate yabridge installation.** Install Wine through your
distribution. For yabridge, use its distribution package where available
or follow the [upstream installation instructions](https://github.com/robbert-vdh/yabridge#installation).
For example, Arch provides `wine`, `yabridge` and `yabridgectl`.

For an upstream tarball install, extract it into `~/.local/share` so that
`~/.local/share/yabridge/yabridgectl` exists. OpenXLR checks this location
and PATH. Adding it to your interactive shell's PATH alone does not change
the environment of a running systemd daemon. The
[editor input note](#windows-editor-input) applies to unpatched bridges.

**Installing and syncing Windows plugins with either bridge.**

With both in place, run the plugin's Windows installer with Wine and let
it install where it offers to:

```sh
wine ~/Downloads/PluginSetup.exe
```

Then open Options and press one button. Which one depends on whether
yabridge has seen that folder before:

| The button | When to press it |
|---|---|
| Bridge Wine's plugins | The first Windows plugin, and any later one installed somewhere new. The button names what it found and disappears once that folder is bridged |
| Sync Windows plugins | Every plugin after that, when the installer wrote into a folder already bridged. This is the usual case |
| Install folder | An installer that wrote outside Wine's usual folders. Press Ctrl+H in the dialog to see `~/.wine`, which is hidden |
| Rescan | Plugins that arrived by other means, such as a package from your distribution. Nothing to press after a bridge or a sync, since both read the catalogues again |

Bridging and syncing end the same way: yabridge wraps each Windows plugin
in the selected bridge's directories: private `vst3`/`clap` directories
for the companion, normally `~/.vst3/yabridge` or `~/.clap/yabridge` for
a system bridge. OpenXLR reads its catalogues again,
and the plugin is in the picker with a VST3 or CLAP badge. Nothing else
has to be restarted.

<a name="plugin-folders"></a>
**Managing Windows plugin folders.** Open Options, PLUGINS, then "Plugin
manager". Its lower half lists the folders registered with the selected
yabridge provider.

- "Add folder" registers a folder holding Windows VST3 or CLAP plugins,
  syncs it and refreshes the catalogue. The original files stay in that
  folder. Use "Install file" or "Install folder" for native Linux plugins.
- Select a folder and press "Remove from list" to unregister it. A
  confirmation explains what will be removed. OpenXLR cleans up only that
  folder's generated VST3 and CLAP wrappers; original plugin files and
  wrappers still covered by another registered folder are kept. A folder
  that was moved or deleted can still be removed from the list.
- "Rescan" syncs the registered folders and reads the catalogue again.

Remove affected plugins from your insert chains before removing their
folder, including bypassed inserts. Folder removal does not edit saved
profiles; re-add the folder before recalling a profile that needs it. It
does not restart the audio service. If unregistering succeeds but cleanup
fails, the result says so and the list refreshes to show the current state.

**Managing individual plugins.** Select a registered folder to see its
Windows VST3 and CLAP files. Each row says whether that source is enabled,
disabled or in use by an insert chain.

- "Disable in OpenXLR" excludes the selected source from yabridge and removes
  its generated wrappers. Its files stay in place and the row remains listed.
  "Enable in OpenXLR" restores it. A separately installed copy can still
  appear in the picker; disabling one source does not ban a plugin name.
- "Delete file" permanently deletes a standalone file or bundle after a
  confirmation. Other files in the same folder are kept. It is unavailable
  for Wine-installed or linked sources, which may depend on an installer,
  registry entries or shared resources.
- "Wine uninstaller" opens the installed-apps list in the selected plugin's
  Wine prefix. Choose the plugin's installer entry there. Close other Wine
  applications and remove affected inserts first: one installer entry may
  remove several effects from the same package. OpenXLR waits without killing
  the uninstaller on a timeout, then syncs and rescans when it closes.

When a row says "In use", choose "Remove from all chains" and confirm to
remove every occurrence from the current input and mix chains, including
bypassed inserts. Other plugins keep their order and settings. This requires
a running mixer and can briefly interrupt affected audio paths. The current
settings are saved before the operation completes, then the row refreshes so
you can disable, delete or uninstall the plugin. Files and exclusions are not
changed by this step. Saved profiles are kept too; recalling one can add the
plugin back. If a chain could not be changed, the result names it rather than
claiming the whole operation succeeded.

Disable and delete actions are refused while the plugin remains in an insert
chain, including a bypassed insert. The controls refresh the catalogue and
open pickers after each change. Sync also removes wrappers whose source
files have gone, but keeps unrelated wrappers and those still used by inserts.
Exclusions are stored in the selected bridge's configuration; system-bridge
exclusions affect other applications using that bridge too. A folder-level
blacklist rule made outside OpenXLR must be removed through yabridge before a
plugin blocked by that rule can be enabled here.

With the OpenXLR companion, this manages its private registry and wrappers.
With system yabridge, the same registry is used by other applications, so
removing a folder affects those applications too. The window and its removal
confirmation call this out. This manager does not change native Linux plugin
search paths or uninstall plugins installed by your package manager.

Wine keeps its Windows drive in `~/.wine` unless `WINEPREFIX` says otherwise, and an installer
writes into `Program Files/Common Files/VST3` inside it, or the same for
CLAP. That is a dotted folder, which file dialogs hide, so OpenXLR looks
there itself and offers what it finds as "Bridge Wine's plugins" rather
than asking you to go and find it. Once a folder is bridged, yabridge keeps
it on its own list, so a second plugin installed into it needs only the
sync, and the Bridge button has nothing left to offer.

OpenXLR switches Wine's USB driver off in the Wine prefixes it starts.
With the driver on, Wine holds every USB device on the computer open, the
Wave among them. A plugin that checks for a USB licence dongle cannot see
it. To keep the driver on, put `wineusb.sys=b` in the daemon's
`WINEDLLOVERRIDES`; OpenXLR passes a value that names the driver
unchanged. The switch only takes effect when OpenXLR is first to start
that prefix, because a Wine program already running there keeps the
drivers it started with.

Picking one Windows `.vst3` or `.clap` file with "Install file" installs
only that plugin. OpenXLR copies it into its own folder under
`~/.local/share/openxlr/windows-plugins`, grouped by format and plugin name,
and registers that folder with yabridge. A single Windows `.vst3` bundle
picked with "Install folder" is copied whole, including its resources.
The original download is kept and can be deleted afterwards. Other plugins
beside the selected one, including other downloads, are not registered.
Selecting the same plugin name again updates its managed copy.

A plugin already installed inside a Wine prefix is linked from its managed
folder instead of being copied or moved. It keeps its original location,
Wine prefix and neighbouring files. Selecting an ordinary folder or using
"Add folder" in the folder manager still registers that folder in place.
VST2 `.dll` files are left out because OpenXLR cannot load VST2.

Older folder registrations are not silently removed. If an earlier
single-file import registered Downloads, remove that entry in the "Plugin
manager" and import the plugins you want individually. Remove affected
inserts first if the manager asks you to. Removing a folder refreshes the
catalogue and the open plugin pickers.

<a name="bundle-not-read"></a>
**A bridged plugin is not in the picker.** A successful yabridge sync means
the wrapper exists, not that OpenXLR could read the plugin. Each CLAP or
VST3 bundle is scanned in a separate process. If that process fails or
takes longer than a minute, the bundle contributes no plugins to the
catalogue. Install, bridge, sync and Rescan replies name up to three scan
failures and count the rest, for example:

```
1 bundle could not be read: TDR Kotelnikov.vst3 (timed out); the daemon's log says more. The scanner output was saved for the diagnostics archive.
```

The daemon also logs scan failures after startup discovery and after these
commands, with the format, bundle path, outcome, exit code when known, and
the name of the saved scanner output:

```sh
journalctl --user -u openxlr-daemon -n 200 --no-pager | grep 'plugin scan'
```

Failed scans, including timeouts and malformed descriptions, are remembered
so automatic catalogue builds skip them. Options, PLUGINS shows a quiet
"Skipped after a failed scan" count and expandable list with the reason and
original failure time. It shows at most 128 bundles and says how many more
were omitted. Older cache entries have no recorded reason or time. A
changed bundle or scanner is tried automatically; Rescan retries failures
even when the files are unchanged. Scanner start errors stay retryable.
Collect [diagnostics](#reporting) after the scan ends.

For a loader conflict, `OPENXLR_PLUGIN_CLEAN_ENV=1` in the daemon's environment
opts its scanners and live plugin hosts into a clean launch. It removes
exactly `LD_LIBRARY_PATH`, `LD_PRELOAD` and `LD_AUDIT`, with either bridge
provider. Normal launches retain all three. Removing them can disable
GameMode, capture and overlays, or break CUDA plugins and runtime wrappers,
so use this only to investigate a conflict. Restart the daemon after
changing its environment, then Rescan to retry remembered failures.
The diagnostics archive records whether this was active, the effective
values, any removed values and the Wine runner selected by `WINELOADER`
or the host's PATH. Values are bounded and paths receive the archive's
usual redaction.

**Capture a deep Wine trace.** If a Windows plugin hangs during scan,
the **Deep Wine trace for plugin scans** checkbox in Options, SUPPORT adds
exception, unwind and module-load output for a bug report. Scans get much
slower and produce large logs. It is off by default and is not meant to be
left on. Only scanners
receive `WINEDEBUG=+seh,+unwind,+loaddll`; live hosts keep their environment.
An existing `WINEDEBUG`, including an empty value, takes precedence.

1. In Options, SUPPORT, turn on **Deep Wine trace for plugin scans**.
   The checkbox reads the daemon's current setting. No restart is needed.
2. In Options, PLUGINS, choose **Rescan** and wait for it to finish.
   Failed bundles are retried even when unchanged. Successful unchanged
   bundles stay cached, so they produce no new trace. The switch does not
   clear the scan cache or start a scan by itself.
3. Choose **Collect diagnostics** in SUPPORT, then turn tracing off.

In the [diagnostics archive](#reporting), open `plugin-discovery.json`. Check
`discovery.hostEnvironment.wineTrace` is true and `scannerWineDebug` names
the effective channels. The failed log under `plugin-scan-logs/` also
records `wineTrace: true` and `wineDebug`. Ordinary scans already include
`trace: scan` phase lines, so the last phase identifies the calls in progress.
A deep trace captures the first 8 MiB of stderr and saves up to 1 MiB after
collapsing repeated lines. Three copies remain, followed by the omitted
count. The 60 second deadline and 4 MiB total log budget still apply.

The switch changes only the running daemon's environment. It is not saved
across restarts. If the daemon's startup environment already contains
`OPENXLR_PLUGIN_WINE_TRACE=1`, tracing starts on and the checkbox shows it.
Turning it off in Options clears the running process's value, but a later
restart inherits the startup environment again.

For a headless or scripted setup, the control API accepts
`{"cmd":"setPluginWineTrace","value":true}`, then
`{"cmd":"rescanPlugins"}`. Collect the evidence before sending
`{"cmd":"setPluginWineTrace","value":false}`. See [api.md](api.md) for
authentication and replies. Alternatively, set the startup environment:

```sh
systemctl --user set-environment OPENXLR_PLUGIN_WINE_TRACE=1
systemctl --user restart openxlr-daemon
```

Rescan, wait, collect diagnostics and check the same fields above. Remove
that startup setting afterward so a restart cannot turn tracing on again:

```sh
systemctl --user unset-environment OPENXLR_PLUGIN_WINE_TRACE
systemctl --user restart openxlr-daemon
```

After a Wine upgrade, a bridged scan can be the first program to use an
older Wine prefix. Wine then updates it and may show an optional Mono or
Gecko installer dialog; the scan can wait behind that dialog until its
deadline. Check for an open Wine dialog and finish or cancel it, declining
optional components you do not need, then Rescan. If there is no visible
dialog, close other Wine applications using the same prefix and start Wine
interactively so any pending update can finish where its dialogs are
visible. For the default prefix:

```sh
WINEPREFIX="$HOME/.wine" wineboot
```

Use the plugin's actual prefix if it is not the default. Wait for Wine to
finish, then Rescan. This is a possible explanation for a timeout, not
proof that every missing plugin is waiting on a dialog.

A helper's deadline includes reading its output, even when its main process
has already exited and a child still holds the output pipe open. Incomplete
output is not a successful scan.

A timeout alone does not tell us why Wine or the plugin stopped answering.
Do not run `wineboot -u` or a prefix-wide `wineserver -k` as a routine fix:
they change or stop other Wine applications using that prefix. Start with
[the scan evidence](#scan-logs) instead.

<a name="scan-logs"></a>
**What a failed scan keeps.**
`plugin-discovery.json` holds one short line per bundle, and a bundle that
fails can print far more than a line: a bridge's startup banner fills the
beginning and a Wine unwind fills the end, so the exception in between
would be the part that is clipped. More output from a failed attempt is
kept in a bounded file with repeated lines collapsed, under
`~/.cache/openxlr/plugin-scan-logs/` (or `$XDG_CACHE_HOME` when that is
set), readable only by you. The scan entry names its file in `logId`, so
the line and the file are held together.

The bounds are fixed, and a log always states where it was cut:

- 256 KiB of standard error normally, or 1 MiB after repetition collapse
  during a deep Wine trace, and 64 KiB of standard output per attempt.
- One file per bundle: a bundle that fails again replaces its own file and
  counts the attempt, so repeated failures do not grow the directory.
- 24 files and 4 MiB in total, oldest deleted first.

Each file starts with what the attempt was: the bundle, the outcome, the
attempt number, when it ran and for how long, the exit status when it could
be read, and whether OpenXLR's own deadline or output limit ended it or the
scanner exited by itself. Nothing there is guessed, and nothing is run to
find it out.

A scan that succeeds writes nothing, and a file is deleted on its own once
newer failures push it out. Failing to write one never fails a scan: the
entry then carries `logNote` with the reason instead of `logId`. Collect
[diagnostics](#reporting) to include them; that copies the files and starts
no plugin, scanner, bridge or Wine process. To clear them by hand, delete
the directory. Review paths and output before sharing.

<a name="windows-editor-input"></a>
Elgato Noise Removal 1.1.2 can crash when its own editor closes or reopens
with Wine 11.17 and the OpenXLR bridge 5.1.1.54. Its native editor is
blocked by default. Use its OpenXLR controls; this does not disable the
effect. Options, Native editors lets users test a different bridge or
plugin version by explicitly allowing an editor again, and lets anyone put
a plugin that is not on the list, Elgato Compressor among them, on OpenXLR
controls for themselves.

**A Windows plugin's own editor ignores the mouse.** The plugin plays, its
interface is drawn and it follows anything you change from OpenXLR, but
clicking its knobs does nothing, wherever you click. This is a known fault
in the unpatched yabridge release with Wine 9.22 and newer. The OpenXLR
companion includes the input fix.

Wine changed how it tracks where a window is in 9.22. A plugin editor is
embedded in a window belonging to its host, and Wine never learns where
that window really is, so it keeps believing the window sits in the
corner of the screen. Every click arrives offset by the distance between
those two positions, which for a window anywhere else on a desktop lands
far outside the plugin, and Wine drops it before the plugin sees it. The
Options window says so when it sees a pair of versions with the fault:
yabridge up to 5.1.1 with Wine 9.22 or newer. yabridge tracks the fix in
[issue 409](https://github.com/robbert-vdh/yabridge/issues/409).

Meanwhile the plugin is still usable: Controls in the insert row opens a
window OpenXLR builds from the plugin's own parameters, and those work,
bridged or not. The optional `openxlr-yabridge` companion carries a pinned build with the
input fix. Select it and restart the daemon. Users managing their own
bridge can follow the upstream development builds linked from issue 409.

<a name="memlock"></a>
**"Low memory locking limit".** yabridge prints this when it starts, in
the daemon's log:

```
With a low memory locking limit, yabridge may not be able to lock its
shared memory audio buffers into main memory.
```

It is a warning, not a failure: the plugin runs, but the buffers it shares
with its Windows half can be paged out under memory pressure, and reading
them back can exceed an audio cycle. The effective limit depends on the
distribution and user session; check the running daemon before changing it.

The daemon's unit asks for the allowance with `LimitMEMLOCK=infinity`,
in the packaged unit, the NixOS module and the one the UI writes for a
source build. That covers a session whose own ceiling is already high but whose
user manager hands units less, and it costs nothing where the allowance
was there anyway.

It cannot invent an allowance the session does not have. A unit raises the
soft limit as far as the hard limit and no further, and the user manager
that starts the daemon is unprivileged, so a session capped at 8 MiB stays
capped whatever the unit asks for. That is what the steps below are for.

Group names alone do not grant a memory-lock limit; the distribution's PAM
and systemd policy determines it. On Arch, the realtime privileges package
provides the relevant group policy:

```sh
sudo pacman -S realtime-privileges
sudo usermod -aG realtime $USER
```

On systems using PAM limits, an administrator can instead grant the
`audio` group a memory-lock allowance:

```sh
printf '@audio - memlock unlimited\n' | sudo tee /etc/security/limits.d/99-openxlr.conf
sudo usermod -aG audio $USER
```

Either way, log out and back in, since the limit is set when the session
starts. To see that it took, ask the daemon itself:

```sh
grep 'Max locked memory' /proc/$(systemctl --user show openxlr-daemon -p MainPID --value)/limits
```

"unlimited" there means the plugins the daemon starts inherit it too.

Options, PLUGINS also checks the running daemon's memory-lock limit. When
Wine, yabridge and the native host are available, a limit below 256 MiB
shows a warning and a Memory-lock setup link here. This is yabridge's
warning threshold; it is not a measurement of how much memory a plugin
needs. An unreadable limit is unknown, not zero. Diagnostics archives
include the same effective limit in `plugin-setup.json`.

After changing group membership, a daemon restart within the old login
session can still inherit the old limit. If user services survive signing
out, reboot to start a fresh session. Check the daemon's limit again after
that. A memory-lock warning and a Windows editor ignoring clicks have
different remedies: the separate Fix Windows editor input link in Options
leads to the companion package instructions above.

<a name="wine-ends"></a>
**Wine ends with the last bridged plugin.** A Windows plugin runs in Wine,
and Wine keeps service processes of its own behind it that stay after the
plugin is gone. OpenXLR ends that Wine session with `wineserver -k` once
the last bridged insert has stopped, and again when the daemon stops, so
`systemctl --user stop openxlr-daemon` and the UI's Restart finish in
a few seconds instead of waiting out the unit's stop timeout. Only the
session OpenXLR started is ended: a plugin installer you are running, or
another DAW with its own bridged plugins, keeps its Wine whatever OpenXLR
does. Without `wineserver` on PATH nothing is ended, and the daemon says
so once in its log. The unit sends SIGTERM to the daemon alone
(`KillMode=mixed`, in the packaged unit and in the one the UI writes
for a source build), which is what gives it the chance: sent to the whole
group, the signal would take `wineserver` down first and leave the rest of
Wine to the timeout.

The picker marks each plugin with its format, since the same plugin often
ships in more than one, and its LV2, CLAP and VST3 buttons narrow the list
to one of them. When the catalogue grows past what the UI can be sent,
the plugins your chains already use come first, then the LV2 list, then
the other formats in the room that is left, with copies of a plugin
already listed going last; a set installed in two formats shows once
rather than pushing anything out. Only the list is cut. A chain keeps
loading, and keeps its controls, whether or not its plugin fits in it.
Changing the plugins used by your chains refreshes the offered list even
when their identifiers contain unusual characters. Copies are recognized by
name, with an optional vendor prefix, and matching input and output widths.
Two different names sharing only their last word remain separate plugins.
The message limit includes escaped Unicode names, parameter labels and
native-plugin metadata such as bundle paths. A large or unusually named
collection is shortened before sending, so it cannot make the UI
discard the entire list as an oversized message.
Controls with malformed metadata are left out; the rest of the plugin and
the catalogue remain available.

<a name="profiles"></a>
### 3.6 Save and recall a scene

1. Set everything the way you want it: hardware controls, sends,
   masters, monitor outputs, inserts.
2. Header, Profiles: type a name and press Save.
3. To recall: Profiles, then the name. To remove: the cross next to it.

Saving or loading from the UI first sends any waiting slider edits.
Saving immediately after a drag therefore includes its last value. Loading
then restores the selected snapshot, and an older queued edit cannot overwrite
it. If loading fails, the preceding edits remain in place.

Profiles belong to the interface they were saved with; another device
shows its own list. With the OpenDeck plugin a key can recall a
profile ([section 4](#stream-deck)).

Legacy profiles directly under `~/.config/openxlr/profiles` are moved into
the Wave XLR Pro folder, `0fd9-00b4`, at startup. If that folder already
contains a profile with the same name, both copies are preserved and the
remaining legacy profiles still migrate. The existing device-specific
profile is the one shown in the picker; the colliding original remains in
the legacy folder for manual comparison.

**Recall on connect.** The "On connect" picker under the list names a
profile the daemon recalls by itself whenever the interface connects
fresh: at daemon start (so at login), after a replug or a power cycle,
and when you switch to it in the device picker. Use it to land on a
known scene at every login. The reconnect after a passing USB error
does not count, so the recall never undoes changes you made since.
Pick "(none)" to stop. Recall waits until the mixer has finished restoring
its settings, so those settings cannot overwrite the chosen profile. A named
profile also waits for a plugin install or rescan in progress; restoring the
last settings does not, so a device plugged in during a rescan comes back at
its last state rather than its boot values.
A manual profile load supersedes an automatic recall still waiting to run.
An arrival that belongs to a previous connection is ignored, including a
replug of the same model. If the chosen profile is missing or unreadable,
the daemon logs the failure and uses the last stored hardware settings
instead of saving the device's boot values over them.

If a profile's device settings apply but its mixer settings fail, the error
says so and gives the mixer failure.

If the profile files cannot be read, the UI keeps showing the device
and the mixer, with a warning and no profiles to choose. Once the files are
readable again the warning clears, without restarting the daemon.

Loading a named profile, by clicking it or on connect, restores its saved
gain even when the gain lock is on. The lock remains on and still blocks
ordinary gain changes. A profile is a saved snapshot: changing a control
does not update it. Press Save with the same name to keep those changes
for the next recall.

A profile only sets the sends and mixes it names. Add a channel or a
virtual microphone after saving a profile, and recalling that profile
leaves the new one as it is: its sends stay closed, the way a new
channel or mix starts, instead of opening at full level because an
older profile had nothing to say about them. Save the profile again to
take the new sends into it. The same goes for a channel's
[volume](#channel-volume): a channel the profile does not know keeps its
volume, and a channel it knows but saved before that channel had a volume
goes back to 100%. A profile saved before XLR 1, XLR 2 and Aux In had a
volume recalls them at 100%, and one saved before Line In existed recalls
its volume at 100% and leaves its gain as it is.

A profile records which mix feeds each selected output, even an output on the
default monitor mix, so it plays the same mixes after you delete or add a
monitor. An output whose saved mixes no longer exist, or one an older profile
left to the default, hears the default monitor mix at the time of the recall.
A ducking setup saved without mixes ducks the default monitor mix as well.

Loading a profile rebuilds only the plugin chains it changes. A chain with
the same plugins, order, bypass and host as the profile's keeps running and
takes the profile's control values as it plays, and an open plugin editor
stays open. A chain the profile changes is rebuilt on its own, so audio
through that chain stops for a moment. A profile replaces the chains it
names. A mix it does not name loses its plugins, as before; an input it
does not name keeps its chain. Older versions saved an input's chain only
once it had been edited, so a profile from them may not name XLR 1 or XLR
2, and loading it leaves that input's plugins as they are. Save it again to
have it name both.

**Restoring settings on connect.** For the original Wave XLR and first
XLR Dock, OpenXLR restores the last settings it observed. This policy
does not describe the device's storage: Wave Link offers a separate
hardware-save action for the Wave XLR that OpenXLR has not mapped. The
daemon remembers changes and writes them back whenever the
interface connects fresh, so a reboot or a replug leaves you where you
were, with no profile needed. The picker shows "(last settings)" in
place of "(none)" on these devices; a chosen profile takes precedence.
"Reset device to defaults", in Options under INTERFACE (away from the
profile picker, where a slip would be costly), writes the firmware
defaults back and forgets the remembered settings (saved profiles
stay). The defaults are recorded the first time the interface connects
after a power cycle, so the button asks for one replug on a fresh
install.

Saved hardware snapshots under `~/.config/openxlr/devices` must be regular
JSON files no larger than 64 KiB. An unreadable snapshot is not applied:
restoring last settings logs the problem, while Reset reports it.

The Wave XLR Pro keeps its settings in its own memory, so there is no
clean state to record and whatever is set on Linux is what Wave Link
finds on Windows, and the other way round. For it the same button
writes OpenXLR's baseline instead: gain 30 dB on both inputs, every
processing stage and phantom power off, headphones and aux level at
half. Output routing, the microphone's level in the headphones, the Line
In gain and saved profiles stay. The gain lock has to be off.

<a name="default-devices"></a>
### 3.7 Hold the system default devices

Session managers like to switch the system default output to a newly
appeared device, and some applications follow that default. In Options,
SYSTEM DEFAULT DEVICES, choose the output and input OpenXLR should
hold; it re-asserts them once a second and reverts any outside change.
"(don't enforce)" leaves the system alone.

Stopping the daemon waits for a repair in progress to stop, so nothing
changes the desktop's default devices after the daemon has exited.

<a name="hardware-only"></a>
### 3.8 Hardware control only

In Options, AUDIO, turn off "Enable software mixer". This controls the
application channels, mixes, virtual microphones and plugin inserts. The
daemon restarts when you change it, briefly interrupting audio. With it
off, the sound card keeps its stock PipeWire layout, including the Pro's
UCM profile where one is installed, and the `OpenXLR …` devices, mixes,
virtual microphones and inserts go away. Output devices muted for a
monitoring pause or a closed monitor feed get their previous mute flags back
as the daemon stops. The INPUTS and HEADPHONES cards keep working. Turn it on
again the same way.

<a name="autostart"></a>
### 3.9 Start at login, tray

Options separates login startup from how the UI behaves.

Under AT LOGIN:

- "Start audio in the background" enables the daemon's
  systemd user service. On a packaged install this is the package's own
  unit; under Flatpak it is a background portal request instead, see
  [Flatpak](flatpak.md). This does not start the UI or create a tray icon. If
  the service cannot be enabled or disabled, Options says so and keeps
  the previous preference rather than saving one the system does not have.
- "Start the OpenXLR app" writes a desktop autostart
  entry for the UI in `~/.config/autostart/openxlr.desktop`,
  including on KDE Plasma and CachyOS. The file is written the way any
  desktop application writes there: the folder keeps its own permissions,
  the entry gets the ordinary permissions your system gives new files,
  and an entry you turned into a symbolic link is never replaced. Failed
  changes show an error in Options and keep the previous preference.
- While that preference is on, a launch repairs the entry only where a
  repair is needed, and only once per installation path:
  - an entry whose command still exists is left exactly as it is, so a
    command you edited yourself keeps working;
  - an entry whose command points at a program that is gone gets this
    installation's path, keeping every other line, including a
    `Hidden=true` disable written by a desktop autostart editor;
  - an entry that has disappeared is created again only when OpenXLR
    moved since it last wrote one. If you removed the entry yourself
    with a desktop tool, it stays removed and Options says so; turn the
    option off and on again to get it back.

The two login switches are independent. Enable both for audio and the app
at login. Audio alone starts without the UI or a tray icon. The app alone
starts without the audio service, which you must start separately to use
it. With both off, neither starts at login. The hint below the switches
describes the combination you chose.

Under WINDOW:

- "On launch" chooses "Show mixer" or "Tray only". It applies the next
  time the app starts, whether at login or by hand; changing it does not
  hide the UI or enable autostart. "Tray only" starts without
  showing the UI; the tray icon shows it the first time you click it. For
  a tray icon at login, enable "Start the OpenXLR app" and choose "Tray
  only". The tray icon is there from the start, whether or not the UI
  has been shown.
- "When closing" chooses "Keep running in tray" or "Quit app" and takes
  effect immediately. Keeping the app in the tray hides the UI
  instead of quitting; the tray icon's "Show mixer" menu item restores it,
  including when the UI was minimized. "Soundboard" opens the Soundboard
  window ([section 3.14](#play-sound-clips)). "Quit OpenXLR" in the tray
  menu always exits. Neither choice stops the background audio service,
  except under Flatpak, where the UI owns its daemon and Quit stops
  both, so the Flatpak keeps running in the tray unless you choose
  otherwise ([Flatpak](flatpak.md)). In Game Mode on SteamOS and
  Bazzite, which has no tray, closing always quits the UI.

Launching OpenXLR again shows the UI that is already running. A launch requested while
the first process is still initializing is kept until the UI is ready,
including when the first process was set to start in the tray. Several such
requests show the UI once, rather than starting additional copies.

A Linux desktop gives an application no way to find out whether a system
tray is there, so OpenXLR cannot fall back to quitting when there is none.
On a desktop with no tray, choose "Show mixer" on launch and "Quit app"
when closing. These choices do not change either login switch.

Only one UI runs per user. Starting OpenXLR again, from the menu or a
shell, brings the running UI to the front, out of the tray if it is
hidden there, instead of opening a second one.

To land on a known scene at every login, mark a profile to recall on
connect ([section 3.6](#profiles)). An interface using connect-time restoration comes back
as you left it without one.

The UI also remembers which of its sections (INPUTS, HEADPHONES,
MONITOR, APPLICATIONS, SUBMIXER) you collapsed with the chevron in
their header, across restarts.

<a name="arrange"></a>
**Arrange**, in the UI's header between Profiles and Flow, shows a
drag handle in each section's header and on each channel and mix tile you
can move. Drag a section's
handle onto another section: the arrow on the other handle shows whether
it lands above or below. Drag a channel's or mix's handle onto another
tile in its row, left or right of it. Only the handles start a move;
faders, buttons and the chevron work as usual. Focus a handle and press an
arrow key to move it one place, or press Escape to cancel a drag. Holding
a handle near the edge of the UI or of the channel row scrolls it, and
letting go anywhere but on a tile of the same kind changes nothing.

The section order is a UI preference, saved in `ui.json` like the
collapsed sections, and **Reset sections** puts the five back in their
usual order. If `ui.json` cannot be written, the new order and collapsed
sections stay for that run, and the note above the sections says why.
The same note explains if a skin's initial collapsed sections cannot be
remembered. Each of these saves writes only its own choice, so the note
stays until the choice it names has been saved: a later move that saves
clears a note about the order, a later fold that saves clears one about
the folded sections, and a save of one never clears a note about the
other. The same holds for the bank choice and for where the submixer
goes.

A skin can place the header and the sections itself, in rows, columns,
grids and docked sides ([Layout](skins.md#layout)). Under such a skin a
section dropped on another trades places with it, in the same row or
column or across from one part of the UI to another; the other
handle shows ⇄ while you hold it there. Between neighbours in one row or
column a drop still lands before or after, which comes to the same thing.
The arrow keys trade places with the next section that way in the same
row, column or grid. To move a section somewhere the arrows do not reach,
focus its handle and press Enter or Space. A menu lists every other
section by name, **Swap with INPUTS** and so on, and choosing one trades
the two. Holding a handle near the edge of a layout wider than the UI
scrolls it sideways.

A skin can also hide a section. While Arrange is on, a **Hidden
sections** line under the header lists each one with a **Show** button;
showing it is kept for that skin. A section the UI itself sets aside,
such as MONITOR without the software mixer, stays hidden whatever the
skin says. A skin may start a section folded the first time you wear it;
after that it stays folded or open as you leave it.

Under a skin with a layout, Arrange starts from the skin's own places and
remembers your moves and the sections you showed for that skin alone;
they do not change the order other skins use. **Reset sections** then
puts the sections back where the skin places them, hides again what it
hides, and forgets only that skin's arrangement. If the skin is updated
and its layout no longer has a place your arrangement names, the whole
arrangement is set aside, the note under the header says so once, and
the skin's own places are used. Switching to a skin without a layout,
such as Material or Deck, brings back the
order you arranged there.

A skin can also order the header's actions and put some of them in a ⋯
menu at the end of the header. The actions in that menu work as they do
in the header. Options is always the first button in the header,
wherever the skin puts the header, so the way back is always there.

<a name="mix-banks"></a>
A skin can also draw INPUTS as a rack, one strip per channel, and the
SUBMIXER as mix banks ([Templates](skins.md#templates)). The rack holds
the INPUTS panel's own controls: an XLR input's gain, switches and plugin
chain, Aux In's level and lock, Line In's gain and lock, and the name, meter and
[volume](#channel-volume) of every other channel. What your interface does
not have stays hidden, as in the panel.

Each mix bank shows one mix: every channel's send into it and the mix's
master. The picker at the top of a bank lists every mix that is not
hidden. Choose one to
show it in that bank; a mix another bank already shows trades places with
it, so two banks never show the same mix and every mix stays reachable
however few banks the skin shows. Your choice is remembered for that skin
in `ui.json` and comes back the next time you wear it; until you choose,
the banks start on the mixes the skin names. When a bank's mix is deleted,
the bank moves on to the next mix. The compact and mini views apply in the
banks too.

Choosing a bank's mix changes no level, mute or route, and the channel
and mix order stays the mixer layout's. In Arrange mode each channel has
one handle, on its strip in the input rack while the rack shows,
otherwise on the first bank, and the daemon saves a move as it saves any
other. While banks show, reorder mixes in the layout editor. A meter
beside a send is the channel's own level, before the send.

Channel and mix tiles follow the mixer layout's one order, the same one
the layout editor's arrows and the terminal mixer change. A drop sends
the new order to the daemon, and the tiles move when it has saved it; a
failed save leaves them where they were and the note shows the error.
Hardware inputs and Aux keep their place and have no handle. Every monitor
mix, Monitor A/B included, can move. A hidden channel keeps its place in the
order. Moving tiles
changes no routing; [Edit the mixer layout](#layout) says where an app
goes when its channel is deleted, which never depends on the order.

<a name="mixer-view"></a>
Options, APPEARANCE, **Mixer view** chooses how the SUBMIXER card is drawn:

- **Full mixer** shows every channel strip the interface can feed and
  every mix, less the hidden ones.
- **Compact** shows one channel strip with its sends into every mix that
  is not hidden. Pick the channel in the box above the strips; hidden
  channels are in that list too, so a hidden channel can still be adjusted
  here.
- **Mini** shows that channel's send into one mix, with that mix's master
  below it. A second box picks the mix, hidden mixes included. INPUTS, HEADPHONES and APPLICATIONS
  are set aside and Arrange is off, so the UI fits a narrow column with
  MONITOR's output volume above the strip.

In a narrow UI, MONITOR puts the device picker above the volume row
so both remain usable. Long device names shorten with an ellipsis; hover
over the picker to read its full summary.

The view, the channel and the mix are saved in `ui.json`. When the chosen
channel or mix is gone, after a layout change or with an interface that
has no jack behind it, the view shows the first available one and goes
back to your choice when it returns. If `ui.json` cannot be written, the
choice stays until the UI closes and a line says why: in Options for
the view, above the strips for the channel and the mix. None of this
changes a level, a mute or a route.

<a name="show-hidden"></a>
**Show hidden** sits in the SUBMIXER header beside Pop out, Edit layout,
[Mic setup](#mic-setup), Ducking and [Soundboard](#play-sound-clips), and appears only while a channel or a mix is hidden in the layout
editor. Switched on, it draws the hidden channels and mixes in their
places in every layout, faded so you can tell them apart, and they work
as usual. This applies in the separate mixer window too. It is a UI
preference saved in `ui.json` like a folded section, so the layout, the
profiles, the terminal mixer and the Stream Deck do not change. A skin
sets how far they fade with `Ox.Mixer.HiddenOpacity`.

<a name="separate-mixer-window"></a>
#### Separate mixer window

**Pop out** in the SUBMIXER header moves the existing channel strips and mix
masters, or a skin's mix banks, into their own resizable window. It can sit on
a second display while the UI is hidden in the tray. The separate window uses
the same live mixer, skin, control sizing and compact or mini view selected in
Options. It scrolls when the controls do not fit, without changing any audio
routing.

<a name="mixer-size"></a>
**Mixer size**, under the return button at the top of the separate window,
enlarges the whole panel there: text, faders and buttons together. The skin
and the UI stay as they are. **100%** is the skin's own size and the default. **125%**
to **200%** enlarge it by that much; the tiles wrap to the window's width and
the window scrolls when the enlarged panel does not fit. **Fit to window**
picks the largest size, up to 300%, at which every control shows without
scrolling. It picks again when you resize the window, add or remove a
channel, or change the view, the skin or Touch sizing. It never goes below
100%, because a smaller panel would draw text and fader targets smaller than
the skin sets them. A window too small for the panel at 100% scrolls. For a
smaller mixer, use the compact or mini view, or Standard sizing.

Fit keeps the panel's proportions. An ultrawide window can still have space
beside it; choose a larger fixed or custom size if you prefer bigger controls
with scrolling.

**Custom** shows a percentage box beside the picker, or under it in a narrow
window. The box starts at the size the panel has now, so picking Custom after
Fit to window keeps the size Fit found. Type any whole size from 100% to
300%, or step it by 5% with the arrows, the arrow keys or the mouse wheel.
Each step applies at once. A typed size applies once it reads as a size from
100 to 300, so the panel does not jump while you type the first digits.
Enter, or leaving the box, settles what you typed. A size past either end
becomes 100% or 300%, a fraction is rounded, and the box shows the size
drawn. The tiles wrap and scroll as they do at the listed sizes. Picking Fit
to window or a listed size hides the box.

The size is saved in `ui.json` as `mixerScale` and applies each time the
mixer pops out, Custom with its percentage. A custom size equal to a listed
one, such as 150, comes back as that listed size. The panel goes back to the
UI at its usual size. If
`ui.json` cannot be written, a note under the picker says so, and the size
applies until the window closes.

**Return mixer to main window**, or closing the separate window, returns the
panel and shows the UI. Quitting OpenXLR closes both. The separate
window opens when you ask for it, or once when you put on a skin that opens
the submixer in its own window; it does not change profiles or section
order. Arrange mode ends when the mixer is detached; arrange tiles in the
UI after returning it.

Pressing Pop out, or returning the mixer, is remembered in `ui.json` as
your choice. While you have made one, no skin moves the submixer: a skin
that would open it in its own window does not, and no skin switch ever
closes the separate window. Switching skins while the mixer is detached
keeps it detached and changes its presentation in place; the window widens
when the new presentation needs more room than the old one and than the
window has, and otherwise keeps the size you gave it. **Reset this skin's
arrangement** in Options forgets the choice. The order is in
[The mixer's own window](skins.md#the-mixers-own-window).

Plugin windows opened from the detached mixer belong to that window and
stay open through skin switches. Returning the mixer to the UI, or
closing the separate window, opens them again under the UI, in
the same place, on the same chain or plugin. Deleting
a mix closes its plugin windows. Recreating the mix opens its new chain rather
than an older window with the same mix id. Renaming a mix updates its open
plugin-chain window without reopening it.

Deleting a channel or mix also discards its unsent fader changes. A new
channel or mix with the same name starts from its own saved values; old
controls cannot change it.

Removing or replacing an insert also retires its controls. Queued parameter
changes are discarded, and late bypass, host, removal or rename actions from
the old controls cannot affect a plugin that now uses the same slot id.

<a name="skins"></a>
### 3.10 Change how the UI looks

Options, APPEARANCE picks a skin. OpenXLR ships seventeen, all built into
the application, so a package brings them with it:

- **Material**, the classic appearance, in a dark palette, which is what
  the UI looked like before Broadcast became the default, and a light
  one;
- **Deck**, which dresses the UI in the visual language of the
  OpenDeck keys and the Wave interfaces: near-black faceplates, black keys
  whose lettering is backlit green when a control is on and red when
  something is muted or bypassed, console faders with a machined cap,
  indicator lamps in a bezel, and meters that run green to amber to red
  across the scale;
- eleven built from the Omarchy palettes, for a desktop already wearing
  one of them: Catppuccin, Catppuccin Latte, Everforest, Gruvbox,
  Kanagawa, Matte Black, Nord, Osaka Jade, Ristretto, Rose Pine and Tokyo
  Night. Catppuccin Latte and Rose Pine are light;
- **Broadcast**, the default, a dark neutral palette that also rearranges
  the UI, described below;
- **Compact Desk**, a denser dark matrix with bounded headphones and
  application tiles wrapping across the page, described below;
- **Nyan Pink**, dark plum cards, candy pink faders and cyan details,
  using the classic arrangement and your saved section order;
- **Wave**, dark charcoal cards, grey faders and green meters, arranged
  the way Broadcast is: everything said about Broadcast below holds for
  Wave too, in its own colours, with a wider name column and the cards a
  little closer together. Your Arrange moves are kept for each of the two
  separately. Choosing it changes appearance and arrangement, not routing
  or audio levels.

A new install opens on Broadcast. An install that never picked a skin
opens on Broadcast too, so after upgrading from a version that started on
Material the UI has the mix matrix. A skin you picked yourself,
Material included, stays. To go back to Material, open Options and choose
**Material** under APPEARANCE; the UI rearranges at once and keeps
Material from then on.

<a name="broadcast"></a>
**Broadcast** draws the SUBMIXER as a mix matrix. Each row is a channel and
each column a mix, both in the mixer's order, and where a row crosses a
column is a cell with that channel's send into that mix: a mute button on
the left and a short fader whose track is the channel's own meter, green
rising to yellow and red. A muted send's cell greys out, its fader faded,
and its mute button's letter turns red. While you move a fader, its level
shows at the top of the cell. A channel with no send into a mix has an
empty cell there.

The head at the start of each row carries the channel's icon and name and
what INPUTS would show for it. XLR 1 and XLR 2 have their mute, their gain
with the meter under it, **FX**, which opens their plugin chain, and over it
**EQ**, which opens their [EQ editor](#microphone-eq); the gain lock and
phantom power's settling time still apply. Beside the name,
**Mic** opens the switches INPUTS shows next to the mute: Low Cut,
Expander, Voice Tune, Phantom, ClipGuard and Compressor, the ones your
interface has, with the Voice Tune strength while Voice Tune is on. On an
interface without a hardware low cut or ClipGuard, XLR 1's software low cut
and ClipGuard are there too. Under them is the microphone's
[noise removal](#noise-removal), which also gives a capture input with no
interface behind it a **Mic** button. They are the same controls as in
INPUTS, so switching one here switches it there. **Mic** turns green while
one of them is on or noise removal runs, and its small lamp lights red while phantom power is on, so you can
see both without opening it. Under **Mic** is the input's gain in dB. While
you move the row's volume fader it shows the volume instead, as a
percentage, and goes back to the gain one second after you let go. A
capture channel that carries the interface's microphone has **Mic** too,
and a strip bound to an additional interface has that interface's
switches. Aux In has its level and **Lock**, Line In its gain and **Lock**, and both
show the level or gain in the name column the same way
([readout](#gain-readout)). Each of them also has its [volume](#channel-volume),
a second fader under the gain or the level, and the row grows to hold
both. An application or capture channel has its volume, with its meter
under it.
XLR 2 has a row only on an interface with two XLR inputs. The head at the
top of each column carries the mix's icon and name, how many outputs ticked in
MONITOR hear it (or what kind of mix it is when none does), a thin line in
the mix's colour when you gave it one in the layout editor, and its mute,
master level and **FX**, which opens the mix's plugin chain. **FX** is
green while the chain is processing and red when its plugins are all
bypassed or failed; an empty chain keeps the plain button.

HEADPHONES and MONITOR share the row above the matrix: the headphone
levels and the direct monitor blend on the left, the output picker and its
volume in a toolbar on the right. INPUTS is hidden, since the row heads
carry the input gain, mute, switches and plugins; **Arrange** lists it with a button
that shows it again, under the matrix. APPLICATIONS sits under the matrix,
its applications in columns of six: the seventh starts the next column to
the right, and columns that do not fit the UI's width continue below.
Every application's tile is the same width, with its channel picker lined
up with the ones above and below it. A name too long for its tile is cut
off at the edge; rest the pointer on the tile and the name glides to its
end, and back when the pointer leaves. Its tooltip shows the whole name.
Nothing starts folded, and every header action stays in the header row.
When the matrix is wider than the UI it scrolls sideways inside its
card, the row heads with it; a UI narrower than about 1000 pixels
scrolls the whole page sideways instead, since HEADPHONES and MONITOR
share a row. In Arrange mode a
channel's handle is on its row head and a mix's on its column head; a drop
saves the new order as it does with the tiles. The compact view shows the
chosen channel's row, and the mini view that row's cell in the chosen mix.
Your Arrange moves are kept for Broadcast alone, and **Reset this skin's
arrangement** in Options puts everything back where the skin places it. A
copy of the skin to start your own from is in
[docs/examples/skins](examples/skins/README.md).

**Compact Desk** is an optional dark skin for a denser desk. Choose it in
Options under APPEARANCE. It keeps the headphone card 600 pixels wide and
lets MONITOR use the rest of the row. Applications wrap across the page
instead of filling six rows first. Longer matrix faders and wider name
columns keep a three-mix desk from leaving half the card empty; muted
faders stay visible against the dark blue-grey surfaces. Profiles,
Flow and Arrange stay in the header, with Restart daemon, the version and
About in its overflow menu. All the same headphone, input, effect and mix
controls remain available. A narrow window scrolls the page sideways as
Broadcast does, and the normal and Touch control sizes still apply.

The choice takes effect at once. Windows that are already open repaint;
audio, the mixer, the routing and the layout are untouched, and nothing is
restarted. The choice is saved in `~/.config/openxlr/ui.json` and is not
part of a profile or of the mixer layout. A skin you install may also set
the order of the UI's sections and run them in a row; the sections
move in place, and [Arrange](#arrange) says how your own moves are kept
under it. It may also draw INPUTS as a rack and the SUBMIXER as
[mix banks](#mix-banks) or a mix matrix, as Broadcast does, MONITOR as a one-row toolbar, APPLICATIONS as a
list for a narrow column or in columns, and open the submixer in its own window the
first time you put it on (see [Separate mixer window](#separate-mixer-window)).

Under the skin picker, one line says what the chosen skin does to the
UI's arrangement, for example "This skin arranges the window: a grid,
an input rack, 2 mix banks." A skin without a layout keeps the usual
arrangement. The next line says where the submixer goes: where you last
put it, or, while you have not chosen, what the skin does with it.
**Reset this skin's arrangement** puts the sections back where the skin
places them, as Reset sections does, starts its banks on the skin's own
mixes again and forgets where you put the submixer. Arrange moves and bank
picks under other skins stay as they are. Nothing opens or closes when you
press it; a skin that opens the submixer in its own window does so again
the next time you put it on.

Switching to a skin that rearranges the UI keeps what you were doing.
A fader held down stops where it is. Focus stays on the same control, or on
the control for the same channel, mix and role when the skin draws it
differently, and the page keeps its scroll. Folded sections, the compact
view's choices, levels and routing are untouched.

Below the skin, **Mode** picks Material's palette: **System**, **Light** or
**Dark**. System, the default, follows the desktop's light or dark
preference through the desktop portal and repaints the open windows when
the desktop switches; a desktop that states no preference gets the dark
palette. On a desktop that prefers light, Material therefore opens light
unless you choose Dark. Other skins keep their own colours in every mode,
so the UI wearing Deck, Broadcast or an Omarchy palette looks as it did, and Mode is
greyed out while one of them is chosen. The mode is saved in `ui.json`
beside the skin.

**Controls** picks **Standard** or **Touch** sizing for the UI.
Touch is meant for a touch screen. Buttons, dropdowns, check boxes and fader
caps get targets at least 44 units square, every slider row is tall enough
to hold its cap, the rows of an open list grow to match, and channel strips
and mix tiles widen to 180 and 280 units. It works with every skin, and a
skin that already draws larger controls keeps them. Switching leaves levels
and routing alone, and the other windows keep their size. The choice goes on
at once, is saved in `ui.json` as `touchControls`, and is not part of a
profile. If it cannot be saved, it is still worn for the run and Options
says why on the line at its foot.

OpenDeck keys and dials take their colours from the chosen skin and
repaint when it changes. While the UI runs they use the colours it
applied, light or dark; without it the plugin reads the saved skin itself
([skins.md](skins.md#stream-deck-colours)).

Anything wrong with a skin is listed under the picker, not in the mixer
itself. A skin that sets only part of the appearance keeps the default for
everything else.

Your own skins go in `~/.local/share/openxlr/skins/<name>/skin.json`;
"Reload" reads the folders again without restarting. A skin is a data file:
it holds named colours, sizes and local images, and it picks from the
control appearances OpenXLR itself draws. It cannot carry code, markup or
a template, reach a file outside its own folder, or make a network
request, and it cannot change what a control does: a fader a skin has
redrawn is still the same fader underneath. A plugin's own editor window belongs to the plugin and is
not skinned. [skins.md](skins.md) is the full list of values and the rules
a skin is read under.

If a skin makes something unreadable, start the UI once with

```sh
OPENXLR_SKIN=default openxlr
```

which ignores the saved choice for that run, wears dark Material whatever
the mode says, and lets you pick another one.

<a name="language"></a>
Options, APPEARANCE, **Language** picks the UI's language. **System
language**, the default, follows the desktop's message language
(`LANGUAGE`, `LC_ALL`, `LC_MESSAGES`, then `LANG`) and falls back to
English. The UI reads its text when it starts, so a change applies the
next time it opens. The choice is saved in `ui.json` as `language`; if it
cannot be saved, Options says why on the line at its foot. OpenXLR ships in
English, German, Spanish, Italian, Tagalog, Cebuano (Bisaya) and Serbian (Cyrillic), and each further language is added by a
contributor who speaks it; [localization.md](localization.md) says how.
Names you give channels, mixes and profiles, text from plugins and
messages from the daemon are shown as they are. Longer translated plugin
actions in Options wrap to another row when they do not fit.

<a name="upgrade"></a>
### 3.11 Upgrade

Packages do not restart a running daemon, so the daemon from the previous
release keeps running after an upgrade. If the UI was open before the
upgrade, quit it from the tray and open it again first. The old UI keeps
talking to the old daemon and cannot reach the new one.

When the UI can connect but the daemon is another version, it shows a
banner naming the daemon's version and its own, with a Restart daemon
button, and offers only the controls the old daemon reports.

Releases before 0.3.0 served their clients on a loopback port with a
token. Since 0.3.0 the daemon uses a private Unix socket instead, authenticated by
Linux peer credentials ([section 6](#files)), so the new UI cannot talk to
the old daemon at all. It still recognises one, because the old daemon
holds the same single-instance lock and answers on its old port. The header then reads
"Older daemon running" instead of "Daemon not running", and the banner says
that a daemon from before the update is still running and this UI cannot
connect to it.

Restart daemon in the banner does the same as the header's button. It
restarts the `openxlr-daemon` user service; in the Flatpak it restarts the
UI's own daemon, or asks the `openxlr-flatpak` user service's daemon to exit
so the service starts the updated one. The UI connects as soon as the new
daemon opens its socket, and the banner goes away. A daemon you started by
hand is not a service, so stop it and start it again yourself. To restart
the service from a terminal, run

```sh
systemctl --user restart openxlr-daemon
```

and for the Flatpak's service, on the host,
`systemctl --user restart openxlr-flatpak`.

The terminal mixer, the OpenDeck plugin, the Omarchy bar and the browser
companion cannot restart the daemon. While the old one runs, the terminal
mixer shows the same sentence with the command above its tabs, OpenDeck keys
and dials read "restart" where they would read "offline", the Omarchy popout
shows the sentence in place of its offline line, and the browser companion
reports it as its error. `openxlr doctor` lists it as a finding with the
restart command. Each of them connects on its own once the daemon has been
restarted. Upgrade the UI, the terminal mixer, the OpenDeck plugin and the
Omarchy plugin together with the daemon; older clients that use the loopback
port cannot connect to the new daemon. No API token file is required, and
the daemon removes the one an older release left behind.

Changing "Enable software mixer" in Options, AUDIO also restarts the daemon.

OpenDeck and the Omarchy bar use the client-only `openxlr-control` launcher.
Upgrade it together with the clients; older installs do not have this entry
point. An OpenDeck plugin installed ahead of OpenXLR shows "update" on its
keys and dials while the older OpenXLR runs: update OpenXLR to the plugin's
release. Source builds must [link it onto PATH](install-from-source.md#6-make-it-permanent).
It exits when the daemon disconnects, even with stdin open, so clients clear
their state and reconnect. When it cannot connect it writes one
`controlError` line saying why in place of its ready line, which is how
OpenDeck and the bar learn about a daemon from before the update
([api.md](api.md)). OpenDeck stops an adapter that has not signalled
readiness within 15 seconds. A runtime-directory or socket-path error is a
startup failure with a specific diagnostic. Only actual lock contention
reports another daemon; Flatpak treats only that case as an existing session.

<a name="layout"></a>
### 3.12 Edit the mixer layout

The default channels and mixes are a starting point. Edit layout in the
SUBMIXER card opens the layout editor: application channels on the left,
mixes on the right, each with move up and down, Rename and Delete, and a
box at the bottom to add one. Add capture input, under that box, turns
another PipeWire capture source into a channel
([Additional capture inputs](#capture-inputs)). The hardware inputs and Aux
are listed in fixed places; only their appearance can change. Every monitor mix, Monitor A/B included, can be
renamed, reordered and removed. The last remaining monitor cannot be deleted.

Under MIXES, enter a name, pick **Virtual microphone** or **Monitor mix**
and press Add mix. A virtual microphone is a source a recorder or a call
app can pick. A monitor mix is another blend to hear, for headphones or
speakers that should not get Monitor A or B: tick the output in the
MONITOR card, set its feed picker to the new mix, then open the sends you
want. Its master is a desktop volume like Monitor A and B, so the desktop's
volume and mute controls move it, and it goes to 150%. It publishes no
microphone. Both kinds count toward one limit of 18 editable mixes,
Monitor A, Monitor B, Stream and Chat included. In each output's feed picker,
tick any combination of mixes, including the monitor mixes you added,
to hear their sum.

Reordering updates the open UI as soon as the daemon publishes the saved
layout, including changes made through the API. Channel tiles, mix controls
and each channel's send rows follow the same order without resetting their
levels or mute state. The order decides only where things are drawn. An app
whose channel is deleted, or whose remembered channel no longer exists,
goes to System; if you deleted System, it goes to the application channel
whose id comes first alphabetically.

Every channel and mix wears an icon. Until you pick one it wears the
default for its kind: a microphone for XLR 1 and XLR 2, a jack for Aux In,
Line In and the Aux port, a microphone for a mono capture input, a headset for a
Bluetooth capture source, a camera for a stereo capture pair, a gamepad, a note, a
browser window, a cog, a speech bubble and a burst for Game, Music,
Browser, System, Voice Chat and SFX, a grid of tiles for any other
application channel, a headset for every monitor mix, a broadcast mast
for Stream, two people for Chat and a podcast microphone for any other
virtual microphone.

**Appearance**, under each channel and mix name in the layout editor, gives
that item an icon and a colour. Click one of the twelve swatches above the
colour field to fill it in, or type any colour as `#RRGGBB`; the struck-out
swatch empties the field. The swatch that matches the field has a ring. The icon list shows each
icon beside its name: **Default** with the name of the kind's icon, **None**
for no icon at all, the built-in set, the worn skin's own icons if it has
any, and under **Text** the seven text glyphs ● ♪ ♫ ✦ ◆ ▶ ◉ that older
layouts used. The same list sits beside the name box where you add a
channel or a mix, starting at **Default**, so a new item gets its icon in
the same step. Leave the colour empty to keep the skin's colours. The
UI draws the icon in that colour in every skin. The channel and mix
tiles put it before the name on the same line, and a channel or mix with
no icon starts its name at the tile's edge. A skin can redraw an icon but
not choose it.
The terminal mixer puts the icon before the name on its strip as one text
character, in the same colour. Stream Deck keys and dials whose target is
that channel or mix show the icon in place of their glyph; a key also takes
the colour on its label and border, a dial only on its corner icon, unless
the key has its own icon chosen in its settings. Mute and offline indicators keep their usual colours. An icon
that only a skin draws shows as the kind's default in the terminal mixer,
on the Stream Deck, and in the UI under another skin.
The headphone outputs are listed at the top of CHANNELS: **Phones 1** and,
on a device with a second jack, **Phones 2** open the same dialog without
the hidden flag. Their icon, a headset until you pick another, and their
colour show before the labels in HEADPHONES.
For a channel, **Hide this channel's strip in the mixer** removes its tile
from the SUBMIXER card and its strip from the terminal mixer's Mixer
section. Nothing about its audio changes: its sends, meter, applications
and routing stay as they were, it stays in the layout editor, the app
pickers and the Matrix section, and Stream Deck keys keep controlling it.
The editor marks it "hidden in the mixer".
For a mix, **Hide this mix in the mixer** does the same for its master and
its column, and works for every mix, Monitor A, Monitor B, Stream, Chat and
Aux included. Its audio, outputs, monitor feeds, sends, volume, mute and
plugins stay as they were. The SUBMIXER card drops its master tile and the
send rows into it. A mix matrix drops its column head and the cell under it
in every row, so the other columns stay lined up. Mix banks leave it out of
their pickers. The terminal mixer leaves it out of its Mixer section and
keeps it in Matrix. It stays in the layout editor, in each output's feed
picker and on Stream Deck keys. If you hide
every mix, the channels show with no sends and the matrix shows only its
row heads until you show one again. MONITOR and HEADPHONES are not mixes
and cannot be hidden. Untick the box to bring a channel or a mix back, or
use [Show hidden](#show-hidden) to see it without changing the layout.
Appearance is part of the layout, saved with it, and not part of a profile.

- A new channel appears as a playback device at once and starts muted in
  every mix, so route an app to it and open the sends you want.
- A new mix receives nothing until you open a send. Pick a new virtual
  microphone in OBS, Discord or any recorder like a microphone; pick a new
  monitor mix in an output's feed picker.
- Renaming a channel reloads its playback device under the new name.
  Apps playing into it keep playing after a short gap; nothing else is
  touched.
- Renaming a mix changes the name in OpenXLR and on the Stream Deck right
  away. Other applications keep listing the old device name until the
  daemon restarts, because reloading the device would throw them off it.
  The UI shows a restart hint; restart when nothing is recording.
- Deleting a channel moves its apps to System, or to the application
  channel whose id comes first when System is gone. Deleting a virtual
  microphone removes the device, and anything recording from it loses it.
  Outputs that followed only the deleted mix
  return to the default monitor mix, while summed feeds keep their other mixes.
  The default is Monitor A while it exists, otherwise the monitor whose stable
  id sorts first. Reordering or renaming mixes never changes that choice, but
  deleting the default does, and so can adding a monitor once Monitor A is gone,
  when the new id sorts first. Outputs already following the default keep the
  mix they were playing, and a profile records the mix of every selected output,
  so recalling it after the default changed plays what it played when saved. If the
  mix was the enforced system default output or input, that choice is
  cleared and the desktop picks the default.

Every change is saved before the editor confirms it. Once the daemon has
finished its final save during shutdown or restart, late layout changes are
refused before they alter the mixer or write settings. Retry them after the
daemon starts again. If the settings
file cannot be written the change is undone and the editor says why. The
same happens when pipewire-pulse has no room for more streams
([section 5.8](#open-files) explains the limit and the drop-in that raises
it), and when the new channel's or mix's sends have not appeared within
three seconds: nothing is kept that the daemon could not set.
Ids are generated from names and never change afterwards, so profiles
and Stream Deck keys survive a rename. The layout file is described in
[mixer-layout.md](mixer-layout.md).

<a name="exclusive-groups"></a>
#### Exclusive groups

An exclusive group is a set of channels of which only one is heard, such
as a broadcast microphone and a headset, or a capture card that serves two
consoles. To make one, click Exclusive groups at the bottom of the layout
editor, name the group, tick at least two channels and save. Pick an
existing group in the list to rename it, change its members or delete it.
A channel belongs to one group at most.

Once a group exists, unmuting a member's send in any mix mutes the other
members in every mix, so switching from the microphone to the headset is
one click on the headset's send. Levels stay where you set them. Muting
the member you are using leaves the whole group muted. If more than one
member is unmuted when you save the group or recall a profile, every
member is muted and you unmute the one you want. Deleting a group leaves
the mutes as they are.

A Stream Deck key can switch the group ([section 4](#stream-deck)).

On a Wave XLR Pro, putting XLR 1 in a group turns off the interface's
zero-latency path from the microphone to the headphone jacks, because
that path bypasses the mixer and the group could not mute it. The
microphone then reaches the jacks through its Monitor A send, with the
latency of the PipeWire graph. Take XLR 1 out of the group to get the
direct path back. The microphone's hardware mute is separate from the
group and mutes it everywhere, as before.

<a name="plugin-editors"></a>
### 3.13 Open a plugin's own editor

The controls window is generated from the plugin's parameters and works
for every plugin. Some plugins also ship an editor of their own, with the
meters and curves their authors drew. Opening one goes through a small
host process that the packages install for you. If you build from source,
add one flag to get it, as
[install-from-source.md](install-from-source.md) describes.

For an LV2 insert:

1. Open a plugin's Controls window. A plugin whose editor OpenXLR can
   host shows a "Native host" button.
2. Turn it on. That one insert moves out of the shared filter chain into
   its own process, which rebuilds the chain and interrupts audio for a
   moment. Every other plugin stays where it is.
3. Press "Plugin UI". The editor opens on the plugin processing your audio.
   Changes to exposed parameters appear in the controls window and are
   saved with the mixer and profiles. Other internal plugin state is not saved.
4. Turn "Native host" off to put the insert back in the shared chain.

A CLAP or VST3 plugin has no shared chain to go back to, so it always runs
this way: its row shows no switch, and the cog opens its editor whenever
the plugin is running.

Some PipeWire packages are built without the filter chain's LV2 loader,
and the filter chain can also refuse a single plugin. When it refuses an
LV2 insert and the host process is installed, OpenXLR runs that insert in
the host process instead and leaves its "Native host" switch as you set
it. In a chain of several plugins each one is tried on its own, so only
the ones PipeWire refuses move. If the plugin has an editor the host can
open, "Plugin UI" opens it. The next time the chain is built, after a
change to it or a daemon restart, the shared chain is tried again.
PipeWire's reason goes to the daemon's log and into the insert's
`filterChainError` in saved diagnostics. A source build without the
native flag has no host process to fall back to, and the insert shows
PipeWire's refusal instead.

Dragging an editor's border respects the plugin's minimum and maximum
dimensions, so shrinking an LSP editor stops before its controls are cut
off. A VST3 editor also decides whether its border can be resized. TDR Nova
disables border resizing; use its own "User Interface Scale" menu to make
it larger. Editors that allow resizing may move in steps if the plugin
requires size increments. The plugin draws its own contents; a Windows
editor running through Wine can still briefly expose an unpainted edge
while it redraws during a drag.

LSP editors use software rendering by default. Their OpenGL renderer can
stop repainting after a continuous drag to a large size, requiring the
editor to be closed and reopened. To test OpenGL again, start the daemon
with `LSP_WS_LIB_GLXSURFACE=1` in its environment. An explicit renderer
setting takes precedence over the default. This setting controls LSP's
editor rendering; it does not change audio processing.

The editor draws on an X11 display, which means XWayland on a Wayland
desktop, and the daemon has to know about it. A user service started
before the desktop published its display has none of its own, so OpenXLR
asks systemd's user manager, where the session puts it, whenever it starts
a plugin. That covers a daemon that came up early and one left running
across a logout.

If an editor still answers "no X display", the session never handed its
display over. Give it to the manager yourself, from a terminal inside the
graphical session, and restart the daemon:

```sh
systemctl --user import-environment DISPLAY XAUTHORITY
systemctl --user restart openxlr-daemon.service
```

The restart interrupts audio briefly.

Editor display failures are handled separately from audio. If the X server goes
away while an editor is open, the editor closes and the plugin keeps
processing; pressing "Plugin UI" again opens a fresh one. If an editor
stops answering, the insert says its controls are frozen and its audio
carries on. A crash of the plugin process interrupts the chain while it
recovers, and so does a plugin that stops passing audio: one that blocks
inside its own processing is noticed after a few seconds and replaced the
same way a crashed one is, since an instance that is neither dead nor
processing would otherwise sit there silent. A plugin that keeps crashing
has its chain switched off after it has failed three times in five
minutes, with the reason on the insert;
changing or bypassing that chain starts it over. When the plugin host said
why it stopped, its last message is shown after that reason and written to
the daemon's log each time the chain stops.

Native LV2, CLAP and VST3 hosts also reject non-finite audio samples (NaN or
infinity). An invalid input never reaches the processor: the host exits at
once with the input named in its message. An invalid output silences the
whole block before it reaches another node. While the plugin is getting
pure digital silence, such as a microphone muted on the device, those
blocks are only silenced: some plugins put out NaN after a minute of
silence and recover on their own once sound returns. With sound coming in,
half a second of invalid output in a row makes the host exit with the
output named in its message. Either way the affected chain is replaced
under the same restart limit. This handles a processor whose internal
state would otherwise stay invalid until a daemon restart. Finite levels
above unity remain unchanged; this check is not a limiter.

A VST3 or CLAP plugin that fails to process a block of audio leaves that
block silent, and the next block is tried. If it keeps failing for half a
second, the daemon replaces the chain under the same restart limit, and
the journal says "the plugin could not process audio". This works even
while the plugin's editor is frozen.

A VST3 scan that says the original Windows plugin is missing usually means
its yabridge wrapper still exists under `~/.vst3/yabridge`, but the source
file was moved or deleted. The wrapper is a bridge, not a copy of the
Windows plugin. Restore the source to its original location, or install
it again and register its current folder in Options. Then sync and rescan.
The scan diagnostics include the missing link target when available.
An ordinary missing plugin path or dangling bundle link is reported
separately. Updating or restoring a linked source causes it to be scanned
again even when the generated Linux wrapper did not change.

<a name="play-sound-clips"></a>
### 3.14 Play sound clips

The soundboard plays short clips into the SFX channel, so the clips go
wherever SFX goes: its fader, its mute in each mix and its sends into
Stream, Chat and the monitor mixes decide who hears them. On a fresh
installation SFX sends into every mix at full level, so the first clip is
heard on the stream, in the call and in your headphones at once. Check
SFX's sends first, and mute it in the mixes that should not hear clips.

The soundboard needs the native plugin host that every package ships. A
build from source without it says so in the Soundboard window and offers
nothing to play.

1. Open the Soundboard window: **Soundboard** in the SUBMIXER header,
   after **Ducking**, the tray menu's **Soundboard**, or **Soundboard**
   under AUDIO in Options. The header button shows while the software
   mixer runs, whatever the skin and whether or not SFX exists, and stays
   in the UI when the mixer is popped out. The tray and Options open the
   window with the mixer off too. The Soundboard window stands on its own:
   it stays open when the UI is minimized or closed to the tray, and
   pressing a pad does not bring the UI forward.
2. Turn on **Soundboard on** if it is off.
3. Press **Add clips** and pick one or more files, or **Add folder** for
   every sound file in a folder (its subfolders are left out), or drag
   files and folders from a file manager onto the Soundboard window.
   Files go onto the page shown; a folder's files go onto a page named
   after the folder (see [pages](#soundboard-pages) below). WAV, FLAC,
   Ogg Vorbis, Opus and MP3 are read, up to 30 seconds and 64 MiB a
   file and 64 clips in all; files past the 64th are left out and
   counted, and **Cancel** stops a long import after the file it is on.
   The clips the player holds share 96 MiB of memory once decoded, about
   four minutes of stereo audio in all; a clip past that budget shows
   why on its pad and does not play.
   OpenXLR copies each file into its own data folder, so moving or
   deleting the original later changes nothing. Pads and Deck keys keep
   playing and stopping clips while files are copied and checked. A file it refuses is named
   under the pads with the reason, such as "The clip is 31.0 s long; the
   limit is 30 s."
4. Press a pad to play its clip. Space or Enter plays the focused pad.
   On a touch screen a pad plays when your finger lifts without having
   moved, so a scroll that starts on a pad plays nothing; a **Hold** pad
   plays as soon as it is touched. A screen reader's press on a Hold pad
   plays the clip and releases it at once. The pad's ring lights while
   the clip plays and the bar along its foot shows how far along it is;
   the header's **Soundboard** button lights too. **Stop all** fades out every clip. A
   Hold clip also stops when the Soundboard window loses focus while its
   pad is held.

A clip with a colour fills its whole pad with it, like a Stream Deck key.
The name, icon and second line turn dark on a light colour and white on a
dark one. A filled pad lightens under the pointer
and darkens while pressed. A pad that cannot play, because its clip is
still loading or cannot be read or the board is off, goes back to the
plain face with its colour as a bar along the top edge.

A pad whose clip cannot be loaded says why in its second line; Edit
shows the whole sentence at the top. Each pad's **⋯** button opens its menu: **Edit**, **Audition in
the monitor mix**, **Stop**, **Move earlier**, **Move later** and **Delete**. Edit holds:

| Field | What it does |
|---|---|
| Name | what the pad and a Deck key show; renaming keeps the clip's id, so keys keep working |
| Gain | the clip's own level before SFX's fader; a change reaches a playing clip at once |
| Press | **Overlap** plays the clip again over any that still play; **Restart** starts it again from the top; **Toggle** plays it and the next press stops it; **Hold** plays it while the pad is held down |
| Start over at the end | loops the clip until it is stopped; turning it off lets the current pass finish |
| Icon, Colour | the pad's icon, and its colour: a swatch fills the field in, any `#RRGGBB` can be typed, and the struck-out swatch removes the colour |
| Page | the page the pad is on; empty puts it on **Main**, and a name no page has yet makes a new page. A clip moved to a page goes last on it |

The fader at the top of the Soundboard window is SFX's own channel volume, the same
fader as on the strip: it is the board's master. Up to eight clips play
at once; a ninth press fades out the oldest.

<a name="audition-a-clip"></a>
To hear a clip before your audience does, choose **Audition in the
monitor mix** in its pad's menu. The clip plays once from the start in
the monitor mixes your monitor outputs play, and nowhere else. It does
not go through SFX, so Stream, Chat and Aux never hear it, whatever SFX's
sends say, and SFX's fader and mutes do not change it. The clip's own
gain applies, and so do the monitor mix's master and mute. The pad's
second line says it is auditioned, and the menu entry becomes **Stop the
audition**. Only one clip is auditioned at a time, and auditioning
another replaces it. Pressing pads meanwhile plays them into SFX as usual, and
the audition goes on. **Stop all**, the pad's **Stop** and deleting the
clip end it too.

An output whose feed sums a monitor mix with Stream or Chat hears the
audition through its monitor mix. An output that plays only Stream or
Chat does not, and while every selected output is like that, the window
says "The monitor outputs play no monitor mix, so an audition would not
be heard." Choose a monitor mix for one of them in its output's feed.
With no monitor output selected, or with listening paused, the window
refuses an audition with a sentence saying which.

Some things follow from the clips sharing SFX:

- Applications routed to SFX share the fader, the mutes and the meter
  with the clips. Route loud applications to System or a channel of
  their own in the layout editor.
- With ducking on, the default setup lowers SFX in your monitor mix while
  the key microphone is loud, and the clips with it; Stream and Chat keep
  them at their level unless you tick those mixes. Untick SFX in the
  Ducking window to keep the clips at their level.
- Renaming or hiding SFX keeps the board playing. Deleting SFX turns the
  board off and the Soundboard window says so. Ticking **Soundboard on**
  again makes a new SFX channel, muted in every mix. Open its sends
  before you expect to hear anything. The clips stay in the library
  meanwhile.
- While the software mixer is off, or waiting for the audio server, the
  Soundboard window keeps its pads and says the mixer is not running;
  nothing plays until it is.
- A clip starts within one block of audio: about 21 ms at PipeWire's
  usual 1024 frames, about 5 ms with Block size set to 256, and about
  43 ms with a forced block of 2048 frames.

<a name="soundboard-pages"></a>
**Pages.** Every clip is on a page, **Main** unless you choose another.
Once a clip is on a named page, the pages stand as tabs over the pads,
**Main** first while it holds a clip and then each page in the order its
first clip comes, and
the pads show the page whose tab is lit. Since a tab stands where its page's
first clip does, moving a clip to another page, or the first clip of a
page in the library, can move the tabs too. A page exists while a clip is on
it: give a clip a page in its Edit window, add a folder, or drop one on
the window. A page name has up to 40 characters and cannot be a dash
alone, which the terminal mixer uses for Main. The **⋯** button beside the tabs renames the page shown, or
moves its clips to **Main**, which ends the page. Renaming a page to the
name of another puts the clips together on it. **Move earlier** and
**Move later** move a pad among the pads of its own page. Pages change
nothing about how a clip plays, and Deck keys and desktop keys find a
clip by its id whatever page it is on.

**Export and import the library.** **Export library** saves every clip
and its settings, pages included, in one zip file you pick. **Import
library** reads one back, on this computer or another. With clips already
on the board it asks whether to **Add** the archive's clips to them or
**Replace** them. Replace deletes the clips here and keeps the ids from
the file, so Deck keys and desktop keys set up for those clips on the
other computer play them here too. It replaces nothing unless every clip
in the file can be imported here, and the line under the pads names any
that cannot. Add keeps the clips here and gives an
imported clip a new id when its id is taken, and the line under the pads
names each one that changed. OpenXLR checks every file in the archive
the way it checks a file from **Add clips**. When adding, a clip that fails
is left out and named, and the others come in. Export library needs a file
name that ends in `.zip`. An archive that is not a soundboard library, holds
anything but its library file and its clips, or is over 512 MiB is
refused whole and nothing changes.

<a name="starter-sounds"></a>
**Starter sounds.** The board comes with eighteen short clips on a page
named **Starter**: applause, a cheer, a laugh, a boo, a rimshot, a
drumroll, a fanfare, a sad trombone, an air horn, a ding, a buzzer,
countdown beeps, crickets, a whoosh, a record scratch, a cash register,
and "You win" and "Game over". OpenXLR adds them once, the first time the
daemon starts with a version that has them, after any clips you already
have. Rename, move, edit or delete them as you would your own clips; a
deleted one stays deleted. **Restore starter sounds**, beside **Import
library**, adds back the ones the board no longer has and says when none
is missing; a starter clip you renamed or moved still counts as there.
All of them are public domain under CC0 1.0. The `CREDITS` file beside
them names each clip's source, its author and how it was trimmed and
levelled; in the distribution packages it is
`/usr/lib/openxlr/daemon/starter-sounds/CREDITS`.

To play clips from the keyboard, tick them in Options, DESKTOP,
**Desktop keys**. Each one gets a global shortcut of its own, and a Hold
clip plays while its key is down on desktops that report the key coming
up. See [soundboard keys](#soundboard-keys).

The library lives in `~/.config/openxlr/soundboard.json` and the copied
files in `~/.local/share/openxlr/soundboard/` (see
[section 6](#files)). Profiles do not carry the library; they recall
SFX's sends and volume like any channel's.

<a name="voice-changer"></a>
### 3.15 Change your voice

The voice changer gives a microphone another voice: a robot, an old radio,
an alien, a vampire, a monster, or your own pitch and formant. It works on
XLR 1, XLR 2 and every capture input on one mono port, each with its own
voice. It runs last in the microphone's processing, after noise removal and
the EQ. Your plugins, your mixes, your calls and Mic setup's loop all get
the changed voice. It needs the native plugin host every package ships.

1. Press **Voice changer** in the SUBMIXER header, right after
   **Soundboard**. The button shows while the software mixer runs and turns
   green while any microphone has a voice on. The Voice changer window
   stands on its own, as the Soundboard window does: it stays open when the
   UI is minimized, and pressing a tile does not bring the UI forward.
2. Pick the microphone at the top right. It starts on XLR 1.
3. Press a voice. Its tile turns green and the microphone takes that voice
   at once. Press **Off** to get your own voice back.

The voices:

- **Custom**: your own pitch and formant, from -12 to 12 semitones each.
  Pitch moves how high or low you sound; formant moves how big the voice
  sounds, so a lower formant with the same pitch sounds like a larger
  person. The two sliders show only while Custom is chosen.
- **Deep** and **High**: lower and darker, or higher and lighter.
- **Robot**: metallic and ringing.
- **Radio**: a narrow band with a little crunch, like an old radio or a
  phone.
- **Alien**: higher, ringing and wobbling.
- **Dracula**: low, in a castle hall.
- **Frankenstein**: very low, with a growl.
- **Mummy**: raspy, in a long tomb.
- **Werewolf**: low and rough.
- **Witch**: high, with a quiver.

Changing the voice, or switching it on or off, crossfades, so it does not
click. Off, the microphone passes through untouched. Pitch and formant
shifting adds a short delay, the same in every mix and call, so the voice
stays in step with itself.

Interfaces with a direct monitor send your own voice to their headphone
jacks in hardware, before any of this. On the MK.2 units, the Wave:3, the
Wave:3 MK.2 and the Wave:1, you hear the changed voice only through a
monitor mix that carries the microphone. Turn the Mic ↔ PC crossfade
towards PC to hear it as your listeners do. On the Wave XLR Pro OpenXLR
turns the [zero-latency path](#volume-direct-monitor) off while a voice is
on, so the jacks play the changed voice, a few milliseconds late.

The voice is saved with the mixer and in profiles, so a profile can recall
a voice; a profile saved before the voice changer leaves it as it is. A
[Deck key](#voice-keys) switches a voice on and off, the terminal mixer's
Inputs tab has the same choice, and `setVoiceChanger` in the
[control API](api.md#voice-changer) drives it from a script.

<a name="stream-deck"></a>
## 4. Stream Deck (OpenDeck)

The plugin has three actions. All are clients of the daemon and show
its live state, so what a key displays is what the UI shows.

**Toggle** (a key) switches one thing: a hardware control (mute,
phantom, low cut, expander, voice tune, ClipGuard, compressor, low
impedance, the Pro's output selectors, the aux level lock, the Line In
level lock, the gain lock), [ducking](#ducking) on or off, the software low cut (cycling Off, 80, 120), the software
ClipGuard, the Aux mix to the USB Aux port, a mix or send mute,
the monitor output (switching the monitor mixes to one specific device),
an output's feed (cycling Monitor A, Monitor B, Monitor A+B and the remaining mixes, lit
when not on the default monitor mix),
the bypass of one insert or of a whole chain, a desktop output's mute, a
5% step up or down in its volume, or its selection as the enforced system
default, the routing of the focused
application, a profile to recall, or the next member of an
[exclusive group](#exclusive-groups). A group key shows the group's name
and the member that is heard, or None; a press unmutes the next member in
the mixes the current one was heard in and mutes the current one. With
every member muted the key shows an alert, because there is nothing to
hand over; unmute a member first.
The key's LED is green for an engaged feature, red for a mute, and grey
when the daemon is offline or the target does not exist on the
connected interface.
Recalling a profile from a key sends any pending dial turns first, so a
delayed turn cannot overwrite the recalled values. The dials then show and
step from the profile's values as soon as its state arrives.
Keys and dials for an interface input, such as its mute, gain, phantom
power and processing toggles, act on the interface whatever feeds the
strips. Their
label names the channel that carries that input: XLR 1 on a Wave XLR Pro's
jack, Mic 1 on a Wave:3's capsule, or, while XLR 1 is
[bound to another microphone](#input-bindings), the capture channel
that carries the interface's microphone, such as Wave XLR Pro input 1. A
key shortens a long name. With no channel carrying the input the
label reads Input 1 or Input 2. A gain dial's meter shows the carrying
channel, and stays empty while there is none. The targets keep their fixed
names in the key's settings, such as XLR 1 mute, because they are the
interface's first and second inputs, and they act on the primary interface
only. A strip bound to a source without unit controls has no hardware mute.
Use a send mute key for it in each mix you want it
out of, or press a dial set to it in all mixes, which mutes every send at
once. With no interface connected the hardware keys go grey, the hardware
dials show OpenXLR and "set up", and a press only flashes an alert. The ducking key, under **Mixer** in the
key's settings, is no hardware key: it works with any device or none,
lights while ducking is on, and a press switches only that, leaving the
key microphone, channels, mixes and presets as they are. A daemon that
reports no ducking leaves it grey. A key's icon can be chosen in its settings, and a
title typed there replaces the built-in label. An icon name the plugin does
not know shows the target's default icon. A key or dial whose target is a
channel or a mix with an icon or colour from the layout editor
([Appearance](#layout)) shows that icon and colour unless the key has its
own icon chosen. An exclusive group key shows those of the member that is
heard.

The property inspector's **Microphone controls** group follows channels
instead. A gain dial stores `chgain:<channel>`; mute, phantom power, low cut
and ClipGuard keys store `chmute:<channel>`, `chphantom:<channel>`,
`chlowcut:<channel>` and `chclipguard:<channel>`. They are labelled with the
channel's current name and work on the primary or an additional driven
interface. Each press or turn resolves the channel's current `unitInput`
and writes that unit with `setWaveControl`, including input 2 where needed.
The gain dial uses that unit's gain range, meters the channel in one lane
and mutes its input on press. Renaming a channel or its unit's capture node
keeps the target. A missing carrier, an unplugged unit or an unsupported
control makes it unavailable. An older daemon without `unitInput` offers no
channel hardware targets; the existing interface targets still work.
The group also lists a gain dial for each capture input that has a
[gain of its own](#capture-gain). It turns that gain one dB a tick within
the source's range with `setCaptureGain`, meters the channel, and on press
mutes or unmutes the channel in every mix, since OpenXLR does not write the
source's mute.

Each microphone with a complete EQ report has a **Microphone EQ** group,
including mono capture microphones. Keys switch the whole EQ or one band
on/off, or cycle one band's filter type. Dials adjust a band's frequency,
gain, Q or type. Turning a frequency dial moves in 24 steps per octave,
rounded to whole hertz; gain moves by the reported step, normally 0.25 dB,
and Q moves one step on its logarithmic grid. A numeric dial keeps showing
its value while its title says **EQ off** or **band off**. Gain reads
**n/a** for a cut or notch, which keeps its stored gain but does not use it.
Pressing an EQ dial switches that band, leaving the whole EQ switch alone.
Use the EQ on/off key to enable the whole EQ. Dial stacks use the same
tap/press cycling as other controls. Where the daemon reports
[noise removal](#noise-removal) for the microphone, the group also has a
**noise removal on/off** key, which switches it with the engine and strength
set in Mic setup; it cannot switch noise removal on without the native
plugin host.

The saved targets use channel ids: `eq:<channel>` for the EQ switch,
`noise:<channel>` for the noise removal switch,
`eqband:<channel>:<band>` for a band switch, and `eqtype:`, `eqfreq:`,
`eqgain:` or `eqq:` followed by the same channel and band for type,
frequency, gain or Q. Bands are numbered from 1. Renaming a microphone
keeps its controls, icon and colour. A removed or unsupported target stays
selected as unavailable in the inspector and sends nothing. Restoring the
microphone restores the target. Changing an action's target or reconnecting
discards its unsent EQ turns; cycling an unchanged stack keeps its last
turn. EQ changes use the existing microphone EQ and its normal mixer and
profile persistence.

<a name="voice-keys"></a>
Where the daemon reports the [voice changer](#voice-changer) for a
microphone, the Toggle action has a **Voice changer** group for it with a
key per voice. The key wears the microphone's icon and colour and lights
while the microphone plays that voice. **Press** in the key's settings
chooses how it works. **Toggle** switches the voice on, or off when it is
the voice playing; **Hold** plays the voice while the key is down and
switches the voice changer off when it comes up. Inside a multi-action a
hold key toggles, since OpenDeck lets the key up straight away there. A key
cannot switch a voice on without the native plugin host. The saved target
is `voice:<channel>:<voice>`, with the voice's name as the API spells it.

Capture chains also appear in the insert key and dial choices, named by
their channel. Saved insert targets keep the channel id through a rename.
Mic setup is not offered on the Deck.

**Dial** (an encoder) changes a level: the monitor output volume, a
gain, the Line In gain, a headphone volume, the aux level, the crossfade, a
mix master, a channel's send into one mix or into all mixes, a channel's
[volume](#channel-volume), XLR 1's, XLR 2's, Aux In's and Line In's
included, a
desktop output's volume
(the system default or a named output, up to 150%), or one control of an
insert, or a microphone EQ band. The touch strip shows a knob, a level meter, the value and a
mute overlay. The title sits beside the icon without overlapping it, and a
title too long for its space scrolls. A dial without a usable target shows
OpenXLR as its title, with "set up" or "offline" as the value, until it is
configured. A headphone dial uses the output selector as mute only when
the device supports output routing. On volume-only jacks, such as the XLR
Dock, it shows the volume and pressing it sends no command.
Pressing other dials mutes (or, for a gain, mutes the input;
for the crossfade, recentres; for a channel volume, the Line In gain or a
capture input's own gain, none of which has a mute OpenXLR writes, mutes
or unmutes the channel in every mix,
as a dial on its sends in all mixes does; for an
insert control, toggles that insert's bypass; for a named output, toggles
that output's mute; for the monitor output volume, pauses or resumes
monitoring, see [output keys](#output-keys)). Pressing an Aux In level
dial sends nothing. A dial can hold several targets, cycled
by tap or press as chosen in its settings. A turn leads: the strip
follows the dial at once and each tick steps from the value shown, while
the daemon hears the first tick immediately and a fast turn as a short
series of changes. The daemon's own value takes over once the turn has
settled.

**Clip** (a key) plays one clip of the [soundboard](#play-sound-clips)
into the SFX channel, or stops every clip. Its settings list the clips by
name and store the one picked by its id, so renaming the clip keeps the
key. A press plays the clip in its own press mode, set in the library:
overlap starts another voice, restart plays it again from the top, toggle
stops it when it is playing, and hold plays it while the key is down and
ends it when the key comes up. **When pressed** in the key's settings gives
the key a mode of its own instead. Every press plays, however fast they
come. With two hold keys on the same clip, the clip ends when the last of
them comes up. A key held down past the end of its clip holds nothing once
the clip has ended, so a later hold key on the same clip still ends its own
play when it comes up. Inside a multi-action a hold key plays its clip as overlap,
because OpenDeck lets the key up a tenth of a second after pressing it. If
the Deck goes away while a hold key is down and no key up arrives, a looping
clip keeps playing until **Stop every clip** ends it.

The key shows the clip's name, icon and colour, or SFX's icon and colour
from the layout editor where the clip has none. Its light is green while the
clip plays, and a bar along the bottom shows how far it is. With a clip
played over itself, the bar follows the newest voice. A title typed in the
key's settings covers that part of the key, so a titled key shows only the
light. **Stop every clip** fades every clip out and is lit while any clip
plays. A key reads in grey why it cannot play: off when the soundboard is
off, no SFX when the SFX channel was deleted, unavailable without the native
plugin host, stopped while the player starts or after it failed, loading
while the clip is being loaded, error when the clip could not be loaded,
gone when the clip was deleted, and update with an OpenXLR older than the
plugin. A press on a grey key only flashes an alert, as does a press
OpenXLR refuses. The key's settings show OpenXLR's own sentence for why. A
deleted clip stays selected there as missing, and while OpenXLR is offline
as unknown. The clips play wherever SFX's sends and fader put them, together
with any application routed to SFX.

Installing: the plugin zip from the release through OpenDeck's
install-from-file, or the folder the package ships in
`/usr/share/openxlr/` copied into `~/.config/opendeck/plugins/`
(copied, not linked; OpenDeck does not serve assets through a symlink).
Restart OpenDeck after installing or updating the plugin.

<a name="troubleshooting"></a>
## 5. Troubleshooting

<a name="no-device"></a>
### 5.1 "No device" in the header

Without an Elgato device this is expected: the mixer runs without one. Bind
your microphone to XLR 1 ([Without Elgato hardware](#without-elgato)). The
rest of this section is for an Elgato device the daemon does not see.

- The interface must be replugged once after installing so the udev
  rule (`/usr/lib/udev/rules.d/70-openxlr.rules`) applies to it.
- `lsusb` should list an `0fd9:` device. If it does but the header
  still says no device, look at the daemon's log:
  `journalctl --user -u openxlr-daemon -n 50`. "present but could not
  be opened" can mean missing USB permission or a busy interface; check
  the udev rule and whether another hardware-control program is running.
- If the desktop audio server is not ready at login, OpenXLR waits up to
  ten seconds each for its initial default-output and default-input
  queries. A failed query leaves that default unknown and the daemon still
  starts; select your preferred desktop default afterwards.
- With more than one supported interface attached, the header shows a
  picker; the mixer's input channels follow the chosen one. Switching
  releases the interface you leave before the chosen one is opened, and
  on an interface whose last settings OpenXLR remembers it saves a
  change made just before the switch instead of losing it. The daemon
  remembers the pick and drives the same unit after a restart, even when
  `OPENXLR_DEVICE` names another model. Two units of
  one model are listed with their USB port. While the chosen unit is
  unplugged during a session the others are left alone. After a restart
  without it, OpenXLR waits ten seconds for it, then drives another
  attached unit (the model `OPENXLR_DEVICE` names, or the first one)
  until the chosen unit is plugged in again, and switches back to it
  ([Multiple Wave interfaces](wave-interfaces.md)).

<a name="dock-silent"></a>
### 5.2 Microphone silent on the XLR Dock, the original Wave XLR or a Wave:3

The kernel starves the dock's capture when playback to it starts before
capture, and the microphone records silence. The original Wave XLR (MK.1)
has the same ordering bug, and so does the Wave:3 on some USB ports and
controllers. On a Wave:3 it shows after a boot as a silent microphone
while everything else looks normal. Joining a call before starting music
brings the sound back for a while, and a replug brings it back where
restarting the daemon does not. The Wave:3 MK.2 has its own form of it.
While its headphones play, its microphone can give nothing, or a steady
buzz and no voice. The packages install WirePlumber rules that keep these
capture sources always active (`50-xlr-dock-capture-hold.conf`,
`52-openxlr-mk1-capture-hold.conf` and
`53-openxlr-wave3-capture-hold.conf`, which covers both Wave:3 models),
and one that opens the Wave:3 MK.2's headphones in the unit's float
format (`55-openxlr-wave3-mk2-playback.conf`). WirePlumber reads them
when it starts, so after installing or upgrading a package run
`systemctl --user restart wireplumber`, then restart the daemon. On a
source install copy them from `packaging/` into
`~/.config/wireplumber/wireplumber.conf.d/` and restart WirePlumber.
OpenXLR starts the interface's capture before it routes any mix into the
interface, the monitor output on its own headphones included. A linked
capture is not enough: OpenXLR waits until the kernel reports the capture
running in `/proc/asound/<card>/stream0`, or until PipeWire runs it where
that file cannot be read. It waits up to five seconds, then routes anyway
with a journal line. The same goes for another Elgato interface that XLR 1
or XLR 2 takes its microphone from, and again whenever the interface
comes back after a replug or a reset. Programs that play straight to the
interface can still start playback first, which is why the rules matter.

On the Wave:3 MK.2 OpenXLR also watches the microphone while a mix plays
into its headphones, and brings it back when it stalls. The sign it acts
on is the kernel keeping the capture stopped for 3 seconds while PipeWire
runs it, which a quiet room or a muted microphone never shows. OpenXLR
then takes the mixes off the headphones, resets the microphone on USB,
and routes the mixes back once the microphone records again, or after 30
seconds. You hear the headphones drop for about 12 seconds. It does this
at most once every 2 minutes and 3 times in 30 minutes; after that the
journal asks you to replug the microphone.

**Recover microphone** in Options, under INTERFACE, is for a stall the
watch does not catch, such as a steady buzz in place of your voice. It
first removes the microphone's links into its strips and makes them
again, which leaves the headphones playing. If the kernel still has the
capture stopped 2 seconds later, it goes on to the USB reset above. Each
step is in the journal (`journalctl --user -u openxlr-daemon`).

A second cause, when the microphone is silent only after a reboot: the
dock forgets its gain at every power cycle and comes back at the gain its
firmware restores, which can differ from the gain used by your insert chain.
OpenXLR gives the gain back when the dock connects, even when the gain
lock is on. A gate or expander tuned at the gain you meant to have stays
shut at a lower one and passes nothing at all, which is what makes the
microphone sound dead rather than quiet.

The dock's gain, mute and headphone volume are controls of its ALSA
card. When the card lacks one of them, OpenXLR drives that control
through the dock's own config block instead and says so in the daemon's
log when the dock connects and in Options, under INTERFACE. If the USB
handle is not open either, because the udev rule has not applied yet
([section 5.1](#no-device)), the daemon reports that control unavailable
until the dock connects again.
The XLR Dock MK.2 answers its settings on one of two USB control banks,
and which one follows its firmware: 1.0.0 answers on `0x0103`, 1.0.15 on
`0x0203`. OpenXLR probes the first and falls back to the second every
time the dock connects, and changes no microphone setting doing it. A
dock on 1.0.15 therefore carries a connection note naming the bank it
selected, in Options under INTERFACE; a dock that answers on neither
bank gets a note too and still connects. The same note says when a
1.0.15 settings block did not decode and the microphone EQ stays in
software. With a dock that answers on neither bank, press **Collect
diagnostics** in Options and open an issue with the archive.

<a name="daemon-not-starting"></a>
### 5.3 Daemon does not start after an upgrade, or after a reboot

- `systemctl --user status openxlr-daemon` shows the state. "203/EXEC"
  in a restart loop means a stale unit in
  `~/.config/systemd/user/openxlr-daemon.service` written by a version
  before 0.1.9. Opening the UI repairs it when **Start audio in the
  background** (Options, AT LOGIN) is on and OpenXLR can prove it wrote
  the unit; otherwise run
  `openxlr installation repair-startup`, or remove the file,
  `systemctl --user daemon-reload`, then
  `systemctl --user enable --now openxlr-daemon`.
- "another OpenXLR daemon is already running (it holds .../openxlr/daemon.lock)": a second
  daemon was started by hand while the service runs. It stops at once;
  stop the service first if you meant to run the daemon by hand.
- "control socket unavailable": OpenXLR cannot bind its private control
  socket. The log names the reason. Check that the runtime directory is
  writable by your user and that `openxlr/control.sock` is not a regular
  file or symbolic link. OpenXLR refuses to delete those. An unused socket
  left by an unclean exit is repaired automatically before audio starts.
- "The mixer is waiting for the audio server": the daemon started, but
  pipewire-pulse did not answer when the daemon built the mixer. A USB
  device that stops responding can hold pipewire-pulse up for minutes.
  The daemon tries again every 30 seconds at most and builds the mixer as
  soon as the server answers, so you do not have to restart anything. A
  profile recalled on connect meanwhile gets its mixer settings once the
  mixer is up. If the warning stays, check the audio server with
  `pactl info` and `journalctl --user -u pipewire-pulse -b`, and look for
  USB errors with `journalctl -k -b | grep -i usb`.

<a name="missing-plugins"></a>
### 5.4 ClipGuard greyed out, empty plugin picker

- The software ClipGuard needs the SWH LADSPA plugins (`swh-plugins`).
  Without them the control is disabled and its tooltip says so; the
  rest keeps working.
- For LV2, check lilv and the installed plugins in `/usr/lib/lv2`, `~/.lv2`
  or `LV2_PATH`. CLAP and VST3 also require the native helper; Options
  reports whether it is installed. A source build without the native
  flag removes it. Check the format filter and press Rescan after an
  external installation. A Windows bundle also needs Wine and a working
  bridge; see [Windows plugins](#windows-plugins).

A yabridge entry can remain after its original Windows plugin was moved or
removed. OpenXLR checks the wrapper's Windows module link before loading it,
including on an explicit rescan, so a dangling link is reported as
`windows-module-missing` without opening a yabridge error dialog. The native
host also checks the link when starting a saved insert. Restore or reinstall
the original Windows plugin at the reported path, then sync and rescan.
Copying the generated Linux wrapper cannot restore the Windows plugin.

<a name="wrong-device"></a>
### 5.5 Sound comes out of the wrong device

The session manager switched the system default when a new device
appeared. Set the defaults in Options ([section 3.7](#default-devices)), or pick the device
you want in your desktop's sound settings once; the daemon defends the
defaults it saw at start only for about half a minute after the mixer is
built.

<a name="control-not-applied"></a>
### 5.6 A control changes in the UI but not on the device

The daemon writes to the interface and reads the state back; if the
device ignores the write, the control snaps back. On the Wave XLR Pro
the mute button shows a countdown after every 48V change: the firmware
holds that input muted for about 13 seconds and unmutes it itself. On
other devices this would be new information: collect diagnostics
([section 5.10](#reporting)) and open an issue.

<a name="daemon-hang"></a>
### 5.7 The daemon froze, or a control hung the UI

On a native installation, the header's **Restart daemon** button restarts
the systemd user service. Audio is interrupted during the restart. The UI
stays responsive, and the button is disabled until the service commands
finish. An explicit restart first clears the daemon service's failed state
and start counter, so the button also works after repeated starts produced
`start-limit-hit` in the journal. Automatic restarts after crashes still
obey the service's start limit. The waiting message clears when the daemon
reconnects, including a connection restored before the service command
finishes. If the explicit restart fails, check
`journalctl --user -u openxlr-daemon`. A daemon started by hand must be
restarted by hand.

Changing **Enable software mixer** in Options uses the same background
restart. Its switch is disabled while a restart is in progress, so the
window stays responsive and concurrent changes cannot race the restart.

An older user unit can fail before starting the daemon with
`218/CAPABILITIES` and `Failed to drop capabilities`. Update the package
and run `systemctl --user daemon-reload` followed by
`systemctl --user restart openxlr-daemon`. Source installs can regenerate
their owned unit by turning **Start audio in the background** off and on
again in Options, AT LOGIN.
That includes a unit you copied by hand from the reference file v0.1.37
to v0.1.70 shipped, as long as you changed only its `ExecStart` line.
The current unit keeps its other restrictions but no longer carries
`CapabilityBoundingSet=`, `ProtectClock`, `ProtectKernelLogs` or
`ProtectKernelModules`, the four lines that asked the user manager to drop
capabilities. Review any personal override
before removing it; OpenXLR never deletes an override it did not create.

A USB transfer that never returns fails after a few
seconds instead of stalling the daemon; the device is dropped and
reconnected after 10 seconds, and the fault is recorded. The USB
library runs in a small helper process of its own (the daemon binary
started with `--usb-helper`), which is killed and started again on such
a hang, so nothing stays stuck inside the daemon. After three hangs in
one run the daemon stops driving that interface and says so under the
UI's header, while the submixer and any other interface keep
working. Unplug the interface and plug it back in, or restart the
daemon, to try again. A helper whose device could not be opened at all
(the udev rule not applied yet, see [section 5.1](#no-device)) is
killed straight away and the daemon tries again two seconds later. A
device that opens but answers its reads with errors is dropped and
reopened after two seconds, and the wait doubles on each failure that
follows, up to 32 seconds, so the log shows the retries at growing
intervals rather than a flood. The journal says once that the device does
not answer, and once more when it answers again; until then the warning
under the header names the device and the error. If the warning stays,
unplug the device and plug it back in.
Collect diagnostics afterwards ([section 5.10](#reporting)): the archive contains the
exact transfer, and that is what makes the report actionable.

The USB deadline covers both sending a request and receiving its reply, so
a helper that stops reading commands cannot block a large transfer indefinitely.
OpenXLR refuses a malformed reply before reading any setting from it and
discards the helper; the next connection starts a fresh process instead of
reusing a broken protocol stream.

A stop or restart finishes inside the unit's 45 second stop timeout even
when the audio server or an interface has stopped answering. Once the
daemon begins to stop it opens no interface again, and it gives the audio
server 15 seconds to remove the mixer's sinks. Past that it ends the
`pactl` call still running, skips the rest, and writes one line to the
journal ("stop: the audio server took longer than 15 s") with the number
of OpenXLR modules it left loaded. It saves your settings before any of
this. The next start removes the leftover modules before it builds the
mixer, so you have nothing to clean up. Until then the leftover OpenXLR
devices can still show in the system sound settings.

<a name="open-files"></a>
### 5.8 Channels or mixes vanish after adding one

pipewire-pulse, PipeWire's PulseAudio server, inherits systemd's default
limit of 1024 open files. OpenXLR's send faders are streams inside that
server, so a layout with a few channels or mixes beyond the default
reaches the limit, and past it the server drops nodes at random: sinks
disappear, apps fall back to the default output, and the UI shows
"Sink not found" errors from pactl.

OpenXLR refuses to add a channel or mix when the server has no room left
and says so in the editor, which also shows a note once the server is at
three quarters of its limit. The fix is a systemd drop-in that raises the
limit to 65536:

1. The deb and rpm packages install it as
   `/usr/lib/systemd/user/pipewire-pulse.service.d/openxlr.conf`; the NixOS
   module sets the same service limit declaratively. On a
   source checkout, or any install without it, create the file yourself:

   ```sh
   mkdir -p ~/.config/systemd/user/pipewire-pulse.service.d
   printf '[Service]\nLimitNOFILE=65536\n' > ~/.config/systemd/user/pipewire-pulse.service.d/openxlr.conf
   ```

2. Apply it, now or at your next login:

   ```sh
   systemctl --user daemon-reload
   systemctl --user restart pipewire-pulse
   ```

   Restarting pipewire-pulse reconnects every PulseAudio client for a
   moment and takes OpenXLR's nodes with it; the daemon notices within two
   seconds and restarts itself to rebuild the graph from the saved layout.

3. Check:

   ```sh
   systemctl --user show pipewire-pulse -p LimitNOFILESoft
   ```

   It should say 65536. If it still says 1024 the file is not where systemd
   looks; `systemctl --user cat pipewire-pulse` lists every file it read.

<a name="quiet-mixes"></a>
### 5.9 The mixes are quieter than the microphone

If OBS or a recorder shows the raw Wave XLR device peaking near -6 dB
while the OpenXLR Stream and Chat microphones sit some 15 dB lower, with
every send and master at 100, one of OpenXLR's own sinks has been turned
down. The channel sinks are playback devices, and a desktop applet or
the session manager restoring a remembered level can set one to half
volume. Only the Monitor A and Monitor B sinks carry a master of their
own; on every other OpenXLR sink the volume is not a control, so turning
it down only cuts audio. The daemon puts those other sinks back to full
volume on its sweep and logs when it had to.

The internal effect stages, such as OpenXLR Mic Filter and a mix's
inserts, show up in desktop volume panels as applications playing and
recording. Their volume is not a control either. The sweep puts them back
to full volume and unmuted in the same way.

Each send is an internal stream of its channel, and the session manager
can set a remembered level on a new one too. That can be full level, or
the level and mute of another send on the same channel. WirePlumber 0.4,
the version in Ubuntu 24.04, does this to every new stream, sometimes
after OpenXLR has already set it. The sweep puts each send back on its
fader within a second and logs which sends it moved.

<a name="reporting"></a>
### 5.10 Reporting a problem

Ask on the OpenXLR Discord server (<https://discord.gg/4bswtnGPW4>,
one post per problem in its support forum), on Reddit at
<https://www.reddit.com/r/OpenXLR/>, or open a GitHub issue; whichever
you pick, attach the diagnostics archive described below.

Options, SUPPORT, Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the daemon's state and
capabilities, a dump of the interface's vendor blocks, the PipeWire
graph and device listings, the last two hours of the daemon journal (under
Flatpak, `daemon.log` and its rotated copy), `mixer.json`, `ui.json` and
version information. Native installations also include `audio-journal.txt`
with the last two hours of the PipeWire, pipewire-pulse and WirePlumber user
service journals. Audio-server errors can explain distortion or disappearing
nodes even when the daemon's own journal has no error. These entries use the
same redaction as the daemon journal; unavailable journals leave a command
failure note. The Flatpak sandbox keeps collecting its own daemon logs and
does not read host journals. Warnings and command failure notes for the graph dump
are kept in `pw-dump-errors.txt`, when present, instead of being appended
to `pw-dump.json`. Plugin evidence includes the
catalogue, effective bridge setup and latest native scan results. The home path, host name
and the serial numbers of attached USB devices are redacted, in the
text files and inside the hex dump of the vendor blocks (the XLR Dock
stores its serial in one). Your login name is redacted where it names
your account: in `pactl info`, in the `user@host` part of each client in
`wpctl status`, in PipeWire's user and core names in the graph dump, and
in paths under `/home`, `/media`, `/run/media` and `/mnt`. The titles of
browser tabs listed by the [Chromium companion](browser-routing.md) are
redacted, and so is the website of each tab left at the browser default.
A tab routed to a channel keeps its website, and `mixer.json` keeps the
remembered website defaults. Files inside
the archive carry no owner name or id. Review the archive anyway before
attaching it to a public issue. Nothing is uploaded automatically. An archive is
created as a new private file; an existing file or symbolic link at its
path is refused without overwriting its contents. A collection that
fails part way removes its incomplete archive.

`meta.txt` identifies the UI that collected it by its full build version and
assembly module id. `daemon-state.json` separately identifies the running
daemon with `daemonBuildVersion` and `daemonBuildId` when it supports those
fields. A build version includes its source commit when one was stamped
at build time. The displayed release number can stay the same across
source rebuilds, so it alone cannot confirm that a fix is running. The
UI and the daemon are separate assemblies; their module ids are not
expected to match. `openxlr doctor` compares the daemon with a receipt-backed
installation instead.
The metadata also records when the state was collected and received, in UTC,
and whether the client was connected then. A disconnected client retains
its last state for investigation; that is historical evidence. A new
connection discards the previous connection's state until its first update
arrives. The graph and vendor blocks are collected separately and can
describe a later moment.

Configuration copies are limited to 4 MiB per file, the preference reader's
limit. Symbolic links, non-regular files and files that cannot be read are
replaced with an omission notice. An unavailable configuration does not stop
the rest of the collection. The Flatpak daemon logs keep their separate size
limit and use the same file checks.

Saved plugin scan logs also have credential-shaped values such as passwords
and API tokens masked, including quoted fields, escaped values and long or
truncated credentials. If this masking exceeds its time limit, the archive
contains an omission notice in place of that log's text, never its
unredacted contents. The original log on disk is kept.

For audio glitches, collect while the problem is present and before restarting
the daemon, if possible. Collection reads the current state without changing
audio, recording samples or starting plugins. The extra reports are:

- `usb-topology.txt`: `lsusb -t`, showing USB port and hub relationships,
  drivers and connection speeds. Match bus and device numbers with
  `lsusb.txt` to locate the interface; the tree's indentation shows which
  port or hub it is attached to.
- `pw-top.txt`: three batches of node statistics, including rate, quantum and
  error counters.
- `sink-inputs.json` and `source-outputs.json`: current playback and recording
  streams, their formats, volume, mute state and buffering as Pulse reports
  them. The archive masks process identity fields and keeps numeric audio
  values as numbers.

Compare the same OpenXLR release and plugin configuration across systems to
separate application changes from differences in the audio stack.

Each helper has a 15-second deadline, an 8 MiB stdout limit and a 64 KiB
stderr limit, and runs in the C locale. A helper that fails, times out or
exits with an error leaves a note beside its report, and collection goes on
with the rest. When `pw-dump` appends update arrays because the graph
changed during collection, the archive folds them into one array with the
daemon's own rules, so `pw-dump.json` stays one JSON document.

For a Windows plugin missing from the picker, collect diagnostics after the
scan finishes and name the plugin and intended insert slot in the report.
The archive adds:

- `plugins.json`: the catalogue, including format, identifier, channel width,
  parameters, host support and bundle paths.
- `plugin-setup.json`: Wine/yabridge versions, selected bridge provider,
  registered Windows folders and install destinations.
- `plugin-discovery.json`: the daemon's search paths and Wine prefix,
  `yabridgectl status`, and the latest completed VST3/CLAP scan results,
  including cache hits, duplicate ids, missing loaders, failures and timeouts.
- `plugin-scan-logs/`: the scanner's own output for scans that failed, one
  file per bundle, named by the `logId` of the scan entry it belongs to. See
  [what a failed scan keeps](#scan-logs) for the sizes and how long they are
  held. `index.txt` lists what was collected, what was skipped and why, and
  any entry whose file was already gone. Nothing here is a plugin binary; it
  is what the scanner printed.

Collection does not sync folders, force a rescan or change live inserts, and
it starts no plugin, scanner, bridge or Wine process: saved scanner output is
copied from disk as it is.
Scan evidence is bounded and marks omitted entries. It is collected from
scans performed by the updated daemon; restart an older daemon after updating
when convenient, then let discovery finish. An older, disconnected or busy
daemon produces an explicit unavailable entry instead of stopping the archive.
Plugin binaries, presets and Wine registry files are not copied.
Review plugin names, paths and scanner output before sharing the archive.

<a name="files"></a>
## 6. Files and services

`openxlr doctor` checks installation receipts, reports missing or changed
files, and compares the installed daemon with the one running. It prints
repair commands and does not change the installation. Package inventories
and private copies retained across upgrades are separate from your mixer
settings. See
[Installation receipts and doctor](installation.md) for removal and repair.

| Path | What it is |
|---|---|
| `~/.config/openxlr/installation.json` | startup ownership and replaced-file metadata |
| `~/.config/openxlr/installation-backups/<sha256>` | private originals for startup restoration |
| `~/.config/openxlr/installation-history/<sha256>.json` | retained package inventories for doctor |
| `~/.config/openxlr/installation.lock` | serializes installation inventory writes |
| `~/.config/openxlr/mixer.json` | every mixer decision, the layout included (`userChannels`, `mixes`, see [mixer-layout.md](mixer-layout.md)), written by the daemon |
| `~/.config/openxlr/profiles/<vid-pid>/<name>.json` | saved profiles, one file each |
| `~/.config/openxlr/profiles/<vid-pid>/recall-on-connect` | the profile recalled when that interface connects, when one is chosen |
| `$XDG_RUNTIME_DIR/openxlr/control.sock` (or `~/.config/openxlr/control.sock` without a runtime directory) | private control API socket, authenticated by Linux peer credentials ([api.md](api.md)); the same commands over HTTP at `/api/v1` ([http-api.md](http-api.md)) |
| `$XDG_RUNTIME_DIR/openxlr/daemon.lock` (or under `~/.config/openxlr` without a runtime directory) | held by the running daemon; a second daemon started for the same user stops at once |
| `~/.config/openxlr/devices/<vid-pid>/last-state.json` | the settings restored on connect when `retainsSettings` is false |
| `~/.config/openxlr/devices/<vid-pid>/defaults.json` | the firmware defaults of such an interface, recorded after a power cycle, written back by "Reset device to defaults" (the Pro has no such file: its reset writes OpenXLR's baseline) |
| `~/.config/openxlr/daemon.json` | the submixer on/off preference and the added plugin folders |
| `~/.config/openxlr/active-device.json` | the interface picked last in the header, driven again after a restart |
| `~/.config/openxlr/gainlock.json` | which devices have the gain lock set |
| `~/.config/openxlr/soundboard.json` | the soundboard's clip library and whether it is on, written by the daemon ([api.md](api.md#soundboard)) |
| `~/.local/share/openxlr/soundboard/` | the soundboard's clips, copied in when imported |
| `~/.config/openxlr/wave-interfaces.json` | the units driven as additional interfaces, by instance id ([Multiple Wave interfaces](wave-interfaces.md)) |
| `~/.config/openxlr/devices/<vid-pid>@<hash>/last-state.json` | the last settings of an additional interface that keeps none on board |
| `~/.config/openxlr/native-editors.json` | your native editor overrides ([section 3.5](#native-editor-compatibility)) |
| `~/.config/openxlr/desktop-keys.json` | desktop shortcut bindings, written by the UI ([Desktop keys](#desktop-keys)) |
| `~/.config/openxlr/bridge/yabridgectl/config.toml` | companion bridge folder registry, separate from the system bridge |
| `~/.local/share/openxlr/yabridge/{vst3,clap,vst2}` | companion-generated wrappers; OpenXLR loads VST3 and CLAP only |
| `~/.config/openxlr/ui.json` | UI preferences, the chosen skin, Material's mode and the language included ([section 3.10](#skins)) |
| `~/.config/openxlr/effect-chain-presets.json` | the UI's saved effect presets ([effect-presets.md](effect-presets.md)) |
| `~/.config/openxlr/deck-palette.json` | the skin colours the UI hands to the OpenDeck plugin |
| `~/.config/autostart/openxlr.desktop` | the UI's autostart entry, written by Start the OpenXLR app ([section 3.9](#autostart)) |
| `~/.cache/openxlr/plugin-scans/`, `~/.cache/openxlr/plugin-scan-logs/` | the plugin catalogue and the latest scan logs |
| `$XDG_STATE_HOME/openxlr/daemon.log` | the daemon's log under Flatpak, with one rotated copy ([Flatpak](flatpak.md)) |
| `~/.local/share/openxlr/skins/<id>/skin.json` | a skin you installed; system skins come from `$XDG_DATA_DIRS` ([skins.md](skins.md)) |
| `openxlr-daemon.service` (systemd user unit) | the daemon; `journalctl --user -u openxlr-daemon` for its log |
| `/usr/lib/systemd/user/pipewire-pulse.service.d/openxlr.conf` | installed by the packages: raises pipewire-pulse's open-file limit ([section 5.8](#open-files)) |
| `/usr/share/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf`, `52-openxlr-mk1-capture-hold.conf`, `53-openxlr-wave3-capture-hold.conf` | installed by the packages: keep the capture of the XLR Dock, the original Wave XLR, the Wave:3 and the Wave:3 MK.2 running ([section 5.2](#dock-silent)) |
| `/usr/share/wireplumber/wireplumber.conf.d/55-openxlr-wave3-mk2-playback.conf` | installed by the packages: opens the Wave:3 MK.2's headphones in the unit's float format ([section 5.2](#dock-silent)) |
| `/usr/share/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf` | installed by the packages: names the Wave XLR Pro's raw multichannel nodes for what they are, so volume applets do not list them like OpenXLR's mixes |
| `/usr/share/wireplumber/main.lua.d/54-openxlr-owned-state.lua` | installed by the `.deb` where WirePlumber is 0.4: keeps desktop state off OpenXLR's own nodes ([section 5.9](#quiet-mixes)) |
| `openxlr-tui` | the mixer in a terminal, the same daemon and the same skins ([The terminal mixer](#terminal)) |

The daemon makes a final attempt to save pending mixer settings when it
shuts down, including when startup fails after the mixer changed. If that
write fails, the last file on disk remains in use. Later cleanup cannot
schedule another save from a mixer that is already being dismantled.

Saved mixer and hardware data are checked before restoration. A null entry
or a number that is not finite (`1e999`) counts as a bad entry. What happens
next depends on the file:

- `mixer.json` with a bad entry: the entry is dropped, the rest of the file
  is used, and the daemon log names the field. The next save writes the file
  without the entry.
- `mixer.json` that cannot be parsed: the daemon logs the path and the
  reason, keeps a private copy as `mixer.json.corrupt`, and starts with default
  settings. The copy is published atomically; an existing backup link is
  replaced without writing through it. The next save replaces the original.
- a hardware snapshot (`last-state.json`, `defaults.json`) with a bad entry:
  treated as unreadable, nothing from it is applied.
- a profile with a bad entry: refused before any of its hardware or mixer
  settings are applied. `loadProfile` returns the profile name and the
  field; a profile recalled on connect is skipped and the last settings are
  restored instead.

Reading a rejected file never rewrites it. Missing optional fields in older
files keep their legacy meaning, and an unavailable plugin can still be
remembered for later use.

Profiles are limited to 8 MiB when saved or loaded. The `recall-on-connect`
marker is limited to 1 KiB and must name a profile, never a path. Pipes and
other special files are refused before reading either file. A refused
automatic recall falls back to the last hardware settings; the journal
reports the reason, and a marker that cannot be read also appears in the
UI's profile warning. Fix or remove that file to choose a profile again.

The preference files, `ui.json` and `daemon.json`, are limited to 4 MiB when
read or saved by the UI, daemon or terminal mixer. Pipes and other
special files are refused before reading, and a save that would exceed the
limit leaves the existing file untouched. When the UI saves one, it keeps
the keys it does not know, such as a setting written by a newer OpenXLR. A
list written as `null` reads as an empty list. The UI leaves a file in
place when it cannot read or parse it, or when the file gives a key twice.
It then runs with default preferences and saves nothing over the file until
you fix or remove it. Changes made in the meantime are not saved, though one
that takes effect at once, such as the skin, still applies for that run. The line beside **Close** at the bottom of
Options names the file and the reason. It appears when Options opens and after
a change that could not be saved, and the next successful save clears it.

The daemon also rejects duplicate keys in `daemon.json`. The plugin manager
reports that file as unreadable and leaves it for you to repair instead of
choosing one of the conflicting values and saving over it.

Configuration paths honor `XDG_CONFIG_HOME`; the private wrapper root honors
`XDG_DATA_HOME`. Without `XDG_RUNTIME_DIR`, runtime files use the private
OpenXLR configuration directory.

OpenXLR writes configuration and profile files completely or not at all, so
an interrupted write leaves the previous file in place.

Uninstalling a package leaves `~/.config/openxlr` in place; remove it
by hand if you want a clean slate.

<a name="capture-inputs"></a>
## Additional capture inputs

Open **Edit layout**, then **Add capture input**. Enter a channel name,
choose an available microphone, capture card or another Wave interface, and
select one mono port or a stereo pair. The picker names ports when PipeWire
reports their positions, such as FL and FR. A mono source starts on port 1;
one port gives the input a mono chain and Mic setup. A stereo pair gives it
a stereo chain.
The new channel starts muted in every mix. Open only the sends you need.

Capture channels can be renamed, reordered and deleted like application
channels. Their layout row shows the connection state and pair; hover over
it to see the exact source name. An offline input stays silent and reconnects
with its saved faders when the same source returns. Profile recall changes its sends and any
insert chain it names, while its source binding stays with the layout. Application routing never targets capture inputs.

Click the source chip in classic INPUTS or the input rack, or the source line
under its name in the matrix, to change its source or ports. The dialog keeps
the channel's name, sends, volume, inserts and group. After a change its
monitor sends start muted and a Mic setup session ends. Unmute the monitor sends you
want to hear. To switch between mono and stereo, remove the chain's inserts
first, including bypassed inserts. A failed save leaves the old binding,
mutes and Mic setup take in place. Rebinding can close a plugin editor as
the input's chain restarts. An offline source stays selected and silent until
it returns. Capture source editing is available in the UI and API; the
terminal mixer and Stream Deck do not offer this picker.

<a name="capture-gain"></a>
A capture input on a source OpenXLR does not drive has a **gain**, which
is the source's own input volume. Webcams and most USB microphones set it
on the device, and the gain then reads in dB the way the device's capture
control does in alsamixer. Its range is the device's own: a Logitech BRIO
goes from +18 to +54 dB. A source without a hardware control has a
software level instead, from -60 to +10 dB. At 0 dB the sound passes as it
arrives.
If the hardware-range lookup fails, OpenXLR uses a range extending 60 dB
below the maximum and tries again after ten seconds, even if the source
volume has not changed. After three more failures it keeps that range
until the daemon restarts. Without `amixer` (the `alsa-utils` package)
there is no lookup to retry, so the range stays at that fallback, as it
does when the card has no matching control. Successful lookups are cached.
The gain sits over the channel's volume in an input rack, as a fader over
the volume in a mix matrix row, and as a Gain row above Volume in the
classic INPUTS block. Its readout shows the volume while you move the
volume fader, as Aux In's does ([gain readout](#gain-readout)). The
terminal mixer has it on the Inputs tab, and a Stream Deck dial can turn
it. Two capture inputs on one source share its gain.

Other programs can change the same volume: a voice chat with automatic
gain control, a browser, or a desktop volume panel. OpenXLR shows the new
value within a second and does not set it back. A capture input on a Wave
interface or an XLR Dock has no gain of this kind; it shows the unit's own
gain while OpenXLR drives the unit. OpenXLR never changes the volume of
those units' sources, the Wave XLR Pro's included.

A capture channel on the primary interface's own source, at the first port
or pair for its first microphone or the third port or second pair for its
second, carries that microphone while XLR 1 or XLR 2 is bound to another
source ([Use another microphone on XLR 1 or XLR 2](#input-bindings)).
The interface's gain, mute and switches are then drawn on it in an input
rack and a mix matrix, and in INPUTS under a heading that names it. Its own
volume stays beside them. When the XLR strip takes the interface again, the
strip has them back and the capture channel keeps only its volume.

Several Wave interfaces can supply capture audio at once. The header picker
chooses the primary interface, whose controls the UI shows and whose
microphone feeds the XLR strips. Only the picker changes it: enabling or
disabling an additional interface leaves the primary where it is. Options, INTERFACE, **Additional
interfaces** drives up to four more units with their own gain, mute and
phantom power, and adds each one's microphone as a capture channel; see
[Multiple Wave interfaces](wave-interfaces.md). A capture input carrying an
additional unit shows its gain and switches under its own name in classic
INPUTS too, labelled with the unit's name. The primary unit keeps its
"plays on" heading because those controls belong to the header's unit.
The software low cut and ClipGuard stay on the XLR strips. A capture input
on one mono port has the [microphone EQ](#microphone-eq) too, and it runs
on this computer, never on the unit.

Capture inputs on supported Elgato units follow the unit when driving it,
switching its card profile or replugging it renames the source. The channel
keeps its sends and insert chain. If the card publishes several replacement
sources, the channel stays silent with a warning rather than choosing one.
Unplugging removes the unit's controls until it returns.

Unticking an additional unit with capture inputs asks before disabling it.
By default its channels and their chains are deleted. Tick **Keep capture
inputs and their chains** to retain them. Their audio continues while the
source is available, but their hardware controls leave the channel. A kept
channel stays silent if releasing the unit removes its source. With an
older daemon that does not report a unit on capture bindings, the
confirmation offers deletion only.

Capture channels host LV2, CLAP and VST3 inserts in the UI and through
the [HTTP API](http-api.md). In the classic layout, INPUTS shows one block
per capture input after Aux In and Line In, in layout order. Each block has its name, a
source chip with a connection light and any warning, on a mono port the
**EQ** and **Denoise** buttons right after the chip, its volume,
and an **Inserts** button with the chain below it. The chip opens the source
dialog. An input rack shows the source in its `binding` part and the
chain in `effects`. Broadcast and Wave put the source under the channel's
name and the chain behind the row head's **FX** button. A skin can leave
these parts out. The Flow window lists the plugins inside the capture
channel's card.

The chain window has the same presets, A/B, copy and paste, plugin controls,
native editors and latency readouts as an XLR chain. Its picker offers only
plugins that fit the input's width. A capture input on one microphone port
draws one meter bar and a stereo pair draws two; XLR 1 and XLR 2 keep their
two. An older daemon that does not report a chain width shows no capture
Inserts button. A capture microphone's chain window also offers
[Mic setup](#mic-setup) and the [microphone EQ](#microphone-eq), which
runs in software only. The terminal mixer edits capture chains too, using
the width the daemon reports.

A capture input on one mono port takes a mono chain; a stereo pair takes a
stereo chain. A capture input on the first pair of a source that has just
one channel, with no inserts, is changed to take that one port; the daemon
saves the change and its audio links stay as they were.
An input with saved inserts keeps its width.

Each chain processes before the channel's sends, volume and meter. Replacing
it keeps the combine and sends running; the new route is linked before the
old one stops, with a brief overlap of dry and processed audio. A channel
without active inserts has the same direct graph as before. An unplugged
source stops its chain, which starts again from saved parameters when it
returns. Its plugin editor closes and any plugin state outside those
parameters is lost. A load failure leaves the direct feed playing with an
error on the insert; repeated host deaths stop only that channel's chain.
Deleting the capture input stops its chain and removes it from the settings.

A saved profile includes every capture chain, empty ones too. Older profiles
leave capture chains alone. Input insert latency is reported per plugin but
is not compensated against other inputs. The Wave XLR Pro's direct path to
its jacks would carry the raw microphone, so OpenXLR turns it off while the
channel carrying the Pro's first input runs an insert, and the jacks hear the
chain. The existing 32 editable-channel and 16 inserts per
chain limits apply; each native insert runs its own host, and Windows
inserts also need Wine and the documented locked-memory allowance.

<a name="desktop-keys"></a>
## Desktop keys and focused application routing

Open Options, DESKTOP, then **Desktop keys**, and enable desktop
integration. For PC shortcuts, select
the application channels you want, then choose **Apply and configure keys**.
The desktop portal asks for the shortcuts and permission to use them. For an
OpenDeck key, choose **Route focused application** and the destination channel
in the Toggle inspector. No PC shortcut needs to be selected for OpenDeck.
Keep the OpenXLR UI process running; hiding it in the tray keeps the keys
active. The daemon alone does not own a desktop shortcut session.

On desktops supporting version 2 of the GlobalShortcuts portal, **Apply and
configure keys** also reopens the desktop's shortcut editor, including when
the desktop has restored an existing binding. Starting OpenXLR restores the
keys without reopening that editor. Older portals keep the keys active and
show a hint to edit them in the desktop's shortcut settings. If the desktop
refuses the editor request after binding succeeded, the error is shown and
the working bindings stay active.

Outside the Flatpak, OpenXLR introduces itself to the portal as `openxlr`
before it asks for keys. Current portals accept that name only when an
`openxlr.desktop` entry is installed, which the packages provide. A portal
that lacks this step or refuses the name does not stop Desktop keys: OpenXLR
continues and the portal identifies the process itself. Some portals then
refuse the shortcut session, and Desktop keys shows that error. A source
installation can add the entry as shown in
[installing from source](install-from-source.md#desktop-entry).

The GlobalShortcuts portal registers keys on Wayland. It does not expose the
focused application's identity, so OpenXLR asks the compositor each time
routing is requested:

- KDE Plasma: a short-lived KWin script reads the active process id and is
  removed after the query.
- Sway: `swaymsg get_tree`, which needs `SWAYSOCK` from the session.
- Hyprland: `hyprctl activewindow`, which needs
  `HYPRLAND_INSTANCE_SIGNATURE` from the session.
- Niri: `niri msg --json focused-window`, using `NIRI_SOCKET` from the session.
- X11: `xprop` reads the active window, its PID and its client machine.
  Windows identifying a different computer are refused. This source only
  supports native applications: a Flatpak window reports a sandbox PID in
  `_NET_WM_PID`, which cannot be resolved to a host process from X11 alone.
  Xwayland cannot identify focused Wayland windows.
- GNOME Shell on Wayland: the optional [OpenXLR Focus extension](gnome-focus.md).
  Its D-Bus endpoint answers only the running OpenXLR desktop service.
  A GNOME X11 session uses the X11 source described above.
  GNOME Flashback and Budgie run no GNOME Shell and cannot load it; they
  use the X11 source in an X11 session and have no focus source otherwise.

Hyprland routing has been run on a live session. Sway, Niri, the new X11
source and GNOME still need owner desktop acceptance.
Launch the UI from the desktop session so it gets these variables, and
have the corresponding command installed. X11 uses `xprop` from
`x11-utils` on Debian or Ubuntu. No window titles are
read. The daemon uses `gdbus`, supplied by the GLib command-line tools, to
ask the UI. Other compositors have no focus source yet. Routing reports an
error when the required extension or UI is unavailable, and inside the
Flatpak, which has no access to the compositor.

With a host PID from KWin, Sway, Hyprland or Niri, native OpenXLR can
also match Flatpak audio clients using their portal instance ids,
`flatpak ps` and the host process namespace. GNOME Shell on Wayland
reports host PIDs for its clients, so the same matching is expected to
work with the GNOME extension; it has not been tested with a Flatpak
application yet. A missing or
failed Flatpak query leaves those candidates unavailable and keeps native
application matching working. It never treats a sandbox PID as a host PID.
An exact host process takes precedence over its descendants. Shared
namespaces keep each instance distinct; ambiguous identities are refused.
The daemon reads one application snapshot for the candidates, then another
to recheck the returned keys before saving the assignment. With no audio
client it reports that the focused application has no identifiable PipeWire
client. A timed-out UI query has its own error; after an update, restart an
older UI as well as the daemon.

PC shortcuts need the GlobalShortcuts portal; Deck routing does not. On a
desktop without the portal, Desktop keys says so and routing
from OpenDeck keeps working. Enable desktop integration and leave the PC key
choices unchecked to use only Deck routing.

The exact process, or a unique audio identity among its child processes,
must appear in PipeWire. The existing application assignment is then updated
and remembered for the next launch. A stream that is still starting joins
that channel when the daemon next discovers it. Multiple possible identities, stopped
processes, unavailable clients and deleted target channels produce an error.
Titles and executable names are not guessed. A PC error is shown in Desktop
keys; OpenDeck flashes an alert for a failed or unanswered command.

Bindings are stored in `desktop-keys.json` independently of profiles. Renaming
a channel keeps its key identity; deleting it makes its key fail until it is
removed from the configured list. After a portal or session-bus restart, open
Desktop keys and apply again to reconnect. A method call that times out or is
cancelled also closes the desktop connection, releasing outstanding replies;
apply again once the desktop service is responsive. Cancelling the permission dialog
leaves the requested preferences saved, with a visible inactive error state;
apply again to retry or disable the integration.

Disabling or replacing a shortcut session stops further activations. A
command already sent to the daemon may still finish; its delayed reply
does not replace the status of the disabled, closed or replacement session.

<a name="soundboard-keys"></a>
### Soundboard keys

In **Desktop keys**, tick the [soundboard](#play-sound-clips) clips that
should have a key of their own, and **Add a key that stops every clip** for a
Stop all key, then choose **Apply and configure keys**. Each clip's shortcut
is named **Play clip** and the clip's name, and the desktop asks which key it
gets. A press does what the clip's **Press** setting says, as its pad does.
Where the desktop reports keys coming up, a key held down plays its clip once,
however often the desktop repeats it.

A **Hold** clip plays while its key is down where the desktop reports the key
coming up. The shortcut portals of KDE Plasma, GNOME and Hyprland do. On any
other desktop a Hold clip plays as **Overlap**, and Desktop keys says so after
the press, until the desktop has reported one key coming up; holds work from
then on.

On Plasma the release is not tied to the clip's own key. KDE's shortcut
service reports the last shortcut pressed as released when any key comes up,
and pressing another shortcut releases it too. A Hold clip therefore ends at
the first key you let go, and pressing a second clip's key ends the first
hold. A shortcut made of modifier keys alone fires when the key comes up, so
a Hold clip on it ends at once; give Hold clips a shortcut with an ordinary
key. A window that grabs the keyboard, such as a virtual machine or a remote
desktop, can take the release before the desktop sees it. The clip then plays
on until it ends or **Stop all** stops it. Pressing its key again plays it
once the clip's length has passed since the lost press, or ten seconds for a
looping clip.

A held clip also ends when the shortcut session closes or is replaced, and
when the UI quits, because the daemon ends the holds of a connection that
goes away.

The keys follow the library while the UI runs. Renaming a clip renames its
shortcut and keeps its key. A new clip has no key until you tick it.
Deleting a clip removes its shortcut and its tick. The daemon never gives a
deleted clip's id to another clip, so a key never moves to a different clip.
OpenXLR counts a clip as deleted only when the daemon listed it and then, on
the same connection, stopped listing it. After the daemon starts, a clip
missing from its first report keeps its key and its tick, so a library file
the daemon could not read, or a clip it refused to load, does not cost the
key you gave it. In **Desktop keys** such a clip is listed as not reported now
and stays ticked, so applying meanwhile keeps it.

OpenXLR binds the changed list on the shortcut session it already has, so
the other keys keep working while the desktop asks about a new one. Plasma
forgets the key of a deleted clip, including when it was the last clip with
a key. Hyprland keeps a deleted clip's shortcut until the next session, where
it does nothing. GNOME binds a session's keys only once, so there OpenXLR
binds the changed list on a new session first and closes the old one only
when the new one holds the keys; if that fails, the keys bound before keep
working and Desktop keys shows the error. If the desktop does not answer a
changed list within two minutes, the keys bound before keep working too, and
Desktop keys says the desktop did not answer. OpenXLR asks the desktop what
it bound right then, and again when a key arrives that the list in use does
not have, and takes the new list once the desktop has it.

Before the daemon reports the board, at login for example, the ticked clips
keep their keys under their last names, or their ids after a restart of the
UI, and the names follow once the board arrives.

A refused press shows the daemon's sentence in Desktop keys, such as "The
soundboard is off. Turn it on first." A key coming up is queued past the 16
waiting presses, up to 64 more, so a held clip hears its release. At most 64
clip keys are kept.

<a name="output-keys"></a>
### Output volume, mute and system output keys

In **Desktop keys**, select **Add volume and mute keys** and choose the current
system default or a named output. Each volume press moves five percentage
points within 0 to 150%. Monitor A and B update their corresponding mix
masters; a selected external monitor output uses the existing linked
monitor-volume behavior. Other external outputs change independently.
Ordinary internal OpenXLR application sinks are not volume targets because
their gains must stay at unity.

What the mute key toggles depends on the output. On one of the selected
physical monitor outputs it pauses and resumes the whole monitor selection,
the same as MONITOR's **Mute** button or pressing a Deck monitor dial.
Mix and channel mutes keep their values. Recording and streaming continue
unless the microphone pause option below is enabled.
On a monitor mix sink it toggles that mix. On any other output it toggles the
sink's mute at the audio server.

If Linux's native mute key targets a selected physical output, or an internal
application sink such as **OpenXLR System** while it is the default output,
OpenXLR treats the press as a monitoring toggle. Selected physical devices
mirror the pause and regain their previous native mute flags on resume.
Application routing sinks stay open so application audio can still reach
recording and streaming mixes. A mute on an internal application sink that is not the
default output is undone without pausing monitoring. The mute
icon on an application sink can return to its previous state while monitoring
remains paused; physical output devices show the native pause. The **Mute** button in OpenXLR's MONITOR header shows the
monitoring state; another mute press resumes monitoring. **OpenXLR Monitor A**
and **OpenXLR Monitor B** keep their individual mix mute controls.
Selecting an output preserves its existing native mute state; a mute key
pressed immediately after selection already controls the monitoring pause.
Reconnecting an output keeps its previous mute state. Delayed device
information does not count as another mute press. OpenXLR waits for the
native and registry identities to agree before accepting a reconnected
output's mute flag. If restoring a mix's mute after a blanket desktop mute
fails, OpenXLR retries on later sweeps and keeps the previous mute choice
until the restoration is confirmed. A refused restoration leaves all remaining
repairs queued and stops helper calls for that batch, rather than waiting for
every output to time out. Native output writes also remain pending
until readback confirms them. OpenXLR does not author a hardware mute before
it has verified that output's native identity. Removing an output keeps its
native mute while an old monitor connection still refuses to disconnect.
Once that connection is gone, a failed release is retried only for that same
device instance. A release still pending when the daemon stops is saved and
finished by the next start, as is the release of an output that `monitorOutput`
or `OPENXLR_MONITOR_OUTPUT` took out of the saved selection. Such a saved
release puts back the original flag on whichever device holds that name,
waiting for the device if it is not connected.
Unmuting an output muted before selection leaves it unmuted without starting
a monitoring pause. Unmuting a native mute authored by the global pause resumes
monitoring. If every assigned mix was individually muted, desktop unmute only
opens the native device: it leaves those deliberate mix mutes unchanged and
does not start a pause. That accepted unmute also replaces the saved native
mute choice, so changing feeds, removing the output or resuming a later pause
does not restore its earlier mute. OpenXLR does not guess which of several
assigned mixes you wanted to open. Explicitly unmuting that monitor in OpenXLR opens both the
mix and its assigned device. A later explicit OpenXLR mute re-arms native mute
control. The native-only release lasts for the current daemon session and device
instance. Restarting the daemon or reconnecting the device reapplies the saved
software feed mutes, while retaining the saved native restore baseline.

Native mute flags are observed on the next mixer sweep. A desktop command
that changes several flags in the same sweep is treated as one press;
changes arriving in separate sweeps are separate actions. For direct monitor
control that does not mute an application routing sink even briefly, bind
OpenXLR's desktop mute key to a selected physical monitor output, use the
MONITOR button or press the Deck monitor dial. The keys and dial use the daemon's
current pause state, and the MONITOR button sets the pause to its checked value.

Select **Switch system output** entries to register keys which select and
enforce that default sink. **Follow selected monitor output** follows the
first output in the monitor selection. These keys keep the configured capture
default and mixer routing intact. Unplugged named outputs report an error;
their bindings remain saved for reconnection. At most 16 output-selection
shortcuts and 32 focused-channel shortcuts are retained.

For OpenDeck, use the Toggle inspector's **System output controls** and
**Enforced system output** groups. Volume up and down keys are momentary:
they acknowledge the press and show no state. The mute key lights red while
its output is muted. Output-selection keys indicate the enforced choice. A
Dial can also take a target from the **Outputs** group, the system default or
any controllable output: turning it sets that output's desktop volume, up to
150%, and pressing it toggles the mute; the ring shows the level and the
mute. These Deck actions only
need the daemon. PC shortcuts also need the running UI and a desktop supporting
the GlobalShortcuts portal, but do not need KDE's focused-window integration.
Desktop shortcuts are explicitly selected in the portal; existing media-key
bindings are not replaced automatically.

### A dedicated monitoring pause key

In **Desktop keys**, select **Add a monitoring pause key**, then **Apply and
configure keys**. Bind the keyboard's mute key to **Pause or resume monitoring**
in the desktop's permission dialog. This action addresses OpenXLR's monitoring
pause directly, independent of the system default output. Volume keys can
keep their desktop bindings. The portal suggests the media mute key for a new
binding; an existing user choice is kept.

Give the mute key one owner. In Plasma, replace the existing Audio Volume
**Mute** binding rather than assigning the same key to both actions. Plasma
6.8's built-in mute action mutes every sink, including OpenXLR's internal
routing sinks, and cancels its global mute when one of those sinks is restored.
That can show a muted OSD followed immediately by an unmuted OSD. The dedicated
OpenXLR action avoids that blanket mute. It stays active while OpenXLR runs in
the tray; closing the UI process releases its shortcut session.
In Plasma's conflict dialog, enter the mute key again and confirm **Reassign**,
then accept the shortcut configuration. If you later disable this integration,
restore **Audio Volume**, **Mute** in Plasma's keyboard shortcut settings to
give the key back to the desktop.

In Options, AUDIO, **Mute microphone sends with monitoring** also pauses the
XLR/Mic strips and mono capture microphones whenever monitoring is paused.
The option starts off and is saved in `mixer.json`, outside profiles. The pause
and the option survive a daemon restart. They leave individual sends, mix
mutes and hardware microphone mute choices intact. Resuming reveals those
choices, including changes made while paused; explicitly opening a monitor
also resumes the linked microphones. Deleting the last monitor mix that was
open when you paused ends the pause as well, so the microphones resume in
every mix, Stream and Chat included, even though the remaining monitor mixes
stay muted.

The microphone pause affects audio routed through OpenXLR, including its
virtual microphones used by Discord or a recording program. It does not mute
applications capturing a physical microphone directly, stereo capture channels
or Aux In and Line In. It stops a Mic setup session and discards its take; a new recording is
refused until microphones resume. A paused microphone that is the
[ducking](#ducking) key stops lowering the other channels on this computer.
Its strip meter keeps moving while you speak, because the meter reads the
microphone before the pause mutes it. If the audio server refuses a microphone
mute, Options shows that silence is not yet confirmed and the daemon retries.
Do not treat that pending state as a confirmed microphone mute.

OpenDeck output volume and mute keys keep rapid presses and apply them in the
order they were pressed, across keys, so a burst at 0 or 150% ends where the
presses say. Each press waits for the previous one to be acknowledged. Up to
eight presses wait per key and 64 across all keys; a press beyond that shows
an alert. An error, a timeout or a disconnect discards the waiting presses
instead of applying a late burst, and changing a key's settings or removing
it drops that key's waiting presses. Focus-routing and output-selection keys
do not wait behind the volume and mute presses: they are sent at once, and a
second press on the same key while its first is unanswered is ignored.

PC shortcut presses are processed in order, including quick volume repeats.
Up to 16 presses can wait behind the active command. If that queue fills,
Desktop keys reports that the additional press was not queued. Disabling or
reconfiguring the shortcuts discards their waiting commands; an already sent
command may still finish. This queue also preserves the order of an output
switch followed by a volume change.

<a name="terminal"></a>
## The terminal mixer

`openxlr-tui` is the same mixer in a terminal. It talks to the same daemon
the UI does, over the same socket, so the two can be open at once and
each shows what the other changed. Run it on a machine with no desktop
session, over ssh, or on a tiling setup where one floating window is the
odd one out.

Start it with the daemon running:

```sh
openxlr-tui
```

The terminal retries when the daemon is unavailable or restarts. A connection
that accepts the socket but never completes its handshake is abandoned after
five seconds, so it cannot prevent the next attempt.

In Inputs, **Microphone EQ** lists each microphone, including mono capture
inputs. Open its **bands** row for the band editor. Up and Down select
a control, Space toggles a switch, Left and Right change the type or value,
and Escape returns to Inputs. Frequency moves by a sixth of an octave, with `[`
and `]` or Ctrl+Left/Right moving a twenty-fourth, as a Deck dial does. Gain moves by 0.25 dB; Q moves one
step on the EQ's logarithmic grid. Home and End choose the range limits.
Gain is retained but cannot be edited for a cut or notch, which does not
use it. Enter on a number opens an exact-value prompt; use a decimal point.
An empty prompt or Escape changes nothing. Values must be finite and in
range, then snap to the same steps as the graphical editor.

**Reset bands** restores the four defaults while keeping the EQ's on/off
switch. Changing a frequency leaves another client's gain or Q alone,
and short bursts keep each step. A removed microphone
closes the editor, and a value being typed before disconnection or removal
cannot be applied afterwards, even if a new microphone reuses the same
channel id before the next screen redraw. Settings are saved by the daemon in the
same mixer and profile state as edits made in the graphical UI.

Below it, **Noise removal** lists each microphone's
[noise removal](#noise-removal): what runs and the delay it adds, or why it
does not run; its on switch; the engine, offered when the daemon's plugin
host carries both; and the strength, in steps of 6 dB with `[` and `]`
moving one, and **no limit** at the top.

Below that, **Voice changer** lists each microphone's
[voice changer](#voice-changer): the voice heard now, or why it does not
run, and one choice that steps through **Off** and every voice. With
**Custom** chosen, its pitch and formant follow in whole semitones, and
`[` and `]` move them by a half.

The mixer opens as a desk of vertical channel strips; the Matrix section
shows the same sends as one grid, every channel against every mix. The left bank shows
sends into the selected mix; the right bank holds the mix masters. Each
strip has a name plate, a percentage, a fader, separate left and right
meters and a mute key. On the channel that carries an interface input,
XLR 1 and XLR 2 unless a strip is bound elsewhere, the key is that input's
own mute, the same control as Mute on the Inputs page. When that channel's
send into the chosen mix is muted, the word muted stands where its
percentage would be, and the matrix is where that send is toggled. A strip
[bound to another microphone](#input-bindings) is not the interface,
so its key mutes its send into the chosen mix like any other channel's.
Its name plate shows the source where INPUT would be, in the warning colour
while the source is silent, and the line under the desk names the source
and port of the selected bound strip, or says why it is silent. Up and down choose a channel, starting with the
masters. Left and right choose the destination mix. The line above the
key hints names the exact send or master under the cursor, and the
channel's own [volume](#channel-volume) when the channel has one. Monitor
masters can reach 150%; other masters, channel volumes and channel sends
stop at 100%. The Matrix section shows a channel's volume beside its name,
XLR 1's, XLR 2's, Aux In's and Line In's included; `<` and `>` move it by five points
and `,` and `.` by one, on the Mixer and Matrix tabs alike.

Above the strips, the selected mix has a stereo meter, a large held RMS
reading and fifteen seconds of level history. Meters read -60 to 0 dBFS
RMS. A hold mark keeps the highest recent RMS reading for one second, then
falls by 18 dB per second. It is not a sample peak measurement. Each meter
cell takes its colour from its place on the scale. Missing meter packets
leave gaps in the history; a reading older than one second falls to zero.

At 150 columns the usual nine channels and five masters fit side by side.
At 120 columns the banks show fewer strips and follow the selection.
Below 120x30 the section rail becomes a top row, with short section names
below 102 columns, the held reading becomes text, and shorter strips keep
the same controls. Resizing keeps the selection.
The counters below each bank show which strips are visible. Below 80x24,
the terminal asks for more room and keeps the selection for when it fits.

Nine sections are reachable with `1` to `9`, Tab and Shift+Tab:

| Section | What is on it |
|---|---|
| 1 Mixer | channel sends, mix masters, stereo meters and level history; channel and user mix creation, renaming, removal and ordering |
| 2 Matrix | the whole submixer as one grid: every channel's send into every mix, the mix masters and their meters across the top, a meter beside each channel. With height to spare a channel's meter is stereo: its name and its sends sit on a row of their own, the left bar on the row above and the right bar on the row below, so the name is centred between its two bars and each bar is centred on its letter. Where only two rows a channel fit, the name sits beside the left bar. Either way a blank row follows each channel, so its right bar never touches the next channel's left one. A short terminal keeps one row a channel and one summed bar. A microphone input, including a mono capture input, carries one bar rather than a pair of the same reading. A strip bound to another microphone shows its source, or why it is silent, on the row under its name, and a silent one's name is in the warning colour |
| 3 Inputs | hardware controls in grouped cards with gain arcs on a wide terminal, or a scrolling list in a small one; gain, mute, low cut, expander, voice tune and its strength, phantom power, ClipGuard and the compressor for each of the interface's inputs, headed by the channel that carries it; what feeds XLR 1 and XLR 2; each capture microphone's source, connection and carried unit controls; what runs each microphone's EQ and, on a unit that can run it, the choice between the unit and software; each microphone's noise removal, its engine and strength; software processing, headphones, direct monitor blend, hardware output routing, [ducking](#terminal-ducking) on any device, USB Aux return and, on a Wave XLR Pro, the Line In gain; each capture input's own gain |
| 4 Outputs | selected monitor sinks and their feeds, shared output volume, the monitoring pause (**Mute monitoring**, microphone sends also pause when enabled in Options), each sink's volume and mute, and the system default sink and source |
| 5 Apps | every known application and its channel assignment, including the desktop's own routing |
| 6 Inserts | the chain on each input and mix, its order, bypass and status; add a plugin from the catalogue, narrowed by typing, of those that fit the chain's width; open a plugin's native editor or edit its generated controls when the editor is blocked or refused |
| 7 Profiles | load, save over, save as, delete and recall on connect; interface selection and recorded device defaults |
| 8 Options | the skin picker and **Material mode**, the interface and its warning, the layout, connection status, daemon version and fresh state request |
| 9 Sounds | the [soundboard](#play-sound-clips): the board's switch, SFX's volume as its master, and each clip with its length, press mode, loop, gain, status and a bar while it plays; play, stop, stop all, audition, reorder, rename, delete, pages, import by a typed path and restoring the starter sounds |

In Outputs, quick Space presses keep the complete selection while the daemon
acknowledges it. Feed arrows likewise keep each step separately for each output.
The arrows choose one mix at a time. On an output that sums two or more mixes,
ticked in the desktop UI's feed picker, the first Left or Right sends nothing:
the bottom line says how many mixes the output hears and which one the same
arrow, pressed again, feeds alone. Only that second press, made while the line
shows and before any other key, replaces the sum. It expires after four
seconds of elapsed time, even when the system clock changes. Switching
sections or opening help withdraws it too.
A refused edit returns to the reported setting and shows the error; reconnecting
discards any unconfirmed choice. The terminal, Stream
Deck and Omarchy clients use the daemon's reported default monitor mix, or the
first reported monitor with an older daemon that does not report that field.

The bottom line shows the current section's keys, with help and quit at
its right end when there is room. Temporary messages stay there for four
seconds; system clock corrections do not shorten or extend that time.
`F1` or `?` opens a key table. Up and down scroll that table if
it does not fit. An unadvertised hardware control is marked unavailable.
Controls below the visible hardware cards appear as the selection moves.

| Where | Keys | Action |
|---|---|---|
| Everywhere | `1` to `9`, Tab, Shift+Tab | choose a section |
| Everywhere | `F1`, `?` | show help |
| Everywhere | `q`, Ctrl+C | quit; in a text prompt `q` is text and Ctrl+C quits |
| Mixer | Up/Down, PageUp/PageDown | choose a channel, or jump ten channels |
| Mixer | Left/Right | choose the mix the sends feed |
| Mixer | Home, End | choose the masters or the last channel |
| Mixer | Space | mute or unmute the selected master or send |
| Mixer | `-`, `+`, `[`, `]` | lower or raise the level by five points, or by one point |
| Mixer, Matrix | `<`, `>`, `,`, `.` | lower or raise the selected channel's volume by five points, or by one point |
| Mixer | `M` | add a monitor mix |
| Mixer | `r`, `n`, `N`, `c`, `d` | rename, add an application channel, add a virtual microphone, add a capture input, or delete |
| Mixer | Ctrl+Left/Right | move the selected channel or user mix one strip along, past hidden ones to the next strip shown |
| Matrix | Up/Down, Left/Right, PageUp/PageDown, Home, End | move through the grid; Home is the masters row |
| Matrix | Space, `-`, `+`, `[`, `]` | mute the cell, or change its level by five points or by one |
| Lists | Up/Down, PageUp/PageDown | move between controls |
| Inputs and numeric controls | Left/Right, `-`, `+` | change a value; Ctrl+Left/Right or `[` and `]` takes the finer step |
| Inputs and numeric controls | Home, End | set the minimum or maximum |
| Toggles and choices | Space, Enter, Left/Right | toggle or choose a value |
| Outputs, on a sink | Space, Left/Right, Enter | select the sink, choose its feed (a summed feed asks for a second press), or make it the main output |
| Outputs, on a sink | `-`, `+`, `m` | change its volume or toggle its mute |
| Apps | Left/Right, `i`, `f` | assign a channel, leave routing to the desktop, or forget the assignment |
| Inserts | Space, Ctrl+Up/Down, `d`, `e` | bypass, reorder, remove, or open the plugin's native editor, with generated controls when blocked or refused |
| Inserts | `a` | add a plugin: the picker lists plugins that fit the reported chain width: mono for a microphone, stereo for a capture pair or mix |
| Plugin picker | letters and digits, Backspace, Up/Down, Enter, Escape | narrow the list by name, category or format; choose; add the plugin after the chain's last one; go back. While the picker is open `q` and the digits are filter text |
| Insert controls | Escape, `r`, `e` | return to the chain, reset every declared control to its default, or try the native editor again |
| Insert controls | Up/Down, PageUp/PageDown, Left/Right, Space, Enter | select a control, change a number or choice, or toggle a switch |
| Insert controls | `-`, `+`, `[`, `]`, Home, End | lower or raise a number, take a fine step, or set the minimum or maximum; Ctrl+Left/Right also takes a fine step |
| Profiles, on a profile | Enter, `s`, `r`, `d` | load, overwrite, recall on connect, or delete |
| Options | Enter, Space, Left/Right, `R` | use a skin, choose the Material mode, or reload the skin list |
| Sounds | Up/Down, PageUp/PageDown, Home, End | choose a clip |
| Sounds | Enter, Space, `s`, `S` | play the clip (a second press lets a Hold clip go), stop it, or stop every clip |
| Sounds | `a` | audition the clip in the monitor mixes only, or stop its audition |
| Sounds | `-`, `+`, `[`, `]` | lower or raise the clip's gain by five points, or by one point |
| Sounds | `m`, `l`, `r`, `d` | step the press mode, turn the loop on or off, rename, or delete |
| Sounds | Ctrl+Up/Down | move the clip earlier or later |
| Sounds | `b`, `i`, `R` | turn the board on or off, import a file by its path, or add back the starter sounds the board no longer has |
| Sounds | `p`, `P`, `g`, `G` | show the next or previous page, move the clip to a page, or rename the page shown |
| Sounds | `<`, `>`, `,`, `.` | lower or raise SFX's volume, the board's master, by five points or by one point |
| Inputs, Bindings | Left/Right, Enter | choose a strip's source or port, then apply it |
| Inputs, Ducking channels and mixes | Left/Right, Home, End, Space, Enter | move between the channels or mixes, then duck the one under the cursor or stop ducking it |
| Text prompts | Enter, Escape | accept or cancel |

Anything that cannot be undone, such as deleting a channel or resetting
the device, asks you to type `yes` first. To create a profile, select
Save settings as and press Enter.

On Inputs, each of the interface's inputs is a card headed by the channel
that carries it. While the input plays on its own strip the heading is the
strip's name, XLR 1, or Mic 1 on a Wave:3. While XLR 1 is bound elsewhere
and a mono capture input carries the interface's microphone, its controls
sit with that channel, headed by the unit and input number. A capture input
on a stereo pair, or any capture channel from a daemon that does not report
`microphone`, keeps the card headed Input 1 on that channel. With no
channel carrying it, it reads Input 1, not in the mixer. The controls work
in every case, so a hot preamp can still be turned down.

Each mono capture input has a source and connection card in Inputs,
including an unplugged microphone and a microphone without hardware
controls. A supported unit's renamed capture node is followed by
`binding.unit`. Its gain and switches appear there while the unit is
driven, for both the primary and additional interfaces. A stereo capture
input stays in the mixer and Inserts tabs. Inserts lists capture chains
in layout order and filters plugins by `chainWidth`, even while the source
is unplugged. Microphone meters read `microphone`; an older daemon without
these fields keeps XLR-only mono meters and XLR and mix insert targets.
Mic setup remains a UI control.

The Bindings card chooses what feeds XLR 1 and XLR 2, the choice the UI
makes in [Use another microphone on XLR 1 or XLR 2](#input-bindings).
Each strip has four rows. The source lists the interface's own input first,
Interface jack (XLR 1) or Built-in microphone on a Wave:3, Wave:3 MK.2 or Wave:1. Every
capture source the daemon accepts follows, leaving out OpenXLR's own nodes,
monitor taps and the primary interface's own nodes. Another Elgato
interface's microphone is listed too, and for one OpenXLR does not drive
yet an Also drive unit row appears, on by default, that enables it as an
additional interface after the binding. If enabling it fails, the strip
stays bound and the bottom line says so. The port
is numbered from 1 and carries its channel position when PipeWire reports
one, such as 2 (FR); the interface's own input takes none. Apply sends the
choice. The feed row says whether the strip is linked to its source, or
why not. Neither the source nor the port changes anything until you press
Enter on Apply, and the Apply row says not applied until then. A saved
source that is unplugged stays listed as missing, and a strip on an Elgato
interface whose source was renamed shows the source it plays from now.
Choosing a Bluetooth
headset adds a row saying it stays in headset mode while bound. The terminal
mixer does not offer the UI's capture channel for the interface's own
microphone; bind from the UI when you want to keep it in the mixer.

<a name="terminal-ducking"></a>
The Ducking card on Inputs holds the whole [ducking](#ducking) setup, on
any device and with no interface at all. **Ducking** is the switch, with
a word beside it while it is on: in software, on the unit, not held yet,
not running, or cannot run. **Key** cycles through the microphones: XLR 1,
XLR 2 on a Wave XLR Pro, and each capture input on one mono port. When a
channel on the list becomes the key, it leaves the list in the same step.
**Channels** and **Mixes** are rows of keys, one per channel or mix, with
a check on each one ducked. Left and right move along the row, which
scrolls when the keys do not fit, and space ducks the key under the
cursor or stops ducking it. The key microphone is never on the Channels
row. **Sensitivity**, **Amount** and **Speed** are the three presets.

On a Wave XLR Pro the card adds **Runs on**, this unit or this computer,
which `setDuckingProcessing` sends, and **Key heard**. On the unit that
reads XLR 1 before inserts: the Pro listens to its own input, so a gate
or noise suppressor on XLR 1 does not stop the unit ducking on noise. On
this computer it reads after the EQ and inserts. While the unit is chosen
and the setup does not fit it, **Not on unit** gives the reason in a few
words; the UI shows the whole sentence. While this computer runs it,
**Lowered by** is a short bar of how far the channels are down right now,
the deepest of the ducked mixes. If the software side cannot run,
**Software** shows the daemon's reason. In hardware-control mode the card
says ducking needs the software mixer.

A strip on a driven additional interface gets a card of that unit's own
controls, such as Wave:3 input 1 on Mic 2, with the gain, mute, low cut,
ClipGuard and phantom power the unit has. On the Mixer tab that strip's key
is the unit's mute. Stop driving the unit and the card goes, and the key is
a send mute again.

<a name="terminal-sounds"></a>
The Sounds tab is the [soundboard](#play-sound-clips) in rows. The first
line has the board's lamp and switch, then SFX's channel volume, which is
the board's master, then how many clips there are and how many play. When
the board cannot play, the line under it is the daemon's sentence, such as
the board being off or SFX deleted. Otherwise it lists the file types this
build reads. Each clip is a row in library order:
its icon and name, in its colour when it has one, its length, its press
mode, whether it loops, its gain, and its status: ready, loading, off, or
the reason it cannot be loaded. While a clip plays, a mark stands before
its name and a bar shows how far its newest voice is, with the number of
voices when more than one plays it. The status of a clip being
auditioned reads auditioning. The line at the foot of the list
shows the selected clip's whole reason when it cannot be loaded, that it
is auditioned, and the answer to the last import.

`a` [auditions](#audition-a-clip) the selected clip in the monitor mixes,
never on the stream, and on the clip being auditioned `a` stops it. A
second `a` less than three quarters of a second after the last is taken
as the key repeating and ignored.

A terminal sends no key-up, so a **Hold** clip cannot play only while a
key is down. Instead the first Enter or Space plays it, and once the
daemon takes the press the row says held. The next press lets it go,
which ends it the way lifting a Deck key does. A press the daemon refuses
holds nothing and shows its sentence on the bottom line. The terminal
also lets go when the clip stops, by itself or by a stop from any client,
when `s` or `S` stops it, and when its mode changes away from Hold, here
or in another client, once the daemon reports the change. The daemon
lets go of every clip the terminal held when its connection closes, so
quitting or losing the daemon leaves nothing playing, and no press goes
out while the terminal is not connected.

A key held down repeats, which would play and stop a Hold or Toggle clip
over and over. Presses of the same Hold or Toggle clip less than three
quarters of a second apart, counting from the last one, are taken as that
repeat and ignored, so press again after a short pause. Overlap and
Restart clips play on every press.

`i` asks for a file's path. The terminal mixer expands a leading `~` and
a `file://` address on this machine, decoding its `%20` escapes and
keeping a `#` as part of the name. It drops the quotes or the backslashes
a terminal puts around or inside a dropped file's name, and reads a
relative path from the folder it was started in. A path longer than 4096
characters is refused before anything is sent, and without a connection
to the daemon the import fails at once. The daemon copies and checks the
file as it does for the UI. Its answer reaches the bottom line on
whatever tab is open, and the foot of the list keeps it. A refused file
shows the daemon's sentence, such as "The clip is 31.0 s long; the limit
is 30 s." After an import the clip the daemon added is selected. `R` adds
back the [starter sounds](#starter-sounds) the board no longer has, and
the bottom line says which, or that none was missing. Deleting a clip asks you to type `yes`. Choose
icons and colours in the UI.

Once a clip is on a page other than Main, the pages stand as tabs over the
rows and the rows are the clips of the page shown. `p` and `P` show the
next and the previous page. `i` imports onto the page shown. `g` asks for
the selected clip's page, and the tab follows it there; `G` renames the
page shown. Typing `-` in either prompt means Main, so `g` with `-` moves a
clip back to Main and `G` with `-` moves the whole page there, which ends
it. No page can be named `-` alone, so the dash always means Main. Ctrl+Up and Ctrl+Down move a clip among the clips of its page. Export
and import the library in the UI.

The header names the connected interface, and its lamp is the interface's
connection. A wide terminal's rail shows each interface input's gain and
the meter of the channel that carries it. The label is the strip's name, or
In 1 when a capture channel carries the input. With no interface connected
the rail says Offline and keeps a bound XLR 1's meter.

On an insert, `e` first asks the daemon for the plugin's own editor. If
compatibility rules block it or the daemon refuses to open it, the terminal
shows generated controls and the reason on one line. The heading names the
plugin and its chain. Controls follow catalogue order and show the saved
parameter value, falling back to the plugin's default when no value is saved.
Switches toggle on or off, enumerations use their scale point labels, and
integer controls move in whole numbers. Continuous controls move by one
twentieth of their range, or one hundredth with the fine keys. Positive
logarithmic ranges move by ratios over that range. A range that includes
zero or negative values uses linear steps. A refused value appears on the
bottom line, and the daemon's state restores the displayed value. Plugins
without declared controls say so.

The terminal requests the catalogue when Inserts is first shown and refreshes
it after chain or native editor rule changes. It keeps the descriptions for
plugins in the chains. Escape returns to the chain, and `r` restores every
declared parameter to its default. These edits use the same live parameters
and persistence as the UI.

The terminal mixer wears the same skins as the UI. It reads the same
files, in the same order, and the choice in Options is the same choice, so
picking Gruvbox in one picks it in the other ([skins.md](skins.md)).
With no saved choice it wears Broadcast, as the UI does, and writes
nothing. If the saved choice cannot be read, the terminal mixer uses
Material for that run and leaves the file as it is. Material's mode is shared too:
**Material mode** on the Options tab changes it with Left and Right, and in
System the terminal follows the desktop's preference through the portal
with `gdbus`, staying dark when there is no session bus or no `gdbus`.
A skin or mode chosen there applies for the run even when `ui.json` cannot
be written, and the bottom line says the saved choice could not be written.
`--skin <id>` uses one appearance for this run without saving it, with
Material held dark, and
`--list-skins` prints what this machine has, `OPENXLR_SKIN` names a skin
the same way, and `--version` prints the version. Deck, Nyan Pink and the
Omarchy skins get console fader caps, bracketed keys and lamps; Wave and
Broadcast get the console cap with plain keys; Material is plain
throughout.
The terminal reads these choices from `controls` and derives its surfaces
and selection from the skin's colours, including light skins. A meter is
one solid bar whichever appearance the skin names, and its colour runs from
the fill colour through the warning colour to the hot colour along the
scale, so the top of a loud bar is red and the bottom stays green. A terminal advertising true colour through
`COLORTERM` gets RGB cells; otherwise it gets xterm-256 colours. Each frame
writes only changed cells, at most fifteen times per second. Adjusting the
system clock does not pause redraws of live meters or key feedback.
Control characters in labels and skin names are displayed as spaces, so
incoming text cannot move the cursor or issue terminal commands.

It needs a terminal. With its output piped somewhere it says so and stops.

<a name="omarchy"></a>
## The Omarchy bar

On Omarchy 4 the AUR package carries the shell plugin when it is built
there. Run `openxlr-omarchy-enable` once after installing it. The bar
then shows up to four microphone input meters in layout order, including
mono capture inputs, dimmed for hardware mute, and the first selected monitor output's feed and
meter. An input appears only while it has had signal above -60 dBFS within
the last 10 seconds. Muting keeps that history; a quiet input disappears
when those 10 seconds elapse. Stereo capture inputs stay out of the bar.
An older daemon without `microphone` keeps the XLR-only bar. Labels use the
bar's regular body font.
Click it for
the mixer, and use the arrows to choose the mix whose sends you are editing.
The bracketed mute key affects that send or master; a carrying channel's
separate mic button affects its unit's hardware mute in every destination.
Input labels are the daemon's strip names: XLR 1 on an XLR jack, Mic 1 on
a Wave:3's capsule or another microphone. The mic button belongs to the
channel that carries the interface's input. A strip
[bound to another microphone](#input-bindings) has no mic button and
is not dimmed by the interface's mute. Its kind line and the tooltip name
its source, or say why it is silent, and its bracketed send mutes work as
usual. A capture channel that carries the interface's microphone gets the
mic button instead, and so does a strip bound to an additional interface
OpenXLR drives, whose button mutes that interface.

Each bank shows whole strips. Use its lower arrows and range indicator to
reach the remaining channels or masters. The header uses friendly output
names and shortens long labels. The popout stays within the screen margins.

The popout uses the terminal mixer's strips with the bar's colours and
font. Its meters take the OpenXLR skin matching the active Omarchy theme,
and repaint on a theme change. An unmatched theme or unreadable theme
file uses foreground shades. Bar meters remain monochrome.
It needs the daemon, but the UI can be closed. When the daemon
is absent the widget dims, clears its meters and waits to reconnect.
`omarchy plugin disable openxlr.mixer` removes it from the bar.
[omarchy.md](omarchy.md) covers installation, package changes, commands
and the checks still needed in a running Omarchy shell.
