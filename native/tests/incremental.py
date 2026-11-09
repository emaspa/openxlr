#!/usr/bin/env python3
"""Check the Make plan after the native host and Sound Check have been built."""

from pathlib import Path
import shlex
import subprocess


NATIVE = Path(__file__).resolve().parents[1]
HOST = "openxlr-lv2-host"


def recipes(*arguments):
    # A dry run assumes the config recipe changed its header even when cmp
    # would keep it. The dependency probes all keep the same configuration.
    plan = subprocess.run(
        ["make", "--no-print-directory", "-n", "-o", "build-config.h", *arguments],
        cwd=NATIVE, capture_output=True, text=True, check=True,
    )
    return [shlex.split(line) for line in plan.stdout.replace("\\\n", " ").splitlines()]


def check(outputs, *arguments):
    commands = recipes(*arguments)
    actual = {command[command.index("-o") + 1] for command in commands if "-o" in command}
    if actual != outputs:
        raise SystemExit(f"make {' '.join(arguments)}: expected {sorted(outputs)}, got {sorted(actual)}")


def removed(target):
    return {path for command in recipes(target) if command[:1] == ["rm"] for path in command[2:]}


check(set(), HOST)
check({"host.o", HOST}, "-W", "icon.h", HOST)
check({"vst3.o", HOST}, "-W", "vst3/pluginterfaces/vst/vstspeaker.h", HOST)
check({"tests/sound-check.o"}, "-W", "tests/../level.c", "tests/sound-check.o")
rust_outputs = {"deepfilter/target", "deepfilter/model/verified"}
native_outputs = removed("clean-native")
if not {HOST, "host.o", "host.o.d"} <= native_outputs or native_outputs & rust_outputs:
    raise SystemExit("clean-native must remove the native objects and keep the Rust outputs")
if removed("clean") != native_outputs | rust_outputs:
    raise SystemExit("clean must also remove the Rust outputs")
print("incremental native build: dependency and clean plans match")
