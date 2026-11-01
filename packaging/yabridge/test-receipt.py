#!/usr/bin/env python3
"""Companion inventory, staging contract and local publication regressions."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("bridge_build", HERE / "build.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
COMMIT = "0123456789abcdef0123456789abcdef01234567"


def without_strip():
    """Real binaries and post-processing are covered by the package job.

    This fixture isolates the inventory and safe publication contract.
    """
    original = bridge.run
    def run(arguments, **kwargs):
        if arguments[0] != "strip":
            return original(arguments, **kwargs)
    return patch.object(bridge, "run", side_effect=run)


def snapshot(root):
    """Every path under root with what it holds, to prove a refused run wrote nothing."""
    tree = {}
    for path in sorted(root.rglob("*")):
        mode = path.lstat().st_mode
        content = os.readlink(path) if path.is_symlink() else path.read_bytes() if path.is_file() else None
        tree[str(path.relative_to(root))] = (mode, content)
    return tree


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="openxlr-companion-")
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        (self.source / "COPYING").write_text("license")
        self.output = self.root / "build"
        (self.output / "native").mkdir(parents=True)
        (self.output / "cargo/release").mkdir(parents=True)
        for name in bridge.FILES:
            (self.output / "native" / name).write_bytes(b"test binary")
        (self.output / "cargo/release/yabridgectl").write_bytes(b"controller")
        self.stage_root = self.root / "stage"
        self.destination = self.root / "installed"
        self.destination.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def stage(self, manager="manual", prefix="/usr/local"):
        with without_strip():
            bridge.stage(self.source, self.output, self.stage_root, prefix, manager=manager)
        return self.stage_root / prefix.lstrip("/")

    def install(self):
        """The documented local install, rehearsed under the test's own root."""
        with without_strip():
            return bridge.stage(self.source, self.output, self.destination, "/usr/local", manager="manual")

    def install_before_receipts(self, contents=b"previous build"):
        """Write the tree the stage before receipts wrote: the payload paths and no installation receipt."""
        root = self.destination / "usr/local"
        payload = root / "lib/openxlr/yabridge"
        payload.mkdir(parents=True)
        for name in bridge.FILES + ["yabridgectl"]:
            (payload / name).write_bytes(contents)
            (payload / name).chmod(0o755 if name in ("yabridgectl", "yabridge-host.exe") else 0o644)
        (payload / "openxlr-yabridge.json").write_text(json.dumps({
            "formatVersion": 1, "version": "5.1.1.54-1", "sourceCommit": COMMIT,
            "wineInputFix": True, "windowsArchitectures": ["x86_64"]}, indent=2) + "\n")
        launcher = root / "bin/openxlr-yabridgectl"
        launcher.parent.mkdir()
        launcher.write_text("#!/bin/sh\nprevious launcher\n")
        launcher.chmod(0o755)
        notices = root / "share/licenses/openxlr-yabridge"
        notices.mkdir(parents=True)
        for name in ("COPYING", "source.json", "private-plugin-home.patch", "SOURCE", "THIRD-PARTY-NOTICES"):
            (notices / name).write_text("previous " + name)
        return root

    def test_manager_is_required_in_cli_and_function(self):
        with self.assertRaises(TypeError):
            bridge.stage(self.source, self.output, self.stage_root)
        result = subprocess.run(["python3", str(HERE / "build.py"), "stage", "--source", str(self.source),
                                 "--output", str(self.output), "--destdir", str(self.stage_root)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("requires --manager", result.stderr)
        self.assertFalse(self.stage_root.exists())

    def test_arch_receipt_uses_the_pin_and_correct_manager(self):
        payload = self.stage(manager="arch", prefix="/usr")
        receipt = json.loads((payload / "share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("arch", receipt["manager"])
        self.assertEqual(bridge.PIN["commit"], receipt["commit"])
        self.assertTrue(any(entry["path"] == "/usr/bin/openxlr-yabridgectl" for entry in receipt["files"]))
        self.assertEqual(bridge.PIN, json.loads((HERE / "source.json").read_text()))

    def test_every_local_patch_is_listed_and_ships_with_the_notices(self):
        self.assertEqual(["private-plugin-home.patch", "disk-identity.patch"], bridge.PATCHES)
        # prepare applies the patches to the pinned checkout and the bridge
        # workflow builds from it; here, check each one parses and touches only
        # the files it is meant to.
        touched = {}
        for name in bridge.PATCHES:
            stat = subprocess.run(["git", "apply", "--numstat", str(HERE / name)], check=True,
                                  capture_output=True, text=True, cwd=self.root).stdout
            touched[name] = {line.split("\t")[2] for line in stat.splitlines()}
        self.assertEqual({"tools/yabridgectl/src/config.rs"}, touched["private-plugin-home.patch"])
        self.assertEqual({"src/wine-host/disk-identity.cpp", "src/wine-host/disk-identity.h",
                          "src/wine-host/host.cpp", "src/wine-host/meson.build"}, touched["disk-identity.patch"])
        payload = self.stage(manager="arch", prefix="/usr")
        receipt = json.loads((payload / "share/openxlr/installations/openxlr-yabridge.json").read_text())
        paths = {entry["path"] for entry in receipt["files"]}
        for name in bridge.PATCHES:
            self.assertEqual((HERE / name).read_bytes(), (payload / "share/licenses/openxlr-yabridge" / name).read_bytes())
            self.assertIn("/usr/share/licenses/openxlr-yabridge/" + name, paths)
        nix = (HERE.parent / "nix/yabridge.nix").read_text()
        for name in bridge.PATCHES:
            self.assertEqual(2, nix.count("../yabridge/" + name), name)

    def test_debian_source_recipe_stages_with_the_debian_manager(self):
        recipe = subprocess.run(["make", "--no-print-directory", "--just-print", "-f", str(HERE / "debian/rules"),
                                 "override_dh_auto_install"], check=True, capture_output=True, text=True)
        commands = [shlex.split(line) for line in recipe.stdout.splitlines()]
        command, = [args for args in commands if len(args) > 2 and args[2] == "stage"]
        for flag, value in (("--source", self.source), ("--output", self.output), ("--destdir", self.stage_root)):
            command[command.index(flag) + 1] = str(value)
        with patch.object(sys, "argv", command[1:]), without_strip():
            bridge.main()
        receipt = json.loads((self.stage_root / "usr/share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("debian", receipt["manager"])
        self.assertEqual(bridge.PIN["commit"], receipt["commit"])

    def test_explicit_local_install_stages_before_publication(self):
        with without_strip(), patch.object(bridge, "install_staged", return_value=Path("/usr/local")) as install:
            self.assertEqual(Path("/usr/local"), bridge.stage(self.source, self.output, Path("/"), "/usr/local", manager="manual"))
            install.assert_called_once()
            staged, prefix, destination = install.call_args.args
            self.assertEqual(("/usr/local", Path("/")), (prefix, destination))
            self.assertNotEqual(Path("/"), staged)
        for manager, prefix in (("arch", "/usr/local"), ("manual", "/usr")):
            with self.assertRaises(RuntimeError):
                bridge.stage(self.source, self.output, Path("/"), prefix, manager=manager)

    def test_manual_stage_under_another_root_runs_the_live_install_there(self):
        tools = self.root / "tools"
        tools.mkdir()
        (tools / "strip").write_text("#!/bin/sh\nexit 0\n")
        (tools / "strip").chmod(0o755)
        environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ.get("PATH", ""))
        command = [sys.executable, str(HERE / "build.py"), "stage", "--source", str(self.source), "--output", str(self.output),
                   "--destdir", str(self.destination), "--prefix", "/usr/local", "--manager", "manual"]
        unrelated = self.destination / "usr/local/unrelated"
        unrelated.parent.mkdir(parents=True)
        unrelated.write_text("keep")
        # The root is not empty, and the second run is the documented upgrade.
        for _ in range(2):
            subprocess.run(command, check=True, env=environment)
        receipt = json.loads((self.destination / "usr/local/share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual(("manual", "/usr/local"), (receipt["manager"], receipt["prefix"]))
        self.assertEqual("keep", unrelated.read_text())
        self.assertFalse(any(path.name.startswith(".openxlr-") for path in self.destination.rglob("*")))

    def test_local_install_owns_only_companion_files_and_can_be_repeated(self):
        self.stage()
        unrelated = self.destination / "usr/local/unrelated"
        unrelated.parent.mkdir(parents=True)
        unrelated.write_text("keep")
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        receipt = json.loads((self.destination / "usr/local/share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("manual", receipt["manager"])
        self.assertFalse(any(f["path"].endswith("unrelated") for f in receipt["files"]))
        self.assertEqual("keep", unrelated.read_text())
        for entry in receipt["files"]:
            if entry["kind"] == "file":
                self.assertEqual(entry["sha256"], hashlib.sha256((self.destination / entry["path"].lstrip("/")).read_bytes()).hexdigest())

    def test_unchanged_owned_files_can_be_upgraded_without_claiming_other_paths(self):
        payload = self.stage()
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        changed = payload / "lib/openxlr/yabridge/libyabridge-vst3.so"
        changed.write_bytes(b"new build")
        bridge.inventory(self.stage_root, "/usr/local", manager="manual")
        bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual(b"new build", (self.destination / "usr/local/lib/openxlr/yabridge/libyabridge-vst3.so").read_bytes())

    def test_local_install_uses_public_modes_only_for_new_directories(self):
        self.stage()
        prefix = self.destination / "usr/local"
        prefix.mkdir(parents=True)
        prefix.chmod(0o750)
        old_umask = os.umask(0o077)
        try:
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        finally:
            os.umask(old_umask)
        self.assertEqual(0o750, prefix.stat().st_mode & 0o777)
        self.assertEqual(0o755, (prefix / "lib/openxlr/yabridge").stat().st_mode & 0o777)
        self.assertEqual(0o755, (prefix / "bin/openxlr-yabridgectl").stat().st_mode & 0o777)

    def test_foreign_file_refuses_the_entire_install_before_writes(self):
        conflicting = self.destination / "usr/local/bin/openxlr-yabridgectl"
        conflicting.parent.mkdir(parents=True)
        conflicting.write_text("foreign")
        before = snapshot(self.destination)
        with self.assertRaises(RuntimeError):
            self.install()
        self.assertEqual(before, snapshot(self.destination))
        conflicting.unlink()
        self.install()
        conflicting.unlink()
        conflicting.symlink_to("/bin/true")
        before = snapshot(self.destination)
        with self.assertRaises(RuntimeError) as refused:
            self.install()
        self.assertEqual(before, snapshot(self.destination))
        self.assertIn(str(conflicting), str(refused.exception))

    def test_modified_owned_file_is_replaced_and_the_receipt_updated(self):
        self.install()
        launcher = self.destination / "usr/local/bin/openxlr-yabridgectl"
        launcher.write_text("edited\n")
        receipt_path = self.destination / "usr/local/share/openxlr/installations/openxlr-yabridge.json"
        self.install()
        self.assertNotEqual("edited\n", launcher.read_text())
        self.assertEqual(0o755, launcher.stat().st_mode & 0o777)
        entry, = [entry for entry in json.loads(receipt_path.read_text())["files"] if entry["path"] == "/usr/local/bin/openxlr-yabridgectl"]
        self.assertEqual(hashlib.sha256(launcher.read_bytes()).hexdigest(), entry["sha256"])

    def test_install_made_before_receipts_is_adopted_and_upgraded(self):
        root = self.install_before_receipts()
        previous = {"/" + str(path.relative_to(self.destination)) for path in self.destination.rglob("*") if path.is_file()}
        self.install()
        receipt = json.loads((root / "share/openxlr/installations/openxlr-yabridge.json").read_text())
        self.assertEqual("manual", receipt["manager"])
        paths = {entry["path"] for entry in receipt["files"] if entry["kind"] != "receipt"}
        self.assertLessEqual(previous, paths)
        # The old stage shipped only the first local patch; the upgrade adds the second.
        self.assertEqual({"/usr/local/share/licenses/openxlr-yabridge/disk-identity.patch"}, paths - previous)
        self.assertEqual(b"test binary", (root / "lib/openxlr/yabridge/libyabridge-vst3.so").read_bytes())
        self.assertEqual(b"controller", (root / "lib/openxlr/yabridge/yabridgectl").read_bytes())
        version = json.loads((root / "lib/openxlr/yabridge/openxlr-yabridge.json").read_text())["version"]
        self.assertEqual(f"{bridge.PIN['version']}-{bridge.PIN['revision']}", version)
        for entry in receipt["files"]:
            if entry["kind"] == "file":
                self.assertEqual(entry["sha256"], hashlib.sha256((self.destination / entry["path"].lstrip("/")).read_bytes()).hexdigest())

    def test_tree_without_our_bridge_receipt_is_refused_listing_every_conflict(self):
        root = self.install_before_receipts()
        bridge_receipt = root / "lib/openxlr/yabridge/openxlr-yabridge.json"
        for foreign in ("not json\n", json.dumps({"formatVersion": 1, "version": "5.1.1.54-1"}) + "\n",
                        json.dumps({"formatVersion": 2, "version": "1", "sourceCommit": COMMIT, "windowsArchitectures": []}) + "\n"):
            bridge_receipt.write_text(foreign)
            before = snapshot(self.destination)
            with self.assertRaises(RuntimeError) as refused:
                self.install()
            self.assertEqual(before, snapshot(self.destination))
            for name in bridge.FILES + ["yabridgectl", "openxlr-yabridge.json"]:
                self.assertIn(str(root / "lib/openxlr/yabridge" / name), str(refused.exception))
            self.assertIn(str(root / "bin/openxlr-yabridgectl"), str(refused.exception))
        self.assertFalse((root / "share/openxlr").exists())

    def test_a_regular_parent_file_is_refused_before_any_live_writes(self):
        self.stage()
        blocked = self.destination / "usr/local/share"
        blocked.parent.mkdir(parents=True)
        blocked.write_text("foreign file")
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual("foreign file", blocked.read_text())
        self.assertFalse((self.destination / "usr/local/bin/openxlr-yabridgectl").exists())
        self.assertFalse((self.destination / "usr/local/lib/openxlr/yabridge").exists())

    def test_redirected_parent_is_never_followed(self):
        self.stage()
        outside = self.root / "outside"
        outside.mkdir()
        (self.destination / "usr").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(RuntimeError):
            bridge.install_staged(self.stage_root, "/usr/local", self.destination)
        self.assertEqual([], list(outside.iterdir()))

    def test_rpm_retains_stripping_and_inventories_after_post_processing(self):
        for name in ("source.spec.in", "openxlr-yabridge.spec.in"):
            recipe = (HERE / name).read_text()
            self.assertNotIn("__strip /usr/bin/true", recipe)
            self.assertIn("%global __os_install_post %{__os_install_post}", recipe)
            self.assertIn("--manager rpm", recipe)


if __name__ == "__main__":
    unittest.main()
