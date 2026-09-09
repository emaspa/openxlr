# OpenXLR Flatpak test build

This variant is distributed as a manually installed GitHub asset, not submitted
to Flathub. Standard Flatpak runtimes and compatible Linux audio extensions may
come from Flathub. SteamOS hardware and Gaming Mode acceptance are still pending.

## Install and update

From the directory containing the downloaded files, verify and install:

```sh
sha256sum -c SHA256SUMS
flatpak install --user --reinstall ./OpenXLR-x86_64.flatpak
flatpak run io.github.emaspa.OpenXLR
```

Install any requested runtime. Updates to OpenXLR are manual: download a newer
bundle and repeat the installation command. The optional GitHub release check
in Options runs at most daily, or immediately with Check now. It links to the
Flatpak asset when present and says when a newer release has no Flatpak yet.
`flatpak update` still handles runtimes and extensions.

Quit a native OpenXLR UI and stop its daemon before running this variant. Both
use port 37890 and the same private runtime token and lock directory, so two
mixers must not operate on the same session at once. Profiles and other saved
configuration are separate under `~/.var/app/io.github.emaspa.OpenXLR/config/openxlr`.

## Device access and host setup

The sandbox cannot install udev rules. Its device permission only exposes
USB and ALSA nodes; the host must already let the logged-in user open them.
First try the interface without changing the host. If vendor controls report
permission errors, install the supplied rule on the host:

```sh
sudo install -Dm644 70-openxlr.rules /etc/udev/rules.d/70-openxlr.rules
sudo udevadm control --reload-rules
```

Unplug and reconnect the interface, then reopen OpenXLR. The rule grants the
active local user access to the five supported Elgato USB product IDs. Do not
run OpenXLR as root. This prototype uses direct device access, not the USB
portal. If the rule cannot be installed on a particular SteamOS image, report
that result before changing the read-only system image. Rule persistence across
SteamOS updates needs testing; the Flatpak itself does not write `/usr` or `/etc`.

The original XLR Dock may need the supplied capture-hold workaround. Install it
on the host, not in the Flatpak's private configuration:

```sh
install -Dm644 50-xlr-dock-capture-hold.conf \
  "${XDG_CONFIG_HOME:-$HOME/.config}/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf"
```

Log out and in to reload the audio session. The optional Wave XLR Pro ALSA UCM
profile is not required for OpenXLR's submixer and is not installed by this bundle.

## Audio server limit

The host PipeWire PulseAudio server must have enough open-file headroom. The
Flatpak cannot inspect that process through its private PID namespace, so this
test build refuses to add channels or mixes when the limit cannot be read. It
also refuses saved layouts with more than 45 channel-to-mix sends before creating
any nodes. Existing levels, assignments, names, order and removals remain editable.
This conservative layout budget is not a measurement of the host's available
resources. If the default layout drops nodes, collect diagnostics and check the
host limit before continuing.

The supplied drop-in can be installed on the host without writing `/usr`:

```sh
install -Dm644 pipewire-pulse-openxlr.conf \
  "${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/pipewire-pulse.service.d/openxlr.conf"
systemctl --user daemon-reload
```

It takes effect at the next login. Restarting pipewire-pulse immediately instead
interrupts current audio. The Flatpak does not perform either operation itself.
The conservative layout restriction remains in this first build even after the
host limit is increased, because the sandbox cannot verify it.

## Plugins and background audio

Compatible Linux LV2 plugins run in PipeWire filter-chain with OpenXLR's own
parameter controls. Install LV2 folders using Options. Installs live under the
Flatpak's data directory; host plugin directories are not searched. The bundle
includes the LADSPA hard limiter needed by software limiting. Compatible
`org.freedesktop.LinuxAudio.Plugins` extensions on branch `25.08` are optional.

CLAP, VST3, Wine/yabridge and native plugin editors are not included. A profile
referencing those plugins or native LV2 hosting reports unavailable processing;
it does not convert the chain silently. Native distribution packages keep their
existing plugin support.

The UI owns a bundled daemon and stops it on Quit. A Quit button is available
in the main window even on desktops without a tray. Closing the window minimizes
to the tray by default; allow the desktop's background request to keep audio
running. Start OpenXLR at login in Options uses the background portal, not a
systemd unit. Desktop and Gaming Mode portal behavior, tray access, login startup
and session switching require SteamOS acceptance testing.

## Diagnostics and permissions

Read-only preflight:

```sh
flatpak run --command=openxlr-check io.github.emaspa.OpenXLR
```

The preflight lists USB nodes and the host audio server; review its output before
sharing. Options also collects a redacted diagnostics archive. The archive and
bounded daemon log are under `~/.var/app/io.github.emaspa.OpenXLR/data/openxlr`;
the log is `logs/daemon.log`. A chatty daemon overwrites the oldest log content
rather than interrupting audio when the file reaches 1 MiB.

Permissions expose raw devices for libusb and ALSA controls, X11 for Avalonia,
the PulseAudio and PipeWire connections for routing, network access for the local
API and optional GitHub checks, the tray service, and only the shared OpenXLR
runtime directory. No blanket home access or host command execution is granted.
The app uses file and background portals. OpenDeck can read the shared token
when its own packaging grants that directory; sandboxed OpenDeck needs testing.

## Build from source

Install Flatpak and the build SDKs once:

```sh
flatpak install --user flathub org.flatpak.Builder org.freedesktop.Sdk.Extension.dotnet10//25.08
python3 packaging/flatpak/build.py all
```

The default output is `~/.cache/openxlr-flatpak-build/dist`. `--output PATH`
selects another build directory. Preparation snapshots the working tree and
fetches NuGet archives. Flatpak Builder downloads the pinned audio dependencies
and builds with networking disabled. NuGet restore uses locked mode against the
committed package graphs. The bundled .NET runtime comes from the SDK extension.
The native plugin host is explicitly excluded, even when a native binary exists
in the source checkout. This build does not replace a local native installation.

The installed bundle can be tested with a private audio server and no hardware
access. This creates and removes a separate mixer graph, and loads an LV2 effect:

```sh
timeout -k 5 90 flatpak run --unshare=network --nodevice=all \
  --command=sh io.github.emaspa.OpenXLR -s < packaging/flatpak/smoke-test.sh
```

This checks the packaged backend, not the host session's routing permissions or
real audio. The separate Flatpak workflow produces downloadable artifacts without modifying
release workflows, tags or versions. A maintainer attaches the `.flatpak`, source
archive, host setup files and checksums to a GitHub release after acceptance.

## Steam Machine acceptance

Test a clean user installation and manual upgrade, then:

- Each attached interface: gain, mute, headphone volume and supported vendor DSP.
- Application routing, monitor outputs, Stream/Chat capture and live meters.
- LV2 loading, parameter changes, bypass, saved profiles and a missing plugin.
- Window close versus Quit, daemon restart, login startup and permission denial.
- USB unplug/replug, suspend/resume and Desktop/Gaming Mode transitions.
- Stock host permissions, host audio-server limits and persistence after an OS update.
- OpenDeck authentication and commands if OpenDeck is installed.

Record the SteamOS, Flatpak, PipeWire and portal versions alongside the interface
model and results. Passing local builds is not SteamOS hardware acceptance.
