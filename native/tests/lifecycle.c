// SPDX-License-Identifier: GPL-3.0-only
// Take a real plugin through the host's backend without a PipeWire graph:
// load, activate, audio cycles, the editor, deactivate, unload. Any child
// the plugin starts (a Wine host for a bridged plugin) is reported as it
// ends, without reaping it, so the bridge's own bookkeeping is untouched.
// A plugin whose process dies takes this check down with it: that is the
// signal. Exits 0 with PASS when every step completed.
#define main host_program_main
#include "../host.c"
#undef main
#include <time.h>

static double started;

static double now_ms(void) {
  struct timespec ts;
  clock_gettime(CLOCK_MONOTONIC, &ts);
  return ts.tv_sec * 1000.0 + ts.tv_nsec / 1e6;
}

static void report(const char *what, double value) {
  fprintf(stderr, "lifecycle: %s %.0f at %.0f ms\n", what, value, now_ms() - started);
}

static char *append_text(char *out, const char *text) {
  while (*text)
    *out++ = *text++;
  return out;
}

static char *append_number(char *out, unsigned long value) {
  char digits[32];
  unsigned count = 0;
  do {
    digits[count++] = (char)('0' + value % 10);
    value /= 10;
  } while (value);
  while (count)
    *out++ = digits[--count];
  return out;
}

static void child_ended(int sig, siginfo_t *info, void *context) {
  const char *how = info->si_code == CLD_EXITED   ? "exited with status"
                    : info->si_code == CLD_KILLED ? "killed by signal"
                    : info->si_code == CLD_DUMPED ? "dumped core on signal"
                                                  : "changed state";
  // stdio is not signal-safe, and this may interrupt a bridge thread while
  // it is printing. Keep the report on the stack and issue one write.
  int saved_errno = errno;
  char message[160], *out = append_text(message, "lifecycle: child ");
  out = append_number(out, (unsigned long)info->si_pid);
  out = append_text(out, " ");
  out = append_text(out, how);
  out = append_text(out, " ");
  out = append_number(out, (unsigned long)info->si_status);
  out = append_text(out, " at ");
  out = append_number(out, (unsigned long)(now_ms() - started));
  out = append_text(out, " ms\n");
  (void)write(STDERR_FILENO, message, (size_t)(out - message));
  errno = saved_errno;
}

// Sleep while servicing the plugin's run loop, as the host's main thread
// does between commands.
static void wait_ms(Host *h, long wait, const char *what) {
  struct pw_loop *loop = pw_main_loop_get_loop(h->loop);
  for (long waited = 0; waited < wait; waited += 10) {
    pw_loop_iterate(loop, 0);
    struct timespec tick = {0, 10000000};
    nanosleep(&tick, NULL);
  }
  fprintf(stderr, "lifecycle: %s wait over at %.0f ms\n", what, now_ms() - started);
}

static long option(int argc, char **argv, int first, const char *name, long fallback) {
  for (int i = first; i + 1 < argc; ++i)
    if (!strcmp(argv[i], name))
      return strtol(argv[i + 1], NULL, 10);
  return fallback;
}

// Every `--set SYMBOL=VALUE`: move that control before the first cycle, so
// a run can show the plugin doing something to the audio rather than
// passing it through at its defaults. Returns how many were applied.
static unsigned apply_sets(Host *h, int argc, char **argv, int first) {
  unsigned applied = 0;
  for (int i = first; i + 1 < argc; ++i) {
    if (strcmp(argv[i], "--set"))
      continue;
    const char *setting = argv[i + 1];
    const char *equals = strchr(setting, '=');
    if (!equals) {
      fprintf(stderr, "lifecycle: --set wants SYMBOL=VALUE, not '%s'\n", setting);
      continue;
    }
    size_t length = (size_t)(equals - setting);
    bool found = false;
    for (uint32_t c = 0; c < h->control_count; ++c) {
      Control *control = &h->controls[c];
      if (strlen(control->symbol) != length || strncmp(control->symbol, setting, length))
        continue;
      float value = strtof(equals + 1, NULL);
      control_set_desired(control, value);
      fprintf(stderr, "lifecycle: set %s to %g\n", control->symbol, value);
      found = true;
      ++applied;
    }
    if (!found)
      fprintf(stderr, "lifecycle: no control named '%.*s'\n", (int)length, setting);
  }
  return applied;
}

int main(int argc, char **argv) {
  started = now_ms();
  struct sigaction on_child = {0};
  on_child.sa_sigaction = child_ended;
  on_child.sa_flags = SA_SIGINFO | SA_RESTART;
  sigaction(SIGCHLD, &on_child, NULL);
  const Backend *backend = argc >= 2 ? backend_named(argv[1]) : NULL;
  int first = backend ? 2 + backend->argument_count : 0;
  if (!backend || argc < first) {
    fputs("usage: lifecycle (lv2 URI | clap FILE ID | vst3 BUNDLE CLASS-ID)\n"
          "       [--channels N] [--frames N] [--cycles N] [--tone HZ] [--wait-before-activate MS]\n"
          "       [--wait-after-activate MS] [--editor-at N] [--reactivate-at N]\n"
          "       [--set SYMBOL=VALUE]...\n",
          stderr);
    return 2;
  }
  long channels = option(argc, argv, first, "--channels", 2);
  if (channels < 1 || channels > MAX_CHANNELS)
    return 2;
  uint32_t frames = (uint32_t)option(argc, argv, first, "--frames", 2048);
  long cycles = option(argc, argv, first, "--cycles", 60);
  long tone = option(argc, argv, first, "--tone", 440);
  if (tone < 1 || tone > 20000)
    return 2;
  long before = option(argc, argv, first, "--wait-before-activate", 0);
  long after = option(argc, argv, first, "--wait-after-activate", 0);
  long editor_at = option(argc, argv, first, "--editor-at", -1);
  long reactivate_at = option(argc, argv, first, "--reactivate-at", -1);
  if (frames == 0 || frames > MAX_FRAMES)
    return 2;

  static Host h;
  h.backend = backend;
  h.node_name = "openxlr-lifecycle";
  h.rate = 48000;
  h.channels = (unsigned)channels;
  h.main_thread = pthread_self();
  h.silence = calloc(MAX_FRAMES, sizeof(float));
  h.scratch = calloc(MAX_FRAMES, sizeof(float));
  if (!h.silence || !h.scratch || !configure_editor_environment())
    return 1;
  XSetErrorHandler(report_x_error);
  XSetIOErrorHandler(lost_x_connection);
  pw_init(NULL, NULL);
  h.loop = pw_main_loop_new(NULL);
  if (!h.loop)
    return 1;
  struct pw_loop *loop = pw_main_loop_get_loop(h.loop);
  pw_loop_enter(loop);

  if (!backend->load(&h, argv + 2)) {
    fputs("lifecycle: load failed\n", stderr);
    return 1;
  }
  fprintf(stderr, "lifecycle: loaded '%s', %u controls, at %.0f ms\n",
          h.plugin_name, h.control_count, now_ms() - started);
  if (before > 0)
    wait_ms(&h, before, "pre-activate");
  if (!backend->activate(&h)) {
    fputs("lifecycle: activate failed\n", stderr);
    return 1;
  }
  report("activated, result", 1);
  // Settings are delivered the way the daemon's are: the main-thread tick
  // hands them to the plugin, here once before the first cycle.
  if (apply_sets(&h, argc, argv, first) > 0 && backend->main_thread)
    backend->main_thread(&h);
  if (after > 0)
    wait_ms(&h, after, "post-activate");

  float *in[MAX_CHANNELS] = {0}, *out[MAX_CHANNELS] = {0};
  for (unsigned c = 0; c < h.channels; ++c) {
    in[c] = calloc(MAX_FRAMES, sizeof(float));
    out[c] = calloc(MAX_FRAMES, sizeof(float));
    if (!in[c] || !out[c])
      return 1;
  }
  double phase = 0;
  long editor_close_at = editor_at >= 0 ? editor_at + 10 : -1;
  for (long n = 0; n < cycles; ++n) {
    if (n == reactivate_at) {
      backend->deactivate(&h);
      report("deactivated then activated, result", backend->activate(&h));
    }
    if (n == editor_at)
      report("editor open, result", open_ui(&h));
    if (n == editor_close_at) {
      close_ui(&h);
      report("editor closed, still open", h.editor_open);
    }
    for (uint32_t i = 0; i < frames; ++i) {
      float sample = 0.25f * (float)sin(phase);
      phase += 2 * 3.14159265358979323846 * (double)tone / 48000.0;
      for (unsigned c = 0; c < h.channels; ++c)
        in[c][i] = sample;
    }
    backend->process(&h, frames, in, out);
    float peak = 0;
    for (uint32_t i = 0; i < frames; ++i)
      peak = fmaxf(peak, fabsf(out[0][i]));
    printf("cycle %ld at %.0f ms, output peak %.3f\n", n, now_ms() - started, peak);
    fflush(stdout);
    for (int k = 0; k < 4; ++k)
      pw_loop_iterate(loop, 0);
    if (backend->main_thread)
      backend->main_thread(&h);
    if (h.editor_open)
      pump_editor(&h, true);
    struct timespec pause = {0, (long)(frames * 1000000000ULL / h.rate)};
    nanosleep(&pause, NULL);
  }
  if (h.editor_open)
    close_ui(&h);
  backend->deactivate(&h);
  backend->unload(&h);
  pw_loop_leave(loop);
  pw_main_loop_destroy(h.loop);
  pw_deinit();
  fprintf(stderr, "lifecycle: PASS, %ld cycles, unloaded at %.0f ms\n", cycles,
          now_ms() - started);
  return 0;
}
