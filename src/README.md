# OpenXLR source tree

The canonical user, installation, hardware-support, architecture, and API
documentation lives in the repository [README](../README.md). Protocol status
is tracked in [hardware-support.md](../docs/hardware-support.md); the chronological
capture notebook is [wave-xlr-pro-protocol.md](../docs/wave-xlr-pro-protocol.md).

This file is intentionally limited to developer orientation so it cannot drift
into a second, contradictory product manual.

## Projects

- `OpenXLR.Core`: device backends, capability model, PipeWire graph, profiles,
  application matching, meters, and plugin chains.
- `OpenXLR.Daemon`: owns hardware and mixer state and exposes the localhost
  WebSocket and HTTP APIs.
- `OpenXLR.UI`: Avalonia client; it never owns hardware or audio state.
- `OpenXLR.Probe`: diagnostics and protocol-development console tool.
- `OpenXLR.Tests`: regression tests for routing, device capabilities, profiles,
  diagnostics, and optional DSP dependencies.
- `../plugin/com.emaspa.openxlr.sdPlugin`: production OpenDeck/Stream Deck
  client of the same API.

## Current audio-graph invariants

- One combine sink per channel fans audio into the mixes; its internal streams
  are the per-mix faders. The matrix does not use one `pw-loopback` process per
  cell.
- Physical outputs use direct PipeWire port links so the hardware sink clocks
  the graph.
- Hardware input channels follow the actively selected interface. There is no
  `setMicInput` command or `OPENXLR_MIC_INPUT` override.
- Low cut and software ClipGuard use PipeWire filter-chain. LV2 inserts
  default to filter-chain and can opt into the native helper; CLAP and VST3
  always use it. Chains can precede XLR channels or process mix outputs. A replacement chain must be complete before
  it replaces the audible route.
- Device controls are capability-gated. A backend must not advertise a control
  until its implementation and hardware mapping are usable.

## Build and test

From the repository root:

```sh
dotnet restore src/OpenXLR.slnx --locked-mode
dotnet build src/OpenXLR.slnx -c Release --no-restore -warnaserror -p:EnableNativeLv2Host=true
dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build
```

Run the daemon with the mixer enabled:

```sh
OPENXLR_BUILD_MIXER=1 ./src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
# in another terminal, from the repository root:
./src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
```

The WebSocket command table and configuration paths are maintained only in the
[API reference](../docs/api.md). The complete check list is in
[CONTRIBUTING.md](../CONTRIBUTING.md); native editor and bridge details
are in [native/README.md](../native/README.md) and the
[companion guide](../packaging/yabridge/README.md).
