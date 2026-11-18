#!/usr/bin/env python3
"""Companion build directories, inventory, staging contract and local publication regressions."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
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
        # build.py refuses a source or output directory that others can write,
        # which a umask of 002 would otherwise give these.
        self.source.chmod(0o755)
        self.output.chmod(0o755)
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


class BuildDirectoryTests(unittest.TestCase):
    """Source and output directories another user could control are refused."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="openxlr-build-dirs-")
        self.root = Path(self.temporary.name)
        self.root.chmod(0o700)
        self.uid = os.getuid()

    def tearDown(self):
        for path in self.root.rglob("*"):
            if path.is_dir() and not path.is_symlink():
                path.chmod(0o700)
        self.temporary.cleanup()

    def refused(self, path, **kwargs):
        with self.assertRaises(RuntimeError) as refusal:
            bridge.build_directory(path, create=kwargs.pop("create", False), **kwargs)
        return str(refusal.exception)

    def test_group_or_other_writable_directory_is_refused(self):
        for mode in (0o770, 0o707, 0o777, 0o1777):
            output = self.root / f"out-{mode:o}"
            output.mkdir()
            output.chmod(mode)
            message = self.refused(output)
            self.assertIn(str(output), message)
            self.assertIn("chmod go-w", message)

    def test_symbolic_link_is_refused_even_to_an_owned_directory(self):
        real = self.root / "real"
        real.mkdir(mode=0o700)
        link = self.root / "link"
        link.symlink_to(real, target_is_directory=True)
        self.assertIn(str(link), self.refused(link))
        self.assertIn(str(link), self.refused(link, create=True))

    def test_missing_directory_is_created_private(self):
        old_umask = os.umask(0o002)
        try:
            output = bridge.build_directory(self.root / "cache/openxlr-yabridge/build", create=True)
        finally:
            os.umask(old_umask)
        self.assertEqual(self.root / "cache/openxlr-yabridge/build", output)
        for path in (output, output.parent, output.parent.parent):
            self.assertEqual(0o700, path.stat().st_mode & 0o7777, path)
        self.assertEqual(output, bridge.build_directory(output, create=True))

    def test_missing_directory_is_left_alone_without_create(self):
        missing = self.root / "missing"
        self.assertEqual(missing, bridge.build_directory(missing, create=False))
        self.assertFalse(missing.exists())

    def test_owned_private_directory_is_accepted(self):
        for mode in (0o700, 0o755):
            output = self.root / f"out-{mode:o}"
            output.mkdir()
            output.chmod(mode)
            self.assertEqual(output, bridge.build_directory(output, create=False))
            self.assertEqual(mode, output.stat().st_mode & 0o7777)

    def owned_by(self, path, uid):
        """Make path look owned by uid, since a test cannot chown without root."""
        real_lstat = os.lstat
        def lstat(name, *args, **kwargs):
            result = real_lstat(name, *args, **kwargs)
            if Path(name) == path:
                fields = list(result)
                fields[4] = uid
                return os.stat_result(fields)
            return result
        return patch.object(bridge.os, "lstat", side_effect=lstat)

    def test_directory_owned_by_someone_else_is_refused(self):
        output = self.root / "out"
        output.mkdir(mode=0o700)
        with self.owned_by(output, self.uid + 1):
            message = self.refused(output)
            self.assertIn(str(output), self.refused(output, create=True))
        self.assertIn(str(output), message)
        self.assertIn(f"uid {self.uid + 1}", message)
        self.assertIn("Another user may have created it first", message)

    def test_parent_owned_by_someone_else_is_refused(self):
        output = self.root / "out"
        output.mkdir(mode=0o700)
        with self.owned_by(self.root, self.uid + 1):
            message = self.refused(output)
        self.assertIn(str(self.root), message)
        self.assertIn(f"uid {self.uid + 1}", message)
        self.assertIn("could replace", message)

    def test_package_build_checks_the_tree_and_not_its_parents(self):
        # sbuild builds as buildd in /build/<package>, and /build belongs to sbuild.
        output = self.root / "out"
        output.mkdir(mode=0o700)
        with self.owned_by(self.root, self.uid + 1):
            self.assertEqual(output, bridge.build_directory(output, create=False, parents=False))
            self.assertEqual(output / "new", bridge.build_directory(output / "new", create=True, parents=False))
            # The package's own directories are still checked.
            with self.owned_by(output, self.uid + 1):
                with self.assertRaises(RuntimeError):
                    bridge.build_directory(output, create=False, parents=False)
            output.chmod(0o777)
            with self.assertRaises(RuntimeError):
                bridge.build_directory(output, create=False, parents=False)
            output.chmod(0o700)

    def test_sudo_accepts_the_invoking_user_and_root_only(self):
        self.assertEqual({1000}, bridge.build_owners(1000, None))
        self.assertEqual({1000}, bridge.build_owners(1000, "0"))
        self.assertEqual({0}, bridge.build_owners(0, None))
        self.assertEqual({0}, bridge.build_owners(0, ""))
        self.assertEqual({0}, bridge.build_owners(0, "not a uid"))
        self.assertEqual({0, 1000}, bridge.build_owners(0, "1000"))
        output = self.root / "out"
        output.mkdir(mode=0o700)
        # Root through sudo from this user accepts this user's directory.
        self.assertEqual(output, bridge.build_directory(output, create=False, euid=0, sudo_uid=str(self.uid)))
        if self.uid != 0:
            # Root without sudo, or sudo from another user, does not.
            self.assertIn(f"uid {self.uid}", self.refused(output, euid=0, sudo_uid=""))
            self.assertIn(f"uid {self.uid}", self.refused(output, euid=0, sudo_uid=str(self.uid + 1)))

    def test_parent_any_user_can_write_needs_the_sticky_bit(self):
        shared = self.root / "shared"
        shared.mkdir()
        shared.chmod(0o777)
        message = self.refused(shared / "build", create=True)
        self.assertIn(str(shared), message)
        self.assertIn("sticky", message)
        self.assertEqual([], list(shared.iterdir()))
        # A private directory the user made inside a sticky one, as under /tmp.
        shared.chmod(0o1777)
        output = bridge.build_directory(shared / "build", create=True)
        self.assertEqual(0o700, output.stat().st_mode & 0o7777)

    def test_command_line_refuses_before_any_step_runs(self):
        source = self.root / "source"
        source.mkdir(mode=0o700)
        output = self.root / "build"
        output.mkdir()
        output.chmod(0o777)
        for command in ("build", "package", "stage", "source-archive"):
            arguments = [sys.executable, str(HERE / "build.py"), command, "--source", str(source), "--output", str(output),
                         "--destdir", str(self.root / "stage"), "--manager", "manual", "--prefix", "/usr/local", "--format", "deb"]
            result = subprocess.run(arguments, capture_output=True, text=True)
            self.assertEqual(1, result.returncode, command)
            self.assertIn("Refusing build directory", result.stderr)
            self.assertIn(str(output), result.stderr)
            self.assertNotIn("Traceback", result.stderr)
        self.assertEqual([], list(output.iterdir()))
        self.assertFalse((self.root / "stage").exists())
        source.chmod(0o775)
        result = subprocess.run([sys.executable, str(HERE / "build.py"), "prepare", "--source", str(source),
                                 "--output", str(self.root / "fresh")], capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn(str(source), result.stderr)
        self.assertEqual([], list(source.iterdir()))

    def test_prepare_clones_into_the_empty_directory_it_created(self):
        source = bridge.build_directory(self.root / "source", create=True)
        calls = []
        def run(arguments, **kwargs):
            calls.append([str(argument) for argument in arguments])
            raise subprocess.CalledProcessError(1, arguments)
        with patch.object(bridge, "run", side_effect=run), self.assertRaises(subprocess.CalledProcessError):
            bridge.prepare(source)
        self.assertEqual(["git", "init", str(source)], calls[0])


class HardeningTests(unittest.TestCase):
    """The plugin libraries are hardened; the Wine host keeps the packager's flags."""

    def test_plugin_libraries_get_hardening_after_the_packagers_flags(self):
        env = {"CFLAGS": "-O2 -g", "CXXFLAGS": "-O2 -g", "LDFLAGS": "-Wl,-z,notext",
               "CXXFLAGS_FOR_BUILD": "-O2 -flto=auto", "LDFLAGS_FOR_BUILD": "-flto=auto"}
        with patch.object(bridge, "fortify_flags", return_value=["-D_FORTIFY_SOURCE=3"]):
            bridge.harden_plugin_libraries(env)
        compile_flags = ["-fstack-protector-strong", "-fstack-clash-protection", "-fcf-protection", "-D_FORTIFY_SOURCE=3"]
        self.assertEqual(["-O2", "-flto=auto", *compile_flags], env["CXXFLAGS_FOR_BUILD"].split())
        self.assertEqual(compile_flags, env["CFLAGS_FOR_BUILD"].split())
        self.assertEqual(["-flto=auto", "-Wl,-z,relro", "-Wl,-z,now", "-Wl,-z,noexecstack"],
                         env["LDFLAGS_FOR_BUILD"].split())
        # Winelib output refuses the hardened link flags; the host is left alone.
        self.assertEqual(("-O2 -g", "-O2 -g", "-Wl,-z,notext"), (env["CFLAGS"], env["CXXFLAGS"], env["LDFLAGS"]))

    def test_fortify_is_defined_once(self):
        def compiler(defined, level3):
            def run(arguments, **kwargs):
                if "-dM" in arguments:
                    return subprocess.CompletedProcess(arguments, 0, "#define _FORTIFY_SOURCE 3\n" if defined else "", "")
                return subprocess.CompletedProcess(arguments, 0 if level3 else 1, "", "")
            return patch.object(bridge.subprocess, "run", side_effect=run)
        with compiler(defined=True, level3=True):
            self.assertEqual([], bridge.fortify_flags(["c++"], ["-Wp,-D_FORTIFY_SOURCE=3"]))
        with compiler(defined=False, level3=True):
            self.assertEqual(["-D_FORTIFY_SOURCE=3"], bridge.fortify_flags(["c++"], []))
        with compiler(defined=False, level3=False):
            self.assertEqual(["-D_FORTIFY_SOURCE=2"], bridge.fortify_flags(["c++"], []))

    @unittest.skipUnless(shutil.which("c++"), "needs a C++ compiler")
    def test_a_packagers_fortify_level_is_kept(self):
        self.assertEqual([], bridge.fortify_flags(["c++"], ["-D_FORTIFY_SOURCE=2"]))


if __name__ == "__main__":
    unittest.main()
