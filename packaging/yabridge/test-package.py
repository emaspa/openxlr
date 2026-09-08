#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Check a staged companion package and its private sync destinations."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile


def pe_fixture(symbol):
    # Enough PE32+ metadata for yabridgectl's architecture inspection. Sync
    # creates wrappers but never loads this fixture as a plugin.
    data = bytearray(1024)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3c, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84, 0x8664, 1, 0, 0, 0, 240, 0x2022)
    struct.pack_into("<H", data, 0x98, 0x20b)
    struct.pack_into("<Q", data, 0x98 + 24, 0x180000000)
    struct.pack_into("<II", data, 0x98 + 32, 4096, 512)
    struct.pack_into("<II", data, 0x98 + 56, 8192, 512)
    struct.pack_into("<I", data, 0x98 + 108, 16)
    struct.pack_into("<II", data, 0x98 + 112, 0x1000, 0x100)
    struct.pack_into("<8sIIIIIIHHI", data, 0x188, b".edata\0\0", 512, 0x1000, 512, 512, 0, 0, 0, 0, 0x40000040)
    struct.pack_into("<IIHHIIIIIII", data, 0x200, 0, 0, 0, 0, 0x1080, 1, 1, 1, 0x1040, 0x1044, 0x1048)
    struct.pack_into("<IIH", data, 0x240, 0x1100, 0x1090, 0)
    data[0x280:0x288] = b"fixture\0"
    exported = symbol.encode() + b"\0"
    data[0x290:0x290 + len(exported)] = exported
    data[0x300] = 0xc3
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", type=Path)
    parser.add_argument("--prefix", default="usr", help="relative installation prefix; empty for a Nix output")
    parser.add_argument("--windows-directory", type=Path, help="optionally sync real Windows plugins too")
    args = parser.parse_args()
    package = args.stage.resolve() / args.prefix / "lib/openxlr/yabridge"
    launcher = args.stage.resolve() / args.prefix / "bin/openxlr-yabridgectl"
    receipt = json.loads((package / "openxlr-yabridge.json").read_text())
    assert receipt["formatVersion"] == 1 and receipt["wineInputFix"]
    for kind in ("vst2", "vst3", "clap"):
        for prefix in ("libyabridge-", "libyabridge-chainloader-"):
            assert (package / (prefix + kind + ".so")).read_bytes()[:4] == b"\x7fELF"
    for name in ("yabridgectl", "yabridge-host.exe"):
        assert os.access(package / name, os.X_OK), name

    global_config = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "yabridgectl/config.toml"
    original = global_config.read_bytes() if global_config.exists() else None
    with tempfile.TemporaryDirectory(prefix="openxlr-package-test-") as temp:
        root = Path(temp)
        env = os.environ.copy()
        env.update(OPENXLR_YABRIDGE=str(package), XDG_CONFIG_HOME=str(root / "config"), XDG_DATA_HOME=str(root / "data"))
        windows = root / "windows"
        windows.mkdir()
        (windows / "OpenXLRFixture.vst3").write_bytes(pe_fixture("GetPluginFactory"))
        (windows / "OpenXLRFixture.clap").write_bytes(pe_fixture("clap_entry"))

        def run(*arguments):
            return subprocess.run([str(launcher), *map(str, arguments)], env=env, check=True,
                                  capture_output=True, text=True, timeout=120).stdout

        assert "5.1.1" in run("--version")
        run("add", windows)
        synced = run("sync", "--no-verify")
        private = root / "data/openxlr/yabridge"
        vst3 = private / "vst3/OpenXLRFixture.vst3/Contents/x86_64-linux/OpenXLRFixture.so"
        clap = private / "clap/OpenXLRFixture.clap"
        for wrapper, source in [(vst3, package / "libyabridge-chainloader-vst3.so"),
                                (clap, package / "libyabridge-chainloader-clap.so")]:
            assert wrapper.exists(), str(wrapper) + "\n" + synced + "\n" + str(list(root.rglob("*")))
            assert hashlib.sha256(wrapper.read_bytes()).digest() == hashlib.sha256(source.read_bytes()).digest()
        assert (root / "config/openxlr/bridge/yabridgectl/config.toml").exists()
        assert not (root / "config/yabridgectl").exists()
        if args.windows_directory:
            run("add", args.windows_directory.resolve())
            run("sync")
        assert (global_config.read_bytes() if global_config.exists() else None) == original
    print("PASS: matched payload, private registry, private VST3/CLAP wrappers, system registry untouched")


if __name__ == "__main__":
    main()
