// SPDX-License-Identifier: GPL-3.0-only
// One isolated plugin instance, its PipeWire ports and its optional editor.
// The core owns the process: the audio node, the command pipe, the X window
// and the threads. A backend owns one plugin format and plugs into it. The
// C backends see the structures; a C++ backend sees them through accessors,
// since their atomics are C's.
#pragma once
#ifdef __cplusplus
#include <cstdint>
extern "C" {
#else
#define _POSIX_C_SOURCE 200809L
#include <X11/Xlib.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <pipewire/thread.h>
#include <pthread.h>
#include <semaphore.h>
#include <setjmp.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#endif

enum {
  MAX_FRAMES = 8192,
  MAX_CHANNELS = 2,
  MAX_TAPS = 2,
  MAX_CONTROLS = 4096,
  MAX_SYMBOL = 255
};

// A plugin run on its own block (`--own-block`) is called with this many
// frames at a time on a worker thread instead of inside PipeWire's cycle.
// The output plays the input this many frames late plus the larger of
// OWN_BLOCK_SLACK and the cycle, which is the time the worker has to answer
// (see own_block_cycle in host.c). The rings hold OWN_BLOCK_RING frames per
// channel, a power of two well above the largest delay and cycle together.
enum {
  OWN_BLOCK_FRAMES = 1024,
  OWN_BLOCK_SLACK = 512,
  OWN_BLOCK_RING = 32768
};

// One mono output beside the audio: its port name and its channel position.
typedef struct {
  const char *port;     // "tap_0"
  const char *channel;  // "MONO", "FL" or "FR"
} TapOutput;

enum { AUDIO_INVALID_INPUT = 1, AUDIO_INVALID_OUTPUT };

typedef struct Host Host;
typedef struct Control Control;

// A plugin format. Every callback runs on the main thread except process,
// which runs on PipeWire's real-time thread and may not allocate or block.
typedef struct {
  const char *name;    // "LV2", "CLAP" or "VST3": node description, title
  int argument_count;  // arguments of its own after the format word
  // Instantiate from its arguments; add controls; say whether an editor exists.
  bool (*load)(Host *h, char **arguments);
  bool (*activate)(Host *h);    // before the audio node connects
  void (*deactivate)(Host *h);  // after the audio node has disconnected
  // in and out hold one valid buffer of `frames` samples per channel.
  void (*process)(Host *h, uint32_t frames, float *const *in,
                  float *const *out);
  // The editor. Called inside the X escape guard with the window in place.
  bool (*editor_open)(Host *h);
  void (*editor_close)(Host *h);
  bool (*editor_idle)(Host *h);  // true when the editor asked to close
  void (*editor_lost)(Host *h);  // the display is gone: forget, touch nothing
  // The frame gained or lost the keyboard focus. A plugin that runs under
  // Wine treats its window as inactive until it is told, and an inactive
  // window ignores the mouse.
  void (*editor_focus)(Host *h, bool focused);
  // The user resized the frame; the plugin may want to lay out again.
  void (*editor_resized)(Host *h, unsigned width, unsigned height);
  // Adjust a user-requested size before resizing the frame or the editor.
  void (*editor_constrain)(Host *h, unsigned *width, unsigned *height);
  // Each tick, outside the guard: whatever the plugin asked for meanwhile.
  void (*main_thread)(Host *h);
  void (*unload)(Host *h);
  // The plugin's algorithmic latency in samples, UINT32_MAX while it has none
  // to give. Main thread, every tick once audio runs, so it returns a figure
  // the backend already holds and asks the plugin again only when the plugin
  // said it changed. NULL for a format with nothing to report.
  uint32_t (*latency)(Host *h);
  // These formats can arrive through Wine and need the coordinate nudge.
  bool editor_coordinate_nudge;
  unsigned key_inputs;  // 0, or one mono detector input separate from the audio
  // The outputs beside the audio, at most MAX_TAPS, ended by an entry with
  // no port; NULL for none. The backend writes tap i through Host.taps[i] in
  // process, every sample of it. Unlinked, a tap is written and dropped.
  const TapOutput *tap_outputs;
  // A command line the core does not know, on the main thread. True when the
  // backend took it. NULL for a backend that only takes `set` lines.
  bool (*command)(Host *h, char *line);
} Backend;

#ifndef __cplusplus
// The plugin's own block: two rings between the audio callback and a worker
// thread that calls the plugin. Positions count frames since the start and
// never wrap; a position's ring slot is its value modulo OWN_BLOCK_RING. The
// worker's answer for input frame n is output frame n, so the delay between
// the two is set by where the callback reads, and nothing else moves it.
typedef struct {
  float *in[MAX_CHANNELS], *out[MAX_CHANNELS];  // the rings
  float *block_in[MAX_CHANNELS], *block_out[MAX_CHANNELS];  // the worker's block
  _Atomic uint64_t written;  // audio thread: input frames taken
  _Atomic uint64_t done;     // worker: input frames answered or dropped
  _Atomic uint64_t lost;     // audio thread: input below this missed a full ring
  _Atomic uint64_t heard;    // audio thread: output already played up to here
  _Atomic uint32_t delay;    // audio thread: the delay now, for the latency report
  uint32_t current;          // audio thread: the same, its own copy
  uint32_t smaller_for;      // audio thread: frames a smaller delay has sufficed
  _Atomic uint64_t short_cycles;    // cycles that played silence for a late block
  _Atomic uint64_t dropped_blocks;  // blocks the worker skipped as too late
  uint64_t reported_short, reported_dropped;  // main thread: last trace
  sem_t wake;
  bool wake_ready;
  _Atomic bool stop;
  struct spa_thread_utils *utils;
  struct spa_thread *thread;
} OwnBlock;

// One value the daemon can set and the editor can move. Values cross threads
// as atomics; nothing here locks and nothing here allocates after load.
struct Control {
  const char *symbol;     // what the daemon addresses it by
  float minimum, maximum;
  bool output;            // a meter: written by the plugin, printed by the tick
  float reported;         // last value printed, so only changes are printed
  _Atomic float desired;  // daemon or editor -> audio thread
  _Atomic float observed; // audio thread -> tick and editor
  _Atomic bool moved;     // the editor changed it on the audio thread
  void *backend;          // the backend's own record for this control
};

struct Host {
  const Backend *backend;
  void *impl;  // the backend's state
  const char *node_name;
  uint32_t rate;
  unsigned channels;
  Control controls[MAX_CONTROLS];
  uint32_t control_count;
  bool has_editor;  // the plugin ships one this backend can show
  char plugin_name[128];  // what the plugin calls itself: the window title
  // PipeWire
  struct pw_main_loop *loop;
  struct pw_filter *filter;
  struct spa_source *timer;
  void *in_ports[MAX_CHANNELS], *out_ports[MAX_CHANNELS];
  void *key_port;
  const float *key;
  void *tap_ports[MAX_TAPS];
  unsigned tap_count;  // the taps the backend declared and the host added
  // Each tap output's buffer for this cycle, or NULL past tap_count.
  float *taps[MAX_TAPS];
  float *silence, *scratch;  // for a channel PipeWire gave no buffer this cycle
  float *tap_scratch[MAX_TAPS];  // the same for each tap output
  // This cycle PipeWire gave none of the main outputs a buffer: nothing is
  // linked to read them, though a tap may be, which keeps the node running.
  bool outputs_unread;
  _Atomic bool audio_error;
  _Atomic bool process_failed;  // the plugin refused to start or process audio
  uint32_t failed_frames;  // audio thread: the current run of refused blocks
  uint32_t invalid_frames; // audio thread: the current run of invalid output on sound
  _Atomic unsigned invalid_audio;  // input or output, explained by the main loop
  _Atomic bool failure_explained;  // a non-audio thread has claimed the report
  _Atomic bool monitor_stop;
  unsigned heartbeat_ticks;
  uint32_t reported_latency;  // the last latency line, valid once reported
  bool latency_reported;
  // Audio-callback progress. The audio thread bumps entered before the
  // plugin's process call and left after it returns, so the two differ
  // exactly while a call is outstanding and entered stops moving when one
  // never returns. An idle or suspended node makes no calls at all, which
  // reads as no call outstanding rather than as a stall.
  _Atomic uint64_t audio_entered, audio_left;
  pthread_t main_thread, audio_thread;
  _Atomic bool audio_thread_known;
  // The plugin runs on its own block: the audio thread above is then the
  // worker that calls it, not PipeWire's.
  bool own_block;
  OwnBlock block;
  // The delay the node adds, measured from its audio (measure.c), and the
  // meter that reports it. Both NULL for a built-in stage, and in a build
  // without measure.o.
  struct LatencyMeter *measure;
  Control *measured;
  // The editor's window
  Display *display;
  struct spa_source *editor_source;
  Window window, child;
  Atom close_message;
  bool editor_open;
  bool editor_resizable;
  unsigned editor_min_width, editor_min_height;
  unsigned editor_max_width, editor_max_height;
  // A plugin bridged from Windows learns where its window is when the window
  // changes size, and not when it opens or moves. Until it has learnt, its
  // clicks land as far from the pointer as the window is from the corner of
  // the screen. One pixel out and back teaches it, and this counts the ticks
  // until that is worth doing again.
  unsigned settle_ticks;
  int frame_x, frame_y;  // where the frame was, to notice that it moved
  unsigned settle_width, settle_height;  // the size to put back afterwards
  // Ignore consecutive duplicates, but deliver A -> B -> A during a drag.
  unsigned notified_width, notified_height;
  // When the frame last changed size for a reason of its own, meaning the
  // user is dragging it. While that is going on the plugin's own requests
  // are left unanswered: two things pulling one window in different
  // directions is what a window that shakes is made of.
  struct timespec dragged_at;
  unsigned self_width, self_height;  // the last size the frame set itself
  unsigned child_width, child_height;  // the last size the frame gave the plugin
  // The command pipe
  char input[16384];
  size_t input_size;
  int exit_code;
};

// Xlib exits the process on both of its error paths, so every X call is made
// inside this escape: a lost connection jumps back to whichever call armed
// it, which drops the editor and leaves the plugin processing audio.
extern sigjmp_buf x_escape;
extern volatile sig_atomic_t x_escape_armed;
#endif

// --- for backends -------------------------------------------------------------

Control *host_add_control(Host *h, const char *symbol, float minimum,
                          float maximum, float initial, bool output,
                          void *backend);
// The editor moved a control on the main thread: apply it and tell the daemon.
void host_control_moved(Host *h, Control *c, float value);
// The editor moved a control on the audio thread: apply it; the tick tells.
void host_control_moved_rt(Host *h, Control *c, float value);
void host_resize_editor(Host *h, unsigned width, unsigned height);
void host_set_editor_resizable(Host *h, bool resizable);
void host_show_editor(Host *h, bool show);
// The display went away under a backend's own callback: drop the editor.
void host_editor_lost(Host *h);
// Run one of the plugin's callbacks inside the X escape, so a lost display
// during it costs the editor and not the process.
void host_run_guarded(Host *h, void (*call)(void *), void *argument);
// Ask the supervisor for a fresh process; audio ends with this one.
void host_fail(Host *h, const char *why);
// Real-time report of one plugin callback. A refused block is silenced by
// the backend; half a second of refusals in a row latches an audio failure,
// which the tick reports before it stops the instance. Only atomic flags
// and the audio thread's own counter are touched.
void host_processing_result(Host *h, bool ok, uint32_t frames);
// The audio thread's verdict on one block of plugin output. An invalid block
// is silenced by the caller. It counts only when the input carried sound,
// and half a second of such blocks in a row latches AUDIO_INVALID_OUTPUT.
void host_output_result(Host *h, bool finite, bool silent_input, uint32_t frames);
// Whether one cycle can be carried at all: the fixed fallback buffers hold
// MAX_FRAMES frames, and the plugin was activated for one sample rate. A
// cycle that fails this is refused before anything is written.
bool host_quantum_supported(const Host *h, uint32_t frames, uint32_t rate_denom);
// A channel PipeWire gave no buffer for this cycle borrows one of the two
// fixed fallback buffers. The clear is bounded by their length, so it can
// never run past the allocation.
float *host_fallback(float *buffer, uint32_t frames, bool clear);
// Whether the plugin's audio callback is stuck: the same call has been
// outstanding across every poll of a window. `entered` and `left` come from
// the host, `last` and `outstanding` are the caller's running state, and
// `window` is how many consecutive polls a single call may span before it
// counts as stuck. A node with no call outstanding, idle or suspended, is
// never stuck.
bool host_audio_stuck(uint64_t entered, uint64_t left, uint64_t *last,
                      unsigned *outstanding, unsigned window);
struct pw_loop *host_loop(Host *h);
bool host_on_main_thread(Host *h);
bool host_on_audio_thread(Host *h);

// The same through accessors, for a backend that cannot see the structures.
uint32_t host_rate(const Host *h);
// The most frames one process call carries, which is what a plugin is set up
// for: MAX_FRAMES, or OWN_BLOCK_FRAMES exactly when it runs on its own block.
uint32_t host_max_block(const Host *h);
unsigned host_channels(const Host *h);
const float *host_key(const Host *h);
unsigned long host_window(const Host *h);  // the X window, or 0
void *host_impl(const Host *h);
void host_set_impl(Host *h, void *impl);
void host_set_has_editor(Host *h, bool has_editor);
void host_set_plugin_name(Host *h, const char *name);
uint32_t host_control_count(const Host *h);
Control *host_control_at(Host *h, uint32_t index);
float control_desired(const Control *c);
void control_set_desired(Control *c, float value);
void control_set_observed(Control *c, float value);
bool control_is_output(const Control *c);
void *control_backend(const Control *c);

extern const Backend lv2_backend;
extern const Backend clap_backend;
extern const Backend vst3_backend;
// `scan-clap FILE` and `scan-vst3 PATH`: describe a bundle's plugins as JSON.
int clap_scan(const char *file);
int vst3_scan(const char *path);

#ifdef __cplusplus
}
#endif
