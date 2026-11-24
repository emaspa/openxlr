// SPDX-License-Identifier: GPL-3.0-only
// Exercise the actual host event loop with a plugin on a separate X connection.
// clap.c is compiled in as well, to drive the CLAP editor requests through a
// fake plugin, so this links without clap.o.
#define main host_program_main
#include "../host.c"
#undef main
#include "../clap.c"
#include <assert.h>
#include <sys/wait.h>

static Display *plugin_display;
static Window plugin_window;
static unsigned resize_calls, idle_calls, constrain_calls, width_seen, height_seen;
static bool constrain_sizes, answer_resize;
static bool fixed_editor;

static bool test_open(Host *h) {
  plugin_display = XOpenDisplay(NULL);
  assert(plugin_display);
  XSetWindowAttributes attributes = {.override_redirect = True};
  XChangeWindowAttributes(h->display, h->window, CWOverrideRedirect, &attributes);
  XSync(h->display, False);
  plugin_window = XCreateSimpleWindow(plugin_display, h->window,
                                      0, 0, 64, 48, 0, 0, 0);
  XMapWindow(plugin_display, plugin_window);
  XSync(plugin_display, False);
  host_set_editor_resizable(h, !fixed_editor);
  return true;
}

static void test_close(Host *h) {
  XDestroyWindow(plugin_display, plugin_window);
  XCloseDisplay(plugin_display);
  plugin_display = NULL;
}

static void test_resize(Host *h, unsigned width, unsigned height) {
  ++resize_calls;
  width_seen = width;
  height_seen = height;
  // Simulate a bridged plugin laying out on its own connection.
  XResizeWindow(plugin_display, plugin_window, width, height);
  XSync(plugin_display, False);
  if (answer_resize)
    host_resize_editor(h, width + 1, height + 1);
}

static void test_constrain(Host *h, unsigned *width, unsigned *height) {
  ++constrain_calls;
  if (constrain_sizes) {
    *width -= *width % 2;
    *height -= *height % 2;
  }
}

static bool test_idle(Host *h) {
  ++idle_calls;
  return false;
}

static const Backend test_backend = {
    .name = "resize test", .editor_open = test_open, .editor_close = test_close,
    .editor_idle = test_idle, .editor_resized = test_resize,
    .editor_constrain = test_constrain, .editor_coordinate_nudge = true};

// A bridged plugin as yabridge builds it: the bridge's wrapper inside the
// frame, and in it the Wine window that draws the plugin, left unmapped the
// way some desktops leave it. A native window beside it, also unmapped, is
// the plugin's own business and stays as it is.
static Window wine_window_id, native_window_id;

static Window unmapped_child(Window parent, const char *class_name) {
  Window window = XCreateSimpleWindow(plugin_display, parent, 0, 0, 64, 48, 0, 0, 0);
  XClassHint hint = {(char *)class_name, (char *)class_name};
  XSetClassHint(plugin_display, window, &hint);
  return window;
}

static bool wine_open(Host *h) {
  assert(test_open(h));
  wine_window_id = unmapped_child(plugin_window, "yabridge-host.exe");
  native_window_id = unmapped_child(plugin_window, "native-plugin");
  XSync(plugin_display, False);
  return true;
}

static int map_state(Window window) {
  XWindowAttributes attributes;
  assert(XGetWindowAttributes(plugin_display, window, &attributes));
  return attributes.map_state;
}

// A backend that learns only while opening that the plugin has no editor,
// as the VST3 backend does now that it creates no view while loading.
static bool refuse_open(Host *h) {
  host_set_has_editor(h, false);
  return false;
}

// Run one command as the daemon would and hand back the reply line.
static void reply_to(Host *h, const char *line, char *reply, size_t size) {
  FILE *capture = tmpfile();
  assert(capture);
  fflush(stdout);
  int saved = dup(STDOUT_FILENO);
  assert(saved >= 0 && dup2(fileno(capture), STDOUT_FILENO) >= 0);
  char command_line[32];
  snprintf(command_line, sizeof(command_line), "%s", line);
  command(h, command_line);
  fflush(stdout);
  assert(dup2(saved, STDOUT_FILENO) >= 0);
  close(saved);
  rewind(capture);
  assert(fgets(reply, (int)size, capture));
  reply[strcspn(reply, "\n")] = 0;
  fclose(capture);
}

static void drain(Host *h) {
  // No host timer is installed. Only readiness of the real X connection can
  // deliver these events. The old 30 Hz polling path cannot pass this test.
  for (unsigned i = 0; i < 20; ++i)
    pw_loop_iterate(host_loop(h), 1);
}

static void resize_to(Host *h, unsigned width, unsigned height) {
  XResizeWindow(plugin_display, h->window, width, height);
  XSync(plugin_display, False);
  drain(h);
}

static void assert_size(Host *h, unsigned width, unsigned height) {
  XWindowAttributes frame, child;
  assert(XGetWindowAttributes(plugin_display, h->window, &frame));
  assert(XGetWindowAttributes(plugin_display, plugin_window, &child));
  assert((unsigned)frame.width == width && (unsigned)frame.height == height);
  assert((unsigned)child.width == width && (unsigned)child.height == height);
  assert(width_seen == width && height_seen == height);
}

static void assert_bounds(Host *h, int min_width, int min_height,
                          int max_width, int max_height) {
  XSizeHints hints;
  long supplied;
  assert(XGetWMNormalHints(plugin_display, h->window, &hints, &supplied));
  assert((hints.flags & (PMinSize | PMaxSize)) == (PMinSize | PMaxSize));
  assert(hints.min_width == min_width && hints.min_height == min_height);
  assert(hints.max_width == max_width && hints.max_height == max_height);
}

static void *lose_connection(void *unused) {
  lost_x_connection(NULL);
  return NULL;
}

// What a lost connection does with the escape armed on the main thread, in a
// child so an exit can be told from a jump: 0 when it jumped back, 1 when
// the handler ended the process, 2 if it returned.
static int lost_connection_outcome(bool on_other_thread) {
  pid_t child = fork();
  assert(child >= 0);
  if (child == 0) {
    x_main_thread = pthread_self();
    if (sigsetjmp(x_escape, 0))
      _exit(0);
    x_escape_armed = 1;
    if (on_other_thread) {
      pthread_t thread;
      if (pthread_create(&thread, NULL, lose_connection, NULL) != 0)
        _exit(3);
      pthread_join(thread, NULL);
    } else
      lost_x_connection(NULL);
    _exit(2);
  }
  int status;
  assert(waitpid(child, &status, 0) == child);
  return WIFEXITED(status) ? WEXITSTATUS(status) : -1;
}

// --- a CLAP plugin that asks for things from a thread of its own ------------

static Display *clap_display;
static Window clap_window;
static unsigned clap_hides, clap_destroys;

static bool cg_create(const clap_plugin_t *p, const char *api, bool floating) {
  return true;
}
static void cg_destroy(const clap_plugin_t *p) {
  ++clap_destroys;
  XDestroyWindow(clap_display, clap_window);
  XCloseDisplay(clap_display);
  clap_display = NULL;
}
static bool cg_set_scale(const clap_plugin_t *p, double scale) { return true; }
static bool cg_get_size(const clap_plugin_t *p, uint32_t *width, uint32_t *height) {
  *width = 64;
  *height = 48;
  return true;
}
static bool cg_set_parent(const clap_plugin_t *p, const clap_window_t *window) {
  clap_display = XOpenDisplay(NULL);
  assert(clap_display);
  // No window manager runs under Xvfb, so the frame is mapped and unmapped
  // exactly when the host says.
  XSetWindowAttributes attributes = {.override_redirect = True};
  XChangeWindowAttributes(clap_display, window->x11, CWOverrideRedirect, &attributes);
  clap_window = XCreateSimpleWindow(clap_display, window->x11, 0, 0, 64, 48, 0, 0, 0);
  XMapWindow(clap_display, clap_window);
  XSync(clap_display, False);
  return true;
}
static bool cg_show(const clap_plugin_t *p) { return true; }
static bool cg_hide(const clap_plugin_t *p) {
  ++clap_hides;
  return true;
}
static const clap_plugin_gui_t clap_gui = {
    .create = cg_create, .destroy = cg_destroy, .set_scale = cg_set_scale,
    .get_size = cg_get_size, .set_parent = cg_set_parent, .show = cg_show,
    .hide = cg_hide};
static void cp_destroy(const clap_plugin_t *p) {}
static const clap_plugin_t clap_plugin = {.destroy = cp_destroy};

// One request, made on a thread that is not the host's main thread. What the
// frame's recorded size was when the call returned is kept, since a host
// that answered the request there would already have changed it.
typedef struct {
  Host *h;
  const clap_host_gui_t *gui;
  enum { ASK_RESIZE, ASK_SHOW, ASK_HIDE, ASK_CLOSED } ask;
  uint32_t width, height;
  bool answer;
  unsigned width_after;
} ClapAsk;

static void *clap_ask(void *data) {
  ClapAsk *a = data;
  Clap *c = a->h->impl;
  assert(!host_on_main_thread(a->h));
  switch (a->ask) {
  case ASK_RESIZE:
    // Twice: only the second size may reach the frame.
    assert(a->gui->request_resize(&c->host, a->width / 2, a->height / 2));
    a->answer = a->gui->request_resize(&c->host, a->width, a->height);
    break;
  case ASK_SHOW: a->answer = a->gui->request_show(&c->host); break;
  case ASK_HIDE: a->answer = a->gui->request_hide(&c->host); break;
  case ASK_CLOSED: a->gui->closed(&c->host, true); a->answer = true; break;
  }
  a->width_after = a->h->self_width;
  return NULL;
}

static void ask_from_thread(ClapAsk *a) {
  pthread_t thread;
  assert(pthread_create(&thread, NULL, clap_ask, a) == 0);
  assert(pthread_join(thread, NULL) == 0);
}

static XWindowAttributes clap_frame(Host *h) {
  XWindowAttributes attributes;
  XSync(clap_display, False);
  assert(XGetWindowAttributes(clap_display, h->window, &attributes));
  return attributes;
}

// CLAP lets a plugin ask for a size, to be shown or hidden, or say its editor
// closed, from any thread. X is only called from the main thread, so a
// request from elsewhere is answered at once and carried out there, through
// the main loop, with no tick in between.
static void clap_requests_off_main_thread(void) {
  Host h = {.backend = &clap_backend, .has_editor = true,
            .node_name = "openxlr-clap-editor-test",
            .main_thread = pthread_self()};
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  pw_loop_enter(host_loop(&h));
  Clap *c = calloc(1, sizeof(Clap));
  assert(c);
  c->h = &h;
  c->plugin = &clap_plugin;
  c->gui = &clap_gui;
  c->host = (clap_host_t){.host_data = c, .get_extension = host_get_extension};
  c->gui_wake = pw_loop_add_event(host_loop(&h), gui_requests_due, c);
  assert(c->gui_wake);
  h.impl = c;
  const clap_host_gui_t *gui = c->host.get_extension(&c->host, CLAP_EXT_GUI);
  assert(gui);
  assert(open_ui(&h));
  drain(&h);
  XWindowAttributes frame = clap_frame(&h);
  assert(frame.width == 64 && frame.height == 48 && frame.map_state == IsViewable);

  ClapAsk resize = {.h = &h, .gui = gui, .ask = ASK_RESIZE, .width = 200, .height = 150};
  ask_from_thread(&resize);
  assert(resize.answer && resize.width_after == 64);
  frame = clap_frame(&h);
  assert(frame.width == 64 && frame.height == 48);
  drain(&h);
  frame = clap_frame(&h);
  assert(frame.width == 200 && frame.height == 150 && h.self_width == 200);
  puts("PASS: a CLAP resize asked off the main thread is carried out there, the latest size");

  ClapAsk hide = {.h = &h, .gui = gui, .ask = ASK_HIDE};
  ask_from_thread(&hide);
  assert(hide.answer && clap_frame(&h).map_state == IsViewable);
  drain(&h);
  assert(clap_frame(&h).map_state == IsUnmapped);
  ClapAsk show = {.h = &h, .gui = gui, .ask = ASK_SHOW};
  ask_from_thread(&show);
  assert(show.answer && clap_frame(&h).map_state == IsUnmapped);
  drain(&h);
  assert(clap_frame(&h).map_state == IsViewable);
  puts("PASS: a CLAP show or hide asked off the main thread is carried out there");

  // On the main thread a request is carried out at once, and it replaces one
  // still waiting from another thread rather than being undone by it.
  assert(gui->request_resize(&c->host, 220, 160) && h.self_width == 220);
  ClapAsk late = {.h = &h, .gui = gui, .ask = ASK_RESIZE, .width = 300, .height = 200};
  ask_from_thread(&late);  // its wake is not dispatched until the drain below
  assert(gui->request_resize(&c->host, 240, 180) && h.self_width == 240);
  drain(&h);
  frame = clap_frame(&h);
  assert(frame.width == 240 && frame.height == 180);
  puts("PASS: a CLAP resize on the main thread is immediate and supersedes a waiting one");

  // The plugin says from its own thread that it destroyed its editor. The
  // editor closes on the next idle pass, and the plugin is sent destroy to
  // acknowledge it, but not hide.
  ClapAsk closed = {.h = &h, .gui = gui, .ask = ASK_CLOSED};
  ask_from_thread(&closed);
  assert(h.editor_open && clap_hides == 0 && clap_destroys == 0);
  pump_editor(&h, true);
  assert(!h.editor_open && !h.display && clap_hides == 0 && clap_destroys == 1);
  // A plain close later still hides first.
  assert(open_ui(&h));
  drain(&h);
  close_ui(&h);
  assert(clap_hides == 1 && clap_destroys == 2);
  puts("PASS: a CLAP editor closed from another thread is destroyed on the main thread");

  clap_unload(&h);
  pw_loop_leave(host_loop(&h));
  pw_main_loop_destroy(h.loop);
}

// --- a CLAP plugin whose display goes away under its timer ------------------

static unsigned clap_timer_calls, clap_fd_calls;
static bool clap_lose_on_timer;

static void ct_on_timer(const clap_plugin_t *p, clap_id id) {
  ++clap_timer_calls;
  // What Xlib does when the plugin's next call finds the server gone.
  if (clap_lose_on_timer) {
    clap_lose_on_timer = false;
    lost_x_connection(clap_display);
  }
}
static void cf_on_fd(const clap_plugin_t *p, int fd, clap_posix_fd_flags_t flags) {
  ++clap_fd_calls;
}
static const clap_plugin_timer_support_t clap_timer_support = {ct_on_timer};
static const clap_plugin_posix_fd_support_t clap_fd_support = {cf_on_fd};

static void iterate_until(Host *h, const unsigned *calls) {
  for (unsigned i = 0; i < 200 && !*calls; ++i)
    pw_loop_iterate(host_loop(h), 10);
  assert(*calls);
}

// The timers and descriptors a plugin registered stop with a lost display:
// a hung-up descriptor would otherwise be ready on every pass of the loop.
// The editor opens again afterwards and its new ones run.
static void clap_lost_display_stops_watches(void) {
  x_main_thread = pthread_self();
  Host h = {.backend = &clap_backend, .has_editor = true,
            .node_name = "openxlr-clap-lost-test",
            .main_thread = pthread_self()};
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  pw_loop_enter(host_loop(&h));
  Clap *c = calloc(1, sizeof(Clap));
  assert(c);
  c->h = &h;
  c->plugin = &clap_plugin;
  c->gui = &clap_gui;
  c->timer_support = &clap_timer_support;
  c->fd_support = &clap_fd_support;
  c->host = (clap_host_t){.host_data = c, .get_extension = host_get_extension};
  h.impl = c;
  const clap_host_timer_support_t *timers =
      c->host.get_extension(&c->host, CLAP_EXT_TIMER_SUPPORT);
  const clap_host_posix_fd_support_t *fds =
      c->host.get_extension(&c->host, CLAP_EXT_POSIX_FD_SUPPORT);
  assert(timers && fds);
  assert(open_ui(&h));
  drain(&h);

  int pipes[2];
  assert(pipe(pipes) == 0);
  clap_id timer;
  assert(timers->register_timer(&c->host, 1, &timer));
  assert(fds->register_fd(&c->host, pipes[0],
                          CLAP_POSIX_FD_READ | CLAP_POSIX_FD_ERROR));
  Display *host_display = h.display;
  Window host_window_id = h.window;
  unsigned destroys = clap_destroys;
  clap_lose_on_timer = true;
  iterate_until(&h, &clap_timer_calls);
  assert(!h.editor_open && !h.display && !c->gui_created);
  // The plugin's connection hangs up and stays ready from now on.
  close(pipes[1]);
  for (unsigned i = 0; i < 10; ++i)
    assert(pw_loop_iterate(host_loop(&h), 5) == 0);
  assert(clap_timer_calls == 1 && clap_fd_calls == 0);
  assert(!timers->unregister_timer(&c->host, timer));
  assert(!fds->unregister_fd(&c->host, pipes[0]));
  assert(clap_destroys == destroys);  // destroying would go through the display
  // The simulated loss leaves both test connections alive for cleanup.
  XDestroyWindow(clap_display, clap_window);
  XCloseDisplay(clap_display);
  clap_display = NULL;
  XDestroyWindow(host_display, host_window_id);
  XCloseDisplay(host_display);
  close(pipes[0]);
  puts("PASS: a lost CLAP editor stops the plugin's timers and descriptors");

  assert(open_ui(&h));
  drain(&h);
  assert(pipe(pipes) == 0);
  assert(timers->register_timer(&c->host, 1, &timer));
  assert(fds->register_fd(&c->host, pipes[0], CLAP_POSIX_FD_READ));
  clap_timer_calls = 0;
  iterate_until(&h, &clap_timer_calls);
  assert(write(pipes[1], "x", 1) == 1);
  iterate_until(&h, &clap_fd_calls);
  assert(h.editor_open);
  assert(timers->unregister_timer(&c->host, timer));
  assert(fds->unregister_fd(&c->host, pipes[0]));
  close_ui(&h);
  assert(clap_destroys == destroys + 1);
  close(pipes[0]);
  close(pipes[1]);
  puts("PASS: a CLAP editor opens again after a lost display and its watches run");

  clap_unload(&h);
  pw_loop_leave(host_loop(&h));
  pw_main_loop_destroy(h.loop);
}

int main(void) {
  // Before any thread or connection of this test exists, so the children
  // inherit nothing but the escape.
  assert(lost_connection_outcome(false) == 0);
  assert(lost_connection_outcome(true) == 1);
  puts("PASS: a lost connection jumps back on the main thread and exits on any other");

  // An X server resets when its last client disconnects, and a connection
  // opened while it resets is refused. Closing the editor drops every
  // connection this test holds, so one is kept open for the whole run.
  Display *keepalive = XOpenDisplay(NULL);
  assert(keepalive);
  const char *renderer = getenv("LSP_WS_LIB_GLXSURFACE");
  char *saved_renderer = renderer ? strdup(renderer) : NULL;
  assert(!renderer || saved_renderer);
  assert(unsetenv("LSP_WS_LIB_GLXSURFACE") == 0);
  assert(configure_editor_environment());
  assert(!strcmp(getenv("LSP_WS_LIB_GLXSURFACE"), "0"));
  assert(setenv("LSP_WS_LIB_GLXSURFACE", "1", 1) == 0);
  assert(configure_editor_environment());
  assert(!strcmp(getenv("LSP_WS_LIB_GLXSURFACE"), "1"));
  if (saved_renderer) {
    assert(setenv("LSP_WS_LIB_GLXSURFACE", saved_renderer, 1) == 0);
    free(saved_renderer);
  } else
    assert(unsetenv("LSP_WS_LIB_GLXSURFACE") == 0);
  puts("PASS: LSP rendering defaults to software and preserves an explicit override");
  pw_init(NULL, NULL);
  Host h = {.backend = &test_backend, .has_editor = true,
            .node_name = "openxlr-editor-test"};
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  pw_loop_enter(host_loop(&h));
  assert(open_ui(&h));
  drain(&h);
  assert_size(&h, 64, 48);

  unsigned calls = resize_calls;
  unsigned settle = h.settle_ticks;
  resize_to(&h, 100, 80);
  assert(resize_calls == calls + 1);
  assert_size(&h, 100, 80);
  assert(idle_calls == 0 && h.settle_ticks == settle);
  puts("PASS: X events resize the editor without an idle tick");

  resize_to(&h, 110, 90);
  calls = resize_calls;
  resize_to(&h, 100, 80);
  assert(resize_calls == calls + 1);
  assert_size(&h, 100, 80);
  puts("PASS: reversing a drag delivers a previously visited size");

  calls = resize_calls;
  for (unsigned i = 0; i < 8; ++i)
    XResizeWindow(plugin_display, h.window, 120 + i, 100 + i);
  XSync(plugin_display, False);
  drain(&h);
  assert(resize_calls == calls + 1);
  assert_size(&h, 127, 107);
  puts("PASS: a queued resize burst delivers only its final size");

  constrain_sizes = true;
  answer_resize = true;
  resize_to(&h, 141, 121);
  assert_size(&h, 140, 120);
  calls = resize_calls;
  drain(&h);
  assert(resize_calls == calls);
  puts("PASS: constraints keep both windows aligned without resize feedback");
  answer_resize = false;

  // The Wine coordinate workaround must still deliver its one-pixel nudge,
  // even when that temporary size violates the plugin's size constraints.
  unsigned constraints = constrain_calls;
  h.settle_ticks = 1;
  pump_editor(&h, true);
  drain(&h);
  assert_size(&h, 140, 119);
  assert(constrain_calls == constraints);
  h.settle_ticks = 1;
  pump_editor(&h, true);
  drain(&h);
  assert_size(&h, 140, 120);
  puts("PASS: the coordinate nudge and restore survive size constraints");

  close_ui(&h);
  assert(!h.editor_source && !h.display && !h.editor_open);

  // A native LV2 editor publishes its bounds on its child window. The WM
  // only sees our frame, and direct X resize requests bypass the WM entirely.
  Backend native_backend = test_backend;
  native_backend.editor_coordinate_nudge = false;
  h.backend = &native_backend;
  constrain_sizes = false;
  assert(open_ui(&h));
  drain(&h);
  assert(h.settle_ticks == 0);
  XSizeHints hints = {.flags = PMinSize | PMaxSize,
                      .min_width = 64, .min_height = 48,
                      .max_width = 160, .max_height = 120};
  XSetWMNormalHints(plugin_display, plugin_window, &hints);
  XSync(plugin_display, False);
  drain(&h);
  assert_bounds(&h, 64, 48, 160, 120);
  resize_to(&h, 32, 24);
  assert_size(&h, 64, 48);
  resize_to(&h, 200, 180);
  assert_size(&h, 160, 120);
  puts("PASS: child size bounds reach the frame and prevent clipped controls");

  hints.min_width = 80;
  hints.min_height = 60;
  hints.max_width = 240;
  hints.max_height = 180;
  XSetWMNormalHints(plugin_display, plugin_window, &hints);
  XSync(plugin_display, False);
  drain(&h);
  assert_bounds(&h, 80, 60, 240, 180);
  resize_to(&h, 64, 48);
  assert_size(&h, 80, 60);
  XMoveWindow(plugin_display, h.window, 10, 10);
  XSync(plugin_display, False);
  drain(&h);
  for (unsigned i = 0; i < 15; ++i)
    pump_editor(&h, true);
  assert(h.settle_ticks == 0 && h.settle_height == 0);
  assert_size(&h, 80, 60);
  puts("PASS: bounds refresh after scaling and native editors get no Wine nudge");
  close_ui(&h);

  // Fixed-size VST3 editors still resize through their own scaling controls.
  h.backend = &test_backend;
  fixed_editor = true;
  assert(open_ui(&h));
  drain(&h);
  assert_bounds(&h, 64, 48, 64, 48);
  resize_to(&h, 100, 80);
  assert_size(&h, 64, 48);
  host_resize_editor(&h, 128, 96);
  drain(&h);
  assert_bounds(&h, 128, 96, 128, 96);
  assert_size(&h, 128, 96);
  h.settle_ticks = 1;
  pump_editor(&h, true);
  drain(&h);
  assert_size(&h, 128, 95);
  h.settle_ticks = 1;
  pump_editor(&h, true);
  drain(&h);
  assert_size(&h, 128, 96);
  assert_bounds(&h, 128, 96, 128, 96);
  puts("PASS: fixed editors reject border resizing but accept plugin scaling");
  close_ui(&h);
  fixed_editor = false;

  Backend wine_backend = test_backend;
  wine_backend.editor_open = wine_open;
  h.backend = &wine_backend;
  assert(open_ui(&h));
  drain(&h);
  assert(map_state(wine_window_id) == IsViewable);
  assert(map_state(native_window_id) == IsUnmapped);
  XUnmapWindow(plugin_display, wine_window_id);
  XSync(plugin_display, False);
  drain(&h);
  assert(map_state(wine_window_id) == IsViewable);
  h.wine_maps = MAX_WINE_MAPS;  // past the cap, whatever hides it wins
  XUnmapWindow(plugin_display, wine_window_id);
  XSync(plugin_display, False);
  drain(&h);
  assert(map_state(wine_window_id) == IsUnmapped);
  close_ui(&h);
  assert(h.wine_maps == 0);
  wine_backend.editor_coordinate_nudge = false;  // a format that never comes through Wine
  assert(open_ui(&h));
  drain(&h);
  assert(map_state(wine_window_id) == IsUnmapped);
  close_ui(&h);
  h.backend = &test_backend;
  puts("PASS: a Wine window left unmapped in the frame is mapped again, nothing else is");

  // Whether a plugin has an editor is settled when one is asked for, not by
  // creating a view while loading, and the reply to show still tells a
  // plugin without an editor apart from a missing display.
  Backend no_editor_backend = test_backend;
  no_editor_backend.editor_open = refuse_open;
  h.backend = &no_editor_backend;
  char reply[160];
  reply_to(&h, "show", reply, sizeof(reply));
  assert(!strcmp(reply, "ui unavailable: this plugin has no editor the host can show"));
  assert(!h.has_editor && !h.editor_source && !h.display && !h.editor_open);
  reply_to(&h, "show", reply, sizeof(reply));  // and it is remembered
  assert(!strcmp(reply, "ui unavailable: this plugin has no editor the host can show"));
  h.has_editor = true;
  h.backend = &test_backend;
  reply_to(&h, "show", reply, sizeof(reply));
  assert(!strcmp(reply, "ui opened") && h.editor_open);
  close_ui(&h);
  puts("PASS: a plugin without an editor is found out on opening and says so");
  fixed_editor = false;
  assert(open_ui(&h));
  drain(&h);
  assert_size(&h, 64, 48);
  Display *host_display = h.display;
  Window host_window_id = h.window;
  editor_events(&h, ConnectionNumber(h.display), SPA_IO_HUP);
  assert(!h.editor_source && !h.display && !h.editor_open);
  // A synthetic hangup leaves these test connections alive for cleanup.
  test_close(&h);
  XDestroyWindow(host_display, host_window_id);
  XCloseDisplay(host_display);
  pw_loop_leave(host_loop(&h));
  pw_main_loop_destroy(h.loop);
  clap_requests_off_main_thread();
  clap_lost_display_stops_watches();
  pw_deinit();
  XCloseDisplay(keepalive);
  puts("PASS: closing, reopening and losing a display remove its event source");
  return 0;
}
