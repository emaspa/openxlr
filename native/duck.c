// SPDX-License-Identifier: GPL-3.0-only
// One stereo duck bus, keyed by up to MAX_KEYS separate mono inputs: the
// detector follows the loudest open key at each sample, so two keys never
// add up. key_open is a mask, bit i for key_i. Only the audio thread owns
// the detector and gain; it reads controls once per block.
#include "host.h"
#include <float.h>
#include <math.h>
#include <stdlib.h>

typedef struct {
  Control *enabled, *threshold, *depth, *attack, *release, *key_open, *reduction;
  double envelope, gain, decay;
  double factor_gain;  // the gain in dB that factor was computed for
  float factor;
} Duck;

static bool duck_load(Host *h, char **arguments) {
  if (h->channels != 2) return false;
  Duck *d = calloc(1, sizeof(*d));
  if (!d) return false;
  h->impl = d;
  d->decay = exp(-1.0 / (0.050 * h->rate));
  d->enabled = host_add_control(h, "enabled", 0, 1, 0, false, NULL);
  d->threshold = host_add_control(h, "threshold", -60, 0, -12, false, NULL);
  d->depth = host_add_control(h, "depth", 0, 40, 12, false, NULL);
  d->attack = host_add_control(h, "attack", 1, 5000, 80, false, NULL);
  d->release = host_add_control(h, "release", 1, 10000, 1200, false, NULL);
  d->key_open = host_add_control(h, "key_open", 0, (1 << MAX_KEYS) - 1, 1, false, NULL);
  d->reduction = host_add_control(h, "reduction", 0, 40, 0, true, NULL);
  return d->enabled && d->threshold && d->depth && d->attack && d->release &&
         d->key_open && d->reduction;
}

static bool duck_activate(Host *h) {
  Duck *d = h->impl;
  d->envelope = d->gain = d->factor_gain = 0;
  d->factor = 1.0f;
  atomic_store(&d->reduction->observed, 0);
  return true;
}

static void duck_deactivate(Host *h) {}

static void duck_unload(Host *h) {
  free(h->impl);
  h->impl = NULL;
}

static void duck_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  Duck *d = h->impl;
  bool enabled = atomic_load(&d->enabled->desired) >= 0.5f;
  long open = lrintf(atomic_load(&d->key_open->desired));
  double threshold = pow(10, atomic_load(&d->threshold->desired) / 20.0);
  double depth = atomic_load(&d->depth->desired);
  double attack_ms = atomic_load(&d->attack->desired);
  double release_ms = atomic_load(&d->release->desired);
  double attack = depth / (attack_ms * h->rate / 1000);
  double release = depth / (release_ms * h->rate / 1000);
  const float *keys[MAX_KEYS];
  unsigned count = 0;
  for (unsigned i = 0; i < MAX_KEYS; i++) {
    const float *key = host_key_at(h, i);
    if ((open & 1L << i) && key) keys[count++] = key;
  }
  for (uint32_t n = 0; n < frames; n++) {
    // An infinite sample would hold the peak follower at infinity for good,
    // so a nonfinite key sample counts as silence.
    double k = 0;
    for (unsigned i = 0; i < count; i++)
      if (isfinite(keys[i][n])) k = fmax(k, fabs(keys[i][n]));
    d->envelope = fmax(k, d->envelope * d->decay);
    // A decaying peak must reach zero instead of lingering as a subnormal.
    if (d->envelope < DBL_MIN) d->envelope = 0;
    double target = enabled && d->envelope >= threshold ? -depth : 0;
    if (depth == 0) d->gain = 0;
    else if (d->gain > target) d->gain = fmax(d->gain - attack, target);
    else if (d->gain < target) d->gain = fmin(d->gain + release, target);
    if (d->gain != d->factor_gain) {
      d->factor_gain = d->gain;
      d->factor = d->gain == 0 ? 1.0f : (float)pow(10, d->gain / 20);
    }
    out[0][n] = in[0][n] * d->factor;
    out[1][n] = in[1][n] * d->factor;
  }
  // Subtracting from zero keeps an idle meter at 0 rather than -0.
  atomic_store(&d->reduction->observed, (float)(0 - d->gain));
}

const Backend duck_backend = {
  .name = "Duck", .argument_count = 0,
  .load = duck_load, .activate = duck_activate,
  .deactivate = duck_deactivate, .process = duck_process,
  .unload = duck_unload, .key_inputs = MAX_KEYS,
};
