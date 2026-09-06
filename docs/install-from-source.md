# Installing from source

## Requirements

- Linux with PipeWire 1.4 or newer (developed on 1.6), `pipewire-pulse`
  and WirePlumber; `pactl`, `pw-cli`, `pw-link`, `pw-dump`, `parec` on PATH
- `swh-plugins` (LADSPA) for the software ClipGuard; everything else
  works without it
- `lilv` and some LV2 plugins for the inserts (`lsp-plugins-lv2` to
  start); without lilv the plugin picker is simply empty
- .NET 10 SDK to build (runtime to run)
- libusb 1.0
- A supported Elgato interface (see the device table in the README); the submixer works
  with any of them, and the aux and output routing features follow the
  device's capabilities

Every step of a from-source deploy, for machines without a package.

## 1. Prerequisites

The .NET 10 SDK, PipeWire with its CLI tools, and libusb. Package names
by distribution:

```sh
# Arch
sudo pacman -S --needed dotnet-sdk pipewire pipewire-pulse wireplumber libusb
# optional: software ClipGuard for the XLR Dock, and LV2 plugins for inserts
sudo pacman -S --needed swh-plugins lilv lsp-plugins-lv2

# Fedora
sudo dnf install dotnet-sdk-10.0 pipewire pipewire-pulseaudio wireplumber libusb1 ladspa-swh-plugins lilv-libs lsp-plugins-lv2

# Debian / Ubuntu (dotnet from Microsoft's feed if the distro lacks 10.0)
sudo apt install dotnet-sdk-10.0 pipewire pipewire-pulse wireplumber libusb-1.0-0 swh-plugins liblilv-0-0 lsp-plugins-lv2
```

Verify the audio stack is PipeWire before going further:

```sh
pactl info | grep "Server Name"    # should say PulseAudio (on PipeWire ...)
```

## 2. Build

```sh
git clone https://github.com/emaspa/openxlr.git
cd openxlr/src
dotnet build -c Release
```

Binaries land in `src/OpenXLR.Daemon/bin/Release/net10.0/` and
`src/OpenXLR.UI/bin/Release/net10.0/`.

## 3. Device access (udev rule, then replug the device):

```sh
sudo tee /etc/udev/rules.d/70-openxlr.rules << 'EOF'
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="00b4", MODE="0660", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="00a6", MODE="0660", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="007d", MODE="0660", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="00b6", MODE="0660", TAG+="uaccess"
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="00c7", MODE="0660", TAG+="uaccess"
EOF
sudo udevadm control --reload
```

## 4. XLR Dock only: the capture-hold rule

XLR Dock owners need one more file. The Linux kernel starves the dock's
capture endpoint whenever playback to it starts before capture, and the
mic then records pure silence (Windows schedules the same duplex fine;
the kernel also logs "bad transfer trb length" warnings from the dock's
malformed feedback endpoint). A WirePlumber rule keeps the dock's
capture source always active, so playback can never come first:

```sh
mkdir -p ~/.config/wireplumber/wireplumber.conf.d
cp packaging/50-xlr-dock-capture-hold.conf ~/.config/wireplumber/wireplumber.conf.d/
systemctl --user restart wireplumber
```

## 5. First run

Run the daemon in a terminal. The mixer graph is opt-in: without the
variable the daemon drives the device only and leaves the PipeWire
graph untouched.

```sh
OPENXLR_BUILD_MIXER=1 ./OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
```

The log should show your device connecting and `submix graph built`.
Then, in a second terminal, the UI:

```sh
./OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
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

OpenXLR does not install a file-descriptor-limit override for pipewire-pulse.
If its journal reports exhausted descriptors, inspect that service's limits
and configure a user override explicitly for your environment.


## 7. OpenDeck plugin (optional)

With [OpenDeck](https://github.com/nekename/OpenDeck) installed, copy
the plugin folder (a symlink breaks OpenDeck's asset serving) and
restart OpenDeck:

```sh
cp -r plugin/com.emaspa.openxlr.sdPlugin ~/.config/opendeck/plugins/
```

## 8. Updating

```sh
cd openxlr && git pull
cd src && dotnet build -c Release
systemctl --user restart openxlr-daemon.service
```

Restart the UI and, if you use it, recopy the OpenDeck plugin folder.

## Uninstall

```sh
systemctl --user disable --now openxlr-daemon.service
rm ~/.config/systemd/user/openxlr-daemon.service
sudo rm /etc/udev/rules.d/70-openxlr.rules
rm -rf ~/.config/openxlr ~/.config/opendeck/plugins/com.emaspa.openxlr.sdPlugin
rm ~/.config/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
```

## Environment variables

| Variable | Effect |
|---|---|
| `OPENXLR_BUILD_MIXER=1` | build the PipeWire submix graph (otherwise device-control only); `daemon.json`'s `submixer` key, written by the Options window, overrides it when present |
| `OPENXLR_MONITOR_OUTPUT=<sink>` | initial monitor output (overrides saved choice) |
| `OPENXLR_DEVICE=<pid>` | which interface to drive at start when several are attached (hex product id, e.g. `00a6`) |
