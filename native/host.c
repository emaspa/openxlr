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
  if (h->editor_source) {
    pw_loop_destroy_source(host_loop(h), h->editor_source);
    h->editor_source = NULL;
  }
  if (h->editor_open && h->backend->editor_lost)
    h->backend->editor_lost(h);
  h->editor_open = false;
  h->display = NULL;
  h->window = 0;
  h->child = 0;
  h->notified_width = h->notified_height = 0;
  h->self_width = h->self_height = 0;
  h->child_width = h->child_height = 0;
  h->settle_ticks = h->settle_width = h->settle_height = 0;
  h->frame_x = h->frame_y = 0;
  h->dragged_at = (struct timespec){0};
}

void host_editor_lost(Host *h) {
  forget_ui(h);
  fputs("editor X11 connection lost; audio continues without it\n", stderr);
}

// A backend's own callbacks (a plugin's timer, say) reach these outside any
// guard, so each one arms its own unless a caller already has.
static bool same_editor_size(Host *h, unsigned width, unsigned height);
static bool dragging(Host *h);
static void update_editor_hints(Host *h);

void host_set_editor_resizable(Host *h, bool resizable) {
  h->editor_resizable = resizable;
}

void host_resize_editor(Host *h, unsigned width, unsigned height) {
  if (!h->display || !h->window || width < 1 || height < 1 ||
      width > 16384 || height > 16384 || dragging(h))
    return;
  h->self_width = width;
  h->self_height = height;
  if (x_escape_armed) {
    if (!h->editor_resizable)
      update_editor_hints(h);
    XResizeWindow(h->display, h->window, width, height);
    return;
  }
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  if (!h->editor_resizable)
    update_editor_hints(h);
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

// A window manager only reads the outer window's hints. Embedded editors
// such as LSP put their limits on the child, so copy the size bounds and
// also enforce them for resize requests that bypass the window manager.
static void update_editor_hints(Host *h) {
  XSizeHints child = {0}, hints = {0};
  long supplied;
  h->editor_min_width = h->editor_min_height = 1;
  h->editor_max_width = h->editor_max_height = 16384;
  if (h->child && XGetWMNormalHints(h->display, h->child, &child, &supplied)) {
    if ((child.flags & PMinSize) && child.min_width > 0 && child.min_height > 0 &&
        child.min_width <= 16384 && child.min_height <= 16384) {
      h->editor_min_width = (unsigned)child.min_width;
      h->editor_min_height = (unsigned)child.min_height;
    }
    if ((child.flags & PMaxSize) && child.max_width >= (int)h->editor_min_width &&
        child.max_height >= (int)h->editor_min_height) {
      if (child.max_width < 16384)
        h->editor_max_width = (unsigned)child.max_width;
      if (child.max_height < 16384)
        h->editor_max_height = (unsigned)child.max_height;
    }
  }
  if (!h->editor_resizable && h->self_width && h->self_height) {
    h->editor_min_width = h->editor_max_width = h->self_width;
    h->editor_min_height = h->editor_max_height = h->self_height;
  }
  hints.flags = PMinSize | PMaxSize;
  hints.min_width = (int)h->editor_min_width;
  hints.min_height = (int)h->editor_min_height;
  hints.max_width = (int)h->editor_max_width;
  hints.max_height = (int)h->editor_max_height;
  XSetWMNormalHints(h->display, h->window, &hints);
}

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
    XSelectInput(h->display, h->child, StructureNotifyMask | PropertyChangeMask);
    XWindowAttributes attributes;
    if (XGetWindowAttributes(h->display, h->child, &attributes) &&
        attributes.width > 0 && attributes.height > 0) {
      h->self_width = (unsigned)attributes.width;
      h->self_height = (unsigned)attributes.height;
      update_editor_hints(h);
      XResizeWindow(h->display, h->window, h->self_width, h->self_height);
    }
  }
  if (children)
    XFree(children);
}

static void close_ui(Host *h) {
  if (h->editor_source) {
    pw_loop_destroy_source(host_loop(h), h->editor_source);
    h->editor_source = NULL;
  }
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
// life happens here, in raise_ui, in the event pump or in a backend's guarded
// callback. Watch the X connection so a drag need not wait for the idle tick.
static void editor_events(void *data, int fd, uint32_t mask);

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
  h->editor_resizable = true;
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
                 StructureNotifyMask | SubstructureNotifyMask | FocusChangeMask);
    h->editor_open = h->backend->editor_open(h);
    if (h->editor_open) {
      fit_to_child(h);  // before mapping, so the frame never opens wrong
      XMapRaised(h->display, h->window);
      XFlush(h->display);
      h->editor_source = pw_loop_add_io(host_loop(h), ConnectionNumber(h->display),
                                        SPA_IO_IN | SPA_IO_HUP | SPA_IO_ERR,
                                        false, editor_events, h);
      if (h->backend->editor_coordinate_nudge)
        h->settle_ticks = 6;   // once it is on screen, teach it where that is
    }
  }
  x_escape_armed = 0;
  if (!h->editor_open)
    close_ui(h);
  return h->editor_open;
}

// Whether the user has the frame's corner right now: it changed size for a
// reason that was not the plugin's, moments ago. A plugin's request is not
// answered until they let go, so the window follows the hand holding it.
static bool dragging(Host *h) {
  if (h->dragged_at.tv_sec == 0 && h->dragged_at.tv_nsec == 0)
    return false;
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  long since = (now.tv_sec - h->dragged_at.tv_sec) * 1000 +
               (now.tv_nsec - h->dragged_at.tv_nsec) / 1000000;
  return since < 400;
}

// X reports moves and echoes as well as new sizes. Only skip the last size:
// returning to an earlier size is normal when the user reverses a drag.
static bool same_editor_size(Host *h, unsigned width, unsigned height) {
  bool seen = h->notified_width == width && h->notified_height == height;
  h->notified_width = width;
  h->notified_height = height;
  return seen;
}

// One pass of the editor: its events and the backend's idle work. Losing
// the display here costs the editor and nothing else.
static void pump_editor(Host *h, bool idle) {
  if (sigsetjmp(x_escape, 0)) {
    x_escape_armed = 0;
    host_editor_lost(h);
    return;
  }
  x_escape_armed = 1;
  unsigned frame_width = 0, frame_height = 0, plugin_width = 0, plugin_height = 0;
  bool frame_resized = false, plugin_resized = false;
  while (XPending(h->display)) {
    XEvent event;
    XNextEvent(h->display, &event);
    if (event.type == ClientMessage &&
        (Atom)event.xclient.data.l[0] == h->close_message) {
      x_escape_armed = 0;
      close_ui(h);
      return;
    }
    // Whether the frame holds the keyboard focus. The plugin is told, since
    // one running under Wine will not take the mouse until it knows.
    if ((event.type == FocusIn || event.type == FocusOut) &&
        event.xfocus.window == h->window && h->backend->editor_focus)
      h->backend->editor_focus(h, event.type == FocusIn);
    // A plugin that builds its window after the editor opens is found when
    // that window appears.
    if (!h->child && (event.type == MapNotify || event.type == CreateNotify))
      fit_to_child(h);
    if (event.type == PropertyNotify && event.xproperty.window == h->child &&
        event.xproperty.atom == XA_WM_NORMAL_HINTS)
      update_editor_hints(h);
    // The window moved. Where a plugin thinks it is decides where it thinks
    // the pointer is, so it is taught the new place the only way it listens.
    if (event.type == ConfigureNotify && event.xconfigure.window == h->window &&
        (event.xconfigure.x != h->frame_x || event.xconfigure.y != h->frame_y)) {
      h->frame_x = event.xconfigure.x;
      h->frame_y = event.xconfigure.y;
      if (h->backend->editor_coordinate_nudge)
        h->settle_ticks = 9;
    }
    // Sizes are noted here and settled once the queue is empty. Dragging a
    // corner fills the queue with them, and telling a plugin about every
    // one, each a call across to its own process, leaves it a size behind
    // the hand. It hears the last one instead, which is the one that counts.
    if (event.type == ConfigureNotify && h->child) {
      const XConfigureEvent *c = &event.xconfigure;
      if (c->window == h->window && c->width > 0 && c->height > 0) {
        frame_width = (unsigned)c->width;
        frame_height = (unsigned)c->height;
        frame_resized = true;
      } else if (c->window == h->child && c->width > 0 && c->height > 0) {
        plugin_width = (unsigned)c->width;
        plugin_height = (unsigned)c->height;
        plugin_resized = true;
      }
    }
  }
  if (h->child) {
    XWindowAttributes attributes;
    // The frame follows the plugin only when the plugin resized its own
    // window: a size the frame gave it is an echo, and while the user has
    // the corner the frame belongs to the user.
    if (plugin_resized && !frame_resized && !dragging(h) &&
        (plugin_width != h->child_width || plugin_height != h->child_height) &&
        XGetWindowAttributes(h->display, h->window, &attributes) &&
        ((unsigned)attributes.width != plugin_width ||
         (unsigned)attributes.height != plugin_height)) {
      h->self_width = plugin_width;
      h->self_height = plugin_height;
      update_editor_hints(h);
      XResizeWindow(h->display, h->window, plugin_width, plugin_height);
    }
    if (frame_resized) {
      bool user_size = frame_width != h->self_width || frame_height != h->self_height;
      if (user_size && h->editor_resizable)
        clock_gettime(CLOCK_MONOTONIC, &h->dragged_at);
      if (user_size) {
        unsigned width = frame_width, height = frame_height;
        if (width < h->editor_min_width) width = h->editor_min_width;
        if (height < h->editor_min_height) height = h->editor_min_height;
        if (width > h->editor_max_width) width = h->editor_max_width;
        if (height > h->editor_max_height) height = h->editor_max_height;
        if (h->editor_resizable && h->backend->editor_constrain)
          h->backend->editor_constrain(h, &width, &height);
        if (width > 0 && height > 0 && width <= 16384 && height <= 16384 &&
            (width != frame_width || height != frame_height)) {
          frame_width = h->self_width = width;
          frame_height = h->self_height = height;
          XResizeWindow(h->display, h->window, width, height);
        }
      }
      // Let the plugin lay out before enlarging its embedding window, which
      // otherwise exposes pixels the plugin has not drawn yet.
      if (h->backend->editor_resized &&
          !same_editor_size(h, frame_width, frame_height))
        h->backend->editor_resized(h, frame_width, frame_height);
      if (XGetWindowAttributes(h->display, h->child, &attributes) &&
          ((unsigned)attributes.width != frame_width ||
           (unsigned)attributes.height != frame_height)) {
        XResizeWindow(h->display, h->child, frame_width, frame_height);
      }
      h->child_width = frame_width;
      h->child_height = frame_height;
    }
  }
  // A window that has just opened or moved has to change size once for a
  // plugin bridged from Windows to find out where it is; until it does, its
  // clicks land as far from the pointer as the window is from the corner of
  // the screen. A pixel smaller, then back a moment later, teaches it. The
  // two halves are ticks apart so the plugin has laid itself out in between,
  // and it costs a plugin that needed none of this one relayout.
  if (idle && h->settle_ticks > 0 && --h->settle_ticks == 0) {
    if (h->settle_height > 0) {
      h->self_width = h->settle_width;
      h->self_height = h->settle_height;
      if (!h->editor_resizable)
        update_editor_hints(h);
      XResizeWindow(h->display, h->window, h->settle_width, h->settle_height);
      XFlush(h->display);
      h->settle_height = 0;
    } else if (h->child) {
      XWindowAttributes attributes;
      if (XGetWindowAttributes(h->display, h->window, &attributes) &&
          attributes.width > 1 && attributes.height > 1) {
        h->settle_width = (unsigned)attributes.width;
        h->settle_height = (unsigned)attributes.height;
        h->self_width = h->settle_width;
        h->self_height = h->settle_height - 1;
        if (!h->editor_resizable)
          update_editor_hints(h);
        XResizeWindow(h->display, h->window, h->settle_width, h->settle_height - 1);
        XFlush(h->display);
        h->settle_ticks = 4;   // put it back once the plugin has caught up
      }
    }
  }
  bool finished = idle && h->backend->editor_idle && h->backend->editor_idle(h);
  XFlush(h->display);
  x_escape_armed = 0;
  if (finished)
    close_ui(h);
}

static void editor_events(void *data, int fd, uint32_t mask) {
  Host *h = data;
  if (mask & (SPA_IO_HUP | SPA_IO_ERR)) {
    host_editor_lost(h);
    return;
  }
  if (h->editor_open)
    pump_editor(h, false);
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
  if (!strcmp(line, "show")) {
    // Two different disappointments, and the user can act on only one.
    if (!h->has_editor)
      puts("ui unavailable: this plugin has no editor the host can show");
    else
      puts(open_ui(h) ? "ui opened"
                      : "ui unavailable: no X display; the daemon has none "
                        "from your desktop session");
  }
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
    pump_editor(h, true);
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
