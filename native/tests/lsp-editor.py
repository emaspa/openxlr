#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Opt-in desktop regression: LSP Gate must repaint after large resize drags.

Requires python-xlib, LSP Gate Mono LV2, DISPLAY and a running PipeWire server.
The isolated instance has no audio links and never changes the user's chain.
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import time

from Xlib import X, display, error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default=str(Path(__file__).resolve().parents[1] / "openxlr-lv2-host"))
    parser.add_argument("--opengl", action="store_true", help="test an explicit OpenGL override")
    parser.add_argument("--position", nargs=2, type=int, default=(0, 0), metavar=("X", "Y"),
                        help="place the test window on a chosen monitor")
    args = parser.parse_args()
    node = f"openxlr-lsp-resize-test-{os.getpid()}"
    env = os.environ.copy()
    env.pop("LSP_WS_LIB_GLXSURFACE", None)
    if args.opengl:
        env["LSP_WS_LIB_GLXSURFACE"] = "1"

    with tempfile.TemporaryFile(mode="w+") as log:
        process = subprocess.Popen(
            [args.host, "lv2", "http://lsp-plug.in/plugins/lv2/gate_mono", node, "1", "48000"],
            stdin=subprocess.PIPE, stdout=log, stderr=log, text=True, env=env,
        )
        connection = None
        try:
            connection = display.Display()
            root = connection.screen().root
            atom = connection.intern_atom("_OPENXLR_NODE")

            def command(line):
                process.stdin.write(line + "\n")
                process.stdin.flush()

            def find(window):
                try:
                    prop = window.get_full_property(atom, X.AnyPropertyType)
                    if prop and prop.value == node.encode():
                        return window
                    for child in window.query_tree().children:
                        found = find(child)
                        if found is not None:
                            return found
                except error.BadWindow:
                    pass  # An unrelated desktop window closed during discovery.
                return None

            command("show")
            window = None
            for _ in range(100):
                time.sleep(0.1)
                window = find(root)
                if window is not None or process.poll() is not None:
                    break
            assert window is not None, "LSP editor did not open"
            window.configure(x=args.position[0], y=args.position[1])
            connection.sync()
            time.sleep(1)
            original = window.get_geometry()

            def repaint(label):
                geometry = window.get_geometry()

                def pixels():
                    return window.get_image(0, 0, geometry.width, geometry.height, X.ZPixmap, 0xffffffff).data

                command("set g_out 1")
                time.sleep(0.35)
                first = pixels()
                command("set g_out 0.25")
                time.sleep(0.35)
                assert first != pixels(), f"LSP stopped repainting {label}"
                print(f"PASS: parameter repaints {label}", flush=True)

            repaint("at its initial size")
            for width, height in [(2400, 1600), (4000, 2200), (6000, 2400), (original.width, original.height)]:
                start = window.get_geometry()
                for step in range(1, 181):
                    window.configure(
                        width=round(start.width + (width - start.width) * step / 180),
                        height=round(start.height + (height - start.height) * step / 180),
                    )
                    connection.flush()
                    time.sleep(0.008)
                connection.sync()
                time.sleep(0.4)
                repaint(f"after dragging to {width} x {height}")
        except Exception:
            log.seek(0)
            print(log.read()[-4000:])
            raise
        finally:
            if process.poll() is None:
                try:
                    process.stdin.write("quit\n")
                    process.stdin.flush()
                    process.wait(timeout=4)
                except (BrokenPipeError, subprocess.TimeoutExpired):
                    process.kill()
                    process.wait()
            process.stdin.close()
            if connection is not None:
                connection.close()


if __name__ == "__main__":
    main()
