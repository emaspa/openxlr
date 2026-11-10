#!/usr/bin/env python3
"""Check the Make plan after the native host and Sound Check have been built."""

import os
from pathlib import Path
import shlex
import subprocess
import tempfile


NATIVE = Path(__file__).resolve().parents[1]
HOST = "openxlr-lv2-host"


def recipes(*arguments, env=None):
    # A dry run assumes the config recipe changed its header even when cmp
    # would keep it. The dependency probes all keep the same configuration.
    plan = subprocess.run(
        ["make", "--no-print-directory", "-n", "-o", "build-config.h", *arguments],
        cwd=NATIVE, env=env, capture_output=True, text=True, check=True,
    )
    return [shlex.split(line) for line in plan.stdout.replace("\\\n", " ").splitlines()]


def check(outputs, *arguments):
    commands = recipes(*arguments)
    actual = {command[command.index("-o") + 1] for command in commands if "-o" in command}
    if actual != outputs:
        raise SystemExit(f"make {' '.join(arguments)}: expected {sorted(outputs)}, got {sorted(actual)}")


def removed(target):
    return {path for command in recipes(target) if command[:1] == ["rm"] for path in command[2:]}


def check_compiler_cache():
    if os.geteuid() == 0:
        return
    with tempfile.TemporaryDirectory() as temporary:
        tools = Path(temporary)
        ccache = tools / "ccache"
        ccache.write_text(f"#!/bin/sh\nprintf '%s\\n' {shlex.quote(str(tools / 'cache'))}\n")
        ccache.chmod(0o700)
        env = dict(os.environ, PATH=f"{tools}:{os.environ['PATH']}")
        env.pop("CI", None)
        env.pop("OPENXLR_BUILD_CACHE", None)

        def compiler(target, *flags):
            commands = recipes("-W", target + ".c", "CC=cc", *flags, HOST, env=env)
            return next(command for command in commands if "-c" in command)

        if compiler("host")[:2] != [str(ccache), "cc"]:
            raise SystemExit("a normal C compile should use the available ccache")
        if compiler("host", "OPENXLR_BUILD_CACHE=0")[0] != "cc":
            raise SystemExit("the cache opt-out should use the compiler directly")
        if compiler("host", "CC=env cc")[:2] != ["env", "cc"]:
            raise SystemExit("an explicit compiler wrapper should keep control")
        (tools / "cache").touch()
        if compiler("host")[0] != "cc":
            raise SystemExit("an unusable ccache directory should use the compiler directly")


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
check_compiler_cache()
print("incremental native build: dependency and clean plans match")
