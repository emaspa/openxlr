# Optional LV2 editor host

This directory builds `openxlr-lv2-host`, a small process that loads one
plugin, LV2, CLAP or VST3, gives it PipeWire ports and, on request, opens
the plugin's own X11 editor on that live instance. `host.c` is the
process: the audio node, the command pipe, the window and the threads.
`lv2.c`, `clap.c` and `vst3.cpp` are the formats behind one interface. It exists because a plugin's editor talks to its
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

## Tracing a VST3 plugin

With `OPENXLR_HOST_TRACE` set in the helper's environment, the VST3 backend
writes the plugin's bus layout, the result of switching processing on and the
first four audio cycles to stderr, which the daemon forwards to its log. Run
the helper by hand for a quick look:

```sh
OPENXLR_HOST_TRACE=1 native/openxlr-lv2-host vst3 /usr/lib/vst3/Plugin.vst3 <class-id> test 2 48000
```

## Scope

For the plugin: URID map and unmap, the worker extension, options, and the
promise that the block length is bounded. The worker runs on a thread of its
own, fed by two lock-free rings, so a plugin that hands off heavy work never
does it in the audio callback; its answers are delivered before the next run.
That is what reverbs and convolvers ask for, and without it they could not be
hosted at all.

For the editor: instance access, parent, resize and the idle interface.

For CLAP: parameters, the audio ports, the X11 editor, timers, file
descriptors, thread checks and a log. The same binary describes a CLAP
bundle for the daemon's catalogue (`openxlr-lv2-host scan-clap FILE`), in a
process of its own so the daemon never loads plugin code. The headers are
vendored under `clap/` (MIT, version 1.2.10).

For VST3: the component and its controller, wired through their connection
points and started in step; the host objects a plugin expects (application,
message and attribute list, component handler, parameter changes, a memory
stream for state); the main audio buses at the chain's width; and the editor
through `IPlugFrame` with a Linux `IRunLoop` on PipeWire's main loop. The
daemon speaks plain values and the processor takes normalised ones, so the
conversion happens on the main thread where the controller lives. The
scanner (`openxlr-lv2-host scan-vst3 BUNDLE`) does not create editors,
since that is most of the cost of describing a large module; every VST3
plugin is assumed to have one and the host finds out when asked. The
interface headers are vendored under `vst3/` (MIT, VST 3.8.1); only their
inline parts are used, so nothing of the SDK is compiled. Windows VST3
plugins arrive through yabridge as ordinary bundles.

State and presets, and VST2, are separate work. Changing an insert's host
rebuilds its chain; nothing here swaps a plugin without a gap.
