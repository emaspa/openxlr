"""Reuse a Cargo build directory keyed by the DeepFilterNet inputs."""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


def input_paths(root):
    paths = [Path("Cargo.toml"), Path("Cargo.lock"),
             Path("model/DeepFilterNet3_onnx.tar.gz")]
    if (root / "build.rs").exists():
        paths.append(Path("build.rs"))
    for folder in (Path("src"), Path("libdf")):
        directory = root / folder
        entries = [directory, *directory.rglob("*")]
        if any(entry.is_symlink() for entry in entries):
            return None
        paths.extend(entry.relative_to(root) for entry in entries if entry.is_file())
    return None if any((root / path).is_symlink() for path in paths) else sorted(paths)


def input_key(root, paths):
    digest = hashlib.sha256()
    for path in paths:
        digest.update(os.fsencode(path.as_posix()) + b"\0")
        digest.update(hashlib.sha256((root / path).read_bytes()).digest())
    return digest.hexdigest()


def matches(root, key):
    paths = input_paths(root)
    if paths is None or not all((root / path).is_file() for path in paths):
        return False
    return input_key(root, paths) == key


def cached_manifest(cache, paths):
    root = Path.cwd()
    key = input_key(root, paths)
    directory = Path(cache).expanduser().resolve() / "v1" / key
    source = directory / "source"
    if not source.is_dir():
        directory.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=directory) as temporary:
            snapshot = Path(temporary) / "source"
            for path in paths:
                destination = snapshot / path
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(root / path, destination)
            if not matches(snapshot, key):
                return None
            try:
                snapshot.rename(source)
            except OSError:
                if not source.is_dir():
                    raise
    if matches(source, key):
        return source / "Cargo.toml"
    return None


def cache_root():
    configured = os.environ.get("OPENXLR_DEEPFILTER_BUILD_CACHE")
    if configured is not None:
        return Path(configured) if configured else None
    if os.geteuid() == 0 or os.environ.get("CI"):
        return None
    xdg_cache = Path(os.environ.get("XDG_CACHE_HOME", ""))
    if not xdg_cache.is_absolute():
        xdg_cache = Path.home() / ".cache"
    return xdg_cache / "openxlr/deepfilter-build"


def supports_build_dir(cargo):
    try:
        version = subprocess.check_output([*cargo, "--version"], text=True, stderr=subprocess.DEVNULL)
    except (OSError, subprocess.CalledProcessError):
        return False
    match = re.match(r"cargo (\d+)\.(\d+)\.", version)
    return match is not None and tuple(map(int, match.groups())) >= (1, 91)


def uses_default_build_dir(cargo):
    try:
        # Match the target directory passed to cargo build, including when
        # the user's config sets both build-dir and target-dir.
        output = subprocess.check_output(
            [*cargo, "metadata", "--locked", "--offline", "--no-deps", "--format-version", "1",
             "--config", 'build.target-dir="target"'],
            stderr=subprocess.DEVNULL,
        )
        metadata = json.loads(output)
        return metadata["build_directory"] == metadata["target_directory"]
    except (OSError, subprocess.CalledProcessError, ValueError, KeyError):
        return False


def main():
    separator = sys.argv.index("--", 1)
    cargo = sys.argv[1:separator]
    command = cargo + sys.argv[separator + 1:]
    if (os.environ.get("OPENXLR_BUILD_CACHE") != "0" and
            not os.environ.get("CARGO_BUILD_BUILD_DIR") and not Path("vendor").is_dir()):
        try:
            cache = cache_root()
            if cache is not None and supports_build_dir(cargo) and uses_default_build_dir(cargo):
                paths = input_paths(Path.cwd())
                if paths is not None and (manifest := cached_manifest(cache, paths)) is not None:
                    build_dir = manifest.parent.parent / "build"
                    build_dir.mkdir(exist_ok=True)
                    if os.access(build_dir, os.W_OK | os.X_OK):
                        # Stable input timestamps survive release extraction.
                        os.environ["CARGO_BUILD_BUILD_DIR"] = str(build_dir)
                        command.extend(("--manifest-path", str(manifest)))
        except (OSError, RuntimeError):
            # A user cache can be missing or unwritable. Build in the checkout.
            pass
    os.execvp(command[0], command)


if __name__ == "__main__":
    main()
