# Native plugin host

This directory builds `openxlr-lv2-host`, a small process that loads one
plugin, LV2, CLAP or VST3, gives it PipeWire ports and, on request, opens
the plugin's own X11 editor on that live instance. `host.c` is the
process: the audio node, the command pipe, the window and the threads.
`lv2.c`, `clap.c` and `vst3.cpp` are the formats behind one interface. It exists because a plugin's editor talks to its
DSP instance directly (LV2 instance access), which a PipeWire filter chain
cannot offer.

The distribution packages build and install it, and its presence changes
existing LV2 inserts: they use filter-chain until explicitly switched in
the controls window. CLAP and VST3 always use this helper.

## Build

An ordinary .NET build does not invoke a C compiler. Opt in with:

```sh
dotnet build src/OpenXLR.slnx -c Release -p:EnableNativeLv2Host=true
```

The flag compiles this directory and copies the helper next to the daemon,
which is what the packaging recipes do. `make -C native` builds it alone.
It needs a C11 and a C++ compiler, make, pkg-config and the development files for
PipeWire, lilv, LV2 and X11; the libraries it links are already runtime
dependencies of every package.

`make -C native test-editor` runs the host's resize regression tests on
`DISPLAY`, using a small test editor and a separate X connection. On a
machine without a desktop, run `xvfb-run -a make -C native test-editor`.
These tests need neither a plugin installation nor a running PipeWire
server. They cover event delivery without idle polling, coalesced sizes,
drag reversal, size constraints, changes to child-window size bounds,
fixed-size editors and plugin-driven scaling, the Wine coordinate nudge
and display cleanup. Real plugin repainting and mouse input still need
desktop testing.

`make -C native test-audio` checks the audio thread's two safety rules
without a graph: a cycle the fixed buffers cannot carry is refused before
anything is written, and a plugin stuck inside one process call is told
apart from a node that has no work. `make -C native test-clap` checks the
CLAP port layout against fake plugins that declare more buses than the
chain uses, and that the scanner and the loader agree about which layouts
can be carried. It also checks that a CLAP plugin's latency is read after
activation and again only after the plugin says it changed, and that the
host reports it once and then once per change. Teardown checks use a private
event loop: a plugin may unregister timers and file-descriptor watches in
its destructor, and the host collects any remaining watches afterwards
without closing the plugin's descriptors. Neither test needs a PipeWire
server, a display or an installed plugin.

`make -C native test-lv2` loads the fixture in `tests/latency.lv2`, a stereo
delay whose latency port follows its delay control, through lilv and the LV2
backend, and checks that its latency is reported once audio has run and again
when the delay changes. It builds the fixture itself and needs lilv, but no
PipeWire, display or installed plugin.

`make -C native test-vst3` checks VST3 parameter queue indices, attribute
identifiers and buffers, the component-state memory stream, and that the
plugin is asked for its latency at activation and after `kLatencyChanged`
only, however often the host reads the figure. Invalid
lengths, seek modes and arithmetic overflow are rejected without changing
stream contents. Reads at or beyond EOF return zero bytes; a later write
fills the skipped bytes with zeroes. Empty binary attributes are supported.
The component can unregister run-loop watches during termination; the host
releases remaining watches before unloading the module and keeps plugin-owned
file descriptors open. Unregistering detaches a watch before releasing its
handler, so a callback from that release cannot destroy the source twice.
Closing an editor or a failed editor attachment also collects its run-loop
watches before releasing the view, including handlers owned by the view.
These checks need no display, installed plugin or PipeWire server. They do
not turn a native plugin into a sandbox or cap its own allocations.

`make -C native test-scan` checks the ordinary CLAP and VST3 phase markers
against a fixture that exits inside each phase. It also scans 200 plugins
with 32 parameters each and checks that markers stay at four lines per
CLAP plugin and five per VST3 audio class, plus four per module. It needs
Python 3 and a compiler with C++20 support for the fixture, but no Wine,
display or installed plugin. The normal host build still uses C++17.

`make -C native test-sound-check` drives the Sound Check backend with fixed
samples: live passthrough, recording, replay, a new recording replacing the
old one, the ten-second bound in samples and in time, and that only the audio
thread reports the mode. It needs no PipeWire, display or plugin.

`make -C native test-eq` drives the microphone EQ backend the same way. It
measures the spectrum of an impulse at 48 kHz and 44.1 kHz against the table
in `tests/eq-reference.txt`, which `tools/eq-reference.py` computes from the
Audio EQ Cookbook, within 0.05 dB; checks that a 0.25 dB or 1 Hz step keeps
the error below -30 dB of the ideal output while an emptied filter state
measures far worse; that switching a band or the EQ and changing a band's type
follow a 5 ms crossfade to within -40 dB; that a minute of silence after a
loud burst leaves no subnormal number and every filter at rest; and prints
what a sample costs.

`python3 native/tests/lsp-editor.py` is an opt-in desktop regression using
an isolated LSP Gate Mono LV2 instance. It needs python-xlib, the installed
plugin, a running PipeWire server and a display large enough for the tested
sizes. It drags the editor to large sizes and back, moves it, closes and reopens
it, checking that changed
parameters still repaint. `--host PATH` selects another build; `--opengl`
tests the explicit OpenGL override. The display must fit the largest tested
window (6000 by 2400 pixels), including its chosen position. A hidden editor
is not treated as an open window. It creates no audio links.

## What it does

- One process per insert, holding one plugin instance and its editor.
- Audio stays in PipeWire: the process is a `pw_filter` with the plugin's
  ports. Samples never cross the command pipe or managed code.
- The pipe carries parameter values, status and editor requests in both
  directions, never audio. Exposed parameter changes are saved with the mixer;
  opaque plugin state and plugin presets are not persisted.
- CLAP read-only parameters are queried on the host's main thread with each
  status tick. Parameter-change events still arrive through processing or
  flushing; the audio callback never calls the main-thread-only `get_value`.
- The helper reports the plugin's algorithmic latency as
  `latency SAMPLES RATE` once audio has run, and again whenever the figure
  changes. `SAMPLES` is 4294967295 while the plugin has no figure to give.
  LV2 reads the plugin's latency port, which the plugin writes on every run.
  CLAP and VST3 ask the plugin on the main thread after activation and again
  only after it says the figure changed (CLAP's latency `changed` callback,
  VST3's `kLatencyChanged`), so the per-tick check costs no call into the
  plugin. Nothing is added to the audio callback. The daemon keeps the last
  report and ignores one made at a rate other than the helper's own.
- The daemon owns the process. Closing its stdin ends the helper, which is
  why it watches that pipe rather than using PDEATHSIG, whose Linux semantics
  follow the thread that spawned it.

Plugin libraries share the helper's stdout. The daemon retains at most 4096
characters per protocol line and discards an oversized line through its next
newline, then resumes reading replies and heartbeats. Parameter symbols must
contain 1 to 255 characters, and values must be finite. Pending control changes
and meter values each retain at most 4096 distinct symbols, matching the native
host's control limit. Existing symbols can still update at that limit; draining
pending changes frees their slots. These bounds protect the daemon's protocol
buffers, not the plugin process's own memory or CPU use.

## Ducking

The built-in `duck` backend applies one gain to a stereo bus, detected from
a separate mono key. It sums nothing and adds no algorithmic latency. The
daemon does not create this stage yet.

```text
openxlr-lv2-host duck NODE 2 RATE [SYMBOL=VALUE ...]
```

`NODE` is the PipeWire node name. `RATE` is the fixed sample rate in Hz,
8000 to 384000; a changed rate or a quantum above 8192 frames is refused
by the host. The backend refuses any channel count other than 2. Its ports
are `playback_0` and `playback_1` for the bus inputs, `capture_0` and
`capture_1` for its outputs, with audio channels `FL` and `FR`. `key_0` is
an input with audio channel `MONO`. All five carry 32-bit float mono DSP
buffers. An unlinked key reads silence. Existing backends have no key port.

| Symbol | Range | Default | Meaning |
|---|---|---|---|
| `enabled` | 0 or 1 | 0 | Duck while the key is above threshold |
| `threshold` | -60 to 0 dBFS | -12 | Key threshold |
| `depth` | 0 to 40 dB | 12 | Full reduction |
| `attack` | 1 to 5000 ms | 80 | Time from unity to full reduction |
| `release` | 1 to 10000 ms | 1200 | Time from full reduction to unity |
| `key_open` | 0 or 1 | 1 | Let the detector hear the key |
| `reduction` | 0 to 40 dB | 0 | Read-only meter of the current reduction |

Initial inputs use `SYMBOL=VALUE` arguments. Each subsequent stdin line
`set SYMBOL VALUE` changes one input, read at the start of the next audio
block. The host clamps finite values to the ranges above and refuses
nonfinite values, unknown symbols and writes to `reduction`. The two
switches treat values below 0.5 as off and values at or above 0.5 as on.
The host prints `meter reduction VALUE` on stdout when the value changes,
checked every 33.3 ms; the audio thread updates it at each block's end.
The ordinary `ready`, `heartbeat` and `ui-heartbeat` messages still apply.
`quit` or stdin EOF stops the helper. This backend has no editor.

For each sample, `k = abs(key[i])`, or zero while `key_open` is off or
the sample is not finite. The double-precision peak envelope follows
`env = max(k, env * exp(-1 / (0.050 * RATE)))`. It is above threshold at
`env >= 10^(threshold / 20)`. The target gain is `-depth` dB while enabled
and above, otherwise 0 dB. Gain moves linearly in dB toward that target,
down by `depth / (attack * RATE / 1000)` per sample or up by
`depth / (release * RATE / 1000)`. Both channels are multiplied by
`10^(gain / 20)`, exactly `1.0f` at zero gain, and `reduction` is `-gain`.
A depth of zero means unity. Disabling releases from the current gain;
closing the key lets the envelope decay before release starts. The
envelope is flushed to zero below the smallest normal double to keep long
silences out of subnormal state. Processing allocates nothing and takes no
locks.

The Pro's sensitivity values are Low -3, Medium -12 and High -24 dBFS;
amount is Low 6, Medium 12 and High 24 dB. Speed is Slow 100/2000,
Medium 80/1200 or Fast 30/200 ms for attack/release. These are numeric
controls here; preset names belong to the caller. With Medium and a
-6 dBFS key, gain reaches -6 dB at 40 ms and -12 dB at 80 ms. After the
key stops, the envelope holds for about 34.5 ms, then releases over
1200 ms.

`make -C native test-duck` drives the backend at 48 kHz in 256-frame
blocks, with a shorter final block when needed and no audio server. It
checks bit-exact idle output, the Medium timing and meter, monotonic
release, all three depths and speeds, Low and High sensitivity, key gating,
ignored infinite and NaN key samples, threshold equality, disabling without a gain jump, zero depth, the
per-sample step bound and a minute of silence without nonfinite or
subnormal state. `make -C native test-audio` checks port creation on an
unconnected filter, including no key port for existing backends and a
silent fallback for a missing key buffer.

## Editor failures and audio recovery

Display loss or stalled editor controls do not rebuild a healthy audio
instance. Plugin code and its editor share a process, so a process crash
can still interrupt that insert's chain.

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
  or bypassing the chain starts it over. The last line the helper wrote to
  stderr goes to the daemon's log at each death and is shown with that
  reason. Trailing blank lines and whitespace do not replace the last useful
  message; a helper that dies mid-line keeps that final partial message.
- A VST3 plugin that reports a latency change keeps running: the host asks
  for the new figure and reports it, without reactivating the plugin, so a
  plugin that only settles a new figure on reactivation goes on reporting
  the old one. Only a
  reload or a changed bus layout (`kReloadComponent`, `kIoChanged`) ends the
  process for a fresh one.

## Session environment

The editor needs an X11 display, so XWayland on a Wayland desktop. The helper
inherits the environment the daemon starts it with. A daemon with no `DISPLAY`
of its own, one started before the desktop published it or left running across
a logout, asks systemd's user manager for the session's display and passes
that on, so an editor usually opens without anyone doing anything.

Where even the manager has no display, an editor answers "no X display" and
the session has to hand it over. From a terminal inside the graphical session:

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
writes the plugin's bus layout, the result of switching processing on, the
first four audio cycles and each `restartComponent` request to stderr. The
daemon forwards the lines that start with `trace: ` to its log, up to 64 for
each helper process, so set the variable on the service to see them in the
journal. Run the helper by hand for a quick look:

```sh
OPENXLR_HOST_TRACE=1 native/openxlr-lv2-host vst3 /usr/lib/vst3/Plugin.vst3 <class-id> test 2 48000
```

## Checking a plugin's lifecycle

`make -C native tests/lifecycle` builds a check that takes a real plugin
through the host's own backend, load, activate, audio cycles, the editor
and back, with no PipeWire graph and no daemon, and reports how any child
process (a Wine plugin host) ended. It takes the same plugin arguments as
the helper:

```sh
PATH="/usr/lib/openxlr/yabridge:$PATH" \
  native/tests/lifecycle vst3 "$HOME/.local/share/openxlr/yabridge/vst3/Plugin.vst3" <class-id> \
  --cycles 60 --editor-at 10
```

For a custom companion, replace `/usr/lib/openxlr/yabridge` with the selected
bridge directory reported by `getPluginSetup`. Run under `xvfb-run -a` to
keep the test editor off your desktop.

`--channels N` sets the chain width (2; 1 is the XLR inputs' width, and a
plugin that reports stereo is asked for a mono arrangement the way the
host asks), `--frames N` the cycle length (2048), `--tone HZ` the test
tone (440), `--set SYMBOL=VALUE`, repeatable, moves a control before the
first cycle so the printed peaks show the plugin acting on the signal
rather than passing it through at its defaults (VST3 symbols are the
parameter ids the scanner prints), `--wait-before-activate MS` and
`--wait-after-activate MS` add pauses that tell a time-based failure from
one a call causes, `--editor-at N` opens the plugin's real editor on
`DISPLAY` at that cycle and closes it ten cycles later (use `xvfb-run -a`
to keep it off the desktop), and `--reactivate-at N` switches the plugin
off and on again. It exits 0 with `PASS` when every step completed and the
plugin unloaded; a plugin whose bridge process dies takes the check down
with it, which is the point. The check does not reproduce the daemon's
separate audio thread; thread-sensitive behaviour still needs a live insert.

This is how the Elgato EQ 1.3.0 failure was found: the plugin answered
audio, then its Wine process exited with status 255 (a pure virtual call)
about 20 ms after activation, every time the host had created and released
a view before activating. The host used to do exactly that while loading,
to learn whether the plugin had an editor. It now assumes one and finds
out when the editor is opened; the check with `--editor-at` shows the
editor itself is fine, and `--reactivate-at` after it shows the plugin's
limit, which the daemon never reaches because it activates once per
process.

It is also how the Elgato effects' mono support was measured before the
catalogue started reporting `widths`. Each of EQ 1.3.0, Compressor 1.0.1,
Noise Removal 1.1.2 and De-Esser 1.0.1 scans as stereo, takes a mono
arrangement at `--channels 1` (the trace shows one channel per main bus,
arrangement `80000`), and at defaults passes a 440 Hz tone at its input
peak apart from its own transient or latency. With a control moved the
mono output follows it: the compressor's threshold at 0 takes the peak
from 0.250 to 0.004 without make-up gain, the EQ's first band at its top
frequency cuts the tone to 0.000, the noise remover's VAD threshold at 1
gates it to 0.000 after a few cycles, and the de-esser's first band on a
6 kHz tone (`--tone 6000`) with its frequency at 0.3, threshold at 0 and
ratio at 1 takes it from 0.234 to 0.034; the same runs at `--channels 2`
give the same figures. A stereo-only plugin asked the same way, Dragonfly
Plate Reverb, is listed with `widths` of 2 alone.

## Sound Check

`openxlr-lv2-host soundcheck NODE 1 RATE` runs the Sound Check loop instead
of a plugin, with the same ports, pipe and supervision as an insert. It is
mono only and allocates ten seconds of samples at the graph rate when it
starts; the audio callback does not allocate, lock or touch files. The
`command` control selects live (0), record (1) or loop (2), and the output
meters `frames` (samples recorded) and `mode` (what the last cycle did)
report back. The audio thread is the only writer of the samples and of both
meters. It sets `command` back to live when the buffer is full; the main
thread does the same when a recording request has gone ten seconds without
a cycle serving it, so a suspended input cannot start recording long after
the request. The test programs that compile `host.c` into themselves do not
link `sound-check.o`; to them `soundcheck` is an unknown backend.

## Microphone EQ

`openxlr-lv2-host eq NODE 1 RATE [SYMBOL=VALUE ...]` runs the microphone EQ:
four biquads in cascade with the coefficients of the Audio EQ Cookbook, mono,
with the same ports, pipe and supervision as an insert and no algorithmic
latency. Its controls are `enabled` and, for N from 1 to 4, `bandN_enabled`,
`bandN_type` (0 low cut, 1 low shelf, 2 bell, 3 notch, 4 high shelf, 5 high
cut), `bandN_frequency` (20 to 20000 Hz), `bandN_gain` (-12 to 12 dB) and
`bandN_qlog10` (-1 to 1, log10 of Q). The daemon starts it with every value
on the command line and changes them with `set` lines.

The audio thread reads the controls at the start of a block. A frequency,
gain or Q change recomputes that band and keeps its filter state, which is
what keeps a drag free of clicks. Every band's filter runs whether the band
is on or not, and the cascade runs while the EQ is off, so switching a band
or the EQ is a 5 ms crossfade between two signals that already exist. A type
change runs the new filter over the band's last 8192 input samples first,
then fades it in over 5 ms while the old filter fades out. The filters run in
double precision, and a filter whose whole state falls below 1e-25 is set to
rest, so a decaying tail never reaches subnormal numbers. The test programs
that compile `host.c` into themselves do not link `eq.o`; to them `eq` is an
unknown backend.

## Scope

For the plugin: URID map and unmap, the worker extension, options, and the
promise that the block length is bounded. The worker runs on a thread of its
own, fed by two lock-free rings, so a plugin that hands off heavy work never
does it in the audio callback; its answers are delivered before the next run.
That is what reverbs and convolvers ask for, and without it they could not be
hosted at all.

For the editor: instance access, parent, resize and the idle interface.
The host calls a UI's LV2 resize interface when present and forwards the
child window's minimum and maximum dimensions to the outer window. Changed
size hints are read again, so a plugin's scaling controls can update them.
LV2 editors do not receive the Wine coordinate nudge.

Before loading a plugin, the helper defaults `LSP_WS_LIB_GLXSURFACE` to `0`
unless the user already set it. LSP's software renderer avoids the repaint
freeze reproduced with its OpenGL renderer during large continuous resizes.
The setting is specific to LSP and applies to its editors in every hosted
format. Set it to `1` in the daemon environment to test OpenGL again.

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
plugin is assumed to have one, and the host too creates no view before it
is asked to open the editor, when a plugin without one says so. The
scanner does ask each plugin which chain widths its main buses take, one
and two channels being the widths the host carries: the default width the
fresh instance reports counts as accepted, and the other is requested
through `setBusArrangements` and confirmed through the bus info, which is
what the host does at load for the chain's own width. Asking changes the
instance, so each question goes to one in the state a load finds it: the
description is read first, the first question goes to that still unasked
instance, and a plugin whose default is neither width gets a fresh
instance for the second. The answer is the plugin's `widths` in the
catalogue, so a plugin that reports stereo and accepts mono, as the
Elgato effects do, is offered on the mono inputs, and one that refuses
both widths is offered nowhere instead of failing at load. The
interface headers are vendored under `vst3/` (MIT, VST 3.8.1); only their
inline parts are used, so nothing of the SDK is compiled. Windows VST3
plugins arrive through yabridge as ordinary bundles. The optional
[OpenXLR companion](../packaging/yabridge/README.md) supplies matching
64-bit libraries and host with the Wine input fix, selected through the
helper's PATH. Wine and plugin settings remain in the user's environment.

Editor X events wake the main loop directly; the 30 Hz tick remains for
idle work and the Wine coordinate workaround. During a frame resize, the
VST3 backend checks the plugin's size constraints before notifying its
view, and the embedding window follows after that callback. A VST3 view
that refuses border resizing gets equal minimum and maximum dimensions;
its own resize requests update those bounds, so plugin-driven UI scaling
still works. Consecutive
duplicate sizes are skipped, but reversing a drag delivers the old size
again. Plugin resize requests and child-window echoes remain suppressed
while the user is resizing the frame.

State and presets, and VST2, are separate work. Changing an insert's host
rebuilds its chain; nothing here swaps a plugin without a gap.
