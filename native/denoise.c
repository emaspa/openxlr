// SPDX-License-Identifier: GPL-3.0-only
// Noise removal: the first thing the microphone stage does to a sample,
// before the EQ. Two engines, both at 48 kHz on mono audio:
//
//   RNNoise (Xiph, BSD-3-Clause, vendored under rnnoise/): 480-sample frames,
//   run on the audio thread. It allocates nothing per frame and costs a small
//   part of a core.
//
//   DeepFilterNet 3 (MIT or Apache-2.0, deepfilter/): 480-sample hops through
//   an ONNX model. Inference allocates, so it never runs on the audio thread:
//   a worker thread of its own takes the input from one ring and puts the
//   output in another, and the audio thread reads the output a lag behind
//   what it wrote. The lag follows the block size: it grows at once with a
//   larger block and shrinks once smaller blocks have held for 2 s, moving
//   by a 5 ms crossfade between the two read positions. It is built in only
//   when the helper was built with it (OPENXLR_DEEPFILTER); the `engines`
//   command says which.
//
// Strength is the most the noise is turned down, in dB, 0 to 100, where 100
// leaves the engine unlimited. DeepFilterNet takes it as its attenuation
// limit. RNNoise mixes the input back in under its output at that level, so
// a sound the engine removes entirely comes out the strength below the
// input, as it does from DeepFilterNet.
//
// Switching. An engine's output is late by the engine's delay and the dry
// signal is not, so the two cannot be crossfaded without a comb. Turning
// noise removal on or off, or changing the engine, fades the running output
// out over 5 ms, starts the next one, and fades that in over 5 ms. Until an
// engine has heard enough of its own input to answer (its delay, counted from
// the switch) the input, delayed to match, stands in for it. A change to
// DeepFilterNet keeps the current output playing until its model has loaded
// on the main thread.
//
// Who owns what. The daemon writes the controls; the audio thread reads them
// once a block and owns the fade, RNNoise and the DeepFilterNet worker it
// holds. The main thread loads and frees DeepFilterNet workers and hands them
// over through df_spare, so neither thread ever frees what the other uses.
#include "denoise.h"
#include "build-config.h"
#include "rnnoise.h"
#include <errno.h>
#include <math.h>
#include <semaphore.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if OPENXLR_DEEPFILTER
// native/deepfilter/src/lib.rs
typedef struct OpenxlrDf OpenxlrDf;
OpenxlrDf *openxlr_df_create(float atten_lim_db);
size_t openxlr_df_hop(const OpenxlrDf *st);
size_t openxlr_df_rate(const OpenxlrDf *st);
size_t openxlr_df_delay(const OpenxlrDf *st);
void openxlr_df_set_atten_lim(OpenxlrDf *st, float atten_lim_db);
int32_t openxlr_df_process(OpenxlrDf *st, const float *input, float *output);
void openxlr_df_free(OpenxlrDf *st);
#endif

enum {
  FADE = 240,        // 5 ms at 48 kHz, each half of a switch
  RNN_FRAME = 480,   // rnnoise_get_frame_size()
  // RNNoise answers a frame two frames after it took it in: one frame for the
  // overlap of its window, one for its pitch filter, which looks a frame
  // ahead. With the frame this stage collects first, three frames in all.
  RNN_DELAY = 3 * RNN_FRAME,
  DRY_SIZE = 1 << 15,  // the delayed input; longer than any delay here
  DF_RING = 1 << 15,   // each DeepFilterNet ring, in samples
  DF_MAX_HOP = 2048,
  DF_SETTLE = 2 * 48000,  // how long smaller blocks hold before the lag shrinks
};

// RNNoise works on 16-bit sample values held in floats.
#define RNN_SCALE 32768.0f

typedef struct DfWorker {
#if OPENXLR_DEEPFILTER
  OpenxlrDf *model;
  pthread_t thread;
#endif
  uint32_t hop, delay;
  const Control *strength;  // read by the worker before each hop
  float applied;            // the limit the model has
  sem_t wake;
  _Atomic bool quit;
  _Atomic bool failed;  // the model failed; the audio thread stops using it
  // Ring positions as running sample counts: the audio thread writes input
  // up to written, and the worker has taken input, and given output, up to
  // done. Output sample j answers input sample j - delay.
  _Atomic uint64_t written, done;
  float in[DF_RING], out[DF_RING];
  float hop_in[DF_MAX_HOP], hop_out[DF_MAX_HOP];
} DfWorker;

struct Denoise {
  Host *h;
  Control *enabled, *engine, *strength;
  Control *active_meter, *status_meter, *late_meter, *latency_meter;
  bool rate_ok;

  // The audio thread's own.
  int active;            // the engine whose output plays
  int phase;             // 0 steady, -1 fading out, +1 fading in
  unsigned level;        // the switch's fade, 0 (silent) to FADE
  uint64_t clock;        // samples taken since the stage started
  uint64_t session;      // samples the active engine has taken
  float dry[DRY_SIZE];   // the input, for delaying
  double mix;            // RNNoise: how much of its output plays
  DenoiseState *rnn;
  float rnn_in[RNN_FRAME], rnn_out[RNN_FRAME];
  unsigned rnn_at;
  DfWorker *df;          // held while it runs, or while a switch to it waits
  uint64_t df_base;      // the ring position the session started at
  uint32_t df_lag;       // ring samples between input written and output read
  uint32_t df_from;      // the lag a move left, while df_moving counts down
  uint32_t df_moving;    // samples left of a move between two lags
  uint32_t df_peak;      // the most lag any block since df_window began needed
  uint32_t df_window;    // samples since df_peak was reset
  uint64_t late;         // blocks DeepFilterNet answered late

  // Main thread only. Remains true while the audio thread borrows the
  // worker, so returning it cannot make a second model load race the first.
  bool df_loaded;

  // Between the threads.
  _Atomic(DfWorker *) df_spare;  // loaded by the main thread, not in use
  _Atomic int df_broken;         // the audio thread gave up on DeepFilterNet
  _Atomic bool df_load_failed;   // the main thread could not load it
  _Atomic uint32_t latency;
};

bool denoise_engine_built(int engine) {
  if (engine == DENOISE_RNNOISE) return true;
#if OPENXLR_DEEPFILTER
  if (engine == DENOISE_DEEPFILTER) return true;
#endif
  return false;
}

int denoise_print_engines(void) {
  printf("{\"denoise\":[\"rnnoise\"%s],\"rate\":%u}\n",
         denoise_engine_built(DENOISE_DEEPFILTER) ? ",\"deepfilternet\"" : "",
         DENOISE_SAMPLE_RATE);
  return fflush(stdout) == 0 ? 0 : 1;
}

static bool on(const Control *c) { return atomic_load(&c->desired) >= 0.5f; }

static int engine_of(const Control *c) {
  return lrintf(atomic_load(&c->desired)) >= DENOISE_DEEPFILTER ? DENOISE_DEEPFILTER : DENOISE_RNNOISE;
}

// --- DeepFilterNet's worker ----------------------------------------------------

#if OPENXLR_DEEPFILTER
static void *df_work(void *data) {
  DfWorker *w = data;
  while (!atomic_load(&w->quit)) {
    // A wait that times out now and then keeps a lost wake from stalling
    // the worker for good.
    struct timespec until;
    clock_gettime(CLOCK_REALTIME, &until);
    until.tv_nsec += 50000000;
    if (until.tv_nsec >= 1000000000) until.tv_sec++, until.tv_nsec -= 1000000000;
    if (sem_timedwait(&w->wake, &until) != 0 && errno != ETIMEDOUT && errno != EINTR) break;
    for (;;) {
      if (atomic_load(&w->quit) || atomic_load(&w->failed)) break;
      uint64_t done = atomic_load_explicit(&w->done, memory_order_relaxed);
      uint64_t written = atomic_load_explicit(&w->written, memory_order_acquire);
      if (written - done < w->hop) break;
      for (uint32_t n = 0; n < w->hop; n++) w->hop_in[n] = w->in[(done + n) & (DF_RING - 1)];
      float strength = atomic_load(&w->strength->desired);
      if (strength != w->applied) {
        openxlr_df_set_atten_lim(w->model, strength);
        w->applied = strength;
      }
      bool ok = openxlr_df_process(w->model, w->hop_in, w->hop_out) == 0;
      for (uint32_t n = 0; ok && n < w->hop; n++) ok = isfinite(w->hop_out[n]);
      if (!ok) {
        atomic_store(&w->failed, true);
        break;
      }
      for (uint32_t n = 0; n < w->hop; n++) w->out[(done + n) & (DF_RING - 1)] = w->hop_out[n];
      atomic_store_explicit(&w->done, done + w->hop, memory_order_release);
    }
  }
  return NULL;
}
#endif

static void df_stop(DfWorker *w) {
  if (!w) return;
#if OPENXLR_DEEPFILTER
  atomic_store(&w->quit, true);
  sem_post(&w->wake);
  pthread_join(w->thread, NULL);
  openxlr_df_free(w->model);
#endif
  sem_destroy(&w->wake);
  free(w);
}

// Load the model and start its worker. Blocks for as long as loading takes,
// so only the main thread calls it. NULL when it cannot run.
static DfWorker *df_start(Denoise *d) {
#if OPENXLR_DEEPFILTER
  DfWorker *w = calloc(1, sizeof(*w));
  if (!w) return NULL;
  if (sem_init(&w->wake, 0, 0) != 0) {
    free(w);
    return NULL;
  }
  w->strength = d->strength;
  w->applied = atomic_load(&d->strength->desired);
  w->model = openxlr_df_create(w->applied);
  size_t hop = w->model ? openxlr_df_hop(w->model) : 0;
  if (!w->model || hop == 0 || hop > DF_MAX_HOP || openxlr_df_rate(w->model) != DENOISE_SAMPLE_RATE
      || openxlr_df_delay(w->model) > DF_RING / 4) {
    if (w->model) openxlr_df_free(w->model);
    sem_destroy(&w->wake);
    free(w);
    return NULL;
  }
  w->hop = (uint32_t)hop;
  w->delay = (uint32_t)openxlr_df_delay(w->model);
  if (pthread_create(&w->thread, NULL, df_work, w) != 0) {
    openxlr_df_free(w->model);
    sem_destroy(&w->wake);
    free(w);
    return NULL;
  }
  return w;
#else
  (void)d;
  return NULL;
#endif
}

// The audio thread takes a loaded worker, if one is waiting. The main
// thread owns its lifetime even while it is borrowed here.
static bool df_take(Denoise *d) {
  DfWorker *w = atomic_exchange(&d->df_spare, NULL);
  if (!w) return false;
  d->df = w;
  return true;
}

static void df_give_back(Denoise *d) {
  if (!d->df) return;
  atomic_store(&d->df_spare, d->df);
  d->df = NULL;
}

// --- loading -------------------------------------------------------------------

Denoise *denoise_load(Host *h) {
  if (h->channels != 1) return NULL;
  Denoise *d = calloc(1, sizeof(*d));
  if (!d) return NULL;
  d->h = h;
  d->rate_ok = h->rate == DENOISE_SAMPLE_RATE;
  d->active = DENOISE_NONE;
  d->level = FADE;
  atomic_init(&d->df_spare, NULL);
  d->rnn = rnnoise_create(NULL);
  d->enabled = host_add_control(h, "denoise_enabled", 0, 1, 0, false, NULL);
  d->engine = host_add_control(h, "denoise_engine", 0, 1, DENOISE_RNNOISE, false, NULL);
  d->strength = host_add_control(h, "denoise_strength", 0, 100, 100, false, NULL);
  d->active_meter = host_add_control(h, "denoise_active", -1, 1, DENOISE_NONE, true, NULL);
  d->status_meter = host_add_control(h, "denoise_status", 0, 4, DENOISE_OK, true, NULL);
  d->late_meter = host_add_control(h, "denoise_late", 0, 1e9f, 0, true, NULL);
  d->latency_meter = host_add_control(h, "denoise_latency", 0, 1e9f, 0, true, NULL);
  if (!d->rnn || !d->enabled || !d->engine || !d->strength || !d->active_meter
      || !d->status_meter || !d->late_meter || !d->latency_meter) {
    denoise_unload(d);
    return NULL;
  }
  return d;
}

static double strength_mix(const Denoise *d) {
  double db = atomic_load(&d->strength->desired);
  return db >= 100 ? 1.0 : 1.0 - pow(10, -db / 20);
}

// What the controls ask to hear now, as far as it can run.
static int wanted(Denoise *d) {
  if (!on(d->enabled) || !d->rate_ok) return DENOISE_NONE;
  int engine = engine_of(d->engine);
  if (!denoise_engine_built(engine)) return DENOISE_NONE;
  if (engine == DENOISE_DEEPFILTER && (atomic_load(&d->df_broken) || atomic_load(&d->df_load_failed)))
    return DENOISE_NONE;
  return engine;
}

static void publish_latency(Denoise *d) {
  uint32_t latency = d->active == DENOISE_RNNOISE ? RNN_DELAY
      : d->active == DENOISE_DEEPFILTER && d->df ? d->df_lag + d->df->delay : 0;
  atomic_store(&d->latency, latency);
  atomic_store(&d->latency_meter->observed, (float)latency);
}

// Make `engine` the one that plays, from its first sample.
static void begin(Denoise *d, int engine) {
  if (d->active == DENOISE_DEEPFILTER && engine != DENOISE_DEEPFILTER) df_give_back(d);
  d->active = engine;
  d->session = 0;
  if (engine == DENOISE_RNNOISE) {
    // Without a model argument this sets up the built-in weights in place;
    // nothing is allocated.
    rnnoise_init(d->rnn, NULL);
    memset(d->rnn_out, 0, sizeof(d->rnn_out));
    d->rnn_at = 0;
    d->mix = strength_mix(d);
  } else if (engine == DENOISE_DEEPFILTER) {
    d->df_base = atomic_load_explicit(&d->df->written, memory_order_relaxed);
    d->df_lag = d->df_moving = d->df_peak = d->df_window = 0;
  }
  atomic_store(&d->active_meter->observed, (float)engine);
  publish_latency(d);
}

bool denoise_activate(Denoise *d) {
  int engine = wanted(d);
  if (engine == DENOISE_DEEPFILTER) {
    DfWorker *w = df_start(d);
    if (w) {
      d->df_loaded = true;
      atomic_store(&d->df_spare, w);
      df_take(d);
    } else {
      atomic_store(&d->df_load_failed, true);
      engine = DENOISE_NONE;
    }
  }
  begin(d, engine);
  d->phase = 0;
  d->level = FADE;
  denoise_main_thread(d);
  return true;
}

void denoise_unload(Denoise *d) {
  if (!d) return;
  df_stop(d->df);
  df_stop(atomic_exchange(&d->df_spare, NULL));
  if (d->rnn) rnnoise_destroy(d->rnn);
  free(d);
}

uint32_t denoise_latency(const Denoise *d) { return d ? atomic_load(&d->latency) : 0; }

// --- the main thread -----------------------------------------------------------

void denoise_main_thread(Denoise *d) {
  bool asked = on(d->enabled) && engine_of(d->engine) == DENOISE_DEEPFILTER;
  // A failure holds until DeepFilterNet is asked for again.
  if (!asked) {
    atomic_store(&d->df_broken, DENOISE_OK);
    atomic_store(&d->df_load_failed, false);
  }
  bool want = asked && d->rate_ok && denoise_engine_built(DENOISE_DEEPFILTER)
      && !atomic_load(&d->df_broken) && !atomic_load(&d->df_load_failed);
  if (want) {
    if (!d->df_loaded) {
      DfWorker *w = df_start(d);
      if (!w) atomic_store(&d->df_load_failed, true);
      else {
        d->df_loaded = true;
        atomic_store(&d->df_spare, w);
      }
    }
  } else {
    DfWorker *w = atomic_exchange(&d->df_spare, NULL);
    if (w) {
      df_stop(w);
      d->df_loaded = false;
    }
  }

  int status = DENOISE_OK;
  if (on(d->enabled)) {
    int engine = engine_of(d->engine);
    if (!d->rate_ok) status = DENOISE_RATE_UNSUPPORTED;
    else if (!denoise_engine_built(engine)) status = DENOISE_NOT_BUILT;
    else if (engine == DENOISE_DEEPFILTER && atomic_load(&d->df_load_failed)) status = DENOISE_LOAD_FAILED;
    else if (engine == DENOISE_DEEPFILTER && atomic_load(&d->df_broken)) status = DENOISE_FELL_BEHIND;
  }
  atomic_store(&d->status_meter->observed, (float)status);
}

void denoise_wait_idle(Denoise *d) {
  DfWorker *w = d->df;
  if (!w) return;
  struct timespec pause = {0, 100000};
  for (int i = 0; i < 100000; i++) {
    uint64_t written = atomic_load(&w->written), done = atomic_load(&w->done);
    if (written - done < w->hop || atomic_load(&w->failed)) return;
    nanosleep(&pause, NULL);
  }
}

// --- the audio thread ----------------------------------------------------------

static inline float dry_at(const Denoise *d, uint64_t t, uint32_t delay) {
  return t >= delay ? d->dry[(t - delay) & (DRY_SIZE - 1)] : 0.0f;
}

// One segment through RNNoise. `t` is the clock of in[0].
static void run_rnnoise(Denoise *d, const float *in, float *out, uint32_t frames, uint64_t t) {
  double target = strength_mix(d), step = (target - d->mix) / frames;
  for (uint32_t n = 0; n < frames; n++) {
    d->mix += step;
    float dry = dry_at(d, t + n, RNN_DELAY);
    d->rnn_in[d->rnn_at] = in[n] * RNN_SCALE;
    float wet = d->session + n < RNN_DELAY ? dry : d->rnn_out[d->rnn_at] / RNN_SCALE;
    out[n] = dry + (float)((wet - dry) * d->mix);
    if (++d->rnn_at == RNN_FRAME) {
      rnnoise_process_frame(d->rnn, d->rnn_out, d->rnn_in);
      d->rnn_at = 0;
    }
  }
  d->mix = target;
  d->session += frames;
}

// The lag a block of `block` samples needs. The worker hears a hop only once
// the block that completes it has arrived, and that block may complete it
// with its first sample, so the lag covers the hop it collects (hop - 1) and
// then the worker's time to answer, which comes in whole blocks: one block,
// or as many as make up a hop when blocks are smaller, so it never has less
// than 10 ms.
static uint32_t df_need(uint32_t block, uint32_t hop) {
  return hop + (hop + block - 1) / block * block;
}

// The lag the next segment reads at: at once the most a block needs, and
// after DF_SETTLE of smaller blocks the most they needed. A change starts a
// move from the old lag.
static void df_follow(Denoise *d, uint32_t need, uint32_t frames) {
  uint32_t lag = d->df_lag;
  if (need >= lag) {
    // A block requiring the current lag ends any smaller-block interval.
    // Start the two-second wait at the last such block, not a fixed window.
    lag = need;
    d->df_peak = d->df_window = 0;
  } else {
    if (need > d->df_peak) d->df_peak = need;
    d->df_window += frames;
    if (d->df_window >= DF_SETTLE) {
      lag = d->df_peak;
      d->df_peak = d->df_window = 0;
    }
  }
  if (lag == d->df_lag) return;
  // The session's first lag plays from the start; nothing came before it.
  d->df_from = d->df_lag;
  d->df_moving = d->df_lag ? FADE : 0;
  d->df_lag = lag;
  publish_latency(d);
}

// The worker's answer for session sample `k` read `lag` behind, or the input
// delayed to match when there is none yet or it is late.
static float df_read(const Denoise *d, uint64_t k, uint64_t t, uint32_t lag, uint64_t done, bool usable,
                     bool *late) {
  const DfWorker *w = d->df;
  uint32_t delay = lag + w->delay;
  float dry = dry_at(d, t, delay);
  if (!usable || k < delay) return dry;  // nothing of this session's to answer yet
  uint64_t j = d->df_base + k - lag;
  if (j < done) return w->out[j & (DF_RING - 1)];
  *late = true;
  return dry;
}

// One segment through DeepFilterNet's worker. `block` is the size of the
// whole block this segment belongs to, which the lag has to cover.
static void run_deepfilter(Denoise *d, const float *in, float *out, uint32_t frames,
                           uint32_t block, uint64_t t) {
  DfWorker *w = d->df;
  df_follow(d, df_need(block, w->hop), frames);
  uint64_t written = atomic_load_explicit(&w->written, memory_order_relaxed);
  uint64_t done = atomic_load_explicit(&w->done, memory_order_acquire);
  bool usable = !atomic_load(&w->failed) && written + frames - done <= DF_RING;
  if (usable) {
    for (uint32_t n = 0; n < frames; n++) w->in[(written + n) & (DF_RING - 1)] = in[n];
    atomic_store_explicit(&w->written, written + frames, memory_order_release);
    if (written + frames - done >= w->hop) sem_post(&w->wake);
  } else if (!atomic_load(&d->df_broken))
    atomic_store(&d->df_broken, DENOISE_FELL_BEHIND);
  bool late = false;
  for (uint32_t n = 0; n < frames; n++) {
    uint64_t k = d->session + n;
    float now = df_read(d, k, t + n, d->df_lag, done, usable, &late);
    if (d->df_moving) {
      // Both read positions are the engine's own output, the same sound at
      // two offsets, so they crossfade where engine and input cannot. The
      // old one is not counted late: a shorter lag is left because the
      // worker could not keep it.
      bool ignored = false;
      float a = (float)d->df_moving / FADE;
      now += a * (df_read(d, k, t + n, d->df_from, done, usable, &ignored) - now);
      d->df_moving--;
    }
    out[n] = now;
  }
  if (late) atomic_store(&d->late_meter->observed, (float)++d->late);
  d->session += frames;
}

static void run(Denoise *d, const float *in, float *out, uint32_t frames, uint32_t block, uint64_t t) {
  if (d->active == DENOISE_RNNOISE) run_rnnoise(d, in, out, frames, t);
  else if (d->active == DENOISE_DEEPFILTER) run_deepfilter(d, in, out, frames, block, t);
  else memcpy(out, in, sizeof(float) * frames);
}

void denoise_process(Denoise *d, const float *in, float *out, uint32_t frames) {
  for (uint32_t n = 0; n < frames; n++) d->dry[(d->clock + n) & (DRY_SIZE - 1)] = in[n];
  int target = wanted(d);
  // DeepFilterNet waits for its model; until it is there, what plays now
  // keeps playing.
  if (target == DENOISE_DEEPFILTER && !d->df && !df_take(d)) target = d->active;
  if (target != DENOISE_DEEPFILTER && d->active != DENOISE_DEEPFILTER) df_give_back(d);
  if (d->active == DENOISE_DEEPFILTER && d->df && atomic_load(&d->df->failed)) {
    atomic_store(&d->df_broken, DENOISE_FELL_BEHIND);
    target = DENOISE_NONE;
  }
  if (target != d->active) d->phase = -1;
  else if (d->phase < 0) d->phase = 1;

  uint32_t n = 0;
  while (n < frames) {
    // A fade out ends inside the block: the rest of it is the next engine's.
    uint32_t count = frames - n;
    if (d->phase < 0 && d->level < count) count = d->level;
    if (count) run(d, in + n, out + n, count, frames, d->clock + n);
    for (uint32_t i = 0; i < count && d->phase; i++) {
      if (d->phase < 0) d->level--;
      else if (d->level < FADE) d->level++;
      out[n + i] *= (float)d->level / FADE;
    }
    n += count;
    if (d->phase < 0 && d->level == 0) {
      begin(d, target);
      d->phase = 1;
    }
    if (d->phase > 0 && d->level == FADE) d->phase = 0;
  }
  d->clock += frames;
}
