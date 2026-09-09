#!/usr/bin/env python3
"""Prepare locked sources, build offline, and export a manually installable Flatpak."""
import argparse
import concurrent.futures
import hashlib
import json
import os
import platform
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
APP_ID = "io.github.emaspa.OpenXLR"


def run(*args, **kwargs):
    subprocess.run(args, check=True, **kwargs)


def locked_packages():
    packages = {}
    for lock in sorted((ROOT / "src").glob("*/packages.lock.json")):
        for framework in json.loads(lock.read_text())["dependencies"].values():
            for name, dependency in framework.items():
                if dependency["type"] == "Project":
                    continue
                key = (name.lower(), dependency["resolved"].lower())
                digest = dependency["contentHash"]
                if key in packages and packages[key] != digest:
                    raise ValueError(f"Conflicting package hashes: {key}")
                packages[key] = digest
    return packages


def prepare(output):
    output.mkdir(parents=True, exist_ok=True)
    nuget = output / "nuget"
    nuget.mkdir(exist_ok=True)

    def fetch(item):
        (name, version), expected = item
        filename = f"{name}.{version}.nupkg"
        target = nuget / filename
        cached = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")) / name / version / filename
        if target.exists():
            data = target.read_bytes()
        elif cached.exists():
            data = cached.read_bytes()
        else:
            url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{filename}"
            with urllib.request.urlopen(url, timeout=60) as response:
                data = response.read()
        # NuGet contentHash is not the raw archive hash for signed packages.
        # Locked restore below verifies it using NuGet's canonical algorithm.
        target.write_bytes(data)
        return {"type": "file", "path": str(target), "sha256": hashlib.sha256(data).hexdigest(), "dest": "nuget-sources"}

    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        packages = list(pool.map(fetch, sorted(locked_packages().items())))
    # Include the working tree, so a development artifact actually tests local changes.
    names = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT).split(b"\0")
    with tarfile.open(output / "openxlr-source.tar.gz", "w:gz") as archive:
        for name in sorted(set(names)):
            if not name:
                continue
            relative = Path(os.fsdecode(name))
            source = ROOT / relative
            if source.is_file() and not any(part in {"bin", "obj", ".git", ".flatpak-builder"} for part in relative.parts):
                archive.add(source, arcname=str(Path("openxlr") / relative), recursive=False)
    manifest = json.loads((ROOT / "packaging/flatpak/io.github.emaspa.OpenXLR.json").read_text())
    # A local archive path alone is not a content identity for Builder's cache.
    # Include the snapshot hash so edits cannot reuse an older application build.
    manifest["modules"][-1]["sources"][0]["sha256"] = hashlib.sha256((output / "openxlr-source.tar.gz").read_bytes()).hexdigest()
    manifest["modules"][-1]["sources"] += packages
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Prepared {len(packages)} NuGet archives for locked restore and source archive in {output}")


def build(output):
    builder = ["flatpak-builder"] if shutil.which("flatpak-builder") else ["flatpak", "run", "org.flatpak.Builder"]
    # Builder fetches declared archives first; compilation has no network permission.
    run(*builder, "--user", "--install-deps-from=flathub", "--force-clean",
        f"--repo={output / 'repo'}", str(output / "build"), str(output / "manifest.json"), cwd=output)
    dist = output / "dist"
    dist.mkdir(exist_ok=True)
    run("flatpak", "build-bundle", "--runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo",
        str(output / "repo"), str(dist / "OpenXLR-x86_64.flatpak"), APP_ID)
    for relative in ["packaging/70-openxlr.rules", "packaging/50-xlr-dock-capture-hold.conf",
                     "packaging/pipewire-pulse-openxlr.conf", "packaging/flatpak/README.md"]:
        shutil.copy2(ROOT / relative, dist / Path(relative).name)
    shutil.copy2(output / "openxlr-source.tar.gz", dist / "openxlr-source.tar.gz")
    sums = []
    for file in sorted(dist.iterdir()):
        if file.is_file() and file.name != "SHA256SUMS":
            sums.append(f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}")
    (dist / "SHA256SUMS").write_text("\n".join(sums) + "\n")
    print(f"Artifacts: {dist}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["prepare", "build", "all"])
    parser.add_argument("--output", type=Path, default=Path.home() / ".cache/openxlr-flatpak-build")
    args = parser.parse_args()
    if platform.machine() != "x86_64":
        parser.error("This test bundle currently targets Linux x86_64 only.")
    output = args.output.resolve()
    if args.command in {"prepare", "all"}:
        prepare(output)
    if args.command in {"build", "all"}:
        build(output)


if __name__ == "__main__":
    main()
