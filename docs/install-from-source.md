# Installing from source

## Requirements

- Linux with PipeWire, `pipewire-pulse` and WirePlumber; `pactl`,
  `pw-cli`, `pw-link`, `pw-dump`, `pw-loopback`, `pw-metadata`, `parec`,
  `wpctl` and `amixer` on PATH
- `gdbus` (the GLib command-line tools) for focused application routing
  from desktop and Deck keys, and `xprop` for it on X11 sessions; GNOME
  on Wayland also needs the [OpenXLR Focus extension](gnome-focus.md);
  `xdg-open` for the links the UI opens
- gdk-pixbuf with the librsvg loader for the applications' own desktop
  icons; without them the UI falls back to PNG icons
- `swh-plugins` (LADSPA) for the software ClipGuard; everything else
  works without it
- `lilv` for LV2 discovery and compatible LV2, CLAP or VST3 plugins
- .NET 10 SDK to build; ASP.NET Core 10 runtime for a framework-dependent
  installation (the base .NET runtime alone does not run the daemon)
- C/C++ compiler, make, pkg-config, PipeWire/lilv/LV2/X11/libsndfile
  headers for the native plugin host; X11 or XWayland for plugin editors
- Rust (cargo and rustc 1.67 or newer) and curl for DeepFilterNet, one of
  the microphone's two noise removal engines. Without cargo the helper is
  built with RNNoise alone; see [noise removal engines](#noise-removal-engines)
- libusb 1.0
- Optionally, a supported Elgato device for hardware control (see the
  device table in the README). The submixer works with any capture source
  PipeWire offers, and the aux and output routing features follow the
  device's capabilities

All commands below run from the repository root unless stated otherwise.
The branch describes current development; check out a release tag before
building if you want exactly that release.

## 1. Prerequisites

The .NET 10 SDK, ASP.NET Core runtime, audio tools and native build headers.
Run only the commands for your distribution. Availability depends on its
release and enabled repositories; the .NET SDK must be version 10.
The native editor tests additionally use Xvfb and xauth.

```sh
# Arch Linux, CachyOS and Omarchy
sudo pacman -S --needed dotnet-sdk aspnet-runtime pipewire pipewire-pulse wireplumber libusb libpulse alsa-utils glib2 xdg-utils gdk-pixbuf2 librsvg xorg-xprop
# optional: software ClipGuard for the XLR Dock, and LV2 plugins for inserts
sudo pacman -S --needed swh-plugins lilv lsp-plugins-lv2
# native host and editor test dependencies (rust, or rustup with a toolchain)
sudo pacman -S --needed base-devel lv2 libx11 libsndfile libxtst xorg-server-xvfb xorg-xauth rust

# Fedora
sudo dnf install dotnet-sdk-10.0 aspnetcore-runtime-10.0 pipewire pipewire-pulseaudio wireplumber libusb1 pulseaudio-utils alsa-utils glib2 xdg-utils gdk-pixbuf2 librsvg2 xprop ladspa-swh-plugins lilv-libs lsp-plugins-lv2
sudo dnf install gcc-c++ make pkgconf-pkg-config pipewire-devel lilv-devel lv2-devel libX11-devel libsndfile-devel libXtst xorg-x11-server-Xvfb xorg-x11-xauth cargo rust

# Debian / Ubuntu (dotnet from Microsoft's feed if the distro lacks 10.0)
sudo apt install dotnet-sdk-10.0 aspnetcore-runtime-10.0 pipewire pipewire-pulse wireplumber libusb-1.0-0 pulseaudio-utils alsa-utils libglib2.0-bin xdg-utils libgdk-pixbuf-2.0-0 librsvg2-common x11-utils swh-plugins liblilv-0-0 lsp-plugins-lv2
sudo apt install build-essential pkg-config libpipewire-0.3-dev liblilv-dev lv2-dev libx11-dev libsndfile1-dev libxtst6 xvfb xauth cargo rustc curl
```

Verify the audio stack is PipeWire before going further:

```sh
pactl info | grep "Server Name"    # should say PulseAudio (on PipeWire ...)
```

## 2. Build

```sh
git clone https://github.com/emaspa/openxlr.git
cd openxlr
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror -p:EnableNativeLv2Host=true
```

Binaries land in `src/OpenXLR.Daemon/bin/Release/net10.0/` and
`src/OpenXLR.UI/bin/Release/net10.0/`; the terminal mixer is
`src/OpenXLR.Tui/bin/Release/net10.0/openxlr-tui`. The native flag builds
`native/` and copies `openxlr-lv2-host` next to the daemon. CLAP/VST3
headers are vendored.

You may omit the flag for hardware control and LV2 filter-chain inserts
without native editors. CLAP, VST3, the microphone EQ in software, noise
removal, ducking and the soundboard require the helper. Keep the flag
on every later build once you rely on it: an existing helper is retained
without the flag, but it is not rebuilt and may miss native fixes. See
[plugin editors](manual.md#plugin-editors).

If the build ends with `MSB3073` from the native target, look above that
line for the compiler or `pkg-config` error. Run `make -C native` from the
same checkout to see the native failure directly. The missing daemon
executable after a failed build is a consequence; finish the build before
trying to launch it.

<a name="noise-removal-engines"></a>
### Noise removal engines

The helper carries two noise removal engines for the microphone. RNNoise
is part of the source tree and always built. DeepFilterNet is a Rust
library under `native/deepfilter`, built when `cargo` is on the PATH. The
first such build downloads its crates from crates.io and its model (7.6 MB,
checked against the SHA-256 in `native/Makefile`), and takes about five
minutes of processor time. Set `DEEPFILTER` to choose. `make` also reads it
from the environment, so it applies to the `dotnet build` above too:

```sh
DEEPFILTER=1 make -C native   # build DeepFilterNet in, fail without cargo
DEEPFILTER=0 make -C native   # RNNoise only, no Rust needed
native/openxlr-lv2-host engines   # which engines this build has
```

Every package sets `DEEPFILTER=1`. For a build with no network, put the
model at `native/deepfilter/model/DeepFilterNet3_onnx.tar.gz` and the
crates under `native/deepfilter/vendor` (`cargo vendor --locked` in
`native/deepfilter` fetches them); the build then reads only those.

## 3. Device access

```sh
sudo install -m 0644 packaging/70-openxlr.rules /etc/udev/rules.d/70-openxlr.rules
sudo udevadm control --reload
```

Replug the interface after reloading the rules.

## 4. WirePlumber rules

XLR Dock, original Wave XLR, Wave:3 and Wave:3 MK.2 owners need a
WirePlumber rule, and the Wave:3 MK.2 a second one. The
Linux kernel starves the dock's capture endpoint whenever playback to it
starts before capture, and the mic then records pure silence (Windows
schedules the same duplex fine; the kernel also logs "bad transfer trb
length" warnings from the dock's malformed feedback endpoint). The
original Wave XLR (MK.1) has the same ordering bug over its full-speed
USB link, and so does a Wave:3 on some USB ports and controllers.
WirePlumber rules keep these capture sources always active, so playback
can never come first. The Wave:3 MK.2's microphone stalls while its
headphones play in the two-channel integer format PipeWire picks, so a
fifth rule opens them in the unit's float format. The following five
`.conf` files use WirePlumber 0.5 syntax. Check the library version with `wireplumber --version`:

```sh
mkdir -p ~/.config/wireplumber/wireplumber.conf.d
cp packaging/50-xlr-dock-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/
cp packaging/51-openxlr-pro-raw-names.conf ~/.config/wireplumber/wireplumber.conf.d/
cp packaging/52-openxlr-mk1-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/
cp packaging/53-openxlr-wave3-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/
cp packaging/55-openxlr-wave3-mk2-playback.conf ~/.config/wireplumber/wireplumber.conf.d/
systemctl --user restart wireplumber
```

The raw-name rule gives the Pro's multichannel nodes readable descriptions
in desktop audio settings. It does not change their routing identities.
Each of the five files matches only its target hardware, so they can be
installed together on any supported setup, which is what the packages do.

WirePlumber 0.4, including the version supplied by Ubuntu 24.04, needs a
separate compatibility rule for OpenXLR's software nodes on every interface.
It honors their declared opt-out from desktop volume and mute restoration,
so an old pause or gain cannot replace the mixer's saved settings. Install
this Lua rule only when `wireplumber --version` reports
`Linked with libwireplumber 0.4.x`:

```sh
mkdir -p ~/.config/wireplumber/main.lua.d
cp packaging/54-openxlr-owned-state.lua ~/.config/wireplumber/main.lua.d/
systemctl --user restart wireplumber
systemctl --user restart openxlr-daemon
```

These restarts briefly interrupt audio; logging out and back in also loads
the rule. Debian packages built for 0.4 install it automatically. Other
older distributions and source installations need the copy above.
WirePlumber 0.5 honors the declaration directly: do not install this Lua
file there. After upgrading from 0.4 to 0.5, remove an unchanged copy with:

```sh
cmp -s packaging/54-openxlr-owned-state.lua ~/.config/wireplumber/main.lua.d/54-openxlr-owned-state.lua && rm -- ~/.config/wireplumber/main.lua.d/54-openxlr-owned-state.lua
```

## 5. First run

Quit a running UI through its tray menu before testing the source build.
OpenXLR allows one UI per user; a second launch brings the existing UI
forward instead of running the newly built one.

If a packaged daemon is already running, stop it before testing the source
build with `systemctl --user stop openxlr-daemon`. This interrupts OpenXLR
audio until the source daemon starts. Opening just the newly built UI
still connects to the installed daemon. Restore the installed service after
the test with `systemctl --user start openxlr-daemon`, after stopping the
source daemon.

Run the daemon in a terminal. On a fresh source installation, the mixer graph is opt-in through this
variable. An existing `daemon.json` submixer preference takes precedence,
as listed in the environment table below.

```sh
OPENXLR_BUILD_MIXER=1 ./src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
```

The log should show your device connecting and `submix graph built`.
A source build may show the same release number as an installed package.
Collect diagnostics to check the UI's build in `meta.txt` and the running
daemon's build in `daemon-state.json`, including source commits when the
build stamped them. A build without source metadata cannot identify its
commit from the version alone.

Then, in a second terminal from the same repository root, run the UI:

```sh
./src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
```

The header dot turns green when the daemon has the device. If it says
"no device", re-check the udev rule and replug.

## 6. Make it permanent

Options (the gear button) → General → AT LOGIN has two checkboxes that install a
systemd user unit for the daemon and an autostart entry for the UI.
On a source build the unit points at the build output; on a packaged
install it enables the package's unit instead.

OpenDeck and the Omarchy bar also need the client-only `openxlr-control`
launcher on their PATH. A build puts it beside the daemon. For a source
installation, link that launcher into your user bin directory:

```sh
mkdir -p ~/.local/bin
ln -s "$PWD/src/OpenXLR.Daemon/bin/Release/net10.0/openxlr-control" ~/.local/bin/openxlr-control
```

Ensure `~/.local/bin` is on the desktop session's PATH, then restart OpenDeck
or the bar. Update an existing link if you move the checkout. The launcher
resolves the link to this build and never selects a packaged daemon from PATH.
Packages install the launcher themselves. Older installations do not have it;
upgrade the daemon together with its clients.

The manual way, using the reference unit in
[packaging/openxlr-daemon.service](../packaging/openxlr-daemon.service):

```sh
mkdir -p ~/.config/systemd/user
cp packaging/openxlr-daemon.service ~/.config/systemd/user/
# edit ExecStart in the copy if you cloned somewhere other than ~/openxlr,
# and change nothing else: the uninstall below recognizes the copy by its bytes
systemctl --user daemon-reload
systemctl --user enable --now openxlr-daemon.service
journalctl --user -u openxlr-daemon.service -f   # watch it come up
```

The supplied unit uses systemd notifications with a 60-second watchdog.
Heartbeats require recent device and mixer progress, including completed steps
inside graph operations. Failed polls still count as progress; a missing audio
server leaves device control running rather than causing a restart loop.
Startup timeout extensions are sent only while the workers make progress.

On a watchdog timeout systemd sends SIGTERM, allowing normal graph teardown.
Failed starts are limited to three attempts in five minutes. After fixing a
persistent failure, use `systemctl --user reset-failed openxlr-daemon` followed
by `systemctl --user start openxlr-daemon`. Manual launches without
`NOTIFY_SOCKET` do not enable the watchdog.

The packages raise pipewire-pulse's open-file limit with a systemd
drop-in; a source install has to create it itself, or the daemon refuses
to grow the mixer layout once the server nears systemd's default of 1024
open files:

```sh
mkdir -p ~/.config/systemd/user/pipewire-pulse.service.d
cp packaging/pipewire-pulse-openxlr.conf ~/.config/systemd/user/pipewire-pulse.service.d/openxlr.conf
systemctl --user daemon-reload
systemctl --user restart pipewire-pulse
```

The restart takes OpenXLR's nodes with it; the daemon notices and
restarts itself to rebuild them. The manual's
[open-files section](manual.md#open-files) has the background and the check.

<a name="desktop-entry"></a>
The packages also install a desktop entry. [Desktop keys](manual.md#desktop-keys)
need it on current desktop portals, which accept OpenXLR's identity only
when `openxlr.desktop` is installed and refuse a shortcut session from a
process they cannot identify. Install the reference entry with the UI's
full path, from the repository root:

```sh
mkdir -p ~/.local/share/applications
sed "s|^Exec=openxlr\$|Exec=$PWD/src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI|" packaging/openxlr.desktop > ~/.local/share/applications/openxlr.desktop
```

The portal ignores an entry whose program it cannot find, so keep the full
path. If the checkout's path contains spaces, quote the `Exec` value in the
copy. The entry also adds OpenXLR to the desktop's application menu.
Restart the UI afterwards.


## 7. OpenDeck plugin (optional)

With [OpenDeck](https://github.com/nekename/OpenDeck) installed, copy
the plugin folder (a symlink breaks OpenDeck's asset serving) and
restart OpenDeck:

```sh
mkdir -p ~/.config/opendeck/plugins
cp -r plugin/com.emaspa.openxlr.sdPlugin ~/.config/opendeck/plugins/
```

## 8. Omarchy bar plugin (optional)

On Omarchy 4, link the checkout's plugin into the shell and enable it:

```sh
bash packaging/omarchy/openxlr-omarchy-enable "$PWD/packaging/omarchy/openxlr.mixer"
```

It needs `qt6-websockets` and the running daemon. `omarchy plugin remove
openxlr.mixer` takes it out again; [omarchy.md](omarchy.md) has the
commands and the package recipe.

## 9. Updating

```sh
git pull --ff-only
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror -p:EnableNativeLv2Host=true
systemctl --user restart openxlr-daemon.service
```

Restart the UI and, if you use it, recopy the OpenDeck plugin folder.
When `packaging/70-openxlr.rules` or a WirePlumber rule changed, repeat
sections 3 and 4 above, then replug the interface.

## Uninstall

```sh
systemctl --user stop openxlr-daemon.service
dotnet run --project src/OpenXLR.UI -c Release --no-build -- installation remove-startup
systemctl --user daemon-reload
```

This removes startup entries recorded by Options and adopts unchanged
entries generated by older versions of the UI or copied from the reference unit
above, including a copy with only `ExecStart` adjusted, whether it came
from the current reference unit or the one v0.1.37 to v0.1.70 shipped.
Changed or foreign files are refused; `openxlr doctor` names them and gives
a recovery command.
An original user file backed up by Options is restored, not deleted.

The other files copied by this guide are not startup entries. Compare and
remove each exact file only if it is still the copy supplied here:

```sh
sudo cmp -s packaging/70-openxlr.rules /etc/udev/rules.d/70-openxlr.rules && sudo rm -- /etc/udev/rules.d/70-openxlr.rules
cmp -s packaging/50-xlr-dock-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf && rm -- ~/.config/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
cmp -s packaging/51-openxlr-pro-raw-names.conf ~/.config/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf && rm -- ~/.config/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf
cmp -s packaging/52-openxlr-mk1-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/52-openxlr-mk1-capture-hold.conf && rm -- ~/.config/wireplumber/wireplumber.conf.d/52-openxlr-mk1-capture-hold.conf
cmp -s packaging/53-openxlr-wave3-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/53-openxlr-wave3-capture-hold.conf && rm -- ~/.config/wireplumber/wireplumber.conf.d/53-openxlr-wave3-capture-hold.conf
cmp -s packaging/55-openxlr-wave3-mk2-playback.conf ~/.config/wireplumber/wireplumber.conf.d/55-openxlr-wave3-mk2-playback.conf && rm -- ~/.config/wireplumber/wireplumber.conf.d/55-openxlr-wave3-mk2-playback.conf
cmp -s packaging/54-openxlr-owned-state.lua ~/.config/wireplumber/main.lua.d/54-openxlr-owned-state.lua && rm -- ~/.config/wireplumber/main.lua.d/54-openxlr-owned-state.lua
cmp -s packaging/pipewire-pulse-openxlr.conf ~/.config/systemd/user/pipewire-pulse.service.d/openxlr.conf && rm -- ~/.config/systemd/user/pipewire-pulse.service.d/openxlr.conf
sed "s|^Exec=openxlr\$|Exec=$PWD/src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI|" packaging/openxlr.desktop | cmp -s - ~/.local/share/applications/openxlr.desktop && rm -- ~/.local/share/applications/openxlr.desktop
sudo udevadm control --reload-rules
systemctl --user daemon-reload
```

A failed comparison leaves that file untouched. Inspect edited files before
moving them. No service or WirePlumber directory needs to be removed.

Stop the UI as well. Remove the OpenDeck plugin through its plugin manager. On Omarchy, `omarchy plugin remove openxlr.mixer`
unlinks the bar plugin. These commands preserve unrelated service overrides. Saved settings, soundboard clips and private
Windows wrappers remain in `~/.config/openxlr`,
`~/.local/share/openxlr/soundboard` and
`~/.local/share/openxlr/yabridge`; back them up before removing them if you
want a clean slate. Restart WirePlumber at a convenient time to unload the
removed rules. If you installed the optional UCM profile, use its revert
script separately.

## Environment variables

Set these in the daemon environment, for example with `systemctl --user
edit openxlr-daemon` and a `[Service]` / `Environment=NAME=value` entry.
Reload the user manager and restart the daemon after changing them. A
shell export does not update an already running service.

| Variable | Effect |
|---|---|
| `OPENXLR_BUILD_MIXER=1` | build the PipeWire submix graph (otherwise device-control only); `daemon.json`'s `submixer` key, written by the Options window, overrides it when present |
| `OPENXLR_MONITOR_OUTPUT=<sink>` | initial monitor output (overrides saved choice) |
| `OPENXLR_DEVICE=<pid>` | which interface to drive at start when several are attached (hex product id, e.g. `00a6`); a unit picked in the header wins over it, also after a restart ([Multiple Wave interfaces](wave-interfaces.md)) |
| `LV2_PATH`, `CLAP_PATH`, `VST3_PATH` | colon-separated plugin search directories; CLAP/VST3 private companion directories still take priority |
| `LADSPA_PATH` | LADSPA directories for the software limiter |
| `OPENXLR_YABRIDGE` | `system` selects the existing bridge; an absolute path selects a complete companion; unset enables automatic discovery |
| `WINEPREFIX` | Wine prefix used when locating Windows plugin folders; defaults to `~/.wine` |
| `WINEDLLOVERRIDES` | passed to bridged plugins with `wineusb.sys=d` appended, which keeps Wine's USB driver off; a value that already names `wineusb.sys` is passed unchanged |
| `LSP_WS_LIB_GLXSURFACE` | native helper defaults to `0` for LSP software rendering; explicit `1` tests OpenGL |
| `DISPLAY`, `XAUTHORITY` | X11/XWayland session and authentication for native editors; the daemon can obtain these from the systemd user manager |
| `OPENXLR_HOST_TRACE=1` | VST3 bus and initial processing diagnostics in the daemon log |
| `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_RUNTIME_DIR` | configuration, private wrapper and soundboard clip, and runtime roots; see [files and services](manual.md#files) |
