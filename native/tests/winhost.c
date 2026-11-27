// SPDX-License-Identifier: GPL-3.0-only
// The Windows backend against the Windows test plugins, a CLAP module and a
// VST3 one, under Wine and without a graph: the host's audio callback is
// called here on plain buffers, with the Windows plugin host started for
// real. It checks that the audio that comes back is the plugin's, sample for
// sample, at every block size; the latency the plugin reports and a restart
// that changes it; the plugin's main-thread callback and a timer it asks
// for; its parameters as controls, set from here and moved by the plugin;
// that a VST3 class is found by the id a Linux host knows it by; and that a
// Windows host that is killed, crashes, stalls or is merely late never holds
// the audio callback and never plays anything but the plugin's audio or
// silence. Neither side may be led anywhere by what the other writes.
//
// It also runs the helper's `wine scan-clap` and `wine scan-vst3` on both
// modules and compares every byte of each description with the one the
// native scanner would write for the same plugins, and the phase lines with
// the native ones. A module that crashes, hangs or prints while it is
// described, and a file that is no module, each have to end the helper
// with the reason and leave no process behind.
//
// Both modules are loaded and described from a directory whose name is not
// ASCII while this process is in the C locale, which is how the daemon runs
// a scan.
//
// Last, the disk identity: what a plugin that asks for \\.\PhysicalDrive0's
// descriptor from its own module is told, in a load and in a scan, against
// the bytes the Linux side built, for a made-up NVMe disk and for this
// machine's own; what the hook must leave to Wine; and that asking once per
// block costs the audio thread no time and no system call.
// Usage: winhost WINHOST-EXE CLAP-PLUGIN VST3-PLUGIN
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
// test runs the same code with less patience. The same for a scan, which
// the product allows three minutes.
#define WINE_STALL_MS 1500
#define WINE_SCAN_MS 6000
#include "../wine.c"
#include "disk-fixture.h"
#include "disk-probe.h"
#include <assert.h>

#define PLUGIN "org.openxlr.test.winhost."

// The VST3 test plugin's classes, by the text a Linux host has for them. The
// module holds each id as a Windows plugin does, with the first eight bytes
// in COM order (tests/winhost-plugin-vst3.cpp), so loading a class by this
// text is what holds the Windows side to the conversion.
#define CLASS "00112233445566778899AABBCCDDEE"
#define VST3_GAIN CLASS "00"
#define VST3_FIXED CLASS "01"
#define VST3_LATENCY CLASS "02"
#define VST3_RESTART CLASS "03"
#define VST3_REFUSE CLASS "04"
#define VST3_PARAMS CLASS "05"
// The gain class with its bytes read in the order a Windows plugin holds
// them, which is the id no Linux host knows it by.
#define VST3_GAIN_IN_COM_ORDER "33221100554477668899AABBCCDDEE00"

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return NULL;
}

static Host h;  // static: the control table is large
static float in_buffers[MAX_CHANNELS][MAX_FRAMES], out_buffers[MAX_CHANNELS][MAX_FRAMES];
static float quiet[MAX_FRAMES], spare[MAX_FRAMES];
static const char *plugin_path;
// The CLAP test plugin built against each C library a Windows plugin may
// bring, msvcrt and the Universal C Runtime. One of the two is never the one
// this host was built with, whichever toolchain built it.
static char plugin_msvcrt[PATH_MAX], plugin_ucrt[PATH_MAX];

// Whether a module names `library` among what it imports: a plain search of
// its bytes, which hold the name of every library they import.
static bool imports(const char *module, const char *library) {
  FILE *file = fopen(module, "rb");
  if (!file)
    return false;
  static char bytes[8 << 20];
  size_t size = fread(bytes, 1, sizeof(bytes), file);
  fclose(file);
  return memmem(bytes, size, library, strlen(library)) != NULL;
}
// The VST3 test plugin: the module by itself, a bundle around it, and a
// bundle with nothing in it.
static const char *vst3_module;
static char vst3_bundle[PATH_MAX], vst3_hollow[PATH_MAX];
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

static bool load_as(const char *format, const char *module, const char *id,
                    unsigned channels) {
  fresh_host(channels);
  char *arguments[] = {(char *)format, (char *)module, (char *)id};
  return wine_backend.load(&h, arguments);
}

static bool load_at(const char *module, const char *id, unsigned channels) {
  return load_as("clap", module, id, channels);
}

static bool begin_at(const char *module, const char *id, unsigned channels) {
  return load_at(module, id, channels) && wine_backend.activate(&h);
}

// A class of the VST3 test plugin, from its bundle.
static bool begin_vst3(const char *id, unsigned channels) {
  return load_as("vst3", vst3_bundle, id, channels) && wine_backend.activate(&h);
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

// Standard error goes to a file of the test's own from here until
// `explained`, which says whether `reason` was written meanwhile and passes
// everything on. A refusal's reason is the backend's own line, or the
// Windows side's, which the backend passes on.
static int kept_stderr = -1;
static FILE *explanation;

static void expect_reason(void) {
  fflush(stderr);
  explanation = tmpfile();
  kept_stderr = dup(STDERR_FILENO);
  assert(explanation && kept_stderr >= 0);
  dup2(fileno(explanation), STDERR_FILENO);
}

static char explained_text[65536];

static bool explained(const char *reason) {
  char *text = explained_text;
  fflush(stderr);
  dup2(kept_stderr, STDERR_FILENO);
  close(kept_stderr);
  rewind(explanation);
  text[fread(text, 1, sizeof(explained_text) - 1, explanation)] = 0;
  fclose(explanation);
  fputs(text, stderr);
  return strstr(text, reason) != NULL;
}

// The loaded plugin halves its input: at every block size, with one buffer
// for both directions, and then its Windows host leaves cleanly.
static void check_halved(const char *format) {
  Wine *w = h.impl;
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
    printf("note: %s, %4u frames: round trip median %.1f us, longest %.1f us, "
           "at ordinary priority\n",
           format, sizes[s], took[CYCLES / 2] * 1e3, took[CYCLES - 1] * 1e3);
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
}

static void check_audio(void) {
  assert(begin("gain", 2));
  assert(!strcmp(h.plugin_name, "Test Gain"));
  check_halved("CLAP");
  puts("PASS: the Windows plugin's audio comes back exact at every block size, in place too, and its host leaves cleanly");
}

// A plugin that prints on standard output while it is made, activated and
// run, through its C library or through Windows, keeps its audio exact and
// its instance alive, and what it printed reaches the log. Standard output
// once was the audio wake, where the first byte a plugin printed failed the
// instance.
static void check_run_print(void) {
  static const char *const places[] = {"when made", "when activated", "in a block"};
  const char *modules[] = {plugin_msvcrt, plugin_ucrt};
  setenv("OPENXLR_TEST_WINHOST_PRINT", "1", 1);
  for (size_t m = 0; m < 2; ++m) {
    expect_reason();
    assert(begin_at(modules[m], PLUGIN "gain", 2));
    Wine *w = h.impl;
    for (unsigned i = 0; i < 40; ++i) {
      cycle(256);
      assert(scaled(256, 0.5f));
    }
    assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
    for (unsigned i = 0; i < 50; ++i) {
      wine_drain_log(w);
      sleep_ms(10);
    }
    wine_backend.deactivate(&h);
    assert(wine_stop_child(w));
    wine_drain_log(w);
    end();
    explained("");
    for (size_t i = 0; i < sizeof(places) / sizeof(places[0]); ++i) {
      char line[96];
      snprintf(line, sizeof(line), "[printed through the C library %s]", places[i]);
      assert(strstr(explained_text, line));
      snprintf(line, sizeof(line), "[printed through the Windows handle %s]", places[i]);
      assert(strstr(explained_text, line));
    }
  }
  unsetenv("OPENXLR_TEST_WINHOST_PRINT");
  puts("PASS: a plugin that prints on standard output while it is made, activated and run, "
       "built against msvcrt or the Universal C Runtime, keeps its audio exact and its "
       "instance alive, and what it prints is its log");
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

typedef bool (*Begin)(const char *plugin, unsigned channels);

static void check_latency(Begin begin, const char *latency, const char *restart) {
  assert(begin(latency, 2));
  assert(wine_latency(&h) == 480);
  assert(!report_latency(&h));  // nothing before the first cycle
  cycle(256);
  assert(report_latency(&h) && h.reported_latency == 480);
  end();

  // The plugin asks for a restart on its eighth block and reports another
  // latency once activated again. The audio is the plugin's or silence
  // throughout, and the new figure arrives without replacing the process.
  assert(begin(restart, 2));
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

static void check_refused(Begin begin, const char *refuse) {
  assert(begin(refuse, 2));
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

enum { TIMED_CYCLES = 4000 };

typedef struct {
  int priority;
  bool allowed;
  // Asked for: how long each of TIMED_CYCLES more cycles took, sorted.
  bool timed;
  double took[TIMED_CYCLES];
} RealTime;

static void *real_time_cycles(void *data) {
  RealTime *rt = data;
  struct sched_param param = {.sched_priority = rt->priority};
  rt->allowed = sched_setscheduler(0, SCHED_FIFO, &param) == 0;
  if (!rt->allowed) return NULL;
  for (unsigned i = 0; i < 20; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  if (!rt->timed) return NULL;
  for (unsigned i = 0; i < TIMED_CYCLES; ++i) {
    rt->took[i] = cycle(256);
    assert(scaled(256, 0.5f));
  }
  qsort(rt->took, TIMED_CYCLES, sizeof(rt->took[0]), compare_doubles);
  return NULL;
}

// The round trip of one block with both audio threads at real-time priority,
// which is how the product runs it.
static void note_round_trip(const RealTime *rt, const char *what) {
  printf("note: %s: round trip of 256 frames at real-time priority %d over %d cycles: "
         "median %.1f us, 99th percentile %.1f us, longest %.1f us\n",
         what, rt->priority, TIMED_CYCLES, rt->took[TIMED_CYCLES / 2] * 1e3,
         rt->took[TIMED_CYCLES * 99 / 100] * 1e3, rt->took[TIMED_CYCLES - 1] * 1e3);
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
  static RealTime rt;
  rt = (RealTime){.priority = 5};
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
  // Both ends are at that priority now: what a block costs as the product
  // runs it.
  rt.timed = true;
  assert(pthread_create(&thread, NULL, real_time_cycles, &rt) == 0);
  pthread_join(thread, NULL);
  note_round_trip(&rt, "CLAP, no editor");
  end();
  puts("PASS: the Windows audio thread is given the scheduling of the thread that waits for it");
}

// --- RTKit --------------------------------------------------------------------

// What winhost-rtkit.py starts the stand-in for RTKit with.
enum { RTKIT_HIGHEST = 7, RTKIT_RTTIME_US = 200000 };

// The last call the stand-in recorded, or false when it recorded none.
static bool rtkit_asked(const char *record, long long *pid, long long *tid, int *priority,
                        long long *rttime) {
  FILE *file = fopen(record, "r");
  if (!file) return false;
  char line[128];
  bool found = false;
  while (fgets(line, sizeof(line), file))
    found = sscanf(line, "%lld %lld %d %lld", pid, tid, priority, rttime) == 4;
  fclose(file);
  return found;
}

// Run by winhost-rtkit.py under `prlimit --rtprio=0:0`, with the system bus
// a private one: RTKIT is `granted` when a stand-in for RTKit on it grants,
// `refused` when it refuses, and `absent` when nothing owns RTKit's name.
// This process can put no thread at real-time priority, so the thread that
// waits for the Windows side is described to the backend as PipeWire's data
// thread would be under RTKit: SCHED_FIFO, at the priority the case asks.
static void check_rtkit(const char *rtkit, const char *record) {
  struct rlimit limit;
  assert(getrlimit(RLIMIT_RTPRIO, &limit) == 0);
  if (limit.rlim_max != 0) {
    puts("FAIL: the RTKit check runs with no real-time limit: prlimit --rtprio=0:0");
    exit(1);
  }
  const char *bus = getenv("DBUS_SYSTEM_BUS_ADDRESS");
  if (!bus || !bus[0] || strstr(bus, "/run/dbus/")) {
    puts("FAIL: the RTKit check runs only with a private system bus");
    exit(1);
  }
  bool granted = !strcmp(rtkit, "granted"), refused = !strcmp(rtkit, "refused");
  assert(granted || refused || !strcmp(rtkit, "absent"));
  // At 3, within the stand-in's highest, and at 9, above it.
  static const int priorities[] = {3, 9};
  for (size_t i = 0; i < (granted ? 2 : 1); ++i) {
    int waiter = priorities[i], expected = waiter < RTKIT_HIGHEST ? waiter : RTKIT_HIGHEST;
    assert(begin("gain", 2));
    Wine *w = h.impl;
    assert(w->audio_tid && w->process);
    atomic_store(&w->waiter_policy, SCHED_FIFO | SCHED_RESET_ON_FORK);
    atomic_store(&w->waiter_priority, waiter);
    atomic_store(&w->waiter_known, true);
    cycle(256);
    assert(scaled(256, 0.5f));
    expect_reason();
    wine_backend.main_thread(&h);
    char line[160];
    if (granted)
      snprintf(line, sizeof(line), "runs at real-time priority %d, granted by RTKit", expected);
    else if (refused)
      snprintf(line, sizeof(line), "runs without real-time priority: this user's real-time "
               "limit does not allow it, and RTKit refused priority %d", expected);
    else
      snprintf(line, sizeof(line), "runs without real-time priority: this user's real-time "
               "limit does not allow it, and RTKit is not on the system bus");
    assert(explained(line));
    assert(w->scheduled);
    assert(w->real_time == (granted ? WINE_RT_RTKIT : WINE_RT_NONE));
    long long pid = 0, tid = 0, rttime = 0;
    int priority = 0;
    bool asked = rtkit_asked(record, &pid, &tid, &priority, &rttime);
    if (granted || refused) {
      // Asked for the Windows host's process and its audio thread, at the
      // waiter's priority or RTKit's highest, with the process's real-time
      // CPU limit set within RTKit's bound beforehand.
      assert(asked && pid == w->process && tid == w->audio_tid && priority == expected);
      assert(rttime > 0 && rttime <= RTKIT_RTTIME_US);
      struct rlimit cpu;
      assert(prlimit(w->process, RLIMIT_RTTIME, NULL, &cpu) == 0);
      assert(cpu.rlim_max == (rlim_t)rttime && cpu.rlim_cur <= cpu.rlim_max);
    } else {
      assert(!asked);
    }
    if (granted) assert(w->real_time_priority == expected);
    // The stand-in changes nothing, so the thread is as it was, and the
    // audio goes on either way.
    assert((sched_getscheduler(w->audio_tid) & ~SCHED_RESET_ON_FORK) == SCHED_OTHER);
    for (unsigned n = 0; n < 200; ++n) {
      cycle(256);
      assert(scaled(256, 0.5f));
    }
    end();
  }
  if (granted)
    puts("PASS: with no real-time limit, RTKit is asked for the Windows audio thread at the "
         "waiter's priority or RTKit's highest, with the real-time CPU limit set first");
  else if (refused)
    puts("PASS: a refusal from RTKit leaves the Windows audio thread at ordinary priority, "
         "says why, and the audio runs");
  else
    puts("PASS: with no RTKit on the bus the Windows audio thread runs at ordinary priority, "
         "says so, and the audio runs");
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

// --- VST3 -------------------------------------------------------------------

static void check_vst3_audio(void) {
  // The class is asked for by the text a Linux host has for it, and found
  // in a module that holds it in COM order. The plugin is named by its
  // bundle, as for the native backend.
  assert(begin_vst3(VST3_GAIN, 2));
  assert(!strcmp(h.plugin_name, "Test VST3 Gain"));
  check_halved("VST3");
  // An older plugin is the module itself.
  assert(load_as("vst3", vst3_module, VST3_GAIN, 2) && wine_backend.activate(&h));
  for (unsigned i = 0; i < 100; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  puts("PASS: a Windows VST3 plugin is loaded by the class id a Linux host knows it by, "
       "from its bundle and as a single file, and its audio comes back exact at every "
       "block size, in place too");
}

static void check_vst3_layouts(void) {
  // This class reports stereo and takes mono when asked, as the native
  // backend asks before it activates.
  assert(begin_vst3(VST3_GAIN, 1));
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  // A second input bus is switched off and handed silence, which this class
  // adds in.
  assert(begin_vst3(VST3_FIXED, 2));
  for (unsigned i = 0; i < 200; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  // It is stereo and nothing else, so a mono chain cannot have it.
  expect_reason();
  assert(!begin_vst3(VST3_FIXED, 1));
  end();
  assert(explained("the plugin's main buses do not match the chain's channels"));
  puts("PASS: a VST3 plugin that takes the chain's width when asked and one with a "
       "second input bus run, and one of the wrong width is refused with its reason");
}

// Cycles a millisecond apart until the output is the input times `gain`.
// Every cycle before that has to be the input times `before`. This side's
// tick is not run meanwhile. False when the gain never comes.
static bool heard(float gain, float before) {
  for (unsigned i = 0; i < 3000; ++i) {
    cycle(256);
    if (scaled(256, gain)) return true;
    assert(scaled(256, before));
    sleep_ms(1);
  }
  return false;
}

static void check_vst3_params(void) {
  // The plugin's parameters arrive as controls under the names a native
  // VST3 plugin's have, with their plain ranges. A hidden one and the
  // plugin's own bypass do not.
  assert(load_as("vst3", vst3_bundle, VST3_PARAMS, 2));
  Wine *w = h.impl;
  Control *gain = control_named("1"), *move = control_named("2"), *level = control_named("3");
  assert(h.control_count == 3 && gain && move && level);
  assert(!gain->output && !move->output && level->output);
  assert(gain->minimum == 0 && gain->maximum == 1 && atomic_load(&gain->desired) == 0.5f);
  assert(level->maximum == 2 && atomic_load(&level->observed) == 1.0f);
  // A value from the command line is set here, before activation. It goes
  // over with the first block, and the backend hands it to the plugin on the
  // Windows side's next tick, the thread a VST3 controller is for.
  assert(set_control(&h, "1", 0.75f));
  assert(!set_control(&h, "3", 1) && !set_control(&h, "4", 1) && !set_control(&h, "5", 1));
  assert(wine_backend.activate(&h));
  assert(heard(0.75f, 0.5f));
  // The same for a value set while audio runs. A value the plugin was sent
  // is no move of its own, so nothing is published for it.
  assert(set_control(&h, "1", 0.125f));
  assert(heard(0.125f, 0.75f));
  assert(wire_value_count(atomic_load(&w->shared->values[0])) == 0);
  // The meter is the processor's own report, twice the gain, and follows on
  // the ticks of both sides.
  bool metered = false;
  for (unsigned i = 0; i < 3000 && !metered; ++i) {
    cycle(256);
    wine_backend.main_thread(&h);
    metered = atomic_load(&level->observed) == 0.25f;
    sleep_ms(1);
  }
  assert(metered);

  // The plugin's controller moves its own gain on the Windows main thread
  // and reports the edit, as its editor would. The audio follows, and the
  // value is published for this side.
  assert(set_control(&h, "2", 0.25f));
  assert(heard(0.25f, 0.125f));
  bool published = false;
  for (unsigned i = 0; i < 1000 && !published; ++i) {
    published = wire_value_count(atomic_load(&w->shared->values[0])) == 1;
    sleep_ms(1);
  }
  assert(published && wire_value_float(atomic_load(&w->shared->values[0])) == 0.25f);
  // This side has not looked yet. A value it sets now was made without
  // knowing of the plugin's, so the Windows side drops it, however many of
  // its ticks go by.
  assert(atomic_load(&gain->desired) == 0.125f);
  assert(set_control(&h, "1", 0.75f));
  for (unsigned i = 0; i < 150; ++i) {
    cycle(256);
    assert(scaled(256, 0.25f));
    sleep_ms(1);
  }
  // The tick takes the plugin's value, and nothing is sent back for it.
  wine_backend.main_thread(&h);
  assert(atomic_load(&gain->desired) == 0.25f);
  for (unsigned i = 0; i < 20; ++i) {
    cycle(256);
    assert(scaled(256, 0.25f) && w->shared->set_count == 0);
  }
  // A value set after that is taken.
  assert(set_control(&h, "1", 0.75f));
  assert(heard(0.75f, 0.25f));
  wine_backend.main_thread(&h);
  assert(!atomic_load(&h.audio_error) && !atomic_load(&w->dead));
  end();
  puts("PASS: a VST3 plugin's parameters are controls: set before the first block and "
       "between blocks, moved by the plugin's controller on the Windows main thread, "
       "and read as a meter");
}

static void check_vst3_refusals(void) {
  // The class by its bytes in the order a Windows plugin holds them. No
  // Linux host has that text for it, and neither has this one.
  expect_reason();
  assert(!begin_vst3(VST3_GAIN_IN_COM_ORDER, 2));
  end();
  assert(explained("the plugin could not be created"));
  expect_reason();
  assert(!begin_vst3("not-a-class-id", 2));
  end();
  assert(explained("the plugin id is not a class id"));
  // A bundle with no Windows module in it never starts Wine.
  expect_reason();
  assert(!load_as("vst3", vst3_hollow, VST3_GAIN, 2));
  assert(((Wine *)h.impl)->child == 0);
  end();
  assert(explained("no 64-bit Windows plugin module in"));
  // Not a Windows module at all.
  expect_reason();
  assert(!load_as("vst3", "/proc/self/exe", VST3_GAIN, 2));
  end();
  assert(explained("cannot load"));
  // A format the Windows side is never asked for.
  expect_reason();
  assert(!load_as("lv2", vst3_bundle, VST3_GAIN, 2));
  end();
  assert(explained("the Windows plugin host carries CLAP and VST3, not lv2"));
  puts("PASS: a VST3 class id in the order of a Windows plugin's own bytes, a text that "
       "is no class id, a bundle with no module, a file that is no module and a format "
       "the host does not carry are each refused with the reason");
}

// --- scanning ---------------------------------------------------------------

// What a scan wrote and how it ended.
typedef struct {
  char *out, *err;  // standard output and standard error, each ended with a zero
  size_t out_size;
  int status;       // as waitpid gives it
  double took_ms;
} Described;

// The CLAP module under a name of this system's that Wine never sees: a
// link whose name is not ASCII, to the module.
static char clap_link[PATH_MAX];
static char winhost_path[PATH_MAX];

// Everything in a file, ended with a zero.
static char *file_text(FILE *file, size_t *size) {
  struct stat status;
  fflush(file);
  assert(fstat(fileno(file), &status) == 0);
  char *text = malloc((size_t)status.st_size + 1);
  assert(text);
  ssize_t count = pread(fileno(file), text, (size_t)status.st_size, 0);
  assert(count >= 0);
  text[count] = 0;
  if (size) *size = (size_t)count;
  return text;
}

// Start the helper's own main as the daemon would start the helper, in a
// process of its own: `wine COMMAND PATH`, with what a test plugin is to do
// wrong during the scan, if anything.
static pid_t start_scan(const char *command, const char *path, const char *trouble,
                        FILE *out, FILE *err) {
  fflush(NULL);
  pid_t helper = fork();
  assert(helper >= 0);
  if (helper == 0) {
    dup2(fileno(out), STDOUT_FILENO);
    dup2(fileno(err), STDERR_FILENO);
    if (trouble) setenv("OPENXLR_TEST_WINHOST_SCAN", trouble, 1);
    char *arguments[] = {"openxlr-lv2-host", "wine", (char *)command, (char *)path, NULL};
    int result = host_program_main(4, arguments);
    fflush(NULL);
    _exit(result);
  }
  return helper;
}

static Described finish_scan(pid_t helper, FILE *out, FILE *err, double started) {
  Described d = {0};
  while (waitpid(helper, &d.status, 0) < 0 && errno == EINTR) {}
  d.took_ms = now_ms() - started;
  d.out = file_text(out, &d.out_size);
  d.err = file_text(err, NULL);
  fclose(out);
  fclose(err);
  fputs(d.err, stderr);
  return d;
}

static Described run_scan(const char *command, const char *path, const char *trouble) {
  FILE *out = tmpfile(), *err = tmpfile();
  assert(out && err);
  double started = now_ms();
  return finish_scan(start_scan(command, path, trouble, out, err), out, err, started);
}

static void forget(Described *d) {
  free(d->out);
  free(d->err);
}

static bool exited_with(const Described *d, int code) {
  return WIFEXITED(d->status) && WEXITSTATUS(d->status) == code;
}

// The Windows hosts this test has running for a scan: every process started
// as `WINHOST-EXE scan`, which is what the helper hands Wine and what the
// Windows program's own command line then reads.
static unsigned scanning_hosts(void) {
  unsigned count = 0;
  DIR *proc = opendir("/proc");
  assert(proc);
  size_t length = strlen(winhost_path);
  struct dirent *entry;
  while ((entry = readdir(proc))) {
    if (entry->d_name[0] < '0' || entry->d_name[0] > '9') continue;
    char path[280], line[PATH_MAX + 512];
    snprintf(path, sizeof(path), "/proc/%s/cmdline", entry->d_name);
    FILE *file = fopen(path, "r");
    if (!file) continue;
    size_t size = fread(line, 1, sizeof(line) - 1, file);
    fclose(file);
    line[size] = 0;
    for (size_t at = 0; at + length + 5 < size; at += strlen(line + at) + 1)
      if (!strcmp(line + at, winhost_path) && !strcmp(line + at + length + 1, "scan"))
        ++count;
  }
  closedir(proc);
  return count;
}

// The scanner's phase lines out of everything a scan wrote to standard
// error. Wine's own lines and the plugin's are not among them.
static char *phase_lines(const char *err) {
  char *text = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&text, &size);
  assert(f);
  for (const char *line = err; *line;) {
    const char *end = strchr(line, '\n');
    size_t length = end ? (size_t)(end - line) + 1 : strlen(line);
    if (!strncmp(line, "trace: scan ", 12)) fwrite(line, 1, length, f);
    line += length;
  }
  fclose(f);
  return text;
}

// Whether the last phase line a scan wrote is this one.
static bool stopped_in(const Described *d, const char *phase) {
  char *lines = phase_lines(d->err);
  size_t all = strlen(lines), length = strlen(phase);
  bool last = all > length && lines[all - 1] == '\n' &&
              !strncmp(lines + all - 1 - length, phase, length) &&
              (all - 1 == length || lines[all - 2 - length] == '\n');
  free(lines);
  return last;
}

static bool same_text(const char *what, const char *got, const char *expected) {
  if (!strcmp(got, expected)) return true;
  size_t at = 0;
  while (got[at] && got[at] == expected[at]) ++at;
  fprintf(stderr, "FAIL: %s differs at byte %zu: \"%.80s\" where \"%.80s\" was expected\n",
          what, at, got + at, expected + at);
  return false;
}

// A scan that finished: the helper exited with 0, and its standard output
// is the description and nothing else.
static bool described(const Described *d, const char *expected) {
  return exited_with(d, 0) && strlen(d->out) == d->out_size &&
         same_text("the description", d->out, expected);
}

// What the native CLAP scanner writes for a module with these plugins
// (clap_scan), byte for byte. The plugin with a timer needs a host service a
// scan does not offer and refuses to initialise, so it is left out. Only
// `gain` and `params` say they have an editor, in a window of Windows' own
// and in no other. Of the five parameters of `params`, the hidden one and
// the plugin's own bypass are left out.
static char *clap_description(const char *file) {
  static const struct {
    const char *id, *name;
    int width;
  } plugins[] = {{"gain", "Test Gain", 2},           {"mono", "Test Mono", 1},
                 {"latency", "Test Latency", 2},     {"restart", "Test Restart", 2},
                 {"callback", "Test Callback", 2},   {"sidechain", "Test Sidechain", 2},
                 {"refuse", "Test Refuse", 2},       {"slow", "Test Slow", 2},
                 {"stall", "Test Stall", 2},         {"crash", "Test Crash", 2},
                 {"params", "Test Params", 2},       {"many", "Test Many", 2},
                 {"disk", "Test Disk", 2}};
  char *text = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&text, &size);
  assert(f);
  fprintf(f, "{\"file\":\"%s\",\"plugins\":[", file);
  for (size_t i = 0; i < sizeof(plugins) / sizeof(plugins[0]); ++i) {
    fprintf(f, "%s{\"id\":\"" PLUGIN "%s\",\"name\":\"%s\",\"vendor\":\"OpenXLR\","
               "\"features\":[\"audio-effect\"],\"audioIns\":%d,\"audioOuts\":%d,"
               "\"gui\":%s,\"params\":[",
            i ? "," : "", plugins[i].id, plugins[i].name, plugins[i].width,
            plugins[i].width,
            i == 0 || !strcmp(plugins[i].id, "params") ? "true" : "false");
    if (!strcmp(plugins[i].id, "params"))
      fputs("{\"id\":1,\"name\":\"Gain\",\"module\":\"\",\"min\":0,\"max\":1,\"default\":0.5,"
            "\"readonly\":false,\"stepped\":false,\"enum\":false},"
            "{\"id\":2,\"name\":\"Move\",\"module\":\"\",\"min\":0,\"max\":1,\"default\":0,"
            "\"readonly\":false,\"stepped\":false,\"enum\":false},"
            "{\"id\":3,\"name\":\"Level\",\"module\":\"\",\"min\":0,\"max\":2,\"default\":1,"
            "\"readonly\":true,\"stepped\":false,\"enum\":false}",
            f);
    if (!strcmp(plugins[i].id, "many"))
      for (unsigned n = 1; n <= 5000; ++n)
        fprintf(f, "%s{\"id\":%u,\"name\":\"Switch %u\",\"module\":\"\",\"min\":0,\"max\":1,"
                   "\"default\":0,\"readonly\":false,\"stepped\":true,\"enum\":false}",
                n > 1 ? "," : "", n, n);
    fputs("]}", f);
  }
  fputs("]}\n", f);
  fclose(f);
  return text;
}

// The native scanner's phase lines for that module (clap_scan): four to a
// plugin, and three for the one that refuses to initialise.
static char *clap_phases(void) {
  char *text = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&text, &size);
  assert(f);
  fputs("trace: scan CLAP: module load\ntrace: scan CLAP: factory plugins\n", f);
  for (unsigned i = 0; i < 14; ++i) {
    fprintf(f, "trace: scan CLAP plugin %u: descriptor and create\n"
               "trace: scan CLAP plugin %u: initialize\n", i, i);
    if (i != 12) fprintf(f, "trace: scan CLAP plugin %u: ports, GUI support and parameters\n", i);
    fprintf(f, "trace: scan CLAP plugin %u: destroy\n", i);
  }
  fputs("trace: scan CLAP: module release\ntrace: scan CLAP: complete\n", f);
  fclose(f);
  return text;
}

// The same for the VST3 module (vst3_scan). Every class id is in the form a
// Linux host has for it, not in the order of the module's own bytes. The
// first class takes a mono chain when asked and the others are stereo only.
// Of the five parameters of the last class, the hidden one and the plugin's
// own bypass are left out, and the meter's range is its plain one. The
// VST3 scanner never asks about an editor and says every plugin has one.
static char *vst3_description(const char *file) {
  static const char *const names[] = {"Gain", "Fixed", "Latency", "Restart", "Refuse", "Params"};
  char *text = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&text, &size);
  assert(f);
  fprintf(f, "{\"file\":\"%s\",\"plugins\":[", file);
  for (unsigned i = 0; i < 6; ++i) {
    fprintf(f, "%s{\"id\":\"" CLASS "%02X\",\"name\":\"Test VST3 %s\",\"vendor\":\"OpenXLR\","
               "\"features\":[\"Fx\",\"Dynamics\"],\"audioIns\":2,\"audioOuts\":2,"
               "\"gui\":true,\"params\":[",
            i ? "," : "", i, names[i]);
    if (i == 5)
      fputs("{\"id\":1,\"name\":\"Gain\",\"module\":\"\",\"min\":0,\"max\":1,\"default\":0.5,"
            "\"readonly\":false,\"stepped\":false,\"enum\":false},"
            "{\"id\":2,\"name\":\"Move\",\"module\":\"\",\"min\":0,\"max\":1,\"default\":0,"
            "\"readonly\":false,\"stepped\":false,\"enum\":false},"
            "{\"id\":3,\"name\":\"Level\",\"module\":\"\",\"min\":0,\"max\":2,\"default\":1,"
            "\"readonly\":true,\"stepped\":false,\"enum\":false}",
            f);
    fprintf(f, "],\"widths\":[%s]}", i == 0 ? "1,2" : "2");
  }
  fputs("]}\n", f);
  fclose(f);
  return text;
}

static char *vst3_phases(void) {
  char *text = NULL;
  size_t size = 0;
  FILE *f = open_memstream(&text, &size);
  assert(f);
  fputs("trace: scan VST3: module load\ntrace: scan VST3: factory classes\n", f);
  for (unsigned i = 0; i < 6; ++i)
    fprintf(f, "trace: scan VST3 class %u: class info and component\n"
               "trace: scan VST3 class %u: controller\n"
               "trace: scan VST3 class %u: buses and parameters\n"
               "trace: scan VST3 class %u: width probes\n"
               "trace: scan VST3 class %u: release\n", i, i, i, i, i);
  fputs("trace: scan VST3: module release\ntrace: scan VST3: complete\n", f);
  fclose(f);
  return text;
}

static void check_scan_clap(void) {
  // The module is named by a link Wine is never shown, and the description
  // has to name it by that same path, byte for byte.
  Described d = run_scan("scan-clap", clap_link, NULL);
  char *expected = clap_description(clap_link), *phases = clap_phases();
  char *lines = phase_lines(d.err);
  assert(described(&d, expected));
  assert(same_text("the phase lines", lines, phases));
  printf("PASS: a Windows CLAP module is described as the native scanner describes one, "
         "byte for byte and under the path it was given, with the same phase lines, "
         "in %.0f ms\n", d.took_ms);
  free(expected);
  free(phases);
  free(lines);
  forget(&d);
}

static void check_scan_vst3(void) {
  char *phases = vst3_phases();
  const char *paths[] = {vst3_bundle, vst3_module};
  double took = 0;
  for (unsigned i = 0; i < 2; ++i) {
    Described d = run_scan("scan-vst3", paths[i], NULL);
    char *expected = vst3_description(paths[i]), *lines = phase_lines(d.err);
    assert(described(&d, expected));
    assert(same_text("the phase lines", lines, phases));
    if (i == 0) took = d.took_ms;
    free(expected);
    free(lines);
    forget(&d);
  }
  free(phases);
  printf("PASS: a Windows VST3 plugin is described as the native scanner describes one, "
         "from its bundle and as a single file, with its class ids in the form a Linux "
         "host has for them, in %.0f ms\n", took);
}

static void check_scan_refusals(void) {
  static const struct {
    const char *command, *path, *reason;
    bool started;  // whether Wine was started at all
  } cases[] = {
      {"scan-clap", "/proc/self/exe", "cannot load", true},
      {"scan-vst3", "/proc/self/exe", "cannot load", true},
      {"scan-clap", "/nonexistent/plugin.clap", "cannot find /nonexistent/plugin.clap", false},
      {"scan-vst3", vst3_hollow, "no 64-bit Windows plugin module in", false},
  };
  for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
    Described d = run_scan(cases[i].command, cases[i].path, NULL);
    assert(exited_with(&d, 1) && d.out_size == 0);
    assert(strstr(d.err, cases[i].reason));
    assert(!strstr(d.err, "the scan did not finish: the Windows plugin host exited "
                          "with code 1") == !cases[i].started);
    assert(scanning_hosts() == 0);
    forget(&d);
  }
  puts("PASS: a file that is no module, a missing file and a bundle with no module are "
       "each refused with the reason, an exit of 1 and no description");
}

static void check_scan_crash(void) {
  static const char *const phases[] = {
      "trace: scan CLAP plugin 0: ports, GUI support and parameters",
      "trace: scan VST3 class 0: buses and parameters"};
  const char *paths[] = {clap_link, vst3_bundle};
  for (unsigned i = 0; i < 2; ++i) {
    Described d = run_scan(i ? "scan-vst3" : "scan-clap", paths[i], "crash");
    assert(exited_with(&d, 1));
    assert(strstr(d.err, "the Windows plugin crashed: exception 0xc0000005"));
    assert(strstr(d.err, "the scan did not finish: the Windows plugin host exited with code 3"));
    assert(stopped_in(&d, phases[i]));
    assert(scanning_hosts() == 0);
    forget(&d);
  }
  puts("PASS: a plugin that crashes while it is described ends the scan with the "
       "exception, the phase it was in and an exit of 1, for CLAP and for VST3");
}

static void check_scan_print(void) {
  const char *paths[] = {clap_link, vst3_bundle, plugin_msvcrt, plugin_ucrt};
  for (unsigned i = 0; i < 4; ++i) {
    bool vst3 = i == 1;
    Described d = run_scan(vst3 ? "scan-vst3" : "scan-clap", paths[i], "print");
    char *expected = vst3 ? vst3_description(paths[i]) : clap_description(paths[i]);
    assert(described(&d, expected));
    assert(strstr(d.err, "[printed through the C library]"));
    assert(strstr(d.err, "[printed through the Windows handle]"));
    free(expected);
    forget(&d);
  }
  puts("PASS: what a plugin prints on standard output while it is described, through "
       "the C library or through Windows, is passed on as its log and the description "
       "is exact, for CLAP and VST3 and for a module built against msvcrt or the "
       "Universal C Runtime");
}

static void check_scan_hang(void) {
  // Never answered, the helper gives the scan up at its limit and ends the
  // Windows host itself.
  Described d = run_scan("scan-clap", clap_link, "hang");
  assert(exited_with(&d, 1));
  assert(strstr(d.err, "the Windows plugin did not finish its scan in"));
  assert(stopped_in(&d, "trace: scan CLAP plugin 0: ports, GUI support and parameters"));
  assert(d.took_ms >= WINE_SCAN_MS && d.took_ms < WINE_SCAN_MS + WINE_QUIT_MS + 1000);
  assert(scanning_hosts() == 0);
  double gave_up = d.took_ms;
  forget(&d);

  // The daemon's own deadline kills the helper outright. The Windows host
  // is then stuck inside the plugin with nobody to end it, and has to go
  // by itself.
  FILE *out = tmpfile(), *err = tmpfile();
  assert(out && err);
  double started = now_ms();
  pid_t helper = start_scan("scan-vst3", vst3_bundle, "hang", out, err);
  bool stuck = false;
  for (unsigned i = 0; i < 3000 && !stuck; ++i) {
    char *so_far = file_text(err, NULL);
    stuck = strstr(so_far, "trace: scan VST3 class 0: buses and parameters") != NULL;
    free(so_far);
    sleep_ms(2);
  }
  assert(stuck && scanning_hosts() == 1);
  kill(helper, SIGKILL);
  double killed = now_ms();
  d = finish_scan(helper, out, err, started);
  assert(WIFSIGNALED(d.status));
  while (scanning_hosts() != 0 && now_ms() - killed < 2000) sleep_ms(1);
  assert(scanning_hosts() == 0);
  printf("PASS: a plugin that never answers while it is described is given up after "
         "%.1f s with the phase it was in, and a helper that is killed during a scan "
         "takes its Windows host with it in %.0f ms\n",
         gave_up / 1e3, now_ms() - killed);
  forget(&d);
}

// --- a path that is not ASCII, in the C locale ------------------------------

// Copies of both modules under a directory whose name is not ASCII. A link
// would not do: the backend resolves links before Wine is shown a path, so
// only a file that really is there puts such a name in front of Wine.
static char umlaut_directory[PATH_MAX], umlaut_clap[PATH_MAX], umlaut_vst3[PATH_MAX];

static void copy_file(const char *from, const char *to) {
  FILE *in = fopen(from, "rb"), *out = fopen(to, "wb");
  assert(in && out);
  char block[65536];
  size_t count;
  while ((count = fread(block, 1, sizeof(block), in)) > 0)
    assert(fwrite(block, 1, count, out) == count);
  assert(!ferror(in) && fclose(out) == 0);
  fclose(in);
}

// The locale variables as this process was given them, set aside so the
// test can stand in the C locale and hand them back afterwards.
static const char *const locale_variables[] = {"LC_ALL", "LC_CTYPE", "LANG"};
static char *kept_locale[3];

static void enter_c_locale(void) {
  for (unsigned i = 0; i < 3; ++i) {
    const char *value = getenv(locale_variables[i]);
    kept_locale[i] = value ? strdup(value) : NULL;
    unsetenv(locale_variables[i]);
  }
  setenv("LC_ALL", "C", 1);
}

static void leave_c_locale(void) {
  for (unsigned i = 0; i < 3; ++i) {
    if (kept_locale[i])
      setenv(locale_variables[i], kept_locale[i], 1);
    else
      unsetenv(locale_variables[i]);
    free(kept_locale[i]);
    kept_locale[i] = NULL;
  }
}

// What the Windows host is given for a locale, by the variables alone.
static void check_locale_choice(void) {
  char entry[128];
  enter_c_locale();
  // The daemon's own case: everything forced to C.
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_ALL=") &&
         !strcmp(entry, "LC_ALL=C.UTF-8"));
  // No variable at all is the C locale too, and then only the character
  // set is given.
  unsetenv("LC_ALL");
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_CTYPE=") &&
         !strcmp(entry, "LC_CTYPE=C.UTF-8"));
  setenv("LANG", "C", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_CTYPE=") &&
         !strcmp(entry, "LC_CTYPE=C.UTF-8"));
  // A locale that reads UTF-8 is left as it is, wherever it is set.
  setenv("LANG", "C.UTF-8", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == 0);
  setenv("LANG", "C", 1);
  setenv("LC_CTYPE", "C.UTF-8", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == 0);
  // LC_ALL overrides both, so it is the one that changes.
  setenv("LC_ALL", "POSIX", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_ALL=") &&
         !strcmp(entry, "LC_ALL=C.UTF-8"));
  // Another character set keeps its language where this system has that
  // locale with UTF-8, and a name the system does not know reads as C.
  setenv("LC_ALL", "en_US.ISO-8859-1", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_ALL=") &&
         (!strcmp(entry, "LC_ALL=en_US.UTF-8") || !strcmp(entry, "LC_ALL=C.UTF-8")));
  setenv("LC_ALL", "xx_XX", 1);
  assert(wine_utf8_locale(entry, sizeof(entry)) == strlen("LC_ALL=") &&
         !strcmp(entry, "LC_ALL=C.UTF-8"));
  leave_c_locale();
}

// Both modules loaded from the directory, in the C locale, with their audio.
static void check_locale_loads(void) {
  enter_c_locale();
  assert(begin_at(umlaut_clap, PLUGIN "gain", 2));
  for (unsigned i = 0; i < 20; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  assert(load_as("vst3", umlaut_vst3, VST3_GAIN, 2) && wine_backend.activate(&h));
  for (unsigned i = 0; i < 20; ++i) {
    cycle(256);
    assert(scaled(256, 0.5f));
  }
  end();
  leave_c_locale();
}

// And described from there, under the path as it was given.
static void check_locale_scans(void) {
  enter_c_locale();
  Described clap = run_scan("scan-clap", umlaut_clap, NULL);
  char *expected = clap_description(umlaut_clap);
  assert(described(&clap, expected));
  free(expected);
  forget(&clap);
  Described vst3 = run_scan("scan-vst3", umlaut_vst3, NULL);
  expected = vst3_description(umlaut_vst3);
  assert(described(&vst3, expected));
  free(expected);
  forget(&vst3);
  leave_c_locale();
  puts("PASS: in the C locale, a CLAP and a VST3 module under a directory whose name is "
       "not ASCII are loaded with their audio exact and described under that path");
}

// --- the disk identity ------------------------------------------------------

static void join(char *out, size_t size, const char *directory, const char *name);

// Made-up disks under the runtime directory (tests/disk-fixture.h), the
// directory that holds the test's prefix, and the file the `disk` plugin
// writes what it was told to.
static char disk_tree[PATH_MAX], disk_holder[PATH_MAX], disk_probe[PATH_MAX];

#define DISK_MODEL "OpenXLR Test NVMe"
#define DISK_REVISION "1.2.3"
#define DISK_SERIAL "0123_4567_89AB_CDEF_FEDC_BA98_7654_3210."

// The prefix on the made-up NVMe disk, which has an NGUID, or on the SATA
// disk, where there is no identity to give.
static void disk_mounted_on(bool nvme) {
  fixture_mounts(disk_tree, disk_holder, nvme ? "/dev/nvme0n1p2" : "/dev/sda1", NULL, NULL);
  disk_identity_test_root = disk_tree;
}

static bool take_probe(ProbeRecord records[PROBE_RECORDS]) {
  FILE *file = fopen(disk_probe, "rb");
  size_t count = file ? fread(records, sizeof(ProbeRecord), PROBE_RECORDS, file) : 0;
  if (file) fclose(file);
  unlink(disk_probe);
  return count == PROBE_RECORDS;
}

// Load the `disk` plugin, which asks its questions as it is made, run blocks
// that each ask again and must each be told the same, and take what it was
// told.
static bool probe_by_load(ProbeRecord records[PROBE_RECORDS]) {
  setenv("OPENXLR_TEST_WINHOST_DISK", disk_probe, 1);
  bool begun = begin("disk", 2), steady = begun;
  unsetenv("OPENXLR_TEST_WINHOST_DISK");
  for (unsigned i = 0; begun && i < 64; ++i) {
    cycle(256);
    steady = steady && scaled(256, 0.5f);
  }
  end();
  return take_probe(records) && steady;
}

static bool told(const ProbeRecord *r, const unsigned char *descriptor, uint32_t size) {
  return r->ok && r->returned == size && !memcmp(r->bytes, descriptor, size);
}

// The descriptor on every question it answers: the first, the same again, one
// into a buffer only a header long, and one on a handle opened again through
// CreateFileW under another spelling of the name.
static bool told_everywhere(const ProbeRecord *got, const unsigned char *descriptor) {
  return told(&got[PROBE_FIRST], descriptor, DISK_IDENTITY_SIZE) &&
         told(&got[PROBE_REPEAT], descriptor, DISK_IDENTITY_SIZE) &&
         told(&got[PROBE_HEADER], descriptor, 8) &&
         told(&got[PROBE_REOPENED], descriptor, DISK_IDENTITY_SIZE);
}

// Wine's own answer for PhysicalDrive0, which is what the hook replaces: a
// bare 40-byte descriptor that names no vendor, product or serial.
static bool wine_placeholder(const ProbeRecord *r) {
  static const unsigned char none[16] = {0};
  return r->ok && r->returned == 40 && r->bytes[4] == 40 && !r->bytes[5] &&
         !memcmp(r->bytes + 12, none, 16);
}

// What the hook leaves to Wine has to come back as Wine gave it without the
// hook: another property, the question whether one exists, another device, a
// handle once it is closed, and a plain file opened after that.
static bool left_alone(const ProbeRecord *got, const ProbeRecord *wine) {
  static const int untouched[] = {PROBE_ADAPTER, PROBE_EXISTS, PROBE_VOLUME, PROBE_CLOSED,
                                  PROBE_REUSED};
  for (size_t i = 0; i < sizeof(untouched) / sizeof(untouched[0]); ++i) {
    const ProbeRecord *a = &got[untouched[i]], *b = &wine[untouched[i]];
    if (a->ok != b->ok || a->returned != b->returned || a->error != b->error ||
        a->returned > PROBE_BUFFER || memcmp(a->bytes, b->bytes, a->returned)) {
      printf("FAIL: question %d of the disk probe was not left to Wine\n", untouched[i]);
      return false;
    }
  }
  return true;
}

typedef struct {
  unsigned count;
  double *times;
  bool real_time;
} Timed;

static void *timed_cycles(void *data) {
  Timed *t = data;
  struct sched_param param = {.sched_priority = 10};
  t->real_time = sched_setscheduler(0, SCHED_FIFO, &param) == 0;
  for (unsigned i = 0; i < t->count; ++i) {
    t->times[i] = cycle(256);
    assert(scaled(256, 0.5f));
  }
  return NULL;
}

// The round trip of a 256-frame block through a plugin, by its median and its
// 99th percentile, from a thread at real-time priority 10 when this user may
// have one. The Windows audio thread is given the same, as in the product.
static double round_trip(const char *kind, unsigned count, double *p99, bool *real_time) {
  static double times[4000];
  assert(count <= sizeof(times) / sizeof(times[0]));
  assert(begin(kind, 2));
  pthread_t thread;
  Timed warm = {32, times, false};
  assert(pthread_create(&thread, NULL, timed_cycles, &warm) == 0);
  pthread_join(thread, NULL);
  wine_backend.main_thread(&h);
  Timed timed = {count, times, false};
  assert(pthread_create(&thread, NULL, timed_cycles, &timed) == 0);
  pthread_join(thread, NULL);
  end();
  qsort(times, count, sizeof(double), compare_doubles);
  *p99 = times[count * 99 / 100];
  *real_time = timed.real_time;
  return times[count / 2];
}

// The system calls the Windows audio thread makes over `count` blocks of the
// plugin `kind`, as strace sees them on that thread alone, beside the read
// that wakes it and the write that answers. -1 when strace is missing or may
// not watch the thread.
static int other_system_calls(const char *kind, unsigned count) {
  char strace[PATH_MAX], log[PATH_MAX + 16], status_path[64], line[512];
  if (!wine_find_program("strace", strace, sizeof(strace)))
    return -1;
  assert(begin(kind, 2));
  Wine *w = h.impl;
  for (unsigned i = 0; i < 8; ++i)
    cycle(256);
  if (!w->audio_tid) {
    end();
    return -1;
  }
  snprintf(log, sizeof(log), "%s.strace", disk_probe);
  char tid[16];
  snprintf(tid, sizeof(tid), "%d", (int)w->audio_tid);
  fflush(NULL);
  pid_t tracer = fork();
  assert(tracer >= 0);
  if (tracer == 0) {
    execl(strace, "strace", "-qq", "-o", log, "-p", tid, (char *)NULL);
    _exit(127);
  }
  // Until strace has the thread, or gives up.
  snprintf(status_path, sizeof(status_path), "/proc/%s/status", tid);
  bool watched = false;
  for (unsigned attempt = 0; attempt < 500 && !watched; ++attempt) {
    FILE *status = fopen(status_path, "r");
    while (status && fgets(line, sizeof(line), status))
      if (!strncmp(line, "TracerPid:", 10))
        watched = atoi(line + 10) == (int)tracer;
    if (status) fclose(status);
    if (!watched) sleep_ms(10);
    int ended = 0;
    if (!watched && waitpid(tracer, &ended, WNOHANG) == tracer) {
      tracer = 0;
      break;
    }
  }
  // strace stops the thread at every system call, so on a slow machine a
  // block can come back after the cycle has given up on it. The cycles that
  // follow are silence, as for any late block, and return at once, so they
  // wait a millisecond for the answer before the next. Anything that is
  // neither the plugin's audio nor silence is still wrong.
  unsigned exact = 0, late = 0;
  double started = now_ms();
  while (watched && exact < count && now_ms() - started < 10000) {
    cycle(256);
    if (scaled(256, 0.5f))
      ++exact;
    else {
      assert(silent(256));
      ++late;
      sleep_ms(1);
    }
  }
  assert(!watched || exact == count);
  if (late)
    printf("note: %u cycles were silence for a block that came late while strace watched\n",
           late);
  if (tracer > 0) {
    kill(tracer, SIGINT);
    while (waitpid(tracer, NULL, 0) < 0 && errno == EINTR) {}
  }
  end();
  // The wake is a one-byte read on the descriptor Wine gave standard input,
  // and the answer a one-byte write on the wake FIFO's. A read strace caught
  // half way when it attached ends as a resumed one, and the read it leaves
  // the thread waiting in as a detached one. On a slow machine strace can
  // leave while the answer is still being written, which is a detached write.
  int others = watched ? 0 : -1;
  FILE *calls = fopen(log, "r");
  while (watched && calls && fgets(line, sizeof(line), calls))
    if (!(!strncmp(line, "read(", 5) && strstr(line, ", \"P\", 1)")) &&
        !(!strncmp(line, "write(", 6) && strstr(line, ", \"D\", 1)")) &&
        !(!strncmp(line, "read(", 5) && strstr(line, "<detached ...>")) &&
        !(!strncmp(line, "write(", 6) && strstr(line, ", \"D\", 1 <detached ...>")) &&
        strncmp(line, "<... read resumed>", 18)) {
      if (others < 4) printf("note: the Windows audio thread called %s", line);
      ++others;
    }
  if (calls) fclose(calls);
  unlink(log);
  return others;
}

static void check_disk_identity(const char *runtime) {
  static ProbeRecord wine[PROBE_RECORDS], got[PROBE_RECORDS];
  unsigned char fixture[DISK_IDENTITY_SIZE], own[DISK_IDENTITY_SIZE];
  fixture_expected(fixture, DISK_MODEL, DISK_REVISION, DISK_SERIAL);
  // Wine has made the prefix by now, so it has a real path for the mount
  // table to name.
  assert(realpath(getenv("WINEPREFIX"), disk_holder));
  join(disk_tree, sizeof(disk_tree), runtime, "disks");
  join(disk_probe, sizeof(disk_probe), runtime, "disk-probe");
  fixture_nvme(disk_tree, 0, "01234567-89ab-cdef-fedc-ba9876543210\n", "00 11 22 33 44 55 66 77\n",
               DISK_MODEL "\n", DISK_REVISION "\n", "NOT-THE-NAME\n");
  fixture_sata(disk_tree);

  // No identity to give: the plugin hears Wine, which is what the rest is
  // measured against.
  disk_mounted_on(false);
  assert(probe_by_load(wine));
  assert(wine_placeholder(&wine[PROBE_FIRST]) && wine_placeholder(&wine[PROBE_REOPENED]));
  puts("PASS: without an identity to give, the plugin hears Wine's empty descriptor");

  disk_mounted_on(true);
  assert(probe_by_load(got));
  assert(told_everywhere(got, fixture) && left_alone(got, wine));
  printf("PASS: a plugin that asks for PhysicalDrive0's descriptor is told the Linux side's "
         "396 bytes, and every other question is left to Wine%s\n",
         got[PROBE_REUSED].same ? " (the closed handle's value came back for a plain file)" : "");

  // This machine's own disk, when its prefix is on an NVMe drive. Only the
  // comparison is printed, never the bytes.
  disk_identity_test_root = NULL;
  if (disk_identity_build(own)) {
    assert(probe_by_load(got));
    assert(told_everywhere(got, own) && left_alone(got, wine));
    puts("PASS: on this machine's own NVMe disk the plugin is told the bytes the Linux side built");
  } else
    puts("SKIP: this machine's prefix is not on an NVMe disk the rules can read, so only the "
         "made-up one was checked");

  // Once per block, on the audio thread: no slower than a plugin that does
  // not ask, and no system call beyond the block's own two.
  disk_mounted_on(true);
  double gain_p99, disk_p99, wine_p99;
  bool real_time;
  double gain = round_trip("gain", 4000, &gain_p99, &real_time);
  double disk = round_trip("disk", 4000, &disk_p99, &real_time);
  disk_mounted_on(false);
  double asked = round_trip("disk", 4000, &wine_p99, &real_time);
  printf("note: a 256-frame round trip, median and 99th percentile%s: %.3f and %.3f ms "
         "without a query, %.3f and %.3f ms with one answered by the hook, %.3f and %.3f ms "
         "with one answered by Wine\n",
         real_time ? " at real-time priority 10" : " at ordinary priority", gain, gain_p99,
         disk, disk_p99, asked, wine_p99);
  assert(disk < gain + 0.02);
  printf("PASS: a query in every block costs the round trip %+.1f us at the median\n",
         (disk - gain) * 1e3);
  disk_mounted_on(true);
  int hooked = other_system_calls("disk", 200), plain = other_system_calls("gain", 200);
  disk_mounted_on(false);
  int unhooked = other_system_calls("disk", 200);
  if (hooked < 0 || plain < 0)
    puts("SKIP: strace is missing or may not watch the Windows audio thread, so its system "
         "calls were not counted");
  else {
    printf("note: over 200 blocks the Windows audio thread made %d system calls beside its "
           "read and write with the hook answering, %d with no query, %d with Wine answering\n",
           hooked, plain, unhooked);
    assert(hooked == 0 && plain == 0);
    puts("PASS: the hook's repeat answers make no system call on the audio thread");
  }
  disk_identity_test_root = NULL;
}

// A scan loads the plugin as a run does, and gets the identity the same way.
static void check_disk_identity_scan(void) {
  static ProbeRecord got[PROBE_RECORDS];
  unsigned char fixture[DISK_IDENTITY_SIZE];
  fixture_expected(fixture, DISK_MODEL, DISK_REVISION, DISK_SERIAL);
  for (int nvme = 1; nvme >= 0; --nvme) {
    disk_mounted_on(nvme);
    setenv("OPENXLR_TEST_WINHOST_DISK", disk_probe, 1);
    Described d = run_scan("scan-clap", plugin_path, NULL);
    unsetenv("OPENXLR_TEST_WINHOST_DISK");
    assert(exited_with(&d, 0) && take_probe(got));
    assert(nvme ? told_everywhere(got, fixture) : wine_placeholder(&got[PROBE_FIRST]));
    forget(&d);
  }
  disk_identity_test_root = NULL;
  assert(scanning_hosts() == 0);
  puts("PASS: a plugin is told the disk identity during a scan too, and Wine's answer when "
       "there is none");
}

// Wine opens a file it is asked for once more than once on some starts, and
// closes it in between. A stand-in for Wine that reads the identity, waits
// and reads it again finds it both times, and the directory it was in is
// gone once the scan is over.
static void check_disk_identity_scan_kept(void) {
  static char script[PATH_MAX], seen[PATH_MAX], directory[PATH_MAX];
  const char *runtime = getenv("XDG_RUNTIME_DIR");
  join(script, sizeof(script), runtime, "wine-reads-twice");
  join(seen, sizeof(seen), runtime, "wine-reads-twice.seen");
  // WINHOST scan DIRECTORY, as the helper starts Wine for a scan.
  FILE *file = fopen(script, "w");
  assert(file);
  fputs("#!/bin/sh\n"
        "first=$(wc -c < \"$3/disk-identity\")\n"
        "sleep 0.5\n"
        "second=$(wc -c < \"$3/disk-identity\")\n"
        "{ echo \"$first $second\"; echo \"$3\"; } > \"$OPENXLR_TEST_WINHOST_SEEN\"\n"
        "exit 3\n",
        file);
  assert(fclose(file) == 0 && chmod(script, 0700) == 0);
  const char *named = getenv("OPENXLR_WINE");
  char *wine = named ? strdup(named) : NULL;
  setenv("OPENXLR_WINE", script, 1);
  setenv("OPENXLR_TEST_WINHOST_SEEN", seen, 1);
  disk_mounted_on(true);
  Described d = run_scan("scan-clap", plugin_path, NULL);
  disk_identity_test_root = NULL;
  unsetenv("OPENXLR_TEST_WINHOST_SEEN");
  if (wine)
    setenv("OPENXLR_WINE", wine, 1);
  else
    unsetenv("OPENXLR_WINE");
  free(wine);
  // The stand-in described no plugin and said so, which fails the scan.
  assert(exited_with(&d, 1));
  forget(&d);
  unsigned first = 0, second = 0;
  file = fopen(seen, "r");
  assert(file && fscanf(file, "%u %u\n", &first, &second) == 2 &&
         fgets(directory, sizeof(directory), file));
  fclose(file);
  directory[strcspn(directory, "\n")] = 0;
  assert(first == DISK_IDENTITY_SIZE && second == DISK_IDENTITY_SIZE);
  assert(directory[0] == '/' && access(directory, F_OK) != 0 && errno == ENOENT);
  unlink(script);
  unlink(seen);
  puts("PASS: the scan's disk identity is still there for a Wine that opens the file twice, "
       "and gone when the scan ends");
}

static void make_directories(char *path) {
  for (char *slash = strchr(path + 1, '/'); slash; slash = strchr(slash + 1, '/')) {
    *slash = 0;
    mkdir(path, 0700);
    *slash = '/';
  }
}

// `name` in `directory`, which has to fit.
static void join(char *out, size_t size, const char *directory, const char *name) {
  int length = snprintf(out, size, "%s/%s", directory, name);
  assert(length > 0 && (size_t)length < size);
}

int main(int argc, char **argv) {
  static char winhost[PATH_MAX], plugin[PATH_MAX], vst3[PATH_MAX], wine[PATH_MAX],
      prefix[PATH_MAX];
  // winhost-rtkit.py runs the RTKit check alone, one case at a time.
  const char *rtkit = NULL, *rtkit_record = NULL;
  if (argc == 7 && !strcmp(argv[1], "--rtkit")) {
    rtkit = argv[2];
    rtkit_record = argv[3];
    argc -= 3;
    argv += 3;
  }
  if (argc != 4 || !realpath(argv[1], winhost) || !realpath(argv[2], plugin) ||
      !realpath(argv[3], vst3)) {
    fputs("usage: winhost [--rtkit granted|refused|absent RECORD] WINHOST-EXE CLAP-PLUGIN "
          "VST3-PLUGIN\n",
          stderr);
    return 2;
  }
  // The backend asks RTKit when the real-time limit refuses it. No test may
  // reach the system's RTKit, so the system bus is one that is not there,
  // unless the RTKit check gave a private one.
  if (!rtkit)
    setenv("DBUS_SYSTEM_BUS_ADDRESS", "unix:path=/nonexistent/openxlr-test-system-bus", 1);
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
  snprintf(winhost_path, sizeof(winhost_path), "%s", winhost);
  plugin_path = plugin;
  // tests/winhost-plugin.clap beside tests/winhost-plugin-msvcrt.clap and
  // tests/winhost-plugin-ucrt.clap, which the Makefile builds with it.
  size_t stem = strlen(plugin) - strlen(".clap");
  assert(strlen(plugin) > strlen(".clap") && !strcmp(plugin + stem, ".clap"));
  snprintf(plugin_msvcrt, sizeof(plugin_msvcrt), "%.*s-msvcrt.clap", (int)stem, plugin);
  snprintf(plugin_ucrt, sizeof(plugin_ucrt), "%.*s-ucrt.clap", (int)stem, plugin);
  if (!imports(plugin_msvcrt, "msvcrt.dll") || imports(plugin_msvcrt, "api-ms-win-crt-") ||
      !imports(plugin_ucrt, "api-ms-win-crt-") || imports(plugin_ucrt, "msvcrt.dll")) {
    printf("FAIL: %s must use msvcrt and %s the Universal C Runtime\n", plugin_msvcrt,
           plugin_ucrt);
    return 1;
  }
  // A runtime directory of the test's own, so what the backend leaves in it
  // can be told exactly: nothing.
  static char runtime[PATH_MAX];
  const char *base = getenv("XDG_RUNTIME_DIR");
  snprintf(runtime, sizeof(runtime), "%s/openxlr-test-winhost.XXXXXX",
           base && base[0] == '/' ? base : "/tmp");
  assert(mkdtemp(runtime));
  setenv("XDG_RUNTIME_DIR", runtime, 1);
  if (rtkit) {
    pw_init(NULL, NULL);
    check_rtkit(rtkit, rtkit_record);
    pw_deinit();
    if (rmdir(runtime) != 0) {
      perror("FAIL: the backend left something in its runtime directory");
      return 1;
    }
    return 0;
  }
  // The VST3 module as it is installed: inside a bundle, under the name a
  // Windows bundle gives it. And a bundle with nothing in it.
  static char contents[PATH_MAX], inside[PATH_MAX], bundled[PATH_MAX];
  vst3_module = vst3;
  join(vst3_bundle, sizeof(vst3_bundle), runtime, "Test Bundle.vst3");
  join(vst3_hollow, sizeof(vst3_hollow), runtime, "Hollow.vst3");
  join(contents, sizeof(contents), vst3_bundle, "Contents");
  join(inside, sizeof(inside), contents, "x86_64-win");
  join(bundled, sizeof(bundled), inside, "Test Bundle.vst3");
  assert(mkdir(vst3_bundle, 0700) == 0 && mkdir(contents, 0700) == 0 &&
         mkdir(inside, 0700) == 0 && symlink(vst3, bundled) == 0 &&
         mkdir(vst3_hollow, 0700) == 0);
  join(clap_link, sizeof(clap_link), runtime, "Pr\xc3\xbc" "fling.clap");
  assert(symlink(plugin, clap_link) == 0);
  join(umlaut_directory, sizeof(umlaut_directory), runtime, "Pr\xc3\xbc" "fung");
  join(umlaut_clap, sizeof(umlaut_clap), umlaut_directory, "Gr\xc3\xb6\xc3\x9f" "e.clap");
  join(umlaut_vst3, sizeof(umlaut_vst3), umlaut_directory, "Gr\xc3\xb6\xc3\x9f" "e.vst3");
  assert(mkdir(umlaut_directory, 0700) == 0);
  copy_file(plugin, umlaut_clap);
  copy_file(vst3, umlaut_vst3);

  pw_init(NULL, NULL);
  check_audio();
  check_run_print();
  check_layouts();
  check_latency(begin, "latency", "restart");
  puts("PASS: the plugin's latency is reported, and a restart it asks for changes it in place");
  check_callback();
  check_timer();
  check_params();
  check_many();
  check_listing();
  check_refused(begin, "refuse");
  puts("PASS: blocks the plugin refuses are silence, and half a second of them fails the instance");
  check_late();
  check_killed();
  check_crashed();
  check_stalled();
  check_scheduling();
  check_refusals(winhost);
  check_vst3_audio();
  check_vst3_layouts();
  check_latency(begin_vst3, VST3_LATENCY, VST3_RESTART);
  puts("PASS: a VST3 plugin's latency is reported, and a reload it asks for changes it in place");
  check_vst3_params();
  check_refused(begin_vst3, VST3_REFUSE);
  puts("PASS: blocks a VST3 plugin refuses are silence, and half a second of them fails the instance");
  check_vst3_refusals();
  check_disk_identity(runtime);
  check_locale_choice();
  check_locale_loads();
  pw_deinit();
  assert(scanning_hosts() == 0);
  check_scan_clap();
  check_scan_vst3();
  check_scan_refusals();
  check_scan_crash();
  check_scan_print();
  check_scan_hang();
  check_locale_scans();
  check_disk_identity_scan();
  check_disk_identity_scan_kept();
  // What the test itself put in the runtime directory.
  char command[PATH_MAX + 32];
  assert(strstr(disk_tree, "/openxlr-test-winhost."));
  snprintf(command, sizeof(command), "rm -rf -- '%s'", disk_tree);
  assert(system(command) == 0);
  unlink(umlaut_clap);
  unlink(umlaut_vst3);
  rmdir(umlaut_directory);
  unlink(clap_link);
  unlink(bundled);
  rmdir(inside);
  rmdir(contents);
  rmdir(vst3_bundle);
  rmdir(vst3_hollow);
  if (rmdir(runtime) != 0) {
    perror("FAIL: the backend left something in its runtime directory");
    return 1;
  }
  puts("PASS: the backend leaves nothing behind in the runtime directory");
  return 0;
}
