// SPDX-License-Identifier: GPL-3.0-only
// A compressor and a downward expander for the microphone stage (eq.c). The
// expander runs after noise removal, so it closes on the noise that is left
// rather than on the noise the engine removes, and before the EQ. The
// compressor runs after the EQ, so it evens out the shaped voice, and before
// the voice changer.
//
// Both are feed-forward and work in the log domain. Each sample's level is
// its peak, 20 log10 |x| with a floor of -120 dBFS. A static curve turns the
// level into the gain reduction the processor wants, and a one-pole filter
// per direction smooths that reduction in dB, so a step in the reduction
// reaches 63.2 percent of its way in the attack or the release time. The
// coefficient is exp(-1 / (time * rate)). Attack is always the response to
// the input getting louder: the compressor's reduction rising, the
// expander's falling as it opens.
//
// The compressor's curve has a soft knee of width W around the threshold T
// (Giannoulis, Massberg and Reiss, "Digital Dynamic Range Compressor Design",
// JAES 2012). With d the level above T and R the ratio:
//   2d < -W     no reduction
//   |2d| <= W   (1 - 1/R) (d + W/2)^2 / (2W)
//   2d > W      d (1 - 1/R)
// Its make-up gain is added after the reduction, ramped across a block when
// it changes.
//
// The expander turns down what falls below its threshold: (T - L)(R - 1) dB,
// at most its range. Before it starts to close it waits for the level to
// stay below what keeps it open for the hold time, which keeps it from
// closing between the peaks of a voice's own waveform.
//
// Switching on or off is a 5 ms crossfade between the dry and the processed
// signal, as the EQ's switch is. Once off and faded, the output is the input
// to the bit and the detector is reset, so switching on starts from no
// reduction.
//
// Meters. While <prefix>_meters is on, the audio thread keeps the highest
// input and output peak and the most reduction it applied since the main
// thread last looked; each tick the main thread publishes them, rounded to
// 0.1 dB, and asks for a fresh count. While the switch is off the meters
// hold the floor, no reduction and the floor, so the tick prints nothing.
//
// Who owns what. The daemon writes the controls; the audio thread reads them
// once a block and owns the detector. Nothing allocates or locks while audio
// runs.
#include "dynamics.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

static const double LN10_OVER_20 = 2.302585092994046 / 20;
static const double FADE_SECONDS = 0.005;
// A level floor for the detector, -120 dBFS, so silence has a level.
static const double LEVEL_FLOOR = 1e-6;
// Below this the smoothed reduction is none: a long release never reaches
// subnormal numbers.
static const double REDUCTION_FLUSH = 1e-9;

struct Dynamics {
  Host *h;
  int kind;
  Control *enabled, *threshold, *ratio, *attack, *release, *shape, *extra, *meters;
  Control *input_meter, *reduction_meter, *output_meter;
  uint32_t fade;
  double step;

  // The audio thread's own.
  double mix, mix_target;
  double reduction;  // dB, smoothed
  float attack_now, release_now, extra_now;
  double attack_coeff, release_coeff;
  double threshold_db, ratio_value, shape_db;
  double makeup_gain;  // the compressor's, linear; 1 for the expander
  uint32_t hold, hold_left;  // the expander's, in samples
  double peak_in, peak_out, least_gain;  // since the main thread last looked

  // Between the threads.
  _Atomic bool fresh;  // the main thread looked: start counting again
  _Atomic float shown_in, shown_out, shown_gain;
};

static bool on(const Control *c) { return atomic_load(&c->desired) >= 0.5f; }

static double value_of(const Control *c, double fallback) {
  float v = atomic_load(&c->desired);
  if (!isfinite(v)) return fallback;
  return fmin(fmax(v, c->minimum), c->maximum);
}

double dynamics_reduction_db(int kind, double level_db, double threshold, double ratio, double shape) {
  if (ratio < 1) ratio = 1;
  if (kind == DYNAMICS_COMPRESSOR) {
    double d = level_db - threshold, slope = 1 - 1 / ratio;
    if (2 * d < -shape) return 0;
    if (shape > 0 && 2 * d <= shape) {
      double into = d + shape / 2;
      return slope * into * into / (2 * shape);
    }
    return d * slope;
  }
  if (level_db >= threshold) return 0;
  return fmin((threshold - level_db) * (ratio - 1), shape);
}

double dynamics_reduction_now(const Dynamics *d) { return d ? d->reduction : 0; }

static double coefficient(uint32_t rate, double milliseconds) {
  return exp(-1.0 / (milliseconds * 0.001 * rate));
}

// Take the controls as they are now. The make-up gain is returned rather
// than set, so a block can ramp to it.
static double follow(Dynamics *d) {
  uint32_t rate = d->h->rate;
  d->mix_target = on(d->enabled);
  d->threshold_db = value_of(d->threshold, d->kind == DYNAMICS_COMPRESSOR ? -18 : -50);
  d->ratio_value = value_of(d->ratio, 3);
  d->shape_db = value_of(d->shape, d->kind == DYNAMICS_COMPRESSOR ? 6 : 24);
  float attack = (float)value_of(d->attack, 5), release = (float)value_of(d->release, 150);
  if (attack != d->attack_now) d->attack_now = attack, d->attack_coeff = coefficient(rate, attack);
  if (release != d->release_now) d->release_now = release, d->release_coeff = coefficient(rate, release);
  float extra = (float)value_of(d->extra, 0);
  if (d->kind == DYNAMICS_COMPRESSOR) return exp(extra * LN10_OVER_20);
  if (extra != d->extra_now) {
    d->extra_now = extra;
    d->hold = (uint32_t)lrint(extra * 0.001 * rate);
    if (d->hold_left > d->hold) d->hold_left = d->hold;
  }
  return 1;
}

static void reset(Dynamics *d) {
  d->reduction = 0;
  d->hold_left = d->hold;
}

Dynamics *dynamics_load(Host *h, int kind) {
  if (h->channels != 1 || (kind != DYNAMICS_EXPANDER && kind != DYNAMICS_COMPRESSOR)) return NULL;
  Dynamics *d = calloc(1, sizeof(*d));
  if (!d) return NULL;
  d->h = h;
  d->kind = kind;
  if (kind == DYNAMICS_COMPRESSOR) {
    d->enabled = host_add_control(h, "comp_enabled", 0, 1, 0, false, NULL);
    d->threshold = host_add_control(h, "comp_threshold", -60, 0, -18, false, NULL);
    d->ratio = host_add_control(h, "comp_ratio", 1, 20, 3, false, NULL);
    d->attack = host_add_control(h, "comp_attack", 0.1f, 200, 5, false, NULL);
    d->release = host_add_control(h, "comp_release", 10, 2000, 150, false, NULL);
    d->shape = host_add_control(h, "comp_knee", 0, 24, 6, false, NULL);
    d->extra = host_add_control(h, "comp_makeup", 0, 24, 0, false, NULL);
    d->meters = host_add_control(h, "comp_meters", 0, 1, 0, false, NULL);
    d->input_meter = host_add_control(h, "comp_input", -120, 48, -120, true, NULL);
    d->reduction_meter = host_add_control(h, "comp_reduction", 0, 120, 0, true, NULL);
    d->output_meter = host_add_control(h, "comp_output", -120, 48, -120, true, NULL);
  } else {
    d->enabled = host_add_control(h, "exp_enabled", 0, 1, 0, false, NULL);
    d->threshold = host_add_control(h, "exp_threshold", -80, 0, -50, false, NULL);
    d->ratio = host_add_control(h, "exp_ratio", 1, 20, 3, false, NULL);
    d->shape = host_add_control(h, "exp_range", 0, 80, 24, false, NULL);
    d->attack = host_add_control(h, "exp_attack", 0.1f, 200, 1, false, NULL);
    d->release = host_add_control(h, "exp_release", 10, 2000, 150, false, NULL);
    d->extra = host_add_control(h, "exp_hold", 0, 500, 50, false, NULL);
    d->meters = host_add_control(h, "exp_meters", 0, 1, 0, false, NULL);
    d->input_meter = host_add_control(h, "exp_input", -120, 48, -120, true, NULL);
    d->reduction_meter = host_add_control(h, "exp_reduction", 0, 120, 0, true, NULL);
    d->output_meter = host_add_control(h, "exp_output", -120, 48, -120, true, NULL);
  }
  if (!d->enabled || !d->threshold || !d->ratio || !d->attack || !d->release || !d->shape || !d->extra
      || !d->meters || !d->input_meter || !d->reduction_meter || !d->output_meter) {
    free(d);
    return NULL;
  }
  d->fade = (uint32_t)lrint(h->rate * FADE_SECONDS);
  if (d->fade == 0) d->fade = 1;
  d->step = 1.0 / d->fade;
  atomic_init(&d->fresh, true);
  atomic_init(&d->shown_in, 0);
  atomic_init(&d->shown_out, 0);
  atomic_init(&d->shown_gain, 1);
  return d;
}

bool dynamics_activate(Dynamics *d) {
  d->attack_now = d->release_now = d->extra_now = NAN;
  d->makeup_gain = follow(d);
  d->mix = d->mix_target;
  reset(d);
  return true;
}

void dynamics_unload(Dynamics *d) { free(d); }

static float decibels(double linear) {
  double db = linear > 0 ? 20 * log10(linear) : DYNAMICS_FLOOR_DB;
  if (!(db > DYNAMICS_FLOOR_DB)) db = DYNAMICS_FLOOR_DB;
  return (float)(round(db * 10) / 10);
}

void dynamics_main_thread(Dynamics *d) {
  if (!on(d->meters)) {
    atomic_store(&d->input_meter->observed, (float)DYNAMICS_FLOOR_DB);
    atomic_store(&d->reduction_meter->observed, 0.0f);
    atomic_store(&d->output_meter->observed, (float)DYNAMICS_FLOOR_DB);
    return;
  }
  float reduction = -decibels(atomic_load(&d->shown_gain));
  atomic_store(&d->input_meter->observed, decibels(atomic_load(&d->shown_in)));
  atomic_store(&d->reduction_meter->observed, reduction > 0 ? reduction : 0.0f);
  atomic_store(&d->output_meter->observed, decibels(atomic_load(&d->shown_out)));
  atomic_store(&d->fresh, true);
}

static inline double ramp(double mix, double target, double step) {
  if (mix < target) return fmin(mix + step, target);
  if (mix > target) return fmax(mix - step, target);
  return mix;
}

void dynamics_process(Dynamics *d, const float *in, float *out, uint32_t frames) {
  if (atomic_exchange(&d->fresh, false)) d->peak_in = d->peak_out = 0, d->least_gain = 1;
  double makeup_from = d->makeup_gain, makeup_to = follow(d);
  d->makeup_gain = makeup_to;
  if (d->mix == 0 && d->mix_target == 0) {
    // Off: the input to the bit, and nothing left of the detector.
    reset(d);
    double top = 0;
    for (uint32_t n = 0; n < frames; n++) top = fmax(top, fabs(in[n]));
    if (in != out) memcpy(out, in, sizeof(float) * frames);
    d->peak_in = fmax(d->peak_in, top);
    d->peak_out = fmax(d->peak_out, top);
  } else {
    bool compressor = d->kind == DYNAMICS_COMPRESSOR;
    double makeup_step = frames ? (makeup_to - makeup_from) / frames : 0;
    double peak_in = d->peak_in, peak_out = d->peak_out, least_gain = d->least_gain;
    for (uint32_t n = 0; n < frames; n++) {
      double x = in[n], level = fabs(x);
      peak_in = fmax(peak_in, level);
      double level_db = 20 * log10(fmax(level, LEVEL_FLOOR));
      double target = dynamics_reduction_db(d->kind, level_db, d->threshold_db, d->ratio_value, d->shape_db);
      if (compressor) {
        double c = target > d->reduction ? d->attack_coeff : d->release_coeff;
        d->reduction = c * d->reduction + (1 - c) * target;
      } else if (target > d->reduction) {
        // Closing: only once the hold has run out.
        if (d->hold_left > 0) d->hold_left--;
        else d->reduction = d->release_coeff * d->reduction + (1 - d->release_coeff) * target;
      } else {
        d->hold_left = d->hold;
        d->reduction = d->attack_coeff * d->reduction + (1 - d->attack_coeff) * target;
      }
      if (d->reduction < REDUCTION_FLUSH) d->reduction = 0;
      double gain = exp(-d->reduction * LN10_OVER_20);
      double makeup = makeup_from + makeup_step * (n + 1);
      d->mix = ramp(d->mix, d->mix_target, d->step);
      double y = x + (x * gain * makeup - x) * d->mix;
      least_gain = fmin(least_gain, 1 + (gain - 1) * d->mix);
      peak_out = fmax(peak_out, fabs(y));
      out[n] = (float)y;
    }
    d->peak_in = peak_in, d->peak_out = peak_out, d->least_gain = least_gain;
  }
  atomic_store(&d->shown_in, (float)d->peak_in);
  atomic_store(&d->shown_out, (float)d->peak_out);
  atomic_store(&d->shown_gain, (float)d->least_gain);
}
