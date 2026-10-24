#!/usr/bin/env python3
"""The browser companion's Store zip and release tar.gz, built into a temporary directory."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import zipfile

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("browser_extension", REPO / "tools/browser-extension.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class BrowserExtensionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="openxlr-browser-test-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def built(self, **kwargs):
        path = package.build(self.root / "out", **kwargs)
        with zipfile.ZipFile(path) as archive:
            return path, {name: archive.read(name) for name in archive.namelist()}

    def test_the_package_is_the_extension_without_its_key(self):
        path, files = self.built()
        source = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        manifest = json.loads(files["manifest.json"])
        self.assertEqual(f"openxlr-browser-{source['version']}.zip", path.name)
        self.assertNotIn("key", manifest)
        del source["key"]
        self.assertEqual(source, manifest)
        self.assertEqual(sorted(package.source_files()), sorted(files))
        self.assertNotIn("key.pem", files)
        for name in ("worker.mjs", "routing.mjs", "popup.html", "offscreen.html", "devices.html", "icons/icon-48.png"):
            self.assertEqual((REPO / "browser/chromium" / name).read_bytes(), files[name])
        self.assertEqual([], package.validate(path))

    def test_two_builds_are_identical(self):
        first = package.build(self.root / "a").read_bytes()
        self.assertEqual(first, package.build(self.root / "b").read_bytes())

    def test_the_manifest_key_pins_the_id_the_native_host_allows(self):
        self.assertEqual(package.EXTENSION_ID, package.extension_id(package.manifest_key()))
        host = json.loads((REPO / "browser/native-host.json").read_text())
        self.assertEqual([f"chrome-extension://{package.EXTENSION_ID}/"], host["allowed_origins"])

    def test_a_matching_private_key_is_added_at_the_root(self):
        pem = self.root / "key.pem"
        pem.write_text("stand-in private key\n")
        with mock.patch.object(package, "public_der", return_value=package.manifest_key()):
            path, files = self.built(key_pem=pem)
        self.assertEqual(pem.read_bytes(), files["key.pem"])
        self.assertEqual([], package.validate(path, key_pem=True))
        self.assertIn("key.pem is present", package.validate(path))

    def test_another_private_key_is_refused(self):
        pem = self.root / "other.pem"
        pem.write_text("stand-in private key\n")
        with mock.patch.object(package, "public_der", return_value=b"another key"), \
                self.assertRaises(SystemExit) as refused:
            package.build(self.root / "out", key_pem=pem)
        self.assertIn(package.EXTENSION_ID, str(refused.exception))
        self.assertFalse((self.root / "out").exists())

    def test_tests_and_hidden_files_stay_out(self):
        source = self.root / "source"
        (source / "tests").mkdir(parents=True)
        for name in ("manifest.json", "worker.mjs", "tests/a.mjs", "b.test.mjs", ".hidden"):
            (source / name).write_text("{}")
        self.assertEqual(["manifest.json", "worker.mjs"], package.source_files(source))

    def test_validation_names_a_missing_reference_a_key_and_a_wrong_icon(self):
        broken = self.root / "broken.zip"
        manifest = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        with zipfile.ZipFile(broken, "w") as archive:
            archive.writestr(zipfile.ZipInfo("icons/icon-128.png", package.STAMP), (REPO / "browser/chromium/icons/icon-16.png").read_bytes())
            archive.writestr(zipfile.ZipInfo("manifest.json", package.STAMP), json.dumps(manifest))
            archive.writestr(zipfile.ZipInfo("popup.html", package.STAMP), '<script type="module" src="popup.mjs"></script>')
        problems = package.validate(broken)
        self.assertIn("the manifest still carries a key, which the Store refuses", problems)
        self.assertIn("icons/icon-128.png is not a 128 px PNG", problems)
        self.assertIn("popup.mjs is referenced but not in the zip", problems)
        self.assertIn("worker.mjs is referenced but not in the zip", problems)
        self.assertIn("icons 16 names icons/icon-16.png, which is not in the zip", problems)

    def test_the_references_cover_imports_pages_and_offscreen_documents(self):
        worker = (REPO / "browser/chromium/worker.mjs").read_text()
        popup = (REPO / "browser/chromium/popup.mjs").read_text()
        self.assertTrue({"routing.mjs", "offscreen.html"} <= package.references("worker.mjs", worker))
        self.assertTrue({"routing.mjs", "devices.html"} <= package.references("popup.mjs", popup))
        self.assertEqual({"style.css", "popup.mjs"},
                         package.references("popup.html", (REPO / "browser/chromium/popup.html").read_text()))

    def unpacked(self):
        path = package.build_unpacked(self.root / "out")
        with tarfile.open(path) as archive:
            members = archive.getmembers()
            files = {m.name: archive.extractfile(m).read() for m in members if m.isfile()}
        return path, members, files

    def test_the_release_archive_is_the_extension_with_its_key_under_one_directory(self):
        path, members, files = self.unpacked()
        source = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        top = f"openxlr-browser-{source['version']}"
        self.assertEqual(f"{top}.tar.gz", path.name)
        self.assertEqual({top}, {m.name.split("/")[0] for m in members})
        self.assertEqual(sorted(f"{top}/{n}" for n in package.source_files()), sorted(files))
        for name in package.source_files():
            self.assertEqual((REPO / "browser/chromium" / name).read_bytes(), files[f"{top}/{name}"])
        manifest = json.loads(files[f"{top}/manifest.json"])
        self.assertEqual(package.EXTENSION_ID, package.extension_id(package.base64.b64decode(manifest["key"])))
        self.assertTrue(all(m.uid == 0 and m.gid == 0 and m.mtime == package.STAMP_SECONDS for m in members))
        self.assertEqual([], package.validate_unpacked(path))

    def test_two_release_archives_are_identical(self):
        first = package.build_unpacked(self.root / "a").read_bytes()
        self.assertEqual(first, package.build_unpacked(self.root / "b").read_bytes())

    def test_release_archive_validation_names_a_missing_key_and_a_second_directory(self):
        manifest = json.loads((REPO / "browser/chromium/manifest.json").read_text())
        del manifest["key"]
        top = f"openxlr-browser-{manifest['version']}"

        def write(path, names):
            with tarfile.open(path, "w:gz") as archive:
                for name, data in names.items():
                    info = tarfile.TarInfo(name)
                    info.size, info.mtime = len(data), package.STAMP_SECONDS
                    archive.addfile(info, io.BytesIO(data))
            return path

        keyless = write(self.root / "keyless.tar.gz", {f"{top}/manifest.json": json.dumps(manifest).encode()})
        problems = package.validate_unpacked(keyless)
        self.assertIn(f"the manifest key must give id {package.EXTENSION_ID}, the one the native host allows", problems)
        self.assertIn("worker.mjs is referenced but not in the zip", problems)
        loose = write(self.root / "loose.tar.gz", {f"{top}/manifest.json": b"{}", "manifest.json": b"{}"})
        self.assertEqual([f"the files must sit under one directory, not manifest.json, {top}"], package.validate_unpacked(loose))

    def test_the_release_archive_never_takes_the_private_key(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as refused:
            package.main([str(self.root / "out"), "--unpacked", "--key-pem", str(self.root / "key.pem")])
        self.assertEqual(2, refused.exception.code)
        self.assertFalse((self.root / "out").exists())

    def test_check_passes_on_the_tree(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, package.check())
        self.assertIn("ok", output.getvalue())


if __name__ == "__main__":
    unittest.main()
