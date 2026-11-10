#!/usr/bin/env python3
"""Exercise CI test selection against real Git changes."""
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().with_name("ci-test-scope.sh")


class CiTestScopeTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "diff.renames", "true")
        self.git("config", "core.hooksPath", "/dev/null")
        self.git("commit", "--allow-empty", "-qm", "Initial commit")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def add(self, path):
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text("fixture\n")
        self.git("add", path)

    def scope(self, event="pull_request", before=""):
        output = subprocess.check_output([SCRIPT, event, before], cwd=self.root, text=True)
        return dict(line.split("=") for line in output.splitlines())

    def assert_scope(self, actual, *, code="false", unit="false", omarchy="false"):
        self.assertEqual({"code": code, "unit": unit, "omarchy": omarchy}, actual)

    def test_prose_images_and_deck_scripts_only_need_checks(self):
        for path in ("README.md", "docs/screenshot.png", "plugin/tests/example.mjs",
                     "plugin/com.emaspa.openxlr.sdPlugin/plugin.mjs"):
            self.add(path)
        self.git("commit", "-qm", "Edit prose, pictures and Deck scripts")
        self.assert_scope(self.scope())

    def test_docs_read_by_unit_tests(self):
        for path in ("skins.md", "manual.md", "install-from-source.md",
                     "localization.md", "examples/skins/README.md"):
            with self.subTest(path=path):
                self.add(f"docs/{path}")
                self.git("commit", "-qm", f"Edit {path}")
                self.assert_scope(self.scope(), unit="true")

    def test_rename_out_of_code_checks_the_removed_path_on_prs_and_pushes(self):
        self.add("src/control.cs")
        self.git("commit", "-qm", "Add source")
        before = self.git("rev-parse", "HEAD")
        (self.root / "docs").mkdir()
        self.git("mv", "src/control.cs", "docs/example.md")
        self.git("commit", "-qm", "Rename source to example")
        self.assertEqual("docs/example.md", self.git("diff", "--name-only", before, "HEAD"))
        self.assert_scope(self.scope(), code="true", unit="true")
        self.assert_scope(self.scope("push", before), code="true", unit="true")

    def test_renaming_tested_docs_or_omarchy_inputs_keeps_their_jobs(self):
        self.add("docs/skins.md")
        self.add("packaging/omarchy/README.md")
        self.git("commit", "-qm", "Add inputs")
        self.git("mv", "docs/skins.md", "docs/old-skins.md")
        self.git("mv", "packaging/omarchy/README.md", "docs/old-omarchy.md")
        self.git("commit", "-qm", "Rename inputs")
        self.assert_scope(self.scope(), unit="true", omarchy="true")

    def test_manual_run_missing_base_and_empty_diff_run_everything(self):
        before = self.git("rev-parse", "HEAD")
        self.add("README.md")
        self.git("commit", "-qm", "Edit README")
        for event, base in (("workflow_dispatch", ""), ("push", "0" * 40),
                            ("push", self.git("rev-parse", "HEAD"))):
            with self.subTest(event=event, base=base):
                self.assert_scope(self.scope(event, base), code="true", unit="true", omarchy="true")
        self.assert_scope(self.scope("push", before))


if __name__ == "__main__":
    unittest.main()
