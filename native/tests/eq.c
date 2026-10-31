// SPDX-License-Identifier: GPL-3.0-only
// The microphone EQ backend driven directly with deterministic signals and
// no audio server: its response against tools/eq-reference.py's table, the
// size of a step while a value moves, the crossfades and the silence after
// a loud burst.
#define main host_program_main
#include "../host.c"
#undef main
#include "../eq.c"
#include <assert.h>
#include <time.h>

// Noise removal's three controls and four meters, and the voice changer's
// four controls and three meters, beside the EQ's own (tests/denoise.c and
// tests/voice.c check them).
enum { DENOISE_CONTROLS = 7, VOICE_CONTROLS = 7 };

static const char *const TYPES[EQ_TYPES] = {"lowCut", "lowShelf", "peak",
                                            "notch", "highShelf", "highCut"};

typedef struct {
  int enabled, type;
  double frequency, gain, q;
} Setting;

static Host *fresh(uint32_t rate) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->rate = rate;
  h->channels = 1;
  assert(eq_load(h, NULL));
  return h;
}

static void release(Host *h) {
  eq_unload(h);
  free(h);
}

static void set(Host *h, const char *symbol, double value) {
  assert(set_control(h, symbol, (float)value));
}

static void set_band(Host *h, int band, Setting s) {
  char symbol[32];
  snprintf(symbol, sizeof(symbol), "band%d_enabled", band);
  set(h, symbol, s.enabled);
  snprintf(symbol, sizeof(symbol), "band%d_type", band);
  set(h, symbol, s.type);
  snprintf(symbol, sizeof(symbol), "band%d_frequency", band);
  set(h, symbol, s.frequency);
  snprintf(symbol, sizeof(symbol), "band%d_gain", band);
  set(h, symbol, s.gain);
  snprintf(symbol, sizeof(symbol), "band%d_qlog10", band);
  set(h, symbol, log10(s.q));
}

// One configured, activated EQ with every band given and the EQ on.
static Host *configured(uint32_t rate, const Setting bands[EQ_BANDS]) {
  Host *h = fresh(rate);
  set(h, "enabled", 1);
  for (int i = 0; i < EQ_BANDS; i++) set_band(h, i + 1, bands[i]);
  assert(eq_activate(h));
  return h;
}

static void run(Host *h, const float *input, float *output, size_t count, uint32_t block) {
  for (size_t at = 0; at < count; at += block) {
    uint32_t frames = (uint32_t)(count - at < block ? count - at : block);
    float *in[] = {(float *)input + at}, *out[] = {output + at};
    eq_process(h, frames, in, out);
  }
}

static int type_named(const char *name) {
  for (int t = 0; t < EQ_TYPES; t++)
    if (!strcmp(TYPES[t], name)) return t;
  fprintf(stderr, "unknown type %s\n", name);
  exit(1);
}

static double db(double ratio) { return 20 * log10(ratio); }

// --- the response against the reference table -------------------------------

enum { POINTS = 64, IMPULSE = 1 << 18 };

static void check_reference(const char *path) {
  FILE *file = fopen(path, "r");
  if (!file) {
    perror(path);
    exit(1);
  }
  static char line[16384];
  double frequencies[POINTS];
  bool have_frequencies = false;
  float *impulse = calloc(IMPULSE, sizeof(float)), *response = calloc(IMPULSE, sizeof(float));
  assert(impulse && response);
  impulse[0] = 1;
  int rows = 0, compared = 0;
  double worst = 0;
  while (fgets(line, sizeof(line), file)) {
    char *field = strtok(line, " \n");
    if (!field || field[0] == '#') continue;
    if (!strcmp(field, "f")) {
      for (int i = 0; i < POINTS; i++) frequencies[i] = strtod(strtok(NULL, " \n"), NULL);
      have_frequencies = true;
      continue;
    }
    assert(!strcmp(field, "r") && have_frequencies);
    uint32_t rate = (uint32_t)strtoul(strtok(NULL, " \n"), NULL, 10);
    Setting bands[EQ_BANDS];
    for (int i = 0; i < EQ_BANDS; i++) {
      bands[i].enabled = atoi(strtok(NULL, " \n"));
      bands[i].type = type_named(strtok(NULL, " \n"));
      bands[i].frequency = strtod(strtok(NULL, " \n"), NULL);
      bands[i].gain = strtod(strtok(NULL, " \n"), NULL);
      bands[i].q = strtod(strtok(NULL, " \n"), NULL);
    }
    Host *h = configured(rate, bands);
    run(h, impulse, response, IMPULSE, 1024);
    release(h);
    for (int i = 0; i < POINTS; i++) {
      double expected = strtod(strtok(NULL, " \n"), NULL);
      // The spectrum of the impulse response at this one frequency.
      double angle = -2 * EQ_PI * frequencies[i] / rate;
      double wr = cos(angle), wi = sin(angle), pr = 1, pi = 0, re = 0, im = 0;
      for (size_t n = 0; n < IMPULSE; n++) {
        re += response[n] * pr;
        im += response[n] * pi;
        double next = pr * wr - pi * wi;
        pi = pr * wi + pi * wr;
        pr = next;
        if ((n & 4095) == 4095) {  // keep the phasor on the unit circle
          double norm = hypot(pr, pi);
          pr /= norm, pi /= norm;
        }
      }
      double measured = db(hypot(re, im));
      // Far below the signal a float output's rounding is all there is
      // to measure; the table is held to 0.05 dB down to -90 dB.
      if (expected < -90) continue;
      double error = fabs(measured - expected);
      if (error > worst) worst = error;
      compared++;
      if (error > 0.05) {
        fprintf(stderr, "row %d at %u Hz: %.2f Hz measured %.4f dB, expected %.4f dB\n",
                rows, rate, frequencies[i], measured, expected);
        exit(1);
      }
    }
    rows++;
  }
  fclose(file);
  free(impulse);
  free(response);
  assert(rows >= 60);
  printf("response: %d settings, %d points within 0.05 dB of the cookbook (worst %.5f dB)\n",
         rows, compared, worst);
}

// --- a value moving while a tone plays --------------------------------------

enum { RATE = 48000, BLOCK = 256 };
static const double TONE = 0.25;  // -12 dBFS

static void tone(float *buffer, size_t count, double frequency) {
  for (size_t n = 0; n < count; n++)
    buffer[n] = (float)(TONE * sin(2 * EQ_PI * frequency * n / RATE));
}

static const Setting OFF = {0, PEAK, 1000, 0, 0.7046930689671475};

// The worst error, relative to the ideal output's peak, after one band's
// value moves from `before` to `after` at a block boundary, against the
// output of an EQ that was set to `after` all along. With `clear` the band's state is
// emptied at the change, the way PipeWire's builtin biquads behave.
static double step_error(Setting before, Setting after, bool clear) {
  enum { SETTLE = RATE, AFTER = RATE / 5 };
  static float input[SETTLE + AFTER], moved[SETTLE + AFTER], ideal[SETTLE + AFTER];
  tone(input, SETTLE + AFTER, 200);
  Setting first[EQ_BANDS] = {OFF, before, OFF, OFF}, second[EQ_BANDS] = {OFF, after, OFF, OFF};
  Host *h = configured(RATE, first), *reference = configured(RATE, second);
  run(h, input, moved, SETTLE, BLOCK);
  set_band(h, 2, after);
  if (clear) {
    eq_follow(h->impl, RATE);
    EqBand *b = &((Eq *)h->impl)->bands[1];
    b->live.s1 = b->live.s2 = 0;
  }
  run(h, input + SETTLE, moved + SETTLE, AFTER, BLOCK);
  run(reference, input, ideal, SETTLE + AFTER, BLOCK);
  release(h);
  release(reference);
  double worst = 0, peak = 0;
  for (size_t n = SETTLE; n < SETTLE + AFTER; n++) {
    worst = fmax(worst, fabs(moved[n] - ideal[n]));
    peak = fmax(peak, fabs(ideal[n]));
  }
  return db(worst / peak);
}

static void check_steps(void) {
  const double q = 0.7046930689671475;
  struct {
    const char *what;
    Setting before, after;
  } cases[] = {
      {"bell at 310 Hz, 0.25 dB", {1, PEAK, 310, 6, q}, {1, PEAK, 310, 6.25, q}},
      {"low shelf at 100 Hz, 0.25 dB", {1, LOW_SHELF, 100, 6, q}, {1, LOW_SHELF, 100, 6.25, q}},
      {"high pass at 36 Hz, 1 Hz", {1, LOW_CUT, 36, 0, q}, {1, LOW_CUT, 37, 0, q}},
      {"bell at 310 Hz, 1 Hz", {1, PEAK, 310, 9.25, q}, {1, PEAK, 311, 9.25, q}},
  };
  for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
    double kept = step_error(cases[i].before, cases[i].after, false);
    double cleared = step_error(cases[i].before, cases[i].after, true);
    printf("step: %s: %.1f dB with the state kept, %.1f dB cleared\n", cases[i].what, kept, cleared);
    assert(kept < -30);
    // The measure tells the two apart, so a backend that clears its state
    // again would fail the line above.
    assert(cleared > kept + 10);
  }
}

// --- crossfades ----------------------------------------------------------------

// The worst distance, relative to the tone, between what the EQ plays after
// a switch at a block boundary and a crossfade between the two settled
// outputs `from` and `to`, where `t(k)` is the fade's position k samples in.
static double fade_error(const float *output, const float *from, const float *to,
                         size_t at, size_t count, double (*t)(size_t, uint32_t), uint32_t fade) {
  double worst = 0;
  for (size_t n = at; n < count; n++) {
    double w = t(n - at, fade);
    double expected = from[n] + (to[n] - from[n]) * w;
    worst = fmax(worst, fabs(output[n] - expected));
  }
  return db(fmax(worst, 1e-300) / TONE);
}

// A switch's mix moves one step before each sample.
static double switch_t(size_t k, uint32_t fade) { return fmin(1.0, (double)(k + 1) / fade); }
// A type change starts on the old filter.
static double change_t(size_t k, uint32_t fade) { return fmin(1.0, (double)k / fade); }

static void check_fades(void) {
  enum { SETTLE = RATE / 2, AFTER = RATE / 10, COUNT = SETTLE + AFTER };
  static float input[COUNT], output[COUNT], from[COUNT], to[COUNT];
  tone(input, COUNT, 200);
  const double q = 1;
  Setting bell = {1, PEAK, 200, 12, q}, shelf = {1, LOW_SHELF, 200, 12, q};
  Setting bell_off = bell;
  bell_off.enabled = 0;
  uint32_t fade = (uint32_t)lrint(RATE * 0.005);

  // A band switched on: from the dry tone to the band's output.
  {
    Setting off[EQ_BANDS] = {OFF, bell_off, OFF, OFF}, on[EQ_BANDS] = {OFF, bell, OFF, OFF};
    Host *h = configured(RATE, off), *a = configured(RATE, off), *b = configured(RATE, on);
    assert(((Eq *)h->impl)->fade == fade);
    run(h, input, output, SETTLE, BLOCK);
    set(h, "band2_enabled", 1);
    run(h, input + SETTLE, output + SETTLE, AFTER, BLOCK);
    run(a, input, from, COUNT, BLOCK);
    run(b, input, to, COUNT, BLOCK);
    double error = fade_error(output, from, to, SETTLE, COUNT, switch_t, fade);
    double hard = fade_error(to, from, to, SETTLE, SETTLE + fade, switch_t, fade);
    printf("fade: band on %.1f dB from a 5 ms crossfade (a hard switch: %.1f dB)\n", error, hard);
    assert(error < -40 && hard > -20);
    // Off again fades back the same way.
    set(h, "band2_enabled", 0);
    run(h, input + SETTLE, output + SETTLE, AFTER, BLOCK);
    release(h), release(a), release(b);
  }
  // The EQ switched on, with a band that changes the tone.
  {
    Setting bands[EQ_BANDS] = {OFF, bell, OFF, OFF};
    Host *h = configured(RATE, bands), *b = configured(RATE, bands);
    set(h, "enabled", 0);
    eq_activate(h);  // built off: the switch below fades in
    run(h, input, output, SETTLE, BLOCK);
    for (size_t n = 0; n < SETTLE; n++) assert(output[n] == input[n]);  // bypassed
    set(h, "enabled", 1);
    run(h, input + SETTLE, output + SETTLE, AFTER, BLOCK);
    run(b, input, to, COUNT, BLOCK);
    double error = fade_error(output, input, to, SETTLE, COUNT, switch_t, fade);
    printf("fade: EQ on %.1f dB from a 5 ms crossfade\n", error);
    assert(error < -40);
    release(h), release(b);
  }
  // A type change: the new filter fades in over the old one.
  struct {
    const char *what;
    Setting before, after;
  } changes[] = {
      {"bell to low shelf at 200 Hz", bell, shelf},
      {"high pass to low pass at 1 kHz", {1, LOW_CUT, 1000, 0, 0.7}, {1, HIGH_CUT, 1000, 0, 0.7}},
      {"bell to notch at 60 Hz, Q 10", {1, PEAK, 60, 12, 10}, {1, NOTCH, 60, 0, 10}},
  };
  for (size_t i = 0; i < sizeof(changes) / sizeof(changes[0]); i++) {
    Setting before[EQ_BANDS] = {OFF, changes[i].before, OFF, OFF};
    Setting after[EQ_BANDS] = {OFF, changes[i].after, OFF, OFF};
    Host *h = configured(RATE, before), *a = configured(RATE, before), *b = configured(RATE, after);
    run(h, input, output, SETTLE, BLOCK);
    set_band(h, 2, changes[i].after);
    run(h, input + SETTLE, output + SETTLE, AFTER, BLOCK);
    run(a, input, from, COUNT, BLOCK);
    run(b, input, to, COUNT, BLOCK);
    double error = fade_error(output, from, to, SETTLE, COUNT, change_t, fade);
    printf("fade: %s %.1f dB from a 5 ms crossfade\n", changes[i].what, error);
    assert(error < -40);
    release(h), release(a), release(b);
  }
}

// --- silence after a burst ---------------------------------------------------------

static bool subnormal(double value) { return fpclassify(value) == FP_SUBNORMAL; }

static void check_states(const Eq *e) {
  for (int i = 0; i < EQ_BANDS; i++) {
    const EqBand *b = &e->bands[i];
    assert(!subnormal(b->live.s1) && !subnormal(b->live.s2));
    assert(!subnormal(b->fading.s1) && !subnormal(b->fading.s2));
  }
}

static void check_silence(void) {
  Setting bands[EQ_BANDS] = {{1, PEAK, 60, 12, 10}, {1, LOW_SHELF, 100, 12, 2},
                             {1, NOTCH, 3000, 0, 10}, {1, HIGH_CUT, 15049, 0, 0.7}};
  Host *h = configured(RATE, bands);
  float input[512], output[512];
  uint32_t seed = 1;
  for (int block = 0; block < RATE / 512; block++) {
    for (int n = 0; n < 512; n++) {
      seed = seed * 1664525u + 1013904223u;
      input[n] = (float)((seed >> 8) / 8388608.0 - 1.0);  // full-scale noise
    }
    float *in[] = {input}, *out[] = {output};
    eq_process(h, 512, in, out);
  }
  set(h, "band3_type", PEAK);  // a type change fading out over the silence
  memset(input, 0, sizeof(input));
  for (int block = 0; block < 60 * RATE / 512; block++) {
    float *in[] = {input}, *out[] = {output};
    eq_process(h, 512, in, out);
    check_states(h->impl);
    for (int n = 0; n < 512; n++) assert(fpclassify(output[n]) != FP_SUBNORMAL);
  }
  const Eq *e = h->impl;
  for (int i = 0; i < EQ_BANDS; i++)
    assert(e->bands[i].live.s1 == 0 && e->bands[i].live.s2 == 0);
  for (int n = 0; n < 512; n++) assert(output[n] == 0);
  release(h);
  puts("silence: no subnormal state or sample, and every filter at rest, after a minute of silence");
}

// --- the controls, and what it costs ------------------------------------------

static void check_controls(void) {
  assert(backend_named("eq") == &eq_backend);
  Host *two = calloc(1, sizeof(*two));
  two->rate = RATE, two->channels = 2;
  assert(!eq_load(two, NULL));
  eq_unload(two);
  free(two);
  Host *h = fresh(RATE);
  assert(h->control_count == 1 + 5 * EQ_BANDS + DENOISE_CONTROLS + VOICE_CONTROLS);
  const char *names[] = {"enabled", "type", "frequency", "gain", "qlog10"};
  for (int band = 1; band <= EQ_BANDS; band++)
    for (int k = 0; k < 5; k++) {
      char symbol[32];
      snprintf(symbol, sizeof(symbol), "band%d_%s", band, names[k]);
      assert(set_control(h, symbol, 0));
    }
  assert(!set_control(h, "band5_gain", 0) && !set_control(h, "band1_gain", NAN));
  assert(set_control(h, "band1_gain", 99));
  assert(atomic_load(&((Eq *)h->impl)->bands[0].gain->desired) == 12);
  assert(set_control(h, "band1_type", 9));
  assert(atomic_load(&((Eq *)h->impl)->bands[0].type->desired) == HIGH_CUT);
  release(h);
  puts("controls: the 21 EQ symbols the daemon writes, clamped to their ranges, beside noise removal's six");
}

static void check_variable_bands(void) {
  assert(backend_named("eq-bands") == &eq_bands_backend);
  const char *invalid[] = {"", "0", "17", "-1", "+4", "4.5", " 4", "99999999999999999999999"};
  for (size_t i = 0; i < sizeof(invalid) / sizeof(invalid[0]); i++) {
    Host *h = calloc(1, sizeof(*h));
    assert(h); h->rate = RATE; h->channels = 1;
    char *args[] = {(char *)invalid[i], NULL};
    assert(!eq_load_bands(h, args));
    eq_unload(h); free(h);
  }
  for (int count = 1; count <= EQ_MAX_BANDS; count++) {
    Host *h = calloc(1, sizeof(*h));
    assert(h); h->rate = RATE; h->channels = 1;
    char number[8]; snprintf(number, sizeof(number), "%d", count);
    char *args[] = {number, NULL};
    assert(eq_load_bands(h, args));
    assert(((Eq *)h->impl)->band_count == (unsigned)count);
    assert(h->control_count == (unsigned)(1 + 5 * count + DENOISE_CONTROLS + VOICE_CONTROLS));
    for (int band = 1; band <= count; band++) set_band(h, band, (Setting)OFF);
    set_band(h, count, (Setting){1, PEAK, 1000, 6, 1});
    set(h, "enabled", 1);
    assert(eq_activate(h));
    enum { FRAMES = RATE / 2 };
    static float input[FRAMES], output[FRAMES];
    tone(input, FRAMES, 1000); run(h, input, output, FRAMES, BLOCK);
    double dry = 0, wet = 0;
    for (int n = FRAMES / 2; n < FRAMES; n++) {
      assert(isfinite(output[n])); dry += input[n] * input[n]; wet += output[n] * output[n];
    }
    assert(fabs(10 * log10(wet / dry) - 6) < 0.02);
    release(h);
  }
  puts("variable bands: every count from 1 to 16 processes its last band, with bounded controls");
}

static void measure_cost(void) {
  Setting bands[EQ_BANDS] = {{1, LOW_CUT, 36, 0, 0.7}, {1, PEAK, 310, 3, 0.7},
                             {1, PEAK, 2172, -3, 0.7}, {1, HIGH_CUT, 15049, 0, 0.7}};
  Host *h = configured(RATE, bands);
  enum { QUANTUM = 1024, SECONDS = 20 };
  static float input[QUANTUM], output[QUANTUM];
  tone(input, QUANTUM, 200);
  struct timespec start, end;
  clock_gettime(CLOCK_MONOTONIC, &start);
  for (int i = 0; i < SECONDS * RATE / QUANTUM; i++) {
    float *in[] = {input}, *out[] = {output};
    eq_process(h, QUANTUM, in, out);
  }
  clock_gettime(CLOCK_MONOTONIC, &end);
  double seconds = (end.tv_sec - start.tv_sec) + (end.tv_nsec - start.tv_nsec) / 1e9;
  printf("cost: %.1f ns a sample, %.2f us a 1024-sample quantum, %.3f%% of one core at 48 kHz\n",
         seconds * 1e9 / (SECONDS * RATE), seconds * 1e6 / (SECONDS * RATE / QUANTUM),
         100 * seconds / SECONDS);
  release(h);
}

int main(int argc, char **argv) {
  setvbuf(stdout, NULL, _IOLBF, 0);
  const char *reference = argc > 1 ? argv[1] : "tests/eq-reference.txt";
  check_controls();
  check_variable_bands();
  check_reference(reference);
  check_steps();
  check_fades();
  check_silence();
  measure_cost();
  puts("PASS: response, steps, crossfades and silence");
  return 0;
}
