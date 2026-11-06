// SPDX-License-Identifier: GPL-3.0-only
// The EQ: one to sixteen biquads in cascade, at the head of a microphone's
// route (mono) or after a mix's inserts (stereo). The coefficients are the ones in Robert Bristow-Johnson's Audio EQ
// Cookbook (https://www.w3.org/TR/audio-eq-cookbook/), the same formulas the
// daemon's EqResponse draws the curve from and tools/eq-reference.py checks
// both against.
//
// What keeps it quiet while values move. A frequency, gain or Q change
// recomputes that band's coefficients at the start of the next block and
// keeps the filter's state, so a drag is a series of small steps rather
// than a restart per step. Every band's filter runs whether the band is on
// or not, and the whole cascade runs while the EQ is off, so switching a
// band or the EQ on or off is a 5 ms crossfade between two signals that
// are both already there. A type change cannot keep its state, which
// belongs to another filter: the new filter is first run over the band's
// recent input, then faded in over 5 ms while the old one fades out.
//
// Two channels. A stereo stage is an output mix's EQ or a capture input's on
// a stereo pair, and runs the EQ alone: no noise removal, dynamics or voice
// changer, and none of their controls.
// Each band has one set of coefficients and, per channel, its own filter
// state and history; its switch and its type fade move once a sample for
// both, so the two sides follow the same curve from the same sample.
//
// The mono stage runs, in order: noise removal (denoise.c), the expander
// (dynamics.c), the EQ, the compressor (dynamics.c) and the voice changer
// (voice.c). The EQ shapes the cleaned voice, the expander closes on the
// noise that noise removal left, the compressor evens out the shaped voice,
// and the character goes on last. Their controls live in this stage as
// denoise_*, exp_*, comp_* and voice_*; noise removal's and the voice
// changer's delays add up to the stage's latency, and the two dynamics
// processors add none.
//
// Who writes what. The daemon writes the controls' desired values through
// the command pipe; only the audio thread reads them, at the start of a
// block, and only the audio thread touches the filters.
//
// Numbers that are not numbers. One NaN or infinity inside a filter's state
// stays there: every later sample comes out NaN, even through a band or an
// EQ that is off, since its mix still multiplies it, and the host silences
// the stage until it restarts. So a non-finite input sample becomes 0 before
// any part of the stage hears it, and a state that turns non-finite anyway
// is cleared where it is found: the EQ's filters here, noise removal's
// engine in denoise.c, the voice in voice.c. The compressor and expander
// (dynamics.c) also take a non-finite sample as 0, so nothing can stick in
// their detectors. The stage_nonfinite meter
// counts the input samples replaced and stage_resets the states cleared,
// and a trace line says so on stderr, at most once a second.
#include "denoise.h"
#include "dynamics.h"
#include "eq.h"
#include "host.h"
#include "voice.h"
#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

enum { EQ_BANDS = 4, EQ_MAX_BANDS = 16, EQ_TYPES = 6, EQ_HISTORY = 8192 };
static const double EQ_PI = 3.14159265358979323846;

// Below this a filter's state is silence: flushing it keeps the arithmetic
// out of subnormal numbers, which are slow on most CPUs, as a loud burst
// decays.
#define EQ_FLUSH 1e-25

typedef struct {
  EqCoefficients c;
  double s1, s2;  // transposed direct form II
} EqFilter;

typedef struct {
  Control *enabled, *type, *frequency, *gain, *qlog10;
  // The audio thread's own: what the filter was built from.
  int type_now;
  float frequency_now, gain_now, qlog10_now;
  // One per channel; fading runs only while a type change fades out.
  EqFilter live[MAX_CHANNELS], fading[MAX_CHANNELS];
  uint32_t change_left;    // samples of that fade still to go
  double mix, mix_target;  // the band's on switch: 0 dry, 1 filtered
  double history[MAX_CHANNELS][EQ_HISTORY];  // the band's recent input, for a new filter
  uint32_t history_at;
} EqBand;

typedef struct {
  Control *enabled;
  unsigned channels;       // 1: a microphone's stage; 2: the EQ alone, on a stereo pair
  // The microphone stage's parts, all NULL on a stereo stage.
  Denoise *denoise;
  float *cleaned;          // noise removal's output, the EQ's input
  Voice *voice;
  float *shaped;           // the EQ's output, the voice changer's input
  Dynamics *expander;      // in place on cleaned, before the EQ
  Dynamics *compressor;    // in place on shaped, after the EQ
  float *guarded[MAX_CHANNELS];  // the input with non-finite samples replaced, when it had any
  _Atomic uint64_t nonfinite;  // input samples replaced; audio thread writes
  _Atomic uint32_t resets;     // times the filters were cleared; audio thread writes
  Control *nonfinite_meter, *resets_meter;
  uint64_t traced_nonfinite;   // main thread: what the last trace line said
  uint32_t traced_resets;
  struct timespec traced_at;
  unsigned band_count;
  double mix, mix_target;  // the EQ's on switch
  uint32_t fade;           // 5 ms in samples
  double step;             // how far a switch moves per sample
  EqBand bands[];
} Eq;

// Coefficients of one band, normalised so a0 is 1. The cookbook's shelf
// takes the same Q as its bell; the 2 Hz floor and the 1e-15 floor on the
// linear gain are kept as curve facts, though the daemon's ranges never
// reach them.
void eq_coefficients(int type, double rate, double frequency, double gain_db,
                     double q, EqCoefficients *out) {
  frequency = fmin(frequency, 0.49 * rate);
  if (type == LOW_SHELF || type == HIGH_SHELF || type == PEAK)
    frequency = fmax(frequency, 2.0);
  double w0 = 2 * EQ_PI * frequency / rate;
  double cosw = cos(w0), sinw = sin(w0);
  double alpha = sinw / (2 * q);
  double a = sqrt(fmax(pow(10, gain_db / 20), 1e-15));  // 10^(dB/40)
  double root = 2 * sqrt(a) * alpha;
  double b0, b1, b2, a0, a1, a2;
  switch (type) {
  case LOW_CUT:
    b0 = (1 + cosw) / 2, b1 = -(1 + cosw), b2 = (1 + cosw) / 2;
    a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
    break;
  case HIGH_CUT:
    b0 = (1 - cosw) / 2, b1 = 1 - cosw, b2 = (1 - cosw) / 2;
    a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
    break;
  case NOTCH:
    b0 = 1, b1 = -2 * cosw, b2 = 1;
    a0 = 1 + alpha, a1 = -2 * cosw, a2 = 1 - alpha;
    break;
  case LOW_SHELF:
    b0 = a * ((a + 1) - (a - 1) * cosw + root);
    b1 = 2 * a * ((a - 1) - (a + 1) * cosw);
    b2 = a * ((a + 1) - (a - 1) * cosw - root);
    a0 = (a + 1) + (a - 1) * cosw + root;
    a1 = -2 * ((a - 1) + (a + 1) * cosw);
    a2 = (a + 1) + (a - 1) * cosw - root;
    break;
  case HIGH_SHELF:
    b0 = a * ((a + 1) + (a - 1) * cosw + root);
    b1 = -2 * a * ((a - 1) + (a + 1) * cosw);
    b2 = a * ((a + 1) + (a - 1) * cosw - root);
    a0 = (a + 1) - (a - 1) * cosw + root;
    a1 = 2 * ((a - 1) - (a + 1) * cosw);
    a2 = (a + 1) - (a - 1) * cosw - root;
    break;
  default:  // PEAK
    b0 = 1 + alpha * a, b1 = -2 * cosw, b2 = 1 - alpha * a;
    a0 = 1 + alpha / a, a1 = -2 * cosw, a2 = 1 - alpha / a;
    break;
  }
  *out = (EqCoefficients){b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0};
}

static inline double eq_run(EqFilter *f, double x) {
  double y = f->c.b0 * x + f->s1;
  f->s1 = f->c.b1 * x - f->c.a1 * y + f->s2;
  f->s2 = f->c.b2 * x - f->c.a2 * y;
  // Both at once: flushing one half alone, at every zero crossing of a
  // resonant band's ringing, feeds that band in step with its own
  // resonance and keeps it ringing far above the threshold.
  if (fabs(f->s1) < EQ_FLUSH && fabs(f->s2) < EQ_FLUSH) f->s1 = f->s2 = 0;
  return y;
}

static int eq_type_of(float value) {
  long type = lrintf(value);
  return type < 0 ? 0 : type >= EQ_TYPES ? EQ_TYPES - 1 : (int)type;
}

// The band's filter from its controls as they are now: the same
// coefficients on each channel's filter.
static void eq_build(EqBand *b, uint32_t rate, EqFilter *f, unsigned channels) {
  b->type_now = eq_type_of(atomic_load(&b->type->desired));
  b->frequency_now = atomic_load(&b->frequency->desired);
  b->gain_now = atomic_load(&b->gain->desired);
  b->qlog10_now = atomic_load(&b->qlog10->desired);
  EqCoefficients c;
  eq_coefficients(b->type_now, rate, b->frequency_now, b->gain_now, pow(10, b->qlog10_now), &c);
  for (unsigned k = 0; k < channels; k++) f[k].c = c;
}

static bool eq_on(const Control *c) { return atomic_load(&c->desired) >= 0.5f; }

static bool eq_load_count(Host *h, unsigned bands) {
  if (h->channels < 1 || h->channels > MAX_CHANNELS || bands < 1 || bands > EQ_MAX_BANDS) return false;
  Eq *e = calloc(1, sizeof(*e) + bands * sizeof(EqBand));
  if (!e) return false;
  h->impl = e;
  e->band_count = bands;
  e->channels = h->channels;
  bool microphone = e->channels == 1;
  for (unsigned k = 0; k < e->channels; k++)
    if (!(e->guarded[k] = calloc(MAX_FRAMES, sizeof(float)))) return false;
  if (microphone) {
    e->cleaned = calloc(MAX_FRAMES, sizeof(float));
    e->shaped = calloc(MAX_FRAMES, sizeof(float));
    e->denoise = denoise_load(h);
    e->voice = voice_load(h);
    if (!e->cleaned || !e->shaped || !e->denoise || !e->voice) return false;
  }
  e->fade = (uint32_t)lrint(h->rate * 0.005);
  if (e->fade == 0) e->fade = 1;
  e->step = 1.0 / e->fade;
  e->enabled = host_add_control(h, "enabled", 0, 1, 0, false, NULL);
  if (!e->enabled) return false;
  // Wave Link's defaults; the daemon passes every value at start anyway.
  static const struct { int type; float frequency; bool on; } defaults[EQ_BANDS] = {
      {LOW_CUT, 36, true}, {PEAK, 310, true}, {PEAK, 2172, true}, {HIGH_CUT, 15049, false}};
  for (unsigned i = 0; i < e->band_count; i++) {
    EqBand *b = &e->bands[i];
    char symbol[32];
#define EQ_CONTROL(field, name, minimum, maximum, initial)                  \
  snprintf(symbol, sizeof(symbol), "band%d_%s", (int)i + 1, name);              \
  char *copy_##field = strdup(symbol);                                    \
  if (!copy_##field) return false;                                        \
  b->field = host_add_control(h, copy_##field, minimum, maximum, initial,   \
                              false, NULL);                               \
  if (!b->field) { free(copy_##field); return false; }
    EQ_CONTROL(enabled, "enabled", 0, 1, i < EQ_BANDS && defaults[i].on)
    EQ_CONTROL(type, "type", 0, EQ_TYPES - 1, i < EQ_BANDS ? defaults[i].type : PEAK)
    EQ_CONTROL(frequency, "frequency", 20, 20000, i < EQ_BANDS ? defaults[i].frequency : 1000)
    EQ_CONTROL(gain, "gain", -12, 12, 0)
    EQ_CONTROL(qlog10, "qlog10", -1, 1, -0.152f)
#undef EQ_CONTROL
  }
  // Last, so every control above keeps its place, and the non-finite
  // meters after them for the same reason.
  if (microphone) {
    e->expander = dynamics_load(h, DYNAMICS_EXPANDER);
    e->compressor = dynamics_load(h, DYNAMICS_COMPRESSOR);
    if (!e->expander || !e->compressor) return false;
  }
  e->nonfinite_meter = host_add_control(h, "stage_nonfinite", 0, 1e9f, 0, true, NULL);
  e->resets_meter = host_add_control(h, "stage_resets", 0, 1e9f, 0, true, NULL);
  return e->nonfinite_meter && e->resets_meter;
}

// Keep the original four-band invocation compatible with older daemons.
static bool eq_load(Host *h, char **arguments) { return eq_load_count(h, EQ_BANDS); }

static bool eq_load_bands(Host *h, char **arguments) {
  if (!arguments || !arguments[0] || !arguments[0][0]) return false;
  for (const char *p = arguments[0]; *p; p++)
    if (*p < '0' || *p > '9') return false;
  errno = 0;
  char *end;
  unsigned long bands = strtoul(arguments[0], &end, 10);
  return !errno && !*end && bands >= 1 && bands <= EQ_MAX_BANDS && eq_load_count(h, (unsigned)bands);
}

// The values given on the command line are in place now: build from them
// without any fade, as if the EQ had always been set this way.
static bool eq_activate(Host *h) {
  Eq *e = h->impl;
  if (e->channels == 1 && (!denoise_activate(e->denoise) || !voice_activate(e->voice)
                           || !dynamics_activate(e->expander) || !dynamics_activate(e->compressor)))
    return false;
  e->mix = e->mix_target = eq_on(e->enabled);
  for (unsigned i = 0; i < e->band_count; i++) {
    EqBand *b = &e->bands[i];
    eq_build(b, h->rate, b->live, e->channels);
    for (unsigned k = 0; k < e->channels; k++) b->live[k].s1 = b->live[k].s2 = 0;
    b->change_left = 0;
    b->mix = b->mix_target = eq_on(b->enabled);
  }
  return true;
}

static void eq_deactivate(Host *h) {}

static void eq_unload(Host *h) {
  Eq *e = h->impl;
  if (!e) return;
  for (uint32_t i = 0; i < h->control_count; i++)
    if (strncmp(h->controls[i].symbol, "band", 4) == 0)
      free((char *)h->controls[i].symbol);
  denoise_unload(e->denoise);
  voice_unload(e->voice);
  dynamics_unload(e->expander);
  dynamics_unload(e->compressor);
  free(e->cleaned);
  free(e->shaped);
  for (unsigned k = 0; k < MAX_CHANNELS; k++) free(e->guarded[k]);
  free(e);
  h->impl = NULL;
}

// Take what the daemon asked for since the last block.
static void eq_follow(Eq *e, uint32_t rate) {
  e->mix_target = eq_on(e->enabled);
  for (unsigned i = 0; i < e->band_count; i++) {
    EqBand *b = &e->bands[i];
    b->mix_target = eq_on(b->enabled);
    int type = eq_type_of(atomic_load(&b->type->desired));
    float frequency = atomic_load(&b->frequency->desired);
    float gain = atomic_load(&b->gain->desired);
    float qlog10 = atomic_load(&b->qlog10->desired);
    if (type != b->type_now) {
      // One type change at a time: a second one waits for the first fade.
      if (b->change_left) continue;
      for (unsigned c = 0; c < e->channels; c++) b->fading[c] = b->live[c];
      eq_build(b, rate, b->live, e->channels);
      // Bring the new filter up to date on what the band heard lately.
      for (unsigned c = 0; c < e->channels; c++) {
        b->live[c].s1 = b->live[c].s2 = 0;
        for (uint32_t k = 0; k < EQ_HISTORY; k++)
          eq_run(&b->live[c], b->history[c][(b->history_at + k) % EQ_HISTORY]);
      }
      b->change_left = e->fade;
    } else if (frequency != b->frequency_now || gain != b->gain_now ||
               qlog10 != b->qlog10_now)
      eq_build(b, rate, b->live, e->channels);  // the state stays
  }
}

static inline double eq_ramp(double mix, double target, double step) {
  if (mix < target) return fmin(mix + step, target);
  if (mix > target) return fmax(mix - step, target);
  return mix;
}

// The input as the stage may hear it: unchanged, or a copy with every
// non-finite sample at 0. The host's buffer is not written; other nodes
// may read it.
static const float *eq_guard(Eq *e, unsigned channel, const float *in, uint32_t frames) {
  uint32_t bad = 0;
  for (uint32_t n = 0; n < frames; n++) bad += !isfinite(in[n]);
  if (!bad) return in;
  float *guarded = e->guarded[channel];
  for (uint32_t n = 0; n < frames; n++) guarded[n] = isfinite(in[n]) ? in[n] : 0.0f;
  atomic_fetch_add_explicit(&e->nonfinite, bad, memory_order_relaxed);
  return guarded;
}

// Clear every band's filters after one turned non-finite. A band's history
// took the bad value only as its newest entry, this sample's, so that entry
// is all it needs cleared.
static void eq_reset(Eq *e) {
  for (unsigned i = 0; i < e->band_count; i++) {
    EqBand *b = &e->bands[i];
    for (unsigned c = 0; c < e->channels; c++) {
      b->live[c].s1 = b->live[c].s2 = b->fading[c].s1 = b->fading[c].s2 = 0;
      double *newest = &b->history[c][(b->history_at + EQ_HISTORY - 1) % EQ_HISTORY];
      if (!isfinite(*newest)) *newest = 0;
    }
  }
  atomic_fetch_add_explicit(&e->resets, 1, memory_order_relaxed);
}

// The bands in cascade over every channel. A band's switch and type fade,
// and the EQ's switch, move once a sample, the same on each channel.
static void eq_shape(Eq *e, const float *const *in, float *const *out, uint32_t frames) {
  const unsigned channels = e->channels;
  for (uint32_t n = 0; n < frames; n++) {
    double x[MAX_CHANNELS], u[MAX_CHANNELS];
    for (unsigned c = 0; c < channels; c++) x[c] = u[c] = in[c][n];
    for (unsigned i = 0; i < e->band_count; i++) {
      EqBand *b = &e->bands[i];
      double y[MAX_CHANNELS];
      double t = b->change_left ? 1.0 - (double)b->change_left / e->fade : 1.0;
      for (unsigned c = 0; c < channels; c++) {
        b->history[c][b->history_at] = u[c];
        y[c] = eq_run(&b->live[c], u[c]);
        if (b->change_left) {
          double old = eq_run(&b->fading[c], u[c]);
          y[c] = old + (y[c] - old) * t;
        }
      }
      b->history_at = (b->history_at + 1) % EQ_HISTORY;
      if (b->change_left) b->change_left--;
      b->mix = eq_ramp(b->mix, b->mix_target, e->step);
      for (unsigned c = 0; c < channels; c++) u[c] += (y[c] - u[c]) * b->mix;
    }
    bool finite = true;
    for (unsigned c = 0; c < channels; c++) finite = finite && isfinite(u[c]);
    if (!finite) {
      // This sample plays dry on every channel; the next one runs through clean filters.
      eq_reset(e);
      for (unsigned c = 0; c < channels; c++) {
        if (!isfinite(x[c])) x[c] = 0;
        u[c] = x[c];
      }
    }
    e->mix = eq_ramp(e->mix, e->mix_target, e->step);
    for (unsigned c = 0; c < channels; c++) out[c][n] = (float)(x[c] + (u[c] - x[c]) * e->mix);
  }
}

static void eq_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  Eq *e = h->impl;
  if (e->channels != 1) {
    // A mix's EQ: the guarded input straight through the bands.
    const float *guarded[MAX_CHANNELS];
    for (unsigned c = 0; c < e->channels; c++) guarded[c] = eq_guard(e, c, in[c], frames);
    eq_follow(e, h->rate);
    eq_shape(e, guarded, out, frames);
    return;
  }
  denoise_process(e->denoise, eq_guard(e, 0, in[0], frames), e->cleaned, frames);
  dynamics_process(e->expander, e->cleaned, e->cleaned, frames);
  eq_follow(e, h->rate);
  const float *cleaned[] = {e->cleaned};
  float *shaped[] = {e->shaped};
  eq_shape(e, cleaned, shaped, frames);
  dynamics_process(e->compressor, e->shaped, e->shaped, frames);
  voice_process(e->voice, e->shaped, out[0], frames);
}

// Publish the non-finite counts and say once a second, while they move,
// what they are.
static void eq_report(Eq *e) {
  uint64_t replaced = atomic_load_explicit(&e->nonfinite, memory_order_relaxed);
  uint32_t filters = atomic_load_explicit(&e->resets, memory_order_relaxed);
  bool microphone = e->channels == 1;
  uint32_t noise = microphone ? denoise_resets(e->denoise) : 0, voice = microphone ? voice_resets(e->voice) : 0;
  uint32_t resets = filters + noise + voice;
  atomic_store(&e->nonfinite_meter->observed, (float)replaced);
  atomic_store(&e->resets_meter->observed, (float)resets);
  if (replaced == e->traced_nonfinite && resets == e->traced_resets) return;
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  if (e->traced_at.tv_sec && now.tv_sec - e->traced_at.tv_sec < 1) return;
  e->traced_at = now;
  e->traced_nonfinite = replaced;
  e->traced_resets = resets;
  if (microphone)
    fprintf(stderr,
            "trace: microphone stage: %llu non-finite input samples replaced; states cleared: "
            "%u EQ, %u noise removal, %u voice\n",
            (unsigned long long)replaced, filters, noise, voice);
  else
    fprintf(stderr, "trace: stereo EQ: %llu non-finite input samples replaced; states cleared: %u EQ\n",
            (unsigned long long)replaced, filters);
}

static void eq_main_thread(Host *h) {
  Eq *e = h->impl;
  if (e->channels == 1) {
    denoise_main_thread(e->denoise);
    voice_main_thread(e->voice);
    dynamics_main_thread(e->expander);
    dynamics_main_thread(e->compressor);
  }
  eq_report(e);
}

// A stereo stage runs nothing that delays its output.
static uint32_t eq_latency(Host *h) {
  Eq *e = h->impl;
  return e->channels == 1 ? denoise_latency(e->denoise) + voice_latency(e->voice) : 0;
}

const Backend eq_backend = {
  .name = "EQ", .argument_count = 0, .guards_input = true,
  .load = eq_load, .activate = eq_activate,
  .deactivate = eq_deactivate, .process = eq_process,
  .main_thread = eq_main_thread, .unload = eq_unload,
  .latency = eq_latency,
};

// The same DSP with a selected band count. Only changing the count replaces
// this stage; every ordinary band edit still uses its live controls.
const Backend eq_bands_backend = {
  .name = "EQ", .argument_count = 1, .guards_input = true,
  .load = eq_load_bands, .activate = eq_activate,
  .deactivate = eq_deactivate, .process = eq_process,
  .main_thread = eq_main_thread, .unload = eq_unload,
  .latency = eq_latency,
};
