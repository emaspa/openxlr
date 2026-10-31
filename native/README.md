# Native plugin host

This directory builds `openxlr-lv2-host`, a small process that loads one
plugin, LV2, CLAP or VST3, gives it PipeWire ports and, on request, opens
the plugin's own X11 editor on that live instance. `host.c` is the
process: the audio node, the command pipe, the window and the threads.
`lv2.c`, `clap.c` and `vst3.cpp` are the formats behind one interface. It exists because a plugin's editor talks to its
DSP instance directly (LV2 instance access), which a PipeWire filter chain
cannot offer.

The distribution packages build and install it, and its presence changes
nothing for existing LV2 inserts: they use filter-chain until explicitly
switched in the controls window. CLAP and VST3 always use this helper, and
so do OpenXLR's own stages: Sound Check, the level meter, the microphone EQ
with noise removal and the voice changer, ducking and the soundboard
player, each described below.

## Build

An ordinary .NET build does not invoke a C compiler. Opt in with:

```sh
dotnet build src/OpenXLR.slnx -c Release -p:EnableNativeLv2Host=true
```

The flag compiles this directory and copies the helper next to the daemon,
which is what the packaging recipes do. `make -C native` builds it alone.
It needs a C11 and a C++ compiler, make, pkg-config and the development files for
PipeWire, lilv, LV2, X11 and libsndfile; the libraries it links are already
runtime dependencies of every package. DeepFilterNet, one of the two
[noise removal](#noise-removal) engines, also needs cargo and rustc and,
the first time, network for its crates and model; `DEEPFILTER=0` builds
without it.

`make -C native test-editor` runs the host's resize regression tests on
`DISPLAY`, using a small test editor and a separate X connection. On a
machine without a desktop, run `xvfb-run -a make -C native test-editor`.
These tests need neither a plugin installation nor a running PipeWire
server. They cover event delivery without idle polling, coalesced sizes,
drag reversal, size constraints, changes to child-window size bounds,
fixed-size editors and plugin-driven scaling, the Wine coordinate nudge
and display cleanup. Real plugin repainting and mouse input still need
desktop testing.

`make -C native test-audio` checks the audio thread's safety rules
without a graph: a cycle the fixed buffers cannot carry is refused before
anything is written, a plugin stuck inside one process call is told apart
from a node that has no work, and invalid output is silenced, counting
toward a restart only on sound and only after half a second of it. `make -C native test-clap` checks the
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
old one, the ten-second bound in samples and in time, that only the audio
thread reports the mode, the raw switch's crossfade onto the tap and back,
the loop's crossfade where it starts over (a take whose end is far from its
start loops with no step larger than its own),
and the take's peaks over its recorded length, counted once per take. It
drives the level backend the same way: peak and RMS of a steady signal, the
peak falling back, silence reaching the floor and a block at full scale
counting a clip. It needs no PipeWire, display or plugin.

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
daemon creates one per mix while ducking runs in software with at least
one ducked cell; see docs/architecture.md.

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
unconnected filter, including no key or tap port for existing backends, a
silent fallback for a missing key buffer and a spare of its own for a
missing tap buffer.

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
meters `frames` (samples recorded), `mode` (what the last cycle did) and
`cursor` (where the loop is, 0 unless it loops) report back. The audio
thread is the only writer of the samples and of these meters. It sets
`command` back to live when the buffer is full; the main
thread does the same when a recording request has gone ten seconds without
a cycle serving it, so a suspended input cannot start recording long after
the request.

Where the loop starts over, the last 50 ms of the take fade out linearly
while its first 50 ms fade in under them, and the next pass carries on
from 50 ms in, so the wrap has no step to click. The first pass starts at
the true start of the take. A take shorter than 0.2 seconds fades over a
quarter of its length, and one of fewer than four samples does not fade.
The two gains sum to one, so a signal both ends share, such as a hum or
an offset, keeps its level through the fade.

Beside `capture_0` the backend has a second mono output, `tap_0`, which
the daemon links straight to the channel past its chain. The `raw` control
(0 or 1) moves the signal from `capture_0` to `tap_0` and back with a
linear crossfade of 20 ms; the two outputs always sum to the signal, and
each settles exactly on the signal or on silence. While recording, the
audio thread keeps the peak of each of 2000 stretches of the buffer. When a
recording ends with samples in it, the audio thread freezes its 200 peaks
and completed length in a bounded snapshot, separate from the next recording's
bins. The main thread copies a consistent snapshot to `p0` to `p199` and
`takeFrames`, then sets `take` to the count. A completion overlapping that
copy is retried at the next tick; the audio thread never waits for it.
Meters are printed in the order they were added, so the peaks and completed
length come before the count. The managed reader stages changed take meters
and publishes their snapshot only when that trailing count arrives. Readers
keep the previous completed take while the next one's lines are still in
transit; unchanged peaks retain their previous values. A later recording
cannot erase an unpublished waveform or change its duration. The test programs that compile `host.c` into themselves do not
link `sound-check.o`; to them `soundcheck` is an unknown backend.

## Level

`openxlr-lv2-host level NODE 1 RATE` measures a microphone before its
chain, for Mic setup. The daemon links it from the channel's capture port
beside the channel's own route; its output carries silence and is left
unlinked. Once per block the audio thread writes three meters: `peak`, the
sample peak in dBFS, falling back 20 dB a second, `rms`, the RMS in dBFS
with a 300 ms time constant, both floored at -120, and `clips`, a count of
the blocks that held a sample at or above -0.1 dBFS. A decaying value
below the floor goes to zero rather than into subnormals. Like the other
built-in stages, it lives in its own object, `level.o`.

## Microphone EQ

`openxlr-lv2-host eq NODE 1 RATE [SYMBOL=VALUE ...]` runs the microphone EQ:
four biquads in cascade with the coefficients of the Audio EQ Cookbook, mono,
with the same ports, pipe and supervision as an insert and no algorithmic
latency. Its controls are `enabled` and, for N from 1 to 4, `bandN_enabled`,
`bandN_type` (0 low cut, 1 low shelf, 2 bell, 3 notch, 4 high shelf, 5 high
cut), `bandN_frequency` (20 to 20000 Hz), `bandN_gain` (-12 to 12 dB) and
`bandN_qlog10` (-1 to 1, log10 of Q). The daemon starts it with every value
on the command line and changes them with `set` lines.
`openxlr-lv2-host eq-bands COUNT NODE 1 RATE` runs the same stage with 1 to
16 bands, N running from 1 to the count; `eq` is the four-band form, kept
for older daemons.

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

## Noise removal

The same stage removes noise before its EQ (`denoise.c`), so the EQ, the
chain and every mix hear the cleaned voice. Its controls sit beside the
EQ's: `denoise_enabled`, `denoise_engine` (0 RNNoise, 1 DeepFilterNet) and
`denoise_strength` (0 to 100 dB, the most the noise is turned down, 100
unlimited). It reports four meters: `denoise_active`, the engine heard
now (-1 none), `denoise_status`, why one asked for does not run (0 it runs,
1 the graph is not at 48 kHz, 2 this build lacks the engine, 3 the engine
could not start, 4 it stopped or fell behind), `denoise_late`, the
blocks DeepFilterNet answered late, and `denoise_latency`, its delay in
samples. The stage's `latency` is that delay plus the voice changer's.
Both engines work at 48 kHz on mono audio; at any other rate the stage
passes the microphone through untouched and says so.

- **RNNoise** 0.2 (Xiph, BSD-3-Clause), vendored under `rnnoise/` with its
  `COPYING` and its model, and always built in. It runs on the audio thread
  in 480-sample frames and allocates nothing per frame; on x86 it picks
  SSE4.1 or AVX2 code at run time. It answers a frame two frames after it
  takes it in, and the stage collects a frame first, so it adds 1440
  samples, 30 ms. Strength mixes the input back in under its output at that
  level, aligned to the same delay.
- **DeepFilterNet** 3 (MIT or Apache-2.0) from `deepfilter/`: a Rust
  static library, `openxlr-deepfilter`, that wraps libDF 0.5.6 (vendored
  under `deepfilter/libdf/` with its licences; the manifest keeps only the
  `tract` feature, and the source carries one later upstream change, which
  zeroes a quiet hop only once more than five came in a row instead of each
  lone one) behind a C interface that catches every panic, and carries the
  model inside it. It runs the model with the thresholds of DeepFilterNet's
  own C API after 0.5.6 (-15, 35 and 35 dB of local SNR) rather than the
  library defaults (-10, 30 and 20), under which a voice rated between 20
  and 30 dB got the ERB gains without deep filtering and lost parts of
  words; the model rates a clean voice lower the longer it runs, so after a
  minute or two much of it landed there.
  Its inference allocates, so it runs on a worker thread of its own: the
  audio thread writes into one ring, wakes the worker with a semaphore and
  reads the other ring a lag behind what it wrote, on top of the model's
  own 1440 samples. The worker hears a hop only when the block that
  completes it arrives, so the lag is the 480 samples of the hop it collects
  plus answering time in whole blocks. That is one block, or enough blocks
  to make up a hop when blocks are smaller. The total is 51 ms at a quantum
  of 256, 61 ms at 1024 and 83 ms at 2048. The lag grows at once with a
  larger block and shrinks once smaller blocks have held for 2 s after the
  last block needing the larger lag, with a
  5 ms crossfade between the two read positions. The latency it reports
  follows it. Strength is
  the model's attenuation limit; below 0.01 dB the library would hand the
  input back without its delay, so the wrapper never goes lower. A hop the
  worker has not answered in time plays the delayed input instead and counts
  in `denoise_late`; a worker that fails or falls a ring behind turns
  DeepFilterNet off until it is asked for again.

Changing what is heard, on, off or the other engine, fades out over 5 ms
and in over 5 ms: the engines are late by different amounts, so their
outputs cannot be crossfaded. Until an engine has heard its own delay's
worth of input, the input delayed to match plays in its place. A change to
DeepFilterNet loads the model on the main thread, which takes a moment, and
keeps what played before playing until the worker is there; the main thread
hands workers over through one atomic slot and frees one only after the
audio thread has handed it back. It keeps ownership while the audio thread
borrows the worker, so a return during a tick cannot start a second model
load. A returned worker is reused or stopped before another is loaded.

`make` builds DeepFilterNet in when cargo is on the `PATH` (`DEEPFILTER=auto`);
`DEEPFILTER=1` requires it and `DEEPFILTER=0` leaves it out, and every package
sets 1. The model is fetched once from the DeepFilterNet 0.5.6 tag and
checked against its SHA-256 (in the Makefile); a build without network puts
the same file at `deepfilter/model/DeepFilterNet3_onnx.tar.gz` first and the
crates under `deepfilter/vendor/`, where cargo reads them offline.
`DEEPFILTER_LIB` names a library built elsewhere. `openxlr-lv2-host engines`
prints the engines a build carries as one line of JSON,
`{"denoise":["rnnoise","deepfilternet"],"rate":48000}`, which the daemon reads
to know what to offer.

`make test-denoise` drives the stage with no audio server: a formant
synthesiser speaking syllables in steady noise. For each engine it checks
that the noise between syllables drops (RNNoise by 10 dB or more,
DeepFilterNet by 15), that the syllables keep their level within 6 dB, and
that the output lines up with the voice at the latency the stage reports;
that strength 0 gives the delayed input back and a limit holds; that RNNoise
gives the same output for blocks of any size; that switching on, off and
between engines dips a steady tone for no more than 6 ms; that a graph at
44.1 kHz passes through with its status; that DeepFilterNet in rooms 10
and 30 dB quieter loses under 8 dB from any syllable and still takes the
noise down by 10 dB; and that the `engines` line matches the build. `./tests/denoise ./openxlr-lv2-host --bench` also prints
each engine's processing time.

The same target runs `tests/denoise-handoff` with a fake model: a worker
returned during a tick, repeated switches while it is borrowed, and a
retry after loading fails. All created models must be freed at unload.

## Voice changer

The same stage ends with a voice changer (`voice.c`), after noise removal
and the EQ, so the EQ shapes the human voice and the character goes on
last. Its controls sit beside the others: `voice_enabled` (0 or 1),
`voice_preset` (0 to 10, below) and, for Custom only, `voice_pitch` and
`voice_formant` (-12 to 12 semitones). It reports three meters:
`voice_active`, the voice heard now (-1 the dry voice),
`voice_status`, why a voice asked for does not run (0 it runs, 1 the
pitch shifter could not start, at a rate outside 16 to 192 kHz or without
memory, 2 a voice put out something that is not a number and was cut off
until a voice is asked for again), and `voice_latency`, its delay in
samples, 0 while the dry voice plays.

| preset | voice | what it does |
|---|---|---|
| 0 | custom | pitch and formant from the two controls |
| 1 | deep | pitch -4, formant -3, a 2 dB low shelf at 180 Hz |
| 2 | high | pitch +5, formant +4 |
| 3 | robot | ring modulator at 55 Hz and a metallic 7.5 ms comb |
| 4 | radio | 300 Hz to 3.4 kHz band, a mid lift and light saturation |
| 5 | alien | pitch +3, formant +5, light ring modulator, a swept flanger |
| 6 | dracula | pitch -3, formant -2, a large hall |
| 7 | frankenstein | pitch -6, formant -4, saturation, dark top |
| 8 | mummy | pitch -2, raspy band and saturation, a long tomb |
| 9 | werewolf | pitch -5, formant -3, slow ring modulator roughness, growl |
| 10 | witch | pitch +4, formant +3, a 6.5 Hz quiver, a small room |

Each voice is a row of the table in `voice.c` over the same blocks, in
this order: the pitch and formant shift, a ring modulator, a comb with
feedback (fixed or swept), tanh saturation, up to four biquads from
`eq_coefficients`, a mono Freeverb (eight combs and four all-passes, its
public-domain tuning) and a gain that brings the voice back near the input's
level. A voice that shifts nothing runs through a plain delay as long as the
shifter's, so every voice is equally late. A soft ceiling above 0.9 keeps
the output below full scale. Formant shifts are relative to the input's own
formants, so pitch alone keeps the speaker's vowels.

The shifter is Signalsmith Stretch, configured for live speech instead of
its 100 to 120 ms presets: a 1600-sample block (33.3 ms at 48 kHz, and the
same 1/30 s at other rates) analysed every 5 ms. Its delay is its block,
1600 samples at 48 kHz, which the stage adds to its `latency` while a voice
runs. 1440 samples (30 ms) was tried first. Stretch pads 1600 to a
2048-point transform but 1440 only to 1536, and on recorded speech Deep's
autocorrelation peak, a measure of how much of the harmonics survive, rose
from 0.54 at 1440 to 0.59 at 1600 (the dry voice scores 0.67). Blocks of
2160 and 2880 reached 0.60 and 0.61. Measured on an AMD Ryzen 9 9950X3D at
48 kHz in blocks of 256, a voice that shifts costs about 1.1 to 1.2
percent of one core, robot and radio about 0.1 percent, and the dry voice
nothing.

Two slots each hold a shifter and the blocks' state, all allocated at
load, so nothing allocates or locks on the audio thread. A new voice starts
in the free slot, hears one shifter delay of input while what played keeps
playing, and then the two crossfade over 20 ms at equal power. Turning the
changer off crossfades the voice to the dry input; after that the stage
copies its input to its output, bit for bit, and reports no delay. A second
switch waits for the first to finish. Custom's pitch and formant follow
their controls without a fade, at the shifter's next 5 ms step; the witch's
quiver moves the pitch once a block.

The pitch shifter is vendored under `signalsmith/`, both parts MIT with
their licences beside them, unchanged from their tags:

- Signalsmith Stretch 1.4.0
  (<https://github.com/Signalsmith-Audio/signalsmith-stretch>), tag
  `1.4.0` at commit `a670068d9aeb64913331d5cc29337b19a457a7df`:
  `signalsmith-stretch.h` (the header under `include/` only includes this
  one; its own version constant still reads 1.3.2) and `LICENSE.txt`.
- Signalsmith Linear 0.6.4 (<https://github.com/Signalsmith-Audio/linear>),
  the tag Stretch's CMakeLists fetches, at commit
  `de55e6a50ffcf6f8f43f649692d94691c7025151`: `stft.h`, `fft.h` and
  `LICENSE.txt` under `signalsmith/signalsmith-linear/`. The rest of that
  repository is used only behind `SIGNALSMITH_USE_*` switches this build
  does not set.

SHA-256 of the vendored files:

```
1188667959ac19dd40c0a6abbce694e44705615ec4f0e8db8af0f1cfb4c5dea7  signalsmith/signalsmith-stretch.h
ee2ef82481ffb445ecdd4b3a4c1f82c0cddb2da8fe39d8e8dc384fffb3e7f06f  signalsmith/LICENSE.txt
7422ff5ddc2a017dc0587dcce99ce5ba30a2414bc2996081f6b00d56905d9b8c  signalsmith/signalsmith-linear/stft.h
fb55f69ac14cdbac66b9c76053069ff63bc36d18af20d8c028b9acc0aad6516a  signalsmith/signalsmith-linear/fft.h
072b5e9eb5b22880bdf6256324654cf3ce53dba828edb911921121e1be09d9c3  signalsmith/signalsmith-linear/LICENSE.txt
```

`voice-stretch.cpp` puts a C interface in front of it and is compiled with
the flags `vst3.cpp` uses (C++17, no exceptions, no RTTI); the headers come
in through `-isystem`, so this project's warnings do not apply to them.
Stretch allocates only when it is configured, at load; the wrapper seeds
its random generator with a constant instead of `std::random_device`.

`make test-voice` drives the stage with no audio server, counting every
allocation made while it processes a block (there must be none). It checks
the controls and meters against the names above; that off passes the input
through bit for bit with no delay; that Custom at 0 and 0 lines up with the
input at the reported latency; that every voice over a synthetic voice stays
finite and below full scale, comes out within 4 dB of the input's level,
dies away to below -100 dBFS within 5 s of the voice stopping, and turns
silence into exact silence; that switching through every voice, on and off,
on a steady buzz never makes a larger sample-to-sample step than either
voice makes on its own, and ends bit-exact when it ends off; and that
44.1 kHz keeps the delay while 8 kHz stays dry with status 1. It prints each
voice's latency, CPU and level. `./tests/voice --render IN.wav DIR` runs
every voice over a recording and writes `DIR/00-dry.wav` to
`DIR/11-witch.wav` with each one's level against the input, for tuning the
table by ear.

## Soundboard player

`openxlr-lv2-host player NODE 2 RATE [SYMBOL=VALUE ...]` plays soundboard
clips. It holds up to 64 decoded clips in memory, one per slot, and mixes
up to 8 voices into one stereo output, `capture_0` and `capture_1`, which
the daemon links into a channel. Its inputs, `playback_0` and `playback_1`,
stay unlinked and are not heard. A press is one line on the command pipe;
nothing in the graph changes. It refuses any channel count other than 2.
PipeWire runs the node only while its outputs are linked into a running
graph, and a press sent before then waits in a queue of 256 lines. A press
the audio thread reaches more than a quarter of a second after it was sent
is dropped, so presses that waited for a link never sound late, all at once.

Beside the main pair the player has an audition pair, `audition_0` (FL)
and `audition_1` (FR), two mono taps the daemon links into the monitor
mixes only. One audition voice plays there, outside the 8 voices. It is
never mixed into `capture_0` and `capture_1`, and a press never steals it.
`restart`, `toggle` and `release` act on the 8 voices alone. An audition
plays its clip once from the start at the slot's gain, whatever the slot's
loop, and a second audition fades the first out under it, of whichever
slot. A stop of its slot, `stop all`, `stop audition` and a new clip in its
slot end it with the usual fade. An audition the audio thread reaches more
than a quarter of a second late is dropped like a press.

A linked audition pair keeps the node running when the main pair's link is
gone. In a cycle where PipeWire gives neither main output a buffer, so that
nothing reads them, the player stands still as an unlinked node would. It
takes nothing from the queue, moves no voice, the audition's included,
and writes silence. Presses sent meanwhile wait and go stale as before,
instead of playing unheard and surfacing mid-clip once the link is back.

Each slot has two controls and three meters, for N from 0 to 63:

| Symbol | Range | Default | Meaning |
|---|---|---|---|
| `gain_N` | 0 to 1 | 1 | The clip's gain, unity at 1 |
| `loop_N` | 0 or 1 | 0 | Start over at the end instead of stopping |
| `playing_N` | 0 to 8 | 0 | Read-only: voices now playing the slot, not counting ones fading out |
| `pos_N` | 0 to 1 | 0 | Read-only: how far through the clip the newest of them is |
| `ended_N` | 0 to 16777215 | 0 | Read-only: the newest numbered press on the slot whose `hold` voice has ended, together with every earlier press's; 0 before the slot took a numbered press |

One more meter is the player's, not a slot's:

| Symbol | Range | Default | Meaning |
|---|---|---|---|
| `audition` | 0 to 64 | 0 | Read-only: the slot the audition voice plays, plus one; 0 while none plays. `playing_N` and `pos_N` never count it |

`set SYMBOL VALUE` moves a control as for every backend, and both apply to
voices already playing. The other lines are the player's own:

| Line | Does |
|---|---|
| `load SLOT TOKEN PATH` | Decode the file into the slot, replacing what it held. Answered with one line that repeats TOKEN |
| `unload SLOT` | Empty the slot, and drop a load for it still decoding. Answered with `clip SLOT unloaded` |
| `play SLOT [MODE [PRESS]]` | `overlap`, the default, starts another voice; `restart` fades out the slot's voices and starts one; `toggle` fades them out when one plays, else starts one; `hold` starts one that `release` ends. `PRESS` numbers the press for `ended_N` |
| `release SLOT` | End the slot's `hold` voices |
| `audition SLOT` | Play the slot's clip once on the audition pair, in place of the audition playing |
| `stop SLOT`, `stop all`, `stop audition` | Fade out the slot's voices and its audition, every voice and the audition, or the audition alone |
| `probe PATH` | Check a file as `load` would and keep nothing. Answered with one line |

`SLOT` is 0 to 63. `TOKEN` is 0 to 4294967295, chosen by the sender and
repeated in the answer, so an answer that comes after its sender gave up
waiting is never taken for a later load's. `PRESS` is 1 to 16777215, the
largest whole number a float meter holds exactly; the daemon counts its
presses in the order it sends them and starts over at 1 past the largest.
A hold voice has ended once it was released, stopped, stolen or replaced,
or once its clip played out. A press that started no voice, such as one
dropped for coming late or a toggle that stopped the slot, has ended once
the audio thread took it. Hold voices of a slot end in the order they
started. While one plays, `ended_N` is the press before the oldest of them;
with none playing it is the slot's last numbered press. Turning loop off
under looping voices can end a newer one first, and then `ended_N` reports
it late, never early. The audio thread computes the value from the same
block as the voices, so it never reports a press it has not reached yet.
The daemon reads it to tell a key held past the end of its clip from one
whose voice still plays. `PATH` is the rest of the line,
spaces included. A load is answered with `clip SLOT TOKEN ok FRAMES
CHANNELS`, the length at the helper's rate and 1 or 2, or with `clip SLOT
TOKEN error MESSAGE`, after which the slot is empty. One load runs at a
time: another sent meanwhile is answered at once with `the player is still
loading another clip; try again`, and a load the slot's `unload` dropped is
answered with `the load was cancelled`. A probe is answered with `probe ok FORMAT CHANNELS RATE FRAMES`, the
file's own rate and length, where `FORMAT` is `wav`, `flac`, `ogg`, `opus`,
`mp3`, `aiff` or `other`, or with `probe error MESSAGE`. A message is the
helper's own sentence, such as `the clip is 31.0 s long; the limit is 30 s`,
never the decoder's. The player reports a malformed line, a press on an
empty slot and a press that finds the queue full on stderr, in a line
starting `note: `, and changes nothing. The daemon never takes such a line
for the reason the helper stopped.

`openxlr-lv2-host probe PATH` prints the same probe line from a process of
its own and exits 0 on `ok`, 1 on `error`, so the daemon can check a file
before any player holds it. `openxlr-lv2-host formats` prints the formats
this build's libsndfile decodes, as one line of JSON,
`{"formats":["wav","flac","ogg","opus","mp3"]}`; MP3 needs libsndfile 1.1
or newer built with it.

The player treats clip files as untrusted and checks every limit before
it asks for the memory:

- the path names a regular file of at most 64 MiB. A pipe or a device is
  refused without being read;
- the file is mono or stereo, at 8000 to 384000 Hz;
- the clip is at most 30 s long. A header that gives a longer length is
  refused at once, and when the header gives none, decoding stops one frame
  past the limit;
- every clip in memory takes at most 96 MiB together: the clips in slots,
  the clip a load would replace, and clips still read by a fading voice or
  on their way to be freed. A slot near the limit is emptied, and its clip
  freed once its voices have faded, before a large clip replaces it.

Each limit is one constant at the top of `player.c`. Decoding runs on a
worker thread of its own with libsndfile, from a descriptor it opened itself,
so the library never opens a name. Non-finite samples become silence and
the rest is held to full scale. A clip at another rate is converted once,
when it loads, with a Blackman-windowed sinc of 16 zero crossings each side,
cut off at 94 % of the lower Nyquist frequency. A thirty second
stereo clip at 44.1 kHz loads in about a tenth of a second, and one at
384 kHz in about a second. Every other line is taken while it does: a stop
during a load is heard at the next block. The answer is printed at the main
thread's next tick, within 33 ms of the decode's end, and the slot's meters
are printed again then, whatever they were. libsndfile's decoders run in
this process, so a file that crashes one ends the helper. The daemon should
therefore probe a file with `openxlr-lv2-host probe` before a player loads
it. libsndfile also leaks a few kilobytes each time a damaged Ogg file
fails to open, which a one-shot probe leaves behind with its process.

Every start fades in over 5 ms and every stop, release or replacement fades
out over 5 ms, so a restart is a crossfade and a stop never clicks. A clip
that ends by itself fades over its last 5 ms, or its last quarter when that
is shorter. A gain change moves at the same pace. Where a loop starts over,
the end fades into the start over 20 ms, or a quarter of the clip, as Sound
Check's loop does. A pass decides whether it wraps where those fades
begin, so turning `loop_N` off lets the current pass play out. A ninth
voice fades out the oldest playing voice, of any slot, while it starts.
Voices fading out have eight places of their own beside the eight that
play, so a steal cuts nothing short unless eight are already fading.

The worker decodes and allocates the clip it decodes, and hands it to the
main thread under a lock, which the audio thread never takes. The main
thread owns every clip from then on and frees it. The audio thread owns the
voices and which clip each slot holds. Clips, presses and stops reach the
audio thread through one single-producer ring. Clips come back through a
second ring once no voice reads them, and the main thread frees them then.
The audio callback allocates nothing, takes no lock and touches no file.

`make -C native test-player` drives the backend at 48 kHz in blocks of 256
with no audio server, with clips it writes with libsndfile. It checks the
controls and limits; that the resampler keeps a 1 kHz tone within 0.1 dB
and 1 Hz from 44.1, 48, 96 and 22.05 kHz and takes a 40 kHz tone at 96 kHz
more than 60 dB down instead of folding it; WAV, FLAC, Ogg Vorbis, Opus and
MP3 loads where this libsndfile writes them, a path with a space, and
non-finite samples; the probe line through the pipe and from its own
process, and `formats`; refusals of a missing file, a directory, a pipe, an
empty file, text, a file one byte over 64 MiB, a clip one frame over 30 s,
three channels, 4000 Hz, a path that is too long, the memory budget and
malformed lines; 150 damaged and truncated copies each of a WAV, FLAC, Ogg
and MP3 file, each answered with one line and played with finite output; each
press mode, release, stop and stop all with no step larger than the ramp's;
the audition on its own pair and never on the main one, through every press
mode, a full pool and a steal, replaced by the next audition without a
step, ended by `stop audition`, a stop of its slot, `stop all` and a new
clip, played once whatever the loop, dropped when late and run with no tap
buffers; and the player standing still while its main pair is unread;
`ended_N` through a release, a stop, a toggle, a clip's own end, a dropped
press, an unnumbered press and the count starting over;
the voice limit and stealing across slots; a clip's end, a loop's wrap and
loop turned off on a running voice; a clip replaced or unloaded while it
plays, and freed once its voices have faded; a stop and a gain change taken
while a 30 s clip at 384 kHz decodes, and the output silent within two
blocks; a second load refused while one runs; an unload cancelling a load
still decoding; the token in every answer; and a press reached 300 ms late
dropped. Every process call counts the allocations made on its own thread
through a wrapped allocator and must make none; the worker's are its own.

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
