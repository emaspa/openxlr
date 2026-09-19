#!/usr/bin/env python3
"""Parse and lint the Omarchy plugin, with explicit external type declarations.

The small lint imports cover unavailable Quickshell/Omarchy modules only.
No warning category is suppressed. This is not a Quickshell runtime check.
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
imports = root / "tools/omarchy-qml-imports"
files = sorted((root / "packaging/omarchy/openxlr.mixer").glob("*.qml"))
files += sorted((root / "packaging/omarchy/tests").glob("*.qml"))
files += sorted(imports.rglob("*.qml"))


def tool(name):
    override = os.environ.get(name.upper())
    candidates = [override] if override else [shutil.which(name),
        f"/usr/lib/qt6/bin/{name}", f"/usr/lib/qt6/libexec/{name}"]
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    sys.exit(f"{name} is required; install Qt 6 declarative development tools")


formatter = tool("qmlformat")
linter = tool("qmllint")
for path in files:
    subprocess.run([formatter, str(path)], check=True, stdout=subprocess.DEVNULL)
subprocess.run([linter, "--ignore-settings", "-I", str(imports), "--max-warnings", "0",
                *map(str, files)], check=True)
print(f"Omarchy QML: {len(files)} files parsed and linted without warnings using external type declarations")
