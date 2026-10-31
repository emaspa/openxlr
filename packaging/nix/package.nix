{ lib
, buildDotnetModule
, dotnetCorePackages
, gcc
, pkg-config
, fontconfig
, icu
, libpulseaudio
, libusb1
, lilv
, libsndfile
, lv2
, pipewire
, libx11
, libice
, libsm
, alsa-utils
, pulseaudio
, xdg-utils
, glib
, xorg
, gdk-pixbuf
, librsvg
, python3
, rustPlatform
, fetchurl
, sourceCommit ? null
, dirtySource ? false
}:

let
  # DeepFilterNet's model, the file native/Makefile fetches, with the same
  # SHA-256 (DF_MODEL_SHA256 there).
  deepfilterVersion = "0.5.6";
  deepfilterModel = fetchurl {
    url = "https://raw.githubusercontent.com/Rikorose/DeepFilterNet/v${deepfilterVersion}/models/DeepFilterNet3_onnx.tar.gz";
    hash = "sha256-yU2R9wkRAByUbg+rtKqa3DcEX0WgO1YAjLDIJEy2NhY=";
  };

  # DeepFilterNet's Rust static library, built as a derivation of its own
  # from Cargo.lock; native/Makefile links it through DEEPFILTER_LIB
  # instead of running cargo.
  deepfilter = rustPlatform.buildRustPackage {
    pname = "openxlr-deepfilter";
    version = deepfilterVersion;
    # Only the crate's sources: a checkout's target/ or vendor/ stays out.
    src = lib.fileset.toSource {
      root = ../../native/deepfilter;
      fileset = lib.fileset.unions [
        ../../native/deepfilter/Cargo.toml
        ../../native/deepfilter/Cargo.lock
        ../../native/deepfilter/src
        ../../native/deepfilter/libdf
      ];
    };
    cargoLock.lockFile = ../../native/deepfilter/Cargo.lock;
    postPatch = ''
      install -Dm644 ${deepfilterModel} model/DeepFilterNet3_onnx.tar.gz
    '';
    # The checks run against the linked helper (make test-denoise).
    doCheck = false;
    meta = {
      description = "DeepFilterNet noise removal for OpenXLR's native helper";
      license = with lib.licenses; [ mit asl20 ];
      platforms = lib.platforms.linux;
    };
  };
in
buildDotnetModule {
  pname = "openxlr";
  version = "0.3.5";

  src = ../..;

  projectFile = [
    "src/OpenXLR.Daemon/OpenXLR.Daemon.csproj"
    "src/OpenXLR.UI/OpenXLR.UI.csproj"
    "src/OpenXLR.Tui/OpenXLR.Tui.csproj"
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

  # The optional LV2 host, a small C program the daemon build compiles beside
  # itself. Its libraries are already runtime dependencies below.
  dotnetFlags = [ "-p:EnableNativeLv2Host=true" ]
    ++ lib.optional (sourceCommit != null) "-p:SourceRevisionId=${sourceCommit}";
  nativeBuildInputs = [ gcc pkg-config python3 ];
  buildInputs = [ pipewire lilv lv2 libx11 libsndfile ];
  # make reads both from the environment: DeepFilterNet is built in, from
  # the library above.
  env = {
    DEEPFILTER = "1";
    DEEPFILTER_LIB = "${deepfilter}/lib/libopenxlr_deepfilter.a";
  };

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.aspnetcore_10_0;

  executables = [ "OpenXLR.Daemon" "OpenXLR.UI" "openxlr-tui" ];

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
    # The window draws SVG application icons through GdkPixbuf, which it
    # loads by name, so the library and the GLib it calls are on the path.
    glib
    gdk-pixbuf
  ];

  # The daemon shells out to amixer, parec, pw-cli, pw-loopback and gdbus
  # (focused-application routing); the UI opens links with xdg-open.
  makeWrapperArgs = [
    "--prefix PATH : ${lib.makeBinPath [ alsa-utils pipewire pulseaudio xdg-utils glib xorg.xprop ]}"
    # GdkPixbuf finds its SVG loader only through a loaders cache; librsvg's
    # lists its own loader beside GdkPixbuf's, as wrapGAppsHook would set it.
    "--set GDK_PIXBUF_MODULE_FILE ${librsvg.out}/${gdk-pixbuf.binaryDir}/loaders.cache"
  ];

  # The bin/ wrappers are created during fixup, so the friendly names
  # have to be linked after that.
  postFixup = ''
    ln -s $out/bin/OpenXLR.Daemon $out/bin/openxlr-daemon
    makeWrapper $out/bin/OpenXLR.Daemon $out/bin/openxlr-control --add-flags "--control-stdio"
    ln -s $out/bin/OpenXLR.UI $out/bin/openxlr
    python3 packaging/install_receipt.py --root / --prefix "$out" --manager nix --version "$version" \
      ${lib.optionalString (sourceCommit != null) "--commit ${lib.escapeShellArg sourceCommit}"} \
      ${lib.optionalString dirtySource "--dirty"}
  '';

  postInstall = ''
    # Native messaging for the separately installed browser companion.
    sed "s|/usr/bin/openxlr|$out/bin/openxlr|" browser/native-host.json > browser-host.json
    install -Dm644 browser-host.json \
      $out/etc/chromium/native-messaging-hosts/com.emaspa.openxlr.browser.json
    install -Dm644 browser-host.json \
      $out/etc/opt/chrome/native-messaging-hosts/com.emaspa.openxlr.browser.json

    # Chromium and Chrome offer the companion from the Chrome Web Store.
    install -Dm644 browser/external-extension.json \
      $out/share/chromium/extensions/beeohanoffjpcofbjiiamkdammhcihaf.json
    install -Dm644 browser/external-extension.json \
      $out/share/google-chrome/extensions/beeohanoffjpcofbjiiamkdammhcihaf.json

    install -Dm644 packaging/70-openxlr.rules -t $out/lib/udev/rules.d
    install -Dm644 packaging/50-xlr-dock-capture-hold.conf \
      packaging/51-openxlr-pro-raw-names.conf \
      packaging/52-openxlr-mk1-capture-hold.conf \
      packaging/53-openxlr-wave3-capture-hold.conf \
      packaging/55-openxlr-wave3-mk2-playback.conf \
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
    mkdir -p $out/share/gnome-shell/extensions/openxlr-focus@emaspa.github.com
    cp packaging/gnome/openxlr-focus@emaspa.github.com/* \
      $out/share/gnome-shell/extensions/openxlr-focus@emaspa.github.com/
  '';

  passthru = { inherit deepfilter deepfilterModel; };

  meta = {
    description = "Linux Wave Link replacement for Elgato XLR interfaces and Wave microphones";
    homepage = "https://github.com/emaspa/openxlr";
    # The native helper also carries RNNoise (BSD-3-Clause), Signalsmith
    # Stretch and Linear (MIT), libDF, the DeepFilterNet model and the Rust
    # crates (MIT, Apache-2.0 or either);
    # the soundboard's starter sounds are CC0-1.0.
    license = with lib.licenses; [ gpl3Only bsd3 mit asl20 cc0 ];
    platforms = lib.platforms.linux;
    mainProgram = "openxlr";
  };
}
