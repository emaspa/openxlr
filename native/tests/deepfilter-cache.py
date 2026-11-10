#!/usr/bin/env python3
"""Exercise Cargo cache selection without compiling the model."""

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
        for name in ("CARGO_BUILD_BUILD_DIR", "OPENXLR_BUILD_CACHE", "CI"):
            self.env.pop(name, None)
        self.cargo = self.root / "cargo.py"
        self.cargo.write_text("""import json, os, sys
if sys.argv[1:] == ["--version"]:
    print("cargo " + os.getenv("TEST_CARGO_VERSION", "1.91.0"))
elif sys.argv[1] == "metadata":
    print(json.dumps({"target_directory": "/target",
                      "build_directory": os.getenv("TEST_CONFIG_BUILD_DIR", "/target")}))
else:
    print(json.dumps([os.getenv("CARGO_BUILD_BUILD_DIR", ""), sys.argv[1:]]))
""")

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
            [sys.executable, str(WRAPPER), sys.executable, str(self.cargo), "--",
             "build", "--target-dir", "target"],
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
        self.assertEqual(second_command[1], ["build", "--target-dir", "target", "--manifest-path",
                                             str(build.parent / "source/Cargo.toml")])
        self.assertEqual((build.parent / "source/src/lib.rs").read_text(), "src/lib.rs")

    def test_damaged_snapshot_falls_back_to_the_checkout(self):
        workspace = self.workspace("openxlr-release", 100)
        build = Path(self.build_dir(workspace))
        (build.parent / "source/src/lib.rs").write_text("corrupt")
        self.assertEqual(self.build_dir(workspace), "")

    def test_untrusted_build_or_source_directory_disables_the_cache(self):
        workspace = self.workspace("openxlr-release", 100)
        build = Path(self.build_dir(workspace))
        for directory in (build, build.parent / "source"):
            with self.subTest(directory=directory):
                original = directory.with_name(directory.name + "-moved")
                directory.rename(original)
                directory.symlink_to(original, target_is_directory=True)
                self.assertEqual(self.build_dir(workspace), "")
                directory.unlink()
                original.rename(directory)
        build.chmod(0o777)
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

    def test_default_uses_xdg_cache_without_an_opt_in(self):
        workspace = self.workspace("openxlr-release", 100)
        self.env.pop("OPENXLR_DEEPFILTER_BUILD_CACHE")
        self.env["XDG_CACHE_HOME"] = str(self.root / "xdg")
        build = self.build_dir(workspace)
        if os.geteuid() == 0:
            self.assertEqual(build, "")
        else:
            self.assertEqual(Path(build).parents[2], self.root / "xdg/openxlr/deepfilter-build")
        self.assertEqual(self.build_dir(workspace, CI="true"), "")

    def test_opt_out_old_cargo_and_unavailable_cache_build_locally(self):
        workspace = self.workspace("openxlr-release", 100)
        self.assertEqual(self.build_dir(workspace, OPENXLR_BUILD_CACHE="0"), "")
        self.assertEqual(self.build_dir(workspace, TEST_CARGO_VERSION="1.90.0"), "")
        unavailable = self.root / "file"
        unavailable.touch()
        self.assertEqual(self.build_dir(workspace, OPENXLR_DEEPFILTER_BUILD_CACHE=str(unavailable)), "")

    def test_cargo_template_paths_fall_back_without_creating_a_cache(self):
        workspace = self.workspace("openxlr-release", 100)
        for name in ("parent{lab}", "parent{", "parent}"):
            with self.subTest(name=name):
                cache = self.root / name / "cache"
                self.assertEqual(self.build_dir(workspace, OPENXLR_DEEPFILTER_BUILD_CACHE=str(cache)), "")
                self.assertFalse(cache.parent.exists())

        resolved = self.root / "resolved{lab}"
        resolved.mkdir()
        link = self.root / "configured"
        link.symlink_to(resolved, target_is_directory=True)
        self.assertEqual(self.build_dir(workspace, OPENXLR_DEEPFILTER_BUILD_CACHE=str(link / "cache")), "")
        self.assertFalse((resolved / "cache").exists())

    def test_cargo_config_keeps_its_build_directory(self):
        workspace = self.workspace("openxlr-release", 100)
        self.assertEqual(self.build_dir(workspace, TEST_CONFIG_BUILD_DIR="/configured"), "")


if __name__ == "__main__":
    unittest.main()
