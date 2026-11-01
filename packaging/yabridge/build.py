#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-only
"""Prepare pinned sources, build the optional bridge, and create binary packages.

Fetches are confined to 'prepare'. 'build' runs Meson and Cargo offline using
the prepared source tree. Only an explicit stage --destdir / --prefix /usr/local --manager manual installs locally.
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
import sys
import stat
import tempfile

HERE = Path(__file__).resolve().parent
PIN = json.loads((HERE / "source.json").read_text())
FILES = [f"libyabridge{loader}-{kind}.so" for loader in ("", "-chainloader") for kind in ("clap", "vst2", "vst3")]
FILES += ["yabridge-host.exe", "yabridge-host.exe.so"]
# Local patches to the pinned source, applied in this order.
PATCHES = ["private-plugin-home.patch", "disk-identity.patch"]


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
    for name in PATCHES:
        patch = HERE / name
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
    shutil.copy2(HERE.parent / "install_receipt.py", source / "openxlr-packaging/install_receipt.py")
    (source / "openxlr-packaging/prepared.json").write_text(json.dumps(PIN, sort_keys=True) + "\n")


TARGET_FLAG = re.compile(r"-(march|mtune|mcpu)=\S+|-m(no-)?(avx|sse|fma|bmi|f16c|lzcnt|movbe|popcnt|xsave|aes|pclmul)\S*")
GENERIC_TARGET = "-march=x86-64 -mtune=generic"


def generic_target_flags(flags):
    """Keep the packager's flags but compile for the baseline x86-64.

    The bridge host calls into Windows code through the Microsoft calling
    convention. GCC 16.2 spills AVX registers around those calls and reloads
    them from the wrong stack slot: with AVX-512 the reload faults on the
    first plugin factory call, with AVX2 it reads garbage. Baseline code has
    no such spills, and the host is IPC glue that gains nothing from the
    wider registers.
    """
    kept = " ".join(TARGET_FLAG.sub("", flags).split())
    return f"{kept} {GENERIC_TARGET}".strip()


def build(source, output, jobs):
    prepared = source / "openxlr-packaging/prepared.json"
    if not prepared.exists() or json.loads(prepared.read_text()) != PIN:
        raise RuntimeError("Run prepare first, or extract the corresponding source archive")
    output.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["SOURCE_DATE_EPOCH"] = str(PIN["sourceDateEpoch"])
    for name in ("CFLAGS", "CXXFLAGS"):
        env[name] = generic_target_flags(env.get(name, ""))
    native = output / "native"
    if not (native / "build.ninja").exists():
        run(["meson", "setup", native, source, "--buildtype=release", "--cross-file=" + str(source / "cross-wine.conf"),
             "--unity=on", "--unity-size=10000", "-Dbitbridge=false", "--wrap-mode=nodownload",
             "--force-fallback-for=asio,bitsery,function2,ghc_filesystem,tomlplusplus,clap"], env=env)
    run(["ninja", "-C", native, "-j", jobs], env=env)
    run(["cargo", "build", "--release", "--locked", "--offline", "--jobs", jobs,
         "--target-dir", output / "cargo"], cwd=source / "tools/yabridgectl", env=env)


def stage(source, output, destination, prefix="/usr", *, manager):
    if not prefix.startswith("/") or prefix.startswith("//") or prefix == "/" or str(Path(prefix)) != prefix or ".." in Path(prefix).parts:
        raise RuntimeError("Prefix must be an absolute normalized installation prefix")
    if destination.is_symlink():
        raise RuntimeError("Staging root must not be a symbolic link")
    if destination.exists() and not destination.is_dir():
        raise RuntimeError("Staging root must be a directory")
    if manager == "manual":
        # The local install publishes from a private staging tree. Under / it is
        # the live install; under any other root it is the same install rehearsed
        # there, so a packager or a check can run the documented upgrade.
        if prefix != "/usr/local":
            raise RuntimeError("Local installation requires --prefix /usr/local --manager manual")
        with tempfile.TemporaryDirectory(prefix="openxlr-yabridge-stage-") as temporary:
            staged = Path(temporary)
            stage_payload(source, output, staged, prefix, manager=manager)
            return install_staged(staged, prefix, destination)
    if destination.resolve() == Path("/"):
        raise RuntimeError("Live installation requires --prefix /usr/local --manager manual")
    if destination.exists() and any(destination.iterdir()):
        raise RuntimeError("Stage into an empty package root, then install the generated package; existing paths must not be overwritten")
    return stage_payload(source, output, destination, prefix, manager=manager)


def stage_payload(source, output, destination, prefix, *, manager):
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
    for name in PATCHES:
        shutil.copy2(HERE / name, notices / name)
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
    inventory(destination, prefix, manager=manager)
    return root


def inventory(destination, prefix="/usr", *, manager):
    tool = HERE / "install_receipt.py" if (HERE / "install_receipt.py").exists() else HERE.parent / "install_receipt.py"
    run([sys.executable, tool, "--root", destination, "--prefix", prefix, "--package", "openxlr-yabridge",
         "--manager", manager, "--version", f"{PIN['version']}-{PIN['revision']}", "--commit", PIN["commit"]])


def written_before_receipts(bridge_receipt):
    """Whether a tree with no installation receipt is one the stage before receipts wrote.

    That stage put the same payload at the same paths and recorded only the
    bridge receipt beside the libraries, in the format stage_payload still writes.
    """
    if bridge_receipt.is_symlink() or not bridge_receipt.is_file() or bridge_receipt.stat().st_size > 64 * 1024:
        return False
    try:
        bridge = json.loads(bridge_receipt.read_text())
    except (UnicodeDecodeError, ValueError):
        return False
    return (isinstance(bridge, dict) and bridge.get("formatVersion") == 1 and isinstance(bridge.get("version"), str)
            and re.fullmatch(r"[0-9a-f]{40}", str(bridge.get("sourceCommit"))) is not None
            and isinstance(bridge.get("windowsArchitectures"), list))


def install_staged(staging, prefix, destination=Path("/")):
    """Publish only the staged inventory, refusing redirected or foreign files."""
    receipt_path = Path(prefix.lstrip("/")) / "share/openxlr/installations/openxlr-yabridge.json"
    staged_receipt = staging / receipt_path
    inventory = json.loads(staged_receipt.read_text())
    target_receipt = destination / receipt_path

    def safe_parent(path):
        for parent in path.parents:
            if parent == destination:
                break
            if parent.is_symlink():
                raise RuntimeError("Refusing linked installation directory: " + str(parent))
            if parent.exists() and not parent.is_dir():
                raise RuntimeError("Installation parent is not a directory: " + str(parent))

    safe_parent(target_receipt)
    # The previous receipt is the proof of ownership: every path it lists may be
    # replaced, whatever it holds now, which is how a modified file is repaired.
    owned = set()
    if os.path.lexists(target_receipt):
        mode = target_receipt.lstat().st_mode
        if not stat.S_ISREG(mode) or target_receipt.stat().st_size > 4 * 1024 * 1024:
            raise RuntimeError("Refusing an unreadable or linked installed receipt")
        previous = json.loads(target_receipt.read_text())
        if (previous.get("formatVersion"), previous.get("package"), previous.get("manager"), previous.get("prefix")) != (1, "openxlr-yabridge", "manual", prefix):
            raise RuntimeError("Installed receipt belongs to another installer")
        owned = {entry["path"] for entry in previous["files"]}
    elif written_before_receipts(destination / prefix.lstrip("/") / "lib/openxlr/yabridge/openxlr-yabridge.json"):
        # An install made before receipts is adopted at the companion's own paths.
        owned = {entry["path"] for entry in inventory["files"] if entry["kind"] != "receipt"}

    def matches(path, entry):
        mode = path.lstat().st_mode
        if entry.get("kind") == "link":
            return stat.S_ISLNK(mode) and os.readlink(path) == entry.get("linkTarget")
        if not stat.S_ISREG(mode) or stat.S_IMODE(mode) != entry.get("mode") or path.stat().st_size > 512 * 1024 * 1024:
            return False
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest() == entry.get("sha256")

    entries = sorted(inventory["files"], key=lambda entry: entry["kind"] == "receipt")
    conflicts = []
    for entry in entries:
        target = destination / entry["path"].lstrip("/")
        safe_parent(target)
        if os.path.lexists(target) and target != target_receipt:
            # An unowned path stays only when it already equals the new payload.
            if not (matches(target, entry) or entry["path"] in owned and stat.S_ISREG(target.lstat().st_mode)):
                conflicts.append(str(target))
    if conflicts:
        raise RuntimeError("Refusing to overwrite paths this install does not own:\n  " + "\n  ".join(conflicts))
    # No live file changes until the entire inventory has passed preflight.
    # Publish the receipt last so an interrupted update remains diagnosable.
    for entry in entries:
        relative = entry["path"].lstrip("/")
        target = destination / relative
        missing = []
        parent = target.parent
        while not parent.exists():
            missing.append(parent)
            parent = parent.parent
        for directory in reversed(missing):
            directory.mkdir(mode=0o755)
            directory.chmod(0o755)
        with tempfile.NamedTemporaryFile(dir=target.parent, prefix=".openxlr-", delete=False) as temporary:
            temporary_path = Path(temporary.name)
        try:
            shutil.copy2(staging / relative, temporary_path)
            os.replace(temporary_path, target)
        finally:
            temporary_path.unlink(missing_ok=True)
    return destination / prefix.lstrip("/")


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
    stage(source, output, destination, manager={"deb": "debian", "rpm": "rpm", "arch": "arch"}[kind])
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
        tools = top / "receipt-tools"
        tools.mkdir()
        tool = HERE / "install_receipt.py" if (HERE / "install_receipt.py").exists() else HERE.parent / "install_receipt.py"
        shutil.copy2(tool, tools / "install_receipt.py")
        run(["tar", "-czf", top / "SOURCES/payload.tar.gz", "-C", destination, "usr", "-C", tools, "install_receipt.py"])
        spec = (HERE / "openxlr-yabridge.spec.in").read_text().replace("@VERSION@", version).replace("@REVISION@", str(revision)).replace("@COMMIT@", PIN["commit"])
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
    parser.add_argument("command", choices=("prepare", "build", "stage", "receipt", "source-archive", "package"))
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("build-yabridge"))
    parser.add_argument("--destdir", type=Path)
    parser.add_argument("--prefix", default="/usr")
    parser.add_argument("--manager", choices=("debian", "rpm", "arch", "nix", "manual"))
    parser.add_argument("--format", choices=("deb", "rpm", "arch", "srpm", "debian-source"))
    parser.add_argument("--jobs", type=int, default=2)
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    if args.command == "prepare": prepare(source)
    elif args.command == "build": build(source, output, args.jobs)
    elif args.command in ("stage", "receipt"):
        if args.destdir is None: parser.error(args.command + " requires --destdir")
        if args.manager is None: parser.error(args.command + " requires --manager")
        if args.command == "receipt": inventory(args.destdir.absolute(), args.prefix, manager=args.manager)
        else: stage(source, output, args.destdir.absolute(), args.prefix, manager=args.manager)
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
