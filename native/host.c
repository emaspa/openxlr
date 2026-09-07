// SPDX-License-Identifier: GPL-3.0-only
// The process around one plugin: its PipeWire node, its command pipe, its
// editor window and its threads. The audio callback never allocates, logs,
// performs IPC or calls the editor.
#include "host.h"
#include "icon.h"

#include <X11/Xatom.h>
#include <X11/Xutil.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

sigjmp_buf x_escape;
volatile sig_atomic_t x_escape_armed;

static int report_x_error(Display *display, XErrorEvent *event) {
  char text[256];
  XGetErrorText(display, event->error_code, text, sizeof(text));
  fprintf(stderr, "editor X11 error: %s (request %u)\n", text,
          event->request_code);
  return 0;
}

static int lost_x_connection(Display *display) {
  if (x_escape_armed) {
    x_escape_armed = 0;
    siglongjmp(x_escape, 1);
  }
  // Xlib only calls this from inside one of its own functions, and every one
  // this host calls is armed. Leave rather than return: Xlib exits either way.
  _exit(1);
}

// --- what backends call ----------------------------------------------------

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

void host_control_moved(Host *h, Control *c, float value) {
  if (c->output || !isfinite(value))
    return;
  value = fminf(c->maximum, fmaxf(c->minimum, value));
  atomic_store(&c->desired, value);
  printf("control %s %.9g\n", c->symbol, value);
}

void host_control_moved_rt(Host *h, Control *c, float value) {
  if (!isfinite(value))
    return;
  value = fminf(c->maximum, fmaxf(c->minimum, value));
  atomic_store(&c->desired, value);
  atomic_store(&c->observed, value);
  atomic_store(&c->moved, true);
}

// Drop the editor without talking to X, for a connection that is already
// gone. What the plugin built inside the window cannot be cleaned up through
// a dead connection, so it stays until this process exits.
static void forget_ui(Host *h) {
  if (h->editor_open && h->backend->editor_lost)
    h->backend->editor_lost(h);
  h->editor_open = false;
  h->display = NULL;
  h->window = 0;
  h->child = 0;
}

void host_editor_lost(Host *h) {
  forget_ui(h);
  fputs("editor X11 connection lost; audio continues without it\n", stderr);
}

// A backend's own callbacks (a plugin's timer, say) reach these outside any
// guard, so each one arms its own unless a caller already has.
void host_resize_editor(Host *h, unsigned width, unsigned height) {
  if (!h->display || !h->window || width < 1 || height < 1 ||
      width > 16384 || height > 16384)
    return;
  if (x_escape_armed) {
    XResizeWindow(h->display, h->window, width, height);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  XResizeWindow(h->display, h->window, width, height);
  XFlush(h->display);
  x_escape_armed = 0;
}

void host_show_editor(Host *h, bool show) {
  if (!h->display || !h->window)
    return;
  if (x_escape_armed) {
    if (show)
      XMapRaised(h->display, h->window);
    else
      XUnmapWindow(h->display, h->window);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  if (show)
    XMapRaised(h->display, h->window);
  else
    XUnmapWindow(h->display, h->window);
  XFlush(h->display);
  x_escape_armed = 0;
}

// Any plugin callback that may touch X runs inside the escape, so a lost
// display costs the editor and not the process. A caller already inside
// the escape is simply called through.
void host_run_guarded(Host *h, void (*call)(void *), void *argument) {
  if (x_escape_armed) {
    call(argument);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  call(argument);
  x_escape_armed = 0;
}

uint32_t host_rate(const Host *h) { return h->rate; }
unsigned host_channels(const Host *h) { return h->channels; }
unsigned long host_window(const Host *h) { return h->window; }
void *host_impl(const Host *h) { return h->impl; }
void host_set_impl(Host *h, void *impl) { h->impl = impl; }
void host_set_has_editor(Host *h, bool has_editor) { h->has_editor = has_editor; }
void host_set_plugin_name(Host *h, const char *name) {
  // A scan has no host: it describes a bundle's plugins without running one.
  if (h && name && name[0])
    snprintf(h->plugin_name, sizeof(h->plugin_name), "%s", name);
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

void host_fail(Host *h, const char *why) {
  fprintf(stderr, "%s; restart the chain\n", why);
  atomic_store(&h->audio_error, true);
}

struct pw_loop *host_loop(Host *h) { return pw_main_loop_get_loop(h->loop); }

bool host_on_main_thread(Host *h) {
  return pthread_equal(pthread_self(), h->main_thread);
}

bool host_on_audio_thread(Host *h) {
  return atomic_load(&h->audio_thread_known) &&
         pthread_equal(pthread_self(), h->audio_thread);
}

// --- audio ------------------------------------------------------------------

static void process_audio(void *data, struct spa_io_position *position) {
  Host *h = data;
  if (!atomic_load(&h->audio_thread_known)) {
    h->audio_thread = pthread_self();
    atomic_store(&h->audio_thread_known, true);
  }
  uint32_t frames = position->clock.duration;
  bool broken = frames > MAX_FRAMES || position->clock.rate.denom != h->rate;
  float *in[MAX_CHANNELS], *out[MAX_CHANNELS];
  for (unsigned i = 0; i < h->channels; ++i) {
    float *buffer = pw_filter_get_dsp_buffer(h->in_ports[i], frames);
    if (!buffer) {
      memset(h->silence, 0, sizeof(float) * frames);
      buffer = h->silence;
    }
    in[i] = buffer;
    out[i] = pw_filter_get_dsp_buffer(h->out_ports[i], frames);
    if (!out[i])
      out[i] = h->scratch;
  }
  if (broken) {
    atomic_store(&h->audio_error, true);
    for (unsigned i = 0; i < h->channels; ++i)
      memset(out[i], 0, sizeof(float) * frames);
    return;
  }
  h->backend->process(h, frames, in, out);
}

static void state_changed(void *data, enum pw_filter_state old,
                          enum pw_filter_state state, const char *error) {
  Host *h = data;
  if (state == PW_FILTER_STATE_ERROR) {
    fprintf(stderr, "PipeWire: %s\n", error ? error : "disconnected");
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  } else if (state == PW_FILTER_STATE_PAUSED)
    puts("ready");
}

static const struct pw_filter_events filter_events = {
    PW_VERSION_FILTER_EVENTS, .state_changed = state_changed,
    .process = process_audio};

// --- the editor's window ----------------------------------------------------

// A plugin builds its interface in a window of its own inside ours, at
// whatever size it wants, and many never call the host's resize. Take the
// size from that window, and watch it so later changes follow.
static void fit_to_child(Host *h) {
  Window root, parent, *children = NULL;
  unsigned count = 0;
  if (!XQueryTree(h->display, h->window, &root, &parent, &children, &count))
    return;
  if (count > 0) {
    h->child = children[count - 1];
    XSelectInput(h->display, h->child, StructureNotifyMask);
    XWindowAttributes attributes;
    if (XGetWindowAttributes(h->display, h->child, &attributes) &&
        attributes.width > 0 && attributes.height > 0)
      XResizeWindow(h->display, h->window, (unsigned)attributes.width,
                    (unsigned)attributes.height);
  }
  if (children)
    XFree(children);
}

static void close_ui(Host *h) {
  if (!h->display) {
    forget_ui(h);
    return;
  }
  if (sigsetjmp(x_escape, 0) == 0) {
    x_escape_armed = 1;
    if (h->editor_open)
      h->backend->editor_close(h);
    h->editor_open = false;
    if (h->window)
      XDestroyWindow(h->display, h->window);
    XCloseDisplay(h->display);
  }
  x_escape_armed = 0;
  forget_ui(h);
}

// Raise a window that is already up. A failure here is a lost connection.
static bool raise_ui(Host *h) {
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    forget_ui(h);
    return false;
  }
  x_escape_armed = 1;
  XMapRaised(h->display, h->window);
  XFlush(h->display);
  x_escape_armed = 0;
  return true;
}

// Open the window and hand it to the backend. Every X call of the editor's
// life happens here, in raise_ui, in the tick or in a backend's guarded
// callback.
static bool open_ui(Host *h) {
  if (h->editor_open)
    return raise_ui(h);
  if (!h->has_editor)
    return false;
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    forget_ui(h);
    return false;
  }
  x_escape_armed = 1;
  h->display = XOpenDisplay(NULL);
  if (h->display) {
    h->window = XCreateSimpleWindow(h->display, DefaultRootWindow(h->display),
                                    0, 0, 900, 600, 0, 0, 0x16181d);
    char title[160];
    snprintf(title, sizeof(title), "OpenXLR - %s",
             h->plugin_name[0] ? h->plugin_name : h->backend->name);
    XStoreName(h->display, h->window, title);
    // The same class as the main window, so the desktop files it with the
    // application, and the application's icon, since no plugin format
    // carries one of its own.
    XClassHint class_hint = {(char *)"openxlr", (char *)"OpenXLR.UI"};
    XSetClassHint(h->display, h->window, &class_hint);
    XChangeProperty(h->display, h->window,
                    XInternAtom(h->display, "_NET_WM_ICON", False), XA_CARDINAL,
                    32, PropModeReplace, (const unsigned char *)openxlr_icon,
                    (int)openxlr_icon_length);
    XChangeProperty(h->display, h->window,
                    XInternAtom(h->display, "_OPENXLR_NODE", False), XA_STRING,
                    8, PropModeReplace, (const unsigned char *)h->node_name,
                    (int)strlen(h->node_name));
    h->close_message = XInternAtom(h->display, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(h->display, h->window, &h->close_message, 1);
    XSelectInput(h->display, h->window,
                 StructureNotifyMask | SubstructureNotifyMask);
    h->editor_open = h->backend->editor_open(h);
    if (h->editor_open) {
      fit_to_child(h);  // before mapping, so the frame never opens wrong
      XMapRaised(h->display, h->window);
      XFlush(h->display);
    }
  }
  x_escape_armed = 0;
  if (!h->editor_open)
    close_ui(h);
  return h->editor_open;
}

// One pass of the editor: its events and the backend's idle work. Losing
// the display here costs the editor and nothing else.
static void pump_editor(Host *h) {
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  while (XPending(h->display)) {
    XEvent event;
    XNextEvent(h->display, &event);
    if (event.type == ClientMessage &&
        (Atom)event.xclient.data.l[0] == h->close_message) {
      x_escape_armed = 0;
      close_ui(h);
      return;
    }
    // A plugin that builds its window after the editor opens is found when
    // that window appears.
    if (!h->child && (event.type == MapNotify || event.type == CreateNotify))
      fit_to_child(h);
    // Keep the frame and the plugin's window the same size, whichever of
    // the two changed. Each resize is skipped when the sizes already agree,
    // so the two notifications cannot chase each other.
    if (event.type == ConfigureNotify && h->child) {
      const XConfigureEvent *c = &event.xconfigure;
      XWindowAttributes attributes;
      if (c->window == h->child &&
          XGetWindowAttributes(h->display, h->window, &attributes) &&
          (attributes.width != c->width || attributes.height != c->height))
        XResizeWindow(h->display, h->window, (unsigned)c->width,
                      (unsigned)c->height);
      else if (c->window == h->window &&
               XGetWindowAttributes(h->display, h->child, &attributes) &&
               (attributes.width != c->width || attributes.height != c->height)) {
        XResizeWindow(h->display, h->child, (unsigned)c->width,
                      (unsigned)c->height);
        if (h->backend->editor_resized && c->width > 0 && c->height > 0)
          h->backend->editor_resized(h, (unsigned)c->width, (unsigned)c->height);
      }
    }
  }
  bool finished = h->backend->editor_idle && h->backend->editor_idle(h);
  x_escape_armed = 0;
  if (finished)
    close_ui(h);
}

// --- the command pipe -------------------------------------------------------

static bool set_control(Host *h, const char *symbol, float value) {
  if (!isfinite(value))
    return false;
  for (uint32_t i = 0; i < h->control_count; ++i) {
    Control *c = &h->controls[i];
    if (!c->output && !strcmp(c->symbol, symbol)) {
      atomic_store(&c->desired, fminf(c->maximum, fmaxf(c->minimum, value)));
      return true;
    }
  }
  return false;
}

static void command(Host *h, char *line) {
  char symbol[MAX_SYMBOL + 1], extra;
  float value;
  if (!strcmp(line, "show"))
    puts(open_ui(h) ? "ui opened"
                    : "ui unavailable: the plugin has no editor this host can "
                      "show, or the display could not be opened");
  else if (!strcmp(line, "hide"))
    close_ui(h);
  else if (!strcmp(line, "quit"))
    pw_main_loop_quit(h->loop);
  else if (sscanf(line, "set %255s %f %c", symbol, &value, &extra) == 2) {
    if (!set_control(h, symbol, value))
      fprintf(stderr, "invalid control: %s\n", symbol);
  }
}

static void read_commands(void *data, int fd, uint32_t mask) {
  Host *h = data;
  ssize_t count =
      read(fd, h->input + h->input_size, sizeof(h->input) - h->input_size - 1);
  if (count == 0 || (count < 0 && errno != EAGAIN)) {
    pw_main_loop_quit(h->loop);
    return;
  }
  if (count < 0)
    return;
  h->input_size += (size_t)count;
  h->input[h->input_size] = 0;
  char *start = h->input, *end;
  while ((end = strchr(start, '\n'))) {
    *end = 0;
    command(h, start);
    start = end + 1;
  }
  h->input_size -= (size_t)(start - h->input);
  memmove(h->input, start, h->input_size);
  if (h->input_size == sizeof(h->input) - 1) {
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
  }
}

// --- the main-thread tick ---------------------------------------------------

static void tick(void *data, uint64_t expirations) {
  Host *h = data;
  // Editor progress is separate from audio progress. A blocked editor must
  // not cause the supervisor to tear down a working audio instance.
  if (++h->heartbeat_ticks == 30) {
    puts("ui-heartbeat");
    h->heartbeat_ticks = 0;
  }
  if (atomic_load(&h->audio_error)) {
    fputs("unsupported audio quantum or sample-rate change; restart the "
          "chain\n",
          stderr);
    h->exit_code = 1;
    pw_main_loop_quit(h->loop);
    return;
  }
  for (uint32_t i = 0; i < h->control_count; ++i) {
    Control *c = &h->controls[i];
    if (c->output) {
      float value = atomic_load(&c->observed);
      if (isfinite(value) && value != c->reported) {
        printf("meter %s %.9g\n", c->symbol, value);
        c->reported = value;
      }
    } else if (atomic_exchange(&c->moved, false))
      printf("control %s %.9g\n", c->symbol, atomic_load(&c->desired));
  }
  if (h->backend->main_thread)
    h->backend->main_thread(h);
  if (h->editor_open)
    pump_editor(h);
}

static void stop(void *data, int signal) {
  pw_main_loop_quit(((Host *)data)->loop);
}

static void *monitor_audio(void *data) {
  Host *h = data;
  while (!atomic_load(&h->monitor_stop)) {
    // The redirected input pipe belongs to the daemon process, unlike
    // PDEATHSIG, which follows the short-lived .NET thread that spawned us.
    // Observe HUP without consuming commands; also works with a stuck UI.
    struct pollfd input = {STDIN_FILENO, POLLHUP | POLLERR, 0};
    if (poll(&input, 1, 1000) > 0 && (input.revents & (POLLHUP | POLLERR)))
      _exit(0);
    puts("heartbeat");
  }
  return NULL;
}

// --- main -------------------------------------------------------------------

static const Backend *backend_named(const char *name) {
  if (!strcmp(name, "lv2"))
    return &lv2_backend;
  if (!strcmp(name, "clap"))
    return &clap_backend;
  if (!strcmp(name, "vst3"))
    return &vst3_backend;
  return NULL;
}

int main(int argc, char **argv) {
  if (argc == 3 && !strcmp(argv[1], "scan-clap"))
    return clap_scan(argv[2]);
  if (argc == 3 && !strcmp(argv[1], "scan-vst3"))
    return vst3_scan(argv[2]);
  const Backend *backend = argc >= 2 ? backend_named(argv[1]) : NULL;
  int first = backend ? 2 + backend->argument_count : 0;  // NODE
  if (!backend || argc < first + 3) {
    fputs("usage: openxlr-lv2-host lv2 URI NODE CHANNELS RATE "
          "[SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host clap FILE ID NODE CHANNELS RATE "
          "[SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host vst3 BUNDLE CLASS-ID NODE CHANNELS RATE "
          "[SYMBOL=VALUE ...]\n"
          "       openxlr-lv2-host scan-clap FILE\n"
          "       openxlr-lv2-host scan-vst3 BUNDLE\n",
          stderr);
    return 2;
  }
  int channels = atoi(argv[first + 1]);
  static Host h;  // large: the control table lives here, not on the stack
  h.backend = backend;
  h.node_name = argv[first];
  h.rate = (uint32_t)strtoul(argv[first + 2], NULL, 10);
  h.channels = (unsigned)channels;
  h.main_thread = pthread_self();
  if (channels < 1 || channels > MAX_CHANNELS || h.rate < 8000 ||
      h.rate > 384000)
    return 2;
  setvbuf(stdout, NULL, _IOLBF, 0);
  XSetErrorHandler(report_x_error);
  XSetIOErrorHandler(lost_x_connection);
  h.silence = calloc(MAX_FRAMES, sizeof(float));
  h.scratch = calloc(MAX_FRAMES, sizeof(float));
  if (!h.silence || !h.scratch)
    return 1;

  pw_init(NULL, NULL);
  h.loop = pw_main_loop_new(NULL);
  if (!h.loop)
    return 1;
  char rate[32], description[64];
  snprintf(rate, sizeof(rate), "1/%u", h.rate);
  snprintf(description, sizeof(description), "OpenXLR %s", backend->name);
  h.filter = pw_filter_new_simple(
      pw_main_loop_get_loop(h.loop), h.node_name,
      pw_properties_new(PW_KEY_NODE_NAME, h.node_name, PW_KEY_NODE_DESCRIPTION,
                        description, PW_KEY_MEDIA_TYPE, "Audio",
                        PW_KEY_MEDIA_CATEGORY, "Filter", PW_KEY_MEDIA_ROLE,
                        "DSP", PW_KEY_NODE_RATE, rate, "node.lock-rate", "true",
                        "node.autoconnect", "false", NULL),
      &filter_events, &h);
  if (!h.filter) {
    h.exit_code = 1;
    goto cleanup;
  }
  for (unsigned i = 0; i < h.channels; ++i) {
    for (int output = 0; output < 2; ++output) {
      char name[32];
      snprintf(name, sizeof(name), "%s_%u", output ? "capture" : "playback",
               i);
      void *port = pw_filter_add_port(
          h.filter, output ? PW_DIRECTION_OUTPUT : PW_DIRECTION_INPUT,
          PW_FILTER_PORT_FLAG_MAP_BUFFERS, 1,
          pw_properties_new(PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                            PW_KEY_PORT_NAME, name, PW_KEY_AUDIO_CHANNEL,
                            h.channels == 1 ? "MONO"
                            : i == 0        ? "FL"
                                            : "FR",
                            NULL),
          NULL, 0);
      if (!port) {
        h.exit_code = 1;
        goto cleanup;
      }
      if (output)
        h.out_ports[i] = port;
      else
        h.in_ports[i] = port;
    }
  }

  if (!backend->load(&h, argv + 2)) {
    h.exit_code = 1;
    goto cleanup;
  }
  for (int i = first + 3; i < argc; ++i) {
    char *equals = strchr(argv[i], '=');
    if (!equals) {
      h.exit_code = 2;
      goto cleanup;
    }
    *equals = 0;
    char *end;
    float value = strtof(equals + 1, &end);
    if (*end || !set_control(&h, argv[i], value)) {
      h.exit_code = 2;
      goto cleanup;
    }
  }
  if (!backend->activate(&h)) {
    h.exit_code = 1;
    goto cleanup;
  }
  if (pw_filter_connect(h.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
    h.exit_code = 1;
    goto deactivate;
  }
  fcntl(STDIN_FILENO, F_SETFL, O_NONBLOCK);
  struct pw_loop *loop = pw_main_loop_get_loop(h.loop);
  pw_loop_add_io(loop, STDIN_FILENO, SPA_IO_IN | SPA_IO_HUP, false,
                 read_commands, &h);
  h.timer = pw_loop_add_timer(loop, tick, &h);
  struct timespec interval = {0, 33333333};
  pw_loop_update_timer(loop, h.timer, &interval, &interval, false);
  pw_loop_add_signal(loop, SIGTERM, stop, &h);
  pw_loop_add_signal(loop, SIGINT, stop, &h);
  pthread_t monitor;
  if (pthread_create(&monitor, NULL, monitor_audio, &h)) {
    h.exit_code = 1;
    goto deactivate;
  }
  pw_main_loop_run(h.loop);
  atomic_store(&h.monitor_stop, true);
  pthread_join(monitor, NULL);
  pw_filter_disconnect(h.filter);
deactivate:
  backend->deactivate(&h);
cleanup:
  close_ui(&h);
  if (h.impl)
    backend->unload(&h);
  if (h.filter)
    pw_filter_destroy(h.filter);
  if (h.loop)
    pw_main_loop_destroy(h.loop);
  free(h.silence);
  free(h.scratch);
  return h.exit_code;
}
