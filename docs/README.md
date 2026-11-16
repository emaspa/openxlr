# Documentation

These guides describe the code on `main`. For a released package, select
its tag on GitHub to read matching documentation. The app's Manual links
open `main`, so they can describe changes newer than the installed release.

## Using OpenXLR

- [Project README](../README.md): supported devices, packages and quick start.
- [Getting started](getting-started.md): the first microphone, listening
  output and routed app after installing, then a separate mix for a call
  or stream.
- [Manual](manual.md): mixer tasks, application routing, plugins, the
  soundboard, profiles, Flow, Stream Deck, troubleshooting, local files,
  the terminal mixer and the Omarchy bar.
- [Features](features.md): supported behaviour by area.
- [Skins](skins.md): the UI's appearance values, where skins live and
  the rules one is read under, with a [JSON schema](skin.schema.json) and an
  [example skin](examples/skins/example/skin.json) to start from.
- [Effect presets](effect-presets.md): copied effects, A/B comparison and
  saved presets, with the preset file format and its limits.
- [Hardware support](hardware-support.md): mapped controls, verification
  records, unmapped hardware features, and what a microphone or interface
  of another brand gets.
- [Omarchy shell plugin](omarchy.md): the bar plugin, its enable command,
  the Arch package recipe and its limits.
- [Multiple Wave interfaces](wave-interfaces.md): driving up to four more
  units beside the primary, their controls and capture channels.
- [Browser tab routing](browser-routing.md): the Chromium companion, its
  native host and per-tab channels, with its
  [privacy policy](browser-privacy.md).
- [GNOME focused routing](gnome-focus.md): the optional OpenXLR Focus
  extension for GNOME Shell on Wayland.
- [Installation receipts and doctor](installation.md): owned files, build
  identity and repair commands.
- [Flatpak](flatpak.md): the experimental Flatpak mode, its startup, logs
  and host setup.
- [Source installation](install-from-source.md): prerequisites, native build,
  service setup, updates, uninstall and environment variables.
- [Windows bridge](../packaging/yabridge/README.md): companion availability,
  private wrappers, package builds and rollback.
- [Experimental UCM profile](../packaging/ucm/README.md): optional Pro channel
  split outside the submixer.
- [OpenDeck patch](../packaging/opendeck-patches/README.md): optional key-image
  quality improvement for the older host version.

## Developing and integrating

- [Contributing](../CONTRIBUTING.md) and [agent instructions](../AGENTS.md).
- [Roadmap](roadmap.md): implemented work and remaining priorities.
- [Architecture](architecture.md) and [source orientation](../src/README.md).
- [WebSocket API and state](api.md), [HTTP API](http-api.md),
  [OpenAPI document](openapi-v1.json) and [saved mixer layout](mixer-layout.md).
- [Native plugin host](../native/README.md): backend contracts, editor
  behaviour, tracing and regression checks.
- [Localization](localization.md): the string catalogue, the languages and
  how a translation is added and checked.
- [Flatpak build](flatpak-build.md) and [Flatpak testing](flatpak-testing.md):
  building the bundle and the installed acceptance checks.

## Protocol evidence and historical research

These records retain dated observations and provisional labels. Use the
hardware support table for current support claims.

- [USB capture guide](usb-capture.md): new captures for unmapped MK.1 controls.
- [Hardware effects capture](hardware-effects-capture.md): collecting the
  evidence for the onboard effects that are still unmapped.
- [Pro protocol notebook](wave-xlr-pro-protocol.md): chronological captures
  and corrections; later verified mappings supersede early guesses.
- [Pro capture plan](wave-xlr-pro-capture-plan.md): completed capture procedure.
- [MK.2 equalizer findings](mk2-eq-findings.md): captures and
  measurements showing the XLR Dock MK.2 and Wave XLR MK.2 run their EQ
  on the device on firmware 1.0.15, its layout and master enable bit,
  and the dock's earlier firmware 1.0.0, where Wave Link ran it on the
  computer.
- [Linux audio research](wave-xlr-linux-audio-stack-research.md): August 2026
  survey and original design decisions.
