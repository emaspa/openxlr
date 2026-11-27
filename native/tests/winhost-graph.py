#!/usr/bin/env python3
"""The Windows backend as a real node in a PipeWire graph.

`make -C native WINHOST=1 test-winhost-graph` runs this. It starts a
PipeWire server of its own, with its own runtime directory, configuration
and socket name and no session manager, and checks that this is the server
every later program reaches before it starts anything else. It then runs
the helper with the `wine` backend as an insert between the two nodes of
tests/winhost-graph, which play a known signal and record what comes back,
links the three by hand, and checks, for the CLAP and the VST3 test plugin,
on the graph's own block and on the plugin's (`--own-block`):

- the plugin's gain is applied to every frame, exactly;
- the latency the helper reports is the plugin's figure plus the delay the
  node really adds, which is none on the graph's block;
- a parameter set from the Linux side is heard, and its meter follows;
- when the Windows program is killed the helper reports it and exits, its
  node leaves the graph, and nothing but the plugin's audio or silence was
  heard on the way;
- asked to quit, the helper leaves with a clean exit and takes its node.

Silence while a plugin's audio runs is a block that came late, which a
loaded machine may cause; it is counted and printed, never failed. Any frame
that is neither silence nor the signal at one gain and one delay fails, and
the failure says how late each channel was and how long it lasted.

OPENXLR_TEST_GRAPH_ONLY=TEXT runs only the cases whose title holds TEXT.

Usage: winhost-graph.py HELPER WINHOST-EXE CLAP-PLUGIN VST3-PLUGIN CLIENT
"""
import json
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time

RATE = 48000
QUANTUM = 256
# A second of audio at the expected gain and delay, without a break, is
# what counts as heard.
HEARD = RATE
# As in tests/winhost-graph.c: positions a channel names before it wraps.
SPAN = 0x100000
# How long the recorder is still listened to after a frame that is not the
# plugin's audio, to say how long that lasted.
AFTER_GARBAGE = 2
PLUGIN = "org.openxlr.test.winhost."
VST3_CLASS = "00112233445566778899AABBCCDDEE"
PLUGIN_LATENCY = 480  # what the latency classes of both test plugins report
SOCKET = "openxlr-winhost-graph-0"
# The highest real-time priority any thread of this check may run at.
RT_LIMIT = 20

SERVER_CONF = """context.properties = {
    core.daemon = true
    core.name = "%(socket)s"
    openxlr.test.marker = "%(marker)s"
    default.clock.rate = %(rate)d
    default.clock.allowed-rates = [ %(rate)d ]
    default.clock.quantum = %(quantum)d
    default.clock.min-quantum = 16
}
context.spa-libs = {
    audio.convert.* = audioconvert/libspa-audioconvert
    support.* = support/libspa-support
}
context.modules = [
    { name = libpipewire-module-rt
      args = { nice.level = 0 rt.prio = %(rt)d rtkit.enabled = false rtportal.enabled = false }
      flags = [ ifexists nofail ] }
    { name = libpipewire-module-protocol-native }
    { name = libpipewire-module-access }
    { name = libpipewire-module-client-node }
    { name = libpipewire-module-adapter }
    { name = libpipewire-module-link-factory }
    { name = libpipewire-module-spa-node-factory }
]
context.objects = [
    { factory = spa-node-factory
      args = { factory.name = support.node.driver node.name = openxlr-graph-driver
               priority.driver = 20000 } }
]
"""

CLIENT_CONF = """context.properties = { }
context.spa-libs = {
    audio.convert.* = audioconvert/libspa-audioconvert
    support.* = support/libspa-support
}
context.modules = [
    { name = libpipewire-module-rt
      args = { nice.level = 0 rt.prio = %(rt)d rtkit.enabled = false rtportal.enabled = false }
      flags = [ ifexists nofail ] }
    { name = libpipewire-module-protocol-native }
    { name = libpipewire-module-client-node }
    { name = libpipewire-module-adapter }
]
"""


class Failure(Exception):
    pass


def check(condition, message):
    if not condition:
        raise Failure(message)


class Lines:
    """A process's standard output, a line at a time, read on a thread."""

    def __init__(self, stream, log=None, label=""):
        self.queue = queue.Queue()
        self.all = []
        self.log, self.label = log, label
        threading.Thread(target=self.read, args=(stream,), daemon=True).start()

    def read(self, stream):
        for line in stream:
            line = line.rstrip("\n")
            self.all.append(line)
            if self.log:
                print(f"{self.label}{line}", file=self.log, flush=True)
            self.queue.put(line)
        self.queue.put(None)

    def next(self, until):
        """The next line before `until` (monotonic), "" on time up, None at the end."""
        try:
            return self.queue.get(timeout=max(0.0, until - time.monotonic()))
        except queue.Empty:
            return ""


class Graph:
    def __init__(self, server, env, helper, log):
        self.server, self.env, self.helper, self.log = server, env, helper, log

    def run(self, *command, timeout=20):
        return subprocess.run(command, env=self.env, capture_output=True, text=True,
                              timeout=timeout, check=False)

    def dump(self):
        result = self.run("pw-dump")
        check(result.returncode == 0, "pw-dump failed: " + result.stderr.strip())
        return json.loads(result.stdout)

    def ports(self):
        result = self.run("pw-link", "--output", "--input")
        return set(result.stdout.split()) if result.returncode == 0 else set()

    def nodes(self):
        return {o["info"]["props"].get("node.name") for o in self.dump()
                if o.get("type") == "PipeWire:Interface:Node"}

    def link(self, output, input_):
        result = self.run("pw-link", output, input_)
        check(result.returncode == 0, f"cannot link {output} to {input_}: {result.stderr.strip()}")


def rt_priorities(pid):
    """The real-time priority of every thread of a process, by thread."""
    found = {}
    for task in Path(f"/proc/{pid}/task").glob("*"):
        try:
            stat = (task / "stat").read_text()
        except OSError:
            continue
        fields = stat[stat.rindex(")") + 2:].split()
        found[task.name] = int(fields[37])  # rt_priority, the 40th field
    return found


def windows_process(helper_pid):
    """The process the Windows audio thread of this helper lives in: the
    thread is named oxwh-PID after the helper (wine.c)."""
    name = f"oxwh-{helper_pid}"
    for comm in Path("/proc").glob("[0-9]*/task/*/comm"):
        try:
            if comm.read_text().strip() == name:
                return int(comm.parts[2])
        except OSError:
            continue
    return None


def start_server(runtime, env, log, marker):
    """Start the private server. Nothing may reach the session's: the
    environment names only the private runtime directory and socket, whose
    name no session server uses."""
    check(env["XDG_RUNTIME_DIR"] == runtime and env["PIPEWIRE_RUNTIME_DIR"] == runtime,
          "the private runtime directory is not the one in the environment")
    check(env["PIPEWIRE_REMOTE"] == SOCKET, "the private socket is not the one in the environment")
    check(not Path(runtime, SOCKET).exists(), "the private socket exists before the server")
    return subprocess.Popen(["pipewire"], env=env, stdout=log, stderr=log)


def verify_server(server, runtime, env, marker):
    """Prove the server this environment reaches is the private one, with
    this run's configuration, before anything else is started. Fails closed."""
    deadline = time.monotonic() + 10
    while not Path(runtime, SOCKET).exists():
        check(server.poll() is None and time.monotonic() < deadline,
              "the private PipeWire server did not create its socket")
        time.sleep(0.05)
    result = subprocess.run(["pw-dump"], env=env, capture_output=True, text=True, timeout=20)
    check(result.returncode == 0, "pw-dump could not reach the private server")
    cores = [o for o in json.loads(result.stdout) if o.get("type") == "PipeWire:Interface:Core"]
    props = cores[0]["info"]["props"] if cores else {}
    check(props.get("openxlr.test.marker") == marker and props.get("core.name") == SOCKET,
          "the server this environment reaches is not the private one; nothing was started")


def scenario(graph, client_path, title, arguments, plugin_latency, param_meter):
    """One helper as an insert: heard, latency, an optional parameter, and
    its end. `param_meter` is the meter the parameter's value moves, or None
    for a plugin without parameters, which is asked to quit instead of being
    killed."""
    only = os.environ.get("OPENXLR_TEST_GRAPH_ONLY")
    if only and only not in title:
        return
    own_block = arguments[0] == "--own-block"
    node = "openxlr-graph-" + secrets.token_hex(4)
    log = graph.log
    print(f"--- {title}", file=log, flush=True)
    client = subprocess.Popen([client_path, str(QUANTUM)], env=graph.env, stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=log, text=True)
    events = Lines(client.stdout, log, "client: ")
    command = [graph.helper, *arguments, node, "2", str(RATE)]
    helper = subprocess.Popen(command, env=graph.env, stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    said = Lines(helper.stdout, log, "helper: ")
    errors = Lines(helper.stderr, log, "helper stderr: ")
    try:
        deadline = time.monotonic() + 10
        while events.next(deadline) not in ("ready", None, ""):
            pass
        # Wine may be updating its prefix before the plugin loads.
        wanted = {f"{node}:{p}_{c}" for p in ("playback", "capture") for c in (0, 1)}
        wanted |= {f"openxlr-graph-{p}" for p in ("play:out_0", "play:out_1",
                                                    "record:in_0", "record:in_1")}
        deadline = time.monotonic() + 180
        while not wanted <= graph.ports():
            check(helper.poll() is None, f"the helper exited with {helper.poll()} before its node had ports")
            check(time.monotonic() < deadline, "the helper's ports never appeared")
            time.sleep(0.1)
        for c in (0, 1):
            graph.link(f"openxlr-graph-play:out_{c}", f"{node}:playback_{c}")
            graph.link(f"{node}:capture_{c}", f"openxlr-graph-record:in_{c}")
        # The links come one at a time, so the first cycles can carry one
        # channel without the other, and on the plugin's own block they
        # come out a delay later. The recorder starts once that has passed.
        time.sleep(0.5)
        client.stdin.write("listen\n")
        client.stdin.flush()

        stream = Stream(events)
        state = stream.until_heard(0.5, None, 30)
        delay = state[3]
        if own_block:
            check(delay > 0, "the plugin's own block adds no delay")
        else:
            check(delay == 0, f"the node adds {delay} frames on the graph's own block")
        # The helper says its latency once audio runs and again on a change.
        latency = None
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            for line in drain(said):
                if m := re.fullmatch(rf"latency (\d+) {RATE}", line):
                    latency = int(m[1])
            if latency is not None:
                break
            time.sleep(0.05)
        check(latency == plugin_latency + delay,
              f"the helper reports {latency} frames for a node {delay} frames late "
              f"around a plugin that reports {plugin_latency}")
        notes = [f"{delay} frames late, reported as {latency}"]

        if param_meter:
            helper.stdin.write("set 1 0.125\n")
            helper.stdin.flush()
            stream.until_heard(0.125, delay, 20)
            meter = f"meter {param_meter} 0.25"
            deadline = time.monotonic() + 5
            while meter not in said.all:
                check(time.monotonic() < deadline, f"no '{meter}' from the helper")
                time.sleep(0.05)
            notes.append("a parameter set from Linux heard and its meter followed")
            windows = windows_process(helper.pid)
            check(windows is not None, "the Windows program's audio thread was not found")
            for pid in (graph.server.pid, client.pid, helper.pid, windows):
                high = {t: p for t, p in rt_priorities(pid).items() if p > RT_LIMIT}
                check(not high, f"process {pid} has threads above real-time priority {RT_LIMIT}: {high}")
            os.kill(windows, signal.SIGKILL)
            code = helper.wait(timeout=15)
            check(code == 1, f"the helper exited with {code} after its Windows program was killed")
            errors_text = wait_text(errors, "the Windows plugin host was ended by signal 9")
            check(errors_text, "the helper did not say its Windows program was killed")
            notes.append("killed Windows program reported")
        else:
            helper.stdin.write("quit\n")
            helper.stdin.flush()
            code = helper.wait(timeout=15)
            check(code == 0, f"the helper exited with {code} when asked to quit")
        deadline = time.monotonic() + 5
        while node in graph.nodes():
            check(time.monotonic() < deadline, "the helper's node is still in the graph")
            time.sleep(0.05)
        check(not any(p.startswith(node + ":") for p in graph.ports()), "the helper's ports remain")
        # Every frame recorded to the end: nothing but the plugin's audio at
        # one of its gains on the same delay, or silence.
        client.stdin.close()
        client.wait(timeout=10)
        stream.read_rest()
        stream.verify(delay, (0.5, 0.125) if param_meter else (0.5,))
        windows_left = windows_process(helper.pid)
        check(windows_left is None, "the Windows program outlived its helper")
        if stream.dropped:
            notes.append(f"{stream.dropped} frames of silence while audio ran")
        print(f"PASS: {title}: {', '.join(notes)}", flush=True)
    finally:
        for process in (helper, client):
            if process.poll() is None:
                process.kill()
                process.wait()


def drain(lines):
    out = []
    while True:
        try:
            line = lines.queue.get_nowait()
        except queue.Empty:
            return out
        if line is None:
            return out
        out.append(line)


def wait_text(lines, text, seconds=5):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if any(text in line for line in lines.all):
            return True
        time.sleep(0.05)
    return False


class Stream:
    """What the recorder heard, as the positions where it changed."""

    def __init__(self, events):
        self.events = events
        self.changes = []  # (position, kind, gain, delay)
        self.frames = {}  # position: (left, right), for each change to garbage
        self.dropped = 0
        self.position = 0

    def take(self, line):
        if line.startswith("state "):
            parts = line.split()
            position = int(parts[1])
            if parts[2] == "gain":
                self.changes.append((position, "audio", float(parts[3]), int(parts[5])))
            else:
                self.changes.append((position, parts[2], None, None))
                if parts[2] == "garbage":
                    # Printed with the nine digits that bring a float back.
                    self.frames[position] = tuple(
                        struct.unpack("f", struct.pack("f", float(v)))[0] for v in parts[3:5])
        elif line.startswith("alive "):
            self.position = int(line.split()[1])
        elif line == "overflow":
            raise Failure("the recorder lost changes")

    def garbage(self, at, gain, delay):
        """Say what the frame at `at` was and how long the recorder heard
        anything but `gain` (on `delay`, if given) from there: a skew between
        the channels that lasts must not read as one that came and went."""
        deadline = time.monotonic() + AFTER_GARBAGE
        while (line := self.events.next(deadline)) not in (None, ""):
            self.take(line)
        what = ""
        left, right = self.frames[at]
        named = [round(v / gain * (1 << 22)) - 1 - (SPAN if c else 0)
                 for c, v in enumerate((left, right))]
        if all(0 <= n < SPAN and (n + 1 + (SPAN if c else 0)) / (1 << 22) * gain == v
               for c, (n, v) in enumerate(zip(named, (left, right)))):
            late = [(at - n) % SPAN for n in named]
            what = f" (at gain {gain} the left channel is {late[0]} frames late, the right {late[1]})"
        after = [p for p, kind, g, d in self.changes
                 if p > at and kind == "audio" and g == gain and (delay is None or d == delay)]
        if after:
            return f"{what}, for {after[0] - at} frames"
        last = max(self.position, self.changes[-1][0])
        return f"{what}, and still at frame {last}, {last - at} frames later"

    def until_heard(self, gain, delay, seconds):
        """Read until HEARD frames in a row at `gain` (and `delay`, if given).
        Returns that state."""
        deadline = time.monotonic() + seconds
        self.position = 0
        while True:
            line = self.events.next(deadline)
            check(line is not None, "the recorder ended")
            check(line != "", f"the plugin's audio at gain {gain} was never heard for a second")
            self.take(line)
            if self.changes:
                position, kind, g, d = self.changes[-1]
                if kind == "garbage":
                    raise Failure(f"the recorder heard something that is not the plugin's audio "
                                  f"at frame {position}{self.garbage(position, gain, delay)}")
                if (kind == "audio" and g == gain and (delay is None or d == delay)
                        and self.position - position >= HEARD):
                    return self.changes[-1]

    def read_rest(self):
        while (line := self.events.next(time.monotonic() + 5)) not in (None, ""):
            self.take(line)

    def verify(self, delay, gains):
        """From the first audio on, every change is to silence or to one of
        `gains` on `delay`. Before it, silence alone."""
        started = False
        for i, (position, kind, g, d) in enumerate(self.changes):
            check(kind != "garbage", f"the recorder heard something that is not the plugin's audio "
                                     f"at frame {position}")
            if kind == "audio":
                check(d == delay, f"the delay moved from {delay} to {d} frames at frame {position}")
                check(g in gains, f"a gain of {g} at frame {position}")
                started = True
            elif started and i + 1 < len(self.changes):
                # A gap while audio ran: a block that came late.
                self.dropped += self.changes[i + 1][0] - position


def main():
    if len(sys.argv) != 6:
        sys.exit(__doc__.split("\n\n")[-1].strip())
    helper, winhost, clap, vst3, client = (str(Path(a).resolve()) for a in sys.argv[1:])
    for program in ("pipewire", "pw-link", "pw-dump"):
        if not shutil.which(program):
            print(f"SKIP: test-winhost-graph needs {program}")
            return 0
    wine = os.environ.get("OPENXLR_WINE") or shutil.which("wine")
    if not wine:
        print("SKIP: test-winhost-graph needs Wine: wine on PATH, or OPENXLR_WINE")
        return 0
    # The Wine prefix the test program uses (tests/winhost.c), kept between
    # runs. Never the user's.
    prefix = os.environ.get("OPENXLR_TEST_WINEPREFIX") or str(
        Path(os.environ.get("XDG_CACHE_HOME") or Path.home() / ".cache") / "openxlr-test" / "wineprefix")
    Path(prefix).mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="openxlr-winhost-graph-") as runtime:
        os.chmod(runtime, 0o700)
        marker = secrets.token_hex(16)
        config = Path(runtime, "config")
        config.mkdir()
        values = {"socket": SOCKET, "marker": marker, "rate": RATE, "quantum": QUANTUM, "rt": RT_LIMIT}
        (config / "pipewire.conf").write_text(SERVER_CONF % values)
        (config / "client.conf").write_text(CLIENT_CONF % values)
        # Nothing of the session's audio, desktop or bus is passed on.
        env = {k: v for k, v in os.environ.items()
               if not k.startswith(("PIPEWIRE_", "PULSE_", "JACK_", "SPA_", "WINE"))
               and k not in ("DBUS_SESSION_BUS_ADDRESS", "DISPLAY", "WAYLAND_DISPLAY")}
        env.update(XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime, PIPEWIRE_REMOTE=SOCKET,
                   PIPEWIRE_CONFIG_DIR=str(config), XDG_CONFIG_HOME=runtime + "/xdg-config",
                   XDG_STATE_HOME=runtime + "/xdg-state", OPENXLR_WINHOST=winhost,
                   WINEPREFIX=prefix, WINEDEBUG=os.environ.get("WINEDEBUG", "-all"),
                   WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d;wineusb.sys=d",
                   # The helper asks RTKit when the real-time limit refuses
                   # it; never the system's.
                   DBUS_SYSTEM_BUS_ADDRESS="unix:path=/nonexistent/openxlr-test-system-bus")
        if os.environ.get("OPENXLR_WINE"):
            env["OPENXLR_WINE"] = wine
        log_path = Path(runtime, "check.log")
        with open(log_path, "w+") as log:
            server = None
            try:
                server = start_server(runtime, env, log, marker)
                verify_server(server, runtime, env, marker)
                graph = Graph(server, env, helper, log)
                print(f"note: a private PipeWire server in {runtime}, Wine prefix {prefix}", flush=True)
                for block in ([], ["--own-block"]):
                    name = " on the plugin's own block" if block else ""
                    scenario(graph, client, "a Windows CLAP plugin as a node" + name,
                             [*block, "wine", "clap", clap, PLUGIN + "params"], 0, "3")
                    scenario(graph, client, "a Windows VST3 plugin as a node" + name,
                             [*block, "wine", "vst3", vst3, VST3_CLASS + "05"], 0, "3")
                    scenario(graph, client, "a Windows CLAP plugin's latency" + name,
                             [*block, "wine", "clap", clap, PLUGIN + "latency"], PLUGIN_LATENCY, None)
                    scenario(graph, client, "a Windows VST3 plugin's latency" + name,
                             [*block, "wine", "vst3", vst3, VST3_CLASS + "02"], PLUGIN_LATENCY, None)
                check(server.poll() is None, "the private PipeWire server exited")
            except (Failure, subprocess.TimeoutExpired) as error:
                log.flush()
                print(log_path.read_text(), file=sys.stderr)
                print(f"FAIL: {error}", flush=True)
                return 1
            finally:
                if server and server.poll() is None:
                    server.terminate()
                    try:
                        server.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        server.kill()
                        server.wait()
    return 0


if __name__ == "__main__":
    sys.exit(main())
