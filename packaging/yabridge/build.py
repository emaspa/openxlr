#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Prepare pinned sources, build the optional bridge, and create binary packages.

Fetches are confined to 'prepare'. 'build' runs Meson and Cargo offline using
the prepared source tree. No command installs into the running system.
"""
import argparse
import configparser
from email.utils import formatdate
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
PIN = json.loads((HERE / "source.json").read_text())
FILES = [f"libyabridge{loader}-{kind}.so" for loader in ("", "-chainloader") for kind in ("clap", "vst2", "vst3")]
FILES += ["yabridge-host.exe", "yabridge-host.exe.so"]


def run(args, cwd=None, **kwargs):
    return subprocess.run([str(a) for a in args], cwd=cwd, check=True, **kwargs)


def prepare(source):
    if not source.exists():
        source.mkdir(parents=True)
        run(["git", "init", source])
        run(["git", "-C", source, "remote", "add", "origin", PIN["repository"]])
        run(["git", "-C", source, "fetch", "--depth=1", "origin", PIN["commit"]])
        run(["git", "-C", source, "checkout", "--detach", "FETCH_HEAD"])
    actual = run(["git", "-C", source, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if actual != PIN["commit"]:
        raise RuntimeError("Source checkout does not match source.json")
    patch = HERE / "private-plugin-home.patch"
    applied = subprocess.run(["git", "-C", str(source), "apply", "--reverse", "--check", str(patch)], capture_output=True)
    if applied.returncode:
        run(["git", "-C", source, "apply", "--check", patch])
        run(["git", "-C", source, "apply", patch])
    sdk = source / "subprojects/vst3.wrap"
    sdk.write_text(re.sub(r"(?m)^revision = .*", "revision = " + PIN["vst3SdkCommit"], sdk.read_text()))
    for wrap in (source / "subprojects").glob("*.wrap"):
        config = configparser.ConfigParser()
        config.read(wrap)
        if not re.fullmatch(r"[0-9a-f]{40}", config["wrap-git"]["revision"]):
            raise RuntimeError(f"Unpinned dependency: {wrap.name}")
    run(["meson", "subprojects", "download"], cwd=source)
    for wrap in (source / "subprojects").glob("*.wrap"):
        config = configparser.ConfigParser()
        config.read(wrap)
        checkout = source / "subprojects" / wrap.stem
        actual = run(["git", "-C", checkout, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        if actual != config["wrap-git"]["revision"]:
            raise RuntimeError(f"Dependency checkout does not match its pin: {wrap.stem}")
        if config["wrap-git"].getboolean("clone-recursive", fallback=False):
            status = run(["git", "-C", checkout, "submodule", "status", "--recursive"], capture_output=True, text=True).stdout
            if any(line.startswith(("-", "+", "U")) for line in status.splitlines()):
                raise RuntimeError(f"Submodule checkout does not match its pin: {wrap.stem}")
    crate = source / "tools/yabridgectl"
    vendor_config = run(["cargo", "vendor", "--locked", "--versioned-dirs", "vendor"], cwd=crate, capture_output=True, text=True).stdout
    (crate / ".cargo").mkdir(exist_ok=True)
    (crate / ".cargo/config.toml").write_text(vendor_config)
    # Keep the build recipe and patches with the corresponding source archive.
    shutil.copytree(HERE, source / "openxlr-packaging", dirs_exist_ok=True)
    (source / "openxlr-packaging/prepared.json").write_text(json.dumps(PIN, sort_keys=True) + "\n")


def build(source, output, jobs):
    prepared = source / "openxlr-packaging/prepared.json"
    if not prepared.exists() or json.loads(prepared.read_text()) != PIN:
        raise RuntimeError("Run prepare first, or extract the corresponding source archive")
    output.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["SOURCE_DATE_EPOCH"] = str(PIN["sourceDateEpoch"])
    native = output / "native"
    if not (native / "build.ninja").exists():
        run(["meson", "setup", native, source, "--buildtype=release", "--cross-file=" + str(source / "cross-wine.conf"),
             "--unity=on", "--unity-size=10000", "-Dbitbridge=false", "--wrap-mode=nodownload",
             "--force-fallback-for=asio,bitsery,function2,ghc_filesystem,tomlplusplus,clap"], env=env)
    run(["ninja", "-C", native, "-j", jobs], env=env)
    run(["cargo", "build", "--release", "--locked", "--offline", "--jobs", jobs,
         "--target-dir", output / "cargo"], cwd=source / "tools/yabridgectl", env=env)


def stage(source, output, destination, prefix="/usr"):
    root = destination / prefix.lstrip("/")
    bridge = root / "lib/openxlr/yabridge"
    bridge.mkdir(parents=True, exist_ok=True)
    for name in FILES:
        shutil.copy2(output / "native" / name, bridge / name)
    shutil.copy2(output / "cargo/release/yabridgectl", bridge / "yabridgectl")
    for file in bridge.iterdir():
        file.chmod(0o755 if file.name in ("yabridgectl", "yabridge-host.exe") else 0o644)
    run(["strip", bridge / "yabridgectl", *[bridge / name for name in FILES if name.endswith(".so")]])
    receipt = {"formatVersion": 1, "version": f"{PIN['version']}-{PIN['revision']}", "sourceCommit": PIN["commit"],
               "wineInputFix": True, "windowsArchitectures": ["x86_64"]}
    (bridge / "openxlr-yabridge.json").write_text(json.dumps(receipt, indent=2) + "\n")
    launcher = root / "bin/openxlr-yabridgectl"
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher.write_text((HERE / "openxlr-yabridgectl.in").read_text().replace("@BRIDGE_DIR@", prefix + "/lib/openxlr/yabridge"))
    launcher.chmod(0o755)
    notices = root / "share/licenses/openxlr-yabridge"
    notices.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source / "COPYING", notices / "COPYING")
    shutil.copy2(HERE / "source.json", notices / "source.json")
    shutil.copy2(HERE / "private-plugin-home.patch", notices / "private-plugin-home.patch")
    (notices / "SOURCE").write_text(
        f"Source: {PIN['repository']}\nCommit: {PIN['commit']}\n"
        "Packaging and corresponding source archive: https://github.com/emaspa/openxlr\n"
        "The matching openxlr-yabridge source archive includes the pinned dependencies and build recipe.\n")
    # Include the dependency notices as well as providing their full sources.
    licenses = []
    for folder in (source / "subprojects", source / "tools/yabridgectl/vendor"):
        for path in sorted(folder.rglob("*")):
            if path.is_file() and ".git" not in path.parts and path.name.lower().startswith(("license", "copying")):
                licenses.append(f"\n--- {path.relative_to(source)} ---\n" + path.read_text(errors="replace"))
    (notices / "THIRD-PARTY-NOTICES").write_text("".join(licenses))
    return root


def source_archive(source, output):
    output.mkdir(parents=True, exist_ok=True)
    name = f"openxlr-yabridge-{PIN['version']}"
    archive = output / (name + "-source.tar.xz")
    run(["tar", "--sort=name", "--mtime=@" + str(PIN["sourceDateEpoch"]), "--owner=0", "--group=0", "--numeric-owner",
         "--exclude=.git", "--exclude=./tools/yabridgectl/target", "--exclude=__pycache__", "--exclude=./build", "--exclude=./out",
         "--transform=s,^\\.," + name + ",", "-cJf", archive, "-C", source, "."])
    return archive


def package(source, output, kind):
    if kind in ("srpm", "debian-source"):
        archive = source_archive(source, output / "dist")
        if kind == "srpm":
            top = output / "source-rpm"
            (top / "SOURCES").mkdir(parents=True, exist_ok=True)
            (top / "SPECS").mkdir(exist_ok=True)
            shutil.copy2(archive, top / "SOURCES" / archive.name)
            spec = (HERE / "source.spec.in").read_text().replace("@VERSION@", PIN["version"]).replace("@REVISION@", str(PIN["revision"]))
            spec_path = top / "SPECS/openxlr-yabridge.spec"
            spec_path.write_text(spec)
            run(["rpmbuild", "-bs", "--define", "_topdir " + str(top), "--define", "_dbpath " + str(top / "rpmdb"), spec_path])
            for file in (top / "SRPMS").glob("*.rpm"): shutil.copy2(file, output / "dist" / file.name)
        else:
            parent = output / "debian-source"
            parent.mkdir(parents=True, exist_ok=True)
            name = f"openxlr-yabridge-{PIN['version']}"
            run(["tar", "-xJf", archive, "-C", parent])
            shutil.copytree(HERE / "debian", parent / name / "debian", dirs_exist_ok=True)
            (parent / name / "debian/rules").chmod(0o755)
            (parent / name / "debian/changelog").write_text(
                f"openxlr-yabridge ({PIN['version']}-{PIN['revision']}) unstable; urgency=medium\n\n"
                "  * Package the pinned Windows plugin bridge and private wrapper support.\n\n"
                " -- Emanuele Sparvoli <sparvoli@gmail.com>  " + formatdate(PIN["sourceDateEpoch"]).replace("-0000", "+0000") + "\n")
            shutil.copy2(archive, parent / f"openxlr-yabridge_{PIN['version']}.orig.tar.xz")
            run(["dpkg-source", "-b", name], cwd=parent)
            for file in parent.glob("openxlr-yabridge_*"):
                if file.is_file(): shutil.copy2(file, output / "dist" / file.name)
        return
    destination = output / ("stage-" + kind)
    if destination.exists():
        raise RuntimeError(f"Staging directory already exists: {destination}")
    stage(source, output, destination)
    dist = output / "dist"
    dist.mkdir(exist_ok=True)
    version = PIN["version"]
    revision = PIN["revision"]
    if kind == "deb":
        control = destination / "DEBIAN"
        control.mkdir()
        # Debian builds should run on the oldest supported Ubuntu runner.
        libc = run(["getconf", "GNU_LIBC_VERSION"], capture_output=True, text=True).stdout.split()[-1]
        gcc = run(["g++", "-dumpversion"], capture_output=True, text=True).stdout.strip().split('.')[0]
        (control / "control").write_text(
            f"Package: openxlr-yabridge\nVersion: {version}-{revision}\nArchitecture: amd64\nSection: sound\nPriority: optional\n"
            "Maintainer: Emanuele Sparvoli <sparvoli@gmail.com>\n"
            f"Depends: wine, libc6 (>= {libc}), libstdc++6 (>= {gcc}), libgcc-s1, libxcb1, libdbus-1-3\n"
            "Homepage: https://github.com/emaspa/openxlr\n"
            "Description: Tested Windows plugin bridge for OpenXLR\n"
            " Pinned yabridge build with the Wine editor input fix, matching libraries\n"
            " and private wrapper support. Supports 64-bit Windows VST3 and CLAP plugins.\n")
        run(["dpkg-deb", "--root-owner-group", "--build", destination, dist / f"openxlr-yabridge_{version}-{revision}_amd64.deb"])
    elif kind == "rpm":
        top = output / "rpm"
        for subdir in ("BUILD", "BUILDROOT", "RPMS", "SOURCES", "SPECS", "SRPMS"):
            (top / subdir).mkdir(parents=True, exist_ok=True)
        run(["tar", "-czf", top / "SOURCES/payload.tar.gz", "-C", destination, "usr"])
        spec = (HERE / "openxlr-yabridge.spec.in").read_text().replace("@VERSION@", version).replace("@REVISION@", str(revision))
        (top / "SPECS/openxlr-yabridge.spec").write_text(spec)
        run(["rpmbuild", "-bb", "--define", "_topdir " + str(top), "--define", "_dbpath " + str(top / "rpmdb"),
             top / "SPECS/openxlr-yabridge.spec"])
        for file in (top / "RPMS").rglob("*.rpm"):
            shutil.copy2(file, dist / file.name)
    elif kind == "arch":
        size = sum(p.stat().st_size for p in destination.rglob("*") if p.is_file())
        libc = run(["getconf", "GNU_LIBC_VERSION"], capture_output=True, text=True).stdout.split()[-1]
        gcc = run(["g++", "-dumpversion"], capture_output=True, text=True).stdout.strip().split('.')[0]
        (destination / ".PKGINFO").write_text(
            f"pkgname = openxlr-yabridge\npkgbase = openxlr-yabridge\npkgver = {version}-{revision}\n"
            "pkgdesc = Tested Windows plugin bridge for OpenXLR\nurl = https://github.com/emaspa/openxlr\n"
            f"builddate = {PIN['sourceDateEpoch']}\npackager = Emanuele Sparvoli <sparvoli@gmail.com>\nsize = {size}\n"
            f"arch = x86_64\nlicense = GPL-3.0-only\nlicense = GPL-3.0-or-later\ndepend = wine\ndepend = gcc-libs>={gcc}\ndepend = glibc>={libc}\ndepend = libxcb\ndepend = dbus\n")
        run(["tar", "--sort=name", "--mtime=@" + str(PIN["sourceDateEpoch"]), "--owner=0", "--group=0", "--numeric-owner",
             "--zstd", "-cf", dist / f"openxlr-yabridge-{version}-{revision}-x86_64.pkg.tar.zst", "-C", destination, ".PKGINFO", "usr"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "build", "stage", "source-archive", "package"))
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("build-yabridge"))
    parser.add_argument("--destdir", type=Path)
    parser.add_argument("--prefix", default="/usr")
    parser.add_argument("--format", choices=("deb", "rpm", "arch", "srpm", "debian-source"))
    parser.add_argument("--jobs", type=int, default=2)
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if args.command == "prepare": prepare(source)
    elif args.command == "build": build(source, output, args.jobs)
    elif args.command == "stage":
        if args.destdir is None: parser.error("stage requires --destdir")
        stage(source, output, args.destdir.resolve(), args.prefix)
    elif args.command == "source-archive": source_archive(source, output / "dist")
    else:
        if args.format is None: parser.error("package requires --format")
        package(source, output, args.format)
    dist = output / "dist"
    if dist.exists():
        (dist / "SHA256SUMS").write_text("".join(
            hashlib.sha256(file.read_bytes()).hexdigest() + "  " + file.name + "\n"
            for file in sorted(dist.iterdir()) if file.is_file() and file.name != "SHA256SUMS"))


if __name__ == "__main__":
    main()
