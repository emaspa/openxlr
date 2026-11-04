# Omarchy shell plugin

`openxlr.mixer` puts the mixer in the Omarchy 4 bar. It is a client of the
existing daemon, so the UI and terminal mixer can remain open and see
the same changes. The plugin source is in
`packaging/omarchy/openxlr.mixer/`, beside the other desktop package inputs.

## The bar and mixer

The bar uses Omarchy's body font, regular weight and bar text colour, like
the clock. It shows at most four microphones with signal, in layout order,
including mono capture inputs. Input labels are the daemon's channel names:
`XLR 1` and `XLR 2` on an XLR jack, `Mic 1` and `Mic 2` on a
Wave:3's capsule or on another microphone a strip is bound to, and the saved
name of a capture input. Stereo capture inputs stay out of the bar. An older
daemon without `microphone` keeps the XLR-only rule. An input's
label and meter dim to 45% opacity while the mute of the interface, or
of the driven additional interface, whose input the strip carries applies
to it; their
weight and size stay the same.
Glyph cells and labels share the font's line height and vertical centre.

An input appears when its RMS level has been above the -60 dBFS floor at
any point in the last 10 seconds. This is signal detection, not cable
detection; the daemon does not report whether an XLR cable is plugged in.
Each microphone keeps its last above-floor time. One timer waits for the
next expiry, including microphones waiting behind the first four.
Muting does not clear that history. A muted input stays until the same
10-second quiet period has elapsed. A shown input keeps a fixed width as
its level changes; appearing or disappearing inputs resize the widget
over 180 ms. With no qualifying inputs, only the monitor feed remains.

The first selected monitor output's feed has its own meter. `Monitor A`
and `Monitor B` read as `Mon A` and `Mon B`; other names are kept. A sum
lists its mix names, and `+1` after the feed means another output is
selected. The single meter lane takes the louder left or right reading;
for a sum it shows the loudest constituent mix, not an estimated sum.
Hovering shows the input levels and every selected output's feed. Output
labels use the daemon's device description when it has one and the node
name otherwise. `No output` means none is selected, and `Silent` is an
empty feed. Long names shorten to fit the bar without moving neighbouring
widgets. The same strip turns with a vertical bar.

The popout and the bar show only the channels the active device can feed,
which the daemon says per channel, so XLR 2 and Aux In are absent on every
model but the Wave XLR Pro.

Click to open the mixer. The arrows choose which mix the channel sends
control. Channels remain in layout order, with a separate bank of mix
masters. Each bank fits only whole strips and has previous and next page
buttons with a visible range, such as `1-4/9`. The arrows above the banks
still choose the send mix. Every strip has its name, kind,
percentage, vertical fader, RMS meter and bracketed mute key. Microphone
inputs, including mono capture inputs, have one wide meter. Other channels
and masters have separate left and right bars. Channel send faders reach
100%; monitor masters reach 150% and other masters reach 100%.

The popout uses Omarchy's first-party `KeyboardPanel`, as its audio panel
does. It fits the card to the screen and clamps its position inside the
screen margins on every bar edge. Banks stack when they cannot fit side
by side; paging never leaves part of a neighbouring strip visible.

The bracketed key mutes that send or master. The channel that carries one
of the interface's inputs also has a `Mic on` or `Mic muted` button for
that input's hardware mute, which affects every destination. Those are
separate controls. The daemon names that channel in each channel's
`unitInput`. It is XLR 1 or XLR 2 while the strip takes the interface, or a
capture channel on the interface's own input while the strip is bound to
another microphone. A strip or capture channel carrying an additional
interface that OpenXLR drives, such as a Wave:3, has that unit's button,
which sends `setWaveControl`; it stays disabled while the unit is unplugged.
A daemon that does not send the field leaves the button on
XLR 1 and XLR 2. Capture carriers keep their mute target when the unit
renames its node: the daemon resolves the saved `captureUnit` and publishes
the carrier in `unitInput`. A strip on a source without unit controls has
no hardware mute button, and its bracketed send mutes work as usual. Its
kind line shows the source's description in place of `INPUT`, with `OFF` while the source is silent.
The tooltip adds the source's description and port to that input's line,
or the daemon's sentence saying why it is silent. A binding to a supported
interface follows that unit's unique capture node when a card profile change
renames it. If several sources match, the saved node name remains on display.
A fader sends its value on
release; focused faders also take the arrow keys in one-percentage-point
steps. Controls wait for the daemon's reply before accepting another edit.
A rejected edit leaves the daemon's value on screen and shows its error.
Escape, Close, clicking outside or opening another bar popout closes it.

The terminal mixer's geometry supplies the blocks, paired meters,
monospace figures, fader caps and bracketed keys. Bar meters stay monochrome,
shading the bar's foreground against its background with warning and hot
anchors at 0.7 and 0.9 on the daemon's -60 to 0 dBFS scale. The bar uses
`barForeground`; the popout retains the bar API's `foreground`. A transparent
bar's RGB background becomes opaque inside the popout.

Popout meters use the OpenXLR skin whose id matches the active Omarchy
theme. The plugin watches
`$HOME/.local/state/omarchy/current/theme.name`, so a theme switch repaints
the open mixer without a shell restart. Eleven Omarchy themes have a
matching skin. `Skins.js` also carries Deck, Broadcast, Nyan Pink and
Wave, which no Omarchy theme is named after; their rows are meter colours
only, and the Broadcast and Wave layouts never reach the plugin. An unknown theme, a missing file or an unreadable file uses
the popout foreground shades instead. The old Omarchy 3 configuration
symlink is not used.

Each lit cell takes the colour for its own position on the scale: fill
below the warning threshold, warning up to the hot threshold, hot above
it. An unlit cell takes the track colour. Levels change the glyphs that
are lit, not the colour zones. The bar meters never take these skin colours,
and the plugin does not change the UI's selected skin.

The skins are embedded in OpenXLR's UI assembly, so the plugin carries
only their six meter tokens in `Skins.js`. Regenerate it after editing an
embedded skin with `python3 tools/omarchy-skins.py`.
`python3 tools/omarchy-skins.py --check` checks for drift without writing;
`OmarchyPluginTests` independently regenerates the table in memory, using
the application's defaults for unset thresholds.

## Install and enable

This needs Omarchy 4's plugin host, a running OpenXLR daemon in the same
user session, and Arch's `qt6-websockets` package. It does not need the
OpenXLR UI. The Arch package installs the plugin under
`/usr/share/openxlr/omarchy/openxlr.mixer/` and the enable command under
`/usr/bin/` when it is built on an Omarchy host. Then run inside your
Omarchy session:

```sh
openxlr-omarchy-enable
```

The command validates the system copy, links it at
`~/.config/omarchy/plugins/openxlr.mixer`, asks `omarchy-shell shell
rescanPlugins` to discover it, then runs `omarchy plugin enable
openxlr.mixer`. That rescan answers before the shell has rebuilt the
registry the enable reads, so the command retries the enable for up to ten
seconds rather than reporting the plugin as unknown. Omarchy writes its own `~/.config/omarchy/shell.json` and
places the widget on the right. The command can be run again; it refuses
to overwrite an existing folder or a link to a different plugin.
Omarchy uses `~/.config/omarchy` even when `XDG_CONFIG_HOME` differs.

```sh
omarchy bar move openxlr.mixer left
omarchy plugin disable openxlr.mixer
omarchy plugin enable openxlr.mixer
omarchy plugin remove openxlr.mixer
```

Removal disables the plugin and unlinks its directory. It leaves the
system copy for the package manager. A package upgrade replaces that
copy; run `omarchy-shell shell rescanPlugins` after upgrading to reload it.
The daemon and the UI keep the code they started with until they are
restarted, which the README's upgrading section covers.
The plugin is package-managed, so `omarchy plugin update` does not update it.
No package hook writes to a user's home or restarts a service.

For source testing, from the repository root:

```sh
bash packaging/omarchy/openxlr-omarchy-enable "$PWD/packaging/omarchy/openxlr.mixer"
```

That links the checkout instead. Remove that link with Omarchy's remove
command before enabling the packaged copy.

## Arch package recipe

The AUR `PKGBUILD` is maintained outside this repository and carries
these lines after its dependency arrays. Omarchy 4 packages live under
`/usr/share/omarchy`; `omarchy-dev` supplies the same shell. This tests the
build host for that shell rather than treating every Arch machine as Omarchy.

```bash
_openxlr_omarchy=0
if [[ -f /usr/share/omarchy/shell/Ui/PluginBarApi.qml ]]; then
    _openxlr_omarchy=1
    depends+=(qt6-websockets)
fi
```

Add these lines inside `package()`, after it changes into the OpenXLR
source root:

```bash
chmod 755 "$pkgdir/usr/lib/openxlr/daemon/openxlr-control"
ln -s ../lib/openxlr/daemon/openxlr-control "$pkgdir/usr/bin/openxlr-control"
if (( _openxlr_omarchy )); then
    install -d "$pkgdir/usr/share/openxlr/omarchy/openxlr.mixer"
    install -m644 packaging/omarchy/openxlr.mixer/{manifest.json,*.qml,*.js} \
        "$pkgdir/usr/share/openxlr/omarchy/openxlr.mixer/"
    install -Dm755 packaging/omarchy/openxlr-omarchy-enable \
        "$pkgdir/usr/bin/openxlr-omarchy-enable"
fi
```

A package built on Omarchy differs from one built elsewhere, on Arch
Linux, on CachyOS or in a clean Arch build chroot: only the Omarchy build
carries the plugin and the command. Omarchy is Arch only, so the Debian,
RPM and Nix packages do not install it. The manifest's version is the
plugin's own and is not tied to the application version.

## Connection and limits

The plugin runs `openxlr-control` from the installed native
package. This client-only adapter connects to the private Unix socket,
checks the server's user and forwards JSON lines. It does not start an
audio service. The plugin sends `getState` after the adapter is connected;
state changes and meter frames (15 Hz while levels change) arrive without a
subscription command.
Commands are documented in [api.md](api.md). There is no token file, HTTP
polling or meter polling loop.

Older packages lack this launcher and must be upgraded with the plugin.
For a source build, [link it onto PATH](install-from-source.md#6-make-it-permanent).

An unavailable daemon leaves a dim `XLR off` indicator, empty meters and
disabled controls. Connection attempts wait 1, 2, 4, 8, then at most 10
seconds apart. Each attempt starts a fresh adapter. Connection and the
initial state have a shared
five-second deadline. Meter frames expire after one second of silence.
An edit with no reply after five seconds reconnects; it is never replayed,
since the daemon may already have applied it. Unloading the widget closes
its socket and cancels its timers. Each bar instance owns one connection.

This popout does not edit routing, output selection, layouts, profiles,
inserts, gain or phantom power. Use the UI or `openxlr-tui` for those.
It does not start the daemon, change login settings or hide the UI's
tray icon. It does not copy the terminal's peak holds or level history.
Its popout meters follow the Omarchy theme, so selecting a different skin
in the OpenXLR UI does not recolour them.

## Checks and desktop acceptance

`OmarchyPluginTests` checks the package against the rules in Omarchy's
`bin/omarchy-plugin-validate`, including safe entry paths and no internal
symlinks, and tests enablement with isolated homes and fake Omarchy commands.
`node --test packaging/omarchy/tests/*.test.mjs` exercises the same protocol
state machine and meter calculations QML uses, with simulated socket and
timer events. It never contacts the user's daemon.

`QT_QPA_PLATFORM=offscreen /usr/lib/qt6/bin/qmltestrunner -input
packaging/omarchy/tests` exercises the actual strip controls with a fake
daemon object: mouse release, keyboard steps, external state updates,
send and hardware mute, disabled controls, layout changes during a drag and
the monitor volume ceiling. Bank tests cover fractional widths, every page,
resizing and removing channels while the last page is selected.
Bar tests check label and meter alignment, regular weight, monitor feeds,
mute dimming and a real 10-second input expiry. Meter tests check colour
zones, track colour and palette changes with the fallback restored.
It loads no bar or Quickshell window.

`python3 tools/check-omarchy.py` parses every QML file and runs qmllint with
no warnings allowed. Its small external type declarations let this run
without Quickshell; Qt Quick, Controls and WebSockets use real Qt modules.
This does not load a shell or test the external components. Bare qmllint
on a machine without Quickshell also reports unresolved imports and the
type errors caused by those missing modules.

Before merging, check on an Omarchy machine:

- Enable and remove the package link through the real CLI. Confirm the
  bar placement persists and the system copy survives removal.
- Open, close and switch popouts, click outside, press Escape, and use
  both banks on a small screen. Check top, bottom and vertical bars,
  multiple monitors, a light theme, a dark theme and a transparent bar.
- Compare the bar text with the clock, then send signal into each XLR
  input and let it fall quiet for 10 seconds. Mute within that window and
  check the dimming. Change to a matching and an unmatched Omarchy theme
  with the popout open; only its meters should take the skin colours.
- Check XLR 1 and 2 hardware mute, send mute, independent stereo levels,
  custom channel names, monitor sums and different feeds on two outputs.
  Compare edits with the UI and terminal, including 150% monitor gain.
- Bind XLR 1 to another microphone from the UI and keep the
  interface's microphone as a capture channel. The bar label reads
  `Mic 1`, the interface's mute leaves it undimmed, its popout strip names
  the source and has no mic button, and the capture channel's mic button
  mutes the interface. Unplug the source and check `OFF` and the tooltip.
- Drag and release a fader, change it with keys, and change the same value
  from another client. Check rejected commands and rapid edits.
- With an idle test daemon, stop and restart it, and
  reload or disable the widget. Confirm quiet disconnects, fresh state,
  no replayed edits, no leftover sockets and no accumulating timers.

Loading, the bar text against the clock, the input hold, the monitor
meter, the popout with its paged strips and the Tokyo Night meter colours
have been checked on Omarchy 4.0.4 with a live daemon and an XLR Dock. The
live theme switch and the hardware controls still need desktop acceptance.
