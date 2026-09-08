// SPDX-License-Identifier: GPL-3.0-only
// Exercise the actual host event loop with a plugin on a separate X connection.
#define main host_program_main
#include "../host.c"
#undef main
#include <assert.h>

static Display *plugin_display;
static Window plugin_window;
static unsigned resize_calls, idle_calls, constrain_calls, width_seen, height_seen;
static bool constrain_sizes, answer_resize;

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
    .editor_constrain = test_constrain};

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

int main(void) {
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
  pw_deinit();
  puts("PASS: closing, reopening and losing a display remove its event source");
  return 0;
}
