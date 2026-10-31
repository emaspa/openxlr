#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Check ordinary scanner markers against a module that exits inside plugin calls."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
env = dict(os.environ)
env.pop("OPENXLR_HOST_TRACE", None)
with tempfile.TemporaryDirectory(prefix="openxlr-scan-phases-") as directory:
    bundle = Path(directory) / "Fixture.vst3" / "Contents" / "x86_64-linux"
    bundle.mkdir(parents=True)
    (bundle / "Fixture.so").symlink_to(root / "tests/scan-plugin.so")
    for kind, source, phases, per_plugin in [
        ("VST3", bundle.parents[1], {
            "module": "module load", "factory": "factory classes",
            "descriptor": "class info and component", "create": "class info and component",
            "initialize": "class info and component", "controller": "controller",
            "buses": "buses and parameters", "parameters": "buses and parameters",
            "widths": "width probes", "release": "release", "module release": "module release",
        }, 5),
        ("CLAP", root / "tests/scan-plugin.so", {
            "module": "module load", "factory": "factory plugins",
            "descriptor": "descriptor and create", "create": "descriptor and create",
            "initialize": "initialize", "buses": "ports, GUI support and parameters",
            "parameters": "ports, GUI support and parameters", "release": "destroy",
            "module release": "module release",
        }, 4),
    ]:
        command = [str(root / "openxlr-lv2-host"), "scan-" + kind.lower(), str(source)]
        for stop, marker in phases.items():
            result = subprocess.run(command, env=env | {"OPENXLR_TEST_SCAN_STOP": stop},
                                    capture_output=True, text=True, timeout=5)
            assert result.returncode == 73, (kind, stop, result.stderr)
            assert result.stderr.splitlines()[-1].endswith(": " + marker), (kind, stop, result.stderr)
        result = subprocess.run(command, env=env | {"OPENXLR_TEST_SCAN_COUNT": "200"},
                                capture_output=True, text=True, timeout=10)
        assert result.returncode == 0, result.stderr
        plugins = json.loads(result.stdout)["plugins"]
        assert len(plugins) == 200 and all(len(p["params"]) == 32 for p in plugins)
        lines = result.stderr.splitlines()
        assert len(lines) == 200 * per_plugin + 4, (kind, len(lines))
        assert lines[-1] == f"trace: scan {kind}: complete"
        print(f"PASS: {kind} phase markers survive {len(phases)} abrupt exits; 200 plugins emit {len(lines)} lines")

# The fixture exits from module initialization. A dangling bridge source must
# be rejected before that code runs, while restored sources still reach it.
with tempfile.TemporaryDirectory(prefix="openxlr-bridge-source-") as directory:
    bundle = Path(directory) / "Fixture.vst3"
    linux = bundle / "Contents/x86_64-linux"
    linux.mkdir(parents=True)
    (linux / "Fixture.so").symlink_to(root / "tests/scan-plugin.so")
    for architecture in ("x86_64-win", "i386-win"):
        win = bundle / "Contents" / architecture
        win.mkdir()
        original = Path(directory) / "original.vst3"
        link = win / "Fixture.vst3"
        link.symlink_to("../../../original.vst3")
        command = [str(root / "openxlr-lv2-host"), "scan-vst3", str(bundle)]
        for suffix in ("", "/"):
            result = subprocess.run(command[:-1] + [str(bundle) + suffix],
                                    env=env | {"OPENXLR_TEST_SCAN_STOP": "module"},
                                    capture_output=True, text=True, timeout=5)
            assert result.returncode == 1, result.stderr
            assert "does not contain a Windows VST3 module" in result.stderr, result.stderr
        original.write_bytes(b"MZ restored source")
        result = subprocess.run(command, env=env | {"OPENXLR_TEST_SCAN_STOP": "module"},
                                capture_output=True, text=True, timeout=5)
        assert result.returncode == 73, result.stderr
        original.unlink()
        link.unlink()
        win.rmdir()
    print("PASS: dangling Windows sources rejected before plugin initialization; restoration loads again")
