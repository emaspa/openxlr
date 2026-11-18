#!/usr/bin/env python3
"""Attach files to the draft release of a tag.

    GH_TOKEN=... GH_REPO=owner/repo python3 -I tools/release-attach.py TAG FILE...

The publish jobs of the release workflows run this. Every asset goes on a
draft, so the release is complete before the maintainer publishes it, which
GitHub's immutable releases require: once published, no asset can be
added, replaced or removed.

When the tag has no release yet, the first job creates the draft. A draft
does not hold its tag name exclusively, so two jobs that start at the same
moment can each create one. Every job uses the oldest draft for the tag,
and a job whose own draft is not the oldest deletes it again. A published
release for the tag is refused, as is a zip other than the deck plugin,
since OpenDeck offers every zip on the latest release as a plugin. A file
whose name is already on the draft replaces it, so a job can be run again.
After the uploads the draft is read back and each file's size and SHA-256
must match. Standard library only.
"""
import argparse
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API = "https://api.github.com"
TAG = re.compile(r"^v\d+\.\d+\.\d+$")
ASSET = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]*$")
PLUGIN_ZIP = "com.emaspa.openxlr.sdPlugin.zip"
PLUGIN_SUFFIXES = (".zip", ".streamdeckplugin")


class ReleaseError(Exception):
    pass


def api(method, url, body=None, content_type="application/json"):
    """One GitHub REST call; returns the decoded JSON, or None when empty."""
    data = body if isinstance(body, bytes) or body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", f"Bearer {os.environ['GH_TOKEN']}")
    request.add_header("Accept", "application/vnd.github+json")
    request.add_header("X-GitHub-Api-Version", "2022-11-28")
    request.add_header("User-Agent", "openxlr-release")
    if data is not None:
        request.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            payload = response.read()
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:500]
        raise ReleaseError(f"{method} {url} answered {error.code}: {detail}") from None
    return json.loads(payload) if payload else None


def releases_for(repo, tag):
    """Every release of the repository whose tag is tag, drafts included."""
    found = []
    page = 1
    while True:
        batch = api("GET", f"{API}/repos/{repo}/releases?per_page=100&page={page}")
        if not batch:
            return found
        found += [r for r in batch if r.get("tag_name") == tag]
        page += 1


def refuse_published(releases, tag):
    if any(not r.get("draft") for r in releases):
        raise ReleaseError(
            f"the release for {tag} is already published; assets are attached to a draft "
            "before it is published, so cut a new version instead")


def draft_for(repo, tag, log=print):
    """The draft release the assets of tag go on, created when there is none."""
    found = releases_for(repo, tag)
    refuse_published(found, tag)
    own = None
    if not found:
        own = api("POST", f"{API}/repos/{repo}/releases", {
            "tag_name": tag, "name": f"OpenXLR {tag[1:]}", "body": "", "draft": True})
        log(f"created draft {own['id']} for {tag}")
        found = releases_for(repo, tag)
        refuse_published(found, tag)
        if own["id"] not in {r["id"] for r in found}:
            found.append(own)
    drafts = sorted(found, key=lambda r: r["id"])
    chosen = drafts[0]
    if own is not None and own["id"] != chosen["id"]:
        api("DELETE", f"{API}/repos/{repo}/releases/{own['id']}")
        log(f"deleted draft {own['id']}: draft {chosen['id']} for {tag} is older")
        drafts = [d for d in drafts if d["id"] != own["id"]]
    if len(drafts) > 1:
        others = ", ".join(str(d["id"]) for d in drafts[1:])
        log(f"warning: {tag} has more than one draft; using {chosen['id']}, not {others}")
    return chosen


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def check_names(paths):
    names = [p.name for p in paths]
    for name in names:
        if not ASSET.match(name):
            raise ReleaseError(f"{name} is not a plain asset name")
        if name.endswith(PLUGIN_SUFFIXES) and name != PLUGIN_ZIP:
            raise ReleaseError(f"{name}: OpenDeck offers every zip on a release as a deck plugin, "
                               f"so {PLUGIN_ZIP} is the only one a release carries")
    if len(set(names)) != len(names):
        raise ReleaseError("two files share an asset name")


def attach(repo, release, paths, log=print):
    """Upload paths to release, replacing assets of the same name."""
    existing = {a["name"]: a for a in release.get("assets", [])}
    upload = release["upload_url"].split("{", 1)[0]
    for path in paths:
        if path.name in existing:
            api("DELETE", f"{API}/repos/{repo}/releases/assets/{existing[path.name]['id']}")
            log(f"replacing {path.name}")
        url = f"{upload}?name={urllib.parse.quote(path.name)}"
        api("POST", url, path.read_bytes(), "application/octet-stream")
        log(f"attached {path.name}")


def confirm(repo, release_id, paths):
    """Read the draft back: every file there, with its size and digest."""
    release = api("GET", f"{API}/repos/{repo}/releases/{release_id}")
    assets = {a["name"]: a for a in release.get("assets", [])}
    for path in paths:
        asset = assets.get(path.name)
        if asset is None:
            raise ReleaseError(f"{path.name} is not on draft {release_id} after the upload")
        if asset.get("size") != path.stat().st_size:
            raise ReleaseError(f"{path.name} on the draft has {asset.get('size')} bytes, not {path.stat().st_size}")
        digest = asset.get("digest")
        if digest and digest != f"sha256:{sha256(path)}":
            raise ReleaseError(f"{path.name} on the draft has digest {digest}, not sha256:{sha256(path)}")
    zips = sorted(n for n in assets if n.endswith(PLUGIN_SUFFIXES) and n != PLUGIN_ZIP)
    if zips:
        raise ReleaseError(f"draft {release_id} carries {', '.join(zips)} beside the deck plugin")
    if not release.get("draft"):
        raise ReleaseError(f"release {release_id} was published while its assets were attached")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("tag", help="the release tag, vX.Y.Z")
    parser.add_argument("files", nargs="+", type=Path, help="files to attach")
    args = parser.parse_args(argv)
    repo = os.environ.get("GH_REPO", "")
    try:
        if not TAG.match(args.tag):
            raise ReleaseError(f"{args.tag} is not a release tag of the form vX.Y.Z")
        if not re.match(r"^[\w.-]+/[\w.-]+$", repo):
            raise ReleaseError("GH_REPO must name the repository as owner/name")
        if not os.environ.get("GH_TOKEN"):
            raise ReleaseError("GH_TOKEN is not set")
        missing = [str(p) for p in args.files if not p.is_file()]
        if missing:
            raise ReleaseError(f"no such file: {', '.join(missing)}")
        check_names(args.files)
        release = draft_for(repo, args.tag)
        attach(repo, release, args.files)
        confirm(repo, release["id"], args.files)
    except ReleaseError as error:
        print(f"release-attach: {error}", file=sys.stderr)
        return 1
    print(f"release-attach: {len(args.files)} files on draft {release['id']} for {args.tag}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
