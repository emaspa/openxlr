// SPDX-License-Identifier: GPL-3.0-only
// The compressor and the expander driven through the microphone stage with
// no audio server, at 48 kHz in blocks of 256, with nothing allocated while
// a block runs. Off, the stage is its input to the bit, before and after the
// processors have run. A steady level, a square wave whose every sample has
// the same magnitude, comes out at the static curve's gain within 0.05 dB,
// and the reduction meter says the gain that was applied. The reduction
// reaches 63.2 percent of a step in the attack or the release time, after
// the hold for the expander. Silence and full scale stay finite, bounded and
// clear of subnormal numbers; the meters stay still while they are off; and
// a live change moves the output no further than the change itself.
#define main host_program_main
#include "../host.c"
#undef main
#include "../eq.c"
#include <assert.h>
#include <time.h>

enum { RATE = 48000, BLOCK = 256 };
static const double PI = 3.14159265358979323846;

// Every allocation while the stage processes a block is counted: the audio
// thread may not allocate. C++'s operator new comes here through malloc.
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

// --- the stage -----------------------------------------------------------------

static Host *stage(void) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &eq_backend;
  h->rate = RATE;
  h->channels = 1;
  assert(h->backend->load(h, NULL));
  assert(h->backend->activate(h));
  return h;
}

static void release(Host *h) {
  h->backend->deactivate(h);
  h->backend->unload(h);
  assert(h->impl == NULL);
  free(h);
}

static void set(Host *h, const char *symbol, float value) { assert(set_control(h, symbol, value)); }

static Control *find(Host *h, const char *symbol) {
  for (uint32_t i = 0; i < h->control_count; i++)
    if (!strcmp(h->controls[i].symbol, symbol)) return &h->controls[i];
  assert(!"no such control");
  return NULL;
}

static float meter(Host *h, const char *symbol) { return atomic_load(&find(h, symbol)->observed); }

// One block, then the tick's part, as the host runs them.
static void block(Host *h, const float *in, float *out, uint32_t frames) {
  float *ins[] = {(float *)in}, *outs[] = {out};
  atomic_store(&counting, true);
  h->backend->process(h, frames, ins, outs);
  atomic_store(&counting, false);
  assert(atomic_load(&allocations) == 0);
  for (uint32_t n = 0; n < frames; n++) {
    assert(isfinite(out[n]));
    assert(fpclassify(out[n]) != FP_SUBNORMAL);
  }
  h->backend->main_thread(h);
}

static void run(Host *h, const float *in, float *out, size_t count) {
  for (size_t at = 0; at < count; at += BLOCK) {
    uint32_t size = (uint32_t)(count - at < BLOCK ? count - at : BLOCK);
    block(h, in + at, out + at, size);
  }
}

static Dynamics *processor(Host *h, int kind) {
  Eq *e = h->impl;
  return kind == DYNAMICS_COMPRESSOR ? e->compressor : e->expander;
}

// --- signals -------------------------------------------------------------------

// A square wave at 100 Hz whose every sample is `db` dBFS in magnitude, so the
// detector sees one steady level.
static void square(float *x, size_t count, double db) {
  float a = (float)pow(10, db / 20);
  for (size_t n = 0; n < count; n++) x[n] = (n / 240) % 2 ? -a : a;
}

// A steady vowel-like buzz: the first twelve harmonics of 140 Hz, falling
// off, at about -16 dBFS, plus zero so no sample is -0.
static void buzz(float *x, size_t count) {
  for (size_t n = 0; n < count; n++) {
    double y = 0;
    for (int k = 1; k <= 12; k++) y += sin(2 * PI * 140 * k * n / RATE + k) / k;
    x[n] = (float)(0.1 * y) + 0.0f;
  }
}

// The gain of output over input at a sample, in dB.
static double gain_db(const float *in, const float *out, size_t n) {
  return 20 * log10(fabs((double)out[n] / in[n]));
}

static float in[8 * RATE], out[8 * RATE];

// --- checks --------------------------------------------------------------------

static void check_controls(void) {
  Host *h = stage();
  const struct { const char *name; float minimum, maximum, initial; bool output; } controls[] = {
      {"comp_enabled", 0, 1, 0, false}, {"comp_threshold", -60, 0, -18, false},
      {"comp_ratio", 1, 20, 3, false}, {"comp_attack", 0.1f, 200, 5, false},
      {"comp_release", 10, 2000, 150, false}, {"comp_knee", 0, 24, 6, false},
      {"comp_makeup", 0, 24, 0, false}, {"comp_meters", 0, 1, 0, false},
      {"comp_input", -120, 48, -120, true}, {"comp_reduction", 0, 120, 0, true},
      {"comp_output", -120, 48, -120, true},
      {"exp_enabled", 0, 1, 0, false}, {"exp_threshold", -80, 0, -50, false},
      {"exp_ratio", 1, 20, 3, false}, {"exp_range", 0, 80, 24, false},
      {"exp_attack", 0.1f, 200, 1, false}, {"exp_release", 10, 2000, 150, false},
      {"exp_hold", 0, 500, 50, false}, {"exp_meters", 0, 1, 0, false},
      {"exp_input", -120, 48, -120, true}, {"exp_reduction", 0, 120, 0, true},
      {"exp_output", -120, 48, -120, true},
  };
  for (size_t i = 0; i < sizeof(controls) / sizeof(controls[0]); i++) {
    const Control *c = find(h, controls[i].name);
    assert(c->minimum == controls[i].minimum && c->maximum == controls[i].maximum);
    assert(c->output == controls[i].output);
    float initial = c->output ? atomic_load(&c->observed) : atomic_load(&c->desired);
    assert(initial == controls[i].initial);
  }
  // The stage's own controls keep their places ahead of the new ones.
  assert(!strcmp(h->controls[h->control_count - 1].symbol, "comp_output"));
  release(h);
  puts("controls: the comp_* and exp_* controls and meters at the planned ranges and defaults, after the others");
}

// Off by default, and off again after both ran: the input to the bit.
static void check_off(void) {
  enum { COUNT = 2 * RATE };
  buzz(in, COUNT);
  Host *h = stage();
  run(h, in, out, COUNT);
  assert(!memcmp(in, out, COUNT * sizeof(float)));
  set(h, "comp_enabled", 1), set(h, "comp_threshold", -40), set(h, "comp_makeup", 6);
  set(h, "exp_enabled", 1), set(h, "exp_threshold", -20);
  run(h, in, out, COUNT);
  double top = 0;
  for (size_t n = 0; n < COUNT; n++) top = fmax(top, fabs((double)out[n] - in[n]));
  assert(top > 0.01);
  set(h, "comp_enabled", 0), set(h, "exp_enabled", 0);
  run(h, in, out, RATE / 10);  // the 5 ms fade, and then some
  run(h, in, out, COUNT);
  assert(!memcmp(in, out, COUNT * sizeof(float)));
  assert(dynamics_reduction_now(processor(h, DYNAMICS_COMPRESSOR)) == 0);
  assert(dynamics_reduction_now(processor(h, DYNAMICS_EXPANDER)) == 0);
  release(h);
  printf("off: bit-exact by default and after running on (largest difference while on %.3f)\n", top);
}

// One setting at several steady levels: the measured gain against the curve,
// and the reduction meter against the measured gain.
static void check_curve(const char *name, int kind, const char *const *symbols, const float *values,
                        int count, double makeup, const double *levels, int level_count, double *worst,
                        double *worst_meter) {
  const char *prefix = kind == DYNAMICS_COMPRESSOR ? "comp" : "exp";
  char symbol[32];
  Host *h = stage();
  for (int i = 0; i < count; i++) set(h, symbols[i], values[i]);
  snprintf(symbol, sizeof(symbol), "%s_enabled", prefix);
  set(h, symbol, 1);
  snprintf(symbol, sizeof(symbol), "%s_meters", prefix);
  set(h, symbol, 1);
  double threshold = atomic_load(&find(h, kind == DYNAMICS_COMPRESSOR ? "comp_threshold" : "exp_threshold")->desired);
  double ratio = atomic_load(&find(h, kind == DYNAMICS_COMPRESSOR ? "comp_ratio" : "exp_ratio")->desired);
  double shape = atomic_load(&find(h, kind == DYNAMICS_COMPRESSOR ? "comp_knee" : "exp_range")->desired);
  printf("%s:", name);
  for (int i = 0; i < level_count; i++) {
    size_t length = 3 * RATE / 2;
    square(in, length, levels[i]);
    run(h, in, out, length);
    double measured = gain_db(in, out, length - 1);
    // The level the detector saw: the float sample, not the decimal figure.
    double seen = 20 * log10(fabs((double)in[0]));
    double expected = makeup - dynamics_reduction_db(kind, seen, threshold, ratio, shape);
    double error = fabs(measured - expected);
    snprintf(symbol, sizeof(symbol), "%s_reduction", prefix);
    double shown = meter(h, symbol);
    double meter_error = fabs(shown - (makeup - measured));
    snprintf(symbol, sizeof(symbol), "%s_input", prefix);
    double shown_in = meter(h, symbol);
    snprintf(symbol, sizeof(symbol), "%s_output", prefix);
    double shown_out = meter(h, symbol);
    printf(" %.0f dB -> %+.3f (curve %+.3f, meter %.1f in %.1f out %.1f)", levels[i], measured, expected, shown,
           shown_in, shown_out);
    fflush(stdout);
    assert(error <= 0.05);
    assert(meter_error <= 0.1);
    assert(fabs(shown_in - levels[i]) <= 0.1);
    assert(fabs(shown_out - fmax(levels[i] + measured, DYNAMICS_FLOOR_DB)) <= 0.1);
    *worst = fmax(*worst, error), *worst_meter = fmax(*worst_meter, meter_error);
  }
  puts("");
  release(h);
}

static void check_curves(void) {
  double worst = 0, worst_meter = 0;
  static const double comp_levels[] = {-40, -26, -22, -20, -18, -14, -10, -3, 0};
  static const char *const hard[] = {"comp_threshold", "comp_ratio", "comp_knee"};
  static const float hard_values[] = {-20, 4, 0};
  check_curve("compressor -20 dB 4:1 hard knee", DYNAMICS_COMPRESSOR, hard, hard_values, 3, 0, comp_levels, 9,
              &worst, &worst_meter);
  static const char *const soft[] = {"comp_threshold", "comp_ratio", "comp_knee", "comp_makeup"};
  static const float soft_values[] = {-20, 4, 12, 6};
  check_curve("compressor -20 dB 4:1 12 dB knee +6 dB", DYNAMICS_COMPRESSOR, soft, soft_values, 4, 6, comp_levels,
              9, &worst, &worst_meter);
  static const float limit_values[] = {-30, 20, 6, 12};
  check_curve("compressor -30 dB 20:1 6 dB knee +12 dB", DYNAMICS_COMPRESSOR, soft, limit_values, 4, 12,
              comp_levels, 9, &worst, &worst_meter);
  static const double exp_levels[] = {-20, -45, -50, -52, -55, -60, -70, -90};
  static const char *const expander[] = {"exp_threshold", "exp_ratio", "exp_range"};
  static const float exp_values[] = {-50, 3, 24};
  check_curve("expander -50 dB 1:3 24 dB range", DYNAMICS_EXPANDER, expander, exp_values, 3, 0, exp_levels, 8,
              &worst, &worst_meter);
  static const float gate_values[] = {-40, 20, 60};
  check_curve("expander -40 dB 1:20 60 dB range", DYNAMICS_EXPANDER, expander, gate_values, 3, 0, exp_levels, 8,
              &worst, &worst_meter);
  printf("curves: largest error against the formula %.4f dB, meter against the applied gain %.3f dB\n", worst,
         worst_meter);
}

// The reduction over a step in level: how long it takes to cover 63.2 percent
// of its way, from the step, in milliseconds.
static double time_constant(const float *x, const float *y, size_t from, size_t to, double makeup, double start,
                            double end) {
  double goal = start + (end - start) * (1 - exp(-1.0));
  for (size_t n = from; n < to; n++) {
    double reduction = makeup - gain_db(x, y, n);
    if (end > start ? reduction >= goal : reduction <= goal) return (n - from + 1) * 1000.0 / RATE;
  }
  assert(!"never got there");
  return 0;
}

static void check_timing(void) {
  const double tolerance_ms = 0.05;  // 5 percent, at least a sample's worth
  // Compressor: -30 dB at 10:1, no knee; -40 dB then -10 dB then -40 dB.
  Host *h = stage();
  set(h, "comp_enabled", 1), set(h, "comp_threshold", -30), set(h, "comp_ratio", 10), set(h, "comp_knee", 0);
  set(h, "comp_attack", 10), set(h, "comp_release", 100);
  size_t part = RATE;
  square(in, part, -40), square(in + part, part, -10), square(in + 2 * part, part, -40);
  run(h, in, out, 3 * part);
  double top = dynamics_reduction_db(DYNAMICS_COMPRESSOR, 20 * log10(fabs((double)in[part])), -30, 10, 0);
  double attack = time_constant(in, out, part, 2 * part, 0, 0, top);
  double release_ms = time_constant(in, out, 2 * part, 3 * part, 0, top, 0);
  release(h);
  printf("compressor timing: %.1f dB step, attack %.3f ms (set 10), release %.3f ms (set 100)\n", top, attack,
         release_ms);
  assert(fabs(attack - 10) <= fmax(10 * tolerance_ms, 1000.0 / RATE));
  assert(fabs(release_ms - 100) <= fmax(100 * tolerance_ms, 1000.0 / RATE));

  // Expander: -50 dB at 1:3, 24 dB range; -30 dB then -70 dB then -30 dB.
  h = stage();
  set(h, "exp_enabled", 1), set(h, "exp_threshold", -50), set(h, "exp_ratio", 3), set(h, "exp_range", 24);
  set(h, "exp_attack", 2), set(h, "exp_release", 100), set(h, "exp_hold", 50);
  square(in, part, -30), square(in + part, part, -70), square(in + 2 * part, part, -30);
  run(h, in, out, 3 * part);
  double close_ms = time_constant(in, out, part, 2 * part, 0, 0, 24);
  double open_ms = time_constant(in, out, 2 * part, 3 * part, 0, 24, 0);
  // Nothing moves before the hold runs out.
  double held = 0;
  for (size_t n = part; n < part + (size_t)(0.050 * RATE); n++) held = fmax(held, fabs(gain_db(in, out, n)));
  release(h);
  printf("expander timing: 24 dB step, closes in %.3f ms (hold 50 + release 100, moved %.4f dB during the hold), "
         "opens in %.3f ms (attack 2)\n", close_ms, held, open_ms);
  assert(held < 1e-6);
  assert(fabs(close_ms - 150) <= fmax(100 * tolerance_ms, 1000.0 / RATE));
  assert(fabs(open_ms - 2) <= fmax(2 * tolerance_ms, 1000.0 / RATE));
}

// Silence after a burst, and full scale: finite, bounded, and the reduction
// itself never subnormal.
static void check_extremes(void) {
  Host *h = stage();
  set(h, "comp_enabled", 1), set(h, "comp_threshold", -60), set(h, "comp_ratio", 20), set(h, "comp_knee", 24);
  set(h, "comp_makeup", 24), set(h, "comp_attack", 0.1f), set(h, "comp_release", 10);
  set(h, "exp_enabled", 1), set(h, "exp_threshold", 0), set(h, "exp_ratio", 20), set(h, "exp_range", 80);
  set(h, "exp_attack", 0.1f), set(h, "exp_release", 10), set(h, "exp_hold", 0);
  size_t loud = RATE, quiet = 6 * RATE;
  square(in, loud, 0);
  memset(in + loud, 0, quiet * sizeof(float));
  double makeup = pow(10, 24 / 20.0), top = 0;
  for (size_t at = 0; at < loud + quiet; at += BLOCK) {
    block(h, in + at, out + at, BLOCK);
    for (int k = 0; k < 2; k++) {
      double r = dynamics_reduction_now(processor(h, k));
      assert(isfinite(r) && r >= 0 && fpclassify(r) != FP_SUBNORMAL);
    }
  }
  for (size_t n = 0; n < loud + quiet; n++) top = fmax(top, fabs(out[n]));
  for (size_t n = loud; n < loud + quiet; n++) assert(out[n] == 0);
  assert(top <= makeup * 1.000001);
  double r_comp = dynamics_reduction_now(processor(h, DYNAMICS_COMPRESSOR));
  double r_exp = dynamics_reduction_now(processor(h, DYNAMICS_EXPANDER));
  release(h);
  printf("extremes: full scale peaks at %.3f (bound %.3f), 6 s of silence after it is silence, "
         "reductions end at %g and %g dB, never subnormal\n", top, makeup, r_comp, r_exp);
}

// Meters off: they hold where they are while both processors work.
static void check_meters_off(void) {
  Host *h = stage();
  set(h, "comp_enabled", 1), set(h, "comp_threshold", -40);
  set(h, "exp_enabled", 1), set(h, "exp_threshold", -10);
  static const char *const meters[] = {"comp_input", "comp_reduction", "comp_output",
                                       "exp_input", "exp_reduction", "exp_output"};
  float before[6];
  for (int i = 0; i < 6; i++) before[i] = meter(h, meters[i]);
  buzz(in, 2 * RATE);
  run(h, in, out, 2 * RATE);
  for (int i = 0; i < 6; i++) assert(meter(h, meters[i]) == before[i]);
  // And on, they move; off again, they go back to rest.
  set(h, "comp_meters", 1), set(h, "exp_meters", 1);
  run(h, in, out, RATE / 10);
  assert(meter(h, "comp_reduction") > 0 && meter(h, "exp_input") > -30);
  set(h, "comp_meters", 0), set(h, "exp_meters", 0);
  run(h, in, out, BLOCK);
  for (int i = 0; i < 6; i++) assert(meter(h, meters[i]) == before[i]);
  release(h);
  printf("meters off: held at %.0f/%.0f/%.0f dB while both processors ran\n", before[0], before[1], before[2]);
}

// A live change: the output moves no further from one sample to the next
// than the input does, scaled by the larger of the two gains.
static void check_live_change(void) {
  Host *h = stage();
  set(h, "comp_enabled", 1), set(h, "comp_threshold", -18);
  buzz(in, 2 * RATE);
  double in_step = 0;
  for (size_t n = 1; n < 2 * RATE; n++) in_step = fmax(in_step, fabs((double)in[n] - in[n - 1]));
  run(h, in, out, RATE);
  set(h, "comp_threshold", -40), set(h, "comp_ratio", 8), set(h, "comp_makeup", 12);
  set(h, "comp_attack", 0.1f), set(h, "comp_knee", 0);
  run(h, in + RATE, out + RATE, RATE);
  double step = 0;
  for (size_t n = RATE; n < RATE + RATE / 20; n++) step = fmax(step, fabs((double)out[n] - out[n - 1]));
  double bound = in_step * pow(10, 12 / 20.0) * 1.05;
  release(h);
  printf("live change: largest step after it %.4f, input's largest %.4f, bound at +12 dB %.4f\n", step, in_step,
         bound);
  assert(step <= bound);
}

// What both cost on top of the stage, at 48 kHz in blocks of 256.
static void check_cost(void) {
  enum { COUNT = 8 * RATE };
  buzz(in, COUNT);
  double used[2];
  for (int k = 0; k < 2; k++) {
    Host *h = stage();
    if (k) set(h, "comp_enabled", 1), set(h, "exp_enabled", 1), set(h, "comp_meters", 1), set(h, "exp_meters", 1);
    struct timespec a, b;
    clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &a);
    run(h, in, out, COUNT);
    clock_gettime(CLOCK_PROCESS_CPUTIME_ID, &b);
    used[k] = (b.tv_sec - a.tv_sec) + (b.tv_nsec - a.tv_nsec) / 1e9;
    release(h);
  }
  printf("cost: both on add %.1f ns a sample, %.3f%% of one core at 48 kHz\n",
         (used[1] - used[0]) * 1e9 / COUNT, 100 * (used[1] - used[0]) / 8);
}

int main(void) {
  setvbuf(stdout, NULL, _IONBF, 0);
  check_controls();
  check_off();
  check_curves();
  check_timing();
  check_extremes();
  check_meters_off();
  check_live_change();
  check_cost();
  puts("dynamics: all checks passed");
  return 0;
}
