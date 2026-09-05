# The publish output is prebuilt .NET; its bundled native libraries must
# not be stripped, scanned for shared-library deps, or given debuginfo.
%global debug_package %{nil}
%global __strip /usr/bin/true
%global _build_id_links none

Name:           openxlr
Version:        0.1.23
Release:        1%{?dist}
Summary:        Control suite and PipeWire submixer for Elgato XLR interfaces
License:        GPL-3.0-only
URL:            https://github.com/emaspa/openxlr
Source0:        %{name}-%{version}.tar.gz
ExclusiveArch:  x86_64

BuildRequires:  dotnet-sdk-10.0
BuildRequires:  systemd-rpm-macros

# Prebuilt .NET assemblies; dependencies are declared by hand, matching
# the Debian and Arch packages.
AutoReqProv:    no
Requires:       aspnetcore-runtime-10.0
Requires:       pipewire
Requires:       pipewire-pulseaudio
Requires:       wireplumber
Requires:       pulseaudio-libs
Requires:       libusb1
Requires:       lilv-libs
Requires:       fontconfig
Requires:       libX11
Requires:       libICE
Requires:       libSM
Recommends:     alsa-utils
Recommends:     pulseaudio-utils
Recommends:     xdg-utils
Suggests:       ladspa-swh-plugins
Suggests:       lsp-plugins-lv2

%description
Native Linux control for Elgato XLR interfaces over reverse-engineered
USB protocols: gain, DSP, phantom power, output routing and the
hardware mixer. Includes a Wave Link style PipeWire submixer with
per-application channels, virtual microphones, multi-output monitoring
and a dedicated mix for a second computer on the USB Aux port, plus an
OpenDeck plugin for Stream Deck control.

Supported devices: Wave XLR Pro, XLR Dock (Stream Deck+ module),
Wave XLR and Wave XLR MK.2.

After installing, enable the per-user daemon with
"systemctl --user enable --now openxlr-daemon" and replug the
interface once so the udev rule applies.

%prep
%autosetup

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
       DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet publish src/OpenXLR.Daemon -c Release -r linux-x64 \
    --self-contained false -o out/daemon
dotnet publish src/OpenXLR.UI -c Release -r linux-x64 \
    --self-contained false -o out/ui

%install
install -dm755 %{buildroot}%{_prefix}/lib/openxlr
cp -r out/daemon %{buildroot}%{_prefix}/lib/openxlr/daemon
cp -r out/ui %{buildroot}%{_prefix}/lib/openxlr/ui
# dotnet publish marks assemblies executable; only the apphosts are.
find %{buildroot}%{_prefix}/lib/openxlr -type f -exec chmod 644 {} +
chmod 755 %{buildroot}%{_prefix}/lib/openxlr/daemon/OpenXLR.Daemon \
    %{buildroot}%{_prefix}/lib/openxlr/ui/OpenXLR.UI

install -dm755 %{buildroot}%{_bindir}
printf '#!/bin/sh\nexec %{_prefix}/lib/openxlr/daemon/OpenXLR.Daemon "$@"\n' \
    > %{buildroot}%{_bindir}/openxlr-daemon
printf '#!/bin/sh\nexec %{_prefix}/lib/openxlr/ui/OpenXLR.UI "$@"\n' \
    > %{buildroot}%{_bindir}/openxlr
chmod 755 %{buildroot}%{_bindir}/openxlr-daemon %{buildroot}%{_bindir}/openxlr

install -Dm644 packaging/70-openxlr.rules \
    %{buildroot}%{_udevrulesdir}/70-openxlr.rules
install -Dm644 packaging/50-xlr-dock-capture-hold.conf \
    %{buildroot}%{_datadir}/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
install -Dm644 packaging/51-openxlr-pro-raw-names.conf \
    %{buildroot}%{_datadir}/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf

# The reference unit points into a source checkout; the package runs
# the wrapper.
sed 's|^ExecStart=.*|ExecStart=%{_bindir}/openxlr-daemon|' \
    packaging/openxlr-daemon.service > openxlr-daemon.service
install -Dm644 openxlr-daemon.service \
    %{buildroot}%{_userunitdir}/openxlr-daemon.service

install -Dm644 packaging/openxlr.desktop \
    %{buildroot}%{_datadir}/applications/openxlr.desktop
for size in 16 32 48 64 128 256; do
    install -Dm644 src/OpenXLR.UI/Assets/icon-$size.png \
        %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps/openxlr.png
done
install -Dm644 src/OpenXLR.UI/Assets/icon.svg \
    %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/openxlr.svg

# OpenDeck loads plugins from the user's config dir; ship it for copying.
install -dm755 %{buildroot}%{_datadir}/openxlr
cp -r plugin/com.emaspa.openxlr.sdPlugin %{buildroot}%{_datadir}/openxlr/
find %{buildroot}%{_datadir}/openxlr -type f -exec chmod 644 {} +
find %{buildroot}%{_datadir}/openxlr -type d -exec chmod 755 {} +

%post
/usr/bin/udevadm control --reload 2>/dev/null || :
/usr/bin/udevadm trigger 2>/dev/null || :
cat <<'MSG'
OpenXLR: replug your interface once so the udev rule applies.
Start the daemon:  systemctl --user enable --now openxlr-daemon
Start the mixer:   openxlr   (or from your application menu)
Stream Deck via OpenDeck:
  cp -r /usr/share/openxlr/com.emaspa.openxlr.sdPlugin ~/.config/opendeck/plugins/
MSG

%files
%license LICENSE
%doc README.md
%{_prefix}/lib/openxlr/
%{_bindir}/openxlr
%{_bindir}/openxlr-daemon
%{_udevrulesdir}/70-openxlr.rules
%{_datadir}/wireplumber/wireplumber.conf.d/50-xlr-dock-capture-hold.conf
%{_datadir}/wireplumber/wireplumber.conf.d/51-openxlr-pro-raw-names.conf
%{_userunitdir}/openxlr-daemon.service
%{_datadir}/applications/openxlr.desktop
%{_datadir}/icons/hicolor/*/apps/openxlr.*
%{_datadir}/openxlr/

%changelog
* Sat Sep 05 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.23-1
- Interfaces without settings memory (Wave XLR, XLR Dock) get their last settings back on every connect, and can be reset to the firmware defaults recorded after a power cycle.
- Fedora COPR and Ubuntu PPA install channels; the README opens with the banner.
- Control API token: every client presents a per-session token first; update the window and the OpenDeck plugin together with the daemon.
- A device that hangs three USB transfers in one run is set aside instead of reconnected for ever; the window shows why.
- Mixer settings that cannot be written are retried and shown in the window instead of vanishing; monitor feed commands are validated.
- Private configuration files (0700/0600, UMask in the unit), deadlines on the control socket, bounded helper processes, quoted paths in generated units.
- Release assets carry SHA-256 checksums and a build provenance attestation; the build pipeline is pinned.

* Sat Sep 05 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.22-1
- Daemon watchdog (Carina Schoppe, PR #18): the packaged unit is now a
  systemd notify service with a 60 s watchdog gated on device and mixer
  progress, restart on failure with a start limit, and a graceful stop
  signal; a missing audio server degrades to device control instead of
  restarting.
- Update notice (Carina Schoppe, PR #23): Options, UPDATES offers a manual
  check against the project's releases and an opt-in daily check at
  startup; off by default, nothing is downloaded or installed.
- About window: a thanks line with a link to the credits, and a link to
  the new OpenXLR Discord server and subreddit, also in the README and the
  manual.
- README: logo, Discord and release badges, credits listing every merged
  and open pull request; roadmap brought to 0.1.22.

* Sat Sep 05 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.21-1
- Application routing: an app can be set to "ignore" in its dropdown; its
  streams go back to the system default output and stay wherever the
  desktop routes them, for headsets with separate game and chat sinks
  (issue #21). assignApp and assignStream accept the pseudo-channel.
- Submixer: the monitor mix is now two, Monitor A and Monitor B; each
  ticked monitor output picks which of them feeds it, so two outputs can
  hear different selections. New setMonitorFeed command and monitorFeeds
  state field; feeds are saved with the mixer and with profiles; the Pro's
  own jacks share one feed. Send rows show mix names.
- Window: the monitor output flyout no longer clips or scrolls sideways;
  the Applications window's scrollbar keeps off the Forget buttons; the
  About text lists the XLR Dock MK.2.
- Docs: manual, features, API and README updated; roadmap header at
  0.1.20; credits list every merged and open pull request.

* Sat Sep 05 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.20-1
- XLR Dock MK.2 verified on hardware: its vendor blocks live at the Pro's
  wIndex 0x0103 (the backend now takes the wIndex per model), every
  control confirmed, and one retry on the transient USB I/O error the dock
  returns while its audio interface streams, so the daemon no longer drops
  and reopens it.
- Profiles: an On connect picker marks one profile per device that the
  daemon recalls whenever the device connects fresh (daemon start, a
  replug or power cycle, a switch to it); the reconnect after a passing
  USB error does not count. New setRecallOnConnect command and
  recallOnConnect state field. With the submixer off a profile load
  applies the hardware half instead of failing.
- Docs: hardware support, README, roadmap, manual, features and API
  updated for both.

* Sat Sep 05 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.19-1
- Contributed fixes (Carina Schoppe, PRs #12 to #17): a stream's move is
  verified on the published sink before it is cached; consecutive
  pw-dump batches are folded by registry id; the window's daemon client
  is hardened (idempotent start and disposal, bounded connect and send,
  keepalive, shared catalog replies, 8 MiB message cap); state
  broadcasts are coalesced outside device callbacks; a Restart daemon
  button in the header runs off the UI thread; api.md names the command
  field correctly.
- The restart status line clears when the connection comes back.
- Docs revised for 0.1.14 to 0.1.18.

* Fri Sep 04 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.18-1
- Control API: commands are validated before they reach the mixer.
  Unknown channel or mix ids, uninstalled or unsupported plugins,
  undeclared parameter symbols, non-finite numbers and over-long strings
  or lists come back as errors instead of being silently ignored; each
  client has a command budget and at most 32 clients connect at once.
- Plugin inserts: LV2 plugins that need a host feature PipeWire's chain
  does not provide are hidden from the picker and refused by the daemon
  instead of failing at graph build.
- Diagnostics: USB serials are masked inside the hex vendor-block dump
  too (the XLR Dock's device-info block carries one).

* Fri Sep 04 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.17-1
- Application channels and the virtual microphones appear in desktop
  audio applets (KDE's, for one): the properties the daemon passed to
  PipeWire's null and combine sinks were cut at the first space, so the
  channels ran without their session priority and suspend-on-idle
  settings and carried the virtual flag applets hide. Every property
  now reaches the node; hardware input channels stay hidden.

* Fri Sep 04 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.16-1
- Daemon memory: workstation GC with a 256 MB hard limit, one PipeWire
  graph dump per sweep parsed from bytes, holder processes no longer
  retain their output. Resident size settles around 350 MB instead of
  climbing past 650 MB.
- Control API: a WebSocket handshake with a browser Origin from anywhere
  but localhost is refused; native clients are unaffected.
- Helpers run in the C locale, so a localised pactl no longer breaks the
  faders; meter reads stay frame-aligned; settings saves are serialized
  and flushed on shutdown; OPENXLR_MONITOR_OUTPUT wins over a saved list.
- Packaging: the sysctl drop-in that reserved port 37890 is gone (it
  replaced the kernel's whole reserved-port list); the daemon waits for
  a busy port instead. Release builds run the test suite.

* Fri Sep 04 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.15-1
- UI: starting minimized to the tray no longer shows the window and
  hides it a moment later, which left a hollow window frame at login on
  KDE Wayland (issue #11, reported by chromacurse). The window is never
  mapped until the tray asks for it.
- Docs: a roadmap (docs/roadmap.md) and a Credits section in the README.

* Fri Sep 04 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.14-1
- Wave XLR Pro: the headphone jacks always hear the Monitor mix. The
  jacks are fed by a mix inside the device; Wave Link on Windows can
  leave it without the USB return the Monitor mix streams on, so
  Headphones 1 heard nothing from the software mixer (issue #8). The
  daemon now asserts that membership whenever a jack is the monitor
  output, and the mic's zero-latency hardware path into the jacks
  follows the XLR 1 send's mute in the Monitor mix.
- UI: collapsed sections are remembered across restarts.
- Diagnostics: secrets are redacted as whole tokens only, so a numeric
  serial no longer corrupts the graph dump.

* Wed Sep 02 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.13-1
- Wave XLR (MK.1): the daemon no longer stalls when the once-a-second
  stream sweep piles up on itself (Michael Brooks, PR #7); helper
  processes are read with concurrent stdout/stderr draining and timeouts
  (Carina Schoppe, PR #4); a sweep failure is now logged at warning so
  diagnostics show why a microphone is unwired.
- Software ClipGuard is refused with a reason when swh-plugins is
  missing instead of breaking the microphone route; the UI and deck
  show it disabled (PR #4).
- Input and filter graph changes are transactional: the old microphone
  route stays until the replacement is up, and a failure rolls back
  (PR #4). Stereo-pair selection fixes for the Aux route and hardware
  outputs; a bounded WebSocket send queue; USB short reads are errors
  on the verified devices; USB serial numbers and identities are
  redacted in diagnostics; systemd sandboxing on the daemon unit; a CI
  workflow and an xUnit test project (PR #4).
- UI: a banner with a Restart button when the daemon is an older build
  than the window; the Flow window draws insert chains in the path.
- OpenDeck plugin: profile keys, lit while the profile is the last one
  recalled (the state reports activeProfile).
- Card-profile parking runs only for the Wave XLR Pro.
- Docs: a user manual (docs/manual.md); every document revised.

* Wed Sep 02 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.12-1
- Wave XLR MK.2: phantom power, ClipGuard and compressor exposed at the
  Pro's bit positions, which a tester's block dump matched; the other
  controls were verified on hardware by that tester (issue #2). The XLR
  Dock MK.2 shares the backend.

* Wed Sep 02 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.11-1
- A USB control transfer that never returns (a Wave XLR MK.1 unit hangs
  on every write, issue #6) no longer freezes the daemon and its API: the
  transfer fails after the libusb timeout plus 3 s, the device is dropped
  and reconnected after 10 s, and the fault (setup packet, payload,
  timing, libusb and kernel versions) is logged and included in the
  diagnostics archive.

* Wed Sep 02 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.10-1
- XLR Dock MK.2 (0fd9:00c7): registered on the Wave XLR MK.2 backend
  after a reported USB descriptor matched the MK.2's; udev rule added.
  Not yet run on hardware, testers wanted.
- UI: the daemon-at-login unit and the window autostart entry point at
  the package's bin/ wrappers when present, which the Nix layout needs.

* Wed Sep 02 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.9-1
- UI: "Start daemon at login" on packaged installs wrote a user unit with
  a build-tree ExecStart that does not exist, shadowing the packaged unit:
  the daemon ran until the next reboot, then failed with 203/EXEC in a
  restart loop. The option now enables the packaged unit instead, and a
  stale unit left by an earlier version is repaired the next time the UI
  starts.

* Mon Aug 31 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.8-1
- Mixer wiring pairs ports by channel and ignores duplicated port
  listings. A USB sink caught mid re-enumeration lists its ports twice,
  which used to produce a crossed link (right channel into the left
  speaker) on every selected monitor output.

* Mon Aug 31 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.7-1
- OpenDeck plugin: inserts on the deck. Keys toggle one insert's bypass
  (green in the path, red bypassed) or a whole chain; dials take any
  control of any insert, stepping along its own scale, with the bypass
  on the press. Choices are listed live from the daemon, and a key or
  dial follows its insert by id with a same-plugin fallback after a
  profile recall rebuilds the chain.
- Wave XLR Pro: the UCM profile parking now retries until the card
  settles, fixing a silent microphone after a reboot (at boot the USB
  device appears before the PipeWire card exists).

* Mon Aug 31 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.6-1
- Plugin inserts: LV2 plugin chains on each XLR input (mono) and on
  every mix (stereo), with a plugin picker, per-plugin control windows
  with grouped parameters and a Defaults button, and bypass LEDs. Saved
  with the mixer and recalled by profiles. Needs lilv; lsp-plugins-lv2
  is a good starter set.
- Submixer on/off toggle in Options: off leaves the sound card in its
  stock PipeWire layout and restarts the daemon in hardware-control mode.
- Wave XLR Pro: coexistence with an ALSA UCM split profile (experimental
  profile in packaging/ucm, manual install): the daemon parks the card
  on pro-audio while it runs and restores the split when it stops or
  switches device. Readable names for the raw multichannel nodes.
- Daemon: stop takes about a second instead of 30 (WebSocket loops
  observe shutdown); a busy API port is waited for instead of
  crash-looping, and the package reserves port 37890 from the kernel's
  ephemeral range.
- Mixer: input feeds heal when their source node vanishes.

* Sun Aug 30 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.5-1
- XLR Dock: 48V phantom power and headphone low impedance, reached over
  the original Wave XLR's protocol dialect (discovery credit: openwave
  PR #8) and verified on hardware.
- Wave XLR Pro: the firmware's ~13 s anti-thump mute around every 48V
  change is now shown as a settling hold with a live countdown on the
  mute button, released the moment the input goes live again.
- Mic filter nodes carry an explicit session priority so they can never
  win the default-device election.

* Sat Aug 29 2026 Emanuele Sparvoli <sparvoli@gmail.com> - 0.1.0-1
- Initial Fedora packaging, mirroring the tested AUR and Debian
  packages: framework-dependent .NET publish of the daemon and UI into
  /usr/lib/openxlr with wrapper scripts in /usr/bin, udev rule,
  WirePlumber capture-hold config, per-user systemd unit, desktop
  entry, icons and the bundled OpenDeck plugin.
