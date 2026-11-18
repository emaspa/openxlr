# Release assets

Each [GitHub release](https://github.com/emaspa/openxlr/releases) carries
the packages and add-ons below, most of them built and attested by a
workflow of this repository.

## What a release carries

| Asset | Checksum file | Built and attested by |
|---|---|---|
| `openxlr_<version>_amd64.deb` | `SHA256SUMS-deb.txt` | `release-deb.yml` |
| `openxlr-<version>-*.x86_64.rpm` | `SHA256SUMS-rpm.txt` | `release-rpm.yml` |
| `com.emaspa.openxlr.sdPlugin.zip` | `SHA256SUMS-plugin.txt` | `release-addons.yml` |
| `openxlr-browser-<version>.tar.gz` | `SHA256SUMS-browser.txt` | `release-addons.yml` |
| `openxlr-<version>-x86_64.flatpak` | `SHA256SUMS-flatpak.txt` | None, built and uploaded by hand |
| `openxlr-yabridge` packages and source | `SHA256SUMS-yabridge.txt` | None, built by the [Optional Windows bridge workflow](https://github.com/emaspa/openxlr/actions/workflows/yabridge.yml) and uploaded by hand |

Releases up to 0.9.1 carry the deck plugin zip and the browser companion
without a checksum file or an attestation. They were made by hand.

The deck plugin is the only zip on a release. OpenDeck offers every zip
on the latest release as a plugin, which is why the browser companion is a
tar.gz.

## Checking a download

### Which workflow built a file

An attested asset carries a build provenance attestation, signed for the
workflow run that built it. Check it with the
[GitHub CLI](https://cli.github.com/), naming the workflow from the table
and the release's tag:

```sh
gh attestation verify openxlr_<version>_amd64.deb \
    --repo emaspa/openxlr \
    --signer-workflow emaspa/openxlr/.github/workflows/release-deb.yml \
    --source-ref refs/tags/v<version> \
    --deny-self-hosted-runners
```

Use `release-rpm.yml` for the `.rpm` and `release-addons.yml` for the deck
plugin zip and the browser companion. The command exits 0 only when an
attestation for that exact file was made by that workflow, running for
that tag on a GitHub-hosted runner. Only the maintainer can push a tag.

Pass all three of `--repo`, `--signer-workflow` and `--source-ref`.
`--owner emaspa` alone accepts an attestation made by any workflow, on any
branch, in any repository of that account, so a workflow pushed to a
branch could attest a file of its own.

### A damaged download

```sh
sha256sum -c --ignore-missing SHA256SUMS-deb.txt
```

A checksum file sits on the same release as the file it lists, so it only
shows that the download is complete and undamaged. It does not show who
made the file, since anyone who could replace the asset could replace the
checksum file too. For the Flatpak bundle and the `openxlr-yabridge`
packages, which have no attestation, the checksum is the only check a
release offers.

### Rebuilding the add-ons from the tag

The deck plugin zip and the browser companion are reproducible. The same
tree gives the same bytes. For a release that `release-addons.yml` built,
a checkout of its tag rebuilds both, and their SHA-256 matches the
release's checksum files:

```sh
git checkout v<version>
python3 -I tools/deck-plugin.py out
python3 -I tools/browser-extension.py out --unpacked
sha256sum out/*
```

## How a release is put together

The release workflows start when the maintainer pushes a version tag, and
attach their files to a draft release for that tag. A release is published
only once every asset is on it, so nothing has to be added afterwards. The
maintainer's steps for the GitHub release:

1. Bump the version in every file `tools/check-version.sh` reads, commit,
   and wait for CI to pass on that commit.
2. Create the signed tag `v<version>` and push it. The `.deb`, `.rpm` and
   add-ons workflows start; each build job checks that the tag names the
   version in every file.
3. The first publish job to finish creates the draft release
   `OpenXLR <version>`; the others attach to it. Fill in the notes with
   `gh release edit v<version> --notes-file <file>`. Do not create a second
   release for the tag.
4. Attach the hand-built assets to the draft: the Flatpak bundle with
   `SHA256SUMS-flatpak.txt`, and the `openxlr-yabridge` files with
   `SHA256SUMS-yabridge.txt`.
5. When the three workflows have passed, check the draft's asset list, then
   verify one asset of each workflow with the commands above.
6. Publish the draft.

A publish job refuses a release that is already published, and refuses any
zip other than the deck plugin. A job run again replaces its own files on
the draft. `tools/release-attach.py` does this work.

A manual run of a release workflow builds and tests the assets and keeps
them as a workflow artifact; it attaches nothing and makes no attestation.
