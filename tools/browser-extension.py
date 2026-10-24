#!/usr/bin/env python3
"""Build the Chrome Web Store package of the browser companion.

    python3 -I tools/browser-extension.py OUTPUT_DIR [--key-pem PATH]
    python3 -I tools/browser-extension.py OUTPUT_DIR --unpacked
    python3 -I tools/browser-extension.py --check

Writes openxlr-browser-<version>.zip from browser/chromium. The Store
refuses a manifest that carries a "key", so the packaged manifest has it
removed. On the first upload only, --key-pem adds the extension's private
key as key.pem at the zip root; the Store then keeps the id the public key
in the source manifest pins, which the native host manifest allows. A zip
built with --key-pem holds a private key and is never published anywhere
else. The zip is reproducible: sorted entries, fixed timestamps and modes.

--unpacked writes openxlr-browser-<version>.tar.gz instead, the copy a
release offers for Load unpacked. It keeps the manifest's public key, so the
loaded extension has the id the native host allows, and it is not a zip
because OpenDeck offers every zip on the latest release as a deck plugin.
Its files sit under openxlr-browser-<version>/, also reproducible.

--check builds both into a temporary directory and validates them.
Standard library only; --key-pem also needs the openssl command.
"""
import argparse
import base64
import gzip
import hashlib
import io
import json
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

REPO = Path(__file__).resolve().parent.parent
SOURCE = REPO / "browser/chromium"
NATIVE_HOST = REPO / "browser/native-host.json"
EXTENSION_ID = "beeohanoffjpcofbjiiamkdammhcihaf"
ICON_SIZES = (16, 32, 48, 128)
# 1980-01-01 is the earliest time a zip entry can record.
STAMP = (1980, 1, 1, 0, 0, 0)
STAMP_SECONDS = 315532800
REFERENCE = re.compile(r"""^\.?/?[\w./-]+\.(?:html|mjs|js|css|png|json)$""")


def source_files(source=SOURCE):
    """Every file the extension ships: hidden files and tests stay out."""
    files = []
    for path in sorted(source.rglob("*")):
        relative = path.relative_to(source).as_posix()
        parts = relative.split("/")
        if not path.is_file() or any(p.startswith(".") for p in parts) or "tests" in parts or ".test." in parts[-1]:
            continue
        files.append(relative)
    return files


def extension_id(public_key_der):
    digest = hashlib.sha256(public_key_der).hexdigest()[:32]
    return "".join(chr(ord("a") + int(c, 16)) for c in digest)


def manifest_key(source=SOURCE):
    return base64.b64decode(json.loads((source / "manifest.json").read_text())["key"])


def public_der(pem):
    """The DER public key of a PEM private key, through openssl."""
    if not shutil.which("openssl"):
        raise SystemExit("--key-pem needs the openssl command to confirm the key matches the extension id")
    result = subprocess.run(["openssl", "pkey", "-in", str(pem), "-pubout", "-outform", "DER"],
                            capture_output=True, check=False)
    if result.returncode != 0:
        raise SystemExit(f"openssl could not read {pem}: {result.stderr.decode(errors='replace').strip()}")
    return result.stdout


def store_manifest(source=SOURCE):
    manifest = json.loads((source / "manifest.json").read_text())
    manifest.pop("key", None)
    return manifest


def entry(name, data, mode=0o644):
    info = zipfile.ZipInfo(name, STAMP)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.external_attr = (0o100000 | mode) << 16
    return info, data


def build(output, key_pem=None, source=SOURCE):
    """Write the zip into the directory output and return its path."""
    manifest = store_manifest(source)
    entries = []
    for name in source_files(source):
        data = (source / name).read_bytes()
        if name == "manifest.json":
            data = (json.dumps(manifest, indent=2) + "\n").encode()
        entries.append(entry(name, data))
    if key_pem is not None:
        key_pem = Path(key_pem)
        if extension_id(public_der(key_pem)) != extension_id(manifest_key(source)):
            raise SystemExit(f"{key_pem} is not the key of extension {extension_id(manifest_key(source))}")
        entries.append(entry("key.pem", key_pem.read_bytes(), 0o600))
    entries.sort(key=lambda item: item[0].filename)
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for info, data in entries:
            archive.writestr(info, data, compresslevel=9)
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    path = output / f"openxlr-browser-{manifest['version']}.zip"
    path.write_bytes(buffer.getvalue())
    return path


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", data[16:24])


def references(name, text):
    """Package files a page or module names: src and href attributes,
    imports and quoted file names such as an offscreen document's url."""
    found = re.findall(r"""\b(?:src|href)\s*=\s*["']([^"']+)["']""", text) if name.endswith(".html") else []
    found += [s for s in re.findall(r"""["']([^"'\s]+)["']""", text) if REFERENCE.match(s)]
    base = PurePosixPath(name).parent
    resolved = set()
    for value in found:
        if re.match(r"^[a-z]+:", value) or value.startswith("#"):
            continue
        path = PurePosixPath(value.lstrip("/")) if value.startswith("/") else base / value
        parts = []
        for part in path.parts:
            if part == "..":
                parts = parts[:-1]
            elif part != ".":
                parts.append(part)
        resolved.add("/".join(parts))
    return resolved


def validate(path, key_pem=False):
    """Problems with a built zip, as a list of sentences."""
    problems = []
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        files = {n: archive.read(n) for n in names}
        infos = archive.infolist()
    if names != sorted(names):
        problems.append("entries are not sorted")
    if any(i.date_time != STAMP for i in infos):
        problems.append("an entry carries a build time")
    manifest, problem = read_manifest(files)
    if manifest is None:
        return problems + [problem]
    if "key" in manifest:
        problems.append("the manifest still carries a key, which the Store refuses")
    if ("key.pem" in files) != key_pem:
        problems.append("key.pem is present" if not key_pem else "key.pem is missing")
    return problems + package_problems(manifest, files)


def read_manifest(files):
    """The manifest of a package's files, or None and the reason."""
    if "manifest.json" not in files:
        return None, "manifest.json is missing"
    try:
        return json.loads(files["manifest.json"]), None
    except ValueError as error:
        return None, f"manifest.json is not JSON: {error}"


def package_problems(manifest, files):
    """What both packages must hold: no tests, the icons, every reference."""
    problems = []
    for name in files:
        if "tests/" in name or ".test." in name:
            problems.append(f"{name} is a test file")
    action = manifest.get("action", {})
    wanted = {str(size) for size in ICON_SIZES}
    for label, icons in (("icons", manifest.get("icons", {})), ("action.default_icon", action.get("default_icon", {}))):
        if set(icons) != wanted:
            problems.append(f"{label} must list sizes {', '.join(sorted(wanted, key=int))}")
        for size, icon in icons.items():
            if icon not in files:
                problems.append(f"{label} {size} names {icon}, which is not in the zip")
            elif png_size(files[icon]) != (int(size), int(size)):
                problems.append(f"{icon} is not a {size} px PNG")
    needed = {manifest.get("background", {}).get("service_worker"), action.get("default_popup")}
    needed |= set(manifest.get("icons", {}).values()) | set(action.get("default_icon", {}).values())
    for name, data in files.items():
        if name.endswith((".html", ".mjs", ".js", ".css")):
            needed |= references(name, data.decode())
    for name in sorted(n for n in needed if n):
        if name not in files:
            problems.append(f"{name} is referenced but not in the zip")
    return problems


def build_unpacked(output, source=SOURCE):
    """Write the release's tar.gz into the directory output and return its path."""
    version = json.loads((source / "manifest.json").read_text())["version"]
    top = f"openxlr-browser-{version}"
    buffer = io.BytesIO()
    # A fixed gzip and entry time, owner and mode make two builds identical.
    with gzip.GzipFile(filename="", mode="wb", fileobj=buffer, mtime=0) as compressed, tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
        directories = sorted({str(d) for n in source_files(source) for d in PurePosixPath(n).parents} - {"."})
        for name in [top] + [f"{top}/{d}" for d in directories]:
            info = tarfile.TarInfo(name)
            info.type, info.mode, info.mtime = tarfile.DIRTYPE, 0o755, STAMP_SECONDS
            archive.addfile(info)
        for name in source_files(source):
            data = (source / name).read_bytes()
            info = tarfile.TarInfo(f"{top}/{name}")
            info.size, info.mode, info.mtime = len(data), 0o644, STAMP_SECONDS
            archive.addfile(info, io.BytesIO(data))
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    path = output / f"{top}.tar.gz"
    path.write_bytes(buffer.getvalue())
    return path


def validate_unpacked(path):
    """Problems with a built tar.gz, as a list of sentences."""
    problems = []
    with tarfile.open(path) as archive:
        members = archive.getmembers()
        files = {m.name: archive.extractfile(m).read() for m in members if m.isfile()}
    tops = {m.name.split("/")[0] for m in members}
    if len(tops) != 1:
        return [f"the files must sit under one directory, not {', '.join(sorted(tops))}"]
    top = tops.pop()
    if any(m.mtime != STAMP_SECONDS or m.uid or m.gid or m.uname or m.gname for m in members):
        problems.append("an entry carries a build time or an owner")
    if any(not (m.isfile() or m.isdir()) for m in members):
        problems.append("an entry is neither a file nor a directory")
    files = {name[len(top) + 1:]: data for name, data in files.items()}
    manifest, problem = read_manifest(files)
    if manifest is None:
        return problems + [problem]
    if top != f"openxlr-browser-{manifest.get('version')}":
        problems.append(f"the directory {top} does not name the manifest's version")
    try:
        key_id = extension_id(base64.b64decode(manifest["key"]))
    except (KeyError, ValueError, TypeError):
        key_id = None
    if key_id != EXTENSION_ID:
        problems.append(f"the manifest key must give id {EXTENSION_ID}, the one the native host allows")
    if "key.pem" in files:
        problems.append("key.pem is present")
    return problems + package_problems(manifest, files)


def check():
    problems = []
    source = json.loads((SOURCE / "manifest.json").read_text())
    if extension_id(manifest_key()) != EXTENSION_ID:
        problems.append(f"the manifest key gives id {extension_id(manifest_key())}, not {EXTENSION_ID}")
    host = json.loads(NATIVE_HOST.read_text())
    if host.get("allowed_origins") != [f"chrome-extension://{EXTENSION_ID}/"]:
        problems.append("the native host does not allow exactly this extension")
    with tempfile.TemporaryDirectory(prefix="openxlr-browser-") as first, \
            tempfile.TemporaryDirectory(prefix="openxlr-browser-") as second:
        built = build(first)
        if built.name != f"openxlr-browser-{source['version']}.zip":
            problems.append(f"unexpected package name {built.name}")
        problems += validate(built)
        if built.read_bytes() != build(second).read_bytes():
            problems.append("two builds differ")
        with zipfile.ZipFile(built) as archive:
            count = len(archive.namelist())
        unpacked = build_unpacked(first)
        if unpacked.name != f"openxlr-browser-{source['version']}.tar.gz":
            problems.append(f"unexpected archive name {unpacked.name}")
        problems += validate_unpacked(unpacked)
        if unpacked.read_bytes() != build_unpacked(second).read_bytes():
            problems.append("two unpacked builds differ")
    for problem in problems:
        print(f"browser-extension: {problem}", file=sys.stderr)
    print(f"browser-extension: {count} files, {'ok' if not problems else str(len(problems)) + ' problems'}")
    return 1 if problems else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("output", nargs="?", help="directory the package is written to")
    parser.add_argument("--key-pem", metavar="PATH", help="private key added as key.pem, for the first Store upload only")
    parser.add_argument("--unpacked", action="store_true", help="write the release tar.gz for Load unpacked, with the manifest key")
    parser.add_argument("--check", action="store_true", help="build into a temporary directory and validate")
    args = parser.parse_args(argv)
    if args.check:
        if args.output or args.key_pem or args.unpacked:
            parser.error("--check takes no other argument")
        return check()
    if not args.output:
        parser.error("name the output directory")
    if args.unpacked and args.key_pem:
        parser.error("--unpacked never carries the private key")
    if args.unpacked:
        path = build_unpacked(args.output)
        problems = validate_unpacked(path)
    else:
        path = build(args.output, args.key_pem)
        problems = validate(path, key_pem=args.key_pem is not None)
    for problem in problems:
        print(f"browser-extension: {problem}", file=sys.stderr)
    print(path)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
