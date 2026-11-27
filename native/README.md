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
with noise removal, the expander, the compressor and the voice changer,
ducking, the soundboard player and a capture input's clock bridge, each
described below.

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

The Makefile hardens the helper and its tests with the stack protector,
`_FORTIFY_SOURCE`, a position-independent executable, full RELRO with
immediate binding and a non-executable stack, plus stack clash and
control-flow protection where the compiler has them. It adds these after
the caller's `CPPFLAGS`, `CFLAGS` and `LDFLAGS`, so a package keeps its
distribution's flags. It defines `_FORTIFY_SOURCE` only when those flags
and the compiler leave it undefined. `HARDENING=0` leaves every flag to the
caller. Make does not track flags, so run `make -C native clean-native`
after changing them.

`make -C native test-editor` runs the host's resize regression tests on
`DISPLAY`, using a small test editor and a separate X connection. On a
machine without a desktop, run `xvfb-run -a make -C native test-editor`.
These tests need neither a plugin installation nor a running PipeWire
server. They cover event delivery without idle polling, coalesced sizes,
drag reversal, size constraints, changes to child-window size bounds,
fixed-size editors and plugin-driven scaling, the Wine coordinate nudge,
a Wine window left unmapped in the frame being mapped again, and display
cleanup, and that a lost connection returns to the armed call
only on the main thread and ends the process on any other. Real plugin
repainting and mouse input still need desktop testing.

`make -C native test-audio` checks the audio thread's safety rules
without a graph: a cycle the fixed buffers cannot carry is refused before
anything is written, a plugin stuck inside one process call is told apart
from a node that has no work, and invalid input reaches the plugin as
silence and never stops the host. That holds for three seconds of it on an
in-place node and for single samples into the duck stage. Invalid output
is silenced, and counts toward a restart only on sound and only after half
a second of it. The same target runs `tests/own-block`, which drives the
[own block](#own-block) without a graph: the output is the input delayed
by exactly the reported delay, sample for sample; a block answered within
its time is never late and one answered later costs only its own late
frames; a 53 ms stall of the worker is at most that much silence and the
same alignment afterwards; a stall that fills the ring plays silence and
never older audio; the delay follows a changed cycle without playing a
frame twice; and with the real worker thread and the plugin held inside a
call, 2000 callbacks return at once. A last part runs four seconds in real
time against a plugin that takes 1 ms a call and once 53 ms. It also runs
`tests/measure`, which checks the [measured delay](#measured-delay) the same
way: a plugin that delays by 0, 480, 2400 or 24000 frames is measured to the
sample over a synthetic voice, in mono, in stereo and at 44.1 kHz; so is one
that gates the pauses like a noise remover; on its own block the figure is
the plugin's delay plus the block's, which is also what the `latency` line
says; a plugin that puts out silence, unrelated noise or noise that follows
the voice's level never gives a figure; and the audio callback allocates
nothing. `make -C native test-clap` checks the
CLAP port layout against fake plugins that declare more buses than the
chain uses, and that the scanner and the loader agree about which layouts
can be carried. Each port is read from the plugin once, so a plugin whose
answer changes after the channel bound was checked cannot widen a bus past
it. It also checks that a CLAP plugin's latency is read after
activation and again only after the plugin says it changed, and that the
host reports it once and then once per change. A plugin that asks for a
restart is restarted in place in the order CLAP sets: processing stops
before deactivation, the plugin is never called while inactive, and its
new latency is reported. The test runs that 500 times with the audio on a
thread of its own. Teardown checks use a private
event loop: a plugin may unregister timers and file-descriptor watches in
its destructor, and the host collects any remaining watches afterwards
without closing the plugin's descriptors. Neither test needs a PipeWire
server, a display or an installed plugin.

`make -C native test-lv2` loads the fixture in `tests/latency.lv2`, a stereo
delay whose latency port follows its delay control, through lilv and the LV2
backend, and checks that its latency is reported once audio has run and again
when the delay changes. A plugin with a port typed as more than one of
audio, control and atom is refused at load, the way any other unusable
port is, with the port named on stderr. It builds the fixture itself and
needs lilv, but no PipeWire, display or installed plugin.

`make -C native test-vst3` checks VST3 parameter queue indices, attribute
identifiers and buffers, the component-state memory stream, and that the
plugin is asked for its latency at activation and after `kLatencyChanged`
only, however often the host reads the figure. A plugin that asks to be
reloaded (`kReloadComponent`, or `kIoChanged` with a new bus layout) is
reloaded in place in the order VST3 sets: the audio thread lets go before
the plugin is touched, processing stops before deactivation, setup and bus
arrangements happen while inactive, the plugin is never processed while
inactive, a parameter edit made meanwhile reaches it afterwards, and its new
latency is read. A request made during the activation that answers one is
absorbed, and a plugin that will not activate again fails the helper
without being called. The test runs 500 reloads with the audio on a thread
of its own. Invalid
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
CLAP plugin and five per VST3 audio class, plus four per module. Each of
those plugins also has a parameter with a NaN bound, one with an infinite
default and one whose name is not UTF-8. The description must still
parse, only the first two may be left out, and the name comes back with
U+FFFD. The same
script runs the LV2 scan over bundles with escaped and non-ASCII names,
an infinite range, a missing range, more ports than the limit, a Turtle
syntax error and bytes that are not UTF-8, and checks the JSON, the
plugins left out and one bundle marker per plugin. It needs
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
loud burst leaves no subnormal number and every filter at rest; that a NaN
and two infinities in the input leave the output, with the EQ on and off,
exactly as zeros there would, counted on `stage_nonfinite`; that a band
state set to NaN plays one sample dry and then matches a fresh EQ bit for
bit, counted on `stage_resets`; and prints what a sample costs.

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
- For an LV2, CLAP or VST3 plugin the helper also measures the delay its
  node really adds, and prints it as `meter host.measured_latency SAMPLES`
  once it is sure of a figure and again when it is sure of a different one;
  see [Measured delay](#measured-delay). The dot keeps the name apart from
  the plugin's own controls, since an LV2 symbol cannot hold one and CLAP
  and VST3 controls are named by number. The daemon reads it apart from the
  plugin's meters.
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

## Own block

```text
openxlr-lv2-host --own-block lv2 URI NODE CHANNELS RATE [SYMBOL=VALUE ...]
openxlr-lv2-host --own-block clap FILE ID NODE CHANNELS RATE [SYMBOL=VALUE ...]
openxlr-lv2-host --own-block vst3 BUNDLE CLASS-ID NODE CHANNELS RATE [SYMBOL=VALUE ...]
```

`--own-block` runs the plugin on a block of its own: the node keeps its
name and ports, so the daemon links it as before, but the plugin is called
on a worker thread with exactly 1024 frames at a time, whatever PipeWire's
cycle. It is for a plugin that cannot finish a short cycle in time. A
Windows plugin through yabridge pays a cost for each call that hardly
depends on its length; at a cycle of 256 one can miss its deadline again
and again, and an interface that drives the graph, such as a Wave XLR Pro,
restarts its playback each time. The built-in stages refuse the option.

The audio callback copies its input into a ring, wakes the worker with a
semaphore each time a block of 1024 is complete, and reads the worker's
output from a second ring a fixed delay behind its input. It never waits
for the worker. A frame the worker has not answered in time plays as
silence, and the answer for input frame n is always output frame n, so a
late block costs its own frames and moves nothing else. The worker drops a
block whose frames have all been played already instead of answering it
late, which puts the plugin back on time at once after a spike; the plugin
hears the jump. A worker stuck for most of a second fills the input ring,
and what the ring could not take is answered with silence, never with
older audio.

The delay is the block plus the larger of 512 frames and the cycle: 1536
frames, 32 ms at 48 kHz, at a cycle of 256 or less, and 2048 at 1024. A
block is complete at the end of a cycle and due when the read position
reaches it, so the worker has the delay less the block plus one cycle to
answer it: 768 frames, 16 ms, at a cycle of 256. A larger cycle lengthens
the delay at once, with silence in between; a smaller one shortens it,
skipping frames, once it has held for two seconds, so a cycle that changes
back and forth does not move the audio each time. The `latency` line is
the plugin's own figure plus the delay, or the delay alone while the plugin
gives none.

The worker is made real-time the way PipeWire makes its own data thread,
through the thread utilities the client context loaded, RTKit or the
process's own limits, at the priority configured for them. Where that
fails it runs at normal priority and says so in a `note:` line on stderr.
It is the thread the plugin is told is its audio thread, and the stall
monitor brackets its calls as it does the audio callback's. The plugin is
set up for the block: a VST3 plugin with a `maxSamplesPerBlock` of 1024, a
CLAP plugin activated for 1024 to 1024 frames, and an LV2 plugin given
1024 as its minimum, maximum and nominal block length. Its controls,
state, editor, latency reports and the main-thread rules above are as
without the option. The plugin's own bypass parameter keeps the delay; the
daemon's bypass takes the insert out of the chain and the delay with it.

With `OPENXLR_HOST_TRACE` set, the helper writes a `trace: own block:` line
once a second while either of its counts moves: the cycles that played
silence for a late block, and the blocks the worker dropped.

## Measured delay

A plugin declares its latency, and the declaration can be wrong. A plugin
bridged from Windows can report 0 and still hold the audio back by tens of
milliseconds. So for an LV2, CLAP or VST3 plugin the helper also measures
the delay from the audio itself (`measure.c`). It measures at the edges of
the audio callback, the input as it arrives and the output as it leaves,
so the figure holds everything the node adds, the [own block](#own-block)
included. The built-in stages report their own figures and are not
measured.

The audio callback copies a mono mix of its input and of its output into
two rings of about five seconds, 1 MB each at 48 kHz. That copy is all it
does: no allocation, no lock, no system call. Once a second of new audio
has arrived, the main-thread tick looks at the last three seconds of output
and the four seconds of input that can explain them:

1. Coarse. Both sides become envelopes, the RMS of each millisecond
   averaged over the eleven around it, and the output's envelope is
   correlated with the input's at every lag up to one second, with
   Pearson's coefficient. Envelopes survive what a noise remover does to
   speech, and the averaging removes the ripple of the voice's pitch
   without moving the peak.
2. Fine. Within 7 ms of the coarse peak the waveforms themselves are
   correlated, sample by sample, over the 250 ms where both sides are
   loudest. Its peak is the figure, to the sample.

A figure is reported only when all of these hold: at least 400 ms of the
window has signal above -50 dBFS on both sides; the envelopes correlate at
0.6 or more and no separate peak more than 25 ms away comes within 0.05 of
that; the waveforms correlate at 0.5 or more; neither peak is on the edge
of its search; and three analyses in a row agree to within two samples.
Nothing is printed before the first such figure, and a figure stays until
three agreeing analyses give another one. With speech going through from
the start, the first figure comes after about six seconds: four to fill the
window, then three agreeing analyses a second apart.

The thresholds come from `tests/measure.c` and from running RNNoise and
DeepFilterNet over the same synthetic voice. Unrelated noise reached 0.15 on
the envelopes, noise that follows the voice's level 0.999 on the envelopes
and 0.03 on the waveforms, and the two noise removers 0.98 or more on the
envelopes and 0.96 or more on the waveforms. RNNoise measured 1436 to 1439
frames against the 1440 it reports, DeepFilterNet 2432 against 2432. An
analysis takes about 3 ms of the main thread once a second, 0.3% of one
core, and the copy adds about 0.3 microseconds to a cycle of 256 stereo
frames.

## Ducking

The built-in `duck` backend applies one gain to a stereo bus, detected from
up to eight separate mono keys. It follows the loudest open key at each
sample, so two keys under the threshold never add up. It sums nothing and
adds no algorithmic latency. The
daemon creates one per mix while ducking runs in software with at least
one ducked cell; see docs/architecture.md.

```text
openxlr-lv2-host duck NODE 2 RATE [SYMBOL=VALUE ...]
```

`NODE` is the PipeWire node name. `RATE` is the fixed sample rate in Hz,
8000 to 384000; a changed rate or a quantum above 8192 frames is refused
by the host. The backend refuses any channel count other than 2. Its ports
are `playback_0` and `playback_1` for the bus inputs, `capture_0` and
`capture_1` for its outputs, with audio channels `FL` and `FR`. `key_0` to
`key_7` are inputs with audio channel `MONO`. All twelve carry 32-bit float
mono DSP buffers. An unlinked key reads silence. The other backends have no
key port.

| Symbol | Range | Default | Meaning |
|---|---|---|---|
| `enabled` | 0 or 1 | 0 | Duck while the key is above threshold |
| `threshold` | -60 to 0 dBFS | -12 | Key threshold |
| `depth` | 0 to 40 dB | 12 | Full reduction |
| `attack` | 1 to 5000 ms | 80 | Time from unity to full reduction |
| `release` | 1 to 10000 ms | 1200 | Time from full reduction to unity |
| `key_open` | 0 to 255 | 1 | A mask of the keys the detector hears, bit i for `key_i` |
| `reduction` | 0 to 40 dB | 0 | Read-only meter of the current reduction |

Initial inputs use `SYMBOL=VALUE` arguments. Each subsequent stdin line
`set SYMBOL VALUE` changes one input, read at the start of the next audio
block. The host clamps finite values to the ranges above and refuses
nonfinite values, unknown symbols and writes to `reduction`. `enabled`
treats values below 0.5 as off and values at or above 0.5 as on, and
`key_open` is rounded to the nearest whole number.
The host prints `meter reduction VALUE` on stdout when the value changes,
checked every 33.3 ms; the audio thread updates it at each block's end.
The ordinary `ready`, `heartbeat` and `ui-heartbeat` messages still apply.
`quit` or stdin EOF stops the helper. This backend has no editor.

For each sample, `k` is the largest `abs(key[i])` across the keys whose
bit is set in `key_open`, a sample that is not finite counting as zero,
and zero with no key open. The double-precision peak envelope follows
`env = max(k, env * exp(-1 / (0.050 * RATE)))`. It is above threshold at
`env >= 10^(threshold / 20)`. The target gain is `-depth` dB while enabled
and above, otherwise 0 dB. Gain moves linearly in dB toward that target,
down by `depth / (attack * RATE / 1000)` per sample or up by
`depth / (release * RATE / 1000)`. Both channels are multiplied by
`10^(gain / 20)`, exactly `1.0f` at zero gain, and `reduction` is `-gain`.
A depth of zero means unity. Disabling releases from the current gain;
closing every key lets the envelope decay before release starts. The
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
several keys (two under the threshold do not add up, the louder open key
ducks, a closed key is not heard),
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
  killed the process immediately. The return to the armed call happens only
  on the main thread, since that jump is defined only within the thread that
  set it. A plugin thread that
  loses a connection of its own ends the helper, and the daemon rebuilds the
  chain as it does after any other exit.
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
  the old one. A plugin that asks to be reloaded or reports a changed bus
  layout (`kReloadComponent`, `kIoChanged`) is deactivated and activated
  again in the same process, as described under VST3 below.

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
cut), `bandN_frequency` (20 to 20000 Hz), `bandN_gain` (-20 to 20 dB) and
`bandN_qlog10` (-1 to 1, log10 of Q). The daemon starts it with every value
on the command line and changes them with `set` lines.
`openxlr-lv2-host eq-bands COUNT NODE 1 RATE` runs the same stage with 1 to
16 bands, N running from 1 to the count; `eq` is the four-band form, kept
for older daemons.

`openxlr-lv2-host eq-bands COUNT NODE 2 RATE` is the same bands on a stereo
pair and nothing else, for an output mix's EQ and for a capture input on a
stereo pair: no noise removal, dynamics or voice changer and none of their
controls or meters. Each band has one set of coefficients and its own
filter state and history on each channel; a switch or a type change moves
on both channels at the same sample. Its trace line reads
`trace: stereo EQ: ...`.

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

A NaN or an infinity that reaches a filter's state stays there. Every later
sample comes out NaN, even with the EQ off, because its crossfade still
multiplies the cascade's output, and the host silences the stage until it
restarts. So the host replaces every non-finite input sample with 0 before
a plugin or a built-in stage hears it, on a copy of the block, and never
in PipeWire's buffer, which other nodes may read. It never stops for
them, however long they keep coming. They come from upstream, and a new
process would hear the same ones. The host counts them, and the main loop
writes `trace: N non-finite input samples replaced with silence` to stderr
when the count first moves and at most once every 10 seconds after that.
The microphone stage instead tells the host it guards its own input
(`guards_input` in `Backend`), and the host hands it the block as it came.
The stage replaces a non-finite input sample with 0 before noise removal,
the EQ or the voice changer hears it, on a copy of the block, never in the
host's buffer. If a state turns non-finite anyway, the part that holds it clears
it: the EQ resets every band's filter and plays that one sample dry, and
noise removal and the voice changer start their engines over as described
below. The host still checks the stage's output as it checks a plugin's.
Two meters count this: `stage_nonfinite`, the input samples replaced, and
`stage_resets`, the states cleared. While either moves, the stage writes
`trace: microphone stage: ...` to stderr at most once a second, which the
daemon passes to its journal.

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
  DeepFilterNet off until it is asked for again. A model that answers with
  something that is not a number is the exception: the stage plays the dry
  input while the main thread loads a fresh model, then switches back to
  it. That happens at most once in 10 s; a second such answer inside that
  time is handled like any other failure.

If RNNoise answers a frame with something that is not a number, the stage
sets it up again on the audio thread, which allocates nothing, and the
delayed input plays until it has heard its delay's worth again, as after a
switch. RNNoise keeps a frame of lookahead in its state, so a NaN in one
frame's input comes out in the next frame's answer: the restart lands one
frame after the bad sample.

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
`tests/denoise` also feeds each engine a NaN and two infinities mid-block
and checks the output against the same input with zeros there, bit for
bit. `tests/denoise-recovery` puts a NaN inside RNNoise's frame and checks
that from the restart on the output is exactly that of an engine switched
on at that sample, then has a fake DeepFilterNet model answer with NaN and
checks that a fresh model takes over once and a second failure holds.

## Voice changer

The same stage ends with a voice changer (`voice.c`), after noise removal
and the EQ, so the EQ shapes the human voice and the character goes on
last. Its controls sit beside the others: `voice_enabled` (0 or 1),
`voice_preset` (0 to 10, below) and, for Custom only, `voice_pitch` and
`voice_formant` (-12 to 12 semitones). It reports three meters:
`voice_active`, the voice heard now (-1 the dry voice),
`voice_status`, why a voice asked for does not run (0 it runs, 1 the
pitch shifter could not start, at a rate outside 16 to 192 kHz or without
memory, 2 a voice put out something that is not a number twice within a
second and was cut off until a voice is asked for again), and
`voice_latency`, its delay in samples, 0 while the dry voice plays.

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

A voice that puts out something that is not a number plays the dry input for
that block and starts over from silence. Stretch's own reset leaves the
energies and predictions it carries from block to block alone, where a NaN
would outlive it, so the slot swaps its shifter for a spare the main thread
made beforehand. The main thread then frees the old shifter and makes the
next spare, so the audio thread still allocates nothing. A voice that fails
again within a second of starting over, or finds no spare ready, is cut off
with status 2.

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
44.1 kHz keeps the delay while 8 kHz stays dry with status 1. It also feeds
the stage a NaN and two infinities mid-block, which must leave the output
exactly as zeros there would, and hands the voice a NaN directly: the voice
must start over without allocating, come back within 4 dB of the input, do
so again two seconds later, and be cut off with status 2 by a third failure
half a second after that. It prints each
voice's latency, CPU and level. `./tests/voice --render IN.wav DIR` runs
every voice over a recording and writes `DIR/00-dry.wav` to
`DIR/11-witch.wav` with each one's level against the input, for tuning the
table by ear.

## Compressor and expander

The same stage carries a downward expander and a compressor
(`dynamics.c`). The order inside the stage is noise removal, the
expander, the EQ, the compressor and the voice changer. The expander
closes on the noise that noise removal left, and the compressor evens out
the shaped voice before any character goes on. Neither adds latency.

| Control | Range | Default |
| --- | --- | --- |
| `exp_enabled` | 0 or 1 | 0 |
| `exp_threshold` | -80 to 0 dB | -50 |
| `exp_ratio` | 1 to 20 (1:N below the threshold) | 3 |
| `exp_range` | 0 to 80 dB, the most it turns down | 24 |
| `exp_attack` | 0.1 to 200 ms, how fast it opens | 1 |
| `exp_release` | 10 to 2000 ms, how fast it closes | 150 |
| `exp_hold` | 0 to 500 ms, how long it waits before it closes | 50 |
| `exp_meters` | 0 or 1 | 0 |
| `comp_enabled` | 0 or 1 | 0 |
| `comp_threshold` | -60 to 0 dB | -18 |
| `comp_ratio` | 1 to 20 (N:1 above the threshold) | 3 |
| `comp_attack` | 0.1 to 200 ms | 5 |
| `comp_release` | 10 to 2000 ms | 150 |
| `comp_knee` | 0 to 24 dB, the width of the soft knee | 6 |
| `comp_makeup` | 0 to 24 dB | 0 |
| `comp_meters` | 0 or 1 | 0 |

Both are feed-forward and work in the log domain. A sample's level is
`20 log10 |x|` with a floor of -120 dBFS, a static curve turns it into the
reduction the processor wants, and a one-pole filter per direction smooths
that reduction in dB, with the coefficient `exp(-1 / (time * rate))`, so a
step reaches 63.2 percent of its way in the attack or release time. Attack
is always the response to a louder input: the compressor's reduction rising
and the expander's falling. With `L` the level, `T` the threshold, `R` the
ratio and `d = L - T`:

- compressor, with a soft knee of width `W` (Giannoulis, Massberg and
  Reiss): no reduction while `2d < -W`, `(1 - 1/R)(d + W/2)^2 / (2W)` while
  `|2d| <= W`, and `d (1 - 1/R)` above; the output is
  `x * 10^((makeup - reduction) / 20)`, and a make-up change ramps across
  one block;
- expander: no reduction at or above `T`, below it
  `min((T - L)(R - 1), range)`. Before it starts to close, the level has to
  stay below what keeps it open for the hold time, so it does not close
  between the peaks of a voice's own waveform.

Switching either on or off is a 5 ms crossfade between the dry and the
processed signal. Once off and faded, the output is the input to the bit
and the detector is reset, so switching on starts from no reduction.
Parameter changes take effect at the next block and keep the detector's
state. The smoothed reduction is set to 0 below 1e-9 dB, so a long release
never reaches subnormal numbers. Each processor takes a non-finite sample as
0 before its detector sees it, besides the stage's own guard: an infinity
would otherwise pin the reduction at infinity and keep the processor silent
for good.

Each reports three meters, `exp_input`, `exp_reduction` and `exp_output`,
and `comp_input`, `comp_reduction` and `comp_output`: the highest input and
output sample peak in dBFS (floor -120) and the most reduction applied in dB
since the last tick, the on and off crossfade included, rounded to 0.1 dB.
They are measured only while `exp_meters` or `comp_meters` is 1; while it
is 0 they rest at -120, 0 and -120, so the tick prints them once at start
and then nothing. The audio thread keeps the peaks; the main thread
publishes them each tick and asks for a fresh count through an atomic flag.

`make test-dynamics` drives the stage with no audio server, at 48 kHz in
blocks of 256, counting every allocation made while a block runs (there
must be none). It checks the controls and meters against the table above;
that both off pass the input through bit for bit, by default and after
running on; that a steady level, a square wave whose every sample has the
same magnitude, comes out at the curve's gain within 0.05 dB for a hard
knee, a soft knee with make-up, a 20:1 setting and two expander settings
with their range caps, with the meters within 0.1 dB of the applied gain
and the levels; that the reduction covers 63.2 percent of a step in the
attack and release times within 5 percent, after the hold for the
expander, with nothing moving during the hold; that full scale and six
seconds of silence after it stay finite, bounded and clear of subnormal
numbers; that a NaN and two infinities, through the stage or handed to a
processor directly, leave the output exactly as zeros there would and the
reduction a number; that the meters stay at rest while off; and that a
live change of threshold, ratio, knee, attack and make-up moves the output
by no more than the new gain allows. It prints the measured figures and the cost of
both processors.

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

## Clock bridge

`openxlr-lv2-host bridge NODE 1|2 RATE [quanta=N] [margin=MS]` carries a
capture device on a clock of its own into the mixer's graph (`bridge.c`).
It loads no plugin and has no editor. It is one client with two nodes,
never linked to each other. `NODE_in` has input ports `playback_N` and is linked
only to the device, so PipeWire runs it in the device's graph, which the
device drives. `NODE` has output ports `capture_N` and is linked into the
mixer's channel, so it runs in the mixer's graph. A link always puts both
ends under one driver, and the two halves of a loopback share one too, so
two nodes joined only inside one process are the way to keep two clocks
apart. The daemon starts one for a capture input on its own clock
(`setCaptureOwnClock`, see [api.md](../docs/api.md#own-clock)).

The device's node writes each cycle into a lock-free ring, with its frame
count, the cycle's time and the next cycle's time. The mixer's node reads
the ring through a resampler: a 64-tap Kaiser-windowed sinc in 256 phases,
interpolated between phases, with its cutoff at 0.92 of the Nyquist
frequency. A delay-locked loop, PipeWire's `spa_dll`, sets the resampler's
ratio from the ring's fill against its target. The fill counts the device's
frames as if they arrived one at a time, from the device's last cycle time,
so the sawtooth of two cycles sliding past each other does not move the
ratio, and a device cycle that comes late is covered by the headroom rather
than read as drift. The loop starts wide and narrows over 15 seconds to
`SPA_DLL_BW_MIN`. The correction is held within 0.5 %, and the nominal
ratio of two different rates (from 1/4 to 4) is applied on top.

The target is the device's cycle plus `quanta` of the mixer's cycles (2 by
default), the resampler's 32 frames and `margin` milliseconds (10 by
default): 1280 frames, 26.7 ms, at 48 kHz with both cycles at 256 frames.
That holds a device cycle late by one mixer cycle plus the margin. When the
reader runs dry it plays what there is, then silence, counts an underrun
and waits for the target again. When the ring holds twice the target, or is
full because the mixer stopped reading, it counts an overrun and drops back
to the target. Once a second the helper writes `heartbeat` and, each when
it changed, the meters `running` (1 while it carries audio), `delay` and
`target` in the mixer's frames, `underruns`, `overruns`, `drift` (the loop's
correction in parts per million), `in_rate` and `in_quantum`. It prints
`ready` once both nodes are up, and exits when its standard input closes.

`make test-bridge` drives the ring, the loop and the resampler between two
simulated clocks with no audio server: one clock and no jitter, a device
120 ppm fast whose cycles come up to 2 ms late and every 3 seconds 12 ms
late, with and without late cycle times, -350 ppm with a 1024-frame device
cycle, a 1024-frame mixer cycle, a device 2000 ppm off, a device that stops
for 200 ms and a mixer that stops for 2 seconds. Each must settle at its
target with no underrun or overrun after 20 seconds, find the true drift
within a part per million and hold it within 5, carry a 1 kHz tone with a
residual under -70 dB and a steady level exactly; the stops must cost
exactly one counted event each and recover.

## Windows plugin host

```text
openxlr-lv2-host [--own-block] wine clap FILE ID NODE CHANNELS RATE [SYMBOL=VALUE ...]
openxlr-lv2-host [--own-block] wine vst3 BUNDLE CLASS-ID NODE CHANNELS RATE [SYMBOL=VALUE ...]
openxlr-lv2-host wine scan-clap FILE
openxlr-lv2-host wine scan-vst3 BUNDLE
```

`make -C native WINHOST=1` builds a second way to run a Windows plugin, one
that does not go through yabridge. It adds a `wine` backend to the helper
(`wine.c`) and builds `openxlr-winhost.exe` beside it. The backend starts
that program under Wine, and the program loads the plugin. The arguments
after the format mean what they mean for a native plugin of that format:

- For CLAP, `FILE` is the Windows `.clap` module by its Linux path and `ID`
  is the plugin's CLAP id.
- For VST3, `BUNDLE` is the Windows plugin by its Linux path: a bundle
  directory with the module under `Contents/x86_64-win`, or a single `.vst3`
  file. `CLASS-ID` is the class id as 32 hex digits, in the form a Linux
  host has for the plugin, described below.

`SYMBOL=VALUE` sets a parameter before the first block. The two `scan`
forms describe a module for the catalogue and run no audio.
[Scanning a Windows plugin](#scanning-a-windows-plugin) has the rest.

The default build leaves both out and is unchanged. The flag needs
`x86_64-w64-mingw32-gcc` and `x86_64-w64-mingw32-g++`, or the compilers
`MINGW_CC` and `MINGW_CXX` name. Either of mingw's thread models builds it,
since the program starts its threads through Windows itself. On Debian and Ubuntu
the compilers are the packages `gcc-mingw-w64-x86-64` and
`g++-mingw-w64-x86-64`. The helper then links libdbus as well, for
RTKit (below): `libdbus-1-dev` on Debian and Ubuntu, `dbus-devel` on
Fedora. The program is linked statically, so it is
one file. Building it does not need Wine; running it does. Run
`make -C native clean-native` when you switch the flag, as after any change
of flags.

This is the start of a host that will replace the yabridge companion. It
carries CLAP and VST3 audio, parameters and latency, it shows a plugin's
editor, it scans, and it reports the system disk's identity to a plugin as
the companion's host does. Every package except the Flatpak builds it. The
package sets `WINHOST=1` in the environment of the daemon's publish, and
the publish copies `openxlr-winhost.exe` beside the helper.
`packaging/check-winhost.py` fails when a package stops doing so. In a build
that has it, the daemon starts it for an insert that asks for it and for no
other, and never for a scan. [The daemon's side](#the-daemons-side) has
the rest.

The helper keeps the PipeWire node and the Windows program holds the plugin.
The Windows program has no CLAP or VST3 host of its own. It is built from
these sources:

- `clap.c` and `vst3.cpp`, the same files the helper builds for native
  plugins, compiled for Windows. The port and bus layouts, the parameters, a
  restart or a reload the plugin asks for, timers, the thread checks and the
  scanner are one implementation for each format on both systems.
- `json.c`, which writes the text in a scanner's JSON, for the same reason.
- `winhost/shim.c`, which gives those backends the services `host.h`
  declares: the control table, the reports from the audio thread, the main
  loop's timers and events, and the module loader. `winhost/shim.h` stands
  in for the PipeWire, X and `dlfcn.h` headers.
- `winhost/winhost.c`, which talks to the Linux side and picks the backend
  the load names.
- `winhost/editor.c`, which has the window an editor is put in and answers
  what a backend asks of the frame around it.

The two backends differ on Windows in few places. `clap.c` skips the
`dlfcn.h` include, has one block for the window an editor sits in, which
is Windows' own kind there, and one that asks a plugin whether its editor
may be resized and tells it a new size. On Linux the frame resizes the
plugin's own X11 window instead. `vst3.cpp` skips the system headers the shim
stands in for, has one block for what a Windows module is, and tells an
editor its scale.

A Windows VST3 module differs from a Linux one in three ways, and that block
holds them:

- The Linux side finds the module. `wine.c` looks under
  `Contents/x86_64-win` for the `.vst3` file and hands the Windows program
  that file, so the Windows side looks through no directories.
- The module is entered through `InitDll` and left through `ExitDll`, which
  take no argument. A Linux module has `ModuleEntry` and `ModuleExit`.
- An editor's window type is `HWND`.

`vst3.cpp` still offers a plugin the Linux run loop interface on Windows.
A Windows plugin has no reason to ask for it. One that did would get its
timers and be refused a file descriptor.

A VST3 class id is 16 bytes, written as 32 hex digits. A Windows plugin
stores the first eight bytes in COM order, three fields with the low byte
first, so the digits read straight from its factory differ from the ones a
Linux host has for the same plugin. yabridge presents the Linux form, and
that is what the catalogue and every saved insert hold. `CLASS-ID` is the
Linux form here too, so an insert saved through yabridge names the same
plugin to this host.

```text
the bytes in a Windows module   33 22 11 00  55 44  77 66  88 99 AA BB CC DD EE FF
the class id, on both systems   00112233445566778899AABBCCDDEEFF
```

The conversion is in one place, `hex_of` and `tuid_of` in `vst3.cpp`. They
read the order from the SDK's own `INLINE_UID` macro, which lays an id out
for the system the file is built for, so the Linux build is unchanged and
the Windows build swaps.

TDR Nova is an example. Its Windows module holds
`54545356356474616472206E6F766100`, and its class id, through yabridge and
through this host, is `56535454643561746472206E6F766100`.

Timers are Windows timers on the main thread's message loop, no finer than
10 ms. An event signalled from the audio thread waits for the main thread's
next tick, 33 ms at most, because waking it at once would be a call to
wineserver inside a block. Windows has no file descriptors to watch, so the
host refuses a plugin that asks it to watch one.

Only what the helper's own backend interface carries crosses between the
two: load, activate, one block of audio, the controls, the latency, the
editor, deactivate. `winhost/wire.h` describes it.

- One shared file, mapped by both sides, holds the audio of one cycle and
  the plugin's controls. It sits in a private directory under
  `XDG_RUNTIME_DIR`, or under `/dev/shm` where there is none, and the backend
  removes the directory as soon as the Windows program has opened what is in
  it.
- The audio wake is the Windows program's standard input and output, one
  byte each way per cycle. Under Wine a blocking read on an inherited pipe is
  a plain `read` in the calling thread. A cycle costs the Windows audio
  thread one `read` and one `write`, and it never calls wineserver.
- Two FIFOs in the same directory carry the control requests, a fixed-size
  request one way and a fixed-size reply the other, between the two main
  threads.
- Paths cross as Linux paths. The Windows side asks Wine for the Windows
  name and falls back to the `\\?\unix\` form, so a prefix with no `Z:`
  drive works. Wine reads Linux file names in the character set of its
  locale. The daemon runs its scans in the C locale, and a user service may
  have no locale at all, which is the C locale too. Wine cannot find a
  plugin there when its path has a character outside ASCII. So the backend
  gives the Windows program a locale that reads UTF-8 when the inherited
  one does not. [The locale](#the-locale-of-the-windows-program) has the
  rule.
- The helper reads the Windows program's standard error and passes it on a
  line at a time. The plugin's log and the `trace:` lines reach the daemon's
  log the way a native backend's do.

The plugin's parameters are controls of the helper, under the names a native
plugin of the same format has, so `set`, `control` and `meter` lines read
the same.
Their values cross in the shared file and cost no system call:

- At load the Windows side lists the controls the backend made, and the
  helper registers the same ones: name, range, value, and whether it is a
  meter.
- A value the helper is told to set goes with the next block. The Linux
  audio thread writes it into the shared file before the wake and the
  Windows audio thread applies it before the plugin runs, so it is heard in
  that block. One cycle carries 256 values; more wait for the next.
- When the plugin moves a value, from its editor for one, or a meter has a
  new reading, the Windows side publishes it in that control's own word of
  the shared file. The helper reads those words on its tick and reports the
  changes to the daemon as it does a native plugin's.
- Every published value has a count, and a set names the count the helper
  had seen. The Windows side drops a set made before the helper had seen the
  plugin's latest move of that control. The plugin's value stands and the
  helper takes it on its next tick, so the two sides disagree for one tick
  at most, and the helper never sends a value it took from the plugin back
  over a newer one.

A CLAP plugin takes a value in the block it arrives with. A VST3 plugin
takes it a tick later. `vst3.cpp` converts a plain value to the plugin's own
scale on the main thread, the thread a VST3 controller belongs to, and here
that is the Windows main thread's tick, 33 ms at most. The native VST3 host
has the same tick. A value the plugin's controller moves on the Windows main
thread is published as it happens.

Each side bounds what it reads from the other. The Windows side checks the
frame count of every block against the fixed buffers and against what the
plugin was activated for, and every set against the control table, the
control's range and its count. The Linux side never uses a value from the
shared file as a size or an index. It refuses a list of controls longer than
the table or with a name that is not one plain word, and checks a published
value before taking it.

The Windows audio thread takes a name, the helper finds the thread by that
name under `/proc`, and gives it the scheduling of the thread that waits for
it: PipeWire's data thread, or the worker of the plugin's own block. A lower
priority would leave a real-time thread waiting on one that is not, and a
higher one would let plugin code outrank PipeWire's threads. A Windows
program cannot ask for this itself. The kernel allows it from outside
between two processes of one user, within their real-time limit
(`RLIMIT_RTPRIO`). The helper does that when the user has a limit that
covers PipeWire's priority, the same limit yabridge's host needs.

On the usual Ubuntu or Fedora desktop the user has no such limit, and
PipeWire's own threads get real-time from RTKit. There the kernel refuses
the helper with `EPERM`, so the helper asks RTKit
(`org.freedesktop.RealtimeKit1` on the system bus) instead, calling
`MakeThreadRealtimeWithPID` for the Windows host's process and its audio
thread. It asks for the same priority, or
RTKit's `MaxRealtimePriority` when that is lower. RTKit grants real-time
only to a process whose hard real-time CPU limit (`RLIMIT_RTTIME`) is set
and within its `RTTimeUSecMax`, so the helper lowers that limit on the
Windows host's process first when it is above, as PipeWire does for its
own. RTKit gives the thread `SCHED_RR` whatever the waiting thread runs
at. The helper reaches the bus through libdbus, which PipeWire's RTKit
support uses too; PipeWire's own thread utilities act only on threads of
the process that calls them, so they cannot be used for a thread of the
Windows host. The desktop portal's `Realtime` interface, which a Flatpak
sandbox would need, is not asked.

One trace line, which the daemon writes to its journal, says what came of
it:

```text
trace: the Windows plugin's audio thread runs at real-time priority 20, within this user's real-time limit
trace: the Windows plugin's audio thread runs at real-time priority 20, granted by RTKit
trace: the Windows plugin's audio thread runs without real-time priority: this user's real-time limit does not allow it, and RTKit is not on the system bus
```

The last form ends with what went wrong: RTKit missing, its refusal with
RTKit's own reason, the thread not found, or, when the thread that waits
for the Windows side has no real-time priority itself,
`the thread that waits for it has none either`. The audio runs in every
case. Without real-time it is more likely to come late under load.

What happens when the Windows side goes wrong:

- It dies, killed or crashed. The audio thread reads the end of the pipe at
  once and the cycle is silence. The next tick writes how the program ended
  and the helper exits, so the daemon rebuilds the chain as after any other
  exit. A fault inside the plugin ends the Windows program on the spot with
  one line naming the exception, without waiting for Wine's debugger.
- A block comes back late. The cycle waits up to twice the block's own
  length and at least 20 ms, so a block late by less is heard late, as a
  native plugin's would be. Past that the cycle is silence, and so is every
  cycle until the answer arrives. The helper leaves the shared file alone
  meanwhile, because the Windows side may still be reading it.
- A block never comes back. After six seconds the helper fails the instance,
  the patience it has with a native plugin stuck inside one callback. Unload
  kills a Windows program that no longer answers.
- The plugin refuses its blocks. These count like a native plugin's: each
  is silence, and half a second of them in a row fails the instance.
- The helper itself is killed. The kernel sends the Windows program a
  SIGKILL, and it would also leave by itself on reading its closed pipes.

### The editor of a Windows plugin

`show` opens a Windows plugin's editor in the frame the helper has for every
editor, with the same title, icon, close button and size rules. The plugin's
editor is a window of Windows' own, an `HWND`, which under Wine's X11 driver
is an X11 window, and the helper takes that window into its frame.
`winhost/editor.c` is the Windows side and the editor part of `wine.c` the
Linux side.

An opening goes in the order that works under a window manager and without
one:

1. The Windows program makes a window with no border, hidden, and has the
   backend put the plugin's editor in it: `IPlugView::attached` with an
   `HWND`, or CLAP's `set_parent` with a `win32` window. It answers with the
   X11 window Wine has for it, which Wine keeps in the window's
   `__wine_x11_whole_window` property, the editor's size, and whether the
   plugin lets a host resize it.
2. The helper gives its frame that size and reparents the X11 window, still
   unmapped, into it.
3. The Windows program shows its window.
4. The helper sets `WM_STATE` to normal on Wine's window, as a window manager
   does for a window it has mapped. Wine 11.22 waits for that before the
   window takes its first click, and no window manager sets it on a window
   inside another.

A window that is shown before it is reparented has been placed by the window
manager as a window of its own, and Wine keeps it at the place it had there.

Wine learns where a window is on the screen from the X server, and a window
inside another hears nothing when the outer one moves. On every tick the
helper compares the frame's place on the root window, and the size of Wine's
window, with what it last told Wine, and sends Wine a `ConfigureNotify` of
its own when they differ. Wine reads one sent by a client as the place on the
root. Without it, clicks and the plugin's menus land where the frame used to
be. The same pass does two more things. Wine 9.0 sets a window's position
with every change of its size, to the one it has on the screen, which inside
the frame moves it away from the frame's corner, so the helper puts it back.
Wine 11.22 leaves the position alone. And it maps a Wine window that was left unmapped in the frame, as
it does for a bridged editor.

A window manager gives the keyboard to the frame. The helper hands it on to
Wine's window with `XSetInputFocus`, and the plugin's own window takes it
from there on a click, as on Windows.

Sizes cross in both directions:

- The plugin asks for a size through `IPlugFrame::resizeView` or CLAP's
  `request_resize`. The Windows side gives its window that size at once and
  sends it to the helper as a note, a message on the reply FIFO that answers
  no request. The helper reads notes on its tick and resizes the frame as
  for a native plugin's request.
- When the user resizes the frame, the helper asks the Windows side which
  size near it the plugin takes (`checkSizeConstraint`, `adjust_size`) and
  gives the frame that size. It then tells the Windows side, which resizes
  its window and has the plugin lay out (`onSize`, `set_size`).
- An editor the plugin does not let a host resize (`canResize`,
  `can_resize`) pins the frame to its size, and can still change that size
  itself.
- Wine writes size hints of its own on every window it owns, and for a
  window with no border they allow one size. They are not the editor's, so
  the frame ignores them for this backend (`foreign_window_hints` in
  `host.h`).

The helper opens every editor at one scale, 1 (`host_editor_scale`). It
crosses with the request to open, and the plugin is told through
`IPlugViewContentScaleSupport` or CLAP's `set_scale` before it is asked for
its size. The helper does not read the desktop's scale yet, for any editor.

All of the plugin's editor calls run on the Windows main thread, whose
message loop also serves the requests. Nothing about the editor reaches the
audio thread. With an editor open that thread still makes one `read` and one
`write` per cycle and no other system call.

Windows runs a loop of its own while a menu is up, inside the plugin's
window procedure, and the main loop does not come round until it ends. A
request made meanwhile, because the user resizes or closes the frame, would
wait for the menu. So every request also reaches the Windows program as a
message to a window nobody sees, which whatever loop is running dispatches.
When that is not the main loop, the Windows program ends the menu. The main
loop then serves the request, after the plugin's code has returned. Nothing
ends any other loop a plugin runs on the main thread, a dialog of its own
for one. A request made meanwhile waits thirty seconds and then fails the
instance.

What happens when something goes wrong:

- The plugin has no editor, or will not open it. `show` answers that the
  plugin has no editor the host can show, the reason is on standard error,
  no frame is left and the audio goes on. The helper does not ask again.
- Wine has no X11 window for the editor, because it has no display or runs
  another driver. `show` answers that there is no X display.
- The Windows program dies with its editor open. The instance fails as it
  does without one, and the frame closes with the helper. What the X server
  says about a window that vanished is written to the log and has no other
  effect.
- The plugin closes its editor itself, which CLAP has a call for. The
  Windows side sends a note and the helper closes the frame.

Three things are not carried: a CLAP plugin asking for its editor to be
shown or hidden, VST3's `onFocus`, and dragging between the desktop and an
editor, which nothing here has tried.

A Wine server that was started without a display keeps a desktop without
one for as long as it runs. A Windows program started in that prefix
meanwhile gets no X11 window, and its editor does not open until the server
has ended, which it does a few seconds after its last program.

### Scanning a Windows plugin

`wine scan-clap FILE` and `wine scan-vst3 BUNDLE` describe a Windows module
the way `scan-clap` and `scan-vst3` describe a native one. `FILE` and
`BUNDLE` are Linux paths, as for a load. The scanner is `clap_scan` or
`vst3_scan`, the function that scans a native plugin, compiled for Windows.
The JSON on standard output has the same fields in the same order, and the
`trace:` phase lines on standard error are the same lines. For a Windows
plugin, three of the fields read as follows:

- `file` is the path the helper was given, as it was given. No Windows path
  is written into a description.
- A VST3 class id is in the Linux form described above.
- `gui` is about a window of Windows' own. A CLAP plugin is asked whether it
  has an editor for `win32`, where a native one is asked about `x11`. The
  VST3 scanner asks no plugin and answers `true` for every class, on both
  systems.

A scan starts the Windows program with the argument `scan`, followed by the
directory that holds [the disk identity](#the-disk-identity) when there is
one to give. None of the audio path is set up: no shared file, no FIFO and no audio thread. The
helper writes one fixed-size request to the program's standard input, with
the module to open and the path to name it by. Both cross as bytes, because
Wine converts a command line through the locale's character set. The program
runs the scanner and exits with its result, and the helper passes on what it
writes as it arrives.

A scan runs plugin code. This is what the two sides do about a plugin that
misbehaves in one:

- The description goes out on a stream only the scanner has. Before any
  plugin code is loaded the Windows program points its standard output at its
  standard error, for the C library and for Windows. Text a plugin prints
  during its scan arrives as a line of its log and cannot land in the JSON.
  This does not cover a Linux library inside the Wine process that writes to
  file descriptor 1.
- A plugin that crashes ends the Windows program with the line naming the
  exception. The helper adds how the program ended and exits with 1. The
  last phase line says where the plugin was.
- The helper gives up on a plugin that never answers after three minutes.
  Two are what Wine may need to start, as before a load, and one is what the
  daemon gives the scan of a native bundle. The daemon ends a native scan
  after that one minute, so its deadline would come first.
- The helper holds the request's pipe open while it waits, and the Windows
  program ends itself when that pipe closes. The helper gives up by closing
  it. A helper that is killed, as the daemon kills one at its deadline,
  closes it by dying, and the kernel also sends the Windows program a
  SIGKILL. The helper kills a Windows program that is still there two
  seconds after it gave up.
- Once the description is written the Windows program leaves without
  unloading the module, so what a plugin does on its way out cannot fail a
  scan that has its answer.

The helper exits with 0 only when the Windows program did. It refuses a
file that is no Windows module, a missing file and a bundle with no 64-bit
module with the reason, exit code 1 and nothing on standard output.

`OPENXLR_WINE` names the Wine to start, `wine` on `PATH` otherwise.
`OPENXLR_WINHOST` names the Windows program when it is not beside the helper.
The Wine prefix and the DLL overrides come from the helper's environment,
which the daemon sets. The backend takes `WAYLAND_DISPLAY` out of the
Windows program's environment, since an editor has to be an X11 window, and
gives it a locale that reads UTF-8.

### The locale of the Windows program

The daemon hands a path over as UTF-8 whatever its own locale is, so Wine
has to read file names as UTF-8 too. The backend looks at the variable that
decides the character set, `LC_ALL` when it is set, else `LC_CTYPE`, else
`LANG`, and at what this system's locale of that name reads:

- UTF-8: the environment goes to Wine as it is.
- Anything else, the C locale and a name the system has no locale for
  included: the Windows program gets the same locale with UTF-8 as its
  character set when the system has that one, so `ja_JP.eucJP` becomes
  `ja_JP.UTF-8` and Wine keeps the language. Otherwise it gets `C.UTF-8`.

One variable changes. It is `LC_ALL` when that was set, since it overrides
the others, else `LC_CTYPE`. A daemon scan runs under `LC_ALL=C`, so its
Windows program runs under `LC_ALL=C.UTF-8`. The helper's own locale is not
touched, and a system with no UTF-8 locale at all is left as it is.

### The disk identity

Some plugins, Elgato Voice Focus among them, tie their licence to a machine
ID built from the system disk. Under Wine, `\\.\PhysicalDrive0` answers the
storage property query with a descriptor that has no model, firmware
revision or serial number, and such a plugin cannot activate. The yabridge
companion's host answers that one query with the real identity of the NVMe
drive that holds the Wine prefix, as
[its own page](../packaging/yabridge/README.md) describes. This host gives
the same answer by the same rules, byte for byte. A plugin then sees one
machine on either host, where a different answer would count as another
device against its licence.

The helper works the descriptor out before it starts Wine, in
`disk-identity.c`, with the companion's rules unchanged:

- The prefix is the one `WINEPREFIX` names, else `~/.wine`, as for Wine.
- The disk is the one behind the mount that holds the prefix's `drive_c` in
  `/proc/self/mountinfo`, under its partition and under a LUKS or LVM
  mapping. The helper reports an NVMe drive and no other kind.
- The serial is the namespace's NGUID, or its EUI-64 when the NGUID is zero
  or missing, as groups of four hex digits joined by underscores with a
  period at the end, which is how Windows' NVMe driver writes it. A disk with
  neither has its controller's serial.
- The descriptor is the 396 bytes that driver returns: no vendor, the model
  at byte 57, the firmware revision at 122 and the serial at 139, bus type
  NVMe.

The helper writes the 396 bytes to a file named `disk-identity` in the
private directory it hands the Windows program, and the file goes with the
directory. A scan has a directory of its own for it, named after `scan` on
the Windows program's command line. The Windows program removes the file
and that directory itself once it has read the file, and the helper removes
what is left when the scan ends. The helper cannot tell from a close of the
file that the program has read it, because Wine opens a file more than once
on some starts. When there is nothing to give, because the prefix is not on
a block device, the disk is not an NVMe drive or its identity cannot be
read, there is no file and one `trace:` line says why. A prefix with no
`drive_c` yet gets no line, as under the companion.
When there is one, the line names the disk, its model and revision, and
the shape of its serial with every letter and digit masked.

The Windows program reads the file before any plugin module is loaded
(`winhost/disk-hook.c`). Like the companion's host, it asks the loader to
tell it of every module that loads from outside `C:\windows`, and points
that module's imports of `DeviceIoControl`, `CreateFileA`, `CreateFileW` and
`CloseHandle` at its own. It notes the handles a plugin opens on
`\\.\PhysicalDrive0` or `\\?\PhysicalDrive0`. On those handles only, it
answers the standard query for the device property, and only where Wine's
answer is its empty descriptor. Every other question goes to Wine unchanged,
another property or device and a handle once closed among them.

A plugin may ask once per audio block, as Voice Focus does, so the question
reaches the audio thread. Once Wine's empty answer has been replaced, a
repeat on a noted handle is answered from the copy without asking Wine, as
the companion does. The noted handles are a fixed table of 256 slots read
without a lock, so a repeat costs no allocation, no lock and no system
call. A plugin with more than 256 open at once has the rest answered by
Wine, and one line says so.

### The daemon's side

The daemon knows a build has this host by the Windows program. It looks
for `openxlr-winhost.exe` beside the helper, or where `OPENXLR_WINHOST` in
its environment names it. `pluginSetup.windowsHostInstalled` reports
it. A helper built without `WINHOST=1` has no `wine` backend, so the two
have to come from the same build.

yabridge stays the host of every Windows plugin unless an insert says
otherwise. `setInsertWindowsHost` moves one insert here and back, and
[docs/api.md](../docs/api.md#windows-host) has the command, what it refuses
and the state fields. The catalogue is not scanned again for it. A Windows
plugin is in the catalogue through the wrapper yabridge made, and the daemon
reads the Windows module back from that wrapper:

- VST3: `NAME.vst3/Contents/x86_64-win/NAME.vst3` in the wrapper is a link
  to the module.
- CLAP: `NAME.clap-win` beside the wrapper's `NAME.clap` is the module or
  a link to it.

Both are yabridgectl's layouts, the same under the companion's
`~/.local/share/openxlr/yabridge` and under a system yabridge's
`~/.vst3/yabridge` and `~/.clap/yabridge`. The daemon follows every link to
the module's real path, checks that it is a 64-bit Windows module, and
starts the helper with `wine vst3 MODULE CLASS-ID` or `wine clap MODULE ID`
and the insert's own id and values. An insert on its own block gets
`--own-block` in front, as on any other backend.

The helper's environment is the one every plugin helper gets, which keeps
Wine's USB driver out of the prefix with `wineusb.sys=d`. Two variables come
on top. Both follow what yabridge does, so a plugin runs in the same Wine
and the same prefix on either host:

- `WINEPREFIX`. A value the daemon was started with stands. Without one it
  is the nearest directory above the module that holds a `dosdevices`
  directory. A module outside every prefix gets none and Wine uses its
  default.
- `OPENXLR_WINE`, from `WINELOADER` when the daemon has one.

The daemon gives every helper eight seconds to say `ready`, this one
included, and ends a helper that has not. The backend itself allows Wine two
minutes before its first answer, and the daemon does not wait that long. It
waits with the mixer's lock held, so every command and the sweep wait with
it, and the service's watchdog stops a daemon whose mixer has made no
progress for a minute. When Wine is still silent at eight seconds, because
it is creating or updating its prefix, the chain's inserts report that Wine
did not answer in time and the audio goes on without them. Turning the
insert off and on tries again. The backend's two minutes are what a scan or
a run by hand gets.

A plugin is told the system disk's identity here as under the yabridge
companion, as [the disk identity](#the-disk-identity) describes. Its editor
opens as on any other host: `showInsertUi` sends the helper `show`, and a
compatibility rule blocks it the same way.

A saved choice the build cannot honour when the chain is built, because the
host is gone after a downgrade or the module is, never fails the chain. The
insert runs through yabridge, and the daemon's log and the insert's status
say why.

`make -C native WINHOST=1 test-winhost` runs `tests/winhost` against two
test plugins built with mingw, a CLAP module (`tests/winhost-plugin.c`) and
a VST3 module (`tests/winhost-plugin-vst3.cpp`), under Wine and with no
audio server and no display. It calls the host's audio callback on plain
buffers with the Windows program started for real. With the CLAP module it
checks:

- the audio that comes back is the plugin's, sample for sample, at block
  sizes from 1 to 8192 frames, in mono and with one buffer for both
  directions;
- a plugin with a sidechain port runs, and one of the wrong width is refused
  with the reason;
- the latency the plugin reports, and a restart the plugin asks for that
  changes it, carried out in the same process;
- a main-thread callback the plugin asks for from its audio thread, and a
  timer it registers;
- the plugin's parameters as controls: a value set before the first block
  and one set between blocks are heard in the next block, a value the plugin
  moves reaches the helper, a meter is read, and a hidden parameter and the
  plugin's own bypass are left out;
- a plugin with 5000 parameters gets the table's 4096, and a thousand values
  set at once arrive over four cycles;
- sets that name no control, carry no number or come with a stale count
  change nothing, and a list of controls the helper cannot carry is refused;
- refused blocks, a block 60 ms late, a killed Windows program, a plugin that
  crashes inside a block and one that never returns. In each the audio
  callback keeps returning, plays only the plugin's audio or silence, and
  the failure is reported;
- the Windows audio thread gets the scheduling of the thread that waits for
  it, at real-time priority 5 where the user's limit allows, and the test
  notes what a block's round trip costs there;
- with no real-time limit at all, RTKit is asked instead
  (`tests/winhost-rtkit.py`, below);
- a missing plugin, module or Windows program is refused, and nothing is
  left in the runtime directory.

With the VST3 module it checks:

- a class is loaded by the class id a Linux host knows it by, from a bundle
  and as a single file. The module holds its ids in COM order, written out
  byte by byte, and gives no factory unless it was entered through
  `InitDll`;
- the same id with its digits in the module's byte order is refused, and so
  are a text that is no class id, a bundle with no module and a file that is
  no module. The test reads the reason each one is refused with;
- the audio is exact at the same block sizes, with one buffer for both
  directions too;
- a plugin that reports stereo and takes mono when asked runs in a mono
  chain, one with a second input bus runs, and one of the wrong width is
  refused;
- the latency the plugin reports, and a reload the plugin asks for that
  changes it, carried out in the same process;
- the plugin's parameters as controls with their plain ranges: a value set
  before the first block and one set between blocks are heard after the
  Windows side's next tick, a meter the processor writes is read, and a
  hidden parameter and the plugin's own bypass are left out;
- a parameter the plugin's controller moves on the Windows main thread, as
  an editor would, reaches the helper, and a value the helper set before it
  had seen that move is dropped;
- refused blocks are silence and half a second of them fails the instance.

The CLAP module's `disk` plugin asks for `\\.\PhysicalDrive0`'s descriptor
from its own module when it is made, the questions `tests/disk-probe.h`
lists, and again in every block. With it the test checks:

- without an identity to give, the plugin hears Wine's empty descriptor;
- with a made-up NVMe disk under `disk_identity_test_root`, the plugin is
  told the helper's 396 bytes on the first question, on a repeat, into a
  buffer one header long, and on a handle opened again through
  `CreateFileW` as `\\?\PHYSICALDRIVE0`. Another property, the question
  whether the property exists, the `C:` volume, the handle once closed and
  a plain file opened after it come back as Wine gave them without the hook;
- on this machine's own disk, when the prefix is on an NVMe drive, the
  plugin is told the bytes the helper built;
- a block that asks once is no slower than one that does not, at real-time
  priority 10 where the user's limit allows, and strace on the Windows
  audio thread sees nothing beside the wake's read and the answer's write.
  The test prints the same figures with Wine answering, for comparison;
- a scan gives the plugin the same answer, and Wine's when there is none;
- a stand-in for Wine that reads the scan's file, waits and reads it again
  finds it both times, and the scan's directory is gone when the scan ends.
  The scan whose helper is killed part way leaves no directory behind
  either, because the Windows program has removed it by then. The test's
  last check, that its runtime directory is empty, fails if one is left.

`test-winhost` then runs `tests/winhost-rtkit.py`, which checks the way to
real-time through RTKit without the system's RTKit. It starts a D-Bus bus of
its own with `dbus-daemon --session` and points `DBUS_SYSTEM_BUS_ADDRESS` at
it. On that bus `tests/winhost-rtkit` stands in for RTKit: it answers
RTKit's properties, with a highest priority of 7, checks each
`MakeThreadRealtimeWithPID` the way RTKit does and records the process,
thread and priority it was asked for. `tests/winhost` runs under
`prlimit --rtprio=0:0`, so the kernel refuses it any real-time priority,
and the thread that waits for the Windows side is described to the backend
as `SCHED_FIFO`, which this process cannot really be. Three cases:

- RTKit grants: the backend asks for the Windows host's process and audio
  thread, at priority 3 as asked and at RTKit's 7 when 9 is asked, with the
  process's hard real-time CPU limit set within `RTTimeUSecMax` first, and
  its trace line says the priority was granted by RTKit;
- RTKit refuses: the trace line says the thread runs without real-time
  priority and gives RTKit's reason;
- nothing owns RTKit's name: the trace line says RTKit is not on the system
  bus.

In each the audio stays exact for 200 more blocks. The stand-in changes no
thread's scheduling, since the kernel weighs that against the Windows
host's own limit of 0, so no thread of the check runs at real-time
priority. The check needs `dbus-daemon` and `prlimit` and says so when one
is missing. Every other Windows host test points `DBUS_SYSTEM_BUS_ADDRESS`
at a socket that does not exist, so none of them can reach the system's
RTKit.

`make -C native test-disk-identity` runs the helper's side alone on
made-up disks, and `test-audio` runs it too, so CI does. It checks an NVMe
disk with an NGUID, with an EUI-64 only, with a serial only and with no
firmware revision, one behind a LUKS mapping, the later of two mounts and a
mount point with a space in its name, against the layout written out byte
by byte. It also checks the cases that give nothing: a disk that is not
NVMe, a prefix on tmpfs, a model or revision too long for the layout, a
device that is not there, a mount table that cannot be read and a prefix
with no `drive_c`. `tests/disk-identity --write FILE` writes the
descriptor for this machine's own prefix, to compare with what the
companion builds.

It then runs the helper's `wine scan-clap` and `wine scan-vst3` in a process
of their own, as the daemon would start them, and checks:

- each description against the text the native scanner writes for the same
  plugins, byte for byte. That covers names, vendor, features, the main
  ports' widths, the widths a VST3 class takes, class ids in the Linux form,
  parameters with their ranges, and the path as it was given. A hidden
  parameter, the plugin's own bypass and a plugin that refuses to initialise
  are left out, as a native scan leaves them out. The CLAP module is scanned
  through a link whose name is not ASCII. Wine is never shown that name,
  since the backend resolves links first;
- the editor field. Two CLAP plugins have an editor for a Windows window
  and for no other, and they alone are listed with one;
- the phase lines, which have to be the native ones line for line;
- a file that is no module, a missing file and a bundle with no module are
  refused with the reason and exit code 1;
- a plugin that crashes while it is described, in both formats: the
  exception, the phase it was in and exit code 1;
- a plugin that prints on standard output with no line end, through the C
  library and through Windows: the description is still exact and the text
  is in the log;
- a plugin that never answers: the helper gives up at its limit, which the
  test builds as six seconds, and a helper killed during a scan takes the
  Windows program with it. No Windows program is left after any of these.

Last it checks the locale. It puts a copy of each module under a directory
whose name is not ASCII and sets `LC_ALL=C`, as the daemon does for a
scan. Both modules have to load there with their audio exact and be
described under that path. It also checks which variable the Windows
program is given for each locale the helper can inherit.

`make -C native test-vst3` checks that the Linux build still reads and
writes a native class id in the order of its bytes.

`xvfb-run -a make -C native WINHOST=1 test-winhost-editor` runs
`tests/winhost-editor`, which opens the editors of the two test plugins with
the Windows program started for real, in the helper's own frame and with its
event loop and tick. Each test plugin has a small real editor
(`tests/winhost-plugin-editor.h`): one colour, two squares, and something to
do for a click, a key and a right click. The test is the user in front of
it, on an X11 connection of its own. It reads the frame's pixels and moves
the pointer and presses keys through XTest, so it needs the XTest
development files. For a CLAP and a VST3 plugin it checks:

- the editor opens in the frame at the plugin's size, with Wine's window in
  the frame's corner, and the frame shows the plugin's picture;
- a click in the editor moves a parameter on the Linux side and in the
  audio, before and after the frame is moved;
- a window the plugin puts up at a click, as it would a menu, appears at
  that place on the screen after the move;
- the keyboard given to the frame reaches the editor;
- a size the plugin asks for reaches the frame, and a frame resized by hand
  ends at the size the plugin takes, with the editor filling it;
- a frame resized while the plugin has a real menu up gets its answer at
  once, and the menu is gone;
- the editor closes, its Windows window goes, and it opens again and takes
  clicks;
- at scale 2 the plugin is told and draws twice as large;
- an editor the plugin does not let a host resize keeps its size.

It then checks an editor the plugin closes itself, a plugin that says it has
an editor and fails to open it, plugins with none, and a Windows program
killed with its editor open, for both formats. Last it measures the audio
round trip at real-time priority 5 with the editor closed, open, and open
with the frame moved and resized throughout, and every block has to come
back exact.

The test places the frame and its clicks from the size of the screen it
finds. 640x480 is enough, which is what `xvfb-run` gives by default on some
systems, and on a smaller screen the test says what it needs and passes.

The test moves the display's pointer, so it is for a display of its own. On
one with a window manager it only runs with `OPENXLR_TEST_WINHOST_WM=1`,
which is for a private one: a headless `kwin_wayland --virtual --xwayland`
with its own session bus and runtime directory serves, with
`XwaylandEisNoPrompt=true` under `[Xwayland]` in that KWin's `kwinrc` so
XTest may move its pointer. The test ends the Wine server of its own prefix
before it starts, for the reason above.

By hand, `tests/winhost-editor try clap|vst3 PLUGIN ID PICTURE.ppm [X Y]...`
opens a real plugin's editor in the prefix `WINEPREFIX` names, saves the
frame's picture, clicks at each `X Y`, prints the parameters the plugin
moves, and closes and reopens the editor. Give it a screen larger than the
editor, with `xvfb-run -a -s '-screen 0 1600x1000x24'` for one.

`test-winhost` and `test-winhost-editor` say what is missing and pass when
the build has no `WINHOST=1`, when mingw is not installed or when Wine is
not, and the second also without a display or the XTest files. They keep a
Wine prefix of their own under `~/.cache/openxlr-test/wineprefix`, or where
`OPENXLR_TEST_WINEPREFIX` points, and never use yours. The first run makes
that prefix, which takes most of a minute. Neither target is part of
`make test`. CI runs `test-winhost` in a job of its own, with Ubuntu's
mingw and Wine, whenever a source file under `native/` changes, and does not
run `test-winhost-editor`.

`make -C native WINHOST=1 test-winhost-graph` runs the helper with the
`wine` backend as a real node in a PipeWire graph
(`tests/winhost-graph.py`). It starts a PipeWire server of its own, with its
own runtime directory, configuration and socket name and no session
manager. Before it starts anything else it checks that the server the
environment reaches is that one, by a token in the server's configuration,
and stops if not. `tests/winhost-graph` holds two nodes, one that plays a
stereo signal in which every frame names its own position and one that
records. The check links the helper's node between them by hand and checks,
for the CLAP and the VST3 test plugin, on the graph's block and on the
plugin's own:

- every recorded frame is the signal at the plugin's gain, or silence;
- the latency the helper reports is the plugin's figure plus the delay the
  node adds, which is none on the graph's block and 1536 frames on the
  plugin's own at a quantum of 256;
- a parameter set on the helper's standard input is heard and its meter
  follows;
- a killed Windows program makes the helper say so and exit, and its node
  leaves the graph. A helper asked to quit exits cleanly and takes its node.

Silence while the plugin's audio runs is a block that came late, which a
loaded machine may cause. It is counted and printed, not failed. No thread
of the check runs above real-time priority 20. The check needs PipeWire's
server, `pw-link` and `pw-dump` as well as Wine, uses the same Wine prefix as
`test-winhost` and no display, and runs in the same CI job.

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

The same binary reads the daemon's LV2 catalogue with
`openxlr-lv2-host scan-lv2 [--skip BUNDLE]... [LV2_PATH]`. lilv parses every
bundle's Turtle files in that process, never in the daemon, and no plugin
code is loaded. The scan prints every plugin as JSON, with the limits the
daemon applies (4096 ports, 512 controls, 256 scale points, 64 features,
4096 bytes a string) and text that is always valid UTF-8.

The scan loads the bundles itself, one at a time, in the order
`lilv_world_load_all` would: the directories of the path given, of
`LV2_PATH`, or of lilv's compiled default, each expanded as lilv expands
it. It writes `trace: scan LV2 manifest N: bundle TEXT` to standard error
before reading a bundle's manifest, and `trace: scan LV2 plugin N: bundle
TEXT` before describing each plugin, when lilv reads the plugin's own data
files. TEXT is the bundle's path with every byte outside printable ASCII,
and every `%`, written as `%XX`, at most 512 bytes. So the log of a scan
that crashed or stalled names the bundle in its last marker, and the daemon
reads the path back from it. `--skip TEXT` leaves out the bundle with that
marker text, and the scan writes `trace: scan LV2: skip bundle TEXT`
instead.

lilv has no call that returns its default path. The scan builds the text
lilv's build writes for each library directory lilv could be installed
under, and uses one only when that exact text is in lilv's loaded image. A
distribution that set its own default matches none of them. Then lilv loads
every bundle in one step under `trace: scan LV2: load bundles`, the scan
unloads a skipped bundle again before describing its plugins, and only the
plugin markers name a bundle.

Before loading a plugin, the helper defaults `LSP_WS_LIB_GLXSURFACE` to `0`
unless the user already set it. LSP's software renderer avoids the repaint
freeze reproduced with its OpenGL renderer during large continuous resizes.
The setting is specific to LSP and applies to its editors in every hosted
format. Set it to `1` in the daemon environment to test OpenGL again.

For CLAP: parameters, the audio ports, the X11 editor, timers, file
descriptors, thread checks and a log. A plugin that asks for a restart
(`request_restart`, usually because its latency changed) gets one in
place, not a new process. On the next tick the main thread asks the audio
thread to let go, and on its next block the audio thread calls
`stop_processing` and plays silence from then on. On the tick after that
the main thread calls `deactivate` and `activate` with the same rate and
block sizes and reads the latency again, which goes out as a `latency`
line like any change. The block after that calls `start_processing`. Parameters and an open editor stay as they were. A
node with no cycles running holds the request until audio comes back. A
plugin that will not activate again ends the helper like any other
failure. The same binary describes a CLAP
bundle for the daemon's catalogue (`openxlr-lv2-host scan-clap FILE`), in a
process of its own so the daemon never loads plugin code. The CLAP and
VST3 scanners write text through the LV2 scanner's writer, so it is always
valid UTF-8. They leave out a parameter whose bound or default is NaN or
infinite, since JSON has no number for it. The headers are
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
both widths is offered nowhere instead of failing at load.

A VST3 plugin that asks to be reloaded (`restartComponent` with
`kReloadComponent`, or `kIoChanged` when its buses changed) gets that in
place, not a new process. The handler only sets a flag, from whichever
thread the plugin calls it on. On the next tick the main thread asks the
audio thread to let go, and on its next block the audio thread stops
calling the plugin and plays silence from then on. On the tick after that
the main thread calls `setProcessing(false)` and `setActive(false)`. If
the main buses no longer carry the chain's width, it asks for that width
again through `setBusArrangements`, and it switches on the main buses
alone. Then it calls `setupProcessing` with the same rate and block size,
`setActive(true)` and `setProcessing(true)`, and reads the latency again,
which goes out as a `latency` line like any change. The block after that
calls the plugin again. The instance, its controller and an open editor stay the same, so
parameters and state are kept without a `getState` and `setState` round
trip, and a parameter edit made meanwhile reaches the processor on the
first block after. A request made during that activation is answered by
it. A node with no cycles running holds the request until audio comes
back. A plugin that will not take the chain's width or activate again
ends the helper like any other failure, and is not called while inactive.

The interface headers are vendored under `vst3/` (MIT, VST 3.8.1); only their
inline parts are used, so nothing of the SDK is compiled. Windows VST3
plugins arrive through yabridge as ordinary bundles. The optional
[OpenXLR companion](../packaging/yabridge/README.md) supplies matching
64-bit libraries and host with the Wine input fix, selected through the
helper's PATH. Wine and plugin settings remain in the user's environment.

Editor X events wake the main loop directly; the 30 Hz tick remains for
idle work and the Wine coordinate workaround. For CLAP and VST3 editors
the host also watches the windows inside the plugin's own window. Some
desktops leave yabridge's Wine window unmapped there, and clicks then never
reach the plugin. When a window whose class ends in `.exe` is unmapped
while the frame is on screen, the host maps it again, at most 16 times per
opening. During a frame resize, the
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
