# Installing from source

## Requirements

- Linux with PipeWire, `pipewire-pulse`
  and WirePlumber; `pactl`, `pw-cli`, `pw-link`, `pw-dump`, `parec`, `wpctl` and `amixer` on PATH
- `swh-plugins` (LADSPA) for the software ClipGuard; everything else
  works without it
- `lilv` for LV2 discovery and compatible LV2, CLAP or VST3 plugins
- .NET 10 SDK to build; ASP.NET Core 10 runtime for a framework-dependent
  installation (the base .NET runtime alone does not run the daemon)
- C/C++ compiler, make, pkg-config, PipeWire/lilv/LV2/X11 headers for the
  native plugin host; X11 or XWayland for plugin editors
- libusb 1.0
- A supported Elgato interface (see the device table in the README); the submixer works
  with any of them, and the aux and output routing features follow the
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
# Arch
sudo pacman -S --needed dotnet-sdk aspnet-runtime pipewire pipewire-pulse wireplumber libusb libpulse alsa-utils
# optional: software ClipGuard for the XLR Dock, and LV2 plugins for inserts
sudo pacman -S --needed swh-plugins lilv lsp-plugins-lv2
# native host and editor test dependencies
sudo pacman -S --needed base-devel lv2 libx11 xorg-server-xvfb xorg-xauth

# Fedora
sudo dnf install dotnet-sdk-10.0 aspnetcore-runtime-10.0 pipewire pipewire-pulseaudio wireplumber libusb1 pulseaudio-utils alsa-utils ladspa-swh-plugins lilv-libs lsp-plugins-lv2
sudo dnf install gcc-c++ make pkgconf-pkg-config pipewire-devel lilv-devel lv2-devel libX11-devel xorg-x11-server-Xvfb xorg-x11-xauth

# Debian / Ubuntu (dotnet from Microsoft's feed if the distro lacks 10.0)
sudo apt install dotnet-sdk-10.0 aspnetcore-runtime-10.0 pipewire pipewire-pulse wireplumber libusb-1.0-0 pulseaudio-utils alsa-utils swh-plugins liblilv-0-0 lsp-plugins-lv2
sudo apt install build-essential pkg-config libpipewire-0.3-dev liblilv-dev lv2-dev libx11-dev xvfb xauth
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
`src/OpenXLR.UI/bin/Release/net10.0/`. The native flag builds `native/` and
copies `openxlr-lv2-host` next to the daemon. CLAP/VST3 headers are vendored.

You may omit the flag for hardware control and LV2 filter-chain inserts
without native editors. CLAP and VST3 require the helper. Keep the flag
on every later build once you rely on it: a build without it removes the
helper and native inserts report that it is missing. See
[plugin editors](manual.md#plugin-editors).

## 3. Device access

```sh
sudo install -m 0644 packaging/70-openxlr.rules /etc/udev/rules.d/70-openxlr.rules
sudo udevadm control --reload
```

Replug the interface after reloading the rules.

## 4. WirePlumber rules

XLR Dock owners need one more file. The Linux kernel starves the dock's
capture endpoint whenever playback to it starts before capture, and the
mic then records pure silence (Windows schedules the same duplex fine;
the kernel also logs "bad transfer trb length" warnings from the dock's
malformed feedback endpoint). A WirePlumber rule keeps the dock's
capture source always active, so playback can never come first:

```sh
mkdir -p ~/.config/wireplumber/wireplumber.conf.d
cp packaging/50-xlr-dock-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/
cp packaging/51-openxlr-pro-raw-names.conf ~/.config/wireplumber/wireplumber.conf.d/
systemctl --user restart wireplumber
```

The raw-name rule gives the Pro's multichannel nodes readable descriptions
in desktop audio settings. It does not change their routing identities. Both files match only their target hardware, so they can be
installed together on any supported setup. These
`.conf` rules use WirePlumber 0.5 syntax.

## 5. First run

Run the daemon in a terminal. On a fresh source installation, the mixer graph is opt-in through this
variable. An existing `daemon.json` submixer preference takes precedence,
as listed in the environment table below.

```sh
OPENXLR_BUILD_MIXER=1 ./src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
```

The log should show your device connecting and `submix graph built`.
Then, in a second terminal from the same repository root, run the UI:

```sh
./src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
```

The header dot turns green when the daemon has the device. If it says
"no device", re-check the udev rule and replug.

## 6. Make it permanent

The Options window (the gear button) has two checkboxes that install a
systemd user unit for the daemon and an autostart entry for the UI.
On a source build the unit points at the build output; on a packaged
install it enables the package's unit instead.

The manual way, using the reference unit in
[packaging/openxlr-daemon.service](../packaging/openxlr-daemon.service):

```sh
mkdir -p ~/.config/systemd/user
cp packaging/openxlr-daemon.service ~/.config/systemd/user/
# edit ExecStart in the copy if you cloned somewhere other than ~/openxlr
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


## 7. OpenDeck plugin (optional)

With [OpenDeck](https://github.com/nekename/OpenDeck) installed, copy
the plugin folder (a symlink breaks OpenDeck's asset serving) and
restart OpenDeck:

```sh
mkdir -p ~/.config/opendeck/plugins
cp -r plugin/com.emaspa.openxlr.sdPlugin ~/.config/opendeck/plugins/
```

## 8. Updating

```sh
git pull --ff-only
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror -p:EnableNativeLv2Host=true
systemctl --user restart openxlr-daemon.service
```

Restart the UI and, if you use it, recopy the OpenDeck plugin folder.

## Uninstall

```sh
systemctl --user disable --now openxlr-daemon.service
rm ~/.config/systemd/user/openxlr-daemon.service
sudo rm /etc/udev/rules.d/70-openxlr.rules
rm -rf ~/.config/opendeck/plugins/com.emaspa.openxlr.sdPlugin
rm ~/.config/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
rm -f ~/.config/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf
rm -f ~/.config/systemd/user/pipewire-pulse.service.d/openxlr.conf
rm -f ~/.config/autostart/openxlr.desktop
systemctl --user daemon-reload
```

Stop the UI as well. These commands remove the files installed by this
guide and preserve other service overrides. Saved settings and private
Windows wrappers remain in `~/.config/openxlr` and
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
| `OPENXLR_DEVICE=<pid>` | which interface to drive at start when several are attached (hex product id, e.g. `00a6`) |
| `LV2_PATH`, `CLAP_PATH`, `VST3_PATH` | colon-separated plugin search directories; CLAP/VST3 private companion directories still take priority |
| `LADSPA_PATH` | LADSPA directories for the software limiter |
| `OPENXLR_YABRIDGE` | `system` selects the existing bridge; an absolute path selects a complete companion; unset enables automatic discovery |
| `WINEPREFIX` | Wine prefix used when locating Windows plugin folders; defaults to `~/.wine` |
| `LSP_WS_LIB_GLXSURFACE` | native helper defaults to `0` for LSP software rendering; explicit `1` tests OpenGL |
| `DISPLAY`, `XAUTHORITY` | X11/XWayland session and authentication for native editors; the daemon can obtain these from the systemd user manager |
| `OPENXLR_HOST_TRACE=1` | VST3 bus and initial processing diagnostics in the daemon log |
| `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_RUNTIME_DIR` | configuration, private wrapper and runtime roots; see [files and services](manual.md#files) |
