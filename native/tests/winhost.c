// SPDX-License-Identifier: GPL-3.0-only
// The Windows backend against the Windows test plugin, under Wine and
// without a graph: the host's audio callback is called here on plain
// buffers, with the Windows plugin host started for real. It checks that
// the audio that comes back is the plugin's, sample for sample, at every
// block size; the latency the plugin reports and a restart that changes it;
// the plugin's main-thread callback and a timer it asks for; its parameters
// as controls, set from here and moved by the plugin; and that a Windows
// host that is killed, crashes, stalls or is merely late never holds the
// audio callback and never plays anything but the plugin's audio or
// silence. Neither side may be led anywhere by what the other writes.
// Usage: winhost WINHOST-EXE TEST-PLUGIN
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
// The product waits six seconds on a block that never returns; the stall
// test runs the same code with less patience.
#define WINE_STALL_MS 1500
#include "../wine.c"
#include <assert.h>

#define PLUGIN "org.openxlr.test.winhost."

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return NULL;
}

static Host h;  // static: the control table is large
static float in_buffers[MAX_CHANNELS][MAX_FRAMES], out_buffers[MAX_CHANNELS][MAX_FRAMES];
static float quiet[MAX_FRAMES], spare[MAX_FRAMES];
static const char *plugin_path;
static uint64_t position;  // of the first frame of the last cycle

static double now_ms(void) {
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  return (double)now.tv_sec * 1e3 + (double)now.tv_nsec / 1e6;
}

static void sleep_ms(unsigned ms) {
  struct timespec t = {ms / 1000, (long)(ms % 1000) * 1000000};
  while (nanosleep(&t, &t) != 0) {}
}

// A signal that is never zero and names its own position exactly, so
// silence, a misplaced frame and another cycle's frame are all told apart
// from the right one. Half and a quarter of it are exact too.
static float signal_at(uint64_t at, unsigned channel) {
  return (float)((at & 0xfffff) + 1 + (channel ? 0x100000 : 0)) / (float)(1 << 22);
}

static void fresh_host(unsigned channels) {
  memset(&h, 0, sizeof(h));
  h.backend = &wine_backend;
  h.rate = 48000;
  h.channels = channels;
  h.main_thread = pthread_self();
  h.silence = quiet;
  h.scratch = spare;
  h.loop = pw_main_loop_new(NULL);
  assert(h.loop);
  for (unsigned c = 0; c < MAX_CHANNELS; ++c) {
    test_buffers[c] = in_buffers[c];
    test_buffers[MAX_CHANNELS + c] = out_buffers[c];
    h.in_ports[c] = &test_buffers[c];
    h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
}

static bool load_at(const char *module, const char *id, unsigned channels) {
  fresh_host(channels);
  char *arguments[] = {"clap", (char *)module, (char *)id};
  return wine_backend.load(&h, arguments);
}

static bool begin_at(const char *module, const char *id, unsigned channels) {
  return load_at(module, id, channels) && wine_backend.activate(&h);
}

// Load without activating, which is where the helper applies the values on
// its command line.
static bool load(const char *kind, unsigned channels) {
  char id[128];
  snprintf(id, sizeof(id), PLUGIN "%s", kind);
  return load_at(plugin_path, id, channels);
}

static bool begin(const char *kind, unsigned channels) {
  return load(kind, channels) && wine_backend.activate(&h);
}

static Control *control_named(const char *symbol) {
  for (uint32_t i = 0; i < h.control_count; ++i)
    if (!strcmp(h.controls[i].symbol, symbol)) return &h.controls[i];
  return NULL;
}

static void end(void) {
  if (h.impl) {
    wine_backend.deactivate(&h);
    wine_backend.unload(&h);
  }
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

static bool silent(uint32_t frames) { return scaled(frames, 0.0f); }

static int compare_doubles(const void *a, const void *b) {
  double x = *(const double *)a, y = *(const double *)b;
  return x < y ? -1 : x > y;
}

static void check_audio(void) {
  assert(begin("gain", 2));
  Wine *w = h.impl;
  assert(!strcmp(h.plugin_name, "Test Gain"));
  assert(w->audio_tid && w->process);
  assert(wine_latency(&h) == 0);
  static const uint32_t sizes[] = {256, 64, 1024, 1, 480, 2048, MAX_FRAMES};
  for (size_t s = 0; s < sizeof(sizes) / sizeof(sizes[0]); ++s) {
    enum { CYCLES = 400 };
    static double took[CYCLES];
    for (unsigned i = 0; i < CYCLES; ++i) {
      took[i] = cycle(sizes[s]);
      assert(scaled(sizes[s], 0.5f));
    }
    qsort(took, CYCLES, sizeof(took[0]), compare_doubles);
    printf("note: %4u frames: round trip median %.1f us, longest %.1f us, "
           "at ordinary priority\n",
           sizes[s], took[CYCLES / 2] * 1e3, took[CYCLES - 1] * 1e3);
  }
  // PipeWire may hand a node one buffer for both directions.
  for (unsigned c = 0; c < MAX_CHANNELS; ++c)
    test_buffers[MAX_CHANNELS + c] = in_buffers[c];
  for (unsigned i = 0; i < 100; ++i) {
    for (unsigned c = 0; c < 2; ++c)
      for (uint32_t n = 0; n < 256; ++n) in_buffers[c][n] = signal_at(i * 256 + n, c);
    struct spa_io_position at = {0};
    at.clock.duration = 256;
    at.clock.rate.denom = h.rate;
    process_audio(&h, &at);
    for (unsigned c = 0; c < 2; ++c)
      for (uint32_t n = 0; n < 256; ++n)
        assert(in_buffers[c][n] == signal_at(i * 256 + n, c) * 0.5f);
  }
  wine_backend.main_thread(&h);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  // Asked to quit, the Windows host takes the plugin down and leaves by
  // itself with a clean exit.
  wine_backend.deactivate(&h);
  assert(wine_stop_child(w));
  end();
  puts("PASS: the Windows plugin's audio comes back exact at every block size, in place too, and its host leaves cleanly");
}

static void check_layouts(void) {
  assert(begin("mono", 1));
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  // A second input port is handed silence, which this plugin adds in.
  assert(begin("sidechain", 2));
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  // A stereo plugin does not fit a mono chain, and says so without loading.
  assert(!begin("gain", 1));
  end();
  puts("PASS: a mono plugin and one with a sidechain port run, and a plugin of the wrong width is refused");
}

static void check_latency(void) {
  assert(begin("latency", 2));
  assert(wine_latency(&h) == 480);
  assert(!report_latency(&h));  // nothing before the first cycle
  cycle(256);
  assert(report_latency(&h) && h.reported_latency == 480);
  end();

  // The plugin asks for a restart on its eighth block and reports another
  // latency once activated again. The audio is the plugin's or silence
  // throughout, and the new figure arrives without replacing the process.
  assert(begin("restart", 2));
  Wine *w = h.impl;
  pid_t process = w->process;
  assert(wine_latency(&h) == 100);
  bool held = false, back = false;
  for (unsigned i = 0; i < 3000 && !back; ++i) {
    cycle(256);
    bool quiet_block = silent(256);
    assert(quiet_block || scaled(256, 0.5f));
    held = held || quiet_block;
    back = held && !quiet_block && wine_latency(&h) == 300;
    wine_backend.main_thread(&h);
    sleep_ms(1);
  }
  assert(held && back && !atomic_load(&h.audio_error));
  assert(w->process == process && !wine_reap(w));
  for (unsigned i = 0; i < 100; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  puts("PASS: the plugin's latency is reported, and a restart it asks for changes it in place");
}

static void check_callback(void) {
  // The plugin asks for its main-thread callback from the audio thread and
  // quarters its input once it has had it.
  assert(begin("callback", 2));
  bool quartered = false;
  for (unsigned i = 0; i < 2000 && !quartered; ++i) {
    cycle(256);
    quartered = scaled(256, 0.25f);
    assert(quartered || scaled(256, 0.5f));
    sleep_ms(1);
  }
  assert(quartered);
  end();
  puts("PASS: a callback the plugin asks for on its audio thread runs on the Windows main thread");
}

static void check_timer(void) {
  // The plugin asks for a timer when it is made and quarters its input once
  // the timer has called it three times.
  assert(begin("timer", 2));
  double start = now_ms();
  bool quartered = false;
  for (unsigned i = 0; i < 3000 && !quartered; ++i) {
    cycle(256);
    quartered = scaled(256, 0.25f);
    assert(quartered || scaled(256, 0.5f));
    sleep_ms(1);
  }
  assert(quartered);
  end();
  printf("PASS: a timer the plugin asks for calls it on the Windows main thread, "
         "three calls of 20 ms in %.0f ms\n", now_ms() - start);
}

// One block sent by hand, with whatever the shared file holds.
static bool raw_cycle(Wine *w) {
  char byte = WIRE_WAKE_PROCESS;
  return write(w->wake_out, &byte, 1) == 1 && wine_await_done(w, 2000);
}

static void check_params(void) {
  // The plugin's parameters arrive as controls under the names a native
  // CLAP plugin's have. A hidden one and the plugin's own bypass do not.
  assert(load("params", 2));
  Wine *w = h.impl;
  Control *gain = control_named("1"), *move = control_named("2"), *level = control_named("3");
  assert(h.control_count == 3 && gain && move && level);
  assert(!gain->output && !move->output && level->output);
  assert(gain->minimum == 0 && gain->maximum == 1 && atomic_load(&gain->desired) == 0.5f);
  assert(level->maximum == 2 && atomic_load(&level->observed) == 1.0f);
  // A value from the command line is set here, before activation, and the
  // first block already carries it.
  assert(set_control(&h, "1", 0.75f));
  assert(!set_control(&h, "3", 1) && !set_control(&h, "4", 1) && !set_control(&h, "5", 1));
  assert(wine_backend.activate(&h));
  cycle(256);
  assert(scaled(256, 0.75f));
  // A value set while audio runs is heard from the next block on. The
  // plugin answers each gain it is sent with the same value, which is no
  // move of its own: nothing is published, and so the second set, made
  // before this side's tick, is not taken for an old one.
  assert(set_control(&h, "1", 0.125f));
  for (unsigned i = 0; i < 50; ++i) {
    cycle(256);
    assert(scaled(256, 0.125f));
  }
  assert(wire_value_count(atomic_load(&w->shared->values[0])) == 0);
  // The meter reads twice the gain, and follows on the ticks of both sides.
  bool metered = false;
  for (unsigned i = 0; i < 3000 && !metered; ++i) {
    cycle(256);
    wine_backend.main_thread(&h);
    metered = atomic_load(&level->observed) == 0.25f;
    sleep_ms(1);
  }
  assert(metered);

  // The plugin moves its own gain, as its editor would. The audio has it in
  // the same block.
  assert(set_control(&h, "2", 0.25f));
  cycle(256);
  assert(scaled(256, 0.25f));
  // This side has not looked yet. A value it sets now was made without
  // knowing of the plugin's, so the Windows side drops it.
  assert(atomic_load(&gain->desired) == 0.125f);
  assert(set_control(&h, "1", 0.75f));
  cycle(256);
  assert(scaled(256, 0.25f));
  // The tick takes the plugin's value, and nothing is sent back for it.
  wine_backend.main_thread(&h);
  assert(atomic_load(&gain->desired) == 0.25f);
  for (unsigned i = 0; i < 20; ++i) {
    cycle(256);
    assert(scaled(256, 0.25f) && w->shared->set_count == 0);
  }
  // A value set after that is taken.
  assert(set_control(&h, "1", 0.75f));
  cycle(256);
  assert(scaled(256, 0.75f) && w->shared->set_count == 1);

  // What the Windows side makes of sets this side would never write: a
  // count past the list, indexes past the table, a value that is no
  // number, a count of the control's value that is not the current one,
  // and a meter. None may change anything.
  WireShared *shared = w->shared;
  uint32_t gain_count = wire_value_count(atomic_load(&shared->values[0]));
  uint32_t level_count = wire_value_count(atomic_load(&shared->values[2]));
  position += MAX_FRAMES;
  for (unsigned c = 0; c < 2; ++c)
    for (uint32_t n = 0; n < 256; ++n) shared->in[c][n] = signal_at(position + n, c);
  shared->frames = 256;
  for (uint32_t i = 0; i < WIRE_MAX_SETS; ++i)
    shared->sets[i] = (WireSet){.index = 0xffffffffu - i, .value = 0.5f};
  shared->sets[0] = (WireSet){.index = 0, .based_on = gain_count, .value = NAN};
  shared->sets[1] = (WireSet){.index = 0, .based_on = gain_count + 1, .value = 0.5f};
  shared->sets[2] = (WireSet){.index = 2, .based_on = level_count, .value = 0.5f};
  shared->sets[3] = (WireSet){.index = h.control_count, .value = 0.5f};
  shared->set_count = 0xffffffffu;
  assert(raw_cycle(w) && shared->status == WIRE_BLOCK_OK);
  for (unsigned c = 0; c < 2; ++c)
    for (uint32_t n = 0; n < 256; ++n)
      assert(shared->out[c][n] == signal_at(position + n, c) * 0.75f);
  // A value past the control's range is brought inside it.
  shared->sets[0] = (WireSet){.index = 0, .based_on = gain_count, .value = 5.0f};
  shared->set_count = 1;
  assert(raw_cycle(w) && shared->status == WIRE_BLOCK_OK);
  for (unsigned c = 0; c < 2; ++c)
    for (uint32_t n = 0; n < 256; ++n)
      assert(shared->out[c][n] == signal_at(position + n, c));
  end();
  puts("PASS: the plugin's parameters are controls: set before the first block and "
       "between blocks, moved by the plugin, read as a meter, and the Windows side "
       "takes no set it should not");
}

static void check_many(void) {
  // The plugin declares 5000 parameters. The host's table holds 4096, the
  // same as for a native plugin, and so does the wire.
  assert(begin("many", 2));
  assert(h.control_count == MAX_CONTROLS);
  assert(control_named("4096") && !control_named("4097"));
  // A thousand values set at once go over 256 to a cycle. The plugin
  // quarters its input only while exactly a thousand are set.
  char symbol[16];
  for (unsigned i = 1; i <= 1000; ++i) {
    snprintf(symbol, sizeof(symbol), "%u", i);
    assert(set_control(&h, symbol, 1));
  }
  for (unsigned i = 0; i < 3; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  cycle(256);
  assert(scaled(256, 0.25f));
  // The last control the table holds works like the first.
  assert(set_control(&h, "4096", 1));
  cycle(256);
  assert(scaled(256, 0.5f));
  wine_backend.main_thread(&h);
  assert(!atomic_load(&h.audio_error));
  end();
  puts("PASS: a plugin with more parameters than the table holds gets the table's 4096, "
       "and a thousand values set at once arrive over four cycles");
}

static void check_listing(void) {
  // The Linux side on a list no Windows host of ours would write. A count
  // past what the wire carries is refused before anything is read.
  static Wine fake;
  memset(&fake, 0, sizeof(fake));
  fake.shared = calloc(1, sizeof(WireShared));
  assert(fake.shared);
  WireControl *listed = fake.shared->controls;
  fresh_host(2);
  assert(!wine_take_controls(&h, &fake, WIRE_MAX_CONTROLS + 1) && h.control_count == 0);
  // A name with a space in it, and an empty one.
  strcpy(listed[0].symbol, "7");
  listed[0].maximum = 1;
  listed[0].initial = 0.5f;
  strcpy(listed[1].symbol, "two words");
  assert(!wine_take_controls(&h, &fake, 2));
  free(fake.controls);
  fake.controls = NULL;
  fresh_host(2);
  listed[1].symbol[0] = 0;
  assert(!wine_take_controls(&h, &fake, 2));
  free(fake.controls);
  fake.controls = NULL;
  fresh_host(2);
  // A name with no end is cut to what a name may hold, and bounds and a
  // value that are no numbers become the host's own.
  memset(listed[1].symbol, 'a', WIRE_SYMBOL);
  listed[1].minimum = NAN;
  listed[1].maximum = INFINITY;
  listed[1].initial = NAN;
  assert(wine_take_controls(&h, &fake, 2) && h.control_count == 2);
  assert(strlen(h.controls[1].symbol) == WIRE_SYMBOL - 1);
  assert(isfinite(h.controls[1].minimum) && isfinite(h.controls[1].maximum));
  assert(atomic_load(&h.controls[1].desired) == 0);
  // A published value that is no number is not taken, and one past the
  // control's range is brought inside it.
  atomic_store(&fake.shared->values[0], wire_value(1, NAN));
  atomic_store(&fake.shared->published, 1);
  wine_take_values(&h, &fake);
  assert(atomic_load(&h.controls[0].desired) == 0.5f);
  atomic_store(&fake.shared->values[0], wire_value(2, 9.0f));
  atomic_store(&fake.shared->published, 2);
  wine_take_values(&h, &fake);
  assert(atomic_load(&h.controls[0].desired) == 1.0f);
  free(fake.controls);
  free(fake.shared);
  pw_main_loop_destroy(h.loop);
  puts("PASS: a list of controls with too many entries or a name that cannot be used "
       "is refused, and a published value is checked before it is taken");
}

static void check_refused(void) {
  assert(begin("refuse", 2));
  // Refusals shorter than half a second are glitches; every one is silence.
  const unsigned run = h.rate / 2 / 256;
  for (unsigned i = 1; i < run; ++i) {
    cycle(256);
    assert(silent(256));
  }
  assert(!atomic_load(&h.audio_error));
  cycle(256);
  cycle(256);
  assert(silent(256) && atomic_load(&h.audio_error) && atomic_load(&h.process_failed));
  end();
  puts("PASS: blocks the plugin refuses are silence, and half a second of them fails the instance");
}

// Run cycles a millisecond apart until the instance has failed, for at most
// `limit_ms`. Every cycle has to be the plugin's audio or silence, and none
// may take longer than a late block is waited for. Returns how long the
// failure took, or a negative number when it never came.
static double cycles_until_failure(unsigned limit_ms, double *longest) {
  double start = now_ms();
  *longest = 0;
  while (now_ms() - start < limit_ms) {
    double took = cycle(256);
    if (took > *longest) *longest = took;
    assert(silent(256) || scaled(256, 0.5f));
    wine_backend.main_thread(&h);
    if (atomic_load(&h.audio_error)) return now_ms() - start;
    sleep_ms(1);
  }
  return -1;
}

static void check_killed(void) {
  assert(begin("gain", 2));
  Wine *w = h.impl;
  for (unsigned i = 0; i < 50; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  kill(w->process, SIGKILL);
  double longest, after = cycles_until_failure(2000, &longest);
  assert(after >= 0 && atomic_load(&h.failure_explained));
  assert(longest < WINE_BLOCK_MS + 30);
  cycle(256);
  assert(silent(256));
  end();
  printf("PASS: a killed Windows host is silence at once and reported %.0f ms later, "
         "the longest cycle %.1f ms\n", after, longest);
}

static void check_crashed(void) {
  assert(begin("crash", 2));
  Wine *w = h.impl;
  pid_t process = w->process;
  double longest, after = cycles_until_failure(5000, &longest);
  assert(after >= 0 && atomic_load(&h.failure_explained));
  assert(longest < WINE_BLOCK_MS + 30);
  assert(wine_reap(w) && WIFEXITED(w->wait_status) && WEXITSTATUS(w->wait_status) == 3);
  end();
  assert(kill(process, 0) != 0 && errno == ESRCH);
  printf("PASS: a plugin that crashes inside a block ends its Windows host, reported "
         "%.0f ms after the first cycle, the longest cycle %.1f ms\n", after, longest);
}

static void check_late(void) {
  // Block 16 takes 60 ms. Its cycle gives up after the wait a late block
  // gets, the cycles after it are silence and return at once, and when the
  // answer lands the audio is exact again. Nothing fails.
  assert(begin("slow", 2));
  Wine *w = h.impl;
  for (unsigned i = 0; i < 15; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  double took = cycle(256);
  assert(silent(256) && took >= WINE_BLOCK_MS - 1 && took < WINE_BLOCK_MS + 30);
  assert(atomic_load(&w->stalled_since) != 0);
  unsigned quiet_cycles = 0;
  bool back = false;
  for (unsigned i = 0; i < 500 && !back; ++i) {
    sleep_ms(1);
    took = cycle(256);
    back = scaled(256, 0.5f);
    assert(back || (silent(256) && took < 5));
    quiet_cycles += !back;
    wine_backend.main_thread(&h);
  }
  assert(back && quiet_cycles > 5 && atomic_load(&w->stalled_since) == 0);
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  end();
  printf("PASS: a block 60 ms late costs silence for %u cycles and nothing after\n",
         quiet_cycles + 1);
}

static void check_stalled(void) {
  // Block 16 never returns. The callback keeps returning, silent, and once
  // the block has been out for as long as the host waits on any plugin the
  // instance fails. Unload then ends a Windows host that cannot hear it.
  assert(begin("stall", 2));
  Wine *w = h.impl;
  pid_t process = w->process;
  double longest, after = cycles_until_failure(WINE_STALL_MS + 3000, &longest);
  assert(after >= WINE_STALL_MS && atomic_load(&h.failure_explained));
  assert(longest < WINE_BLOCK_MS + 30);
  assert(!wine_reap(w));
  double start = now_ms();
  end();
  assert(kill(process, 0) != 0 && errno == ESRCH);
  printf("PASS: a plugin stuck inside a block never holds the audio callback, fails the "
         "instance after %.1f s, and its Windows host is ended in %.0f ms\n",
         after / 1e3, now_ms() - start);
}

typedef struct {
  int priority;
  bool allowed;
} RealTime;

static void *real_time_cycles(void *data) {
  RealTime *rt = data;
  struct sched_param param = {.sched_priority = rt->priority};
  rt->allowed = sched_setscheduler(0, SCHED_FIFO, &param) == 0;
  if (rt->allowed)
    for (unsigned i = 0; i < 20; ++i) {
      cycle(256);
      assert(scaled(256, 0.5f));
    }
  return NULL;
}

static void check_scheduling(void) {
  assert(begin("gain", 2));
  Wine *w = h.impl;
  // At ordinary priority the Windows audio thread is left as it is.
  cycle(256);
  wine_backend.main_thread(&h);
  assert(w->scheduled && (sched_getscheduler(w->audio_tid) & ~SCHED_RESET_ON_FORK) == SCHED_OTHER);
  end();

  assert(begin("gain", 2));
  w = h.impl;
  RealTime rt = {.priority = 5};
  pthread_t thread;
  assert(pthread_create(&thread, NULL, real_time_cycles, &rt) == 0);
  pthread_join(thread, NULL);
  if (!rt.allowed) {
    end();
    puts("SKIP: this user has no real-time limit, so the Windows audio thread's "
         "scheduling was only checked at ordinary priority");
    return;
  }
  wine_backend.main_thread(&h);
  struct sched_param param = {0};
  assert((sched_getscheduler(w->audio_tid) & ~SCHED_RESET_ON_FORK) == SCHED_FIFO);
  assert(sched_getparam(w->audio_tid, &param) == 0 && param.sched_priority == rt.priority);
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  puts("PASS: the Windows audio thread is given the scheduling of the thread that waits for it");
}

static void check_refusals(const char *winhost) {
  assert(!begin("no-such-plugin", 2));
  end();
  assert(!begin_at("/nonexistent/plugin.clap", PLUGIN "gain", 2));
  end();
  // Not a Windows module at all.
  assert(!begin_at("/proc/self/exe", PLUGIN "gain", 2));
  end();
  setenv("OPENXLR_WINHOST", "/nonexistent/openxlr-winhost.exe", 1);
  assert(!begin("gain", 2));
  end();
  setenv("OPENXLR_WINHOST", winhost, 1);
  puts("PASS: a missing plugin, module or Windows host is refused with its reason");
}

static void make_directories(char *path) {
  for (char *slash = strchr(path + 1, '/'); slash; slash = strchr(slash + 1, '/')) {
    *slash = 0;
    mkdir(path, 0700);
    *slash = '/';
  }
}

int main(int argc, char **argv) {
  static char winhost[PATH_MAX], plugin[PATH_MAX], wine[PATH_MAX], prefix[PATH_MAX];
  if (argc != 3 || !realpath(argv[1], winhost) || !realpath(argv[2], plugin)) {
    fputs("usage: winhost WINHOST-EXE TEST-PLUGIN\n", stderr);
    return 2;
  }
  setvbuf(stdout, NULL, _IOLBF, 0);
  const char *named = getenv("OPENXLR_WINE");
  if (!wine_find_program(named && named[0] ? named : "wine", wine, sizeof(wine))) {
    puts("SKIP: test-winhost needs Wine: wine on PATH, or OPENXLR_WINE");
    return 0;
  }
  // A Wine prefix of the test's own, kept between runs since making one
  // takes most of a minute. Never the user's.
  const char *chosen = getenv("OPENXLR_TEST_WINEPREFIX"), *cache = getenv("XDG_CACHE_HOME");
  if (chosen && chosen[0])
    snprintf(prefix, sizeof(prefix), "%s", chosen);
  else if (cache && cache[0] == '/')
    snprintf(prefix, sizeof(prefix), "%s/openxlr-test/wineprefix", cache);
  else
    snprintf(prefix, sizeof(prefix), "%s/.cache/openxlr-test/wineprefix",
             getenv("HOME") ? getenv("HOME") : "/tmp");
  make_directories(prefix);
  struct stat made;
  if (stat(prefix, &made) != 0)
    printf("note: making a Wine prefix at %s, which takes a while the first time\n", prefix);
  setenv("WINEPREFIX", prefix, 1);
  setenv("WINEDEBUG", "-all", 0);
  // No Mono or Gecko installers, no menu entries, and no USB driver: Wine
  // has no business with the machine's devices here.
  setenv("WINEDLLOVERRIDES", "mscoree,mshtml=;winemenubuilder.exe=d;wineusb.sys=d", 1);
  unsetenv("DISPLAY");
  unsetenv("WAYLAND_DISPLAY");
  setenv("OPENXLR_WINHOST", winhost, 1);
  plugin_path = plugin;
  // A runtime directory of the test's own, so what the backend leaves in it
  // can be told exactly: nothing.
  static char runtime[PATH_MAX];
  const char *base = getenv("XDG_RUNTIME_DIR");
  snprintf(runtime, sizeof(runtime), "%s/openxlr-test-winhost.XXXXXX",
           base && base[0] == '/' ? base : "/tmp");
  assert(mkdtemp(runtime));
  setenv("XDG_RUNTIME_DIR", runtime, 1);

  pw_init(NULL, NULL);
  check_audio();
  check_layouts();
  check_latency();
  check_callback();
  check_timer();
  check_params();
  check_many();
  check_listing();
  check_refused();
  check_late();
  check_killed();
  check_crashed();
  check_stalled();
  check_scheduling();
  check_refusals(winhost);
  pw_deinit();
  if (rmdir(runtime) != 0) {
    perror("FAIL: the backend left something in its runtime directory");
    return 1;
  }
  puts("PASS: the backend leaves nothing behind in the runtime directory");
  return 0;
}
