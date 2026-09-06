#!/usr/bin/env python3
"""Every file the RPM %install section puts under %{buildroot} must be
claimed by a %files entry, and no bare path may sit in %install (rpm
would execute it as a command). Standard library only, so it runs on any
CI runner without rpm tools."""
import fnmatch
import re
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "packaging/rpm/openxlr.spec"
text = open(path).read()


SECTIONS = "prep|build|install|check|files|changelog|pre|post|preun|postun|description|package"


def section(name):
    m = re.search(r"^%" + name + r"\b[^\n]*\n(.*?)(?=^%(?:" + SECTIONS + r")\b|\Z)", text, re.S | re.M)
    return m.group(1) if m else ""


install, files = section("install"), section("files")
errors = []
# Files and directories the recipe installs; directories made for the
# sake of a later install line are claimed through their contents.
installed = [t for line in install.splitlines() if not re.search(r"install\s+-d|mkdir", line)
             for t in re.findall(r"%\{buildroot\}(\S+)", line)]
claims = [line.split()[-1] for line in files.splitlines()
          if line.strip() and not line.startswith(("%license", "%doc", "%defattr", "#"))]
claims = [re.sub(r"^%(?:config(?:\(noreplace\))?|attr\([^)]*\))\s*", "", c) for c in claims]


def claimed(target):
    for c in claims:
        if fnmatch.fnmatch(target, c) or target == c.rstrip("/") or target.startswith(c.rstrip("/") + "/"):
            return True
    return False


for line in install.splitlines():
    s = line.strip()
    if s.startswith("%{_") and not s.startswith("%{_bindir}/") and " " not in s:
        errors.append(f"bare path in %install, rpm would execute it: {s}")
for target in installed:
    target = target.rstrip("/")
    if not claimed(target):
        errors.append(f"installed but not in %files: {target}")
for e in errors:
    print(f"spec: {e}", file=sys.stderr)
print(f"spec: {len(installed)} install targets, {len(claims)} %files entries, {'ok' if not errors else str(len(errors)) + ' problems'}")
sys.exit(1 if errors else 0)
