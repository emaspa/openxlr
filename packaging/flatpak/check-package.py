#!/usr/bin/env python3
"""Check the built Flatpak payload, including dependencies missing from the runtime."""
from pathlib import Path
import sys

root = Path(sys.argv[1])
required = [
    "bin/openxlr", "bin/openxlr-check", "bin/amixer", "bin/pw-cli", "bin/pw-link",
    "lib/dotnet/dotnet", "lib/openxlr/ui/OpenXLR.UI", "lib/openxlr/daemon/OpenXLR.Daemon",
    "lib/pipewire-0.3/libpipewire-module-filter-chain.so",
    "lib/spa-0.2/filter-graph/libspa-filter-graph-plugin-lv2.so",
    "lib/ladspa/hard_limiter_1413.so", "lib/liblilv-0.so.0",
    "share/applications/io.github.emaspa.OpenXLR.desktop",
    "share/metainfo/io.github.emaspa.OpenXLR.metainfo.xml",
]
for path in required:
    if not (root / path).exists():
        raise SystemExit(f"Missing Flatpak dependency: {path}")
for path in root.rglob("*"):
    if path.name == "openxlr-lv2-host" or "yabridge" in path.name or path.name.endswith(".clap"):
        raise SystemExit(f"Unexpected native plugin host or bridge: {path}")
for name in ["serd", "zix", "sord", "lv2", "sratom", "lilv", "pipewire-client", "amixer", "dotnet", "hard-limiter", "openxlr"]:
    if not any(p.is_file() for p in (root / "share/licenses" / name).rglob("*")):
        raise SystemExit(f"Missing dependency notices: {name}")
if not list((root / "lib/lv2").glob("*.lv2")):
    raise SystemExit("No LV2 bundles or specifications installed")
print("Flatpak payload: UI, daemon, LV2 chain, limiter and desktop integration present; native host excluded")
