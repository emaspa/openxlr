// SPDX-License-Identifier: GPL-3.0-only
// The Windows side of host.h. A backend built for the Windows plugin host
// is the same source the Linux helper builds, and this file is what it
// links against there: the control table, the thread checks, the reports
// from the audio thread, timers and events on the Windows main thread's
// message loop, and the module loader. The audio node, the command pipe
// and the editor's frame stay on the Linux side. What a backend asks of
// that frame is in editor.c, with the editor's window.
//
// The functions that only keep a value do what host.c's do. Where host.c
// prints a line for the daemon, the value is published for the Linux side
// instead (winhost_publish), which prints that line itself.
#include "winhost.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#include <windows.h>

static DWORD main_thread_id;
static _Atomic DWORD audio_thread_id;
static HANDLE wake;
static bool block_refused;  // audio thread

// --- the main loop ----------------------------------------------------------

enum { SOURCES = 128 };
enum { SOURCE_FREE, SOURCE_TIMER, SOURCE_EVENT };

struct spa_source {
  int kind;
  spa_source_timer_func_t timer_call;
  spa_source_event_func_t event_call;
  void *data;
  UINT_PTR timer;     // the Windows timer while one runs, else 0
  UINT interval_ms;   // what the timer is set to after its first call
  bool once, first;   // one call only; the first call is still to come
  _Atomic bool signalled;
};

struct pw_loop {
  struct spa_source sources[SOURCES];
};
static struct pw_loop loop;

static struct spa_source *source_new(int kind, void *data) {
  for (int i = 0; i < SOURCES; ++i) {
    struct spa_source *s = &loop.sources[i];
    if (s->kind != SOURCE_FREE)
      continue;
    memset(s, 0, sizeof(*s));
    s->kind = kind;
    s->data = data;
    return s;
  }
  return NULL;
}

static void timer_stop(struct spa_source *s) {
  if (s->timer)
    KillTimer(NULL, s->timer);
  s->timer = 0;
}

// Windows calls this from the message loop of the thread that set the
// timer, whichever loop is pumping at the time.
static void CALLBACK timer_fired(HWND window, UINT message, UINT_PTR id,
                                 DWORD time) {
  for (int i = 0; i < SOURCES; ++i) {
    struct spa_source *s = &loop.sources[i];
    if (s->kind != SOURCE_TIMER || s->timer != id)
      continue;
    if (s->once)
      timer_stop(s);
    else if (s->first) {
      s->first = false;
      SetTimer(NULL, s->timer, s->interval_ms, timer_fired);
    }
    // The call may destroy its own source, so nothing touches it after.
    s->timer_call(s->data, 1);
    return;
  }
  // A timer that was destroyed with a message already waiting.
  KillTimer(NULL, id);
}

struct spa_source *pw_loop_add_timer(struct pw_loop *l,
                                     spa_source_timer_func_t func, void *data) {
  struct spa_source *s = source_new(SOURCE_TIMER, data);
  if (s)
    s->timer_call = func;
  return s;
}

static UINT milliseconds(const struct timespec *t) {
  if (!t || (t->tv_sec == 0 && t->tv_nsec == 0))
    return 0;
  uint64_t ms = (uint64_t)t->tv_sec * 1000 + (uint64_t)(t->tv_nsec + 999999) / 1000000;
  return ms > 0x7fffffff ? 0x7fffffff : (UINT)ms;
}

int pw_loop_update_timer(struct pw_loop *l, struct spa_source *s,
                         struct timespec *value, struct timespec *interval,
                         bool absolute) {
  if (!s || s->kind != SOURCE_TIMER || absolute)
    return -1;
  UINT first_ms = milliseconds(value);
  timer_stop(s);
  if (!first_ms)
    return 0;
  s->interval_ms = milliseconds(interval);
  s->once = s->interval_ms == 0;
  s->first = !s->once && s->interval_ms != first_ms;
  s->timer = SetTimer(NULL, 0, first_ms, timer_fired);
  return s->timer ? 0 : -1;
}

struct spa_source *pw_loop_add_event(struct pw_loop *l,
                                     spa_source_event_func_t func, void *data) {
  struct spa_source *s = source_new(SOURCE_EVENT, data);
  if (s)
    s->event_call = func;
  return s;
}

int pw_loop_signal_event(struct pw_loop *l, struct spa_source *s) {
  if (!s || s->kind != SOURCE_EVENT)
    return -1;
  atomic_store(&s->signalled, true);
  if (GetCurrentThreadId() != atomic_load(&audio_thread_id))
    SetEvent(wake);
  return 0;
}

struct spa_source *pw_loop_add_io(struct pw_loop *l, int fd, uint32_t mask,
                                  bool close, spa_source_io_func_t func,
                                  void *data) {
  return NULL;
}

int pw_loop_update_io(struct pw_loop *l, struct spa_source *s, uint32_t mask) {
  return -1;
}

void pw_loop_destroy_source(struct pw_loop *l, struct spa_source *s) {
  if (!s)
    return;
  if (s->kind == SOURCE_TIMER)
    timer_stop(s);
  s->kind = SOURCE_FREE;
}

void shim_run_events(void) {
  for (int i = 0; i < SOURCES; ++i) {
    struct spa_source *s = &loop.sources[i];
    if (s->kind == SOURCE_EVENT && atomic_exchange(&s->signalled, false))
      s->event_call(s->data, 1);
  }
}

bool shim_start(void) {
  main_thread_id = GetCurrentThreadId();
  wake = CreateEventW(NULL, FALSE, FALSE, NULL);
  return wake != NULL;
}

void *shim_wake(void) { return wake; }

void shim_enter_audio_thread(void) {
  atomic_store(&audio_thread_id, GetCurrentThreadId());
}

// --- controls ---------------------------------------------------------------

Control *host_add_control(Host *h, const char *symbol, float minimum,
                          float maximum, float initial, bool output,
                          void *backend) {
  if (h->control_count == MAX_CONTROLS)
    return NULL;
  Control *c = &h->controls[h->control_count++];
  c->symbol = symbol;
  c->minimum = isfinite(minimum) ? minimum : -1e10f;
  c->maximum = isfinite(maximum) ? maximum : 1e10f;
  c->output = output;
  c->reported = NAN;
  atomic_init(&c->desired, isfinite(initial) ? initial : 0);
  atomic_init(&c->observed, isfinite(initial) ? initial : 0);
  atomic_init(&c->moved, false);
  c->backend = backend;
  return c;
}

// Only a change is published. A plugin that answers a value it was sent
// with the same value has moved nothing, and publishing it would make the
// Linux side's next set look older than it is.
static void control_moved(Host *h, Control *c, float value) {
  value = fminf(c->maximum, fmaxf(c->minimum, value));
  float before = atomic_exchange(&c->desired, value);
  atomic_store(&c->observed, value);
  if (before != value)
    winhost_publish((uint32_t)(c - h->controls), value);
}

void host_control_moved(Host *h, Control *c, float value) {
  if (!c->output && isfinite(value))
    control_moved(h, c, value);
}

void host_control_moved_rt(Host *h, Control *c, float value) {
  if (isfinite(value))
    control_moved(h, c, value);
}

// A meter is written by the backend, wherever it reads the plugin, and
// looked at here on the tick, as host.c's tick looks at it before printing.
void shim_publish_meters(Host *h) {
  for (uint32_t i = 0; i < h->control_count; ++i) {
    Control *c = &h->controls[i];
    if (!c->output)
      continue;
    float value = atomic_load(&c->observed);
    if (isfinite(value) && value != c->reported) {
      c->reported = value;
      winhost_publish(i, value);
    }
  }
}

uint32_t host_control_count(const Host *h) { return h->control_count; }
Control *host_control_at(Host *h, uint32_t index) {
  return index < h->control_count ? &h->controls[index] : NULL;
}
float control_desired(const Control *c) { return atomic_load(&c->desired); }
void control_set_desired(Control *c, float value) {
  atomic_store(&c->desired, fminf(c->maximum, fmaxf(c->minimum, value)));
}
void control_set_observed(Control *c, float value) {
  atomic_store(&c->observed, value);
}
bool control_is_output(const Control *c) { return c->output; }
void *control_backend(const Control *c) { return c->backend; }

// --- the host a backend sees ------------------------------------------------

uint32_t host_rate(const Host *h) { return h->rate; }
uint32_t host_max_block(const Host *h) {
  return h->own_block ? OWN_BLOCK_FRAMES : MAX_FRAMES;
}
unsigned host_channels(const Host *h) { return h->channels; }
unsigned long host_window(const Host *h) { return h->window; }
void *host_impl(const Host *h) { return h->impl; }
void host_set_impl(Host *h, void *impl) { h->impl = impl; }
void host_set_has_editor(Host *h, bool has_editor) { h->has_editor = has_editor; }
void host_set_plugin_name(Host *h, const char *name) {
  if (h && name && name[0])
    snprintf(h->plugin_name, sizeof(h->plugin_name), "%s", name);
}

struct pw_loop *host_loop(Host *h) { return &loop; }

bool host_on_main_thread(Host *h) {
  return GetCurrentThreadId() == main_thread_id;
}

bool host_on_audio_thread(Host *h) {
  return GetCurrentThreadId() == atomic_load(&audio_thread_id);
}

void shim_begin_block(void) { block_refused = false; }
bool shim_block_refused(void) { return block_refused; }

// The Linux side counts refused blocks and decides when a run of them is a
// failure; here the block is only marked, which is what crosses.
void host_processing_result(Host *h, bool ok, uint32_t frames) {
  if (!ok)
    block_refused = true;
}

// There is no display to lose on this side.
void host_run_guarded(Host *h, void (*call)(void *), void *argument) {
  call(argument);
}

// The instance cannot go on. Leaving is how the Linux side learns: it
// reports the exit and the daemon replaces the instance. The reason goes
// ahead of it on standard error.
void host_fail(Host *h, const char *why) {
  fprintf(stderr, "%s\n", why);
  fflush(stderr);
  TerminateProcess(GetCurrentProcess(), 1);
}

// --- the dynamic loader -----------------------------------------------------

static char load_error[64];

void *dlopen(const char *file, int mode) {
  int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, file, -1,
                                   NULL, 0);
  WCHAR *path = length > 0 ? HeapAlloc(GetProcessHeap(), 0,
                                       (size_t)length * sizeof(WCHAR))
                           : NULL;
  if (!path) {
    snprintf(load_error, sizeof(load_error), "its path cannot be named");
    return NULL;
  }
  MultiByteToWideChar(CP_UTF8, 0, file, -1, path, length);
  HMODULE module = LoadLibraryW(path);
  if (!module)
    snprintf(load_error, sizeof(load_error), "Windows error %lu",
             (unsigned long)GetLastError());
  HeapFree(GetProcessHeap(), 0, path);
  return module;
}

void *dlsym(void *library, const char *name) {
  return (void *)GetProcAddress(library, name);
}

int dlclose(void *library) { return 0; }

const char *dlerror(void) { return load_error; }
