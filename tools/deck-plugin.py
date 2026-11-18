#!/usr/bin/env python3
"""Build the OpenDeck plugin zip a release carries.

    python3 -I tools/deck-plugin.py OUTPUT_DIR
    python3 -I tools/deck-plugin.py --check

Writes com.emaspa.openxlr.sdPlugin.zip from plugin/com.emaspa.openxlr.sdPlugin,
with every file under that folder name, the layout OpenDeck's install from
file expects. OpenDeck offers every zip on the latest release as a deck
plugin, so this is the only zip a release carries. The zip is
reproducible: sorted entries, fixed timestamps and modes, so the same tree
gives the same bytes and the checksum a release workflow attests.

--check builds twice into a temporary directory and validates the result.
Standard library only.
"""
import argparse
import io
import json
import sys
import tempfile
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
FOLDER = "com.emaspa.openxlr.sdPlugin"
SOURCE = REPO / "plugin" / FOLDER
NAME = f"{FOLDER}.zip"
# 1980-01-01 is the earliest time a zip entry can record.
STAMP = (1980, 1, 1, 0, 0, 0)


def source_files(source=SOURCE):
    """Every file the plugin ships, relative to its folder; hidden files stay out."""
    files = []
    for path in source.rglob("*"):
        relative = path.relative_to(source).as_posix()
        if path.is_file() and not any(part.startswith(".") for part in relative.split("/")):
            files.append(relative)
    return sorted(files)


def build(output, source=SOURCE):
    """Write the zip into the directory output and return its path."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name in source_files(source):
            info = zipfile.ZipInfo(f"{FOLDER}/{name}", STAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, (source / name).read_bytes(), compresslevel=9)
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    path = output / NAME
    path.write_bytes(buffer.getvalue())
    return path


def referenced(manifest):
    """Files the manifest names. Icons are named without an extension."""
    files = [manifest.get("CodePath")]
    icons = [manifest.get("Icon"), manifest.get("CategoryIcon")]
    for action in manifest.get("Actions", []):
        files.append(action.get("PropertyInspectorPath"))
        files.append(action.get("Encoder", {}).get("layout"))
        icons.append(action.get("Icon"))
        icons += [state.get("Image") for state in action.get("States", [])]
    return [f for f in files if f], [i for i in icons if i]


def validate(path):
    """Problems with a built zip, as a list of sentences."""
    problems = []
    with zipfile.ZipFile(path) as archive:
        infos = archive.infolist()
        files = {i.filename: archive.read(i) for i in infos}
    names = [i.filename for i in infos]
    if names != sorted(names):
        problems.append("entries are not sorted")
    if any(i.date_time != STAMP for i in infos):
        problems.append("an entry carries a build time")
    if any(not n.startswith(f"{FOLDER}/") for n in names):
        problems.append(f"every entry must sit under {FOLDER}/")
    files = {n[len(FOLDER) + 1:]: data for n, data in files.items() if n.startswith(f"{FOLDER}/")}
    if "manifest.json" not in files:
        return problems + ["manifest.json is missing"]
    try:
        manifest = json.loads(files["manifest.json"])
    except ValueError as error:
        return problems + [f"manifest.json is not JSON: {error}"]
    wanted, icons = referenced(manifest)
    for name in wanted:
        if name not in files:
            problems.append(f"the manifest names {name}, which is not in the zip")
    for icon in icons:
        if not any(f"{icon}{ext}" in files for ext in (".png", ".svg", "@2x.png")):
            problems.append(f"the manifest names icon {icon}, which is not in the zip")
    return problems


def check():
    problems = []
    with tempfile.TemporaryDirectory(prefix="openxlr-deck-") as first, \
            tempfile.TemporaryDirectory(prefix="openxlr-deck-") as second:
        built = build(first)
        problems += validate(built)
        if built.read_bytes() != build(second).read_bytes():
            problems.append("two builds differ")
        with zipfile.ZipFile(built) as archive:
            count = len(archive.namelist())
    for problem in problems:
        print(f"deck-plugin: {problem}", file=sys.stderr)
    print(f"deck-plugin: {count} files, {'ok' if not problems else str(len(problems)) + ' problems'}")
    return 1 if problems else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("output", nargs="?", help="directory the zip is written to")
    parser.add_argument("--check", action="store_true", help="build into a temporary directory and validate")
    args = parser.parse_args(argv)
    if args.check:
        if args.output:
            parser.error("--check takes no other argument")
        return check()
    if not args.output:
        parser.error("name the output directory")
    path = build(args.output)
    problems = validate(path)
    for problem in problems:
        print(f"deck-plugin: {problem}", file=sys.stderr)
    print(path)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
