// SPDX-License-Identifier: GPL-3.0-only
// The clock bridge without an audio server: two simulated clocks, the
// device's off the mixer's by a known number of parts per million, call the
// writer and the reader in the order their cycles would come, with the
// device's cycles late by a random amount and now and then by a lot. It
// checks that the ring settles at its target and stays there with no
// underrun or overrun, that the loop's ratio converges on the true one and
// does not wobble, that a tone comes through clean, that a level comes
// through exact, and that a device that stops and a mixer that stops each
// cost one counted event and recover.
#include "../bridge.c"
#include <assert.h>

enum { RATE = 48000 };

typedef struct {
  const char *name;
  double ppm;               // the device's clock against the mixer's
  uint32_t in_q, out_q;     // the two cycles
  double in_jitter_ms;      // how late each device cycle may run
  double out_jitter_ms;     // and each mixer cycle
  double burst_every_s;     // now and then the device runs this late
  double burst_ms;
  bool burst_moves_time;    // a late device cycle also reports a late time
  double seconds;
  double device_stall_from, device_stall_to;  // the device writes nothing
  double mixer_stall_from, mixer_stall_to;    // the mixer reads nothing
} Scenario;

typedef struct {
  uint64_t underruns, overruns;     // over the whole run
  uint64_t late_underruns, late_overruns;  // after the settling time
  double ppm_mean, ppm_spread;      // the ratio over the last ten seconds
  double fill_min, fill_max;        // the ring after settling, device frames
  double delay_ms;
  double target;
  double worst_residual_db;         // the tone's residual against a fit
  double level_error;               // the DC channel's largest error
  bool running;
} Result;

// A small fixed generator, so every run sees the same jitter.
static uint64_t seed = 0x9e3779b97f4a7c15ull;
static double uniform(void) {
  seed ^= seed << 13;
  seed ^= seed >> 7;
  seed ^= seed << 17;
  return (double)(seed >> 11) / (double)(1ull << 53);
}

static float in_buffer[2][BRIDGE_MAX_QUANTUM], out_buffer[2][BRIDGE_MAX_QUANTUM];

// Output frames of the tone channel and the level channel, kept for checking.
static float *played[2];
static size_t played_count;

static Result run(const Scenario *s) {
  Bridge b;
  assert(bridge_init(&b, 2, BRIDGE_DEFAULT_QUANTA, BRIDGE_DEFAULT_MARGIN_MS));
  const double device_rate = RATE * (1 + s->ppm * 1e-6);
  const double t_in = s->in_q / device_rate * 1e9, t_out = s->out_q / (double)RATE * 1e9;
  const double end = s->seconds * 1e9, settle = 20e9;
  size_t capacity = (size_t)(s->seconds * RATE) + BRIDGE_MAX_QUANTUM;
  played[0] = realloc(played[0], capacity * sizeof(float));
  played[1] = realloc(played[1], capacity * sizeof(float));
  played_count = 0;
  Result r = {.fill_min = 1e18, .fill_max = -1e18};
  uint64_t k = 0, j = 0, device_frames = 0;
  double device_last = 0, mixer_last = 0, next_burst = s->burst_every_s * 1e9;
  double ppm_sum = 0, ppm_min = 1e18, ppm_max = -1e18;
  unsigned ppm_count = 0;
  uint64_t settled_underruns = 0, settled_overruns = 0;
  bool settled = false;
  double device_at = 0, mixer_at = 0;
  // Each side's next callback: its cycle's start plus how late it runs.
  bool device_next_late = false;
  double device_late = 0;
  for (;;) {
    double start_in = k * t_in, start_out = j * t_out + 0.37 * t_out;
    if (!device_next_late) {
      device_late = uniform() * s->in_jitter_ms * 1e6;
      if (s->burst_every_s > 0 && start_in >= next_burst) {
        device_late = s->burst_ms * 1e6;
        next_burst += s->burst_every_s * 1e9;
      }
      device_next_late = true;
    }
    device_at = start_in + device_late;
    if (device_at < device_last) device_at = device_last;  // bunched behind a late one
    mixer_at = start_out + uniform() * s->out_jitter_ms * 1e6;
    if (mixer_at < mixer_last) mixer_at = mixer_last;
    if (device_at > end && mixer_at > end) break;
    if (device_at <= mixer_at) {
      device_last = device_at;
      device_next_late = false;
      double seconds = start_in / 1e9;
      bool stalled = seconds >= s->device_stall_from && seconds < s->device_stall_to;
      if (!stalled) {
        // A 1 kHz tone at the device's own rate, and a steady level.
        for (uint32_t n = 0; n < s->in_q; ++n) {
          double t = (double)(device_frames + n) / device_rate;
          in_buffer[0][n] = (float)(0.5 * sin(2 * M_PI * 1000 * t));
          in_buffer[1][n] = 0.25f;
        }
        const float *in[] = {in_buffer[0], in_buffer[1]};
        // Late by a burst, or bunched behind one.
        bool moved = s->burst_moves_time &&
                     (device_late > s->in_jitter_ms * 1e6 || device_at > start_in + device_late);
        uint64_t nsec = (uint64_t)(moved ? device_at : start_in);
        bridge_produce(&b, in, s->in_q, RATE, nsec, nsec + (uint64_t)t_in);
      }
      // The device's clock runs on whether or not anything was written.
      device_frames += s->in_q;
      ++k;
    } else {
      mixer_last = mixer_at;
      double seconds = start_out / 1e9;
      bool stalled = seconds >= s->mixer_stall_from && seconds < s->mixer_stall_to;
      if (!stalled) {
        float *out[] = {out_buffer[0], out_buffer[1]};
        bridge_consume(&b, out, s->out_q, RATE, (uint64_t)start_out);
        for (uint32_t n = 0; n < s->out_q && played_count < capacity; ++n, ++played_count) {
          played[0][played_count] = out_buffer[0][n];
          played[1][played_count] = out_buffer[1][n];
        }
        // BRIDGE_TRACE=1 prints the loop once a second.
        if (getenv("BRIDGE_TRACE") && j % (RATE / s->out_q) == 0)
          fprintf(stderr, "%5.1f s corr %+9.3f ppm fill %7.1f state %d\n", start_out / 1e9, (b.corr - 1) * 1e6, b.fill_avg, atomic_load(&b.state));
        if (start_out >= settle) {
          if (!settled) {
            settled = true;
            settled_underruns = atomic_load(&b.underruns);
            settled_overruns = atomic_load(&b.overruns);
          }
          // The ring as the loop sees it, from the reader's own state.
          double fill = b.fill_avg;
          if (fill < r.fill_min) r.fill_min = fill;
          if (fill > r.fill_max) r.fill_max = fill;
          if (start_out >= end - 10e9) {
            double ppm = (b.corr - 1) * 1e6;
            ppm_sum += ppm;
            ppm_count++;
            if (ppm < ppm_min) ppm_min = ppm;
            if (ppm > ppm_max) ppm_max = ppm;
          }
        }
      }
      ++j;
    }
  }
  r.underruns = atomic_load(&b.underruns);
  r.overruns = atomic_load(&b.overruns);
  r.late_underruns = r.underruns - settled_underruns;
  r.late_overruns = r.overruns - settled_overruns;
  r.ppm_mean = ppm_count ? ppm_sum / ppm_count : NAN;
  r.ppm_spread = ppm_count ? ppm_max - ppm_min : NAN;
  r.delay_ms = atomic_load(&b.delay) * 1000.0 / RATE;
  r.target = b.target;
  r.running = atomic_load(&b.state) == BRIDGE_RUNNING;

  // The tone in tenth-second windows over the last ten seconds: a
  // least-squares fit at 1 kHz on the mixer's clock, and what is left over.
  size_t window = RATE / 10;
  r.worst_residual_db = -1e9;
  r.level_error = 0;
  for (size_t at = played_count - 10 * (size_t)RATE; at + window <= played_count; at += window) {
    double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
    for (size_t n = 0; n < window; ++n) {
      double phase = 2 * M_PI * 1000 * (double)(at + n) / RATE;
      double sn = sin(phase), cs = cos(phase), y = played[0][at + n];
      ss += sn * sn, cc += cs * cs, sc += sn * cs, ys += y * sn, yc += y * cs;
      double level = fabs(played[1][at + n] - 0.25);
      if (level > r.level_error) r.level_error = level;
    }
    double det = ss * cc - sc * sc;
    double a = (ys * cc - yc * sc) / det, c = (yc * ss - ys * sc) / det;
    double residual = 0;
    for (size_t n = 0; n < window; ++n) {
      double phase = 2 * M_PI * 1000 * (double)(at + n) / RATE;
      double e = played[0][at + n] - (a * sin(phase) + c * cos(phase));
      residual += e * e;
    }
    double amplitude = sqrt(a * a + c * c);
    double db = 10 * log10(residual / window / (amplitude * amplitude / 2));
    if (db > r.worst_residual_db) r.worst_residual_db = db;
    assert(fabs(amplitude - 0.5) < 0.005);
  }
  printf("%-34s target %4.0f  delay %5.2f ms  fill %6.1f..%6.1f  drift %+9.3f ppm (spread %.3f)  "
         "residual %6.1f dB  xruns %llu/%llu\n",
         s->name, r.target, r.delay_ms, r.fill_min, r.fill_max, r.ppm_mean, r.ppm_spread,
         r.worst_residual_db, (unsigned long long)r.underruns, (unsigned long long)r.overruns);
  bridge_free(&b);
  return r;
}

// What every bridge that holds has to show, after it settled.
static void holds(const Scenario *s, const Result *r) {
  assert(r->running);
  assert(r->late_underruns == 0 && r->late_overruns == 0);
  // The ratio found the device's clock, within a part per million, and
  // moves far less than the 578 ppm of one cent of pitch.
  assert(fabs(r->ppm_mean - s->ppm) < 1.0);
  assert(r->ppm_spread < 5.0);
  // The ring stays near its target and never near empty.
  assert(r->fill_min > r->target - s->in_q - 64);
  assert(r->fill_max < r->target + s->in_q + 64);
  assert(fabs(r->delay_ms - r->target * 1000.0 / RATE) < 2.0);
  // A clean tone, and a level that arrives exactly.
  assert(r->worst_residual_db < -70);
  assert(r->level_error < 1e-5);
}

static void check_kernel(void) {
  bridge_kernel_init();
  for (int p = 0; p <= BRIDGE_PHASES; ++p) {
    double sum = 0;
    for (int t = 0; t < BRIDGE_TAPS; ++t) sum += kernel[p][t];
    assert(fabs(sum - 1) < 1e-6);
  }
  // At a whole frame the kernel is centred on that frame, and symmetric.
  for (int t = 0; t < BRIDGE_TAPS; ++t) {
    if (t != BRIDGE_HALF - 1) assert(fabs(kernel[0][t]) < 0.1 * kernel[0][BRIDGE_HALF - 1]);
    if (t > 0 && t < BRIDGE_TAPS - 1) assert(fabs(kernel[0][t] - kernel[0][2 * (BRIDGE_HALF - 1) - t]) < 1e-6);
  }
  // The last row is the first moved by one frame.
  for (int t = 1; t < BRIDGE_TAPS; ++t) assert(fabs(kernel[BRIDGE_PHASES][t] - kernel[0][t - 1]) < 1e-6);
}

static void check_arguments(void) {
  Bridge b;
  assert(!bridge_init(&b, 0, 2, 5));
  assert(!bridge_init(&b, 3, 2, 5));
  assert(!bridge_init(&b, 2, 0.1, 5));
  assert(!bridge_init(&b, 2, 2, -1));
  assert(!bridge_init(&b, 2, NAN, 5));
  assert(bridge_init(&b, 1, 2, 5));
  // The target: the device's cycle, two mixer cycles, the kernel and 5 ms.
  assert(bridge_target(&b, 256, 48000, 256, 48000) == 256 + 512 + BRIDGE_HALF + 240);
  assert(bridge_target(&b, 1024, 48000, 256, 48000) == 1024 + 512 + BRIDGE_HALF + 240);
  bridge_free(&b);
  // The default margin is 10 ms: 1280 frames, 26.7 ms, at a 256 frame cycle.
  assert(bridge_init(&b, 1, BRIDGE_DEFAULT_QUANTA, BRIDGE_DEFAULT_MARGIN_MS));
  assert(bridge_target(&b, 256, 48000, 256, 48000) == 1280);
  assert(bridge_target(&b, 8192, 48000, 8192, 48000) == BRIDGE_RING / 4);
  // Nothing written yet: silence, and still waiting.
  float left[256], *out[] = {left};
  for (int n = 0; n < 256; ++n) left[n] = 1;
  bridge_consume(&b, out, 256, 48000, 1000);
  for (int n = 0; n < 256; ++n) assert(left[n] == 0);
  assert(atomic_load(&b.state) == BRIDGE_WAITING && atomic_load(&b.underruns) == 0);
  // A cycle larger than PipeWire allows is ignored on both sides.
  bridge_produce(&b, NULL, BRIDGE_MAX_QUANTUM + 1, 48000, 1, 2);
  assert(atomic_load(&b.written) == 0);
  bridge_free(&b);
}

// One channel, as a microphone on a mono port runs: a level arrives exact.
static void check_mono(void) {
  Bridge b;
  assert(bridge_init(&b, 1, BRIDGE_DEFAULT_QUANTA, BRIDGE_DEFAULT_MARGIN_MS));
  float level[256], heard[256];
  for (int n = 0; n < 256; ++n) level[n] = -0.3f;
  const float *in[] = {level};
  float *out[] = {heard};
  uint64_t t = 1000000000;
  unsigned running = 0;
  for (int cycle = 0; cycle < 400; ++cycle, t += 5333333) {
    bridge_produce(&b, in, 256, 48000, t, t + 5333333);
    bridge_consume(&b, out, 256, 48000, t + 2000000);
    if (atomic_load(&b.state) == BRIDGE_RUNNING && ++running > 2)
      for (int n = 0; n < 256; ++n) assert(fabsf(heard[n] + 0.3f) < 1e-6f);
  }
  assert(running > 300 && atomic_load(&b.underruns) == 0 && atomic_load(&b.overruns) == 0);
  bridge_free(&b);
}

int main(void) {
  setvbuf(stdout, NULL, _IOLBF, 0);
  check_kernel();
  check_arguments();
  check_mono();
  played[0] = played[1] = NULL;

  Scenario same = {.name = "one clock, no jitter", .ppm = 0, .in_q = 256, .out_q = 256, .seconds = 40};
  Result r = run(&same);
  holds(&same, &r);
  assert(r.underruns == 0 && r.overruns == 0);

  // The 4K X case: a device a little fast, its cycles late by up to 2 ms
  // and every few seconds by 12 ms, as an uneven USB controller would. The
  // default target holds a device cycle late by a mixer cycle plus the
  // margin, 15 ms here, less where in the cycle the delay begins.
  Scenario uneven = {.name = "+120 ppm, uneven device", .ppm = 120, .in_q = 256, .out_q = 256,
                     .in_jitter_ms = 2, .out_jitter_ms = 0.5, .burst_every_s = 3, .burst_ms = 12,
                     .seconds = 60};
  r = run(&uneven);
  holds(&uneven, &r);
  assert(r.underruns == 0 && r.overruns == 0);

  // The same, with the late cycles also reporting late times.
  Scenario moved = uneven;
  moved.name = "+120 ppm, late times reported";
  moved.burst_moves_time = true;
  r = run(&moved);
  holds(&moved, &r);

  Scenario slow = {.name = "-350 ppm, device cycle 1024", .ppm = -350, .in_q = 1024, .out_q = 256,
                   .in_jitter_ms = 3, .out_jitter_ms = 0.5, .seconds = 60};
  r = run(&slow);
  holds(&slow, &r);
  assert(r.underruns == 0 && r.overruns == 0);

  Scenario coarse = {.name = "+40 ppm, mixer cycle 1024", .ppm = 40, .in_q = 256, .out_q = 1024,
                     .in_jitter_ms = 1, .out_jitter_ms = 1, .seconds = 60};
  r = run(&coarse);
  holds(&coarse, &r);

  // A cheap crystal far off: a fifth of a percent.
  Scenario cheap = {.name = "+2000 ppm", .ppm = 2000, .in_q = 256, .out_q = 256,
                    .in_jitter_ms = 1, .seconds = 60};
  r = run(&cheap);
  holds(&cheap, &r);

  // The device stops for a fifth of a second: one underrun, silence, then
  // the bridge fills to its target again and holds.
  Scenario gap = {.name = "device stops 200 ms", .ppm = 80, .in_q = 256, .out_q = 256,
                  .in_jitter_ms = 1, .seconds = 60, .device_stall_from = 10, .device_stall_to = 10.2};
  r = run(&gap);
  assert(r.underruns == 1 && r.overruns == 0);
  holds(&gap, &r);

  // The mixer stops for two seconds, longer than the ring: the device's
  // cycles are dropped while it is full, which counts once, and the reader
  // drops back to the target when it returns, which counts once more.
  Scenario pause = {.name = "mixer stops 2 s", .ppm = -60, .in_q = 256, .out_q = 256,
                    .in_jitter_ms = 1, .seconds = 60, .mixer_stall_from = 10, .mixer_stall_to = 12};
  r = run(&pause);
  assert(r.underruns == 0 && r.overruns == 2);
  holds(&pause, &r);

  free(played[0]);
  free(played[1]);
  puts("bridge: ok");
  return 0;
}
