#!/usr/bin/env python3
"""Attaching release assets to a draft, against an in-memory stand-in for the GitHub API."""
import contextlib
import hashlib
import importlib.util
import io
import os
from pathlib import Path
import re
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("release_attach", REPO / "tools/release-attach.py")
attach = importlib.util.module_from_spec(spec)
spec.loader.exec_module(attach)

API = attach.API
NAME = "emaspa/openxlr"


class FakeGitHub:
    """The release endpoints the script calls, kept in a dictionary."""

    def __init__(self):
        self.releases = {}
        self.next_id = 100
        self.calls = []
        self.on_create = None
        self.corrupt = False

    def add(self, tag, draft=True, assets=()):
        release = {"id": self.next_id, "tag_name": tag, "draft": draft, "assets": [],
                   "upload_url": f"https://uploads.example/releases/{self.next_id}/assets{{?name,label}}"}
        self.next_id += 1
        for name, data in assets:
            self.store(release, name, data)
        self.releases[release["id"]] = release
        return release

    def store(self, release, name, data):
        digest = hashlib.sha256(b"other" if self.corrupt else data).hexdigest()
        release["assets"].append({"id": self.next_id, "name": name, "size": len(data), "digest": f"sha256:{digest}"})
        self.next_id += 1

    def __call__(self, method, url, body=None, content_type="application/json"):
        self.calls.append((method, url))
        listing = re.fullmatch(rf"{API}/repos/{NAME}/releases\?per_page=100&page=(\d+)", url)
        if method == "GET" and listing:
            ordered = sorted(self.releases.values(), key=lambda r: -r["id"])
            page = int(listing.group(1))
            return [dict(r) for r in ordered[(page - 1) * 100:page * 100]]
        if method == "POST" and url == f"{API}/repos/{NAME}/releases":
            if self.on_create:
                self.on_create()
            return self.add(body["tag_name"], body["draft"]) | {"name": body["name"]}
        one = re.fullmatch(rf"{API}/repos/{NAME}/releases/(\d+)", url)
        if one and method == "GET":
            return self.releases[int(one.group(1))]
        if one and method == "DELETE":
            del self.releases[int(one.group(1))]
            return None
        asset = re.fullmatch(rf"{API}/repos/{NAME}/releases/assets/(\d+)", url)
        if asset and method == "DELETE":
            for release in self.releases.values():
                release["assets"] = [a for a in release["assets"] if a["id"] != int(asset.group(1))]
            return None
        upload = re.fullmatch(r"https://uploads\.example/releases/(\d+)/assets\?name=(.+)", url)
        if upload and method == "POST":
            release = self.releases[int(upload.group(1))]
            if any(a["name"] == upload.group(2) for a in release["assets"]):
                raise attach.ReleaseError("already_exists")
            self.store(release, upload.group(2), body)
            return {}
        raise AssertionError(f"unexpected call {method} {url}")


class ReleaseAttachTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix="openxlr-attach-test-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.github = FakeGitHub()
        patcher = mock.patch.object(attach, "api", self.github)
        patcher.start()
        self.addCleanup(patcher.stop)
        environment = mock.patch.dict(os.environ, {"GH_TOKEN": "token", "GH_REPO": NAME})
        environment.start()
        self.addCleanup(environment.stop)

    def file(self, name, data=None):
        path = self.root / name
        path.write_bytes(data if data is not None else name.encode())
        return path

    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = attach.main([str(a) for a in args])
        return code, out.getvalue(), err.getvalue()

    def names(self, release):
        return sorted(a["name"] for a in release["assets"])

    def test_the_first_job_creates_the_draft(self):
        self.github.add("v0.9.0", draft=False)
        deb, sums = self.file("openxlr_0.9.2_amd64.deb"), self.file("SHA256SUMS-deb.txt")
        code, out, err = self.run_main("v0.9.2", deb, sums)
        self.assertEqual(0, code, err)
        drafts = [r for r in self.github.releases.values() if r["tag_name"] == "v0.9.2"]
        self.assertEqual(1, len(drafts))
        self.assertTrue(drafts[0]["draft"])
        self.assertEqual(["SHA256SUMS-deb.txt", "openxlr_0.9.2_amd64.deb"], self.names(drafts[0]))
        self.assertIn("created draft", out)

    def test_a_later_job_uses_the_existing_draft(self):
        draft = self.github.add("v0.9.2", assets=[("openxlr_0.9.2_amd64.deb", b"deb")])
        code, _, err = self.run_main("v0.9.2", self.file("openxlr-0.9.2-1.fc44.x86_64.rpm"))
        self.assertEqual(0, code, err)
        self.assertEqual([draft["id"]], [r["id"] for r in self.github.releases.values() if r["tag_name"] == "v0.9.2"])
        self.assertEqual(["openxlr-0.9.2-1.fc44.x86_64.rpm", "openxlr_0.9.2_amd64.deb"], self.names(draft))
        self.assertNotIn(("POST", f"{API}/repos/{NAME}/releases"), self.github.calls)

    def test_a_published_release_is_refused(self):
        published = self.github.add("v0.9.2", draft=False)
        code, _, err = self.run_main("v0.9.2", self.file("openxlr_0.9.2_amd64.deb"))
        self.assertEqual(1, code)
        self.assertIn("already published", err)
        self.assertEqual([], published["assets"])

    def test_two_jobs_creating_at_once_end_on_one_draft(self):
        # Another job's draft lands between this job's listing and its own create.
        self.github.on_create = lambda: (setattr(self.github, "on_create", None), self.github.add("v0.9.2"))
        code, out, err = self.run_main("v0.9.2", self.file("openxlr_0.9.2_amd64.deb"))
        self.assertEqual(0, code, err)
        drafts = [r for r in self.github.releases.values() if r["tag_name"] == "v0.9.2"]
        self.assertEqual(1, len(drafts))
        self.assertEqual(["openxlr_0.9.2_amd64.deb"], self.names(drafts[0]))
        self.assertIn("is older", out)

    def test_a_rerun_replaces_an_asset_of_the_same_name(self):
        draft = self.github.add("v0.9.2", assets=[("SHA256SUMS-deb.txt", b"old")])
        code, _, err = self.run_main("v0.9.2", self.file("SHA256SUMS-deb.txt", b"new sums"))
        self.assertEqual(0, code, err)
        self.assertEqual([len(b"new sums")], [a["size"] for a in draft["assets"]])

    def test_only_the_deck_plugin_may_be_a_zip(self):
        for name in ("openxlr-browser-0.9.2.zip", "other.streamdeckplugin"):
            with self.subTest(name=name):
                code, _, err = self.run_main("v0.9.2", self.file(name))
                self.assertEqual(1, code)
                self.assertIn("only one a release carries", err)
        self.assertEqual([], self.github.calls)
        code, _, err = self.run_main("v0.9.2", self.file("com.emaspa.openxlr.sdPlugin.zip"))
        self.assertEqual(0, code, err)

    def test_a_draft_that_already_holds_another_zip_fails(self):
        self.github.add("v0.9.2", assets=[("stray.zip", b"zip")])
        code, _, err = self.run_main("v0.9.2", self.file("openxlr-browser-0.9.2.tar.gz"))
        self.assertEqual(1, code)
        self.assertIn("stray.zip beside the deck plugin", err)

    def test_a_digest_that_differs_after_the_upload_fails(self):
        self.github.corrupt = True
        code, _, err = self.run_main("v0.9.2", self.file("openxlr_0.9.2_amd64.deb"))
        self.assertEqual(1, code)
        self.assertIn("has digest", err)

    def test_bad_arguments_make_no_call(self):
        deb = self.file("openxlr_0.9.2_amd64.deb")
        for args in (("main", deb), ("v0.9", deb), ("v0.9.2", self.root / "missing.deb"),
                     ("v0.9.2", self.file("bad name.deb"))):
            with self.subTest(args=args):
                self.assertEqual(1, self.run_main(*args)[0])
        with mock.patch.dict(os.environ, {"GH_REPO": ""}):
            self.assertEqual(1, self.run_main("v0.9.2", deb)[0])
        self.assertEqual([], self.github.calls)


if __name__ == "__main__":
    unittest.main()
