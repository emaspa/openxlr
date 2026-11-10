"""Opt into a Cargo build directory keyed by the DeepFilterNet inputs."""

import hashlib
import os
from pathlib import Path
import shutil
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


def main():
    command = sys.argv[1:]
    cache = os.environ.get("OPENXLR_DEEPFILTER_BUILD_CACHE")
    if cache and not os.environ.get("CARGO_BUILD_BUILD_DIR") and not Path("vendor").is_dir():
        paths = input_paths(Path.cwd())
        if paths is not None and (manifest := cached_manifest(cache, paths)) is not None:
            # Stable source timestamps keep Cargo from rebuilding the wrapper
            # when makepkg copies the same model into a new release directory.
            os.environ["CARGO_BUILD_BUILD_DIR"] = str(manifest.parent.parent / "build")
            command.extend(("--manifest-path", str(manifest)))
    os.execvp(command[0], command)


if __name__ == "__main__":
    main()
