#!/usr/bin/env python3
"""Check which caches the Debian build and clean targets use."""

import os
from pathlib import Path
import shlex
import subprocess
from tempfile import TemporaryDirectory
import unittest


RULES = Path(__file__).resolve().parents[1] / "debian/rules"


class DebianBuildCacheTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.home = root / "home"
        self.home.mkdir()
        self.source = root / "source"
        self.source.mkdir()
        self.package_home = self.source / "debian/.home"
        dotnet = root / "dotnet"
        dotnet.write_text(
            '#!/bin/sh\nprintf "%s\\n" "$HOME" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" '
            '"$CARGO_HOME" "$OPENXLR_BUILD_CACHE"\n'
        )
        dotnet.chmod(0o700)
        self.env = dict(os.environ, PATH=f"{root}:{os.environ['PATH']}",
                        HOME=str(self.home), DOTNET_CLI_HOME=str(self.home / "dotnet"),
                        NUGET_PACKAGES=str(self.home / "nuget"), CARGO_HOME=str(self.home / "cargo"))
        for variable in ("CI", "MAKEFLAGS", "OPENXLR_BUILD_CACHE"):
            self.env.pop(variable, None)

    def run_make(self, *arguments):
        return subprocess.check_output(
            ["make", "--no-print-directory", "-s", "-f", str(RULES), *arguments],
            cwd=self.source, env=self.env, text=True,
        )

    def assert_private_caches(self):
        expected = [self.package_home, self.package_home, self.package_home / ".nuget/packages",
                    self.package_home / ".cargo", "0"]
        self.assertEqual(self.run_make("override_dh_auto_build").splitlines(),
                         list(map(str, expected)) * 3)

    def test_local_build_preserves_caches_and_clean_only_removes_checkout_outputs(self):
        expected = [self.env[key] for key in ("HOME", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "CARGO_HOME")]
        self.assertEqual(self.run_make("override_dh_auto_build").splitlines(), (expected + [""]) * 3)
        clean_arguments = shlex.split(self.run_make("-n", "override_dh_auto_clean"))
        self.assertIn(str(self.package_home), clean_arguments)
        self.assertTrue(set(expected).isdisjoint(clean_arguments))

    def test_vendored_dependencies_use_private_caches(self):
        for path in ("vendor/nuget", "native/deepfilter/vendor"):
            with self.subTest(path=path):
                vendor = self.source / path
                vendor.mkdir(parents=True)
                self.assert_private_caches()
                vendor.rmdir()

    def test_ci_uses_private_caches(self):
        self.env["CI"] = "true"
        self.assert_private_caches()

    def test_missing_home_uses_private_caches(self):
        self.home.rmdir()
        self.assert_private_caches()


if __name__ == "__main__":
    unittest.main()
