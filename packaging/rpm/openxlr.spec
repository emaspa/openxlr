# The publish output is prebuilt .NET; its bundled native libraries must
# not be stripped, scanned for shared-library deps, or given debuginfo.
%global debug_package %{nil}
%global __strip /usr/bin/true
%global _build_id_links none

Name:           openxlr
Version:        0.1.10
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
install -Dm644 packaging/60-openxlr-port.conf \
    %{buildroot}%{_sysctldir}/60-openxlr.conf

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
%sysctl_apply 60-openxlr.conf
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
%{_sysctldir}/60-openxlr.conf
%{_userunitdir}/openxlr-daemon.service
%{_datadir}/applications/openxlr.desktop
%{_datadir}/icons/hicolor/*/apps/openxlr.*
%{_datadir}/openxlr/

%changelog
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
