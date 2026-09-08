{ lib, runCommand, fetchFromGitHub, yabridge, yabridgectl, wineWow64Packages }:
let
  pin = builtins.fromJSON (builtins.readFile ../yabridge/source.json);
  src = fetchFromGitHub {
    owner = "robbert-vdh";
    repo = "yabridge";
    rev = pin.commit;
    hash = "sha256-grfLlJNPH2Tpn9D2GBgLFPDVVQezU/OcZ3VA0O87AvQ=";
  };
  asio = fetchFromGitHub {
    owner = "chriskohlhoff";
    repo = "asio";
    rev = "ed6aa8a13d51dfc6c00ae453fc9fb7df5d6ea963";
    hash = "sha256-B9tFXcmBn7n4wEdnfjw5o90fC/cG5+WMdu/K4T6Y+jI=";
  };
  # Use the maintained Wine dependency rather than nixpkgs' old Wine 9.21
  # compatibility selection for the unpatched upstream release.
  winePackages = wineWow64Packages // { yabridge = wineWow64Packages.stable; };
  bridge = (yabridge.override { wineWow64Packages = winePackages; }).overrideAttrs (old: {
    version = pin.version;
    inherit src;
    patches = builtins.filter (patch: !(lib.hasInfix "libyabridge-drop-32-bit-support" (toString patch))) old.patches;
    postUnpack = old.postUnpack + ''
      rm -rf "$sourceRoot/subprojects/asio"
      cp -R --no-preserve=mode,ownership ${asio} "$sourceRoot/subprojects/asio"
    '';
  });
  controller = (yabridgectl.override { yabridge = bridge; wineWow64Packages = winePackages; }).overrideAttrs (old: {
    patches = old.patches ++ [ ../yabridge/private-plugin-home.patch ];
  });
  receipt = builtins.toJSON {
    formatVersion = 1;
    version = "${pin.version}-${toString pin.revision}";
    sourceCommit = pin.commit;
    wineInputFix = true;
    windowsArchitectures = [ "x86_64" ];
  };
in runCommand "openxlr-yabridge-${pin.version}" {
  passthru = { inherit bridge controller; };
  meta = {
    description = "Tested Windows plugin bridge for OpenXLR";
    homepage = "https://github.com/emaspa/openxlr";
    license = [ lib.licenses.gpl3Only lib.licenses.gpl3Plus ];
    platforms = [ "x86_64-linux" ];
  };
} ''
  mkdir -p "$out/lib/openxlr/yabridge" "$out/bin" "$out/share/licenses/openxlr-yabridge"
  cp ${bridge}/lib/libyabridge*.so "$out/lib/openxlr/yabridge/"
  cp ${bridge}/bin/yabridge-host.exe* "$out/lib/openxlr/yabridge/"
  cp ${controller}/bin/yabridgectl "$out/lib/openxlr/yabridge/"
  printf '%s\n' '${receipt}' > "$out/lib/openxlr/yabridge/openxlr-yabridge.json"
  substitute ${../yabridge/openxlr-yabridgectl.in} "$out/bin/openxlr-yabridgectl" \
    --subst-var-by BRIDGE_DIR "$out/lib/openxlr/yabridge"
  chmod +x "$out/bin/openxlr-yabridgectl"
  patchShebangs "$out/bin/openxlr-yabridgectl"
  cp ${src}/COPYING ${../yabridge/source.json} ${../yabridge/private-plugin-home.patch} \
    "$out/share/licenses/openxlr-yabridge/"
''
