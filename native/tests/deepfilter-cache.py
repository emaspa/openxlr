#!/usr/bin/env python3
"""Exercise the optional Cargo build cache without compiling the model."""

from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


WRAPPER = Path(__file__).resolve().parents[1] / "deepfilter" / "build-cache.py"


class CacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.env = dict(os.environ, OPENXLR_DEEPFILTER_BUILD_CACHE=str(self.root / "cache"))
        self.env.pop("CARGO_BUILD_BUILD_DIR", None)

    def workspace(self, name, mtime):
        root = self.root / name
        for relative in ("Cargo.toml", "Cargo.lock", "src/lib.rs", "libdf/Cargo.toml",
                         "libdf/src/lib.rs", "model/DeepFilterNet3_onnx.tar.gz"):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(relative)
            os.utime(path, (mtime, mtime))
        return root

    def command(self, workspace, **overrides):
        return json.loads(subprocess.check_output(
            [sys.executable, str(WRAPPER), sys.executable, "-c",
             "import json, os, sys; print(json.dumps([os.getenv('CARGO_BUILD_BUILD_DIR', ''), sys.argv[1:]]))",
             "--target-dir", "target"],
            cwd=workspace, env=self.env | overrides, text=True,
        ))

    def build_dir(self, workspace, **overrides):
        return self.command(workspace, **overrides)[0]

    def test_release_directories_with_identical_inputs_share_a_cache(self):
        first = self.workspace("openxlr-first", 100)
        second = self.workspace("openxlr-second", 200)
        with ThreadPoolExecutor(max_workers=2) as pool:
            first_command, second_command = pool.map(self.command, (first, second))
        self.assertEqual(first_command, second_command)
        build = Path(second_command[0])
        self.assertEqual(build.parent.parent, self.root / "cache" / "v1")
        self.assertEqual(second_command[1],
                         ["--target-dir", "target", "--manifest-path", str(build.parent / "source/Cargo.toml")])
        self.assertEqual((build.parent / "source/src/lib.rs").read_text(), "src/lib.rs")

    def test_damaged_snapshot_falls_back_to_the_checkout(self):
        workspace = self.workspace("openxlr-release", 100)
        build = Path(self.build_dir(workspace))
        (build.parent / "source/src/lib.rs").write_text("corrupt")
        self.assertEqual(self.build_dir(workspace), "")

    def test_changes_to_sources_lockfile_and_model_select_new_caches(self):
        workspace = self.workspace("openxlr-release", 100)
        previous = self.build_dir(workspace)
        for name in ("src/lib.rs", "Cargo.lock", "model/DeepFilterNet3_onnx.tar.gz",
                     "libdf/src/new-module.rs"):
            path = workspace / name
            path.write_text("changed")
            os.utime(path, (100, 100))
            current = self.build_dir(workspace)
            self.assertNotEqual(previous, current)
            previous = current

    def test_explicit_and_vendored_builds_keep_their_own_caches(self):
        workspace = self.workspace("openxlr-release", 100)
        self.assertEqual(self.build_dir(workspace, CARGO_BUILD_BUILD_DIR="/explicit"), "/explicit")
        self.assertEqual(self.build_dir(workspace, OPENXLR_DEEPFILTER_BUILD_CACHE=""), "")
        (workspace / "vendor").mkdir()
        self.assertEqual(self.build_dir(workspace), "")


if __name__ == "__main__":
    unittest.main()
