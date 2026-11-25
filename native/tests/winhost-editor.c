// SPDX-License-Identifier: GPL-3.0-only
// The editor of a plugin on the Windows backend, with the Windows plugin
// host started for real under Wine and the host's own frame and event loop
// around it. The two Windows test plugins each have a small real editor
// (tests/winhost-plugin-editor.h), and this program is the user in front of
// it, on an X11 connection of its own: it looks at the frame's pixels, moves
// the pointer, clicks, types, and moves and resizes the frame as a window
// manager would.
//
// For a CLAP and a VST3 plugin it checks that the editor opens in the frame
// at the plugin's size and is drawn there, that a click in it moves a
// parameter on the Linux side and in the audio, before and after the frame
// moves, that a window the plugin puts up lands where the editor is, that
// the keyboard reaches it, that a size the plugin asks for and a size the
// user gives the frame both arrive, that an editor of one size keeps it,
// that the scale reaches the plugin, and that the editor closes and opens
// again. It then closes an editor from the plugin's side, kills the Windows
// host with an editor open, asks for editors that do not exist or will not
// open, and measures the audio round trip with an editor open.
//
// It needs a display, and it moves that display's pointer. Run it under
// xvfb-run. Everything it places is placed from the size of the screen it
// finds, and 640x480, which is what xvfb-run gives by default on some
// systems, is enough. On a display with a window manager it only runs when
// OPENXLR_TEST_WINHOST_WM is set, which is for a private one.
// Usage: winhost-editor WINHOST-EXE CLAP-PLUGIN VST3-PLUGIN
//
// By hand, against a real plugin in the Wine prefix WINEPREFIX names:
//   winhost-editor try clap|vst3 PLUGIN ID PICTURE.ppm [X Y]...
// opens the editor, saves the frame's picture, clicks at each X Y in the
// frame, closes the editor, opens it again and saves PICTURE.ppm.again.
// The parameters the plugin moves are printed as the host reports them to
// the daemon.
#define _GNU_SOURCE
#include "../host.h"
#include <stdio.h>
static float *test_buffers[MAX_CHANNELS * 2];
static void *test_dsp_buffer(void *port, uint32_t frames);
#define pw_filter_get_dsp_buffer test_dsp_buffer
#define main host_program_main
#include "../host.c"
#undef main
#undef pw_filter_get_dsp_buffer
#include "../wine.c"
#include "winhost-plugin-editor.h"
#include <X11/extensions/XTest.h>
#include <X11/keysym.h>
#include <assert.h>

#define PLUGIN "org.openxlr.test.winhost."
#define CLASS "00112233445566778899AABBCCDDEE"
#define VST3_GAIN CLASS "00"
#define VST3_PARAMS CLASS "05"

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return NULL;
}

static Host h;  // static: the control table is large
static float in_buffers[MAX_CHANNELS][MAX_FRAMES], out_buffers[MAX_CHANNELS][MAX_FRAMES];
static float quiet[MAX_FRAMES], spare[MAX_FRAMES];
static uint64_t position;  // of the first frame of the last cycle
static Display *user;      // the connection of the one in front of the editor
static Window root;
static bool managed;       // the display has a window manager
static int screen_width, screen_height;

// The least screen the test fits on: the editor at twice its size, in the
// screen's corner. A window manager's borders and title need more.
enum {
  SCREEN_WIDTH = 2 * TEST_EDITOR_WIDTH,
  SCREEN_HEIGHT = 2 * TEST_EDITOR_HEIGHT,
  MANAGED_SCREEN_WIDTH = 800,
  MANAGED_SCREEN_HEIGHT = 600,
};

// A place for the frame's corner as near to `wanted` as leaves a frame of
// this size whole on the screen. A click outside the screen never arrives:
// the server keeps the pointer inside.
static int placed(int wanted, int size, int screen) {
  int furthest = screen - size;
  return wanted < furthest ? wanted : furthest > 0 ? furthest : 0;
}
static const char *clap_plugin, *vst3_plugin;

static double now_ms(void) {
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  return (double)now.tv_sec * 1e3 + (double)now.tv_nsec / 1e6;
}

// --- the host around the backend --------------------------------------------

static float signal_at(uint64_t at, unsigned channel) {
  return (float)((at & 0xfffff) + 1 + (channel ? 0x100000 : 0)) / (float)(1 << 22);
}

static bool begin(const char *format, const char *module, const char *id) {
  memset(&h, 0, sizeof(h));
  h.backend = &wine_backend;
  h.node_name = "openxlr-winhost-editor-test";
  h.rate = 48000;
  h.channels = 2;
  h.main_thread = pthread_self();
  h.silence = quiet;
  h.scratch = spare;
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  pw_loop_enter(host_loop(&h));
  for (unsigned c = 0; c < MAX_CHANNELS; ++c) {
    test_buffers[c] = in_buffers[c];
    test_buffers[MAX_CHANNELS + c] = out_buffers[c];
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
  char *arguments[] = {(char *)format, (char *)module, (char *)id};
  return wine_backend.load(&h, arguments) && wine_backend.activate(&h);
}

// What the helper's main does on its way out: the frame first, then the
// plugin.
static void end(void) {
  close_ui(&h);
  if (h.impl) {
    wine_backend.deactivate(&h);
    wine_backend.unload(&h);
  }
  pw_loop_leave(host_loop(&h));
  pw_main_loop_destroy(h.loop);
}

// One cycle of the host's audio callback, and how long it took.
static double cycle(uint32_t frames) {
  position += MAX_FRAMES;
  for (unsigned c = 0; c < h.channels; ++c)
    for (uint32_t n = 0; n < frames; ++n) {
      in_buffers[c][n] = signal_at(position + n, c);
      out_buffers[c][n] = 7.0f;  // what a frame nobody wrote would read
    }
  struct spa_io_position at = {0};
  at.clock.duration = frames;
  at.clock.rate.denom = h.rate;
  double start = now_ms();
  process_audio(&h, &at);
  return now_ms() - start;
}

// Whether the last cycle's output is its input times `gain`, every sample.
static bool scaled(uint32_t frames, float gain) {
  for (unsigned c = 0; c < h.channels; ++c)
    for (uint32_t n = 0; n < frames; ++n)
      if (out_buffers[c][n] != signal_at(position + n, c) * gain) return false;
  return true;
}

// Let `ms` pass as the helper's main loop would spend it: the frame's X
// events and the Windows host's log as they come, the tick 30 times a
// second, and, unless another thread has the audio, a cycle every pass.
static bool audio_elsewhere;

static void turn(unsigned ms) {
  static double ticked;
  double until = now_ms() + ms;
  do {
    pw_loop_iterate(host_loop(&h), 3);
    if (!audio_elsewhere) cycle(256);
    if (now_ms() - ticked >= 33) {
      ticked = now_ms();
      tick(&h, 1);
    }
  } while (now_ms() < until);
}

static void describe_editor(void);

// A check that never came true ends the test with a plain exit, which says
// as much as a core dump would and bothers no crash reporter.
static void fail(void) {
  fflush(NULL);
  _exit(1);
}

// Turn until `condition` holds, for five seconds at most.
#define settle(condition) \
  do { \
    for (double until_ = now_ms() + 5000; !(condition) && now_ms() < until_;) turn(20); \
    if (!(condition)) { \
      fprintf(stderr, "FAIL: line %d: never came true: %s\n", __LINE__, #condition); \
      describe_editor(); \
      fail(); \
    } \
  } while (0)

static Control *control_named(const char *symbol) {
  for (uint32_t i = 0; i < h.control_count; ++i)
    if (!strcmp(h.controls[i].symbol, symbol)) return &h.controls[i];
  return NULL;
}

// Run one command as the daemon would and hand back the reply line.
static const char *reply_to(const char *line) {
  static char reply[160];
  FILE *capture = tmpfile();
  assert(capture);
  fflush(stdout);
  int saved = dup(STDOUT_FILENO);
  assert(saved >= 0 && dup2(fileno(capture), STDOUT_FILENO) >= 0);
  char command_line[32];
  snprintf(command_line, sizeof(command_line), "%s", line);
  command(&h, command_line);
  fflush(stdout);
  assert(dup2(saved, STDOUT_FILENO) >= 0);
  close(saved);
  rewind(capture);
  reply[0] = 0;
  if (fgets(reply, sizeof(reply), capture)) reply[strcspn(reply, "\n")] = 0;
  fclose(capture);
  return reply;
}

// --- the user's side of the display -----------------------------------------

static int expected_x_error(Display *display, XErrorEvent *event) { return 0; }

// A window's attributes through the user's connection, false for one that
// is gone, without the complaint the server has about that.
static bool attributes_of(Window window, XWindowAttributes *attributes) {
  int (*report)(Display *, XErrorEvent *) = XSetErrorHandler(expected_x_error);
  bool there = window && XGetWindowAttributes(user, window, attributes);
  XSync(user, False);
  XSetErrorHandler(report);
  return there;
}

static bool viewable(Window window) {
  XWindowAttributes attributes;
  return attributes_of(window, &attributes) && attributes.map_state == IsViewable;
}

// Where a window's corner is on the root.
static void origin_of(Window window, int *x, int *y) {
  Window below;
  assert(XTranslateCoordinates(user, window, root, 0, 0, x, y, &below));
}

static bool size_is(Window window, int width, int height) {
  XWindowAttributes attributes;
  return attributes_of(window, &attributes) && attributes.width == width &&
         attributes.height == height;
}

// The frame and Wine's window inside it both have this size, and the
// window sits in the frame's corner.
static bool editor_is(int width, int height) {
  Wine *w = h.impl;
  XWindowAttributes inside;
  return h.editor_open && w->editor && size_is(h.window, width, height) &&
         attributes_of(w->editor, &inside) && inside.width == width &&
         inside.height == height && inside.x == 0 && inside.y == 0 &&
         inside.map_state == IsViewable;
}

// What a check that failed was looking at.
static void describe_editor(void) {
  Wine *w = h.impl;
  XWindowAttributes frame = {0}, inside = {0};
  bool has_frame = h.window && attributes_of(h.window, &frame);
  bool has_inside = w && w->editor && attributes_of(w->editor, &inside);
  fprintf(stderr, "the frame: %s, %dx%d; Wine's window: %s, %dx%d at %d,%d, %s\n",
          has_frame ? "there" : "gone", frame.width, frame.height,
          has_inside ? "there" : "gone", inside.width, inside.height, inside.x, inside.y,
          inside.map_state == IsViewable ? "shown" : "not shown");
}

// The colour of one pixel of a window as the screen shows it, what is drawn
// inside it included, or -1 where there is nothing to read.
static long pixel_of(Window window, int x, int y) {
  int (*report)(Display *, XErrorEvent *) = XSetErrorHandler(expected_x_error);
  XImage *image = viewable(window)
                      ? XGetImage(user, window, x, y, 1, 1, AllPlanes, ZPixmap)
                      : NULL;
  XSync(user, False);
  XSetErrorHandler(report);
  if (!image) return -1;
  long pixel = (long)(XGetPixel(image, 0, 0) & 0xffffff);
  XDestroyImage(image);
  return pixel;
}

// The editor's picture is in the frame: its colour in a corner and the two
// squares where the plugin draws them at this scale.
static bool drawn(int scale) {
  int middle = scale * (TEST_EDITOR_SQUARES_Y + TEST_EDITOR_SQUARE / 2);
  return pixel_of(h.window, 4, 4) == TEST_EDITOR_COLOUR &&
         pixel_of(h.window, scale * (TEST_EDITOR_GAIN_X + TEST_EDITOR_SQUARE / 2),
                  middle) == TEST_EDITOR_GAIN_COLOUR &&
         pixel_of(h.window, scale * (TEST_EDITOR_SIZE_X + TEST_EDITOR_SQUARE / 2),
                  middle) == TEST_EDITOR_SIZE_COLOUR;
}

// Move the pointer to a place in the frame and press a button there. The
// button is only pressed when the pointer arrived over the frame, since a
// press goes wherever the pointer is.
static void click(int x, int y, unsigned button) {
  int frame_x, frame_y, at_x, at_y, in_x, in_y;
  unsigned mask;
  Window in_root, below = None;
  origin_of(h.window, &frame_x, &frame_y);
  for (unsigned attempt = 0; attempt < 20; ++attempt) {
    XTestFakeMotionEvent(user, DefaultScreen(user), frame_x + x, frame_y + y, CurrentTime);
    XSync(user, False);
    turn(40);
    XQueryPointer(user, h.window, &in_root, &below, &at_x, &at_y, &in_x, &in_y, &mask);
    if (in_x == x && in_y == y) break;
  }
  if (in_x != x || in_y != y || below != ((Wine *)h.impl)->editor) {
    fprintf(stderr, "FAIL: no click: the pointer is at %d,%d in the frame and %d,%d on the "
            "root, wanted %d,%d and %d,%d, over window 0x%lx where Wine's is 0x%lx\n",
            in_x, in_y, at_x, at_y, x, y, frame_x + x, frame_y + y, (unsigned long)below,
            (unsigned long)((Wine *)h.impl)->editor);
    describe_editor();
    fail();
  }
  XTestFakeButtonEvent(user, button, True, CurrentTime);
  XSync(user, False);
  turn(60);
  XTestFakeButtonEvent(user, button, False, CurrentTime);
  XSync(user, False);
  turn(60);
}

static void press_key(KeySym key) {
  KeyCode code = XKeysymToKeycode(user, key);
  assert(code);
  XTestFakeKeyEvent(user, code, True, CurrentTime);
  XSync(user, False);
  turn(60);
  XTestFakeKeyEvent(user, code, False, CurrentTime);
  XSync(user, False);
  turn(60);
}

static Window focus_now(void) {
  Window focus;
  int revert;
  XGetInputFocus(user, &focus, &revert);
  return focus;
}

// A window of the root's that is not the frame's, shown with its corner at
// this place on the root, of this size and this colour: what the test
// plugin's editor puts up for a right click.
static bool popup_at(int x, int y, int width, int height, long colour) {
  Window in_root, parent, *children = NULL;
  unsigned count = 0;
  bool found = false;
  if (!XQueryTree(user, root, &in_root, &parent, &children, &count)) return false;
  for (unsigned i = 0; i < count && !found; ++i) {
    XWindowAttributes attributes;
    int at_x, at_y;
    if (!attributes_of(children[i], &attributes) || attributes.map_state != IsViewable ||
        !attributes.override_redirect || attributes.width != width ||
        attributes.height != height)
      continue;
    origin_of(children[i], &at_x, &at_y);
    found = at_x == x && at_y == y && pixel_of(children[i], width / 2, height / 2) == colour;
  }
  if (children) XFree(children);
  return found;
}

// Ask for the frame to be somewhere else, as a user dragging it would. A
// window manager has the last word on where it lands.
static void move_frame(int x, int y) {
  XMoveWindow(user, h.window, x, y);
  XSync(user, False);
  turn(300);
}

// --- one plugin's editor, start to end ---------------------------------------

// The gain has this value on the Linux side and in the audio.
static bool gain_is(float value) {
  Control *gain = control_named("1");
  cycle(256);
  return gain && atomic_load(&gain->desired) == value && scaled(256, value);
}

static void open_editor(int width, int height, int scale) {
  assert(!strcmp(reply_to("show"), "ui opened"));
  settle(editor_is(width, height));
  settle(drawn(scale));
}

static void check_editor(const char *format, const char *backend, const char *module,
                         const char *id) {
  enum {
    WIDTH = TEST_EDITOR_WIDTH,
    HEIGHT = TEST_EDITOR_HEIGHT,
    GAIN_X = TEST_EDITOR_GAIN_X + TEST_EDITOR_SQUARE / 2,
    SIZE_X = TEST_EDITOR_SIZE_X + TEST_EDITOR_SQUARE / 2,
    SQUARES_Y = TEST_EDITOR_SQUARES_Y + TEST_EDITOR_SQUARE / 2,
  };
  assert(begin(backend, module, id));
  Wine *w = h.impl;
  assert(h.has_editor);
  settle(gain_is(0.5f));

  // The frame opens at the plugin's size with Wine's window in its corner,
  // and the plugin's picture is what the frame shows.
  open_editor(WIDTH, HEIGHT, 1);
  Window first_window = w->editor;
  XClassHint hint = {0};
  assert(XGetClassHint(user, w->editor, &hint) && strstr(hint.res_class, ".exe"));
  XFree(hint.res_name);
  XFree(hint.res_class);
  printf("PASS: %s: the editor opens in the frame at the plugin's size, %dx%d, and is "
         "drawn there\n", format, WIDTH, HEIGHT);

  // A click in the first square moves the gain. It arrives on the Linux
  // side as a native plugin's move does, and the audio has it.
  enum { WANTED_WIDTH = 487, WANTED_HEIGHT = 333 };  // what the user will ask of the frame
  move_frame(placed(300, WIDTH + 20, screen_width), placed(200, HEIGHT + 20, screen_height));
  int frame_x, frame_y;
  origin_of(h.window, &frame_x, &frame_y);
  click(GAIN_X, SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_GAIN));
  // The frame moves, and the next click lands in the same square.
  // It stays at the new place for the resizes below, so that place leaves
  // room for the largest of them.
  int second_x = placed(520, WANTED_WIDTH + 13, screen_width);
  int second_y = placed(120, WANTED_HEIGHT + 7, screen_height);
  move_frame(second_x, second_y);
  int moved_x, moved_y;
  origin_of(h.window, &moved_x, &moved_y);
  assert(managed || (moved_x == second_x && moved_y == second_y));
  assert(managed || moved_x != frame_x || moved_y != frame_y);
  if (moved_x == frame_x && moved_y == frame_y)
    puts("note: the window manager kept the frame where it was");
  click(GAIN_X, SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_OTHER_GAIN));
  printf("PASS: %s: a click in the editor moves a parameter on the Linux side and in the "
         "audio, with the frame at %d,%d and again at %d,%d\n",
         format, frame_x, frame_y, moved_x, moved_y);

  // A window the plugin puts up at the click, as it would a menu, lands
  // there on the screen, which it only can when Wine knows where the editor
  // is after the move.
  enum { POPUP_X = 200, POPUP_Y = 150 };
  click(POPUP_X, POPUP_Y, 3);
  settle(popup_at(moved_x + POPUP_X, moved_y + POPUP_Y, TEST_EDITOR_POPUP_WIDTH,
                  TEST_EDITOR_POPUP_HEIGHT, TEST_EDITOR_POPUP_COLOUR));
  click(POPUP_X + 80, POPUP_Y, 3);
  settle(!popup_at(moved_x + POPUP_X, moved_y + POPUP_Y, TEST_EDITOR_POPUP_WIDTH,
                   TEST_EDITOR_POPUP_HEIGHT, TEST_EDITOR_POPUP_COLOUR));
  printf("PASS: %s: a window the plugin puts up at a click appears at that place on the "
         "screen\n", format);

  // The keyboard goes to the frame, as from a window manager, and the frame
  // hands it to Wine's window. The plugin takes it on a click, and a key
  // then reaches it.
  XSetInputFocus(user, h.window, RevertToPointerRoot, CurrentTime);
  XSync(user, False);
  settle(focus_now() == w->editor);
  click(POPUP_X, POPUP_Y + 30, 1);
  press_key(XK_k);
  settle(gain_is(TEST_EDITOR_KEY_GAIN));
  printf("PASS: %s: the keyboard given to the frame reaches the editor\n", format);

  // The plugin asks for another size from its second square. The frame and
  // Wine's window take it, and the plugin draws all of it.
  click(SIZE_X, SQUARES_Y, 1);
  settle(editor_is(TEST_EDITOR_GROWN_WIDTH, TEST_EDITOR_GROWN_HEIGHT));
  settle(pixel_of(h.window, TEST_EDITOR_GROWN_WIDTH - 4, TEST_EDITOR_GROWN_HEIGHT - 4) ==
         TEST_EDITOR_COLOUR);
  click(SIZE_X, SQUARES_Y, 1);
  settle(editor_is(WIDTH, HEIGHT));
  printf("PASS: %s: a size the plugin asks for reaches the frame, %dx%d and back\n", format,
         TEST_EDITOR_GROWN_WIDTH, TEST_EDITOR_GROWN_HEIGHT);

  // The user resizes the frame. The plugin is asked what it would take
  // near that size, and the frame, Wine's window and the plugin's picture
  // all end up there.
  int fitted_width = WANTED_WIDTH - WANTED_WIDTH % TEST_EDITOR_STEP;
  int fitted_height = WANTED_HEIGHT - WANTED_HEIGHT % TEST_EDITOR_STEP;
  XResizeWindow(user, h.window, WANTED_WIDTH, WANTED_HEIGHT);
  XSync(user, False);
  settle(editor_is(fitted_width, fitted_height));
  settle(pixel_of(h.window, fitted_width - 4, fitted_height - 4) == TEST_EDITOR_COLOUR);
  // A click still lands where it is aimed.
  click(GAIN_X, SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_GAIN));
  printf("PASS: %s: a frame resized by hand to %dx%d ends at the size the plugin takes, "
         "%dx%d, with the editor filling it\n",
         format, WANTED_WIDTH, WANTED_HEIGHT, fitted_width, fitted_height);

  // The key M makes the plugin put up a real menu, and Windows does not
  // return to the plugin, or to the Windows host's own loop, until the menu
  // is gone. The frame is resized under the open menu. What this side asks
  // about that takes the menu down, so the answer comes at once and not
  // when the user gets round to the menu.
  press_key(XK_m);
  turn(300);
  assert(pixel_of(h.window, 4, 4) == TEST_EDITOR_COLOUR);  // still inside its menu
  double asked = now_ms();
  XResizeWindow(user, h.window, TEST_EDITOR_GROWN_WIDTH, TEST_EDITOR_GROWN_HEIGHT);
  XSync(user, False);
  settle(editor_is(TEST_EDITOR_GROWN_WIDTH, TEST_EDITOR_GROWN_HEIGHT));
  settle(pixel_of(h.window, 4, 4) == TEST_EDITOR_AFTER_MENU_COLOUR);
  assert(!atomic_load(&w->dead) && now_ms() - asked < 3000);
  printf("PASS: %s: a frame resized while the plugin has a menu up is answered in %.0f ms, "
         "the menu taken down\n", format, now_ms() - asked);

  // Closed, the frame is gone and so is the Windows side's window. Opened
  // again, it is a new window with the same editor in it.
  assert(!strcmp(reply_to("hide"), ""));
  XWindowAttributes gone;
  assert(!h.editor_open && !h.display && !w->editor);
  settle(!attributes_of(first_window, &gone));
  settle(gain_is(TEST_EDITOR_GAIN));
  open_editor(WIDTH, HEIGHT, 1);
  assert(w->editor != first_window);
  click(GAIN_X, SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_GAIN));  // a new editor starts its turns again
  click(GAIN_X, SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_OTHER_GAIN));
  // Asked to show an editor that is open, the host raises it.
  Window open_window = w->editor;
  assert(!strcmp(reply_to("show"), "ui opened") && w->editor == open_window);
  assert(!strcmp(reply_to("hide"), ""));
  printf("PASS: %s: the editor closes, opens again as a new window and takes clicks\n", format);

  // At twice the scale the plugin is told so and draws twice as large.
  h.editor_scale = 2;
  open_editor(2 * WIDTH, 2 * HEIGHT, 2);
  click(2 * GAIN_X, 2 * SQUARES_Y, 1);
  settle(gain_is(TEST_EDITOR_GAIN));
  assert(!strcmp(reply_to("hide"), ""));
  h.editor_scale = 0;
  printf("PASS: %s: the scale the host opens an editor at reaches the plugin, which draws "
         "%dx%d at 2\n", format, 2 * WIDTH, 2 * HEIGHT);
  wine_backend.main_thread(&h);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  end();

  // An editor of one size. The frame says so in its hints, and a resize
  // that gets past a window manager is put back.
  setenv("OPENXLR_TEST_WINHOST_EDITOR", "fixed", 1);
  assert(begin(backend, module, id));
  unsetenv("OPENXLR_TEST_WINHOST_EDITOR");
  open_editor(WIDTH, HEIGHT, 1);
  XSizeHints hints;
  long supplied;
  assert(XGetWMNormalHints(user, h.window, &hints, &supplied) &&
         (hints.flags & (PMinSize | PMaxSize)) == (PMinSize | PMaxSize) &&
         hints.min_width == WIDTH && hints.max_width == WIDTH &&
         hints.min_height == HEIGHT && hints.max_height == HEIGHT);
  XResizeWindow(user, h.window, WANTED_WIDTH, WANTED_HEIGHT);
  XSync(user, False);
  turn(300);
  settle(editor_is(WIDTH, HEIGHT));
  // Its own size it may still change.
  click(SIZE_X, SQUARES_Y, 1);
  settle(editor_is(TEST_EDITOR_GROWN_WIDTH, TEST_EDITOR_GROWN_HEIGHT));
  end();
  printf("PASS: %s: an editor the plugin does not let the host resize keeps its size and "
         "can still change it itself\n", format);
}

// --- what goes wrong ---------------------------------------------------------

static void check_closed_by_plugin(void) {
  assert(begin("clap", clap_plugin, PLUGIN "params"));
  Wine *w = h.impl;
  open_editor(TEST_EDITOR_WIDTH, TEST_EDITOR_HEIGHT, 1);
  // A middle click makes the plugin tell its host the editor has closed.
  click(250, 150, 2);
  settle(!h.editor_open && !h.display && !w->editor);
  // Nothing else went with it, and the editor opens again.
  settle(gain_is(0.5f));
  open_editor(TEST_EDITOR_WIDTH, TEST_EDITOR_HEIGHT, 1);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  end();
  puts("PASS: an editor the plugin closes takes the frame with it, the audio goes on and "
       "the editor opens again");
}

static void check_no_editor(void) {
  static const char none[] = "ui unavailable: this plugin has no editor the host can show";
  // This plugin says it has an editor and fails to make one.
  assert(begin("clap", clap_plugin, PLUGIN "gain"));
  assert(h.has_editor);
  assert(!strcmp(reply_to("show"), none));
  assert(!h.has_editor && !h.editor_open && !h.display && !h.editor_source);
  assert(!strcmp(reply_to("show"), none));  // and it is not asked again
  cycle(256);
  assert(scaled(256, 0.5f));
  turn(100);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&((Wine *)h.impl)->dead));
  end();
  // A plugin with parameters and no editor at all says so at load.
  assert(begin("clap", clap_plugin, PLUGIN "many"));
  assert(!h.has_editor && !strcmp(reply_to("show"), none));
  end();
  // A VST3 plugin is only found out when its editor is asked for.
  assert(begin("vst3", vst3_plugin, VST3_GAIN));
  assert(h.has_editor);
  assert(!strcmp(reply_to("show"), none));
  assert(!h.has_editor && !h.editor_open && !h.display);
  cycle(256);
  assert(scaled(256, 0.5f));
  turn(100);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&((Wine *)h.impl)->dead));
  end();
  puts("PASS: a plugin whose editor will not open and plugins that have none say so, "
       "leave no frame behind and keep their audio");
}

static void check_killed(const char *format, const char *backend, const char *module,
                         const char *id) {
  assert(begin(backend, module, id));
  Wine *w = h.impl;
  open_editor(TEST_EDITOR_WIDTH, TEST_EDITOR_HEIGHT, 1);
  Window frame = h.window;
  kill(w->process, SIGKILL);
  // The instance fails as it does without an editor, and until the helper
  // has gone through its way out the frame's own passes go on.
  settle(atomic_load(&h.audio_error) && atomic_load(&h.failure_explained));
  cycle(256);
  assert(scaled(256, 0));
  turn(200);
  h.exit_code = 0;
  end();
  XWindowAttributes gone;
  assert(!h.display && !attributes_of(frame, &gone));
  printf("PASS: %s: a Windows host killed with its editor open fails the instance as "
         "without one, and the frame closes\n", format);
}

// --- the audio round trip with an editor open ---------------------------------

enum { TIMED_CYCLES = 4000 };

typedef struct {
  int priority;
  bool allowed, timed;
  _Atomic bool done;
  double took[TIMED_CYCLES];
} RealTime;

static int compare_doubles(const void *a, const void *b) {
  double x = *(const double *)a, y = *(const double *)b;
  return x < y ? -1 : x > y;
}

static void *real_time_cycles(void *data) {
  RealTime *rt = data;
  struct sched_param param = {.sched_priority = rt->priority};
  rt->allowed = sched_setscheduler(0, SCHED_FIFO, &param) == 0;
  if (rt->allowed) {
    for (unsigned i = 0; i < (rt->timed ? TIMED_CYCLES : 20); ++i) {
      double took = cycle(256);
      assert(scaled(256, 0.5f));  // the gain nothing here moves
      if (rt->timed) rt->took[i] = took;
    }
    if (rt->timed) qsort(rt->took, TIMED_CYCLES, sizeof(rt->took[0]), compare_doubles);
  }
  atomic_store(&rt->done, true);
  return NULL;
}

// Cycles on a thread at real-time priority while this one goes on turning.
static void run_real_time(RealTime *rt, bool timed) {
  pthread_t thread;
  rt->timed = timed;
  atomic_store(&rt->done, false);
  audio_elsewhere = true;
  assert(pthread_create(&thread, NULL, real_time_cycles, rt) == 0);
  while (!atomic_load(&rt->done)) turn(5);
  pthread_join(thread, NULL);
  audio_elsewhere = false;
}

static void note_round_trip(const RealTime *rt, const char *what) {
  printf("note: %s: round trip of 256 frames at real-time priority %d over %d cycles: "
         "median %.1f us, 99th percentile %.1f us, longest %.1f us\n",
         what, rt->priority, TIMED_CYCLES, rt->took[TIMED_CYCLES / 2] * 1e3,
         rt->took[TIMED_CYCLES * 99 / 100] * 1e3, rt->took[TIMED_CYCLES - 1] * 1e3);
}

static void check_round_trip(void) {
  static RealTime rt;
  rt = (RealTime){.priority = 5};
  // One plugin throughout, so the editor is all that differs.
  assert(begin("clap", clap_plugin, PLUGIN "params"));
  Wine *w = h.impl;
  run_real_time(&rt, false);
  if (!rt.allowed) {
    end();
    puts("SKIP: this user has no real-time limit, so the audio round trip with an editor "
         "open was not measured");
    return;
  }
  // The tick gives the Windows audio thread the waiting thread's priority.
  settle(w->scheduled);
  assert((sched_getscheduler(w->audio_tid) & ~SCHED_RESET_ON_FORK) == SCHED_FIFO);
  run_real_time(&rt, true);
  note_round_trip(&rt, "CLAP, editor closed");
  open_editor(TEST_EDITOR_WIDTH, TEST_EDITOR_HEIGHT, 1);
  run_real_time(&rt, true);
  note_round_trip(&rt, "CLAP, editor open");
  // And with the editor at work: the frame moved about and resized while
  // the cycles run, each one exact.
  pthread_t thread;
  atomic_store(&rt.done, false);
  audio_elsewhere = true;
  assert(pthread_create(&thread, NULL, real_time_cycles, &rt) == 0);
  for (unsigned i = 0; !atomic_load(&rt.done); ++i) {
    // Up to 440x280, and always whole on the screen.
    XMoveWindow(user, h.window, (int)(i % 7) * (screen_width - 440) / 7,
                (int)(i % 5) * (screen_height - 280) / 5);
    XResizeWindow(user, h.window, 320 + (i % 4) * 40, 200 + (i % 3) * 40);
    XSync(user, False);
    turn(15);
  }
  pthread_join(thread, NULL);
  audio_elsewhere = false;
  note_round_trip(&rt, "CLAP, editor open, frame moved and resized throughout");
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  end();
  puts("PASS: with an editor open, and with its frame moved and resized, every block at "
       "real-time priority comes back exact");
}

// --- by hand -----------------------------------------------------------------

static bool save_picture(const char *path) {
  XWindowAttributes frame;
  if (!attributes_of(h.window, &frame) || frame.map_state != IsViewable) return false;
  XImage *image = XGetImage(user, h.window, 0, 0, (unsigned)frame.width,
                            (unsigned)frame.height, AllPlanes, ZPixmap);
  FILE *file = image ? fopen(path, "wb") : NULL;
  if (!file) return false;
  fprintf(file, "P6\n%d %d\n255\n", frame.width, frame.height);
  for (int y = 0; y < frame.height; ++y)
    for (int x = 0; x < frame.width; ++x) {
      unsigned long pixel = XGetPixel(image, x, y);
      fputc((int)(pixel >> 16 & 0xff), file);
      fputc((int)(pixel >> 8 & 0xff), file);
      fputc((int)(pixel & 0xff), file);
    }
  XDestroyImage(image);
  return fclose(file) == 0;
}

static int try_plugin(int argc, char **argv) {
  const char *format = argv[2], *path = argv[5];
  char again[PATH_MAX];
  snprintf(again, sizeof(again), "%s.again", path);
  if (!begin(format, argv[3], argv[4])) {
    fputs("the plugin did not load\n", stderr);
    return 1;
  }
  Wine *w = h.impl;
  printf("loaded %s, %u controls\n", h.plugin_name, h.control_count);
  double asked = now_ms();
  const char *shown = reply_to("show");
  printf("show: %s, after %.0f ms\n", shown, now_ms() - asked);
  if (!h.editor_open) return 1;
  turn(4000);
  XWindowAttributes frame, inside;
  if (!attributes_of(h.window, &frame) || !attributes_of(w->editor, &inside)) return 1;
  int frame_x, frame_y;
  origin_of(h.window, &frame_x, &frame_y);
  printf("the frame is %dx%d at %d,%d; Wine's window is %dx%d at %d,%d in it, %s\n",
         frame.width, frame.height, frame_x, frame_y, inside.width, inside.height,
         inside.x, inside.y, inside.map_state == IsViewable ? "shown" : "not shown");
  printf("picture %s: %s\n", path, save_picture(path) ? "saved" : "not saved");
  for (int i = 6; i + 1 < argc; i += 2) {
    printf("click at %s,%s\n", argv[i], argv[i + 1]);
    click(atoi(argv[i]), atoi(argv[i + 1]), 1);
    turn(700);
  }
  printf("hide: %s\n", reply_to("hide"));
  turn(500);
  asked = now_ms();
  shown = reply_to("show");
  printf("show again: %s, after %.0f ms\n", shown, now_ms() - asked);
  turn(4000);
  if (h.editor_open && attributes_of(h.window, &frame))
    printf("the frame is %dx%d; picture %s: %s\n", frame.width, frame.height, again,
           save_picture(again) ? "saved" : "not saved");
  bool failed = atomic_load(&h.audio_error) || atomic_load(&w->dead);
  end();
  puts(failed ? "the instance failed" : "the instance ended in good order");
  return failed;
}

// --- main -------------------------------------------------------------------

// A Wine server left in the prefix by a run that had no display, test-winhost
// for one, lingers for some seconds with a desktop that has none, and an
// editor opened meanwhile gets no window. The prefix is the test's own, so
// its server is ended, and waited for, before anything starts.
static void end_wine_server(const char *wine) {
  char server[PATH_MAX];
  snprintf(server, sizeof(server), "%s", wine);
  char *slash = strrchr(server, '/');
  size_t directory = slash ? (size_t)(slash - server) + 1 : 0;
  snprintf(server + directory, sizeof(server) - directory, "wineserver");
  if (access(server, X_OK) != 0 && !wine_find_program("wineserver", server, sizeof(server)))
    return;
  static const char *const steps[] = {"-k", "-w"};
  for (size_t i = 0; i < sizeof(steps) / sizeof(steps[0]); ++i) {
    pid_t child = fork();
    assert(child >= 0);
    if (child == 0) {
      int null = open("/dev/null", O_WRONLY);
      if (null >= 0) dup2(null, STDERR_FILENO);
      execl(server, server, steps[i], (char *)NULL);
      _exit(127);
    }
    int status;
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {}
  }
}

static void make_directories(char *path) {
  for (char *slash = strchr(path + 1, '/'); slash; slash = strchr(slash + 1, '/')) {
    *slash = 0;
    mkdir(path, 0700);
    *slash = '/';
  }
}

int main(int argc, char **argv) {
  static char winhost[PATH_MAX], clap[PATH_MAX], vst3[PATH_MAX], wine[PATH_MAX],
      prefix[PATH_MAX];
  bool by_hand = argc >= 6 && argc % 2 == 0 && !strcmp(argv[1], "try");
  if (!by_hand && (argc != 4 || !realpath(argv[1], winhost) || !realpath(argv[2], clap) ||
                   !realpath(argv[3], vst3))) {
    fputs("usage: winhost-editor WINHOST-EXE CLAP-PLUGIN VST3-PLUGIN\n"
          "       winhost-editor try clap|vst3 PLUGIN ID PICTURE.ppm [X Y]...\n",
          stderr);
    return 2;
  }
  setvbuf(stdout, NULL, _IOLBF, 0);
  const char *named = getenv("OPENXLR_WINE");
  if (!wine_find_program(named && named[0] ? named : "wine", wine, sizeof(wine))) {
    puts("SKIP: test-winhost-editor needs Wine: wine on PATH, or OPENXLR_WINE");
    return 0;
  }
  // Wine draws on X11 here, whatever else the session has.
  unsetenv("WAYLAND_DISPLAY");
  user = XOpenDisplay(NULL);
  if (!user) {
    puts("SKIP: test-winhost-editor needs a display; run it under xvfb-run");
    return 0;
  }
  root = DefaultRootWindow(user);
  Atom type;
  int format;
  unsigned long count, left;
  unsigned char *manager = NULL;
  managed = XGetWindowProperty(user, root, XInternAtom(user, "_NET_SUPPORTING_WM_CHECK", False),
                               0, 1, False, XA_WINDOW, &type, &format, &count, &left,
                               &manager) == Success && count == 1;
  if (manager) XFree(manager);
  int event, error, major, minor;
  if (!XTestQueryExtension(user, &event, &error, &major, &minor)) {
    puts("SKIP: test-winhost-editor needs a display with the XTEST extension");
    return 0;
  }
  if (managed && !getenv("OPENXLR_TEST_WINHOST_WM")) {
    puts("SKIP: this display has a window manager, and this test moves the pointer and "
         "clicks. Run it under xvfb-run, or on a private display with a window manager "
         "and OPENXLR_TEST_WINHOST_WM=1");
    return 0;
  }
  screen_width = DisplayWidth(user, DefaultScreen(user));
  screen_height = DisplayHeight(user, DefaultScreen(user));
  int least_width = managed ? MANAGED_SCREEN_WIDTH : SCREEN_WIDTH;
  int least_height = managed ? MANAGED_SCREEN_HEIGHT : SCREEN_HEIGHT;
  if (!by_hand && (screen_width < least_width || screen_height < least_height)) {
    printf("SKIP: the screen is %dx%d and this test needs %dx%d: run it under "
           "xvfb-run -a -s '-screen 0 %dx%dx24'\n",
           screen_width, screen_height, least_width, least_height, least_width, least_height);
    return 0;
  }
  // What the helper's main sets up before any editor opens.
  x_main_thread = pthread_self();
  XSetErrorHandler(report_x_error);
  XSetIOErrorHandler(lost_x_connection);
  setenv("WINEDEBUG", "-all", 0);
  if (by_hand) {
    if (!getenv("WINEPREFIX")) {
      fputs("set WINEPREFIX to the prefix the plugin is installed in\n", stderr);
      return 2;
    }
    pw_init(NULL, NULL);
    int result = try_plugin(argc, argv);
    pw_deinit();
    return result;
  }
  // The Wine prefix test-winhost uses, never the user's.
  const char *chosen = getenv("OPENXLR_TEST_WINEPREFIX"), *cache = getenv("XDG_CACHE_HOME");
  if (chosen && chosen[0])
    snprintf(prefix, sizeof(prefix), "%s", chosen);
  else if (cache && cache[0] == '/')
    snprintf(prefix, sizeof(prefix), "%s/openxlr-test/wineprefix", cache);
  else
    snprintf(prefix, sizeof(prefix), "%s/.cache/openxlr-test/wineprefix",
             getenv("HOME") ? getenv("HOME") : "/tmp");
  make_directories(prefix);
  setenv("WINEPREFIX", prefix, 1);
  setenv("WINEDLLOVERRIDES", "mscoree,mshtml=;winemenubuilder.exe=d;wineusb.sys=d", 1);
  setenv("OPENXLR_WINHOST", winhost, 1);
  end_wine_server(wine);
  clap_plugin = clap;
  vst3_plugin = vst3;
  printf("note: the display has %s window manager and a screen of %dx%d\n",
         managed ? "a" : "no", screen_width, screen_height);

  pw_init(NULL, NULL);
  check_editor("CLAP", "clap", clap, PLUGIN "params");
  check_editor("VST3", "vst3", vst3, VST3_PARAMS);
  check_closed_by_plugin();
  check_no_editor();
  check_killed("CLAP", "clap", clap, PLUGIN "params");
  check_killed("VST3", "vst3", vst3, VST3_PARAMS);
  check_round_trip();
  pw_deinit();
  XCloseDisplay(user);
  return 0;
}
