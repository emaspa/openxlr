#!/usr/bin/env python3
"""Every file the RPM %install section puts under %{buildroot} must be
claimed by a %files entry, and no bare path may sit in %install (rpm
would execute it as a command). A udevadm trigger in a scriptlet is
limited to the USB vendor in packaging/70-openxlr.rules and cannot fail
the install. Standard library only, so it runs on any CI runner without
rpm tools."""
import fnmatch
import os
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
          if line.strip() and not line.startswith(("%defattr", "#"))]
# Relative %doc/%license inputs are copied by rpm; absolute ones were
# already staged and are owned just like an ordinary %files entry.
claims = [c for c in claims if c.startswith(("/", "%{"))]
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
    if not target:  # The build root itself is a staging directory, not a payload file.
        continue
    if not claimed(target):
        errors.append(f"installed but not in %files: {target}")
# A scriptlet that replays udev events replays them only for the USB
# vendors the rule names, not for every device on the machine, and a
# system without udev (a container, a chroot) does not fail the install.
rule = os.path.join(os.path.dirname(os.path.abspath(path)), "..", "70-openxlr.rules")
vendors = set(re.findall(r'ATTRS\{idVendor\}=="([0-9a-fA-F]{4})"', open(rule).read()))
triggered = set()
for name in ("pre", "post", "preun", "postun"):
    for line in section(name).splitlines():
        if "udevadm" not in line or "trigger" not in line:
            continue
        matched = re.findall(r"--attr-match=idVendor=([0-9a-fA-F]{4})\b", line)
        if "--subsystem-match=usb" not in line or len(matched) != 1 or matched[0] not in vendors:
            errors.append(f"%{name}: udevadm trigger is not limited to one vendor of the rule: {line.strip()}")
        elif not line.rstrip().endswith("|| :"):
            errors.append(f"%{name}: udevadm trigger can fail the install: {line.strip()}")
        triggered.update(matched)
if triggered and triggered != vendors:
    errors.append(f"udevadm trigger leaves out vendors of the rule: {', '.join(sorted(vendors - triggered))}")
for e in errors:
    print(f"spec: {e}", file=sys.stderr)
print(f"spec: {len(installed)} install targets, {len(claims)} %files entries, {'ok' if not errors else str(len(errors)) + ' problems'}")
sys.exit(1 if errors else 0)
