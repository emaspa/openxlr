// SPDX-License-Identifier: GPL-3.0-only
// One isolated plugin instance, its PipeWire ports and its optional editor.
// The core owns the process: the audio node, the command pipe, the X window
// and the threads. A backend owns one plugin format and plugs into it.
#pragma once
#define _POSIX_C_SOURCE 200809L
#include <X11/Xlib.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>

enum {
  MAX_FRAMES = 8192,
  MAX_CHANNELS = 2,
  MAX_CONTROLS = 4096,
  MAX_SYMBOL = 255
};

// One value the daemon can set and the editor can move. Values cross threads
// as atomics; nothing here locks and nothing here allocates after load.
typedef struct {
  const char *symbol;     // what the daemon addresses it by
  float minimum, maximum;
  bool output;            // a meter: written by the plugin, printed by the tick
  float reported;         // last value printed, so only changes are printed
  _Atomic float desired;  // daemon or editor -> audio thread
  _Atomic float observed; // audio thread -> tick and editor
  _Atomic bool moved;     // the editor changed it on the audio thread
  void *backend;          // the backend's own record for this control
} Control;

typedef struct Host Host;

// A plugin format. Every callback runs on the main thread except process,
// which runs on PipeWire's real-time thread and may not allocate or block.
typedef struct {
  const char *name;    // "LV2" or "CLAP": node description, window title
  int argument_count;  // arguments of its own after the format word
  // Instantiate from its arguments; add controls; set the channel counts.
  bool (*load)(Host *h, char **arguments);
  bool (*activate)(Host *h);    // before the audio node connects
  void (*deactivate)(Host *h);  // after the audio node has disconnected
  // in and out hold one valid buffer of `frames` samples per channel.
  void (*process)(Host *h, uint32_t frames, float *const *in,
                  float *const *out);
  // The editor. Called inside the X escape guard with h->window in place.
  bool (*editor_open)(Host *h);
  void (*editor_close)(Host *h);
  bool (*editor_idle)(Host *h);  // true when the editor asked to close
  void (*editor_lost)(Host *h);  // the display is gone: forget, touch nothing
  // Each tick, outside the guard: whatever the plugin asked for meanwhile.
  void (*main_thread)(Host *h);
  void (*unload)(Host *h);
} Backend;

struct Host {
  const Backend *backend;
  void *impl;  // the backend's state
  const char *node_name;
  uint32_t rate;
  unsigned channels;
  Control controls[MAX_CONTROLS];
  uint32_t control_count;
  bool has_editor;  // the plugin ships one this backend can show
  // PipeWire
  struct pw_main_loop *loop;
  struct pw_filter *filter;
  struct spa_source *timer;
  void *in_ports[MAX_CHANNELS], *out_ports[MAX_CHANNELS];
  float *silence, *scratch;  // for a channel PipeWire gave no buffer this cycle
  _Atomic bool audio_error;
  _Atomic bool monitor_stop;
  unsigned heartbeat_ticks;
  pthread_t main_thread, audio_thread;
  _Atomic bool audio_thread_known;
  // The editor's window
  Display *display;
  Window window, child;
  Atom close_message;
  bool editor_open;
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

// For backends.
Control *host_add_control(Host *h, const char *symbol, float minimum,
                          float maximum, float initial, bool output,
                          void *backend);
// The editor moved a control on the main thread: apply it and tell the daemon.
void host_control_moved(Host *h, Control *c, float value);
// The editor moved a control on the audio thread: apply it; the tick tells.
void host_control_moved_rt(Host *h, Control *c, float value);
void host_resize_editor(Host *h, unsigned width, unsigned height);
void host_show_editor(Host *h, bool show);
// The display went away under a backend's own callback: drop the editor.
void host_editor_lost(Host *h);
// Ask the supervisor for a fresh process; audio ends with this one.
void host_fail(Host *h, const char *why);
struct pw_loop *host_loop(Host *h);
bool host_on_main_thread(Host *h);
bool host_on_audio_thread(Host *h);

extern const Backend lv2_backend;
extern const Backend clap_backend;
// `scan-clap FILE`: describe a bundle's plugins as JSON, for the catalogue.
int clap_scan(const char *file);
