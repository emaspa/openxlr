# OpenXLR manual

This manual follows `main`. A released package can lag behind it; read the
manual at that release's tag for matching behaviour. The app's Manual links
open this page on GitHub `main`.

How to use OpenXLR day to day: what it changes on your system, the
concepts behind the mixer window, step-by-step tasks, and what to do
when something does not work. For installing, see the README; for the
full list of controls per device, [hardware-support.md](hardware-support.md);
for scripting, [api.md](api.md). Every section carries a fixed anchor
(the `<a name>` before its heading), so a link like `manual.md#open-files`
keeps working when sections are renumbered.

<a name="first-run"></a>
## 1. First run

OpenXLR is two programs. The daemon (`openxlr-daemon`, a systemd user
service) talks to the interface over USB, builds the PipeWire mixer and
keeps running whether or not a window is open. The mixer window
(`openxlr`) shows and changes what the daemon holds; closing it changes
nothing.

When the daemon starts with the submixer on (the default), these things
happen on your system:

- New audio devices appear in your desktop's sound settings, all named
  `OpenXLR …`: one output per channel and one input per virtual
  microphone. By default that is `OpenXLR Game`, `OpenXLR Music`,
  `OpenXLR Browser`, `OpenXLR System`, `OpenXLR Voice Chat`, `OpenXLR
  SFX` and the hardware channels as outputs, and `OpenXLR Stream` and
  `OpenXLR Chat` as inputs; a layout you have edited ([section 3.11](#layout))
  comes back as you left it.
- Applications that play audio are moved onto a channel output by name
  ([section 2](#concepts)). They keep playing; only the device they play into
  changes.
- Your system default output and input are left as they were. The
  daemon remembers them at start and puts them back if the session
  manager switches to one of the new devices in the following seconds.
  If you set defaults in Options ([section 3.7](#default-devices)), those are held instead.
- On the Wave XLR Pro the daemon parks the card on its pro-audio
  profile while it runs, so the raw multichannel device is available to
  the mixer, and restores the previous profile when it stops.

The window's header shows the connected interface with a green dot.
"No device" means the daemon cannot open the interface: replug it once
after installing so the udev rule applies ([section 5.1](#no-device)).

If you only want hardware control and no mixer, turn the submixer off
in Options ([section 3.8](#hardware-only)). The daemon restarts in hardware-control mode
and the `OpenXLR …` devices disappear.

Update checks are also controlled in Options. They are off by default. You can
make one explicit check with **Check now**, or opt into a background check at
startup; opted-in checks run at most once per 24 hours. A notice contains the
upstream stable release notes as plain text and a link to GitHub. OpenXLR never
downloads or installs an update.

<a name="concepts"></a>
## 2. Concepts

**Channels** are where audio enters the mixer. Three carry the
interface's inputs (XLR 1, XLR 2 where the device has one, Aux In for
the Pro's Line In and USB Aux input) and the rest carry application
groups: Game, Music, Browser, System, Voice Chat and SFX by default, and
whatever you add, rename or remove in the layout editor
([section 3.11](#layout)). Each channel is a PipeWire output device an
application can play into.

**Mixes** are where audio leaves. The default layout has five; the
virtual microphones among them can be added, renamed and removed:

| Mix | What it is | Where it goes |
|---|---|---|
| Monitor A | what you hear | the ticked outputs in the MONITOR card, unless their feed is set to Monitor B |
| Monitor B | a second selection to hear | the ticked outputs whose feed is set to Monitor B, or summed with A on outputs set to Monitor A+B |
| Stream | what your audience hears | the `OpenXLR Stream` virtual microphone, for OBS or any recorder |
| Chat | what your call partners hear | the `OpenXLR Chat` virtual microphone, for Discord, Zoom and the like |
| any you add | whatever you route into it | its own `OpenXLR <name>` virtual microphone |
| Aux | what a second computer receives | the interface's USB Aux port (Wave XLR Pro only) |

Every channel has a **send** into every mix: a level and a mute. The
SUBMIXER card shows them as a grid, channels down, mixes across. Each
mix also has a **master** level and mute. A typical setup keeps Music
loud in Monitor A and lower in Stream, and out of Chat entirely.

**Application routing**: when an app starts playing, the daemon reads
its name from PipeWire and picks a channel by rules: browsers to
Browser; Spotify, YouTube Music and media players to Music; Discord,
Zoom, Slack and other chat apps to Voice Chat; Steam, Lutris, Heroic
and games to Game; anything else to System. Electron apps report
"Chromium", so the process name is used instead, which is how Discord
lands in Voice Chat. An app you move to another channel is remembered
([section 3.3](#apps)).

**Profiles** are named scenes: the interface's hardware settings plus
the whole submixer (sends, masters, monitor outputs, aux state, insert
chains). They are saved per interface. Application routing and the
system default devices are not part of a profile, so recalling one
does not rewire the desktop.

**Inserts** are LV2, CLAP or VST3 effects placed in the signal path: a mono
chain on each XLR input, a stereo chain on each mix. On the first XLR Dock
and original Wave XLR, OpenXLR also offers software low cut and limiting
on XLR 1 because their backends do not expose those hardware controls.
Unmapped processing previously enabled in Wave Link may still be active
on the original Wave XLR. See [hardware support](hardware-support.md).

<a name="tasks"></a>
## 3. Tasks

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

<a name="monitor"></a>
### 3.2 Choose what you hear and how loud

1. In the MONITOR card, tick every device the monitor mixes should play
   on: your speakers, a headset, or several at once. On the Wave XLR
   Pro its own outputs (Headphones 1, Headphones 2, Line Out) appear
   here too; ticking one switches the hardware's output routing.
2. Next to a ticked device, the feed picker says which monitor mix it
   hears. Leave it on Monitor A, or choose Monitor B for an output that
   should hear a different selection: a headset whose game side and
   chat side are two sinks, for instance, gets the voice channels on
   Monitor B and everything else on Monitor A. Monitor B has its own
   sends and master in the SUBMIXER card. Monitor A+B plays both mixes
   summed on one output: put the desktop on A and only the microphone
   on B, add a denoiser under the Monitor B master, and one pair of
   headphones hears both, with the plugin touching only your voice.
   The Pro's own jacks share one feed.
3. The Volume slider sets the level of the selected devices.
4. The HEADPHONES card holds the interface's own headphone volume,
   low-impedance mode, and on the Pro the Mic ↔ PC crossfade, which is
   the zero-latency direct monitor inside the device: left is only your
   microphone, right is only computer audio.

On the Wave XLR Pro the headphone jacks are fed by a mix inside the
device. When a jack is the only monitor output, XLR 1's send to the
Monitor mix drives that hardware path: unmuted, you hear yourself with
zero latency; muted, you do not. If another device (speakers, a headset)
is ticked as well, the microphone reaches everything through the
software mix instead, with a few milliseconds of delay.

<a name="audio-flow"></a>
### Audio flow

Open **Flow** from the main window to see inputs, channels, mixes and outputs
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
Outputs follow the selected monitor feeds, including Monitor A+B, and the
current virtual microphones. Apps left to desktop routing appear without an
OpenXLR connection. The window sizes itself to the graph when it opens, or when
the first routing state arrives, up to the screen's usable area. Larger layouts
scroll horizontally and vertically. Resizing the window manually turns off automatic sizing.

<a name="apps"></a>
### 3.3 Put an application on a different channel

The APPLICATIONS card lists every app that is currently registered with
PipeWire as an audio client; a green light means it is playing.

An app's playback streams and audio client share the same identity even
when PipeWire puts the process name only on the client. Windows executable
names such as `Balatro.exe` use the same normalized key as their Wine or
Proton client, so the app keeps one entry and one channel assignment.
When older settings contain both that key and a stale executable-name
alias, the existing normalized assignment takes precedence.

1. Change the channel in the dropdown next to the app. The move happens
   immediately and is remembered for that app. The channels also appear
   as playback devices in your desktop's audio applet (KDE's, for one),
   so you can see there which OpenXLR channel an app is playing into;
   the hardware inputs (XLR 1, XLR 2, Aux In) are deliberately not
   listed, nothing should play into a microphone's channel.
2. To pre-assign an app that has not played yet, open Manage…, pick it
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
   A running app re-registers on the next sweep and is routed by the
   rules again; use "Not managed" for a lasting opt-out.

An app that is missing from the card is not registered with PipeWire
as a client. That happens with some applications until they start
playing.

<a name="usb-aux"></a>
### 3.4 Feed a second computer over USB Aux (Wave XLR Pro)

1. Connect the second computer to the Pro's USB Aux port. It sees the
   Pro as a plain USB audio device.
2. In the SUBMIXER card, set the sends into the Aux mix: typically your
   microphone and the game, without the second computer's own chat.
3. Tick "To USB Aux port" on the Aux mix. The interface's audio stream
   restarts once, which interrupts playback for a moment; the device
   only picks up the new routing at stream start.

The USB Aux *input* (what the second computer sends back) is the Aux In
channel, with its level and lock in the INPUTS card.

<a name="plugins"></a>
### 3.5 Add a plugin to the signal path

1. Under XLR 1, XLR 2 or a mix, press "Add plugin…". The picker lists
   compatible installed LV2, CLAP and VST3 effects (mono for an input,
   stereo for a mix), searchable by name, category or format. Host
   feature requirements can exclude a plugin; install a compatible set
   such as `lsp-plugins-lv2` if the list is empty.
2. Add. The plugin appears in the Inserts row with a green light while
   active.
3. Controls opens a window generated from the plugin's parameters,
   grouped, with a Defaults button. Bypass takes it out of the path
   (red light); the arrows reorder the chain; the cross removes it.
4. Chains and exposed parameter values are saved with the mixer and
   profiles. OpenXLR does not yet save opaque plugin state, sample-file
   selections or plugin presets.

The generated controls cover the parameters in the bounded catalogue. A
plugin's own editor can be opened as well, with the native host described
in 3.12. CLAP and VST3 plugins appear in the same picker and always run in
that host. VST2 plugins cannot be loaded.

<a name="install-plugins"></a>
**Installing plugins.** The quickest set comes from your distribution:
on Arch, `lsp-plugins-lv2` and `x42-plugins` cover the microphone path
well. Choose LV2, CLAP or VST3 packages available for your distribution;
the picker offers only plugins compatible with the selected slot.
For a plugin you downloaded, press "Install file…" or "Install folder…" in the picker or in Options and pick it; OpenXLR puts it where
it looks and the picker lists it a moment later. A file is a `.clap` or a
single-file `.vst3`; a folder is a `.vst3` or `.lv2` bundle, or a folder
holding several of them, such as an extracted download. Linux plugins are
copied into `~/.clap`, `~/.vst3` or `~/.lv2`, so the download can go
afterwards. An archive has to be extracted first. Plugins installed by
other means, or copied into `/usr/lib/clap`, `/usr/lib/vst3` or
`/usr/lib/lv2` by a package, appear after "Rescan" in Options or a daemon
restart (`LV2_PATH`, `CLAP_PATH` and `VST3_PATH` override the places
searched).

<a name="windows-plugins"></a>
**Windows VST3 and CLAP plugins.** They run through
[yabridge](https://github.com/robbert-vdh/yabridge), which wraps them as
Linux bundles.

The **openxlr-yabridge** package supplies a tested bridge for 64-bit
Windows VST3 and CLAP plugins, built from a pinned source with the Wine
editor input fix. Install it from the same place you installed OpenXLR.
Wine comes with it as a dependency.

```sh
# Arch, from the AUR
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
yourself.

Restart the daemon with `systemctl --user restart openxlr-daemon` so it
finds the companion; this briefly interrupts audio. Options then
identifies the selected bridge as "OpenXLR bridge". Use "Bridge Wine's
plugins" or pick a Windows plugin folder to create private wrappers.

Without the companion, a distribution yabridge works only with Wine older
than 9.22. From 9.22 an embedded plugin window never learns where it is,
so every click lands as far from the pointer as the window is from the
corner of the screen, and the plugin ignores the mouse entirely. Options
says so when it sees that pair of versions.

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
| Install folder… | An installer that wrote outside Wine's usual folders. Press Ctrl+H in the dialog to see `~/.wine`, which is hidden |
| Rescan | Plugins that arrived by other means, such as a package from your distribution. Nothing to press after a bridge or a sync, since both read the catalogues again |

Bridging and syncing end the same way: yabridge wraps each Windows plugin
in the selected bridge's directories: private `vst3`/`clap` directories
for the companion, normally `~/.vst3/yabridge` or `~/.clap/yabridge` for
a system bridge. OpenXLR reads its catalogues again,
and the plugin is in the picker with a VST3 or CLAP badge. Nothing else
has to be restarted.

The first press is the one that needs explaining. Wine keeps its Windows
drive in `~/.wine` unless `WINEPREFIX` says otherwise, and an installer
writes into `Program Files/Common Files/VST3` inside it, or the same for
CLAP. That is a dotted folder, which file dialogs hide, so OpenXLR looks
there itself and offers what it finds as "Bridge Wine's plugins" rather
than asking you to go and find it. Once a folder is bridged, yabridge keeps
it on its own list, so a second plugin installed into it needs only the
sync, and the Bridge button has nothing left to offer.

A plugin that comes as a bare Windows `.vst3` or `.clap` file, with no
installer, is picked with "Install file…". Put it in a folder of its own
first: OpenXLR bridges the folder a Windows plugin sits in, and a file
picked straight out of Downloads would hand yabridge your whole Downloads
folder. VST2 `.dll` files are left out either way, since OpenXLR cannot
load VST2.

<a name="windows-editor-input"></a>
**A Windows plugin's own editor ignores the mouse.** The plugin plays, its
interface is drawn and it follows anything you change from OpenXLR, but
clicking its knobs does nothing, wherever you click. This is a known fault
in the unpatched yabridge release with Wine 9.22 and newer. The OpenXLR
companion includes the input fix.

Wine changed how it tracks where a window is in 9.22. A plugin editor is
embedded in a window belonging to its host, and Wine never learns where
that window really is, so it keeps believing the window sits in the very
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

Group names alone do not grant a memory-lock limit; the distribution's
PAM and systemd policy determines it. On Arch, the realtime privileges
package provides the relevant group policy:

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

The picker marks each plugin with its format, since the same plugin often
ships in more than one, and its LV2, CLAP and VST3 buttons narrow the list
to one of them. When the catalogue grows past what the window can be sent,
the LV2 list stays whole and the other formats fill the remaining room,
with copies of a plugin already listed going last; a set installed in two
formats shows once rather than pushing anything out.

<a name="profiles"></a>
### 3.6 Save and recall a scene

1. Set everything the way you want it: hardware controls, sends,
   masters, monitor outputs, inserts.
2. Header, Profiles: type a name and press Save.
3. To recall: Profiles, then the name. To remove: the cross next to it.

Profiles belong to the interface they were saved with; another device
shows its own list. With the OpenDeck plugin a key can recall a
profile ([section 4](#stream-deck)).

**Recall on connect.** The "On connect" picker under the list names a
profile the daemon recalls by itself whenever the interface connects
fresh: at daemon start (so at login), after a replug or a power cycle,
and when you switch to it in the device picker. Use it to land on a
known scene at every login. The reconnect after a passing USB error
does not count, so the recall never undoes changes you made since.
Pick "(none)" to stop.

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

The Wave XLR Pro keeps its settings in its own memory, so there is no
clean state to record and whatever is set on Linux is what Wave Link
finds on Windows, and the other way round. For it the same button
writes OpenXLR's baseline instead: gain 30 dB on both inputs, every
processing stage and phantom power off, headphones and aux level at
half, the crossfade fully on PC. Output routing and saved profiles
stay. The gain lock has to be off.

<a name="default-devices"></a>
### 3.7 Hold the system default devices

Session managers like to switch the system default output to a newly
appeared device, and some applications follow that default. In Options,
SYSTEM DEFAULT DEVICES, choose the output and input OpenXLR should
hold; it re-asserts them once a second and reverts any outside change.
"(don't enforce)" leaves the system alone.

<a name="hardware-only"></a>
### 3.8 Hardware control only

Options, Submixer: off. The daemon restarts in hardware-control mode:
the sound card keeps its stock PipeWire layout (on the Pro, its UCM
profile where one is installed), and the `OpenXLR …` devices, mixes,
virtual microphones and inserts go away. The INPUTS and HEADPHONES
cards keep working. Turn it on again the same way.

<a name="autostart"></a>
### 3.9 Start at login, tray

Options, STARTUP:

- "Start the daemon at login" enables the daemon's systemd user
  service. On a packaged install this is the package's own unit.
- "Start the mixer UI at login" adds an autostart entry for the window.
- With "Close button minimizes to tray", the window hides instead of
  quitting; the tray icon's menu shows it again or quits. "Start
  minimized to tray" starts with no window at all; the tray icon shows
  it the first time you click it.
- Only one window runs per user. Starting OpenXLR again, from the menu
  or a shell, brings the running window to the front (out of the tray
  if it is hidden there) instead of opening a second one.

To land on a known scene at every login, mark a profile to recall on
connect ([section 3.6](#profiles)). An interface using connect-time restoration comes back
as you left it without one.

The window also remembers which of its sections (INPUTS, HEADPHONES,
MONITOR, APPLICATIONS, SUBMIXER) you collapsed with the chevron in
their header, across restarts.

<a name="upgrade"></a>
### 3.10 Upgrade

Packages do not restart a running daemon. After an upgrade the window
shows a banner naming the daemon's version and its own, with a Restart
daemon button; press it, or run

```sh
systemctl --user restart openxlr-daemon
```

Until then the window offers only the controls the old daemon reports.
Toggling the submixer in Options also restarts the daemon.

Since 0.1.23 every client presents a token the daemon writes at start
([section 6](#files)). A window or OpenDeck plugin older than the daemon is
refused with "unauthorized" until it is updated too; the plugin zip on
the release page matches the daemon of that release.

<a name="layout"></a>
### 3.11 Edit the mixer layout

The default channels and mixes are a starting point. Edit layout… in the
SUBMIXER card opens the layout editor: application channels on the left,
mixes on the right, each with move up and down, Rename and Delete, and a
box at the bottom to add one. The hardware inputs, Monitor A, Monitor B
and Aux are listed but fixed.

- A new channel appears as a playback device at once and starts muted in
  every mix, so route an app to it and open the sends you want.
- A new virtual microphone receives nothing until you open a send; then
  pick it in OBS, Discord or any recorder like a microphone.
- Renaming a channel reloads its playback device under the new name.
  Apps playing into it keep playing after a short gap; nothing else is
  touched.
- Renaming a mix changes the name in OpenXLR and on the Stream Deck right
  away. Other applications keep listing the old microphone name until the
  daemon restarts, because reloading the device would throw them off it.
  The window shows a restart hint; restart when nothing is recording.
- Deleting a channel moves its apps to the first remaining application
  channel. Deleting a mix removes its virtual microphone, and anything
  recording from it loses the device.

Every change is saved before the editor confirms it. If the settings
file cannot be written the change is undone and the editor says why. The
same happens when pipewire-pulse has no room for more streams
([section 5.8](#open-files) explains the limit and the drop-in that raises
it), and when the new channel's or mix's sends have not appeared within
three seconds: nothing is kept that the daemon could not set.
Ids are generated from names and never change afterwards, so profiles
and Stream Deck keys survive a rename. The layout file is described in
[mixer-layout.md](mixer-layout.md).

<a name="plugin-editors"></a>
### 3.12 Open a plugin's own editor

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
recovers. A plugin that keeps crashing has its chain switched off after
it has failed three times in five minutes, with the reason on the insert;
changing or bypassing that chain starts it over.

<a name="stream-deck"></a>
## 4. Stream Deck (OpenDeck)

The plugin has two actions. Both are clients of the daemon and show
its live state, so what a key displays is what the mixer window shows.

**Toggle** (a key) switches one thing: a hardware control (mute,
phantom, low cut, expander, voice tune, ClipGuard, compressor, low
impedance, the Pro's output selectors, the aux level lock, the gain
lock), the software low cut (cycling Off, 80, 120), a mix or send mute,
the monitor output (switching the monitor mixes to one specific device),
an output's feed (cycling Monitor A, Monitor B and Monitor A+B, lit
when not on A),
the bypass of one insert or of a whole chain, or a profile to recall.
The key's LED is green for an engaged feature, red for a mute, and grey
when the daemon is offline or the target does not exist on the
connected interface. A key's icon can be chosen in its settings, and a
title typed there replaces the built-in label.

**Dial** (an encoder) changes a level: the monitor output volume, a
gain, a headphone volume, the aux level, the crossfade, a mix master, a
channel's send into one mix or into all mixes, or one control of an
insert. The touch strip shows a knob, a level meter, the value and a
mute overlay; pressing the dial mutes (or, for a gain, mutes the input;
for the crossfade, recentres). A dial can hold several targets, cycled
by tap or press as chosen in its settings.

Installing: the plugin zip from the release through OpenDeck's
install-from-file, or the folder the package ships in
`/usr/share/openxlr/` copied into `~/.config/opendeck/plugins/`
(copied, not linked; OpenDeck does not serve assets through a symlink).
Restart OpenDeck after installing or updating the plugin.

<a name="troubleshooting"></a>
## 5. Troubleshooting

<a name="no-device"></a>
### 5.1 "No device" in the header

- The interface must be replugged once after installing so the udev
  rule (`/usr/lib/udev/rules.d/70-openxlr.rules`) applies to it.
- `lsusb` should list an `0fd9:` device. If it does but the header
  still says no device, look at the daemon's log:
  `journalctl --user -u openxlr-daemon -n 50`. "present but could not
  be opened" can mean missing USB permission or a busy interface; check
  the udev rule and whether another hardware-control program is running.
- With more than one supported interface attached, the header shows a
  picker; the mixer's input channels follow the chosen one.

<a name="dock-silent"></a>
### 5.2 Microphone silent on the XLR Dock

The kernel starves the dock's capture when playback to it starts before
capture, and the microphone records silence. The package installs a
WirePlumber rule that keeps the dock's capture source always active
(`50-xlr-dock-capture-hold.conf`). On a source install copy it from
`packaging/` into `~/.config/wireplumber/wireplumber.conf.d/` and
restart WirePlumber.

A second cause, when the microphone is silent only after a reboot: the
dock forgets its gain at every power cycle and comes back at the gain its
firmware restores, which can differ from the gain used by your insert chain.
OpenXLR gives the
gain back when the dock connects, even when the gain lock is on. This
fix is on `main` after 0.1.29; on a build without it, take the lock off
and set the gain again. A gate or expander tuned at the gain you meant to have stays shut at a lower one and passes nothing at all, which
is what makes the microphone sound dead rather than quiet.

<a name="daemon-not-starting"></a>
### 5.3 Daemon does not start after an upgrade, or after a reboot

- `systemctl --user status openxlr-daemon` shows the state. "203/EXEC"
  in a restart loop means a stale unit in
  `~/.config/systemd/user/openxlr-daemon.service` written by a version
  before 0.1.9; opening the mixer window once repairs it, or remove the
  file, `systemctl --user daemon-reload`, then
  `systemctl --user enable --now openxlr-daemon`.
- "another OpenXLR daemon is already running for this user": a second
  daemon was started by hand while the service runs. It stops at once;
  stop the service first if you meant to run the daemon by hand.
- "port 37890 busy": another program holds the daemon's API port,
  which sits inside the kernel's ephemeral range. The daemon waits up
  to a minute for it and otherwise exits for systemd to retry; nothing
  needs to be configured.

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
- Before 0.1.27 an insert whose plugin URI contains a `#` (the x42
  plugins, for one: `darc#mono`) failed with "PipeWire filter chain did
  not create the required ports ... Could not load module", because
  PipeWire's argument parser reads the `#` as a comment. The daemon now
  escapes it; on an older version pick a plugin without one, such as the
  LSP set.

<a name="wrong-device"></a>
### 5.5 Sound comes out of the wrong device

The session manager switched the system default when a new device
appeared. Set the defaults in Options ([section 3.7](#default-devices)), or pick the device
you want in your desktop's sound settings once; the daemon defends the
defaults it saw at start only for the first seconds.

<a name="control-not-applied"></a>
### 5.6 A control changes in the window but not on the device

The daemon writes to the interface and reads the state back; if the
device ignores the write, the control snaps back. On the Wave XLR Pro
the mute button shows a countdown after every 48V change: the firmware
holds that input muted for about 13 seconds and unmutes it itself. On
other devices this would be new information: collect diagnostics
([section 5.10](#reporting)) and open an issue.

<a name="daemon-hang"></a>
### 5.7 The daemon froze, or a control hung the window

The header's **Restart daemon** button restarts the systemd user service.
Audio is interrupted during the restart. The window stays responsive, and
the button is disabled until the service command finishes. If it fails,
check `journalctl --user -u openxlr-daemon`. A daemon started by hand must
be restarted by hand.

Since 0.1.11 a USB transfer that never returns fails after a few
seconds instead of stalling the daemon; the device is dropped and
reconnected after 10 seconds, and the fault is recorded. The USB
library runs in a small helper process of its own (the daemon binary
started with `--usb-helper`), which is killed and started again on such
a hang, so nothing stays stuck inside the daemon. After three hangs in
one run the daemon stops driving that interface and says so under the
window's header, while the submixer and any other interface keep
working. Unplug the interface and plug it back in, or restart the
daemon, to try again. A helper whose device could not be opened at all
(the udev rule not applied yet, see [section 5.1](#no-device)) is
killed straight away and the daemon tries again two seconds later.
Collect diagnostics afterwards ([section 5.10](#reporting)): the archive contains the
exact transfer, and that is what makes the report actionable.

<a name="open-files"></a>
### 5.8 Channels or mixes vanish after adding one

pipewire-pulse, PipeWire's PulseAudio server, inherits systemd's default
limit of 1024 open files. OpenXLR's send faders are streams inside that
server, so a layout with a few channels or mixes beyond the default
reaches the limit, and past it the server drops nodes at random: sinks
disappear, apps fall back to the default output, and the window shows
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
volume; nothing in OpenXLR uses a sink's own volume as a control, so
that only cuts audio. Since 0.1.27 the daemon puts every OpenXLR sink
back to full volume on its sweep and logs when it had to. On an older
version, set them by hand:

```sh
for s in $(pactl list sinks short | awk '/OpenXLR_/ {print $2}'); do pactl set-sink-volume "$s" 100%; done
```

<a name="reporting"></a>
### 5.10 Reporting a problem

Ask on the OpenXLR Discord server (<https://discord.gg/4bswtnGPW4>,
one post per problem in its support forum), on Reddit at
<https://www.reddit.com/r/OpenXLR/>, or open a GitHub issue; whichever
you pick, attach the diagnostics archive described below.

Options, SUPPORT, Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the daemon's state and
capabilities, a dump of the interface's vendor blocks, the PipeWire
graph and device listings, the recent daemon journal, the
configuration files and version information. The home path, host name
and the serial numbers of attached USB devices are redacted, in the
text files and inside the hex dump of the vendor blocks (the XLR Dock
stores its serial in one); review the archive anyway before attaching
it to a public issue. Nothing is uploaded automatically.

<a name="files"></a>
## 6. Files and services

| Path | What it is |
|---|---|
| `~/.config/openxlr/mixer.json` | every mixer decision, the layout included (`userChannels`, `userMixes`, see [mixer-layout.md](mixer-layout.md)), written by the daemon |
| `~/.config/openxlr/profiles/<vid-pid>/<name>.json` | saved profiles, one file each |
| `~/.config/openxlr/profiles/<vid-pid>/recall-on-connect` | the profile recalled when that interface connects, when one is chosen |
| `$XDG_RUNTIME_DIR/openxlr/token` (or `~/.config/openxlr/token` without a runtime directory) | the control API token for this daemon run, readable by your user only; the window and the OpenDeck plugin read it, a daemon older than the window will not have it ([section 3.10](#upgrade)) |
| `$XDG_RUNTIME_DIR/openxlr/daemon.lock` | held by the running daemon; a second daemon started for the same user stops at once instead of waiting for the port |
| `~/.config/openxlr/devices/<vid-pid>/last-state.json` | the settings restored on connect when `retainsSettings` is false |
| `~/.config/openxlr/devices/<vid-pid>/defaults.json` | the firmware defaults of such an interface, recorded after a power cycle, written back by "Reset device to defaults" (the Pro has no such file: its reset writes OpenXLR's baseline) |
| `~/.config/openxlr/daemon.json` | the submixer on/off preference |
| `~/.config/openxlr/gainlock.json` | which devices have the gain lock set |
| `~/.config/openxlr/bridge/yabridgectl/config.toml` | companion bridge folder registry, separate from the system bridge |
| `~/.local/share/openxlr/yabridge/{vst3,clap,vst2}` | companion-generated wrappers; OpenXLR loads VST3 and CLAP only |
| `~/.config/openxlr/ui.json` | window preferences |
| `openxlr-daemon.service` (systemd user unit) | the daemon; `journalctl --user -u openxlr-daemon` for its log |
| `/usr/lib/systemd/user/pipewire-pulse.service.d/openxlr.conf` | installed by the packages: raises pipewire-pulse's open-file limit ([section 5.8](#open-files)) |
| `ws://127.0.0.1:37890/ws` | the daemon's API, documented in [api.md](api.md); the same commands over HTTP at `/api/v1` ([http-api.md](http-api.md)) |

Configuration paths honor `XDG_CONFIG_HOME`; the private wrapper root honors
`XDG_DATA_HOME`. Without `XDG_RUNTIME_DIR`, runtime files use the private
OpenXLR configuration directory.

Uninstalling a package leaves `~/.config/openxlr` in place; remove it
by hand if you want a clean slate.
