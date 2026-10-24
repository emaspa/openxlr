# Flatpak

Flatpak support is experimental. On distributions with an OpenXLR
package, that package remains the recommended installation. The Flatpak
is for systems without one, such as SteamOS and Bazzite, and each
release carries it as `openxlr-<version>-x86_64.flatpak` with a checksum
file. It is not on Flathub, whose rules do not allow AI-assisted
development.

The Flatpak UI owns its daemon unless the daemon runs as a user service
(see [Audio in Game Mode](#audio-in-game-mode)): closing to the tray
keeps audio running; Quit stops both. For that reason the close button
keeps the Flatpak in the tray unless "When closing" in Options says
"Quit app". Stop the packaged daemon before launching the Flatpak, since
they use the same control port. Disable the native installation's login
options before enabling Flatpak login startup.

## Install on SteamOS and Bazzite

Do this in Desktop Mode, in a terminal (Konsole on SteamOS and on
Bazzite's KDE images). Some steps use `sudo`. On SteamOS, set a password
for the `deck` user first with `passwd` if you never have.

1. Download `openxlr-<version>-x86_64.flatpak` and `SHA256SUMS-flatpak.txt`
   from the [latest release](https://github.com/emaspa/openxlr/releases/latest)
   into the same folder, go to that folder, and check the download:

   ```sh
   sha256sum -c SHA256SUMS-flatpak.txt
   ```

2. Install it for your user. Flatpak fetches the Freedesktop runtime from
   Flathub, so the first line makes Flathub available to your user:

   ```sh
   flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
   flatpak install --user ./openxlr-*-x86_64.flatpak
   ```

3. Let your user reach the interface's USB controls, then unplug the
   interface and plug it back in:

   ```sh
   sudo install -m 644 "$(flatpak info --show-location io.github.emaspa.openxlr)/files/share/openxlr/70-openxlr.rules" /etc/udev/rules.d/70-openxlr.rules
   sudo udevadm control --reload-rules
   ```

4. Start OpenXLR from the application menu and choose Set up in the first
   dialog. Log out and back in, or restart, so the WirePlumber rules it
   added take effect.

5. To keep the mixer running in Game Mode, follow
   [Audio in Game Mode](#audio-in-game-mode).

LV2 plugins come from Flathub's LinuxAudio extensions, for example
`flatpak install --user flathub org.freedesktop.LinuxAudio.Plugins.LSP//25.08`.
To update OpenXLR, download the newer file and run
`flatpak install --user --reinstall ./openxlr-*-x86_64.flatpak`. To remove
it, run the service script's `remove` if you used it, then
`flatpak uninstall --user io.github.emaspa.openxlr`, and delete
`/etc/udev/rules.d/70-openxlr.rules` and the four rules in
`~/.config/wireplumber/wireplumber.conf.d`: `50-xlr-dock-capture-hold.conf`,
`51-openxlr-pro-raw-names.conf`, `52-openxlr-mk1-capture-hold.conf` and
`53-openxlr-wave3-capture-hold.conf`.

On GNOME the tray icon needs an AppIndicator extension enabled. Without
a tray, set "When closing" to "Quit app" in Options.

## Audio in Game Mode

SteamOS's Game Mode does not run desktop autostart entries, and
switching modes ends the Desktop Mode session together with the UI and
the daemon it started. Bazzite's Game Mode is expected to behave the same
way; it has not been checked. To keep the mixer running in both modes, run
the daemon as a systemd user service. Quit OpenXLR from its tray menu,
then run on the host:

```sh
sh "$(flatpak info --show-location io.github.emaspa.openxlr)/files/share/openxlr/openxlr-flatpak-service"
```

The script writes `~/.config/systemd/user/openxlr-flatpak.service`, which
runs the Flatpak's daemon without the UI, and enables it. It refuses to
replace a unit it did not write, or to run beside an enabled packaged
`openxlr-daemon.service`. Run it again with `remove` to undo it.

With the service running, the UI finds the daemon already there and
connects to it instead of starting its own. Quit then closes only the UI;
audio keeps running. Restart daemon asks the daemon to exit with code 75,
and systemd starts it again. The login options in Options start only the
UI, and they say so.

For the mixer itself in Game Mode, add OpenXLR to Steam in Desktop Mode
with Add a Game, Add a Non-Steam Game. It then opens from the Steam
library in Game Mode. Game Mode has no tray, so closing the UI there
quits it, and Steam sees it end.

On SteamOS 3.8 the service kept the same daemon running across switches
between Desktop Mode and Game Mode. Whether Steam in Game Mode sends game
audio through OpenXLR's channels or straight to the interface has not
been checked yet.

## Startup and logs

The login settings use the desktop background portal. Start audio in the
background launches in the tray. Start the OpenXLR app shows the UI,
unless Tray only is selected under On launch. A refused request is reported
without saving it. If autostart is granted but background activity is not,
the login choice is saved with a warning so the entry can still be disabled.
Desktop settings can revoke permission later.

Restart daemon restarts the child without touching host services, or
asks the user service's daemon to restart when one serves the UI. Failed
children are retried with a delay; a busy API port or owned daemon lock
ends the attempt. Restart interrupts a pending retry delay. A parent-process wait also stops the daemon if the
UI crashes. The tray uses the app's D-Bus namespace, including when
the desktop tray host restarts. There is no systemd watchdog. Private `daemon.log`
under `$XDG_STATE_HOME/openxlr` has one rotated copy. Collect
diagnostics includes both instead of the journal.
Existing log paths must be regular files, so a leftover pipe cannot block
the daemon. Diagnostic copies use the same privacy filtering as native logs.
Log links are omitted, and credential-shaped fields are masked even when
their values are not known in advance.
Tray menu queries and event batches have a fixed item limit, so an invalid
desktop request cannot create an unbounded list of menu items.
Tray hosts can request the complete menu with an empty item list.

## Host setup

The first launch explains this setup and offers Set up. Set up installs
the four WirePlumber rules in the host user's
`~/.config/wireplumber/wireplumber.conf.d`, readable like the packaged
copies, and leaves that directory's permissions as they were. Cancel
writes nothing and asks again at the next launch. Identical files
are kept. Different files and symbolic links are reported and never
overwritten. Configuration directory links are refused too. These include the capture-hold rule that prevents a silent
Wave:3 microphone after playback starts. The rules take effect at the next login. For a custom host
`XDG_CONFIG_HOME`, grant its `wireplumber/wireplumber.conf.d` directory and set
`OPENXLR_HOST_CONFIG_HOME` to that host `XDG_CONFIG_HOME` with a Flatpak override. App
preferences and profiles stay in Flatpak's private XDG directories.

USB controls require the host udev rule. Run outside the sandbox, then
reconnect the interface:

```sh
sudo install -m 644 "$(flatpak info --show-location io.github.emaspa.openxlr)/files/share/openxlr/70-openxlr.rules" /etc/udev/rules.d/70-openxlr.rules
sudo udevadm control --reload-rules
```

The optional [Pro UCM profile](../packaging/ucm/README.md) remains a manual
host step. Flatpak cannot raise the host pipewire-pulse open-file limit or
grant unlimited memory locking. Large layouts and bridged Windows plugins
remain better suited to distro packages. Removing the app leaves manually
installed host files in place. Remove only the exact files you installed,
after checking they have not been modified.

## SteamOS and Bazzite notes

The Flatpak was checked on SteamOS 3.8 in Desktop Mode with an XLR Dock
MK.2: the device connects, the mixer graph builds, the monitor mix
reaches the Dock's output, and USB controls answer with the udev rule in
`/etc/udev/rules.d`, which SteamOS keeps across updates. Closing to the
tray, Quit from the tray menu and Start audio in the background were
checked too, the last across a reboot. Bazzite has not been tested yet;
it is Fedora Atomic underneath, where `/etc` is writable, so the same
steps apply.

SteamOS puts a loopback in front of each hardware output and shows
PulseAudio clients only the loopback. OpenXLR lists the loopback under the
device's name, and that is the one to pick as a monitor output. A fresh
installation starts with the monitor not routed, so pick an output first.

## Plugins

LV2 uses Flathub LinuxAudio plugin extensions at
`/app/extensions/Plugins/lv2`. The catalogue and both hosts share this path.
Host plugin folders and yabridge are not exposed. Native editors need X11
or XWayland. The native helper retains CLAP and VST3 support for compatible
bundles imported into the app's private storage, provided their external
dependencies are available in the sandbox.
Imports under `~/.lv2`, `~/.clap` and `~/.vst3` are private persistent
Flatpak mappings. They never replace bundles in the host's folders.

Desktop-specific helper features, such as Plasma's shared maximum-volume
preference, require helpers not included in the Freedesktop runtime. The
normal mixer volume controls still work. The installation doctor does not
yet inventory the manual Flatpak host setup; use the exact paths above.
