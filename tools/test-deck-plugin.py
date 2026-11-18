#!/usr/bin/env python3
"""The OpenDeck plugin zip a release carries, built into a temporary directory."""
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("deck_plugin", REPO / "tools/deck-plugin.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class DeckPluginTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="openxlr-deck-test-")
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def copy(self):
        source = self.root / "source" / package.FOLDER
        shutil.copytree(package.SOURCE, source)
        return source

    def test_the_zip_holds_the_plugin_folder_unchanged(self):
        path = package.build(self.root / "out")
        self.assertEqual("com.emaspa.openxlr.sdPlugin.zip", path.name)
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            self.assertEqual([f"{package.FOLDER}/{n}" for n in package.source_files()], names)
            for name in ("manifest.json", "plugin.mjs", "propertyInspector/dial.html", "icons/plugin.png"):
                self.assertEqual((package.SOURCE / name).read_bytes(), archive.read(f"{package.FOLDER}/{name}"))
            for info in archive.infolist():
                self.assertEqual(package.STAMP, info.date_time)
                self.assertEqual(0o100644, info.external_attr >> 16)
        self.assertEqual([], package.validate(path))

    def test_two_builds_are_identical(self):
        first = package.build(self.root / "a").read_bytes()
        self.assertEqual(first, package.build(self.root / "b").read_bytes())

    def test_hidden_files_stay_out(self):
        source = self.copy()
        (source / ".eslintrc").write_text("{}\n")
        (source / "icons" / ".cache").mkdir()
        (source / "icons" / ".cache" / "x.png").write_bytes(b"x")
        self.assertEqual(package.source_files(), package.source_files(source))

    def test_a_file_the_manifest_names_must_be_in_the_zip(self):
        source = self.copy()
        manifest = json.loads((source / "manifest.json").read_text())
        (source / manifest["CodePath"]).unlink()
        (source / manifest["Actions"][0]["PropertyInspectorPath"]).unlink()
        (source / "icons" / "toggle.png").unlink()
        problems = package.validate(package.build(self.root / "out", source))
        self.assertIn(f"the manifest names {manifest['CodePath']}, which is not in the zip", problems)
        self.assertIn(f"the manifest names {manifest['Actions'][0]['PropertyInspectorPath']}, which is not in the zip", problems)
        self.assertIn("the manifest names icon icons/toggle, which is not in the zip", problems)

    def test_a_zip_without_its_folder_or_with_times_is_refused(self):
        path = self.root / "loose.zip"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(zipfile.ZipInfo("manifest.json", (2026, 11, 18, 12, 0, 0)), "{}")
        problems = package.validate(path)
        self.assertIn("an entry carries a build time", problems)
        self.assertIn(f"every entry must sit under {package.FOLDER}/", problems)
        self.assertIn("manifest.json is missing", problems)

    def test_check_passes_on_the_tree(self):
        self.assertEqual(0, package.check())


if __name__ == "__main__":
    unittest.main()
