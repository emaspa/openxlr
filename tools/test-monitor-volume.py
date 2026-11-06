#!/usr/bin/env python3
"""Run monitor controls, DSP audio and profile startup on private PipeWire sockets.

Arguments are passed on to `dotnet test`, so a build that lives somewhere
other than the default output can be tested with `--artifacts-path <dir>`.
OPENXLR_TEST_FILTER selects a focused regression instead of the full audio suite.
OPENXLR_TEST_SHARD=N/M runs the Nth of M slices of the selected tests, so CI
can spread the suite over parallel jobs, each with its own private servers.
"""
import os
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parent.parent
PROJECT = "src/OpenXLR.Tests/OpenXLR.Tests.csproj"
DEFAULT_FILTER = ("FullyQualifiedName~MonitorVolumeIntegrationTests|FullyQualifiedName~FilterStateIntegrationTests"
                  "|FullyQualifiedName~ProfileStartupTests|FullyQualifiedName~DspAudioIntegrationTests"
                  "|FullyQualifiedName~MixDelayIntegrationTests|FullyQualifiedName~PluginLatencyIntegrationTests"
                  "|FullyQualifiedName~SoundCheckIntegrationTests|FullyQualifiedName~SoundboardIntegrationTests")


def shard_filter(test_filter, shard, arguments):
    """Narrow the filter to every Mth test, by sorted name, starting at the Nth."""
    index, count = (int(part) for part in shard.split("/"))
    if not 1 <= index <= count:
        raise ValueError("OPENXLR_TEST_SHARD must be N/M with 1 <= N <= M, not " + shard)
    listing = subprocess.run(["dotnet", "test", PROJECT, "-c", "Release", "--no-build", "--list-tests",
                              "--filter", test_filter, *arguments],
                             check=True, capture_output=True, text=True, cwd=ROOT).stdout
    # xUnit lists each test by its fully qualified name, indented under a
    # header, and each theory case with its arguments after the name. The
    # filter takes the method name alone and runs every case of a theory.
    names = sorted({line.strip().split("(")[0] for line in listing.splitlines() if line.startswith("    ")})
    if not names:
        raise RuntimeError("no tests match " + test_filter)
    selected = names[index - 1::count]
    print(f"Shard {shard}: {len(selected)} of {len(names)} tests", flush=True)
    return "|".join("FullyQualifiedName=" + name for name in selected)


class Supervised:
    """A WirePlumber that starts again when it exits, as systemd starts the
    user unit again after `systemctl --user restart`. Its process id is in
    `pidfile`, so a test can restart it the way restartAudioServices does
    and see the new one come up."""

    def __init__(self, command, env, log, pidfile):
        self.command, self.env, self.log, self.pidfile = command, env, log, Path(pidfile)
        self.lock = threading.Lock()
        self.stopping = False
        self.process = self.start()
        self.thread = threading.Thread(target=self.watch, daemon=True)
        self.thread.start()

    def start(self):
        process = subprocess.Popen(self.command, env=self.env, stdout=self.log, stderr=self.log)
        temporary = self.pidfile.with_suffix(".tmp")
        temporary.write_text(str(process.pid))
        temporary.replace(self.pidfile)
        return process

    def watch(self):
        while True:
            process = self.process
            process.wait()
            with self.lock:
                if self.stopping:
                    return
                time.sleep(0.2)
                self.process = self.start()

    def poll(self):
        return self.process.poll() if self.stopping else None

    def terminate(self):
        with self.lock:
            self.stopping = True
        self.process.terminate()

    def wait(self, timeout=None):
        return self.process.wait(timeout=timeout)

    def kill(self):
        self.process.kill()


def main():
    with tempfile.TemporaryDirectory(prefix="openxlr-monitor-test-") as runtime:
        env = dict(os.environ, XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime,
                   XDG_CONFIG_HOME=runtime + "/config", XDG_STATE_HOME=runtime + "/state",
                   XDG_DATA_HOME=runtime + "/data",
                   CLAP_PATH=runtime + "/plugins", VST3_PATH=runtime + "/plugins",
                   PULSE_SERVER="unix:" + runtime + "/pulse/native",
                   PIPEWIRE_REMOTE="pipewire-0", OPENXLR_TEST_MONITOR_VOLUME="1")
        fixtures = str(ROOT / "native" / "tests")
        test_filter = os.environ.get("OPENXLR_TEST_FILTER", DEFAULT_FILTER)
        if sys.argv[1:2] != ["--command"] and os.environ.get("OPENXLR_TEST_SHARD"):
            test_filter = shard_filter(test_filter, os.environ["OPENXLR_TEST_SHARD"], sys.argv[1:])
        if sys.argv[1:2] != ["--command"]:
            # Capture chains, Sound Check and input chains load these bundles
            # as inserts. Without their binaries PipeWire builds the chain and
            # only fails when the graph starts, so the tests see silence.
            subprocess.run(["make", "-s", "--no-print-directory", "-C", str(Path(fixtures).parent),
                            "test-fixtures"], check=True)
        env["LV2_PATH"] = fixtures + ":" + env.get("LV2_PATH", "/usr/lib/lv2:/usr/local/lib/lv2")
        processes = []
        with open(runtime + "/servers.log", "w+") as log:
            try:
                bus = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address"],
                                       stdout=subprocess.PIPE, stderr=log, text=True)
                processes.append(bus)
                env["DBUS_SESSION_BUS_ADDRESS"] = bus.stdout.readline().strip()
                # The policy-only session manager never discovers ALSA devices.
                for command, socket in [("pipewire", "pipewire-0"), ("pipewire-pulse", "pulse/native")]:
                    process = subprocess.Popen([command], env=env, stdout=log, stderr=log)
                    processes.append(process)
                    deadline = time.monotonic() + 10
                    while not Path(runtime, socket).exists():
                        if process.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError(command + " did not create its socket")
                        time.sleep(0.05)
                help_text = subprocess.check_output(["wireplumber", "--help"], text=True)
                modern = "--profile" in help_text
                # Seed the format this policy version actually reads. Both
                # internal filters and combines must ignore old desktop state.
                state = Path(env["XDG_STATE_HOME"], "wireplumber",
                             "stream-properties" if modern else "restore-stream")
                state.parent.mkdir(parents=True, exist_ok=True)
                keys = ["Audio/Sink:media.name:Combine stream"]
                for description in ("OpenXLR Mic Filter", "OpenXLR mix latency delay"):
                    for kind, suffix in (("Audio/Sink", "input"), ("Audio/Source", "output"),
                                         ("Stream/Input/Audio", "input"), ("Stream/Output/Audio", "output")):
                        keys.append(f"{kind}:media.name:{description} {suffix}")
                values = {"volume": 0.125, "mute": True, "channelVolumes": [0.125]}
                lines = ["[stream-properties]" if modern else "[restore-stream]"]
                for key in keys:
                    key = key.replace(" ", r"\s")
                    if modern:
                        lines.append(f"{key}={json.dumps(values)}")
                    else:
                        lines.extend((f"{key}:volume=0.125", f"{key}:mute=true",
                                      f"{key}:channelVolumes=0.125;"))
                state.write_text("\n".join(lines) + "\n")
                if modern:
                    policies = [["--profile", "policy"]]
                else:
                    # Use the same compatibility rule as a legacy package.
                    # Merely declaring state.restore-props=false on a node
                    # does not disable WirePlumber 0.4's Boolean-only check.
                    fragments = Path(env["XDG_CONFIG_HOME"], "wireplumber", "main.lua.d")
                    fragments.mkdir(parents=True)
                    rule = ROOT / "packaging" / "54-openxlr-owned-state.lua"
                    (fragments / rule.name).write_bytes(rule.read_bytes())
                    # Default-node management and routing are separate in 0.4.
                    # The private servers must never discover host hardware.
                    (fragments / "51-no-hardware.lua").write_text(
                        "alsa_monitor.enabled = false\nv4l2_monitor.enabled = false\n"
                        "libcamera_monitor.enabled = false\n")
                    policies = [["--config-file", "main.conf"], ["--config-file", "policy.conf"]]
                # Each WirePlumber runs supervised and names its pid file in
                # OPENXLR_TEST_WIREPLUMBER_PIDS, so the audio clock test can
                # restart it as systemd would.
                pidfiles = [runtime + f"/wireplumber-{index}.pid" for index in range(len(policies))]
                env["OPENXLR_TEST_WIREPLUMBER_PIDS"] = ":".join(pidfiles)
                for policy, pidfile in zip(policies, pidfiles):
                    processes.append(Supervised(["wireplumber", *policy], env, log, pidfile))
                command = ["dotnet", "test", PROJECT, "-c", "Release", "--no-build", "--filter",
                           test_filter, *sys.argv[1:]]
                if sys.argv[1:2] == ["--command"]:
                    command = sys.argv[2:]
                    if not command: raise ValueError("--command needs an executable")
                subprocess.run(command,
                               env=env, check=True, timeout=900,
                               cwd=ROOT)
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read())
                raise
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()


if __name__ == "__main__":
    main()
