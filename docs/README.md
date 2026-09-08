# Documentation

These guides describe the code on `main`. For a released package, select
its tag on GitHub to read matching documentation. The app's Manual links
open `main`, so they can describe changes newer than the installed release.

## Using OpenXLR

- [Project README](../README.md): supported devices, packages and quick start.
- [Manual](manual.md): mixer tasks, application routing, plugins, profiles,
  Flow, Stream Deck, troubleshooting and local files.
- [Features](features.md): supported behaviour by area.
- [Hardware support](hardware-support.md): mapped controls, verification
  records and unmapped hardware features.
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

## Protocol evidence and historical research

These records retain dated observations and provisional labels. Use the
hardware support table for current support claims.

- [USB capture guide](usb-capture.md): new captures for unmapped MK.1 controls.
- [Pro protocol notebook](wave-xlr-pro-protocol.md): chronological captures
  and corrections; later verified mappings supersede early guesses.
- [Pro capture plan](wave-xlr-pro-capture-plan.md): completed capture procedure.
- [Linux audio research](wave-xlr-linux-audio-stack-research.md): August 2026
  survey and original design decisions.
