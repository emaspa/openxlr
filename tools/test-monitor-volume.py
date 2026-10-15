#!/usr/bin/env python3
"""Run monitor controls, DSP audio and profile startup on private PipeWire sockets.

Arguments are passed on to `dotnet test`, so a build that lives somewhere
other than the default output can be tested with `--artifacts-path <dir>`.
OPENXLR_TEST_FILTER selects a focused regression instead of the full audio suite.
"""
import os
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def main():
    with tempfile.TemporaryDirectory(prefix="openxlr-monitor-test-") as runtime:
        env = dict(os.environ, XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime,
                   XDG_CONFIG_HOME=runtime + "/config", XDG_STATE_HOME=runtime + "/state",
                   XDG_DATA_HOME=runtime + "/data",
                   CLAP_PATH=runtime + "/plugins", VST3_PATH=runtime + "/plugins",
                   PULSE_SERVER="unix:" + runtime + "/pulse/native",
                   PIPEWIRE_REMOTE="pipewire-0", OPENXLR_TEST_MONITOR_VOLUME="1")
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
                # Internal filters must ignore stale desktop volume and mute.
                # Seed the private session manager with values from an older build.
                state = Path(env["XDG_STATE_HOME"], "wireplumber", "stream-properties")
                state.parent.mkdir(parents=True, exist_ok=True)
                values = json.dumps({"volume": 0.125, "mute": True, "channelVolumes": [0.125]})
                lines = ["[stream-properties]"]
                for description in ("OpenXLR Mic Filter", "OpenXLR mix latency delay"):
                    for kind, suffix in (("Sink", "input"), ("Source", "output")):
                        name = (description + " " + suffix).replace(" ", r"\s")
                        lines.append(f"Audio/{kind}:media.name:{name}={values}")
                state.write_text("\n".join(lines) + "\n")
                help_text = subprocess.check_output(["wireplumber", "--help"], text=True)
                if "--profile" in help_text:
                    policies = [["--profile", "policy"]]
                else:
                    # WirePlumber 0.4 keeps default-node management in main,
                    # separate from routing policy. Disable its hardware
                    # monitors before loading that configuration as well.
                    fragments = Path(env["XDG_CONFIG_HOME"], "wireplumber", "main.lua.d")
                    fragments.mkdir(parents=True)
                    (fragments / "51-no-hardware.lua").write_text(
                        "alsa_monitor.enabled = false\nv4l2_monitor.enabled = false\n"
                        "libcamera_monitor.enabled = false\n")
                    policies = [["--config-file", "main.conf"], ["--config-file", "policy.conf"]]
                for policy in policies:
                    processes.append(subprocess.Popen(["wireplumber", *policy], env=env, stdout=log, stderr=log))
                subprocess.run(["dotnet", "test", "src/OpenXLR.Tests/OpenXLR.Tests.csproj",
                                "-c", "Release", "--no-build", "--filter",
                                os.environ.get("OPENXLR_TEST_FILTER", "FullyQualifiedName~MonitorVolumeIntegrationTests|FullyQualifiedName~FilterStateIntegrationTests|FullyQualifiedName~ProfileStartupTests|FullyQualifiedName~DspAudioIntegrationTests|FullyQualifiedName~MixDelayIntegrationTests|FullyQualifiedName~PluginLatencyIntegrationTests|FullyQualifiedName~SoundCheckIntegrationTests"), *sys.argv[1:]],
                               env=env, check=True, timeout=180,
                               cwd=Path(__file__).resolve().parent.parent)
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
