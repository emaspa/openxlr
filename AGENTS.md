# Working on OpenXLR with an AI agent

AI-assisted contributions are allowed here, under one rule: **the
person who opens the pull request is the author.** You are responsible
for the code, you must be able to explain every line of it when asked,
and you must have run it. A pull request whose author cannot answer
questions about it is closed, whatever helped write it.

The rest of this page is for the agent. [CONTRIBUTING.md](CONTRIBUTING.md)
holds the full guidelines; this is the short version an agent should
read before touching the tree.

## Ground rules

- Read [docs/roadmap.md](docs/roadmap.md) first. Work that goes
  against its order of priorities is not taken.
- Do not bump the version anywhere, do not tag, do not touch the
  release workflows' outputs. Releases are one commit by the maintainer.
- Do not add attribution trailers, tool names, or references to
  reviews in commit messages. A message says what changed and why, in
  plain sentences, subject line named by area (`Mixer: ...`,
  `Window: ...`, `Daemon: ...`).
- Commits are signed; the repository refuses unsigned pushes.
- No em dashes in prose, code comments, docs or messages. Plain
  sentences, sentence case.
- Every change carries its tests and its docs:
  [docs/api.md](docs/api.md) for commands and state,
  [docs/http-api.md](docs/http-api.md) for the HTTP transport,
  [docs/mixer-layout.md](docs/mixer-layout.md) for the layout file and
  its live commands, [docs/manual.md](docs/manual.md) for behaviour
  users see.

## Build and check

```sh
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror
dotnet test src/OpenXLR.slnx -c Release --no-build
node --check plugin/com.emaspa.openxlr.sdPlugin/plugin.mjs
node --test plugin/tests/*.test.mjs
python3 -m json.tool plugin/com.emaspa.openxlr.sdPlugin/manifest.json >/dev/null
shellcheck --severity=error tools/check-version.sh tools/check-locked-restore.sh packaging/ppa/make-source.sh
tools/check-version.sh
tools/check-locked-restore.sh
tools/check-openapi.py docs/openapi-v1.json
tools/check-spec.py packaging/rpm/openxlr.spec
make -C native  # optional LV2 host; needs the PipeWire, lilv, LV2 and X11 headers
```

CI runs exactly these; the build treats warnings as errors, so a build
counts as clean only with `0 Error(s)` and `0 Warning(s)`.
After a package change, regenerate the lock files with a plain
`dotnet restore src/OpenXLR.slnx` and the Nix dependency list with
`nix build .#openxlr.passthru.fetch-deps -o /tmp/fd && /tmp/fd packaging/nix/deps.json`,
and commit both.

## Where things are

- `src/OpenXLR.Core`: device protocols (`Devices/`), the PipeWire
  submixer (`Mixing/`), profiles, shared paths and the process runner.
- `src/OpenXLR.Daemon`: the hosted service, WebSocket hub, command
  validation, token, watchdog.
- `src/OpenXLR.UI`: the Avalonia window. It has no reference to Core;
  the two files both need are compiled in as linked sources.
- `plugin/com.emaspa.openxlr.sdPlugin`: the OpenDeck plugin
  (`plugin.mjs`) and its property inspectors; tests in `plugin/tests`.
- `packaging/`: unit, udev rule, WirePlumber rules, RPM spec, Nix,
  PPA script; `debian/` for the .deb.

## Rules the code already follows

- Files under `~/.config/openxlr` are written through
  `OpenXlrPaths.WriteAtomic`; helper processes run through
  `ProcessRunner`. Do not add a `Process.Start` or a `File.WriteAllText`
  for either.
- A new command is registered in `CommandValidation`, dispatched in
  `WebSocketHub`, documented in `docs/api.md`, and handled in the client
  that uses it.
- Tests that redirect `XDG_CONFIG_HOME` or `XDG_RUNTIME_DIR` join the
  xUnit collection `xdg-config`.
- Hardware behaviour is verified on hardware. When you cannot, say so
  in the pull request instead of describing a test you did not run.
