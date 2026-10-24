# Contributing to OpenXLR

Thanks for helping. This page says how the project works so a change
lands on the first try. Questions before you start are welcome in the
`#dev` channel of the [Discord server](https://discord.gg/4bswtnGPW4)
or in a GitHub issue.

## What helps most

- **Hardware reports.** If you own a device we could not test ourselves,
  run the controls, note what works and what does not, and open an issue
  with a diagnostics archive (Options, SUPPORT, Collect diagnostics in
  the UI). The per-control table in
  [docs/hardware-support.md](docs/hardware-support.md) shows what is
  still unverified.
- **Bugs with a way to reproduce them.** The archive again, plus what
  you did and what you expected. The daemon's journal is
  `journalctl --user -u openxlr-daemon`.
- **A translation of the UI.** The UI ships in English and reads
  every string from one catalogue; a language is a copy of it with the
  values translated. [docs/localization.md](docs/localization.md) has the
  steps. Only translate a language you read and write well.
- **Code.** The [roadmap](docs/roadmap.md) is the priority list. Right
  now mixer stability and the mixer's presentation (per-mix
  customization) come before plugin-host expansion; a pull request in
  that order of priorities is easier to take than one against it. Ask first for anything large, so the work
  is not done twice or built on a model that is about to change.

## Building and testing

The .NET test process uses private configuration, data, cache, state and
Wine-prefix directories, with empty default CLAP and VST3 search roots.
Installed desktop plugins are not test inputs. `LV2_PATH` is retained for
the system LV2 and native latency-fixture checks listed below.

[docs/install-from-source.md](docs/install-from-source.md) covers the
prerequisites and the build. The checks CI runs:

```sh
dotnet restore src/OpenXLR.slnx --locked-mode   # the committed lock files must match
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror
dotnet test src/OpenXLR.slnx -c Release --no-build
python3 tools/test-installation-receipt.py
python3 packaging/yabridge/test-receipt.py
node --check plugin/com.emaspa.openxlr.sdPlugin/plugin.mjs
node --test plugin/tests/*.test.mjs
node --test browser/tests/*.test.mjs
node --check browser/tests/chromium-integration.mjs
python3 -I tools/browser-extension.py --check  # the Store zip (no manifest key) and the release tar.gz (the key), every referenced file present
python3 tools/test-browser-extension.py
python3 -m json.tool plugin/com.emaspa.openxlr.sdPlugin/manifest.json >/dev/null
python3 -m json.tool packaging/omarchy/openxlr.mixer/manifest.json >/dev/null
python3 tools/deck-skins.py --check  # regenerate with no flag after changing embedded skins or colour defaults
python3 tools/deck-icons.py --check  # regenerate with no flag after changing LayoutIcons.cs
python3 tools/eq-reference.py --check  # regenerate with no flag after changing the EQ reference settings
python3 packaging/flatpak/test-package.py  # the Flatpak manifest and its files
python3 packaging/flatpak/nuget-sources.py --check  # regenerate with no flag after a package change
shellcheck --severity=error tools/check-version.sh tools/check-locked-restore.sh tools/ci-install-packages.sh packaging/ppa/make-source.sh
tools/check-version.sh                          # the six version locations agree
tools/check-locked-restore.sh                   # every packaging path restores locked
tools/check-openapi.py docs/openapi-v1.json     # the HTTP API document keeps its shape
tools/check-spec.py packaging/rpm/openxlr.spec  # every installed file is in %files
python3 tools/check-omarchy.py  # Qt 6 QML tools, with external type declarations
python3 tools/omarchy-skins.py --check  # embedded meter tokens match the generated table
QT_QPA_PLATFORM=offscreen /usr/lib/qt6/bin/qmltestrunner -input packaging/omarchy/tests
node --test packaging/omarchy/tests/*.test.mjs
shellcheck --severity=error packaging/omarchy/openxlr-omarchy-enable
make -C native  # C/C++, PipeWire, lilv, LV2 and X11 development headers
make -C native test-audio test-clap test-lv2 test-vst3 test-scan  # audio bounds, stall detection, CLAP bus layouts, latency reports, VST3 parameter and stream checks, scan phase markers
dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Lv2BundleTests|FullyQualifiedName~PluginSearchPathTests"  # run with lilv installed, even if the earlier suite ran without it
python3 tools/test-monitor-volume.py  # private PipeWire server and session bus; pipewire-pulse, wireplumber, pactl, dbus-daemon
xvfb-run -a make -C native test-editor  # also needs Xvfb and xauth
make -C native test-sound-check  # Sound Check loop backend, no audio server
make -C native test-eq  # microphone EQ backend against the cookbook table, no audio server
make -C native test-duck  # duck detector, gain depth and timing, no audio server
OPENXLR_TEST_DESKTOP=1 xvfb-run -a dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~TrayWindowTests
OPENXLR_TEST_LAYOUT=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~WindowLayoutTests
OPENXLR_TEST_TOOLTIP=1 xvfb-run -a -s '-screen 0 1600x1000x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ToolTipInputTests
OPENXLR_TEST_SKIN=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~SkinWindowTests
OPENXLR_TEST_EQ=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~EqEditorWindowTests  # also needs libXtst
OPENXLR_TEST_CAPTURE=1 xvfb-run -a -s '-screen 0 2560x1440x24' dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build --filter FullyQualifiedName~CaptureInputWindowTests
```

The monitor gain test plays a constant 0.1 signal, not a sine. On PipeWire
1.0.x the two combine legs of a summed monitor feed are not sample-aligned,
so a 1 kHz sine summed from Monitor A and B reads below the expected level
although each leg's gain is right. With DC the sum does not depend on that
delay. The test measures the settled part of the capture and the peak of
the whole capture; it does not measure frequency response.
The private audio runner allows fifteen minutes for the full test process,
including startup. Individual test deadlines and audio assertions remain
in force; the suite limit bounds a hung run before its servers are stopped.
Sound Check waits for captured samples to settle within a deadline after a
filter change, rather than assuming a fixed startup time for the stream.

The desktop keys tests in the main suite start a private session bus with
`dbus-daemon` and skip when it is not installed. `OPENXLR_TEST_KWIN=1` adds
a check that asks a running KDE Plasma session for its focused process; it
routes no audio and is not part of CI.

The optional Chromium acceptance run needs Playwright and Chrome for Testing
151 or newer. Set `OPENXLR_PLAYWRIGHT_MODULE` to its `playwright/index.mjs`
and `OPENXLR_TEST_CHROMIUM` to the Chrome executable, then run in a container
without sound, USB or hidraw devices and with port 37890 free:

```sh
python3 tools/test-monitor-volume.py --command node browser/tests/chromium-integration.mjs
```

Use Xvfb for a visible browser or leave DISPLAY unset for headless mode.
`OPENXLR_TEST_BROWSER_SCREENSHOT` optionally names a new popup PNG. The run
uses its own browser profile and daemon configuration. It checks separate
tab audio, live channel changes, navigation and daemon restart. A real Teams
call still needs a listening check for delay and echo cancellation.

The idle graph allocation check measures reads on a dedicated warmed thread,
so test-runner diagnostic allocations are outside the measured interval. It
still requires zero bytes and the same cached snapshot across 10,000 reads.

Tests of retries, cancellation and shutdown wait on a handshake with a
deadline, not on timer scheduling or on which thread wins a race. A test
that waits for a helper process to exit also waits for the output it
expects, since exit alone does not mean its output has been read.

The private PipeWire runner also checks profile startup ordering. To exercise
ClipGuard with recorded test audio, low cut and a native LSP gate, run
`OPENXLR_TEST_DSP=1 python3 tools/test-monitor-volume.py` after a native-enabled
build, with swh-plugins and LSP LV2 plugins installed. The same run checks
latency compensation against the fixture in `native/tests`; the runner puts
that folder first on `LV2_PATH` itself, so the export is only needed when
you call `dotnet test` directly.
Unless it is given `--command`, the runner first builds the LV2 test bundles
there with `make -C native test-fixtures`; capture chains, Sound Check and
input chains load them as inserts and hear silence without their binaries.
The runner isolates plugin scans from user-installed CLAP and VST3 bundles.
CI installs the DSP test plugins, rebuilds with the native helper and runs
this check separately with
`OPENXLR_TEST_FILTER="FullyQualifiedName~DspAudioIntegrationTests|FullyQualifiedName~PluginLatencyIntegrationTests|FullyQualifiedName=OpenXLR.Tests.MonitorVolumeIntegrationTests.MixInsertAndPostLinksRecoverWithoutRestartingThePlugin"`.
The mix insert route-recovery check also needs DSP enabled, so it joins this
selection even though it lives in the monitor test class.
For a focused rerun on the same private server, set `OPENXLR_TEST_FILTER` to
the desired `dotnet test` filter instead of passing a second `--filter`.
CI spreads both runs over parallel jobs with `OPENXLR_TEST_SHARD=N/M`, which
runs every Mth test of the selection by sorted name, starting at the Nth.
A failing CI shard reruns locally with the same value, for example
`OPENXLR_TEST_SHARD=2/3 python3 tools/test-monitor-volume.py`.

Application routing tests run the normal mixer sweep while waiting for the
destination to settle. PipeWire's move acknowledgement can arrive before
the session manager publishes the new link. Check both the tracked channel
and the real stream destination instead of assuming they change together.
The capture, output-route and key-control audio tests enable the registry
subscription so they exercise the daemon's normal discovery path.

The window layout test runs in its own process using X11 and isolated
configuration, runtime and session-bus settings. It checks narrow plugin
windows and mixer widths from 640 to 2400 logical pixels. Set
`OPENXLR_LAYOUT_ARTIFACTS` to a directory to save rendered previews. Set
`OPENXLR_LAYOUT_FONT` to an installed font family, such as `DejaVu Sans`, to
check wrapping with different font metrics. The same run checks Arrange mode
with a real pointer and keyboard through XTEST, so it needs `libXtst` like
the tooltip test.

The tooltip test runs in its own process too. It drives a real pointer
through the X server's XTEST extension, so it needs `libXtst`, and it
checks that a tooltip shown at the bottom of the screen still lets the
control underneath it be clicked. Set `AVALONIA_GLOBAL_SCALE_FACTOR` to
run the same cases at another desktop scale.

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

The native-enabled application build uses `-p:EnableNativeLv2Host=true`.
Keep that flag when rebuilding a local installation that uses CLAP, VST3
or native LV2 editors. Desktop acceptance checks must include plugin
controls, resizing, moving and reopening editors; Xvfb tests do not replace
those checks. The optional Windows bridge has a separate artifact workflow
and [package checks](packaging/yabridge/README.md).

## Dependency updates

The CodeQL init and analyze steps must use the same pinned commit. Dependabot
groups these actions so a version update changes both steps in one pull request.

Dependabot groups Avalonia packages because the UI uses them as one
framework. A grouped update can still leave a referencing project's lock
file stale. Run the full solution restore and regenerate the Nix dependency
list as described above, then commit all changed lock files before the
locked restore and tests. Do not disable locked mode to make an update pass.

## Pull requests

CI and CodeQL run for pull requests and for pushes to `main`. A newer
push to a pull request cancels the run it replaces. To check a branch
before its pull request exists, open the pull request as a draft or start
CI by hand with `gh workflow run CI --ref <branch>`. The checks that need
no .NET build run for every change. A change that touches only Markdown
files other than `docs/skins.md`, pictures under `docs/`, or the deck
plugin's script, pages and tests skips the build and tests, and the
Omarchy QML check runs only when its own files or the workflow change.
Skipped jobs count as passing for the required `ci-ok` check. Every night
the whole CI runs on `main` with nothing skipped, and a failure opens an
issue labelled `ci-nightly`, or comments on the open one. CodeQL
skips a change to `README.md` alone.

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
  file and its commands, [docs/skins.md](docs/skins.md) for appearance
  values and the skin format); user-facing behaviour into
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

- The UI (`OpenXLR.UI`) has no reference to `OpenXLR.Core`, so
  nothing from libusb or lilv ends up in it. The two files both sides
  need, `OpenXlrPaths.cs` and `ProcessRunner.cs`, are compiled into the
  UI as linked sources; anything else shared goes the same way.
- Every file under `~/.config/openxlr` is written through
  `OpenXlrPaths.WriteAtomic` (private modes, atomic replace), and every
  helper process runs through `ProcessRunner` (C locale, deadline,
  output cap, process tree killed past either); a program handed to the
  user, such as an installer or the desktop's link opener, goes through
  its `RunInteractiveAsync`, which sets no deadline. The raw processes
  are the ones that live as long as what they serve: the meter reader's
  `parec`, the `pw-loopback` and filter-chain `pw-cli` nodes of the graph,
  the native plugin host and the USB helper, each stopped with its owner.
- Commands are validated in `CommandValidation` before the mixer sees
  them; a new command needs an entry there, in the hub's dispatch, in
  the API doc, and in whichever client uses it.
- Tests that redirect `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_DATA_DIRS`
  or `XDG_RUNTIME_DIR` join the xUnit collection `xdg-config`, so they
  never run in parallel with each other.
- A test that needs a fake helper program builds it with
  `ExecutableScript.Write`. A program the test process wrote itself
  cannot be run while other test classes start helpers: the write handle
  reaches their forked children and Linux answers exec with "Text file
  busy".
- The UI holds no colour, font size or corner radius of its own. A
  new surface reads an `Ox.*` resource with `DynamicResource`, and a new
  value is added to `src/OpenXLR.UI/Skinning/SkinTokens.cs`, then to its
  table in [docs/skins.md](docs/skins.md) and to
  [docs/skin.schema.json](docs/skin.schema.json); `SkinDocumentTests`
  holds the three together. A control appearance a skin can choose is an
  entry in `src/OpenXLR.UI/Skinning/SkinControls.cs` and a control theme
  in `Skinning/Controls.axaml`.
- Text the UI shows is an entry in
  `src/OpenXLR.UI/Localization/Strings.resx`, read by its literal key with
  `{loc:Text Key=...}` in markup or `Localizer.Text` and `Localizer.Format`
  in code; `LocalizationTests` holds the catalogue and the sources together.
  [docs/localization.md](docs/localization.md) has the details.
- Prose in docs, comments and messages: plain sentences, no em dashes.

## License

OpenXLR is GPL-3.0. By contributing you agree that your contribution
is licensed the same way.
