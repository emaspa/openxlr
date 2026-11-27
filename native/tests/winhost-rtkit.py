#!/usr/bin/env python3
"""The Windows backend's way to real-time through RTKit.

`make -C native WINHOST=1 test-winhost` runs this after tests/winhost. It
starts a D-Bus bus of its own (`dbus-daemon --session`) and points
DBUS_SYSTEM_BUS_ADDRESS, and the session bus address, at it, so nothing
here reaches the system's RTKit. On that bus tests/winhost-rtkit stands in
for RTKit. tests/winhost then runs under `prlimit --rtprio=0:0`, so the
kernel refuses it any real-time priority, as on a desktop where only RTKit
grants it, three times:

- granted: the stand-in grants. The backend asks it for the Windows host's
  process and audio thread, at the waiting thread's priority or RTKit's
  highest, with the process's real-time CPU limit set first, and says the
  priority was granted by RTKit;
- refused: the stand-in refuses. The backend says so and why;
- absent: nothing owns RTKit's name. The backend says RTKit is not there.

In each the audio keeps running. The stand-in changes no thread's
scheduling, and no thread of the check runs at real-time priority.

Usage: winhost-rtkit.py TEST WINHOST-EXE CLAP-PLUGIN VST3-PLUGIN STAND-IN
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

# What tests/winhost.c expects the stand-in to report.
HIGHEST = 7
RTTIME_US = 200000


def start_bus(directory):
    """A bus of the check's own, and its address."""
    socket = Path(directory, "bus")
    bus = subprocess.Popen(
        ["dbus-daemon", "--session", "--nofork", "--nopidfile", "--print-address=1",
         f"--address=unix:path={socket}"],
        stdout=subprocess.PIPE, text=True)
    address = bus.stdout.readline().strip()
    if not address.startswith("unix:path=" + str(socket)):
        bus.kill()
        bus.wait()
        raise SystemExit(f"FAIL: the private bus did not start: {address!r}")
    return bus, address


def start_rtkit(stand_in, address, mode, record, env):
    rtkit = subprocess.Popen([stand_in, address, mode, record, str(HIGHEST), str(RTTIME_US)],
                             stdout=subprocess.PIPE, text=True, env=env)
    ready = rtkit.stdout.readline().strip()
    if ready != "ready":
        rtkit.kill()
        rtkit.wait()
        raise SystemExit(f"FAIL: the stand-in for RTKit did not start: {ready!r}")
    return rtkit


def stop(process):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def main():
    if len(sys.argv) != 6:
        sys.exit(__doc__.split("\n\n")[-1].strip())
    test, winhost, clap, vst3, stand_in = (str(Path(a).resolve()) for a in sys.argv[1:])
    for program in ("dbus-daemon", "prlimit"):
        if not shutil.which(program):
            print(f"SKIP: the Windows backend's RTKit check needs {program}")
            return 0
    with tempfile.TemporaryDirectory(prefix="openxlr-winhost-rtkit-") as directory:
        os.chmod(directory, 0o700)
        bus, rtkit = None, None
        try:
            bus, address = start_bus(directory)
            env = dict(os.environ, DBUS_SYSTEM_BUS_ADDRESS=address, DBUS_SESSION_BUS_ADDRESS=address)
            for case in ("granted", "refused", "absent"):
                record = str(Path(directory, case + ".record"))
                if case != "absent":
                    rtkit = start_rtkit(stand_in, address, "grant" if case == "granted" else "refuse",
                                        record, env)
                print(f"note: the RTKit check, {case}, on a private bus at {address}", flush=True)
                result = subprocess.run(["prlimit", "--rtprio=0:0", "--", test, "--rtkit", case, record,
                                         winhost, clap, vst3], env=env)
                stop(rtkit)
                rtkit = None
                if result.returncode != 0:
                    print(f"FAIL: the RTKit check, {case}, exited with {result.returncode}")
                    return 1
        finally:
            stop(rtkit)
            stop(bus)
    return 0


if __name__ == "__main__":
    sys.exit(main())
