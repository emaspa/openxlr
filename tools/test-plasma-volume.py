#!/usr/bin/env python3
"""Exercise Plasma's volume preference and OpenXLR under an isolated KWin session.

Build Release first. Requires Plasma 6's volume QML module, Qt 6 Test,
KWin, XWayland, D-Bus and KDE's config helpers. Never uses the live session.
"""
import os
from pathlib import Path
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile


def main():
    for program in ("dbus-run-session", "kwin_wayland", "Xwayland", "dotnet", "kreadconfig6", "kwriteconfig6"):
        if not shutil.which(program):
            raise SystemExit(f"Required program not found: {program}")
    qml = os.environ.get("OPENXLR_TEST_QML_RUNNER") or shutil.which("qmltestrunner6")
    if not qml:
        qml = next((str(p) for p in (Path("/usr/lib/qt6/bin/qmltestrunner"),
                                   Path("/usr/lib64/qt6/bin/qmltestrunner")) if p.is_file()), None)
    if not qml or not os.access(qml, os.X_OK):
        raise SystemExit("Set OPENXLR_TEST_QML_RUNNER to the Qt 6 qmltestrunner executable")
    repo = Path(__file__).resolve().parent.parent
    with tempfile.TemporaryDirectory(prefix="openxlr-plasma-test-") as directory:
        root = Path(directory)
        (root / "runtime").mkdir(mode=0o700)
        (root / "isolated-session").touch()
        env = dict(os.environ, XDG_RUNTIME_DIR=str(root / "runtime"),
                   XDG_CONFIG_HOME=str(root / "config"), XDG_CONFIG_DIRS=str(root / "config"),
                   XDG_DATA_HOME=str(root / "data"), XDG_STATE_HOME=str(root / "state"),
                   XDG_CACHE_HOME=str(root / "cache"), XDG_CURRENT_DESKTOP="KDE", XDG_SESSION_TYPE="wayland",
                   QT_QUICK_BACKEND="software", LIBGL_ALWAYS_SOFTWARE="1", KWIN_COMPOSE="Q", KDE_DEBUG="1",
                   PIPEWIRE_RUNTIME_DIR=str(root / "runtime"), PIPEWIRE_REMOTE="no-pipewire",
                   PULSE_SERVER="unix:" + str(root / "no-pulse"),
                   OPENXLR_TEST_PLASMA_WAYLAND="1", OPENXLR_TEST_DESKTOP_ROOT=directory,
                   OPENXLR_TEST_QML_RUNNER=qml)
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "SESSION_MANAGER", "DBUS_SESSION_BUS_ADDRESS", "QT_QPA_PLATFORM"):
            env.pop(key, None)
        command = ["dotnet", "test", "src/OpenXLR.Tests/OpenXLR.Tests.csproj", "-c", "Release",
                   "--no-build", "--filter", "FullyQualifiedName~PlasmaWaylandTests", *sys.argv[1:]]
        session = root / "session"
        session.write_text("#!/bin/sh\n" + shlex.join(command) + "\nresult=$?\nprintf '%s\\n' \"$result\" > "
                           + shlex.quote(str(root / "result")) + "\nexit \"$result\"\n")
        session.chmod(0o700)
        with (root / "session.log").open("w+") as log:
            process = subprocess.Popen(["dbus-run-session", "--", "kwin_wayland", "--virtual", "--xwayland",
                                        "--no-lockscreen", "--no-global-shortcuts", "--no-kactivities",
                                        "--width", "1920", "--height", "1080", "--exit-with-session", str(session)],
                                       cwd=repo, env=env, stdout=log, stderr=log, start_new_session=True)
            try:
                process.wait(timeout=120)
                result = root / "result"
                if (process.returncode or not result.exists() or result.read_text().strip() != "0"
                        or not (root / "acceptance-passed").exists()):
                    raise RuntimeError("The isolated Plasma/Wayland acceptance failed")
            finally:
                # Reap only this private session, including children after a timeout.
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                log.seek(0)
                print(log.read())
                for name in ("qml.log", "failure.log"):
                    extra = root / name
                    if extra.exists():
                        print(extra.read_text())


if __name__ == "__main__":
    main()
