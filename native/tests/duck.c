// SPDX-License-Identifier: GPL-3.0-only
// The duck backend driven directly at 48 kHz in 256-frame blocks. No
// audio server: the gain is measured from the stereo output samples.
#define main host_program_main
#include "../host.c"
#undef main
#include "../duck.c"
#include <assert.h>

enum { RATE = 48000, BLOCK = 256, COUNT = 3 * RATE };
static float input[2][COUNT], output[2][COUNT], key[COUNT], second[COUNT];

static Host *fresh(void) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &duck_backend;
  h->rate = RATE, h->channels = 2;
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

static void set(Host *h, const char *symbol, float value) {
  assert(set_control(h, symbol, value));
}

static void fill(float level) {
  for (unsigned n = 0; n < COUNT; n++) {
    input[0][n] = 1, input[1][n] = -0.5f;
    key[n] = level;
  }
}

static void run(Host *h, size_t start, size_t count) {
  assert(start + count <= COUNT);
  for (size_t at = start; at < start + count; at += BLOCK) {
    uint32_t frames = (uint32_t)fmin(start + count - at, BLOCK);
    float *in[] = {input[0] + at, input[1] + at};
    float *out[] = {output[0] + at, output[1] + at};
    h->keys[0] = key + at;
    h->keys[1] = second + at;
    h->backend->process(h, frames, in, out);
    const Duck *d = h->impl;
    assert(isfinite(d->envelope) && isfinite(d->gain));
    assert(fpclassify(d->envelope) != FP_SUBNORMAL && fpclassify(d->gain) != FP_SUBNORMAL);
    assert(fabs(atomic_load(&d->reduction->observed) + d->gain) < 1e-6);
    for (unsigned c = 0; c < 2; c++)
      for (uint32_t n = 0; n < frames; n++) assert(isfinite(out[c][n]));
  }
}

static double gain_at(size_t n) { return 20 * log10(output[0][n] / input[0][n]); }

static void unity(size_t count) {
  for (unsigned c = 0; c < 2; c++)
    assert(memcmp(input[c], output[c], count * sizeof(float)) == 0);
}

static double check_steps(size_t count, double limit) {
  double previous = 0, worst = 0;
  for (size_t n = 0; n < count; n++) {
    double gain = gain_at(n);
    worst = fmax(worst, fabs(gain - previous));
    previous = gain;
    assert(output[1][n] == -0.5f * output[0][n]);
  }
  assert(worst <= limit + 1e-6);
  return worst;
}

static void check_controls(void) {
  assert(backend_named("duck") == &duck_backend && duck_backend.key_inputs == MAX_KEYS);
  Host *h = fresh();
  const struct { const char *name; float minimum, maximum, initial; bool output; } controls[] = {
      {"enabled", 0, 1, 0, false}, {"threshold", -60, 0, -12, false},
      {"depth", 0, 40, 12, false}, {"attack", 1, 5000, 80, false},
      {"release", 1, 10000, 1200, false}, {"key_open", 0, 255, 1, false},
      {"reduction", 0, 40, 0, true}};
  assert(h->control_count == sizeof(controls) / sizeof(controls[0]));
  for (uint32_t i = 0; i < h->control_count; i++) {
    Control *c = &h->controls[i];
    assert(!strcmp(c->symbol, controls[i].name));
    assert(c->minimum == controls[i].minimum && c->maximum == controls[i].maximum);
    assert(atomic_load(&c->desired) == controls[i].initial && c->output == controls[i].output);
    assert(!set_control(h, c->symbol, NAN) && !set_control(h, c->symbol, INFINITY));
    if (c->output) assert(!set_control(h, c->symbol, 1));
    else {
      set(h, c->symbol, -100000);
      assert(atomic_load(&c->desired) == c->minimum);
      set(h, c->symbol, 100000);
      assert(atomic_load(&c->desired) == c->maximum);
    }
  }
  assert(!set_control(h, "unknown", 0));
  char line[] = "set enabled 0";
  command(h, line);
  assert(atomic_load(&((Duck *)h->impl)->enabled->desired) == 0);
  release(h);
  Host mono = {.channels = 1, .rate = RATE};
  assert(!duck_load(&mono, NULL) && mono.impl == NULL);
  puts("controls: six inputs and one read-only meter, defaults and bounds checked; mono refused");
}

static void check_idle(void) {
  Host *h = fresh();
  fill(0);
  uint32_t seed = 1;
  for (unsigned c = 0; c < 2; c++)
    for (unsigned n = 0; n < RATE; n++) {
      seed = seed * 1664525u + 1013904223u;
      input[c][n] = (float)((seed >> 8) / 8388608.0 - 1);
    }
  input[0][0] = -0.0f;
  set(h, "enabled", 1);
  run(h, 0, RATE);
  unity(RATE);
  float idle = atomic_load(&((Duck *)h->impl)->reduction->observed);
  assert(idle == 0 && !signbit(idle));
  release(h);
  puts("idle: 48000 stereo noise samples with a silent key pass bit for bit");
}

static void check_medium(void) {
  Host *h = fresh();
  fill((float)pow(10, -6.0 / 20));
  for (unsigned n = RATE; n < COUNT; n++) key[n] = 0;
  set(h, "enabled", 1);
  run(h, 0, RATE);
  assert(atomic_load(&((Duck *)h->impl)->reduction->observed) == 12);
  run(h, RATE, COUNT - RATE);
  double half = gain_at(1920 - 1), full = gain_at(3840 - 1);
  assert(fabs(half + 6) <= 0.5 && fabs(full + 12) <= 0.05);
  for (unsigned n = 3840 - 1; n < RATE + 1440; n++) assert(fabs(gain_at(n) + 12) <= 0.05);
  unsigned first = 0, recovered = 0;
  for (unsigned n = RATE; n < COUNT; n++) {
    if (!first && gain_at(n) > -12 + 1e-5) first = n + 1;
    if (!recovered && output[0][n] == input[0][n]) recovered = n + 1;
    assert(output[0][n] >= output[0][n - 1]);
  }
  double hold_ms = (first - RATE) * 1000.0 / RATE;
  double back_ms = recovered * 1000.0 / RATE;
  assert(first && recovered && fabs(back_ms - 2234) <= 5);
  assert(fabs(hold_ms - 50 * log(pow(10, -6.0 / 20) / pow(10, -12.0 / 20))) <= 1000.0 / RATE);
  double worst = check_steps(COUNT, 12.0 / 3840);
  printf("medium: %.6f dB at 40 ms, %.6f dB at 80 ms, meter 12 dB\n", half, full);
  printf("release: -12 dB at 1030 ms, hold %.3f ms, unity at %.3f ms (2234 +/- 5 ms), monotonic\n",
         hold_ms, back_ms);
  printf("steps: largest %.9f dB (limit %.9f dB)\n", worst, 12.0 / 3840 + 1e-6);
  release(h);
}

static void check_key(void) {
  const struct { float threshold, key, open; bool ducks; } cases[] = {
      {-3, (float)pow(10, -6.0 / 20), 1, false},
      {-12, 1, 0, false}, {-24, (float)pow(10, -18.0 / 20), 1, true},
      {0, -1, 1, true}};
  for (unsigned i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
    Host *h = fresh();
    fill(cases[i].key);
    set(h, "enabled", 1);
    set(h, "threshold", cases[i].threshold);
    set(h, "key_open", cases[i].open);
    run(h, 0, RATE);
    if (cases[i].ducks) assert(fabs(gain_at(RATE - 1) + 12) < 0.05);
    else unity(RATE);
    release(h);
  }
  puts("key: Low ignores -6 dBFS; closed key ignores 0 dBFS; High triggers at -18 dBFS; equality and negative polarity trigger");
}

// Two keys: the detector follows the louder, never their sum, and a key
// whose bit is clear in key_open is not heard at all.
static void check_keys(void) {
  const float below = (float)pow(10, -15.0 / 20);  // each under -12 dBFS; together +6 dB over it
  struct { float first, second; float open; bool ducks; } cases[] = {
      {below, below, 3, false}, {0, 1, 3, true}, {0, 1, 1, false},
      {1, 0, 2, false}, {0, 1, 2, true}, {1, 1, 0, false}};
  for (unsigned i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
    Host *h = fresh();
    fill(cases[i].first);
    for (unsigned n = 0; n < COUNT; n++) second[n] = cases[i].second;
    set(h, "enabled", 1);
    set(h, "key_open", cases[i].open);
    run(h, 0, RATE);
    if (cases[i].ducks) assert(fabs(gain_at(RATE - 1) + 12) < 0.05);
    else unity(RATE);
    release(h);
  }
  memset(second, 0, sizeof(second));
  puts("keys: two keys under the threshold do not add up; the louder open key ducks; a closed key is not heard");
}

static void check_nonfinite_key(void) {
  Host *h = fresh();
  fill(0);
  key[100] = INFINITY, key[200] = -INFINITY, key[300] = NAN;
  set(h, "enabled", 1);
  run(h, 0, RATE);
  unity(RATE);
  const Duck *d = h->impl;
  assert(d->envelope == 0 && d->gain == 0);
  release(h);
  puts("nonfinite key: infinite and NaN key samples are ignored and leave no state behind");
}

static void check_presets(void) {
  const struct { const char *name; float attack, release; } speeds[] = {
      {"slow", 100, 2000}, {"medium", 80, 1200}, {"fast", 30, 200}};
  const float depths[] = {6, 12, 24};
  for (unsigned s = 0; s < 3; s++)
    for (unsigned a = 0; a < 3; a++) {
      Host *h = fresh();
      fill(1);
      set(h, "enabled", 1);
      set(h, "depth", depths[a]);
      set(h, "attack", speeds[s].attack);
      set(h, "release", speeds[s].release);
      unsigned attack = (unsigned)(speeds[s].attack * RATE / 1000);
      unsigned recovery = (unsigned)(speeds[s].release * RATE / 1000);
      run(h, 0, RATE);
      assert(fabs(gain_at(attack / 2 - 1) + depths[a] / 2) < 0.05);
      assert(fabs(gain_at(attack - 1) + depths[a]) < 0.05);
      assert(atomic_load(&((Duck *)h->impl)->reduction->observed) == depths[a]);
      set(h, "enabled", 0);
      run(h, RATE, recovery);
      double step = depths[a] / recovery;
      assert(fabs(gain_at(RATE) + depths[a] - step) < 1e-6);
      assert(fabs(gain_at(RATE + recovery / 2 - 1) + depths[a] / 2) < 0.05);
      assert(fabs(gain_at(RATE + recovery - 1)) < 1e-6);
      for (unsigned n = RATE; n < RATE + recovery; n++)
        assert(output[0][n] >= output[0][n - 1]);
      double worst = check_steps(RATE + recovery, depths[a] / attack);
      printf("presets: %g dB, %s: %.6f dB at %g ms, %.6f dB after %g ms disabled, step %.9f dB\n",
             depths[a], speeds[s].name, gain_at(attack - 1), speeds[s].attack,
             gain_at(RATE + recovery - 1), speeds[s].release, worst);
      release(h);
    }
}

static void check_zero_depth(void) {
  Host *h = fresh();
  fill(1);
  run(h, 0, RATE);
  unity(RATE);  // off by default, even with a loud key
  set(h, "enabled", 1);
  run(h, 0, RATE);
  assert(fabs(gain_at(RATE - 1) + 12) < 0.05);
  set(h, "depth", 0);
  run(h, 0, RATE);
  unity(RATE);
  assert(atomic_load(&((Duck *)h->impl)->reduction->observed) == 0);
  release(h);
  puts("unity: disabled by default; changing depth to zero restores exact unity");
}

static void check_silence(void) {
  Host *h = fresh();
  fill(1);
  set(h, "enabled", 1);
  run(h, 0, RATE);
  fill(0);
  memset(input, 0, sizeof(input));
  for (unsigned second = 0; second < 60; second++) run(h, 0, RATE);
  const Duck *d = h->impl;
  assert(d->envelope == 0 && d->gain == 0);
  unity(RATE);
  release(h);
  puts("silence: 60 seconds after a burst leave zero envelope and gain, with no NaN or subnormal state");
}

int main(void) {
  setvbuf(stdout, NULL, _IOLBF, 0);
  check_controls();
  check_idle();
  check_medium();
  check_key();
  check_keys();
  check_nonfinite_key();
  check_presets();
  check_zero_depth();
  check_silence();
  puts("PASS: duck controls, exact idle output, detector, depths, timing, ramps and silence");
  return 0;
}
