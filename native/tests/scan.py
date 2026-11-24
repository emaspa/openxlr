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
                                capture_output=True, encoding="utf-8", timeout=10)
        assert result.returncode == 0, result.stderr
        plugins = json.loads(result.stdout)["plugins"]
        # The fixture's 33rd parameter is its own bypass, which the scan leaves
        # out. A NaN bound and an infinite default have no JSON number, so the
        # scan leaves those two out as well, and only them; a name that is not
        # UTF-8 is written with U+FFFD in its place.
        assert len(plugins) == 200 and all(len(p["params"]) == 33 for p in plugins)
        assert all(q["id"] not in (32, 33, 34) for p in plugins for q in p["params"])
        bad = {"CLAP": "Bad \ufffd byte", "VST3": "Bad \ufffd\ufffd\ufffd surrogate"}[kind]
        assert all(p["params"][-1]["id"] == 35 and p["params"][-1]["name"] == bad for p in plugins), plugins[0]
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

# LV2: lilv parses every bundle's Turtle in the scanner, never in the daemon.
# A marker before each plugin names its bundle, so a scan that crashed or
# stalled says where; data lilv refuses costs its own plugin; a plugin past
# the port limit is left out; the output is JSON whatever the bundles hold.
with tempfile.TemporaryDirectory(prefix="openxlr-lv2-scan-") as directory:
    lv2 = Path(directory)
    (lv2 / "gain.lv2").symlink_to(root / "tests/gain.lv2")
    prefixes = ("@prefix lv2: <http://lv2plug.in/ns/lv2core#> .\n"
                "@prefix doap: <http://usefulinc.com/ns/doap#> .\n"
                "@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .\n")
    ports = ('lv2:port [ a lv2:AudioPort, lv2:InputPort ; lv2:index 0 ; lv2:symbol "in" ; lv2:name "In" ],\n'
             ' [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 1 ; lv2:symbol "out" ; lv2:name "Out" ]')

    def bundle(name, data):
        path = lv2 / (name + ".lv2")
        path.mkdir()
        (path / "manifest.ttl").write_text(
            prefixes + f"<urn:openxlr:scan:{name}> a lv2:Plugin ; lv2:binary <{name}.so> ; rdfs:seeAlso <{name}.ttl> .\n")
        (path / (name + ".ttl")).write_bytes(prefixes.encode() + data)

    bundle("odd", ('<urn:openxlr:scan:odd> a lv2:Plugin ; doap:name "Tab\\t \\"quote\\" \\\\ é" ;\n' + ports
                   + ',\n [ a lv2:ControlPort, lv2:InputPort ; lv2:index 2 ; lv2:symbol "endless" ; lv2:name "Endless" ;'
                   ' lv2:default 0 ; lv2:minimum 0 ; lv2:maximum 1e999 ],\n'
                   ' [ a lv2:ControlPort, lv2:InputPort ; lv2:index 3 ; lv2:symbol "loose" ; lv2:name "Loose" ] .\n').encode())
    bundle("monster", ("<urn:openxlr:scan:monster> a lv2:Plugin ;\n" + ports + "".join(
        f',\n [ a lv2:ControlPort, lv2:InputPort ; lv2:index {i} ; lv2:symbol "c{i}" ]' for i in range(2, 4100))
        + " .\n").encode())
    bundle("broken", b"<urn:openxlr:scan:broken> a lv2:Plugin ; this is not turtle")
    bundle("naïve", b'<urn:openxlr:scan:na\xc3\xafve> a lv2:Plugin ; doap:name "Bad \xff\xc3\x28" ;\n' + ports.encode() + b" .\n")
    command = [str(root / "openxlr-lv2-host"), "scan-lv2", str(lv2)]
    result = subprocess.run(command, env=env | {"LV2_PATH": "/nonexistent"}, capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr
    plugins = {p["uri"]: p for p in json.loads(result.stdout)["plugins"]}
    assert set(plugins) == {"urn:openxlr:test:gain", "urn:openxlr:test:gain:mono", "urn:openxlr:scan:odd",
                            "urn:openxlr:scan:broken", "urn:openxlr:scan:naïve"}, sorted(plugins)
    odd = plugins["urn:openxlr:scan:odd"]
    assert odd["name"] == 'Tab\t "quote" \\ é', odd["name"]
    assert [p["symbol"] for p in odd["params"]] == ["loose"], odd["params"]
    assert (odd["params"][0]["min"], odd["params"][0]["max"], odd["params"][0]["default"]) == (0, 1, 0)
    assert (odd["audioIns"], odd["audioOuts"], odd["inputs"], odd["outputs"]) == (1, 1, ["in"], ["out"])
    gain = plugins["urn:openxlr:test:gain"]
    assert gain["inputs"] == ["in_l", "in_r"] and gain["params"][0]["default"] == 1, gain
    assert plugins["urn:openxlr:scan:broken"]["audioIns"] == 0
    assert plugins["urn:openxlr:scan:naïve"]["audioIns"] == 0
    # lilv's own messages print the bundle as it is; the markers never do.
    lines = result.stderr.decode("utf-8", "replace").splitlines()
    assert lines[0] == "trace: scan LV2: load bundles" and lines[-1] == "trace: scan LV2: complete", lines
    markers = [line for line in lines if line.startswith("trace: scan LV2 plugin ")]
    assert len(markers) == 6, markers
    assert all(": bundle " + str(lv2) + "/" in m for m in markers), markers
    assert all(m.isascii() for m in markers) and any(m.endswith("/na%C3%AFve.lv2/") for m in markers), markers
    # Each bundle's manifest is read under a marker of its own, naming the
    # bundle by the same text its plugins' markers use.
    manifests = [line for line in lines if line.startswith("trace: scan LV2 manifest ")]
    assert len(manifests) == 5, manifests
    assert [m.split(": bundle ")[0] for m in manifests] == [f"trace: scan LV2 manifest {i}" for i in range(5)]
    assert {m.split(": bundle ")[1] for m in manifests} == {m.split(": bundle ")[1] for m in markers}, (manifests, markers)

    result = subprocess.run(command[:2] + [str(lv2 / "missing")], env=env, capture_output=True, timeout=10)
    assert result.returncode == 0 and json.loads(result.stdout) == {"plugins": []}, result
    result = subprocess.run(command + ["extra"], env=env, capture_output=True, timeout=10)
    assert result.returncode == 2, result
    print(f"PASS: LV2 scan describes {len(plugins)} plugins in readable JSON, one bundle marker per plugin, "
          "refused data and the port limit cost their own plugin")

    for arguments in (["--skip"], ["--skip", "a", "--skip"], [str(lv2), "--skip", "a"]):
        result = subprocess.run(command[:2] + arguments, env=env, capture_output=True, timeout=10)
        assert result.returncode == 2, (arguments, result)

    # A bundle that stalls lilv. A manifest that is a pipe nobody writes
    # blocks while the manifests are read; a plugin data file that is one
    # blocks when that plugin is described. Each scan names the bundle in its
    # last marker, and `--skip` with that text leaves the bundle out.
    def last_marker(stderr):
        traces = [line for line in stderr.decode("utf-8", "replace").splitlines() if line.startswith("trace: scan LV2")]
        return traces[-1]

    stall = lv2 / "stall.lv2"
    stall.mkdir()
    os.mkfifo(stall / "manifest.ttl")
    slow = lv2 / "slow.lv2"
    slow.mkdir()
    (slow / "manifest.ttl").write_text(
        prefixes + "<urn:openxlr:scan:slow> a lv2:Plugin ; lv2:binary <slow.so> ; rdfs:seeAlso <slow.ttl> .\n")
    os.mkfifo(slow / "slow.ttl")
    skips = []
    for phase, name in (("manifest", "stall"), ("plugin", "slow")):
        try:
            subprocess.run(command[:2] + skips + [str(lv2)], env=env, capture_output=True, timeout=3)
            raise AssertionError(f"the scan finished with {name}.lv2 in the way")
        except subprocess.TimeoutExpired as stalled:
            marker = last_marker(stalled.stderr)
        assert marker.startswith(f"trace: scan LV2 {phase} ") and marker.endswith(f": bundle {lv2}/{name}.lv2/"), marker
        skips += ["--skip", marker.split(": bundle ", 1)[1]]
    result = subprocess.run(command[:2] + skips + [str(lv2)], env=env, capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr
    assert set(p["uri"] for p in json.loads(result.stdout)["plugins"]) == set(plugins), result.stdout
    lines = result.stderr.decode("utf-8", "replace").splitlines()
    assert sorted(line for line in lines if line.startswith("trace: scan LV2: skip bundle ")) == [
        f"trace: scan LV2: skip bundle {lv2}/{name}.lv2/" for name in ("slow", "stall")], lines
    assert not any(line.endswith(("/slow.lv2/", "/stall.lv2/")) for line in lines if line.startswith(
        ("trace: scan LV2 manifest ", "trace: scan LV2 plugin "))), lines
    # The marker text is the one the scan printed, escapes included.
    result = subprocess.run(command[:2] + skips + ["--skip", f"{lv2}/na%C3%AFve.lv2/", str(lv2)],
                            env=env, capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr
    assert "urn:openxlr:scan:naïve" not in {p["uri"] for p in json.loads(result.stdout)["plugins"]}
    print("PASS: LV2 scan stalled in a manifest and in a plugin's data names each bundle last; "
          "--skip leaves them out and the rest is described")

# An LV2 path is split on colons. An empty entry, at the start, in the middle
# or at the end, names no directory and the entries after it are still read.
with tempfile.TemporaryDirectory(prefix="openxlr-lv2-path-") as directory:
    first, second = Path(directory) / "a", Path(directory) / "b"
    first.mkdir()
    second.mkdir()
    (first / "gain.lv2").symlink_to(root / "tests/gain.lv2")
    (second / "latency.lv2").symlink_to(root / "tests/latency.lv2")
    both = {"urn:openxlr:test:gain", "urn:openxlr:test:gain:mono", "urn:openxlr:test:latency"}
    for path, expected in ((f"{first}:{second}", both), (f"{first}::{second}", both), (f":{first}", both - {
            "urn:openxlr:test:latency"}), (f"::{second}", {"urn:openxlr:test:latency"}),
            (f"{first}:", both - {"urn:openxlr:test:latency"}), (f"{first}:{second}:", both)):
        result = subprocess.run([str(root / "openxlr-lv2-host"), "scan-lv2", path], env=env,
                                capture_output=True, timeout=30)
        assert result.returncode == 0, (path, result.stderr)
        found = {p["uri"] for p in json.loads(result.stdout)["plugins"]}
        assert found == expected, (path, sorted(found))
    print("PASS: LV2 path entries after an empty one are still scanned")
