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
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#endif

enum {
  MAX_FRAMES = 8192,
  MAX_CHANNELS = 2,
  MAX_CONTROLS = 4096,
  MAX_SYMBOL = 255
};

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
} Backend;

#ifndef __cplusplus
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
  float *silence, *scratch;  // for a channel PipeWire gave no buffer this cycle
  _Atomic bool audio_error;
  _Atomic bool monitor_stop;
  unsigned heartbeat_ticks;
  pthread_t main_thread, audio_thread;
  _Atomic bool audio_thread_known;
  // The editor's window
  Display *display;
  struct spa_source *editor_source;
  Window window, child;
  Atom close_message;
  bool editor_open;
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
void host_show_editor(Host *h, bool show);
// The display went away under a backend's own callback: drop the editor.
void host_editor_lost(Host *h);
// Run one of the plugin's callbacks inside the X escape, so a lost display
// during it costs the editor and not the process.
void host_run_guarded(Host *h, void (*call)(void *), void *argument);
// Ask the supervisor for a fresh process; audio ends with this one.
void host_fail(Host *h, const char *why);
struct pw_loop *host_loop(Host *h);
bool host_on_main_thread(Host *h);
bool host_on_audio_thread(Host *h);

// The same through accessors, for a backend that cannot see the structures.
uint32_t host_rate(const Host *h);
unsigned host_channels(const Host *h);
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
