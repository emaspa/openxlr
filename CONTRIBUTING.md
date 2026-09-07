# Contributing to OpenXLR

Thanks for helping. This page says how the project works so a change
lands on the first try. Questions before you start are welcome in the
`#dev` channel of the [Discord server](https://discord.gg/4bswtnGPW4)
or in a GitHub issue.

## What helps most

- **Hardware reports.** Own a device we could not test ourselves? Run
  the controls, note what works and what does not, and open an issue
  with a diagnostics archive (Options, SUPPORT, Collect diagnostics in
  the window). The per-control table in
  [docs/hardware-support.md](docs/hardware-support.md) shows what is
  still unverified.
- **Bugs with a way to reproduce them.** The archive again, plus what
  you did and what you expected. The daemon's journal is
  `journalctl --user -u openxlr-daemon`.
- **Code.** The [roadmap](docs/roadmap.md) is the priority list. Right
  now mixer stability and the mixer's presentation (per-mix
  customization) come before plugin-host expansion; a pull request in
  that order of priorities is easier to take than one against it. Ask first for anything large, so the work
  is not done twice or built on a model that is about to change.

## Building and testing

[docs/install-from-source.md](docs/install-from-source.md) covers the
prerequisites and the build. The checks CI runs:

```sh
dotnet restore src/OpenXLR.slnx --locked-mode   # the committed lock files must match
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror
dotnet test src/OpenXLR.slnx -c Release --no-build
node --check plugin/com.emaspa.openxlr.sdPlugin/plugin.mjs
node --test plugin/tests/*.test.mjs
python3 -m json.tool plugin/com.emaspa.openxlr.sdPlugin/manifest.json >/dev/null
shellcheck --severity=error tools/check-version.sh tools/check-locked-restore.sh packaging/ppa/make-source.sh
tools/check-version.sh                          # the five version locations agree
tools/check-locked-restore.sh                   # every packaging path restores locked
tools/check-openapi.py docs/openapi-v1.json     # the HTTP API document keeps its shape
tools/check-spec.py packaging/rpm/openxlr.spec  # every installed file is in %files
make -C native  # the plugin host; needs a C and C++ compiler and the PipeWire, lilv, LV2 and X11 headers
```

If you add or change a NuGet package, regenerate the lock files with a
plain `dotnet restore src/OpenXLR.slnx` (the linux-x64 graph is part of
the projects' runtime identifiers, so one restore covers it)
and the Nix dependency list
(`nix build .#openxlr.passthru.fetch-deps -o /tmp/fd && /tmp/fd packaging/nix/deps.json`)
and commit both. Do not bump the version: releases do that in one
commit across all five files.

Real hardware is the final test. Say in the pull request what you ran
it on, or that you could not; the maintainer tests on a Wave XLR Pro
and both XLR Dock modules before merging.

## Pull requests

- **Branch from `main`** and keep one topic per pull request. Split an
  independent part out into its own request when you can; small ones
  merge fast, large ones wait for a review slot.
- **Sign your commits.** The repository refuses unsigned commits on
  every branch. SSH signing takes a minute: add your public key to
  GitHub as a signing key (Settings, SSH and GPG keys, "Signing key"),
  then

  ```sh
  git config --global gpg.format ssh
  git config --global user.signingkey ~/.ssh/<key>.pub
  git config --global commit.gpgsign true
  ```

  To sign commits you already made:
  `git rebase --exec 'git commit --amend --no-edit -S' main` and a
  `git push --force-with-lease`.
- **Tests and docs travel with the change.** A new command or state
  field goes into [docs/api.md](docs/api.md) (and
  [docs/http-api.md](docs/http-api.md) when the HTTP transport is
  affected, [docs/mixer-layout.md](docs/mixer-layout.md) for the layout
  file and its commands); user-facing behaviour into
  [docs/manual.md](docs/manual.md) and, when it is a feature,
  [docs/features.md](docs/features.md). Tests live in
  `src/OpenXLR.Tests` (xUnit) and `plugin/tests` (Node's test runner).
- **Commit messages describe the work**, in plain sentences: what
  changed and why, no ticket-style tags, no attribution trailers, and
  no references to reviews or tools that helped write it. The subject
  line names the area first, as in `Mixer: ...` or `Window: ...`.
- **Allow edits from maintainers** on the pull request; small fixes
  then land on your branch instead of a review round trip.

Merged contributors are listed in the README's Credits and get the
Contributor role on Discord, with access to the private `#contributors`
channel.

## AI-assisted contributions

AI-assisted contributions are allowed. The person who opens the pull
request is the author: responsible for the code, able to explain every
part of it, and the one who ran it. [AGENTS.md](AGENTS.md) is the short
brief an agent should read before working on the tree.

## Code conventions

- The window (`OpenXLR.UI`) has no reference to `OpenXLR.Core`, so
  nothing from libusb or lilv ends up in it. The two files both sides
  need, `OpenXlrPaths.cs` and `ProcessRunner.cs`, are compiled into the
  window as linked sources; anything else shared goes the same way.
- Every file under `~/.config/openxlr` is written through
  `OpenXlrPaths.WriteAtomic` (private modes, atomic replace), and every
  helper process runs through `ProcessRunner` (C locale, deadline,
  output cap, process tree killed past either).
- Commands are validated in `CommandValidation` before the mixer sees
  them; a new command needs an entry there, in the hub's dispatch, in
  the API doc, and in whichever client uses it.
- Tests that redirect `XDG_CONFIG_HOME` or `XDG_RUNTIME_DIR` join the
  xUnit collection `xdg-config`, so they never run in parallel with
  each other.
- Prose in docs, comments and messages: plain sentences, no em dashes.

## License

OpenXLR is GPL-3.0. By contributing you agree that your contribution
is licensed the same way.
