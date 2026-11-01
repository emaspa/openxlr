// SPDX-License-Identifier: GPL-3.0-only
// The measured delay, without a graph: the audio callback is called here on
// plain buffers with a speech-like signal, irregular syllables of a formant
// voice over a little noise, and the main-thread tick runs as it would every
// 33 ms. A plugin that delays by a known amount has to be measured to the
// sample at 0, 480, 2400 and 24000 frames, in mono and in stereo and at
// 44.1 kHz; a noise remover that gates the pauses and filters the speech has
// to be measured too; on its own block the figure has to be the plugin's
// delay plus the block's, which is what the host reports; and a plugin that
// puts out silence, unrelated noise or noise shaped like the speech must
// never give a figure. The audio thread's part must not allocate.
#include "../host.h"
#include <stdio.h>
static float *test_buffers[MAX_CHANNELS * 2];
static void *test_dsp_buffer(void *port, uint32_t frames);
#define pw_filter_get_dsp_buffer test_dsp_buffer
#define main host_program_main
#include "../host.c"
#undef main
#undef pw_filter_get_dsp_buffer
#include <assert.h>

static void *test_dsp_buffer(void *port, uint32_t frames) {
  for (unsigned i = 0; i < MAX_CHANNELS * 2; i++)
    if (port == &test_buffers[i]) return test_buffers[i];
  return NULL;
}

// Every allocation made on the audio thread is counted; it may make none.
extern void *__libc_malloc(size_t size);
extern void *__libc_calloc(size_t count, size_t size);
extern void *__libc_realloc(void *at, size_t size);
static _Atomic bool counting;
static _Atomic unsigned allocations;
void *malloc(size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_malloc(size);
}
void *calloc(size_t count, size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_calloc(count, size);
}
void *realloc(void *at, size_t size) {
  if (atomic_load(&counting)) atomic_fetch_add(&allocations, 1);
  return __libc_realloc(at, size);
}

static const double PI = 3.14159265358979323846;

static uint64_t random_state;
static double uniform(void) {
  random_state ^= random_state << 13, random_state ^= random_state >> 7,
      random_state ^= random_state << 17;
  return ((random_state >> 11) + 0.5) / 9007199254740992.0;
}
static double gaussian(void) {
  return sqrt(-2 * log(uniform())) * cos(2 * PI * uniform());
}

// --- the signal ------------------------------------------------------------------

// Syllables of 120 to 380 ms with 40 to 450 ms between them and a longer
// pause now and then, each with its own pitch, vowels and level, so no two
// stretches of the envelope look alike. A glottal pulse train through three
// formant resonators, as in tests/denoise.c, with steady noise under it.
static void make_speech(float *x, size_t count, uint32_t rate, double noise_db) {
  static const double vowels[5][3] = {
      {730, 1090, 2440}, {270, 2290, 3010}, {530, 1840, 2480}, {570, 840, 2410}, {300, 870, 2240}};
  static const double widths[3] = {90, 110, 170};
  double state[3][2] = {{0}}, open = 0, period = 0.01, glottal = 0;
  size_t start = 0, length = 0, next = (size_t)(0.1 * rate);
  double f0 = 140, slope = 0, level = 0.5;
  const double *from = vowels[0], *to = vowels[1];
  unsigned syllables = 0;
  double noise_level = pow(10, noise_db / 20);
  for (size_t n = 0; n < count; n++) {
    if (n == next) {
      start = n;
      length = (size_t)((0.12 + 0.26 * uniform()) * rate);
      double gap = 0.04 + 0.41 * uniform();
      if (++syllables % 7 == 0) gap += 0.6 + 0.8 * uniform();
      next = start + length + (size_t)(gap * rate);
      f0 = 95 + 135 * uniform();
      slope = (uniform() - 0.6) * 60;
      level = 0.3 + 0.7 * uniform();
      from = vowels[(unsigned)(uniform() * 5) % 5];
      to = vowels[(unsigned)(uniform() * 5) % 5];
    }
    double at = n >= start && n < start + length ? (double)(n - start) / length : -1;
    double glide = at < 0 ? 0 : at;
    double pitch = f0 + slope * glide;
    if ((open += 1.0 / rate) >= period) open = 0, period = (1 + 0.02 * (uniform() - 0.5)) / pitch;
    double phase = open / period;
    double pulse = phase < 0.6 ? 0.5 - 0.5 * cos(PI * phase / 0.6) : cos(PI * (phase - 0.6) / 0.8);
    if (pulse < 0) pulse = 0;
    double y = pulse - glottal + 0.02 * (uniform() - 0.5);
    glottal = pulse;
    for (int f = 0; f < 3; f++) {
      double frequency = from[f] + (to[f] - from[f]) * glide;
      double r = exp(-PI * widths[f] / rate), w = 2 * PI * frequency / rate;
      double out = (1 - r) * y + 2 * r * cos(w) * state[f][0] - r * r * state[f][1];
      state[f][1] = state[f][0], state[f][0] = out;
      y = out;
    }
    double envelope = at < 0 ? 0 : 0.5 - 0.5 * cos(2 * PI * at);
    x[n] = (float)(120 * level * envelope * y + noise_level * gaussian());
  }
}

// --- the plugin ------------------------------------------------------------------

typedef enum { DELAY, DENOISE, SILENCE, NOISE, SHAPED } Mode;

enum { RING = 1 << 16 };

typedef struct {
  Mode mode;
  uint32_t delay;
  float ring[MAX_CHANNELS][RING];
  uint64_t position;
  float follow[MAX_CHANNELS], gain[MAX_CHANNELS], previous[MAX_CHANNELS][2];
  uint64_t noise;
} Fixture;

static float noise_sample(Fixture *f) {
  f->noise ^= f->noise << 13, f->noise ^= f->noise >> 7, f->noise ^= f->noise << 17;
  return (float)((f->noise >> 40) / 8388608.0 - 1.0);
}

static void fixture_process(Host *h, uint32_t frames, float *const *in,
                            float *const *out) {
  Fixture *f = h->impl;
  for (uint32_t n = 0; n < frames; ++n) {
    for (unsigned c = 0; c < h->channels; ++c) {
      f->ring[c][(f->position + n) & (RING - 1)] = in[c][n];
      float x = f->ring[c][(f->position + n - f->delay) & (RING - 1)];
      float y = x;
      if (f->mode == DENOISE) {
        // A noise remover's effect: the pauses gated down by 30 dB with a
        // 50 ms release, and the speech through a short smoothing filter,
        // [1/4 1/2 1/4], which delays it by one more sample.
        float level = fabsf(x);
        f->follow[c] = level > f->follow[c] ? level : f->follow[c] * 0.9996f;
        float want = f->follow[c] > 0.02f ? 1.0f : 0.03f;
        f->gain[c] += (want - f->gain[c]) * 0.002f;
        y = f->gain[c] * (0.25f * x + 0.5f * f->previous[c][0] + 0.25f * f->previous[c][1]);
        f->previous[c][1] = f->previous[c][0];
        f->previous[c][0] = x;
      } else if (f->mode == SILENCE) {
        y = 0;
      } else if (f->mode == NOISE) {
        y = 0.1f * noise_sample(f);
      } else if (f->mode == SHAPED) {
        // Noise that rises and falls with the speech, 10 ms behind it.
        float level = fabsf(x);
        f->follow[c] += (level - f->follow[c]) * 0.002f;
        y = 4 * f->follow[c] * noise_sample(f);
      }
      out[c][n] = y;
    }
  }
  f->position += frames;
  host_processing_result(h, true, frames);
}

static uint32_t fixture_latency(Host *h) {
  Fixture *f = h->impl;
  return f->mode == DELAY ? f->delay : UINT32_MAX;
}

static const Backend fixture_backend = {.name = "fixture", .process = fixture_process,
                                        .latency = fixture_latency};

// --- the rig -----------------------------------------------------------------------

typedef struct {
  Host h;
  Fixture *f;
  float *speech;
  size_t length, position;
  uint32_t since_tick;
  double analysis_ms, slowest_ms;
  unsigned analyses;
  float first_value;   // the first figure reported
  size_t known_at;     // frames run when it was
  bool wrong;          // a figure other than the expected one was reported
  FILE *printed;       // what the tick wrote to the daemon
} Rig;

static double now_ms(void) {
  struct timespec t;
  clock_gettime(CLOCK_MONOTONIC, &t);
  return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

static void rig_open(Rig *r, unsigned channels, uint32_t rate, Mode mode,
                     uint32_t delay, double seconds, double noise_db, bool own_block) {
  memset(r, 0, sizeof(*r));
  r->f = calloc(1, sizeof(Fixture));
  assert(r->f);
  r->f->mode = mode;
  r->f->delay = delay;
  r->f->noise = 0x2545f4914f6cdd1du;
  r->h.backend = &fixture_backend;
  r->h.impl = r->f;
  r->h.rate = rate;
  r->h.channels = channels;
  r->h.own_block = own_block;
  r->h.silence = calloc(MAX_FRAMES, sizeof(float));
  r->h.scratch = calloc(MAX_FRAMES, sizeof(float));
  assert(r->h.silence && r->h.scratch);
  if (own_block) assert(own_block_init(&r->h));
  for (unsigned c = 0; c < channels; ++c) {
    test_buffers[c] = calloc(MAX_FRAMES, sizeof(float));
    test_buffers[MAX_CHANNELS + c] = calloc(MAX_FRAMES, sizeof(float));
    assert(test_buffers[c] && test_buffers[MAX_CHANNELS + c]);
    r->h.in_ports[c] = &test_buffers[c];
    r->h.out_ports[c] = &test_buffers[MAX_CHANNELS + c];
  }
  r->length = (size_t)(seconds * rate);
  r->speech = calloc(r->length, sizeof(float));
  assert(r->speech);
  random_state = 0x9e3779b97f4a7c15u ^ ((uint64_t)delay << 20) ^ mode;
  make_speech(r->speech, r->length, rate, noise_db);
  // The path a real plugin takes, less the format check this fixture fails.
  measure_attach(&r->h);
  assert(r->h.measure && r->h.measured);
  assert(isnan(atomic_load(&r->h.measured->observed)));
  r->first_value = NAN;
  r->printed = tmpfile();
  assert(r->printed);
}

// The tick, with what it prints to the daemon kept apart from this test's
// own lines.
static void rig_tick(Rig *r) {
  fflush(stdout);
  int saved = dup(STDOUT_FILENO);
  assert(saved >= 0 && dup2(fileno(r->printed), STDOUT_FILENO) >= 0);
  tick(&r->h, 1);
  fflush(stdout);
  assert(dup2(saved, STDOUT_FILENO) >= 0);
  close(saved);
}

// The `meter host.measured_latency` lines the tick printed, and the last one.
static unsigned printed_figures(Rig *r, double *last) {
  rewind(r->printed);
  char line[256];
  unsigned count = 0;
  while (fgets(line, sizeof(line), r->printed)) {
    double value;
    if (sscanf(line, "meter host.measured_latency %lf", &value) == 1) {
      count++;
      *last = value;
    }
  }
  return count;
}

static void rig_close(Rig *r) {
  if (r->h.own_block) {
    own_block_stop(&r->h);
    own_block_free(&r->h);
  }
  measure_free(&r->h);
  fclose(r->printed);
  for (unsigned c = 0; c < r->h.channels; ++c) {
    free(test_buffers[c]), free(test_buffers[MAX_CHANNELS + c]);
    test_buffers[c] = test_buffers[MAX_CHANNELS + c] = NULL;
  }
  free(r->h.silence), free(r->h.scratch), free(r->speech), free(r->f);
}

// Run the whole signal through in cycles of `frames`, with the tick every
// 33 ms of audio. The second channel, when there is one, carries the same
// voice a little quieter.
static void rig_run(Rig *r, uint32_t frames, float expected) {
  uint32_t tick_frames = r->h.rate / 30;
  while (r->position + frames <= r->length) {
    for (unsigned c = 0; c < r->h.channels; ++c)
      for (uint32_t n = 0; n < frames; ++n)
        test_buffers[c][n] = r->speech[r->position + n] * (c ? 0.7f : 1.0f);
    struct spa_io_position position = {0};
    position.clock.duration = frames;
    position.clock.rate.denom = r->h.rate;
    atomic_store(&counting, true);
    process_audio(&r->h, &position);
    atomic_store(&counting, false);
    if (r->h.own_block) own_block_drain(&r->h);  // a worker that answers at once
    r->position += frames;
    if ((r->since_tick += frames) < tick_frames) continue;
    r->since_tick = 0;
    unsigned before;
    latency_meter_last(r->h.measure, &before);
    double started = now_ms();
    rig_tick(r);
    double took = now_ms() - started;
    unsigned after;
    latency_meter_last(r->h.measure, &after);
    if (after != before) {
      r->analyses++;
      r->analysis_ms += took;
      if (took > r->slowest_ms) r->slowest_ms = took;
    }
    float value = r->h.measured->reported;  // what the tick last printed
    if (isfinite(value)) {
      if (isnan(r->first_value)) r->first_value = value, r->known_at = r->position;
      if (!(fabsf(value - expected) <= 1)) r->wrong = true;
    }
  }
}

static void check_known(unsigned channels, uint32_t rate, Mode mode, uint32_t delay,
                        float expected, const char *what) {
  Rig r;
  rig_open(&r, channels, rate, mode, delay, 14, -60, false);
  rig_run(&r, 256, expected);
  unsigned count;
  const LatencyAnalysis *a = latency_meter_last(r.h.measure, &count);
  printf("%s: measured %.0f (expected %.0f) after %.1f s of audio; last match: "
         "envelopes %.3f, away from the peak %.3f, waveforms %.3f, %u active ms\n",
         what, r.h.measured->reported, expected, (double)r.known_at / rate,
         a->coarse, a->side, a->fine, a->active);
  assert(isfinite(r.first_value));
  assert(fabsf(r.h.measured->reported - expected) <= 1);
  assert(!r.wrong);
  double line = NAN;
  assert(printed_figures(&r, &line) == 1 && line == r.h.measured->reported);
  assert(atomic_load(&allocations) == 0);
  rig_close(&r);
}

static void check_never(Mode mode, const char *what) {
  Rig r;
  rig_open(&r, 1, 48000, mode, 480, 30, -60, false);
  rig_run(&r, 256, -1000);
  unsigned count;
  latency_meter_last(r.h.measure, &count);
  // Every analysis of the run is looked at, not just the last one.
  printf("%s: %u analyses, none confident; nothing reported\n", what, count);
  assert(count >= 20);
  assert(isnan(r.first_value) && !isfinite(r.h.measured->reported));
  assert(latency_meter_samples(r.h.measure) == -1);
  double line;
  assert(printed_figures(&r, &line) == 0);
  assert(atomic_load(&allocations) == 0);
  rig_close(&r);
}

// Like check_never, but with every analysis checked as it happens.
static void check_never_confident(Mode mode, const char *what) {
  Rig r;
  rig_open(&r, 1, 48000, mode, 480, 30, -60, false);
  uint32_t tick_frames = r.h.rate / 30;
  unsigned seen = 0, confident = 0, enough = 0;
  float coarse = 0, fine = 0;
  while (r.position + 256 <= r.length) {
    for (uint32_t n = 0; n < 256; ++n) test_buffers[0][n] = r.speech[r.position + n];
    struct spa_io_position position = {0};
    position.clock.duration = 256;
    position.clock.rate.denom = r.h.rate;
    process_audio(&r.h, &position);
    r.position += 256;
    if ((r.since_tick += 256) < tick_frames) continue;
    r.since_tick = 0;
    rig_tick(&r);
    unsigned count;
    const LatencyAnalysis *a = latency_meter_last(r.h.measure, &count);
    if (count == seen) continue;
    seen = count;
    confident += a->confident;
    enough += a->enough;
    if (a->coarse > coarse) coarse = a->coarse;
    if (a->fine > fine) fine = a->fine;
  }
  printf("%s: %u analyses, %u with enough signal, %u confident; best envelope "
         "match %.3f, best waveform match %.3f\n",
         what, seen, enough, confident, coarse, fine);
  assert(seen >= 20 && confident == 0);
  assert(!isfinite(r.h.measured->reported));
  double line;
  assert(printed_figures(&r, &line) == 0);
  rig_close(&r);
}

// On its own block the plugin's figure and the block's delay add up, and the
// measured delay has to say the same as the host's report.
static void check_own_block(void) {
  Rig r;
  rig_open(&r, 1, 48000, DELAY, 480, 14, -60, true);
  rig_run(&r, 256, 480 + 1536);
  assert(r.h.latency_reported);
  printf("own block: measured %.0f, reported %u (the plugin's 480 and the "
         "block's 1536)\n",
         r.h.measured->reported, r.h.reported_latency);
  assert(r.h.reported_latency == 480 + 1536);
  assert(r.h.measured->reported == (float)r.h.reported_latency);
  assert(atomic_load(&allocations) == 0);
  rig_close(&r);
}

// Only the plugin formats measure; the built-in stages report their own.
static void check_formats(void) {
  Host h = {0};
  h.rate = 48000;
  h.backend = &fixture_backend;
  measure_start(&h);
  assert(!h.measure && !h.measured && h.control_count == 0);
  const Backend *formats[] = {&lv2_backend, &clap_backend, &vst3_backend};
  for (unsigned i = 0; i < 3; ++i) {
    Host *p = calloc(1, sizeof(Host));
    assert(p);
    p->rate = 48000;
    p->backend = formats[i];
    measure_start(p);
    assert(p->measure && p->measured && p->control_count == 1);
    assert(!strcmp(p->measured->symbol, "host.measured_latency") && p->measured->output);
    measure_free(p);
    free(p);
  }
  puts("PASS: LV2, CLAP and VST3 nodes measure, a built-in stage does not");
}

// What the measurement costs: the audio thread's copy per cycle, and the
// main thread's analysis once a second.
static void check_cost(void) {
  Rig r;
  rig_open(&r, 2, 48000, DELAY, 2400, 14, -60, false);
  enum { CYCLES = 20000 };
  double with = 0, without = 0;
  for (int pass = 0; pass < 2; ++pass) {
    LatencyMeter *saved = r.h.measure;
    if (pass == 1) r.h.measure = NULL;
    double started = now_ms();
    for (unsigned i = 0; i < CYCLES; ++i) {
      struct spa_io_position position = {0};
      position.clock.duration = 256;
      position.clock.rate.denom = 48000;
      process_audio(&r.h, &position);
    }
    *(pass == 0 ? &with : &without) = (now_ms() - started) * 1e6 / CYCLES;
    r.h.measure = saved;
  }
  rig_close(&r);
  rig_open(&r, 1, 48000, DELAY, 2400, 30, -60, false);
  rig_run(&r, 256, 2400);
  printf("cost: audio callback %.0f ns a cycle of 256 stereo frames with the "
         "copy, %.0f without; analysis %.2f ms on average, %.2f at most, once "
         "a second (%u analyses), %.2f%% of one core\n",
         with, without, r.analysis_ms / r.analyses, r.slowest_ms, r.analyses,
         r.analysis_ms / r.analyses / 10);
  rig_close(&r);
}

int main(void) {
  check_formats();
  const uint32_t delays[] = {0, 480, 2400, 24000};
  for (unsigned i = 0; i < 4; ++i) {
    char what[64];
    snprintf(what, sizeof(what), "a delay of %u", delays[i]);
    check_known(1, 48000, DELAY, delays[i], (float)delays[i], what);
  }
  check_known(2, 48000, DELAY, 2400, 2400, "stereo, a delay of 2400");
  check_known(1, 44100, DELAY, 0, 0, "44.1 kHz, no delay");
  check_known(1, 44100, DELAY, 2205, 2205, "44.1 kHz, a delay of 2205");
  check_known(1, 48000, DENOISE, 960, 961, "a noise remover, 960 and its filter's 1");
  {
    // The noise remover again with the room 15 dB louder.
    Rig r;
    rig_open(&r, 1, 48000, DENOISE, 960, 14, -45, false);
    rig_run(&r, 256, 961);
    printf("a noise remover in a noisy room: measured %.0f (expected 961)\n",
           r.h.measured->reported);
    assert(fabsf(r.h.measured->reported - 961) <= 1 && !r.wrong);
    rig_close(&r);
  }
  check_own_block();
  check_never(SILENCE, "silence out");
  check_never_confident(NOISE, "unrelated noise out");
  check_never_confident(SHAPED, "noise shaped like the speech out");
  puts("PASS: known delays to the sample, the own block added in, and no "
       "figure from silence or noise");
  check_cost();
  return 0;
}
