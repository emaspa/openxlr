# Optional LV2 editor host

`lv2-host.c` builds `openxlr-lv2-host`, a small process that loads one LV2
plugin, gives it PipeWire ports and, on request, opens the plugin's own X11
editor on that live instance. It exists because a plugin's editor talks to its
DSP instance directly (LV2 instance access), which a PipeWire filter chain
cannot offer.

The distribution packages build and install it, and its presence changes
nothing on its own: inserts use the filter chain until one is explicitly
switched to the native host in its controls window.

## Build

An ordinary .NET build does not invoke a C compiler. Opt in with:

```sh
dotnet build src/OpenXLR.slnx -c Release -p:EnableNativeLv2Host=true
```

The flag compiles this directory and copies the helper next to the daemon,
which is what the packaging recipes do. `make -C native` builds it alone.
It needs a C11 compiler, make, pkg-config and the development files for
PipeWire, lilv, LV2 and X11; the libraries it links are already runtime
dependencies of every package.

## What it does

- One process per insert, holding one plugin instance and its editor.
- Audio stays in PipeWire: the process is a `pw_filter` with the plugin's
  ports. Samples never cross the command pipe or managed code.
- The pipe carries control values only, in both directions. What the editor
  changes comes back and is saved with the mixer, like any other control.
- The daemon owns the process. Closing its stdin ends the helper, which is
  why it watches that pipe rather than using PDEATHSIG, whose Linux semantics
  follow the thread that spawned it.

## What it will not do to your audio

The whole point is that an editor cannot cost you the microphone.

- A protocol error from the editor is logged and the plugin keeps running.
  Xlib's default handler would have exited the process.
- A lost X connection drops the editor and leaves the plugin processing.
  Verified by asking the X server to close the helper's connection while an
  editor was open: the process kept its heartbeat, kept its ports, and opened
  a fresh editor on request. The same test on a build without the handlers
  killed the process immediately.
- An editor that stops answering freezes that insert's controls and says so.
  Its audio continues, and the daemon does not rebuild the chain for it.
- A chain that dies on its own is rebuilt, until it has failed three times
  within five minutes; then it is left off with the reason on the insert,
  because every rebuild of an input chain interrupts the microphone. Changing
  or bypassing the chain starts it over.

## Session environment

The editor needs an X11 display, so XWayland on a Wayland desktop. The helper
inherits the daemon's environment and does not go looking for a session. A
user service that started before the desktop published its display has none,
which shows up as an editor that will not open. From a terminal inside the
graphical session:

```sh
systemctl --user import-environment DISPLAY XAUTHORITY
systemctl --user restart openxlr-daemon.service
```

The restart interrupts audio for a moment. Repeat it after a graphical login
that assigns a new display. `XAUTHORITY`, when set, has to point at a cookie
file the daemon can read; display managers commonly put it under `/run/user`.
Use the session's own value rather than a copy, and never `xhost +`.

## Scope

URID map and unmap for the plugin, and the X11 editor features this host
implements: instance access, parent, resize and the idle interface. Worker
threads, state and presets, and the VST and CLAP formats are separate work.
Changing an insert's host rebuilds its chain; nothing here swaps a plugin
without a gap.
