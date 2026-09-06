{ lib
, buildDotnetModule
, dotnetCorePackages
, fontconfig
, icu
, libpulseaudio
, libusb1
, lilv
, pipewire
, libx11
, libice
, libsm
, alsa-utils
, pulseaudio
, xdg-utils
}:

buildDotnetModule {
  pname = "openxlr";
  version = "0.1.24";

  src = ../..;

  projectFile = [
    "src/OpenXLR.Daemon/OpenXLR.Daemon.csproj"
    "src/OpenXLR.UI/OpenXLR.UI.csproj"
  ];
  nugetDeps = ./deps.json;

  # deps.json already pins every package by hash and the builder restores
  # from its own feed, whose repacked archives fail the lock files' content
  # hash check (NU1403). The lock files stay authoritative for CI and the
  # distribution packages; this build drops them and restores without one.
  postPatch = ''
    rm -f src/*/packages.lock.json
  '';
  dotnetRestoreFlags = [ "-p:RestorePackagesWithLockFile=false" ];
  dotnetBuildFlags = [ "-p:RestorePackagesWithLockFile=false" ];

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.aspnetcore_10_0;

  executables = [ "OpenXLR.Daemon" "OpenXLR.UI" ];

  runtimeDeps = [
    fontconfig
    icu
    libpulseaudio
    libusb1
    lilv          # the daemon reads the LV2 plugin catalog through liblilv
    pipewire
    libx11
    libice
    libsm
  ];

  # The daemon shells out to amixer, parec, pw-cli and pw-loopback; the UI
  # opens links with xdg-open.
  makeWrapperArgs = [
    "--prefix PATH : ${lib.makeBinPath [ alsa-utils pipewire pulseaudio xdg-utils ]}"
  ];

  # The bin/ wrappers are created during fixup, so the friendly names
  # have to be linked after that.
  postFixup = ''
    ln -s $out/bin/OpenXLR.Daemon $out/bin/openxlr-daemon
    ln -s $out/bin/OpenXLR.UI $out/bin/openxlr
  '';

  postInstall = ''
    install -Dm644 packaging/70-openxlr.rules -t $out/lib/udev/rules.d
    install -Dm644 packaging/50-xlr-dock-capture-hold.conf \
      packaging/51-openxlr-pro-raw-names.conf \
      -t $out/share/wireplumber/wireplumber.conf.d
    install -Dm644 packaging/pipewire-pulse-openxlr.conf \
      $out/lib/systemd/user/pipewire-pulse.service.d/openxlr.conf
    install -Dm644 packaging/openxlr.desktop -t $out/share/applications
    for s in 16 32 48 64 128 256; do
      install -Dm644 src/OpenXLR.UI/Assets/icon-$s.png \
        $out/share/icons/hicolor/''${s}x''${s}/apps/openxlr.png
    done
    install -Dm644 src/OpenXLR.UI/Assets/icon.svg \
      $out/share/icons/hicolor/scalable/apps/openxlr.svg

    mkdir -p $out/share/openxlr
    cp -r plugin/com.emaspa.openxlr.sdPlugin $out/share/openxlr/
  '';

  meta = {
    description = "Control suite and PipeWire submixer for Elgato XLR interfaces";
    homepage = "https://github.com/emaspa/openxlr";
    license = lib.licenses.gpl3Only;
    platforms = lib.platforms.linux;
    mainProgram = "openxlr";
  };
}
